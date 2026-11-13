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
        AssertEqual(true, existingGoods.All(id => materialGoods.Any(good => good.Value<uint>("Id") == id)),
            "Material Shop retains the existing goods");
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
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid),
                CreateDrawCompatibilityPlayer(uid), sessionId: "equipment-one-click-shop-test", startClientLoop: false);
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
