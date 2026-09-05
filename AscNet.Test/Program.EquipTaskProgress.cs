using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.Test;

internal static partial class Program
{
    private static void ValidateEquipTaskProgressCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> players,
            out RecordingMongoCollectionProxy<Character> characters,
            out RecordingMongoCollectionProxy<Inventory> inventories);
        const long uid = 99_780;
        const int taskId = 3520; // CurrentCondition 12203: five equipment awakening operations.
        Player player = CreateDrawCompatibilityPlayer(uid);
        Character character = CreateDrawCompatibilityCharacter(uid);
        character.Equips =
        [
            new EquipData
            {
                Id = 1837, TemplateId = 3036008, Level = 45, Breakthrough = 4,
                ResonanceInfo = [new ResonanceInfo { Slot = 1 }, new ResonanceInfo { Slot = 2 }],
                AwakeSlotList = []
            }
        ];
        Inventory inventory = CreateDrawCompatibilityInventory(uid,
        [
            new Item { Id = Inventory.Coin, Count = 200_000 },
            new Item { Id = 70001, Count = 2_000 },
            new Item { Id = 70002, Count = 1_000 }
        ]);
        using LoopbackSessionHarness harness = new(character, player, inventory, "equipment-task-progress");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        MethodInfo dispatch = typeof(Session).GetMethod("InvokeRequestHandler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Session).FullName, "InvokeRequestHandler");
        int packetId = 47_800;
        CheckProgress(0, "Initial awakening progress");

        (EquipQuickAwakeResponse rejected, List<SyncTask> rejectedTasks) = Dispatch(new EquipQuickAwakeRequest
        {
            EquipQuickAwakeInfos = [new EquipQuickAwakeInfo { EquipId = 1837, Slots = [1, 1] }]
        });
        AssertEqual(20021038, rejected.Code, "Duplicate-slot awakening is rejected");
        AssertEqual(0, character.Equips.Single().AwakeSlotList.Count, "Rejected awakening commits no slots");
        AssertEqual(false, rejectedTasks.Any(task => task.Id == taskId), "Rejected awakening publishes no progress");
        CheckProgress(0, "Rejected awakening leaves task unchanged");

        EquipQuickAwakeRequest request = new()
        {
            EquipQuickAwakeInfos = [new EquipQuickAwakeInfo { EquipId = 1837, Slots = [1, 2] }]
        };
        (EquipQuickAwakeResponse accepted, List<SyncTask> acceptedTasks) = Dispatch(request);
        AssertEqual(0, accepted.Code, "Two-slot awakening succeeds");
        AssertIntegerList([1, 2], character.Equips.Single().AwakeSlotList.Select(Convert.ToInt64).ToArray(),
            "Two-slot awakening commits both slots");
        AssertEqual(2, acceptedTasks.Last(task => task.Id == taskId).Schedule.Single().Value,
            "One request publishes two committed awakening units");
        CheckProgress(2, "Two-slot awakening task progress");

        harness.Session.player = BsonSerializer.Deserialize<Player>((players.LastReplacement
            ?? throw new InvalidDataException("Awakening task progress did not persist Player.")).ToBson());
        harness.Session.character = BsonSerializer.Deserialize<Character>((characters.LastReplacement
            ?? throw new InvalidDataException("Awakening did not persist Character.")).ToBson());
        harness.Session.inventory = BsonSerializer.Deserialize<Inventory>((inventories.LastReplacement
            ?? throw new InvalidDataException("Awakening did not persist Inventory.")).ToBson());
        CheckProgress(2, "BSON reload retains committed awakening progress");
        AssertIntegerList([1, 2], harness.Session.character.Equips.Single().AwakeSlotList.Select(Convert.ToInt64).ToArray(),
            "BSON reload retains committed awakening slots");
        long coins = harness.Session.inventory.Items.Single(item => item.Id == Inventory.Coin).Count;
        (EquipQuickAwakeResponse replay, List<SyncTask> replayTasks) = Dispatch(request);
        AssertEqual(0, replay.Code, "Already-awakened slots replay succeeds as a no-op");
        AssertEqual(false, replayTasks.Any(task => task.Id == taskId), "Awakening replay publishes no task advance");
        AssertEqual(coins, harness.Session.inventory.Items.Single(item => item.Id == Inventory.Coin).Count,
            "Awakening replay consumes no coins");
        CheckProgress(2, "Awakening replay does not count already committed slots");

        void CheckProgress(int expected, string context)
        {
            var task = RequiredStoryLoginTask(BuildTaskData(harness.Session), taskId);
            AssertEqual(expected, task.Schedule.Single().Value, context);
            AssertEqual(1, task.State, $"{context} remains below five-unit completion");
            AssertEqual(200_000L - harness.Session.inventory.Items.Single(item => item.Id == Inventory.Coin).Count,
                (long)RequiredStoryLoginTask(BuildTaskData(harness.Session), 3600).Schedule.Single().Value,
                $"{context} coin task counts only actual spending");
            AssertEqual(0, RequiredStoryLoginTask(BuildTaskData(harness.Session), 50046).Schedule.Single().Value,
                $"{context} equipment costs do not count as serum");
        }

        (EquipQuickAwakeResponse Response, List<SyncTask> Tasks) Dispatch(EquipQuickAwakeRequest payload)
        {
            int id = packetId++;
            dispatch.Invoke(harness.Session, [GetRegisteredRequestHandler(nameof(EquipQuickAwakeRequest)), new Packet.Request
            {
                Id = id, Name = nameof(EquipQuickAwakeRequest), Content = MessagePackSerializer.Serialize(payload)
            }]);
            List<SyncTask> tasks = [];
            while (true)
            {
                Packet packet = harness.ReadPacket("Equipment task progress response or push");
                if (packet.Type == Packet.ContentType.Response)
                {
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(id, response.Id, "Equipment task progress response request id");
                    AssertEqual(nameof(EquipQuickAwakeResponse), response.Name, "Equipment task progress response name");
                    while (harness.TryReadAvailablePacket("Equipment task progress trailing push", out Packet? trailing))
                        Collect(trailing!);
                    return (MessagePackSerializer.Deserialize<EquipQuickAwakeResponse>(response.Content), tasks);
                }
                Collect(packet);
            }

            void Collect(Packet packet)
            {
                AssertEqual(Packet.ContentType.Push, packet.Type, "Equipment task progress notification type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name == nameof(NotifyTask))
                    tasks.AddRange(MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks);
            }
        }
    }
}
