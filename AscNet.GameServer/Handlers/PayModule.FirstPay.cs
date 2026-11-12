using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.pay;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class GetFirstPayRewardRequest { }

[MessagePackObject(true)]
public sealed class GetFirstPayRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
}

// The 4.8 client declares PayCheckResultRequest (XPayManager.METHOD_NAME.CheckResult) but never
// issues it, so GameOrder is the only identity a caller can sensibly supply (cpOrderId in Pay()).
[MessagePackObject(true)]
public sealed class PayCheckResultRequest
{
    public string? GameOrder { get; set; }
}

[MessagePackObject(true)]
public sealed class PayCheckResultResponse
{
    public int Code { get; set; }
    public string GameOrder { get; set; } = "";
    public bool LocalCompleted { get; set; }
    public List<RewardGoods> RewardList { get; set; } = new();
}

internal partial class PayModule
{
    private const int PayFirstPayRewardIsGet = 40001013;
    private const int PayTotalPayMoneyNotEnough = 40001014;
    private const int PayReceiptGameOrderIsNullOrEmpty = 40001006;

    internal static NotifyPayInfo BuildNotifyPayInfo(Player player) => new()
    {
        // CN XPayManager compares this value with its client table, which is in yuan.
        TotalPayMoney = player.ClientRegion == "cn" ? player.TotalPayMoney / 100f : player.TotalPayMoney,
        FirstRewardReceivedList = [.. player.FirstPayRewardIds]
    };

    [RequestPacketHandler("GetFirstPayRewardRequest")]
    public static void GetFirstPayRewardRequestHandler(Session session, Packet.Request packet)
    {
        GetFirstPayRewardResponse response = new() { Code = 2 };
        lock (session.player)
        {
            try { response.Code = ClaimFirstPay(session, response); }
            catch (Exception error) { session.log.Error($"First recharge claim failed: {error}"); response.Code = 2; }
            session.SendResponse(response, packet.Id);
        }
    }

    private static int ClaimFirstPay(Session session, GetFirstPayRewardResponse response)
    {
        List<FirstPayRewardTable> rows = [.. TableReaderV2.Parse<FirstPayRewardTable>(session.player.ClientRegion).OrderBy(row => row.Id)];
        if (rows.Count == 0) return 2;
        List<FirstPayRewardTable> open = rows.Where(row => !session.player.FirstPayRewardIds.Contains(row.Id)).ToList();
        if (open.Count == 0) return PayFirstPayRewardIsGet;
        List<FirstPayRewardTable> due = open.Where(row => session.player.TotalPayMoney >= row.NeedPayMoney).ToList();
        if (due.Count == 0) return PayTotalPayMoneyNotEnough;

        List<RewardGoodsTable> goods = [];
        foreach (FirstPayRewardTable row in due)
            foreach (int rewardId in new[] { row.BigRewardId, row.SmallRewardId })
            {
                if (rewardId <= 0) continue;
                List<RewardGoodsTable> rewards = RewardHandler.GetRewardGoods(rewardId);
                if (rewards.Count == 0) return 2;
                goods.AddRange(rewards);
            }
        string claimKey = $"first-pay:{session.player.PlayerData.Id}:{string.Join('+', due.Select(row => row.Id))}";
        if (!session.inventory.AppliedRewardClaims.Contains(claimKey, StringComparer.Ordinal)
            && !HasItemCapacity(session, goods))
            return PayItemCapacityNotEnough;

        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant(claimKey, goods)], session);
        int[] ids = due.Select(row => row.Id).ToArray();
        session.player.FirstPayRewardIds.AddRange(ids);
        try { session.player.SaveChecked(); }
        catch { session.player.FirstPayRewardIds.RemoveAll(ids.Contains); throw; }
        session.SendPush(BuildNotifyPayInfo(session.player));
        response.RewardList = result.RewardGoods;
        result.SendPushes(session);
        return 0;
    }

    [RequestPacketHandler("PayCheckResultRequest")]
    public static void PayCheckResultRequestHandler(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<PayCheckResultRequest>();
        PayCheckResultResponse response = new() { Code = PayReceiptGameOrderIsNullOrEmpty };
        lock (session.player)
        {
            try
            {
                PlayerPendingRecharge? pending = session.player.PendingRecharge;
                string order = request.GameOrder ?? "";
                if (pending is not null && (order.Length == 0 || order == pending.Order))
                {
                    // An interrupted recharge is finalized exactly as PayInitiatedRequest would.
                    PayInitiatedResponse done = CompleteLocalRecharge(session, pending.Key, null);
                    response = new() { Code = done.Code, GameOrder = done.GameOrder,
                        LocalCompleted = done.LocalCompleted, RewardList = done.RewardList };
                }
                else if (pending is null && order.Length > 0
                    && session.inventory.AppliedRewardClaims.Contains(order, StringComparer.Ordinal)
                    && order.StartsWith($"local:{session.player.PlayerData.Id}:", StringComparison.Ordinal))
                {
                    response = new() { Code = 0, GameOrder = order, LocalCompleted = true };
                }
            }
            catch (Exception error) { session.log.Error($"Pay result check failed: {error}"); response.Code = 2; }
            session.SendResponse(response, packet.Id);
        }
    }
}
