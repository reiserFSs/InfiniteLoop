using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.robot;
using AscNet.Table.V2.client.functional;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.functional;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class BossInshotEnterNextTowerRequest { }
[MessagePackObject(true)] public sealed class BossInshotEnterNextTowerResponse { public int Code { get; set; } public BossInshotTowerData? NextTowerData { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerSelectBossRequest { public int TowerId { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerSelectBossResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerSelectBossAfterAllPassRequest { public int TowerId { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerSelectBossAfterAllPassResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerQueryRankRequest { public int CharacterCfgId { get; set; } public int BossId { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotTowerQueryRankResponse { public int Code { get; set; } public int TowerId { get; set; } public int Score { get; set; } public int Rank { get; set; } public int CharacterId { get; set; } public int TotalCount { get; set; } public List<BossInshotRankPlayerInfo> RankPlayerInfos { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BossInshotRankPlayerInfo { public long Id { get; set; } public string Name { get; set; } = string.Empty; public uint HeadPortraitId { get; set; } public uint HeadFrameId { get; set; } public int Score { get; set; } public int TowerId { get; set; } public int CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class NotifyBossInshotData { public BossInshotData BossInshotData { get; set; } = new(); }
[MessagePackObject(true)] public sealed class NotifyBossInshotPlayback { public bool IsPlayback { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotData { public int ActivityId { get; set; } public bool IsPassTeach { get; set; } public List<BossInshotPassStageData> PassStageDatas { get; set; } = new(); public List<BossInshotBossUnlockData> BossUnlockDatas { get; set; } = new(); public List<BossInshotCharacterData> CharacterDatas { get; set; } = new(); public List<object> RecordForRanks { get; set; } = new(); public int CurrentTowerId { get; set; } public Dictionary<int, BossInshotTowerData> TowerDataDict { get; set; } = new(); public Dictionary<int, object> TowerRankDataDict { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BossInshotPassStageData { public int StageId { get; set; } public int MaxScore { get; set; } }
[MessagePackObject(true)] public sealed class BossInshotBossUnlockData { public int BossId { get; set; } public List<int> DifficultySet { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BossInshotCharacterData { public int CharacterId { get; set; } public int DefaultTalentId { get; set; } public List<int> SelectTalentIds { get; set; } = new(); public List<int> UnlockTalentIds { get; set; } = new(); public int TotalScore { get; set; } public List<int> PassStageSet { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BossInshotTowerData { public int TowerId { get; set; } public List<int> DrawStageIds { get; set; } = new(); public int SelectStageId { get; set; } public int SelectStageIdAfterAllPass { get; set; } public bool IsPass { get; set; } public int TriggerProtectCount { get; set; } public Dictionary<int, BossInshotBossMaxScoreData> BossMaxScoreDict { get; set; } = new(); }
[MessagePackObject(true)] public sealed class BossInshotBossMaxScoreData { public Dictionary<int, int> CharacterScoreDict { get; set; } = new(); }

internal static class BossInshotModule
{
    private const int NotOpen = 20215001, NeedPassTeach = 20215009, BossLocked = 20215015, InvalidRank = 20215018, TowerConditionNotMet = 20215019;
    private const int TowerIdError = 20215020, FightDataError = 20215022, PreFightDataError = 20215023, PlayerTowerDataError = 20215025, NotMatchStage = 20215026;
    private const int NotSelectable = 20215027, PassedSelection = 20215028, RepeatSelection = 20215029, SelectionMissing = 20215030, PreviousNotPassed = 20215031, TowerLocked = 20215032, NotAllPassed = 20215033, InvalidLineup = 20215035;
    private const int LocalRankLimit = 100;
    private static readonly Lazy<List<BossInshotActivityTable>> Activities = new(() => TableReaderV2.Parse<BossInshotActivityTable>());
    private static readonly Lazy<Dictionary<int, BossInshotTowerTable>> Towers = new(() => TableReaderV2.Parse<BossInshotTowerTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, BossInshotTowerStageTable>> TowerStages = new(() => TableReaderV2.Parse<BossInshotTowerStageTable>().ToDictionary(x => x.StageId));
    private static readonly Lazy<Dictionary<int, BossInshotStageTable>> Stages = new(() => TableReaderV2.Parse<BossInshotStageTable>().ToDictionary(x => x.StageId));
    private static readonly Lazy<Dictionary<int, BossInshotCharacterTable>> Characters = new(() => TableReaderV2.Parse<BossInshotCharacterTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, RobotTable>> Robots = new(() => TableReaderV2.Parse<RobotTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<List<BossInshotTalentTable>> Talents = new(() => TableReaderV2.Parse<BossInshotTalentTable>());
    private static readonly Lazy<Dictionary<int, BossInshotScoreTable>> Scores = new(() => TableReaderV2.Parse<BossInshotScoreTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(x => x.Id));
    private static readonly Lazy<int[]> FunctionOpenConditions = new(() =>
    {
        HashSet<int> functionIds = TableReaderV2.Parse<SkipFunctionalTable>()
            .Where(row => row.UiName == "SkipToBossInshot" && row.FunctionalId is > 0)
            .Select(row => row.FunctionalId!.Value)
            .ToHashSet();
        return TableReaderV2.Parse<FunctionalOpenTable>()
            .Where(row => functionIds.Contains(row.Id))
            .SelectMany(row => row.Condition)
            .Where(conditionId => conditionId > 0)
            .Distinct()
            .OrderBy(conditionId => conditionId)
            .ToArray();
    });
    internal static bool PrepareLogin(Player player, DateTimeOffset now)
    {
        BossInshotActivityTable? activity = SelectOpenActivity(now);
        if (activity is null || !FunctionGateSatisfied(player))
        {
            if (player.BossInshot is null
                || (player.BossInshot.AuthorizedActivityId == 0 && player.BossInshot.AuthorizedTimeIds is not { Count: > 0 }))
                return false;

            player.BossInshot.AuthorizedActivityId = 0;
            player.BossInshot.AuthorizedTimeIds = new();
            player.Save();
            return true;
        }

        BossInshotState state;
        bool changed;
        if (player.BossInshot?.ActivityId == activity.Id)
        {
            state = player.BossInshot;
            changed = false;
        }
        else
        {
            state = new BossInshotState { ActivityId = activity.Id };
            player.BossInshot = state;
            changed = true;
        }
        if (state.AuthorizedTimeIds is null)
        {
            state.AuthorizedTimeIds = new();
            changed = true;
        }

        List<int> authorizedTimeIds = AuthorizedTimeIds(activity, state, now);
        if (state.AuthorizedActivityId != activity.Id)
        {
            state.AuthorizedActivityId = activity.Id;
            changed = true;
        }
        if (!state.AuthorizedTimeIds.SequenceEqual(authorizedTimeIds))
        {
            state.AuthorizedTimeIds = authorizedTimeIds;
            changed = true;
        }
        if (changed)
            player.Save();
        return changed;
    }

    private static BossInshotActivityTable? SelectOpenActivity(DateTimeOffset now) =>
        Activities.Value
            .Where(activity => activity.TimeId is > 0 && ActivityScheduleService.IsOpen(activity.TimeId.Value, now))
            .OrderByDescending(activity => activity.Id)
            .FirstOrDefault();

    private static List<int> AuthorizedTimeIds(BossInshotActivityTable activity, BossInshotState state, DateTimeOffset now)
    {
        List<int> timeIds = [activity.TimeId!.Value];
        if (state.IsPassTeach
            && activity.TowerConditions is > 0
            && ConditionSatisfied(state, activity.TowerConditions.Value, new HashSet<int>()))
        {
            timeIds.AddRange(Towers.Value.Values
                .Select(tower => tower.TimeId)
                .Where(timeId => timeId > 0 && ActivityScheduleService.IsOpen(timeId, now)));
        }
        return timeIds.Distinct().OrderBy(timeId => timeId).ToList();
    }

    private static bool FunctionGateSatisfied(Player player) =>
        FunctionOpenConditions.Value.Length > 0
        && FunctionOpenConditions.Value.All(conditionId => PlayerConditionSatisfied(player, conditionId, new HashSet<int>()));

    private static bool PlayerConditionSatisfied(Player player, int conditionId, HashSet<int> visiting)
    {
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition) || !visiting.Add(conditionId))
            return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                bool any = condition.Formula.Contains('|');
                if (any && condition.Formula.Contains('&'))
                    return false;
                string[] terms = condition.Formula.Split(any ? '|' : '&', StringSplitOptions.RemoveEmptyEntries);
                return terms.Length > 0 && (any ? terms.Any(Evaluate) : terms.All(Evaluate));

                bool Evaluate(string term)
                {
                    string value = term.Trim();
                    bool negate = value.StartsWith('!');
                    if (negate)
                        value = value[1..];
                    return int.TryParse(value, out int child)
                        && (PlayerConditionSatisfied(player, child, visiting) != negate);
                }
            }
            return condition.Type == 10101
                && condition.Params.Count > 0
                && condition.Params.All(requiredLevel => player.PlayerData.Level >= requiredLevel);
        }
        finally
        {
            visiting.Remove(conditionId);
        }
    }


    private static BossInshotActivityTable? ActiveActivity(Player player)
    {
        BossInshotState? state = player.BossInshot;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BossInshotActivityTable? activity = SelectOpenActivity(now);
        return state is not null
            && activity is not null
            && FunctionGateSatisfied(player)
            && state.ActivityId == activity.Id
            && state.AuthorizedActivityId == activity.Id
            && state.AuthorizedTimeIds.Contains(activity.TimeId!.Value)
            ? activity
            : null;
    }

    internal static NotifyBossInshotData BuildNotifyBossInshotData(Player player)
    {
        BossInshotActivityTable? activity = ActiveActivity(player);
        if (activity is null) return new();
        BossInshotState state = Reconcile(player, activity, null);
        return new NotifyBossInshotData { BossInshotData = BuildData(state, activity) };
    }

    internal static NotifyBossInshotPlayback BuildNotifyBossInshotPlayback(Player player)
    {
        BossInshotActivityTable? activity = ActiveActivity(player);
        bool hasReplayableStage = activity is not null && Stages.Value.Values.Any(stage =>
            stage.StageId != activity.TeachStageId && activity.BossIds.Contains(stage.BossId));
        return new NotifyBossInshotPlayback { IsPlayback = hasReplayableStage };
    }
    internal static bool IsStage(uint stageId) => stageId <= int.MaxValue
        && (TowerStages.Value.ContainsKey((int)stageId) || Stages.Value.ContainsKey((int)stageId));

    [RequestPacketHandler("BossInshotEnterNextTowerRequest")]
    public static void Enter(Session session, Packet.Request packet)
    {
        BossInshotState? original = session.player.BossInshot;
        if (!TryActive(session, out BossInshotActivityTable activity, out BossInshotState state)) { session.SendResponse(new BossInshotEnterNextTowerResponse { Code = NotOpen }, packet.Id); return; }
        if (!TowerAccessAllowed(session, activity, state, out int gate)) { session.SendResponse(new BossInshotEnterNextTowerResponse { Code = gate }, packet.Id); return; }
        int next = state.CurrentTowerId == 0 ? Towers.Value.Keys.Min() : state.CurrentTowerId + 1;
        if (state.CurrentTowerId != 0 && state.Towers.FirstOrDefault(x => x.TowerId == state.CurrentTowerId)?.IsPass != true) { session.SendResponse(new BossInshotEnterNextTowerResponse { Code = PreviousNotPassed }, packet.Id); return; }
        if (!Towers.Value.TryGetValue(next, out BossInshotTowerTable? table)) { session.SendResponse(new BossInshotEnterNextTowerResponse { Code = TowerIdError }, packet.Id); return; }
        if (table.TimeId <= 0 || !state.AuthorizedTimeIds.Contains(table.TimeId)) { session.SendResponse(new BossInshotEnterNextTowerResponse { Code = TowerConditionNotMet }, packet.Id); return; }

        BossInshotState candidate = CloneState(state);
        BossInshotTowerState tower = candidate.Towers.FirstOrDefault(x => x.TowerId == next)
            ?? InitializeTower(activity.Id, session.player.PlayerData.Id, table);
        if (!candidate.Towers.Contains(tower))
            candidate.Towers.Add(tower);
        candidate.CurrentTowerId = next;
        session.player.BossInshot = candidate;
        try
        {
            session.player.Save();
        }
        catch (Exception exception)
        {
            session.player.BossInshot = original;
            session.log.Error($"Failed to persist BossInshot tower entry: {exception}");
            session.SendResponse(new BossInshotEnterNextTowerResponse { Code = PreFightDataError }, packet.Id);
            return;
        }
        session.SendResponse(new BossInshotEnterNextTowerResponse { Code = 0, NextTowerData = BuildTower(tower) }, packet.Id);
    }

    [RequestPacketHandler("BossInshotTowerSelectBossRequest")]
    public static void Select(Session session, Packet.Request packet)
    {
        var req = packet.Deserialize<BossInshotTowerSelectBossRequest>(); int code = SelectCore(session, req.TowerId, req.StageId, false);
        session.SendResponse(new BossInshotTowerSelectBossResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("BossInshotTowerSelectBossAfterAllPassRequest")]
    public static void SelectAfterPass(Session session, Packet.Request packet)
    {
        var req = packet.Deserialize<BossInshotTowerSelectBossAfterAllPassRequest>(); int code = SelectCore(session, req.TowerId, req.StageId, true);
        session.SendResponse(new BossInshotTowerSelectBossAfterAllPassResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("BossInshotTowerQueryRankRequest")]
    public static void QueryRank(Session session, Packet.Request packet)
    {
        BossInshotTowerQueryRankRequest request = packet.Deserialize<BossInshotTowerQueryRankRequest>();
        BossInshotActivityTable? activity = ActiveActivity(session.player);
        if (activity is null)
        {
            session.SendResponse(new BossInshotTowerQueryRankResponse { Code = NotOpen }, packet.Id);
            return;
        }
        if (!activity.CharacterIds.Contains(request.CharacterCfgId)
            || !activity.TowerBossIds.Contains(request.BossId)
            || !ResolveCharacter(request.CharacterCfgId, out int characterId))
        {
            session.SendResponse(new BossInshotTowerQueryRankResponse { Code = InvalidRank }, packet.Id);
            return;
        }
        if (!TryFlushPendingRankUpdates(session))
        {
            session.SendResponse(new BossInshotTowerQueryRankResponse { Code = InvalidRank }, packet.Id);
            return;
        }

        try
        {
            FilterDefinition<BossInshotRankEntry> participants =
                Builders<BossInshotRankEntry>.Filter.And(
                    Builders<BossInshotRankEntry>.Filter.Eq(entry => entry.ActivityId, activity.Id),
                    Builders<BossInshotRankEntry>.Filter.Eq(entry => entry.BossId, request.BossId),
                    Builders<BossInshotRankEntry>.Filter.Eq(entry => entry.CharacterId, characterId));
            long totalCount = BossInshotRankEntry.collection.CountDocuments(participants);
            string ownId = BossInshotRankEntry.BuildId(
                activity.Id,
                request.BossId,
                characterId,
                session.player.PlayerData.Id);
            BossInshotRankEntry? own = BossInshotRankEntry.collection
                .Find(entry => entry.Id == ownId)
                .Limit(1)
                .FirstOrDefault();
            long rank = own is null
                ? 0
                : BossInshotRankEntry.collection.CountDocuments(
                    Builders<BossInshotRankEntry>.Filter.And(
                        participants,
                        BetterThan(own))) + 1;
            List<BossInshotRankEntry> leaders = BossInshotRankEntry.collection.Find(participants)
                .SortByDescending(entry => entry.TowerId)
                .ThenByDescending(entry => entry.Score)
                .ThenBy(entry => entry.AchievedAt)
                .ThenBy(entry => entry.PlayerId)
                .Limit(LocalRankLimit)
                .ToList();
            session.SendResponse(new BossInshotTowerQueryRankResponse
            {
                Code = 0,
                CharacterId = characterId,
                TowerId = own?.TowerId ?? 0,
                Score = own?.Score ?? 0,
                Rank = ToProtocolCount(rank),
                TotalCount = ToProtocolCount(totalCount),
                RankPlayerInfos = leaders.Select(entry => new BossInshotRankPlayerInfo
                {
                    Id = entry.PlayerId,
                    Name = entry.Name,
                    HeadPortraitId = ToProtocolId(entry.HeadPortraitId),
                    HeadFrameId = ToProtocolId(entry.HeadFrameId),
                    Score = entry.Score,
                    TowerId = entry.TowerId,
                    CharacterId = characterId
                }).ToList()
            }, packet.Id);
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to query BossInshot tower rank: {exception}");
            session.SendResponse(new BossInshotTowerQueryRankResponse { Code = InvalidRank }, packet.Id);
        }
    }

    private static FilterDefinition<BossInshotRankEntry> BetterThan(BossInshotRankEntry own)
    {
        FilterDefinitionBuilder<BossInshotRankEntry> filter = Builders<BossInshotRankEntry>.Filter;
        return filter.Or(
            filter.Gt(entry => entry.TowerId, own.TowerId),
            filter.And(
                filter.Eq(entry => entry.TowerId, own.TowerId),
                filter.Gt(entry => entry.Score, own.Score)),
            filter.And(
                filter.Eq(entry => entry.TowerId, own.TowerId),
                filter.Eq(entry => entry.Score, own.Score),
                filter.Lt(entry => entry.AchievedAt, own.AchievedAt)),
            filter.And(
                filter.Eq(entry => entry.TowerId, own.TowerId),
                filter.Eq(entry => entry.Score, own.Score),
                filter.Eq(entry => entry.AchievedAt, own.AchievedAt),
                filter.Lt(entry => entry.PlayerId, own.PlayerId)));
    }

    internal static bool TryFlushPendingRankUpdates(Session session)
    {
        try
        {
            FlushPendingRankUpdates(session.player);
            return true;
        }
        catch (Exception exception)
        {
            session.log.Error($"Failed to persist BossInshot rank projection: {exception}");
            return false;
        }
    }

    private static void FlushPendingRankUpdates(Player player)
    {
        BossInshotState? state = player.BossInshot;
        if (state is null)
            return;
        state.PendingRankUpdates ??= new();
        if (state.RankProjectionVersion < 2)
        {
            NormalizeState(state);
            StageAllRankUpdates(state);
            state.RankProjectionVersion = 2;
            player.Save();
        }
        if (state.PendingRankUpdates.Count == 0)
            return;

        List<BossInshotRankUpdate> pending = state.PendingRankUpdates
            .Select(CloneRankUpdate)
            .ToList();
        foreach (BossInshotRankUpdate update in pending)
            PersistRankUpdate(player, update);
        HashSet<string> completedKeys = pending.Select(RankUpdateKey).ToHashSet();
        state.PendingRankUpdates.RemoveAll(update => completedKeys.Contains(RankUpdateKey(update)));
        try
        {
            player.Save();
        }
        catch
        {
            foreach (BossInshotRankUpdate update in pending)
            {
                if (!state.PendingRankUpdates.Any(existing => RankUpdateKey(existing) == RankUpdateKey(update)))
                    state.PendingRankUpdates.Add(update);
            }
            throw;
        }
    }

    private static void StageAllRankUpdates(BossInshotState state)
    {
        foreach ((int bossId, int characterId) in state.Towers
            .SelectMany(tower => tower.Scores)
            .Where(score => score.BossId > 0 && score.CharacterId > 0)
            .Select(score => (score.BossId, score.CharacterId))
            .Distinct())
        {
            StageRankUpdate(state, state.ActivityId, bossId, characterId);
        }
    }

    private static void StageRankUpdate(
        BossInshotState state,
        int activityId,
        int bossId,
        int characterId)
    {
        BossInshotRankUpdate? candidate = state.Towers
            .SelectMany(tower => tower.Scores
                .Where(score => score.BossId == bossId
                    && score.CharacterId == characterId
                    && score.MaxScore > 0)
                .Select(score => new BossInshotRankUpdate
                {
                    ActivityId = activityId,
                    BossId = bossId,
                    CharacterId = characterId,
                    TowerId = tower.TowerId,
                    Score = score.MaxScore,
                    AchievedAt = score.AchievedAt
                }))
            .OrderByDescending(update => update.TowerId)
            .ThenByDescending(update => update.Score)
            .ThenBy(update => update.AchievedAt)
            .FirstOrDefault();
        if (candidate is null)
            return;

        BossInshotRankUpdate? existing = state.PendingRankUpdates.FirstOrDefault(update =>
            RankUpdateKey(update) == RankUpdateKey(candidate));
        if (existing is null)
            state.PendingRankUpdates.Add(candidate);
        else if (IsBetter(candidate.TowerId, candidate.Score, candidate.AchievedAt,
                     existing.TowerId, existing.Score, existing.AchievedAt))
        {
            existing.TowerId = candidate.TowerId;
            existing.Score = candidate.Score;
            existing.AchievedAt = candidate.AchievedAt;
        }
    }

    private static void PersistRankUpdate(Player player, BossInshotRankUpdate update)
    {
        string id = BossInshotRankEntry.BuildId(
            update.ActivityId,
            update.BossId,
            update.CharacterId,
            player.PlayerData.Id);
        BossInshotRankEntry candidate = new()
        {
            Id = id,
            ActivityId = update.ActivityId,
            BossId = update.BossId,
            CharacterId = update.CharacterId,
            PlayerId = player.PlayerData.Id,
            Name = player.PlayerData.Name,
            HeadPortraitId = player.PlayerData.CurrHeadPortraitId,
            HeadFrameId = player.PlayerData.CurrHeadFrameId,
            TowerId = update.TowerId,
            Score = update.Score,
            AchievedAt = update.AchievedAt
        };
        FilterDefinitionBuilder<BossInshotRankEntry> filter = Builders<BossInshotRankEntry>.Filter;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            BossInshotRankEntry? current = BossInshotRankEntry.collection
                .Find(entry => entry.Id == id)
                .Limit(1)
                .FirstOrDefault();
            if (current is not null)
            {
                if (!IsBetter(
                        candidate.TowerId,
                        candidate.Score,
                        candidate.AchievedAt,
                        current.TowerId,
                        current.Score,
                        current.AchievedAt))
                    return;
                ReplaceOneResult replaced = BossInshotRankEntry.collection.ReplaceOne(
                    filter.And(
                        filter.Eq(entry => entry.Id, id),
                        filter.Eq(entry => entry.TowerId, current.TowerId),
                        filter.Eq(entry => entry.Score, current.Score),
                        filter.Eq(entry => entry.AchievedAt, current.AchievedAt)),
                    candidate);
                if (replaced.IsAcknowledged && replaced.MatchedCount == 1)
                    return;
                continue;
            }

            try
            {
                ReplaceOneResult inserted = BossInshotRankEntry.collection.ReplaceOne(
                    filter.And(
                        filter.Eq(entry => entry.Id, id),
                        filter.Exists(entry => entry.ActivityId, false)),
                    candidate,
                    new ReplaceOptions { IsUpsert = true });
                if (inserted.IsAcknowledged
                    && (inserted.MatchedCount == 1 || inserted.UpsertedId is not null))
                    return;
            }
            catch (MongoWriteException exception)
                when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
            }
        }
        throw new MongoException($"Could not persist BossInshot rank entry {id} after concurrent updates.");
    }

    private static bool IsBetter(
        int candidateTower,
        int candidateScore,
        long candidateAchievedAt,
        int currentTower,
        int currentScore,
        long currentAchievedAt) =>
        candidateTower > currentTower
        || candidateTower == currentTower && candidateScore > currentScore
        || candidateTower == currentTower
            && candidateScore == currentScore
            && candidateAchievedAt < currentAchievedAt;

    private static BossInshotRankUpdate CloneRankUpdate(BossInshotRankUpdate update) => new()
    {
        ActivityId = update.ActivityId,
        BossId = update.BossId,
        CharacterId = update.CharacterId,
        TowerId = update.TowerId,
        Score = update.Score,
        AchievedAt = update.AchievedAt
    };

    private static string RankUpdateKey(BossInshotRankUpdate update) =>
        $"{update.ActivityId}:{update.BossId}:{update.CharacterId}";

    private static uint ToProtocolId(long value) =>
        checked((uint)Math.Clamp(value, 0L, uint.MaxValue));

    private static int ToProtocolCount(long count) =>
        count >= int.MaxValue ? int.MaxValue : checked((int)Math.Max(0, count));

    internal static int CalculateScore(IReadOnlyDictionary<int, int> records, int cap)
    {
        long weighted = 0;
        long multiplier = 10000;
        checked
        {
            foreach ((int id, int value) in records)
            {
                if (!Scores.Value.TryGetValue(id, out BossInshotScoreTable? row)
                    || value < 0
                    || row.Type == 2 && value > 1)
                    throw new ArgumentOutOfRangeException(nameof(records));
                long points = row.Score ?? 0;
                if (row.Type == 2)
                    multiplier += (long)value * points;
                else
                    weighted += (long)value * points;
            }
        }
        long rounded = checked((weighted * multiplier + 50000) / 100000);
        return checked((int)Math.Min(Math.Max(0, cap), rounded));
    }

    private sealed record BossInshotPreFightContext(
        BossInshotActivityTable Activity,
        BossInshotStageTable? OrdinaryStage,
        BossInshotTowerStageTable? TowerStage,
        int? TowerId,
        int RobotId,
        int CharacterConfigId,
        int CharacterId)
    {
        public bool IsTower => TowerId.HasValue;
    }

    internal static bool ValidatePreFightRequest(
        Session session,
        PreFightRequest.PreFightRequestPreFightData request,
        out int code) =>
        TryResolvePreFight(session, request, out code, out _);

    private static bool TryResolvePreFight(
        Session session,
        PreFightRequest.PreFightRequestPreFightData request,
        out int code,
        out BossInshotPreFightContext? context)
    {
        code = 0;
        context = null;
        bool claimed = request.BossInshotTowerId.HasValue || IsStage(request.StageId);
        if (!claimed) return false;
        if (request.StageId > int.MaxValue) { code = NotMatchStage; return true; }
        int stageId = (int)request.StageId;
        if (!TryActive(session, out BossInshotActivityTable activity, out BossInshotState state)) { code = NotOpen; return true; }

        BossInshotStageTable? ordinaryStage = Stages.Value.GetValueOrDefault(stageId);
        BossInshotTowerStageTable? towerStage = TowerStages.Value.GetValueOrDefault(stageId);
        int? towerId = request.BossInshotTowerId;
        if (towerId.HasValue)
        {
            if (!TowerAccessAllowed(session, activity, state, out code)) return true;
            if (towerId.Value > state.CurrentTowerId) { code = TowerLocked; return true; }
            if (!Towers.Value.TryGetValue(towerId.Value, out BossInshotTowerTable? towerConfig)
                || !state.Towers.Any(x => x.TowerId == towerId.Value)
                || towerStage is null) { code = TowerIdError; return true; }
            if (towerConfig.TimeId <= 0 || !state.AuthorizedTimeIds.Contains(towerConfig.TimeId)) { code = TowerConditionNotMet; return true; }
            BossInshotTowerState tower = state.Towers.First(x => x.TowerId == towerId.Value);
            int selected = tower.SelectStageIdAfterAllPass != 0 ? tower.SelectStageIdAfterAllPass : tower.SelectStageId;
            if (!towerConfig.Stages.Contains(stageId) || selected != stageId) { code = NotMatchStage; return true; }
        }
        else if (ordinaryStage is null
            || (ordinaryStage.StageId != activity.TeachStageId && !activity.BossIds.Contains(ordinaryStage.BossId)))
        {
            code = NotMatchStage;
            return true;
        }
        else if (ordinaryStage.StageId != activity.TeachStageId && !state.IsPassTeach)
        {
            code = NeedPassTeach;
            return true;
        }
        else if (ordinaryStage.StageId != activity.TeachStageId
            && ordinaryStage.UnlockConditionId is > 0
            && !ConditionSatisfied(state, ordinaryStage.UnlockConditionId.Value, new HashSet<int>()))
        {
            code = BossLocked;
            return true;
        }

        if (request.CardIds is { Count: > 3 }
            || request.RobotIds is null
            || request.RobotIds.Count > 3
            || (request.CardIds?.Any(x => x > 0) ?? false))
        {
            code = InvalidLineup;
            return true;
        }
        List<int> robotIds = request.RobotIds.Where(x => x > 0).ToList();
        if (robotIds.Count != 1)
        {
            code = InvalidLineup;
            return true;
        }
        int robotId = robotIds[0];
        List<int> matchingConfigs = activity.CharacterIds
            .Where(cfg => Characters.Value.TryGetValue(cfg, out BossInshotCharacterTable? character)
                && character.RobotId == robotId)
            .ToList();
        if (matchingConfigs.Count != 1) { code = InvalidLineup; return true; }
        int characterConfigId = matchingConfigs[0];
        if (!ResolveCharacter(characterConfigId, out int characterId)) { code = 20215008; return true; }

        context = new(
            activity,
            ordinaryStage,
            towerStage,
            towerId,
            robotId,
            characterConfigId,
            characterId);
        return true;
    }

    internal static bool ApplyPreFight(Session session, PreFightRequest.PreFightRequestPreFightData request, PreFightResponse response, out int code)
    {
        if (!TryResolvePreFight(session, request, out code, out BossInshotPreFightContext? context))
            return false;
        if (code != 0)
            return true;

        Dictionary<int, dynamic>? npcMap = response.FightData.RoleData.FirstOrDefault(x => x.Id == session.player.PlayerData.Id)?.NpcData;
        if (npcMap is null) { code = FightDataError; return true; }
        int robotId = context!.RobotId;
        KeyValuePair<int, dynamic> deployed = npcMap.FirstOrDefault(pair => Convert.ToInt32(pair.Value.GetType().GetProperty("RobotId")?.GetValue(pair.Value) ?? 0) == robotId);
        if (deployed.Value is null) { code = FightDataError; return true; }
        int npcKey = deployed.Key;
        Dictionary<string, object?> augmented = ((object)deployed.Value).GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(deployed.Value));
        augmented["BossInshotCharacterCfgUid"] = context.CharacterConfigId;
        augmented["EventIds"] = Characters.Value[context.CharacterConfigId].FightEventIds > 0 ? new[] { Characters.Value[context.CharacterConfigId].FightEventIds } : Array.Empty<int>();
        augmented["AttrRateTable"] = new Dictionary<int, int>();
        augmented["BaseAttrs"] = null;
        augmented["CurAttrs"] = null;
        augmented["MaxAttrs"] = null;
        npcMap[npcKey] = augmented;

        int fightEventId = context.IsTower ? context.TowerStage!.FightEventIds : context.OrdinaryStage!.FightEventIds;
        response.FightData.EventIds = fightEventId > 0 ? [(dynamic)fightEventId] : new();
        response.FightData.FightCheckType = 1;
        response.FightData.SegmentFightCheckSecond = 60;
        response.FightData.Restartable = true;
        session.PendingBossInshotFight = new PendingBossInshotFight
        {
            ActivityId = context.Activity.Id,
            TowerId = context.TowerId,
            StageId = request.StageId,
            FightId = response.FightData.FightId,
            CharacterConfigId = context.CharacterConfigId,
            CharacterId = context.CharacterId,
            IsTower = context.IsTower
        };
        return true;
    }

    internal static bool TrySettle(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = null!;
        PendingBossInshotFight? pending = session.PendingBossInshotFight;
        bool claimed = IsStage(result.StageId) || pending is not null;
        if (!claimed)
            return false;

        if (pending is null || result.StageId != pending.StageId || result.FightId != pending.FightId)
        {
            session.PendingBossInshotFight = null;
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }
        if (!result.IsWin || result.IsForceExit)
        {
            session.PendingBossInshotFight = null;
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }

        BossInshotActivityTable? activity = ActiveActivity(session.player);
        BossInshotState? persistedState = session.player.BossInshot;
        if (activity is null
            || persistedState is null
            || persistedState.ActivityId != activity.Id
            || pending.ActivityId != activity.Id)
        {
            session.PendingBossInshotFight = null;
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }
        BossInshotState state = CloneState(persistedState);

        int stageId;
        int leftTime;
        int score;
        try
        {
            stageId = checked((int)pending.StageId);
            leftTime = checked((int)result.LeftTime);
            if (!TryToRecords(result.IntToIntRecord, out Dictionary<int, int> records))
            {
                response = new FightSettleResponse { Code = PreFightDataError };
                return true;
            }
            score = CalculateScore(records, checked((int)(activity.TowerMaxScoreLimit ?? int.MaxValue)));
        }
        catch (Exception exception) when (exception is OverflowException
            or ArgumentOutOfRangeException
            or FormatException
            or InvalidCastException)
        {
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }

        bool isNew = pending.PersistedSettlementScore == score
            && pending.PersistedSettlementWasNewRecord;
        if (pending.IsTower)
        {
            if (pending.TowerId is not int towerId
                || !Towers.Value.TryGetValue(towerId, out BossInshotTowerTable? config)
                || !TowerStages.Value.TryGetValue(stageId, out BossInshotTowerStageTable? stage)
                || !config.Stages.Contains(stageId))
            {
                response = new FightSettleResponse { Code = PreFightDataError };
                return true;
            }

            BossInshotTowerState? tower = state.Towers.FirstOrDefault(candidate => candidate.TowerId == towerId);
            if (tower is null)
            {
                response = new FightSettleResponse { Code = PlayerTowerDataError };
                return true;
            }

            BossInshotTowerScoreState? maximum = tower.Scores.FirstOrDefault(existing =>
                existing.BossId == stage.BossId && existing.CharacterId == pending.CharacterId);
            if (maximum is null)
            {
                maximum = new BossInshotTowerScoreState { BossId = stage.BossId, CharacterId = pending.CharacterId };
                tower.Scores.Add(maximum);
            }
            if (score > maximum.MaxScore)
            {
                maximum.MaxScore = score;
                maximum.AchievedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                isNew = true;
            }
            if (!tower.IsPass)
            {
                if (score >= config.PassScore)
                    tower.IsPass = true;
                else if (config.FailReBackToId >= 0 && tower.TriggerProtectCount >= config.ProtectCount)
                    state.CurrentTowerId = config.FailReBackToId;
                else if (config.FailReBackToId >= 0)
                    tower.TriggerProtectCount++;
            }
            StageRankUpdate(state, activity.Id, stage.BossId, pending.CharacterId);
        }
        else
        {
            if (!Stages.Value.TryGetValue(stageId, out BossInshotStageTable? ordinary)
                || (ordinary.StageId != activity.TeachStageId && !activity.BossIds.Contains(ordinary.BossId)))
            {
                response = new FightSettleResponse { Code = PreFightDataError };
                return true;
            }

            if (ordinary.StageId == activity.TeachStageId)
            {
                state.IsPassTeach = true;
            }
            else
            {
                BossInshotPassStage? globalMaximum = state.PassStages.FirstOrDefault(existing =>
                    existing.StageId == ordinary.StageId);
                int previousGlobal = globalMaximum?.MaxScore ?? 0;
                BossInshotCharacterState? character = state.Characters.FirstOrDefault(existing =>
                    existing.CharacterId == pending.CharacterId);
                int previousCharacter = 0;
                if (character is not null)
                {
                    if (!character.StageMaxScores.TryGetValue(ordinary.StageId, out previousCharacter)
                        && character.PassStageSet.Contains(ordinary.StageId))
                    {
                        previousCharacter = Math.Min(previousGlobal, Math.Max(0, character.TotalScore));
                    }
                }

                int nextCharacterTotal;
                try
                {
                    nextCharacterTotal = checked((character?.TotalScore ?? 0)
                        + Math.Max(0, score - previousCharacter));
                }
                catch (OverflowException)
                {
                    response = new FightSettleResponse { Code = PreFightDataError };
                    return true;
                }

                if (globalMaximum is null)
                {
                    globalMaximum = new BossInshotPassStage { StageId = ordinary.StageId };
                    state.PassStages.Add(globalMaximum);
                }
                if (score > previousGlobal)
                {
                    globalMaximum.MaxScore = score;
                    isNew = true;
                }

                if (character is null)
                {
                    character = new BossInshotCharacterState { CharacterId = pending.CharacterId };
                    state.Characters.Add(character);
                }
                if (!character.PassStageSet.Contains(ordinary.StageId))
                    character.PassStageSet.Add(ordinary.StageId);
                character.StageMaxScores[ordinary.StageId] = Math.Max(previousCharacter, score);
                character.TotalScore = nextCharacterTotal;

                BossInshotBossUnlock? unlock = state.BossUnlocks.FirstOrDefault(existing =>
                    existing.BossId == ordinary.BossId);
                if (unlock is null)
                {
                    unlock = new BossInshotBossUnlock { BossId = ordinary.BossId };
                    state.BossUnlocks.Add(unlock);
                }
                if (!unlock.DifficultySet.Contains(ordinary.Difficulty))
                    unlock.DifficultySet.Add(ordinary.Difficulty);
            }
        }

        session.player.BossInshot = state;
        try
        {
            session.player.Save();
            pending.PersistedSettlementScore = score;
            pending.PersistedSettlementWasNewRecord = isNew;
        }
        catch (Exception exception)
        {
            session.player.BossInshot = persistedState;
            session.log.Error($"Failed to persist BossInshot settlement: {exception}");
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }
        if (!TryFlushPendingRankUpdates(session))
        {
            response = new FightSettleResponse { Code = PreFightDataError };
            return true;
        }
        session.PendingBossInshotFight = null;
        response = new FightSettleResponse
        {
            Code = 0,
            Settle = new FightSettleResponse.FightSettleResponseSettle
            {
                IsWin = true,
                StageId = result.StageId,
                LeftTime = leftTime,
                NpcHpInfo = result.NpcHpInfo,
                ChallengeCount = 0,
                BossInshotSettleResult = new BossInshotSettleResult { Score = score, IsNewRecord = isNew }
            }
        };
        return true;
    }

    private static bool TryToRecords(dynamic? source, out Dictionary<int, int> records)
    {
        records = new();
        if (source is null)
            return true;
        if (source is IDictionary<int, int> typed)
        {
            records = typed.ToDictionary(entry => entry.Key, entry => entry.Value);
            return true;
        }
        if (source is not System.Collections.IDictionary map)
            return false;
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            if (!TryReadProtocolInteger(entry.Key, out int key)
                || !TryReadProtocolInteger(entry.Value, out int value)
                || !records.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static bool TryReadProtocolInteger(object? value, out int result)
    {
        switch (value)
        {
            case sbyte typed: result = typed; return true;
            case byte typed: result = typed; return true;
            case short typed: result = typed; return true;
            case ushort typed: result = typed; return true;
            case int typed: result = typed; return true;
            case uint typed when typed <= int.MaxValue: result = (int)typed; return true;
            case long typed when typed is >= int.MinValue and <= int.MaxValue: result = (int)typed; return true;
            case ulong typed when typed <= int.MaxValue: result = (int)typed; return true;
            default: result = 0; return false;
        }
    }

    private static BossInshotState CloneState(BossInshotState source) =>
        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BossInshotState>(source.ToBson());

    private static int SelectCore(Session session, int towerId, int stageId, bool afterPass)
    {
        BossInshotState? original = session.player.BossInshot;
        if (!TryActive(session, out BossInshotActivityTable activity, out BossInshotState state)) return NotOpen;
        if (!TowerAccessAllowed(session, activity, state, out int gate)) return gate;
        if (!Towers.Value.TryGetValue(towerId, out BossInshotTowerTable? config)) return TowerIdError;
        if (config.TimeId <= 0 || !state.AuthorizedTimeIds.Contains(config.TimeId)) return TowerConditionNotMet;
        BossInshotTowerState? tower = state.Towers.FirstOrDefault(x => x.TowerId == towerId);
        if (tower is null) return PlayerTowerDataError;
        if (config.Type != 2) return NotSelectable;
        if (afterPass)
        {
            if (state.Towers.FirstOrDefault(x => x.TowerId == Towers.Value.Keys.Max())?.IsPass != true) return NotAllPassed;
            if (!config.Stages.Contains(stageId)) return SelectionMissing;
            int effective = tower.SelectStageIdAfterAllPass != 0 ? tower.SelectStageIdAfterAllPass : tower.SelectStageId;
            if (effective == stageId) return RepeatSelection;
        }
        else
        {
            if (tower.IsPass) return PassedSelection;
            if (!config.Stages.Contains(stageId)) return NotMatchStage;
            if (!tower.DrawStageIds.Contains(stageId)) return SelectionMissing;
            if (tower.SelectStageId != 0) return RepeatSelection;
        }

        BossInshotState candidate = CloneState(state);
        BossInshotTowerState candidateTower = candidate.Towers.Single(x => x.TowerId == towerId);
        if (afterPass)
            candidateTower.SelectStageIdAfterAllPass = stageId;
        else
            candidateTower.SelectStageId = stageId;
        session.player.BossInshot = candidate;
        try
        {
            session.player.Save();
            return 0;
        }
        catch (Exception exception)
        {
            session.player.BossInshot = original;
            session.log.Error($"Failed to persist BossInshot tower selection: {exception}");
            return PreFightDataError;
        }
    }

    private static bool TryActive(Session session, out BossInshotActivityTable activity, out BossInshotState state)
    {
        activity = ActiveActivity(session.player)!;
        if (activity is null) { state = null!; session.PendingBossInshotFight = null; return false; }
        state = Reconcile(session.player, activity, session);
        return true;
    }
    private static BossInshotState Reconcile(Player player, BossInshotActivityTable activity, Session? session)
    {
        BossInshotState? old = player.BossInshot;
        if (old?.ActivityId == activity.Id)
        {
            NormalizeState(old);
            return old;
        }
        if (session is not null) session.PendingBossInshotFight = null;
        player.BossInshot = new BossInshotState { ActivityId = activity.Id, AuthorizedActivityId = old?.AuthorizedActivityId ?? 0, AuthorizedTimeIds = old?.AuthorizedTimeIds.ToList() ?? new() };
        return player.BossInshot;
    }

    private static void NormalizeState(BossInshotState state)
    {
        state.Towers ??= new();
        state.Towers = state.Towers
            .GroupBy(tower => tower.TowerId)
            .Select(group =>
            {
                List<BossInshotTowerState> rows = group.ToList();
                List<BossInshotTowerScoreState> scores = rows
                    .SelectMany(row => row.Scores ?? new())
                    .GroupBy(score => (score.BossId, score.CharacterId))
                    .Select(scoreGroup => scoreGroup
                        .OrderByDescending(score => score.MaxScore)
                        .ThenBy(score => score.AchievedAt <= 0 ? long.MaxValue : score.AchievedAt)
                        .First())
                    .ToList();
                return new BossInshotTowerState
                {
                    TowerId = group.Key,
                    DrawStageIds = rows.SelectMany(row => row.DrawStageIds ?? new()).Distinct().ToList(),
                    SelectStageId = rows.Select(row => row.SelectStageId).FirstOrDefault(id => id != 0),
                    SelectStageIdAfterAllPass = rows.Select(row => row.SelectStageIdAfterAllPass).FirstOrDefault(id => id != 0),
                    IsPass = rows.Any(row => row.IsPass),
                    TriggerProtectCount = rows.Max(row => row.TriggerProtectCount),
                    Scores = scores
                };
            })
            .ToList();
    }
    private static bool ResolveCharacter(int cfg, out int id) { id = 0; if (!Characters.Value.TryGetValue(cfg, out var c) || !Robots.Value.TryGetValue(c.RobotId, out var r)) return false; id = r.CharacterId; return id > 0; }

    private static bool TowerAccessAllowed(Session session, BossInshotActivityTable activity, BossInshotState state, out int code)
    {
        code = TowerConditionNotMet;
        if (activity.TimeId is not > 0 || !state.AuthorizedTimeIds.Contains(activity.TimeId.Value)) { code = NotOpen; return false; }
        if (activity.TowerConditions is not > 0 || !ConditionSatisfied(state, activity.TowerConditions.Value, new HashSet<int>())) return false;
        code = 0;
        return true;
    }

    private static bool ConditionSatisfied(BossInshotState state, int conditionId, HashSet<int> visiting)
    {
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition) || !visiting.Add(conditionId)) return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                bool any = condition.Formula.Contains('|');
                if (any && condition.Formula.Contains('&')) return false;
                string[] terms = condition.Formula.Split(any ? '|' : '&', StringSplitOptions.RemoveEmptyEntries);
                bool Evaluate(string term)
                {
                    string value = term.Trim();
                    bool negate = value.StartsWith('!');
                    if (negate) value = value[1..];
                    return int.TryParse(value, out int child) && (ConditionSatisfied(state, child, visiting) != negate);
                }
                return terms.Length > 0 && (any ? terms.Any(Evaluate) : terms.All(Evaluate));
            }
            return condition.Type == 15306 && condition.Params.Count >= 2
                && state.PassStages.Any(x => x.StageId == condition.Params[0] && x.MaxScore >= condition.Params[1]);
        }
        finally { visiting.Remove(conditionId); }
    }
    private static BossInshotTowerState InitializeTower(int activityId, long playerId, BossInshotTowerTable table)
    {
        List<(int Stage, int Weight)> candidates = table.Stages
            .Select((stage, index) => (Stage: stage, Weight: index < table.Weight.Count ? table.Weight[index] : 0))
            .Where(candidate => candidate.Stage > 0 && candidate.Weight > 0)
            .ToList();
        StableRandom random = new(activityId, playerId, table.Id);
        List<int> draw = new();
        while (draw.Count < Math.Min(table.SelectNum, candidates.Count))
        {
            long roll = random.Next(candidates.Sum(candidate => (long)candidate.Weight));
            long upper = 0;
            for (int index = 0; index < candidates.Count; index++)
            {
                upper += candidates[index].Weight;
                if (roll >= upper)
                    continue;
                draw.Add(candidates[index].Stage);
                candidates.RemoveAt(index);
                break;
            }
        }
        return new BossInshotTowerState
        {
            TowerId = table.Id,
            DrawStageIds = draw,
            SelectStageId = table.Type == 1 ? draw.SingleOrDefault() : 0
        };
    }

    private struct StableRandom
    {
        private ulong state;

        public StableRandom(int activityId, long playerId, int towerId)
        {
            const ulong offset = 14695981039346656037UL;
            state = Mix(Mix(Mix(offset, unchecked((ulong)(uint)activityId)), unchecked((ulong)playerId)), unchecked((ulong)(uint)towerId));
        }

        public long Next(long exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            ulong bound = checked((ulong)exclusiveMaximum);
            ulong threshold = unchecked(0UL - bound) % bound;
            ulong value;
            do
            {
                value = NextUInt64();
            }
            while (value < threshold);
            return checked((long)(value % bound));
        }

        private ulong NextUInt64()
        {
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            ulong value = state;
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            return value ^ (value >> 31);
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            const ulong prime = 1099511628211UL;
            for (int shift = 0; shift < 64; shift += 8)
                hash = unchecked((hash ^ (byte)(value >> shift)) * prime);
            return hash;
        }
    }

    private static BossInshotData BuildData(BossInshotState state, BossInshotActivityTable activity) => new()
    {
        ActivityId = state.ActivityId, IsPassTeach = state.IsPassTeach, CurrentTowerId = state.CurrentTowerId,
        PassStageDatas = state.PassStages.Select(x => new BossInshotPassStageData { StageId = x.StageId, MaxScore = x.MaxScore }).ToList(),
        BossUnlockDatas = state.BossUnlocks.Select(x => new BossInshotBossUnlockData { BossId = x.BossId, DifficultySet = x.DifficultySet.ToList() }).ToList(),
        CharacterDatas = state.IsPassTeach ? activity.CharacterIds.Where(cfg => ResolveCharacter(cfg, out _)).Select(cfg => { ResolveCharacter(cfg, out int id); var persisted = state.Characters.FirstOrDefault(x => x.CharacterId == id); var talents = Talents.Value.Where(x => x.CharacterId == id).ToList(); return new BossInshotCharacterData { CharacterId = id, DefaultTalentId = talents.FirstOrDefault(x => x.TalentType == 1)?.Id ?? 0, UnlockTalentIds = talents.Where(x => x.TalentType == 2).Select(x => x.Id).ToList(), SelectTalentIds = persisted?.SelectTalentIds.ToList() ?? new(), TotalScore = persisted?.TotalScore ?? 0, PassStageSet = persisted?.PassStageSet.ToList() ?? new() }; }).ToList() : new(),
        TowerDataDict = state.Towers.ToDictionary(x => x.TowerId, BuildTower)
    };
    private static BossInshotTowerData BuildTower(BossInshotTowerState tower) => new() { TowerId = tower.TowerId, DrawStageIds = tower.DrawStageIds?.ToList() ?? new(), SelectStageId = tower.SelectStageId, SelectStageIdAfterAllPass = tower.SelectStageIdAfterAllPass, IsPass = tower.IsPass, TriggerProtectCount = tower.TriggerProtectCount, BossMaxScoreDict = (tower.Scores ?? new()).GroupBy(x => x.BossId).ToDictionary(g => g.Key, g => new BossInshotBossMaxScoreData { CharacterScoreDict = g.GroupBy(x => x.CharacterId).ToDictionary(x => x.Key, x => x.Max(score => score.MaxScore)) }) };
}
