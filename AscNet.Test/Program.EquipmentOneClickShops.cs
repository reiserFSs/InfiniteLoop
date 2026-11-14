using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using ClientShop = AscNet.Common.MsgPack.GetShopInfoResponse.GetShopInfoResponseClientShop;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateEquipmentOneClickShops()
    {
        const uint materialShopId = 1422;
        const uint memoryShopId = 1423;
        const uint guildPersonalShopId = 4001;
        const uint guildProcurementShopId = 9998;
        const int guildGetShopHasNotGuild = 20063226;
        const long uid = 99_491;
        const long leaderUid = uid + 1;
        const uint guildId = 494;

        JObject catalog = JObject.Parse(File.ReadAllText(ResourcePath("Configs", "client_shops.json")));
        JObject materialShop = (JObject)catalog[materialShopId.ToString()]!;
        AssertEqual(materialShopId, materialShop.Value<uint>("Id"), "Material Shop catalog id");
        JArray materialGoods = (JArray)materialShop["GoodsList"]!;
        AssertEqual(materialGoods.Count, materialGoods.Select(good => good.Value<uint>("Id")).Distinct().Count(),
            "Material Shop goods IDs are unique");
        uint[] existingGoods = [142030, 142297, 142031, 142032, 142033, 142034, 142035, 142036,
            142037, 142038, 142039, 142040, 142041, 142042, 142043, 142044, 143341];
        // Simulator policy (AscNet, not retail): 4.9 EN ItemAutoExchange maps each Overclock item to shop 1422 and these goods.
        const int exchangeToken = 62738;
        (uint GoodsId, int ItemId, int Price)[] overclockOffers =
        [
            (147066, 40104, 35), (147067, 40103, 45), (147068, 40100, 30),
            (147069, 40114, 70), (147070, 40113, 90), (147071, 40110, 60)
        ];
        AssertIntegerList(existingGoods.Concat(overclockOffers.Select(offer => offer.GoodsId)).Select(id => (long)id).ToArray(),
            materialGoods.Select(good => (long)good.Value<uint>("Id")).ToArray(),
            "Material Shop retains all existing goods and adds the six client Auto Exchange routes");
        AssertEqual(true, materialShop.Value<string>("Provenance")!.Contains("not retail data"),
            "Material Shop labels the simulator offers as AscNet policy");
        foreach (JToken good in materialGoods)
        {
            uint id = good.Value<uint>("Id");
            JToken reward = good["RewardGoods"]!;
            AssertEqual((int)RewardType.Item, reward.Value<int>("RewardType"), $"Material Shop {id} reward type");
            AssertEqual(true, Inventory.IsValidClientItemId(reward.Value<int>("TemplateId")) && reward.Value<int>("Count") > 0,
                $"Material Shop {id} reward refers to a valid item and quantity");
            AssertEqual(true, good["ConsumeList"] is JArray { Count: > 0 } costs
                && costs.All(cost => Inventory.IsValidClientItemId(cost.Value<int>("Id")) && cost.Value<int>("Count") > 0),
                $"Material Shop {id} costs refer to valid items and quantities");
        }

        var originalGuilds = Guild.collection;
        var client = AscNet.Common.Common.db.Client;
        string databaseName = "ascnet_equipment_shop_test_" + Guid.NewGuid().ToString("N");
        Guild.collection = client.GetDatabase(databaseName).GetCollection<Guild>("guilds");
        try
        {
            using MongoCollectionOverride saves = MongoCollectionOverride.InstallForShopCompatibility();
            const long initialTokens = 600;
            var inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = exchangeToken, Count = initialTokens }]);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                CreateDrawCompatibilityPlayer(uid), inventory, sessionId: "equipment-one-click-shop-test", startClientLoop: false);
            harness.Session.stage = new Stage { Uid = uid, Stages = new() };
            int packetId = 49_200;
            GetFixedShopListResponse ListShops(params uint[]? ids)
            {
                InvokeRegisteredRequestHandler(nameof(GetFixedShopListRequest), harness.Session, ++packetId,
                    new GetFixedShopListRequest { IdList = ids?.ToList() });
                return ReadResponsePayload<GetFixedShopListResponse>(harness, packetId,
                    nameof(GetFixedShopListResponse), "Equipment shop preload");
            }

            GetFixedShopListResponse empty = ListShops(null);
            AssertEqual(0, empty.Code, "An empty Lua list succeeds");
            AssertEqual(0, empty.ClientShopList.Count, "An empty Lua list returns no shops");
            GetFixedShopListResponse weaponPreload = ListShops(materialShopId);
            AssertEqual(0, weaponPreload.Code, "Weapon preload succeeds");
            ClientShop weaponShop = weaponPreload.ClientShopList.Single();
            GetFixedShopListResponse memoryPreload = ListShops(materialShopId, guildPersonalShopId);
            AssertEqual(0, memoryPreload.Code, "Memory preload succeeds for a player outside the Command Bureau");
            ClientShop memoryMaterialShop = memoryPreload.ClientShopList.Single();
            AssertEqual(materialShopId, memoryMaterialShop.Id, "Memory preload returns only the accessible Material Shop");
            AssertIntegerList(materialGoods.Select(row => (long)row.Value<uint>("Id")).ToArray(),
                memoryMaterialShop.GoodsList.Select(row => (long)row.Id).ToArray(), "Memory preload returns every catalog good");
            AssertEqual(true, JToken.DeepEquals(JObject.FromObject(weaponShop), JObject.FromObject(memoryMaterialShop)),
                "Weapon and Memory preloads return the same Material Shop");
            BuyResponse Buy(uint shopId, uint goodsId, int count)
            {
                InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, ++packetId,
                    new BuyRequest { ShopId = shopId, GoodsId = goodsId, Count = count });
                return ReadResponsePayload<BuyResponse>(harness, packetId, nameof(BuyResponse), "Material Shop purchase", maxPacketsToRead: 8);
            }
            long Tokens() => inventory.Items.Single(item => item.Id == exchangeToken).Count;

            // The client resolves each Auto Exchange route from the cached goods: reward template, consume list, bundle count.
            foreach ((uint goodsId, int itemId, int price) in overclockOffers)
            {
                var goods = memoryMaterialShop.GoodsList.Single(row => row.Id == goodsId);
                AssertEqual(((uint)itemId, 4), (goods.RewardGoods.TemplateId, goods.RewardGoods.Count), $"Offer {goodsId} bundle");
                AssertEqual((exchangeToken, (uint)price), (goods.ConsumeList.Single().Id, goods.ConsumeList.Single().Count),
                    $"Offer {goodsId} price");
                AssertEqual((0, 0, 0, 0, 0, 0), (goods.BuyTimesLimit, goods.OnSaleTime, goods.SelloutTime,
                    goods.AutoResetClockId, goods.RefreshTime, goods.TotalBuyTimes), $"Offer {goodsId} availability and initial counters");
                AssertEqual(0, goods.ConditionIds.Count, $"Offer {goodsId} has no unlock requirements");

                long before = Tokens();
                BuyResponse purchaseResult = Buy(materialShopId, goodsId, 1);
                AssertEqual(0, purchaseResult.Code, $"Offer {goodsId} purchase succeeds");
                AssertEqual((itemId, 4), (purchaseResult.GoodList.Single().TemplateId, purchaseResult.GoodList.Single().Count),
                    $"Offer {goodsId} grants one bundle");
                AssertEqual(4L, inventory.Items.Single(item => item.Id == itemId).Count, $"Offer {goodsId} inventory credit");
                AssertEqual(before - price, Tokens(), $"Offer {goodsId} exact debit");
            }
            long tokensLeft = initialTokens - overclockOffers.Sum(offer => offer.Price);
            AssertEqual(overclockOffers.Length + 1, inventory.Items.Count, "Exchanges grant only the purchased items");
            // Insufficient currency (a 100-bundle costs more than the balance) and an unavailable offer.
            foreach ((uint goodsId, int count) in new[] { (147066U, 100), (147072U, 1) })
            {
                BuyResponse rejected = Buy(materialShopId, goodsId, count);
                AssertEqual(1, rejected.Code, "Insufficient funds or an unavailable offer reject a purchase");
                AssertEqual(0, rejected.GoodList.Count, "Rejected purchases grant no items");
            }
            AssertEqual(tokensLeft, Tokens(), "Rejected purchases preserve the balance");
            ClientShop updatedShop = ListShops(materialShopId).ClientShopList.Single();
            AssertEqual(overclockOffers.Length, updatedShop.TotalBuyTimes, "Shop purchase count comes from player state");
            foreach ((uint goodsId, _, _) in overclockOffers)
                AssertEqual(1, updatedShop.GoodsList.Single(row => row.Id == goodsId).TotalBuyTimes, $"Offer {goodsId} purchase count");

            // Limit-reached: the generic purchase path (shared with 1422) stops at BuyTimesLimit; 1421 goods 147078 has limit 20.
            const uint limitedShop = 1421, limitedGoods = 147078;
            int limit = catalog[limitedShop.ToString()]!["GoodsList"]!.Single(good => good.Value<uint>("Id") == limitedGoods).Value<int>("BuyTimesLimit");
            int limitPrice = catalog[limitedShop.ToString()]!["GoodsList"]!.Single(good => good.Value<uint>("Id") == limitedGoods)["ConsumeList"]![0]!.Value<int>("Count");
            harness.Session.player.ShopBuyTimes[limitedGoods] = limit - 1;
            AssertEqual(0, Buy(limitedShop, limitedGoods, 1).Code, "The last allowed purchase succeeds");
            AssertEqual(tokensLeft - limitPrice, Tokens(), "The last allowed purchase is debited exactly");
            BuyResponse limited = Buy(limitedShop, limitedGoods, 1);
            AssertEqual((1, 0), (limited.Code, limited.GoodList.Count), "A purchase past BuyTimesLimit is rejected");
            AssertEqual(tokensLeft - limitPrice, Tokens(), "A limit-reached purchase preserves the balance");

            GetFixedShopListResponse repeated = ListShops(guildPersonalShopId, materialShopId, materialShopId, memoryShopId);
            AssertEqual(0, repeated.Code, "A mixed preload succeeds in either request order");
            AssertIntegerList([materialShopId, memoryShopId], repeated.ClientShopList.Select(shop => (long)shop.Id).ToArray(),
                "Public shops are returned once in request order");
            AssertEqual(((JArray)catalog[memoryShopId.ToString()]!["GoodsList"]!).Count,
                repeated.ClientShopList[1].GoodsList.Count, "Memory Shop keeps its full catalog");

            GetFixedShopListResponse guildOnly = ListShops(guildPersonalShopId);
            AssertEqual(guildGetShopHasNotGuild, guildOnly.Code, "Guild-only preload still requires membership");
            AssertEqual(0, guildOnly.ClientShopList.Count, "No personal guild catalog for a nonmember");
            AssertEqual(guildGetShopHasNotGuild, ListShops(guildPersonalShopId, 0).Code,
                "An empty catalog fallback cannot turn a denied request into success");
            AssertEqual(guildGetShopHasNotGuild, ListShops(materialShopId, guildProcurementShopId).Code,
                "Guild procurement keeps its batch access error");

            // A Theatre 5 shop is unavailable outside its mode: a failure that is not the guild code.
            uint failingShop = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.client.theatre5.Theatre5TaskShopTable>()
                .First(row => row.Type == 1).ShopId!.Value is int theatreShop ? (uint)theatreShop : 0;
            int unknownShopCode = ListShops(failingShop).Code;
            AssertEqual(true, unknownShopCode != 0 && unknownShopCode != guildGetShopHasNotGuild, "Unavailable shop code differs from the guild code");
            AssertEqual(unknownShopCode, ListShops(failingShop, guildPersonalShopId).Code,
                "The omitted guild shop does not mask another failure in the batch");
            AssertEqual(unknownShopCode, ListShops(guildPersonalShopId, failingShop, materialShopId).Code,
                "A failing shop fails the whole batch in any request order");

            InvokeRegisteredRequestHandler(nameof(GetShopInfoRequest), harness.Session, ++packetId,
                new GetShopInfoRequest { Id = guildPersonalShopId });
            GetShopInfoResponse direct = ReadResponsePayload<GetShopInfoResponse>(harness, packetId,
                nameof(GetShopInfoResponse), "Direct guild shop request");
            AssertEqual(guildGetShopHasNotGuild, direct.Code, "Direct guild shop still requires membership");

            uint guildGood = catalog[guildPersonalShopId.ToString()]!["GoodsList"]!.First!.Value<uint>("Id");
            InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, ++packetId,
                new BuyRequest { ShopId = guildPersonalShopId, GoodsId = guildGood, Count = 1 });
            BuyResponse purchase = ReadResponsePayload<BuyResponse>(harness, packetId, nameof(BuyResponse), "Nonmember purchase");
            AssertEqual(20063228, purchase.Code, "Guild purchase still requires membership");
            AssertEqual(0, purchase.GoodList.Count, "Rejected guild purchase grants nothing");

            Guild guild = new() { Id = guildId, Name = "Equipment test", LeaderId = leaderUid, Active = true, MemberIds = [leaderUid, uid] };
            Guild.collection.InsertOne(guild);
            GetFixedShopListResponse memberPreload = ListShops(materialShopId, guildPersonalShopId);
            AssertEqual(0, memberPreload.Code, "Memory preload succeeds for a member");
            AssertIntegerList([materialShopId, guildPersonalShopId], memberPreload.ClientShopList.Select(shop => (long)shop.Id).ToArray(),
                "Member receives both requested shops");
            AssertEqual(((JArray)catalog[guildPersonalShopId.ToString()]!["GoodsList"]!).Count,
                memberPreload.ClientShopList[1].GoodsList.Count, "Member retains the personal guild catalog");
        }
        finally
        {
            Guild.collection = originalGuilds;
            client.DropDatabase(databaseName);
        }
    }
}
