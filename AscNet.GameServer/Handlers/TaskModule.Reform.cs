using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.reform;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private static readonly Lazy<IReadOnlyList<TaskTimeLimitTable>> ReformTaskGroups = new(() =>
    {
        HashSet<int> ids = TableReaderV2.Parse<ReformCfgTable>().Where(row => row.TaskGroupId > 0).Select(row => row.TaskGroupId).ToHashSet();
        return TableReaderV2.Parse<TaskTimeLimitTable>().Where(group => ids.Contains(group.Id)).ToArray();
    });
    private static readonly Lazy<IReadOnlyDictionary<int, ConditionTable>> ReformConditions = new(() =>
    {
        HashSet<int> ids = ReformTaskGroups.Value.SelectMany(group => group.TaskId).ToHashSet();
        HashSet<int> conditionIds = TaskRowsById.Value.Values.Where(task => ids.Contains(task.Id)).Select(task => task.Condition).ToHashSet();
        return TableReaderV2.Parse<ConditionTable>().Where(condition => conditionIds.Contains(condition.Id)).ToDictionary(condition => condition.Id);
    });

    internal static bool IsReformTask(TaskTable task) => task.Type == 11 &&
        ReformTaskGroups.Value.Any(group => group.TaskId.Contains(task.Id));

    // 61005 [stageId]: that stage cleared. 61004 [N]: wish mass accumulated in hard mode
    // (ReformModule.HardWishTotal, AscNet policy: the sum of each hard stage's best final wish mass).
    private static List<MissionTaskProgress> BuildReformTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<MissionTaskProgress> progress = [];
        if (ReformModule.ActiveActivity(now) is not { } activity)
            return progress;
        foreach (TaskTimeLimitTable group in ReformTaskGroups.Value)
        {
            if (group.Id != activity.TaskGroupId || group.TimeId is not int timeId || !ActivityScheduleService.IsOpen(timeId, now))
                continue;
            foreach (int id in group.TaskId)
            {
                if (!TaskRowsById.Value.TryGetValue(id, out TaskTable? task) || !IsReformTask(task) || !IsTaskActive(task, now)
                    || !ReformConditions.Value.TryGetValue(task.Condition, out ConditionTable? condition))
                    continue;
                int target = task.Result ?? 1;
                int value = condition.Type switch
                {
                    61005 when condition.Params.Count >= 1 => ReformModule.IsStagePassed(session.player, condition.Params[0]) ? 1 : 0,
                    61004 => ReformModule.HardWishTotal(session.player),
                    _ => 0
                };
                progress.Add(new MissionTaskProgress(task.Id, condition.Id, Math.Min(value, target),
                    claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive));
            }
        }
        return progress;
    }

    internal static void SendReformTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildReformTaskProgress(session);
        if (progress.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = progress.Select(ToSyncTask).ToList() } });
    }
}
