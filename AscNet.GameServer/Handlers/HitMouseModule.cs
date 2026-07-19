using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.miniactivity.hitmouse;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class NotifyHitMouseData { public int ActivityId { get; set; } public List<HitMouseLevelScore> LevelScores { get; set; } = new(); public List<int> GetRewardIndex { get; set; } = new(); }
[MessagePackObject(true)] public sealed class HitMouseLevelScore { public int StageId { get; set; } public int Scores { get; set; } }
[MessagePackObject(true)] public sealed class HitMouseUnlockRequest { public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class HitMouseUnlockResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class FinishHitMouseRequest { public int StageId { get; set; } public int Scores { get; set; } }
[MessagePackObject(true)] public sealed class FinishHitMouseResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class HitMouseGetAwardRequest { }
[MessagePackObject(true)] public sealed class HitMouseGetAwardResponse { public int Code { get; set; } public List<int> GetRewardIndex { get; set; } = new(); public List<RewardGoods> RewardGoods { get; set; } = new(); }

internal static class HitMouseModule
{
    internal const int ActivityNotOpen = 20160001;
    internal const int ScoreError = 20160002;
    internal const int ConfigNotFind = 20160003;
    internal const int StageNotUnlock = 20160004;
    internal const int PreStageNotUnlock = 20160005;
    internal const int PreStageNotFinish = 20160006;
    internal const int NotRewardGet = 20160007;
    internal const int UnlockStage = 20160008;
    internal const int ItemCountNotEnough = 20012004;

    private static readonly Lazy<List<HitMouseActivityTable>> Activities = new(() => TableReaderV2.Parse<HitMouseActivityTable>());
    private static readonly Lazy<Dictionary<int, HitMouseStageTable>> Stages = new(() => TableReaderV2.Parse<HitMouseStageTable>().ToDictionary(row => row.Id));

    // TimeLimit/ETCD is unavailable. Do not infer an open window from the activity row alone.
    private static HitMouseActivityTable? ActiveActivity() => null;

    internal static NotifyHitMouseData BuildNotifyHitMouseData(Player player)
    {
        HitMouseActivityTable? activity = ActiveActivity();
        if (activity is null)
            return new NotifyHitMouseData { ActivityId = 0 };

        HitMouseState state = Reconcile(player, activity.Id);
        return new NotifyHitMouseData
        {
            ActivityId = activity.Id,
            LevelScores = state.LevelScores.OrderBy(pair => pair.Key).Select(pair => new HitMouseLevelScore { StageId = pair.Key, Scores = pair.Value }).ToList(),
            GetRewardIndex = state.ClaimedRewardIndices.Distinct().OrderBy(index => index).ToList()
        };
    }

    [RequestPacketHandler("HitMouseUnlockRequest")]
    public static void Unlock(Session session, Packet.Request packet)
    {
        HitMouseUnlockRequest request = packet.Deserialize<HitMouseUnlockRequest>();
        if (!TryActive(session.player, out HitMouseActivityTable activity, out HitMouseState state))
        {
            session.SendResponse(new HitMouseUnlockResponse { Code = ActivityNotOpen }, packet.Id);
            return;
        }
        if (!Stages.Value.TryGetValue(request.StageId, out HitMouseStageTable? stage) || stage.ActivityId != activity.Id)
        {
            session.SendResponse(new HitMouseUnlockResponse { Code = ConfigNotFind }, packet.Id);
            return;
        }

        int code = TryUnlock(state, activity, stage, session.inventory, out Item? changedItem);
        if (code != 0)
        {
            session.SendResponse(new HitMouseUnlockResponse { Code = code }, packet.Id);
            return;
        }

        session.player.Save();
        session.inventory.Save();
        session.SendPush(new NotifyItemDataList { ItemDataList = [changedItem!] });
        session.SendResponse(new HitMouseUnlockResponse { Code = 0 }, packet.Id);
    }

    [RequestPacketHandler("FinishHitMouseRequest")]
    public static void Finish(Session session, Packet.Request packet)
    {
        FinishHitMouseRequest request = packet.Deserialize<FinishHitMouseRequest>();
        if (!TryActive(session.player, out HitMouseActivityTable activity, out HitMouseState state))
        {
            session.SendResponse(new FinishHitMouseResponse { Code = ActivityNotOpen }, packet.Id);
            return;
        }
        if (!Stages.Value.TryGetValue(request.StageId, out HitMouseStageTable? stage) || stage.ActivityId != activity.Id)
        {
            session.SendResponse(new FinishHitMouseResponse { Code = ConfigNotFind }, packet.Id);
            return;
        }

        int code = TryFinish(state, request.StageId, request.Scores);
        if (code == 0)
            session.player.Save();
        session.SendResponse(new FinishHitMouseResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("HitMouseGetAwardRequest")]
    public static void GetAward(Session session, Packet.Request packet)
    {
        _ = packet.Deserialize<HitMouseGetAwardRequest>();
        if (!TryActive(session.player, out HitMouseActivityTable activity, out HitMouseState state))
        {
            session.SendResponse(new HitMouseGetAwardResponse { Code = ActivityNotOpen }, packet.Id);
            return;
        }

        List<int> indices = EligibleRewardIndices(state, activity.RewardScores);
        if (indices.Count == 0)
        {
            session.SendResponse(new HitMouseGetAwardResponse { Code = NotRewardGet }, packet.Id);
            return;
        }
        if (!TryPrepareItemRewards(activity, indices, session.inventory, out List<RewardGoodsTable> rows))
        {
            session.SendResponse(new HitMouseGetAwardResponse { Code = ConfigNotFind }, packet.Id);
            return;
        }

        List<Item> changedItems = ApplyItemRewards(rows, session.inventory);
        state.ClaimedRewardIndices.AddRange(indices);
        state.ClaimedRewardIndices = state.ClaimedRewardIndices.Distinct().OrderBy(index => index).ToList();
        session.player.Save();
        session.inventory.Save();
        if (changedItems.Count > 0)
            session.SendPush(new NotifyItemDataList { ItemDataList = changedItems });
        session.SendResponse(new HitMouseGetAwardResponse
        {
            Code = 0,
            GetRewardIndex = indices,
            RewardGoods = rows.Select(row => new RewardGoods { Id = row.Id, TemplateId = row.TemplateId, Count = row.Count, RewardType = (int)RewardType.Item }).ToList()
        }, packet.Id);
    }

    internal static int TryUnlock(HitMouseState state, HitMouseActivityTable activity, HitMouseStageTable stage, Inventory inventory, out Item? changedItem)
    {
        changedItem = null;
        if (state.LevelScores.ContainsKey(stage.Id)) return UnlockStage;
        int predecessor = stage.PreStageId ?? 0;
        if (predecessor > 0)
        {
            if (!state.LevelScores.TryGetValue(predecessor, out int predecessorScore)) return PreStageNotUnlock;
            if (predecessorScore <= 0) return PreStageNotFinish;
        }
        int cost = stage.UnlockItemCount;
        if (cost < 0 || activity.UseItem <= 0) return ConfigNotFind;
        Item? item = inventory.Items.FirstOrDefault(value => value.Id == activity.UseItem);
        if (item is null || item.Count < cost) return ItemCountNotEnough;
        changedItem = inventory.Do(activity.UseItem, -cost);
        state.LevelScores.Add(stage.Id, 0);
        return 0;
    }

    internal static int TryFinish(HitMouseState state, int stageId, int score)
    {
        if (score < 0) return ScoreError;
        if (!state.LevelScores.TryGetValue(stageId, out int best)) return StageNotUnlock;
        if (score > best) state.LevelScores[stageId] = score;
        return 0;
    }

    internal static List<int> EligibleRewardIndices(HitMouseState state, IReadOnlyList<int> thresholds)
    {
        long total = state.LevelScores.Values.Aggregate(0L, (sum, score) => checked(sum + score));
        HashSet<int> claimed = state.ClaimedRewardIndices.ToHashSet();
        List<int> result = new();
        for (int index = 0; index < thresholds.Count; index++)
            if (thresholds[index] >= 0 && total >= thresholds[index] && !claimed.Contains(index)) result.Add(index);
        return result;
    }

    private static bool TryPrepareItemRewards(HitMouseActivityTable activity, IReadOnlyList<int> indices, Inventory inventory, out List<RewardGoodsTable> rows)
    {
        rows = new();
        foreach (int index in indices)
        {
            if (index < 0 || index >= activity.RewardIds.Count || activity.RewardIds[index] <= 0) return false;
            List<RewardGoodsTable> rewardRows = RewardHandler.GetRewardGoods(activity.RewardIds[index]);
            if (rewardRows.Count == 0 || rewardRows.Any(row => RewardHandler.GetRewardType(row) != RewardType.Item || row.Count < 0)) return false;
            rows.AddRange(rewardRows);
        }
        foreach (IGrouping<int, RewardGoodsTable> group in rows.GroupBy(row => row.TemplateId))
        {
            ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().FirstOrDefault(row => row.Id == group.Key);
            if (itemTable is null || !Inventory.IsValidClientItemId(group.Key)) return false;
            long current = inventory.Items.Where(item => item.Id == group.Key).Sum(item => item.Count);
            long addition = group.Sum(row => (long)row.Count);
            if (addition > Inventory.GetMaxCount(itemTable) - current) return false;
        }
        return true;
    }

    private static List<Item> ApplyItemRewards(IEnumerable<RewardGoodsTable> rows, Inventory inventory) => rows
        .GroupBy(row => row.TemplateId)
        .Select(group => inventory.Do(group.Key, checked(group.Sum(row => row.Count))))
        .ToList();

    private static bool TryActive(Player player, out HitMouseActivityTable activity, out HitMouseState state)
    {
        activity = ActiveActivity()!;
        if (activity is null) { state = null!; return false; }
        state = Reconcile(player, activity.Id);
        return true;
    }

    private static HitMouseState Reconcile(Player player, int activityId)
    {
        if (player.HitMouse?.ActivityId != activityId)
            player.HitMouse = new HitMouseState { ActivityId = activityId };
        return player.HitMouse;
    }
}
