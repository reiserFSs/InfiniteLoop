using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidatePurchaseCatalog()
    {
        JObject Definition(int id, int ui, int price, long start = 0, long end = 0, long invalid = 0) => new()
        {
            ["Id"] = id, ["UiType"] = ui, ["ConsumeId"] = 5, ["ConsumeCount"] = price,
            ["TimeToShelve"] = start, ["TimeToUnShelve"] = end, ["TimeToInvalid"] = invalid,
            ["RewardGoodsList"] = new JArray(new JObject { ["TemplateId"] = 2, ["Count"] = 1, ["RewardType"] = 1 })
        };
        string Json(params JObject[] entries) => new JObject
        { ["SchemaVersion"] = 1, ["Purchases"] = new JArray(entries) }.ToString();
        JObject offer = Definition(1, 5, 240);
        offer["NormalDiscounts"] = new JObject { ["1"] = 7000, ["3"] = 5000 };
        JObject disabled = Definition(5, 8, 5);
        disabled["Enabled"] = false;
        PurchaseCatalog catalog = PurchaseCatalog.Parse(Json(offer, Definition(2, 8, 28, start: 100),
            Definition(3, 8, 40, end: 200), Definition(4, 8, 68, invalid: 150), disabled));
        AssertEqual(4, catalog.List(null, 99).Count, "all enabled entries include future countdowns");
        AssertEqual(1, catalog.List([5, 5], 99).Count, "duplicate requested tabs do not duplicate packages");
        AssertEqual(0, catalog.List([999], 99).Count, "unknown tabs do not fall back to unrelated snapshots");
        AssertEqual(0, catalog.List([], 99).Count, "empty tab request returns empty list");
        AssertEqual(2, catalog.List([8], 150).Count, "invalid time removes package at the exact boundary");
        AssertEqual(1, catalog.List([8], 200).Count, "unshelve removes package at the exact boundary");
        AssertEqual(true, catalog.Find(5) is null, "disabled catalog offer cannot be purchased");
        AssertEqual(20053002, PurchaseCatalog.AvailabilityCode(catalog.Find(2)!, 99), "future purchase denied");
        AssertEqual(0, PurchaseCatalog.AvailabilityCode(catalog.Find(2)!, 100), "purchase opens at start");
        AssertEqual(20053003, PurchaseCatalog.AvailabilityCode(catalog.Find(4)!, 150), "configured expiration is enforced");
        AssertEqual(20053004, PurchaseCatalog.AvailabilityCode(catalog.Find(3)!, 200), "configured unshelve is enforced");
        AssertEqual(168, PurchaseCatalog.UnitPrice(catalog.Find(1)!, 0), "initial discount price");
        AssertEqual(120, PurchaseCatalog.UnitPrice(catalog.Find(1)!, 2), "purchase count selects later discount tier");
        offer["ConsumeCount"] = 100;
        PurchaseCatalog repriced = PurchaseCatalog.Parse(Json(offer));
        AssertEqual(70, PurchaseCatalog.UnitPrice(repriced.Find(1)!, 0), "server catalog change adjusts debit");
        AssertEqual(100, Convert.ToInt32((object)repriced.Find(1)!["ConvertSwitch"]), "client base price follows edited catalog");
        Dictionary<dynamic, dynamic> copy = catalog.Find(1)!;
        copy["BuyTimes"] = 9;
        ((Dictionary<dynamic, dynamic>)((List<dynamic>)copy["RewardGoodsList"])[0])["Count"] = 99;
        AssertEqual(false, catalog.Find(1)!.ContainsKey("BuyTimes"), "request state never enters catalog");
        AssertEqual(1, Convert.ToInt32((object)((Dictionary<dynamic, dynamic>)
            ((List<dynamic>)catalog.Find(1)!["RewardGoodsList"])[0])["Count"]), "nested reward data is isolated");
        void Reject(string json, string name)
        {
            try { PurchaseCatalog.Parse(json); }
            catch (InvalidDataException) { return; }
            throw new InvalidDataException($"Catalog must reject {name}");
        }
        Reject(Json(Definition(1, 5, 1), Definition(1, 8, 2)), "duplicate package IDs");
        JObject playerState = Definition(1, 5, 1);
        playerState["BuyTimes"] = 1;
        Reject(Json(playerState), "player state in static configuration");
        Reject("{\"Responses\":{}}", "legacy response snapshots");
        ValidateCatalogPurchaseResponses();
        Console.WriteLine("Purchase catalog, schedule, pricing and player isolation checks passed.");
    }

    private static void ValidateCatalogPurchaseResponses()
    {
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        const long uid = 468680;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.HongKa, Count = 2000 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "purchase-catalog");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int sequence = 468680;
        JArray List(params int[] uiTypes)
        {
            int packet = sequence++;
            InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, packet,
                new GetPurchaseListRequest { UiTypeList = uiTypes.ToList() });
            var response = ReadResponsePayload<GetPurchaseListResponse>(harness, packet, nameof(GetPurchaseListResponse), "catalog list");
            AssertEqual(0, response.Code, "catalog response succeeds");
            return JArray.Parse(MessagePack.MessagePackSerializer.ConvertToJson(
                MessagePack.MessagePackSerializer.Serialize(response.PurchaseInfoList)));
        }
        PurchaseResponse Buy(uint id, int tab)
        {
            int packet = sequence++;
            InvokeRegisteredRequestHandler(nameof(PurchaseRequest), harness.Session, packet,
                new PurchaseRequest { Id = id, Count = 1, DiscountId = -1, UiTypeList = [tab] });
            return ReadResponsePayload<PurchaseResponse>(harness, packet, nameof(PurchaseResponse), "catalog purchase", maxPacketsToRead: 64);
        }
        JArray first = List(5, 8, 5);
        AssertEqual(first.Count, first.Select(row => row.Value<int>("Id")).Distinct().Count(), "wire list IDs are unique");
        AssertEqual(true, first.All(row => row.Value<int>("UiType") is 5 or 8), "wire list respects requested tabs");
        foreach (uint id in new uint[] { 10007, 2059, 2074 })
            AssertEqual(0L, first.Single(row => row.Value<uint>("Id") == id).Value<long>("TimeToInvalid"),
                "local catalog has no inherited retail sales deadline");
        AssertEqual(0, Buy(10007, 5).Code, "previously expired seven-day package can be purchased");
        AssertEqual(0, Buy(2059, 5).Code, "second previously expired package can be purchased");
        AssertEqual(0, Buy(2074, 8).Code, "discounted coating item can be purchased");
        AssertEqual(1820L, inventory.Items.Single(item => item.Id == Inventory.HongKa).Count,
            "debits equal displayed 24 + 128 + discounted 28");
        AssertEqual(20053005, Buy(2074, 8).Code, "purchase limit remains enforced");
        AssertEqual(7L, player.PurchaseDailyPasses[10007].EndDay - player.PurchaseDailyPasses[10007].StartDay,
            "removing sales deadline does not remove sign-in entitlement duration");
        foreach (uint id in new uint[] { 2062, 2068 })
        {
            AssertEqual(0, Buy(id, 5).Code, "daily-mail-only package can be purchased");
            AssertEqual(10L, player.PurchaseDailyPasses[id].EndDay
                - (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 25200) / 86400,
                "configured ten-day term survives unlimited sales window");
        }
        AssertEqual(1722L, inventory.Items.Single(item => item.Id == Inventory.HongKa).Count,
            "daily-mail-only packs charge their configured 68 and 30");
        JToken firstBonus = List(3).Single(row => row.Value<uint>("Id") == 101)["FirstRewardGoods"]!;
        AssertEqual(true, firstBonus.Type == JTokenType.Object, "unconsumed first-purchase bonus is advertised");
        PurchaseResponse firstExchange = Buy(101, 3);
        AssertEqual(0, firstExchange.Code, "first exchange purchase succeeds");
        AssertEqual(true, firstExchange.RewardList.Any(reward => reward.TemplateId == 3 && reward.Count == 50),
            "first exchange purchase grants its first-purchase bonus");
        JToken? consumedBonus = List(3).Single(row => row.Value<uint>("Id") == 101)["FirstRewardGoods"];
        AssertEqual(true, consumedBonus is null || consumedBonus.Type == JTokenType.Null,
            "consumed first-purchase bonus is no longer advertised");
        PurchaseResponse repeatExchange = Buy(101, 3);
        AssertEqual(0, repeatExchange.Code, "repeat exchange purchase succeeds");
        AssertEqual(false, repeatExchange.RewardList.Any(reward => reward.TemplateId == 3),
            "repeat exchange purchase does not grant the first-purchase bonus again");
        JArray purchased = List(5, 8);
        AssertEqual(1, purchased.Single(row => row.Value<int>("Id") == 2059).Value<int>("BuyTimes"), "buy response state persists");
        harness.Session.player = CreateDrawCompatibilityPlayer(uid + 1);
        JArray fresh = List(5, 8);
        AssertEqual(0, fresh.Single(row => row.Value<int>("Id") == 2059).Value<int>("BuyTimes"), "second player has independent count");
        AssertEqual(0L, fresh.Single(row => row.Value<int>("Id") == 2059).Value<long>("LastBuyTime"), "second player has independent timestamp");
        harness.Session.player = player;
    }
}
