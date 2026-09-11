using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre3;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;

namespace AscNet.GameServer.Handlers;

internal static partial class Theatre3Module
{
    private static readonly Lazy<HashSet<int>> AchievementTaskIds = new(() => TableReaderV2.Parse<Theatre3AchievementTable>()
        .SelectMany(row => row.TaskIds).Where(id => id > 0).ToHashSet());
    private static readonly Lazy<Dictionary<int, TaskTable>> MetaTasks = new(() =>
    {
        HashSet<int> ids = TableReaderV2.Parse<AscNet.Table.V2.client.theatre3.Theatre3TaskTable>()
            .SelectMany(row => row.TaskId).Where(id => id > 0).ToHashSet();
        ids.UnionWith(AchievementTaskIds.Value);
        return TableReaderV2.Parse<TaskTable>().Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });
    private static readonly Lazy<Dictionary<int, AscNet.Table.V2.share.task.ConditionTable>> MetaTaskConditions = new(() =>
    {
        HashSet<int> ids = MetaTasks.Value.Values.Select(row => row.Condition).ToHashSet();
        return TableReaderV2.Parse<AscNet.Table.V2.share.task.ConditionTable>()
            .Where(row => ids.Contains(row.Id)).ToDictionary(row => row.Id);
    });

    internal static bool IsMetaTask(int id) => MetaTasks.Value.ContainsKey(id);

    private static int MetaTaskValue(Mutation mutation, TaskTable task)
    {
        var condition = MetaTaskConditions.Value[task.Condition];
        int value = condition.Type switch
        {
            104010 => mutation.Data.Characters.Count(character => character.Level >= condition.Params[0]),
            104018 => mutation.Data.UnlockStrengthTree.Distinct().Count(),
            104002 or 104004 or 104005 or 104007 or 104008 or 104013 or 104014 or 104015 or 104017
                or 104019 or 104020 or 104021 or 104022 => mutation.State.TaskProgress.GetValueOrDefault(condition.Id),
            _ => throw new InvalidDataException($"Unsupported Theatre3 task condition {condition.Id}/{condition.Type}.")
        };
        return Math.Min(Math.Max(0, value), Math.Max(1, task.Result ?? 1));
    }

    private static int MetaTaskState(Mutation mutation, TaskTable task, int value) =>
        mutation.IsTaskClaimed(task.Id) ? 4 : value >= Math.Max(1, task.Result ?? 1) ? 3 : 1;

    internal static List<LoginTask> BuildTasks(Session session)
    {
        Mutation mutation = new(session);
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

    internal static int AchievementClaimCount(Mutation mutation) => AchievementTaskIds.Value.Count(mutation.IsTaskClaimed);

    internal static bool CanClaim(Mutation mutation, int id) => MetaTasks.Value.TryGetValue(id, out TaskTable? task)
        && !mutation.IsTaskClaimed(id) && MetaTaskValue(mutation, task) >= Math.Max(1, task.Result ?? 1);

    internal static List<RewardGoods> Claim(Mutation mutation, int id)
    {
        Require(MetaTasks.Value.TryGetValue(id, out TaskTable? task), 20026005);
        Require(!mutation.IsTaskClaimed(id), 20026006);
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
            mutation.Grant(new RewardGrant($"theatre3-task:{mutation.Session.player.PlayerData.Id}:{id}", goods));
        }
        mutation.MarkTaskClaimed(id);
        mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
        return result;
    }

    // Called only after the producer has accepted a durable, once-only gameplay transition.
    // amount is a delta except for snapshot types 104013/14/15; argument selects the table parameter.
    internal static void RecordProgress(Mutation mutation, int conditionType, int amount = 1, int argument = 0)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (conditionType is not (104002 or 104004 or 104005 or 104007 or 104008 or 104010 or 104013
            or 104014 or 104015 or 104017 or 104018 or 104019 or 104020 or 104021 or 104022))
            throw new InvalidDataException($"Unsupported Theatre3 task event {conditionType}.");
        bool changed = conditionType is 104010 or 104018;
        foreach (var condition in MetaTaskConditions.Value.Values.Where(row => row.Type == conditionType))
        {
            int previous = mutation.State.TaskProgress.GetValueOrDefault(condition.Id);
            int next;
            switch (conditionType)
            {
                case 104002: // Artifacts acquired across adventures.
                case 104007: // Complete equipment sets acquired across adventures.
                case 104008: // Successful endings.
                case 104017: // Successful equipment entropy changes.
                case 104019: // Battle nodes, not individual stages in a linked fight.
                case 104021: // An ending with the iterative-projection character in the team.
                case 104022: // Iterative projection triggered below guaranteed probability.
                    next = (int)Math.Min(int.MaxValue, (long)previous + amount);
                    break;
                case 104004: // Params begins with the number of accepted chapter IDs.
                    if (!condition.Params.Skip(1).Take(condition.Params[0]).Contains(argument)) continue;
                    next = Math.Max(previous, amount > 0 ? 1 : 0);
                    break;
                case 104005: // Ending ID, required count, optional difficulty restriction.
                    if (condition.Params[0] != argument
                        || (condition.Params.Count > 2 && condition.Params[2] > 0
                            && condition.Params[2] != mutation.Data.DifficultyId)) continue;
                    next = (int)Math.Min(int.MaxValue, (long)previous + amount);
                    break;
                case 104020: // Completed event step ID, required count.
                    if (condition.Params[0] != argument) continue;
                    next = (int)Math.Min(int.MaxValue, (long)previous + amount);
                    break;
                case 104013: // Most complete sets held at the end of any adventure.
                    next = Math.Max(previous, amount);
                    break;
                case 104014: // Branch-specific entropy threshold evaluated at settlement.
                    if (condition.Params[0] != argument) continue;
                    next = Math.Max(previous, amount >= condition.Params[1] ? 1 : 0);
                    break;
                case 104015: // First activation is immediate; level-four achievement needs an ending.
                    if (condition.Params[0] > 1 && argument != 1) continue;
                    next = Math.Max(previous, amount >= condition.Params[0] ? 1 : 0);
                    break;
                case 104010: // Character level and strength-tree state are authoritative snapshots.
                case 104018:
                    continue;
                default:
                    throw new InvalidDataException($"Unsupported Theatre3 task event {conditionType}.");
            }
            if (next == previous) continue;
            mutation.State.TaskProgress[condition.Id] = next;
            changed = true;
        }
        if (changed) mutation.Push(new NotifyTask { Tasks = new() { Tasks = BuildTaskUpdates(mutation) } });
    }
}
