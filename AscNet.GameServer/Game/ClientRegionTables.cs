using System.Collections.Concurrent;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.bigworld.common.task;
using AscNet.Table.V2.share.task;
using MessagePack;

namespace AscNet.GameServer.Game;

/// <summary>
/// Region-safe task pushes. The task builders (30+ of them) read the shared EN-derived tables; a TW/KR/JP/CN client resolves every
/// pushed task id against its own Task.tab + BigWorldTask.tab and logs a stack trace per unknown id (hundreds per login, which
/// freezes the client past the server's idle drop). One filter at the push boundary drops ids the client's region lacks;
/// regions without a Task overlay (EN) are untouched.
/// </summary>
internal static class ClientRegionTables
{
    private static readonly ConcurrentDictionary<string, HashSet<int>?> taskIds = new();

    /// <summary>Task ids the region's client has, or null when the region reads the shared tables.</summary>
    internal static HashSet<int>? TaskIds(string? region)
    {
        if (string.IsNullOrEmpty(region))
            return null;
        return taskIds.GetOrAdd(region, key =>
        {
            List<TaskTable> own = TableReaderV2.Parse<TaskTable>(key);
            if (ReferenceEquals(own, TableReaderV2.Parse<TaskTable>()))
                return null;
            HashSet<int> ids = own.Select(row => row.Id).ToHashSet();
            ids.UnionWith(TableReaderV2.Parse<BigWorldTaskTable>(key).Select(row => row.Id));
            return ids;
        });
    }

    /// <summary>Drops task ids the player's client region lacks from a task push, in place.</summary>
    internal static void FilterTasks(string? region, object push)
    {
        if (TaskIds(region) is not { } ids)
            return;
        switch (push)
        {
            case NotifyTask { Tasks: { } sync }:
                sync.Tasks.RemoveAll(task => !ids.Contains((int)task.Id));
                break;
            case NotifyTaskData { TaskData: { } login }:
                login.Tasks.RemoveAll(task => !ids.Contains((int)task.Id));
                login.FinishedTasks.RemoveAll(id => !ids.Contains(id));
                break;
        }
    }

    /// <summary>Serialized-push form of <see cref="FilterTasks"/> (theatre mutations queue NotifyTask as bytes).</summary>
    internal static byte[] FilterTasks(string? region, string name, byte[] push)
    {
        if (name != nameof(NotifyTask) || TaskIds(region) is null)
            return push;
        NotifyTask notify = MessagePackSerializer.Deserialize<NotifyTask>(push);
        int before = notify.Tasks?.Tasks.Count ?? 0;
        FilterTasks(region, notify);
        return notify.Tasks is not null && notify.Tasks.Tasks.Count != before ? MessagePackSerializer.Serialize(notify) : push;
    }
}
