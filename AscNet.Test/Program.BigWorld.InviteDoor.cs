using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using MessagePack;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-interaction-only, issue #73 claim G: the shared Sky Garden invite door (level 6001 group 2012 place 3500002) has
        // interact options 1/2/3 (quests 2015/2016/2006 -> RequestEnterInstLevel 4040/4041/4044). Its LevelSceneObject row must decode
        // as a server-owned (IsOnlyClient 0) interactable object, so the level snapshot replicates it with an enabled interactable component.
        private static void ValidateBigWorldInviteDoor()
        {
            const long playerId = 99_922;
            const int levelId = 6001, doorPlaceId = 3500002;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            foreach (int questId in new[] { 2015, 2016, 2006 })
                player.BigWorldState.QuestData.ActiveQuests[questId] = new Theatre5DlcQuest { QuestId = questId, DynamicData = new Theatre5DlcQuestDynamicData { QuestState = 2 } };
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player,
                CreateDrawCompatibilityInventory(playerId, []), "big-world-invite-door");
            BigWorldCoreClient client = new(harness);
            Session session = harness.Session;
            player.BigWorldState.LastWorldId = 400;

            var door = BigWorldModule.SceneObjectsOf(levelId)[doorPlaceId];
            AssertEqual((0, 1), (door.IsOnlyClient, door.Interactable), "door row: server-owned and interactable");
            AssertEqual(true, BigWorldModule.InteractOptionsOf(levelId, BigWorldActors.SceneObjectType, doorPlaceId) is { } options
                && options.Keys.Order().SequenceEqual([1, 2, 3]), "door options 1/2/3");

            // Enters level 6001 from scratch (leaving any instance) and returns the door's replicate and the player's NPC uuid.
            (object?[] Door, int PlayerUuid) Enter()
            {
                BigWorldEnterWorldResponse enter = client.Call<BigWorldEnterWorldResponse>("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { LevelId = levelId });
                AssertEqual(0, enter.Code, "enter 6001");
                object?[] level = MessagePackSerializer.Deserialize<object?[]>(enter.EnterResultData!.LevelData!);
                List<object?[]> actors = ((object?[])((object?[])level[2]!)[0]!).Cast<object?[]>().ToList();
                AssertEqual(true, BigWorldActors.TryGetUuid(session, levelId, BigWorldActors.SceneObjectType, doorPlaceId, out int doorUuid), "door replicated in the 6001 snapshot");
                return (actors.Single(r => (string)r[0]! == "XSceneObject" && BwInt(r[5]) == doorUuid),
                    (int)BwInt(actors.First(r => (string)r[0]! == "XNpc" && BwInt(r[9]) == playerId)[5]));
            }
            object[] Interact(int doorUuid, int playerUuid, int optionId) => ["RpcPlayerInteractRequest",
                MessagePackSerializer.Serialize(new object[] { (int)playerId, 1, playerUuid, doorUuid, doorPlaceId, BigWorldActors.SceneObjectType, levelId, optionId }), (byte)15, (byte)1, levelId];

            foreach ((int optionId, int instLevelId) in new[] { (3, 4044), (1, 4040), (2, 4041) })
            {
                (object?[] doorActor, int playerUuid) = Enter();
                var interactable = MessagePackSerializer.Deserialize<object?[]>((byte[])doorActor[7]!).Cast<object?[]>().Where(c => (string)c[1]! == "XSceneObjectInteractableComponent").ToList();
                AssertEqual("91C3", string.Join(",", interactable.Select(c => Convert.ToHexString((byte[])c[0]!))), $"door replicates one enabled interactable component (option {optionId})");
                AssertEqual(0, client.Call<XRpcCommonResponse>("XRpcCommon", Interact((int)BwInt(doorActor[5]), playerUuid, optionId)).Code, $"door option {optionId} interact");
                AssertEqual(instLevelId, player.BigWorldState.InstLevelId, $"door option {optionId} enters instance {instLevelId}");
                AssertEqual(true, client.Pushes.Any(push => push.Name == "XRpcCommon" && (string)MessagePackSerializer.Deserialize<object?[]>(push.Content)[0]! == "RpcPlayerSwitchLevelNotify"),
                    $"door option {optionId} switches the client level");
            }
        }
    }
}
