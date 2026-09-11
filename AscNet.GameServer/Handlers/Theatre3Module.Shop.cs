using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre3;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    internal static void GenerateShopItems(Mutation mutation, Theatre3NodeSlot slot)
    {
        // Offers, including exhausted slots and discounts, survive purchases and reconnects.
        if (slot.ShopItems.Count != 0) return;
        var shop = TableReaderV2.Parse<Theatre3NodeShopTable>().SingleOrDefault(row => row.Id == slot.ShopId);
        Require(shop != null, 20203017);
        Require(shop!.ShopType is 1 or 2, 20203022);
        var goods = TableReaderV2.Parse<Theatre3NodeShopItemTable>();
        for (int index = 0; index < shop.GroupId.Count; index++)
        {
            int groupId = shop.GroupId[index];
            var candidates = goods.Where(row => row.GroupId == groupId && row.Weight > 0
                && (row.ItemType != 1 || CanAddItem(mutation, row.ItemId ?? 0,
                    1 + slot.ShopItems.Count(offer => offer.ItemType == 1 && offer.ItemId == row.ItemId)))).ToList();
            var offer = new Theatre3ShopItem { Uid = AllocateUid(mutation) };
            if (candidates.Count == 0) offer.IsLock = 2;
            else
            {
                var source = Weighted(candidates, row => row.Weight);
                Require(source.ItemType is 1 or 2 or 3 && source.Price >= 0, 20203023);
                offer.ItemType = source.ItemType;
                offer.ItemId = source.ItemId ?? 0;
                offer.ItemBoxId = source.ItemBoxId ?? 0;
                offer.EquipBoxId = source.EquipBoxId ?? 0;
                offer.Price = shop.ShopType == 2 ? checked(-source.Price) : source.Price;
                offer.DiscountPrice = offer.Price;
                int conditionId = index < shop.ConditionId.Count ? shop.ConditionId[index] : 0;
                offer.IsLock = IsConditionSatisfied(mutation, conditionId) ? 0 : 1;
            }
            slot.ShopItems.Add(offer);
        }

        // GiveMoney pays the displayed original price; discounts apply only to normal costs.
        if (shop.ShopType == 2) return;
        var effects = GetEffects(mutation);
        int discountCount = Math.Max(0, checked(shop.DiscountNum + effects.ShopDiscountSlots));
        decimal discountRate = Math.Clamp(shop.DiscountRate / 100m * effects.ShopPriceMultiplier, 0m, 1m);
        var discountCandidates = slot.ShopItems.Where(offer => offer.IsLock != 2).ToList();
        for (int index = 0; index < discountCount && discountCandidates.Count > 0; index++)
        {
            var offer = Weighted(discountCandidates, _ => 1);
            discountCandidates.Remove(offer);
            // Local rule: round payable costs upward so a positive price cannot display as no discount.
            offer.DiscountPrice = checked((int)decimal.Ceiling(offer.Price * discountRate));
            offer.DiscountPercent = (double)(discountRate * 100m);
        }
    }

    [RequestPacketHandler("Theatre3NodeShopBuyItemRequest")]
    public static void Theatre3NodeShopBuyItemRequestHandler(Session session, Packet.Request packet) =>
        Handle<Theatre3NodeShopBuyItemRequest, Theatre3NodeShopBuyItemResponse>(session, packet, NodeShopBuyItem);

    internal static void NodeShopBuyItem(Mutation mutation, Theatre3NodeShopBuyItemRequest request,
        Theatre3NodeShopBuyItemResponse response)
    {
        var parent = CurrentStep(mutation, 2);
        var slot = CurrentSlot(mutation);
        Require(slot.SlotType == 3 && slot.Selected != 0, 20203022);
        var offer = slot.ShopItems.SingleOrDefault(item => item.Uid == request.ShopItemUid);
        Require(offer != null && offer.IsLock == 0, 20203023);
        Require(offer!.IsBuy == 0, 20203024);
        Require(offer.ItemType is 1 or 2 or 3, 20203023);
        var shop = TableReaderV2.Parse<Theatre3NodeShopTable>().SingleOrDefault(row => row.Id == slot.ShopId);
        Require(shop != null && shop.ShopType is 1 or 2, 20203017);
        if (shop!.ShopType == 2)
        {
            Require(offer.Price <= 0, 20203023);
            AddCoin(mutation, checked(-offer.Price));
        }
        else
        {
            Require(offer.DiscountPrice >= 0 && offer.DiscountPrice <= offer.Price, 20203023);
            // The price is the persisted authoritative offer, never a client amount or a new roll.
            mutation.Cost(96189, offer.DiscountPrice);
        }
        offer.IsBuy = 1;
        switch (offer.ItemType)
        {
            case 1:
                AddItem(mutation, offer.ItemId);
                response.InnerItemId = offer.ItemId;
                break;
            case 2:
                OpenEquipBox(mutation, offer.EquipBoxId, rootUid: parent.Uid);
                break;
            case 3:
                OpenItemBox(mutation, offer.ItemBoxId, rootUid: parent.Uid);
                break;
        }
        ApplyEffectTrigger(mutation, Theatre3EffectTrigger.ShopPurchase, offer.Uid);
        response.LotteryRewards = new(mutation.State.LastLotteryRewards);
    }
}
