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
    // Main-line chapter 13 (Fake Ascension / 终焉福音) runs on the TRPG system. Source rows: TRPGSecondMainStage 10000 =
    // stage 10130200 (reward 10000), 11007 = stage 10130203 (reward 11007); TRPGSyncStage 10130200 -> target 10000, 10130203 -> 11007;
    // TRPGTarget 10000/11001 are function targets (Params 100000/110011); TRPGFunction 110012 is NeedSave 0; TRPGMemoirStory 1 = 10 x item 61601.
    private static void ValidateTrpgCompatibility()
    {
        PacketFactory.LoadPacketHandlers();
        const long uid = 49_301;
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _, out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageSaves);
        Type trpgModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TRPGModule");
        int packetId = 49_300;

        LoopbackSessionHarness Harness(Player player, AscNet.Common.Database.Stage stage, Inventory inventory)
        {
            Character character = CreateDrawCompatibilityCharacter(uid);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1_021_001, fashionId: 3_021_001));
            LoopbackSessionHarness harness = new(character, player, inventory, "trpg-compat");
            harness.Session.stage = stage;
            return harness;
        }

        // Every packet up to the response: pushes by name, then the response payload.
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

        // Relogin: the saved player/stage documents through BSON, then the login push a fresh session would send.
        JObject Relogin(LoopbackSessionHarness from)
        {
            Player player = BsonSerializer.Deserialize<Player>((playerSaves.LastReplacement ?? from.Session.player).ToBson());
            AscNet.Common.Database.Stage stage = BsonSerializer.Deserialize<AscNet.Common.Database.Stage>((stageSaves.LastReplacement ?? from.Session.stage).ToBson());
            using LoopbackSessionHarness fresh = Harness(player, stage, from.Session.inventory);
            NotifyTRPGData data = InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [fresh.Session]);
            return JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(data)));
        }

        // Fresh account: nothing finished, exploration page, first main-line chain selected.
        Player player = CreateDrawCompatibilityPlayer(uid);
        Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = 61_601, Count = 4 }]);
        using LoopbackSessionHarness h = Harness(player, CreateLoginAccountCompatibilityStage(uid), inventory);
        JObject login = Relogin(h);
        AssertEqual(10_001L, login.Value<long>("CurTargetLink"), "fresh login selects the opening target chain");
        AssertEqual("", Join(Longs(login["TargetList"])), "fresh login has no finished targets");
        AssertEqual("", Join(Longs(login["StageList"])), "fresh login has no passed chapter stages");
        AssertEqual(false, login.Value<bool>("IsNormalPage"), "fresh login opens the exploration page");

        // State 1: page switch + target chain choice persist across a relog and reject no-ops/unknown chains.
        AssertEqual(0, Code(Call(h, "TRPGChangePageStatusRequest", new TRPGChangePageStatusRequest { Status = true }).Response), "page switch accepted");
        AssertEqual(20_104_068, Code(Call(h, "TRPGChangePageStatusRequest", new TRPGChangePageStatusRequest { Status = true }).Response), "unchanged page status rejected");
        AssertEqual(0, Code(Call(h, "TRPGSelectTargetLinkRequest", new TRPGSelectTargetLinkRequest { Id = 10_002 }).Response), "known target chain accepted");
        AssertEqual(20_104_001, Code(Call(h, "TRPGSelectTargetLinkRequest", new TRPGSelectTargetLinkRequest { Id = 99_999 }).Response), "unknown target chain rejected");
        login = Relogin(h);
        AssertEqual(true, login.Value<bool>("IsNormalPage"), "regular page persists");
        AssertEqual(10_002L, login.Value<long>("CurTargetLink"), "selected target chain persists");

        // State 2: clearing the first chapter stage completes its synced target and shows in the login stage list.
        AssertEqual(20_104_069, Code(Call(h, "TRPGRewardRequest", new TRPGRewardRequest { Id = 10_000 }).Response), "reward locked before the stage is cleared");
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
        Fight(10_130_200, out Dictionary<string, List<JObject>> pushes1);
        JObject sync = pushes1["NotifyTRPGSyncInfo"].Single();
        AssertEqual("10000", Join(Longs(sync["TargetList"])), "first stage pushes its synced target");
        AssertEqual("10130200", Join(Longs(sync["StageList"])), "first stage pushes the passed chapter stage");
        login = Relogin(h);
        AssertEqual("10000", Join(Longs(login["TargetList"])), "state 2 login target list");
        AssertEqual("10130200", Join(Longs(login["StageList"])), "state 2 login stage list");

        // Re-clearing the same stage finishes nothing new.
        Fight(10_130_200, out Dictionary<string, List<JObject>> repeat);
        AssertEqual("", Join(Longs(repeat["NotifyTRPGSyncInfo"].Single()["TargetList"])), "re-clear finishes no new target");

        // State 3: a different stage yields a different target; reward claims once and persists.
        Fight(10_130_203, out Dictionary<string, List<JObject>> pushes3);
        AssertEqual("11007", Join(Longs(pushes3["NotifyTRPGSyncInfo"].Single()["TargetList"])), "second stage pushes a different synced target");
        (JObject claim, _) = Call(h, "TRPGRewardRequest", new TRPGRewardRequest { Id = 10_000 });
        AssertEqual(0, Code(claim), "reward claim after the stage clear");
        AssertEqual(true, ((JArray)claim["RewardList"]!).Count > 0, "reward claim returns goods");
        AssertEqual(20_104_061, Code(Call(h, "TRPGRewardRequest", new TRPGRewardRequest { Id = 10_000 }).Response), "reward claims once");
        AssertEqual(20_104_060, Code(Call(h, "TRPGRewardRequest", new TRPGRewardRequest { Id = 1 }).Response), "unknown reward rejected");
        login = Relogin(h);
        AssertEqual("10000,11007", Join(Longs(login["TargetList"])), "state 3 login target list");
        AssertEqual("10130200,10130203", Join(Longs(login["StageList"])), "state 3 login stage list");
        AssertEqual("10000", Join(Longs(login["RewardList"])), "claimed reward persists");

        // Functions: story function persists and finishes its target; non-saved and repeated ones reject.
        (JObject fn, Dictionary<string, List<JObject>> fnPushes) = Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_011 });
        AssertEqual(0, Code(fn), "story function accepted");
        AssertEqual("11001", Join(Longs(fnPushes["NotifyTRPGTargetFinish"].Single()["TargetList"])), "function finishes its target");
        AssertEqual(20_104_011, Code(Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_011 }).Response), "function finishes once");
        AssertEqual(20_104_008, Code(Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 110_012 }).Response), "NeedSave 0 function rejected");
        AssertEqual(20_104_007, Code(Call(h, "TRPGFunctionFinishRequest", new TRPGFunctionFinishRequest { FunctionId = 1 }).Response), "unknown function rejected");
        login = Relogin(h);
        AssertEqual("110011", Join(Longs(login["FuncList"])), "finished function persists");
        AssertEqual("10000,11001,11007", Join(Longs(login["TargetList"])), "function target persists");

        // Memory story: needs 10 of item 61601 (the account holds 4), then spends them once.
        AssertEqual(20_104_059, Code(Call(h, "TRPGOpenMemoirRequest", new TRPGOpenMemoirRequest { Id = 1 }).Response), "memoir needs its unlock items");
        AssertEqual(20_104_057, Code(Call(h, "TRPGOpenMemoirRequest", new TRPGOpenMemoirRequest { Id = 99 }).Response), "unknown memoir rejected");
        inventory.Items.Single(x => x.Id == 61_601).Count = 12;
        AssertEqual(0, Code(Call(h, "TRPGOpenMemoirRequest", new TRPGOpenMemoirRequest { Id = 1 }).Response), "memoir opens with enough items");
        AssertEqual(2L, inventory.Items.Single(x => x.Id == 61_601).Count, "memoir spends its unlock items");
        AssertEqual(20_104_058, Code(Call(h, "TRPGOpenMemoirRequest", new TRPGOpenMemoirRequest { Id = 1 }).Response), "memoir opens once");
        AssertEqual("1", Join(Longs(Relogin(h)["MemoirList"])), "opened memoir persists");

        // A second account is untouched by the first one's progress, and one that cleared chapter stages before the
        // targets were persisted gets them back at login (recovery of already-lost progress).
        Player other = CreateDrawCompatibilityPlayer(uid + 1);
        AscNet.Common.Database.Stage otherStage = CreateLoginAccountCompatibilityStage(uid + 1);
        using LoopbackSessionHarness o = Harness(other, otherStage, CreateDrawCompatibilityInventory(uid + 1, []));
        NotifyTRPGData otherLogin = InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [o.Session]);
        AssertEqual(0, otherLogin.TargetList.Count + otherLogin.StageList.Count + otherLogin.FuncList.Count, "other account has no chapter progress");
        otherStage.AddStage(new StageDatum { StageId = 10_130_203, Passed = true });
        otherLogin = InvokePrivateStaticWithArgs<NotifyTRPGData>(trpgModule, "BuildLoginData", [o.Session]);
        AssertEqual("11007", Join(otherLogin.TargetList.Select(x => (long)x)), "pre-existing stage clear recovers its synced target at login");
        AssertEqual("10130203", Join(otherLogin.StageList.Select(x => (long)x)), "pre-existing stage clear shows in the stage list");
        AssertEqual(true, other.Trpg.Targets.SequenceEqual([11007]), "recovered target is stored on the player");
    }

    // Reads packets until the response for `id`, collecting pushes by name.
    private static JObject ReadUntilResponse(LoopbackSessionHarness harness, int id, string name, out Dictionary<string, List<JObject>> pushes)
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
}
