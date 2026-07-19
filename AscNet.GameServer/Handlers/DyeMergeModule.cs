using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)]
public sealed class DyeMergeStagesRecordNotify
{
    public int ActivityId { get; set; }
    public List<int> StageRecord { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class DyeMergeTryEnterStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)]
public sealed class DyeMergeTryEnterStageResponse { public int Code { get; set; } }
[MessagePackObject(true)]
public sealed class DyeMergeTryCompleteStageRequest { public int StageId { get; set; } }
[MessagePackObject(true)]
public sealed class DyeMergeTryCompleteStageResponse { public int Code { get; set; } }

internal static class DyeMergeModule
{
    private const int FunctionNotOpen = 20425001;
    private const int ChapterNotOpen = 20425002;
    private const int ChapterNotFound = 20425003;
    private const int StageNotFound = 20425004;
    private const int StageNotInActivity = 20425005;
    private const int PreviousStageNotComplete = 20425006;
    private const int CompletionTaskConditionType = 139000;

    private static readonly Lazy<List<DyeMergeActivityTable>> Activities = new(() => TableReaderV2.Parse<DyeMergeActivityTable>());
    private static readonly Lazy<Dictionary<int, DyeMergeChapterTable>> Chapters = new(() => TableReaderV2.Parse<DyeMergeChapterTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, DyeMergeStageTable>> Stages = new(() => TableReaderV2.Parse<DyeMergeStageTable>().ToDictionary(row => row.Id));
    private static readonly Lazy<Dictionary<int, ConditionTable>> Conditions = new(() => TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id));

    // TimeLimit/ETCD is not available, so no configured activity or chapter is implicitly authorized.
    private static DyeMergeActivityTable? ActiveActivity() => null;

    internal static DyeMergeStagesRecordNotify BuildNotify(Player player)
    {
        DyeMergeActivityTable? activity = ActiveActivity();
        return activity is null
            ? new DyeMergeStagesRecordNotify()
            : BuildNotify(player, activity);
    }

    internal static DyeMergeStagesRecordNotify BuildNotify(Player player, DyeMergeActivityTable activity)
    {
        DyeMergeState state = Reconcile(player, activity.Id);
        return new DyeMergeStagesRecordNotify
        {
            ActivityId = activity.Id,
            StageRecord = state.CompletedStageIds.Distinct().Order().ToList()
        };
    }

    [RequestPacketHandler("DyeMergeTryEnterStageRequest")]
    public static void Enter(Session session, Packet.Request packet)
    {
        DyeMergeTryEnterStageRequest request = packet.Deserialize<DyeMergeTryEnterStageRequest>();
        DyeMergeActivityTable? activity = ActiveActivity();
        int code = activity is null ? FunctionNotOpen : TryEnter(session.player, activity, request.StageId);
        session.SendResponse(new DyeMergeTryEnterStageResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler("DyeMergeTryCompleteStageRequest")]
    public static void Complete(Session session, Packet.Request packet)
    {
        DyeMergeTryCompleteStageRequest request = packet.Deserialize<DyeMergeTryCompleteStageRequest>();
        DyeMergeActivityTable? activity = ActiveActivity();
        if (activity is null)
        {
            session.SendResponse(new DyeMergeTryCompleteStageResponse { Code = FunctionNotOpen }, packet.Id);
            return;
        }

        int code = TryComplete(session.player, activity, request.StageId, out bool inserted);
        if (code == 0 && inserted)
        {
            if (activity.TaskTimeLimitId is int taskTimeLimitId)
                TaskModule.RecordTableDrivenProgress(session, taskTimeLimitId, CompletionTaskConditionType, request.StageId);
        }
        session.SendResponse(new DyeMergeTryCompleteStageResponse { Code = code }, packet.Id);
    }

    internal static int TryEnter(Player player, DyeMergeActivityTable activity, int stageId) =>
        Validate(player, activity, stageId);

    internal static int TryComplete(Player player, DyeMergeActivityTable activity, int stageId, out bool inserted)
    {
        inserted = false;
        int code = Validate(player, activity, stageId);
        if (code != 0) return code;

        DyeMergeState state = Reconcile(player, activity.Id);
        if (state.CompletedStageIds.Contains(stageId)) return 0;
        state.CompletedStageIds.Add(stageId);
        state.CompletedStageIds = state.CompletedStageIds.Distinct().Order().ToList();
        player.Save();
        inserted = true;
        return 0;
    }

    private static int Validate(Player player, DyeMergeActivityTable activity, int stageId)
    {
        foreach (int chapterId in activity.ChapterIds)
            if (!Chapters.Value.ContainsKey(chapterId)) return ChapterNotFound;

        if (!Stages.Value.TryGetValue(stageId, out DyeMergeStageTable? stage)) return StageNotFound;
        List<DyeMergeChapterTable> matchingChapters = activity.ChapterIds
            .Select(id => Chapters.Value[id])
            .Where(row => row.StageIds.Contains(stageId))
            .ToList();
        if (matchingChapters.Count != 1) return StageNotInActivity;
        DyeMergeChapterTable chapter = matchingChapters[0];

        // A nonzero chapter condition is the only currently available authorization boundary.
        if (chapter.Condition is int conditionId && conditionId != 0 && !ChapterConditionSatisfied(player, conditionId)) return ChapterNotOpen;
        if (stage.PreStage is int preStage && preStage > 0 && !Reconcile(player, activity.Id).CompletedStageIds.Contains(preStage))
            return PreviousStageNotComplete;
        return 0;
    }

    private static bool ChapterConditionSatisfied(Player player, int conditionId)
    {
        if (!Conditions.Value.TryGetValue(conditionId, out ConditionTable? condition) || condition.Type != 23220)
            return false;
        return condition.Params.Count > 0 && condition.Params.All(player.DyeMerge.CompletedStageIds.Contains);
    }

    private static DyeMergeState Reconcile(Player player, int activityId)
    {
        player.DyeMerge ??= new();
        if (player.DyeMerge.ActivityId != activityId)
        {
            player.DyeMerge = new DyeMergeState { ActivityId = activityId };
        }
        player.DyeMerge.CompletedStageIds ??= new();
        return player.DyeMerge;
    }
}
