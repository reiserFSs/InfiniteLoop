using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.fashion;
using AscNet.Table.V2.share.passport;
using AscNet.Table.V2.share.reward;
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

internal static class PassportModule
{
    private sealed record PlannedGood(RewardGoodsTable Table, RewardType Type);

    [RequestPacketHandler("PassportGetSupplyRewardRequest")]
    public static void GetSupplyReward(Session session, Packet.Request packet)
    {
        PassportGetSupplyRewardResponse response = new();
        if (!TryActivity(session.player, out PassportActivityTable? activity)) response.Code = 20137001;
        else if (session.player.Passport.IsGetSupplyReward) response.Code = 20137016;
        else
        {
            int rewardId = ReadSupplyReward(activity!);
            if (rewardId <= 0) response.Code = 20137017;
            else if (!TryPlan([rewardId], out List<PlannedGood> plan)) response.Code = 20137010;
            else
            {
                string claimKey = $"passport-supply:{activity!.Id}";
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
        if (!TryActivity(session.player, out PassportActivityTable? activity)) response.Code = 20137001;
        else if (!TryBaseInfo(session, activity!.Id, out PassportBaseInfo baseInfo)) response.Code = 20137009;
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

    internal static NotifyPassportData BuildNotifyPassportData(Player player, Inventory inventory)
    {
        PassportState state = player.Passport;
        int level = ResolveLevel(state.ActivityId, inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0);
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

    private static bool TryActivity(Player player, out PassportActivityTable? activity)
    {
        activity = TableReaderV2.Parse<PassportActivityTable>().FirstOrDefault(row => row.Id == player.Passport.ActivityId);
        return player.Passport.ActivityId > 0 && activity is not null;
    }

    private static int ReadSupplyReward(PassportActivityTable activity)
    {
        object? value = activity.GetType().GetProperty("SupplyReward")?.GetValue(activity);
        return value is int id ? id : 0;
    }

    private static bool TryBaseInfo(Session session, int activityId, out PassportBaseInfo info)
    {
        long exp = session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;
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
