using System.Runtime.CompilerServices;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Game;

namespace AscNet.GameServer.Handlers;

internal static partial class TaskModule
{
    // Dreamhouse Tycoon tasks. SkyTowerModule owns the catalog, values and perform lifecycle; this projects them onto the task wire.
    private static readonly ConditionalWeakTable<Session, Dictionary<int, (int Value, int State)>> SkyTowerSent = new();

    /// <summary>A Type-11 TaskTimeLimit task of the mode (perform tasks pay through SkyTowerFinishPerformRequest, never FinishTask).</summary>
    internal static bool IsSkyTowerActivityTask(int taskId) =>
        SkyTowerModule.Catalog.Value.TryGetValue(taskId, out SkyTowerModule.SkyTask? task) && task.PerformId == 0;

    private static string SkyTowerClaimKey(Session session, int taskId) => SkyTowerModule.Catalog.Value[taskId].Daily
        ? $"skytower-task:{taskId}:{session.player.SkyTower.DailyPeriod}" : $"skytower-task:{taskId}";

    private static List<MissionTaskProgress> BuildSkyTowerTaskProgress(Session session, Func<int, bool>? claimed = null)
    {
        claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
        if (SkyTowerModule.Prepare(session) is not { } s) return [];
        DateTimeOffset now = SkyTowerModule.Clock();
        List<MissionTaskProgress> progress = [];
        foreach (SkyTowerModule.SkyTask task in SkyTowerModule.Catalog.Value.Values)
        {
            if (!IsTaskActive(task.Row, now)) continue;
            int value = (int)Math.Min(SkyTowerModule.TaskValue(s, task), task.Target);
            int state;
            if (task.PerformId > 0)
            {
                // A perform's tasks exist once it started, and stay finished after its reward was taken.
                if (!s.Performs.TryGetValue(task.PerformId, out SkyTowerPerformState? perform) || perform.State == SkyTowerModule.PerformNotStart) continue;
                if (perform.State == SkyTowerModule.PerformFinish) (value, state) = (task.Target, TaskStateFinish);
                else state = value >= task.Target ? TaskStateAchieved : TaskStateActive;
            }
            else
            {
                if (!ActivityScheduleService.IsOpen(task.TimeId, now)) continue;
                state = claimed(task.Id) ? TaskStateFinish : value >= task.Target ? TaskStateAchieved : TaskStateActive;
            }
            progress.Add(new MissionTaskProgress(task.Id, task.Condition.Id, value, state));
        }
        return progress;
    }

    /// <summary>Login rows; also seeds the per-session memo so later syncs only send what changed.</summary>
    private static List<MissionTaskProgress> BuildSkyTowerLoginTasks(Session session)
    {
        List<MissionTaskProgress> progress = BuildSkyTowerTaskProgress(session);
        Dictionary<int, (int Value, int State)> sent = SkyTowerSent.GetOrCreateValue(session);
        sent.Clear();
        foreach (MissionTaskProgress task in progress) sent[task.TaskId] = (task.Value, task.State);
        return progress;
    }

    /// <summary>Pushes the Dreamhouse Tycoon tasks whose value or state changed since the last push of this session.</summary>
    internal static void SendSkyTowerTaskSync(Session session)
    {
        List<MissionTaskProgress> progress = BuildSkyTowerTaskProgress(session);
        Dictionary<int, (int Value, int State)> sent = SkyTowerSent.GetOrCreateValue(session);
        List<MissionTaskProgress> changed = progress.Where(task => !sent.TryGetValue(task.TaskId, out (int Value, int State) old)
            || old != (task.Value, task.State)).ToList();
        foreach (MissionTaskProgress task in changed) sent[task.TaskId] = (task.Value, task.State);
        if (changed.Count > 0)
            session.SendPush(new NotifyTask { Tasks = new() { Tasks = changed.Select(ToSyncTask).ToList() } });
    }
}
