using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.pay;
using AscNet.Table.V2.share.reward;

namespace AscNet.GameServer.Handlers;

internal partial class PayModule
{
    // Client XPurchaseConfigs.PayAddType.Forever. The installed table has one such row.
    private const int AccumulatedPayForever = 2;
    private const int AccumulatedPayIdNotExist = 40001018;
    private const int AccumulatedPayRewardIdError = 40001020;
    private const int AccumulatedPayRewardIdNotExist = 40001021;
    private const int AccumulatedPayRewardAlreadyGot = 40001022;
    private const int AccumulatedPayMoneyNotEnough = 40001023;
    private const int AccumulatedExtraRewardIdNotExist = 40001029;
    private const int PayItemCapacityNotEnough = 20027011;

    internal static NotifyAccumulatedPayData BuildAccumulatedPayData(Player player)
    {
        AccumulatedPayTable? current = CurrentAccumulatedPay();
        return new()
        {
            PayId = current?.Id ?? 0,
            PayMoney = player.AccumulatedPayMoney,
            PayRewardIds = [.. player.AccumulatedPayRewardIds],
            ExtraPayRewardIds = [.. player.AccumulatedExtraPayRewardIds]
        };
    }

    [RequestPacketHandler("GetAccumulatePayRequest")]
    public static void GetAccumulatePayRequestHandler(Session session, Packet.Request packet)
    {
        GetAccumulatePayRequest request = packet.Deserialize<GetAccumulatePayRequest>();
        GetAccumulatePayResponse response = new() { Code = 1 };
        lock (session.player)
        {
            try
            {
                response.Code = TryClaimAccumulatedPay(session, request, response);
            }
            catch (Exception error)
            {
                session.log.Error($"Accumulated recharge claim failed: {error}");
                response.Code = 2;
            }
            session.SendResponse(response, packet.Id);
        }
    }

    private static int TryClaimAccumulatedPay(Session session, GetAccumulatePayRequest request, GetAccumulatePayResponse response)
    {
        AccumulatedPayTable? current = CurrentAccumulatedPay();
        if (current is null || request.PayId != current.Id)
            return AccumulatedPayIdNotExist;

        AccumulatedPayRewardTable? reward = TableReaderV2.Parse<AccumulatedPayRewardTable>()
            .SingleOrDefault(row => row.Id == request.RewardId);
        if (reward is null)
            return AccumulatedPayRewardIdNotExist;
        if (current.PayRewardId?.Contains(request.RewardId) != true)
            return AccumulatedPayRewardIdError;
        AccumulatedExtraPayRewardTable? extra = TableReaderV2.Parse<AccumulatedExtraPayRewardTable>()
            .SingleOrDefault(row => row.Id == reward.ExtraPayRewardId);
        if (extra is null)
            return AccumulatedExtraRewardIdNotExist;
        if (session.player.AccumulatedPayMoney < reward.Money)
            return AccumulatedPayMoneyNotEnough;
        if (session.player.AccumulatedPayRewardIds.Contains(reward.Id))
            return AccumulatedPayRewardAlreadyGot;

        List<RewardGoodsTable> goods = [];
        foreach (int rewardId in new[] { reward.BigRewardId, reward.SmallRewardId, extra.ExtraBigRewardId, extra.ExtraSmallRewardId })
        {
            if (rewardId <= 0)
                continue;
            List<RewardGoodsTable> rows = RewardHandler.GetRewardGoods(rewardId);
            if (rows.Count == 0)
                return 1;
            goods.AddRange(rows);
        }

        string claimKey = $"accumulated-pay:{session.player.PlayerData.Id}:{reward.Id}";
        bool alreadyGranted = session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
            && session.character.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal);
        if (!alreadyGranted && !HasItemCapacity(session, goods))
            return PayItemCapacityNotEnough;

        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
            [new RewardGrant(claimKey, goods)], session);
        session.player.AccumulatedPayRewardIds.Add(reward.Id);
        bool addedExtra = extra.Id > 0 && !session.player.AccumulatedExtraPayRewardIds.Contains(extra.Id);
        if (addedExtra)
            session.player.AccumulatedExtraPayRewardIds.Add(extra.Id);
        try
        {
            session.player.SaveChecked();
        }
        catch
        {
            session.player.AccumulatedPayRewardIds.Remove(reward.Id);
            if (addedExtra)
                session.player.AccumulatedExtraPayRewardIds.Remove(extra.Id);
            throw;
        }

        session.SendPush(BuildAccumulatedPayData(session.player));
        response.ExtraPayRewardId = extra.Id;
        response.RewardGoodsList = result.RewardGoods;
        result.SendPushes(session);
        return 0;
    }

    private static AccumulatedPayTable? CurrentAccumulatedPay()
    {
        List<AccumulatedPayTable> rows = TableReaderV2.Parse<AccumulatedPayTable>()
            .Where(row => row.Type == AccumulatedPayForever)
            .OrderBy(row => row.Id)
            .ToList();
        return rows.Count == 1 ? rows[0] : null;
    }
}
