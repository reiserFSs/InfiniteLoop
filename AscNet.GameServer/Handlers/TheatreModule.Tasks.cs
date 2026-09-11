using AscNet.Common.MsgPack;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.GameServer.Handlers;

internal static partial class TheatreModule
{
    private static readonly Lazy<Dictionary<int, TaskTable>> MetaTasks = new(() =>
    {
        HashSet<int> ids = Rows<AscNet.Table.V2.client.theatre.TheatreTaskTable>()
            .SelectMany(row => row.TaskId).Where(id => id > 0).ToHashSet();
        var tasks = Rows<TaskTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
        if (tasks.Count != ids.Count) throw new InvalidDataException("Missing original Theatre task rows.");
        return tasks;
    });
    private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.task.ConditionTable>> MetaTaskConditions = new(() =>
    {
        HashSet<int> ids = MetaTasks.Value.Values.Select(row => row.Condition).ToHashSet();
        var conditions = Rows<AscNet.Table.V2.share.task.ConditionTable>()
            .Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
        if (conditions.Count != ids.Count) throw new InvalidDataException("Missing original Theatre task conditions.");
        return conditions;
    });

    internal static bool IsMetaTask(int id) => MetaTasks.Value.ContainsKey(id);

    // The three Type59 tasks share the common daily clock, but keep claims in this
    // mutation's ledger so period reset and reward recovery cannot race a global reset.
    internal static void InitializeMetaTasks(Mutation mutation)
    {
        long period = TaskModule.CurrentDailyResetPeriod(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (mutation.State.TaskDailyPeriod == period) return;
        mutation.State.TaskDailyPeriod = period;
        mutation.State.DailyClaimedTaskIds.Clear();
        foreach (var task in MetaTasks.Value.Values.Where(task => task.Type == 59))
            mutation.State.TaskProgress.Remove(task.Condition);
    }

    private static bool IsMetaTaskClaimed(Mutation mutation, TaskTable task) => task.Type == 59
        ? mutation.State.DailyClaimedTaskIds.Contains(task.Id) : mutation.IsTaskClaimed(task.Id);

    private static int MetaTaskValue(Mutation mutation, TaskTable task)
    {
        var condition = MetaTaskConditions.Value[task.Condition];
        // LOCAL: the server-only aggregate-level conditions use the highest authored level
        // per identity, never row IDs, item balances, or a sum of historical upgrade rows.
        int value = condition.Type switch
        {
            73007 => Rows<TheatrePowerFavorTable>().Where(row => mutation.Data.UnlockPowerFavorIds.Contains(row.Id))
                .GroupBy(row => row.PowerId).Sum(group => group.Max(row => row.Lv ?? 0)),
            73005 => Rows<TheatreDecorationTable>().Where(row => mutation.Data.Decorations.Contains(row.Id))
                .GroupBy(row => row.DecorationId).Sum(group => group.Max(row => row.Lv ?? 0)),
            73001 or 73014 or 73013 or 73015 or 73017 => mutation.State.TaskProgress.GetValueOrDefault(condition.Id),
            _ => throw new InvalidDataException($"Unsupported original Theatre task condition {condition.Id}/{condition.Type}.")
        };
        return Math.Clamp(value, 0, Math.Max(1, task.Result ?? 1));
    }

    private static int MetaTaskState(Mutation mutation, TaskTable task, int value) =>
        IsMetaTaskClaimed(mutation, task) ? 4 : value >= Math.Max(1, task.Result ?? 1) ? 3 : 1;

    internal static List<LoginTask> BuildTasks(Session session)
    {
        Mutation mutation = new(session);
        InitializeMetaTasks(mutation);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return MetaTasks.Value.Values.Select(task =>
        {
            int value = MetaTaskValue(mutation, task);
            return new LoginTask
            {
                Id = (uint)task.Id, State = MetaTaskState(mutation, task, value), RecordTime = now,
                Schedule = [new() { Id = (uint)task.Condition, Value = value }]
            };
        }).ToList();
    }

    internal static List<SyncTask> BuildTaskUpdates(Mutation mutation)
    {
        InitializeMetaTasks(mutation);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return MetaTasks.Value.Values.Select(task =>
        {
            int value = MetaTaskValue(mutation, task);
            return new SyncTask
            {
                Id = (uint)task.Id, State = MetaTaskState(mutation, task, value), RecordTime = now,
                Schedule = [new() { Id = (uint)task.Condition, Value = value }]
            };
        }).ToList();
    }

    internal static bool CanClaim(Mutation mutation, int id)
    {
        InitializeMetaTasks(mutation);
        return MetaTasks.Value.TryGetValue(id, out TaskTable? task)
            && !IsMetaTaskClaimed(mutation, task) && MetaTaskValue(mutation, task) >= Math.Max(1, task.Result ?? 1);
    }

    internal static List<RewardGoods> Claim(Mutation mutation, int id)
    {
        InitializeMetaTasks(mutation);
        Require(MetaTasks.Value.TryGetValue(id, out TaskTable? task), 20026005);
        Require(!IsMetaTaskClaimed(mutation, task!), 20026006);
        Require(CanClaim(mutation, id), 20026007);
        List<RewardGoods> result = [];
        if (task!.RewardId is > 0)
        {
            var goods = RewardHandler.GetRewardGoods(task.RewardId.Value);
            Require(goods.Count > 0, 20026003);
            foreach (var good in goods)
            {
                var type = RewardHandler.GetRewardType(good);
                Require(type is not null, 20026003);
                result.Add(new RewardGoods
                {
                    Id = good.Id, TemplateId = good.TemplateId, Count = good.Count, RewardType = (int)type!.Value
                });
            }
            // LOCAL cause assignment: WheelchairManualGuideActivity owns causes 92004/92008;
            // common Theatre task rewards use 92004. Shared reward receipts drive guide totals.
            string key = $"theatre-task:{mutation.Session.player.PlayerData.Id}:{id}";
            if (task.Type == 59) key += $":{mutation.State.TaskDailyPeriod}";
            mutation.Grant(new RewardGrant(key, goods, EventCause: 92004));
        }
        if (task.Type == 59) mutation.State.DailyClaimedTaskIds.Add(id);
        else mutation.MarkTaskClaimed(id);
        mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
        return result;
    }

    // Call only after a durable gameplay transition is accepted. Counts are earned deltas;
    // 73005/73007 refresh authored aggregate levels and ignore amount/argument.
    internal static void RecordProgress(Mutation mutation, int type, int amount = 1, int argument = 0)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (type is not (73001 or 73005 or 73007 or 73013 or 73014 or 73015 or 73017))
            throw new InvalidDataException($"Unsupported original Theatre task event {type}.");
        InitializeMetaTasks(mutation);
        bool changed = type is 73005 or 73007;
        foreach (var condition in MetaTaskConditions.Value.Values.Where(row => row.Type == type))
        {
            if (type is 73005 or 73007) continue;
            if (type == 73001 && condition.Params[0] != argument) continue;
            if (type == 73014 && (condition.Params[0] != mutation.Data.DifficultyId || condition.Params[1] != argument)) continue;
            int previous = mutation.State.TaskProgress.GetValueOrDefault(condition.Id);
            int next = (int)Math.Min(int.MaxValue, (long)previous + amount);
            if (next == previous) continue;
            mutation.State.TaskProgress[condition.Id] = next;
            changed = true;
        }
        if (changed) mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
    }
}
