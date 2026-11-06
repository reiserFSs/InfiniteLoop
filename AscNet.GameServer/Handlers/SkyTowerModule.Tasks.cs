using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.skytower;
using AscNet.Table.V2.share.task;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Dreamhouse Tycoon tasks: the Type-118 perform tasks (rewards are paid by SkyTowerFinishPerformRequest) and the Type-11
/// TaskTimeLimit tasks of SkyTowerActivity (claimed through FinishTaskRequest). Condition types 143001-143011 are the
/// client's XMVCA.XSkyTower.PerformTaskType; the client simulates progress locally and the server is authoritative.
/// AscNet policy: perform tasks that count production (143002/143003/143011) and income speed (143008) count only while
/// their perform is running, like the client; the rest are derived from the persisted state.
/// </summary>
internal static partial class SkyTowerModule
{
    internal sealed record SkyTask(int Id, TaskTable Row, ConditionTable Condition, int Target, int PerformId, bool Daily, int TimeId)
    {
        public int Param(int index) => index < Condition.Params.Count ? Condition.Params[index] : 0;
    }

    /// <summary>Every task the mode can show, by task id (perform tasks have PerformId &gt; 0, activity tasks 0).</summary>
    internal static readonly Lazy<Dictionary<int, SkyTask>> Catalog = new(() =>
    {
        Dictionary<int, TaskTable> rows = TableReaderV2.Parse<TaskTable>().ToDictionary(row => row.Id);
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .Where(row => row.Type is >= 143001 and <= 143011).ToDictionary(row => row.Id);
        Dictionary<int, SkyTask> catalog = [];
        void Add(int taskId, int performId, bool daily, int timeId)
        {
            if (rows.TryGetValue(taskId, out TaskTable? row) && conditions.TryGetValue(row.Condition, out ConditionTable? condition))
                catalog.TryAdd(taskId, new(taskId, row, condition, row.Result ?? 1, performId, daily, timeId));
        }
        foreach (SkyTowerPerformTable perform in PerformRows.Value.Values)
            foreach (int taskId in perform.TaskIds) Add(taskId, perform.Id, false, 0);
        HashSet<int> limits = Activity.Value?.TimeLimitTaskIds.ToHashSet() ?? [];
        foreach (TaskTimeLimitTable limit in TableReaderV2.Parse<TaskTimeLimitTable>().Where(row => limits.Contains(row.Id)))
        {
            foreach (int taskId in limit.TaskId) Add(taskId, 0, false, limit.TimeId ?? 0);
            foreach (int taskId in limit.DayTaskId) Add(taskId, 0, true, limit.TimeId ?? 0);
        }
        return catalog;
    });

    /// <summary>Current value of a task (before capping at the target).</summary>
    internal static long TaskValue(SkyTowerState s, SkyTask task) => task.Condition.Type switch
    {
        143001 => task.Daily ? s.DailyGoldGained : s.TotalGoldGained,
        143002 or 143003 or 143008 or 143011 => s.TaskProgress.GetValueOrDefault(task.Id),
        143004 => s.Level,
        // [target, characterId]: character 0 means the best staff level.
        143005 => task.Param(1) == 0 ? s.Characters.Values.DefaultIfEmpty(0).Max() : s.Characters.GetValueOrDefault(task.Param(1)),
        // [target, performType]: finished performs of that type (0 = any).
        143006 => s.Performs.Count(entry => entry.Value.State == PerformFinish
            && (task.Param(1) == 0 || PerformRows.Value.GetValueOrDefault(entry.Key)?.Type == task.Param(1))),
        143007 => s.Performs.TryGetValue(task.Param(0), out SkyTowerPerformState? perform) && perform.State == PerformFinish ? 1 : 0,
        // [count, level]: staff at or above the level.
        143009 => s.Characters.Values.Count(level => level >= task.Param(1)),
        143010 => s.SpeedUpDailyUsedCount,
        _ => 0
    };

    private static bool PerformRunning(SkyTowerState s, int performId) =>
        s.Performs.TryGetValue(performId, out SkyTowerPerformState? perform) && perform.State == PerformOnGoing;

    /// <summary>Adds a simulation's output to the running perform tasks that count it.</summary>
    private static void ApplyProduction(SkyTowerState s, SimResult sim)
    {
        if (sim.Produced.Count == 0 && sim.Sold.Count == 0) return;
        foreach (SkyTask task in Catalog.Value.Values.Where(task => task.PerformId > 0 && PerformRunning(s, task.PerformId)))
        {
            long add = task.Condition.Type switch
            {
                143002 => sim.Produced.GetValueOrDefault((ProductFood, task.Param(0))),
                143011 => sim.Produced.GetValueOrDefault((ProductIngredient, task.Param(0))),
                143003 => sim.Sold.GetValueOrDefault(task.Param(0)),
                _ => 0
            };
            if (add > 0) s.TaskProgress[task.Id] = Math.Min(task.Target, s.TaskProgress.GetValueOrDefault(task.Id) + add);
        }
    }

    /// <summary>143008: best gold-per-hour a floor group has reached since the task began (params: group, speed, sale-position limit).</summary>
    private static void RefreshSpeedTasks(SkyTowerState s)
    {
        List<SkyTask> tasks = Catalog.Value.Values.Where(task => task.Condition.Type == 143008 && PerformRunning(s, task.PerformId)).ToList();
        if (tasks.Count == 0) return;
        Plan plan = new(s);
        foreach (SkyTask task in tasks)
        {
            int group = task.Param(0), limit = task.Param(2);
            if (limit > 0 && s.WorkPos.Count(entry => entry.Value.WorkType == WorkSale && GroupOf(ParseKey(entry.Key).Floor) == group) > limit) continue;
            long speed = (long)Math.Floor(GoldSpeed(s, group, plan));
            s.TaskProgress[task.Id] = Math.Min(task.Target, Math.Max(s.TaskProgress.GetValueOrDefault(task.Id), speed));
        }
    }
}
