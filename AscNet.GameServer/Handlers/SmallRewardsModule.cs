using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.miniactivity.accumulateexpendshop;
using AscNet.Table.V2.share.miniactivity.gamecollection;
using AscNet.Table.V2.share.miniactivity.versiongift;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using ConditionTable = AscNet.Table.V2.share.condition.ConditionTable;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class NotifyVersionGiftActivity
{
    public int ActivityId { get; set; }
    public bool IsGetDailyGiftReward { get; set; }
    public bool IsGetVersionGiftReward { get; set; }
    public List<int> ProgressRewardIndexSet { get; set; } = new();
}

// The client names this field differently from NotifyVersionGiftActivity (XVersionGiftModel:RefreshGiftRewardData).
[MessagePackObject(true)]
public sealed class NotifyGiftRewardUpdate
{
    public int ActivityId { get; set; }
    public bool IsGetDailyGiftReward { get; set; }
    public bool IsGetVersionGiftReward { get; set; }
    public List<int> GetProgressRewardIndexSet { get; set; } = new();
}

[MessagePackObject(true)] public sealed class VersionGiftGetRewardRequest { public int Type { get; set; } }
[MessagePackObject(true)]
public sealed class VersionGiftGetRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods>? RewardList { get; set; }
}

[MessagePackObject(true)] public sealed class AccumulatedConsumeDrawDoDrawRequest { public int Count { get; set; } }
[MessagePackObject(true)]
public sealed class AccumulatedConsumeDrawDoDrawResponse
{
    public int Code { get; set; }
    public List<RewardGoods>? DropRewardList { get; set; }
    public List<RewardGoods>? ProgressRewardList { get; set; }
}

[MessagePackObject(true)]
public sealed class AccumulateExpendSignRewardData
{
    public int Id { get; set; }
    public int Index { get; set; }
    public int TodaySignRewardStatus { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyAccumulateExpendShopData
{
    public int ActivityId { get; set; }
    public AccumulateExpendSignRewardData SignRewardData { get; set; } = new();
    public int TotalConsumeCount { get; set; }
    public int ConvertedCount { get; set; }
}

[MessagePackObject(true)] public sealed class EnterAccumulateExpendShopRequest { }
[MessagePackObject(true)]
public sealed class EnterAccumulateExpendShopResponse
{
    public int Code { get; set; }
    public List<RewardGoods>? RewardGoodsList { get; set; }
}

[MessagePackObject(true)] public sealed class ClaimSignRewardRequest { }
[MessagePackObject(true)]
public sealed class ClaimSignRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods>? RewardGoodsList { get; set; }
}

[MessagePackObject(true)] public sealed class GameCollectionScore { public int MaxScore { get; set; } }
[MessagePackObject(true)]
public sealed class GameCollectionDataDto
{
    public int ActivityId { get; set; }
    public Dictionary<int, GameCollectionScore> GameData { get; set; } = new();
}
[MessagePackObject(true)] public sealed class NotifyGameCollectionData { public GameCollectionDataDto GameCollectionData { get; set; } = new(); }
[MessagePackObject(true)] public sealed class GameCollectionEnterGameRequest { public int GameType { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class GameCollectionEnterGameResponse { public int Code { get; set; } }

/// <summary>
/// Version gift, accumulated-consume draw (closed), accumulate-expend shop and game collection entry.
/// Source tables: share/miniactivity/{versiongift,accumulateexpendshop,gamecollection}. None of these tables carries a
/// usable calendar (installed TimeId is 0 or absent), so AscNet policy (AGENTS.md missing-retail policy) treats the
/// authored activity row as permanently available; the accumulate-expend shop additionally honours its authored
/// OpenConditionIds. Which version-gift row is "current" is not authored either; AscNet policy: the highest Id.
/// </summary>
internal static class SmallRewardsModule
{
    // EN CodeText 20247001-20247008 (VersionGift*).
    private const int VersionGiftNotOpen = 20247001, VersionGiftConfigError = 20247002, VersionGiftClientError = 20247003,
        DailyAlreadyGet = 20247004, VersionAlreadyGet = 20247005, ProgressAlreadyGet = 20247006,
        TaskConfigError = 20247007, NeedFinishTask = 20247008;
    // EN CodeText 20162002 AccumulatedConsumeDrawIsNotOpen.
    private const int AccumulatedConsumeDrawNotOpen = 20162002;
    // EN CodeText 20322001-20322007 (AccumulatedShop*).
    private const int ShopNotOpen = 20322001, ShopConfigMissing = 20322002, ShopTokenRewardFail = 20322005,
        SignAlreadyGet = 20322006, SignNotInTime = 20322007;
    // EN CodeText 20424001-20424004 (GameCollection*).
    private const int CollectionNotOpen = 20424001, OtherGameInSuspend = 20424002, NoSuchGameType = 20424003, StageIdMismatch = 20424004;
    private const int ServerInternalError = 2;
    private const int DailyReward = 1, VersionReward = 2, ProgressReward = 3;
    private const int PlayerLevelConditionType = 10101;
    private const int FangKuaiGameType = 3; // XEnumConst.GameCollection.GameType.FangKong

    private static readonly Lazy<List<VersionGiftActivityTable>> VersionGiftActivities = new(() => TableReaderV2.Parse<VersionGiftActivityTable>());
    private static readonly Lazy<Dictionary<int, VersionGiftProcessTable>> VersionGiftProcesses = new(() => TableReaderV2.Parse<VersionGiftProcessTable>().ToDictionary(row => row.ActivityId));
    private static readonly Lazy<Dictionary<int, TaskTimeLimitTable>> TaskGroups = new(() => TableReaderV2.Parse<TaskTimeLimitTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<List<AccumulateExpendShopActivityTable>> ShopActivities = new(() => TableReaderV2.Parse<AccumulateExpendShopActivityTable>());
    private static readonly Lazy<Dictionary<int, AccumulateExpendShopSignRewardTable>> SignRewards = new(() => TableReaderV2.Parse<AccumulateExpendShopSignRewardTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<List<GameCollectionTable>> CollectionGames = new(() => TableReaderV2.Parse<GameCollectionTable>());
    private static readonly Lazy<List<GameCollectionActivityTable>> CollectionActivities = new(() => TableReaderV2.Parse<GameCollectionActivityTable>());

    private static long Unix(DateTimeOffset now) => now.ToUnixTimeSeconds();
    private static long NextReset(DateTimeOffset now) => DormModule.NextDailyRefreshTime(checked((uint)Unix(now)));

    private static List<RewardGoods>? Goods(RewardApplicationResult? grant) =>
        grant is { RewardGoods.Count: > 0 } ? grant.RewardGoods : null;

    private static RewardApplicationResult? TryGrant(Session session, List<RewardGrant> grants, out int code)
    {
        code = 0;
        try { return RewardHandler.ApplyRewardsOnceAndPersist(grants, session); }
        catch (Exception exception)
        {
            session.log.Error($"Small reward grant {grants[0].ClaimKey} failed: {exception.Message}");
            code = ServerInternalError;
            return null;
        }
    }

    // ---------------------------------------------------------------- version gift

    private static VersionGiftActivityTable? CurrentVersionGift() => VersionGiftActivities.Value.OrderByDescending(row => row.Id).FirstOrDefault();

    private static VersionGiftState VersionGiftState(Player player, int activityId)
    {
        if (player.VersionGift.ActivityId != activityId)
        {
            player.VersionGift = new() { ActivityId = activityId };
            player.Save();
        }
        return player.VersionGift;
    }

    internal static NotifyVersionGiftActivity? BuildVersionGiftNotify(Player player, DateTimeOffset now)
    {
        if (CurrentVersionGift() is not { } activity) return null;
        VersionGiftState state = VersionGiftState(player, activity.Id);
        return new()
        {
            ActivityId = activity.Id,
            IsGetDailyGiftReward = Unix(now) < state.DailyClaimedUntil,
            IsGetVersionGiftReward = state.VersionClaimed,
            ProgressRewardIndexSet = state.ProgressIndexes.Order().ToList()
        };
    }

    private static NotifyGiftRewardUpdate BuildVersionGiftUpdate(Player player, DateTimeOffset now)
    {
        NotifyVersionGiftActivity data = BuildVersionGiftNotify(player, now)!;
        return new()
        {
            ActivityId = data.ActivityId,
            IsGetDailyGiftReward = data.IsGetDailyGiftReward,
            IsGetVersionGiftReward = data.IsGetVersionGiftReward,
            GetProgressRewardIndexSet = data.ProgressRewardIndexSet
        };
    }

    // The client's task progress counts Finish (claimed) tasks of the daily, normal and activity groups.
    private static bool TryFinishedTaskCount(Player player, VersionGiftActivityTable activity, out int finished)
    {
        finished = 0;
        foreach (int groupId in new[] { activity.DailyTaskGroup, activity.NormalTaskGroup, activity.ActivityTaskGroup })
        {
            if (!TaskGroups.Value.TryGetValue(groupId, out TaskTimeLimitTable? group)) return false;
            finished += group.TaskId.Concat(group.DayTaskId).Concat(group.WeekTaskId)
                .Count(player.MissionProgress.ClaimedTaskIds.Contains);
        }
        return true;
    }

    internal static int ClaimVersionGift(Session session, int type, DateTimeOffset now, out RewardApplicationResult? grant)
    {
        grant = null;
        if (CurrentVersionGift() is not { } activity) return VersionGiftNotOpen;
        VersionGiftState state = VersionGiftState(session.player, activity.Id);
        List<RewardGrant> grants = [];
        Action commit;
        switch (type)
        {
            case DailyReward:
            {
                if (Unix(now) < state.DailyClaimedUntil) return DailyAlreadyGet;
                long until = NextReset(now);
                List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(activity.DailyGiftReward);
                if (goods.Count == 0) return VersionGiftConfigError;
                grants.Add(new RewardGrant($"version-gift:{activity.Id}:daily:{until}", goods));
                commit = () => state.DailyClaimedUntil = until;
                break;
            }
            case VersionReward:
            {
                if (state.VersionClaimed) return VersionAlreadyGet;
                List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(activity.VersionGiftReward);
                if (goods.Count == 0) return VersionGiftConfigError;
                grants.Add(new RewardGrant($"version-gift:{activity.Id}:version", goods));
                commit = () => state.VersionClaimed = true;
                break;
            }
            case ProgressReward:
            {
                if (!VersionGiftProcesses.Value.TryGetValue(activity.Id, out VersionGiftProcessTable? process)
                    || process.TaskCompleteCount.Count != process.RewardId.Count) return VersionGiftConfigError;
                if (!TryFinishedTaskCount(session.player, activity, out int finished)) return TaskConfigError;
                List<int> reached = Enumerable.Range(1, process.TaskCompleteCount.Count)
                    .Where(index => process.TaskCompleteCount[index - 1] <= finished).ToList();
                List<int> pending = reached.Except(state.ProgressIndexes).ToList();
                if (pending.Count == 0) return reached.Count == 0 ? NeedFinishTask : ProgressAlreadyGet;
                foreach (int index in pending)
                {
                    List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(process.RewardId[index - 1]);
                    if (goods.Count == 0) return VersionGiftConfigError;
                    grants.Add(new RewardGrant($"version-gift:{activity.Id}:progress:{index}", goods));
                }
                commit = () => state.ProgressIndexes.AddRange(pending);
                break;
            }
            default: return VersionGiftClientError;
        }

        grant = TryGrant(session, grants, out int code);
        if (code != 0) return code;
        commit();
        session.player.Save();
        return 0;
    }

    [RequestPacketHandler("VersionGiftGetRewardRequest")]
    public static void VersionGiftGetReward(Session session, Packet.Request packet)
    {
        VersionGiftGetRewardRequest request = packet.Deserialize<VersionGiftGetRewardRequest>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int code = ClaimVersionGift(session, request.Type, now, out RewardApplicationResult? grant);
        if (code == 0)
            // The UI callback re-reads the model, so the state push precedes the response.
            session.SendPush(BuildVersionGiftUpdate(session.player, now));
        session.SendResponse(new VersionGiftGetRewardResponse { Code = code, RewardList = Goods(grant) ?? (code == 0 ? new() : null) }, packet.Id);
        grant?.SendPushes(session);
    }

    // ---------------------------------------------------------------- accumulated consume draw (closed)

    // EN ships neither AccumulatedConsumeDrawActivity (coin item, cost, drop id) nor a drop table; only display-rounded
    // ProbShow text exists client-side. The draw therefore fails closed (Docs/closed.md) and no odds are invented.
    [RequestPacketHandler("AccumulatedConsumeDrawDoDrawRequest")]
    public static void AccumulatedConsumeDrawDoDraw(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<AccumulatedConsumeDrawDoDrawRequest>();
        session.SendResponse(new AccumulatedConsumeDrawDoDrawResponse { Code = AccumulatedConsumeDrawNotOpen }, packet.Id);
    }

    // ---------------------------------------------------------------- accumulate expend shop

    private static AccumulateExpendShopActivityTable? CurrentShop(Player player)
    {
        AccumulateExpendShopActivityTable? activity = ShopActivities.Value.OrderByDescending(row => row.Id).FirstOrDefault();
        if (activity is null) return null;
        // Authored OpenConditionIds; only the player-level condition type is evaluable here, anything else stays closed.
        foreach (int conditionId in activity.OpenConditionIds.Where(id => id > 0))
            if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition)
                || condition.Type != PlayerLevelConditionType || condition.Params.Count == 0
                || player.PlayerData.Level < condition.Params[0]) return null;
        return activity;
    }

    private static AccumulateExpendShopState ShopState(Session session, AccumulateExpendShopActivityTable activity)
    {
        Player player = session.player;
        if (player.AccumulateExpendShop.ActivityId != activity.Id)
        {
            player.AccumulateExpendShop = new()
            {
                ActivityId = activity.Id,
                SpentBaseline = session.inventory.SpentTotals.GetValueOrDefault(activity.ItemId.ToString())
            };
            player.Save();
        }
        return player.AccumulateExpendShop;
    }

    private static long TotalConsumed(Session session, AccumulateExpendShopActivityTable activity, AccumulateExpendShopState state) =>
        Math.Max(0, session.inventory.SpentTotals.GetValueOrDefault(activity.ItemId.ToString()) - state.SpentBaseline);

    internal static NotifyAccumulateExpendShopData BuildShopNotify(Session session, DateTimeOffset now)
    {
        if (CurrentShop(session.player) is not { } activity) return new();
        AccumulateExpendShopState state = ShopState(session, activity);
        int rewardCount = SignRewards.Value.TryGetValue(activity.SignRewardId, out AccumulateExpendShopSignRewardTable? sign) ? sign.RewardIds.Count(id => id > 0) : 0;
        // The client treats status 0 as "today's reward can be claimed".
        bool claimable = state.SignCount < rewardCount && Unix(now) >= state.SignedUntil;
        return new()
        {
            ActivityId = activity.Id,
            SignRewardData = new() { Id = activity.SignRewardId, Index = state.SignCount, TodaySignRewardStatus = claimable ? 0 : 1 },
            TotalConsumeCount = (int)Math.Min(int.MaxValue, TotalConsumed(session, activity, state)),
            ConvertedCount = state.ConvertedCount
        };
    }

    internal static int EnterShop(Session session, DateTimeOffset now, out RewardApplicationResult? grant)
    {
        grant = null;
        if (CurrentShop(session.player) is not { } activity) return ShopNotOpen;
        if (activity.ItemExchangeRate <= 0) return ShopConfigMissing;
        AccumulateExpendShopState state = ShopState(session, activity);
        // Spend converts into tokens one-for-ItemExchangeRate until ShopItemMaxCount tokens are reached.
        long target = Math.Min(TotalConsumed(session, activity, state), activity.ShopItemMaxCount / activity.ItemExchangeRate);
        if (target <= state.ConvertedCount) return 0;
        int tokens = checked((int)(target - state.ConvertedCount) * activity.ItemExchangeRate);
        grant = TryGrant(session, [new RewardGrant($"accumulate-expend-shop:{activity.Id}:convert:{state.ConvertedCount}:{target}",
            [new RewardGoodsTable { Id = activity.ShopItemId, TemplateId = activity.ShopItemId, Count = tokens, Params = [] }])], out int code);
        if (code != 0) return ShopTokenRewardFail;
        state.ConvertedCount = (int)target;
        session.player.Save();
        return 0;
    }

    internal static int ClaimSign(Session session, DateTimeOffset now, out RewardApplicationResult? grant)
    {
        grant = null;
        if (CurrentShop(session.player) is not { } activity) return ShopNotOpen;
        if (!SignRewards.Value.TryGetValue(activity.SignRewardId, out AccumulateExpendShopSignRewardTable? sign)) return ShopConfigMissing;
        AccumulateExpendShopState state = ShopState(session, activity);
        if (Unix(now) < state.SignedUntil) return SignAlreadyGet;
        List<int> rewardIds = sign.RewardIds.Where(id => id > 0).ToList();
        if (state.SignCount >= rewardIds.Count) return SignNotInTime;
        List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardIds[state.SignCount]);
        if (goods.Count == 0) return ShopConfigMissing;
        long until = NextReset(now);
        grant = TryGrant(session, [new RewardGrant($"accumulate-expend-shop:{activity.Id}:sign:{state.SignCount}", goods)], out int code);
        if (code != 0) return ShopTokenRewardFail;
        state.SignCount++;
        state.SignedUntil = until;
        session.player.Save();
        return 0;
    }

    [RequestPacketHandler("EnterAccumulateExpendShopRequest")]
    public static void EnterAccumulateExpendShop(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<EnterAccumulateExpendShopRequest>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int code = EnterShop(session, now, out RewardApplicationResult? grant);
        if (code == 0) session.SendPush(BuildShopNotify(session, now));
        session.SendResponse(new EnterAccumulateExpendShopResponse { Code = code, RewardGoodsList = Goods(grant) }, packet.Id);
        grant?.SendPushes(session);
    }

    [RequestPacketHandler("ClaimSignRewardRequest")]
    public static void ClaimSignReward(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<ClaimSignRewardRequest>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int code = ClaimSign(session, now, out RewardApplicationResult? grant);
        if (code == 0) session.SendPush(BuildShopNotify(session, now));
        session.SendResponse(new ClaimSignRewardResponse { Code = code, RewardGoodsList = Goods(grant) }, packet.Id);
        grant?.SendPushes(session);
    }

    // ---------------------------------------------------------------- game collection

    internal static NotifyGameCollectionData BuildGameCollectionNotify() => new()
    {
        GameCollectionData = new() { ActivityId = CollectionActivities.Value.OrderByDescending(row => row.Id).FirstOrDefault()?.Id ?? 0 }
    };

    // Entering only validates: the client reads Code alone, and no durable fact changes until a child game starts.
    internal static int EnterCollectionGame(Player player, int gameType, int stageId)
    {
        if (BuildGameCollectionNotify().GameCollectionData.ActivityId == 0) return CollectionNotOpen;
        GameCollectionTable? game = CollectionGames.Value.FirstOrDefault(row => row.GameType == gameType);
        if (game is null) return NoSuchGameType;
        if (stageId != (game.StageId ?? 0)) return StageIdMismatch;
        // A FangKuai run counts as suspended while its stage is the chapter's current stage; no other child game persists a run server-side.
        if (gameType != FangKuaiGameType
            && CollectionGames.Value.FirstOrDefault(row => row.GameType == FangKuaiGameType) is { StageId: > 0 } fangKuai
            && player.FangKuai.StageDataDict.Values.Any(run => run.StageId == fangKuai.StageId)) return OtherGameInSuspend;
        return 0;
    }

    [RequestPacketHandler("GameCollectionEnterGameRequest")]
    public static void GameCollectionEnterGame(Session session, Packet.Request packet)
    {
        GameCollectionEnterGameRequest request = packet.Deserialize<GameCollectionEnterGameRequest>();
        session.SendResponse(new GameCollectionEnterGameResponse { Code = EnterCollectionGame(session.player, request.GameType, request.StageId) }, packet.Id);
    }
}
