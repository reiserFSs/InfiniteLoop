using System.Diagnostics.CodeAnalysis;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.passport;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.task;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class PassportGetSupplyRewardRequest { }
[MessagePackObject(true)]
public sealed class PassportGetSupplyRewardResponse
{
    public int Code { get; set; }
    public List<PassportRewardGoods> RewardGoodsList { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class PassportRecvAllRewardRequest { }
[MessagePackObject(true)]
public sealed class PassportRecvAllRewardResponse
{
    public int Code { get; set; }
    public List<PassportRewardGoods> RewardList { get; set; } = new();
    public List<PassportInfo> PassportInfos { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class PassportRecvRewardRequest
{
    public int Id { get; set; }
}
[MessagePackObject(true)]
public sealed class PassportRecvRewardResponse
{
    public int Code { get; set; }
    public List<PassportRewardGoods> RewardList { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class PassportBuyPassportRequest
{
    public int Id { get; set; }
}
[MessagePackObject(true)]
public sealed class PassportBuyPassportResponse
{
    public int Code { get; set; }
    public List<PassportRewardGoods> RewardList { get; set; } = new();
    public PassportInfo PassportInfo { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class PassportBuyExpRequest
{
    public int ToLevel { get; set; }
}
[MessagePackObject(true)]
public sealed class PassportBuyExpResponse
{
    public int Code { get; set; }
}

internal static class PassportModule
{
    private sealed record PlannedGood(RewardGoodsTable Table, RewardType Type);

    [RequestPacketHandler("PassportGetSupplyRewardRequest")]
    public static void GetSupplyReward(Session session, Packet.Request packet)
    {
        PassportGetSupplyRewardResponse response = new();
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) response.Code = 20137001;
        else if (session.player.Passport.IsGetSupplyReward) response.Code = 20137016;
        else
        {
            int rewardId = ReadSupplyReward(activity);
            if (rewardId <= 0) response.Code = 20137017;
            else if (!TryPlan([rewardId], out List<PlannedGood> plan)) response.Code = 20137010;
            else
            {
                string claimKey = $"passport-supply:{activity.Id}";
                bool inventoryClaimed = session.inventory.AppliedRewardClaims.Contains(
                    claimKey,
                    StringComparer.Ordinal);
                IEnumerable<PlannedGood> pendingPlan = inventoryClaimed
                    ? Array.Empty<PlannedGood>()
                    : plan;
                if (!CanApplyItems(session, pendingPlan)) response.Code = 20137010;
                else
                {
                    long currentExp = session.inventory.Items
                        .FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;
                    long creditedExp = pendingPlan
                        .Where(entry => entry.Table.TemplateId == Inventory.PassportExp)
                        .Sum(entry => (long)entry.Table.Count);
                    int level = ResolveLevel(activity.Id, currentExp + creditedExp);
                    if (level <= 0) response.Code = 20137009;
                    else
                    {
                        RewardApplicationResult? rewardApplication;
                        try
                        {
                            rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                                [new RewardGrant(claimKey, plan.Select(entry => entry.Table).ToList())],
                                session);
                        }
                        catch (Exception exception)
                        {
                            session.log.Error(
                                $"Failed to persist passport supply reward {activity.Id}: {exception}");
                            response.Code = 20137010;
                            rewardApplication = null;
                        }

                        if (rewardApplication is not null)
                        {
                            session.player.Passport.IsGetSupplyReward = true;
                            try
                            {
                                session.player.SaveChecked();
                            }
                            catch (Exception exception)
                            {
                                session.player.Passport.IsGetSupplyReward = false;
                                session.log.Error(
                                    $"Failed to persist passport supply claim {activity.Id}: {exception}");
                                response.Code = 20137010;
                                rewardApplication = null;
                            }
                        }

                        if (rewardApplication is not null)
                        {
                            PassportBaseInfo baseInfo = new()
                            {
                                Level = level,
                                Exp = currentExp + creditedExp
                            };
                            session.SendPush(new NotifyPassportBaseInfo { BaseInfo = baseInfo });
                            rewardApplication.SendPushes(session);
                            response.RewardGoodsList = plan.Select(ToDto).ToList();
                        }
                    }
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("PassportRecvAllRewardRequest")]
    public static void RecvAllReward(Session session, Packet.Request packet)
    {
        PassportRecvAllRewardResponse response = new();
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) response.Code = 20137001;
        else if (!TryBaseInfo(session, activity.Id, out PassportBaseInfo baseInfo)) response.Code = 20137009;
        else
        {
            List<PassportTypeInfoTable> types = TableReaderV2.Parse<PassportTypeInfoTable>()
                .Where(row => row.ActivityId == activity.Id).OrderBy(row => row.Id).ToList();
            Dictionary<int, PassportStateInfo> ownedById;
            int distinctOwnedCount = session.player.Passport.PassportInfos
                .Select(info => info.Id)
                .Distinct()
                .Count();
            if (session.player.Passport.PassportInfos.Count == 0) response.Code = 20137002;
            else if (distinctOwnedCount != session.player.Passport.PassportInfos.Count) response.Code = 20137005;
            else if ((ownedById = session.player.Passport.PassportInfos.ToDictionary(info => info.Id))
                     .Keys.Any(id => !types.Any(type => type.Id == id))) response.Code = 20137005;
            else
            {
                List<(PassportStateInfo Info, PassportRewardTable Reward)> eligible = new();
                foreach (PassportTypeInfoTable type in types)
                {
                    if (!ownedById.TryGetValue(type.Id, out PassportStateInfo? info))
                        continue;
                    foreach (PassportRewardTable reward in TableReaderV2.Parse<PassportRewardTable>()
                        .Where(row => row.PassportId == info.Id && row.Level <= baseInfo.Level && row.RewardId > 0)
                        .OrderBy(row => row.Level).ThenBy(row => row.Id))
                    {
                        if (!info.GotRewardList.Contains(reward.Id)) eligible.Add((info, reward));
                    }
                }
                if (eligible.Count == 0) response.Code = 20137007;
                else
                {
                    List<(PassportStateInfo Info, PassportRewardTable Reward, string ClaimKey, List<PlannedGood> Goods)>
                        planned = [];
                    foreach ((PassportStateInfo info, PassportRewardTable reward) in eligible)
                    {
                        if (!TryPlan([reward.RewardId!.Value], out List<PlannedGood> goods))
                        {
                            planned.Clear();
                            break;
                        }
                        planned.Add((
                            info,
                            reward,
                            $"passport-reward:{activity.Id}:{info.Id}:{reward.Id}",
                            goods));
                    }

                    if (planned.Count != eligible.Count) response.Code = 20137010;
                    else
                    {
                        List<PlannedGood> pendingPlan = planned
                            .Where(grant => !session.inventory.AppliedRewardClaims.Contains(
                                grant.ClaimKey,
                                StringComparer.Ordinal))
                            .SelectMany(grant => grant.Goods)
                            .ToList();
                        if (!CanApplyItems(session, pendingPlan)) response.Code = 20137010;
                        else
                        {
                            RewardApplicationResult? rewardApplication;
                            try
                            {
                                rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                                    planned.Select(grant => new RewardGrant(
                                        grant.ClaimKey,
                                        grant.Goods.Select(entry => entry.Table).ToList())).ToList(),
                                    session);
                            }
                            catch (Exception exception)
                            {
                                session.log.Error(
                                    $"Failed to persist passport rewards for activity {activity.Id}: {exception}");
                                response.Code = 20137010;
                                rewardApplication = null;
                            }

                            if (rewardApplication is not null)
                            {
                                foreach (var grant in planned)
                                    grant.Info.GotRewardList.Add(grant.Reward.Id);
                                try
                                {
                                    session.player.SaveChecked();
                                }
                                catch (Exception exception)
                                {
                                    foreach (var grant in planned)
                                        grant.Info.GotRewardList.Remove(grant.Reward.Id);
                                    session.log.Error(
                                        $"Failed to persist passport reward claims for activity {activity.Id}: {exception}");
                                    response.Code = 20137010;
                                    rewardApplication = null;
                                }
                            }

                            if (rewardApplication is not null)
                            {
                                rewardApplication.SendPushes(session);
                                response.RewardList = planned
                                    .SelectMany(grant => grant.Goods)
                                    .Select(ToDto)
                                    .ToList();
                                response.PassportInfos = planned
                                    .Select(grant => grant.Info)
                                    .Distinct()
                                    .Select(ToDto)
                                    .ToList();
                            }
                        }
                    }
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("PassportRecvRewardRequest")]
    public static void RecvReward(Session session, Packet.Request packet)
    {
        PassportRecvRewardRequest request =
            packet.Deserialize<PassportRecvRewardRequest>();
        PassportRecvRewardResponse response = new();
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) response.Code = 20137001;
        else
        {
            PassportRewardTable? reward = TableReaderV2.Parse<PassportRewardTable>()
                .FirstOrDefault(row => row.Id == request.Id);
            if (reward is null || reward.RewardId is not > 0) response.Code = 20137010;
            else if (!TableReaderV2.Parse<PassportTypeInfoTable>()
                .Any(type => type.Id == reward.PassportId && type.ActivityId == activity.Id))
                response.Code = 20137005;
            else
            {
                PassportStateInfo? info = session.player.Passport.PassportInfos
                    .FirstOrDefault(candidate => candidate.Id == reward.PassportId);
                if (info is null) response.Code = 20137002;
                else if (!TryBaseInfo(session, activity.Id, out PassportBaseInfo baseInfo))
                    response.Code = 20137009;
                else if (reward.Level > baseInfo.Level) response.Code = 20137006;
                else if (info.GotRewardList.Contains(reward.Id)) response.Code = 20137013;
                else if (!TryPlan([reward.RewardId.Value], out List<PlannedGood> plan))
                    response.Code = 20137010;
                else if (!CanApplyItems(session, plan)) response.Code = 20137010;
                else
                {
                    string claimKey = $"passport-reward:{activity.Id}:{info.Id}:{reward.Id}";
                    RewardApplicationResult? rewardApplication;
                    try
                    {
                        rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                            [new RewardGrant(claimKey, plan.Select(entry => entry.Table).ToList())],
                            session);
                    }
                    catch (Exception exception)
                    {
                        session.log.Error(
                            $"Failed to persist passport reward {activity.Id}:{info.Id}:{reward.Id}: {exception}");
                        response.Code = 20137010;
                        rewardApplication = null;
                    }

                    if (rewardApplication is not null)
                    {
                        if (!info.GotRewardList.Contains(reward.Id)) info.GotRewardList.Add(reward.Id);
                        try
                        {
                            session.player.SaveChecked();
                        }
                        catch (Exception exception)
                        {
                            info.GotRewardList.Remove(reward.Id);
                            session.log.Error(
                                $"Failed to persist passport reward claim {activity.Id}:{info.Id}:{reward.Id}: {exception}");
                            response.Code = 20137010;
                            rewardApplication = null;
                        }
                    }

                    if (rewardApplication is not null)
                    {
                        rewardApplication.SendPushes(session);
                        response.RewardList = plan.Select(ToDto).ToList();
                    }
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("PassportBuyPassportRequest")]
    public static void BuyPassport(Session session, Packet.Request packet)
    {
        PassportBuyPassportRequest request =
            packet.Deserialize<PassportBuyPassportRequest>();
        PassportBuyPassportResponse response = new();
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) response.Code = 20137001;
        else
        {
            PassportTypeInfoTable? type = TableReaderV2.Parse<PassportTypeInfoTable>()
                .FirstOrDefault(row => row.Id == request.Id && row.ActivityId == activity.Id);
            if (type is null) response.Code = 20137005;
            else if (type.IsFree == 1) response.Code = 20137003;
            else if (session.player.Passport.PassportInfos.Any(info => info.Id == type.Id))
                response.Code = 20137003;
            else if (!BuyWindowOpen(activity, DateTimeOffset.UtcNow)) response.Code = 20137014;
            else if (type.CostItemId is not > 0 || type.CostItemCount is not > 0 || type.RewardId is not > 0)
                response.Code = 20137010;
            else if (!HasItems(session, type.CostItemId.Value, type.CostItemCount.Value)) response.Code = 20012004;
            else if (!TryPlan([type.RewardId.Value], out List<PlannedGood> plan)) response.Code = 20137010;
            else if (!CanApplyItems(session, plan)) response.Code = 20137010;
            else
            {
                string claimKey = $"passport-tier:{activity.Id}:{type.Id}";
                bool receiptExists = session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal);
                Item? consumed = null;
                if (!receiptExists)
                {
                    consumed = session.inventory.Do(type.CostItemId!.Value, -type.CostItemCount!.Value);
                    session.inventory.Save();
                }
                RewardApplicationResult? rewardApplication;
                try
                {
                    rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                        [new RewardGrant(claimKey, plan.Select(entry => entry.Table).ToList())],
                        session);
                }
                catch (Exception exception)
                {
                    if (!receiptExists)
                    {
                        session.inventory.Do(type.CostItemId!.Value, type.CostItemCount!.Value);
                        session.inventory.Save();
                    }
                    session.log.Error(
                        $"Failed to persist passport tier purchase {activity.Id}:{type.Id}: {exception}");
                    response.Code = 20137010;
                    rewardApplication = null;
                }

                PassportStateInfo info = new() { Id = type.Id };
                if (rewardApplication is not null)
                {
                    session.player.Passport.PassportInfos.Add(info);
                    try
                    {
                        session.player.SaveChecked();
                    }
                    catch (Exception exception)
                    {
                        session.player.Passport.PassportInfos.Remove(info);
                        session.log.Error(
                            $"Failed to persist passport tier ownership {activity.Id}:{type.Id}: {exception}");
                        response.Code = 20137010;
                        rewardApplication = null;
                    }
                }

                if (rewardApplication is not null)
                {
                    if (plan.Any(entry => entry.Table.TemplateId == Inventory.PassportExp))
                        session.SendPush(new NotifyPassportBaseInfo
                        {
                            BaseInfo = ReadBaseInfo(session, activity.Id)
                        });
                    if (consumed is not null)
                        session.SendPush(new NotifyItemDataList { ItemDataList = { consumed } });
                    rewardApplication.SendPushes(session);
                    response.RewardList = plan.Select(ToDto).ToList();
                    response.PassportInfo = ToDto(info);
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("PassportBuyExpRequest")]
    public static void BuyExp(Session session, Packet.Request packet)
    {
        PassportBuyExpRequest request =
            packet.Deserialize<PassportBuyExpRequest>();
        PassportBuyExpResponse response = new();
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) response.Code = 20137001;
        else if (!TryBaseInfo(session, activity.Id, out PassportBaseInfo baseInfo)) response.Code = 20137009;
        else if (request.ToLevel <= baseInfo.Level) response.Code = 20137011;
        else
        {
            List<PassportLevelTable> levels = TableReaderV2.Parse<PassportLevelTable>()
                .Where(row => row.ActivityId == activity.Id).OrderBy(row => row.Level).ToList();
            if (levels.Count == 0) response.Code = 20137009;
            else if (request.ToLevel > levels.Last().Level) response.Code = 20137008;
            else
            {
                PassportLevelTable dest = levels.First(row => row.Level == request.ToLevel);
                if (!BuyWindowOpen(activity, DateTimeOffset.UtcNow)) response.Code = 20137014;
                else
                {
                    long totalCost = levels
                        .Where(row => row.Level > baseInfo.Level && row.Level <= request.ToLevel)
                        .Sum(row => (long)row.CostItemCount);
                    int costItemId = dest.CostItemId;
                    if (costItemId <= 0 || totalCost <= 0 || !Inventory.IsValidClientItemId(costItemId))
                        response.Code = 20137012;
                    else if (!HasItems(session, costItemId, totalCost)) response.Code = 20012004;
                    else
                    {
                        Item? expItem = session.inventory.Items
                            .FirstOrDefault(item => item.Id == Inventory.PassportExp);
                        if (expItem is null) expItem = session.inventory.Do(Inventory.PassportExp, 0);
                        expItem.Count = dest.TotalExp ?? 0;
                        Item consumed = session.inventory.Do(costItemId, -(int)totalCost);
                        session.inventory.Save();
                        session.SendPush(new NotifyItemDataList { ItemDataList = { expItem, consumed } });
                        response.Code = 0;
                    }
                }
            }
        }
        session.SendResponse(response, packet.Id);
    }

    internal static void PrepareLogin(Session session) => ReconcileActivePassport(session);

    /// <summary>Reconciles persisted season state to the currently open activity and pushes the retail login BP block.</summary>
    internal static void ReconcileAndPushLogin(Session session)
    {
        PassportActivityTable? active = ReconcileActivePassport(session);
        if (active is null) return;
        session.SendPush(new NotifyPassportBaseInfo { BaseInfo = ReadBaseInfo(session, active.Id) });
        session.SendPush(BuildNotifyPassportData(session.player, session.inventory));
    }

    /// <summary>Initializes/resets season state against the open activity. Returns the active activity or null when none is open.</summary>
    private static PassportActivityTable? ReconcileActivePassport(Session session)
    {
        PassportActivityTable? active = ResolveOpenActivity(DateTimeOffset.UtcNow);
        if (active is null) return null;

        PassportState state = session.player.Passport;
        bool playerChanged = false;
        bool inventoryChanged = false;
        if (state.ActivityId != active.Id)
        {
            if (state.ActivityId > 0)
            {
                long exp = Exp(session);
                state.LastTimeBaseInfo.Level = ResolveLevel(state.ActivityId, exp);
                state.LastTimeBaseInfo.Exp = exp;
            }
            else
            {
                state.LastTimeBaseInfo = new PassportStateBaseInfo();
            }
            state.ActivityId = active.Id;
            state.PassportInfos.Clear();
            state.PassportInfos.AddRange(FreeTypes(active.Id)
                .Select(type => new PassportStateInfo { Id = type.Id }));
            state.IsGetSupplyReward = false;
            state.IsActivateRegressionTask = false;
            state.IsActivateNewbieTask = false;
            playerChanged = true;
            Item? expItem = session.inventory.Items
                .FirstOrDefault(item => item.Id == Inventory.PassportExp);
            if (expItem is not null && expItem.Count != 0)
            {
                expItem.Count = 0;
                inventoryChanged = true;
            }
        }
        else
        {
            HashSet<int> owned = state.PassportInfos.Select(info => info.Id).ToHashSet();
            foreach (PassportTypeInfoTable type in FreeTypes(active.Id))
            {
                if (owned.Add(type.Id))
                {
                    state.PassportInfos.Add(new PassportStateInfo { Id = type.Id });
                    playerChanged = true;
                }
            }
        }

        if (playerChanged) session.player.SaveChecked();
        if (inventoryChanged) session.inventory.SaveChecked();
        return active;
    }

    /// <summary>Handler gate: an open season must exist and match the persisted activity. No implicit rollover.</summary>
    internal static bool TryActiveActivity(Session session, [NotNullWhen(true)] out PassportActivityTable? activity)
    {
        activity = ResolveOpenActivity(DateTimeOffset.UtcNow);
        return activity is not null && session.player.Passport.ActivityId == activity.Id;
    }

    private static PassportActivityTable? ResolveOpenActivity(DateTimeOffset now) =>
        TableReaderV2.Parse<PassportActivityTable>()
            .Where(row => row.TimeId is > 0 && ActivityScheduleService.IsOpen(row.TimeId.Value, now))
            .OrderByDescending(row => row.Id)
            .FirstOrDefault();

    private static IEnumerable<PassportTypeInfoTable> FreeTypes(int activityId) =>
        TableReaderV2.Parse<PassportTypeInfoTable>().Where(row => row.ActivityId == activityId && row.IsFree == 1);

    private static long Exp(Session session) =>
        session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;

    internal static PassportBaseInfo ReadBaseInfo(Session session, int activityId)
    {
        long exp = Exp(session);
        return new PassportBaseInfo { Level = ResolveLevel(activityId, exp), Exp = exp };
    }

    internal static bool IsActivePassportTask(Session session, int taskId) =>
        TryActiveActivity(session, out PassportActivityTable? activity)
        && ActivePassportTaskIds(activity, DateTimeOffset.UtcNow).Contains(taskId);

    internal static IReadOnlySet<int> PassportTaskIdsByType(Session session, int type)
    {
        if (!TryActiveActivity(session, out PassportActivityTable? activity)) return new HashSet<int>();
        return TableReaderV2.Parse<PassportTaskGroupTable>()
            .Where(group => group.Group == activity.DailyTaskGroup || group.Group == activity.WeekTaskGroup)
            .Where(group => group.Type == type && group.TaskId is not null)
            .SelectMany(group => group.TaskId!)
            .Where(id => id > 0)
            .ToHashSet();
    }

    internal static string PassportTaskClaimKey(Session session, int taskId)
    {
        PassportActivityTable activity = ResolveOpenActivity(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("No active Passport activity.");
        PassportTaskGroupTable? group = ActiveTaskGroups(activity, DateTimeOffset.UtcNow)
            .FirstOrDefault(candidate => candidate.TaskId?.Contains(taskId) == true);
        long period = group?.Type == 1
            ? TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            : CurrentRound(activity, DateTimeOffset.UtcNow);
        return $"passport-task:{activity.Id}:{period}:{taskId}";
    }

    private static HashSet<int> ActivePassportTaskIds(PassportActivityTable activity, DateTimeOffset now)
    {
        HashSet<int> ids = ActiveTaskGroups(activity, now)
            .Where(group => group.TaskId is not null)
            .SelectMany(group => group.TaskId!)
            .Where(id => id > 0)
            .ToHashSet();
        if (activity.BPTask is not null)
            ids.UnionWith(activity.BPTask.Where(id => id > 0));
        return ids;
    }

    private static IEnumerable<PassportTaskGroupTable> ActiveTaskGroups(
        PassportActivityTable activity,
        DateTimeOffset now)
    {
        List<PassportTaskGroupTable> groups = TableReaderV2.Parse<PassportTaskGroupTable>()
            .Where(group => group.Group == activity.DailyTaskGroup || group.Group == activity.WeekTaskGroup)
            .ToList();
        foreach (PassportTaskGroupTable daily in groups.Where(group => group.Type == 1))
            yield return daily;
        PassportTaskGroupTable[] rounds = groups.Where(group => group.Type == 2)
            .OrderBy(group => group.TimeId).ToArray();
        if (rounds.Length > 0)
            yield return rounds[Math.Clamp(CurrentRound(activity, now), 0, rounds.Length - 1)];
    }

    private static int CurrentRound(PassportActivityTable activity, DateTimeOffset now)
    {
        if (activity.TimeId is not > 0
            || !ActivityScheduleService.TryGet(activity.TimeId.Value, out ActivityScheduleEntry schedule))
            return 0;
        long firstWeeklyReset = checked(schedule.StartTime
            + TaskModule.RemainingSecondsInWeeklyResetPeriod(schedule.StartTime));
        if (now.ToUnixTimeSeconds() < firstWeeklyReset) return 0;
        return checked(1 + (int)((now.ToUnixTimeSeconds() - firstWeeklyReset) / 604_800));
    }

    private static bool BuyWindowOpen(PassportActivityTable activity, DateTimeOffset now) =>
        activity.TimeId is > 0
        && ActivityScheduleService.TryGet(activity.TimeId.Value, out ActivityScheduleEntry entry)
        && now.ToUnixTimeSeconds() < entry.EndTime - activity.BuyPassPortEarlyEndTime;

    private static bool HasItems(Session session, int itemId, long count) =>
        (session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0) >= count;

    internal static NotifyPassportData BuildNotifyPassportData(Player player, Inventory inventory)
    {
        PassportState state = player.Passport;
        int level = ResolveLevel(state.ActivityId, Exp(inventory));
        return new NotifyPassportData
        {
            ActivityId = state.ActivityId,
            Level = level,
            PassportInfos = state.PassportInfos.Select(ToDto).ToList(),
            LastTimeBaseInfo = new PassportBaseInfo { Level = state.LastTimeBaseInfo.Level, Exp = state.LastTimeBaseInfo.Exp },
            IsGetSupplyReward = state.IsGetSupplyReward,
            IsActivateRegressionTask = state.IsActivateRegressionTask,
            IsActivateNewbieTask = state.IsActivateNewbieTask
        };
    }

    private static long Exp(Inventory inventory) =>
        inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;

    private static int ReadSupplyReward(PassportActivityTable activity)
    {
        object? value = activity.GetType().GetProperty("SupplyReward")?.GetValue(activity);
        return value is int id ? id : 0;
    }

    private static bool TryBaseInfo(Session session, int activityId, out PassportBaseInfo info)
    {
        long exp = Exp(session);
        int level = ResolveLevel(activityId, exp);
        info = new PassportBaseInfo { Level = level, Exp = exp };
        return level > 0;
    }

    private static int ResolveLevel(int activityId, long exp) => TableReaderV2.Parse<PassportLevelTable>()
        .Where(row => row.ActivityId == activityId && (row.TotalExp ?? 0) <= exp)
        .Select(row => row.Level).DefaultIfEmpty(0).Max();

    private static bool TryPlan(IEnumerable<int> rewardIds, out List<PlannedGood> plan)
    {
        plan = new();
        foreach (int rewardId in rewardIds)
        {
            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewardId);
            if (goods.Count == 0) return false;
            foreach (RewardGoodsTable good in goods)
            {
                RewardType? type = RewardHandler.GetRewardType(good);
                if (good.Count <= 0
                    || type is null
                    || (type == RewardType.Item
                        && !Inventory.IsValidClientItemId(good.TemplateId))
                    || (type == RewardType.FashionColor
                        && !TableReaderV2.Parse<FashionColorTable>()
                            .Any(color => color.Id == good.TemplateId))
                    || (type != RewardType.Item && type != RewardType.FashionColor))
                {
                    return false;
                }
                plan.Add(new PlannedGood(good, type.Value));
            }
        }
        return true;
    }

    private static bool CanApplyItems(Session session, IEnumerable<PlannedGood> plan)
    {
        foreach (IGrouping<int, PlannedGood> group in plan
                     .Where(entry => entry.Type == RewardType.Item)
                     .GroupBy(entry => entry.Table.TemplateId))
        {
            ItemTable? table = TableReaderV2.Parse<ItemTable>()
                .FirstOrDefault(row => row.Id == group.Key);
            long current = session.inventory.Items
                .FirstOrDefault(item => item.Id == group.Key)?.Count ?? 0;
            if (group.Sum(entry => (long)entry.Table.Count) > Inventory.GetMaxCount(table) - current)
                return false;
        }
        return true;
    }

    private static PassportRewardGoods ToDto(PlannedGood entry) => new()
    {
        Id = entry.Table.Id, TemplateId = entry.Table.TemplateId, Count = entry.Table.Count,
        RewardType = (int)entry.Type
    };
    private static PassportInfo ToDto(PassportStateInfo info) => new() { Id = info.Id, GotRewardList = info.GotRewardList.ToList() };
}
