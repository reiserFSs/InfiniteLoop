using AscNet.Common.Database;
using AscNet.GameServer.Handlers.Drops;
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
        public List<int>? SelectRewardIds { get; set; }
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
    public class ItemExchangeRequest
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public int UseItemId { get; set; }
    }

    [MessagePackObject(true)]
    public class ItemExchangeResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
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
        
        [RequestPacketHandler("ItemUseRequest")]
        public static void ItemUseRequestHandler(Session session, Packet.Request packet)
        {
            ItemUseRequest request = packet.Deserialize<ItemUseRequest>();
            if (request.Id <= 0 || request.Count <= 0 || request.RecycleTime < 0
                || request.SelectRewardIds is { Count: > 0 })
            {
                session.SendResponse(new ItemUseResponse { Code = 1 }, packet.Id);
                return;
            }

            ItemUsePendingOperation? pending = session.player.PendingItemUse;
            if (pending is not null)
            {
                if (pending.ItemId != request.Id || pending.Count != request.Count
                    || pending.RecycleTime != request.RecycleTime)
                {
                    session.SendResponse(new ItemUseResponse { Code = 1 }, packet.Id);
                    return;
                }
            }
            else
            {
                ItemTable? item = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == request.Id);
                List<Item> stacks = session.inventory.Items.Where(row => row.Id == request.Id).ToList();
                if (item is null || stacks.Count != 1 || stacks[0].Count < request.Count
                    || stacks[0].Count > Inventory.GetMaxCount(item)
                    || !TryBuildItemUseRewards(item, request.Count, out List<RewardGoodsTable> goods)
                    || !CanApplyItemUseGoods(session, request.Id, request.Count, goods))
                {
                    session.SendResponse(new ItemUseResponse { Code = 1 }, packet.Id);
                    return;
                }

                pending = new ItemUsePendingOperation
                {
                    ClaimKey = $"item-use:{session.player.PlayerData.Id}:{Guid.NewGuid():N}",
                    ItemId = request.Id,
                    Count = request.Count,
                    RecycleTime = request.RecycleTime,
                    Goods = goods.Select(row => new ItemUsePendingReward
                    {
                        Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                        Params = row.Params.ToList()
                    }).ToList()
                };
                session.player.PendingItemUse = pending;
                try { session.player.SaveChecked(); }
                catch
                {
                    session.player.PendingItemUse = null;
                    throw;
                }
            }

            RewardApplicationResult result = CompletePendingItemUse(session);
            result.SendPushes(session);
            session.SendResponse(new ItemUseResponse { RewardGoodsList = result.RewardGoods }, packet.Id);
        }

        public static void ResumePendingItemUse(Session session)
        {
            if (session.player.PendingItemUse is not null)
                CompletePendingItemUse(session);
        }

        private static RewardApplicationResult CompletePendingItemUse(Session session)
        {
            ItemUsePendingOperation pending = session.player.PendingItemUse
                ?? throw new InvalidOperationException("No pending item use.");
            List<RewardGoodsTable> goods = pending.Goods.Select(row => new RewardGoodsTable
            {
                Id = row.Id, TemplateId = row.TemplateId, Count = row.Count, Params = row.Params.ToList()
            }).ToList();
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(pending.ClaimKey, goods,
                    new Dictionary<int, int> { [pending.ItemId] = pending.Count })], session);
            session.player.PendingItemUse = null;
            try { session.player.SaveChecked(); }
            catch
            {
                session.player.PendingItemUse = pending;
                throw;
            }
            return result;
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

        private static bool TryBuildItemUseRewards(ItemTable item, int count, out List<RewardGoodsTable> goods)
        {
            goods = [];
            if (item.ItemType != (int)AscNet.Common.ItemType.Gift || item.SubTypeParams.Count < 2)
                return false;

            int sourceId = item.SubTypeParams[1];
            switch (item.SubTypeParams[0])
            {
                case 1:
                case 5:
                    List<RewardGoodsTable> fixedGoods = RewardHandler.GetRewardGoods(sourceId);
                    RewardTable? source = TableReaderV2.Parse<RewardTable>().Find(row => row.Id == sourceId);
                    if (source is null || fixedGoods.Count == 0
                        || source.SubIds.Count != fixedGoods.Count)
                        return false;
                    foreach (RewardGoodsTable row in fixedGoods)
                    {
                        long total = (long)row.Count * count;
                        if (total <= 0 || total > int.MaxValue || RewardHandler.GetRewardType(row) is null)
                            return false;
                        goods.Add(new RewardGoodsTable
                        {
                            Id = row.Id, TemplateId = row.TemplateId, Count = (int)total,
                            Params = row.Params.ToList()
                        });
                    }
                    return true;
                case 2:
                case 6:
                    if (!EquipmentOverclockDropPolicy.TryResolve(sourceId,
                            out IReadOnlyList<RewardGoodsTable> pool, out int countPerBox)
                        || (long)count * countPerBox > int.MaxValue)
                        return false;
                    Dictionary<int, int> counts = new();
                    for (int index = 0; index < count; index++)
                    {
                        int templateId = pool[Random.Shared.Next(pool.Count)].TemplateId;
                        counts[templateId] = counts.GetValueOrDefault(templateId) + countPerBox;
                    }
                    goods.AddRange(counts.Select(entry => new RewardGoodsTable
                    {
                        TemplateId = entry.Key, Count = entry.Value, Params = []
                    }));
                    return true;
                default:
                    return false;
            }
        }

        private static bool CanApplyItemUseGoods(Session session, int itemId, int count,
            IReadOnlyList<RewardGoodsTable> goods)
        {
            foreach (IGrouping<int, RewardGoodsTable> group in goods
                         .Where(row => RewardHandler.GetRewardType(row) == RewardType.Item)
                         .GroupBy(row => row.TemplateId))
            {
                ItemTable? item = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == group.Key);
                List<Item> stacks = session.inventory.Items.Where(row => row.Id == group.Key).ToList();
                if (item is null || stacks.Count > 1 || stacks.Any(row => row.Count < 0))
                    return false;
                long current = stacks.Count == 0 ? 0 : stacks[0].Count;
                long award = group.Sum(row => (long)row.Count);
                long cost = group.Key == itemId ? count : 0;
                if (current > Inventory.GetMaxCount(item) - award + cost)
                    return false;
            }
            return true;
        }

        [RequestPacketHandler("ItemExchangeRequest")]
        public static void ItemExchangeRequestHandler(Session session, Packet.Request packet)
        {
            ItemExchangeRequest request = packet.Deserialize<ItemExchangeRequest>();
            if (!TryBuildItemExchange(session.inventory, request, out int errorCode, out int totalCost))
            {
                session.SendResponse(new ItemExchangeResponse { Code = errorCode }, packet.Id);
                return;
            }

            List<Item> changedItems =
            [
                session.inventory.Do(request.UseItemId, -totalCost),
                session.inventory.Do(request.ItemId, request.Count)
            ];
            session.SendPush(new NotifyItemDataList { ItemDataList = changedItems });
            session.inventory.Save();
            session.SendResponse(new ItemExchangeResponse
            {
                RewardGoodsList =
                {
                    new RewardGoods
                    {
                        RewardType = (int)RewardType.Item,
                        TemplateId = request.ItemId,
                        Count = request.Count
                    }
                }
            }, packet.Id);
        }

        private static bool TryBuildItemExchange(
            AscNet.Common.Database.Inventory inventory,
            ItemExchangeRequest request,
            out int errorCode,
            out int totalCost)
        {
            const int invalidRequestCode = 20012001;
            const int itemCountNotEnoughCode = 20012004;
            errorCode = invalidRequestCode;
            totalCost = 0;

            ItemExchangeTable? recipe = TableReaderV2.Parse<ItemExchangeTable>()
                .SingleOrDefault(candidate =>
                    candidate.ItemId == request.ItemId
                    && candidate.UseItemId == request.UseItemId);
            if (recipe is null
                || request.Count <= 0
                || request.Count < recipe.MinCount
                || recipe.UseItemCount <= 0)
            {
                return false;
            }

            Dictionary<int, ItemTable> itemTables = TableReaderV2.Parse<ItemTable>()
                .ToDictionary(item => item.Id);
            if (!itemTables.TryGetValue(request.ItemId, out ItemTable? rewardTable)
                || !itemTables.ContainsKey(request.UseItemId)
                || !AscNet.Common.Database.Inventory.IsValidClientItemId(request.ItemId)
                || !AscNet.Common.Database.Inventory.IsValidClientItemId(request.UseItemId))
            {
                return false;
            }

            try
            {
                totalCost = checked(request.Count * recipe.UseItemCount);
            }
            catch (OverflowException)
            {
                return false;
            }

            List<Item> costStacks = inventory.Items.Where(item => item.Id == request.UseItemId).ToList();
            if (costStacks.Count == 0 || costStacks.Any(item => item.Count < 0))
            {
                errorCode = itemCountNotEnoughCode;
                return false;
            }

            long available;
            try
            {
                available = costStacks.Aggregate(0L, (count, item) => checked(count + item.Count));
            }
            catch (OverflowException)
            {
                return false;
            }
            if (available < totalCost)
            {
                errorCode = itemCountNotEnoughCode;
                return false;
            }

            long finalRewardCount;
            try
            {
                long currentRewardCount = inventory.Items
                    .Where(item => item.Id == request.ItemId)
                    .Aggregate(0L, (count, item) => checked(count + item.Count));
                finalRewardCount = checked(currentRewardCount + request.Count);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (finalRewardCount < 0
                || finalRewardCount > AscNet.Common.Database.Inventory.GetMaxCount(rewardTable))
            {
                return false;
            }

            Dictionary<int, List<Item>> inventoryItems = inventory.Items
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.ToList());
            return TryNormalizeInventoryItemStacks(
                inventory,
                inventoryItems,
                new[] { request.UseItemId, request.ItemId });
        }

        [RequestPacketHandler("ItemBuyAssetRequest")]
        public static void ItemBuyAssetRequestHandler(Session session, Packet.Request packet)
        {
            ItemBuyAssetRequest request = packet.Deserialize<ItemBuyAssetRequest>();
            lock (session.player)
            {
                // Resolve the requested conversion from the installed client's recipe table.
                BuyAssetTable? asset = TableReaderV2.Parse<BuyAssetTable>().Find(row => row.Id == request.ItemId);
                if (asset is null || request.Times <= 0 || asset.Config.Count == 0
                    || asset.TimeId > 0
                    || (asset.BuyLimit > 0 && request.Times > asset.BuyLimit))
                {
                    session.SendResponse(new ItemBuyAssetResponse { Code = 20012001 }, packet.Id);
                    return;
                }
                Item? owned = session.inventory.Items.FirstOrDefault(i => i.Id == request.ItemId);
                int bought = owned is not null && PayModule.PurchaseDay(owned.LastBuyTime) == PayModule.PurchaseDay() ? owned.BuyTimes : 0;
                if ((asset.DailyLimit > 0 && (long)bought + request.Times > asset.DailyLimit)
                    || (asset.TotalLimit > 0 && (long)(owned?.TotalBuyTimes ?? 0) + request.Times > asset.TotalLimit)
                    || (long)(owned?.TotalBuyTimes ?? 0) + request.Times > int.MaxValue)
                {
                    session.SendResponse(new ItemBuyAssetResponse { Code = 20012001 }, packet.Id);
                    return;
                }
                var recipes = TableReaderV2.Parse<BuyAssetConfigTable>().Where(row => asset.Config.Contains(row.Id)).OrderBy(row => row.Times).ToArray();
                if (recipes.Length != asset.Config.Count || recipes.Any(row => row.ConsumeId.Count == 0))
                {
                    session.SendResponse(new ItemBuyAssetResponse { Code = 20012001 }, packet.Id);
                    return;
                }
                int currency = request.ConsumeId == 0 ? recipes[0].ConsumeId[0] : request.ConsumeId;
                long cost = 0, count = 0;
                // Unlimited exchanges have a fixed price; capped daily resources use a price ladder.
                int iterations = recipes.Length == 1 ? 1 : request.Times;
                if (iterations > 1000) { session.SendResponse(new ItemBuyAssetResponse { Code = 20012001 }, packet.Id); return; }
                for (int i = 0; i < iterations; i++)
                {
                    var recipe = recipes.LastOrDefault(row => row.Times <= (long)bought + i + 1) ?? recipes[0];
                    int index = recipe.ConsumeId.IndexOf(currency);
                    if (index < 0 || index >= recipe.ConsumeCount.Count || recipe.GainCount <= 0
                        || !Inventory.IsValidClientItemId(request.ItemId))
                    {
                        session.SendResponse(new ItemBuyAssetResponse { Code = 20012001 }, packet.Id);
                        return;
                    }
                    int multiplier = recipes.Length == 1 ? request.Times : 1;
                    cost += (long)recipe.ConsumeCount[index] * multiplier;
                    count += (long)recipe.GainCount * multiplier;
                }
                if (request.ItemId == Inventory.Coin)
                {
                    var configs = TableReaderV2.Parse<AscNet.Table.V2.share.config.ConfigTable>();
                    long factor = long.Parse(configs.Single(c => c.Key == "BuyAssetCoinBase").Value)
                        + session.player.PlayerData.Level * long.Parse(configs.Single(c => c.Key == "BuyAssetCoinMul").Value);
                    count = checked(count * factor);
                }
                ItemTable? item = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == request.ItemId);
                if (cost <= 0 || cost > int.MaxValue || count > int.MaxValue
                    || session.inventory.SpendableCount(currency) < cost
                    || session.inventory.Items.Where(i => i.Id == request.ItemId).Sum(i => i.Count) + count > Inventory.GetMaxCount(item))
                {
                    session.SendResponse(new ItemBuyAssetResponse { Code = 20012004 }, packet.Id);
                    return;
                }
                var before = session.inventory.Items.Select(i => new Item { Id = i.Id, Count = i.Count,
                    CreateTime = i.CreateTime, RefreshTime = i.RefreshTime, BuyTimes = i.BuyTimes,
                    TotalBuyTimes = i.TotalBuyTimes, LastBuyTime = i.LastBuyTime }).ToList();
                List<Item> changed;
                try
                {
                    changed = session.inventory.Spend(currency, (int)cost);
                    Item updated = session.inventory.Do(request.ItemId, (int)count);
                    updated.BuyTimes = checked(bought + request.Times);
                    updated.TotalBuyTimes = checked(updated.TotalBuyTimes + request.Times);
                    updated.LastBuyTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    changed.Add(updated);
                    session.inventory.SaveChecked();
                }
                catch (Exception error)
                {
                    session.inventory.Items = before;
                    session.log.Error("Asset conversion failed", error);
                    session.SendResponse(new ItemBuyAssetResponse { Code = 2 }, packet.Id);
                    return;
                }
                session.SendPush(new NotifyItemDataList { ItemDataList = changed });
                session.SendResponse(new ItemBuyAssetResponse { Count = (int)count }, packet.Id);
            }
        }

        internal static void ReconcileDailyAssetPurchaseCounts(Inventory inventory)
        {
            long today = PayModule.PurchaseDay();
            HashSet<int> dailyAssets = TableReaderV2.Parse<BuyAssetTable>()
                .Where(row => row.DailyLimit > 0)
                .Select(row => row.Id)
                .ToHashSet();
            List<(Item Item, int BuyTimes)> stale = inventory.Items
                .Where(item => item.BuyTimes != 0
                    && dailyAssets.Contains(item.Id)
                    && PayModule.PurchaseDay(item.LastBuyTime) != today)
                .Select(item => (item, item.BuyTimes))
                .ToList();
            if (stale.Count == 0)
                return;

            foreach ((Item item, _) in stale)
                item.BuyTimes = 0;
            try { inventory.SaveChecked(); }
            catch
            {
                foreach ((Item item, int buyTimes) in stale)
                    item.BuyTimes = buyTimes;
                throw;
            }
        }
     }
}
