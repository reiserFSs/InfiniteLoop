using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.skytower;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Dreamhouse Tycoon (client XSkyTower, TimeId 51011, functional id 10514).
/// Authoritative: share/skytower tables, Reward/Task/Condition tables, client CodeText 20434xxx, the client Lua contract.
/// AscNet policy (not retail parity; authorised by the AGENTS.MD missing-retail policy): no retail server exists, so
/// production runs the client's own algorithm over the tables (SkyTowerModule.Engine.cs); offline settlement is
/// capped by MaxSettleTime and OfflineBillLimit + SkyTowerLv.OfflineBillAdd; speed-up orders advance one position and
/// unused orders are returned; the daily grant is AccelerateDailyNum orders per daily reset; perform unlocks follow
/// SkyTowerLv.PerformIds + UnlockConditionIds; the window is the missing-calendar policy in ActivityScheduleService.
/// </summary>
internal static partial class SkyTowerModule
{
    // Client CodeText 20434xxx.
    internal const int NotOpen = 20434001, ConfigNotFound = 20434008, InvalidRequest = 20434004, SettleTimeError = 20434005, SettleFailed = 20434006,
        MetMaxTowerLevel = 20434007, GoldNotEnough = 20434009, CharacterNotUnlock = 20434010, CharacterMetMaxLevel = 20434011,
        CharacterCannotUpLevel = 20434012, CharacterLvUpItemNotEnough = 20434013, CharacterUpLevelConditionNotMet = 20434014,
        WorkPosNotUnlock = 20434015, WorkPosNoCharacter = 20434016, CharacterFloorGroupNotMatch = 20434017, WorkPosAppointWorkNotMatch = 20434018,
        ConditionNotMet = 20434019, PerformNotExist = 20434020, PerformNotAchieved = 20434021, PerformAlreadyFinish = 20434022,
        PerformStoryError = 20434023, TowerUpLevelConditionNotMet = 20434024, SpeedUpItemNotEnough = 20434025,
        SpeedUpDailyLimitReached = 20434026, WorkPosNotWorking = 20434027, ItemNotExist = 20434028, SpeedUpPerUseLimitOver = 20434029;

    private const int StoryTalkSelect = 2;

    /// <summary>Test seam; production uses the wall clock.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly Lazy<int[]> EntryConditions = new(() => TableReaderV2.Parse<FunctionalOpenTable>()
        .Single(row => row.Id == 10514).Condition.Where(id => id > 0).ToArray());
    private static readonly Lazy<Dictionary<int, SkyTowerItemTable>> Items = new(() => TableReaderV2.Parse<SkyTowerItemTable>().ToDictionary(row => row.Id));

    private static bool FeatureAvailable(Session session) => EntryConditions.Value.All(id => GuildModule.ConditionSatisfied(session, id, []));

    internal static bool IsSkyTowerItem(int templateId) => templateId is >= 38000000 and <= 38999999;

    internal static SkyTowerActivityTable? OpenActivity(Session session, DateTimeOffset at) =>
        Activity.Value is { TimeId: > 0 } activity && ActivityScheduleService.IsOpen(activity.TimeId, at) && FeatureAvailable(session) ? activity : null;

    // ---- lifecycle ----

    private static long ItemCap(int itemId) => Items.Value.GetValueOrDefault(itemId)?.MaxCount is > 0 and int max ? max : int.MaxValue;

    /// <summary>Credits gold. <paramref name="produced"/> (sales) also feeds TotalGoldProduced and its conversion into staff promotion merit.</summary>
    internal static long AddGold(SkyTowerState s, long count, bool produced)
    {
        long granted = Math.Min(ItemCap(GoldItemId), s.Gold + count) - s.Gold;
        if (granted <= 0) return 0;
        s.Gold += granted;
        s.DailyGoldGained += granted;
        s.TotalGoldGained += granted;
        if (!produced) return granted;
        s.TotalGoldProduced += granted;
        int step = ConfigInt("GoldTransCharaLvUpItemBase"), per = ConfigInt("GoldTransCharaLvUpItemNum");
        if (step > 0 && per > 0)
        {
            long pending = s.TotalGoldProduced / step * per - s.TotalTransCharacterLvUpItem;
            long tickets = pending > 0 ? Math.Min(ItemCap(CharacterLvUpItemId), s.CharacterLvUpItem + pending) - s.CharacterLvUpItem : 0;
            if (tickets > 0)
            {
                s.TotalTransCharacterLvUpItem += tickets;
                s.CharacterLvUpItem += tickets;
            }
        }
        return granted;
    }

    /// <summary>Grants a SkyTower item (reward pipeline, initial items, daily orders). Throws on an unknown item.</summary>
    internal static void GrantItem(SkyTowerState s, int itemId, long count)
    {
        if (count <= 0 || !Items.Value.ContainsKey(itemId)) throw new InvalidDataException($"Invalid SkyTower item grant {itemId} x{count}.");
        if (itemId == GoldItemId) AddGold(s, count, false);
        else if (itemId == CharacterLvUpItemId) s.CharacterLvUpItem = Math.Min(ItemCap(itemId), s.CharacterLvUpItem + count);
        else if (itemId == SpeedUpItemId) s.SpeedUpItem = Math.Min(ItemCap(itemId), s.SpeedUpItem + count);
        else throw new InvalidDataException($"SkyTower item {itemId} has no balance.");
    }

    private static void Initialise(Session session, int activityId, long now, DateTimeOffset at)
    {
        SkyTowerState s = session.player.SkyTower = new()
        {
            ActivityId = activityId, Level = 1, LastSettleTime = now, DailyPeriod = TaskModule.CurrentDailyResetPeriod(now)
        };
        UnlockLevels(s, 0, 1);
        void Seed(string key, string countKey, int productType)
        {
            if (!Config.Value.TryGetValue(key, out List<int>? ids)) return;
            for (int i = 0; i < ids.Count; i++)
                if (Groups.Value.Values.FirstOrDefault(group => (productType == ProductIngredient ? group.IngredientIds : group.FoodIds).Contains(ids[i])) is { } group)
                    SetStock(s, group.Id, productType, ids[i], ConfigInt(countKey, i));
        }
        Seed("InitIngredient", "InitIngredientCount", ProductIngredient);
        Seed("InitFood", "InitFoodCount", ProductFood);
        if (Config.Value.TryGetValue("InitItem", out List<int>? items))
            for (int i = 0; i < items.Count; i++) GrantItem(s, items[i], ConfigInt("InitItemCount", i));
        RefreshPerforms(s, now, at);
    }

    /// <summary>Opens work positions, staff and returns what a level range newly unlocks.</summary>
    private static (List<int> WorkPos, List<int> Characters, List<int> Performs) UnlockLevels(SkyTowerState s, int from, int to)
    {
        List<int> characters = [], performs = [], positions = [];
        for (int level = from + 1; level <= to; level++)
        {
            SkyTowerLvTable row = Levels.Value[level];
            foreach (int id in row.UnlockCharacter)
                if (s.Characters.TryAdd(id, 1)) characters.Add(id);
            performs.AddRange(row.PerformIds);
        }
        foreach (int key in PositionsAt(to))
            if (s.WorkPos.TryAdd(key, new() { State = StateStop })) positions.Add(key);
        if (positions.Count > 0)
        {
            s.LastUnlockedWorkPosIndex = positions[^1];
            s.LastUnlockedFloorId = ParseKey(positions[^1]).Floor;
        }
        return (positions, characters, performs);
    }

    /// <summary>
    /// Applies the daily reset: used speed-up count and daily gold clear, AccelerateDailyNum dispatch orders are granted
    /// (AscNet policy: one grant per reset the player is observed at, no back-fill), claimed daily tasks reopen.
    /// </summary>
    private static bool RollDaily(Player player, long now)
    {
        SkyTowerState s = player.SkyTower;
        long period = TaskModule.CurrentDailyResetPeriod(now);
        if (s.DailyPeriod == period) return false;
        s.DailyPeriod = period;
        s.SpeedUpDailyUsedCount = 0;
        s.DailyGoldGained = 0;
        s.SpeedUpGrantedDays++;
        GrantItem(s, SpeedUpItemId, ConfigInt("AccelerateDailyNum"));
        HashSet<int> daily = Catalog.Value.Values.Where(task => task.Daily).Select(task => task.Id).ToHashSet();
        player.MissionProgress.ClaimedTaskIds.RemoveAll(daily.Contains);
        return true;
    }

    /// <summary>Opens the player's state for the current window: initialises on first use or a new activity, applies the daily reset.</summary>
    internal static SkyTowerState? Prepare(Session session, bool notifyReset = false)
    {
        DateTimeOffset at = Clock();
        if (OpenActivity(session, at) is not { } activity) return null;
        long now = at.ToUnixTimeSeconds();
        if (session.player.SkyTower.ActivityId != activity.Id) Initialise(session, activity.Id, now, at);
        else if (RollDaily(session.player, now) && notifyReset)
            session.SendPush(new NotifySkyTowerSpeedUpDailyReset
            { SpeedUpDailyUsedCount = session.player.SkyTower.SpeedUpDailyUsedCount, SpeedUpItemCount = session.player.SkyTower.SpeedUpItem });
        return session.player.SkyTower;
    }

    // ---- settlement ----

    /// <summary>
    /// Runs production up to <paramref name="now"/> and credits the gold. Only MaxSettleTime of elapsed time counts. An offline
    /// settle (AscNet policy: ActivityData after OfflineBillTime hours away) credits at most OfflineBillLimit + OfflineBillAdd gold.
    /// </summary>
    private static void Settle(SkyTowerState s, long now, bool offline, out long gold, out long tickets)
    {
        long window = Math.Min(Math.Max(0, now - s.LastSettleTime), ConfigInt("MaxSettleTime"));
        SimResult sim = Advance(s, now - window, now);
        long credit = offline ? Math.Min(sim.Gold, ConfigInt("OfflineBillLimit") + Levels.Value[s.Level].OfflineBillAdd) : sim.Gold;
        long ticketsBefore = s.CharacterLvUpItem;
        gold = AddGold(s, credit, true);
        tickets = s.CharacterLvUpItem - ticketsBefore;
        s.LastSettleTime = Math.Max(s.LastSettleTime, now);
        ApplyProduction(s, sim);
    }

    /// <summary>Post-change bookkeeping shared by every mutation: start work at once, unlock performs, refresh income tasks.</summary>
    private static void Finalise(SkyTowerState s, long now, DateTimeOffset at)
    {
        SimResult sim = Advance(s, now, now);
        AddGold(s, sim.Gold, true);
        ApplyProduction(s, sim);
        RefreshPerforms(s, now, at);
        RefreshSpeedTasks(s);
    }

    // ---- performs ----

    /// <summary>Starts every perform whose level, window and unlock conditions are met (SkyTowerLv.PerformIds + SkyTowerPerform).</summary>
    internal static void RefreshPerforms(SkyTowerState s, long now, DateTimeOffset at)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int performId in Levels.Value.Where(row => row.Key <= s.Level).SelectMany(row => row.Value.PerformIds).Distinct())
            {
                if (s.Performs.ContainsKey(performId) || !PerformRows.Value.TryGetValue(performId, out SkyTowerPerformTable? row)) continue;
                if (row.TimeId > 0 && !ActivityScheduleService.IsOpen(row.TimeId, at)) continue;
                if (!row.UnlockConditionIds.All(id => ConditionMet(s, id))) continue;
                s.Performs[performId] = new() { State = PerformOnGoing, UpdateTime = now };
                changed = true;
            }
        }
    }

    // ---- wire builders ----

    private static SkyTowerWorkPosInfoDto ToDto(SkyTowerWorkPosState wp) => new()
    {
        WorkType = wp.WorkType, ProductType = wp.ProductId > 0 ? ProductTypeOf(wp.WorkType) : 0, ProductId = wp.ProductId, CharacterId = wp.CharacterId,
        State = wp.State, WorkedSeconds = wp.WorkedSeconds, ProduceRemainder = wp.ProduceRemainder, ReservedExtraProduceRatio = wp.ReservedExtraProduceRatio,
        HasStartedRound = wp.HasStartedRound,
        ReservedInputs = wp.ReservedInputs.Select(input => new SkyTowerProductAmountDto { ProductType = input.ProductType, ProductId = input.ProductId, Count = input.Count }).ToList()
    };

    private static SkyTowerFloorGroupStorageDto StorageDto(SkyTowerState s, int groupId) => new()
    {
        StorageInfos = s.Storage.Where(entry => entry.Key / StorageGroupFactor == groupId).ToDictionary(
            entry => entry.Key % StorageGroupFactor,
            entry => new SkyTowerProductAmountDto { ProductType = entry.Key % StorageGroupFactor / StorageTypeFactor, ProductId = entry.Key % StorageTypeFactor, Count = entry.Value })
    };

    private static SkyTowerPerformInfoDto ToDto(int performId, SkyTowerPerformState perform) => new()
    {
        PerformId = performId, State = perform.State, Type = PerformRows.Value.GetValueOrDefault(performId)?.Type ?? 0,
        PerformStoryInfo = new(perform.Story), UpdateTime = perform.UpdateTime
    };

    private static SkyTowerCharacterInfoDto ToDto(int level, Dictionary<int, int> ratios) => new() { CharacterLv = level, ActiveSkillRatios = new(ratios) };

    private static SkyTowerDataDto DataDto(SkyTowerState s)
    {
        Dictionary<int, Dictionary<int, int>> ratios = ComputeRatios(s);
        return new()
        {
            ActivityId = s.ActivityId, SkyTowerLv = s.Level, GoldItemCount = s.Gold, CharacterLvUpItemCount = s.CharacterLvUpItem,
            SpeedUpItemCount = s.SpeedUpItem, SpeedUpDailyUsedCount = s.SpeedUpDailyUsedCount, SpeedUpGrantedDays = s.SpeedUpGrantedDays,
            TotalGoldProduced = s.TotalGoldProduced, TotalTransCharacterLvUpItem = s.TotalTransCharacterLvUpItem, LastSettleTime = s.LastSettleTime,
            WorkPosInfos = s.WorkPos.ToDictionary(entry => entry.Key, entry => ToDto(entry.Value)),
            Storages = s.Storage.Keys.Select(key => key / StorageGroupFactor).Distinct().ToDictionary(group => group, group => StorageDto(s, group)),
            CharacterInfos = s.Characters.ToDictionary(entry => entry.Key, entry => ToDto(entry.Value, ratios.GetValueOrDefault(entry.Key) ?? [])),
            PerformInfos = s.Performs.ToDictionary(entry => entry.Key, entry => ToDto(entry.Key, entry.Value)),
            LastUnlockedWorkPosLocation = s.LastUnlockedWorkPosIndex > 0
                ? new() { FloorId = s.LastUnlockedFloorId, WorkPosIndex = s.LastUnlockedWorkPosIndex } : null
        };
    }

    /// <summary>The state-based push for rewards that touch SkyTower items (the client has no item-only notify).</summary>
    internal static NotifySkyTowerData BuildDataPush(Session session) => new() { ActivityData = DataDto(session.player.SkyTower) };

    // ---- operation context ----

    private sealed class Snapshot
    {
        public Dictionary<int, SkyTowerWorkPosState> Pos = new();
        public Dictionary<int, long> Storage = new();
        public Dictionary<int, int> Characters = new();
        public Dictionary<int, Dictionary<int, int>> Ratios = new();
        public Dictionary<int, (int State, string Story)> Performs = new();

        public static Snapshot Take(SkyTowerState s) => new()
        {
            Pos = s.WorkPos.ToDictionary(entry => entry.Key, entry => BsonSerializer.Deserialize<SkyTowerWorkPosState>(entry.Value.ToBson())),
            Storage = new(s.Storage), Characters = new(s.Characters), Ratios = ComputeRatios(s),
            Performs = s.Performs.ToDictionary(entry => entry.Key, entry => (entry.Value.State, string.Join(",", entry.Value.Story.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"))))
        };
    }

    private static bool Same(SkyTowerWorkPosState a, SkyTowerWorkPosState b) =>
        a.WorkType == b.WorkType && a.ProductId == b.ProductId && a.CharacterId == b.CharacterId && a.State == b.State && a.WorkedSeconds == b.WorkedSeconds
        && a.ProduceRemainder == b.ProduceRemainder && a.ReservedExtraProduceRatio == b.ReservedExtraProduceRatio && a.HasStartedRound == b.HasStartedRound
        && a.ReservedInputs.Select(i => (i.ProductType, i.ProductId, i.Count)).SequenceEqual(b.ReservedInputs.Select(i => (i.ProductType, i.ProductId, i.Count)));

    private sealed class Op
    {
        public Session Session = null!;
        public SkyTowerState S = null!;
        public DateTimeOffset At;
        public long Now => At.ToUnixTimeSeconds();
        public Snapshot Before = null!;
        /// <summary>Pushes sent after the task sync and before the perform/character diffs (reward pipeline pushes).</summary>
        public List<Action> ExtraPushes = [];
        /// <summary>Requests whose response already carries the perform list skip the perform diff push.</summary>
        public bool SkipPerformPush;
        public bool PushCharacters;

        public static Op? Begin(Session session)
        {
            if (Prepare(session, notifyReset: true) is not { } s) return null;
            return new() { Session = session, S = s, At = Clock(), Before = Snapshot.Take(s) };
        }

        public SkyTowerDataUpdateDto Update()
        {
            SkyTowerDataUpdateDto update = new()
            {
                SpeedUpItemCount = S.SpeedUpItem, GoldItemCount = S.Gold, CharacterLvUpItemCount = S.CharacterLvUpItem,
                TotalGoldProduced = S.TotalGoldProduced, TotalTransCharacterLvUpItem = S.TotalTransCharacterLvUpItem
            };
            foreach ((int key, SkyTowerWorkPosState wp) in S.WorkPos)
                if (!Before.Pos.TryGetValue(key, out SkyTowerWorkPosState? old) || !Same(old, wp))
                    update.ChangedWorkPosInfos[key] = ToDto(wp);
            foreach (int group in S.Storage.Where(entry => Before.Storage.GetValueOrDefault(entry.Key, -1) != entry.Value)
                .Select(entry => entry.Key / StorageGroupFactor).Distinct())
                update.ChangedStorages[group] = StorageDto(S, group);
            return update;
        }

        public Dictionary<int, SkyTowerCharacterInfoDto> ChangedCharacters()
        {
            Dictionary<int, Dictionary<int, int>> ratios = ComputeRatios(S);
            Dictionary<int, SkyTowerCharacterInfoDto> changed = [];
            foreach ((int id, int level) in S.Characters)
            {
                Dictionary<int, int> now = ratios.GetValueOrDefault(id) ?? [], old = Before.Ratios.GetValueOrDefault(id) ?? [];
                if (!Before.Characters.TryGetValue(id, out int oldLevel) || oldLevel != level
                    || now.Count != old.Count || now.Any(entry => !old.TryGetValue(entry.Key, out int value) || value != entry.Value))
                    changed[id] = ToDto(level, now);
            }
            return changed;
        }

        public List<SkyTowerPerformInfoDto> ChangedPerforms() => S.Performs.Where(entry =>
            !Before.Performs.TryGetValue(entry.Key, out (int State, string Story) old) || old.State != entry.Value.State
            || old.Story != string.Join(",", entry.Value.Story.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")))
            .Select(entry => ToDto(entry.Key, entry.Value)).ToList();
    }

    /// <summary>
    /// Runs one request: prepare, body, save, then the standard push order (task sync, reward pushes, perform diff, staff diff)
    /// before the response. A failed save restores the in-memory state and answers SettleFailed.
    /// </summary>
    private static void Handle<TResponse>(Session session, Packet.Request packet, Func<Op, TResponse> body)
        where TResponse : ISkyTowerResponse, new()
    {
        Op? op = Op.Begin(session);
        if (op is null)
        {
            session.SendResponse(new TResponse { Code = NotOpen }, packet.Id);
            return;
        }
        SkyTowerState backup = BsonSerializer.Deserialize<SkyTowerState>(op.S.ToBson());
        TResponse response;
        try
        {
            response = body(op);
            session.player.SaveChecked();
        }
        catch (Exception exception)
        {
            session.player.SkyTower = backup;
            session.log.Error($"SkyTower request failed: {exception}");
            session.SendResponse(new TResponse { Code = SettleFailed }, packet.Id);
            return;
        }
        TaskModule.SendSkyTowerTaskSync(session);
        foreach (Action push in op.ExtraPushes) push();
        if (!op.SkipPerformPush && op.ChangedPerforms() is { Count: > 0 } performs)
            session.SendPush(new NotifySkyTowerPerform { ChangedPerformInfos = performs });
        if (op.PushCharacters && op.ChangedCharacters() is { Count: > 0 } characters)
            session.SendPush(new NotifySkyTowerCharacter { ChangedCharacterInfos = characters });
        session.SendResponse(response, packet.Id);
    }

    // ---- login ----

    /// <summary>Entrance data: the client reads ActivityId/Level/Gold/PerformInfos before the player has opened the mode.</summary>
    internal static void SendLoginPushes(Session session)
    {
        if (Prepare(session) is not { } s) return;
        session.SendPush(new NotifySkyTowerLoginData
        {
            ActivityId = s.ActivityId, Level = s.Level, GoldItemCount = s.Gold,
            PerformInfos = s.Performs.ToDictionary(entry => entry.Key, entry => ToDto(entry.Key, entry.Value))
        });
    }

    // ---- requests ----

    [RequestPacketHandler("SkyTowerActivityDataRequest")]
    public static void ActivityDataRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<SkyTowerActivityDataRequest>();
        long offlineBill = 0, offlineTime = 0, offlineTickets = 0;
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            long elapsed = op.Now - s.LastSettleTime;
            bool offline = elapsed >= ConfigInt("OfflineBillTime") * 3600L;
            Settle(s, op.Now, offline, out long gold, out long tickets);
            Finalise(s, op.Now, op.At);
            if (offline) (offlineBill, offlineTime, offlineTickets) = (gold, elapsed, tickets);
            op.SkipPerformPush = true;
            return new SkyTowerActivityDataResponse
            {
                ActivityData = DataDto(s), OfflineBill = offlineBill, OfflineTime = offlineTime, OfflineCharacterLvUpItemCount = offlineTickets
            };
        });
    }

    [RequestPacketHandler("SkyTowerSettleToNowRequest")]
    public static void SettleToNowRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<SkyTowerSettleToNowRequest>();
        Handle(session, packet, op =>
        {
            if (op.Now < op.S.LastSettleTime) return new SkyTowerSettleToNowResponse { Code = SettleTimeError };
            Settle(op.S, op.Now, false, out _, out _);
            Finalise(op.S, op.Now, op.At);
            return new SkyTowerSettleToNowResponse { DataUpdate = op.Update() };
        });
    }

    [RequestPacketHandler("SkyTowerExitRequest")]
    public static void ExitRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<SkyTowerExitRequest>();
        // Leaving settles once, so the next entry's offline bill measures only the time spent away.
        Handle(session, packet, op =>
        {
            if (op.Now < op.S.LastSettleTime) return new SkyTowerExitResponse();
            Settle(op.S, op.Now, false, out _, out _);
            Finalise(op.S, op.Now, op.At);
            return new SkyTowerExitResponse();
        });
    }

    [RequestPacketHandler("SkyTowerPerformDataRequest")]
    public static void PerformDataRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<SkyTowerPerformDataRequest>();
        Handle(session, packet, op =>
        {
            RefreshPerforms(op.S, op.Now, op.At);
            op.SkipPerformPush = true;
            return new SkyTowerPerformDataResponse { PerformInfos = op.S.Performs.Select(entry => ToDto(entry.Key, entry.Value)).ToList() };
        });
    }

    [RequestPacketHandler("SkyTowerSavePerformStoryRequest")]
    public static void SavePerformStoryRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerSavePerformStoryRequest request = packet.Deserialize<SkyTowerSavePerformStoryRequest>();
        Handle(session, packet, op =>
        {
            if (!op.S.Performs.TryGetValue(request.PerformId, out SkyTowerPerformState? perform) || perform.State != PerformOnGoing)
                return new SkyTowerSavePerformStoryResponse { Code = PerformNotExist };
            Dictionary<int, int> record = request.PerformStoryInfo ?? [];
            HashSet<int> talks = StoryRows.Value.GetValueOrDefault(PerformRows.Value[request.PerformId].StoryId)?.TalkIds.ToHashSet() ?? [];
            // Only the story's own choice talks take an answer, and the answer must be one of that talk's replies (1-based).
            if (record.Any(entry => !talks.Contains(entry.Key) || !TalkRows.Value.TryGetValue(entry.Key, out SkyTowerTalkTable? talk)
                || talk.Type != StoryTalkSelect || entry.Value < 1 || entry.Value > talk.Answers.Count))
                return new SkyTowerSavePerformStoryResponse { Code = PerformStoryError };
            foreach ((int talkId, int answer) in record) perform.Story[talkId] = answer;
            perform.UpdateTime = op.Now;
            op.SkipPerformPush = true;
            return new SkyTowerSavePerformStoryResponse { PerformInfo = ToDto(request.PerformId, perform) };
        });
    }

    [RequestPacketHandler("SkyTowerFinishPerformRequest")]
    public static void FinishPerformRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerFinishPerformRequest request = packet.Deserialize<SkyTowerFinishPerformRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.Performs.TryGetValue(request.PerformId, out SkyTowerPerformState? perform) || perform.State == PerformNotStart)
                return new SkyTowerFinishPerformResponse { Code = PerformNotExist };
            if (perform.State == PerformFinish) return new SkyTowerFinishPerformResponse { Code = PerformAlreadyFinish };
            Settle(s, op.Now, false, out _, out _);
            SkyTowerPerformTable row = PerformRows.Value[request.PerformId];
            if (!row.TaskIds.All(taskId => Catalog.Value.TryGetValue(taskId, out SkyTask? task) && TaskValue(s, task) >= task.Target))
                return new SkyTowerFinishPerformResponse { Code = PerformNotAchieved };
            List<AscNet.Table.V2.share.reward.RewardGoodsTable> goods = RewardHandler.GetRewardGoods(row.RewardId);
            RewardApplicationResult? application = goods.Count > 0
                ? RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant($"skytower-perform:{request.PerformId}", goods)], session) : null;
            // The reward pipeline persists the player (our state included); finishing after it stays retry-safe because the claim is idempotent.
            perform = s.Performs[request.PerformId];
            perform.State = PerformFinish;
            perform.UpdateTime = op.Now;
            Finalise(s, op.Now, op.At);
            if (application is not null) op.ExtraPushes.Add(() => application.SendPushes(session));
            return new SkyTowerFinishPerformResponse { PerformInfo = ToDto(request.PerformId, perform), RewardGoodsList = application?.RewardGoods ?? [] };
        });
    }

    [RequestPacketHandler("SkyTowerFloorUpLevelRequest")]
    public static void FloorUpLevelRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerFloorUpLevelRequest request = packet.Deserialize<SkyTowerFloorUpLevelRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (s.Level >= MaxLevel) return new SkyTowerFloorUpLevelResponse { Code = MetMaxTowerLevel };
            if (request.SkyTowerLv <= s.Level || request.SkyTowerLv > MaxLevel) return new SkyTowerFloorUpLevelResponse { Code = InvalidRequest };
            // Upgrading settles first, so the cost is paid from the income earned up to now.
            Settle(s, op.Now, false, out _, out _);
            long cost = Enumerable.Range(s.Level + 1, request.SkyTowerLv - s.Level).Sum(level => (long)Levels.Value[level].NeedGoldCount);
            if (s.Gold < cost) return new SkyTowerFloorUpLevelResponse { Code = GoldNotEnough };
            // Every level on the way must pass its own condition (the client checks them one by one).
            for (int level = s.Level + 1; level <= request.SkyTowerLv; level++)
                if (!ConditionMet(s, Levels.Value[level].ConditionId)) return new SkyTowerFloorUpLevelResponse { Code = TowerUpLevelConditionNotMet };
            int from = s.Level;
            s.Gold -= cost;
            s.Level = request.SkyTowerLv;
            (List<int> positions, List<int> characters, List<int> performs) = UnlockLevels(s, from, s.Level);
            Finalise(s, op.Now, op.At);
            op.PushCharacters = true;
            return new SkyTowerFloorUpLevelResponse
            {
                Result = new()
                {
                    SkyTowerLv = s.Level, GoldItemCount = s.Gold, DataUpdate = op.Update(), NewWorkPosIds = positions, NewCharacterIds = characters,
                    NewPerformIds = performs.Distinct().ToList()
                }
            };
        });
    }

    [RequestPacketHandler("SkyTowerCharacterUpLevelRequest")]
    public static void CharacterUpLevelRequestHandler(Session session, Packet.Request packet)
    {
        SkyTowerCharacterUpLevelRequest request = packet.Deserialize<SkyTowerCharacterUpLevelRequest>();
        Handle(session, packet, op =>
        {
            SkyTowerState s = op.S;
            if (!s.Characters.TryGetValue(request.CharacterId, out int level)) return new SkyTowerCharacterUpLevelResponse { Code = CharacterNotUnlock };
            if (IsUniversal(request.CharacterId)) return new SkyTowerCharacterUpLevelResponse { Code = CharacterCannotUpLevel };
            int max = CharacterMaxLevel(request.CharacterId);
            if (level >= max) return new SkyTowerCharacterUpLevelResponse { Code = CharacterMetMaxLevel };
            if (request.CharacterLv <= level || request.CharacterLv > max) return new SkyTowerCharacterUpLevelResponse { Code = InvalidRequest };
            Settle(s, op.Now, false, out _, out _);
            long cost = 0;
            for (int next = level + 1; next <= request.CharacterLv; next++)
            {
                SkyTowerCharacterLvTable? row = CharacterLvRow(request.CharacterId, next);
                if (row is null) return new SkyTowerCharacterUpLevelResponse { Code = ConfigNotFound };
                if (!ConditionMet(s, row.Condition)) return new SkyTowerCharacterUpLevelResponse { Code = CharacterUpLevelConditionNotMet };
                cost += row.NeedItemCount;
            }
            if (s.CharacterLvUpItem < cost) return new SkyTowerCharacterUpLevelResponse { Code = CharacterLvUpItemNotEnough };
            s.CharacterLvUpItem -= cost;
            s.Characters[request.CharacterId] = request.CharacterLv;
            Finalise(s, op.Now, op.At);
            op.PushCharacters = true;
            return new SkyTowerCharacterUpLevelResponse { CharacterId = request.CharacterId, CharacterLv = request.CharacterLv, DataUpdate = op.Update() };
        });
    }
}
