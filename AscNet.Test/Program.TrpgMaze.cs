using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;

namespace AscNet.Test;

internal partial class Program
{
    // Fake Ascension TRPG maze. Source rows (EN 4.9 tables): TRPGMaze 100 (layer 1001: nodes 1-6, start index 6 / card 2; card 1001051 = examine
    // 1001051 -> target 10009, 1001041 = story -> target 11000, 1001101 = Over; area 100 needs stage 10010304) and TRPGMaze 102 (layer 1021, area 102 needs
    // target 11000): 1021091/1021092 Fight [level 2, stage 30200001/30200002, quick reward 400530], 1021071/1021072 Random [1021021 200, 1021061/62 50, 1021091 50],
    // 1021061..63 Reward, 1021051/52 Examine, 1021031 FightWin (stage 10130102), 1021081 Skip -> layer 1022 node 26 card 2. TRPGLevel 1 needs 300 exp (item 61002).
    // TRPGFunctionGroup 11003 [110013 saved] needs target 13011; group 12001 [120011] needs target 20000 and third area 2001 [12001] needs target 13011.
    private static void ValidateTrpgMazeCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 49_401;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);
        Type trpgModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TRPGModule");
        Type mazeType = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TrpgMaze");
        FieldInfo rollField = mazeType.GetField("Roll", BindingFlags.NonPublic | BindingFlags.Static)!;
        object originalRoll = rollField.GetValue(null)!;
        int packetId = 49_400;

        LoopbackSessionHarness Harness(Player player, AscNet.Common.Database.Stage stage, Inventory inventory)
        {
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness harness = new(character, player, inventory, "trpg-maze");
            harness.Session.stage = stage;
            return harness;
        }

        (JObject Response, Dictionary<string, List<JObject>> Pushes) Call(LoopbackSessionHarness harness, string request, object body)
        {
            int id = ++packetId;
            InvokeRegisteredRequestHandler(request, harness.Session, id, body);
            JObject response = ReadUntilResponse(harness, id, request, out Dictionary<string, List<JObject>> pushes);
            return (response, pushes);
        }

        long[] Longs(JToken? token) => token is JArray array ? array.Values<long>().OrderBy(x => x).ToArray() : [];
        string Join(IEnumerable<long> values) => string.Join(',', values);
        int Code(JObject response) => (int)(response.Value<long?>("Code") ?? 0);
        TRPGMazePos Pos(int layer, int node, int index) => new() { LayerId = layer, NodeId = node, Index = index };

        Player player = CreateDrawCompatibilityPlayer(uid);
        AscNet.Common.Database.Stage stage = CreateLoginAccountCompatibilityStage(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, []);
        using LoopbackSessionHarness h = Harness(player, stage, inventory);

        (JObject Response, Dictionary<string, List<JObject>> Pushes) Select(int layer, int node, int index) =>
            Call(h, "TRPGMazeSelectCardRequest", new TRPGMazeSelectCardRequest { SelectPos = Pos(layer, node, index) });
        JObject Result(JObject response) => (JObject)response["Result"]!;
        void Ok(JObject response, string name, bool success = true)
        {
            AssertEqual(0, Code(response), $"{name} accepted");
            AssertEqual(success, Result(response).Value<bool>("Success"), $"{name} result success flag");
        }
        // A request that always fails, to collect the pushes a hook queued without a request of its own.
        Dictionary<string, List<JObject>> Flush() => Select(0, 0, 0).Pushes;
        (JObject Response, Dictionary<string, List<JObject>> Pushes) GiveUp(int layer, int node, int index) =>
            Call(h, "TRPGMazeGiveUpChallengeRequest", new TRPGMazeGiveUpChallengeRequest { Pos = Pos(layer, node, index) });
        (JObject Response, Dictionary<string, List<JObject>> Pushes) Quick(int layer, int node, int index) =>
            Call(h, "TRPGMazeQuickChallengeRequest", new TRPGMazeQuickChallengeRequest { Pos = Pos(layer, node, index) });
        void PinRoll(int value) => rollField.SetValue(null, (Func<int, int>)(_ => value));
        JObject Login() => JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
            InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [h.Session]))));
        // Relogin: the saved player document through BSON, then the login push a fresh session would send.
        JObject Relogin()
        {
            Player reloaded = BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement ?? h.Session.player).ToBson());
            AscNet.Common.Database.Stage reloadedStage = BsonSerializer.Deserialize<AscNet.Common.Database.Stage>((stageSaves.LastReplacement ?? h.Session.stage).ToBson());
            using LoopbackSessionHarness fresh = Harness(reloaded, reloadedStage, h.Session.inventory);
            return JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(
                InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [fresh.Session]))));
        }
        JObject MazeInfo(JObject login, int mazeId) => login["MazeInfos"]!.OfType<JObject>().SingleOrDefault(x => x.Value<int>("Id") == mazeId)!;
        string Finished(JObject? info) => info is null ? "" : Join(info["LayerInfos"]!.SelectMany(x => Longs(x["FinishCardId"])));
        void Fight(int stageId, out Dictionary<string, List<JObject>> pushes)
        {
            int preId = ++packetId;
            InvokeRegisteredRequestHandler(nameof(PreFightRequest), h.Session, preId, new PreFightRequest
            {
                PreFightData = new() { ChallengeCount = 1, StageId = (uint)stageId, CardIds = [1_021_001], RobotIds = [], FirstFightPos = 1, CaptainPos = 1 }
            });
            JObject pre = ReadUntilResponse(h, preId, nameof(PreFightRequest), out _);
            AssertEqual(0, Code(pre), $"pre-fight {stageId}");
            int settleId = ++packetId;
            InvokeRegisteredRequestHandler(nameof(FightSettleRequest), h.Session, settleId,
                CreateMissingStageSettleRequest((uint)stageId, pre["FightData"]!.Value<long>("FightId"), uid));
            AssertEqual(0, Code(ReadUntilResponse(h, settleId, nameof(FightSettleRequest), out pushes)), $"settle {stageId}");
        }

        try
        {
            // Entry boundaries and a fresh login.
            AssertEqual(0, Login()["MazeInfos"]!.Count(), "fresh login has no maze progress");
            AssertEqual(20_104_022, Code(Select(1_001, 1, 2).Response), "select before entering a maze");
            AssertEqual(20_104_022, Code(GiveUp(1_001, 1, 2).Response), "give up before entering a maze");
            AssertEqual(20_104_021, Code(Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 99_999 }).Response), "unknown maze rejected");
            AssertEqual(20_104_069, Code(Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 100 }).Response), "maze 100 locked until stage 10010304 is passed");
            AssertEqual(20_104_069, Code(Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 102 }).Response), "maze 102 locked until target 11000");
            stage.AddStage(new StageDatum { StageId = 10_010_304, Passed = true });
            JObject enter = Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 100 }).Response;
            AssertEqual(0, Code(enter), "maze 100 opens after stage 10010304");
            AssertEqual("1001/6/2", $"{enter["CurrentPos"]!.Value<int>("LayerId")}/{enter["CurrentPos"]!.Value<int>("NodeId")}/{enter["CurrentPos"]!.Value<int>("Index")}", "enter reports the start position");
            AssertEqual("", Finished(enter["MazeInfo"] as JObject), "fresh maze has no finished cards");

            // Maze 100 walk: selection boundaries at the current node.
            AssertEqual(20_104_023, Code(Select(1_021, 1, 2).Response), "layer of another maze");
            AssertEqual(20_104_023, Code(Select(1_002, 1, 2).Response), "layer id that is in no maze");
            AssertEqual(20_104_028, Code(Select(1_001, 99, 2).Response), "node outside the layer");
            AssertEqual(20_104_027, Code(Select(1_001, 2, 2).Response), "node that is not the current one");
            AssertEqual(20_104_029, Code(Select(1_001, 1, 0).Response), "card index 0");
            AssertEqual(20_104_029, Code(Select(1_001, 1, 4).Response), "card index past the node");
            JObject pass = Result(Select(1_001, 1, 2).Response);
            AssertEqual(true, pass.Value<bool>("Success"), "pass card succeeds");
            AssertEqual(0, pass["RewardList"]!.Count(), "pass card gives nothing");
            AssertEqual(20_104_034, Code(Select(1_001, 2, 2).Response), "block card cannot be selected");
            AssertEqual(20_104_027, Code(Select(1_001, 1, 2).Response), "a passed node is no longer current");
            Ok(Select(1_001, 2, 1).Response, "pass card left of centre");
            // The move shifted the cards one slot: node 3 [block, pass, pass] now only reaches indexes 1-2.
            AssertEqual(20_104_031, Code(Select(1_001, 3, 3).Response), "card outside the selectable window");
            Ok(Select(1_001, 3, 2).Response, "pass card in window");

            // Examine card 1001051: pending until the examine finishes.
            JObject examineCard = Result(Select(1_001, 4, 2).Response);
            AssertEqual(false, examineCard.Value<bool>("Success"), "examine card waits for its examine");
            AssertEqual(20_104_032, Code(GiveUp(1_001, 4, 2).Response), "examine card cannot be given up");
            AssertEqual(20_104_032, Code(Quick(1_001, 4, 2).Response), "examine card has no quick challenge");
            AssertEqual(20_104_072, InvokePrivateStaticWithArgs<int>(mazeType, "CheckExamineCard", [h.Session, 1_021_051]), "other examine id rejected for the pending card");
            AssertEqual(0, InvokePrivateStaticWithArgs<int>(mazeType, "CheckExamineCard", [h.Session, 1_001_051]), "matching examine id accepted");
            InvokePrivateStaticWithArgs<object?>(mazeType, "OnExamineFinished", [h.Session, 1_001_051, false]);
            AssertEqual(false, Flush().ContainsKey("NotifyTRPGMazeSelectCardResult"), "failed examine leaves the card pending");
            AssertEqual(20_104_027, Code(Select(1_001, 5, 2).Response), "pending card blocks the next node");
            InvokePrivateStaticWithArgs<object?>(mazeType, "OnExamineFinished", [h.Session, 1_001_051, true]);
            Dictionary<string, List<JObject>> examinePushes = Flush();
            JObject examineResult = Result(new JObject { ["Result"] = examinePushes["NotifyTRPGMazeSelectCardResult"].Single()["Result"] });
            AssertEqual(true, examineResult.Value<bool>("Success"), "examine success completes the card");
            AssertEqual(2, examineResult["CurPos"]!.Value<int>("Index"), "examine result names the card index");
            AssertEqual(true, Longs(examinePushes["NotifyTRPGTargetFinish"].Single()["TargetList"]).Contains(10_009L), "examine card finishes target 10009");
            AssertEqual(1_001_051, examinePushes["NotifyMazeRecordCardId"].Single().Value<int>("RecordCardId"), "examine card is a record card");
            AssertEqual(20_104_071, InvokePrivateStaticWithArgs<int>(mazeType, "CheckExamineCard", [h.Session, 1_001_051]), "no examine card is pending any more");

            // Story card 1001041 completes target 11000, which opens maze 102.
            (JObject storyResponse, Dictionary<string, List<JObject>> storyPushes) = Select(1_001, 5, 2);
            Ok(storyResponse, "story card");
            AssertEqual(true, Longs(storyPushes["NotifyTRPGTargetFinish"].Single()["TargetList"]).Contains(11_000L), "story card finishes target 11000");
            Ok(Select(1_001, 6, 2).Response, "over card");
            AssertEqual(20_104_022, Code(Select(1_001, 1, 2).Response), "over card ends the maze visit");
            JObject login = Relogin();
            AssertEqual("1001041,1001051", Finished(MazeInfo(login, 100)), "finished cards persist across relogin");
            AssertEqual("1001041,1001051", Join(Longs(MazeInfo(login, 100)["RecordCardIds"])), "record cards persist across relogin");
            AssertEqual(true, Longs(login["TargetList"]).Contains(10_009L) && Longs(login["TargetList"]).Contains(11_000L), "card targets persist across relogin");

            // Re-entering restarts the visit but keeps the durable cards.
            enter = Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 100 }).Response;
            AssertEqual("1001041,1001051", Finished(enter["MazeInfo"] as JObject), "re-enter reports the finished cards");
            Ok(Select(1_001, 1, 2).Response, "walk restarts at node 1");

            // Maze 102: fight cards, quick challenge, give up, random events, rewards, examine, fight-win.
            enter = Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 102 }).Response;
            AssertEqual(0, Code(enter), "maze 102 opens after target 11000");
            Ok(Select(1_021, 1, 2).Response, "node 1");
            Ok(Select(1_021, 2, 2).Response, "node 2");
            Ok(Select(1_021, 3, 2).Response, "node 3");
            AssertEqual(20_104_032, Code(GiveUp(1_021, 4, 1).Response), "nothing to give up before the fight card is selected");
            JObject fightCard = Result(Select(1_021, 4, 1).Response);
            AssertEqual(false, fightCard.Value<bool>("Success"), "fight card waits for its fight");
            AssertEqual(20_104_032, Code(Quick(1_021, 4, 2).Response), "quick challenge on another position");
            AssertEqual(20_104_033, Code(Quick(1_021, 4, 1).Response), "quick challenge needs exploration level 2");
            inventory.Items.Add(new Item { Id = 61_002, Count = 300 });
            (JObject quick, _) = Quick(1_021, 4, 1);
            Ok(quick, "quick challenge at level 2");
            AssertEqual(true, Result(quick)["RewardList"]!.Count() > 0, "quick challenge pays the quick reward");
            AssertEqual(20_104_027, Code(Quick(1_021, 4, 1).Response), "quick challenge moved the run past node 4");
            AssertEqual(20_104_031, Code(Select(1_021, 5, 3).Response), "card outside the window");
            JObject realFight = Result(Select(1_021, 5, 1).Response);
            AssertEqual(false, realFight.Value<bool>("Success"), "second fight card waits");
            Fight(10_130_200, out Dictionary<string, List<JObject>> otherStage);
            AssertEqual(false, otherStage.ContainsKey("NotifyTRPGMazeSelectCardResult"), "another stage does not complete the fight card");
            Fight(30_200_002, out Dictionary<string, List<JObject>> fightPushes);
            JObject fightResult = Result(new JObject { ["Result"] = fightPushes["NotifyTRPGMazeSelectCardResult"].Single()["Result"] });
            AssertEqual(true, fightResult.Value<bool>("Success"), "won stage completes the fight card");
            AssertEqual("1021/5/1", $"{fightResult["CurPos"]!.Value<int>("LayerId")}/{fightResult["CurPos"]!.Value<int>("NodeId")}/{fightResult["CurPos"]!.Value<int>("Index")}", "fight result names its position");
            AssertEqual(20_104_027, Code(Select(1_021, 5, 1).Response), "completed fight moved the run on");

            // Random card 1021071: outcomes 1021021 pass (200), 1021061 reward (50), 1021091 fight (50) -> roll 250 is the fight.
            PinRoll(250);
            JObject random = Result(Select(1_021, 6, 2).Response);
            AssertEqual(false, random.Value<bool>("Success"), "random card rolled a fight");
            AssertEqual(1_021_091, random.Value<int>("ReplaceId"), "random card names the rolled card");
            PinRoll(0);
            AssertEqual(1_021_091, Result(Select(1_021, 6, 2).Response).Value<int>("ReplaceId"), "selecting again keeps the rolled outcome");
            AssertEqual(20_104_032, Code(GiveUp(1_021, 6, 1).Response), "give up on another position");
            (JObject giveUp, _) = GiveUp(1_021, 6, 2);
            Ok(giveUp, "give up the rolled fight");
            AssertEqual(1_021_091, Result(giveUp).Value<int>("ReplaceId"), "give up keeps the rolled card id");
            AssertEqual(0, Result(giveUp)["RewardList"]!.Count(), "give up pays nothing");

            // Reward card 1021062 (forever finished + record) and a random card rolling a plain pass.
            (JObject rewardResponse, Dictionary<string, List<JObject>> rewardPushes) = Select(1_021, 7, 2);
            Ok(rewardResponse, "reward card");
            AssertEqual(true, Result(rewardResponse)["RewardList"]!.Count() > 0, "reward card pays its reward");
            AssertEqual(1_021_062, rewardPushes["NotifyMazeRecordCardId"].Single().Value<int>("RecordCardId"), "reward card is a record card");
            JObject passRoll = Result(Select(1_021, 8, 3).Response);
            AssertEqual(true, passRoll.Value<bool>("Success"), "random card rolled a pass");
            AssertEqual(1_021_021, passRoll.Value<int>("ReplaceId"), "random pass names the rolled card");

            // Examine cards: index 1 is outside the window, index 3 is 1021051.
            AssertEqual(20_104_031, Code(Select(1_021, 9, 1).Response), "examine card outside the window");
            AssertEqual(false, Result(Select(1_021, 9, 3).Response).Value<bool>("Success"), "examine card pending");
            AssertEqual(20_104_072, InvokePrivateStaticWithArgs<int>(mazeType, "CheckExamineCard", [h.Session, 1_021_052]), "the other examine card's id is rejected");
            InvokePrivateStaticWithArgs<object?>(mazeType, "OnExamineFinished", [h.Session, 1_021_051, true]);
            AssertEqual(true, Flush().ContainsKey("NotifyTRPGMazeSelectCardResult"), "examine 1021051 completes its card");

            // Fight-win card 1021031 completes with its stage.
            AssertEqual(false, Result(Select(1_021, 10, 2).Response).Value<bool>("Success"), "fight-win card waits for its stage");
            Fight(10_130_102, out Dictionary<string, List<JObject>> winPushes);
            AssertEqual(true, winPushes["NotifyTRPGMazeSelectCardResult"].Single()["Result"]!.Value<bool>("Success"), "stage 10130102 completes the fight-win card");
            Ok(Select(1_021, 11, 1).Response, "reward card 1021063");
            Ok(Select(1_021, 12, 2).Response, "over card");
            login = Relogin();
            AssertEqual("1021031,1021051,1021062,1021063", Finished(MazeInfo(login, 102)), "only durable cards persist in maze 102");
            AssertEqual("1021031,1021051,1021062,1021063", Join(Longs(MazeInfo(login, 102)["RecordCardIds"])), "record cards of maze 102");
            AssertEqual("1001041,1001051", Finished(MazeInfo(login, 100)), "maze 100 progress is separate");

            // A finished durable card passes without paying again; single-use cards reset with a new visit.
            Call(h, "TRPGEnterMazeRequest", new TRPGEnterMazeRequest { MazeId = 102 });
            TrpgMazeRun run = h.Session.player.TrpgMazeRun!;
            run.NodeIndex = 7;
            run.StartPos[(1_021, 7)] = 8;
            (JObject again, _) = Select(1_021, 7, 2);
            Ok(again, "finished reward card passes");
            AssertEqual(0, Result(again)["RewardList"]!.Count(), "finished reward card pays once");
            run.NodeIndex = 6;
            run.StartPos[(1_021, 6)] = 8;
            PinRoll(0);
            AssertEqual(1_021_021, Result(Select(1_021, 6, 2).Response).Value<int>("ReplaceId"), "random card works again in a new visit");
            run.NodeIndex = 6;
            run.StartPos[(1_021, 6)] = 8;
            PinRoll(250);
            AssertEqual(0, Result(Select(1_021, 6, 2).Response).Value<int>("ReplaceId"), "used random card passes without a new roll");

            // Skip card 1021081 -> layer 1022 node 26 card 2.
            run.NodeIndex = 10;
            run.StartPos[(1_021, 10)] = 8;
            Ok(Select(1_021, 10, 1).Response, "skip card");
            AssertEqual(1_022, run.LayerId, "skip card moves to its layer");
            AssertEqual(1, run.NodeIndex, "skip card stands before node 26 of the new layer");
            AssertEqual(20_104_026, Code(Select(1_021, 1, 2).Response), "old layer is no longer current");
            AssertEqual(20_104_023, Code(Select(1_031, 1, 2).Response), "layer of another maze");

            // Examine punishment GoToOrigin restarts the layer: the run goes back to the layer start.
            run.NodeIndex = 9;
            InvokePrivateStaticWithArgs<object?>(mazeType, "RestartCurrentLayer", [h.Session]);
            AssertEqual(1, run.NodeIndex, "restart returns to the layer start");
            Ok(Select(1_022, 1, 2).Response, "layer 1022 node 1 after restart", success: true);

            // Function order from the group/third-area tables.
            int FinishFunction(int id) => Code(Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = id }).Response);
            AssertEqual(20_104_002, FinishFunction(110_013), "group 11003 closed until target 13011");
            player.Trpg.Targets.Add(20_000);
            AssertEqual(20_104_002, FinishFunction(120_011), "third area 2001 closed until target 13011 although its group is open");
            player.Trpg.Targets.Add(13_011);
            AssertEqual(0, FinishFunction(110_013), "group function once its condition holds");
            AssertEqual(0, FinishFunction(120_011), "function once group and third area are open");
            AssertEqual(0, Code(Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_011 }).Response), "function outside every group stays unconstrained");

            // Another account has no maze state.
            Player other = CreateDrawCompatibilityPlayer(uid + 1);
            using LoopbackSessionHarness o = Harness(other, CreateLoginAccountCompatibilityStage(uid + 1), CreateDrawCompatibilityInventory(uid + 1, []));
            AssertEqual(0, InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [o.Session]).MazeInfos.Count, "other account has no maze progress");
        }
        finally
        {
            rollField.SetValue(null, originalRoll);
        }
    }
}
