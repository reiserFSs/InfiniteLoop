using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatre5pve;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static Theatre5ItemTable ItemConfig(int itemId)
    {
        var row = Rows<Theatre5ItemTable>().FirstOrDefault(row => row.Id == itemId);
        Require(row != null, 20281003);
        return row;
    }

    internal static void InitializeBag(Mutation mutation, Theatre5AdventureData adventure)
    {
        adventure.BagData = new()
        {
            BagGridsNum = ConfigInt("BagItemGridMaxNum"),
            SkillGridsNum = ConfigInt("SkillGridMaxNum"),
            // Chapter entry attaches its authored chapter before assigning the PvE count.
            RuneGridsNum = adventure is Theatre5PvpAdventureData ? ConfigInt("RuneGridMinNum") : 0
        };
        if (adventure is Theatre5PveAdventureData { PveChapterData: not null } pve)
            adventure.BagData.RuneGridsNum = Rows<Theatre5PveChapterTable>()
                .Single(row => row.Id == pve.PveChapterData.ChapterId).BagRuneGridInitCount;
    }

    internal static Theatre5Item NewItem(Theatre5AdventureData adventure, int itemId) => new()
    {
        InstanceId = adventure.IdSequence = checked(adventure.IdSequence + 1),
        ItemId = itemId,
        ItemType = ItemConfig(itemId).Type
    };

    private static IEnumerable<Dictionary<int, Theatre5Item>> ItemContainers(Theatre5AdventureData adventure)
    {
        yield return adventure.BagData.BagItemDict;
        yield return adventure.BagData.TempItemDict;
        yield return adventure.BagData.SkillDict;
        yield return adventure.BagData.RuneDict;
        yield return adventure.BagData.RelicDict;
    }

    internal static IEnumerable<Theatre5Item> OwnedItems(Theatre5AdventureData adventure) =>
        ItemContainers(adventure).SelectMany(container => container.Values);

    internal static void RemoveItem(Theatre5AdventureData adventure, int instanceId)
    {
        foreach (var container in ItemContainers(adventure))
        {
            int slot = container.FirstOrDefault(pair => pair.Value.InstanceId == instanceId).Key;
            if (slot <= 0) continue;
            container.Remove(slot);
            adventure.RelicOrders.Remove(instanceId);
            return;
        }
        Require(false, 20281003);
    }

    internal static bool HasItems(Mutation mutation, int itemId, int count) => count >= 0 &&
        (itemId == ConfigInt("ChapterCoin")
            ? Adventure(mutation).GoldNum >= count
            : OwnedItems(Adventure(mutation)).Count(item => item.ItemId == itemId) >= count);

    internal static void SpendItems(Mutation mutation, int itemId, int count)
    {
        Require(count >= 0 && HasItems(mutation, itemId, count), 20282037);
        var adventure = Adventure(mutation);
        if (itemId == ConfigInt("ChapterCoin"))
        {
            adventure.GoldNum -= count;
            return;
        }
        // Materialize only the consumed instance IDs: removal mutates these sparse dictionaries.
        foreach (int id in OwnedItems(adventure).Where(item => item.ItemId == itemId)
                     .OrderBy(item => item.InstanceId).Take(count).Select(item => item.InstanceId).ToArray())
            RemoveItem(adventure, id);
    }

    internal static List<Theatre5Item> AddItems(Mutation mutation, int itemId, int count = 1)
    {
        Require(count >= 0, 20282039);
        var adventure = Adventure(mutation);
        List<Theatre5Item> added = [];
        if (itemId == ConfigInt("ChapterCoin"))
        {
            adventure.GoldNum = checked(adventure.GoldNum + count);
            return added;
        }
        for (int i = 0; i < count; i++)
        {
            var item = NewItem(adventure, itemId);
            AddExistingItem(mutation, item);
            added.Add(item);
        }
        return added;
    }

    internal static void AddExistingItem(Mutation mutation, Theatre5Item item)
    {
        var adventure = Adventure(mutation);
        Require(item.InstanceId > 0 && !OwnedItems(adventure).Any(owned => owned.InstanceId == item.InstanceId), 20281003);
        Require(item.ItemType == ItemConfig(item.ItemId).Type, 20281008);
        adventure.IdSequence = Math.Max(adventure.IdSequence, item.InstanceId);
        switch (item.ItemType)
        {
            case 4:
                adventure.GoldNum = checked(adventure.GoldNum + 1);
                return;
            case 8:
                AddExperience(mutation, 1);
                return;
            case 5:
                Require(mutation.Data.PvpType == 2 && Rows<Theatre5PveDeduceClueTable>().Any(row => row.Id == item.ItemId), 20282039);
                mutation.Data.PveClues[item.ItemId] = new() { ClueId = item.ItemId, IsComplete = true };
                return;
            case 7:
                adventure.BagData.RelicDict.Add(item.InstanceId, item);
                adventure.RelicOrders.Add(item.InstanceId);
                if (!mutation.Data.RelicCollects.Contains(item.ItemId)) mutation.Data.RelicCollects.Add(item.ItemId);
                return;
            case 1:
            case 2:
            case 3:
            case 6:
                PlaceItem(mutation, item, false, -1);
                return;
            default:
                throw new InvalidDataException($"Unsupported authored Theatre5 item type {item.ItemType}.");
        }
    }

    private static int EmptySlot(Dictionary<int, Theatre5Item> container, int capacity)
    {
        for (int index = 1; index <= capacity; index++)
            if (!container.ContainsKey(index)) return index;
        return 0;
    }

    private static (Dictionary<int, Theatre5Item> Items, int Capacity) ItemDestination(
        Theatre5AdventureData adventure, bool equipped, int itemType)
    {
        if (!equipped) return (adventure.BagData.BagItemDict, adventure.BagData.BagGridsNum);
        Require(itemType is 1 or 2, 20281008);
        return itemType == 1
            ? (adventure.BagData.SkillDict, adventure.BagData.SkillGridsNum)
            : (adventure.BagData.RuneDict, adventure.BagData.RuneGridsNum);
    }

    internal static void PlaceItem(Mutation mutation, Theatre5Item item, bool equipped, int targetIndex)
    {
        var adventure = Adventure(mutation);
        Require(item.ItemType is 1 or 2 or 3 or 6, 20281008);
        var (destination, capacity) = ItemDestination(adventure, equipped, item.ItemType);
        // EN ItemDetailBtns uses -1 for click Buy/Choose; dragging supplies a 1-based slot.
        Require(targetIndex == -1 || (targetIndex > 0 && targetIndex <= capacity), 20281007);
        int slot = targetIndex == -1 ? EmptySlot(destination, capacity) : targetIndex;
        if (slot == 0)
        {
            // Reward overflow is real temporary inventory, never an invisible extra bag slot.
            destination = adventure.BagData.TempItemDict;
            slot = EmptySlot(destination, ConfigInt("TempItemBagGridMaxNum"));
            Require(slot > 0, 20281006);
        }
        Require(!destination.ContainsKey(slot), 20281006);
        destination.Add(slot, item);
    }

    internal static void PushBag(Mutation mutation)
    {
        var adventure = Adventure(mutation);
        mutation.Push(new NotifyTheatre5BagDataUpdate
        {
            Status = adventure.Status, GoldNum = adventure.GoldNum, BagData = adventure.BagData
        });
    }

    [RequestPacketHandler("XTheatre5BagItemMoveRequest")]
    public static void BagItemMove(Session session, Packet.Request packet) =>
        Handle<XTheatre5BagItemMoveRequest, XTheatre5BagItemMoveResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session);
            var adventure = Adventure(mutation);
            Require(adventure.Status is 3 or 4, 20281001);
            Require(!(request.SrcEquipped && request.SrcIsTempItem), 20281008);
            var (source, sourceCapacity) = request.SrcIsTempItem
                ? (adventure.BagData.TempItemDict, ConfigInt("TempItemBagGridMaxNum"))
                : ItemDestination(adventure, request.SrcEquipped, request.ItemType);
            Require(request.SrcIndex > 0 && request.SrcIndex <= sourceCapacity, 20281007);
            Require(source.TryGetValue(request.SrcIndex, out var item) && item.InstanceId == request.InstanceId && item.ItemType == request.ItemType, 20281003);
            var (target, capacity) = ItemDestination(adventure, request.TargetEquipped, item.ItemType);
            // ShopControl equip/unequip/drag and TempBag auto-moves resolve a positive slot.
            // Unlike Buy/Choose, BagItemMove has no automatic-placement wire sentinel.
            Require(request.TargetIndex > 0 && request.TargetIndex <= capacity, 20281007);
            int targetIndex = request.TargetIndex;
            if (ReferenceEquals(source, target) && request.SrcIndex == targetIndex)
            {
                response.BagData = adventure.BagData;
                return;
            }
            target.TryGetValue(targetIndex, out var displaced);
            Require(displaced == null || !request.SrcEquipped || displaced.ItemType == item.ItemType, 20281008);
            source.Remove(request.SrcIndex);
            if (displaced != null) source[request.SrcIndex] = displaced;
            target[targetIndex] = item;
            // Items are distinct instances. Source has no stack/merge recipe; same BuffType
            // runes coexist and native/client activation picks highest quality, then first slot.
            response.BagData = adventure.BagData;
        });

    [RequestPacketHandler("XTheatre5ShopUnlockGridRequest")]
    public static void ShopUnlockGrid(Session session, Packet.Request packet) =>
        Handle<XTheatre5ShopUnlockGridRequest, XTheatre5ShopUnlockGridResponse>(session, packet, (mutation, request, response) =>
        {
            EnsureAvailable(session);
            var adventure = Adventure(mutation);
            Require(adventure.Status is 3 or 4, 20281001);
            Require(request.GridType == 2, 20281008);
            Require(adventure.BagData.RuneGridsNum < ConfigInt("RuneGridMaxNum"), 20281010);
            int initial = ConfigInt("RuneGridMinNum"), discount = ConfigInt("PvpGridUnlockDiscount");
            if (adventure is Theatre5PveAdventureData pve)
            {
                Require(pve.PveChapterData != null, 20282019);
                var chapter = Rows<Theatre5PveChapterTable>().Single(row => row.Id == pve.PveChapterData.ChapterId);
                initial = chapter.BagRuneGridInitCount;
                discount = chapter.GridUnlockDiscount;
            }
            int unlock = adventure.BagData.RuneGridsNum - initial + 1;
            var cost = Rows<Theatre5GridUnlockCostTable>().FirstOrDefault(row => row.UnlockNum == unlock);
            Require(cost != null, 20281007);
            int price = adventure.IsCanFreeUnlockGrid ? 0 : Math.Max(checked(cost.GoldCost - discount * adventure.BagData.RoundNumWithoutGridUnlock), 1);
            Require(adventure.GoldNum >= price, 20281005);
            adventure.GoldNum -= price;
            adventure.BagData.RuneGridsNum++;
            adventure.BagData.RoundNumWithoutGridUnlock = 0;
            adventure.IsCanFreeUnlockGrid = false;
            UpdateMissionProgress(mutation, "SpendGold", price);
            UpdateMissionProgress(mutation, "UnlockGrid", 2);
            response.BagData = adventure.BagData;
            response.GoldNum = adventure.GoldNum;
            response.IsCanFreeUnlockGrid = adventure.IsCanFreeUnlockGrid;
        });
}
