using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.pay;
using AscNet.Table.V2.share.reward;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidatePurchasesCompatibility()
    {
        using MongoCollectionOverride noOpStages = MongoCollectionOverride.InstallNoOpStageCollection();
        using MongoCollectionOverride storage = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        const long uid = 468700;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 80;
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
            [new Item { Id = Inventory.HongKa, Count = 10 }, new Item { Id = 106, Count = 2 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "purchases-compat");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 468700;
        TResponse Call<TRequest, TResponse>(TRequest request)
        {
            int sequence = packetId++;
            InvokeRegisteredRequestHandler(typeof(TRequest).Name, harness.Session, sequence, request!);
            return ReadResponsePayload<TResponse>(harness, sequence, typeof(TResponse).Name, typeof(TRequest).Name, maxPacketsToRead: 64);
        }
        long Balance(int id) => inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0;
        Player Reload() => BsonSerializer.Deserialize<Player>(player.ToBson());

        object lazyCatalog = typeof(PayModule).GetField("Catalog", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object catalog = lazyCatalog.GetType().GetProperty("Value")!.GetValue(lazyCatalog)!;
        var combos = (Dictionary<uint, JObject>)typeof(PurchaseCatalog).GetField("combos", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(catalog)!;
        JObject Combo(uint id, int limit, params (uint sub, int price)[] subs) => new()
        {
            ["Id"] = id, ["UiType"] = 5, ["ConsumeId"] = Inventory.HongKa, ["BuyLimitParam"] = limit,
            ["TimeToShelve"] = 0, ["TimeToUnShelve"] = 0, ["TimeToInvalid"] = 0, ["Name"] = "bundle",
            ["SubPackageDiscounts"] = new JObject(subs.Select(sub => new JProperty(sub.sub.ToString(), sub.price)))
        };
        combos[9000001] = Combo(9000001, 1, (2063, 4), (2064, 20), (2065, 50));
        combos[9000002] = Combo(9000002, 0, (2063, 3), (2064, 20));
        combos[9000003] = Combo(9000003, 0, (2064, 20));
        try
        {
            PurchaseComboResponse Buy(uint id) => Call<PurchaseComboRequest, PurchaseComboResponse>(new() { ComboId = id });
            AssertEqual(20053037, Buy(1234).Code, "unknown bundle");
            AssertEqual(20012004, Buy(9000001).Code, "bundle needs the combined discounted price");
            AssertEqual(0, player.PurchaseBuyTimes.Count, "rejected bundle buys no member");
            AssertEqual(10L, Balance(Inventory.HongKa), "rejected bundle does not debit");
            inventory.Items.Single(item => item.Id == Inventory.HongKa).Count = 1000;
            PurchaseComboResponse bought = Buy(9000001);
            AssertEqual(0, bought.Code, "bundle purchase");
            AssertEqual(1000L - 74, Balance(Inventory.HongKa), "bundle charges the sum of member discounts");
            foreach (uint sub in new uint[] { 2063, 2064, 2065 })
                AssertEqual(1, player.PurchaseBuyTimes.GetValueOrDefault(sub), $"member {sub} counted");
            AssertEqual(1, player.PurchaseComboBuyTimes[9000001], "bundle counted");
            AssertEqual(1, Convert.ToInt32((object)((Dictionary<dynamic, dynamic>)bought.ComboInfo!)["BuyTimes"]), "response reports bundle count");
            PurchaseCatalog source = (PurchaseCatalog)catalog;
            long expectedRewards = new uint[] { 2063, 2064, 2065 }.Sum(sub => ((IEnumerable<dynamic>)source.Find(sub)!["RewardGoodsList"])
                .Sum(reward => (long)Convert.ToInt32((object)reward["Count"])));
            AssertEqual(expectedRewards, bought.RewardList.Sum(reward => (long)reward.Count), "bundle grants every member reward");
            Player saved = Reload();
            AssertEqual(1, saved.PurchaseComboBuyTimes[9000001], "bundle count survives reload");
            AssertEqual(true, saved.PendingCombo is null, "no pending bundle after success");
            AssertEqual(20053038, Buy(9000001).Code, "bundle limit");
            AssertEqual(1000L - 74, Balance(Inventory.HongKa), "limit rejection does not debit");
            // 2064 reached its own limit, so only 2063 (limit 2) is bought, at that bundle's discount.
            AssertEqual(0, Buy(9000002).Code, "bundle skips sold-out members");
            AssertEqual(1000L - 74 - 3, Balance(Inventory.HongKa), "bundle price counts only available members");
            AssertEqual(1, player.PurchaseBuyTimes[2064], "sold-out member not bought again");
            AssertEqual(2, player.PurchaseBuyTimes[2063], "available member bought");
            AssertEqual(20053039, Buy(9000003).Code, "all members sold out");
        }
        finally { combos.Remove(9000001); combos.Remove(9000002); combos.Remove(9000003); }

        // ---- First recharge reward ----
        GetFirstPayRewardResponse ClaimFirst() => Call<GetFirstPayRewardRequest, GetFirstPayRewardResponse>(new());
        PayInitiatedResponse Recharge(string key) => Call<PayInitiatedRequest, PayInitiatedResponse>(new() { Key = key });
        List<FirstPayRewardTable> firstRows = [.. TableReaderV2.Parse<FirstPayRewardTable>().OrderBy(row => row.Id)];
        inventory.Items.Single(item => item.Id == Inventory.HongKa).Count = 0;
        NotifyPayInfo NotifyFirstPay(Player current) => PayModule.BuildNotifyPayInfo(current,
            harness.Session.ClientApplicationVersion, harness.Session.ClientDocumentVersion);
        AssertEqual(0f, NotifyFirstPay(player).TotalPayMoney, "fresh player has paid nothing");
        AssertEqual(40001014, ClaimFirst().Code, "no recharge, no first reward");
        AssertEqual(0, Recharge("PayWin28").Code, "small recharge");
        AssertEqual(499f, NotifyFirstPay(player).TotalPayMoney, "total counts $4.99 in hundredths");
        AssertEqual(40001014, ClaimFirst().Code, "below threshold");
        AssertEqual(0, Recharge("PayWin600").Code, "large recharge");
        AssertEqual(499f + 9999f, NotifyFirstPay(player).TotalPayMoney, "total accumulates the second price");
        player.TotalPayMoney = firstRows[0].NeedPayMoney - 1;
        AssertEqual(40001014, ClaimFirst().Code, "one hundredth short");
        player.TotalPayMoney = firstRows[0].NeedPayMoney;
        List<RewardGoodsTable> expected = firstRows.SelectMany(row => new[] { row.BigRewardId, row.SmallRewardId })
            .Where(id => id > 0).SelectMany(RewardHandler.GetRewardGoods).ToList();
        string Sig(IEnumerable<(int id, long count)> goods) => string.Join(";", goods.GroupBy(g => g.id).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Sum(x => x.count)}"));
        GetFirstPayRewardResponse first = ClaimFirst();
        AssertEqual(0, first.Code, "threshold reached");
        AssertEqual(Sig(expected.Select(g => (g.TemplateId, (long)g.Count))), Sig(first.RewardList.Select(g => (g.TemplateId, (long)g.Count))), "rewards come from the table rows");
        AssertEqual(firstRows.Count, Reload().FirstPayRewardIds.Count, "claim persists");
        AssertEqual(firstRows.Count, NotifyFirstPay(Reload()).FirstRewardReceivedList.Count, "login reports received ids");
        long afterFirst = Balance(2025001);
        AssertEqual(40001013, ClaimFirst().Code, "claimed once");
        AssertEqual(afterFirst, Balance(2025001), "repeat grants nothing");

        // ---- PayCheckResult over the pending-recharge state ----
        PayCheckResultResponse Check(string? order) => Call<PayCheckResultRequest, PayCheckResultResponse>(new() { GameOrder = order });
        AssertEqual(40001006, Check(null).Code, "nothing pending");
        string orderId = $"local:{player.PlayerData.Id}:{player.RechargeSequence}";
        player.PendingRecharge = new() { Key = "PayWin5", Count = 5, Order = orderId };
        long cards = Balance(Inventory.HongKa), paid = player.TotalPayMoney;
        AssertEqual(40001006, Check(orderId + "x").Code, "foreign order");
        PayCheckResultResponse checkedOrder = Check(orderId);
        AssertEqual(0, checkedOrder.Code, "pending order finalized");
        AssertEqual(true, checkedOrder.LocalCompleted, "reports completion");
        AssertEqual(cards + 5, Balance(Inventory.HongKa), "credits the cards once");
        AssertEqual(paid + 99, player.TotalPayMoney, "adds $0.99");
        AssertEqual(0, Check(orderId).Code, "retry after completion");
        AssertEqual(cards + 5, Balance(Inventory.HongKa), "retry grants nothing");

        // ---- Makeup Sign-in Card ----
        static long DayOf(int y, int m, int d) => (new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Days;
        PlayerPurchaseDailyPass window = new() { FirstDay = DayOf(2026, 2, 20), EndDay = DayOf(2026, 4, 20), ClaimedDays = [DayOf(2026, 3, 2)] };
        AssertEqual(13, PayModule.MissedDays(window, DayOf(2026, 3, 15)).Count, "unclaimed days of the month before today");
        AssertEqual(0, PayModule.MissedDays(window, DayOf(2026, 4, 1)).Count, "new month starts clean");
        window.EndDay = DayOf(2026, 3, 10);
        AssertEqual(8, PayModule.MissedDays(window, DayOf(2026, 3, 15)).Count, "expired days are not missed");
        AssertEqual(0, PayModule.MissedDays(new() { EndDay = DayOf(2026, 4, 20) }, DayOf(2026, 3, 15)).Count, "legacy pass without a start day");

        PurchaseSupplementGetDailyRewardResponse Supplement(uint id) =>
            Call<PurchaseSupplementGetDailyRewardRequest, PurchaseSupplementGetDailyRewardResponse>(new() { Id = id });
        AssertEqual(20053001, Supplement(777777).Code, "unknown package");
        AssertEqual(20053040, Supplement(90291).Code, "package without a makeup card");
        AssertEqual(20053036, Supplement(90032).Code, "pass not bought");
        long today = PayModule.PurchaseDay();
        DateTime now = DateTime.UnixEpoch.AddDays(today);
        long monthStart = DayOf(now.Year, now.Month, 1);
        player.PurchaseDailyPasses[83028] = new() { FirstDay = today - 40, EndDay = today + 20, LastClaimDay = today, ClaimedDays = [today] };
        int missed = (int)(today - monthStart);
        if (missed == 0)
            AssertEqual(20053041, Supplement(83028).Code, "first of the month has nothing to make up");
        else
        {
            int used = Math.Min(2, missed);
            var info = (Dictionary<dynamic, dynamic>)((PurchaseCatalog)catalog).Find(83028)!;
            long perDay = ((IEnumerable<dynamic>)info["DailyRewardGoodsList"]).Sum(reward => (long)Convert.ToInt32((object)reward["Count"]));
            PurchaseSupplementGetDailyRewardResponse made = Supplement(83028);
            AssertEqual(0, made.Code, "makeup claim");
            AssertEqual(2L - used, Balance(106), "one card per missed day");
            AssertEqual(perDay * used, made.RewardList.Sum(reward => (long)reward.Count), "daily reward per made-up day");
            AssertEqual(1 + used, Reload().PurchaseDailyPasses[83028].ClaimedDays.Count, "made-up days persist");
            var data = (Dictionary<dynamic, dynamic>)((Dictionary<dynamic, dynamic>)made.PurchaseInfo!)["DailyRewardSupplementGetData"];
            AssertEqual(missed - used, Convert.ToInt32((object)data["Count"]), "response reports remaining missed days");
            AssertEqual(missed > used ? 20012004 : 20053041, Supplement(83028).Code, "no double claim");
            AssertEqual(2L - used, Balance(106), "rejected claim keeps cards");
        }
    }
}
