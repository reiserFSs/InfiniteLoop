using System.Text.Json;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.maintaineraction;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Operation Guardians (client MaintainerAction, functional id 1302).
/// Authoritative: MaintainerActionConfig/Level/Event tables, FunctionalOpen 1302, Stage/Reward tables, client CodeText.
/// AscNet policy (not retail parity; authorised by AGENTS.MD missing-retail policy): no node-pool table exists for
/// NodePoolId, so each player's weekly board, node contents and card draws are generated here from a persisted seed;
/// the mode is permanently available (TimeId has no calendar); only the player's own record is shown; mentor nodes
/// stay inert because the mentor system is absent.
/// </summary>
public static class MaintainerActionModule
{
    // Client CodeText 2010300x.
    private const int WrongCard = 20103001, CardPoolError = 20103002, BoxFinish = 20103003, NodeEventValueError = 20103004,
        DailyActionPointLimit = 20103005, CurrentNodeException = 20103006, NodeTypeIsNotFight = 20103007, StageIdError = 20103008,
        FightFinish = 20103009, NodeNotTriggered = 20103010, NodeIsTriggered = 20103011, NotOpen = 20103012,
        ExplorationIsComplete = 20103016, WarehouseFinish = 20103017;

    // XMaintainerActionConfigs.NodeType.
    internal const int TypeUnKnow = 0, TypeStart = 1, TypeFight = 2, TypeBox = 3, TypeNone = 4, TypeForward = 5, TypeFallBack = 6,
        TypeCardChange = 7, TypeDirectionChange = 8, TypeActionPoint = 9, TypeSimulationFight = 10, TypeWarehouse = 11, TypeExplore = 12;

    // MaintainerActionEvent rows used per node type (names/texts are authored there).
    internal const int EventEmptyRoad = 0, EventMove = 1, EventFight = 2, EventBox = 3, EventForward = 4, EventFallBack = 5,
        EventCardChange = 6, EventDirectionChange = 7, EventHighMorale = 8, EventUnknownArea = 9, EventStart = 10,
        EventExternalReinforcement = 11, EventStartBonus = 12, EventBonusAction = 13, EventWarehouse = 14, EventExplore = 15;

    // AscNet policy: the tables state "+2" only for event 13; the other grants are one action.
    private const int HighMoraleActions = 1, BonusActionActions = 2, StartBonusActions = 1, RecordLimit = 100;

    /// <summary>Test seam; production uses the wall clock.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly Lazy<MaintainerActionConfigTable[]> Configs = new(() => TableReaderV2.Parse<MaintainerActionConfigTable>().ToArray());
    private static readonly Lazy<MaintainerActionLevelTable[]> Levels = new(() => TableReaderV2.Parse<MaintainerActionLevelTable>().OrderBy(x => x.Level).ToArray());
    private static readonly Lazy<Dictionary<int, StageTable>> Stages = new(() =>
    {
        HashSet<int> ids = Levels.Value.SelectMany(x => x.StageIds).ToHashSet();
        return TableReaderV2.Parse<StageTable>().Where(x => ids.Contains((int)x.StageId)).ToDictionary(x => (int)x.StageId);
    });
    private static readonly Lazy<int[]> EntryConditions = new(() => TableReaderV2.Parse<FunctionalOpenTable>()
        .Single(x => x.Id == 1302).Condition.Where(x => x > 0).ToArray());

    private static MaintainerActionConfigTable? ActiveConfig() => Configs.Value
        .Where(x => x.TimeId > 0 && ActivityScheduleService.IsOpen(x.TimeId, Clock())).OrderByDescending(x => x.Id).FirstOrDefault();

    private static bool FeatureAvailable(Session session) => EntryConditions.Value.All(id => GuildModule.ConditionSatisfied(session, id, []));

    internal static bool IsStage(uint stageId) => stageId <= int.MaxValue && Levels.Value.Any(x => x.StageIds.Contains((int)stageId));

    // ---- deterministic counter-based RNG (SplitMix64); state = persisted Seed + Draws ----

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static int Next(MaintainerActionState state, int bound) =>
        (int)(Mix((ulong)state.Seed ^ ((ulong)(++state.Draws) * 0x632BE59BD9B4E019UL)) % (ulong)bound);

    private static void Shuffle<T>(MaintainerActionState state, IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Next(state, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static string Json(params (string Key, object Value)[] fields) =>
        JsonSerializer.Serialize(fields.ToDictionary(x => x.Key, x => x.Value));

    // ---- board generation (AscNet policy) ----

    private static void BuildBoard(Session session, MaintainerActionConfigTable cfg, long now)
    {
        MaintainerActionState s = session.player.MaintainerAction;
        long week = TaskModule.CurrentWeeklyResetPeriod(now);
        s.ConfigId = cfg.Id;
        s.Week = week;
        s.Day = TaskModule.CurrentDailyResetPeriod(now);
        s.Seed = (long)Mix((ulong)session.player.PlayerData.Id * 0x9E3779B97F4A7C15UL ^ (ulong)week * 0xC2B2AE3D27D4EB4FUL ^ (ulong)cfg.Id);
        s.Draws = 0;
        s.PlayerNodeId = 0;
        s.Reverse = false;
        s.Triggered = true;
        s.Landing = 0;
        s.FightWinCount = s.BoxCount = s.UsedActionCount = s.ExtraActionCount = s.WarehouseFinishCount = 0;
        s.StartBonusClaimed = false;
        s.Records = [];
        s.LastEvent = null;

        int count = cfg.NodeCount, ring = count + 1;
        MaintainerActionLevelTable level = Levels.Value.LastOrDefault(x => x.Level <= session.player.PlayerData.Level) ?? Levels.Value[0];
        // Composition respects the authored weekly caps: fights, boxes and the optional warehouse.
        List<int> types = [];
        void Add(int type, int n) { for (int i = 0; i < n; i++) types.Add(type); }
        Add(TypeFight, Math.Min(3 + Next(s, 2), cfg.MaxFightWinCount));
        Add(TypeSimulationFight, 1);
        Add(TypeBox, Math.Min(2 + Next(s, 2), cfg.MaxBoxCount));
        s.HasWarehouseNode = cfg.MaxWarehouseFinishCount > 0 && Next(s, 2) == 0;
        if (s.HasWarehouseNode) Add(TypeWarehouse, 1);
        Add(TypeExplore, 1);
        Add(TypeCardChange, 1);
        Add(TypeDirectionChange, 1);
        Add(TypeActionPoint, 1);

        // Relocation nodes never land on another relocation node, so one landing cannot chain forever.
        int forwardPos, forwardStep, backPos, backStep, forwardTarget, backTarget, guard = 0;
        do
        {
            if (++guard > 1000) throw new InvalidOperationException("Maintainer action relocation placement failed.");
            forwardPos = 1 + Next(s, count); forwardStep = 1 + Next(s, 3);
            backPos = 1 + Next(s, count); backStep = 1 + Next(s, 3);
            // AscNet policy (retail never relocates past the Starting Point; the client's own CradRoute also
            // stops at node 0 once it has left it): a relocation is clamped at the start node, and Step shows
            // the distance actually travelled.
            forwardTarget = forwardPos + forwardStep >= ring ? 0 : forwardPos + forwardStep;
            backTarget = Math.Max(backPos - backStep, 0);
            forwardStep = forwardTarget == 0 ? ring - forwardPos : forwardStep;
            backStep = backPos - backTarget;
        } while (forwardPos == backPos || forwardTarget == backPos || backTarget == forwardPos);

        List<int> slots = Enumerable.Range(1, count).Where(x => x != forwardPos && x != backPos).ToList();
        if (types.Count > slots.Count) throw new InvalidDataException("Maintainer action node composition exceeds NodeCount.");
        Shuffle(s, slots);
        Dictionary<int, int> typeAt = new() { [forwardPos] = TypeForward, [backPos] = TypeFallBack };
        for (int i = 0; i < slots.Count; i++) typeAt[slots[i]] = i < types.Count ? types[i] : TypeNone;

        int PickStage() => level.StageIds[Next(s, level.StageIds.Count)];
        int PickReward() => level.BoxRewardIds;
        s.Nodes = [new MaintainerActionNodeState { NodeId = 0, NodeType = TypeStart, EventId = EventStart, Value = Json(("ExtraActionPoint", 0)), Revealed = true }];
        for (int id = 1; id <= count; id++)
        {
            int type = typeAt[id];
            MaintainerActionNodeState node = new() { NodeId = id, NodeType = type, Revealed = true };
            switch (type)
            {
                case TypeFight: node.EventId = EventFight; node.Value = Json(("StageId", PickStage())); break;
                case TypeSimulationFight: node.EventId = EventExternalReinforcement; node.Value = Json(("StageId", PickStage())); break;
                case TypeBox: node.EventId = EventBox; node.Value = Json(("RewardId", PickReward())); break;
                case TypeWarehouse: node.EventId = EventWarehouse; node.Value = Json(("RewardId", PickReward())); break;
                case TypeForward: node.EventId = EventForward; node.Value = Json(("Step", forwardStep), ("TargetNodeId", forwardTarget)); break;
                case TypeFallBack: node.EventId = EventFallBack; node.Value = Json(("Step", backStep), ("TargetNodeId", backTarget)); break;
                case TypeCardChange: node.EventId = EventCardChange; break;
                case TypeDirectionChange: node.EventId = EventDirectionChange; break;
                case TypeExplore: node.EventId = EventExplore; break;
                case TypeActionPoint:
                    bool morale = Next(s, 2) == 0;
                    node.EventId = morale ? EventHighMorale : EventBonusAction;
                    node.Value = Json(("ExtraActionPoint", morale ? HighMoraleActions : BonusActionActions));
                    break;
                default: node.EventId = EventEmptyRoad; break;
            }
            s.Nodes.Add(node);
        }
        // Hide a few nodes behind "Unknown Area" (never the sensing node, which reveals them).
        List<MaintainerActionNodeState> hideable = s.Nodes.Where(x => x.NodeId > 0 && x.NodeType != TypeExplore).ToList();
        Shuffle(s, hideable);
        foreach (MaintainerActionNodeState node in hideable.Take(Math.Min(3 + Next(s, 2), hideable.Count))) node.Revealed = false;

        s.Cards = [];
        for (int i = 0; i < cfg.MaxHandCardCount; i++) s.Cards.Add(DrawCard(s, cfg));
    }

    private static int DrawCard(MaintainerActionState s, MaintainerActionConfigTable cfg) => 1 + Next(s, cfg.MaxCardNumber);

    private static void DailyReset(MaintainerActionState s, long now)
    {
        s.Day = TaskModule.CurrentDailyResetPeriod(now);
        s.UsedActionCount = 0;
        s.ExtraActionCount = 0;
        s.StartBonusClaimed = false;
    }

    // ---- wire projection ----

    private static NotifyMaintainerActionData.NotifyMaintainerActionDataNode Wire(MaintainerActionNodeState node) => node.Revealed
        ? new() { NodeId = node.NodeId, NodeType = node.NodeType, EventId = node.EventId, Value = node.Value! }
        : new() { NodeId = node.NodeId, NodeType = TypeUnKnow, EventId = EventUnknownArea, Value = null! };

    private static NotifyMaintainerActionData.NotifyMaintainerActionDataNode Wire(MaintainerActionEventReceipt r) =>
        new() { NodeId = r.NodeId, NodeType = r.NodeType, EventId = r.EventId, Value = r.Value! };

    private static long ResetTime(long now) => now + TaskModule.RemainingSecondsInWeeklyResetPeriod(now);

    private static NotifyMaintainerActionData Project(Session session, MaintainerActionState s, long now) => new()
    {
        Id = s.ConfigId,
        ResetTime = checked((uint)ResetTime(now)),
        Nodes = s.Nodes.Select(node => Wire(node)).ToList(),
        // Local single-player: only the owner stands on the board.
        Players = [new()
        {
            PlayerId = checked((uint)session.player.PlayerData.Id),
            PlayerName = session.player.PlayerData.Name,
            HeadPortraitId = checked((uint)session.player.PlayerData.CurrHeadPortraitId),
            HeadFrameId = checked((int)session.player.PlayerData.CurrHeadFrameId),
            NodeId = s.PlayerNodeId,
            IsNodeTriggered = s.Triggered,
            Reverse = s.Reverse
        }],
        Cards = [.. s.Cards],
        FightWinCount = s.FightWinCount,
        BoxCount = s.BoxCount,
        UsedActionCount = s.UsedActionCount,
        ExtraActionCount = s.ExtraActionCount,
        HasWarehouseNode = s.HasWarehouseNode,
        WarehouseFinishCount = s.WarehouseFinishCount,
        // Mentor system is absent: mentor nodes are never generated.
        HasMentorNode = false,
        MentorStatus = 0
    };

    /// <summary>Login projection of the persisted board; null while the mode is closed for this player.</summary>
    public static NotifyMaintainerActionData? BuildNotify(Session session)
    {
        MaintainerActionConfigTable? cfg = ActiveConfig();
        if (cfg is null || !FeatureAvailable(session)) return null;
        long now = Clock().ToUnixTimeSeconds();
        Mutate(session, s =>
        {
            if (s.ConfigId == cfg.Id && s.Week == TaskModule.CurrentWeeklyResetPeriod(now))
            {
                bool repaired = ClampRelocations(s, cfg.NodeCount + 1);
                if (s.Day == TaskModule.CurrentDailyResetPeriod(now)) return repaired;
                DailyReset(s, now);
                return true;
            }
            BuildBoard(session, cfg, now);
            return true;
        });
        return Project(session, session.player.MaintainerAction, now);
    }

    /// <summary>Applies the Starting Point clamp from <see cref="BuildBoard"/> to a board generated before it existed.</summary>
    private static bool ClampRelocations(MaintainerActionState s, int ring)
    {
        bool changed = false;
        foreach (MaintainerActionNodeState node in s.Nodes.Where(x => x.NodeType is TypeForward or TypeFallBack && x.Value is not null))
        {
            JsonElement doc = JsonDocument.Parse(node.Value!).RootElement;
            int step = doc.GetProperty("Step").GetInt32(), target = doc.GetProperty("TargetNodeId").GetInt32();
            int clamped = node.NodeType == TypeForward
                ? (node.NodeId + step >= ring ? 0 : node.NodeId + step)
                : Math.Max(node.NodeId - step, 0);
            if (clamped == target) continue;
            int travelled = node.NodeType == TypeForward ? (clamped == 0 ? ring - node.NodeId : step) : node.NodeId - clamped;
            node.Value = Json(("Step", travelled), ("TargetNodeId", clamped));
            changed = true;
        }
        return changed;
    }

    /// <summary>Applies daily/weekly rollovers of the board login created, pushing the client's reset notifies.</summary>
    public static void EnsureResets(Session session)
    {
        MaintainerActionConfigTable? cfg = ActiveConfig();
        if (cfg is null || !FeatureAvailable(session)) return;
        MaintainerActionState s = session.player.MaintainerAction;
        // Login (BuildNotify) creates the first board; a player who unlocks the mode mid-session receives it on next login.
        if (s.ConfigId == 0) return;
        long now = Clock().ToUnixTimeSeconds();
        bool newBoard = s.ConfigId != cfg.Id || s.Week != TaskModule.CurrentWeeklyResetPeriod(now);
        if (!newBoard && s.Day == TaskModule.CurrentDailyResetPeriod(now)) return;
        Mutate(session, state =>
        {
            if (newBoard) BuildBoard(session, cfg, now); else DailyReset(state, now);
            return true;
        });
        if (newBoard) session.SendPush(Project(session, session.player.MaintainerAction, now));
        else session.SendPush(new NotifyMaintainerActionDailyReset { UsedActionCount = s.UsedActionCount, ExtraActionCount = s.ExtraActionCount });
    }

    /// <summary>Runs a mutation, persists it, and restores the in-memory board if the save fails.</summary>
    private static void Mutate(Session session, Func<MaintainerActionState, bool> action)
    {
        MaintainerActionState backup = BsonSerializer.Deserialize<MaintainerActionState>(session.player.MaintainerAction.ToBson());
        try
        {
            if (action(session.player.MaintainerAction)) session.player.SaveChecked();
        }
        catch
        {
            session.player.MaintainerAction = backup;
            throw;
        }
    }

    private static MaintainerActionConfigTable? OpenConfig(Session session)
    {
        MaintainerActionConfigTable? cfg = ActiveConfig();
        if (cfg is null || !FeatureAvailable(session)) return null;
        EnsureResets(session);
        MaintainerActionState s = session.player.MaintainerAction;
        return s.ConfigId == cfg.Id && s.Nodes.Count == cfg.NodeCount + 1 ? cfg : null;
    }

    private static void AddRecord(MaintainerActionState s, long now, int eventId, params int[] values)
    {
        s.Records.Insert(0, new MaintainerActionRecordState { Time = now, EventId = eventId, Values = [.. values] });
        if (s.Records.Count > RecordLimit) s.Records.RemoveRange(RecordLimit, s.Records.Count - RecordLimit);
    }

    // ---- requests ----

    [RequestPacketHandler("MaintainerActionRecordRequest")]
    public static void MaintainerActionRecordRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<MaintainerActionRecordRequest>();
        if (OpenConfig(session) is null)
        {
            session.SendResponse(new MaintainerActionRecordResponse { Code = NotOpen }, packet.Id);
            return;
        }
        session.SendResponse(new MaintainerActionRecordResponse
        {
            Records = session.player.MaintainerAction.Records
                .Select(x => new MaintainerActionRecord { Time = x.Time, EventId = x.EventId, EventValues = [.. x.Values] }).ToList()
        }, packet.Id);
    }

    /// <summary>Mirrors XUiPanelIntermediate.CreateRoute (CradRoute): a card stops early on the start node.</summary>
    internal static List<int> CardRoute(int from, bool reverse, int card, int ring)
    {
        List<int> route = [];
        int current = from;
        bool inStart = from == 0, first = true;
        for (int index = 1; index <= card + 1; index++)
        {
            route.Add(current);
            if (!first && current == 0) break;
            current = !reverse
                ? (current < ring - 1 ? current + 1 : 0)
                : (current > 0 ? current - 1 : (!inStart ? 0 : ring - 1));
            first = false;
        }
        return route;
    }

    [RequestPacketHandler("MaintainerActionPlayCardRequest")]
    public static void MaintainerActionPlayCardRequestHandler(Session session, Packet.Request packet)
    {
        MaintainerActionPlayCardRequest request = packet.Deserialize<MaintainerActionPlayCardRequest>();
        MaintainerActionConfigTable? cfg = OpenConfig(session);
        int code = 0, nodeId = 0, newCard = 0;
        if (cfg is null) code = NotOpen;
        else
        {
            long now = Clock().ToUnixTimeSeconds();
            Mutate(session, s =>
            {
                if (!s.Cards.Contains(request.Card)) { code = WrongCard; return false; }
                // Fights are optional: the client's UseCard never checks the trigger state and soft-locks on any error,
                // so a player standing on an unwon fight node may move on. Every other node resolves on landing.
                if (!s.Triggered && FightNode(s) is null) { code = NodeNotTriggered; return false; }
                if (s.UsedActionCount >= cfg.MaxDailyActionCount + s.ExtraActionCount) { code = DailyActionPointLimit; return false; }
                if (cfg.MaxCardNumber < 1) { code = CardPoolError; return false; }
                List<int> route = CardRoute(s.PlayerNodeId, s.Reverse, request.Card, cfg.NodeCount + 1);
                s.PlayerNodeId = nodeId = route[^1];
                s.Triggered = false;
                s.Landing++;
                s.LastEvent = null;
                s.UsedActionCount++;
                s.Cards.Remove(request.Card);
                s.Cards.Add(newCard = DrawCard(s, cfg));
                AddRecord(s, now, EventMove, route.Count - 1);
                return true;
            });
        }
        if (code == 0)
            TaskModule.AddPassportConditionProgress(session, 44001, 1);
        session.SendResponse(new MaintainerActionPlayCardResponse { Code = code, NodeId = nodeId, NewCard = newCard }, packet.Id);
    }

    private static List<RewardGoodsTable> StageDrops(Session session, int stageId)
    {
        StageTable stage = Stages.Value[stageId];
        StageLevelControlTable? control = FightModule.ResolveStageLevelControl((uint)stageId, (int)session.player.PlayerData.Level);
        // Same repeat-clear rule as generic settlement: drop ids first, display ids only when no drop resolves.
        List<RewardGoodsTable> goods = new[] { stage.FinishDropId, control?.FinishDropId }.OfType<int>().Where(x => x > 0).Distinct()
            .SelectMany(RewardHandler.GetRewardGoods).ToList();
        if (goods.Count == 0)
            goods = new[] { stage.FinishRewardShow, control?.FinishRewardShow }.OfType<int>().Where(x => x > 0).Distinct()
                .SelectMany(RewardHandler.GetRewardGoods).ToList();
        return goods;
    }

    private static RewardApplicationResult? Grant(Session session, MaintainerActionState s, string kind, List<RewardGoodsTable> goods) =>
        goods.Count == 0 ? null : RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant($"maintaineraction:{session.player.PlayerData.Id}:{s.ConfigId}:{s.Week}:{s.Landing}:{kind}", goods)], session);

    [RequestPacketHandler("MaintainerActionNodeEventRequest")]
    public static void MaintainerActionNodeEventRequestHandler(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<MaintainerActionNodeEventRequest>();
        MaintainerActionConfigTable? cfg = OpenConfig(session);
        if (cfg is null)
        {
            session.SendResponse(new MaintainerActionNodeEventResponse { Code = NotOpen }, packet.Id);
            return;
        }
        long now = Clock().ToUnixTimeSeconds();
        MaintainerActionNodeEventResponse response = new();
        RewardApplicationResult? reward = null;
        Mutate(session, s => Resolve(session, cfg, s, now, response, out reward));
        reward?.SendPushes(session);
        session.SendResponse(response, packet.Id);
    }

    private static bool Resolve(Session session, MaintainerActionConfigTable cfg, MaintainerActionState s, long now,
        MaintainerActionNodeEventResponse response, out RewardApplicationResult? reward)
    {
        reward = null;
        MaintainerActionNodeState? node = s.Nodes.FirstOrDefault(x => x.NodeId == s.PlayerNodeId);
        if (node is null) { response.Code = CurrentNodeException; return false; }
        if (s.Triggered)
        {
            // Retry of the accepted event of this landing replays the identical outcome.
            if (s.LastEvent is { } receipt && receipt.Landing == s.Landing && receipt.NodeId == node.NodeId)
            {
                response.Node = Wire(receipt);
                return false;
            }
            response.Code = NodeIsTriggered;
            response.Node = Wire(node);
            return false;
        }

        // An unexplored node shows its real face at the moment the player resolves it.
        node.Revealed = true;
        string? value = node.Value;
        int eventId = node.EventId;
        JsonElement Doc() => JsonDocument.Parse(node.Value ?? "{}").RootElement;
        bool Fail(int code)
        {
            response.Code = code;
            s.Triggered = true;
            return true;
        }
        void Accept()
        {
            s.Triggered = true;
            s.LastEvent = new MaintainerActionEventReceipt { Landing = s.Landing, NodeId = node.NodeId, NodeType = node.NodeType, EventId = eventId, Value = value };
        }
        switch (node.NodeType)
        {
            case TypeStart:
                eventId = s.StartBonusClaimed ? EventStart : EventStartBonus;
                int bonus = s.StartBonusClaimed ? 0 : StartBonusActions;
                value = Json(("ExtraActionPoint", bonus));
                s.StartBonusClaimed = true;
                s.ExtraActionCount += bonus;
                AddRecord(s, now, eventId, eventId == EventStartBonus ? new[] { bonus } : Array.Empty<int>());
                Accept();
                break;
            case TypeFight:
                // Resolved by the fight itself (PreFight/FightSettle); the node only presents its stage.
                if (s.FightWinCount >= cfg.MaxFightWinCount) { response.Node = Wire(node); return Fail(FightFinish); }
                response.Node = Wire(node);
                return true;
            case TypeBox:
                if (s.BoxCount >= cfg.MaxBoxCount) { response.Node = Wire(node); return Fail(BoxFinish); }
                reward = Grant(session, s, "box", RewardHandler.GetRewardGoods(Doc().GetProperty("RewardId").GetInt32()));
                s.BoxCount++;
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeWarehouse:
                if (s.WarehouseFinishCount >= cfg.MaxWarehouseFinishCount) { response.Node = Wire(node); return Fail(WarehouseFinish); }
                reward = Grant(session, s, "warehouse", RewardHandler.GetRewardGoods(Doc().GetProperty("RewardId").GetInt32()));
                s.WarehouseFinishCount++;
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeSimulationFight:
                if (s.FightWinCount >= cfg.MaxFightWinCount) { response.Node = Wire(node); return Fail(FightFinish); }
                List<RewardGoodsTable> drops = StageDrops(session, Doc().GetProperty("StageId").GetInt32());
                reward = Grant(session, s, "simulation", drops);
                s.FightWinCount++;
                value = Json(("StageId", Doc().GetProperty("StageId").GetInt32()),
                    ("RewardGoodsList", (reward?.RewardGoods ?? []).Select(x => new Dictionary<string, object>
                    { ["RewardType"] = x.RewardType, ["TemplateId"] = x.TemplateId, ["Count"] = x.Count, ["Level"] = x.Level,
                      ["Quality"] = x.Quality, ["Grade"] = x.Grade, ["Breakthrough"] = x.Breakthrough, ["ConvertFrom"] = x.ConvertFrom,
                      ["ShowQuality"] = x.ShowQuality, ["Id"] = x.Id, ["IsGift"] = x.IsGift, ["RewardMulti"] = x.RewardMulti }).ToList()));
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeForward:
            case TypeFallBack:
                // The client moves to TargetNodeId and asks for that node's event next: a fresh landing.
                int target = Doc().GetProperty("TargetNodeId").GetInt32();
                AddRecord(s, now, eventId, Doc().GetProperty("Step").GetInt32());
                Accept();
                s.PlayerNodeId = target;
                s.Triggered = false;
                s.Landing++;
                break;
            case TypeCardChange:
                int oldCard = s.Cards[Next(s, s.Cards.Count)], newCard = DrawCard(s, cfg);
                s.Cards.Remove(oldCard);
                s.Cards.Add(newCard);
                value = Json(("OldCard", oldCard), ("NewCard", newCard));
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeDirectionChange:
                s.Reverse = !s.Reverse;
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeActionPoint:
                int extra = Doc().GetProperty("ExtraActionPoint").GetInt32();
                s.ExtraActionCount += extra;
                AddRecord(s, now, eventId, extra);
                Accept();
                break;
            case TypeExplore:
                List<MaintainerActionNodeState> hidden = s.Nodes.Where(x => !x.Revealed && x.NodeId != node.NodeId).ToList();
                if (hidden.Count == 0) { response.Node = Wire(node); return Fail(ExplorationIsComplete); }
                MaintainerActionNodeState sensed = hidden[Next(s, hidden.Count)];
                sensed.Revealed = true;
                NotifyMaintainerActionData.NotifyMaintainerActionDataNode inner = Wire(sensed);
                value = Json(("Node", new Dictionary<string, object?>
                    { ["NodeId"] = inner.NodeId, ["NodeType"] = inner.NodeType, ["EventId"] = inner.EventId, ["Value"] = inner.Value }));
                AddRecord(s, now, eventId);
                Accept();
                break;
            case TypeNone:
                AddRecord(s, now, eventId);
                Accept();
                break;
            default:
                response.Code = NodeEventValueError;
                return true;
        }
        response.Node = new() { NodeId = node.NodeId, NodeType = node.NodeType, EventId = eventId, Value = value! };
        return true;
    }

    // ---- fight integration (generic PreFight / FightSettle remain the combat owners) ----

    private static MaintainerActionNodeState? FightNode(MaintainerActionState s) =>
        s.Nodes.FirstOrDefault(x => x.NodeId == s.PlayerNodeId) is { Revealed: true, NodeType: TypeFight } node ? node : null;

    private static int NodeStage(MaintainerActionNodeState node) => JsonDocument.Parse(node.Value!).RootElement.GetProperty("StageId").GetInt32();

    /// <summary>0 when the stage is not an Operation Guardians stage or the player may fight it now.</summary>
    public static int ValidatePreFight(Session session, PreFightRequest.PreFightRequestPreFightData request)
    {
        if (!IsStage(request.StageId)) return 0;
        if (request.ChallengeCount != 1 || request.SpeedrunStageId != 0) return StageIdError;
        MaintainerActionConfigTable? cfg = OpenConfig(session);
        if (cfg is null) return NotOpen;
        MaintainerActionState s = session.player.MaintainerAction;
        if (FightNode(s) is not { } node || s.Triggered) return NodeTypeIsNotFight;
        if (NodeStage(node) != (int)request.StageId) return StageIdError;
        return s.FightWinCount >= cfg.MaxFightWinCount ? FightFinish : 0;
    }

    /// <summary>Counts a won, authorised Operation Guardians fight once and pushes the client's win notify.</summary>
    internal static void RecordFightWin(Session session, FightSettleResult result)
    {
        if (!result.IsWin || result.IsForceExit || !IsStage(result.StageId)
            || session.fight?.PreFight.PreFightData.StageId != result.StageId) return;
        try
        {
            long now = Clock().ToUnixTimeSeconds();
            bool counted = false;
            Mutate(session, s =>
            {
                MaintainerActionConfigTable? cfg = ActiveConfig();
                if (cfg is null || s.Triggered || FightNode(s) is not { } node || NodeStage(node) != (int)result.StageId
                    || s.FightWinCount >= cfg.MaxFightWinCount) return false;
                s.FightWinCount++;
                s.Triggered = true;
                AddRecord(s, now, EventFight, (int)result.StageId);
                return counted = true;
            });
            if (counted) session.SendPush(new NotifyMaintainerActionFightWin { FightWinCount = session.player.MaintainerAction.FightWinCount });
        }
        catch (Exception exception)
        {
            // Rewards and stage progress are already committed; the node stays unresolved so the fight can be replayed.
            session.log.Error($"Failed to record Maintainer Action fight win: {exception}");
        }
    }
}
