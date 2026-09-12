using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    internal static bool CanOwnItem(Mutation m, int itemId)
    {
        var config = ItemConfig(itemId);
        return config.CharacterId == null || !config.CharacterId.Any(id => id > 0)
            || config.CharacterId.Contains(Adventure(m).CharacterId);
    }

    internal static int SellPrice(Theatre5Item item)
    {
        int price = Convert.ToInt32(ItemConfig(item.ItemId).SellPrice);
        return item.IsStrengthen ? checked((int)((long)price * ConfigInt("StrengthenRate") / 10000)) : price;
    }

    internal static int DrawItemGroup(Mutation m, int groupId, HashSet<int>? excluded = null, bool allowExhausted = false)
    {
        var adventure = Adventure(m);
        var offered = adventure.ShopData?.Goods.Where(goods => !goods.IsSoldOut).Select(goods => goods.ItemInfo)
            ?? Enumerable.Empty<Theatre5Item>();
        var owned = OwnedItems(adventure).Concat(offered).GroupBy(item => item.ItemId).ToDictionary(g => g.Key, g => g.Count());
        List<(int Id, long Weight)> choices = [];
        var rows = Rows<Theatre5ItemRandomGroupTable>().Where(row => row.GroupId == groupId).ToList();
        Require(rows.Count > 0, 20281002);
        foreach (var row in rows)
        {
            int id = row.GoodsId;
            bool compatible = CanOwnItem(m, id); // Validate referenced content even when excluded.
            long weight = Convert.ToInt32(row.GoodsWeight);
            Require(weight >= 0 && Convert.ToInt32(row.ExpectedNum) >= 0, 20281002);
            // LOCAL composition: matching authored condition weights add to the base weight.
            for (int i = 0; i < (row.Conditions?.Count ?? 0); i++)
                if (row.Conditions![i] > 0)
                {
                    Require(row.ConditionWeights != null && i < row.ConditionWeights.Count, 20281002);
                    Require(row.ConditionWeights[i] >= 0, 20281002);
                    if (IsConditionMet(m.Session, row.Conditions[i], m.State))
                        weight = checked(weight + row.ConditionWeights[i]);
                }
            if (excluded?.Contains(id) == true || !compatible) continue;
            // LOCAL: ExpectedNum is the cap; enum effect2 establishes shop/temp/bag/equipped counting.
            if (Convert.ToInt32(row.ExpectedNum) > 0 && owned.GetValueOrDefault(id) >= Convert.ToInt32(row.ExpectedNum)) continue;
            if (weight > 0) choices.Add((id, weight));
        }
        if (choices.Count == 0 && allowExhausted) return 0;
        Require(choices.Count > 0, 20281002);
        long ticket = Random.Shared.NextInt64(choices.Sum(choice => choice.Weight));
        foreach (var choice in choices)
        {
            if (ticket < choice.Weight) return choice.Id;
            ticket -= choice.Weight;
        }
        throw new InvalidOperationException("Invalid Theatre5 group weight total.");
    }

    private static Theatre5ShopData ShopForAction(Mutation m, int error = 20281001)
    {
        EnsureAvailable(m.Session);
        var adventure = Adventure(m);
        Require(adventure.Status == 4, error);
        Require(adventure.ShopData != null, 20283018);
        return adventure.ShopData;
    }

    private static Theatre5ShopTable ShopConfig(int id)
    {
        var config = Rows<Theatre5ShopTable>().FirstOrDefault(row => row.Id == id);
        Require(config != null, 20281002);
        return config;
    }

    internal static void ReplaceShopGoods(Mutation m, IEnumerable<int> slots)
    {
        var adventure = Adventure(m);
        Require(adventure.ShopData != null, 20283018);
        var shop = adventure.ShopData;
        var config = ShopConfig(shop.ShopId);
        var replace = slots.Distinct().Order().ToList();
        Require(replace.All(slot => slot > 0 && slot <= shop.UnlockGridsNum && slot <= config.GroupIds.Count), 20281007);
        replace.RemoveAll(slot => slot <= shop.Goods.Count && shop.Goods[slot - 1].IsFreeze);
        if (replace.Count == 0) return;
        foreach (int slot in replace)
            if (slot <= shop.Goods.Count) shop.Goods[slot - 1].IsSoldOut = true;
        // LOCAL sampling without replacement within an offer, including retained frozen goods.
        var excluded = shop.Goods.Where((goods, index) => !replace.Contains(index + 1) && !goods.IsSoldOut)
            .Select(goods => goods.ItemInfo.ItemId).ToHashSet();
        foreach (int slot in replace)
        {
            int id = DrawItemGroup(m, config.GroupIds[slot - 1], excluded);
            excluded.Add(id);
            var goods = new Theatre5Goods { ItemInfo = NewItem(adventure, id) };
            Require(slot <= shop.Goods.Count + 1, 20281007);
            if (slot <= shop.Goods.Count) shop.Goods[slot - 1] = goods;
            else shop.Goods.Add(goods);
        }
        var weights = Rows<Theatre5ConfigTable>().FirstOrDefault(row => row.Key == "ShopItemDiscountWeight");
        Require(weights != null && weights.Values != null, 20281002);
        var discountWeights = weights.Values;
        Require(discountWeights.All(weight => weight >= 0) && discountWeights.Sum(weight => (long)weight) > 0, 20281002);
        long ticket = Random.Shared.NextInt64(discountWeights.Sum(weight => (long)weight));
        int discountCount = 0;
        for (; discountCount < discountWeights.Count; discountCount++)
        {
            if (ticket < discountWeights[discountCount]) break;
            ticket -= discountWeights[discountCount];
        }
        // Weight index zero denotes zero discounted offers. Retained offers keep their exact price flag.
        for (int i = 0; i < Math.Min(discountCount, replace.Count); i++)
        {
            int selected = Random.Shared.Next(i, replace.Count);
            (replace[i], replace[selected]) = (replace[selected], replace[i]);
            shop.Goods[replace[i] - 1].IsSpecialPrice = true;
        }
    }

    private static void PushShop(Mutation m)
    {
        var adventure = Adventure(m);
        Require(adventure.ShopData != null, 20283018);
        m.Push(new NotifyTheatre5ShopUpdate { GoldNum = adventure.GoldNum, BagData = adventure.BagData, ShopData = adventure.ShopData });
    }

    internal static void EnterShop(Mutation m)
    {
        EnsureAvailable(m.Session);
        var adventure = Adventure(m);
        if (adventure.Status is 3 or 4)
        {
            // Re-entry hydrates the existing offer; it never rerolls, pays income or triggers effects.
            PushShop(m);
            if (adventure.Status == 3)
                m.Push(new NotifyTheatre5SkillChoiceUpdate
                {
                    GoldNum = adventure.GoldNum, BagData = adventure.BagData, SkillChoiceData = adventure.SkillChoiceData
                });
            return;
        }
        Require(adventure.Status is 2 or 7, 20281001);
        var rounds = Rows<Theatre5PvpRoundRefreshTable>().OrderBy(row => row.RoundNum).ToList();
        Require(rounds.Count > 0 && adventure.RoundNum > 0, 20281002);
        Theatre5PvpRoundRefreshTable round;
        int shopId;
        if (adventure is Theatre5PveAdventureData pve)
        {
            Require(pve.PveChapterData?.CurPveChapterLevel != null, 20282020);
            int level = pve.PveChapterData.CurPveChapterLevel.Level;
            Require(level > 0, 20282020);
            // LOCAL shared-economy curve, NOT a recovered retail PvE mapping:
            // ordinal chapter level selects non-PvP shop tiers and the authored PvP income/skill curve.
            var pvpShops = rounds.Select(row => row.ShopId).ToHashSet();
            var tiers = Rows<Theatre5ShopTable>().Where(row => !pvpShops.Contains(row.Id)).OrderBy(row => row.Id).ToList();
            Require(tiers.Count > 0, 20281002);
            shopId = tiers[Math.Min(level, tiers.Count) - 1].Id;
            round = rounds[Math.Min(level, rounds.Count) - 1];
        }
        else
        {
            var authoredRound = rounds.FirstOrDefault(row => row.RoundNum == adventure.RoundNum);
            Require(authoredRound != null, 20281002);
            round = authoredRound;
            shopId = round.ShopId;
        }
        var config = ShopConfig(shopId);
        adventure.ShopData ??= new Theatre5ShopData();
        var shop = adventure.ShopData;
        shop.ShopId = shopId;
        shop.UnlockGridsNum = Math.Max(shop.UnlockGridsNum, config.UnlockNum);
        Require(shop.UnlockGridsNum > 0 && shop.UnlockGridsNum <= config.GroupIds.Count, 20281002);
        shop.RefreshCnt = 0;
        adventure.GoldNum = checked(adventure.GoldNum + round.AddGoldNum);
        adventure.EnterShopCnt = checked(adventure.EnterShopCnt + 1);
        adventure.MissionLevelUpForRound = 0;
        ReplaceShopGoods(m, Enumerable.Range(1, shop.UnlockGridsNum));
        adventure.SkillChoiceData = null;
        var skillGroups = round.SkillGroupIds?.Where(group => group > 0).ToList() ?? [];
        if (skillGroups.Count > 0)
        {
            adventure.SkillChoiceData = new Theatre5SkillChoiceData();
            HashSet<int> excluded = [];
            foreach (int group in skillGroups)
            {
                int id = DrawItemGroup(m, group, excluded);
                Require(ItemConfig(id).Type == 1, 20281002);
                excluded.Add(id);
                adventure.SkillChoiceData.SkillGroups.Add(NewItem(adventure, id));
            }
        }
        adventure.Status = adventure.SkillChoiceData == null ? 4 : 3;
        TriggerEffects(m, "EnterShop");
        if (adventure.SkillChoiceData != null) TriggerEffects(m, "EnterSkillFrame");
        if (adventure.Status == 4) GenerateMissionChoices(m);
        PushShop(m);
        if (adventure.SkillChoiceData != null)
            m.Push(new NotifyTheatre5SkillChoiceUpdate
            {
                GoldNum = adventure.GoldNum, BagData = adventure.BagData, SkillChoiceData = adventure.SkillChoiceData
            });
    }

    [RequestPacketHandler("Theatre5EnterShopRequest")]
    public static void EnterShopRequest(Session session, Packet.Request packet) =>
        Handle<Theatre5EnterShopRequest, Theatre5EnterShopResponse>(session, packet, (m, request, response) =>
        {
            EnterShop(m);
            var adventure = Adventure(m);
            response.EnterShopCnt = adventure.EnterShopCnt;
            response.Status = adventure.Status;
            response.ChooseMissions = adventure.ChooseMissions;
        });

    [RequestPacketHandler("XTheatre5SkillChoiceRequest")]
    public static void SkillChoiceRequest(Session session, Packet.Request packet) =>
        Handle<XTheatre5SkillChoiceRequest, XTheatre5SkillChoiceResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(session);
            var adventure = Adventure(m);
            Require(adventure.Status == 3 && adventure.SkillChoiceData != null, 20281001);
            var item = adventure.SkillChoiceData.SkillGroups.FirstOrDefault(item => item.InstanceId == request.InstanceId);
            Require(item != null && CanOwnItem(m, item.ItemId), 20281003);
            PlaceItem(m, item, request.IsEquipped, request.TargetIndex);
            adventure.SkillChoiceData = null;
            adventure.Status = 4;
            TriggerEffects(m, "ChooseSkill", item.ItemId);
            GenerateMissionChoices(m);
            PushShop(m);
            response.Status = adventure.Status;
            response.ChooseMissions = adventure.ChooseMissions;
        }, (response, code) =>
        {
            response.Code = code;
            response.Status = (session.player.Theatre5.Data.PvpType == 1
                ? session.player.Theatre5.Data.PvpAdventureData?.Status
                : session.player.Theatre5.Data.PveAdventureData?.Status) ?? 0;
        });

    [RequestPacketHandler("XTheatre5ShopBuyItemRequest")]
    public static void ShopBuyRequest(Session session, Packet.Request packet) =>
        Handle<XTheatre5ShopBuyItemRequest, XTheatre5ShopBuyItemResponse>(session, packet, (m, request, response) =>
        {
            var shop = ShopForAction(m);
            var adventure = Adventure(m);
            var goods = shop.Goods.Take(shop.UnlockGridsNum).FirstOrDefault(goods => goods.ItemInfo.InstanceId == request.InstanceId);
            Require(goods != null, 20281003);
            Require(!goods.IsSoldOut, 20281004);
            Require(CanOwnItem(m, goods.ItemInfo.ItemId), 20281003);
            var itemConfig = ItemConfig(goods.ItemInfo.ItemId);
            int price = Convert.ToInt32(goods.IsSpecialPrice ? itemConfig.DiscountPrice : itemConfig.Price);
            Require(price >= 0 && adventure.GoldNum >= price, 20281005);
            adventure.GoldNum -= price;
            goods.IsSoldOut = true;
            goods.IsFreeze = false;
            if (goods.ItemInfo.ItemType == 2 && !goods.ItemInfo.IsStrengthen && adventure.RuneAutoStrengthenCnt > 0)
            {
                var rune = Rows<Theatre5ItemRuneTable>().FirstOrDefault(row => row.Id == goods.ItemInfo.ItemId);
                Require(rune != null, 20281003);
                if (Convert.ToInt32(rune.EvolveAttrId) > 0 || Convert.ToInt32(rune.EvolveMagicId) > 0)
                {
                    goods.ItemInfo.IsStrengthen = true;
                    adventure.RuneAutoStrengthenCnt--;
                }
            }
            if (goods.ItemInfo.ItemType is 1 or 2 or 3 or 6)
                PlaceItem(m, goods.ItemInfo, request.IsEquipped, request.TargetIndex);
            else
            {
                Require(!request.IsEquipped && request.TargetIndex == -1, 20281007);
                AddExistingItem(m, goods.ItemInfo);
            }
            UpdateMissionProgress(m, "SpendGold", price);
            UpdateMissionProgress(m, "BuyItem", 1, goods.ItemInfo.ItemId);
            TriggerEffects(m, "BuyItem", goods.ItemInfo.ItemId);
            response.GoldNum = adventure.GoldNum;
            response.BagData = adventure.BagData;
            response.ShopData = adventure.ShopData;
            response.RuneAutoStrengthenCnt = adventure.RuneAutoStrengthenCnt;
        });

    [RequestPacketHandler("XTheatre5ShopSellItemRequest")]
    public static void ShopSellRequest(Session session, Packet.Request packet) =>
        Handle<XTheatre5ShopSellItemRequest, XTheatre5ShopSellItemResponse>(session, packet, (m, request, response) =>
        {
            EnsureAvailable(m.Session);
            var adventure = Adventure(m);
            Require(adventure.Status is 3 or 4, 20281001);
            Require(adventure.ShopData != null, 20283018);
            var bag = adventure.BagData;
            var items = request.IsEquipped ? bag.SkillDict.Values.Concat(bag.RuneDict.Values)
                : bag.BagItemDict.Values.Concat(bag.TempItemDict.Values);
            var item = items.FirstOrDefault(item => item.InstanceId == request.InstanceId);
            Require(item != null && item.ItemType == request.ItemType && item.ItemType is 1 or 2 or 6, 20281003);
            int price = SellPrice(item);
            Require(price >= 0, 20281002);
            RemoveItem(adventure, item.InstanceId);
            adventure.GoldNum = checked(adventure.GoldNum + price);
            response.GoldNum = adventure.GoldNum;
            response.BagData = adventure.BagData;
            response.ShopData = adventure.ShopData;
        });

    [RequestPacketHandler("XTheatre5ShopRefreshRequest")]
    public static void ShopRefreshRequest(Session session, Packet.Request packet) =>
        Handle<XTheatre5ShopRefreshRequest, XTheatre5ShopRefreshResponse>(session, packet, (m, request, response) =>
        {
            var shop = ShopForAction(m, 20281009);
            var adventure = Adventure(m);
            var config = ShopConfig(shop.ShopId);
            var cost = Rows<Theatre5ShopRefreshCostTable>().Where(row => row.GroupId == config.RefreshCostGroupId
                && Convert.ToInt32(row.RefreshCnt) <= shop.RefreshCnt).OrderBy(row => row.Id).FirstOrDefault();
            Require(cost != null, 20281002);
            int price = adventure.EffectFreeRefreshCnt > 0 ? 0 : Convert.ToInt32(cost.GoldCost);
            Require(price >= 0 && adventure.GoldNum >= price, 20281005);
            adventure.GoldNum -= price;
            if (adventure.EffectFreeRefreshCnt > 0) adventure.EffectFreeRefreshCnt--;
            else shop.RefreshCnt = checked(shop.RefreshCnt + 1);
            ReplaceShopGoods(m, Enumerable.Range(1, shop.UnlockGridsNum));
            UpdateMissionProgress(m, "SpendGold", price);
            response.ShopData = adventure.ShopData;
            response.GoldNum = adventure.GoldNum;
            response.UpdateEffectFreeRefreshCnt = adventure.EffectFreeRefreshCnt;
        });

    [RequestPacketHandler("XTheatre5ShopFreezeRequest")]
    public static void ShopFreezeRequest(Session session, Packet.Request packet) =>
        Handle<XTheatre5ShopFreezeRequest, XTheatre5ShopFreezeResponse>(session, packet, (m, request, response) =>
        {
            var shop = ShopForAction(m);
            var targets = shop.Goods.Take(shop.UnlockGridsNum).Where(goods => !goods.IsSoldOut
                && (request.InstanceId == -1 || goods.ItemInfo.InstanceId == request.InstanceId)).ToList();
            Require(request.InstanceId == -1 || targets.Count == 1, 20281003);
            foreach (var goods in targets) goods.IsFreeze = request.IsFreeze;
            response.ShopData = shop;
        });
}
