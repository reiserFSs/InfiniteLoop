using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal partial class TaskModule
{
    private static readonly Lazy<Dictionary<int, ConditionTable>> DormTaskConditions = new(() =>
        TableReaderV2.Parse<ConditionTable>().Where(condition => condition.Type is 17203 or >= 29000 and < 29100)
            .ToDictionary(condition => condition.Id));
    private static readonly Lazy<TaskTable[]> DormTaskRows = new(() => TableReaderV2.Parse<TaskTable>()
        .Where(task => IsDormTask(task) && DormTaskConditions.Value.ContainsKey(task.Condition)).ToArray());
    private static readonly Lazy<HashSet<int>> DormSnapshotTaskIds = new(() => DormTaskRows.Value
        .Where(task => DormTaskConditions.Value[task.Condition].Type is 17203 or 29001 or 29002 or 29003 or 29005 or 29006 or 29009 or 29010 or 29011 or 29013 or 29020)
        .Select(task => task.Id).ToHashSet());

    private static int DormConditionValue(Session session, DormModule.TaskCounts counts,
        ConditionTable condition, Func<int, bool> claimed) =>
        condition.Type switch
        {
            17203 => condition.Params.Skip(1).Count(claimed),
            29001 => counts.Rooms,
            29002 => counts.Residents,
            29003 => counts.RoomsWithAttribute(condition.Params[1], condition.Params[2]),
            29009 or 29010 => counts.FurnitureOfQuality(condition.Params[3], condition.Type == 29010),
            29011 => counts.SetOwned(condition.Params[1]),
            29013 => counts.HighMoodDays(condition.Params[1], condition.Params[2], ProgressClock().ToUnixTimeSeconds()),
            29020 => counts.SetPlaced(condition.Params[1]),
            _ => session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id)
        };

    private static List<MissionTaskProgress> BuildDormTaskProgress(Session session, bool includeUnstarted = false)
    {
        DateTimeOffset now = ProgressClock();
        Func<int, bool> claimed = session.player.MissionProgress.ClaimedTaskIds.Contains;
        DormModule.TaskCounts counts = new(session.player.Dorm);
        var regionTasks = ClientRegionTables.TaskIds(session.player.ClientRegion);
        List<MissionTaskProgress> progress = [];
        foreach (TaskTable task in DormTaskRows.Value)
        {
            if (regionTasks?.Contains(task.Id) == false || !IsTaskActive(task, now))
                continue;
            bool finished = claimed(task.Id);
            if (!finished && task.ShowAfterTaskId is > 0 && !claimed(task.ShowAfterTaskId.Value))
                continue;
            int target = task.Result ?? 1;
            int value = Math.Min(target, DormConditionValue(session, counts, DormTaskConditions.Value[task.Condition], claimed));
            int state = finished ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive;
            if (includeUnstarted || state != TaskStateActive || value > 0)
                progress.Add(new(task.Id, task.Condition, value, state));
        }
        return progress;
    }
}
