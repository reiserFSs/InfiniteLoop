using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    // Event-page tasks: Type-11 TaskTimeLimit tasks that no dedicated mode owns, counted while their calendar window is open.
    // 15201 [N, stageIds...] stage clears and 15223 [N, stageId] Simulated Battlefield serum accumulate in ConditionCounters
    // (RecordStageClear / AddEventConditionProgress); 123002 [N, minStar, activityId] is derived from the persisted PacMan2 records.
    private sealed record EventTask(TaskTable Task, ConditionTable Condition, int[] TimeIds);

    private static readonly Lazy<IReadOnlyDictionary<int, EventTask>> EventTasks = new(() =>
    {
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .Where(row => row.Type is 15201 or 15223 or 123002).ToDictionary(row => row.Id);
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(limit => limit.TimeId is > 0)
            .SelectMany(limit => limit.TaskId.Select(id => (id, TimeId: limit.TimeId!.Value)))
            .GroupBy(entry => entry.id)
            .Select(group => (Task: TaskRowsById.Value.GetValueOrDefault(group.Key), TimeIds: group.Select(entry => entry.TimeId).Distinct().ToArray()))
            .Where(entry => entry.Task is { Type: 11 } task && !CurrentTaskIds.Value.Contains(task.Id)
                && conditions.ContainsKey(task.Condition) && !HasDedicatedEventOwner(task))
            .ToDictionary(entry => entry.Task!.Id, entry => new EventTask(entry.Task!, conditions[entry.Task!.Condition], entry.TimeIds));
    });

    // Modes that project their own tasks (and value rules) for the same condition types.
    private static bool HasDedicatedEventOwner(TaskTable task) =>
        IsCerberusGameTask(task) || IsTransfiniteTowerTask(task) || IsPunishaarTask(task) || IsFangKuaiTask(task)
        || IsReformTask(task) || IsEnvelopeTask(task) || IsSkyTowerActivityTask(task.Id);

    private static bool IsEventTaskOpen(EventTask task, DateTimeOffset now) =>
        IsTaskActive(task.Task, now) && task.TimeIds.Any(timeId => ActivityScheduleService.IsOpen(timeId, now));

    private static HashSet<int> EventConditionIds(DateTimeOffset now, Func<ConditionTable, bool> matches) =>
        EventTasks.Value.Values.Where(task => IsEventTaskOpen(task, now) && matches(task.Condition))
            .Select(task => task.Condition.Id).ToHashSet();

    private static int EventTaskValue(Session session, ConditionTable condition) => condition switch
    {
        { Type: 123002, Params.Count: >= 3 } => PacMan2Module.PassedStageCount(session.player, condition.Params[2], condition.Params[1]),
        _ => session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id)
    };

    private static List<MissionTaskProgress> BuildEventTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        DateTimeOffset now = ProgressClock();
        List<MissionTaskProgress> progress = [];
        foreach (EventTask entry in EventTasks.Value.Values)
        {
            if (!IsEventTaskOpen(entry, now)) continue;
            int target = Math.Max(1, entry.Task.Result ?? 1);
            int value = Math.Min(target, EventTaskValue(session, entry.Condition));
            bool prerequisite = entry.Task.ShowAfterTaskId is not > 0 || claimed(entry.Task.ShowAfterTaskId.Value);
            progress.Add(new MissionTaskProgress(entry.Task.Id, entry.Condition.Id, value,
                claimed(entry.Task.Id) ? TaskStateFinish : prerequisite && value >= target ? TaskStateAchieved : TaskStateActive));
        }
        return progress;
    }

    // Committed gameplay event: credit the open event tasks' conditions of one type, persist, then push the changed tasks.
    internal static void AddEventConditionProgress(Session session, int conditionType, int amount, Func<ConditionTable, bool> matches)
    {
        if (amount <= 0) return;
        EnsureMissionResets(session);
        HashSet<int> ids = EventConditionIds(ProgressClock(), condition => condition.Type == conditionType && matches(condition));
        if (ids.Count == 0) return;
        Dictionary<int, int> counters = session.player.MissionProgress.ConditionCounters;
        Dictionary<int, int> previous = ids.ToDictionary(id => id, id => counters.GetValueOrDefault(id));
        try
        {
            foreach (int id in ids) counters[id] = checked(previous[id] + amount);
            session.player.SaveChecked();
        }
        catch
        {
            foreach ((int id, int value) in previous) counters[id] = value;
            throw;
        }
        session.SendPush(new NotifyTask
        {
            Tasks = new() { Tasks = BuildEventTaskProgress(session).Where(task => ids.Contains(task.ConditionId)).Select(ToSyncTask).ToList() }
        });
    }
}
