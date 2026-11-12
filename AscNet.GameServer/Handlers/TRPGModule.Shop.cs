using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.trpg;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class TRPGShopBuyItemRequest
    {
        public int ShopId { get; set; }
        /// <summary>The TRPGShopItem id (XTRPGManager.RequestShopBuyItemSend, AddBuyTimes).</summary>
        public int ItemId { get; set; }
        public int ItemCount { get; set; }
    }

    [MessagePackObject(true)]
    public class TRPGShopBuyItemResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGShopItemInfo
    {
        public int Id { get; set; }
        public int Count { get; set; }
    }

    /// <summary>XTRPGManager.NotifyTRPGShopExtraData: per-shop discount and buy-limit bonus (XTRPGClientShopInfo.UpdateData).</summary>
    [MessagePackObject(true)]
    public class NotifyTRPGShopExtraData
    {
        public List<TRPGShopExtraData> ExtraDatas { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class TRPGShopExtraData
    {
        public int Id { get; set; }
        public int DisCount { get; set; }
        public int AddBuyCount { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal partial class TRPGModule
    {
        // CodeText 20104053-56 (Trpg*), 20012004/5 (ItemCountNotEnough / ItemCapacityNotEnough).
        private const int ShopIdIsError = 20104053, ShopItemIdIsError = 20104054, ShopItemCfgNotExist = 20104055,
            ShopItemBuyCountNotEnough = 20104056, ItemCountNotEnough = 20012004, ItemCapacityNotEnough = 20012005;
        // TRPGBuff.Type of the two talents that touch the shop ("Get 10% off", "shop daily item limit +1").
        private const int ShopDiscountBuffType = 8, ShopDailyLimitBuffType = 9;
        private const int ShopItemResetDaily = 1;

        /// <summary>Resets the resettable buy counts when the daily period changed (TRPGShopItem.ResetType 1 = "reset tomorrow").</summary>
        private static void RollShopDay(TrpgShopBossState state)
        {
            long today = TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (state.ShopDay == today) return;
            HashSet<int> resettable = TableReaderV2.Parse<TRPGShopItemTable>().Where(x => x.ResetType == ShopItemResetDaily).Select(x => x.Id).ToHashSet();
            state.ShopBuys.RemoveAll(x => resettable.Contains(x.ItemId));
            state.ShopDay = today;
        }

        /// <summary>Discount percent (100 = none) and extra buy count of a shop from the activated talents.
        /// AscNet reading of TRPGBuff.Params: [shop id or 0 for every shop, value] (the table only carries the numbers).</summary>
        private static (int DisCount, int AddBuyCount) ShopModifiers(Session session, int shopId)
        {
            List<TRPGBuffTable> buffs = TrpgTalents.ActiveBuffs(session)
                .Where(x => x.Params.Count >= 2 && (x.Params[0] == 0 || x.Params[0] == shopId)).ToList();
            return (buffs.Where(x => x.Type == ShopDiscountBuffType).Select(x => x.Params[1]).DefaultIfEmpty(100).Min(),
                buffs.Where(x => x.Type == ShopDailyLimitBuffType).Sum(x => x.Params[1]));
        }

        internal static List<NotifyTRPGData.NotifyTRPGDataShopInfo> TrpgShopLogin(Session session)
        {
            TrpgShopBossState state = session.player.TrpgShopBoss;
            RollShopDay(state);
            return TableReaderV2.Parse<TRPGShopTable>().Select(shop =>
            {
                (int disCount, int addBuyCount) = ShopModifiers(session, shop.Id);
                return new NotifyTRPGData.NotifyTRPGDataShopInfo
                {
                    Id = (uint)shop.Id,
                    DisCount = disCount,
                    AddBuyCount = addBuyCount,
                    ItemInfos = state.ShopBuys.Where(x => x.ShopId == shop.Id && x.Count > 0)
                        .Select(x => (dynamic)new TRPGShopItemInfo { Id = x.ItemId, Count = x.Count }).ToList()
                };
            }).ToList();
        }

        /// <summary>Re-sends discount and limit bonus after a talent change.</summary>
        internal static void TrpgPushShopExtra(Session session) =>
            session.SendPush(new NotifyTRPGShopExtraData
            {
                ExtraDatas = TableReaderV2.Parse<TRPGShopTable>().Select(shop =>
                {
                    (int disCount, int addBuyCount) = ShopModifiers(session, shop.Id);
                    return new TRPGShopExtraData { Id = shop.Id, DisCount = disCount, AddBuyCount = addBuyCount };
                }).ToList()
            });

        [RequestPacketHandler("TRPGShopBuyItemRequest")]
        public static void TRPGShopBuyItemRequestHandler(Session session, Packet.Request packet)
        {
            TRPGShopBuyItemRequest request = packet.Deserialize<TRPGShopBuyItemRequest>();
            TRPGShopBuyItemResponse response = new() { Code = BuyShopItem(session, request, out List<RewardGoods> rewardList) };
            response.RewardList = rewardList;
            session.SendResponse(response, packet.Id);
        }

        private static int BuyShopItem(Session session, TRPGShopBuyItemRequest request, out List<RewardGoods> rewardList)
        {
            rewardList = new();
            TrpgShopBossState state = session.player.TrpgShopBoss;
            TRPGShopTable? shop = TableReaderV2.Parse<TRPGShopTable>().Find(x => x.Id == request.ShopId);
            if (shop is null) return ShopIdIsError;
            int index = shop.ShopItemId.IndexOf(request.ItemId);
            if (index < 0) return ShopItemIdIsError;
            TRPGShopItemTable? item = TableReaderV2.Parse<TRPGShopItemTable>().Find(x => x.Id == request.ItemId);
            List<RewardGoodsTable> goods = item is null ? [] : RewardHandler.GetRewardGoods(item.RewardId);
            if (item is null || goods.Count == 0) return ShopItemCfgNotExist;
            // 0 means "no condition" in the tables; anything else must hold (10105 stage passed / 10119 target finished).
            if (new[] { shop.OpenCondition, shop.Condition, item.Condition }.Any(x => x > 0 && !ConditionSatisfied(session, x)))
                return ConditionNotPass;

            RollShopDay(state);
            (int disCount, int addBuyCount) = ShopModifiers(session, shop.Id);
            int bought = state.ShopBuys.Where(x => x.ShopId == shop.Id && x.ItemId == item.Id).Sum(x => x.Count);
            // XTRPGManager.GetShopItemCanBuyCount: limit - bought + bonus, never below zero.
            long canBuy = Math.Max(shop.ShopItemCount[index] - bought + addBuyCount, 0);
            if (request.ItemCount <= 0 || request.ItemCount > canBuy) return ShopItemBuyCountNotEnough;

            // Same float math as XTRPGManager.GetShopItemConsumeCount, so server and client price agree.
            long price = checked((long)Math.Ceiling(item.ConsumeCount * (disCount / 100.0)) * request.ItemCount);
            if (session.inventory.SpendableCount(item.ConsumeId) < price) return ItemCountNotEnough;

            List<RewardGoodsTable> scaled = new();
            foreach (RewardGoodsTable row in goods)
            {
                long count = (long)row.Count * request.ItemCount;
                if (count > int.MaxValue) return ShopItemBuyCountNotEnough;
                scaled.Add(new RewardGoodsTable { Id = row.Id, TemplateId = row.TemplateId, Count = (int)count, Params = row.Params.ToList() });
            }
            foreach (IGrouping<int, RewardGoodsTable> group in scaled.GroupBy(x => x.TemplateId))
                if (session.inventory.SpendableCount(group.Key) + group.Sum(x => (long)x.Count) > TrpgItems.MaxCount(session, group.Key))
                    return ItemCapacityNotEnough;

            // One claim key per purchase: a crash between the inventory save and the player save replays the same key and
            // is not charged twice.
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant($"trpg:shop:{state.ShopSerial}", scaled,
                    price > 0 ? new Dictionary<int, int> { [item.ConsumeId] = checked((int)price) } : null)], session);
            TrpgShopBossState.ShopBuy? entry = state.ShopBuys.Find(x => x.ShopId == shop.Id && x.ItemId == item.Id);
            if (entry is null) state.ShopBuys.Add(new() { ShopId = shop.Id, ItemId = item.Id, Count = request.ItemCount });
            else entry.Count += request.ItemCount;
            state.ShopSerial++;
            session.player.SaveChecked();
            result.SendPushes(session);
            rewardList = result.RewardGoods;
            return 0;
        }
    }
}
