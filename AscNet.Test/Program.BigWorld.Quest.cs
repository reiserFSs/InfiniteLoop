using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.Table.V2.share.dlcworld.questsystem.environmentquest;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using MongoDB.Bson.Serialization;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateBigWorldQuest()
        {
            ValidateBigWorldQuestCore();
            ValidateBigWorldQuestRetail();
            ValidateBigWorldQuestObjectives();
            ValidateBigWorldQuestInvite();
            Console.WriteLine("big world quest: ok");
        }

        private static void ValidateBigWorldQuestCore()
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
            const long playerId = 99_812;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "big-world-quest");
            int packetId = 0;
            List<Packet.Push> pushes = [];
            void Drain()
            {
                while (harness.TryReadAvailablePacket("drain", out Packet p))
                    if (p.Type == Packet.ContentType.Push)
                        pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(p.Content));
            }
            // Pushes sent outside a request/response pair may still be in flight on the loopback socket: wait briefly for one.
            T Push<T>(string name)
            {
                for (int attempt = 0; attempt < 50 && !pushes.Any(p => p.Name == name); attempt++)
                {
                    Thread.Sleep(10);
                    Drain();
                }
                return MessagePackSerializer.Deserialize<T>(pushes.Last(p => p.Name == name).Content);
            }
            int Count(string name) => pushes.Count(p => p.Name == name);
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
                        AssertEqual((packetId, name.Replace("Request", "Response")), (r.Id, r.Name), $"{name} response");
                        code = MessagePackSerializer.Deserialize<Dictionary<string, object>>(r.Content) is { } m && m.TryGetValue("Code", out object? c) ? Convert.ToInt32(c) : 0;
                    }
                }
                Drain();
                return code.Value;
            }
            Player Reload() => BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            BigWorldPlayerState S = player.BigWorldState;
            Theatre5DlcQuestInfo Q() => S.QuestData;
            int Update(params (int Id, int State)[] list) => Req("DlcQuestUpdateRequest", new DlcQuestUpdateRequest
            {
                QuestStepObjectiveList = list.Select(x => new Theatre5DlcQuestStepObjective { Id = x.Id, ObjectiveState = x.State }).ToList()
            });
            // Completes every open objective of the given step (drives the quest through the table's own step graph).
            int FinishStep(int questId, int stepId) => Update(Q().ActiveQuests[questId].DynamicData!.Steps[stepId].Objectives.Values.Where(o => o.ObjectiveState != 6).Select(o => (o.Id, 6)).ToArray());

            // ---- Auto-activation from tables (2002: IsAllowAutoActivate, no preconditions, AutoUndertake 0 -> Ready) ----
            // Ungated quests are bound to their LevelId: in district 4001 only 2002/6001 activate (retail: 2002 active,
            // tutorial 6001 finished); 2018 (level 6002) waits until the player is in 6002.
            S.LastLevelId = 4001;
            var worldData = (Theatre5DlcQuestInfo)Call("BuildWorldQuestData", harness.Session)!;
            Drain();
            AssertEqual((true, 1), (worldData.ReadyQuestIds.Contains(2002), worldData.ActiveQuests[2002].DynamicData!.QuestState), "2002 ready");
            AssertEqual("2002,6001", string.Join(",", worldData.ActiveQuests.Keys.Order()), "district 4001 activates only its level-bound quests");
            S.LastLevelId = 6002;
            worldData = (Theatre5DlcQuestInfo)Call("BuildWorldQuestData", harness.Session)!;
            Drain();
            AssertEqual(2, worldData.ActiveQuests[2018].DynamicData!.QuestState, "2018 auto-undertaken in level 6002");
            AssertEqual(1, worldData.ActiveQuests[2018].DynamicData!.Steps.Values.Single().StepState, "2018 first step open");
            AssertEqual(false, worldData.ActiveQuests.ContainsKey(4012), "instance quest 4012 waits for level 4012");
            S.LastLevelId = 4001;
            AssertEqual(false, Q().ActiveQuests.ContainsKey(1001), "1001 waits for PreFinishQuests 2002");
            AssertEqual(true, Reload().BigWorldState.QuestData.ReadyQuestIds.Contains(2002), "activation persisted");

            // ---- Ready tracking ----
            AssertEqual(25100001, Req("BigWorldSetTrackReadyQuestIdRequest", new BigWorldSetTrackReadyQuestIdRequest { QuestId = 999_999 }), "ready unknown");
            AssertEqual(25100047, Req("BigWorldSetTrackReadyQuestIdRequest", new BigWorldSetTrackReadyQuestIdRequest { QuestId = 2018 }), "ready not ready");
            AssertEqual(0, Req("BigWorldSetTrackReadyQuestIdRequest", new BigWorldSetTrackReadyQuestIdRequest { QuestId = 2002 }), "ready ok");
            AssertEqual(2002, Reload().BigWorldState.TraceReadyQuestId, "ready persisted");

            // ---- DlcQuestUpdate ----
            AssertEqual(25100013, Update(), "update empty");
            AssertEqual(25100004, Update((1, 6)), "update unknown objective");
            AssertEqual(25100008, Update((2002052, 6)), "ready quest second-step objective");
            AssertEqual(25100045, Update((2002014, 7)), "state out of range");
            AssertEqual(0, Update((2002014, 6)), "first-step objective undertakes 2002");
            AssertEqual((2, 0, false), (Q().ActiveQuests[2002].DynamicData!.QuestState, S.TraceReadyQuestId, Q().ReadyQuestIds.Contains(2002)), "2002 undertaken");
            AssertEqual(true, Count(nameof(NotifyDlcQuestUpdate)) == 1 && Push<NotifyDlcQuestUpdate>(nameof(NotifyDlcQuestUpdate)).QuestData!.DynamicData!.FinishedObjectiveIds.Contains(2002014), "update push");
            AssertEqual(25100045, Update((2002014, 3)), "no regression");
            AssertEqual(0, Update((2002015, 4)), "partial state");
            AssertEqual(4, Reload().BigWorldState.QuestData.ActiveQuests[2002].DynamicData!.Steps[200201].Objectives[2002015].ObjectiveState, "partial persisted");
            AssertEqual(25100003, Update((2002052, 6)), "step not active");

            // ---- Trace ----
            AssertEqual(25100041, Req("DlcQuestTraceIdChangeRequest", new DlcQuestTraceIdChangeRequest { ChangeTraceQuestId = 2018 }), "trace inst category");
            AssertEqual(25100001, Req("DlcQuestTraceIdChangeRequest", new DlcQuestTraceIdChangeRequest { ChangeTraceQuestId = 999_999 }), "trace unknown");
            AssertEqual(25100023, Req("DlcQuestTraceIdChangeRequest", new DlcQuestTraceIdChangeRequest { ChangeTraceQuestId = 2011 }), "trace ready quest");
            AssertEqual(0, Req("DlcQuestTraceIdChangeRequest", new DlcQuestTraceIdChangeRequest { ChangeTraceQuestId = 2002 }), "trace ok");
            AssertEqual(2002, Reload().BigWorldState.TraceQuestId, "trace persisted");

            // ---- Finish 2002: steps advance by PreStep, IsEndStep finishes, RewardId granted once, 1001 activates ----
            AssertEqual(0, FinishStep(2002, 200201), "step 200201");
            AssertEqual((2, 1), (Q().ActiveQuests[2002].DynamicData!.Steps[200201].StepState, Q().ActiveQuests[2002].DynamicData!.Steps[200202].StepState), "step advanced");
            while (Q().ActiveQuests.TryGetValue(2002, out var q2002))
                AssertEqual(0, FinishStep(2002, q2002.DynamicData!.Steps.Values.First(s => s.StepState == 1).StepId), "drive 2002");
            AssertEqual((true, true, 0, 2002), (Q().FinishedQuests.Contains(2002), S.RewardedQuestIds.Contains(2002), S.TraceQuestId, S.LastTraceQuestId), "2002 finished");
            AssertEqual(true, Push<BigWorldNotifyReward>(nameof(BigWorldNotifyReward)).RewardGoodsList.Count > 0, "quest reward push");
            AssertEqual(true, Push<NotifyDlcQuestFinish>(nameof(NotifyDlcQuestFinish)).NewFinishedQuestIds.SequenceEqual([2002]), "finish push");
            AssertEqual(true, Push<NotifyDlcQuestActivate>(nameof(NotifyDlcQuestActivate)).NewActivatedQuestIds.Contains(1001), "follow-up activated");
            AssertEqual(true, Reload().BigWorldState.QuestData.FinishedQuests.Contains(2002), "finish persisted");
            AssertEqual(25100045, Update((2002014, 6)), "finished quest rejects updates");

            // ---- Environment groups ----
            BigWorldPlayerData pd = new();
            Call("FillPlayerData", player, pd);
            AssertEqual(1, pd.EnvironmentQuestData.ActivatedQuestGroupIds[5001], "env default group");
            AssertEqual(25100046, Req("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 4001, QuestGroupId = 2 }), "env wrong level");
            AssertEqual(0, Req("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 5001, QuestGroupId = 2 }), "env ok");
            AssertEqual(2, Reload().BigWorldState.EnvironmentQuestGroups[5001], "env persisted");

            // ---- Encounter Journal (BigworldAIMemory.Condition -> objective finished): environment quests undertake silently on duty; ----
            // ---- Teddy 30070101..03 / Lucia 30060101..03 are InteractComplete counts 1/2/3 on NPC places 1600001 / 1800002. ----
            AssertEqual(0, Req("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 5001, QuestGroupId = 1 }), "env group A");
            harness.Session.BigWorldWorldId = 400;
            S.LastLevelId = 5001;
            int State(int questId, int stepId, int objectiveId) => Q().ActiveQuests[questId].DynamicData!.Steps[stepId].Objectives[objectiveId].ObjectiveState;
            HashSet<int> knownQuests = Q().ActiveQuests.Keys.ToHashSet();
            Rt("OnLevelEntered", harness.Session, 5001, false);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual((2, 2, 3, 3), (Q().ActiveQuests[3006].DynamicData!.QuestState, Q().ActiveQuests[3007].DynamicData!.QuestState, State(3007, 300701, 30070101), State(3006, 300601, 30060103)), "on-duty environment quests undertaken");
            AssertEqual(false, Q().ActiveQuests.ContainsKey(3009), "group B environment quest stays off duty");
            AssertEqual(0, Q().ActiveQuests[3007].DynamicData!.FinishedObjectiveIds.Count + Q().ActiveQuests[3006].DynamicData!.FinishedObjectiveIds.Count, "journal entries locked before any encounter");
            // Schedule A also lists Vera (3008, quest LevelId 0): the group's level, not the quest's, puts her on duty.
            AssertEqual((2, 3), (Q().ActiveQuests[3008].DynamicData!.QuestState, State(3008, 300801, 30080101)), "Vera (LevelId 0 quest) on duty in schedule A");
            foreach ((int group, int[] onDuty) in new[] { (2, new[] { 3009, 3010 }), (3, new[] { 3011, 3012, 3013 }) })
            {
                AssertEqual(0, Req("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 5001, QuestGroupId = group }), $"env group {group}");
                Rt("OnLevelEntered", harness.Session, 5001, false);
                Rt("Tick", harness.Session);
                Drain();
                AssertEqual("2", string.Join(",", onDuty.Select(id => Q().ActiveQuests.TryGetValue(id, out var eq) ? eq.DynamicData!.QuestState : -1).Distinct()), $"schedule {group} environment quests undertaken");
            }
            AssertEqual(0, Req("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = 5001, QuestGroupId = 1 }), "env back to group A");
            HashSet<int> questIds = TableReaderV2.Parse<DlcQuestTable>().Select(row => row.Id).ToHashSet();
            AssertEqual("", string.Join(",", TableReaderV2.Parse<DlcQuestStepTable>().Where(step => !questIds.Contains(step.QuestId)).Select(step => step.Id)), "every quest step belongs to a quest (3015/3016 environment steps)");
            AssertEqual("", string.Join(",", TableReaderV2.Parse<DlcEnvironmentQuestTable>().Where(row => row.ObjectiveIds.Any(ids => $"{ids}".Split('|').All(id => TableReaderV2.Parse<DlcQuestObjectiveTable>().Single(o => o.Id == int.Parse(id)).QuestId <= 0))).Select(row => row.Name)), "every Encounter Journal entry resolves to a quest");
            pushes.Clear();
            Rt("OnInteract", harness.Session, 1, 1600001, 1);
            Drain();
            AssertEqual((6, 3, 3), (State(3007, 300701, 30070101), State(3007, 300701, 30070102), State(3007, 300701, 30070103)), "first Teddy encounter unlocks entry 1 only");
            AssertEqual((3, 3, 3), (State(3006, 300601, 30060101), State(3006, 300601, 30060102), State(3006, 300601, 30060103)), "Lucia untouched by Teddy");
            AssertEqual(true, pushes.Any(p => p.Name == "XRpcCommon" && MessagePackSerializer.Deserialize<object[]>(p.Content)[0] as string == "RpcQuestObjectiveProgressNotify"), "encounter incremental push");
            Rt("OnInteract", harness.Session, 1, 1800002, 1);
            Drain();
            AssertEqual((6, 3, 3), (State(3006, 300601, 30060101), State(3006, 300601, 30060102), State(3006, 300601, 30060103)), "Lucia encounter unlocks her entry 1");
            Rt("OnInteract", harness.Session, 1, 1600001, 2);
            Drain();
            Theatre5DlcQuestInfo reloaded = Reload().BigWorldState.QuestData;
            AssertEqual("30070101,30070102", string.Join(",", reloaded.ActiveQuests[3007].DynamicData!.FinishedObjectiveIds.Order()), "Teddy entries 1-2 persisted, 3 locked");
            AssertEqual("30060101", string.Join(",", reloaded.ActiveQuests[3006].DynamicData!.FinishedObjectiveIds.Order()), "Lucia entry 1 persisted");
            worldData = (Theatre5DlcQuestInfo)Call("BuildWorldQuestData", harness.Session)!;
            Drain();
            AssertEqual(true, worldData.ActiveQuests[3007].DynamicData!.FinishedObjectiveIds.Contains(30070102) && !worldData.ActiveQuests[3007].DynamicData!.FinishedObjectiveIds.Contains(30070103), "world entry carries finished entries only");
            foreach (int added in Q().ActiveQuests.Keys.Except(knownQuests).ToList())
                Q().ActiveQuests.Remove(added);
            Q().ReadyQuestIds.RemoveAll(id => !knownQuests.Contains(id));
            S.LastLevelId = 4001;
            harness.Session.BigWorldWorldId = 0;

            // ---- Invite quests ----
            AssertEqual(25100040, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "invite condition unmet");
            AssertEqual(25100001, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2002 }), "not an invite");
            Q().FinishedQuests.Add(7001); // DlcInviteQuest.Condition 50002052 = quest 7001 finished
            harness.Session.BigWorldWorldId = 400; // level actions (RequestEnterInstLevel) need the player inside the world
            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "invite accept");
            AssertEqual(true, pushes.Any(p => p.Name == "NotifyBigWorldNotReadMessage"), "accept delivers short-message objective");
            AssertEqual(25100006, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "invite already active");
            AssertEqual(25100040, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2009 }), "one invite at a time");

            // Retail (sequence J): RpcShortMessageReadComplete is a bare ack; the objective completes with the game-server
            // message record reaching its terminal step, and its state pushes precede the record response.
            pushes.Clear();
            AssertEqual(true, (bool)Call("TryHandleXRpc", harness.Session, "RpcShortMessageReadComplete", MessagePackSerializer.Serialize(new object[] { 200701 }), 5001)!, "xrpc handled");
            Drain();
            AssertEqual((false, 0), (Q().ActiveQuests[2007].DynamicData!.FinishedObjectiveIds.Contains(20070101), Count("XRpcCommon")), "read-complete xrpc is a bare ack");
            var msgSteps = TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.message.BigWorldMessageStepTable>().ToDictionary(row => row.Id);
            int terminal = TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.message.BigWorldMessageTable>().Single(row => row.Id == 200701).FirstStepId;
            while (msgSteps[terminal].NextStep.Any(id => id > 0))
                terminal = msgSteps[terminal].NextStep.First(id => id > 0);
            AssertEqual(0, Req("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 200701, StepId = terminal }), "message record");
            AssertEqual(true, Q().ActiveQuests[2007].DynamicData!.FinishedObjectiveIds.Contains(20070101), "message record completed objective");
            Packet.Push xrpc = pushes.First(p => p.Name == "XRpcCommon");
            object[] envelope = MessagePackSerializer.Deserialize<object[]>(xrpc.Content);
            AssertEqual(("RpcQuestObjectiveUpdate", "2007,20070101,4", 0), (envelope[0] as string, string.Join(",", MessagePackSerializer.Deserialize<object[]>((byte[])envelope[1])), Convert.ToInt32(envelope[4])), "xrpc objective push");
            AssertEqual(false, (bool)Call("TryHandleXRpc", harness.Session, "RpcUnrelated", Array.Empty<byte>(), 5001)!, "xrpc unrelated");
            // Step 200702/200703 are branches gated on quest var "2007" (PreLevelConditionGroup CompareVar).
            AssertEqual(false, Q().ActiveQuests[2007].DynamicData!.Steps.ContainsKey(200702), "branch gated on var");
            pushes.Clear();
            Rt("SetVar", harness.Session, 2007, 1, "2007", 1);
            Drain();
            // RpcQuestSyncVars [QuestId, RepVarModify[{EVarType: map{key: value} + removed-key array}]] (client XVarModifyDictionary format).
            string SyncedVars()
            {
                List<string> synced = [];
                foreach (Packet.Push p in pushes.Where(p => p.Name == "XRpcCommon"))
                {
                    object[] env = MessagePackSerializer.Deserialize<object[]>(p.Content);
                    if ((string)env[0] != "RpcQuestSyncVars")
                        continue;
                    object[] args = MessagePackSerializer.Deserialize<object[]>((byte[])env[1]);
                    foreach (KeyValuePair<object, object> entry in (Dictionary<object, object>)((object[])args[1])[0])
                    {
                        MessagePackReader reader = new((byte[])entry.Value);
                        int count = reader.ReadMapHeader();
                        string key = reader.ReadString()!;
                        object value = reader.NextMessagePackType switch { MessagePackType.Boolean => reader.ReadBoolean(), MessagePackType.Float => reader.ReadSingle(), _ => reader.ReadInt32() };
                        synced.Add($"{args[0]}/{entry.Key}/{count}/{key}={value}/removed{reader.ReadArrayHeader()}");
                    }
                }
                pushes.Clear();
                return string.Join(";", synced);
            }
            AssertEqual("2007/1/1/2007=1/removed0", SyncedVars(), "int var set syncs RpcQuestSyncVars");
            Rt("SetVar", harness.Session, 2007, 2, "Score", 1.5f);
            Rt("SetVar", harness.Session, 2007, 3, "Flag", true);
            Drain();
            AssertEqual("2007/2/1/Score=1.5/removed0;2007/3/1/Flag=True/removed0", SyncedVars(), "float and bool var sets sync");
            AssertEqual(0, FinishStep(2007, 200701), "invite step 1");
            AssertEqual(true, Q().ActiveQuests[2007].DynamicData!.Steps.ContainsKey(200702) && !Q().ActiveQuests[2007].DynamicData!.Steps.ContainsKey(200703), "var 1 opens only branch 200702");
            AssertEqual(25100042, Req("DlcInviteQuestResultNumRewardRequest", new DlcInviteQuestResultNumRewardRequest { QuestId = 2007 }), "results incomplete");
            // 20070201 (EnterLevel 5001) closes online; its ExitActions run UnlockBranchQuestResult on the next tick.
            pushes.Clear();
            Rt("OnLevelEntered", harness.Session, 5001, false);
            Rt("Tick", harness.Session);
            Drain();
            var result1 = Push<NotifyDlcInviteQuestResultReward>(nameof(NotifyDlcInviteQuestResultReward));
            AssertEqual((2007, 1, true), (result1.QuestId, result1.ResultId, result1.RewardItems.Count > 0), "result 1");
            AssertEqual(true, Reload().BigWorldState.UnlockedInviteQuestResultIds.Contains(1), "result persisted");

            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2007 }), "invite replay");
            Rt("SetVar", harness.Session, 2007, 1, "2007", 2);
            Drain();
            AssertEqual(0, FinishStep(2007, 200701), "replay step 1");
            AssertEqual(true, Q().ActiveQuests[2007].DynamicData!.Steps.ContainsKey(200703) && !Q().ActiveQuests[2007].DynamicData!.Steps.ContainsKey(200702), "var 2 opens only branch 200703");
            pushes.Clear();
            Rt("OnLevelEntered", harness.Session, 5001, false);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual(2, Push<NotifyDlcInviteQuestResultReward>(nameof(NotifyDlcInviteQuestResultReward)).ResultId, "result 2");
            AssertEqual(0, Count(nameof(BigWorldNotifyReward)), "invite quest has no RewardId");

            AssertEqual(0, Req("DlcInviteQuestResultNumRewardRequest", new DlcInviteQuestResultNumRewardRequest { QuestId = 2007 }), "total reward");
            var total = Push<NotifyDlcInviteQuestResultNumReward>(nameof(NotifyDlcInviteQuestResultNumReward));
            AssertEqual((true, 2), (total.RewardItems.Count > 0, total.InviteQuestInfo.ReceivedRewardInviteQuestIds[2007]), "total reward push");
            AssertEqual(25100043, Req("DlcInviteQuestResultNumRewardRequest", new DlcInviteQuestResultNumRewardRequest { QuestId = 2007 }), "total reward once");
            AssertEqual(2, Reload().BigWorldState.ReceivedRewardInviteQuestIds[2007], "total persisted");

            // ---- OnLevelEntered (EnterLevel objectives, 2 levels) ----
            AssertEqual(0, Req("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = 2009 }), "invite 2009");
            Rt("SetVar", harness.Session, 2009, 1, "2009", 5);
            Drain();
            AssertEqual(0, FinishStep(2009, 200901), "2009 step 1");
            pushes.Clear();
            Call("OnLevelEntered", harness.Session, 4001);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual(true, Q().ActiveQuests.ContainsKey(2009), "wrong level leaves 2009 open");
            Call("OnLevelEntered", harness.Session, 5001);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual(true, pushes.Any(p => p.Name == "XRpcCommon" && MessagePackSerializer.Deserialize<object[]>(p.Content)[0] as string == "RpcQuestUpdate"), "online completion pushes RpcQuestUpdate");
            AssertEqual(true, Reload().BigWorldState.QuestData.FinishedQuests.Contains(2009), "enter level finishes 2009");

            // ---- Instance quests start on entering their level (live: 4026 "Froggie's Day" stayed Ready, stage empty) ----
            int savedInst = S.InstLevelId;
            S.InstLevelId = 4026;
            Call("OnLevelEntered", harness.Session, 4026);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual((2, true), (Q().ActiveQuests[4026].DynamicData!.QuestState, Q().ActiveQuests[4026].DynamicData!.Steps.ContainsKey(402601)), "instance quest 4026 undertaken on entering 4026");
            AssertEqual(false, Q().ActiveQuests.TryGetValue(4012, out var other) && other.DynamicData!.QuestState == 2, "other instance quests untouched");
            // Entering 4027: the instance quest starts (40270101 EnterActions disable TaskEntry 5) and the client's level-loaded
            // report must not re-run that list (live: TaskEntry disabled twice, the single re-enable at 40270110 left it off).
            // 40270101's list starts with a client-waiting screen effect, so a second run shows as a second first segment.
            S.InstLevelId = 4027;
            pushes.Clear();
            Call("OnLevelEntered", harness.Session, 4027);
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            Rt("Tick", harness.Session);
            Thread.Sleep(50); // pushes outside a request/response pair may still be in flight on the loopback socket
            Drain();
            AssertEqual((2, 1), (Q().ActiveQuests[4027].DynamicData!.QuestState,
                pushes.Count(p => p.Name == "XRpcCommon" && (string)MessagePackSerializer.Deserialize<object[]>(p.Content)[0] == "RpcLevelActionClientExecute")), "4027 entry actions run once");
            // Re-entering 4027 with its quest in progress relaunches it (client re-tracks the instance quest); first entry did not.
            AssertEqual(false, pushes.Any(p => p.Name == "XRpcCommon" && (string)MessagePackSerializer.Deserialize<object[]>(p.Content)[0] == "RpcReLaunchQuests"), "first entry: no relaunch");
            pushes.Clear();
            Call("OnLevelEntered", harness.Session, 4027);
            Thread.Sleep(50);
            Drain();
            // The client rejects relaunching a quest it holds: RpcRemoveQuest [4027] must come first.
            AssertEqual("RpcRemoveQuest,RpcReLaunchQuests", string.Join(",", pushes.Where(p => p.Name == "XRpcCommon")
                .Select(p => (string)MessagePackSerializer.Deserialize<object[]>(p.Content)[0]).Where(n => n is "RpcRemoveQuest" or "RpcReLaunchQuests")), "remove then relaunch");
            object[]? relaunched = pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content))
                .FirstOrDefault(e => (string)e[0] == "RpcReLaunchQuests");
            AssertEqual((true, 4027L), (relaunched is not null, relaunched is null ? 0L : BwInt(((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])relaunched[1])[0]!)[0]!)[0])), "re-entry relaunches 4027");
            // Quest-enabled function entries are replayed when the level loads (live: ScanAbility off after relog in 4027).
            string Batch(string enable, string disable) => $$$"""[{"ActionType":19007,"Params":{"$type":"XConfigLevelActionParamsSetSystemFuncEntryEnableBatch","EnableList":[{{{enable}}}],"DisableList":[{{{disable}}}]}}]""";
            string[] Replayed()
            {
                Thread.Sleep(50); // the action's own push may still be in flight
                Drain();
                pushes.Clear();
                Rt("OnPlayerEnterLevelComplete", harness.Session);
                Thread.Sleep(50);
                Drain();
                return pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content))
                    .Where(e => (string)e[0] == "RpcSetSystemFuncEntryEnableBatchRequest")
                    .Select(e => string.Join("|", ((object?[])((object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])e[1]))[0]!).Select(Convert.ToInt32))).ToArray();
            }
            lock (Session.GetPlayerOperationLock(harness.Session.player.PlayerData.Id))
                AscNet.GameServer.Handlers.BigWorld.BigWorldLevelActions.Run(harness.Session, Newtonsoft.Json.Linq.JArray.Parse(Batch("9", "")),
                    new AscNet.GameServer.Handlers.BigWorld.QuestActionContext(4027, 40270106, 2), () => { });
            AssertEqual("9", string.Join(",", Replayed()), "enabled ScanAbility replayed on level load");
            lock (Session.GetPlayerOperationLock(harness.Session.player.PlayerData.Id))
                AscNet.GameServer.Handlers.BigWorld.BigWorldLevelActions.Run(harness.Session, Newtonsoft.Json.Linq.JArray.Parse(Batch("", "9")),
                    new AscNet.GameServer.Handlers.BigWorld.QuestActionContext(4027, 40270107, 2), () => { });
            AssertEqual("", string.Join(",", Replayed()), "disabled again: no replay");
            // Controls are set-semantics client state held in memory only: replayed from the quest's saves on level load.
            lock (Session.GetPlayerOperationLock(harness.Session.player.PlayerData.Id))
                AscNet.GameServer.Handlers.BigWorld.BigWorldLevelActions.Run(harness.Session, Newtonsoft.Json.Linq.JArray.Parse(
                        """[{"ActionType":3003,"Params":{"$type":"XConfigLevelActionParamsControlSystemFunctionTask","LimitType":1,"SystemFunctionType":2}}]"""),
                    new AscNet.GameServer.Handlers.BigWorld.QuestActionContext(4027, 40270107, 2), () => { });
            Replayed();
            object[][] controls = pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content))
                .Where(e => (string)e[0] == "RpcControlSystemFunctionRequest").ToArray();
            // One request per saved control (earlier actions of this scenario saved others); each carries its own union.
            string[] replayedControls = controls.Select(c =>
            {
                MessagePackReader r = new((byte[])c[1]);
                return (r.ReadArrayHeader(), r.ReadMapHeader(), r.ReadInt32(), r.ReadString()) is (1, 1, var type, var union) ? $"{type}:{union}" : "?";
            }).ToArray();
            AssertEqual(true, replayedControls.Contains("2:XConfigLevelActionParamsControlSystemFunctionTask"), "Task control replayed on level load");
            AssertEqual(replayedControls.Length, replayedControls.Select(c => c.Split(':')[0]).Distinct().Count(), "one replay per function type");
            // Quest system-function state belongs to the quest's level and must stay balanced against the client's per-type counters
            // (live: unfinished instance 4012 left via save exit; relog into 5001 replayed 4012's TaskEntry enable and every switch
            // logged "功能入口屏蔽计数小于0 funcType:TaskEntry"). The model below is XSystemFuncManager [DUMP48 0x1F1F9A0]: disable
            // +1, enable -1 only on an existing key (below 0 = error, clamp), Recover adds, enter/leave apply Level.DisableSystemFuncEnumStr.
            Dictionary<int, int> clientFn = [];
            int underflows = 0;
            void ClientEnable(int type)
            {
                if (clientFn.TryGetValue(type, out int c) && --c < 0) { underflows++; c = 0; }
                if (clientFn.ContainsKey(type)) clientFn[type] = c;
            }
            void ClientLevel(int level, bool enter)
            {
                foreach (int type in AscNet.GameServer.Handlers.BigWorld.BigWorldModule.Levels.Value[level].DisableSystemFuncEnumStr?.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse) ?? [])
                    if (enter) clientFn[type] = clientFn.GetValueOrDefault(type) + 1;
                    else ClientEnable(type);
            }
            List<string> FnPushes()
            {
                Thread.Sleep(50);
                Drain();
                List<string> seen = [];
                foreach (object[] rpc in pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content)))
                {
                    string name = (string)rpc[0];
                    object?[] args = MessagePackSerializer.Deserialize<object?[]>((byte[])rpc[1]);
                    if (name == "RpcRecoverSystemFuncEntryEnableBatchRequest")
                    {
                        foreach (var pair in (IDictionary<object, object>)args[0]!)
                            clientFn[Convert.ToInt32(pair.Key)] = clientFn.GetValueOrDefault(Convert.ToInt32(pair.Key)) + Convert.ToInt32(pair.Value);
                        seen.Add($"recover:{string.Join(",", ((IDictionary<object, object>)args[0]!).Select(p => $"{p.Key}={p.Value}"))}");
                    }
                    else if (name == "RpcSetSystemFuncEntryEnableBatchRequest")
                    {
                        int[] on = ((object?[])args[0]!).Select(Convert.ToInt32).ToArray(), off = ((object?[])args[1]!).Select(Convert.ToInt32).ToArray();
                        foreach (int type in on) ClientEnable(type);
                        foreach (int type in off) clientFn[type] = clientFn.GetValueOrDefault(type) + 1;
                        seen.Add($"batch:{string.Join(",", on)}|{string.Join(",", off)}");
                    }
                    else if (name == "RpcControlSystemFunctionRequest")
                        seen.Add("control");
                }
                pushes.Clear();
                return seen;
            }
            int savedFnLast = S.LastLevelId;
            Rt("OnLevelSwitching", harness.Session, 4027);
            FnPushes();
            Q().ActiveQuests[4012] = new Theatre5DlcQuest
            {
                QuestId = 4012,
                StaticData = new(),
                DynamicData = new()
                {
                    QuestState = 2,
                    FuncEntryEnabled = [5],
                    FuncEntryDisableDict = new() { [9] = 1 },
                    SystemFuncControlSaveData = [new Theatre5DlcSystemFunctionControlSave { SystemFunctionType = 2, LimitType = 0 }],
                },
            };
            // Relog into District B (5001) with 4012 unfinished: nothing of 4012 (nor of 4027) is replayed there.
            S.InstLevelId = 0;
            S.LastLevelId = 5001;
            clientFn.Clear();
            ClientLevel(5001, true);
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            AssertEqual("", string.Join(";", FnPushes()), "no quest system-function replay in a level the quests are not bound to");
            // Relog into 4012: the client's TaskEntry default disable is lifted by the quest's enable, ScanAbility disable recovered.
            S.InstLevelId = 4012;
            clientFn.Clear();
            ClientLevel(4012, true);
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            AssertEqual("recover:9=1;batch:5|;control", string.Join(";", FnPushes()), "4012 state replayed inside 4012");
            AssertEqual("5=0,9=1", string.Join(",", clientFn.OrderBy(p => p.Key).Where(p => p.Key is 5 or 9).Select(p => $"{p.Key}={p.Value}")), "client counters after the 4012 replay");
            // Leaving 4012 (instance exit / level switch): the server gives its state back before the client's OnLeaveLevel.
            Rt("OnLevelSwitching", harness.Session, 4012);
            AssertEqual("batch:9|5", string.Join(";", FnPushes()), "switch hands back recovered disables and re-arms the consumed default");
            ClientLevel(4012, false);
            S.InstLevelId = 0;
            ClientLevel(5001, true);
            Rt("OnPlayerEnterLevelComplete", harness.Session);
            AssertEqual(("", 0, "5=0,9=0"), (string.Join(";", FnPushes()), underflows,
                string.Join(",", clientFn.OrderBy(p => p.Key).Where(p => p.Key is 5 or 9).Select(p => $"{p.Key}={p.Value}"))), "4012 -> 5001 switch: no replay, no counter underflow");
            Q().ActiveQuests.Remove(4012);
            S.LastLevelId = savedFnLast;
            S.InstLevelId = 4027;
            // Serial steps run their whole chain even with NeedCompleteCount 1 (402801: drama 40280101, then exit interaction
            // 40280102 whose ExitActions leave instance 4028; live: quest finished after the drama and the exit did nothing).
            S.InstLevelId = 4028;
            Call("OnLevelEntered", harness.Session, 4028);
            Rt("Tick", harness.Session);
            Drain();
            AssertEqual(0, Update((40280101, 6)), "4028 drama done");
            AssertEqual((true, true), (Q().ActiveQuests.ContainsKey(4028) && !Q().FinishedQuests.Contains(4028),
                Q().ActiveQuests.TryGetValue(4028, out var q4028) && q4028.DynamicData!.Steps[402801].Objectives[40280102].ObjectiveState > 0), "4028 exit objective opens after the drama");
            // Quest photo mode (TakePhotoComplete with CamParamId) is opened by the server (live: 40120107 "Take a photo with Lucia"
            // showed no photo UI). Two camera setups, plus a CamParamId 0 objective that uses the normal camera and sends nothing.
            Rt("OnObjectsLoaded", harness.Session); // photo mode only opens once the level's objects are loaded
            string[] Photographs(int objectiveId)
            {
                Thread.Sleep(30); Drain(); pushes.Clear();
                var photoCfg = AscNet.GameServer.Handlers.BigWorld.BigWorldQuestModule.Objectives.Value[objectiveId];
                Rt("Scoped", harness.Session, false, new Action(() => Rt("SendDrama", harness.Session, new Theatre5DlcQuest { QuestId = photoCfg.QuestId }, photoCfg)));
                Thread.Sleep(50); Drain();
                return pushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content))
                    .Where(e => (string)e[0] == "RpcOpenGameplayPhotographRequest")
                    .Select(e => MessagePackSerializer.Deserialize<object?[]>((byte[])e[1]))
                    .Select(a => $"{a[0]}:{string.Join("|", ((object?[])a[1]!).Select(Convert.ToInt32))}:{((object?[])a[2]!).Length}:{a[6]}").ToArray();
            }
            S.InstLevelId = 4012;
            AssertEqual("10::0:0", string.Join(",", Photographs(40120107)), "4012 photo with Lucia opens quest photo mode (cam 10)");
            S.InstLevelId = 0;
            int savedLast = S.LastLevelId;
            S.LastLevelId = 5001;
            AssertEqual(("12:1500004:0:0", ""), (string.Join(",", Photographs(20110107)), string.Join(",", Photographs(20110201))), "cam 12 with detection NPC; cam 0 sends nothing");
            S.LastLevelId = savedLast;
            S.InstLevelId = 4012;
            AssertEqual("", string.Join(",", Photographs(20110107)), "photo objective of another level sends nothing");
            // RpcTakePhotoCompleteNotify completes the matching photo objective (live: the shot was ignored). Drive 4012 to 40120107.
            S.InstLevelId = 4012;
            Call("OnLevelEntered", harness.Session, 4012);
            Rt("Tick", harness.Session);
            Drain();
            var before4012 = AscNet.GameServer.Handlers.BigWorld.BigWorldQuestModule.ObjectivesByStep.Value[401201]
                .TakeWhile(o => o.Id != 40120107).Select(o => (o.Id, 6)).ToArray();
            AssertEqual(0, Update(before4012), "4012 up to the photo objective");
            int PhotoState() => Q().ActiveQuests[4012].DynamicData!.Steps[401201].Objectives[40120107].ObjectiveState;
            AssertEqual(3, PhotoState(), "40120107 in progress");
            S.InstLevelId = 4027;
            Rt("OnTakePhoto", harness.Session, new List<int>(), new List<int>(), 0, new Dictionary<int, string>(), new Dictionary<int, string>());
            AssertEqual(3, PhotoState(), "a shot in another level does not count");
            S.InstLevelId = 4012;
            Rt("OnTakePhoto", harness.Session, new List<int>(), new List<int>(), 0, new Dictionary<int, string>(), new Dictionary<int, string>());
            Rt("Tick", harness.Session);
            AssertEqual(true, PhotoState() >= 4, "the shot in 4012 closes 40120107");
            // Entering the level resets the gate: before RpcRLObjectLoadCompleted nothing is sent (live: "cannot activate in current
            // state" when the request arrived mid-load).
            Call("OnLevelEntered", harness.Session, 4012);
            AssertEqual("", string.Join(",", Photographs(40120107)), "no photo mode before the level's objects load");
            Rt("OnObjectsLoaded", harness.Session);
            S.InstLevelId = savedInst;

            // ---- RepInitialQuests shape ----
            var rep = (object[])Call("BuildRepInitialQuests", player)!;
            var quests = (List<object?[]>)rep[0];
            object?[] rep2018 = quests.Single(q => (int)q[0]! == 2018);
            object?[] repStep = ((List<object?[]>)rep2018[2]!)[0];
            object?[] repObjective = ((List<object?[]>)repStep[2]!)[0];
            AssertEqual((4, 2, 3, 4), (rep2018.Length, (int)rep2018[1]!, repStep.Length, repObjective.Length), "rep shape");
            AssertEqual((1, true), ((int)repStep[1]!, (int)repObjective[2]! is > 0 and < 6), "rep states");
            AssertEqual(true, ((List<int>)rep[1]).Contains(2002), "rep finished ids");

            // ---- Quest item bag (MaxCount clamp) ----
            Call("ApplySliceRewards", harness.Session, new List<RewardGoods> { new() { TemplateId = 28000000, Count = 5 }, new() { TemplateId = 28000001, Count = 1 }, new() { TemplateId = 1, Count = 9 } });
            Drain();
            var display = Push<NotifyDlcQuestItemObtainDisplay>(nameof(NotifyDlcQuestItemObtainDisplay));
            AssertEqual((2, 1, 2), (display.DlcQuestItemChangeDict[28000000].Count, display.DlcQuestItemChangeDict[28000001].Count, display.DlcQuestItemChangeDict.Count), "bag grant clamped");
            pushes.Clear();
            Call("ApplySliceRewards", harness.Session, new List<RewardGoods> { new() { TemplateId = 28000000, Count = 1 } });
            Drain();
            AssertEqual(0, pushes.Count, "bag at max");
            var bag = (Dictionary<int, DlcQuestItem>)Call("BuildQuestBag", Reload())!;
            AssertEqual((2, 1), (bag[28000000].Count, bag[28000001].Count), "bag persisted");

            Call("FillPlayerData", player, pd = new());
            AssertEqual((true, 0, 2002, 2), (pd.TraceQuestData.IsEnabled, pd.TraceQuestData.CurrentTraceQuestId, pd.TraceQuestData.LastTraceQuestId, pd.InviteQuestInfo.UnlockedInviteQuestResultIds.Count(id => id is 1 or 2)), "player data");
        }

        // Retail sequences A-K of the babylonia.pcap quest 2002 -> 2003 progression (oracle: local bw-wire-map §2), driven through the
        // real client reports; ActionRuntime performs the level actions, the test plays the client's ExecuteFinish.
        // Positional args; map payloads (RpcLevelActionClientExecute) decode to a single null slot.
        private static object?[] DecodeRpcArgs(byte[] args)
        {
            try { return MessagePackSerializer.Deserialize<object?>(args, Packet.InboundOptions) is object?[] list ? list : [null]; }
            catch (MessagePackSerializationException) { return [null]; }
        }

        private static void ValidateBigWorldQuestRetail()
        {
            Type runtime = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BigWorld.BigWorldQuestRuntime");
            object? Rt(string name, params object[] args)
            {
                try { return runtime.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
                catch (TargetInvocationException exception) { throw exception.InnerException!; }
            }
            const long playerId = 99_813;
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _);
            Player player = CreateDrawCompatibilityPlayer(playerId);
            Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "big-world-quest-retail");
            int packetId = 0;
            List<(string Name, object?[] Args, int Target, int Level)> rpcs = [];
            List<Packet.Push> allPushes = [];
            void Drain()
            {
                while (harness.TryReadAvailablePacket("drain", out Packet p))
                    if (p.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(p.Content);
                        allPushes.Add(push);
                        if (push.Name == "XRpcCommon")
                        {
                            object[] envelope = MessagePackSerializer.Deserialize<object[]>(push.Content);
                            rpcs.Add(((string)envelope[0], DecodeRpcArgs((byte[])envelope[1]), Convert.ToInt32(envelope[2]), Convert.ToInt32(envelope[4])));
                        }
                    }
            }
            // Sends a request and reads until its response (pushes before it are collected).
            void Send(string name, object? request)
            {
                InvokeRegisteredRequestHandler(name, harness.Session, ++packetId, request);
                while (true)
                {
                    Packet p = harness.ReadPacket(name);
                    if (p.Type != Packet.ContentType.Push)
                        break;
                    allPushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(p.Content));
                    Packet.Push push = allPushes[^1];
                    if (push.Name == "XRpcCommon")
                    {
                        object[] envelope = MessagePackSerializer.Deserialize<object[]>(push.Content);
                        rpcs.Add(((string)envelope[0], DecodeRpcArgs((byte[])envelope[1]), Convert.ToInt32(envelope[2]), Convert.ToInt32(envelope[4])));
                    }
                }
                Drain();
            }
            // Client XRpc; the response is followed by the server tick (queued action lists).
            void X(string rpc, byte[] args)
            {
                Send("XRpcCommon", new object[] { rpc, args, 15, 1, 4001 });
                Rt("Tick", harness.Session);
                Drain();
            }
            string Token((string Name, object?[] Args, int Target, int Level) r) => r.Name switch
            {
                "RpcQuestObjectiveUpdate" => $"{r.Args[1]}:{r.Args[2]}",
                "RpcAddQuestNavPoint" or "RpcAddQuestNavPointForLevelNpc" or "RpcAddQuestNavPointForLevelSceneObject" => $"nav+{r.Args[7]}",
                "RpcRemoveNavPoint" => $"nav-{r.Args[0]}",
                "RpcQuestObjectiveProgressNotify" => $"progress{r.Args[1]}",
                "RpcPlayQuestDramaRequest" => $"drama:{r.Args[1]}",
                "RpcActivateQuests" => "activate",
                "RpcQuestStepUpdate" => $"step:{r.Args[1]}:{r.Args[2]}",
                "RpcQuestUpdate" => $"quest:{r.Args[0]}:{r.Args[1]}",
                "RpcLevelActionClientExecute" => "exec",
                _ => ""
            };
            string[] Seq()
            {
                Drain();
                // Pushes sent outside a request/response pair may still be in flight on the loopback socket.
                Thread.Sleep(30);
                Drain();
                string[] tokens = rpcs.Select(Token).Where(t => t.Length > 0).ToArray();
                rpcs.Clear();
                return tokens;
            }
            void Expect(string what, params string[] expected) => AssertEqual(string.Join(" ", expected), string.Join(" ", Seq()), what);
            // Plays the client's RpcLevelActionClientExecuteFinish for the last RpcLevelActionClientExecute (echo, LaunchContext -> Context).
            void ClientFinish(string what)
            {
                Drain();
                var execute = allPushes.Where(p => p.Name == "XRpcCommon").Select(p => MessagePackSerializer.Deserialize<object[]>(p.Content))
                    .Last(e => (string)e[0] == "RpcLevelActionClientExecute");
                // LaunchContext is [type name][map] back to back (custom union); Finish echoes it under "Context".
                byte[] args = (byte[])execute[1];
                MessagePackReader reader = new(args);
                int list = 0, start = 0, end = 0;
                byte[] context = [];
                for (int i = reader.ReadMapHeader(); i > 0; i--)
                    switch (reader.ReadString())
                    {
                        case "ActionListId": list = reader.ReadInt32(); break;
                        case "StartIndex": start = reader.ReadInt32(); break;
                        case "EndIndex": end = reader.ReadInt32(); break;
                        case "LaunchContext":
                            long from = reader.Consumed;
                            reader.ReadString();
                            MessagePackSerializer.Deserialize<object>(ref reader, Packet.InboundOptions);
                            context = args[(int)from..(int)reader.Consumed];
                            break;
                    }
                System.Buffers.ArrayBufferWriter<byte> buffer = new();
                MessagePackWriter writer = new(buffer);
                writer.WriteMapHeader(4);
                writer.Write("ActionListId"); writer.Write(list);
                writer.Write("StartIndex"); writer.Write(start);
                writer.Write("EndIndex"); writer.Write(end);
                writer.Write("Context"); writer.WriteRaw(context);
                writer.Flush();
                byte[] finish = buffer.WrittenMemory.ToArray();
                X("RpcLevelActionClientExecuteFinish", finish);
            }
            byte[] Args(params object[] values) => MessagePackSerializer.Serialize(values);
            byte[] Complete(int questId, int objectiveId) => MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = questId, ["ObjectiveId"] = objectiveId });
            BigWorldPlayerState S = player.BigWorldState;
            int State(int questId, int stepId, int objectiveId) => S.QuestData.ActiveQuests[questId].DynamicData!.Steps[stepId].Objectives[objectiveId].ObjectiveState;

            // ---- Enter the world: 2002 Ready; client-reported progress up to 2002034 (client-hosted path, no actions) ----
            Send("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = 400 });
            Rt("Tick", harness.Session);
            AssertEqual(true, S.QuestData.ActiveQuests[2002].DynamicData!.QuestState == 1, "2002 ready");
            // 2002's first objective (Drama_200203) runs while the quest is Ready; its ExitActions UnderTakeSelfQuest start it.
            // Its EnterActions (LoadNpc) run once the level is loaded (RpcPlayerEnterLevelComplete).
            AssertEqual(2, State(2002, 200201, 2002014), "2002014 entering while Ready");
            X("RpcPlayerEnterLevelComplete", Args());
            AssertEqual(3, State(2002, 200201, 2002014), "2002014 in progress while Ready");
            AssertEqual(true, Seq().Contains("drama:Drama_200203"), "opening drama requested");
            X("RpcDramaFinishNotify", Args("Drama_200203", 0, new Dictionary<int, int>()));
            // ScriptExit (5) until the client finishes the exit caption segment.
            AssertEqual((2, 5), (S.QuestData.ActiveQuests[2002].DynamicData!.QuestState, State(2002, 200201, 2002014)), "drama finish undertakes 2002");
            var objectives = TableReaderV2.Parse<AscNet.Table.V2.share.statussyncfight.quest.DlcQuestObjectiveTable>().Where(o => o.StepId == 200201)
                .OrderBy(o => Convert.ToInt32(o.Order)).ToList();
            List<int> before = objectives.TakeWhile(o => o.Id != 2002034).Select(o => o.Id).ToList();
            AssertEqual(true, before.Count > 0 && objectives.Skip(before.Count).Take(4).Select(o => o.Id).SequenceEqual([2002034, 2002046, 2002047, 2002048]), "native objective order 2002034,46,47,48");
            Send("DlcQuestUpdateRequest", new DlcQuestUpdateRequest { QuestStepObjectiveList = before.Select(id => new Theatre5DlcQuestStepObjective { Id = id, ObjectiveState = 6 }).ToList() });
            AssertEqual(3, State(2002, 200201, 2002034), "2002034 in progress");
            rpcs.Clear();

            // ---- RpcPlayerEnterLevelComplete replays the active nav point (retail B991 fields) ----
            Send("XRpcCommon", new object[] { "RpcPlayerEnterLevelComplete", Args(), 15, 1, 4001 });
            Drain();
            var nav = rpcs.Single(r => r.Name == "RpcAddQuestNavPointForLevelSceneObject");
            AssertEqual("31,4001,2002,6,0/2/0,False,False,2002034,False,0,101,,False,100066",
                string.Join(",", nav.Args.Select(a => a switch { null => "", string text => text, IEnumerable<object?> l => string.Join("/", l.Select(Convert.ToSingle)), float f when f == 0 => "0", _ => a.ToString() })), "nav point fields");
            AssertEqual((1, 0), (nav.Target, nav.Level), "nav envelope target/level");
            rpcs.Clear();

            // ---- A: interact scene object 100066 (InteractComplete) ----
            Rt("OnInteract", harness.Session, 2, 100066, 1);
            var a = rpcs.Count; Drain();
            var progress = rpcs.Single(r => r.Name == "RpcQuestObjectiveProgressNotify");
            AssertEqual((0, 2002, 2002034, 0), (progress.Target, Convert.ToInt32(progress.Args[0]), Convert.ToInt32(progress.Args[1]), progress.Level), "progress notify target 0");
            Expect("A interact", "progress2002034", "2002034:4", "nav-31", "2002034:5", "2002034:6", "2002046:1", "2002046:2", "2002046:3", "nav+2002046");
            AssertEqual(true, S.QuestData.ActiveQuests[2002].DynamicData!.FinishedObjectiveIds.Contains(2002034), "2002034 finished persisted");

            // ---- B: reach 2002046 (client-detected); exit caption client segment ----
            X("RpcQuestObjectiveCompleteRequest", Complete(2002, 2002046));
            Expect("B reach 2002046", "2002046:4", "nav-47", "2002046:5", "exec");
            ClientFinish("B finish");
            Expect("B finish", "2002046:6", "2002047:1", "2002047:2", "2002047:3", "nav+2002047");

            // ---- C: reach 2002047 (no exit actions: everything before the response) ----
            X("RpcQuestObjectiveCompleteRequest", Complete(2002, 2002047));
            Expect("C reach 2002047", "2002047:4", "nav-63", "2002047:5", "2002047:6", "2002048:1", "2002048:2", "2002048:3", "nav+2002048");

            // ---- D: reach 2002048; exit = LoadNpc + caption + teleporter ----
            X("RpcQuestObjectiveCompleteRequest", Complete(2002, 2002048));
            Expect("D reach 2002048", "2002048:4", "nav-79", "2002048:5", "exec");
            ClientFinish("D finish");
            Expect("D finish", "2002048:6", "2002036:1", "2002036:2", "2002036:3", "nav+2002036");

            // ---- E: interact NPC 700003 (2002036); 2002037 enter = screen effect + unload NPC ----
            Rt("OnInteract", harness.Session, 1, 700003, 1);
            Rt("Tick", harness.Session);
            Expect("E interact npc", "progress2002036", "2002036:4", "nav-95", "2002036:5", "2002036:6", "2002037:1", "2002037:2", "exec");
            ClientFinish("E finish");
            Expect("E finish", "2002037:3", "drama:Drama_200206");

            // ---- F: drama finish; teleporter exit is server-only ----
            X("RpcDramaFinishNotify", Args("Drama_200206", 0, new Dictionary<int, int>()));
            Expect("F drama", "2002037:4", "2002037:5", "2002037:6", "2002049:1", "2002049:2", "2002049:3", "nav+2002049");

            // ---- G: reach 2002049; 2002038 (CheckRimSystemCondition, true) runs through; 2002040 drama ----
            X("RpcQuestObjectiveCompleteRequest", Complete(2002, 2002049));
            Expect("G reach 2002049", "2002049:4", "nav-111", "2002049:5", "2002049:6", "2002038:1", "2002038:2", "2002038:3", "2002038:4", "2002038:5", "2002038:6",
                "2002040:1", "2002040:2", "2002040:3", "drama:Drama_200207");

            // ---- H: drama finish; 2002039 waits for guide 200100 ----
            X("RpcDramaFinishNotify", Args("Drama_200207", 0, new Dictionary<int, int>()));
            Expect("H drama", "2002040:4", "2002040:5", "2002040:6", "2002039:1", "2002039:2", "2002039:3");
            AssertEqual(3, State(2002, 200201, 2002039), "2002039 waits for the guide");

            // ---- I: guide complete re-evaluates the condition before the response; exit guide activation; 2003 activates ----
            Send("GuideCompleteRequest", new AscNet.GameServer.Handlers.GuideCompleteRequest { GuideGroupId = 200100 });
            Rt("Tick", harness.Session);
            Expect("I guide", "2002039:4", "2002039:5", "exec");
            ClientFinish("I finish");
            Expect("I finish", "2002039:6", "activate", "step:200301:1", "2002041:1", "2002041:2", "2002041:3", "2002041:4", "2002041:5", "2002041:6",
                "2002042:1", "2002042:2", "2002042:3", "20030101:1", "20030101:2", "quest:2003:2", "exec");
            ClientFinish("I 20030101 finish");
            Expect("I 20030101", "20030101:3", "nav+20030101");
            AssertEqual((2, 1), (S.QuestData.ActiveQuests[2003].DynamicData!.QuestState, S.QuestData.ActiveQuests[2003].DynamicData!.Steps[200301].StepState), "2003 undertaken by its own EnterActions");
            AssertEqual(true, allPushes.Any(p => p.Name == "NotifyBigWorldNotReadMessage"), "2002042 AutoSend message");

            // ---- J: short message 200202 read -> 2002042 done, 2002050 opens ----
            var msgSteps = TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.message.BigWorldMessageStepTable>().ToDictionary(row => row.Id);
            int terminal = TableReaderV2.Parse<AscNet.Table.V2.share.bigworld.common.message.BigWorldMessageTable>().Single(row => row.Id == 200202).FirstStepId;
            while (msgSteps[terminal].NextStep.Any(id => id > 0))
                terminal = msgSteps[terminal].NextStep.First(id => id > 0);
            Send("XRpcCommon", new object[] { "RpcShortMessageReadComplete", Args(200202), 15, 1, 4001 });
            Expect("J read complete is a bare ack");
            Send("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = 200202, StepId = terminal });
            Rt("Tick", harness.Session);
            Expect("J message record", "2002042:4", "2002042:5", "2002042:6", "2002050:1", "2002050:2", "2002050:3", "nav+2002050");

            // ---- K: leaving the world removes both navs; relog restores states and replays navs with restarted ids ----
            Rt("OnPlayerLeaveLevel", harness.Session);
            Expect("K leave", "nav-143", "nav-127");
            Player relogged = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson!);
            AssertEqual((3, 3), (relogged.BigWorldState.QuestData.ActiveQuests[2002].DynamicData!.Steps[200201].Objectives[2002050].ObjectiveState,
                relogged.BigWorldState.QuestData.ActiveQuests[2003].DynamicData!.Steps[200301].Objectives[20030101].ObjectiveState), "states persisted");
            using LoopbackSessionHarness relog = new(CreateDrawCompatibilityCharacter(playerId), relogged, CreateDrawCompatibilityInventory(playerId, []), "big-world-quest-relog");
            relog.Session.BigWorldWorldId = 400;
            Rt("OnPlayerEnterLevelComplete", relog.Session);
            List<string> relogNavs = [];
            // Pushes outside a request/response pair may still be in flight on the loopback socket: poll briefly.
            for (int attempt = 0; attempt < 50 && relogNavs.Count < 2; attempt++, Thread.Sleep(10))
                while (relog.TryReadAvailablePacket("relog", out Packet rp))
                    if (rp.Type == Packet.ContentType.Push && MessagePackSerializer.Deserialize<Packet.Push>(rp.Content) is { Name: "XRpcCommon" } rpush)
                    {
                        object[] env = MessagePackSerializer.Deserialize<object[]>(rpush.Content);
                        object?[] navArgs = MessagePackSerializer.Deserialize<object?[]>((byte[])env[1]);
                        if (((string)env[0]).StartsWith("RpcAddQuestNavPoint"))
                            relogNavs.Add($"{navArgs[0]}:{navArgs[7]}");
                    }
            AssertEqual("31:2002050 47:20030101", string.Join(" ", relogNavs), "relog nav replay ids restart");
        }
    }
}
