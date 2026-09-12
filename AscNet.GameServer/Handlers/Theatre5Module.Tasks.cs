using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre5;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre5Module
{
    private static readonly Lazy<Dictionary<int, TaskTimeLimitTable>> MetaTaskLimits = new(() =>
    {
        var ids = Rows<AscNet.Table.V2.client.theatre5.Theatre5TaskShopTable>()
            .Where(row => row.Type == 2).Select(row => row.TaskTimeLimitId).ToHashSet();
        return Rows<TaskTimeLimitTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });
    private static readonly Lazy<Dictionary<int, TaskTable>> MetaTasks = new(() =>
    {
        var ids = MetaTaskLimits.Value.Values.SelectMany(row => row.TaskId).ToHashSet();
        return Rows<TaskTable>().Where(row => row.Type == 11 && ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });
    private static readonly Lazy<Dictionary<int, ConditionTable>> MetaTaskConditions = new(() =>
    {
        var ids = MetaTasks.Value.Values.Select(row => row.Condition).ToHashSet();
        return Rows<ConditionTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    internal static bool IsMetaTask(int id) => MetaTasks.Value.ContainsKey(id);

    private static bool IsMetaTaskOpen(TaskTable task, DateTimeOffset now) =>
        MetaTaskLimits.Value.Values.Any(limit => limit.TaskId.Contains(task.Id)
            && limit.TimeId is > 0 && ActivityScheduleService.IsOpen(limit.TimeId.Value, now));

    // Task conditions are not the similarly numbered client UI conditions. The
    // third 131008 operand selects wins (1) versus completed battles (0).
    internal static int EvaluateMetaCondition(PlayerTheatre5State state, ConditionTable condition)
    {
        var p = condition.Params;
        if (condition.Type == 131008 && p.Count == 3 && p[0] >= 0 && p[1] > 0 && p[2] is 0 or 1)
            return Math.Min(p[1], state.TaskProgress.GetValueOrDefault(condition.Id));
        if (condition.Type == 131010 && p.Count == 2 && p[0] >= 0)
        {
            var rank = Rows<Theatre5RankTable>().FirstOrDefault(row => row.Id == p[1]);
            if (rank is null) throw new InvalidDataException($"Missing Godfall rank {p[1]}.");
            bool reached = state.Data.Characters.Any(pair => (p[0] == 0 || pair.Key == p[0]) && pair.Value.Rating >= rank.Rating);
            return Math.Max(state.TaskProgress.GetValueOrDefault(condition.Id), reached ? 1 : 0);
        }
        throw new InvalidDataException($"Unsupported Godfall task condition {condition.Id}/{condition.Type}.");
    }

    private static int MetaTaskValue(PlayerTheatre5State state, TaskTable task) =>
        Math.Min(Math.Max(0, EvaluateMetaCondition(state, MetaTaskConditions.Value[task.Condition])), Math.Max(1, task.Result ?? 1));

    private static int MetaTaskState(Mutation mutation, TaskTable task, int value) =>
        mutation.IsTaskClaimed(task.Id) ? 4 : value >= Math.Max(1, task.Result ?? 1) ? 3 : 1;

    internal static List<LoginTask> BuildTasks(Session session) =>
        BuildTaskUpdates(new Mutation(session, newOperation: false)).Select(task => new LoginTask
        {
            Id = task.Id, State = task.State, RecordTime = task.RecordTime,
            Schedule = task.Schedule.Select(value => new LoginTask.NotifyTaskDataTaskDataTaskSchedule { Id = value.Id, Value = value.Value }).ToList()
        }).ToList();

    internal static List<SyncTask> BuildTaskUpdates(Mutation mutation)
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return MetaTasks.Value.Values.Select(task =>
        {
            int value = MetaTaskValue(mutation.State, task);
            return new SyncTask
            {
                Id = (uint)task.Id, State = MetaTaskState(mutation, task, value), RecordTime = now,
                Schedule = [new() { Id = (uint)task.Condition, Value = value }]
            };
        }).ToList();
    }

    internal static bool CanClaimMetaTask(Mutation mutation, int id) =>
        MetaTasks.Value.TryGetValue(id, out var task) && !mutation.IsTaskClaimed(id)
        && IsMetaTaskOpen(task, DateTimeOffset.UtcNow)
        && (task.ShowAfterTaskId is not > 0 || mutation.IsTaskClaimed(task.ShowAfterTaskId.Value))
        && MetaTaskValue(mutation.State, task) >= Math.Max(1, task.Result ?? 1);

    internal static List<RewardGoods> ClaimMetaTask(Mutation mutation, int id)
    {
        Require(MetaTasks.Value.TryGetValue(id, out var task), 20026005);
        Require(!mutation.IsTaskClaimed(id), 20026006);
        Require(CanClaimMetaTask(mutation, id), 20026007);
        List<RewardGoods> result = [];
        if (task!.RewardId is > 0)
        {
            var goods = RewardHandler.GetRewardGoods(task.RewardId.Value);
            Require(goods.Count > 0, 20026003);
            foreach (var good in goods)
            {
                var type = RewardHandler.GetRewardType(good);
                Require(type is not null, 20026003);
                result.Add(new RewardGoods { Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type!.Value });
            }
            mutation.Grant(new RewardGrant($"theatre5-task:{mutation.Session.player.PlayerData.Id}:{id}", goods));
        }
        mutation.MarkTaskClaimed(id);
        mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
        return result;
    }

    // Producers call this after accepting a once-only transition. No daily or
    // seasonal reset is authored for these Type11 tasks; claims remain durable.
    internal static void RecordMetaProgress(Mutation mutation, string trigger, int characterId = 0, int value = 1)
    {
        Require(value >= 0 && characterId >= 0, 1);
        if (trigger is not ("BattleWin" or "BattleFinish" or "PvpRankChanged" or "StoryContentFinished"))
            throw new InvalidDataException($"Unsupported Godfall meta trigger {trigger}.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = false;
        foreach (var condition in MetaTasks.Value.Values.Where(task => IsMetaTaskOpen(task, now))
                     .Select(task => MetaTaskConditions.Value[task.Condition]).DistinctBy(row => row.Id))
        {
            int previous = mutation.State.TaskProgress.GetValueOrDefault(condition.Id);
            int next = previous;
            var p = condition.Params;
            if (condition.Type == 131008 && p.Count == 3 && (p[0] == 0 || p[0] == characterId)
                && (p[2] == 1 && trigger == "BattleWin" || p[2] == 0 && trigger == "BattleFinish"))
                next = Math.Min(p[1], checked(previous + value));
            else if (condition.Type == 131010 && trigger == "PvpRankChanged")
                next = EvaluateMetaCondition(mutation.State, condition);
            if (next == previous) continue;
            mutation.State.TaskProgress[condition.Id] = next;
            changed = true;
        }
        // Story completion has no condition among this authored task set. Its
        // archive/story gates read the producer's persisted story state directly.
        if (changed) mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
    }
}
