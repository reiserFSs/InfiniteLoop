using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // Fake Ascension TRPG shop and world boss. Source rows: TRPGShop 191002/192002 (items 1910022 limit 5 for 300 x item 61001 -> 61101,
    // 1910024 limit 2 for 500 -> 61103, 1920021 limit 1 ResetType 0 for 3000 -> 61601 behind finished target 11012; shop/item Condition 770400 =
    // stage 10010304 passed), TRPGItem 61101 Capacity 5, TRPGBoss 1 (TimeId 77, stage 30200299, ChallengeCount 1),
    // TRPGBossPhasesReward 1..5 = 80/60/40/20/0 % -> rewards 400541..400545.
    private static void ValidateTrpgShopBossCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 49_501;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);
        Type trpgModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TRPGModule");
        int packetId = 49_500;

        LoopbackSessionHarness Harness(Player player, AscNet.Common.Database.Stage stage, Inventory inventory, long playerId)
        {
            Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness harness = new(character, player, inventory, "trpg-shop-boss");
            harness.Session.stage = stage;
            return harness;
        }

        JObject ReadUntil(LoopbackSessionHarness harness, int id, string name, out Dictionary<string, List<JObject>> pushes)
        {
            pushes = new();
            for (int index = 0; index < 64; index++)
            {
                Packet packet = harness.ReadPacket($"{name} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                    if (!pushes.TryGetValue(push.Name, out List<JObject>? list)) pushes[push.Name] = list = [];
                    list.Add(JObject.Parse(MessagePackSerializer.ConvertToJson(push.Content)));
                    continue;
                }
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, response.Id, $"{name} response id");
                return JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
            }
            throw new InvalidDataException($"{name}: missing response.");
        }

        (JObject Response, Dictionary<string, List<JObject>> Pushes) Call(LoopbackSessionHarness harness, string request, object? body)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(request, harness.Session, id, body);
            JObject response = ReadUntil(harness, id, request, out Dictionary<string, List<JObject>> pushes);
            return (response, pushes);
        }

        int Code(JObject response) => (int)(response.Value<long?>("Code") ?? 0);
        long Held(Inventory inventory, int itemId) => inventory.Items.Where(x => x.Id == itemId).Sum(x => (long)x.Count);
        TRPGShopBuyItemRequest Buy(int shopId, int itemId, int count) => new() { ShopId = shopId, ItemId = itemId, ItemCount = count };

        JObject Relogin(LoopbackSessionHarness from, long playerId)
        {
            Player player = BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement ?? from.Session.player).ToBson());
            AscNet.Common.Database.Stage stage = BsonSerializer.Deserialize<AscNet.Common.Database.Stage>((stageSaves.LastReplacement ?? from.Session.stage).ToBson());
            using LoopbackSessionHarness fresh = Harness(player, stage, from.Session.inventory, playerId);
            NotifyTRPGData data = InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [fresh.Session]);
            return JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(data)));
        }

        JToken Shop(JObject login, int shopId) => login["ShopInfos"]!.Single(x => x.Value<int>("Id") == shopId);
        string Bought(JObject login, int shopId) => string.Join(',', Shop(login, shopId)["ItemInfos"]!.OrderBy(x => x.Value<int>("Id")).Select(x => $"{x["Id"]}x{x["Count"]}"));

        // ---------------- Shop ----------------
        Player player = CreateDrawCompatibilityPlayer(uid);
        AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness h = Harness(player, stage, inventory, uid);

        JObject login = Relogin(h, uid);
        AssertEqual("191002,192002,193002,194002", string.Join(',', login["ShopInfos"]!.Select(x => x.Value<int>("Id")).OrderBy(x => x)), "login lists every TRPG shop");
        AssertEqual(true, login["ShopInfos"]!.All(x => x.Value<int>("DisCount") == 100 && x.Value<int>("AddBuyCount") == 0 && !x["ItemInfos"]!.Any()), "fresh shops: no discount, no bonus, nothing bought");

        AssertEqual(20_104_053, Code(Call(h, "TRPGShopBuyItemRequest", Buy(1, 1_910_022, 1)).Response), "unknown shop rejected");
        AssertEqual(20_104_054, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_920_022, 1)).Response), "item that is not in the shop rejected");
        AssertEqual(20_104_069, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 1)).Response), "item condition (stage 10010304) not met");
        stage.AddStage(new StageDatum { StageId = 10_010_304, Passed = true });
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 0)).Response), "zero count rejected");
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, -1)).Response), "negative count rejected");
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 6)).Response), "more than the daily limit rejected");
        AssertEqual(20_012_004, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 1)).Response), "currency (61001) short rejected");
        AssertEqual(0L, Held(inventory, 61_101), "rejections grant nothing");

        inventory.Do(61_001, 5_000);
        (JObject buy, Dictionary<string, List<JObject>> buyPushes) = Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 2));
        AssertEqual(0, Code(buy), "buy 2 x 1910022");
        AssertEqual("61101x2", string.Join(',', ((JArray)buy["RewardList"]!).Select(x => $"{x["TemplateId"]}x{x["Count"]}")), "response lists the purchased goods");
        AssertEqual(2L, Held(inventory, 61_101), "purchased items arrive");
        AssertEqual(4_400L, Held(inventory, 61_001), "purchase spends 2 x 300");
        AssertEqual(true, buyPushes.ContainsKey("NotifyItemDataList"), "purchase pushes the item change");
        login = Relogin(h, uid);
        AssertEqual("1910022x2", Bought(login, 191_002), "bought count persists across relog");
        AssertEqual("", Bought(login, 192_002), "another shop is untouched");

        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 4)).Response), "only 3 left of 5");
        AssertEqual(0, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 3)).Response), "buy the remaining 3");
        AssertEqual(5L, Held(inventory, 61_101), "item reaches its carry limit");
        AssertEqual(3_500L, Held(inventory, 61_001), "5 x 300 spent in total");
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_910_022, 1)).Response), "sold out");
        AssertEqual(20_012_005, Code(Call(h, "TRPGShopBuyItemRequest", Buy(192_002, 1_910_022, 1)).Response), "same item in another shop is stopped by the carry limit");
        AssertEqual(3_500L, Held(inventory, 61_001), "capacity rejection spends nothing");

        AssertEqual(0, Code(Call(h, "TRPGShopBuyItemRequest", Buy(192_002, 1_910_024, 2)).Response), "different item in another shop");
        AssertEqual(2L, Held(inventory, 61_103), "different item grants its own goods");
        AssertEqual(2_500L, Held(inventory, 61_001), "2 x 500 spent");
        login = Relogin(h, uid);
        AssertEqual("1910022x5", Bought(login, 191_002), "shop 191002 counts");
        AssertEqual("1910024x2", Bought(login, 192_002), "shop 192002 counts");

        // ResetType 0 item sits behind finished target 11012 and is never reset; ResetType 1 counts reset at the daily period.
        AssertEqual(20_104_069, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_920_021, 1)).Response), "target condition (11012) not met");
        player.Trpg.Targets.Add(11_012);
        AssertEqual(20_012_004, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_920_021, 1)).Response), "3000 needed, 2500 held");
        inventory.Do(61_001, 1_000);
        AssertEqual(0, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_920_021, 1)).Response), "never-reset item bought");
        AssertEqual(1L, Held(inventory, 61_601), "never-reset item grants its goods");
        AssertEqual(500L, Held(inventory, 61_001), "3000 spent");
        player.TrpgShopBoss.ShopDay -= 1;
        player.SaveChecked();
        login = Relogin(h, uid);
        AssertEqual("1920021x1", Bought(login, 191_002), "daily reset clears resettable counts only");
        AssertEqual("", Bought(login, 192_002), "daily reset clears the other shop too");
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(191_002, 1_920_021, 1)).Response), "never-reset item stays sold out");
        AssertEqual(0, Code(Call(h, "TRPGShopBuyItemRequest", Buy(192_002, 1_910_024, 1)).Response), "resettable item buyable again after the reset");
        AssertEqual(0L, Held(inventory, 61_001), "last 500 spent");

        // Talents of an owned investigator (role 4, unlocked by target 43013): talent 6 = buff 20406 "10% off", talent 8 = buff 20408 "daily limit +1".
        inventory.Do(61_001, 2_000);
        player.Trpg.Targets.Add(43_013);
        player.Trpg.Roles.Add(new TrpgRoleState { Id = 4, Talents = [6, 8] });
        player.SaveChecked();
        login = Relogin(h, uid);
        AssertEqual(true, login["ShopInfos"]!.All(x => x.Value<int>("DisCount") == 90 && x.Value<int>("AddBuyCount") == 1), "talents give every shop 90% price and +1 limit");
        trpgModule.GetMethod("TrpgPushShopExtra", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, [h.Session]);
        Packet.Push extra = MessagePackSerializer.Deserialize<Packet.Push>(h.ReadPacket("shop extra push").Content);
        AssertEqual("NotifyTRPGShopExtraData", extra.Name, "talent change pushes the shop extra data");
        JArray extras = (JArray)JObject.Parse(MessagePackSerializer.ConvertToJson(extra.Content))["ExtraDatas"]!;
        AssertEqual(true, extras.Count == 4 && extras.All(x => x.Value<int>("DisCount") == 90 && x.Value<int>("AddBuyCount") == 1), "push carries every shop's discount and bonus");
        // 1910024: limit 2 + 1, one bought after the reset, price ceil(500 * 0.9) = 450 each.
        AssertEqual(20_104_056, Code(Call(h, "TRPGShopBuyItemRequest", Buy(192_002, 1_910_024, 3)).Response), "limit 3 with 1 bought leaves 2");
        AssertEqual(0, Code(Call(h, "TRPGShopBuyItemRequest", Buy(192_002, 1_910_024, 2)).Response), "bonus limit lets 2 more through");
        AssertEqual(1_100L, Held(inventory, 61_001), "2 x 450 spent with the discount");
        // Another account is untouched.
        Player other = CreateDrawCompatibilityPlayer(uid + 1);
        using LoopbackSessionHarness o = Harness(other, CreateLoginAccountCompatibilityStage(uid + 1), CreateDrawCompatibilityInventory(uid + 1, []), uid + 1);
        NotifyTRPGData otherLogin = InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [o.Session]);
        AssertEqual(0, otherLogin.ShopInfos.Sum(x => x.ItemInfos.Count), "other account has bought nothing");

        // ---------------- World boss ----------------
        const long bossUid = 49_511;
        Player bossPlayer = CreateDrawCompatibilityPlayer(bossUid);
        AscNet.Common.Database.Stage bossStage = CreateLoginAccountCompatibilityStage(bossUid);
        Inventory bossInventory = CreateDrawCompatibilityInventory(bossUid, []);
        using LoopbackSessionHarness b = Harness(bossPlayer, bossStage, bossInventory, bossUid);
        const int bossStageId = 30_200_299;

        login = Relogin(b, bossUid);
        AssertEqual("1/0/", $"{login["BossInfo"]!["Id"]}/{login["BossInfo"]!["ChallengeCount"]}/{string.Join(',', login["BossInfo"]!["PhasesRewardList"]!)}", "fresh boss info");
        JObject detail = Call(b, "TRPGBossDetailRequest", null).Response;
        AssertEqual("0/10000000", $"{detail["LoseHp"]}/{detail["TotalHp"]}", "fresh boss HP");
        AssertEqual(20_104_063, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 99 }).Response), "unknown phase reward rejected");
        AssertEqual(20_104_065, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 1 }).Response), "phase reward needs the boss HP below 80%");

        PreFightRequest Pre() => new() { PreFightData = new() { ChallengeCount = 1, StageId = bossStageId, CardIds = [1_021_001], RobotIds = [], FirstFightPos = 1, CaptainPos = 1 } };
        int PreFight(out JObject response)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), b.Session, id, Pre());
            response = ReadUntil(b, id, nameof(PreFightRequest), out _);
            return Code(response);
        }
        // Settle with the given reported damage; the DPS rows sum to dpsDamage.
        JObject Settle(JObject pre, long totalDamage, long dpsDamage, bool win, out Dictionary<string, List<JObject>> pushes)
        {
            FightSettleRequest settle = CreateMissingStageSettleRequest(bossStageId, pre["FightData"]!.Value<long>("FightId"), bossUid);
            settle.Result.IsWin = win;
            settle.Result.TotalDamage = totalDamage;
            settle.Result.StartFrame = 100;
            settle.Result.SettleFrame = 100 + 20 * 120 + 40;
            settle.Result.PauseFrame = 40;
            settle.Result.NpcDpsTable = new() { [1] = new NpcDpsTable { CharacterId = 1_021_001, RoleId = (int)bossUid, DamageTotal = dpsDamage } };
            int id = ++packetId;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), b.Session, id, settle);
            return ReadUntil(b, id, nameof(FightSettleRequest), out pushes);
        }

        AssertEqual(0, PreFight(out JObject pre1), "boss pre-fight inside the window");
        // Damage the DPS rows cannot explain is rejected and does not use the attempt.
        AssertEqual(1033, Code(Settle(pre1, 9_000_000, 3_000_000, true, out _)), "inflated damage rejected");
        AssertEqual(0, ((int?)Call(b, "TRPGBossDetailRequest", null).Response["LoseHp"]) ?? 0, "rejected settle costs no HP");
        JObject fight1 = Settle(pre1, 3_000_000, 3_000_000, true, out Dictionary<string, List<JObject>> fight1Pushes);
        AssertEqual(0, Code(fight1), "boss win settles");
        JToken result1 = fight1["Settle"]!["TRPGBossFightResult"]!;
        AssertEqual("120/3000000", $"{result1["FightTime"]}/{result1["BossDamage"]}", "win screen shows fight time and damage");
        AssertEqual(1, (int)fight1Pushes["NotifyTRPGClientBossData"].Single()["BaseInfo"]!["ChallengeCount"]!, "challenge count pushed");
        AssertEqual("3000000", Call(b, "TRPGBossDetailRequest", null).Response["LoseHp"]!.ToString(), "boss lost the reported damage");
        login = Relogin(b, bossUid);
        AssertEqual("1", login["BossInfo"]!["ChallengeCount"]!.ToString(), "attempt persists across relog");

        AssertEqual(20_104_067, PreFight(out _), "second attempt on the same day rejected");
        AssertEqual(20_104_065, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 2 }).Response), "60% phase needs more damage");
        (JObject phase, _) = Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 1 });
        AssertEqual(0, Code(phase), "80% phase reward after 30% damage");
        AssertEqual(true, ((JArray)phase["RewardList"]!).Count > 0, "phase reward returns goods");
        AssertEqual(20_104_064, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 1 }).Response), "phase reward claims once");
        AssertEqual("1", string.Join(',', Relogin(b, bossUid)["BossInfo"]!["PhasesRewardList"]!), "claimed phase persists");

        // Next day: a loss or forced exit keeps the attempt; a second win adds up and caps at the total.
        bossPlayer.TrpgShopBoss.BossChallengeDay -= 1;
        AssertEqual(0, PreFight(out JObject pre2), "attempts return the next day");
        AssertEqual(0, Code(Settle(pre2, 0, 0, false, out _)), "loss settles");
        AssertEqual("3000000", Call(b, "TRPGBossDetailRequest", null).Response["LoseHp"]!.ToString(), "a loss does not damage the boss");
        AssertEqual(0, PreFight(out JObject pre3), "pre-fight again after a loss");
        AssertEqual(0, Code(Settle(pre3, 8_000_000, 8_000_000, true, out _)), "second win settles");
        AssertEqual("10000000", Call(b, "TRPGBossDetailRequest", null).Response["LoseHp"]!.ToString(), "boss HP loss caps at the total");
        AssertEqual(0, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 5 }).Response), "final phase at 0% HP");
        AssertEqual("1,5", string.Join(',', Relogin(b, bossUid)["BossInfo"]!["PhasesRewardList"]!.Select(x => (int)x).OrderBy(x => x)), "both claimed phases persist");

        // Outside its window the boss answers NotInOpenTime everywhere.
        try
        {
            ActivityScheduleService.TestEntries = [new ActivityScheduleEntry(77, 1, 2, "test:closed")];
            AssertEqual(20_104_062, Code(Call(b, "TRPGBossDetailRequest", null).Response), "detail outside the window");
            AssertEqual(20_104_062, Code(Call(b, "TRPGBossPhasesRewardRequest", new TRPGBossPhasesRewardRequest { Id = 2 }).Response), "phase reward outside the window");
            AssertEqual(20_104_062, PreFight(out _), "pre-fight outside the window");
        }
        finally { ActivityScheduleService.TestEntries = []; }
    }
}
