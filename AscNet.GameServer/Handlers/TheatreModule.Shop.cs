using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    internal static void InitialiseShopSlot(Mutation mutation, TheatreSlot slot, int shopId)
    {
        var shop = Rows<TheatreNodeShopTable>().SingleOrDefault(row => row.Id == shopId);
        Require(shop != null && slot.SlotType == 2, 20155029);
        if (slot.ShopItems.Count != 0)
        {
            Require(slot.ConfigId == shopId, 20155029);
            return;
        }

        var modifiers = GetModifiers(mutation);
        Require(shop!.InitCount > 0 && shop.MaxCount >= shop.InitCount
            && modifiers.ShopItemCountBonus >= 0 && modifiers.ShopPriceMultiplier >= 0, 20155029);
        var candidates = new List<(int Type, int Weight, int Price, int Count)>
        {
            (1, shop.SkillWeight, shop.SkillPrice, 1),
            (2, shop.LvWeight, shop.LvPrice, 1),
            (3, shop.DecorationWeight, shop.DecorationPrice, shop.DecorationCount),
            (4, shop.FavorWeight, shop.FavorPrice, shop.FavorCount)
        };
        Require(candidates.All(item => item.Weight >= 0 && item.Price >= 0 && item.Count > 0), 20155029);
        candidates.RemoveAll(item => item.Weight == 0);
        int count = (int)Math.Min(shop.MaxCount, (long)shop.InitCount + modifiers.ShopItemCountBonus);
        Require(count <= candidates.Count, 20155029);

        // LOCAL policy: weighted sampling without replacement because buyType identifies one offer.
        // One skill/level grant per offer; currency counts and base prices come from the shop row.
        // Seed only persisted run/slot identity; frozen stock, not RNG replay, governs later requests.
        int seed = unchecked((int)mutation.State.RunId ^ (int)(mutation.State.RunId >> 32)
            ^ slot.SlotId * 397 ^ shopId * 31);
        var random = new Random(seed);
        slot.ConfigId = shopId;
        for (int index = 0; index < count; index++)
        {
            var item = PickNodeWeighted(candidates, candidate => candidate.Weight, random);
            candidates.Remove(item);
            // LOCAL economics: freeze floor(base price * authored decoration multiplier) on creation.
            slot.ShopItems.Add(new TheatreShopItem
            {
                ItemType = item.Type, Count = item.Count, Price = modifiers.ShopPrice(item.Price)
            });
        }
    }

    [RequestPacketHandler("TheatreNodeShopBuyItemRequest")]
    public static void TheatreNodeShopBuyItemRequestHandler(Session session, Packet.Request packet) =>
        Handle<TheatreNodeShopBuyItemRequest, TheatreNodeShopBuyItemResponse>(session, packet, NodeShopBuyItem);

    internal static void NodeShopBuyItem(Mutation mutation, TheatreNodeShopBuyItemRequest request,
        TheatreNodeShopBuyItemResponse response)
    {
        Require(!HasPendingSkillChoice(mutation), 20155023);
        var slot = CurrentSlot(mutation);
        Require(slot.SlotType == 2 && slot.Selected != 0, 20155025);
        Require(Rows<TheatreNodeShopTable>().Any(row => row.Id == slot.ConfigId), 20155026);
        Require(request.Type is >= 1 and <= 4, 20155026);
        Require(slot.ShopItems.Count(item => item.ItemType == request.Type) == 1, 20155026);
        var offer = slot.ShopItems.First(item => item.ItemType == request.Type);
        Require(offer.IsBuy == 0, 20155027);
        Require(offer.Price >= 0 && offer.Count > 0, 20155026);
        if (offer.ItemType == 1)
            Require(mutation.State.ShopSkillOpened && offer.Skills.Count > 0 && offer.PowerId > 0, 20155023);

        // XTheatreConfigs.TheatreCoin: buyType is a reward enum, never an inventory item ID.
        // Core persists stock, queued rewards and cost claims together before sending any success.
        mutation.Cost(96101, offer.Price);
        offer.IsBuy = 1;
        if (offer.ItemType == 1) ActivateShopSkillChoice(mutation, offer);
        else ApplyNodeReward(mutation, offer.ItemType, offer.Count);
        RecordProgress(mutation, 73013);
    }
}
