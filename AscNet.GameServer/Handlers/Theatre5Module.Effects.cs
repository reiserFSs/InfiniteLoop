using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.theatre5;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    // LOCAL dispatch fills the missing server subscription table; explicit timing
    // is transcribed from Theatre5Item.Desc (40001..40213,610xx,611xx,690xx).
    // No runtime text parsing. The owned-relic snapshot bounds execution: effects
    // never recursively trigger acquisitions or other effects.
    internal static void TriggerEffects(Mutation mutation, string trigger, int itemId = 0, int value = 0)
    {
        Require(trigger is "EnterShop" or "RefreshShop" or "BuyItem" or "SellItem" or "BeforeBattle"
            or "BattleWin" or "BattleLose" or "BattleDraw" or "RoundEnd" or "StrengthenRune"
            or "LevelUp" or "ChooseRelic" or "ChooseSkill" or "EnterSkillFrame", 20283016);
        var adventure = Adventure(mutation);
        // ChooseRelic's value is the instance owned by its response callback.
        // Mission rewards have no ChooseRelic response and leave value at zero.
        if (trigger == "ChooseRelic" && value > 0)
            Require(adventure.BagData.RelicDict.TryGetValue(value, out var chosen)
                && chosen.ItemId == itemId, 20283005);
        if (trigger == "BeforeBattle")
        {
            adventure.EffectQueue = GetRelicBattleEffects(mutation.Session.player, mutation.State);
            // An empty queue is meaningful: the client clears previous temporary buffs.
            mutation.Push(new NotifyTheatre5Effect { EffectQueue = adventure.EffectQueue });
            return;
        }
        if (trigger is not ("EnterShop" or "ChooseRelic" or "LevelUp" or "BattleWin")) return;
        int oldGold = adventure.GoldNum;
        var effects = new List<Theatre5Effect>();
        bool bagChanged = false;
        var replacedShopSlots = new HashSet<int>();
        foreach (var relic in adventure.BagData.RelicDict.Values.ToArray())
        {
            if (trigger == "ChooseRelic" && relic.ItemId != itemId) continue;
            if (trigger == "ChooseRelic" && value > 0 && relic.InstanceId != value) continue;
            var config = Rows<Theatre5ItemRelicTable>().SingleOrDefault(row => row.Id == relic.ItemId);
            Require(config != null && config.Condition.Count == config.Effect.Count, 20283014);
            for (int i = 0; i < config.Effect.Count; i++)
            {
                var row = EffectConfig(config.Effect[i]);
                Require(row.Type is >= 1 and <= 13, 20283015);
                if (RelicEffectTrigger(relic.ItemId, row, i) != trigger) continue;
                string onceKey = $"{relic.InstanceId}:{row.Id}:{i}";
                bool once = trigger == "ChooseRelic" || relic.ItemId is 40013 or 40022;
                if (once && adventure.TriggeredRelicEffects.Contains(onceKey)) continue;
                if (!IsConditionMet(mutation.Session, config.Condition[i], mutation.State)) continue;
                var effect = ApplyRelicEffect(mutation, row, replacedShopSlots, ref bagChanged);
                if (effect != null) effects.Add(effect);
                if (once) adventure.TriggeredRelicEffects.Add(onceKey);
            }
        }
        // Full bag hydration covers equipped-slot changes, which BagUpdate's client
        // consumer cannot represent. Keep PRE-effect gold: type10 adds a delta.
        if (bagChanged)
        {
            var bag = adventure.BagData;
            if (trigger == "ChooseRelic" && value > 0)
            {
                // UpdateOneRelic appends RelicOrders only for an unseen instance.
                // Keep it in durable state, but let the response introduce it.
                bag = Clone(bag);
                bag.RelicDict.Remove(value);
            }
            mutation.Push(new NotifyTheatre5BagDataUpdate
            { Status = adventure.Status, GoldNum = oldGold, BagData = bag });
        }
        if (effects.Count != 0) mutation.Push(new NotifyTheatre5Effect { EffectQueue = effects });
    }

    private static string RelicEffectTrigger(int relicId, Theatre5RelicEffectTable effect, int index)
    {
        if (effect.Type is 1 or 9) return "BeforeBattle";
        if (effect.Type == 13) return "EnterShop";
        return relicId switch
        {
            40002 or 40018 or 40023 => "ChooseRelic",
            40003 or 40010 or 40012 or 40013 => "LevelUp",
            40006 => effect.Id == 1007 ? "ChooseRelic" : "EnterShop",
            40009 => effect.Id == 1010 ? "ChooseRelic" : "EnterShop",
            40022 => "BattleWin",
            >= 61081 and <= 61084 => "ChooseRelic",
            // Commission rewards put the one-time coin grant first; their other
            // economic operands explicitly describe subsequent tavern visits.
            >= 61011 and <= 61075 or >= 61181 and <= 61185
                or >= 69091 and <= 69155 => index == 0 ? "ChooseRelic" : "EnterShop",
            >= 40001 and <= 40213 => "EnterShop",
            // Unused/test-only economic definitions remain executable on explicit
            // acquisition, not periodically injected into ordinary gameplay.
            _ => "ChooseRelic"
        };
    }

    internal static List<Theatre5Effect> GetRelicBattleEffects(Player? player, PlayerTheatre5State state)
    {
        Theatre5AdventureData? adventure = state.Data.PvpType == 1
            ? state.Data.PvpAdventureData : state.Data.PveAdventureData;
        Require(adventure != null, 20280007);
        var effects = new List<Theatre5Effect>();
        foreach (var relic in adventure.BagData.RelicDict.Values)
        {
            var config = Rows<Theatre5ItemRelicTable>().SingleOrDefault(row => row.Id == relic.ItemId);
            Require(config != null && config.Condition.Count == config.Effect.Count, 20283014);
            for (int i = 0; i < config.Effect.Count; i++)
            {
                var row = EffectConfig(config.Effect[i]);
                Require(row.Type is >= 1 and <= 13, 20283015);
                if (row.Type is not (1 or 9) || !IsConditionMet(player, config.Condition[i], state)) continue;
                if (row.Type == 1)
                {
                    Require(row.Param.Count > 0 && row.Param.All(id => id > 0), 20283017);
                    effects.Add(new() { Type = 1, AddBuffResult = new() { Buffs = row.Param.ToList() } });
                }
                else
                {
                    Require(row.Param.Count == 5, 20283017);
                    effects.Add(new() { Type = 9, AddAttrResult = new()
                    {
                        AttrType = row.Param[0], FixVal = row.Param[1], RateVal = row.Param[2],
                        SpecificVal = EffectSpecific(adventure, row.Param[3], state), SpecificRateVal = row.Param[4]
                    } });
                }
            }
        }
        return effects;
    }

    private static Theatre5RelicEffectTable EffectConfig(int id)
    {
        var row = Rows<Theatre5RelicEffectTable>().SingleOrDefault(row => row.Id == id);
        Require(row != null, 20283014);
        return row;
    }

    // LOCAL zero-based operand decoding, supported by authored rows1009 (round0),
    // 1017 (level5),1019 (empty bag7). Do not use the one-based UI label enum.
    // Wins/losses are run-local; archetypes count distinct equipped rune tags.
    private static int EffectSpecific(Theatre5AdventureData adventure, int operand, PlayerTheatre5State state) => operand switch
    {
        0 => adventure.RoundNum,
        1 => adventure is Theatre5PvpAdventureData ? state.PvpWinCount
            : ((Theatre5PveAdventureData)adventure).PveChapterData?.BattleStatus.Count(win => win) ?? 0,
        2 => adventure is Theatre5PvpAdventureData ? state.PvpLoseCount
            : ((Theatre5PveAdventureData)adventure).PveChapterData?.BattleStatus.Count(win => !win) ?? 0,
        3 => adventure.Health,
        4 => adventure.GoldNum,
        5 => adventure.CharacterLv,
        6 => adventure.BagData.RuneDict.Values.SelectMany(item => ItemConfig(item.ItemId).Tags).Where(tag => tag > 0).Distinct().Count(),
        7 => Math.Max(0, adventure.BagData.BagGridsNum - adventure.BagData.BagItemDict.Count),
        _ => throw new InvalidDataException($"Unknown Theatre5 effect operand {operand}.")
    };

    private static int EffectAmount(Mutation mutation, List<int> parameters)
    {
        Require(parameters.Count is 1 or 3, 20283017);
        return parameters.Count == 1 ? parameters[0] : checked(parameters[0]
            + (int)Math.Floor((long)EffectSpecific(Adventure(mutation), parameters[1], mutation.State) * parameters[2] / 10000d));
    }

    private static Theatre5Effect? ApplyRelicEffect(Mutation mutation, Theatre5RelicEffectTable row,
        HashSet<int> replacedShopSlots, ref bool bagChanged)
    {
        var adventure = Adventure(mutation);
        var parameters = row.Param;
        var effect = new Theatre5Effect { Type = row.Type };
        switch (row.Type)
        {
            case 2:
                Require(parameters.Count == 1, 20283017);
                int randomId = DrawItemGroup(mutation, parameters[0], allowExhausted: true);
                if (randomId == 0) return null; // Valid pool has reached its authored ownership caps.
                var drawn = AddItems(mutation, randomId);
                Require(drawn.Count == 1, 20283017);
                effect.RandomItemGroupEffectResult = new() { UpdateItems = drawn.Select(item => EffectBagUpdate(adventure, item)).ToList() };
                bagChanged = true;
                break;
            case 3:
                Require(parameters.Count == 1, 20283017);
                var added = AddItems(mutation, parameters[0]);
                Require(added.Count == 1, 20283017);
                effect.AddItemGroupEffectResult = new() { UpdateItem = EffectBagUpdate(adventure, added[0]) };
                bagChanged = true;
                break;
            case 4:
                adventure.GoldNum = Math.Max(0, checked(adventure.GoldNum + EffectAmount(mutation, parameters)));
                effect.ChangeGoldResult = new() { NewGold = adventure.GoldNum };
                break;
            case 5:
                adventure.EffectFreeRefreshCnt = Math.Max(0, checked(adventure.EffectFreeRefreshCnt + EffectAmount(mutation, parameters)));
                effect.AddFreeShopFreshCntResult = new() { NewEffectFreeRefreshCnt = adventure.EffectFreeRefreshCnt };
                break;
            case 6:
                adventure.CharacterExp = Math.Max(0, checked(adventure.CharacterExp + EffectAmount(mutation, parameters)));
                effect.AddExpResult = new() { NewExp = adventure.CharacterExp };
                break;
            case 7:
                Require(parameters.Count == 1 && parameters[0] > 0 && adventure.ShopData != null, 20283017);
                var steals = new List<Theatre5BagUpdate>();
                for (int count = 0; count < parameters[0]; count++)
                {
                    var candidates = adventure.ShopData.Goods.Where(goods => !goods.IsSoldOut && goods.ItemInfo.ItemType == 2).ToArray();
                    // No rune in the shop is a valid empty target set, not unsupported content.
                    if (candidates.Length == 0) break;
                    var goods = candidates[Random.Shared.Next(candidates.Length)];
                    goods.IsSoldOut = true;
                    AddExistingItem(mutation, goods.ItemInfo);
                    steals.Add(EffectBagUpdate(adventure, goods.ItemInfo));
                }
                effect.RandomStealRuneResult = new() { UpdateItems = steals };
                bagChanged = true;
                break;
            case 8:
                Require(parameters.Count == 1 && parameters[0] >= 0, 20283017);
                adventure.RuneAutoStrengthenCnt = checked(adventure.RuneAutoStrengthenCnt + parameters[0]);
                effect.AddAutoStrengthenCntResult = new() { NewAutoStrengthenCnt = adventure.RuneAutoStrengthenCnt };
                break;
            case 10:
            case 11:
                Require(parameters.Count == (row.Type == 10 ? 2 : 6) && parameters[0] is 1 or 2 && parameters[1] > 0, 20283017);
                var slots = parameters[0] == 1 ? adventure.BagData.RuneDict : adventure.BagData.BagItemDict;
                slots.TryGetValue(parameters[1], out var old);
                // A valid position without a rune has no target (skills are never
                // sold/replaced). No invented removal instance or payload is sent.
                if (old == null || old.ItemType != 2) return null;
                var removed = new Theatre5BagUpdate { UpdateType = 1, Index = parameters[1], Item = Clone(old) };
                RemoveItem(adventure, old.InstanceId);
                if (row.Type == 10)
                {
                    int gold = SellPrice(old);
                    adventure.GoldNum = checked(adventure.GoldNum + gold);
                    effect.AutoSellRuneResult = new() { UpdateItem = removed, SellGold = gold };
                }
                else
                {
                    int quality = Convert.ToInt32(ItemConfig(old.ItemId).Quality);
                    Require(quality is >= 1 and <= 4, 20283017);
                    int id = DrawItemGroup(mutation, parameters[quality + 1], [old.ItemId]);
                    var replacement = NewItem(adventure, id);
                    Require(replacement.ItemType == 2, 20283017);
                    PlaceItem(mutation, replacement, parameters[0] == 1, parameters[1]);
                    effect.AutoRuneReplaceResult = new()
                    {
                        UpdateItems = parameters[0] == 1 ? []
                            : [removed, EffectBagUpdate(adventure, replacement)]
                    };
                }
                bagChanged = true;
                break;
            case 12:
                Require(parameters.Count == 1, 20283017);
                adventure.Health = Math.Max(0, checked(adventure.Health + parameters[0]));
                effect.ChangeHpEffectResult = new() { NewHp = adventure.Health };
                break;
            case 13:
                Require(parameters.Count == 2 && parameters[1] > 0 && adventure.ShopData != null, 20283017);
                var available = Enumerable.Range(0, adventure.ShopData.Goods.Count)
                    .Where(index => !adventure.ShopData.Goods[index].IsFreeze && !replacedShopSlots.Contains(index)).ToArray();
                Require(parameters[1] <= adventure.ShopData.Goods.Count, 20283017);
                var exclusions = new HashSet<int>();
                var replacements = new Dictionary<int, Theatre5Goods>();
                foreach (int index in available.Take(parameters[1]))
                {
                    var previous = adventure.ShopData.Goods[index];
                    bool wasSold = previous.IsSoldOut;
                    previous.IsSoldOut = true; // The replaced offer must not consume its ownership cap.
                    int id = DrawItemGroup(mutation, parameters[0], exclusions, allowExhausted: true);
                    if (id == 0)
                    {
                        previous.IsSoldOut = wasSold;
                        break; // Authored "until all qualifying items have been purchased".
                    }
                    exclusions.Add(id);
                    var goods = new Theatre5Goods { ItemInfo = NewItem(adventure, id) };
                    adventure.ShopData.Goods[index] = goods;
                    replacements.Add(index + 1, goods);
                    replacedShopSlots.Add(index);
                }
                if (replacements.Count == 0) return null; // Frozen/full or exhausted shop is a valid empty target set.
                effect.ReplaceShopGoodsResult = new() { ReplaceShopGoods = replacements };
                break;
            default:
                throw new InvalidDataException($"Theatre5 effect {row.Id} has invalid economic type {row.Type}.");
        }
        return effect;
    }

    private static Theatre5BagUpdate EffectBagUpdate(Theatre5AdventureData adventure, Theatre5Item item)
    {
        foreach (var pair in adventure.BagData.BagItemDict)
            if (pair.Value.InstanceId == item.InstanceId) return new() { UpdateType = 3, Index = pair.Key, Item = Clone(item) };
        foreach (var pair in adventure.BagData.TempItemDict)
            if (pair.Value.InstanceId == item.InstanceId) return new() { UpdateType = 3, IsTempBag = true, Index = pair.Key, Item = Clone(item) };
        throw new InvalidDataException($"Theatre5 effect item {item.InstanceId} did not enter a bag slot.");
    }
}
