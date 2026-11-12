using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    private const int CollaborationTaskType = 28;
    private const int QualificationTaskType = 29;
    private const int RecruitmentChatCondition = 48001;

    private static readonly Lazy<Dictionary<int, (TaskTable Task, ConditionTable Condition)>> GuidanceTasks = new(() =>
    {
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        return TableReaderV2.Parse<TaskTable>()
            .Where(task => task.Type == QualificationTaskType || task.Type == CollaborationTaskType
                && conditions.GetValueOrDefault(task.Condition)?.Type == RecruitmentChatCondition)
            .ToDictionary(task => task.Id, task => (task, conditions[task.Condition]));
    });

    private static bool IsGuidanceTaskVisible(Session session, TaskTable task) =>
        ClientRegionTables.TaskIds(session.player.ClientRegion)?.Contains(task.Id) != false
        && IsTaskActive(task, ProgressClock());

    private static IEnumerable<MissionTaskProgress> BuildGuidanceTaskProgress(Session session) =>
        GuidanceTasks.Value.Values.Where(entry => IsGuidanceTaskVisible(session, entry.Task))
            .Select(entry => GuidanceTaskProgress(session, entry.Task, entry.Condition, session.player.MissionProgress.ClaimedTaskIds.Contains));

    private static MissionTaskProgress GuidanceTaskProgress(Session session, TaskTable task, ConditionTable condition, Func<int, bool> claimed)
    {
        List<int> parameters = condition.Params;
        int target = task.Result ?? 1;
        int value = condition.Type switch
        {
            RecruitmentChatCondition => session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id),
            15201 => parameters.Skip(1).Count(stageId => HasPassedStage(session, stageId)),
            12201 => CountQualifyingEquipment(session, parameters),
            13102 => CountCharactersAtQuality(session, parameters, parameters.ElementAtOrDefault(5)),
            25006 => BossModule.BestNormalScoreForGrade(session.player.SimulatedBattlefield, parameters[1]),
            _ => 0
        };
        return new(task.Id, condition.Id, Math.Min(value, target),
            claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive);
    }

    internal static void RecordRecruitmentChat(Session session)
    {
        EnsureMissionResets(session);
        if (GuidanceTasks.Value.Values.Any(entry => entry.Condition.Type == RecruitmentChatCondition
            && IsGuidanceTaskVisible(session, entry.Task)
            && GuidanceTaskProgress(session, entry.Task, entry.Condition, session.player.MissionProgress.ClaimedTaskIds.Contains).State == TaskStateActive))
            RecordTableDrivenProgress(session, [(RecruitmentChatCondition, null, 1)]);
    }

    private static void ResetGuidanceWeeklyTasks(Session session)
    {
        foreach (var (task, condition) in GuidanceTasks.Value.Values.Where(entry => entry.Task.Type == CollaborationTaskType))
        {
            session.player.MissionProgress.ClaimedTaskIds.Remove(task.Id);
            session.player.MissionProgress.ConditionCounters.Remove(condition.Id);
        }
    }
}
