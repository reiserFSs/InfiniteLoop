using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-core-only: BigWorldModule enter/inst/save channel/scene objects/settings/login pushes/engine modes.
        private static void ValidateBigWorldCore()
        {
            string? originalEngine = Environment.GetEnvironmentVariable("ASCNET_BIGWORLD_ENGINE");
            try
            {
                Environment.SetEnvironmentVariable("ASCNET_BIGWORLD_ENGINE", null);
                ValidateBigWorldFreshOpeningGuide();
                ValidateBigWorldCoreOnline();
                ValidateBigWorldSceneObjects();
                Environment.SetEnvironmentVariable("ASCNET_BIGWORLD_ENGINE", "offline");
                ValidateBigWorldCoreOffline();
            }
            finally
            {
                Environment.SetEnvironmentVariable("ASCNET_BIGWORLD_ENGINE", originalEngine);
            }
            Console.WriteLine("BigWorld core validation passed.");
        }

        private sealed class BigWorldCoreClient(LoopbackSessionHarness harness)
        {
            private int packetId = 40_000;
            public readonly List<(string Name, byte[] Content)> Pushes = [];

            // Invokes a request and collects every packet until the response (pushes before it) plus trailing pushes.
            public T Call<T>(string requestName, object? request)
            {
                Pushes.Clear();
                KeepAlive();
                int id = ++packetId;
                InvokeRegisteredRequestHandler(requestName, harness.Session, id, request);
                byte[]? response = null;
                while (response is null)
                {
                    Packet packet = harness.ReadPacket(requestName);
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        Pushes.Add((push.Name, push.Content));
                        continue;
                    }
                    Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(id, wire.Id, $"{requestName} response id");
                    response = wire.Content;
                }
                DrainTrailing();
                return MessagePackSerializer.Deserialize<T>(response);
            }

            // Requests without a response (DlcWorldEnterSucceed).
            public void Send(string requestName, object request)
            {
                Pushes.Clear();
                KeepAlive();
                InvokeRegisteredRequestHandler(requestName, harness.Session, ++packetId, request);
                DrainTrailing();
            }

            // Handlers are invoked directly, so nothing crosses the socket and the session's 10 s read idle timeout would drop
            // the client mid-test (the core test now runs past 10 s); a zero-length frame, ignored by the session, counts as traffic.
            private void KeepAlive() => harness.WriteClientBytes([0, 0, 0, 0]);

            private void DrainTrailing()
            {
                for (int idle = 0; idle < 3; idle++)
                {
                    while (harness.TryReadAvailablePacket("trailing push", out Packet packet))
                    {
                        idle = 0;
                        AssertEqual(Packet.ContentType.Push, packet.Type, "trailing packet type");
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        Pushes.Add((push.Name, push.Content));
                    }
                    Thread.Sleep(30);
                }
            }

            public T Pushed<T>(string name) => MessagePackSerializer.Deserialize<T>(Pushes.Single(push => push.Name == name).Content);
            public bool HasPush(string name) => Pushes.Any(push => push.Name == name);
            public List<object?[]> XRpcPushes(string envelope) => Pushes.Where(push => push.Name == envelope)
                .Select(push => MessagePackSerializer.Deserialize<object?[]>(push.Content)).ToList();
        }

        private static object? BigWorldModuleCall(string method, params object?[] args) =>
            RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldModule")
                .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(null, args);

        private static long BwInt(object? value) => Convert.ToInt64(value);

        // Scene objects vs retail level 4001 (same collected chests / activated teleporters as the captured account): every
        // retail XSceneObject is replicated with byte-identical content and components; the only extras are config objects
        // retail did not create for reasons not recoverable from the capture.
        private static void ValidateBigWorldSceneObjects()
        {
            // Config groups are per sector bundle: Commandant's Lounge (4003, SkyGarden_SuShe) shares group id 5001 with
            // District B (5001, SkyGarden) but not its objects; District B's teleporter 1100049 crashed the lounge load.
            var lounge = AscNet.GameServer.Handlers.BigWorld.BigWorldModule.SceneObjectsOf(4003);
            var districtB = AscNet.GameServer.Handlers.BigWorld.BigWorldModule.SceneObjectsOf(5001);
            AssertEqual((true, false, true, true), (lounge.Count > 0, lounge.ContainsKey(1100049), districtB.ContainsKey(1100049),
                lounge.Values.All(so => so.Sector == "skygarden_sushe")), "lounge objects come from its own sector bundle");
            const long playerId = 99_911;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.BigWorldState.ClaimedSceneObjects.Add(new BigWorldClaimedSceneObject { LevelId = 4001, PlaceId = 100007 });
            player.BigWorldState.ClaimedSceneObjects.Add(new BigWorldClaimedSceneObject { LevelId = 4001, PlaceId = 100018 });
            player.BigWorldState.TeleporterData[4001] = [100121, 100122];
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-scene-objects");
            BigWorldEnterWorldResponse enter = new BigWorldCoreClient(harness).Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, enter.Code, "enter");

            static Dictionary<long, (string Content, string Components)> SceneObjects(object?[] level) =>
                ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XSceneObject")
                    .ToDictionary(r => BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[1]),
                        r => (Convert.ToHexString((byte[])r[1]!), Convert.ToHexString((byte[])r[7]!)));
            var retail = SceneObjects(ExtractEnterBytes(Convert.FromBase64String(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigWorld", "big_world_enter_world_response.msgpack.b64")).Trim()), "LevelData"));
            object?[] level = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!);
            var ours = SceneObjects(level);
            AssertEqual(50, retail.Count, "retail scene object count");
            AssertEqual("", string.Join(",", retail.Keys.Where(placeId => !ours.ContainsKey(placeId))), "every retail scene object replicated");
            foreach ((long placeId, (string content, string components)) in retail)
                AssertEqual((content, components), ours[placeId], $"scene object {placeId} content/components");
            AssertEqual("100055,100056,100057,100091", string.Join(",", ours.Keys.Where(placeId => !retail.ContainsKey(placeId)).Order()), "extra scene objects");
            AssertEqual(false, ours.ContainsKey(100007) || ours.ContainsKey(100018), "collected chests not replicated");

            object?[] manager = ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Single(r => (string)r[0]! == "XSceneObjectManager");
            var listed = ((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])manager[1]!)[0]!).Select(BwInt).ToHashSet();
            var replicated = ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XSceneObject").Select(r => BwInt(r[5])).ToHashSet();
            AssertEqual(true, listed.SetEquals(replicated) && replicated.Count == ours.Count, "XSceneObjectManager lists every scene object UUID");

            // Default-loaded level NPCs vs retail: the same five place ids, content byte-identical, components byte-identical
            // except the 43 attribute records the 4.8 client added after the capture (first 105 records and 12 energies equal).
            static Dictionary<long, object?[]> LevelNpcs(object?[] level) =>
                ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0)
                    .ToDictionary(r => BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[3]));
            var retailNpcs = LevelNpcs(ExtractEnterBytes(Convert.FromBase64String(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigWorld", "big_world_enter_world_response.msgpack.b64")).Trim()), "LevelData"))
                .Where(entry => new long[] { 100002, 100039, 100114, 800002, 800003 }.Contains(entry.Key)).ToDictionary();
            var ourNpcs = LevelNpcs(level);
            AssertEqual("100002,100039,100114,800002,800003", string.Join(",", ourNpcs.Keys.Order()), "default-loaded level NPCs");
            foreach ((long placeId, object?[] want) in retailNpcs)
            {
                object?[] got = ourNpcs[placeId];
                AssertEqual(Convert.ToHexString((byte[])want[1]!), Convert.ToHexString((byte[])got[1]!), $"level NPC {placeId} content");
                AssertEqual(((byte)want[2]!, (bool)want[3]!), ((byte)got[2]!, (bool)got[3]!), $"level NPC {placeId} controller/IsAI");
                var wantComponents = MessagePackSerializer.Deserialize<object?[]>((byte[])want[7]!).Cast<object?[]>().ToList();
                var gotComponents = MessagePackSerializer.Deserialize<object?[]>((byte[])got[7]!).Cast<object?[]>().ToList();
                AssertEqual(string.Join(",", wantComponents.Select(c => c[1])), string.Join(",", gotComponents.Select(c => c[1])), $"level NPC {placeId} components");
                foreach ((object?[] w, object?[] g) in wantComponents.Zip(gotComponents))
                {
                    byte[] wb = (byte[]?)w[0] ?? [], gb = (byte[]?)g[0] ?? [];
                    if ((string)w[1]! == "XAttribComponent")
                        AssertEqual((true, true), (wb.AsSpan(0, 105 * 21).SequenceEqual(gb.AsSpan(0, 105 * 21)), wb.AsSpan(105 * 21).SequenceEqual(gb.AsSpan(148 * 21, 12 * 4))), $"level NPC {placeId} attrib");
                    else if ((string)w[1]! == "XNpcMoveComponent")
                    {
                        // Retail captured wandering ecological NPCs mid-route; a fresh level spawns them at the config placement.
                        object?[] move = MessagePackSerializer.Deserialize<object?[]>(gb);
                        var row = AscNet.Common.Util.TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelNpcTable>()
                            .First(r => r.PlaceId == placeId);
                        object?[] pos = (object?[])move[0]!;
                        AssertEqual((5, 3, 4, true, true, 0D, (float)row.PosX, (float)row.PosZ),
                            (move.Length, pos.Length, ((object?[])move[1]!).Length, move[2] is null, move[3] is null, Convert.ToDouble(move[4]),
                                Convert.ToSingle(pos[0]), Convert.ToSingle(pos[2])), $"level NPC {placeId} move at config placement");
                    }
                    else
                        AssertEqual(Convert.ToHexString(wb), Convert.ToHexString(gb), $"level NPC {placeId} {w[1]}");
                }
            }
            // Client-simulated NPC movement: the controlling client reports level-NPC poses (XRpcNpcPositionAndRotation) and the
            // next enter snapshot carries the last one (retail: 100002 is off its config placement); untouched NPCs stay at theirs.
            BigWorldCoreClient mover = new(harness);
            var uuids = ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0)
                .ToDictionary(r => BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])r[1]!)[3]), r => BwInt(r[5]));
            foreach ((long placeId, float x, float z) in new[] { (100002L, 561.25F, 1360.5F), (800002L, 600.5F, 1250.75F) })
                AssertEqual(0, mover.Call<XRpcComponentActionResponse>("XRpcComponentAction", new object[] { "XRpcNpcPositionAndRotation",
                    MessagePackSerializer.Serialize(new object[] { x, 145F, z, 0F, 0F, 0F, 1F }), (byte)15, (byte)1, 4001, (int)uuids[placeId] }).Code, $"NPC {placeId} pose ack");
            var moved = LevelNpcs(MessagePackSerializer.Deserialize<object?[]>(mover.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest()).EnterResultData!.LevelData!));
            static (float, float) XZ(object?[] npc) => MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>((byte[])npc[7]!)
                .Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0]!) is { } m ? (Convert.ToSingle(((object?[])m[0]!)[0]), Convert.ToSingle(((object?[])m[0]!)[2])) : default;
            AssertEqual(((561.25F, 1360.5F), (600.5F, 1250.75F)), (XZ(moved[100002]), XZ(moved[800002])), "reported NPC poses survive re-enter");
            AssertEqual(XZ(ourNpcs[100114]), XZ(moved[100114]), "unreported NPC stays at its placement");

            // BeScannedComponent: replicated iff the object's config has one (the firefly, 4027 place 59, is not a chest); the
            // client's RpcChangeActorScannedStateNotify persists and the next enter snapshot carries CurBeScanned = true.
            static List<object?[]> Components(object?[] replicate) => MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[7]!).Cast<object?[]>().ToList();
            static List<(string Name, string Snapshot)> Scans(object?[] replicate) => Components(replicate).Where(c => ((string)c[1]!).Contains("Scanned"))
                .Select(c => ((string)c[1]!, Convert.ToHexString((byte[])c[0]!))).ToList();
            object?[] Replicate(int levelId, AscNet.Table.V2.share.statussyncfight.level.sceneconfig.LevelSceneObjectTable so, int uuid) =>
                MessagePackSerializer.Deserialize<object?[]>(AscNet.GameServer.Handlers.BigWorld.BigWorldModule.BuildSceneObjectReplicate(player, levelId, so, uuid, 15, 1));
            var sceneObjects4027 = AscNet.GameServer.Handlers.BigWorld.BigWorldModule.SceneObjectsOf(4027);
            AssertEqual("XSceneObjectBeScannedComponent:91C2", string.Join(",", Scans(Replicate(4027, sceneObjects4027[59], 1263)).Select(s => $"{s.Name}:{s.Snapshot}")), "4027 place 59 replicates the scan component, hidden");
            AssertEqual(0, Scans(Replicate(4027, sceneObjects4027.Values.First(so => so.BeScanned == 0 && so.IsOnlyClient == 0), 1264)).Count, "object without config has no scan component");
            var enteredObjects = ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().Where(r => (string)r[0]! == "XSceneObject").ToList();
            object?[] chest = enteredObjects.First(r => Scans(r).Count == 1);
            object?[] noScan = enteredObjects.First(r => Scans(r).Count == 0);
            object[] Scan(object?[] actor, bool value) => ["RpcChangeActorScannedStateNotify", MessagePackSerializer.Serialize(new object[] { value }), (byte)15, (byte)1, 4001, (int)BwInt(actor[5])];
            AssertEqual(0, mover.Call<XRpcComponentActionResponse>("XRpcComponentAction", Scan(chest, true)).Code, "scan notify ack");
            AssertEqual(0, mover.Call<XRpcComponentActionResponse>("XRpcComponentAction", Scan(noScan, true)).Code, "scan notify on a non-scannable object ack");
            var rescanned = ((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>(mover.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest()).EnterResultData!.LevelData!)[2]!)[0]!)
                .Cast<object?[]>().Where(r => (string)r[0]! == "XSceneObject").ToDictionary(r => BwInt(r[5]));
            AssertEqual("91C3", Scans(rescanned[BwInt(chest[5])]).Single().Snapshot, "scanned state persisted into the next snapshot");
            AssertEqual(1, player.BigWorldState.ActorOverrides.Count(o => o.BeScanned == true), "only the scannable object persisted");
        }

        // Fresh account (never entered Babylonia): enter, opening guide 101 -> DIY -> 102 -> 103, re-fetch, save data, LoadComplete.
        private static void ValidateBigWorldFreshOpeningGuide()
        {
            foreach (long playerId in new long[] { 99_921, 99_922 })
            {
                using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
                Player player = CreateDrawCompatibilityPlayer(playerId);
                using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                    CreateDrawCompatibilityInventory(playerId, []), "big-world-fresh-guide");
                BigWorldCoreClient client = new(harness);
                BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
                AssertEqual((0, 0, false), (enter.Code, enter.PlayerData!.BigWorldGuideData.Count, enter.PlayerData.CharacterInitialized), "fresh enter: no guide, DIY not initialized");
                AssertEqual((true, false), (client.HasPush(nameof(NotifyNewEnteredBigWorldId)), client.HasPush(nameof(NotifyNewEnteredBigWorldLevelId))), "fresh enter pushes: world yes, level no (map model not built yet)");
                AssertEqual("4001", string.Join(",", enter.PlayerData.EnteredLevelIds), "fresh enter level in PlayerData");
                // Every team actor needs a decodable move snapshot (XNpcMoveComponent.Deserialize throws on nil).
                object?[] replicates = (object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!)[2]!)[0]!;
                foreach (object?[] actor in replicates.Cast<object?[]>().Where(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == playerId))
                {
                    byte[]? move = MessagePackSerializer.Deserialize<object?[]>((byte[])actor[7]!).Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0] as byte[];
                    AssertEqual(true, move is not null && MessagePackSerializer.Deserialize<object?[]>(move).Length == 5, "team actor move snapshot present");
                }
                AssertEqual(0, client.Call<BigWorldOnModuleLoadCompleteResponse>("BigWorldOnModuleLoadCompleteRequest", null).Code, "module load complete");

                // 101 (CG) -> 102 (DIY confirm with the outfit the client shows by default, XBigWorldCommanderDIYControl:RequestUpdate) -> 103 (CG).
                AssertEqual(0, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 101 }).Code, "guide 101");
                // First DIY save inside the guide opens UiBigWorldFirstPerson; its confirm sends BigWorldSaveFovDataRequest for level 4001's
                // perspective group (no BigWorldLevelFovSave row -> default group 0) and only continues on Code 0.
                AssertEqual(0, client.Call<BigWorldSaveFovDataResponse>("BigWorldSaveFovDataRequest", new BigWorldSaveFovDataRequest { FovType = 1, FovGroupId = 0 }).Code, "perspective confirm in the guide");
                int outfit = enter.PlayerData.CurCommanderOutfitType;
                AssertEqual(0, client.Call<AscNet.GameServer.Handlers.BigWorld.BigWorldCommanderFashionUpdateResponse>("BigWorldCommanderFashionUpdateRequest", new AscNet.GameServer.Handlers.BigWorld.BigWorldCommanderFashionUpdateRequest
                {
                    OutfitType = outfit, Gender = enter.PlayerData.Gender,
                    CommanderFashionList = enter.PlayerData.CommanderFashionOutfits[outfit].WearFashionDict
                }).Code, "DIY confirm with the default outfit");
                AssertEqual(0, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 102 }).Code, "guide 102");
                AssertEqual(0, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 103 }).Code, "guide 103");

                // XBigWorldOpenGuide:Finish (DIY ran) -> RequestGetEnterBigWorldData; OnOpenGuideFinished -> DlcWorldSaveData -> LoadComplete.
                BigWorldGetEnterWorldDataResponse again = client.Call<BigWorldGetEnterWorldDataResponse>("BigWorldGetEnterWorldDataRequest", null);
                AssertEqual(("101,102,103", true), (string.Join(",", again.PlayerData!.BigWorldGuideData), again.PlayerData.CharacterInitialized), "refetched enter data after the guide");
                AssertEqual(0, client.Call<DlcWorldSaveDataResponse>("DlcWorldSaveDataRequest", new DlcWorldSaveDataRequest { WorldId = 400 }).Code, "save data after the guide");
                harness.Session.BigWorldFightStartedAt = DateTime.UtcNow.AddSeconds(-5);
                InvokeRegisteredRequestHandler("LoadCompleteRequest", harness.Session, 41_000, new Dictionary<string, object>());
                AssertEqual("StartFightNotify", MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("start fight").Content).Name, "StartFightNotify after the guide");
            }
        }

        private static void ValidateBigWorldCoreOnline()
        {
            const long playerId = 99_901;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-core");
            BigWorldCoreClient client = new(harness);

            // ---- Login pushes before any entry: SysModule 1 (world 400) red, no entered worlds.
            NotifyBigWorldMainRedPoint redBefore = (NotifyBigWorldMainRedPoint)BigWorldModuleCall("BuildMainRedPoint", player)!;
            AssertEqual(true, redBefore.RedPoints[1], "red point before first entry");
            NotifyExternalRequiredBigWorldPlayerData external = (NotifyExternalRequiredBigWorldPlayerData)BigWorldModuleCall("BuildExternalRequiredPlayerData", player)!;
            AssertEqual(0, external.EnteredBigWorldIds.Count, "external entered ids before entry");

            // ---- Not in world yet.
            AssertEqual(20292004, client.Call<BigWorldOnModuleLoadCompleteResponse>("BigWorldOnModuleLoadCompleteRequest", null).Code, "module load complete outside world");
            AssertEqual(25000001, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 201 }).Code, "non-BigWorld world rejected");
            AssertEqual(20290019, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 400, LevelId = 4008 }).Code, "instance level rejected as world level");
            AssertEqual(20290019, client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 400, LevelId = 9999 }).Code, "other world's level rejected");

            // ---- Enter {0,0}: BigWorldConfig.FirstEnterDefaultWorldId 400 -> World.DefaultLevel 4001.
            BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual(0, enter.Code, "enter code");
            Theatre5WorldData world = enter.EnterResultData!.WorldData;
            AssertEqual((400, 4001, true, true, 4), (world.WorldId, world.LevelId, world.Online, world.IsSingleOnline, world.WorldType), "enter world data");
            AssertEqual((int)playerId, world.Players[0].Id, "world player id");
            AssertEqual(player.PlayerData.Name, world.Players[0].Name, "world player name");
            AssertEqual(true, client.HasPush("NotifyBigWorldMapData") && client.HasPush("NotifySgDormData"), "enter pushes");
            AssertEqual(400, client.Pushed<NotifyNewEnteredBigWorldId>(nameof(NotifyNewEnteredBigWorldId)).WorldId, "new world push");
            // XBigWorldMapModel._UnlockLevelMap exists only after UpdatePlayerData (BigWorldOnModuleLoadComplete): a level push in the enter burst throws in the client.
            AssertEqual(false, client.HasPush(nameof(NotifyNewEnteredBigWorldLevelId)), "enter announces its level through PlayerData, not a push");
            NotifyBigWorldMapData map = client.Pushed<NotifyBigWorldMapData>(nameof(NotifyBigWorldMapData));
            AssertEqual(0, map.BoxRewardedCntData[4001], "map box count 4001 before collect");
            AssertEqual(false, map.BoxRewardedCntData.ContainsKey(9999), "map data only lists this world's levels");
            AssertEqual(2, enter.PlayerData!.FovData.FovType, "unset fov falls back to BigWorldConfig.DefaultPerspective");
            AssertEqual("4001", string.Join(",", enter.PlayerData.EnteredLevelIds), "PlayerData.EnteredLevelIds");
            AssertEqual(false, ((NotifyBigWorldMainRedPoint)BigWorldModuleCall("BuildMainRedPoint", player)!).RedPoints[1], "red point cleared after entry");
            AssertEqual("400", string.Join(",", ((NotifyExternalRequiredBigWorldPlayerData)BigWorldModuleCall("BuildExternalRequiredPlayerData", player)!).EnteredBigWorldIds), "external entered ids after entry");

            // ---- Online-min engine snapshots (installed-client layouts; wire-shape oracle = retail capture).
            object?[] oracle = ExtractEnterBytes(Convert.FromBase64String(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigWorld", "big_world_enter_world_response.msgpack.b64")).Trim()), "FightData");
            object?[] fight = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData.FightData!);
            AssertEqual(oracle.Length, fight.Length, "RepFight key count matches retail");
            AssertEqual(0D, Convert.ToDouble(fight[0]), "RepFight.Time");
            var floats = (IDictionary<object, object>)fight[4]!;
            AssertEqual((-50D, -15D), (Convert.ToDouble(floats["JumpGravity"]), Convert.ToDouble(floats["FreeFallGravity"])), "FloatConfigDict from FightConfig");
            AssertEqual(2, ((object?[])fight[8]!).Length, "RepInitialQuests [Quests, FinishedQuestIds]");
            object?[] level = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData.LevelData!);
            AssertEqual(6, level.Length, "RepLevel key count matches retail");
            AssertEqual(4001L, BwInt(level[0]), "RepLevel.LevelId");
            object?[] controller = (object?[])((object?[])level[1]!)[0]!;
            AssertEqual((4001L, true, 1L, playerId, true, player.PlayerData.Name), (BwInt(controller[0]), (bool)controller[1]!, BwInt(controller[2]), BwInt(controller[3]), (bool)controller[5]!, (string)controller[6]!), "player RepController");
            object?[] replicates = (object?[])((object?[])level[2]!)[0]!;
            string[] managers = ["XSceneObjectManager", "XGameplayManager", "XLevelPlayTimer", "XLevelProcess", "XGameplayBigWorldMain"];
            int sceneObjectCount = replicates.Cast<object?[]>().Count(r => (string)r[0]! == "XSceneObject");
            int levelNpcCount = replicates.Cast<object?[]>().Count(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == 0);
            AssertEqual((true, 5), (sceneObjectCount > 0, levelNpcCount), "level scene objects and default-loaded level NPCs replicated");
            AssertEqual(string.Join(",", managers.Take(1).Concat(Enumerable.Repeat("XSceneObject", sceneObjectCount))
                    .Concat(Enumerable.Repeat("XNpc", levelNpcCount)).Concat(managers.Skip(1))
                    .Concat(Enumerable.Repeat("XNpc", world.Players[0].NpcList.Count))),
                string.Join(",", replicates.Cast<object?[]>().Select(r => (string)r[0]!)), "scene object manager, scene objects, level NPCs, other managers, team NPCs");
            object?[] firstReplicate = replicates.Cast<object?[]>().First(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == playerId);
            AssertEqual(("XNpc", 10, playerId), ((string)firstReplicate[0]!, firstReplicate.Length, BwInt(firstReplicate[9])), "XRpcActorReplicate shape");
            object?[] repNpc = MessagePackSerializer.Deserialize<object?[]>((byte[])firstReplicate[1]!);
            AssertEqual((23, 92, 2), (repNpc.Length, ((object?[])repNpc[7]!).Length, ((object?[])repNpc[10]!).Length), "XRepNpc keys / ENpcFlag counters / camera collision flags");
            // Retail sends NpcData.AttribsData = nil; an empty blob breaks dramas that clone the player NPC.
            AssertEqual(true, ((IDictionary<object, object>)repNpc[0]!)["AttribsData"] is null, "player NpcData.AttribsData is nil");
            int playerNpcUuid = (int)BwInt(firstReplicate[5]);
            AssertEqual((15L, 1L), (BwInt(((object?[])level[3]!)[2]), BwInt(level[4])), "ServerController id / MasterControllerId");

            // XNpc components vs the retail 4.5 commandant replicate (same Npc 3004 / DlcWorldAttrib 1002): snapshots are
            // byte-identical except Move (no saved pose yet), Part UUIDs (actor-UUID derived) and the 43 attribute records
            // the 4.8 client added after the capture.
            object?[] oracleLevel = ExtractEnterBytes(Convert.FromBase64String(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BigWorld", "big_world_enter_world_response.msgpack.b64")).Trim()), "LevelData");
            object?[] retailSelf = ((object?[])((object?[])oracleLevel[2]!)[0]!).Cast<object?[]>().Single(r => (string)r[0]! == "XNpc" && BwInt(r[5]) == 991);
            static List<(string Name, byte[]? Snapshot)> Components(object?[] replicate) =>
                MessagePackSerializer.Deserialize<object?[]>((byte[])replicate[7]!).Cast<object?[]>()
                    .Select(c => ((string)c[1]!, (byte[]?)c[0])).ToList();
            var expected = Components(retailSelf);
            var actual = Components(firstReplicate);
            AssertEqual(string.Join(",", expected.Select(c => c.Name)), string.Join(",", actual.Select(c => c.Name)), "XNpc component order");
            foreach (((string name, byte[]? want), (_, byte[]? got)) in expected.Zip(actual))
            {
                if (name == "XNpcMoveComponent")
                {
                    // XNpcMoveComponent.Deserialize throws on nil: a player without a saved pose stands at the level's authored start.
                    var start = AscNet.GameServer.Handlers.BigWorld.BigWorldModule.Levels.Value[4001];
                    object?[] pose = (object?[])MessagePackSerializer.Deserialize<object?[]>(got!)[0]!;
                    AssertEqual(((float)start.PositionX, (float)start.PositionY, (float)start.PositionZ), (Convert.ToSingle(pose[0]), Convert.ToSingle(pose[1]), Convert.ToSingle(pose[2])), "move snapshot at the level start before a saved pose");
                }
                else if (name == "XPartComponent")
                    AssertEqual("0,100201", string.Join(",", ((object?[])MessagePackSerializer.Deserialize<object?[]>(got!)[0]!).Select(p => BwInt(((object?[])p!)[0]))), "part ids from Part rows");
                else if (name == "XAttribComponent")
                    AssertEqual((148 * 21 + 13 * 4, true, true), (got!.Length, want!.AsSpan(0, 105 * 21).SequenceEqual(got.AsSpan(0, 105 * 21)), want.AsSpan(105 * 21).SequenceEqual(got.AsSpan(148 * 21, 12 * 4))), "attrib block from DlcWorldAttrib 1002");
                else
                    AssertEqual(Convert.ToHexString(want ?? []), Convert.ToHexString(got ?? []), $"{name} snapshot");
            }

            // Level managers vs retail: same shape once UUIDs are mapped (retail references its own UUIDs).
            var retailByName = ((object?[])((object?[])oracleLevel[2]!)[0]!).Cast<object?[]>().Where(r => managers.Contains((string)r[0]!)).ToDictionary(r => (string)r[0]!);
            var oursByName = replicates.Cast<object?[]>().Where(r => managers.Contains((string)r[0]!)).ToDictionary(r => (string)r[0]!);
            foreach (string name in new[] { "XLevelPlayTimer", "XLevelProcess", "XGameplayBigWorldMain", "XSceneObjectManager", "XGameplayManager" })
                AssertEqual(Convert.ToHexString((byte[])retailByName[name][7]!), Convert.ToHexString((byte[])oursByName[name][7]!), $"{name} components");
            AssertEqual(Convert.ToHexString((byte[])retailByName["XLevelPlayTimer"][1]!), Convert.ToHexString((byte[])oursByName["XLevelPlayTimer"][1]!), "XLevelPlayTimer content");
            object?[] process = MessagePackSerializer.Deserialize<object?[]>((byte[])oursByName["XLevelProcess"][1]!);
            AssertEqual((4001L, BwInt(oursByName["XSceneObjectManager"][5]), BwInt(oursByName["XGameplayManager"][5]), BwInt(oursByName["XLevelPlayTimer"][5]), 13),
                (BwInt(process[0]), BwInt(process[1]), BwInt(process[11]), BwInt(process[12]), process.Length), "XLevelProcess links its managers");
            AssertEqual(BwInt(oursByName["XGameplayBigWorldMain"][5]), BwInt(((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])oursByName["XGameplayManager"][1]!)[0]!)[0]), "XGameplayManager lists XGameplayBigWorldMain");
            AssertEqual((1L, BwInt(((object?[])level[3]!)[4])), (BwInt(oursByName["XLevelProcess"][5]), BwInt(oursByName["XLevelProcess"][8])), "XLevelProcess UUID 1; manager incId = server controller incId");

            AssertEqual(0, client.Call<BigWorldOnModuleLoadCompleteResponse>("BigWorldOnModuleLoadCompleteRequest", null).Code, "module load complete");

            // ---- Save data + LoadComplete bootstrap (online).
            AssertEqual(20292004, client.Call<DlcWorldSaveDataResponse>("DlcWorldSaveDataRequest", new DlcWorldSaveDataRequest { WorldId = 999 }).Code, "save data for another world");
            DlcWorldSaveDataResponse save = client.Call<DlcWorldSaveDataResponse>("DlcWorldSaveDataRequest", new DlcWorldSaveDataRequest { WorldId = 400 });
            AssertEqual((0, 400, 4001), (save.Code, save.WorldSaveData!.LevelDataDict[4001].WorldId, save.WorldSaveData.LevelDataDict[4001].LevelId), "save data current level");
            AssertEqual(true, save.WorldSaveData.LevelDataDict[4001].ReliablePos is null, "no persisted pose yet");
            harness.Session.BigWorldFightStartedAt = DateTime.UtcNow.AddSeconds(-5);
            InvokeRegisteredRequestHandler("LoadCompleteRequest", harness.Session, 41_000, new Dictionary<string, object>());
            AssertEqual("StartFightNotify", MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("start fight").Content).Name, "StartFightNotify first");
            AssertEqual(Packet.ContentType.Response, harness.ReadPacket("load complete response").Type, "LoadCompleteResponse second");
            string[] bootstrap = new string[4];
            object?[]? beginUpdate = null;
            for (int i = 0; i < 4; i++)
            {
                object?[] rpc = MessagePackSerializer.Deserialize<object?[]>(MessagePackSerializer.Deserialize<Packet.Push>(harness.ReadPacket("bootstrap").Content).Content);
                bootstrap[i] = (string)rpc[0]!;
                if (bootstrap[i] == "RpcSetCombatState")
                    AssertEqual(playerId, BwInt(((IDictionary<object, object>)MessagePackSerializer.Deserialize<object>((byte[])rpc[1]!))["PlayerId"]), "RpcSetCombatState PlayerId");
                if (bootstrap[i] == "RpcBeginUpdateLevel")
                    beginUpdate = MessagePackSerializer.Deserialize<object?[]>((byte[])rpc[1]!);
            }
            AssertEqual("RpcEcologyConstructClearStateUpdateNotify,RpcBeginCheckRLObjectCompleted,RpcSetCombatState,RpcBeginUpdateLevel", string.Join(",", bootstrap), "bootstrap order");
            AssertEqual(true, Convert.ToDouble(beginUpdate![0]) >= 5D, "RpcBeginUpdateLevel time from the clock");

            // ---- Scene objects: reward once, invalid placeId rejected, raw state round trip.
            Dictionary<string, object> Collected(bool active) => new() { ["Active"] = active, ["Flags"] = Array.Empty<int>(), ["IsInteractable"] = active, ["PlaceId"] = 100016, ["BeScanned"] = true,
                ["VarCompData"] = new Dictionary<string, object> { ["SyncNoteKeys"] = new[] { 7 }, ["IntDict"] = new Dictionary<int, int> { [5] = 3 }, ["FloatDict"] = new Dictionary<int, float> { [6] = 1.5f } } };
            AssertEqual(4, client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [999_999] = Collected(false) } }).Code, "unknown placeId rejected");
            AssertEqual(0, player.BigWorldState.SceneObjectStates.Count, "rejected state not persisted");
            DlcSceneObjectStateSetResponse set = client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [100016] = Collected(false) } });
            AssertEqual(true, set.Code == 0 && set.RewardGoods.Count > 0, "collectable reward granted");
            AssertEqual(1, client.Pushed<NotifyBigWorldBoxData>(nameof(NotifyBigWorldBoxData)).BoxRewardedCnt, "box count 1");
            AssertEqual(0, client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [100016] = Collected(false) } }).RewardGoods.Count, "second report grants nothing");
            AssertEqual(0, client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [100008] = Collected(true) } }).RewardGoods.Count, "non-collectable object grants nothing");
            var restored = (IDictionary<object, object>)client.Call<DlcWorldSceneObjectDataResponse>("DlcWorldSceneObjectDataRequest", new DlcWorldSceneObjectDataRequest { WorldId = 400, LevelId = 4001 }).SceneObjectStates[100016];
            AssertEqual((false, true), ((bool)restored["Active"], (bool)restored["BeScanned"]), "raw scene object state preserved (retail-only fields too)");
            var varComp = (IDictionary<object, object>)restored["VarCompData"];
            AssertEqual((3, 1.5, 7), (BwInt(((IDictionary<object, object>)varComp["IntDict"])[(byte)5]), Convert.ToDouble(((IDictionary<object, object>)varComp["FloatDict"])[(byte)6]), BwInt(((IEnumerable<object>)varComp["SyncNoteKeys"]).Single())), "XVarCompData replayed with the reported scene object state");
            AssertEqual(true, client.Call<DlcWorldSaveDataResponse>("DlcWorldSaveDataRequest", new DlcWorldSaveDataRequest { WorldId = 400 }).WorldSaveData!.LevelDataDict[4001].ActorSaveData.SoSaveDatas.ContainsKey(100016), "save data carries SoSaveDatas");
            // Client-hosted engine: the teleporter flag (2) in a reported scene-object state activates it; an empty Flags set does not.
            Dictionary<string, object> TeleporterState(int placeId, int[] flags) => new() { ["Active"] = true, ["Flags"] = flags, ["IsInteractable"] = true, ["PlaceId"] = placeId, ["BeScanned"] = false };
            AssertEqual(0, client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [100027] = TeleporterState(100027, []) } }).Code, "inactive teleporter state accepted");
            AssertEqual(false, player.BigWorldState.TeleporterData.GetValueOrDefault(4001)?.Contains(100027) ?? false, "teleporter without flag stays inactive");
            AssertEqual(0, client.Call<DlcSceneObjectStateSetResponse>("DlcSceneObjectStateSetRequest", new DlcSceneObjectStateSetRequest { WorldId = 400, LevelId = 4001, SceneObjectStates = new() { [100047] = TeleporterState(100047, [2]) } }).Code, "active teleporter state accepted");
            AssertEqual(100047, client.Pushed<NotifyBigWorldActivateTeleporter>(nameof(NotifyBigWorldActivateTeleporter)).PlaceId, "client-hosted teleporter activated");
            AssertEqual(true, player.BigWorldState.TeleporterData[4001].Contains(100047), "client-hosted teleporter persisted");

            // ---- Online interact: second chest via XRpc, then a teleporter.
            object[] Interact(int uuid, int placeId) => new object[] { "RpcPlayerInteractRequest",
                MessagePackSerializer.Serialize(new object[] { (int)playerId, 1, playerNpcUuid, uuid, placeId, 2, 4001, 1 }), (byte)15, (byte)1, 4001 };
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(95, 100017)).Code, "interact chest");
            AssertEqual(2, client.Pushed<NotifyBigWorldBoxData>(nameof(NotifyBigWorldBoxData)).BoxRewardedCnt, "box count 2 via interact");
            AssertEqual("RpcSceneObjectCollectNotify", (string)client.XRpcPushes("XRpcComponentAction").Single()[0]!, "collect component action");
            AssertEqual("RpcNpcInteractStartNotify,RpcNpcInteractFinishNotify", string.Join(",", client.XRpcPushes("XRpcCommon").Select(rpc => (string)rpc[0]!)), "interact start/finish");
            AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(96, 100122)).Code, "interact teleporter");
            AssertEqual(100122, client.Pushed<NotifyBigWorldActivateTeleporter>(nameof(NotifyBigWorldActivateTeleporter)).PlaceId, "teleporter activated");
            AssertEqual(4, client.Call<XRpcCommonResponse>("XRpcCommon", Interact(97, 123_456)).Code, "interact unknown place rejected");

            // ---- Positions: EnterSucceed + component position update.
            client.Send("DlcWorldEnterSucceedRequest", new DlcWorldEnterSucceedRequest { WorldId = 400, LevelId = 4001, LastPosition = new Theatre5DlcVector3 { X = 1, Y = 2, Z = 3 }, LastEulerAngles = new Theatre5DlcVector3 { Y = 450 } });
            AssertEqual((1D, 90D), (player.BigWorldState.LastPosition!.X, player.BigWorldState.LastRotationY!.Value), "EnterSucceed pose persisted");
            object[] position = new object[] { "XRpcNpcPositionAndRotation",
                MessagePackSerializer.Serialize(new object[] { 10.5F, 20F, 30F, 0F, 0.7071068F, 0F, 0.7071068F }), (byte)15, (byte)1, 4001, playerNpcUuid };
            AssertEqual(0, client.Call<XRpcComponentActionResponse>("XRpcComponentAction", position).Code, "position ack");
            AssertEqual((10.5D, 90), (player.BigWorldState.LastPosition!.X, (int)Math.Round(player.BigWorldState.LastRotationY!.Value)), "XRpc pose persisted for the player NPC uuid");
            // Client actor XRpcs (live client: XRpcNpcSetActiveNotify [true] on the player NPC) get XRpcActorActionResponse.
            object[] setActive = new object[] { "XRpcNpcSetActiveNotify", MessagePackSerializer.Serialize(new object[] { true }), (byte)0, (byte)1, 4001, playerNpcUuid };
            AssertEqual(0, client.Call<XRpcActorActionResponse>("XRpcActorAction", setActive).Code, "actor action ack");
            AssertEqual(4, client.Call<XRpcActorActionResponse>("XRpcActorAction", new object[] { "XRpcNpcSetActiveNotify" }).Code, "malformed actor action rejected");
            // Client-created actors (live 4033: XMissile Fangs shells) arrive as XRpcActorReplicate requests and get
            // XRpcActorReplicateResponse; a wrong level or a foreign controller is rejected.
            object[] Replicate(string type, int controller, int level, int uuid) => new object[] { type, MessagePackSerializer.Serialize(new object[] { 3 }),
                (byte)controller, true, level, uuid, 0, MessagePackSerializer.Serialize(Array.Empty<object>()), 2, 0 };
            AssertEqual((0, 0, 4, 4), (client.Call<XRpcActorReplicateResponse>("XRpcActorReplicate", Replicate("XMissile", 1, 4001, 33)).Code,
                client.Call<XRpcActorReplicateResponse>("XRpcActorReplicate", Replicate("XNpc", 1, 4001, 49)).Code,
                client.Call<XRpcActorReplicateResponse>("XRpcActorReplicate", Replicate("XMissile", 1, 4033, 65)).Code,
                client.Call<XRpcActorReplicateResponse>("XRpcActorReplicate", Replicate("XMissile", 15, 4001, 81)).Code),
                "client actor replicate: acked in the current level, rejected for another level or controller");

            // ---- Settings.
            AssertEqual(4, client.Call<BigWorldSaveFovDataResponse>("BigWorldSaveFovDataRequest", new BigWorldSaveFovDataRequest { FovType = 3 }).Code, "invalid fov type");
            AssertEqual(4, client.Call<BigWorldSaveFovDataResponse>("BigWorldSaveFovDataRequest", new BigWorldSaveFovDataRequest { FovType = 1, FovGroupId = 77 }).Code, "invalid fov group");
            AssertEqual(0, client.Call<BigWorldSaveFovDataResponse>("BigWorldSaveFovDataRequest", new BigWorldSaveFovDataRequest { FovType = 1 }).Code, "default fov");
            AssertEqual(0, client.Call<BigWorldSaveFovDataResponse>("BigWorldSaveFovDataRequest", new BigWorldSaveFovDataRequest { FovType = 2, FovGroupId = 1 }).Code, "group fov");
            AssertEqual(20292001, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 99 }).Code, "unknown guide");
            AssertEqual(0, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 101 }).Code, "guide 101");
            AssertEqual(20292003, client.Call<BigWorldGuideOpenResponse>("BigWorldGuideOpenRequest", new BigWorldGuideOpenRequest { GuideId = 101 }).Code, "guide 101 again");
            AssertEqual(4, client.Call<BigWorldMarkCustomParamResponse>("BigWorldMarkCustomParamRequest", new BigWorldMarkCustomParamRequest { Id = 7 }).Code, "unknown custom param");
            AssertEqual(0, client.Call<BigWorldMarkCustomParamResponse>("BigWorldMarkCustomParamRequest", new BigWorldMarkCustomParamRequest { Id = 1001 }).Code, "mark 1001");
            AssertEqual(0, client.Call<BigWorldMarkCustomParamResponse>("BigWorldMarkCustomParamRequest", new BigWorldMarkCustomParamRequest { Id = 1002 }).Code, "mark 1002");
            AssertEqual(0, client.Call<BigWorldMarkCustomParamResponse>("BigWorldMarkCustomParamRequest", new BigWorldMarkCustomParamRequest { Id = 1001, IsUnmark = true }).Code, "unmark 1001");
            AssertEqual(20292002, client.Call<BigWorldCheckIsShowMainRedPointResponse>("BigWorldCheckIsShowMainRedPointRequest", new BigWorldCheckIsShowMainRedPointRequest { SysModuleId = 9 }).Code, "unknown sys module");
            AssertEqual(0, client.Call<BigWorldCheckIsShowMainRedPointResponse>("BigWorldCheckIsShowMainRedPointRequest", new BigWorldCheckIsShowMainRedPointRequest { SysModuleId = 1 }).Code, "red point check");
            AssertEqual(false, client.Pushed<NotifyBigWorldMainRedPoint>(nameof(NotifyBigWorldMainRedPoint)).RedPoints[1], "red point push before response");
            AssertEqual(4, client.Call<BigWorldSetTrackMapPinIdResponse>("BigWorldSetTrackMapPinIdRequest", new BigWorldSetTrackMapPinIdRequest { Opt = 1, MapTrackPinData = new() { WorldId = 400, LevelId = 4001 } }).Code, "begin track needs a pin");
            AssertEqual(0, client.Call<BigWorldSetTrackMapPinIdResponse>("BigWorldSetTrackMapPinIdRequest", new BigWorldSetTrackMapPinIdRequest { Opt = 1, MapTrackPinData = new() { WorldId = 400, LevelId = 4001, TrackPinId = 12 } }).Code, "track pin");
            AssertEqual(4, client.Call<BigWorldCurNpcPosUpdateResponse>("BigWorldCurNpcPosUpdateRequest", new BigWorldCurNpcPosUpdateRequest { WorldId = 400, LevelId = 4001, CurNpcPos = 9 }).Code, "cur npc pos out of team");

            // ---- Quest trace / environment / invite (BigWorldQuestModule through the core session).
            AssertEqual(25100001, client.Call<DlcQuestTraceIdChangeResponse>("DlcQuestTraceIdChangeRequest", new DlcQuestTraceIdChangeRequest { ChangeTraceQuestId = 999_999 }).Code, "trace unknown quest");
            AssertEqual(25100046, client.Call<DlcEnvironmentQuestGroupChangeResponse>("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 4001, QuestGroupId = 2 }).Code, "environment group of another level");
            DlcEnvironmentQuestGroupChangeResponse env = client.Call<DlcEnvironmentQuestGroupChangeResponse>("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 5001, QuestGroupId = 2 });
            AssertEqual((0, 2), (env.Code, env.EnvironmentQuestData!.ActivatedQuestGroupIds[5001]), "environment group change");
            AssertEqual(25100001, client.Call<DlcInviteQuestAcceptResponse>("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2002 }).Code, "invite accept of a non-invite quest");
            AssertEqual(25100040, client.Call<DlcInviteQuestAcceptResponse>("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }).Code, "invite condition unmet");
            AssertEqual(25100042, client.Call<DlcInviteQuestResultNumRewardResponse>("DlcInviteQuestResultNumRewardRequest", new DlcInviteQuestResultNumRewardRequest { QuestId = 2007 }).Code, "invite total reward needs every ending");

            // ---- Instance levels.
            AssertEqual(20290019, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest()).Code, "leave inst when not in one");
            AssertEqual(20290019, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 9999 }).Code, "foreign inst level");
            AssertEqual(20292004, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 999, InstLevelId = 4008 }).Code, "inst level of another world");
            // The engine's in-world transfer command sends WorldId 0 (= current world).
            client.Pushes.Clear();
            EnterInstLevelResponse inst = client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 0, InstLevelId = 4008 });
            AssertEqual((0, 400, 4008), (inst.Code, inst.EnterResultData!.WorldData.WorldId, inst.EnterResultData.WorldData.LevelId), "enter inst 4008 with WorldId 0");
            AssertEqual(4008, client.Pushed<NotifyNewEnteredBigWorldLevelId>(nameof(NotifyNewEnteredBigWorldLevelId)).LevelId, "inst new level push");
            // The client only passes EnterResultData to a callback: the server moves it with a level switch (live: Space Leap
            // 4008 did nothing), and quest activation goes over XRpc, never the Lua-less NotifyDlcQuestActivate.
            AssertEqual((true, false), (client.XRpcPushes("XRpcCommon").Any(rpc => (string)rpc[0]! == "RpcPlayerSwitchLevelNotify"
                    && BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])rpc[1]!)[1]) == 4008),
                client.Pushes.Any(p => p.Name == nameof(NotifyDlcQuestActivate))), "inst entry switches the level; no NotifyDlcQuestActivate");
            AssertEqual((4008, 4001), (player.BigWorldState.InstLevelId, player.BigWorldState.LastLevelId), "inst tracked, open world kept");
            // A request for the level the player already stands on (issue #73 A) is a re-entry, not an unknown level: only the pose moves
            // and the switch still goes out (the client loads it as ReenterLevel, EFightState 6).
            client.Pushes.Clear();
            EnterInstLevelResponse reentry = client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008, TargetPos = new Theatre5DlcVector3 { X = 9 } });
            AssertEqual((0, 9.0, true), (reentry.Code, player.BigWorldState.InstPosition!.X, client.XRpcPushes("XRpcCommon").Any(rpc => (string)rpc[0]! == "RpcPlayerSwitchLevelNotify"
                && BwInt(MessagePackSerializer.Deserialize<object?[]>((byte[])rpc[1]!)[1]) == 4008)), "entering the current level is a re-entry");
            AssertEqual(0, client.Call<DlcReChallengeInstResponse>("DlcReChallengeInstRequest", null).Code, "rechallenge");
            AssertEqual(4, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 5 }).Code, "invalid save option");
            AssertEqual(0, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 2 }).Code, "leave inst");
            AssertEqual(0, player.BigWorldState.InstLevelId, "inst cleared");
            EnterInstLevelResponse district = client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 5001, TargetPos = new Theatre5DlcVector3 { X = 7 } });
            AssertEqual((0, 5001, 0), (district.Code, player.BigWorldState.LastLevelId, player.BigWorldState.InstLevelId), "open-world level transfer");
            // Gameplay actors follow XGameplay*.NeedCreate (gameplay config GameplayLevelId): 4008 has none, District B the UAV
            // gameplay, the lounge the dormitory (its client half builds the room); BigWorldMain stays in 4001 only.
            static string Gameplays(EnterInstLevelResponse r) => string.Join(",",
                ((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>(r.EnterResultData!.LevelData!)[2]!)[0]!)
                .Cast<object?[]>().Select(a => (string)a[0]!).Where(n => n.StartsWith("XGameplay") && n != "XGameplayManager"));
            EnterInstLevelResponse lounge = client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4003 });
            AssertEqual(("", "XGameplayUAV", "XGameplayDormitory"), (Gameplays(inst), Gameplays(district), Gameplays(lounge)), "level gameplay actors");

            // ---- Jumper runs are not resumed on relog; the two exit options (LeaveInstLevelRequest SaveExit 1 / NoSaveExit 2).
            BigWorldPlayerState bws = player.BigWorldState;
            Theatre5DlcVector3 Vec3(float x, float y, float z) => new() { X = x, Y = y, Z = z };
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4001, TargetPos = Vec3(50, 60, 70) }).Code, "open-world pose set");
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008, TargetPos = Vec3(1, 2, 3) }).Code, "enter 4008 for relog");
            AssertEqual(0, client.Call<XRpcComponentActionResponse>("XRpcComponentAction", new object[] { "XRpcNpcPositionAndRotation",
                MessagePackSerializer.Serialize(new object[] { 11.5F, 12F, 13F, 0F, 0.7071068F, 0F, 0.7071068F }), (byte)15, (byte)1, 4008, playerNpcUuid }).Code, "instance position ack");
            AssertEqual((11.5, 50.0), (bws.InstPosition!.X, bws.LastPosition!.X), "instance pose saved apart from the open-world pose");
            // The team's move snapshot uses the instance pose (live: a resumed 4008 put the team at District A coordinates).
            object?[] comps = MessagePackSerializer.Deserialize<object?[]>(AscNet.GameServer.Handlers.BigWorld.BigWorldModule.BuildNpcComponents(player,
                AscNet.GameServer.Handlers.BigWorld.BigWorldModule.BuildNpcList(player)[bws.CurNpcPos], 0, false));
            object?[] move = MessagePackSerializer.Deserialize<object?[]>((byte[])comps.Cast<object?[]>().Single(c => (string)c[1]! == "XNpcMoveComponent")[0]!);
            AssertEqual(11.5, Convert.ToDouble(((object?[])move[0]!)[0]), "team stands at the instance pose");
            // Relog: a Space Leap run is never saved (Level.ShouldSaveInstLevelProgress 0), so the entrance puts the player in the open
            // world at the saved pose; the run restarts on the next entry (live: the resumed stale run settled at once).
            BigWorldEnterWorldResponse resumed = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            Theatre5WorldPlayerBornData born = resumed.EnterResultData!.WorldData.Players[0].BornData!;
            AssertEqual((0, 4001, 0, 4001, 50f), (resumed.Code, resumed.EnterResultData.WorldData.LevelId, bws.InstLevelId, bws.LastLevelId, born.Position!.X), "relog from a jumper run lands in the open world");
            // SaveExit: back to the saved open-world pose through a level switch; a fresh run was started by the entry.
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008, TargetPos = Vec3(1, 2, 3) }).Code, "re-enter 4008");
            client.Pushes.Clear();
            AssertEqual(0, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 1 }).Code, "save exit");
            AssertEqual(true, client.XRpcPushes("XRpcCommon").Any(rpc => (string)rpc[0]! == "RpcPlayerSwitchLevelNotify"), "save exit switches the level");
            AssertEqual((0, 4001, 50.0, 2), (bws.InstLevelId, bws.LastLevelId, bws.LastPosition!.X, bws.QuestData.ActiveQuests[5001].DynamicData!.QuestState), "save exit: open-world pose, quest progress kept");
            // NoSaveExit: the instance quest returns to Ready (progress discarded).
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4008, TargetPos = Vec3(1, 2, 3) }).Code, "re-enter 4008");
            client.Pushes.Clear();
            AssertEqual(0, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 2 }).Code, "no-save exit");
            string pushed = string.Join(",", client.XRpcPushes("XRpcCommon").Select(rpc => (string)rpc[0]!));
            AssertEqual(true, pushed.Contains("RpcRemoveQuest,RpcActivateQuests") && pushed.Contains("RpcPlayerSwitchLevelNotify"), "no-save exit rolls the quest back and switches the level");
            AssertEqual((0, 1, true, 50.0), (bws.InstLevelId, bws.QuestData.ActiveQuests[5001].DynamicData!.QuestState, bws.QuestData.ReadyQuestIds.Contains(5001), bws.LastPosition!.X), "no-save exit: quest Ready again, open-world pose kept");

            // ---- Instance 4033 with its trial team (the commander armor, AddTrialNpcToTeam Cover CurNpcPos 1 on a 1-member team).
            // Live: RepFight.TrialCurNpcPos 1 became the client's XController._curNpcPos [DUMP48 InitCurNpcPos 0x1B033F0], CurNpc threw
            // KeyNotFound, the level switch / LeaveWorld hung; Save and Exit then left the armor in the square (stale trial team);
            // the trial NPC's pose reports were not saved (its uuid was not the team's) and a fall (OnAirType 1, y=-62) is not a pose.
            bws.Teams[bws.CurrentTeamId] = [new() { CharacterId = 2011001, Pos = 0 }, new() { CharacterId = 1531005, Pos = 1 }, new() { CharacterId = 1041005, Pos = 2 }];
            bws.CurNpcPos = 1;
            int realTeam = AscNet.GameServer.Handlers.BigWorld.BigWorldModule.BuildNpcList(player).Count;
            AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = 4033, TargetPos = Vec3(87.81f, 3.283f, 65) }).Code, "enter 4033");
            lock (Session.GetPlayerOperationLock(player.PlayerData.Id))
                AscNet.GameServer.Handlers.BigWorld.BigWorldTrialTeam.Add(harness.Session, 4033, [999910301], 1, 1, null);
            AssertEqual((1, 0), (bws.TrialNpcIds.Count, bws.CurNpcPos), "trial team covers the team; CurNpcPos is a member of it");
            // Live 4033 black box: the trial NPC's empty AttribsData made XDrama.ClonePlayerNpc (Drama_1002_010) overrun XAttrib.Deserialize
            // and the drama never finished; every NPC the client can clone must carry nil AttribsData like retail.
            AssertEqual(true, AscNet.GameServer.Handlers.BigWorld.BigWorldModule.BuildNpcList(player).All(n => n.AttribsData is null), "trial team NpcData.AttribsData is nil");
            object?[] trialFight = MessagePackSerializer.Deserialize<object?[]>(client.Call<BigWorldGetEnterWorldDataResponse>("BigWorldGetEnterWorldDataRequest", null).EnterResultData!.FightData!);
            AssertEqual((0L, 1L), (BwInt(trialFight[6]), BwInt(trialFight[7])), "RepFight TrialCurNpcPos stays inside the 1-member trial team");
            int trialUuid = AscNet.GameServer.Handlers.BigWorld.BigWorldActors.TeamNpcUuid(harness.Session, 4033, 0);
            object[] Pose(float y, int onAir) => ["XRpcNpcPositionAndRotation",
                MessagePackSerializer.Serialize(new object[] { 90F, y, 66F, 0F, 0.7071068F, 0F, 0.7071068F, 2, 3, 1, onAir, false, 0, 0, 1 }), (byte)15, (byte)1, 4033, trialUuid];
            AssertEqual(0, client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Pose(3.3F, 0)).Code, "grounded trial pose ack");
            AssertEqual(0, client.Call<XRpcComponentActionResponse>("XRpcComponentAction", Pose(-62F, 1)).Code, "airborne pose ack");
            AssertEqual((90D, 3.3F), (bws.InstPosition!.X, (float)bws.InstPosition.Y), "the controlled trial NPC's grounded pose is saved, a fall is not");
            // Save and Exit: the real team returns in the state and in the level switch.

            client.Pushes.Clear();
            AssertEqual(0, client.Call<LeaveInstLevelResponse>("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 1 }).Code, "4033 save exit");
            object?[] exitSwitch = client.XRpcPushes("XRpcCommon").Single(rpc => (string)rpc[0]! == "RpcPlayerSwitchLevelNotify");
            object?[] exitLevel = MessagePackSerializer.Deserialize<object?[]>((byte[])MessagePackSerializer.Deserialize<object?[]>((byte[])exitSwitch[1]!)[3]!);
            int exitTeam = ((object?[])((object?[])exitLevel[2]!)[0]!).Cast<object?[]>().Count(r => (string)r[0]! == "XNpc" && BwInt(r[9]) != 0);
            AssertEqual((0, 0, 1, true, true), (bws.TrialNpcIds.Count, bws.TrialAddMode, bws.CurNpcPos, exitTeam == realTeam && realTeam > 1,
                bws.QuestData.ActiveQuests.Values.All(q => q.DynamicData?.TrialCharacterIdList.Count is null or 0)), "save exit drops the trial team, restores CurNpcPos, switch replicates the real team");
            // LeaveWorld from that state answers and detaches.
            InvokeRegisteredRequestHandler("LeaveWorldRequest", harness.Session, 41_002, new Dictionary<string, object>());
            Packet leaveResponse;
            do leaveResponse = harness.ReadPacket("4033 leave world response"); while (leaveResponse.Type != Packet.ContentType.Response);
            AssertEqual("LeaveWorldResponse", MessagePackSerializer.Deserialize<Packet.Response>(leaveResponse.Content).Name, "leave world answered");
            AssertEqual(0, harness.Session.BigWorldWorldId, "session detached after the instance exit");
            client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());

            // ---- Relog inside an unsaved story instance (Level.ShouldSaveInstLevelProgress 0 for every instance): the entrance does not
            // resume it. 4033 with its trial team and 4032 without one: the player lands in the open world at the saved pose, the real
            // team and CurNpcPos are back, and the instance quest returns to Ready so the next entry replays the intro (and with it the
            // trial-team swap that turns the heavy-artillery aim input map on).
            foreach (int instance in new[] { 4033, 4032 })
            {
                bws.CurNpcPos = 1;
                AssertEqual(0, client.Call<EnterInstLevelResponse>("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = 400, InstLevelId = instance, TargetPos = Vec3(87.81f, 3.283f, 65) }).Code, $"enter {instance}");
                if (instance == 4033)
                    lock (Session.GetPlayerOperationLock(player.PlayerData.Id))
                        AscNet.GameServer.Handlers.BigWorld.BigWorldTrialTeam.Add(harness.Session, 4033, [999910301], 1, 0, null);
                int instanceQuest = bws.QuestData.ActiveQuests.Values.Single(q => AscNet.GameServer.Handlers.BigWorld.BigWorldQuestModule.Quests.Value.TryGetValue(q.QuestId, out var c) && c.Category is 1 or 2 && c.LevelId == instance).QuestId;
                AssertEqual((instance, 2), (bws.InstLevelId, bws.QuestData.ActiveQuests[instanceQuest].DynamicData!.QuestState), $"{instance} run in progress before the relog");
                BigWorldEnterWorldResponse relog = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
                AssertEqual((0, 4001, 0, 4001, 0, 1), (relog.Code, relog.EnterResultData!.WorldData.LevelId, bws.InstLevelId, bws.LastLevelId, bws.TrialNpcIds.Count,
                    bws.QuestData.ActiveQuests[instanceQuest].DynamicData!.QuestState), $"relog from {instance} lands in the open world, trial team gone, quest Ready again");
                AssertEqual((realTeam, 1), (relog.EnterResultData.WorldData.Players[0].NpcList.Count, relog.EnterResultData.WorldData.Players[0].CurNpcPos), $"{instance}: real team and CurNpcPos restored in the entry");
            }

            // ---- Babylonia tutorial guides use the generic Guide* requests with BigWorldGuideGroup ids.
            AssertEqual(0, client.Call<GuideOpenResponse>("GuideOpenRequest", new GuideCompleteRequest { GuideGroupId = 200281 }).Code, "BigWorld guide open");
            AssertEqual(0, client.Call<GuideCompleteResponse>("GuideCompleteRequest", new GuideCompleteRequest { GuideGroupId = 200281 }).Code, "BigWorld guide complete");
            AssertEqual(200281, client.Pushed<NotifyGuide>(nameof(NotifyGuide)).GuideGroupId, "BigWorld guide notify");
            AssertEqual(0, client.Call<GuideCompleteResponse>("GuideCompleteRequest", new GuideCompleteRequest { GuideGroupId = 200281 }).Code, "BigWorld guide complete retry");
            AssertEqual(0, client.Call<GuideGroupFinishResponse>("GuideGroupFinishRequest", new GuideGroupFinishRequest { GroupId = 200100 }).Code, "BigWorld guide group finish");
            AssertEqual(1, client.Call<GuideCompleteResponse>("GuideCompleteRequest", new GuideCompleteRequest { GuideGroupId = 299999 }).Code, "unknown guide rejected");
            AssertEqual(1, player.PlayerData.GuideData.Count(id => id == 200281), "BigWorld guide recorded once");
            AssertEqual(true, player.PlayerData.GuideData.Contains(200100), "BigWorld guide group recorded");

            // ---- Leave, persistence, re-enter a second world.
            InvokeRegisteredRequestHandler("LeaveWorldRequest", harness.Session, 41_001, new Dictionary<string, object>());
            _ = harness.ReadPacket("leave world response");
            AssertEqual(0, harness.Session.BigWorldWorldId, "session detached");
            Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            BigWorldPlayerState saved = reloaded.BigWorldState;
            AssertEqual(("400", "4001,4003,4008,4032,4033,5001", "1002", "101"), (string.Join(",", saved.EnteredWorldIds), string.Join(",", saved.EnteredLevelIds.Order()), string.Join(",", saved.CustomParamMarkData), string.Join(",", saved.GuideData)), "persisted ids");
            AssertEqual((1, 2, 12), (saved.FovType, saved.LevelFovDatas[1], saved.MapTrackPin!.TrackPinId), "persisted fov/pin");
            AssertEqual("100016,100017", string.Join(",", saved.ClaimedSceneObjects.Select(claim => claim.PlaceId).Order()), "persisted collected objects");
            AssertEqual("100047,100122", string.Join(",", saved.TeleporterData[4001]), "persisted teleporters");
            AssertEqual(true, saved.SceneObjectStates[4001].States.ContainsKey(100016), "persisted raw states");
            harness.Session.player = reloaded;
            player = reloaded;

            BigWorldEnterWorldResponse second = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 999 });
            AssertEqual((0, 999, 9999), (second.Code, second.EnterResultData!.WorldData.WorldId, second.EnterResultData.WorldData.LevelId), "second world default level");
            AssertEqual(999, client.Pushed<NotifyNewEnteredBigWorldId>(nameof(NotifyNewEnteredBigWorldId)).WorldId, "second world new push");
            BigWorldEnterWorldResponse back = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 400, LevelId = 5001 });
            AssertEqual((0, 5001), (back.Code, back.EnterResultData!.WorldData.LevelId), "explicit level enter");
            AssertEqual(false, client.HasPush(nameof(NotifyNewEnteredBigWorldId)) || client.HasPush(nameof(NotifyNewEnteredBigWorldLevelId)), "no new-entry pushes on re-entry");
            AssertEqual(2, back.PlayerData!.CustomParamMarkData.Count + back.PlayerData.BigWorldGuideData.Count, "PlayerData from persisted settings");
            AssertEqual((1, 2), (back.PlayerData.FovData.FovType, back.PlayerData.FovData.LevelFovDatas[1]), "PlayerData.FovData");
            AssertEqual(2, client.Pushed<NotifyBigWorldMapData>(nameof(NotifyBigWorldMapData)).BoxRewardedCntData[4001], "map data box count after reload");
            AssertEqual(2, back.PlayerData.EnvironmentQuestData.ActivatedQuestGroupIds[5001], "environment group persisted");
        }

        private static void ValidateBigWorldCoreOffline()
        {
            const long playerId = 99_902;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-core-offline");
            BigWorldCoreClient client = new(harness);
            BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest());
            AssertEqual((0, false, true), (enter.Code, enter.EnterResultData!.WorldData.Online, enter.EnterResultData.WorldData.IsSingleOnline), "offline world data");
            AssertEqual(true, enter.EnterResultData.FightData is null && enter.EnterResultData.LevelData is null, "offline has no engine snapshots");
            AssertEqual(0, client.Call<DlcWorldSaveDataResponse>("DlcWorldSaveDataRequest", new DlcWorldSaveDataRequest { WorldId = 400 }).Code, "offline save data");
            AssertEqual(false, harness.Session.PendingBigWorldStartFightNotify || harness.Session.PendingBigWorldLoadCompleteXRpc, "offline sets no fight bootstrap");
        }

        private static object?[] ExtractEnterBytes(byte[] enterResponse, string field)
        {
            var map = (IDictionary<object, object>)MessagePackSerializer.Deserialize<object>(enterResponse);
            var result = (IDictionary<object, object>)map["EnterResultData"];
            return MessagePackSerializer.Deserialize<object?[]>((byte[])result[field]);
        }
    }
}
