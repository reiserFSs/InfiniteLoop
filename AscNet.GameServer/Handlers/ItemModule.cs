using AscNet.Common.Database;
using AscNet.GameServer.Handlers.Drops;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

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
            if (request.Id <= 0 || request.Count <= 0 || request.RecycleTime < 0)
            {
                session.SendResponse(new ItemUseResponse { Code = 1 }, packet.Id);
                return;
            }

            ItemUsePendingOperation? pending = session.player.PendingItemUse;
            if (pending is not null)
            {
                if (pending.ItemId != request.Id || pending.Count != request.Count
                    || pending.RecycleTime != request.RecycleTime
                    || !SameSelection(pending.SelectRewardIds, request.SelectRewardIds))
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
                    || !TryBuildItemUseRewards(item, request.Count, request.SelectRewardIds, out List<RewardGoodsTable> goods)
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
                    SelectRewardIds = request.SelectRewardIds is { Count: > 0 } selected ? selected.ToList() : [],
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

        private static bool SameSelection(IReadOnlyList<int>? pending, IReadOnlyList<int>? requested)
        {
            int pendingCount = pending?.Count ?? 0;
            int requestedCount = requested?.Count ?? 0;
            if (pendingCount != requestedCount)
                return false;
            for (int index = 0; index < pendingCount; index++)
                if (pending![index] != requested![index])
                    return false;
            return true;
        }

        private static bool TryBuildItemUseRewards(ItemTable item, int count, IReadOnlyList<int>? selectedIds,
            out List<RewardGoodsTable> goods)
        {
            goods = [];
            if (item.ItemType != (int)AscNet.Common.ItemType.Gift || item.SubTypeParams.Count < 2)
                return false;

            int sourceId = item.SubTypeParams[1];
            // A selection is only valid for a choice pack. Other gifts keep rejecting it.
            if (item.SubTypeParams[0] != 3 && selectedIds is { Count: > 0 })
                return false;
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
                case 3:
                    // Authored choice packs select exactly one reward-goods id per box.
                    if (item.SubTypeParams.Count < 3 || item.SubTypeParams[2] != 1)
                        return false;
                    int selectedCount = selectedIds?.Count ?? 0;
                    bool oneChoice = selectedCount == 1;
                    if (selectedCount == 0 || (!oneChoice && selectedCount != count))
                        return false;
                    RewardTable? choice = TableReaderV2.Parse<RewardTable>().Find(row => row.Id == sourceId);
                    List<RewardGoodsTable> options = RewardHandler.GetRewardGoods(sourceId);
                    if (choice is null || options.Count == 0 || choice.SubIds.Count != options.Count)
                        return false;
                    Dictionary<int, RewardGoodsTable> optionsById = new();
                    foreach (RewardGoodsTable option in options)
                        if (!optionsById.TryAdd(option.Id, option))
                            return false;
                    IEnumerable<int> picks = oneChoice
                        ? Enumerable.Repeat(selectedIds![0], count)
                        : selectedIds!;
                    foreach (int selectedId in picks)
                    {
                        if (!optionsById.TryGetValue(selectedId, out RewardGoodsTable? row)
                            || !choice.SubIds.Contains(selectedId)
                            || row.Count <= 0)
                            return false;
                        RewardType? rewardType = RewardHandler.GetRewardType(row);
                        if (rewardType is null)
                            return false;
                        // Item stacks combine. A frame stays one grant per pack so a duplicate
                        // converts into that frame's shards instead of being dropped.
                        if (rewardType == RewardType.Item)
                        {
                            RewardGoodsTable? stacked = goods.Find(good => good.Id == row.Id && good.TemplateId == row.TemplateId);
                            long total = (long)(stacked?.Count ?? 0) + row.Count;
                            if (total > int.MaxValue)
                                return false;
                            if (stacked is null)
                            {
                                goods.Add(new RewardGoodsTable
                                {
                                    Id = row.Id, TemplateId = row.TemplateId, Count = (int)total,
                                    Params = row.Params.ToList()
                                });
                            }
                            else
                                stacked.Count = (int)total;
                        }
                        else
                        {
                            goods.Add(new RewardGoodsTable
                            {
                                Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                                Params = row.Params.ToList()
                            });
                        }
                    }
                    return goods.Count > 0;
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
            BuyAssetPendingOperation? pending = session.player.PendingBuyAsset;
            if (pending is not null)
            {
                // Same contract as ItemUse: an unfinished identical purchase is the client's retry; finish it once.
                List<Item>? resumed = CompletePendingBuyAsset(session);
                if (pending.ItemId == request.ItemId && pending.Times == request.Times)
                {
                    SendBuyAssetResult(session, packet.Id, resumed, pending);
                    return;
                }
                if (resumed is not null)
                    session.SendPush(new NotifyItemDataList { ItemDataList = resumed });
            }

            DateTimeOffset now = Game.DrawManager.UtcNow();
            int code = TryPlanBuyAsset(session, request, now, out List<(int ItemId, int Count)> costPlan,
                out int gain, out int buyTimes);
            if (code != 0)
            {
                session.SendResponse(new ItemBuyAssetResponse { Code = code }, packet.Id);
                return;
            }

            pending = new BuyAssetPendingOperation
            {
                ItemId = request.ItemId,
                Times = request.Times,
                Gain = gain,
                BuyTimes = buyTimes + request.Times,
                TotalBuyTimes = (session.inventory.Items.FirstOrDefault(item => item.Id == request.ItemId)?.TotalBuyTimes ?? 0)
                    + request.Times,
                OccurredAt = now.ToUnixTimeSeconds(),
                Debits = costPlan.Select(cost => new BuyAssetPendingDebit { ItemId = cost.ItemId, Count = cost.Count }).ToList()
            };
            session.player.PendingBuyAsset = pending;
            try { session.player.SaveChecked(); }
            catch
            {
                session.player = Player.collection.Find(row => row.PlayerData.Id == session.player.PlayerData.Id).Single();
                if (session.player.PendingBuyAsset?.ItemId != pending.ItemId
                    || session.player.PendingBuyAsset.TotalBuyTimes != pending.TotalBuyTimes)
                    throw;
            }
            SendBuyAssetResult(session, packet.Id, CompletePendingBuyAsset(session), pending);
        }

        private static void SendBuyAssetResult(Session session, int packetId, List<Item>? changed,
            BuyAssetPendingOperation pending)
        {
            if (changed is null)
            {
                session.SendResponse(new ItemBuyAssetResponse { Code = 20012004 }, packetId);
                return;
            }
            session.SendPush(new NotifyItemDataList { ItemDataList = changed });
            TaskModule.SendTaskSync(session);
            session.SendResponse(new ItemBuyAssetResponse { Count = pending.Gain, IsCrit = false }, packetId);
        }

        public static void ResumePendingBuyAsset(Session session)
        {
            if (session.player.PendingBuyAsset is not null)
                CompletePendingBuyAsset(session);
        }

        // Journal steps: (1) inventory debit+credit in one document, idempotent via the TotalBuyTimes marker;
        // (2) 11202 spend progress and journal clear in one Player write. Null = journal no longer applies (dropped).
        private static List<Item>? CompletePendingBuyAsset(Session session)
        {
            BuyAssetPendingOperation pending = session.player.PendingBuyAsset
                ?? throw new InvalidOperationException("No pending BuyAsset.");
            HashSet<int> ids = pending.Debits.Select(debit => debit.ItemId).Append(pending.ItemId).ToHashSet();
            Inventory inventory = session.inventory;
            int Stored(Inventory source) =>
                source.Items.FirstOrDefault(item => item.Id == pending.ItemId)?.TotalBuyTimes ?? 0;
            if (Stored(inventory) != pending.TotalBuyTimes)
            {
                Item? stack = inventory.Items.FirstOrDefault(item => item.Id == pending.ItemId);
                ItemTable? target = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == pending.ItemId);
                bool applies = target is not null
                    && Stored(inventory) == pending.TotalBuyTimes - pending.Times
                    && ids.All(id => inventory.Items.Count(item => item.Id == id) <= 1)
                    && pending.Debits.All(debit =>
                        (inventory.Items.FirstOrDefault(item => item.Id == debit.ItemId)?.Count ?? 0) >= debit.Count)
                    && (stack?.Count ?? 0) + pending.Gain <= Inventory.GetMaxCount(target);
                if (!applies)
                {
                    ClearPendingBuyAsset(session);
                    return null;
                }
                foreach (BuyAssetPendingDebit debit in pending.Debits)
                    inventory.Do(debit.ItemId, -debit.Count);
                Item bought = inventory.Do(pending.ItemId, pending.Gain);
                bought.BuyTimes = pending.BuyTimes;
                bought.TotalBuyTimes = pending.TotalBuyTimes;
                bought.LastBuyTime = pending.OccurredAt;
                try
                {
                    inventory.SaveChecked();
                }
                catch
                {
                    // Pre-write failure or ack loss after commit: adopt the stored document (as GuildModule does)
                    // so a later Session.Save can neither roll back nor replay it; the marker tells which happened.
                    session.inventory = Inventory.collection.Find(row => row.Uid == inventory.Uid).Single();
                    if (Stored(session.inventory) != pending.TotalBuyTimes)
                    {
                        ClearPendingBuyAsset(session); // Nothing was bought: the request fails as an error.
                        throw;
                    }
                }
            }

            TaskModule.EnsureMissionResets(session);
            session.player.PendingBuyAsset = null;
            TaskModule.ApplyTableDrivenProgressUnsaved(session,
                pending.Debits.Select(debit => (11202, (int?)debit.ItemId, debit.Count)), pending.OccurredAt);
            try { session.player.SaveChecked(); }
            catch
            {
                session.player = Player.collection.Find(row => row.PlayerData.Id == session.player.PlayerData.Id).Single();
                if (session.player.PendingBuyAsset is not null)
                    throw;
            }
            return session.inventory.Items.Where(item => ids.Contains(item.Id)).ToList();
        }

        private static void ClearPendingBuyAsset(Session session)
        {
            session.player.PendingBuyAsset = null;
            try { session.player.SaveChecked(); }
            catch
            {
                session.player = Player.collection.Find(row => row.PlayerData.Id == session.player.PlayerData.Id).Single();
                if (session.player.PendingBuyAsset is not null)
                    throw;
            }
        }

        // Source: share/item/BuyAsset.tab + BuyAssetConfig.tab, priced like XItemManager.GetBuyAssetInfo/XUiBuyAsset
        // (template by today's BuyTimes + 1, total = ConsumeCount[selected] * Times; ConsumeId 0 = first option).
        internal static int TryPlanBuyAsset(Session session, ItemBuyAssetRequest request, DateTimeOffset now,
            out List<(int ItemId, int Count)> costPlan, out int gain, out int buyTimes)
        {
            const int invalid = 20012001, notEnough = 20012004, capacity = 20012005, noTable = 20012007,
                maxTimes = 20012008, badConsume = 20012029, notInTime = 20012035, maxTotal = 20012036;
            costPlan = [];
            gain = 0;
            buyTimes = 0;
            BuyAssetTable? asset = TableReaderV2.Parse<BuyAssetTable>().Find(row => row.Id == request.ItemId);
            if (asset is null)
                return noTable;
            ItemTable? target = TableReaderV2.Parse<ItemTable>().Find(row => row.Id == request.ItemId);
            if (target is null || !Inventory.IsValidClientItemId(request.ItemId))
                return 20012003;
            if (request.Times <= 0)
                return invalid;
            if (asset.BuyLimit > 0 && request.Times > asset.BuyLimit)
                return maxTimes;
            if (asset.TimeId > 0 && !Game.ActivityScheduleService.IsOpen(asset.TimeId, now))
                return notInTime;

            List<Item> targetStacks = session.inventory.Items.Where(item => item.Id == request.ItemId).ToList();
            if (targetStacks.Count > 1)
                return invalid;
            Item? stack = targetStacks.FirstOrDefault();
            long today = TaskModule.CurrentDailyResetPeriod(now.ToUnixTimeSeconds());
            buyTimes = stack is not null && TaskModule.CurrentDailyResetPeriod(stack.LastBuyTime) == today
                ? stack.BuyTimes : 0;
            if (asset.DailyLimit > 0 && (long)buyTimes + request.Times > asset.DailyLimit)
                return maxTimes;
            if (asset.TotalLimit > 0 && (long)(stack?.TotalBuyTimes ?? 0) + request.Times > asset.TotalLimit)
                return maxTotal;

            Dictionary<int, BuyAssetConfigTable> configRows = TableReaderV2.Parse<BuyAssetConfigTable>()
                .ToDictionary(row => row.Id);
            if (asset.Config.Count == 0 || asset.Config.Any(id => !configRows.ContainsKey(id)))
                return noTable;
            List<BuyAssetConfigTable> templates = asset.Config.Select(id => configRows[id]).OrderBy(row => row.Times).ToList();
            int purchaseOrdinal = buyTimes + 1;
            BuyAssetConfigTable template = templates.LastOrDefault(row => row.Times <= purchaseOrdinal) ?? templates[0];

            int option = request.ConsumeId == 0 ? 0 : template.ConsumeId.IndexOf(request.ConsumeId);
            if (option < 0 || option >= template.ConsumeId.Count || option >= template.ConsumeCount.Count)
                return badConsume;
            int consumeId = template.ConsumeId[option];
            if (Inventory.CombinedItemIds(consumeId).Contains(request.ItemId) || template.ConsumeCount[option] <= 0)
                return badConsume;

            long perTime = template.GainCount;
            if (request.ItemId == Inventory.Coin)
                perTime *= GuildBossModule.ConfigInt("BuyAssetCoinBase")
                    + session.player.PlayerData.Level * GuildBossModule.ConfigInt("BuyAssetCoinMul");
            long totalGain = perTime * request.Times;
            if (perTime <= 0 || totalGain > int.MaxValue)
                return invalid;

            long cost = (long)template.ConsumeCount[option] * request.Times;
            List<(int ItemId, int Count)>? plan;
            if (consumeId is 2 or 3)
            {
                // Client XItemManager.GetCount shows item 2 + 3 as one black-card balance, so it will offer a purchase
                // the balance covers. AscNet policy: spend item 2 (client FreeGem) first, then item 3 (client PaidGem).
                long free = session.inventory.Items.Where(item => item.Id == 2).Sum(item => item.Count);
                long paid = session.inventory.Items.Where(item => item.Id == 3).Sum(item => item.Count);
                if (cost > int.MaxValue || free < 0 || paid < 0 || free + paid < cost)
                    return notEnough;
                long fromFree = Math.Min(free, cost);
                plan = new[] { (2, (int)fromFree), (3, (int)(cost - fromFree)) }.Where(debit => debit.Item2 > 0).ToList();
            }
            else
                plan = session.inventory.PlanCombinedCost(consumeId, cost, now);
            if (plan is null)
                return notEnough;
            if (plan.Any(cost => session.inventory.Items.Count(item => item.Id == cost.ItemId) > 1))
                return invalid;
            if ((stack?.Count ?? 0) + totalGain > Inventory.GetMaxCount(target))
                return capacity;

            costPlan = plan;
            gain = (int)totalGain;
            return 0;
        }

        // The client displays stored BuyTimes verbatim; clear stale daily counts on login
        // using the same reset period as TryPlanBuyAsset.
        internal static void ReconcileDailyAssetPurchaseCounts(Inventory inventory)
        {
            long today = TaskModule.CurrentDailyResetPeriod(Game.DrawManager.UtcNow().ToUnixTimeSeconds());
            HashSet<int> dailyAssets = TableReaderV2.Parse<BuyAssetTable>()
                .Where(row => row.DailyLimit > 0)
                .Select(row => row.Id)
                .ToHashSet();
            List<(Item Item, int BuyTimes)> stale = inventory.Items
                .Where(item => item.BuyTimes != 0
                    && dailyAssets.Contains(item.Id)
                    && TaskModule.CurrentDailyResetPeriod(item.LastBuyTime) != today)
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
