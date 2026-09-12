using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.theatre5;
using AscNet.Table.V2.share.theatre5.theatremission;
using MessagePack;
using Newtonsoft.Json;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateGodfallEconomyChecks()
    {
        ValidateGodfallShopAndBag();
        ValidateGodfallClickPlacement();
        ValidateGodfallHammer();
        ValidateGodfallLevels();
        ValidateGodfallMissions();
        ValidateGodfallEconomicEffects();
        ValidateGodfallUnboundEffectOperations();
    }

    // These are explicitly authored, funded-before-scenario fixtures, not natural runs.
    private static GodfallCase GodfallEconomyFixture(string name, int gold = 1000)
    {
        GodfallCase test = new("authored-economy-" + name);
        test.Data.PvpType = 1;
        test.Data.PvpAdventureData = new Theatre5PvpAdventureData
        {
            CharacterId = test.Data.Characters.Keys.Order().First(), Status = 4, RoundNum = 1,
            Health = 5, GoldNum = gold, CharacterLv = 1,
            BagData = new() { BagGridsNum = 6, SkillGridsNum = 3, RuneGridsNum = 2 },
            ShopData = new() { ShopId = 1, UnlockGridsNum = 3 }
        };
        return test;
    }

    private static Theatre5Item GodfallEconomyItem(GodfallCase test, int id) => new()
    {
        InstanceId = ++test.Adventure.IdSequence, ItemId = id,
        ItemType = TableReaderV2.Parse<Theatre5ItemTable>().Single(row => row.Id == id).Type
    };

    private static string GodfallEconomySnapshot(GodfallCase test) => JsonConvert.SerializeObject(test.Adventure);

    private static void GodfallEconomyReject(GodfallCase test, string name, object? request = null)
    {
        string before = GodfallEconomySnapshot(test);
        test.Call(name, request, success: false);
        AssertEqual(before, GodfallEconomySnapshot(test), name + " rejection preserves complete adventure economy");
        // Session archive reconciliation is orthogonal to the rejected mode mutation.
        // Keep every other notification guarded, including generic reward/inventory pushes.
        AssertEqual(0, test.Pushes.Count(push => push.Name != "NotifyArchiveCgs"), name + " rejection emits no economic success push");
    }

    private static void ValidateGodfallShopAndBag()
    {
        using GodfallCase test = GodfallEconomyFixture("sparse-swaps-shop");
        var rune = TableReaderV2.Parse<Theatre5ItemTable>().First(row => row.Type == 2 && row.Quality == 1);
        var first = GodfallEconomyItem(test, rune.Id);
        var second = GodfallEconomyItem(test, rune.Id);
        var offered = GodfallEconomyItem(test, rune.Id);
        test.Adventure.BagData.BagItemDict[5] = first;
        test.Adventure.BagData.RuneDict[2] = second;
        test.Adventure.ShopData!.Goods = [new() { ItemInfo = offered, IsSpecialPrice = true }];
        test.SaveFixture();
        test.Relog("sparse bag fixture");
        test.Call(nameof(XTheatre5BagItemMoveRequest), new XTheatre5BagItemMoveRequest
        { InstanceId = first.InstanceId, ItemType = 2, SrcIndex = 5, TargetEquipped = true, TargetIndex = 2 });
        AssertEqual(first.InstanceId, test.Adventure.BagData.RuneDict[2].InstanceId, "Sparse source equips at requested occupied slot");
        AssertEqual(second.InstanceId, test.Adventure.BagData.BagItemDict[5].InstanceId, "Swap returns displaced instance to sparse source slot");
        GodfallEconomyReject(test, nameof(XTheatre5BagItemMoveRequest), new XTheatre5BagItemMoveRequest
        { InstanceId = first.InstanceId, ItemType = 2, SrcEquipped = true, SrcIndex = 2, TargetIndex = 7 });
        int gold = test.Adventure.GoldNum;
        test.Call(nameof(XTheatre5ShopBuyItemRequest), new XTheatre5ShopBuyItemRequest { InstanceId = offered.InstanceId, TargetIndex = -1 });
        AssertEqual(offered.InstanceId, test.Adventure.BagData.BagItemDict[1].InstanceId, "Automatic placement fills first hole rather than count plus one");
        AssertEqual(gold - Convert.ToInt32(rune.DiscountPrice), test.Adventure.GoldNum, "Discount purchase charges authored price exactly");
        GodfallEconomyReject(test, nameof(XTheatre5ShopBuyItemRequest), new XTheatre5ShopBuyItemRequest { InstanceId = offered.InstanceId, TargetIndex = -1 });
        gold = test.Adventure.GoldNum;
        test.Call(nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest { InstanceId = offered.InstanceId, ItemType = 2 });
        AssertEqual(gold + Convert.ToInt32(rune.SellPrice), test.Adventure.GoldNum, "Selling credits exact authored resale value");
        AssertEqual(false, test.Adventure.BagData.BagItemDict.ContainsKey(1), "Selling removes only purchased instance");
        GodfallEconomyReject(test, nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest { InstanceId = offered.InstanceId, ItemType = 2 });
        test.Call(nameof(XTheatre5ShopRefreshRequest));
        var freeze = test.Adventure.ShopData!.Goods[0];
        test.Call(nameof(XTheatre5ShopFreezeRequest), new XTheatre5ShopFreezeRequest { InstanceId = freeze.ItemInfo.InstanceId, IsFreeze = true });
        string retained = JsonConvert.SerializeObject(test.Adventure.ShopData.Goods[0]);
        gold = test.Adventure.GoldNum;
        int refreshCount = test.Adventure.ShopData.RefreshCnt;
        var shop = TableReaderV2.Parse<Theatre5ShopTable>().Single(row => row.Id == test.Adventure.ShopData.ShopId);
        var cost = TableReaderV2.Parse<Theatre5ShopRefreshCostTable>().Where(row => row.GroupId == shop.RefreshCostGroupId && Convert.ToInt32(row.RefreshCnt) <= refreshCount).OrderByDescending(row => Convert.ToInt32(row.RefreshCnt)).First();
        test.Call(nameof(XTheatre5ShopRefreshRequest));
        AssertEqual(retained, JsonConvert.SerializeObject(test.Adventure.ShopData.Goods[0]), "Refresh preserves frozen instance and discount");
        AssertEqual(gold - Convert.ToInt32(cost.GoldCost), test.Adventure.GoldNum, "Paid refresh conserves gold");
        AssertEqual(refreshCount + 1, test.Adventure.ShopData.RefreshCnt, "Paid refresh advances price counter once");
        GodfallEconomyReject(test, nameof(XTheatre5ShopFreezeRequest), new XTheatre5ShopFreezeRequest { InstanceId = int.MaxValue, IsFreeze = true });
        gold = test.Adventure.GoldNum;
        int unlockCost = TableReaderV2.Parse<Theatre5GridUnlockCostTable>().Single(row => row.UnlockNum == 1).GoldCost;
        test.Call(nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 2 });
        AssertEqual(3, test.Adventure.BagData.RuneGridsNum, "Unlock adds exactly one rune slot");
        AssertEqual(gold - unlockCost, test.Adventure.GoldNum, "Unlock charges authored first-slot cost");
        GodfallEconomyReject(test, nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 1 });
        test.Relog("purchased and swapped economy");

        using GodfallCase poor = GodfallEconomyFixture("insufficient-funds", 0);
        var expensive = GodfallEconomyItem(poor, rune.Id);
        poor.Adventure.ShopData!.Goods = [new() { ItemInfo = expensive }];
        poor.SaveFixture();
        GodfallEconomyReject(poor, nameof(XTheatre5ShopBuyItemRequest), new XTheatre5ShopBuyItemRequest { InstanceId = expensive.InstanceId, TargetIndex = -1 });
        GodfallEconomyReject(poor, nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 2 });
        using GodfallCase discount = GodfallEconomyFixture("one-completed-round-grid-discount", 9);
        // Authored settled-state fixture; Battle checks own settlement/replay counter transitions.
        discount.Adventure.BagData.RoundNumWithoutGridUnlock = 1;
        discount.SaveFixture();
        discount.Call(nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 2 });
        AssertEqual(8, discount.Adventure.GoldNum, "First grid cost three minus one-round discount three floors at one gold");
        AssertEqual(0, discount.Adventure.BagData.RoundNumWithoutGridUnlock, "Paid unlock consumes accrued round discount");
        discount.Call(nameof(XTheatre5ShopUnlockGridRequest), new XTheatre5ShopUnlockGridRequest { GridType = 2 });
        AssertEqual(0, discount.Adventure.GoldNum, "Immediate second unlock pays full authored eight gold without reusing discount");
        AssertEqual(4, discount.Adventure.BagData.RuneGridsNum, "Discounted and full-price unlocks each add one slot");
    }

    private static void ValidateGodfallHammer()
    {
        using GodfallCase test = GodfallEconomyFixture("hammer");
        var rows = TableReaderV2.Parse<Theatre5ItemTable>();
        var hammer = rows.First(row => row.Type == 6 && rows.Any(rune => rune.Type == 2 && rune.Quality == row.Quality && (row.Tags.Count == 0 || row.Tags.Intersect(rune.Tags).Any())));
        var rune = rows.First(row => row.Type == 2 && row.Quality == hammer.Quality && (hammer.Tags.Count == 0 || hammer.Tags.Intersect(row.Tags).Any()));
        var tool = GodfallEconomyItem(test, hammer.Id);
        var spare = GodfallEconomyItem(test, hammer.Id);
        var target = GodfallEconomyItem(test, rune.Id);
        test.Adventure.BagData.BagItemDict[3] = tool;
        test.Adventure.BagData.BagItemDict[5] = spare;
        test.Adventure.BagData.RuneDict[2] = target;
        test.SaveFixture();
        int gold = test.Adventure.GoldNum;
        test.Call(nameof(XTheatre5HammerStrengthenRequest), new XTheatre5HammerStrengthenRequest { HammerId = tool.InstanceId, RuneId = target.InstanceId });
        AssertEqual(true, test.Adventure.BagData.RuneDict[2].IsStrengthen, "Hammer strengthens the exact equipped rune");
        AssertEqual(target.InstanceId, test.Adventure.BagData.RuneDict[2].InstanceId, "Strengthening preserves rune instance");
        AssertEqual(false, test.Adventure.BagData.BagItemDict.ContainsKey(3), "Strengthening consumes one hammer");
        AssertEqual(spare.InstanceId, test.Adventure.BagData.BagItemDict[5].InstanceId, "Strengthening preserves spare hammer");
        AssertEqual(gold, test.Adventure.GoldNum, "Hammer does not spend gold");
        GodfallEconomyReject(test, nameof(XTheatre5HammerStrengthenRequest), new XTheatre5HammerStrengthenRequest { HammerId = spare.InstanceId, RuneId = target.InstanceId });
        test.Call(nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest { InstanceId = target.InstanceId, ItemType = 2, IsEquipped = true });
        int rate = TableReaderV2.Parse<Theatre5ConfigTable>().Single(row => row.Key == "StrengthenRate").Values[0];
        AssertEqual(gold + (int)((long)Convert.ToInt32(rune.SellPrice) * rate / 10000), test.Adventure.GoldNum, "Strengthened resale applies multiplier once");
    }

    private static void ValidateGodfallLevels()
    {
        using GodfallCase test = GodfallEconomyFixture("levels-relics");
        test.Adventure.CharacterLv = 0;
        test.Adventure.CharacterExp = 9;
        test.SaveFixture();
        GodfallEconomyReject(test, nameof(XTheatre5CharacterLevelUpRequest));
        GodfallEconomyReject(test, nameof(XTheatre5BuyExpRequest), new XTheatre5BuyExpRequest { Exp = 2 });
        int gold = test.Adventure.GoldNum;
        test.Call(nameof(XTheatre5BuyExpRequest), new XTheatre5BuyExpRequest { Exp = 1 });
        AssertEqual(10, test.Adventure.CharacterExp, "Buying missing EXP reaches exact threshold");
        AssertEqual(gold - 1, test.Adventure.GoldNum, "One EXP costs authored shop price");
        test.Call(nameof(XTheatre5CharacterLevelUpRequest));
        AssertEqual(1, test.Adventure.CharacterLv, "Level transition advances once");
        AssertEqual(0, test.Adventure.CharacterExp, "Level consumes its threshold once");
        AssertEqual(3, test.Adventure.RandomRelics.Count, "Initial transition grants three authored relic offers");
        AssertEqual(3, test.Adventure.RandomRelics.Select(item => item.ItemId).Distinct().Count(), "Relic alternatives have distinct configurations");
        var stale = test.Adventure.RandomRelics[0].InstanceId;
        GodfallEconomyReject(test, nameof(XTheatre5BuyExpRequest), new XTheatre5BuyExpRequest { Exp = 1 });
        test.Call(nameof(XTheatre5RelicRefreshRequest));
        AssertEqual(1, test.Adventure.UseRelicRefreshCount, "Relic refresh consumes its allowance");
        GodfallEconomyReject(test, nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = stale });
        GodfallEconomyReject(test, nameof(XTheatre5RelicRefreshRequest));
        var chosen = test.Adventure.RandomRelics[0];
        test.Call(nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = chosen.InstanceId });
        AssertEqual(chosen.ItemId, test.Adventure.BagData.RelicDict[chosen.InstanceId].ItemId, "Selected relic retains offered identity");
        AssertEqual(0, test.Adventure.RandomRelics.Count, "Relic choice closes alternatives");
        AssertEqual(true, test.Data.RelicCollects.Contains(chosen.ItemId), "Relic acquisition enters collection");
        GodfallEconomyReject(test, nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = chosen.InstanceId });
        test.Relog("chosen relic");
        using GodfallCase capped = GodfallEconomyFixture("maximum-level");
        capped.Adventure.CharacterLv = TableReaderV2.Parse<Theatre5CharacterLevelTable>()
            .Where(row => row.GroupId == 1).Max(row => row.Level ?? 0);
        capped.Adventure.CharacterExp = 17;
        capped.SaveFixture();
        GodfallEconomyReject(capped, nameof(XTheatre5CharacterLevelUpRequest));
        GodfallEconomyReject(capped, nameof(XTheatre5BuyExpRequest), new XTheatre5BuyExpRequest { Exp = 1 });
    }

    private static void ValidateGodfallMissions()
    {
        using GodfallCase test = GodfallEconomyFixture("missions");
        test.Adventure.ChooseMissions[1] = new()
        { MissionId = 101, MissionState = 1, MissionBounty = new() { Bounty = 101, BountyLevel = 1 }, MissionCondition = new() { ConditionId = 1 } };
        test.Adventure.ChooseMissions[2] = new()
        { MissionId = 102, MissionState = 1, MissionBounty = new() { Bounty = 102, BountyLevel = 1 }, MissionCondition = new() { ConditionId = 11 } };
        test.Adventure.FreshMissionBounty = [101, 102];
        test.Adventure.FreshMissionCondition = [1, 11];
        test.SaveFixture();
        test.Call(nameof(Theatre5MissionFreshRequest), new Theatre5MissionFreshRequest { PositionId = 2 });
        AssertEqual(1, test.Adventure.FreshMissionCounts[2], "Mission refresh consumes per-slot allowance");
        AssertEqual(false, new[] { 101, 102 }.Contains(test.Adventure.ChooseMissions[2].MissionBounty.Bounty), "Refresh avoids previously offered bounties");
        GodfallEconomyReject(test, nameof(Theatre5MissionFreshRequest), new Theatre5MissionFreshRequest { PositionId = 2 });
        test.Call(nameof(Theatre5MissionChooseRequest), new Theatre5MissionChooseRequest { PositionId = 1 });
        AssertEqual(0, test.Adventure.ChooseMissions.Count, "Accepting mission closes all alternatives");
        GodfallEconomyReject(test, nameof(Theatre5MissionRewardRequest), new Theatre5MissionRewardRequest { ChooseItemId = 61011 });
        int gold = test.Adventure.GoldNum;
        test.Call(nameof(Theatre5MissionLevelUpRequest), new Theatre5MissionLevelUpRequest { CurLevel = 1 });
        AssertEqual(gold - 6, test.Adventure.GoldNum, "Mission upgrade charges authored six gold");
        AssertEqual(0, test.Adventure.Missioning!.MissionCondition.ConditionCounter, "Bounty upgrades do not advance spending mission");
        GodfallEconomyReject(test, nameof(Theatre5MissionLevelUpRequest), new Theatre5MissionLevelUpRequest { CurLevel = 2 });
        test.Call(nameof(XTheatre5BuyExpRequest), new XTheatre5BuyExpRequest { Exp = 10 });
        AssertEqual(10, test.Adventure.Missioning!.MissionCondition.ConditionCounter, "EXP purchase contributes exact spending amount");
        AssertEqual(2, test.Adventure.Missioning.MissionState, "Threshold spending completes mission");
        GodfallEconomyReject(test, nameof(Theatre5MissionRewardRequest), new Theatre5MissionRewardRequest { ChooseItemId = 61011 });
        gold = test.Adventure.GoldNum;
        test.Call(nameof(Theatre5MissionRewardRequest), new Theatre5MissionRewardRequest { ChooseItemId = 61012 });
        AssertEqual(3, test.Adventure.Missioning!.MissionState, "Claim marks mission rewarded");
        AssertEqual(1, test.Adventure.BagData.RelicDict.Values.Count(item => item.ItemId == 61012), "Claim grants exact upgraded bounty relic once");
        AssertEqual(gold + 14, test.Adventure.GoldNum, "Bounty acquisition applies authored one-time gold effect");
        GodfallEconomyReject(test, nameof(Theatre5MissionRewardRequest), new Theatre5MissionRewardRequest { ChooseItemId = 61012 });
        test.Relog("claimed mission");
    }

    private static List<Theatre5Effect> GodfallEconomyEffects(GodfallCase test) => test.Pushes
        .Where(push => push.Name == nameof(NotifyTheatre5Effect))
        .SelectMany(push => MessagePackSerializer.Deserialize<NotifyTheatre5Effect>(push.Content).EffectQueue).ToList();

    private static void ValidateGodfallEconomicEffects()
    {
        foreach (int relicId in new[] { 40002, 40006 })
        {
            using GodfallCase test = GodfallEconomyFixture("acquisition-effect-" + relicId);
            var relic = GodfallEconomyItem(test, relicId);
            Theatre5Item? prior = null;
            if (relicId == 40002)
            {
                prior = GodfallEconomyItem(test, relicId);
                test.Adventure.BagData.RelicDict[prior.InstanceId] = prior;
                test.Adventure.RelicOrders.Add(prior.InstanceId);
            }
            test.Adventure.RandomRelics = [relic];
            test.SaveFixture();
            var response = test.Call(nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = relic.InstanceId });
            var effects = GodfallEconomyEffects(test);
            if (relicId == 40002)
            {
                var updates = effects.Where(effect => effect.Type == 2).SelectMany(effect => effect.RandomItemGroupEffectResult!.UpdateItems).ToList();
                AssertEqual(4, updates.Count, "Four authored acquisition draws emit four inventory grants");
                AssertEqual(4, updates.Select(update => update.Item.InstanceId).Distinct().Count(), "Random grants use distinct instances");
                foreach (var update in updates)
                    AssertEqual(true, test.Adventure.BagData.BagItemDict.Values.Concat(test.Adventure.BagData.TempItemDict.Values).Any(item => item.InstanceId == update.Item.InstanceId), "Effect grant exists in durable visible inventory");
                // Source-grounded C# projection, not execution of the Lua runtime:
                // AdventureDataBase.UpdateFullBagData, HandleBagUpdate/UpdateItem,
                // UpdateOneRelic; Agency's ChooseRelic callback runs after pushes.
                Theatre5BagData clientBag = new();
                List<int> clientOrders = [prior!.InstanceId];
                foreach (var push in test.Pushes)
                {
                    if (push.Name == nameof(NotifyTheatre5BagDataUpdate))
                        clientBag = MessagePackSerializer.Deserialize<NotifyTheatre5BagDataUpdate>(push.Content).BagData;
                    else if (push.Name == nameof(NotifyTheatre5Effect))
                    {
                        foreach (var update in MessagePackSerializer.Deserialize<NotifyTheatre5Effect>(push.Content).EffectQueue
                            .Where(effect => effect.Type == 2).SelectMany(effect => effect.RandomItemGroupEffectResult!.UpdateItems))
                        {
                            var slots = update.IsTempBag ? clientBag.TempItemDict : clientBag.BagItemDict;
                            foreach (int key in slots.Where(pair => pair.Value.InstanceId == update.Item.InstanceId).Select(pair => pair.Key).ToArray())
                                slots.Remove(key);
                            slots[update.Index] = update.Item;
                        }
                    }
                }
                AssertEqual(prior.InstanceId, clientBag.RelicDict[prior.InstanceId].InstanceId, "Pre-response snapshot preserves prior same-config relic instance");
                AssertEqual(false, clientBag.RelicDict.ContainsKey(relic.InstanceId), "Pre-response snapshot leaves selected instance for callback insertion");
                var selected = response["ChooseRelic"]!.ToObject<Theatre5Item>()!;
                bool wasKnown = clientBag.RelicDict.Values.Any(item => item.InstanceId == selected.InstanceId);
                clientBag.RelicDict[selected.InstanceId] = selected;
                if (!wasKnown) clientOrders.Add(selected.InstanceId);
                AssertEqual(1, clientOrders.Count(id => id == selected.InstanceId), "Push then choice callback appends selected relic order exactly once");
                AssertEqual(2, clientBag.RelicDict.Values.Count(item => item.ItemId == relicId), "Callback retains both distinct same-config relics");
                AssertEqual(string.Join(",", test.Adventure.RelicOrders), string.Join(",", clientOrders), "Projected consumer relic order matches durable order");
                AssertEqual(JsonConvert.SerializeObject(test.Adventure.BagData.BagItemDict), JsonConvert.SerializeObject(clientBag.BagItemDict), "Rune draw updates survive full-bag push and callback");
                string order = string.Join(",", test.Adventure.RelicOrders);
                test.Relog("selected relic order");
                AssertEqual(order, string.Join(",", test.Adventure.RelicOrders), "Relog preserves selected relic ordering");
                AssertEqual(2, test.Adventure.BagData.RelicDict.Values.Count(item => item.ItemId == relicId), "Relog preserves prior and selected same-config relic instances");
            }
            else
            {
                AssertEqual(8, test.Adventure.CharacterExp, "Acquisition grants eight EXP once");
                AssertEqual(8, effects.Single(effect => effect.Type == 6).AddExpResult!.NewExp, "EXP effect publishes absolute post-grant EXP");
            }
            GodfallEconomyReject(test, nameof(XTheatre5RelicChooseRequest), new XTheatre5RelicChooseRequest { InstanceId = relic.InstanceId });
        }

        foreach (int relicId in new[] { 40005, 40007, 40015, 40019, 40201 })
        {
            using GodfallCase test = GodfallEconomyFixture("enter-shop-effect-" + relicId);
            test.Adventure.Status = 2;
            var relic = GodfallEconomyItem(test, relicId);
            test.Adventure.BagData.RelicDict[relic.InstanceId] = relic;
            test.Adventure.RelicOrders.Add(relic.InstanceId);
            var runeId = TableReaderV2.Parse<Theatre5ItemTable>().First(row => row.Type == 2 && row.Quality == 1).Id;
            var oldRune = GodfallEconomyItem(test, runeId);
            test.Adventure.BagData.BagItemDict[1] = oldRune;
            for (int i = 0; i < 3; i++) test.Adventure.ShopData!.Goods.Add(new() { ItemInfo = GodfallEconomyItem(test, runeId), IsFreeze = relicId != 40201 });
            int gold = test.Adventure.GoldNum;
            test.SaveFixture();
            test.Call(nameof(Theatre5EnterShopRequest));
            var effects = GodfallEconomyEffects(test);
            int income = TableReaderV2.Parse<Theatre5PvpRoundRefreshTable>().Single(row => row.RoundNum == 1).AddGoldNum;
            switch (relicId)
            {
                case 40005:
                    AssertEqual(1, effects.Single(effect => effect.Type == 5).AddFreeShopFreshCntResult!.NewEffectFreeRefreshCnt, "Free-refresh relic grants one allowance");
                    if (test.Adventure.Status == 3)
                        test.Call(nameof(XTheatre5SkillChoiceRequest), new XTheatre5SkillChoiceRequest
                        { InstanceId = test.Adventure.SkillChoiceData!.SkillGroups[0].InstanceId, IsEquipped = true, TargetIndex = 1 });
                    int freeGold = test.Adventure.GoldNum;
                    int paidCount = test.Adventure.ShopData!.RefreshCnt;
                    test.Call(nameof(XTheatre5ShopRefreshRequest));
                    AssertEqual(freeGold, test.Adventure.GoldNum, "Free refresh spends no gold");
                    AssertEqual(paidCount, test.Adventure.ShopData!.RefreshCnt, "Free refresh preserves paid-price counter");
                    AssertEqual(0, test.Adventure.EffectFreeRefreshCnt, "Free refresh consumes exactly one allowance");
                    break;
                case 40007:
                    AssertEqual(gold + income + 1, test.Adventure.GoldNum, "Round operand zero grants current round gold in addition to income");
                    AssertEqual(test.Adventure.GoldNum, effects.Single(effect => effect.Type == 4).ChangeGoldResult!.NewGold, "Gold effect publishes absolute balance");
                    break;
                case 40015:
                    var stolen = effects.Single(effect => effect.Type == 7).RandomStealRuneResult!.UpdateItems.Single().Item;
                    AssertEqual(true, test.Adventure.ShopData!.Goods.Single(goods => goods.ItemInfo.InstanceId == stolen.InstanceId).IsSoldOut, "Steal consumes the exact shop offer");
                    AssertEqual(true, test.Adventure.BagData.BagItemDict.Values.Any(item => item.InstanceId == stolen.InstanceId), "Stolen instance enters bag without duplication");
                    AssertEqual(gold + income - 6, test.Adventure.GoldNum, "Steal relic charges authored six-gold effect only");
                    break;
                case 40019:
                    AssertEqual(1, effects.Count(effect => effect.Type == 11), "Replacement emits one rune replacement effect");
                    AssertEqual(false, test.Adventure.BagData.BagItemDict[1].InstanceId == oldRune.InstanceId, "Replacement removes old instance at exact bag slot");
                    AssertEqual(2, TableReaderV2.Parse<Theatre5ItemTable>().Single(row => row.Id == test.Adventure.BagData.BagItemDict[1].ItemId).Quality, "Green rune replacement draws authored blue pool");
                    AssertEqual(gold + income - 2, test.Adventure.GoldNum, "Rune replacement charges authored two gold");
                    break;
                case 40201:
                    var replacements = effects.Single(effect => effect.Type == 13).ReplaceShopGoodsResult!.ReplaceShopGoods;
                    AssertEqual(1, replacements.Count, "Shop replacement effect replaces one authored slot");
                    foreach (var replacement in replacements)
                        AssertEqual(replacement.Value.ItemInfo.InstanceId, test.Adventure.ShopData!.Goods[replacement.Key - 1].ItemInfo.InstanceId, "Replacement wire slots are one-based and match persisted goods");
                    break;
            }
            string entered = GodfallEconomySnapshot(test);
            test.Call(nameof(Theatre5EnterShopRequest));
            AssertEqual(entered, GodfallEconomySnapshot(test), "Shop reentry neither pays income nor retriggers relics");
            AssertEqual(0, GodfallEconomyEffects(test).Count, "Shop hydration contains no repeated effect queue");
        }
    }
    private static void ValidateGodfallUnboundEffectOperations()
    {
        // Operation-only synthetic fixtures: these real effect rows have no authored
        // relic binding. This does not claim natural reachability or positive RPC coverage.
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre5Module");
        Type mutationType = module.GetNestedType("Mutation", BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Missing Godfall mutation type.");
        MethodInfo apply = module.GetMethod("ApplyRelicEffect", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Missing Godfall effect operation.");
        foreach (int id in new[] { 10003, 10008, 10010, 10012 })
        {
            using GodfallCase test = GodfallEconomyFixture("unbound-operation-" + id);
            var row = TableReaderV2.Parse<Theatre5RelicEffectTable>().Single(row => row.Id == id);
            var runeRow = TableReaderV2.Parse<Theatre5ItemTable>().First(row => row.Type == 2 && row.Quality == 1);
            var rune = GodfallEconomyItem(test, runeRow.Id);
            test.Adventure.BagData.RuneDict[1] = rune;
            test.SaveFixture();
            object mutation = Activator.CreateInstance(mutationType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [test.Session, false], null) ?? throw new InvalidDataException("Cannot create effect fixture.");
            var state = (PlayerTheatre5State)mutationType.GetProperty("State")!.GetValue(mutation)!;
            Theatre5AdventureData adventure = state.Data.PvpAdventureData!;
            object?[] args = [mutation, row, new HashSet<int>(), false];
            var effect = (Theatre5Effect?)apply.Invoke(null, args)
                ?? throw new InvalidDataException("Authored effect unexpectedly had no target.");
            AssertEqual(row.Type, effect.Type, "Operation publishes authored effect discriminant");
            switch (row.Type)
            {
                case 3:
                    var added = effect.AddItemGroupEffectResult!.UpdateItem.Item;
                    AssertEqual(row.Param[0], added.ItemId, "Specified-item operation grants exact authored item");
                    AssertEqual(added.InstanceId, adventure.BagData.BagItemDict[1].InstanceId, "Specified item is visible in first empty bag slot");
                    AssertEqual(test.Adventure.GoldNum, adventure.GoldNum, "Item grant does not alter gold");
                    break;
                case 8:
                    AssertEqual(2, adventure.RuneAutoStrengthenCnt, "Auto-strengthen operation adds authored two charges");
                    AssertEqual(2, effect.AddAutoStrengthenCntResult!.NewAutoStrengthenCnt, "Auto-strengthen publishes absolute charge count");
                    break;
                case 10:
                    AssertEqual(false, adventure.BagData.RuneDict.ContainsKey(1), "Auto-sale removes the specified equipped slot");
                    AssertEqual(rune.InstanceId, effect.AutoSellRuneResult!.UpdateItem.Item.InstanceId, "Auto-sale identifies removed instance");
                    AssertEqual(Convert.ToInt32(runeRow.SellPrice), effect.AutoSellRuneResult.SellGold, "Auto-sale publishes exact gold delta");
                    AssertEqual(test.Adventure.GoldNum + Convert.ToInt32(runeRow.SellPrice), adventure.GoldNum, "Auto-sale conserves authored resale gold");
                    int gold = adventure.GoldNum;
                    AssertEqual<Theatre5Effect?>(null, (Theatre5Effect?)apply.Invoke(null, args), "Empty rune slot is a no-target operation");
                    AssertEqual(gold, adventure.GoldNum, "Repeated auto-sale cannot credit removed rune");
                    break;
                case 12:
                    AssertEqual(test.Adventure.Health + 1, adventure.Health, "HP operation adds authored one heart");
                    AssertEqual(adventure.Health, effect.ChangeHpEffectResult!.NewHp, "HP effect publishes absolute health");
                    break;
            }
        }
    }
    private static void ValidateGodfallClickPlacement()
    {
        foreach (bool skillChoice in new[] { false, true })
        foreach (bool equipped in new[] { false, true })
        {
            using GodfallCase test = GodfallEconomyFixture($"click-{skillChoice}-{equipped}");
            int itemId = TableReaderV2.Parse<Theatre5ItemTable>().First(row => row.Type == (skillChoice ? 1 : 2)
                && (row.CharacterId == null || !row.CharacterId.Any(id => id > 0) || row.CharacterId.Contains(test.Adventure.CharacterId))).Id;
            var item = GodfallEconomyItem(test, itemId);
            if (skillChoice)
            {
                test.Adventure.Status = 3;
                test.Adventure.SkillChoiceData = new() { SkillGroups = [item] };
            }
            else test.Adventure.ShopData!.Goods = [new() { ItemInfo = item }];
            test.SaveFixture();
            int capacity = !equipped ? test.Adventure.BagData.BagGridsNum
                : skillChoice ? test.Adventure.BagData.SkillGridsNum : test.Adventure.BagData.RuneGridsNum;
            string requestName = skillChoice ? nameof(XTheatre5SkillChoiceRequest) : nameof(XTheatre5ShopBuyItemRequest);
            object Request(int index) => skillChoice
                ? new XTheatre5SkillChoiceRequest { InstanceId = item.InstanceId, IsEquipped = equipped, TargetIndex = index }
                : new XTheatre5ShopBuyItemRequest { InstanceId = item.InstanceId, IsEquipped = equipped, TargetIndex = index };
            foreach (int invalid in new[] { 0, -2, capacity + 1 })
                GodfallEconomyReject(test, requestName, Request(invalid));
            int gold = test.Adventure.GoldNum;
            test.Call(requestName, Request(-1));
            var destination = !equipped ? test.Adventure.BagData.BagItemDict
                : skillChoice ? test.Adventure.BagData.SkillDict : test.Adventure.BagData.RuneDict;
            AssertEqual(item.InstanceId, destination[1].InstanceId, "Consumer click sentinel places exact instance in first free destination");
            int price = skillChoice ? 0 : Convert.ToInt32(TableReaderV2.Parse<Theatre5ItemTable>().Single(row => row.Id == itemId).Price);
            AssertEqual(gold - price, test.Adventure.GoldNum, "Click placement charges only the authored purchase cost");
        }
        using (GodfallCase full = GodfallEconomyFixture("skill-interruption-sell"))
        {
            full.Adventure.Status = 3;
            var skillRow = TableReaderV2.Parse<Theatre5ItemTable>().First(row => row.Type == 1
                && (row.CharacterId == null || !row.CharacterId.Any(id => id > 0) || row.CharacterId.Contains(full.Adventure.CharacterId)));
            for (int slot = 1; slot <= full.Adventure.BagData.SkillGridsNum; slot++)
                full.Adventure.BagData.SkillDict[slot] = GodfallEconomyItem(full, skillRow.Id);
            for (int slot = 1; slot <= full.Adventure.BagData.BagGridsNum; slot++)
                full.Adventure.BagData.BagItemDict[slot] = GodfallEconomyItem(full, skillRow.Id);
            var selected = GodfallEconomyItem(full, skillRow.Id);
            full.Adventure.SkillChoiceData = new() { SkillGroups = [selected] };
            full.SaveFixture();
            int occupiedSlot = full.Adventure.BagData.SkillGridsNum;
            int soldId = full.Adventure.BagData.SkillDict[occupiedSlot].InstanceId;
            int gold = full.Adventure.GoldNum;
            GodfallEconomyReject(full, nameof(XTheatre5ShopRefreshRequest));
            GodfallEconomyReject(full, nameof(XTheatre5ShopBuyItemRequest), new XTheatre5ShopBuyItemRequest
            { InstanceId = selected.InstanceId, IsEquipped = true, TargetIndex = -1 });
            GodfallEconomyReject(full, nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest
            { InstanceId = soldId, ItemType = 2, IsEquipped = true });
            full.Call(nameof(XTheatre5ShopSellItemRequest), new XTheatre5ShopSellItemRequest
            { InstanceId = soldId, ItemType = 1, IsEquipped = true });
            AssertEqual(gold + Convert.ToInt32(skillRow.SellPrice), full.Adventure.GoldNum, "Skill interruption sale credits exact owned skill value");
            AssertEqual(3, full.Adventure.Status, "Sale preserves pending skill interruption");
            AssertEqual(false, full.Adventure.BagData.SkillDict.ContainsKey(occupiedSlot), "Sale frees chosen equipped slot while bag stays full");
            full.Call(nameof(XTheatre5SkillChoiceRequest), new XTheatre5SkillChoiceRequest
            { InstanceId = selected.InstanceId, IsEquipped = true, TargetIndex = occupiedSlot });
            AssertEqual(selected.InstanceId, full.Adventure.BagData.SkillDict[occupiedSlot].InstanceId, "Pending skill can be chosen into the sold slot");
            AssertEqual(4, full.Adventure.Status, "Full inventory interruption resolves after selling and choosing");
            AssertEqual(0, full.Adventure.BagData.TempItemDict.Count, "Freed-slot skill choice creates no temporary overflow");
        }
        using GodfallCase natural = new("natural-skill-before-missions");
        if (natural.Data.PvpType != 1) natural.Call(nameof(PveOrPvpChangeRequest));
        natural.Call(nameof(Theatre5InitGameRequest), new Theatre5InitGameRequest { CharacterId = natural.Data.Characters.Keys.Min() });
        var enter = natural.Call(nameof(Theatre5EnterShopRequest));
        AssertEqual(3, natural.Adventure.Status, "First natural shop interrupts with skill choice");
        AssertEqual(0, natural.Adventure.ChooseMissions.Count, "Skill interruption has no persisted mission offers");
        AssertEqual(0, enter["ChooseMissions"]!.Count(), "EnterShop consumer receives no premature mission choices");
        var choice = natural.Call(nameof(XTheatre5SkillChoiceRequest), new XTheatre5SkillChoiceRequest
        { InstanceId = natural.Adventure.SkillChoiceData!.SkillGroups[0].InstanceId, IsEquipped = true, TargetIndex = -1 });
        AssertEqual(4, natural.Adventure.Status, "Skill click resolves interruption");
        var offered = choice["ChooseMissions"]!.ToObject<Dictionary<int, Theatre5Mission>>()!.First();
        natural.Call(nameof(Theatre5MissionChooseRequest), new Theatre5MissionChooseRequest { PositionId = offered.Key });
        AssertEqual(offered.Value.MissionId, natural.Adventure.Missioning!.MissionId, "Skill completion exposes an actionable mission alternative");
        AssertEqual(0, natural.Adventure.ChooseMissions.Count, "Accepting exposed mission closes alternatives");
    }
}
