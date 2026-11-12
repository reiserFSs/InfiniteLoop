using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.trpg.examine;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // TRPG investigators / talents / items / examine / reverse sync. Source rows: TRPGRole 1 Lee (unlock target 10000, init [6,3,9], talent
    // group 1: talent 1 cost 1 buff 20101 Arithmetic+1, talent 2 cost 2 pre 1 buff 20102 Combat+1, talent 3 cost 3 pre 2 buff 20103 Combat floor+3,
    // talent 4 cost 4 pre 3), role 2 Liv (21001), role 4 (43013, group 4: talent 1 buff 20401 type 5, 2 buff 20402 type 3 reward 400500 = 61001x50,
    // 3 buff 20403 type 10 carry +1); TRPGLevel 1 = 8 talent points, UpExp 300; TRPGItem 61103 AddBuff 10001 (Combat+1), 61101 ClearBuff one,
    // 61102 ClearBuff all, 61201/61211 roll +3 (attr 1/2), 61302 reset cost; function 100000 -> target 10000, 110013 -> target 11012 (TRPGSyncTarget
    // -> 10 chapter stages), 30002 hands in 61503 x1; examine 1021051 (endurance 1, action 31021051 type 2 need 1), 1022051 (two rounds, need 18),
    // 1021052 (item action 11021052 = 61501), TRPGExaminePunish 20001 DeBuff 3001 / 30001 LoseItem 61001 x20.
    private static void ValidateTrpgCoreCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 49_401;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TRPGModule");
        Type items = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TrpgItems");
        Type talents = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TrpgTalents");
        Type rewards = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardHandler");
        Type grantType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.RewardGrant");
        int packetId = 49_400;

        LoopbackSessionHarness Harness(Player player, AscNet.Common.Database.Stage stage, Inventory inventory)
        {
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness created = new(character, player, inventory, "trpg-core");
            created.Session.stage = stage;
            return created;
        }

        (JObject Response, Dictionary<string, List<JObject>> Pushes) Call(LoopbackSessionHarness harness, string request, object body)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(request, harness.Session, id, body);
            JObject response = ReadUntilResponse(harness, id, request, out Dictionary<string, List<JObject>> pushes);
            return (response, pushes);
        }

        int Code(JObject response) => (int)(response.Value<long?>("Code") ?? 0);
        int Do(LoopbackSessionHarness harness, string request, object body) => Code(Call(harness, request, body).Response);
        long[] Longs(JToken? token) => token is JArray array ? array.Values<long>().OrderBy(x => x).ToArray() : [];
        string Join(IEnumerable<long> values) => string.Join(',', values);
        void Grant(LoopbackSessionHarness harness, int itemId, int count) => InvokePrivateStatic<object?>(items, "Grant", harness.Session, itemId, count);
        long Held(LoopbackSessionHarness harness, int itemId) => harness.Session.inventory.Items.FirstOrDefault(x => x.Id == itemId)?.Count ?? 0;
        long Period() => (long)talents.GetProperty("Period", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Player Reloaded(LoopbackSessionHarness from) => BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement ?? from.Session.player).ToBson());

        JObject Relogin(LoopbackSessionHarness from)
        {
            AscNet.Common.Database.Stage stage = BsonSerializer.Deserialize<AscNet.Common.Database.Stage>((stageSaves.LastReplacement ?? from.Session.stage).ToBson());
            using LoopbackSessionHarness fresh = Harness(Reloaded(from), stage, from.Session.inventory);
            NotifyTRPGData data = InvokePrivateStaticWithArgs<NotifyTRPGData>(module, "BuildLoginData", [fresh.Session]);
            return JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(data)));
        }

        JObject Attr(JToken character, int id) => (JObject)character["Attributes"]!.Single(x => x.Value<int>("Id") == id);
        JObject Lee(JObject login) => (JObject)login["Characters"]!.Single(x => x.Value<int>("Id") == 1);
        string Ints(JToken? token) => Join(Longs(token));

        Player player = CreateDrawCompatibilityPlayer(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, [
            new Item { Id = 61_103, Count = 3 }, new Item { Id = 61_101, Count = 2 }, new Item { Id = 61_102, Count = 1 },
            new Item { Id = 61_211, Count = 1 }, new Item { Id = 61_201, Count = 1 }, new Item { Id = 61_302, Count = 1 },
            new Item { Id = 61_301, Count = 0 }, new Item { Id = 61_001, Count = 100 }]);
        using LoopbackSessionHarness h = Harness(player, CreateLoginAccountCompatibilityStage(uid), inventory);

        // Fresh account: no investigators, level 1, full endurance, no carry bonus.
        JObject login = Relogin(h);
        AssertEqual(0, ((JArray)login["Characters"]!).Count, "fresh login has no investigators");
        AssertEqual("1/0/30", $"{login["BaseInfo"]!.Value<int>("Level")}/{login["BaseInfo"]!.Value<int>("Exp")}/{login["BaseInfo"]!.Value<int>("Endurance")}", "fresh base info");
        AssertEqual(0, login.Value<int>("ItemCapacityAdd"), "fresh carry bonus");

        // Nothing is usable before the investigator is owned.
        AssertEqual(20_104_015, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 99, TalentId = 1 }), "unknown investigator config");
        AssertEqual(20_104_014, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 1 }), "unowned investigator cannot take talents");
        AssertEqual(20_104_014, Do(h, "TRPGResetTalentRequest", new TRPGResetTalentRequest { CharacterId = 1 }), "unowned investigator cannot reset");
        AssertEqual(20_104_014, Do(h, "TRPGUseItemRequest", new TRPGUseItemRequest { ItemId = 61_103, ItemCount = 1, CharacterId = 1 }), "unowned investigator cannot use items");
        AssertEqual(20_104_042, Do(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_021_051 }), "result without a check");
        AssertEqual(20_104_042, Do(h, "TRPGExamineCharacterRequest", new TRPGExamineCharacterRequest { Id = 1_021_051, ActionId = 31_021_051, Round = 1, CharacterId = 1 }), "roll without a check");
        AssertEqual(20_104_042, Do(h, "TRPGExamineCharacterResetRequest", new TRPGExamineCharacterResetRequest { CharacterId = 1 }), "reset without a check");
        AssertEqual(20_104_042, Do(h, "TRPGExamineChangeRoundRequest", new TRPGExamineChangeRoundRequest()), "change round without a check");

        // Function 100000 is gated by condition 770400 (stage 10010304 passed), 110013 by 601011 (target 13011), 30002 by 609001 (target 300001).
        h.Session.stage.AddStage(new StageDatum { StageId = 10_010_304, Passed = true });
        // Function 100000 finishes target 10000, which unlocks Lee; the gain-investigator target 91001 follows.
        (JObject fn, Dictionary<string, List<JObject>> unlock) = Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 100_000 });
        AssertEqual(0, Code(fn), "opening function accepted");
        AssertEqual("10000,91001", Ints(unlock["NotifyTRPGTargetFinish"].Single()["TargetList"]), "unlock target and its gain-investigator target");
        JObject pushedLee = (JObject)unlock["NotifyTRPGCharacterData"].Single()["Characters"]![0]!;
        AssertEqual(1, pushedLee.Value<int>("Id"), "Lee is pushed when unlocked");
        login = Relogin(h);
        JObject lee = Lee(login);
        AssertEqual("6/6/3/3/9/9", string.Join('/', Enumerable.Range(1, 3).Select(a => $"{Attr(lee, a).Value<int>("Value")}/{Attr(lee, a).Value<int>("MaxRollValue")}")), "Lee base attributes");
        AssertEqual(1, Attr(lee, 1).Value<int>("MinRollValue"), "base roll floor");

        // Talents: order, points (level 1 = 8), persistence, reset cost.
        AssertEqual(20_104_016, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 99 }), "unknown talent");
        AssertEqual(20_104_018, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 3 }), "talent needs its predecessor");
        (JObject t1, Dictionary<string, List<JObject>> t1Pushes) = Call(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 1 });
        AssertEqual(0, Code(t1), "first talent");
        JObject afterT1 = (JObject)t1Pushes["NotifyTRPGCharacterData"].Single()["Characters"]![0]!;
        AssertEqual("1", Ints(afterT1["TalentIds"]), "talent pushed");
        AssertEqual(10, Attr(afterT1, 3).Value<int>("Value"), "talent 1 adds Arithmetic +1");
        AssertEqual(20_104_017, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 1 }), "talent activates once");
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 2 }), "second talent");
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 3 }), "third talent");
        AssertEqual(20_104_019, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 4 }), "1+2+3 points used, talent 4 costs 4 of the remaining 2");
        lee = Lee(Relogin(h));
        AssertEqual("1,2,3", Ints(lee["TalentIds"]), "talents persist");
        AssertEqual("7/4/7", $"{Attr(lee, 1).Value<int>("Value")}/{Attr(lee, 1).Value<int>("MinRollValue")}/{Attr(lee, 1).Value<int>("MaxRollValue")}", "Combat +1 and the roll floor talent");
        AssertEqual(20_104_037, Do(h, "TRPGResetTalentRequest", new TRPGResetTalentRequest { CharacterId = 1 }), "reset needs its item");
        Grant(h, 61_301, 1);
        AssertEqual(0, Do(h, "TRPGResetTalentRequest", new TRPGResetTalentRequest { CharacterId = 1 }), "reset with the item");
        AssertEqual(0L, Held(h, 61_301), "reset spends its item");
        AssertEqual(20_104_020, Do(h, "TRPGResetTalentRequest", new TRPGResetTalentRequest { CharacterId = 1 }), "nothing left to reset");
        AssertEqual("", Ints(Lee(Relogin(h))["TalentIds"]), "reset persists");
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 1 }), "points are refunded");
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 1, TalentId = 2 }), "talent 2 again");

        // Items: validation, buffs, clearing, expiry.
        int Use(int itemId, int count = 1, int role = 1) => Do(h, "TRPGUseItemRequest", new TRPGUseItemRequest { ItemId = itemId, ItemCount = count, CharacterId = role });
        AssertEqual(20_104_035, Use(99_999), "unknown item");
        AssertEqual(20_104_036, Use(61_103, 2), "one use per request");
        AssertEqual(20_104_037, Use(61_104), "item not held");
        AssertEqual(20_104_038, Use(61_201), "roll items are not usable from the bag");
        AssertEqual(20_104_038, Use(61_101), "nothing to clear");
        long combatBefore = Attr(Lee(Relogin(h)), 1).Value<long>("Value");
        (JObject buff, Dictionary<string, List<JObject>> buffPushes) = Call(h, "TRPGUseItemRequest", new TRPGUseItemRequest { ItemId = 61_103, ItemCount = 1, CharacterId = 1 });
        AssertEqual(0, Code(buff), "buff item");
        AssertEqual(2L, Held(h, 61_103), "buff item is spent");
        JObject buffed = (JObject)buffPushes["NotifyTRPGCharacterData"].Single()["Characters"]![0]!;
        AssertEqual("10001", Ints(buffed["BuffIds"]), "buff pushed");
        AssertEqual(combatBefore + 1, Attr(buffed, 1).Value<long>("Value"), "buff adds Combat +1");
        AssertEqual(20_104_038, Use(61_103), "buff already active");
        AssertEqual(2L, Held(h, 61_103), "refused use keeps the item");
        AssertEqual("10001", Ints(Lee(Relogin(h))["BuffIds"]), "buff persists");
        TrpgRoleState leeState = player.Trpg.Roles.Single(x => x.Id == 1);
        leeState.Buffs.Add(new TrpgBuffState { Id = 3_001, Period = Period() });
        leeState.Buffs.Add(new TrpgBuffState { Id = 3_003, Period = Period() });
        AssertEqual(0, Use(61_101), "clear one");
        AssertEqual(1, leeState.Buffs.Count(x => x.Id is 3_001 or 3_003), "clear one removes exactly one negative buff");
        AssertEqual(0, Use(61_102), "clear all");
        AssertEqual("10001", Ints(Lee(Relogin(h))["BuffIds"]), "clear all keeps the positive buff");
        leeState.Buffs.Add(new TrpgBuffState { Id = 3_001, Period = Period() - 1 });
        AssertEqual("10001", Ints(Lee(Relogin(h))["BuffIds"]), "yesterday's buff is gone");

        // Carry limit and exploration level.
        Grant(h, 61_101, 99);
        AssertEqual(5L, Held(h, 61_101), "grant stops at the carry limit (TRPGItem 61101 capacity 5; 2 held before the clear item)");
        Grant(h, 61_002, 300);
        JObject lv = Relogin(h)["BaseInfo"]!.Value<JObject>()!;
        AssertEqual("2/0", $"{lv.Value<int>("Level")}/{lv.Value<int>("Exp")}", "300 exp reaches level 2");
        Grant(h, 61_002, 100);
        AssertEqual("2/100", $"{Relogin(h)["BaseInfo"]!.Value<int>("Level")}/{Relogin(h)["BaseInfo"]!.Value<int>("Exp")}", "exp accumulates inside the level");
        // Exp that arrives through the generic reward path (reward 400501 = 61002 x20) updates the client too.
        object goods = rewards.GetMethod("GetRewardGoods", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [400_501])!;
        Array grants = Array.CreateInstance(grantType, 1);
        grants.SetValue(Activator.CreateInstance(grantType, ["test:trpg-exp", goods, null, null]), 0);
        object applied = rewards.GetMethod("ApplyRewardsOnceAndPersist", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [grants, h.Session])!;
        applied.GetType().GetMethod("SendPushes")!.Invoke(applied, [h.Session]);
        (JObject probe, Dictionary<string, List<JObject>> rewardPushes) = Call(h, "TRPGChangePageStatusRequest", new TRPGChangePageStatusRequest { Status = true });
        AssertEqual("2/120", $"{rewardPushes["NotifyTRPGBaseInfo"].Last()["BaseInfo"]!.Value<int>("Level")}/{rewardPushes["NotifyTRPGBaseInfo"].Last()["BaseInfo"]!.Value<int>("Exp")}", "reward-path exp reaches the client");

        // Examine: validation, item check, roll check with items/reset/rounds, persistence of results.
        player.Trpg.Targets.Add(21_001); // Liv
        // The maze gates a check on the selected examine card (card id = examine id); the maze run is cleared again so a pass does not try to move a maze position that this test does not have.
        int Examine(int id, int action)
        {
            player.TrpgMazeRun = new TrpgMazeRun { PendingCard = id };
            int code = Do(h, "TRPGExamineRequest", new TRPGExamineRequest { Id = id, ActionId = action });
            player.TrpgMazeRun = null;
            return code;
        }
        (JObject Response, Dictionary<string, List<JObject>> Pushes) Roll(int examine, int action, int round, int role, int? item = null) =>
            Call(h, "TRPGExamineCharacterRequest", new TRPGExamineCharacterRequest { Id = examine, ActionId = action, Round = round, CharacterId = role, UseItemId = item });
        AssertEqual(20_104_039, Examine(99, 1), "unknown check");
        AssertEqual(20_104_071, Do(h, "TRPGExamineRequest", new TRPGExamineRequest { Id = 1_021_051, ActionId = 31_021_051 }), "maze: no examine card selected");
        player.TrpgMazeRun = new TrpgMazeRun { PendingCard = 1_021_052 };
        AssertEqual(20_104_072, Do(h, "TRPGExamineRequest", new TRPGExamineRequest { Id = 1_021_051, ActionId = 31_021_051 }), "maze: another examine's card");
        player.TrpgMazeRun = null;
        AssertEqual(20_104_040, Examine(1_021_051, 11_022_051), "action of another check");
        player.Trpg.Endurance = 0;
        player.Trpg.EndurancePeriod = Period();
        AssertEqual(20_104_041, Examine(1_021_051, 31_021_051), "no endurance");
        AssertEqual(20_104_037, Examine(1_021_052, 11_021_052), "item check needs its item");
        Grant(h, 61_501, 1);
        AssertEqual(0, Examine(1_021_052, 11_021_052), "item check starts without endurance cost");
        AssertEqual(0L, Held(h, 61_501), "item check spends the item");
        AssertEqual(20_104_046, Roll(1_021_052, 11_021_052, 1, 1).Response.Value<int?>("Code") ?? 0, "item checks have no rolls");
        (JObject itemResult, _) = Call(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_021_052 });
        AssertEqual("True", itemResult.Value<bool>("Success").ToString(), "item check passes outright");
        AssertEqual(true, Reloaded(h).Trpg.ExaminesPassed.Contains(1_021_052), "passed check persists");
        AssertEqual(true, Reloaded(h).Trpg.Examine is null, "finished check leaves no instance");

        player.Trpg.Endurance = 30;
        player.TrpgMazeRun = new TrpgMazeRun { PendingCard = 1_021_051 };
        (JObject start, Dictionary<string, List<JObject>> startPushes) = Call(h, "TRPGExamineRequest", new TRPGExamineRequest { Id = 1_021_051, ActionId = 31_021_051 });
        player.TrpgMazeRun = null;
        AssertEqual(0, Code(start), "roll check starts");
        AssertEqual(29, startPushes["NotifyTRPGBaseInfo"].Last()["BaseInfo"]!.Value<int>("Endurance"), "check costs 1 endurance");
        AssertEqual(29, Relogin(h)["BaseInfo"]!.Value<int>("Endurance"), "endurance persists");
        AssertEqual(20_104_045, Roll(1_022_051, 31_021_051, 1, 1).Response.Value<int?>("Code") ?? 0, "roll for another check");
        AssertEqual(20_104_047, Roll(1_021_051, 31_021_051, 2, 1).Response.Value<int?>("Code") ?? 0, "wrong round");
        AssertEqual(20_104_014, Roll(1_021_051, 31_021_051, 1, 3).Response.Value<int?>("Code") ?? 0, "unowned investigator cannot roll");
        AssertEqual(20_104_049, Roll(1_021_051, 31_021_051, 1, 1, 99_999).Response.Value<int?>("Code") ?? 0, "unknown roll item");
        AssertEqual(20_104_050, Roll(1_021_051, 31_021_051, 1, 1, 61_103).Response.Value<int?>("Code") ?? 0, "buff item is not a roll item");
        AssertEqual(20_104_051, Roll(1_021_051, 31_021_051, 1, 1, 61_201).Response.Value<int?>("Code") ?? 0, "Combat roll item on a Perception check");
        (JObject roll, _) = Roll(1_021_051, 31_021_051, 1, 1, 61_211);
        AssertEqual(0, Code(roll), "roll with a Perception item");
        int score = roll.Value<int>("Score");
        AssertEqual(true, score is >= 4 and <= 6, "Lee's Perception 3 rolls 1..3 plus the item's +3");
        AssertEqual(0L, Held(h, 61_211), "roll item is spent");
        AssertEqual(20_104_048, Roll(1_021_051, 31_021_051, 1, 1).Response.Value<int?>("Code") ?? 0, "one roll per round");
        AssertEqual(20_104_043, Do(h, "TRPGExamineChangeRoundRequest", new TRPGExamineChangeRoundRequest()), "single-round check has no next round");
        AssertEqual(20_104_052, Do(h, "TRPGExamineCharacterResetRequest", new TRPGExamineCharacterResetRequest { CharacterId = 2 }), "reset needs a roll");
        AssertEqual(0, Do(h, "TRPGExamineCharacterResetRequest", new TRPGExamineCharacterResetRequest { CharacterId = 1 }), "reset with its item");
        AssertEqual(20_104_052, Do(h, "TRPGExamineCharacterResetRequest", new TRPGExamineCharacterResetRequest { CharacterId = 1 }), "reset clears the score");
        AssertEqual(0L, Held(h, 61_302), "reset spends its item");
        AssertEqual(0, Code(Roll(1_021_051, 31_021_051, 1, 1).Response), "re-roll after the reset");
        AssertEqual(20_104_045, Do(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_022_051 }), "result for another check");
        (JObject passed, _) = Call(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_021_051 });
        AssertEqual("1021051/True/0", $"{passed.Value<int>("Id")}/{passed.Value<bool>("Success")}/{passed.Value<int>("PunishId")}", "check passes with a roll");
        AssertEqual(true, Reloaded(h).Trpg.ExaminesPassed.SequenceEqual([1_021_052, 1_021_051]), "both passes persist");

        // Two-round check: round gating, carried score, result against NeedValue 18.
        player.Trpg.Endurance = 30;
        AssertEqual(0, Examine(1_022_051, 11_022_051), "two-round check starts");
        int total = 0;
        total += Roll(1_022_051, 11_022_051, 1, 1).Response.Value<int>("Score");
        AssertEqual(20_104_044, Do(h, "TRPGExamineChangeRoundRequest", new TRPGExamineChangeRoundRequest()), "everyone must roll before the next round");
        total += Roll(1_022_051, 11_022_051, 1, 2).Response.Value<int>("Score");
        AssertEqual(0, Do(h, "TRPGExamineChangeRoundRequest", new TRPGExamineChangeRoundRequest()), "next round");
        AssertEqual(20_104_047, Roll(1_022_051, 11_022_051, 1, 1).Response.Value<int?>("Code") ?? 0, "round 1 is over");
        total += Roll(1_022_051, 11_022_051, 2, 1).Response.Value<int>("Score");
        AssertEqual(20_104_043, Do(h, "TRPGExamineChangeRoundRequest", new TRPGExamineChangeRoundRequest()), "no round 3");
        (JObject multi, _) = Call(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_022_051 });
        AssertEqual(total >= 18, multi.Value<bool>("Success"), $"result is the summed score {total} against 18");
        AssertEqual(true, multi.Value<int>("PunishId") is 0 or 1_022_041 or 1_022_021 or 1_022_022 or 1_022_023, "failure punishment comes from the check's list");
        player.Trpg.Roles.ForEach(r => r.Buffs.Clear());

        // Punishments applied by the server: DeBuff lands on an owned investigator, LoseItem never overdraws.
        MethodInfo applyPunish = module.GetMethod("ApplyPunish", BindingFlags.NonPublic | BindingFlags.Static)!;
        TRPGExamineTable debuff = new() { Id = 1, PunishId = [20_001], PunishWeight = [100], ActionId = [1] };
        TRPGExamineTable lose = new() { Id = 2, PunishId = [30_001], PunishWeight = [100], ActionId = [1] };
        AssertEqual(20_001, (int)applyPunish.Invoke(null, [h.Session, debuff])!, "debuff punishment id");
        AssertEqual(1, player.Trpg.Roles.Sum(r => r.Buffs.Count(x => x.Id == 3_001)), "debuff reaches one investigator");
        long money = Held(h, 61_001);
        AssertEqual(30_001, (int)applyPunish.Invoke(null, [h.Session, lose])!, "lose-item punishment id");
        AssertEqual(money - 20, Held(h, 61_001), "lose-item takes 20");
        inventory.Items.Single(x => x.Id == 61_001).Count = 5;
        applyPunish.Invoke(null, [h.Session, lose]);
        AssertEqual(0L, Held(h, 61_001), "lose-item stops at what is held");
        player.Trpg.Roles.ForEach(r => r.Buffs.Clear());

        // Investigator 4: carry limit talent, examine bonus reward talent.
        player.Trpg.Targets.Add(43_013);
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 4, TalentId = 1 }), "role 4 talent 1");
        AssertEqual(0, Do(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 4, TalentId = 2 }), "role 4 talent 2");
        (JObject cap, Dictionary<string, List<JObject>> capPushes) = Call(h, "TRPGActivateTalentRequest", new TRPGActivateTalentRequest { CharacterId = 4, TalentId = 3 });
        AssertEqual(0, Code(cap), "role 4 talent 3");
        AssertEqual(1, capPushes["NotifyTRPGItemCapacityChange"].Last().Value<int>("ItemCapacityAdd"), "carry talent pushes its bonus");
        AssertEqual(1, Relogin(h).Value<int>("ItemCapacityAdd"), "carry bonus persists");
        Grant(h, 61_101, 99);
        AssertEqual(6L, Held(h, 61_101), "carry limit is now 6");
        player.Trpg.Endurance = 30;
        AssertEqual(0, Examine(1_021_051, 31_021_051), "check for the bonus-reward talent");
        AssertEqual(0, Code(Roll(1_021_051, 31_021_051, 1, 1).Response), "roll");
        long moneyBefore = Held(h, 61_001);
        AssertEqual(true, Call(h, "TRPGExamineResultRequest", new TRPGExamineResultRequest { Id = 1_021_051 }).Response.Value<bool>("Success"), "pass");
        AssertEqual(moneyBefore + 50, Held(h, 61_001), "talent bonus reward: +50 Battle Support Points on a passed check");

        // Reverse sync: finishing target 11012 (function 110013) passes the ten chapter stages TRPGSyncTarget lists.
        AssertEqual(20_104_002, Do(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_013 }), "function group closed before target 13011");
        player.Trpg.Targets.Add(13_011);
        (JObject sync, Dictionary<string, List<JObject>> syncPushes) = Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_013 });
        AssertEqual(0, Code(sync), "function for target 11012");
        JObject syncInfo = syncPushes["NotifyTRPGSyncInfo"].Single();
        AssertEqual("10130171,10130172,10130173,10130174,10130200,10130201,10130202,10130203,10130204,10130205", Ints(syncInfo["StageList"]), "sync push lists the passed stages");
        AssertEqual(true, syncPushes.ContainsKey("NotifyStageData"), "stage data is pushed");
        AssertEqual(10, new[] { 10130200, 10130171, 10130172, 10130201, 10130202, 10130203, 10130173, 10130204, 10130174, 10130205 }
            .Count(id => stageSaves.LastReplacement!.Stages.TryGetValue((uint)id, out StageDatum? s) && s.Passed), "stages are passed in the stage document");
        AssertEqual("10130200,10130203", string.Join(',', new[] { 10130200L, 10130203L }.Intersect(Longs(Relogin(h)["StageList"]))), "login lists them");

        // Hand-in function: needs the item, spends it, finishes the commit-item target; the gain-item target comes with the grant.
        // Function 110013 paid item 61503 (reward 400523), which finished the gain-item target 300001 and so opened the hand-in group.
        AssertEqual(1L, Held(h, 61_503), "function reward granted the hand-in item");
        Item handIn = inventory.Items.Single(x => x.Id == 61_503);
        handIn.Count = 0;
        AssertEqual(20_104_037, Do(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 30_002 }), "hand-in needs the item");
        handIn.Count = 1;
        (JObject commit, Dictionary<string, List<JObject>> commitPushes) = Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 30_002 });
        AssertEqual(0, Code(commit), "hand-in");
        AssertEqual(0L, Held(h, 61_503), "hand-in spends the item");
        AssertEqual(true, Longs(Relogin(h)["TargetList"]).Contains(300_001) && Longs(Relogin(h)["TargetList"]).Contains(300_002), "gain-item and commit-item targets finish");
        AssertEqual(20_104_011, Do(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 30_002 }), "hand-in once");

        // Endurance refills when the daily reset period moves on.
        player.Trpg.Endurance = 0;
        player.Trpg.EndurancePeriod = Period() - 1;
        player.SaveChecked();
        AssertEqual(30, Relogin(h)["BaseInfo"]!.Value<int>("Endurance"), "endurance refills on the next period");

        // Another account is untouched.
        Player other = CreateDrawCompatibilityPlayer(uid + 1);
        using LoopbackSessionHarness o = Harness(other, CreateLoginAccountCompatibilityStage(uid + 1), CreateDrawCompatibilityInventory(uid + 1, []));
        NotifyTRPGData otherLogin = InvokePrivateStaticWithArgs<NotifyTRPGData>(module, "BuildLoginData", [o.Session]);
        AssertEqual("0/1", $"{otherLogin.Characters.Count}/{otherLogin.BaseInfo.Level}", "other account has no investigators");
    }
}
