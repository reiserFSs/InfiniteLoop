using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.bigworld.common.message;
using MessagePack;

namespace AscNet.Test
{
    internal partial class Program
    {
        // Issue #73 (qingyuebaimu): Sky Garden invitation replay - invite instance level quests (4038/4039/...), the replayed message
        // thread, the repair of a stuck read objective, level actions held until the client loaded the instance, and TargetArgs as option ids.
        private static void ValidateBigWorldQuestInvite()
        {
            Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldQuestModule");
            object? Call(string name, params object[] args)
            {
                try { return module.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException!; }
            }
            Type runtime = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime");
            object? Rt(string name, params object[] args)
            {
                try { return runtime.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException!; }
            }
            const long playerId = 99_818;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, CreateDrawCompatibilityInventory(playerId, []), "big-world-quest-invite");
            int packetId = 0;
            List<Packet.Push> pushes = [];
            void Drain()
            {
                while (harness.TryReadAvailablePacket("drain", out Packet p))
                    if (p.Type == Packet.ContentType.Push)
                        pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(p.Content));
            }
            int Req(string name, object? request)
            {
                pushes.Clear();
                InvokeRegisteredRequestHandler(name, harness.Session, ++packetId, request);
                int? code = null;
                while (code is null)
                {
                    Packet p = harness.ReadPacket(name);
                    if (p.Type == Packet.ContentType.Push)
                        pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(p.Content));
                    else
                    {
                        Packet.Response r = MessagePackSerializer.Deserialize<Packet.Response>(p.Content);
                        code = MessagePackSerializer.Deserialize<Dictionary<string, object>>(r.Content) is { } m && m.TryGetValue("Code", out object? c) ? Convert.ToInt32(c) : 0;
                    }
                }
                return code.Value;
            }
            // Pushes sent after the response (the tick that runs queued action lists) may still be in flight on the loopback socket.
            void Settle() { Thread.Sleep(80); Drain(); }
            List<object[]> Rpcs()
            {
                Settle();
                return pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content)).ToList();
            }
            static string Name(object[] rpc) => (string)rpc[0];
            static long Arg0(object[] rpc) => BwInt(MessagePackSerializer.Deserialize<object[]>((byte[])rpc[1])[0]);
            BigWorldPlayerState S = player.BigWorldState;
            Theatre5DlcQuestInfo Q() => S.QuestData;
            Dictionary<int, BigWorldMessageStepTable> steps = TableReaderV2.Parse<BigWorldMessageStepTable>().ToDictionary(row => row.Id);
            int Terminal(int messageId)
            {
                int step = TableReaderV2.Parse<BigWorldMessageTable>().Single(row => row.Id == messageId).FirstStepId;
                while (steps[step].NextStep.Any(id => id > 0))
                    step = steps[step].NextStep.First(id => id > 0);
                return step;
            }
            int ReadMessage(int messageId) => Req("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = messageId, StepId = Terminal(messageId) });
            void FinishRun(int inviteId, int levelQuestId)
            {
                Q().ActiveQuests.Remove(inviteId);
                Q().ActiveQuests.Remove(levelQuestId);
                foreach (int id in new[] { inviteId, levelQuestId })
                    if (!Q().FinishedQuests.Contains(id))
                        Q().FinishedQuests.Add(id);
                S.InstLevelId = 0;
                S.InstPosition = null;
                S.InstLevelFinished = false;
            }

            // Invite instance levels come from the tables: objective lists of invite quests and invite-gated door options.
            AssertEqual("4038,4039,4040,4041,4044,,", string.Join(",", new[] { 4038, 4039, 4040, 4041, 4044, 4026, 4008 }
                .Select(level => string.Join("+", (List<int>)Call("InviteLevelQuests", level)!))), "invite instance level quests");

            Q().FinishedQuests.Add(7001); // DlcInviteQuest.Condition 50002052
            S.LastLevelId = 5001;
            harness.Session.BigWorldWorldId = 400;

            // ---- First run: reading the message ends objective 20070101, whose ExitActions enter instance 4039.
            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "accept 2007");
            Settle();
            AssertEqual(0, ReadMessage(200701), "read 200701");
            List<object[]> rpcs = Rpcs();
            int switchAt = rpcs.FindIndex(r => Name(r) == "RpcPlayerSwitchLevelNotify");
            AssertEqual((4039, 2, true), (S.InstLevelId, Q().ActiveQuests[4039].DynamicData!.QuestState, switchAt >= 0), "invite 2007 enters 4039 and its level quest starts");
            // The client loads 4039 after the switch: lists pushed before RpcPlayerEnterLevelComplete reach no level (issue #73 E).
            AssertEqual("", string.Join(",", rpcs.Skip(switchAt + 1).Select(Name).Where(n => n is "RpcLevelActionClientExecute" or "RpcAddTrialNpcToTeam")), "4039 enter lists wait for the client");
            pushes.Clear();
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            Rt("Tick", harness.Session);
            AssertEqual(true, Rpcs().Any(r => Name(r) == "RpcLevelActionClientExecute"), "4039 enter lists run when the level is loaded");

            // ---- Replay (issue #73 B): the client keeps the finished quest object and the finished thread must restart.
            FinishRun(2007, 4039);
            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "replay 2007");
            rpcs = Rpcs();
            AssertEqual(("RpcRemoveQuest", 2007L, "RpcActivateQuests"), (Name(rpcs[0]), Arg0(rpcs[0]), Name(rpcs[1])), "replay drops the client's quest object first");
            BigWorldMessageData synced = MessagePackSerializer.Deserialize<NotifyBigWorldMessageRecordUpdate>(pushes.Last(p => p.Name == nameof(NotifyBigWorldMessageRecordUpdate)).Content).BigWorldMessageDict[200701];
            AssertEqual((2, 0, true), (synced.State, synced.StepRecordList.Count, pushes.Any(p => p.Name == nameof(NotifyBigWorldNotReadMessage))), "replayed thread restarts unread and is resynced");
            AssertEqual((2, 0), (S.Messages[200701].State, S.Messages[200701].StepIds.Count), "restarted record persisted");

            // ---- Entering the invite instance again restarts its finished level quest (issue #73 D).
            AssertEqual(0, ReadMessage(200701), "read 200701 again");
            rpcs = Rpcs();
            switchAt = rpcs.FindIndex(r => Name(r) == "RpcPlayerSwitchLevelNotify");
            AssertEqual((4039, 2, true, true), (S.InstLevelId, Q().ActiveQuests[4039].DynamicData!.QuestState, Q().FinishedQuests.Contains(4039),
                rpcs.Take(switchAt).Any(r => Name(r) == "RpcRemoveQuest" && Arg0(r) == 4039)), "finished level quest 4039 restarts on entry");

            // ---- Saves stuck on a finished record with an open read objective (issue #73 C): repaired once the level is loaded.
            FinishRun(2007, 4039);
            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2009 }), "accept 2009");
            Settle();
            S.Messages[200901] = new BigWorldMessageRecord { State = 1, StepIds = [Terminal(200901)] };
            AssertEqual(false, Q().ActiveQuests[2009].DynamicData!.FinishedObjectiveIds.Contains(20090101), "2009 read objective open");
            pushes.Clear();
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            Rt("Tick", harness.Session);
            Settle();
            AssertEqual((true, 4038), (Q().ActiveQuests[2009].DynamicData!.FinishedObjectiveIds.Contains(20090101), S.InstLevelId), "stuck read objective completes and enters 4038");
            int finishedCount = Q().ActiveQuests[2009].DynamicData!.FinishedObjectiveIds.Count;
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            Rt("Tick", harness.Session);
            AssertEqual(finishedCount, Q().ActiveQuests[2009].DynamicData!.FinishedObjectiveIds.Count, "repair is idempotent");

            // ---- TargetArgs {actorType: {placeId: optionId}} (issue #73 F): Liv's two answers at 2600002 are options 1 and 2.
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            // Step 403806 is parallel (one answer ends it), so each answer is played from a fresh step.
            int State(int objectiveId) => Q().ActiveQuests[4038].DynamicData!.Steps[403806].Objectives[objectiveId].ObjectiveState;
            void Answer(int optionId)
            {
                Q().ActiveQuests[4038] = new Theatre5DlcQuest
                {
                    QuestId = 4038,
                    DynamicData = new Theatre5DlcQuestDynamicData
                    {
                        QuestState = 2,
                        Steps =
                        {
                            [403806] = new Theatre5DlcQuestStep
                            {
                                StepId = 403806, StepState = 1,
                                Objectives = { [40380601] = new() { Id = 40380601, ObjectiveState = 3 }, [40380602] = new() { Id = 40380602, ObjectiveState = 3 } }
                            }
                        }
                    }
                };
                Rt("OnInteract", harness.Session, 1, 2600002, optionId);
                Rt("Tick", harness.Session);
            }
            Answer(2);
            AssertEqual((3, true), (State(40380601), State(40380602) >= 4), "answer 2 completes only 40380602");
            Answer(1);
            AssertEqual((true, 3), (State(40380601) >= 4, State(40380602)), "answer 1 completes only 40380601");
            Console.WriteLine("big world quest invite: ok");
        }
    }
}
