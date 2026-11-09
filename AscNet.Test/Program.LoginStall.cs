using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.task;
using MessagePack;
using System.Buffers.Binary;
using System.Reflection;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test;

internal partial class Program
{
    // EN report: an existing account stalled on the home screen forever. Login pushed the 3,126-task list twice (NotifyTaskData, then a
    // byte-identical NotifyTask, ~356 KB each), the low-end client froze past the 10 s idle drop, and the reconnect answered
    // ReconnectResponse.RequestNo with the *server push* sequence, so the client's ReCall dropped its pending RPCs instead of resending.
    private static void ValidateLoginStallCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        MethodInfo doLogin = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "DoLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);

        const long uid = 99_861;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 4;
        using LoopbackSessionHarness login = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "login-stall-login", startClientLoop: false);
        login.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        login.Session.character.Characters.Add(CreateLoginAccountCompatibilityCharacter(1021001, fashionId: 3021001));
        doLogin.Invoke(null, [login.Session]);

        int taskDataPushes = 0, taskDataBytes = 0, taskDataRows = 0, largestTaskPush = 0;
        for (int index = 0, done = 0; index < 400 && done == 0; index++)
        {
            Packet.Push push = ReadStartupPush(login, $"login stall push {index}");
            switch (push.Name)
            {
                case nameof(NotifyTaskData):
                    taskDataPushes++;
                    taskDataBytes = push.Content.Length;
                    taskDataRows = MessagePackSerializer.Deserialize<NotifyTaskData>(push.Content).TaskData.Tasks.Count;
                    break;
                case nameof(NotifyTask):
                    largestTaskPush = Math.Max(largestTaskPush, MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks.Count);
                    break;
                case "NotifyWheelchairManualActivityUpdate":
                    done = 1;
                    break;
            }
        }
        AssertEqual(1, taskDataPushes, "login sends exactly one full task list");
        AssertEqual(true, taskDataRows > 0, "login task list is not empty");
        AssertEqual(true, largestTaskPush < taskDataRows / 2,
            $"login emits no full duplicate NotifyTask (largest {largestTaskPush} rows vs {taskDataRows} in NotifyTaskData)");

        // Only rows the client rebuilds itself are dropped: XTaskManager.InitTaskData fills every Task.tab row that is absent
        // as Active/0, except Daily(2), Weekly(3) and InfestorWeekly(22).
        List<LoginTask> full = BuildTaskData(login.Session);
        List<LoginTask> sent = TaskModule.BuildLoginTasks(login.Session);
        Dictionary<int, TaskTable> clientRows = TableReaderV2.Parse<TaskTable>().ToDictionary(row => row.Id);
        HashSet<uint> sentIds = sent.Select(task => task.Id).ToHashSet();
        foreach (LoginTask dropped in full.Where(task => !sentIds.Contains(task.Id)))
        {
            AssertEqual(1, dropped.State, $"dropped task {dropped.Id} is Active");
            AssertEqual(true, dropped.Schedule.All(schedule => schedule.Value == 0), $"dropped task {dropped.Id} has no progress");
            AssertEqual(true, clientRows.TryGetValue((int)dropped.Id, out TaskTable? row) && row.Type is not (2 or 3 or 22),
                $"dropped task {dropped.Id} is a client-default Task.tab row");
        }
        AssertEqual(true, full.Where(task => task.State != 1 || task.Schedule.Any(schedule => schedule.Value != 0)).All(task => sentIds.Contains(task.Id)),
            "every task with progress or a non-Active state is still sent");
        AssertEqual(true, full.Where(task => clientRows.TryGetValue((int)task.Id, out TaskTable? row) && row.Type is 2 or 3 or 22)
            .All(task => sentIds.Contains(task.Id)), "daily/weekly rows are still sent");
        AssertEqual(true, sent.Count * 2 < full.Count, $"level-4 login list shrinks by more than half ({full.Count} -> {sent.Count})");
        int fullBytes = MessagePackSerializer.Serialize(new NotifyTaskData { TaskData = new() { Tasks = full } }).Length;
        Console.WriteLine($"login-stall payload: {full.Count} rows / {fullBytes} B -> {taskDataRows} rows / {taskDataBytes} B (MessagePack)");

        // Reconnect: the old session is gone, the client keeps its task model, so nothing is re-pushed; the server only needs
        // delta baselines for the new session and must report the request number it really received.
        const long reconnectUid = 99_862;
        const string token = "login-stall-token";
        Player reconnectPlayer = CreateDrawCompatibilityPlayer(reconnectUid);
        reconnectPlayer.Token = token;
        using LoopbackSessionHarness reconnect = new(CreateDrawCompatibilityCharacter(reconnectUid), reconnectPlayer,
            CreateDrawCompatibilityInventory(reconnectUid, []), "login-stall-reconnect");
        reconnect.Session.stage = CreateLoginAccountCompatibilityStage(reconnectUid);
        reconnect.WriteClientBytes(LoginStallRequestFrame(nameof(HeartbeatRequest), 1, no: 7));
        reconnect.WriteClientBytes(LoginStallRequestFrame(nameof(HeartbeatRequest), 2, no: 9));
        for (int answered = 0; answered < 2;)
            if (reconnect.ReadPacket("login stall heartbeat").Type == Packet.ContentType.Response)
                answered++;

        const int lastServerMsgSeqNo = 500;
        InvokeRegisteredRequestHandler(nameof(ReconnectRequest), reconnect.Session, 3,
            new ReconnectRequest { Token = token, PlayerId = (uint)reconnectUid, LastMsgSeqNo = lastServerMsgSeqNo });
        reconnect.WriteClientBytes(LoginStallRequestFrame(nameof(HeartbeatRequest), 4, no: 10));
        bool sawReconnect = false;
        for (bool done = false; !done;)
        {
            Packet packet = reconnect.ReadPacket("login stall reconnect packet");
            if (packet.Type == Packet.ContentType.Push)
            {
                string name = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content).Name;
                AssertEqual(false, name == nameof(NotifyTask) || name == nameof(NotifyTaskData), "reconnect does not re-push the task list");
                continue;
            }
            Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
            if (response.Name == nameof(ReconnectResponse))
            {
                sawReconnect = true;
                AssertEqual(9, MessagePackSerializer.Deserialize<ReconnectResponse>(response.Content).RequestNo,
                    "ReconnectResponse.RequestNo is the last client packet the server received, not the push sequence");
            }
            done = response.Id == 4;
        }
        AssertEqual(true, sawReconnect, "reconnect answered");
        AssertEqual(true, reconnect.Session.TaskSnapshotProgress is { Count: > 0 }, "reconnect seeds the session's task delta baseline");
    }

    private static byte[] LoginStallRequestFrame(string requestName, int packetId, int no)
    {
        byte[] serialized = MessagePackSerializer.Serialize(new Packet
        {
            No = no,
            Type = Packet.ContentType.Request,
            Content = MessagePackSerializer.Serialize(new Packet.Request { Id = packetId, Name = requestName, Content = [] })
        }, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block));
        Crypto.HaruCrypt.Encrypt(serialized);
        byte[] frame = new byte[serialized.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, serialized.Length);
        serialized.CopyTo(frame, sizeof(int));
        return frame;
    }
}
