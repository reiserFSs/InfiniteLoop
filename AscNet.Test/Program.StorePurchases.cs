using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.item;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateStorePurchases()
    {
        ValidatePurchaseCatalog();
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        const long uid = 468600;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [new Item { Id = Inventory.HongKa, Count = 2000 }, new Item { Id = Inventory.FreeGem, Count = 2000 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "store-purchase");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 468600;
        PurchaseResponse Buy(uint id, object? param = null)
        {
            int sequence = packetId++;
            InvokeRegisteredRequestHandler(nameof(PurchaseRequest), harness.Session, sequence,
                new PurchaseRequest { Id = id, Count = 1, DiscountId = -1, UiTypeList = [15],
                    Param = param ?? new Dictionary<string, object> { ["FromMsg"] = 2 } });
            return ReadResponsePayload<PurchaseResponse>(harness, sequence, nameof(PurchaseResponse), "store purchase", maxPacketsToRead: 64);
        }
        PayInitiatedResponse Recharge(string key)
        {
            int sequence = packetId++;
            InvokeRegisteredRequestHandler(nameof(PayInitiatedRequest), harness.Session, sequence,
                new PayInitiatedRequest { Key = key });
            return ReadResponsePayload<PayInitiatedResponse>(harness, sequence, nameof(PayInitiatedResponse), "recharge", maxPacketsToRead: 32);
        }
        PurchaseGetDailyRewardResponse Claim(uint id)
        {
            int sequence = packetId++;
            InvokeRegisteredRequestHandler(nameof(PurchaseGetDailyRewardRequest), harness.Session, sequence,
                new PurchaseGetDailyRewardRequest { Id = id });
            return ReadResponsePayload<PurchaseGetDailyRewardResponse>(harness, sequence, nameof(PurchaseGetDailyRewardResponse), "daily claim", maxPacketsToRead: 32);
        }
        int LoginMonthlyRemaining(uint id)
        {
            var data = (System.Collections.IEnumerable)typeof(PurchaseRequest).Assembly
                .GetType("AscNet.GameServer.Handlers.AccountModule")!
                .GetMethod("BuildPurchaseClientInfoLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { player })!;
            var typed = data.Cast<GetPurchaseListResponse.GetPurchaseListResponsePurchaseInfo>().ToList();
            string json = MessagePack.MessagePackSerializer.ConvertToJson(MessagePack.MessagePackSerializer.Serialize(typed));
            var item = Newtonsoft.Json.Linq.JArray.Parse(json).Single(row => row.Value<uint>("Id") == id);
            if (item["BuyLimitRemainDay"] is null) throw new InvalidDataException("Login monthly pass omits BuyLimitRemainDay");
            return item.Value<int>("BuyLimitRemainDay");
        }
        AssertEqual(0, LoginMonthlyRemaining(83028), "login includes zero monthly renewal days before purchase");
        long Balance(int id) => inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        ItemBuyAssetResponse Exchange(int itemId, int times, int currency = 0)
        {
            int seq = packetId++;
            InvokeRegisteredRequestHandler(nameof(ItemBuyAssetRequest), harness.Session, seq,
                new ItemBuyAssetRequest { ItemId = itemId, Times = times, ConsumeId = currency });
            return ReadResponsePayload<ItemBuyAssetResponse>(harness, seq, nameof(ItemBuyAssetResponse), "asset conversion", maxPacketsToRead: 32);
        }
        inventory.Items.Add(new Item { Id = 2, Count = 100 });
        inventory.Items.Single(i => i.Id == 3).Count = 50;
        AssertEqual(0, Exchange(50002, 120).Code, "omitted currency uses recipe and pooled black cards");
        AssertEqual(0L, Balance(2), "free balance spent first");
        AssertEqual(30L, Balance(3), "remainder deducted from paid balance");
        AssertEqual(120L, Balance(50002), "event tickets granted exactly");
        AssertEqual(0, Exchange(50000, 20, 3).Code, "basic tickets explicit currency");
        AssertEqual(10L, Balance(3), "basic tickets deduct black cards");
        AssertEqual(20012004, Exchange(50000, 11).Code, "insufficient cards reject conversion");
        AssertEqual(20012001, Exchange(50000, 1, 5).Code, "forged currency rejected");
        AssertEqual(20012001, Exchange(50000, -1).Code, "negative conversion rejected");
        AssertEqual(20012001, Exchange(50000, int.MaxValue).Code, "oversized conversion rejected");
        AssertEqual(20L, Balance(50000), "failed conversions grant nothing");
        inventories.ThrowOnReplaceOne = true;
        AssertEqual(2, Exchange(50000, 1).Code, "failed save rejects exchange");
        inventories.ThrowOnReplaceOne = false;
        AssertEqual(10L, Balance(3), "failed save restores black cards");
        AssertEqual(20L, Balance(50000), "failed save restores tickets");
        inventory.Items.Single(i => i.Id == 2).Count = 50;
        inventory.Items.Single(i => i.Id == 3).Count = 0;
        int dormBuy = packetId++;
        InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, dormBuy,
            new BuyRequest { ShopId = 1011001, GoodsId = 801550, Count = 1 });
        var dormResult = ReadResponsePayload<BuyResponse>(harness, dormBuy, nameof(BuyResponse), "dorm black card purchase", maxPacketsToRead: 64);
        AssertEqual(0, dormResult.Code, "dorm furniture accepts pooled black cards");
        AssertEqual(20L, Balance(2), "dorm deducts configured 30 black cards");
        int rejectedDorm = packetId++;
        InvokeRegisteredRequestHandler(nameof(BuyRequest), harness.Session, rejectedDorm,
            new BuyRequest { ShopId = 1011001, GoodsId = 801550, Count = 1 });
        AssertEqual(1, ReadResponsePayload<BuyResponse>(harness, rejectedDorm, nameof(BuyResponse), "unaffordable furniture").Code,
            "dorm rejects insufficient pooled currency");
        AssertEqual(20L, Balance(2), "failed dorm purchase does not debit");
        inventory.Items.Single(i => i.Id == 3).Count = 2000;
        long cardsBeforeResources = Balance(2) + Balance(3);
        AssertEqual(0, Exchange(1, 2).Code, "coin exchange preserves price ladder");
        AssertEqual(7L * (9915 + 80 * 85), Balance(1), "coin yield follows player level");
        AssertEqual(cardsBeforeResources - 30, Balance(2) + Balance(3), "coin ladder spends 10 plus 20");
        AssertEqual(0, Exchange(4, 2).Code, "serum exchange preserves price ladder");
        AssertEqual(125L, Balance(4), "serum ladder yields 60 plus 65");
        AssertEqual(20012001, Exchange(4, 9).Code, "serum daily purchase cap enforced");
        Dictionary<int, BuyAssetTable> dailyAssets = TableReaderV2.Parse<BuyAssetTable>()
            .Where(row => row.Id is Inventory.Coin or Inventory.ActionPoint)
            .ToDictionary(row => row.Id);
        Item staleCogs = inventory.Items.Single(item => item.Id == Inventory.Coin);
        Item staleSerum = inventory.Items.Single(item => item.Id == Inventory.ActionPoint);
        staleCogs.BuyTimes = dailyAssets[Inventory.Coin].DailyLimit;
        staleSerum.BuyTimes = dailyAssets[Inventory.ActionPoint].DailyLimit;
        staleCogs.TotalBuyTimes = staleCogs.BuyTimes + 7;
        staleSerum.TotalBuyTimes = staleSerum.BuyTimes + 11;
        int cogsLifetimeBuys = staleCogs.TotalBuyTimes, serumLifetimeBuys = staleSerum.TotalBuyTimes;
        long yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        staleCogs.LastBuyTime = yesterday;
        staleSerum.LastBuyTime = yesterday;
        NotifyLogin nextDayLogin = (NotifyLogin)typeof(PurchaseRequest).Assembly
            .GetType("AscNet.GameServer.Handlers.AccountModule")!
            .GetMethod("BuildNotifyLogin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [harness.Session])!;
        AssertEqual(0, nextDayLogin.ItemList.Single(item => item.Id == Inventory.Coin).BuyTimes,
            "next-day login resets exhausted Cogs purchases");
        AssertEqual(0, nextDayLogin.ItemList.Single(item => item.Id == Inventory.ActionPoint).BuyTimes,
            "next-day login resets exhausted serum purchases");
        AssertEqual(cogsLifetimeBuys, staleCogs.TotalBuyTimes, "Cogs lifetime purchases survive daily reset");
        AssertEqual(serumLifetimeBuys, staleSerum.TotalBuyTimes, "serum lifetime purchases survive daily reset");
        Inventory persistedDailyReset = BsonSerializer.Deserialize<Inventory>(inventories.LastReplacement!.ToBson());
        AssertEqual(0, persistedDailyReset.Items.Single(item => item.Id == Inventory.Coin).BuyTimes,
            "next-day Cogs reset is persisted before login projection");
        AssertEqual(0, persistedDailyReset.Items.Single(item => item.Id == Inventory.ActionPoint).BuyTimes,
            "next-day serum reset is persisted before login projection");
        AssertEqual(0, Exchange(1, 1).Code, "next-day login enables another Cogs purchase");
        AssertEqual(0, Exchange(4, 1).Code, "next-day login enables another serum purchase");
        AssertEqual(20053031, Buy(90943, new Dictionary<string, object> { ["Unknown"] = 1 }).Code, "unknown metadata rejected");
        AssertEqual(0, Buy(90943).Code, "Store FromMsg permits free daily pack");
        long serum = Balance(90031);
        AssertEqual(20053005, Buy(90943).Code, "daily limit enforced");
        AssertEqual(serum, Balance(90031), "daily repeat never grants");
        player.PurchaseLastBuyTimes[90943] -= 86400;
        AssertEqual(0, Buy(90943).Code, "next day restores daily purchase");
        AssertEqual(serum + 1, Balance(90031), "reset purchase has distinct durable receipt");
        AssertEqual(20053005, Buy(90943).Code, "reset period still limited to one");
        AssertEqual(0, Buy(90289).Code, "weekly supply accepts reset config");
        AssertEqual(20053005, Buy(90289).Code, "weekly supply limit");
        player.PurchaseLastBuyTimes[90289] -= 7 * 86400;
        AssertEqual(0, Buy(90289).Code, "weekly supply resets");
        AssertEqual(0, Buy(19).Code, "monthly reset supply succeeds");
        player.PurchaseLastBuyTimes[19] -= 32 * 86400;
        AssertEqual(0, Buy(19).Code, "monthly reset supply renews");
        AssertEqual(0, Buy(1968).Code, "beginner limited supply grants equipment");
        AssertEqual(20053005, Buy(1968).Code, "limited supply cannot be bought twice");
        long savedRc = Balance(Inventory.HongKa);
        inventory.Items.Single(item => item.Id == Inventory.HongKa).Count = 0;
        AssertEqual(20012004, Buy(25).Code, "insufficient RC rejects store debit");
        inventory.Items.Single(item => item.Id == Inventory.HongKa).Count = savedRc;
        long beforeThreeDay = Balance(Inventory.HongKa);
        AssertEqual(0, Buy(10004).Code, "beginner three-day sign-in purchase succeeds");
        AssertEqual(beforeThreeDay - 12, Balance(Inventory.HongKa), "three-day package price");
        AssertEqual(3, checked((int)(player.PurchaseDailyPasses[10004].EndDay - player.PurchaseDailyPasses[10004].StartDay)), "three-day duration");
        Dictionary<dynamic, dynamic> signPackage = PurchaseCatalog
            .Load(JsonSnapshot.ResolvePath("Configs/client_purchases.json")).Find(10004)!;
        var signInfo = (Dictionary<dynamic, dynamic>)signPackage["PurchaseSignInInfo"];
        int firstRewardId = Convert.ToInt32((object)((List<dynamic>)signInfo["PurchaseSignInRewardInfos"])[0]);
        HashSet<int> firstRewardGoodsIds = TableReaderV2.Parse<RewardTable>()
            .Single(reward => reward.Id == firstRewardId).SubIds.ToHashSet();
        HashSet<int> itemIds = TableReaderV2.Parse<ItemTable>().Select(item => item.Id).ToHashSet();
        RewardGoodsTable firstItemReward = TableReaderV2.Parse<RewardGoodsTable>()
            .Single(reward => firstRewardGoodsIds.Contains(reward.Id) && itemIds.Contains(reward.TemplateId));
        ItemTable firstItemTable = TableReaderV2.Parse<ItemTable>()
            .Single(item => item.Id == firstItemReward.TemplateId);
        Item capacityItem = inventory.Items.FirstOrDefault(item => item.Id == firstItemReward.TemplateId)
            ?? inventory.Do(firstItemReward.TemplateId, 0);
        capacityItem.Count = Inventory.GetMaxCount(firstItemTable) - firstItemReward.Count + 1;
        long capacityBefore = capacityItem.Count;
        long claimDayBefore = player.PurchaseDailyPasses[10004].LastClaimDay;
        int claimReceiptsBefore = inventory.AppliedRewardClaims.Count;
        AssertEqual(20027011, Claim(10004).Code, "daily claim rejects a reward that would exceed item capacity");
        AssertEqual(capacityBefore, capacityItem.Count, "capacity rejection grants no truncated reward");
        AssertEqual(claimDayBefore, player.PurchaseDailyPasses[10004].LastClaimDay,
            "capacity rejection does not consume the daily entitlement");
        AssertEqual(0, player.PurchaseDailyPasses[10004].RewardIndexList.Count,
            "capacity rejection does not advance sign-in progress");
        AssertEqual(claimReceiptsBefore, inventory.AppliedRewardClaims.Count,
            "capacity rejection does not write a reward receipt");
        capacityItem.Count--;
        AssertEqual(0, Claim(10004).Code, "first sign-in reward");
        AssertEqual(Inventory.GetMaxCount(firstItemTable), Balance(firstItemReward.TemplateId),
            "daily claim grants the complete reward after capacity is available");
        AssertEqual(0, Claim(10004).RewardList.Count, "same-day sign-in is idempotent");
        AssertEqual(true, player.PurchaseDailyPasses[10004].RewardIndexList.SequenceEqual(new[] { 1 }), "sign-in index saved");
        var signNotify = new PurchaseDailyNotify();
        typeof(PurchaseRequest).Assembly.GetType("AscNet.GameServer.Handlers.PayModule")!
            .GetMethod("AddSignInNotifications", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { signNotify, player });
        AssertEqual(1, signNotify.PurchaseSignInInfoList.Count, "active sign-in included at login");
        AssertEqual(0, Buy(1969).Code, "monthly companion bundle succeeds");
        AssertEqual(true, player.PurchaseDailyPasses.ContainsKey(83028), "companion bundle activates monthly pass");
        AssertEqual(30, LoginMonthlyRemaining(83028), "login includes purchased monthly renewal days");
        long end = player.PurchaseDailyPasses[83028].EndDay;
        AssertEqual(30L, end - (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 25200) / 86400, "monthly duration follows client config");
        AssertEqual(0, Buy(83028).Code, "monthly renewal succeeds");
        AssertEqual(end + 30, player.PurchaseDailyPasses[83028].EndDay, "renewal extends existing term");
        AssertEqual(60, LoginMonthlyRemaining(83028), "login renewal days track extended term");
        long blackCards = Balance(Inventory.FreeGem);
        AssertEqual(0, Claim(83028).Code, "monthly daily claim succeeds");
        AssertEqual(blackCards + 100, Balance(Inventory.FreeGem), "daily Black Cards delivered");
        AssertEqual(0, Claim(83028).RewardList.Count, "second daily claim is idempotent");
        long passDay = player.PurchaseDailyPasses[83028].LastClaimDay;
        player.PurchaseDailyPasses[83028].LastClaimDay--;
        AssertEqual(0, Claim(83028).Code, "stale daily claim state recovers using receipt");
        AssertEqual(blackCards + 100, Balance(Inventory.FreeGem), "stale claim timestamp cannot duplicate daily grant");
        player.PurchaseDailyPasses[83028].EndDay = passDay;
        AssertEqual(20053031, Claim(83028).Code, "expired pass cannot claim");
        player.PurchaseDailyPasses[83028].EndDay = end + 30;
        AssertEqual(20053101, Buy(90032).Code, "active alternative monthly pass excluded");
        AssertEqual(0, Buy(90291).Code, "serum monthly pass purchase succeeds");
        int mails = player.Mails.Count;
        AssertEqual(true, mails > 0, "mail-based daily benefit delivered to inbox");
        AssertEqual(true, player.Mails.Any(mail => mail.RewardGoodsList?.Any(g => g.TemplateId == 90031 && g.Count == 2) == true), "monthly mail has configured serum");
        int listPacket = packetId++;
        InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, listPacket,
            new GetPurchaseListRequest { UiTypeList = [2] });
        ReadResponsePayload<GetPurchaseListResponse>(harness, listPacket, nameof(GetPurchaseListResponse), "mail refresh");
        AssertEqual(mails, player.Mails.Count, "daily mail is not duplicated");
        long rc = Balance(Inventory.HongKa);
        AssertEqual(20053031, Recharge("PayWin999999").Code, "unknown recharge cannot grant guessed amount");
        foreach (var (key, amount) in new[] { ("PayWin5", 5), ("PayWin28", 28), ("PayWin34", 34),
                     ("PayWin59", 59), ("PayWin71", 71), ("PayWin119", 119), ("PayWin299", 299), ("PayWin600", 600) })
        {
            PayInitiatedResponse response = Recharge(key);
            AssertEqual(0, response.Code, "known recharge succeeds");
            AssertEqual(true, response.LocalCompleted, "client bypasses external SDK only on local completion");
            AssertEqual(amount, response.RewardList.Single().Count, "exact tier returned");
            rc += amount;
            AssertEqual(rc, Balance(Inventory.HongKa), "correct RC inventory entry updated");
        }
        inventories.ThrowOnReplaceOne = true;
        AssertEqual(2, Recharge("PayWin28").Code, "failed recharge inventory save is retryable");
        inventories.ThrowOnReplaceOne = false;
        AssertEqual(rc, Balance(Inventory.HongKa), "failed save cannot grant in memory");
        characters.ThrowOnReplaceOne = true;
        AssertEqual(2, Recharge("PayWin28").Code, "late persistence failure retains receipt");
        characters.ThrowOnReplaceOne = false;
        AssertEqual(rc + 28, Balance(Inventory.HongKa), "durable inventory credited once");
        harness.Session.player = BsonSerializer.Deserialize<Player>(players.LastReplacement!.ToBson());
        AssertEqual(20053031, Recharge("PayWin600").Code, "pending tier prevents overlapping recharge");
        AssertEqual(0, Recharge("PayWin28").Code, "reloaded pending recharge resumes");
        AssertEqual(rc + 28, Balance(Inventory.HongKa), "recovery never double credits");
        AssertEqual(end + 30, harness.Session.player.PurchaseDailyPasses[83028].EndDay, "monthly term survives reload");
        AssertEqual(0, Claim(83028).RewardList.Count, "daily claim survives reload");
        ValidateFullMonthlyCombo();
        ValidateTenDayPackage();
        Console.WriteLine("Store purchase/recharge compatibility checks passed.");
    }

    private static void ValidateFullMonthlyCombo()
    {
        const long uid = 468601;
        Player player = CreateDrawCompatibilityPlayer(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [new Item { Id = Inventory.HongKa, Count = 100 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "full-monthly-combo");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        long day = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 25200) / 86400;
        player.PurchaseDailyPasses[83028] = new() { EndDay = day + 179, LastClaimDay = day };
        int sequence = 1;
        PurchaseResponse Buy(uint id)
        {
            int packet = sequence++;
            InvokeRegisteredRequestHandler(nameof(PurchaseRequest), harness.Session, packet,
                new PurchaseRequest { Id = id, Count = 1, DiscountId = -1, UiTypeList = [15] });
            return ReadResponsePayload<PurchaseResponse>(harness, packet, nameof(PurchaseResponse), "full monthly", maxPacketsToRead: 64);
        }
        AssertEqual(20053005, Buy(83028).Code, "full direct renewal still rejects");
        AssertEqual(100L, inventory.Items.Single(i => i.Id == Inventory.HongKa).Count, "rejection does not debit");
        AssertEqual(0, Buy(1969).Code, "beginner combo bypasses companion renewal cap");
        AssertEqual(day + 209, player.PurchaseDailyPasses[83028].EndDay, "combo extends full pass by thirty days");
        AssertEqual(day, player.PurchaseDailyPasses[83028].LastClaimDay, "combo preserves today's claim");
        AssertEqual(52L, inventory.Items.Single(i => i.Id == Inventory.HongKa).Count, "combo charges only its own price");
        AssertEqual(20053005, Buy(1969).Code, "combo still limited to one purchase");
        AssertEqual(20053005, Buy(83028).Code, "direct renewal still rejects above cap");
        AssertEqual(day + 209, player.PurchaseDailyPasses[83028].EndDay, "rejected purchases preserve term");
    }

    private static void ValidateTenDayPackage()
    {
        const long uid = 468602;
        Player player = CreateDrawCompatibilityPlayer(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [new Item { Id = Inventory.HongKa, Count = 100 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "ten-day-package");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int sequence = 1;
        PurchaseResponse Buy()
        {
            int packet = sequence++;
            InvokeRegisteredRequestHandler(nameof(PurchaseRequest), harness.Session, packet,
                new PurchaseRequest { Id = 9, Count = 1, DiscountId = -1, UiTypeList = [5] });
            return ReadResponsePayload<PurchaseResponse>(harness, packet, nameof(PurchaseResponse), "ten day", maxPacketsToRead: 64);
        }
        void Refresh()
        {
            int packet = sequence++;
            InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, packet,
                new GetPurchaseListRequest { UiTypeList = [5] });
            ReadResponsePayload<GetPurchaseListResponse>(harness, packet, nameof(GetPurchaseListResponse), "ten day refresh", maxPacketsToRead: 64);
        }
        AssertEqual(0, Buy().Code, "ten-day package is purchasable");
        long day = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 25200) / 86400;
        AssertEqual(day + 10, player.PurchaseDailyPasses[9].EndDay, "ten-day configured duration");
        AssertEqual(95L, inventory.Items.Single(i => i.Id == Inventory.HongKa).Count, "ten-day purchase price");
        AssertEqual(10L, inventory.Items.Single(i => i.Id == 12).Count, "instant reward delivered");
        AssertEqual(1, player.Mails.Count, "first day's reward arrives by mail");
        AssertEqual(250, player.Mails.Single().RewardGoodsList!.Single().Count, "daily mail reward quantity");
        AssertEqual(50000u, player.Mails.Single().RewardGoodsList!.Single().TemplateId, "daily mail reward item");
        Refresh();
        AssertEqual(1, player.Mails.Count, "same-day refresh does not duplicate mail");
        AssertEqual(20053005, Buy().Code, "ten-day package lifetime limit enforced");
        // Model the tenth day, keeping earlier mail on its original date.
        player.Mails.Clear();
        player.PurchaseDailyPasses[9].EndDay = day + 1;
        player.PurchaseDailyPasses[9].LastClaimDay = day - 1;
        Refresh();
        AssertEqual(1, player.Mails.Count, "final entitled day delivers mail");
        harness.Session.player = BsonSerializer.Deserialize<Player>(player.ToBson());
        Refresh();
        AssertEqual(1, harness.Session.player.Mails.Count, "mail claim survives player reload");
        harness.Session.player.PurchaseDailyPasses[9].EndDay = day;
        harness.Session.player.PurchaseDailyPasses[9].LastClaimDay = day - 1;
        Refresh();
        AssertEqual(1, harness.Session.player.Mails.Count, "expired ten-day pass sends no further mail");
    }
}
