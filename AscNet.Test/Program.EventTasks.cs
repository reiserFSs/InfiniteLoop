using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.repeatchallenge;
using AscNet.Table.V2.share.miniactivity.pacman2;
using AscNet.Table.V2.share.task;
using MessagePack;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    // Event-page tasks (TaskTimeLimit groups of an open calendar window) of the 4.9 "A Comet's Afterglow" page:
    // Simulated Battlefield clears (15201) and total serum (15223, bar of group 790), Munchy Pixels first passes (123002).
    private static void ValidateEventTasksCompatibility()
    {
        Func<DateTimeOffset> originalClock = TaskModule.ProgressClock;
        DateTimeOffset pinned = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        TaskModule.ProgressClock = () => pinned;
        try { ValidateEventTasksFlow(pinned); }
        finally
        {
            TaskModule.ProgressClock = originalClock;
            ActivityScheduleService.TestEntries = [];
        }
    }

    private static void ValidateEventTasksFlow(DateTimeOffset pinned)
    {
        const int sbStageId = 30_090_802, sbStageTimeId = 51_502, pageTimeId = 51_903;
        const int onceTask = 120_766, fiveTimesTask = 120_767, pixelsTask = 120_768, bossTask = 120_771;
        int[] serumTasks = [91_192, 91_193, 91_194, 91_195, 91_196];
        int[] serumTargets = [240, 840, 1_710, 2_820, 4_050];
        foreach (int timeId in new[] { sbStageTimeId, pageTimeId })
            AssertEqual(true, ActivityScheduleService.IsOpen(timeId, pinned), $"TimeId {timeId} open at the pinned clock");

        Dictionary<int, TaskTable> taskRows = TableReaderV2.Parse<TaskTable>().ToDictionary(row => row.Id);
        RepeatChallengeActivityTable activity = TableReaderV2.Parse<RepeatChallengeActivityTable>().OrderBy(row => row.Id).First();
        int cost = TableReaderV2.Parse<StageTable>().Single(row => row.StageId == sbStageId).RequireActionPoint!.Value;
        const long playerId = 99_251;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.SimulatedBattlefield = new()
        {
            RepeatChallengeActivityId = activity.Id,
            RepeatChallengeCleared = true,
            RepeatChallengeLevel = TableReaderV2.Parse<RepeatChallengeLevelTable>().Count(),
            RepeatChallengeExp = TableReaderV2.Parse<RepeatChallengeLevelTable>().Sum(row => row.UpExp)
        };
        Inventory inventory = CreateDrawCompatibilityInventory(playerId, [new Item { Id = Inventory.ActionPoint, Count = cost * 60 }]);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> playerSaves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, inventory, "event-tasks", startClientLoop: false);
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

        int packetId = 4_000;
        List<Packet.Push> pushes = [];
        T Call<T>(string name, object request)
        {
            int id = ++packetId;
            pushes.Clear();
            InvokeRegisteredRequestHandler(name, harness.Session, id, request);
            while (true)
            {
                Packet packet = harness.ReadPacket(name);
                if (packet.Type == Packet.ContentType.Push) { pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content)); continue; }
                Packet.Response wire = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(id, wire.Id, $"{name} response id");
                AssertEqual(name.Replace("Request", "Response"), wire.Name, $"{name} response name");
                return MessagePackSerializer.Deserialize<T>(wire.Content);
            }
        }
        // "value/state" of a task: latest across the NotifyTask pushes of the last request, or in the login list ("-" = absent).
        string Pushed(int taskId)
        {
            string last = "-";
            foreach (Packet.Push push in pushes.Where(push => push.Name == nameof(NotifyTask)))
                foreach (var task in MessagePackSerializer.Deserialize<NotifyTask>(push.Content).Tasks.Tasks.Where(task => task.Id == taskId))
                    last = $"{task.Schedule[0].Value}/{task.State}";
            return last;
        }
        string Login(int taskId) => TaskModule.BuildTaskData(harness.Session).Where(row => row.Id == taskId)
            .Select(row => $"{row.Schedule[0].Value}/{row.State}").SingleOrDefault() ?? "-";
        string Show(long value, int state) => $"{value}/{state}";
        Player Current() => harness.Session.player;
        long Counter(int taskId) => Current().MissionProgress.ConditionCounters.GetValueOrDefault(taskRows[taskId].Condition);
        void Sweep(int count)
        {
            AssertEqual(0, Call<SweepResponse>("SweepRequest", new SweepRequest { StageId = sbStageId, Count = count }).Code, $"sweep x{count}");
        }
        int State(bool achieved) => achieved ? 3 : 1;

        // Fresh player: every event task of the window is listed at login with no progress (they were missing before).
        foreach (int id in new[] { onceTask, fiveTimesTask, pixelsTask, bossTask }.Concat(serumTasks))
            AssertEqual("0/1", Login(id), $"task {id} listed at login, not started");

        // Sweep x2: the clears and the spent serum both count; unrelated stage tasks stay at 0.
        Sweep(2);
        AssertEqual("1/3", Pushed(onceTask), "clear-once pushed achieved after two clears");
        AssertEqual("2/1", Pushed(fiveTimesTask), "clear-5-times pushed at 2/5");
        AssertEqual(Show(Math.Min(serumTargets[0], 2 * cost), 1), Pushed(serumTasks[0]), "serum bar pushed with the spent serum");
        AssertEqual(2, Counter(fiveTimesTask), "clear counter counts both sweep clears");
        AssertEqual(2 * cost, Counter(serumTasks[0]), "serum counter equals the committed spend");
        AssertEqual("0/1", Login(bossTask), "a clear of another stage is not credited to the boss-stage task");
        
        // Claim boundaries: unearned, earned once, duplicate.
        AssertEqual(20026007, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = fiveTimesTask }).Code, "unearned claim rejected");
        long RewardSum(IEnumerable<RewardGoods> goods) => goods.Sum(row => (long)row.Count);
        long expectedReward = RewardSum(RewardHandler.GetRewardGoods(taskRows[onceTask].RewardId ?? 0).Select(row => new RewardGoods { Count = row.Count }));
        FinishTaskResponse claimed = Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = onceTask });
        AssertEqual(0, claimed.Code, "clear-once claim accepted");
        AssertEqual(true, expectedReward > 0 && expectedReward == RewardSum(claimed.RewardGoodsList), "claim grants the task's reward goods");
        AssertEqual("1/4", Login(onceTask), "claimed task finished at login");
        AssertEqual(20026006, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = onceTask }).Code, "duplicate claim rejected");

        // Persistence: the progress lives in the saved player document.
        harness.Session.player = BsonSerializer.Deserialize<Player>(Current().ToBson());
        AssertEqual("1/4", Login(onceTask), "claim survives BSON reload");
        AssertEqual("2/1", Login(fiveTimesTask), "clear counter survives BSON reload");
        AssertEqual(Show(2 * cost, 1), Login(serumTasks[0]), "serum counter survives BSON reload");

        // Distinct inputs: 3 more clears finish the 5-times task; 6 more clears cross the first serum milestone.
        int clears = 2;
        foreach (int count in new[] { 3, 3, 22 })
        {
            Sweep(count);
            clears += count;
            AssertEqual(Show(Math.Min(5, clears), State(clears >= 5)), Pushed(fiveTimesTask), $"clear-5-times after {clears} clears");
            for (int index = 0; index < serumTasks.Length; index++)
            {
                int value = Math.Min(serumTargets[index], clears * cost);
                AssertEqual(Show(value, State(value >= serumTargets[index])), Login(serumTasks[index]), $"serum milestone {serumTargets[index]} after {clears * cost} serum");
            }
        }
        FinishTaskResponse bar = Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = fiveTimesTask });
        AssertEqual(0, bar.Code, "clear-5-times claim accepted once earned");
        AssertEqual(20026007, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = serumTasks[^1] }).Code, "unreached serum milestone rejected");

        // Calendar window: a closed TaskTimeLimit window stops crediting and listing (here only the serum group is closed).
        long serumBefore = Counter(serumTasks[0]), clearsBefore = Counter(fiveTimesTask);
        ActivityScheduleService.TestEntries = [new ActivityScheduleEntry(sbStageTimeId, 0, pinned.ToUnixTimeSeconds() - 60, "test:closed-window")];
        Sweep(1);
        AssertEqual(serumBefore, Counter(serumTasks[0]), "closed window credits no serum");
        AssertEqual(clearsBefore + 1, Counter(fiveTimesTask), "the page window (still open) keeps counting clears");
        AssertEqual("-", Login(serumTasks[0]), "closed window hides the bar tasks");
        ActivityScheduleService.TestEntries = [];

        // A manual Simulated Battlefield battle (PreFight + FightSettle, 2 attempts): clears count; serum counts what was deducted.
        long apBefore = inventory.Items.Single(item => item.Id == Inventory.ActionPoint).Count;
        long clearsBeforeBattle = Counter(fiveTimesTask), serumBeforeBattle = Counter(serumTasks[0]);
        PreFightResponse fight = Call<PreFightResponse>(nameof(PreFightRequest), new PreFightRequest
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData { ChallengeCount = 2, StageId = sbStageId, CardIds = [1_021_001], RobotIds = [] }
        });
        AssertEqual(0, fight.Code, "Simulated Battlefield pre-fight");
        FightSettleResponse settle = Call<FightSettleResponse>(nameof(FightSettleRequest), new FightSettleRequest
        {
            Result = new FightSettleResult { IsWin = true, StageId = sbStageId, FightId = fight.FightData.FightId }
        });
        AssertEqual(0, settle.Code, "Simulated Battlefield settle");
        long apSpent = apBefore - inventory.Items.Single(item => item.Id == Inventory.ActionPoint).Count;
        AssertEqual(clearsBeforeBattle + 2, Counter(fiveTimesTask), "battle settle counts both attempts as clears");
        AssertEqual(serumBeforeBattle + apSpent, Counter(serumTasks[0]), "battle serum counted equals the serum actually deducted");

        // Munchy Pixels (PacMan2 activity 3): first passes of its stages, derived from the saved records.
        PacMan2StageTable[] chain = TableReaderV2.Parse<PacMan2StageTable>().Where(row => row.GroupId == 3).OrderBy(row => row.GroupOrder).ToArray();
        PacMan2StageTable s1 = chain[0], s2 = chain[1];
        int Start(PacMan2StageTable stage) => Call<PacMan2StageStartResponse>("PacMan2StageStartRequest", new PacMan2StageStartRequest { StageId = stage.Id }).Code;
        PacMan2SettleResponse Pass(PacMan2StageTable stage, int score) => Call<PacMan2SettleResponse>("PacMan2SettleRequest", new PacMan2SettleRequest
        {
            StageId = stage.Id, Score = score, SettleType = 1, OperateType = 1, Time = 0, Orbs = stage.RedOrb, Kills = 0, Hp = stage.InitialHp,
            Shoe = [new PacMan2ShoeData { Id = 6, Times = 0 }]
        });
        AssertEqual(0, Start(s1), "first Munchy Pixels stage starts");
        AssertEqual(0, Pass(s1, s1.Star[0] - 1).Code, "sub-star run settles");
        AssertEqual("0/1", Login(pixelsTask), "a run below the first star is not a pass");
        AssertEqual(0, Start(s1), "restart first stage");
        AssertEqual(0, Pass(s1, s1.Star[0]).Code, "first pass settles");
        AssertEqual("1/1", Pushed(pixelsTask), "first pass pushes 1/2");
        AssertEqual(0, Start(s1), "replay first stage");
        AssertEqual(0, Pass(s1, s1.Star[2]).Code, "better score settles");
        AssertEqual("1/1", Login(pixelsTask), "a replay of a passed stage is not a new first pass");
        AssertEqual(0, Start(s2), "second stage starts after the first pass");
        AssertEqual(0, Pass(s2, s2.Star[0]).Code, "second pass settles");
        AssertEqual("2/3", Pushed(pixelsTask), "second distinct pass achieves the task");
        harness.Session.player = BsonSerializer.Deserialize<Player>(Current().ToBson());
        AssertEqual("2/3", Login(pixelsTask), "pass count survives BSON reload");
        AssertEqual(0, Call<FinishTaskResponse>(nameof(FinishTaskRequest), new FinishTaskRequest { TaskId = pixelsTask }).Code, "Munchy Pixels claim accepted");
        AssertEqual("2/4", Login(pixelsTask), "Munchy Pixels task finished");

        // The same producers serve every event of this kind: another page's stage-clear tasks (Endeavorer's Charm, TimeId 51205).
        TaskTimeLimitTable charm = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => row.Id == 792);
        AssertEqual(true, ActivityScheduleService.IsOpen(charm.TimeId!.Value, pinned), "Endeavorer's Charm window open");
        ConditionTable[] charmConditions = charm.TaskId.Select(id => TableReaderV2.Parse<ConditionTable>().Single(row => row.Id == taskRows[id].Condition)).ToArray();
        int firstStage = charmConditions[0].Params[1], secondStage = charmConditions[1].Params[1];
        AssertEqual("0/1", Login(charm.TaskId[0]), "stage task listed before its stage is cleared");
        TaskModule.RecordStageClear(harness.Session, firstStage, 1, 0, true);
        AssertEqual("1/3", Login(charm.TaskId[0]), "clearing the first stage achieves its task");
        AssertEqual("0/1", Login(charm.TaskId[1]), "clearing the first stage leaves the second stage's task");
        TaskModule.RecordStageClear(harness.Session, secondStage, 1, 0, true);
        AssertEqual("1/3", Login(charm.TaskId[1]), "clearing the second stage achieves its task");
        AssertEqual(true, playerSaves.ReplaceOneCalls > 0, "progress was persisted");
    }
}
