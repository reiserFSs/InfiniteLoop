using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MessagePack;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GetAndroidOrIosMoneyCardResponse
    {
        public int Code;
        public int MoneyCard;
        public int Count;
    }

    [MessagePackObject(true)]
    public class ItemUseRequest
    {
        public int Id;
        public int RecycleTime;
        public int Count;
    }
    
    [MessagePackObject(true)]
    public class ItemUseResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ItemSellRequest
    {
        public Dictionary<int, int> SellItems { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ItemSellResponse
    {
        public int Code { get; set; }
        public Dictionary<int, int> ObtainItems { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ItemBuyAssetRequest
    {
        public int Times { get; set; }
        public int ItemId { get; set; }
        public int ConsumeId { get; set; }
    }

    [MessagePackObject(true)]
    public class ItemBuyAssetResponse
    {
        public int Code { get; set; }
        public int Count { get; set; }
        public bool IsCrit { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class ItemModule
    {
        [RequestPacketHandler("GetAndroidOrIosMoneyCardRequest")]
        public static void GetAndroidOrIosMoneyCardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(new GetAndroidOrIosMoneyCardResponse()
            {
                Code = 0,
                Count = 0,
                MoneyCard = 0
            }, packet.Id);
        }
        
        // Fixed-reward gift packs, such as Cog Packs.
        [RequestPacketHandler("ItemUseRequest")]
        public static void ItemUseRequestHandler(Session session, Packet.Request packet)
        {
            ItemUseRequest request = packet.Deserialize<ItemUseRequest>();
            int itemId = request.Id;
            int count = request.Count;
            if (itemId <= 0 || count <= 0)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            ItemTable? itemTable = TableReaderV2.Parse<ItemTable>().FirstOrDefault(item => item.Id == itemId);
            Item? inventoryItem = session.inventory.Items.FirstOrDefault(item => item.Id == itemId);
            if (itemTable is null || inventoryItem is null || inventoryItem.Count < count)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            List<Reward> rewards = [];
            ItemUseResponse response = new() { Code = 0 };
            if (!TryBuildItemUseRewards(itemTable, count, response.RewardGoodsList, rewards))
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            if (rewards.Count == 0)
            {
                session.SendResponse(new ItemUseResponse() { Code = 1 }, packet.Id);
                return;
            }

            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = { session.inventory.Do(itemId, -count) }
            });
            RewardHandler.GiveRewards(rewards, session);
            session.inventory.Save();
            session.character.Save();
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("ItemSellRequest")]
        public static void ItemSellRequestHandler(Session session, Packet.Request packet)
        {
            ItemSellRequest request = packet.Deserialize<ItemSellRequest>();
            if (!TryBuildItemSale(
                    session.inventory,
                    request.SellItems,
                    out ItemSellFailure failure,
                    out Dictionary<int, int> obtainItems,
                    out Dictionary<int, int> itemDeltas))
            {
                session.log.Warn($"ItemSellRequest rejected: {failure}; SellItems={FormatItemSellRequest(request.SellItems)}.");
                session.SendResponse(new ItemSellResponse { Code = 1 }, packet.Id);
                return;
            }

            List<Item> changedItems = itemDeltas
                .OrderBy(itemDelta => itemDelta.Key)
                .Where(itemDelta => itemDelta.Value != 0)
                .Select(itemDelta => session.inventory.Do(itemDelta.Key, itemDelta.Value))
                .ToList();

            session.SendPush(new NotifyItemDataList { ItemDataList = changedItems });
            session.inventory.Save();
            session.SendResponse(new ItemSellResponse
            {
                Code = 0,
                ObtainItems = obtainItems
            }, packet.Id);
        }

        private enum ItemSellFailure
        {
            None,
            EmptyRequest,
            InvalidRequest,
            UnknownItem,
            UnconfiguredSale,
            InvalidPayout,
            InvalidInventoryState,
            InsufficientStock,
            ArithmeticOverflow,
            InvalidFinalBalance
        }

        private static bool TryBuildItemSale(
            AscNet.Common.Database.Inventory inventory,
            Dictionary<int, int>? sellItems,
            out ItemSellFailure failure,
            out Dictionary<int, int> obtainItems,
            out Dictionary<int, int> itemDeltas)
        {
            failure = ItemSellFailure.None;
            obtainItems = [];
            itemDeltas = [];
            if (sellItems is null || sellItems.Count == 0)
                return FailItemSale(out failure, ItemSellFailure.EmptyRequest);

            Dictionary<int, ItemTable> itemTables = TableReaderV2.Parse<ItemTable>()
                .ToDictionary(item => item.Id);
            Dictionary<int, List<Item>> inventoryItems = inventory.Items
                .GroupBy(inventoryItem => inventoryItem.Id)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach ((int itemId, int count) in sellItems)
            {
                if (itemId <= 0 || count <= 0)
                    return FailItemSale(out failure, ItemSellFailure.InvalidRequest);
                if (!itemTables.TryGetValue(itemId, out ItemTable? itemTable))
                    return FailItemSale(out failure, ItemSellFailure.UnknownItem);
                if (itemTable.SellForId is not int obtainItemId
                    || itemTable.SellForCount is not int obtainItemCount
                    || obtainItemId <= 0
                    || obtainItemCount <= 0)
                {
                    return FailItemSale(out failure, ItemSellFailure.UnconfiguredSale);
                }
                if (!itemTables.ContainsKey(obtainItemId)
                    || !AscNet.Common.Database.Inventory.IsValidClientItemId(obtainItemId))
                {
                    return FailItemSale(out failure, ItemSellFailure.InvalidPayout);
                }
                if (!TryGetInventoryItemCount(inventoryItems, itemId, out long inventoryCount))
                    return FailItemSale(out failure, ItemSellFailure.InvalidInventoryState);
                if (inventoryCount < count)
                    return FailItemSale(out failure, ItemSellFailure.InsufficientStock);

                long obtainedCount;
                try
                {
                    obtainedCount = checked((long)count * obtainItemCount);
                }
                catch (OverflowException)
                {
                    return FailItemSale(out failure, ItemSellFailure.ArithmeticOverflow);
                }

                if (obtainedCount > int.MaxValue
                    || !TryAddItemDelta(itemDeltas, itemId, -count)
                    || !TryAddItemDelta(itemDeltas, obtainItemId, obtainedCount)
                    || !TryAddObtainItem(obtainItems, obtainItemId, obtainedCount))
                {
                    return FailItemSale(out failure, ItemSellFailure.ArithmeticOverflow);
                }
            }

            foreach ((int itemId, int delta) in itemDeltas)
            {
                ItemTable itemTable = itemTables[itemId];
                long currentCount = 0;
                if (inventoryItems.ContainsKey(itemId)
                    && !TryGetInventoryItemCount(inventoryItems, itemId, out currentCount))
                {
                    return FailItemSale(out failure, ItemSellFailure.InvalidInventoryState);
                }

                long finalCount;
                try
                {
                    finalCount = checked(currentCount + delta);
                }
                catch (OverflowException)
                {
                    return FailItemSale(out failure, ItemSellFailure.ArithmeticOverflow);
                }

                if (finalCount < 0
                    || itemTable.MaxCount is int maxCount && finalCount > maxCount)
                {
                    return FailItemSale(out failure, ItemSellFailure.InvalidFinalBalance);
                }
            }

            return TryNormalizeInventoryItemStacks(inventory, inventoryItems, itemDeltas.Keys)
                || FailItemSale(out failure, ItemSellFailure.InvalidInventoryState);
        }

        private static bool FailItemSale(out ItemSellFailure failure, ItemSellFailure reason)
        {
            failure = reason;
            return false;
        }

        private static string FormatItemSellRequest(Dictionary<int, int>? sellItems)
        {
            if (sellItems is null)
                return "<null>";

            const int maxLoggedItems = 32;
            SortedDictionary<int, int> loggedItems = new();
            foreach ((int itemId, int count) in sellItems)
            {
                if (loggedItems.Count < maxLoggedItems)
                {
                    loggedItems.Add(itemId, count);
                    continue;
                }

                int largestLoggedItemId = loggedItems.Last().Key;
                if (itemId < largestLoggedItemId)
                {
                    loggedItems.Remove(largestLoggedItemId);
                    loggedItems.Add(itemId, count);
                }
            }

            string truncation = sellItems.Count > loggedItems.Count ? ",..." : string.Empty;
            return $"[{string.Join(",", loggedItems.Select(item => $"{item.Key}:{item.Value}"))}{truncation}] total={sellItems.Count}";
        }

        private static bool TryGetInventoryItemCount(Dictionary<int, List<Item>> inventoryItems, int itemId, out long count)
        {
            count = 0;
            if (!inventoryItems.TryGetValue(itemId, out List<Item>? items))
                return false;

            try
            {
                foreach (Item item in items)
                {
                    if (item.Count < 0)
                        return false;

                    count = checked(count + item.Count);
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        private static bool TryNormalizeInventoryItemStacks(
            AscNet.Common.Database.Inventory inventory,
            Dictionary<int, List<Item>> inventoryItems,
            IEnumerable<int> itemIds)
        {
            foreach (int itemId in itemIds)
            {
                if (!inventoryItems.TryGetValue(itemId, out List<Item>? items) || items.Count <= 1)
                    continue;
                if (!TryGetInventoryItemCount(inventoryItems, itemId, out long count))
                    return false;

                Item primaryItem = items[0];
                primaryItem.Count = count;
                foreach (Item duplicateItem in items.Skip(1))
                    inventory.Items.Remove(duplicateItem);

                inventoryItems[itemId] = [primaryItem];
            }

            return true;
        }

        private static bool TryAddItemDelta(Dictionary<int, int> itemDeltas, int itemId, long delta)
        {
            long currentDelta = itemDeltas.TryGetValue(itemId, out int existingDelta)
                ? existingDelta
                : 0;
            long nextDelta;
            try
            {
                nextDelta = checked(currentDelta + delta);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (nextDelta < int.MinValue || nextDelta > int.MaxValue)
                return false;

            itemDeltas[itemId] = (int)nextDelta;
            return true;
        }

        private static bool TryAddObtainItem(Dictionary<int, int> obtainItems, int itemId, long count)
        {
            long currentCount = obtainItems.TryGetValue(itemId, out int existingCount)
                ? existingCount
                : 0;
            long nextCount;
            try
            {
                nextCount = checked(currentCount + count);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (nextCount > int.MaxValue)
                return false;

            obtainItems[itemId] = (int)nextCount;
            return true;
        }

        private static int ResolveFixedGiftRewardId(ItemTable itemTable)
        {
            if (itemTable.ItemType != (int)AscNet.Common.ItemType.Gift)
                return 0;

            return itemTable.SubTypeParams.Count >= 2 && itemTable.SubTypeParams[0] == 1
                ? itemTable.SubTypeParams[1]
                : 0;
        }

        private static bool TryBuildItemUseRewards(ItemTable itemTable, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            if (TryBuildRandomGiftRewards(itemTable, count, rewardGoodsList, rewards))
                return true;

            int fixedRewardId = ResolveFixedGiftRewardId(itemTable);
            if (fixedRewardId > 0)
                return TryAddRewardGoods(RewardHandler.GetRewardGoods(fixedRewardId), count, rewardGoodsList, rewards);

            return false;
        }

        private static bool TryBuildRandomGiftRewards(ItemTable itemTable, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            if (itemTable.ItemType != (int)AscNet.Common.ItemType.Gift
                || itemTable.SubTypeParams.Count < 2
                || itemTable.SubTypeParams[0] != 2)
            {
                return false;
            }

            List<RewardGoodsTable> configuredRewards = RewardHandler.GetRewardGoods(itemTable.SubTypeParams[1])
                .Where(rewardGoods => RewardHandler.GetRewardType(rewardGoods) is not null)
                .ToList();
            if (configuredRewards.Count == 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                RewardGoodsTable selectedReward = configuredRewards[Random.Shared.Next(configuredRewards.Count)];
                if (!TryAddSingleRewardGoods(selectedReward, 1, rewardGoodsList, rewards))
                    return false;
            }

            return true;
        }


        private static bool TryAddRewardGoods(IEnumerable<RewardGoodsTable> configuredRewards, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            foreach (var rewardGoods in configuredRewards)
            {
                if (!TryAddSingleRewardGoods(rewardGoods, count, rewardGoodsList, rewards))
                    return false;
            }

            return rewards.Count > 0;
        }

        private static bool TryAddSingleRewardGoods(RewardGoodsTable rewardGoods, int count, List<RewardGoods> rewardGoodsList, List<Reward> rewards)
        {
            RewardType? rewardType = RewardHandler.GetRewardType(rewardGoods);
            if (rewardType is null)
                return true;

            long rewardCountLong = (long)rewardGoods.Count * count;
            if (rewardCountLong > int.MaxValue)
                return false;

            int rewardCount = (int)rewardCountLong;
            rewardGoodsList.Add(new RewardGoods
            {
                Id = rewardGoods.Id,
                TemplateId = rewardGoods.TemplateId,
                Count = rewardCount,
                RewardType = (int)rewardType.Value
            });
            rewards.Add(new Reward
            {
                Id = rewardGoods.TemplateId,
                Count = rewardCount,
                Type = rewardType.Value
            });

            return true;
        }

        [RequestPacketHandler("ItemBuyAssetRequest")]
        public static void ItemBuyAssetRequestHandler(Session session, Packet.Request packet)
        {
            ItemBuyAssetRequest request = packet.Deserialize<ItemBuyAssetRequest>();
            int count = Math.Max(0, request.Times);

            Item consumedItem = session.inventory.Do(request.ConsumeId, -count);
            Item boughtItem = session.inventory.Do(request.ItemId, count);

            session.SendPush(new NotifyItemDataList
            {
                ItemDataList = { consumedItem, boughtItem }
            });
            session.inventory.Save();
            session.SendResponse(new ItemBuyAssetResponse
            {
                Count = count,
                IsCrit = false
            }, packet.Id);
        }
     }
}
