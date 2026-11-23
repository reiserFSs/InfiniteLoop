using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers.BigWorld;
using AscNet.Table.V2.share.bigworld.common.condition;
using AscNet.Table.V2.share.bigworld.skygarden.sgdronegame;
using AscNet.Table.V2.share.bigworld.skygarden.cafe;
using AscNet.Table.V2.share.bigworld.common.message;
using AscNet.Table.V2.share.dlcworld.questsystem.invitequest;
using AscNet.Table.V2.share.statussyncfight.level.sceneconfig;
using AscNet.Table.V2.share.statussyncfight.quest;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        // --big-world-quest-sweep-only: one player plays the whole Babylonia quest table in prerequisite order. Everything the
        // player does is a real client request (RpcPlayerInteractRequest, RpcDramaFinishNotify, RpcUiClosedNotify, ...) and the
        // simulated client only reacts to what the server pushed (ClientExecute -> Finish, drama request -> DramaFinish, level
        // switch -> EnterLevelComplete, photo mode -> TakePhotoComplete), so an objective the server never makes completable
        // stalls. A stalled quest is reported (objective, reason) and force-finished so its dependents still run.
        private static void ValidateBigWorldQuestSweep()
        {
            // The session logger prints every push (hundreds of KB per level switch): silenced while sweeping, 3x faster.
            TextWriter console = Console.Out;
            BigWorldQuestSweep.Out = console;
            List<BigWorldQuestSweep.Outcome> outcomes;
            if (Environment.GetEnvironmentVariable("BIGWORLD_SWEEP_SERVERLOG") != "1")
                Console.SetOut(TextWriter.Null);
            try { BigWorldQuestSweep.Run(out outcomes); }
            finally { Console.SetOut(console); }
            int finished = outcomes.Count(o => o.Finished);
            Console.WriteLine($"big world quest sweep: {finished}/{outcomes.Count} quests finished (largest push {BigWorldQuestSweep.LargestPush.Name} {BigWorldQuestSweep.LargestPush.Bytes} bytes)");
            if (Environment.GetEnvironmentVariable("BIGWORLD_SWEEP_TIMING") == "1")
                foreach (var (key, (count, ms)) in BigWorldQuestSweep.TimingOf().OrderByDescending(t => t.Value.Ms).Take(12))
                    Console.WriteLine($"  time {key}: {count}x {ms}ms");
            foreach (BigWorldQuestSweep.Outcome o in outcomes.Where(o => !o.Finished))
                Console.WriteLine($"  STALL {o.QuestId}: {o.Reason}");
            List<string> unexpected = outcomes.Where(o => !o.Finished && !BigWorldQuestSweep.KnownStalls.ContainsKey(o.QuestId)).Select(o => $"{o.QuestId}: {o.Reason}").ToList();
            AssertEqual("", string.Join("\n", unexpected), "quests the sweep cannot finish (not in KnownStalls)");
            AssertEqual("", string.Join(",", outcomes.Where(o => o.Finished && BigWorldQuestSweep.KnownStalls.ContainsKey(o.QuestId)).Select(o => o.QuestId)), "known stalls that now finish (remove from KnownStalls)");
            Console.WriteLine("big world quest sweep: ok");
        }

        internal static class BigWorldQuestSweep
        {
            internal static Dictionary<string, (int Count, long Ms)> TimingOf() => Client.Timing;

            // Every (envelope, rpc) the server pushed during the sweep.
            internal static readonly HashSet<(string Envelope, string Rpc)> Pushed = [];

            internal static TextWriter Out = Console.Out;
            internal static (string Name, int Bytes) LargestPush;

            internal sealed record Outcome(int QuestId, bool Finished, string Reason);

            // Quests the sweep cannot finish and why; each entry names the evidence that would settle it. Empty when everything finishes.
            internal static readonly Dictionary<int, string> KnownStalls = new()
            {
                [2011] = "no activation path: DlcQuest LevelId 0, IsAllowAutoActivate 1, no gate, not an environment/invite quest; no table, level action (21000 only undertakes the quest's own), Lua or retail capture (district 4001 only: ReadyQuestIds [], only 2002 active) activates it; server policy [INF] never auto-activates LevelId 0. Needs a retail capture in district 5001 (its objectives' level) or after an invite result",
                [30071] = "no activation path: DlcQuest LevelId 0, IsAllowAutoActivate 1, no gate, absent from DlcEnvironmentQuest (siblings 30061/30081 carry LevelId 5001, parent 3007 is the environment quest); nothing activates it; server policy [INF] never auto-activates LevelId 0. Needs a retail capture in district 5001",
            };

            private const int PopupUndertake = 1;
            private static readonly BindingFlags Any = BindingFlags.Static | BindingFlags.NonPublic;
            private static readonly Lazy<Dictionary<int, DlcQuestObjectiveTable>> Objectives = new(() => TableReaderV2.Parse<DlcQuestObjectiveTable>().ToDictionary(o => o.Id));
            private static readonly Lazy<ILookup<int, DlcQuestObjectiveTable>> ObjectivesByQuest = new(() => Objectives.Value.Values.ToLookup(o => o.QuestId));
            private static readonly Lazy<Dictionary<int, DlcQuestTable>> Quests = new(() => TableReaderV2.Parse<DlcQuestTable>().ToDictionary(q => q.Id));
            private static int I(object? value) => value is null ? 0 : Convert.ToInt32(value);

            internal static void Run(out List<Outcome> outcomes)
            {
                outcomes = [];
                FieldInfo schedule = typeof(BigWorldLevelActions).GetField("Schedule", Any)!, now = typeof(BigWorldQuestRuntime).GetField("Now", Any)!;
                object oldSchedule = schedule.GetValue(null)!, oldNow = now.GetValue(null)!;
                List<(TimeSpan Delay, Action Callback)> scheduled = [];
                DateTime clock = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                schedule.SetValue(null, (Action<TimeSpan, Action>)((delay, callback) => scheduled.Add((delay, callback))));
                now.SetValue(null, (Func<DateTime>)(() => clock));
                try
                {
                    const long playerId = 99_820;
                    using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out _, out _, out _);
                    Player player = CreateDrawCompatibilityPlayer(playerId);
                    using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, CreateDrawCompatibilityInventory(playerId, []), "big-world-quest-sweep");
                    Client client = new(harness, player, scheduled, delay => clock += delay);
                    // BIGWORLD_SWEEP_QUESTS=2002,1001 limits the run to those quests (debugging); the exact list is not asserted then.
                    HashSet<int>? only = Environment.GetEnvironmentVariable("BIGWORLD_SWEEP_QUESTS")?.Split(',').Select(int.Parse).ToHashSet();
                    foreach (DlcQuestTable quest in Order().Where(q => only is null || only.Contains(q.Id)))
                    {
                        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                        outcomes.Add(client.Play(quest));
                        if (Environment.GetEnvironmentVariable("BIGWORLD_SWEEP_TIMING") == "1")
                            Out.WriteLine($"  quest {quest.Id}: {(outcomes[^1].Finished ? "finished" : "STALL")} in {watch.ElapsedMilliseconds} ms {(outcomes[^1].Finished ? "" : outcomes[^1].Reason[..Math.Min(260, outcomes[^1].Reason.Length)])}");
                    }
                }
                finally
                {
                    schedule.SetValue(null, oldSchedule);
                    now.SetValue(null, oldNow);
                }
            }

            // Prerequisite order: PreFinishQuests, the quest of each PreFinishObjectives, and the quests whose state the quest's own gate
            // (DlcQuest.Condition, invite Condition, CheckRim objective conditions) reads come first, ties by id.
            private static List<DlcQuestTable> Order()
            {
                Dictionary<int, DlcQuestTable> all = Quests.Value;
                Dictionary<int, BigWorldConditionTable> conditions = TableReaderV2.Parse<BigWorldConditionTable>().ToDictionary(c => c.Id);
                Dictionary<int, int> questOfStep = TableReaderV2.Parse<DlcQuestStepTable>().ToDictionary(s => s.Id, s => s.QuestId);
                Dictionary<int, DlcInviteQuestTable> invites = TableReaderV2.Parse<DlcInviteQuestTable>().ToDictionary(i => i.Id);
                // Quests whose finished state / progress / unlocked invite result a condition reads.
                IEnumerable<int> Reads(int conditionId, HashSet<int> seen)
                {
                    if (!conditions.TryGetValue(conditionId, out BigWorldConditionTable? row) || !seen.Add(conditionId))
                        return [];
                    List<int> p = (row.Params ?? []).ToList();
                    int first = p.FirstOrDefault();
                    return row.Type switch
                    {
                        0 => System.Text.RegularExpressions.Regex.Matches(row.Formula ?? "", @"\d+").SelectMany(m => Reads(int.Parse(m.Value), seen)),
                        10101001 or 10101005 => [first],
                        10101002 => questOfStep.TryGetValue(first, out int stepQuest) ? [stepQuest] : [],
                        10101003 => Objectives.Value.TryGetValue(first, out var objective) ? [objective.QuestId] : [],
                        // Any unlocked result of the listed ids: the first invite quest owning one.
                        10101007 => invites.Values.Where(i => i.ResultIds.Any(id => p.Skip(1).Contains(id))).Select(i => i.Id).Order().Take(1),
                        _ => []
                    };
                }
                IEnumerable<int> Pre(DlcQuestTable q) => q.PreFinishQuests
                    .Concat(q.PreFinishObjectives.Where(Objectives.Value.ContainsKey).Select(id => Objectives.Value[id].QuestId))
                    .Concat(Reads(q.Condition, []))
                    .Concat(invites.TryGetValue(q.Id, out DlcInviteQuestTable? invite) ? Reads(invite.Condition, []) : [])
                    .Concat(ObjectivesByQuest.Value[q.Id].SelectMany(o => o.ConditionIds ?? []).SelectMany(id => Reads(id, [])))
                    // An instance's own quest (Category 1/2) completes the instance once, so a quest whose InstanceComplete objective waits on a
                    // fresh completion (no history, no leave) has to run before it.
                    .Concat(q.Category is 1 or 2 ? Objectives.Value.Values.Where(o => I(o.ObjectiveType) == 4 && I(o.InstLevelId) == q.LevelId && I(o.AppendHistoryCount) == 0 && I(o.PassOnLeaveInstLevel) == 0).Select(o => o.QuestId) : [])
                    .Where(id => all.ContainsKey(id) && id != q.Id);
                List<DlcQuestTable> ordered = [];
                HashSet<int> done = [];
                void Visit(DlcQuestTable q, HashSet<int> path)
                {
                    if (done.Contains(q.Id) || !path.Add(q.Id))
                        return;
                    foreach (int pre in Pre(q).Order())
                        Visit(all[pre], path);
                    done.Add(q.Id);
                    ordered.Add(q);
                }
                foreach (DlcQuestTable q in all.Values.OrderBy(q => q.Id))
                    Visit(q, []);
                return ordered;
            }

            private sealed class Client
            {
                private readonly LoopbackSessionHarness harness;
                private readonly Player player;
                private readonly BigWorldPlayerState S;
                private readonly List<(TimeSpan Delay, Action Callback)> scheduled;
                private readonly Action<TimeSpan> advance;
                private int packetId;
                private readonly List<(string Name, object?[] Args, byte[] Raw)> rpcs = [];
                private int reacted;
                private readonly Dictionary<int, int> attempts = [];
                private readonly HashSet<string> dramasSeen = [];
                private string lastError = "";
                private readonly Dictionary<int, string> notes = [];

                internal Client(LoopbackSessionHarness harness, Player player, List<(TimeSpan, Action)> scheduled, Action<TimeSpan> advance)
                {
                    this.harness = harness;
                    this.player = player;
                    S = player.BigWorldState;
                    this.scheduled = scheduled;
                    this.advance = advance;
                    StartReader();
                    StartKeepAlive();
                }

                private static readonly bool Verbose = Environment.GetEnvironmentVariable("BIGWORLD_SWEEP_TRACE") == "1";
                private void Trace(string text) { if (Verbose) Out.WriteLine($"  [sweep] {text}"); }

                private readonly System.Diagnostics.Stopwatch deadline = new();

                private Session Session => harness.Session;
                private int Level => BigWorldModule.CurrentLevelId(S);
                private Theatre5DlcQuestInfo Data => S.QuestData;

                #region Transport

                // XNpc replicates seen so far: uuid -> spawn position (the XNpcMoveComponent snapshot), for the KillEnemyGroup player model.
                private readonly Dictionary<int, (double X, double Y, double Z)> memberSpots = [];

                private void Take(Packet p)
                {
                    if (p.Type != Packet.ContentType.Push)
                        return;
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(p.Content);
                    if (push.Content.Length > LargestPush.Bytes)
                        LargestPush = (push.Name, push.Content.Length);
                    if (push.Name == "XRpcActorReplicate" && MessagePackSerializer.Deserialize<object?[]>(push.Content) is ["XNpc", _, _, _, _, var uuid, _, byte[] components, ..])
                    {
                        if (MessagePackSerializer.Deserialize<object?[]>(components).Cast<object?[]>().SingleOrDefault(c => (string)c[1]! == "XNpcMoveComponent") is { } moveComponent)
                        {
                            object?[] at = (object?[])MessagePackSerializer.Deserialize<object?[]>((byte[])moveComponent[0]!)[0]!;
                            memberSpots[Convert.ToInt32(uuid)] = (Convert.ToDouble(at[0]), Convert.ToDouble(at[1]), Convert.ToDouble(at[2]));
                        }
                    }
                    if (push.Name is "XRpcCommon" or "XRpcActorAction" or "XRpcComponentAction")
                    {
                        object[] envelope = MessagePackSerializer.Deserialize<object[]>(push.Content);
                        rpcs.Add(((string)envelope[0], DecodeRpcArgs((byte[])envelope[1]), (byte[])envelope[1]));
                        Pushed.Add((push.Name, (string)envelope[0]));
                    }
                }

                // A reader thread keeps the loopback socket empty: the server writes pushes (a level snapshot is >100 KB) from the
                // thread that is driving it, so an unread socket would block it.
                private readonly System.Collections.Concurrent.BlockingCollection<Packet> inbox = [];
                private Thread? reader;
                private volatile Exception? readerFailure;

                // The session drops a client that sends nothing for 10 s (Session read loop idle timeout) and then discards every push; the sweep
                // talks to the handlers directly, so a zero-length frame (ignored by the session) goes over the socket every second.
                private void StartKeepAlive() =>
                    new Thread(() =>
                    {
                        try
                        {
                            while (!inbox.IsAddingCompleted)
                            {
                                harness.WriteClientBytes([0, 0, 0, 0]);
                                Thread.Sleep(1000);
                            }
                        }
                        catch (Exception) { }
                    }) { IsBackground = true }.Start();

                private void StartReader() =>
                    (reader = new Thread(() =>
                    {
                        try
                        {
                            // Polled: a blocking read would hit the harness's 5 s receive timeout during quiet stretches (table loading).
                            for (int idle = 0; ; )
                                if (harness.TryReadAvailablePacket("sweep", out Packet p))
                                {
                                    inbox.Add(p);
                                    idle = 0;
                                }
                                else if (++idle < 20_000)
                                    Thread.Yield();
                                else
                                    Thread.Sleep(1);
                        }
                        catch (Exception ex) { readerFailure = ex; inbox.CompleteAdding(); }
                    }) { IsBackground = true }).Start();

                private void Drain()
                {
                    while (inbox.TryTake(out Packet? p))
                        Take(p);
                }

                // The session writes pushes through a queue in order, so the response of an acknowledged no-op XRpc arrives after every push
                // the server has sent so far (timers and ticks included).
                private void Quiesce()
                {
                    Drain();
                    RequestCore("XRpcCommon", new object[] { "RpcEcologyConstructClearStateRequest", Pack(), 15, 1, Level });
                    Drain();
                }

                private int Request(string name, object? request)
                {
                    int code = RequestCore(name, request);
                    BigWorldQuestRuntime.Tick(Session);
                    Quiesce();
                    return code;
                }

                internal static readonly Dictionary<string, (int Count, long Ms)> Timing = [];

                private int RequestCore(string name, object? request)
                {
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    try { return RequestTimed(name, request); }
                    finally
                    {
                        string key = request is object[] o ? $"{name}:{o[0]}" : name;
                        Timing[key] = Timing.TryGetValue(key, out var t) ? (t.Count + 1, t.Ms + watch.ElapsedMilliseconds) : (1, watch.ElapsedMilliseconds);
                    }
                }

                private int requestsInPlay;

                private int RequestTimed(string name, object? request)
                {
                    // Runaway guard: a quest that needs more than 1500 client requests is looping (the driver's or the server's).
                    if (++requestsInPlay > 1500)
                        throw new InvalidOperationException($"request storm ({name}) at {string.Join("<", new System.Diagnostics.StackTrace(false).GetFrames().Skip(1).Take(10).Select(f => f.GetMethod()?.Name))}");
                    if (Verbose) Trace($"-> {name} {(request is object[] o ? o[0] : request?.GetType().Name)} {(name is "EnterInstLevelRequest" or "LeaveInstLevelRequest" ? string.Join("<", new System.Diagnostics.StackTrace(false).GetFrames().Skip(2).Take(5).Select(f => f.GetMethod()?.Name)) : "")}");
                    InvokeRegisteredRequestHandler(name, Session, ++packetId, request);
                    while (true)
                    {
                        if (!inbox.TryTake(out Packet? p, 10_000))
                            throw new TimeoutException($"[largest push so far {LargestPush.Name} {LargestPush.Bytes}] " + (inbox.IsAddingCompleted ? $"{name}: client connection lost ({readerFailure?.Message})" : $"{name}: no response (reader alive {reader?.IsAlive}, state {reader?.ThreadState}, inbox {inbox.Count}, response expected after packet {packetId})"));
                        Take(p);
                        if (p.Type != Packet.ContentType.Push)
                        {
                            Packet.Response r = MessagePackSerializer.Deserialize<Packet.Response>(p.Content);
                            Drain();
                            return MessagePackSerializer.Deserialize<Dictionary<string, object>>(r.Content) is { } m && m.TryGetValue("Code", out object? c) ? Convert.ToInt32(c) : 0;
                        }
                    }
                }

                private int X(string rpc, byte[] args, string envelope = "XRpcCommon", int actorUuid = 0) =>
                    Request(envelope, envelope == "XRpcCommon" ? new object[] { rpc, args, 15, 1, Level } : new object[] { rpc, args, 15, 1, Level, actorUuid });

                private static byte[] Pack(params object[] values) => MessagePackSerializer.Serialize(values);

                #endregion

                #region Client reactions to server pushes

                // Reacts to every push not yet seen until the server falls quiet.
                private void Settle()
                {
                    Quiesce();
                    for (int guard = 0; guard < 400 && reacted < rpcs.Count; guard++)
                    {
                        if (deadline.Elapsed > TimeSpan.FromSeconds(25))
                            throw new TimeoutException("the server keeps pushing work to the client for 25 s (endless action loop)");
                        (string name, object?[] args, byte[] raw) = rpcs[reacted++];
                        switch (name)
                        {
                            case "RpcLevelActionClientExecute":
                                int finishCode = X("RpcLevelActionClientExecuteFinish", FinishFor(raw));
                                if (finishCode != 0) Trace($"ClientExecute finish code {finishCode}");
                                break;
                            case "RpcPlayQuestDramaRequest" when args is [_, string drama, ..]:
                                dramasSeen.Add(drama);
                                // The client clones the controlled player NPC from its NpcData (XDrama.ClonePlayerNpc -> XAttrib.Deserialize):
                                // a non-nil AttribsData (the trial team once sent an empty blob) overruns, the drama never plays and never finishes.
                                if (BigWorldModule.BuildNpcList(Session.player).Any(n => n.AttribsData is not null))
                                    throw new InvalidOperationException($"drama {drama}: the team carries AttribsData, the client cannot clone the player NPC");
                                X("RpcDramaFinishNotify", Pack(drama, 0, new Dictionary<int, int>()));
                                break;
                            case "RpcPlayerSwitchLevelNotify":
                                X("RpcPlayerEnterLevelComplete", Pack());
                                X("RpcRLObjectLoadCompleted", Pack());
                                break;
                            case "RpcOpenGameplayPhotographRequest" when args is [var cam, var npcs, var sos, _, var playerAnims, var levelAnims, var filter, ..]:
                                X("RpcTakePhotoCompleteNotify", Pack(npcs!, sos!, filter!, playerAnims!, levelAnims!, 0));
                                break;
                        }
                    }
                }

                // RpcLevelActionClientExecuteFinish echoing the Execute's list id, indexes and launch context.
                private static byte[] FinishFor(byte[] args)
                {
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
                            default: reader.Skip(); break;
                        }
                    System.Buffers.ArrayBufferWriter<byte> buffer = new();
                    MessagePackWriter writer = new(buffer);
                    writer.WriteMapHeader(4);
                    writer.Write("ActionListId"); writer.Write(list);
                    writer.Write("StartIndex"); writer.Write(start);
                    writer.Write("EndIndex"); writer.Write(end);
                    writer.Write("Context"); writer.WriteRaw(context);
                    writer.Flush();
                    return buffer.WrittenMemory.ToArray();
                }

                #endregion

                #region World / level

                private static int WorldOf(int levelId) => Enumerable.Range(1, 2000).FirstOrDefault(w => BigWorldModule.IsBigWorld(w) && BigWorldModule.IsWorldLevel(w, levelId));

                private static bool IsInstance(int levelId) => BigWorldModule.Levels.Value.TryGetValue(levelId, out var level) && level.LevelType == 2;

                private void LoadClient()
                {
                    Settle();
                    X("RpcPlayerEnterLevelComplete", Pack());
                    X("RpcRLObjectLoadCompleted", Pack());
                    Settle();
                }

                // The player walks to a level through whichever transfer reaches it (portal / instance door): the same server transfer
                // the SwitchLevel / RequestEnterInstLevel actions use, then the client's load-complete reports.
                private bool GoTo(int levelId)
                {
                    if (levelId <= 0 || levelId == Level)
                        return false;
                    int world = WorldOf(levelId);
                    if (world == 0)
                    {
                        lastError = $"level {levelId} belongs to no BigWorld world";
                        return false;
                    }
                    int before = Level;
                    if (Session.BigWorldWorldId != world || (S.InstLevelId != 0 && !IsInstance(levelId)))
                    {
                        Request("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = world, LevelId = IsInstance(levelId) ? 0 : levelId });
                        LoadClient();
                        if (Level == levelId)
                            return true;
                    }
                    int code = Request("EnterInstLevelRequest", new EnterInstLevelRequest { WorldId = world, InstLevelId = levelId });
                    if (code != 0)
                    {
                        lastError = $"transfer to level {levelId} refused with code {code}";
                        return false;
                    }
                    Settle();
                    return Level != before;
                }

                #endregion

                #region Quest play

                private bool Finished(int questId) => Data.FinishedQuests.Contains(questId);

                private List<(Theatre5DlcQuest Quest, DlcQuestObjectiveTable Cfg, Theatre5DlcQuestStepObjective Objective)> Open() =>
                    Data.ActiveQuests.Values.OrderBy(q => q.QuestId).Where(q => q.DynamicData is not null)
                        .SelectMany(q => q.DynamicData!.Steps.Values.Where(s => s.StepState == 1).SelectMany(s => s.Objectives.Values
                            .Where(o => o.ObjectiveState is > 0 and < 6 && Objectives.Value.ContainsKey(o.Id)).Select(o => (q, Objectives.Value[o.Id], o))))
                        .ToList();

                private string Signature() =>
                    string.Join("|", Data.ActiveQuests.Values.OrderBy(q => q.QuestId).Select(q =>
                        $"{q.QuestId}:{q.DynamicData?.QuestState}:" + string.Join(",", q.DynamicData!.Steps.Values.SelectMany(s => s.Objectives.Values)
                            .Select(o => $"{o.Id}={o.ObjectiveState}/{o.InteractProgressRecords.Values.Sum(r => r.Count)}/{o.NarrativeCompletedRecords.Count}/{o.KilledEnemies.Count}"))))
                    + $"#{Data.FinishedQuests.Count}#{Level}#{rpcs.Count}";

                internal Outcome Play(DlcQuestTable quest)
                {
                    if (Finished(quest.Id))
                        return new Outcome(quest.Id, true, "finished earlier in the campaign");
                    string? error = null;
                    try
                    {
                        error = Run(quest);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Exception inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                        error = $"exception {inner.GetType().Name}: {inner.Message}";
                    }
                    if (error is null)
                        return new Outcome(quest.Id, true, "");
                    try { ForceFinish(quest.Id); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { error += $" (cleanup: {ex.Message})"; }
                    return new Outcome(quest.Id, false, error);
                }

                // Returns null when the quest finished, else the stall reason.
                private string? Run(DlcQuestTable quest)
                {
                    if (S.EnteredWorldIds.Count == 0)
                    {
                        int firstLevel = QuestLevel(quest);
                        Request("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = WorldOf(firstLevel), LevelId = IsInstance(firstLevel) ? 0 : firstLevel });
                        LoadClient();
                    }
                    lastError = "";
                    attempts.Clear();
                    deadline.Restart();
                    requestsInPlay = 0;
                    // Activation: the quest binds to its level (instance quests start when the level is entered).
                    int level = QuestLevel(quest);
                    if (!Data.ActiveQuests.ContainsKey(quest.Id))
                        Activate(quest, level);
                    if (!Data.ActiveQuests.ContainsKey(quest.Id))
                        return $"not auto-activated at level {level} (IsAllowAutoActivate {quest.IsAllowAutoActivate}, PreFinishQuests [{string.Join(",", quest.PreFinishQuests)}], PreFinishObjectives [{string.Join(",", quest.PreFinishObjectives)}])";
                    string last = Signature();
                    Dictionary<string, int> visits = [];
                    System.Diagnostics.Stopwatch budget = System.Diagnostics.Stopwatch.StartNew();
                    for (int iteration = 0; iteration < 200; iteration++)
                    {
                        if (budget.Elapsed > TimeSpan.FromSeconds(20))
                            return $"no convergence in 20 s: {Describe(quest)}";
                        Settle();
                        if (Finished(quest.Id))
                            return null;
                        if (Data.ActiveQuests.TryGetValue(quest.Id, out Theatre5DlcQuest? active) && active.DynamicData?.QuestState == 1)
                            X("RpcQuestPopupClosedNotify", Pack(quest.Id, PopupUndertake));
                        bool acted = false;
                        // The quest under play and, while the player is inside an instance, the instance's own quest.
                        var open = Open().Where(r => r.Quest.QuestId == quest.Id || S.InstLevelId != 0 && Quests.Value[r.Quest.QuestId] is { Category: 1 or 2 } c && c.LevelId == S.InstLevelId).ToList();
                        // Objectives play in their level: act on those of the current level, else the player goes to the first one's level.
                        var here = open.Where(r => I(r.Cfg.LevelId) is 0 || I(r.Cfg.LevelId) == Level || I(r.Cfg.ObjectiveType) is BigWorldQuestRuntime.TypeEnterLevel or BigWorldQuestRuntime.TypeInstanceComplete).ToList();
                        if (here.Count == 0 && open.Count > 0)
                        {
                            acted = Attempt(open[0].Cfg.Id, "travel") <= 3 && GoTo(I(open[0].Cfg.LevelId));
                            Trace($"{quest.Id} it{iteration} travel to {I(open[0].Cfg.LevelId)} from {Level}: {acted} {lastError}");
                        }
                        foreach ((Theatre5DlcQuest q, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective) in here)
                        {
                            bool did = Act(q, cfg, objective);
                            Trace($"{quest.Id} it{iteration} obj {cfg.Id} type {I(cfg.ObjectiveType)} state {objective.ObjectiveState} lvl {I(cfg.LevelId)}/{Level} acted={did} {(notes.TryGetValue(cfg.Id, out string? n) ? n : "")} {lastError}");
                            acted |= did;
                        }
                        Settle();
                        if (Finished(quest.Id))
                            return null;
                        string now = Signature();
                        string state = now[..now.LastIndexOf('#')];
                        visits[state] = visits.GetValueOrDefault(state) + 1;
                        Trace($"{quest.Id} it{iteration} end state visits={visits[state]} {(visits[state] > 3 ? state : "")}");
                        if (visits[state] > 6)
                            return $"cycling without progress: {Describe(quest)}";
                        if (now == last)
                        {
                            if (scheduled.Count > 0)
                            {
                                (TimeSpan delay, Action callback) = scheduled[0];
                                Trace($"{quest.Id} firing timer {delay.TotalSeconds}s ({scheduled.Count} queued)");
                                scheduled.RemoveAt(0);
                                advance(delay);
                                callback();
                                Quiesce();
                                Settle();
                                visits.Clear();
                                continue;
                            }
                            if (!acted || iteration > 3 && now == last)
                                return Describe(quest);
                        }
                        last = now;
                    }
                    return $"no convergence in 200 steps: {Describe(quest)}";
                }

                private static readonly Lazy<List<(int GroupId, int[] ObjectiveIds)>> EnvQuests = new(() => TableReaderV2.Parse<AscNet.Table.V2.share.dlcworld.questsystem.environmentquest.DlcEnvironmentQuestTable>()
                    .Select(row => (row.GroupId, row.ObjectiveIds.SelectMany(ids => $"{ids}".Split('|')).Select(int.Parse).ToArray())).ToList());
                private static readonly Lazy<Dictionary<int, int>> EnvGroupLevels = new(() => TableReaderV2.Parse<AscNet.Table.V2.share.dlcworld.questsystem.environmentquest.DlcEnvironmentQuestGroupTable>().ToDictionary(g => g.Id, g => g.LevelId));

                // How the player gets a quest: invite quests are accepted in the phone, environment quests by selecting their schedule group
                // for the level, everything else activates by itself in its level.
                private void Activate(DlcQuestTable quest, int level)
                {
                    int[] objectiveIds = ObjectivesByQuest.Value[quest.Id].Select(o => o.Id).ToArray();
                    if (quest.Category == 4)
                    {
                        int code = Request("DlcInviteQuestAcceptRequest", new DlcInviteQuestAcceptRequest { QuestId = quest.Id });
                        if (code != 0)
                            lastError = $"DlcInviteQuestAcceptRequest refused with code {code}";
                    }
                    else if (EnvQuests.Value.FirstOrDefault(e => e.ObjectiveIds.Any(objectiveIds.Contains)) is { GroupId: > 0 } env)
                    {
                        GoTo(EnvGroupLevels.Value[env.GroupId]);
                        int code = Request("DlcEnvironmentQuestGroupChangeRequest", new DlcEnvironmentQuestGroupChangeRequest { LevelId = EnvGroupLevels.Value[env.GroupId], QuestGroupId = env.GroupId });
                        if (code != 0)
                            lastError = $"DlcEnvironmentQuestGroupChangeRequest refused with code {code}";
                    }
                    else
                    {
                        GoTo(level);
                        BigWorldQuestRuntime.OnConditionsChanged(Session);
                    }
                    Settle();
                }

                private static int QuestLevel(DlcQuestTable quest) =>
                    quest.LevelId > 0 ? quest.LevelId : ObjectivesByQuest.Value[quest.Id].Select(o => I(o.LevelId)).FirstOrDefault(l => l > 0);

                private void ForceFinish(int questId)
                {
                    // Lists of the abandoned quest still in flight are dropped, as are the client's unanswered executes.
                    BigWorldLevelActions.Reset(Session);
                    Drain();
                    reacted = rpcs.Count;
                    Data.ActiveQuests.Remove(questId);
                    Data.ReadyQuestIds.Remove(questId);
                    if (!Data.FinishedQuests.Contains(questId))
                        Data.FinishedQuests.Add(questId);
                    if (S.InstLevelId != 0 && Quests.Value[questId].LevelId == S.InstLevelId)
                    {
                        Request("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 0 });
                        Settle();
                    }
                }

                private string Describe(DlcQuestTable quest)
                {
                    if (!Data.ActiveQuests.TryGetValue(quest.Id, out Theatre5DlcQuest? active) || active.DynamicData is null)
                        return "quest vanished";
                    List<string> parts = [];
                    foreach (Theatre5DlcQuestStep step in active.DynamicData.Steps.Values.Where(s => s.StepState == 1))
                        foreach (Theatre5DlcQuestStepObjective o in step.Objectives.Values.Where(o => o.ObjectiveState is > 0 and < 6))
                        {
                            DlcQuestObjectiveTable cfg = Objectives.Value[o.Id];
                            parts.Add($"objective {o.Id} type {I(cfg.ObjectiveType)} state {o.ObjectiveState} level {I(cfg.LevelId)} (player in {Level}){(notes.TryGetValue(o.Id, out string? note) ? $": {note}" : "")}");
                        }
                    if (parts.Count == 0)
                        parts.Add($"quest state {active.DynamicData.QuestState}, no open objective (steps {string.Join(",", active.DynamicData.Steps.Values.Select(s => $"{s.StepId}:{s.StepState}"))})");
                    return string.Join("; ", parts) + (lastError.Length > 0 ? $" [{lastError}]" : "");
                }

                private int Attempt(int objectiveId, string kind) => attempts[objectiveId * 31 + kind.GetHashCode()] = attempts.GetValueOrDefault(objectiveId * 31 + kind.GetHashCode()) + 1;

                // One player action for an open objective; true when something was sent.
                private bool Act(Theatre5DlcQuest quest, DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective)
                {
                    int type = I(cfg.ObjectiveType), objectiveLevel = I(cfg.LevelId);
                    JObject config = BigWorldQuestRuntime.Config(cfg);
                    if (objective.ObjectiveState != BigWorldQuestRuntime.StateInProgress)
                    {
                        notes[cfg.Id] = $"{(objective.ObjectiveState is 1 or 2 ? "Enter" : "Exit")} action list never completed";
                        return false;
                    }
                    notes.Remove(cfg.Id);
                    switch (type)
                    {
                        case BigWorldQuestRuntime.TypeCheckRim:
                            return SatisfyConditions(cfg);
                        case BigWorldQuestRuntime.TypeDramaPlayFinish:
                            notes[cfg.Id] = $"drama {cfg.DramaName} {(dramasSeen.Contains(cfg.DramaName) ? "played, DramaFinish ignored" : "never requested by the server")}";
                            return false;
                        case BigWorldQuestRuntime.TypeEnterLevel:
                        {
                            // EnterLevel closes on the level entry itself (OnLevelEntered); a player already standing in the target level
                            // re-enters it, as a relog or travel back would.
                            int enterTarget = I(cfg.TargetLevelId);
                            if (Attempt(cfg.Id, "enter") > 2)
                                return false;
                            if (enterTarget != Level)
                                return GoTo(enterTarget);
                            Request("BigWorldEnterWorldRequest", new BigWorldEnterWorldRequest { WorldId = WorldOf(enterTarget), LevelId = IsInstance(enterTarget) ? 0 : enterTarget });
                            LoadClient();
                            return true;
                        }
                        case BigWorldQuestRuntime.TypeInstanceComplete:
                        {
                            int inst = I(cfg.InstLevelId);
                            if (S.InstLevelId != inst && Attempt(cfg.Id, "inst") <= 2)
                                return GoTo(inst);
                            // The player leaves an instance once its own quest has nothing left to play (its quest vars, e.g. 4038 -> 2009's
                            // branch gate, are produced by playing it).
                            bool instQuestOpen = Open().Any(r => Quests.Value[r.Quest.QuestId] is { Category: 1 or 2 } c && c.LevelId == inst);
                            if (I(cfg.PassOnLeaveInstLevel) == 1 && S.InstLevelId == inst && !instQuestOpen && Attempt(cfg.Id, "leave") <= 2)
                            {
                                Request("LeaveInstLevelRequest", new LeaveInstLevelRequest { InstSaveOption = 0 });
                                Settle();
                                return true;
                            }
                            notes[cfg.Id] = $"instance {inst} (player in instance {S.InstLevelId}) completes through its own quest";
                            return false;
                        }
                        case BigWorldQuestRuntime.TypeInteractComplete:
                            return InteractTargets(cfg, objective, config);
                        case BigWorldQuestRuntime.TypeReadShortMessage:
                            return ReadMessage(cfg);
                        case BigWorldQuestRuntime.TypeKillEnemyGroup:
                        {
                            // The server spawned the group's members when the objective opened (RpcActorChangeController hands each to the client).
                            int opened = rpcs.FindLastIndex(r => r.Name == "RpcQuestObjectiveUpdate" && r.Args is [_, var id, var st, ..] && Convert.ToInt32(id) == cfg.Id && Convert.ToInt32(st) == 1);
                            bool killed = false;
                            for (int i = opened + 1; i < rpcs.Count; i++)
                            {
                                if (rpcs[i].Name != "RpcActorChangeController" || rpcs[i].Args is not [var lvl, var uuid, ..] || Convert.ToInt32(lvl) != Level || Attempt(Convert.ToInt32(uuid), "kill") != 1)
                                    continue;
                                // The player shoots from outside: a member standing in rock or at a cliff foot cannot be hit, so the quest waits for it
                                // (live 4033 wave 2 stalled at 9/10 on such a slot while this driver killed every spawned uuid).
                                if (memberSpots.TryGetValue(Convert.ToInt32(uuid), out var spot) && BigWorldModule.InSceneSolid(Level, spot.X, spot.Y + 1, spot.Z, 1))
                                    notes[cfg.Id] = $"group member {Convert.ToInt32(uuid)} spawned inside scene geometry at ({spot.X:F1}, {spot.Y:F1}, {spot.Z:F1}): the player cannot hit it";
                                else
                                    killed |= X("XRpcNpcDie", Pack(0, 0), "XRpcActorAction", Convert.ToInt32(uuid)) == 0;
                            }
                            if (!killed && !notes.ContainsKey(cfg.Id))
                                notes[cfg.Id] = "no group member was spawned for the client";
                            return killed;
                        }
                        case BigWorldQuestRuntime.TypeReachTarget:
                        case BigWorldQuestRuntime.TypeScanPlusSearch:
                            return Attempt(cfg.Id, "complete") <= 1 && X("RpcQuestObjectiveCompleteRequest", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["QuestId"] = cfg.QuestId, ["ObjectiveId"] = cfg.Id })) == 0;
                        case BigWorldQuestRuntime.TypeUiClosed:
                            return Attempt(cfg.Id, "ui") <= 1 && X("RpcUiClosedNotify", Pack(cfg.UiName, (cfg.IntParams ?? []).ToList())) == 0;
                        case BigWorldQuestRuntime.TypeTakePhoto:
                            if (I(cfg.CamParamId) > 0)
                            {
                                notes[cfg.Id] = "photo mode request never pushed";
                                return false;
                            }
                            return Attempt(cfg.Id, "photo") <= 1 && X("RpcTakePhotoCompleteNotify", Pack((cfg.DetectionNpcPlaceIdList ?? []).ToList(), (cfg.DetectionSceneObjectPlaceIdList ?? []).ToList(), I(cfg.PhotoFilterId),
                                config["PlayerNpcAnimationDict"]?.ToObject<Dictionary<int, string>>() ?? [], config["LevelNpcAnimationDict"]?.ToObject<Dictionary<int, string>>() ?? [], 0)) == 0;
                        case BigWorldQuestRuntime.TypeOnLevelTimeOut:
                            notes[cfg.Id] = "level timer never started or never elapsed";
                            return false;
                        case BigWorldQuestRuntime.TypeCheckIntVar:
                            if (Attempt(cfg.Id, "var") > 1)
                                return false;
                            // The var is produced by the fight script / statistics (client simulated); the sweep sets what satisfies the check.
                            int target = I(cfg.CheckType) switch { 1 => I(cfg.Value) + 1, 5 => I(cfg.Value) - 1, _ => I(cfg.Value) };
                            BigWorldQuestRuntime.SetVar(Session, cfg.QuestId, 1, cfg.Key!, target);
                            return true;
                        case BigWorldQuestRuntime.TypeNarrativeComplete:
                            foreach (int id in cfg.NarrativeIds ?? [])
                                X("RpcNarrativeCompleteNotify", Pack(id));
                            return Attempt(cfg.Id, "narrative") <= 1;
                        case BigWorldQuestRuntime.TypeCollectSceneObject:
                            foreach (int place in cfg.PlaceIdList ?? [])
                                Interact(BigWorldActors.SceneObjectType, place, cfg);
                            return Attempt(cfg.Id, "collect") <= 1;
                        case BigWorldQuestRuntime.TypeDeliverItems:
                            return Attempt(cfg.Id, "deliver") <= 1 && X("RpcQuestDeliverItemsRequest", Pack(cfg.Id, config["RequiredItemInfoDict"]?.ToObject<Dictionary<int, int>>() ?? [])) == 0;
                        case BigWorldQuestRuntime.TypeKillEnemy:
                            foreach (object place in cfg.ExistsEnemyList ?? [])
                                if (BigWorldActors.TryGetUuid(Session, Level, BigWorldActors.NpcType, I(place), out int uuid))
                                    X("XRpcNpcDie", Pack(0, 0), "XRpcActorAction", uuid);
                                else
                                    notes[cfg.Id] = $"enemy NPC place {I(place)} not replicated in level {Level}";
                            return Attempt(cfg.Id, "kill") <= 1;
                        default:
                            notes[cfg.Id] = $"objective type {type} has no player action";
                            return false;
                    }
                }

                private static readonly Lazy<Dictionary<int, BigWorldConditionTable>> Conditions = new(() => TableReaderV2.Parse<BigWorldConditionTable>().ToDictionary(c => c.Id));

                // CheckRimSystemCondition reads game state the player changes elsewhere: a finished main-game guide (10187) and a marked custom
                // param (10101008) are produced the way the client does (guide completion, BigWorldMarkCustomParamRequest); anything else is reported.
                private bool SatisfyConditions(DlcQuestObjectiveTable cfg)
                {
                    bool sent = false;
                    foreach (int id in cfg.ConditionIds ?? [])
                    {
                        if (BigWorldConditionService.Check(player, id) || Attempt(cfg.Id * 7 + id, "condition") > 1)
                            continue;
                        BigWorldConditionTable row = Conditions.Value[id];
                        IReadOnlyList<int> p = row.Params ?? [];
                        if (row.Type == 10187 && p.Count > 1 && p[0] == 1)
                        {
                            (player.PlayerData.GuideData ??= new()).Add(p[1]);
                            BigWorldQuestRuntime.OnConditionsChanged(Session);
                            sent = true;
                        }
                        else if (row.Type == 10101008 && p.Count > 1 && p[1] == 1)
                            sent = Request("BigWorldMarkCustomParamRequest", new BigWorldMarkCustomParamRequest { Id = p[0] }) == 0;
                        // Minigame state (cafe stage star, street stage cleared) is produced by playing the stage, as the client does.
                        else if (row.Type == 10203001 && p.Count > 1)
                            sent = PlayCafe(p[0], p[1]);
                        else if (row.Type == 10202028 && p.Count > 0)
                            sent = PlayStreet(p[0]);
                        else if (row.Type == 10204001 && p.Count > 1)
                            sent = PlayDrone(p[0]);
                    }
                    notes[cfg.Id] = "conditions " + string.Join(",", (cfg.ConditionIds ?? []).Select(id => $"{id}(type {Conditions.Value[id].Type})={BigWorldConditionService.Check(player, id)}"));
                    Settle();
                    return sent;
                }

                private static readonly Lazy<Dictionary<int, SgDroneGameStageTable>> DroneStages = new(() => TableReaderV2.Parse<SgDroneGameStageTable>().ToDictionary(s => s.Id));
                private static readonly Lazy<Dictionary<int, SgDroneGameStarTargetTable>> DroneTargets = new(() => TableReaderV2.Parse<SgDroneGameStarTargetTable>().ToDictionary(t => t.Id));

                // The client simulates the drone run (XSGDGInstance) and reports the star-target progress; the player meets every target (type 7
                // reach: 1, 8 collect: >= P1, 9 max damage: <= P1). Pre-stages of the chain are passed first.
                private bool PlayDrone(int stageId)
                {
                    SgDroneGameStageTable stage = DroneStages.Value[stageId];
                    List<int> targets = stage.StarTargets.Where(id => id > 0).ToList();
                    if (stage.PreStageId > 0 && !(S.SgDroneStages.GetValueOrDefault(stage.PreStageId) is { } pre && (pre.IsFinished || pre.FinishedStarTargets.Count > 0)) && !PlayDrone(stage.PreStageId))
                        return false;
                    int code = Request("SgDroneGameStageStartRequest", new SgDroneGameStageStartRequest { StageId = stageId });
                    if (code == 0)
                        code = Request("SgDroneGameStageSettleRequest", new SgDroneGameStageSettleRequest
                        {
                            StageId = stageId,
                            CostTime = 60,
                            TargetProgress = targets.ToDictionary(id => id, id => DroneTargets.Value[id] is { Type: 8 } t ? t.Params.FirstOrDefault() : DroneTargets.Value[id].Type == 7 ? 1 : 0)
                        });
                    if (code != 0)
                        lastError = $"drone stage {stageId} refused with code {code}";
                    return code == 0;
                }

                private static readonly Lazy<Dictionary<int, SGCafeStageTable>> CafeStages = new(() => TableReaderV2.Parse<SGCafeStageTable>().ToDictionary(s => s.Id));

                // The client plays a Kuroro Coffee story stage round by round (it simulates the sales) and reports the final period; the player
                // reaches the target of the wanted star. Earlier stages of the chain (PreStage) are played first.
                private bool PlayCafe(int stageId, int star)
                {
                    SGCafeStageTable stage = CafeStages.Value[stageId];
                    if (stage.PreStage > 0 && S.SgCafe?.CafeStageList.GetValueOrDefault(stage.PreStage)?.GetMaxStarReward is not > 0 && !PlayCafe(stage.PreStage, 1))
                        return false;
                    if (stage.Type != 1)
                    {
                        lastError = $"cafe stage {stageId} is a challenge stage (needs a card deck)";
                        return false;
                    }
                    int sales = stage.Target.Where(target => target > 0).Take(star).LastOrDefault();
                    int code = Request("BigWorldCafeNewRoundRequest", new BigWorldCafeNewRoundRequest { CafeGambling = new CafeGambling { StageId = stageId, Round = 1 } });
                    for (int round = 2; code == 0 && round <= stage.Rounds + 1; round++)
                        code = Request("BigWorldCafeNextRoundRequest", new BigWorldCafeNextRoundRequest { CafeGambling = new CafeGambling { StageId = stageId, Round = round, SumSales = round == stage.Rounds + 1 ? sales : 0 } });
                    if (code != 0)
                        lastError = $"cafe stage {stageId} round request refused with code {code}";
                    return code == 0;
                }

                // The player plays a shopping street stage turn by turn (open shop, settle the day with no events) until the stage settles as won.
                private bool PlayStreet(int stageId)
                {
                    SgStreetData street = S.SgStreet;
                    for (int id = 1; id <= stageId; id++)
                    {
                        if (street.PassedStageRecords.ContainsKey(id))
                            continue;
                        int code = Request("SgStreetStageStartRequest", new SgStreetStageStartRequest { StageId = id });
                        if (code != 0)
                        {
                            lastError = $"street stage {id} start refused with code {code}";
                            return false;
                        }
                        for (int turn = 0; turn < 60 && !street.PassedStageRecords.ContainsKey(id); turn++)
                        {
                            if (street.CurStageData?.BillboardData is { CurrentBillboardId: 0, RandomBillboards.Count: > 0 } billboards)
                                Request("SgStreetBillboardSelectRequest", new SgStreetBillboardSelectRequest { BillboardId = billboards.RandomBillboards[0] });
                            Request("SgStreetOperatingStartRequest", new SgStreetOperatingStartRequest());
                            Request("SgStreetOperatingSettleRequest", new SgStreetOperatingSettleRequest());
                            Request("SgStreetStageWinSettleRequest", new SgStreetStageWinSettleRequest { StageId = id });
                        }
                        if (!street.PassedStageRecords.ContainsKey(id))
                        {
                            lastError = $"street stage {id} never settled as won";
                            return false;
                        }
                    }
                    return true;
                }

                private bool InteractTargets(DlcQuestObjectiveTable cfg, Theatre5DlcQuestStepObjective objective, JObject config)
                {
                    if (Attempt(cfg.Id, "interact") > 3 || config["TargetArgs"] is not JObject targets)
                        return false;
                    bool sent = false;
                    foreach (JProperty type in targets.Properties())
                        foreach (JProperty place in ((JObject)type.Value).Properties())
                        {
                            int actorType = int.Parse(type.Name), placeId = int.Parse(place.Name), wanted = place.Value.Value<int>();
                            if (!(objective.InteractProgressRecords.GetValueOrDefault(actorType)?.Any(r => r.WasCompleted && r.TargetPlaceId == placeId) ?? false))
                                sent |= Interact(actorType, placeId, cfg, wanted);
                        }
                    return sent;
                }

                // IsOnlyClient NPCs are created by the client itself (LoadNpc / level load), never replicated: the player controller gives each one
                // XController.GenerateUUID = (++IncId << 4) | controllerId [DUMP48 0x1B01BD0] and the interact request carries that local uuid.
                private readonly Dictionary<(int Level, int PlaceId), int> clientUuids = [];
                private readonly Dictionary<int, int> clientIncIds = [];

                private bool Interact(int actorType, int placeId, DlcQuestObjectiveTable cfg, int wantedOption = 0)
                {
                    int level = Level;
                    if (!BigWorldActors.TryGetUuid(Session, level, actorType, placeId, out int uuid))
                    {
                        // The server replicates only IsOnlyClient 0; -1 (importer could not delimit the record's tail) is left to the client too.
                        if (actorType != BigWorldActors.NpcType || BigWorldModule.LevelNpcOf(level, placeId) is not { IsOnlyClient: not 0 })
                        {
                            notes[cfg.Id] = $"actor type {actorType} place {placeId} is not replicated in level {level}";
                            return false;
                        }
                        if (!clientUuids.TryGetValue((level, placeId), out uuid))
                            clientUuids[(level, placeId)] = uuid = (clientIncIds[level] = clientIncIds.GetValueOrDefault(level) + 1) << 4 | BigWorldXRpc.PlayerControllerId;
                    }
                    // TargetArgs names the option the objective takes (0 = any): the player picks it.
                    int optionId = wantedOption > 0 ? wantedOption : BigWorldModule.InteractOptionsOf(level, actorType, placeId) is { Count: > 0 } options ? options.Keys.Min() : 0;
                    int code = X("RpcPlayerInteractRequest", Pack((int)player.PlayerData.Id, 1, BigWorldActors.TeamNpcUuid(Session, level, S.CurNpcPos), uuid, placeId, actorType, level, optionId));
                    if (code != 0)
                        notes[cfg.Id] = $"RpcPlayerInteractRequest on type {actorType} place {placeId} option {optionId} refused with code {code}";
                    Settle();
                    return code == 0;
                }

                private static readonly Lazy<Dictionary<int, BigWorldMessageTable>> Messages = new(() => TableReaderV2.Parse<BigWorldMessageTable>().ToDictionary(m => m.Id));
                private static readonly Lazy<Dictionary<int, BigWorldMessageStepTable>> MessageSteps = new(() => TableReaderV2.Parse<BigWorldMessageStepTable>().ToDictionary(s => s.Id));

                // The player reads the message down its first branch to a terminal step.
                private bool ReadMessage(DlcQuestObjectiveTable cfg)
                {
                    int messageId = I(cfg.ShortMessageId);
                    if (Attempt(cfg.Id, "message") > 1)
                        return false;
                    if (!Messages.Value.TryGetValue(messageId, out BigWorldMessageTable? message))
                    {
                        notes[cfg.Id] = $"short message {messageId} is not in BigWorldMessage";
                        return false;
                    }
                    if (!S.Messages.ContainsKey(messageId))
                    {
                        notes[cfg.Id] = $"short message {messageId} was never activated for the player";
                        return false;
                    }
                    for (int step = message.FirstStepId, guard = 0; step > 0 && guard < 64; guard++)
                    {
                        int code = Request("BigWorldMessageReadRecordRequest", new BigWorldMessageReadRecordRequest { MessageId = messageId, StepId = step });
                        if (code != 0)
                        {
                            notes[cfg.Id] = $"BigWorldMessageReadRecordRequest step {step} refused with code {code}";
                            return true;
                        }
                        step = MessageSteps.Value[step].NextStep.FirstOrDefault(id => id > 0);
                    }
                    return true;
                }

                #endregion
            }
        }
    }
}
