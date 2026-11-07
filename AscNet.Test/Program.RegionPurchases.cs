using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.config;
using AscNet.Table.V2.client.purchase;

namespace AscNet.Test;

internal static partial class Program
{
    /// <summary>
    /// Issue #52: a CN client tapped a monthly pass and sent nothing. XUiPurchaseYKList:OnRefresh sorts the YK tab by
    /// XPurchaseConfigs.GetPurchasePackageYKUiConfig(id).SortWeight (xuipurchaseyklist.lua:159-164), which indexes nil for an id the
    /// client's own PurchasePackageYKUiConfig lacks, so no card item (and no BtnBuy handler, :184) was ever created. The server sent
    /// the EN ids 83028/90291/90292/90032; CN resolves 10000/201/202. Also covers the CN Pay table (Rainbow Card tiers, ShowUIType 0).
    /// Run inside the store-purchase storage override (ValidateStorePurchases).
    /// </summary>
    private static void ValidateRegionPurchases()
    {
        int ykTotal = TableReaderV2.Parse<StoreClientConfigTable>().Single(row => row.Key == "PurchaseYKTotalCount").Value;
        int ykSpan = TableReaderV2.Parse<StoreClientConfigTable>().Single(row => row.Key == "PurchaseYKLimtCount").Value;

        // The client's decision from the pushed fields: XUiPurchaseYKList:OnRefresh renders only when every YK-tab id resolves in the
        // region's PurchasePackageYKUiConfig and the count matches the placeholders (3, or 4 on EN); a card's confirm then passes
        // XUiPurchaseBuyTips._CanBuy (xuipurchasebuytips.lua:917-931) and XYKPurchasePackage:CheckCanBuy (xykpurchasepackage.lua:18-47).
        static bool ClientRendersList(IEnumerable<int> ids, HashSet<int> config, int placeholders) =>
            ids.All(config.Contains) && ids.Count() == placeholders;
        bool ClientAllowsBuy(dynamic info, long rainbowCards)
        {
            int limit = Convert.ToInt32((object)info["BuyLimitTimes"]), bought = Convert.ToInt32((object)info["BuyTimes"]);
            int remain = Convert.ToInt32((object)info["BuyLimitRemainDay"]);
            return !(limit > 0 && bought >= limit)
                && (int)Math.Ceiling(remain / (double)ykSpan) != ykTotal
                && Convert.ToInt32((object)info["ConsumeCount"]) <= rainbowCards;
        }

        void Run(string? region, long uid, Action<LoopbackSessionHarness, Player, Inventory, Func<int, long>> body)
        {
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.ClientRegion = region;
            Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.HongKa, Count = 3721 }]);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, $"region-purchase-{region ?? "en"}");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            body(harness, player, inventory, id => inventory.Items.FirstOrDefault(item => item.Id == id)?.Count ?? 0);
        }

        int packetId = 468700;
        foreach ((string? region, long uid, int[] expectedIds, int placeholders) in new (string?, long, int[], int)[]
                 { (null, 468701, [83028, 90032, 90291, 90292], 4), ("cn", 468702, [10000, 201, 202], 3) })
            Run(region, uid, (harness, player, inventory, balance) =>
            {
                string label = region ?? "en";
                HashSet<int> config = TableReaderV2.Parse<PurchasePackageYKUiConfigTable>(region).Select(row => row.Id).ToHashSet();

                List<dynamic> List()
                {
                    int sequence = packetId++;
                    InvokeRegisteredRequestHandler(nameof(GetPurchaseListRequest), harness.Session, sequence,
                        new GetPurchaseListRequest { UiTypeList = [2] });
                    return ReadResponsePayload<GetPurchaseListResponse>(harness, sequence, nameof(GetPurchaseListResponse), $"{label} YK list").PurchaseInfoList;
                }
                PurchaseResponse Buy(uint id)
                {
                    int sequence = packetId++;
                    InvokeRegisteredRequestHandler(nameof(PurchaseRequest), harness.Session, sequence,
                        new PurchaseRequest { Id = id, Count = 1, DiscountId = -1, UiTypeList = [2],
                            Param = new Dictionary<string, object> { ["FromMsg"] = 2 } });
                    return ReadResponsePayload<PurchaseResponse>(harness, sequence, nameof(PurchaseResponse), $"{label} YK buy", maxPacketsToRead: 64);
                }

                List<dynamic> list = List();
                int[] ids = list.Select(info => Convert.ToInt32((object)info["Id"])).OrderBy(id => id).ToArray();
                AssertEqual(true, expectedIds.OrderBy(id => id).SequenceEqual(ids), $"{label} client lists exactly its own monthly passes");
                AssertEqual(true, ClientRendersList(ids, config, placeholders), $"{label} client builds the monthly-pass cards (every id has a YK UI config)");
                AssertEqual(false, list.Any(info => info.ContainsKey("Regions")), "the region gate never reaches the client");
                dynamic firstPass = list.Single(info => Convert.ToInt32((object)info["Id"]) == expectedIds[0]);
                AssertEqual(true, ClientAllowsBuy(firstPass, balance(Inventory.HongKa)), $"{label} fresh monthly pass passes the client's buy gate");

                // The two states of the report: the other region's ids leave the CN client without a card; EN ids render on EN.
                if (region is not null)
                    AssertEqual(false, ClientRendersList([83028, 90032, 90291, 90292], config, placeholders),
                        "EN ids make the CN list sort throw, so no BtnBuy handler exists (the reported dead button)");

                // The other region's package id is not purchasable.
                uint foreign = region is null ? 10000u : 83028u;
                long before = balance(Inventory.HongKa);
                AssertEqual(20053001, Buy(foreign).Code, $"{label} cannot buy the other region's pass {foreign}");
                AssertEqual(before, balance(Inventory.HongKa), "a rejected foreign pass costs nothing");

                if (region is not null)
                {
                    AssertEqual(0, Buy(10000).Code, "CN monthly pass purchase succeeds");
                    AssertEqual(before - 30, balance(Inventory.HongKa), "CN monthly pass costs 30 Rainbow Cards");
                    dynamic bought = List().Single(info => Convert.ToInt32((object)info["Id"]) == 10000);
                    AssertEqual(30, Convert.ToInt32((object)bought["DailyRewardRemainDay"]), "CN pass term pushed");
                    AssertEqual(30, Convert.ToInt32((object)bought["BuyLimitRemainDay"]), "client renewal counter follows the term");
                    AssertEqual(true, ClientAllowsBuy(bought, balance(Inventory.HongKa)), "an active pass can still be renewed below the cap");
                    player.PurchaseDailyPasses[10000].EndDay = PayModule.PurchaseDay() + ykTotal * ykSpan - 5;
                    bought = List().Single(info => Convert.ToInt32((object)info["Id"]) == 10000);
                    AssertEqual(false, ClientAllowsBuy(bought, balance(Inventory.HongKa)), "client refuses renewal at the stack cap");
                    AssertEqual(20053005, Buy(10000).Code, "server enforces the same cap");
                    dynamic weapon = List().Single(info => Convert.ToInt32((object)info["Id"]) == 202);
                    AssertEqual(128, Convert.ToInt32((object)weapon["ConsumeCount"]), "CN weapon pass is 128 Rainbow Cards");
                }

                // Login-time monthly-pass info names the client region's own ids.
                var login = (System.Collections.IEnumerable)typeof(PurchaseRequest).Assembly
                    .GetType("AscNet.GameServer.Handlers.AccountModule")!
                    .GetMethod("BuildPurchaseClientInfoLoginData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { player })!;
                AssertEqual(true, login.Cast<GetPurchaseListResponse.GetPurchaseListResponsePurchaseInfo>()
                    .Select(info => (int)info.Id).ToHashSet().SetEquals(config), $"{label} login purchase info carries the region's package ids");

                // Recharge: the region's own Pay table decides the tiers.
                PayInitiatedResponse Recharge(string key)
                {
                    int sequence = packetId++;
                    InvokeRegisteredRequestHandler(nameof(PayInitiatedRequest), harness.Session, sequence, new PayInitiatedRequest { Key = key });
                    return ReadResponsePayload<PayInitiatedResponse>(harness, sequence, nameof(PayInitiatedResponse), $"{label} recharge", maxPacketsToRead: 32);
                }
                AssertEqual(20053031, Recharge("Pay999999").Code, $"{label}: unknown key still rejected");
                long cards = balance(Inventory.HongKa);
                if (region is null)
                {
                    AssertEqual(20053031, Recharge("Pay648").Code, "EN has no CN card tier Pay648");
                    AssertEqual(0, Recharge("PayWin28").Code, "EN recharge unchanged");
                    AssertEqual(cards + 28, balance(Inventory.HongKa), "EN tier credits its own MoneyCard");
                }
                else
                {
                    AssertEqual(20053031, Recharge("PayWin28").Code, "CN rejects an EN-only key");
                    AssertEqual(0, Recharge("Pay648").Code, "CN Pay648 completes locally despite ShowUIType 0");
                    AssertEqual(cards + 648, balance(Inventory.HongKa), "CN Pay648 credits 648 Rainbow Cards");
                    AssertEqual(648, player.AccumulatedPayMoney, "CN accumulated recharge counts the tier amount");
                }
            });
    }
}
