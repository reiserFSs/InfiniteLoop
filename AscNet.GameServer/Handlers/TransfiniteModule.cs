using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class TransfiniteGetRotateSettleInfoRequest { }
[MessagePackObject(true)] public sealed class TransfiniteGetRotateSettleInfoResponse
{
    public int Code { get; set; }
    public int MaxStageProgressIndex { get; set; }
    public int SettleTransfiniteScore { get; set; }
    public int UnSettleTransfiniteScore { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = new();
}

[MessagePackObject(true)] public sealed class NotifyTransfiniteData
{
    public TransfiniteData? TransfiniteData { get; set; }
}

[MessagePackObject(true)] public sealed class TransfiniteData
{
    public int ActivityId { get; set; }
    public int CircleId { get; set; }
    public long BeginTime { get; set; }
    public int RegionId { get; set; }
    public int StageGroupIndex { get; set; }
    public List<TransfiniteBattleInfo> BattleInfo { get; set; } = new();
    public List<TransfiniteBestSpendTime> BestSpendTime { get; set; } = new();
    public List<int> GotScoreRewardIndex { get; set; } = new();
    public bool SendActivityStartMail { get; set; }
    public int MaxRotateStageProgressIndex { get; set; }
    public TransfiniteRotateSettleInfo? RotateSettleInfo { get; set; }
    public int StageGroupId { get; set; }
    public long LastModifyTime { get; set; }
    public int ScoreRewardGroupId { get; set; }
}

[MessagePackObject(true)] public sealed class TransfiniteBattleInfo
{
    public int StageGroupId { get; set; }
    public int StageProgressIndex { get; set; }
    public int StartStageProgress { get; set; }
    public object? TeamInfo { get; set; }
    public List<TransfiniteStageInfo> StageInfo { get; set; } = new();
    public TransfiniteBattleResult? Result { get; set; }
    public object? LastResult { get; set; }
    public List<object> HistoryResults { get; set; } = new();
}

[MessagePackObject(true)] public sealed class TransfiniteStageInfo
{
    public int StageId { get; set; }
    public bool IsWin { get; set; }
    public int SpendTime { get; set; }
    public int Score { get; set; }
}

[MessagePackObject(true)] public sealed class TransfiniteBattleResult
{
    public int LastWinStageId { get; set; }
    public List<object> CharacterResultList { get; set; } = new();
    public int StageSpendTime { get; set; }
}

[MessagePackObject(true)] public sealed class TransfiniteBestSpendTime
{
    public int StageGroupId { get; set; }
    public int BestSpendTime { get; set; }
}

[MessagePackObject(true)] public sealed class TransfiniteRotateSettleInfo { public int MaxStageProgressIndex { get; set; } public int ScoreRewardGroupId { get; set; } public int SettleTransfiniteScore { get; set; } public int UnSettleTransfiniteScore { get; set; } public List<int> GotScoreRewardIndex { get; set; } = new(); }

internal static class TransfiniteModule
{
    internal const int ActivityNotOpen = 20008001;
    internal const int ConfigNotFound = 20008002;
    internal const int NoRotateSettlement = 20008003;

    private static readonly Lazy<Dictionary<int, TransfiniteActivityTable>> Activities = new(() => TableReaderV2.Parse<TransfiniteActivityTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<List<TransfiniteRegionTable>> Regions = new(() => TableReaderV2.Parse<TransfiniteRegionTable>());
    private static readonly Lazy<List<TransfiniteScoreRewardGroupTable>> RewardGroups = new(() => TableReaderV2.Parse<TransfiniteScoreRewardGroupTable>());
    private static readonly Lazy<Dictionary<int, ItemTable>> Items = new(() =>
        TableReaderV2.Parse<ItemTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<HashSet<int>> FightStages = new(() =>
        TableReaderV2.Parse<TransfiniteStageTable>().Select(row => row.StageId).ToHashSet());
    private static readonly Lazy<Dictionary<int, TransfiniteRotateGroupTable>> RotateGroups = new(() =>
        TableReaderV2.Parse<TransfiniteRotateGroupTable>().ToDictionary(row => row.RotateGroupId));
    private static readonly Lazy<Dictionary<int, TransfiniteStageGroupTable>> StageGroups = new(() =>
        TableReaderV2.Parse<TransfiniteStageGroupTable>().ToDictionary(row => row.StageGroupId));

    internal static bool IsStage(uint stageId) => stageId <= int.MaxValue && FightStages.Value.Contains((int)stageId);

    internal static bool ApplyPreFight(
        Session session,
        PreFightRequest.PreFightRequestPreFightData request,
        out int code)
    {
        if (!IsStage(request.StageId))
        {
            code = 0;
            return false;
        }

        code = IsAuthorized(session.player.Transfinite) ? ConfigNotFound : ActivityNotOpen;
        return true;
    }

    internal static bool TrySettle(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        if (!IsStage(result.StageId))
        {
            response = null!;
            return false;
        }

        // The current client tables do not define the authoritative battle-result/confirm transition.
        // Claim the stage here so it can never fall through to generic story progression or rewards.
        response = new FightSettleResponse
        {
            Code = IsAuthorized(session.player.Transfinite) ? ConfigNotFound : ActivityNotOpen
        };
        return true;
    }

    internal static void PrepareLogin(Player player, long now)
    {
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(now);
        TransfiniteActivityTable? activity = Activities.Value.Values
            .Where(row => ActivityScheduleService.TryGet(row.TimeId, out ActivityScheduleEntry schedule)
                && schedule.IsOpen(timestamp))
            .OrderByDescending(row => row.Id)
            .FirstOrDefault();
        if (activity is null
            || !ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry activeSchedule)
            || !TrySelectConfiguration(activity, checked((int)player.PlayerData.Level), now, out TransfiniteState? expected))
        {
            if (player.Transfinite is not null && player.Transfinite.ActivityAuthorizedUntil != 0)
            {
                player.Transfinite.ActivityAuthorizedUntil = 0;
                player.Save();
            }
            return;
        }
        TransfiniteState selected = expected!;

        selected.ActivityAuthorizedUntil = activeSchedule.EndTime == 0 ? long.MaxValue : activeSchedule.EndTime;
        if (IsCurrentConfiguration(player.Transfinite, selected)) return;
        if (player.Transfinite is { } previous && previous.ActivityId == selected.ActivityId)
        {
            selected.RotateSettleInfo = previous.RotateSettleInfo;
            selected.LastRotateReceipt = previous.LastRotateReceipt;
            selected.LastModifyTime = previous.LastModifyTime;
        }
        player.Transfinite = selected;
        player.Save();
    }

    private static bool TrySelectConfiguration(
        TransfiniteActivityTable activity,
        int playerLevel,
        long now,
        out TransfiniteState? state)
    {
        state = null;
        if (now < 0 || activity.CycleSeconds <= 0) return false;
        TransfiniteRegionTable? region = Regions.Value.SingleOrDefault(row =>
            playerLevel >= row.MinLv && playerLevel <= row.MaxLv);
        if (region is null
            || !RotateGroups.Value.TryGetValue(region.RotateGroupId, out TransfiniteRotateGroupTable? rotate)
            || rotate.StageGroupId.Count == 0)
        {
            return false;
        }

        long beginTime = now - now % activity.CycleSeconds;
        long circle = beginTime / activity.CycleSeconds + 1;
        if (circle > int.MaxValue) return false;
        int stageGroupIndex = (int)((circle - 1) % rotate.StageGroupId.Count);
        int stageGroupId = rotate.StageGroupId[stageGroupIndex];
        if (!StageGroups.Value.ContainsKey(stageGroupId)) return false;

        TransfiniteScoreRewardGroupTable? reward = RewardGroups.Value.SingleOrDefault(row =>
            row.RegionId == region.RegionId && row.ScoreRewardGroupId == region.ScoreRewardGroupId);
        if (reward?.ScoreRewardGroupId is not int scoreRewardGroupId) return false;

        state = new TransfiniteState
        {
            ActivityId = activity.Id,
            CircleId = (int)circle,
            BeginTime = beginTime,
            RegionId = region.RegionId,
            StageGroupIndex = stageGroupIndex,
            StageGroupId = stageGroupId,
            ScoreRewardGroupId = scoreRewardGroupId
        };
        return true;
    }

    private static bool IsCurrentConfiguration(TransfiniteState? state, TransfiniteState expected) =>
        state is not null
        && state.ActivityId == expected.ActivityId
        && state.ActivityAuthorizedUntil == expected.ActivityAuthorizedUntil
        && state.CircleId == expected.CircleId
        && state.BeginTime == expected.BeginTime
        && state.RegionId == expected.RegionId
        && state.StageGroupIndex == expected.StageGroupIndex
        && state.StageGroupId == expected.StageGroupId
        && state.ScoreRewardGroupId == expected.ScoreRewardGroupId
        && TryValidateConfiguration(state, out _);

    internal static NotifyTransfiniteData BuildNotify(Player player)
    {
        TransfiniteState? state = player.Transfinite;
        return IsAuthorized(state) && TryValidateConfiguration(state!, out _)
            ? new NotifyTransfiniteData { TransfiniteData = ToWire(state!) }
            : new NotifyTransfiniteData();
    }

    [RequestPacketHandler("TransfiniteGetRotateSettleInfoRequest")]
    public static void GetRotateSettleInfo(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<TransfiniteGetRotateSettleInfoRequest>();
        TransfiniteState? state = session.player.Transfinite;
        if (!IsAuthorized(state))
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ActivityNotOpen }, packet.Id);
            return;
        }
        if (!TryResolveSettlementConfiguration(state!, out TransfiniteScoreRewardGroupTable? rewardGroup))
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }

        TransfiniteRotateSettleState? pending = state!.RotateSettleInfo;
        if (pending is null)
        {
            if (state.LastRotateReceipt is not { } receipt)
            {
                session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = NoRotateSettlement }, packet.Id);
                return;
            }
            session.SendResponse(ToResponse(receipt), packet.Id);
            return;
        }

        if (!TryResolveRewards(pending, rewardGroup!, out List<RewardGoodsTable> rows))
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }

        List<TransfiniteInventoryReceipt> matchingReceipts = session.inventory.TransfiniteReceipts
            .Where(receipt => receipt.ActivityId == state.ActivityId && receipt.RotationId == pending.RotationId)
            .Take(2)
            .ToList();
        if (matchingReceipts.Count > 1)
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }
        if (matchingReceipts.SingleOrDefault() is { } inventoryReceipt)
        {
            if (!ReceiptMatches(inventoryReceipt, pending, rows)
                || !TryCommitPlayerReceipt(session, state, ToPlayerReceipt(inventoryReceipt)))
            {
                session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
                return;
            }
            SendReceiptItemPush(session, inventoryReceipt);
            session.SendResponse(ToResponse(inventoryReceipt), packet.Id);
            return;
        }

        if (!CanApplyItemRewards(rows, session.inventory))
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }

        List<RewardGoods> granted = rows.Select(row => new RewardGoods
        {
            Id = row.Id,
            TemplateId = row.TemplateId,
            Count = row.Count,
            RewardType = (int)RewardType.Item
        }).ToList();
        TransfiniteInventoryReceipt newReceipt = new()
        {
            ActivityId = state.ActivityId,
            RotationId = pending.RotationId,
            RegionId = pending.RegionId,
            ScoreRewardGroupId = pending.ScoreRewardGroupId,
            MaxStageProgressIndex = pending.MaxStageProgressIndex,
            SettleTransfiniteScore = pending.SettleTransfiniteScore,
            UnSettleTransfiniteScore = pending.UnSettleTransfiniteScore,
            RewardGoods = granted.Select(ToReceipt).ToList()
        };

        Inventory inventorySnapshot = BsonSerializer.Deserialize<Inventory>(session.inventory.ToBson());
        List<Item> changedItems = rows
            .GroupBy(row => row.TemplateId)
            .Select(group => session.inventory.Do(group.Key, checked(group.Sum(row => row.Count))))
            .ToList();
        session.inventory.TransfiniteReceipts.Add(newReceipt);
        try
        {
            session.inventory.Save();
        }
        catch (Exception exception)
        {
            session.inventory.Items = inventorySnapshot.Items;
            session.inventory.TransfiniteReceipts = inventorySnapshot.TransfiniteReceipts;
            session.log.Error($"Failed to persist Transfinite reward receipt: {exception}");
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }

        if (!TryCommitPlayerReceipt(session, state, ToPlayerReceipt(newReceipt)))
        {
            session.SendResponse(new TransfiniteGetRotateSettleInfoResponse { Code = ConfigNotFound }, packet.Id);
            return;
        }
        if (changedItems.Count > 0)
            session.SendPush(new NotifyItemDataList { ItemDataList = changedItems });
        session.SendResponse(ToResponse(newReceipt), packet.Id);
    }

    private static bool TryResolveRewards(TransfiniteRotateSettleState pending, TransfiniteScoreRewardGroupTable group, out List<RewardGoodsTable> rows)
    {
        rows = new();
        if (pending.MaxStageProgressIndex < 0 || pending.SettleTransfiniteScore < 0 || pending.UnSettleTransfiniteScore < 0) return false;
        foreach (int oneBasedIndex in pending.GotScoreRewardIndex.Distinct().OrderBy(value => value))
        {
            int index = oneBasedIndex - 1;
            if (index < 0 || index >= group.Score.Count || index >= group.RewardId.Count || pending.SettleTransfiniteScore < group.Score[index] || group.RewardId[index] <= 0) return false;
            List<RewardGoodsTable> configured = RewardHandler.GetRewardGoods(group.RewardId[index]);
            if (configured.Count == 0) return false;
            rows.AddRange(configured);
        }
        return true;
    }

    private static bool CanApplyItemRewards(IReadOnlyList<RewardGoodsTable> rows, Inventory inventory)
    {
        if (rows.Any(row => row.Count <= 0
            || RewardHandler.GetRewardType(row) != RewardType.Item
            || !Items.Value.ContainsKey(row.TemplateId)))
        {
            return false;
        }

        try
        {
            foreach (IGrouping<int, RewardGoodsTable> group in rows.GroupBy(row => row.TemplateId))
            {
                List<Item> currentRows = inventory.Items.Where(item => item.Id == group.Key).ToList();
                if (currentRows.Count > 1) return false;
                long current = currentRows.SingleOrDefault()?.Count ?? 0;
                long addition = checked(group.Sum(row => (long)row.Count));
                long maximum = Inventory.GetMaxCount(Items.Value[group.Key]);
                if (current < 0 || addition > maximum - current) return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
        return true;
    }

    private static bool ReceiptMatches(
        TransfiniteInventoryReceipt receipt,
        TransfiniteRotateSettleState pending,
        IReadOnlyList<RewardGoodsTable> rows)
    {
        if (receipt.RotationId != pending.RotationId
            || receipt.RegionId != pending.RegionId
            || receipt.ScoreRewardGroupId != pending.ScoreRewardGroupId
            || receipt.MaxStageProgressIndex != pending.MaxStageProgressIndex
            || receipt.SettleTransfiniteScore != pending.SettleTransfiniteScore
            || receipt.UnSettleTransfiniteScore != pending.UnSettleTransfiniteScore
            || receipt.RewardGoods.Count != rows.Count)
        {
            return false;
        }

        for (int index = 0; index < rows.Count; index++)
        {
            RewardGoodsTable row = rows[index];
            TransfiniteRewardReceipt granted = receipt.RewardGoods[index];
            if (RewardHandler.GetRewardType(row) != RewardType.Item
                || granted.RewardType != (int)RewardType.Item
                || granted.Id != row.Id
                || granted.TemplateId != row.TemplateId
                || granted.Count != row.Count
                || granted.Level != 0
                || granted.Quality != 0
                || granted.Grade != 0
                || granted.Breakthrough != 0
                || granted.ConvertFrom != 0
                || granted.ShowQuality != 0
                || granted.IsGift
                || granted.RewardMulti != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryCommitPlayerReceipt(
        Session session,
        TransfiniteState state,
        TransfiniteRotateSettleReceipt receipt)
    {
        TransfiniteRotateSettleState? previousPending = state.RotateSettleInfo;
        TransfiniteRotateSettleReceipt? previousReceipt = state.LastRotateReceipt;
        long previousModifyTime = state.LastModifyTime;
        state.LastRotateReceipt = receipt;
        state.RotateSettleInfo = null;
        state.LastModifyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            session.player.Save();
            return true;
        }
        catch (Exception exception)
        {
            state.RotateSettleInfo = previousPending;
            state.LastRotateReceipt = previousReceipt;
            state.LastModifyTime = previousModifyTime;
            session.log.Error($"Failed to persist Transfinite settlement state: {exception}");
            return false;
        }
    }

    private static TransfiniteRotateSettleReceipt ToPlayerReceipt(TransfiniteInventoryReceipt receipt) => new()
    {
        RotationId = receipt.RotationId,
        MaxStageProgressIndex = receipt.MaxStageProgressIndex,
        SettleTransfiniteScore = receipt.SettleTransfiniteScore,
        UnSettleTransfiniteScore = receipt.UnSettleTransfiniteScore,
        RewardGoods = receipt.RewardGoods.Select(reward => ToReceipt(ToWireReward(reward))).ToList()
    };

    private static TransfiniteGetRotateSettleInfoResponse ToResponse(TransfiniteRotateSettleReceipt receipt) => new()
    {
        Code = 0,
        MaxStageProgressIndex = receipt.MaxStageProgressIndex,
        SettleTransfiniteScore = receipt.SettleTransfiniteScore,
        UnSettleTransfiniteScore = receipt.UnSettleTransfiniteScore,
        RewardGoodsList = receipt.RewardGoods.Select(ToWireReward).ToList()
    };

    private static TransfiniteGetRotateSettleInfoResponse ToResponse(TransfiniteInventoryReceipt receipt) =>
        ToResponse(ToPlayerReceipt(receipt));

    private static void SendReceiptItemPush(Session session, TransfiniteInventoryReceipt receipt)
    {
        HashSet<int> templateIds = receipt.RewardGoods.Select(reward => reward.TemplateId).ToHashSet();
        List<Item> items = session.inventory.Items
            .Where(item => templateIds.Contains(item.Id))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        if (items.Count > 0)
            session.SendPush(new NotifyItemDataList { ItemDataList = items });
    }

    private static TransfiniteRewardReceipt ToReceipt(RewardGoods reward) => new() { RewardType = reward.RewardType, TemplateId = reward.TemplateId, Count = reward.Count, Level = reward.Level, Quality = reward.Quality, Grade = reward.Grade, Breakthrough = reward.Breakthrough, ConvertFrom = reward.ConvertFrom, ShowQuality = reward.ShowQuality, Id = reward.Id, IsGift = reward.IsGift, RewardMulti = reward.RewardMulti };
    private static RewardGoods ToWireReward(TransfiniteRewardReceipt reward) => new() { RewardType = reward.RewardType, TemplateId = reward.TemplateId, Count = reward.Count, Level = reward.Level, Quality = reward.Quality, Grade = reward.Grade, Breakthrough = reward.Breakthrough, ConvertFrom = reward.ConvertFrom, ShowQuality = reward.ShowQuality, Id = reward.Id, IsGift = reward.IsGift, RewardMulti = reward.RewardMulti };
    private static bool IsAuthorized(TransfiniteState? state) =>
        state is not null
        && state.ActivityId > 0
        && state.ActivityAuthorizedUntil >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        && Activities.Value.ContainsKey(state.ActivityId);

    private static bool TryResolveSettlementConfiguration(
        TransfiniteState state,
        out TransfiniteScoreRewardGroupTable? rewardGroup)
    {
        rewardGroup = null;
        if (!Activities.Value.ContainsKey(state.ActivityId)
            || !Regions.Value.Any(row => row.RegionId == state.RegionId))
        {
            return false;
        }

        TransfiniteRotateSettleState? pending = state.RotateSettleInfo;
        int regionId = pending?.RegionId ?? state.RegionId;
        int groupId = pending?.ScoreRewardGroupId ?? state.ScoreRewardGroupId;
        rewardGroup = RewardGroups.Value.SingleOrDefault(row =>
            row.RegionId == regionId && row.ScoreRewardGroupId == groupId);
        return rewardGroup is not null;
    }

    private static bool TryValidateConfiguration(TransfiniteState state, out TransfiniteScoreRewardGroupTable? rewardGroup)
    {
        rewardGroup = null;
        if (!Activities.Value.TryGetValue(state.ActivityId, out TransfiniteActivityTable? activity)
            || activity.CycleSeconds <= 0 || state.CircleId <= 0 || state.BeginTime < 0
            || state.BeginTime % activity.CycleSeconds != 0
            || state.BeginTime / activity.CycleSeconds + 1 != state.CircleId)
            return false;
        TransfiniteRegionTable? region = Regions.Value.SingleOrDefault(row => row.RegionId == state.RegionId);
        if (region is null || !RotateGroups.Value.TryGetValue(region.RotateGroupId, out TransfiniteRotateGroupTable? rotate)
            || state.StageGroupIndex < 0 || state.StageGroupIndex >= rotate.StageGroupId.Count
            || rotate.StageGroupId[state.StageGroupIndex] != state.StageGroupId
            || !StageGroups.Value.ContainsKey(state.StageGroupId)
            || region.ScoreRewardGroupId != state.ScoreRewardGroupId)
            return false;
        TransfiniteRotateSettleState? pending = state.RotateSettleInfo;
        int rewardRegionId = pending?.RegionId ?? state.RegionId;
        int rewardGroupId = pending?.ScoreRewardGroupId ?? state.ScoreRewardGroupId;
        rewardGroup = RewardGroups.Value.SingleOrDefault(row =>
            row.RegionId == rewardRegionId && row.ScoreRewardGroupId == rewardGroupId);
        return rewardGroup is not null;
    }

    private static TransfiniteData ToWire(TransfiniteState state) => new()
    {
        ActivityId = state.ActivityId, CircleId = state.CircleId, BeginTime = state.BeginTime,
        RegionId = state.RegionId, StageGroupIndex = state.StageGroupIndex,
        BattleInfo = state.BattleInfo is null
            ? new()
            :
            [
                new TransfiniteBattleInfo
                {
                    StageGroupId = state.StageGroupId,
                    StageProgressIndex = state.BattleInfo.StageProgressIndex,
                    StartStageProgress = 0,
                    TeamInfo = null,
                    StageInfo = state.BattleInfo.PassedStageIds.Select(stageId => new TransfiniteStageInfo
                    {
                        StageId = stageId,
                        IsWin = true,
                        SpendTime = 0,
                        Score = state.BattleInfo.Score
                    }).ToList(),
                    Result = new TransfiniteBattleResult(),
                    LastResult = null,
                    HistoryResults = new()
                }
            ],
        BestSpendTime = state.BestSpendTime
            .OrderBy(entry => entry.Key)
            .Select(entry => new TransfiniteBestSpendTime
            {
                StageGroupId = entry.Key,
                BestSpendTime = entry.Value
            })
            .ToList(),
        SendActivityStartMail = state.SendActivityStartMail,
        MaxRotateStageProgressIndex = state.MaxRotateStageProgressIndex,
        RotateSettleInfo = state.RotateSettleInfo is null ? null : new TransfiniteRotateSettleInfo
        {
            MaxStageProgressIndex = state.RotateSettleInfo.MaxStageProgressIndex,
            ScoreRewardGroupId = state.RotateSettleInfo.ScoreRewardGroupId,
            SettleTransfiniteScore = state.RotateSettleInfo.SettleTransfiniteScore,
            UnSettleTransfiniteScore = state.RotateSettleInfo.UnSettleTransfiniteScore,
            GotScoreRewardIndex = state.RotateSettleInfo.GotScoreRewardIndex.ToList()
        },
        StageGroupId = state.StageGroupId, LastModifyTime = state.LastModifyTime,
        ScoreRewardGroupId = state.ScoreRewardGroupId
    };
}
