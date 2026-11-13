using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.guide;
using AscNet.Table.V2.share.task;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;

namespace AscNet.Test;

internal static partial class Program
{
    // 4.8 guides (650xx): registered completion and group finish persist.
    private static void ValidateVersion48GuideCompletion()
    {
        List<GuideGroupTable> guides = TableReaderV2.Parse<GuideGroupTable>();
        HashSet<int> completions = TableReaderV2.Parse<GuideCompleteTable>().Select(row => row.Id).ToHashSet();
        GuideGroupTable[] added = guides.Where(row => row.Id is >= 65001 and <= 65051).ToArray();
        AssertEqual(true, added.Length > 0 && added.All(row => completions.Contains(row.CompleteId)),
            "4.8 guides resolve their authored GuideComplete rows");
        GuideGroupTable complete = added.First(row => row.Ignore == 0 && row.RewardId == 0);
        GuideGroupTable group = added.Last(row => row.RewardId == 0 && row.GroupId != 0 && row.Id != complete.Id);

        const long uid = 48_810;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.GuideData = [];
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "v48-guide");
        InvokeRegisteredRequestHandler(nameof(GuideCompleteRequest), harness.Session, 48_810,
            new GuideCompleteRequest { GuideGroupId = complete.Id });
        AssertEqual(complete.Id, ReadPushPayload<NotifyGuide>(harness, nameof(NotifyGuide), "4.8 guide notify").GuideGroupId,
            "4.8 guide completion notify");
        AssertEqual(0, ReadResponsePayload<GuideCompleteResponse>(harness, 48_810, nameof(GuideCompleteResponse), "4.8 guide").Code,
            "4.8 guide completion accepted");
        InvokeRegisteredRequestHandler(nameof(GuideGroupFinishRequest), harness.Session, 48_811,
            new GuideGroupFinishRequest { GroupId = group.GroupId });
        AssertEqual(0, ReadResponsePayload<GuideGroupFinishResponse>(harness, 48_811, nameof(GuideGroupFinishResponse),
            "4.8 guide group", maxPacketsToRead: 8).Code, "4.8 guide group finish accepted");
        AssertEqual(true, player.PlayerData.GuideData.Contains(complete.Id) && player.PlayerData.GuideData.Contains(group.Id),
            "4.8 guide completion and group finish recorded");
        AssertEqual(2, saves.ReplaceOneCalls, "4.8 guide completion and group finish persisted");

    }

    // 4.8 activity-panel tasks (TaskTimeLimit 768..779): progress, claim, reload and closed-window expiry.
    private static void ValidateVersion48EventPanelTasks()
    {
        Dictionary<int, TaskTimeLimitTable> limits = TableReaderV2.Parse<TaskTimeLimitTable>().ToDictionary(row => row.Id);
        Dictionary<int, CurrentTaskTable> tasks = TableReaderV2.Parse<CurrentTaskTable>().ToDictionary(row => row.Id);
        Dictionary<int, CurrentConditionTable> conditions = TableReaderV2.Parse<CurrentConditionTable>().ToDictionary(row => row.Id);
        TaskTimeLimitTable panel = limits[768];
        CurrentTaskTable stageTask = tasks[panel.TaskId.First(id => conditions[tasks[id].Condition] is { Type: 15201 } c && c.Params[0] == 1)];
        CurrentTaskTable countTask = tasks[panel.TaskId.First(id => conditions[tasks[id].Condition] is { Type: 15201 } c && c.Params[0] > 1)];
        CurrentTaskTable fangKuaiTask = tasks[panel.TaskId.First(id => conditions[tasks[id].Condition].Type == 107001)];
        int stageId = conditions[stageTask.Condition].Params[1];
        AssertEqual(stageId, conditions[countTask.Condition].Params[1], "4.8 panel count task shares its stage");

        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int index = Array.FindIndex(schedules, entry => entry.Id == panel.TimeId);
        ActivityScheduleEntry window = schedules[index];
        try
        {
            schedules[index] = window with { StartTime = 0, EndTime = 0, Source = "synthetic-test:v48-panel-open" };
            const long uid = 48_820;
            Player player = CreateDrawCompatibilityPlayer(uid);
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
                out RecordingMongoCollectionProxy<Player> saves, out _, out _);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
                CreateDrawCompatibilityInventory(uid, []), "v48-panel-tasks");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            int packetId = 48_820;
            FinishTaskResponse Claim(int taskId)
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, id, new FinishTaskRequest { TaskId = taskId });
                return ReadResponsePayload<FinishTaskResponse>(harness, id, nameof(FinishTaskResponse), $"claim {taskId}", maxPacketsToRead: 16);
            }
            LoginTask Row(int id) => BuildTaskData(harness.Session).Single(row => row.Id == id);

            AssertEqual(1, Row(stageTask.Id).State, "4.8 panel task visible and active before its stage");
            AssertEqual(20026007, Claim(stageTask.Id).Code, "4.8 panel task rejects claim before its stage");
            MethodInfo recordStageClear = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
                "RecordStageClear", BindingFlags.Static | BindingFlags.Public, [typeof(AscNet.GameServer.Session), typeof(int), typeof(int), typeof(int)]);
            harness.Session.stage.AddStage(new StageDatum { StageId = (uint)stageId, Passed = true });
            for (int clear = 0; clear < countTask.Result; clear++)
                recordStageClear.Invoke(null, [harness.Session, stageId, 1, 0]);
            AssertEqual(3, Row(stageTask.Id).State, "4.8 panel stage task achieved by the authored stage");
            AssertEqual((long)countTask.Result, Row(countTask.Id).Schedule[0].Value, "4.8 panel count task counts stage clears");
            AssertEqual(3, Row(countTask.Id).State, "4.8 panel count task achieved at its authored result");
            player.FangKuai.PlayedStageIds.Add(conditions[fangKuaiTask.Condition].Params[1]);
            AssertEqual(3, Row(fangKuaiTask.Id).State, "4.8 panel FangKuai task achieved by the played stage");

            int savesBefore = saves.ReplaceOneCalls;
            FinishTaskResponse claimed = Claim(stageTask.Id);
            AssertEqual(0, claimed.Code, "4.8 panel task claim accepted");
            AssertEqual(true, claimed.RewardGoodsList.Count > 0, "4.8 panel task grants its authored reward");
            AssertEqual(true, saves.ReplaceOneCalls > savesBefore && player.MissionProgress.ClaimedTaskIds.Contains(stageTask.Id),
                "4.8 panel task claim persisted");
            AssertEqual(4, Row(stageTask.Id).State, "4.8 panel task finished after reload");
            AssertEqual(20026006, Claim(stageTask.Id).Code, "4.8 panel task cannot be claimed twice");

            schedules[index] = window with { StartTime = 1, EndTime = 2, Source = "synthetic-test:v48-panel-closed" };
            AssertEqual(false, BuildTaskData(harness.Session).Any(row => row.Id == countTask.Id), "4.8 panel task hidden after its window");
            AssertEqual(20026007, Claim(countTask.Id).Code, "4.8 panel task claim rejected after its window");
        }
        finally
        {
            schedules[index] = window;
        }
    }

    // CharacterStoryActivity 1421003 -> TaskTimeLimit 762: outside its TimeId window nothing counts; an in-window clear progresses.
    // Windows are synthetic so this holds whether or not 50920 gets an authored date.
    private static void ValidateVersion48AffectionStoryTask()
    {
        TaskTimeLimitTable group = TableReaderV2.Parse<TaskTimeLimitTable>().Single(row => row.Id == 762);
        CurrentTaskTable task = TableReaderV2.Parse<CurrentTaskTable>().Single(row => row.Id == group.TaskId.Single());
        CurrentConditionTable condition = TableReaderV2.Parse<CurrentConditionTable>().Single(row => row.Id == task.Condition);
        AssertEqual(true, condition.Type == 15222, "Affection task authored in-event stage condition");
        int stageId = condition.Params[1];
        long timeId = group.TimeId ?? 0;

        const long uid = 48_830;
        Player player = CreateDrawCompatibilityPlayer(uid);
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(
            out RecordingMongoCollectionProxy<Player> saves, out _, out _);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player,
            CreateDrawCompatibilityInventory(uid, []), "v48-affection-task");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
        MethodInfo recordStageClear = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "RecordStageClear", BindingFlags.Static | BindingFlags.Public, [typeof(AscNet.GameServer.Session), typeof(int), typeof(int), typeof(int)]);
        int packetId = 48_830;
        FinishTaskResponse Claim()
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(FinishTaskRequest), harness.Session, id, new FinishTaskRequest { TaskId = task.Id });
            return ReadResponsePayload<FinishTaskResponse>(harness, id, nameof(FinishTaskResponse), "affection claim", maxPacketsToRead: 16);
        }

        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int slot = Array.FindIndex(schedules, entry => entry.Id == timeId);
        if (slot < 0) slot = Array.FindLastIndex(schedules, entry => entry.Id != timeId);
        ActivityScheduleEntry displaced = schedules[slot];
        try
        {
            schedules[slot] = new ActivityScheduleEntry(timeId, 1, 2, "synthetic-test:v48-affection-closed");
            harness.Session.stage.AddStage(new StageDatum { StageId = (uint)stageId, Passed = true });
            recordStageClear.Invoke(null, [harness.Session, stageId, 1, 0]);
            AssertEqual(false, BuildTaskData(harness.Session).Any(row => row.Id == task.Id), "Closed affection task hidden");
            AssertEqual(0, player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id), "Out-of-event clear does not count");
            AssertEqual(20026007, Claim().Code, "Closed affection task claim rejected");

            schedules[slot] = new ActivityScheduleEntry(timeId, 0, 0, "synthetic-test:v48-affection-open");
            AssertEqual(1, BuildTaskData(harness.Session).Single(row => row.Id == task.Id).State, "Open affection task active before in-event clear");
            recordStageClear.Invoke(null, [harness.Session, stageId, 1, 0]);
            AssertEqual(3, BuildTaskData(harness.Session).Single(row => row.Id == task.Id).State, "In-event story clear achieves affection task");
            AssertEqual(0, Claim().Code, "Affection task claim accepted");
            AssertEqual(true, player.MissionProgress.ClaimedTaskIds.Contains(task.Id) && saves.ReplaceOneCalls > 0, "Affection claim persisted");
            AssertEqual(20026006, Claim().Code, "Affection task cannot be claimed twice");
        }
        finally
        {
            schedules[slot] = displaced;
        }
    }
}
