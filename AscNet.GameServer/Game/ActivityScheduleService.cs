using AscNet.Common.Util;
using AscNet.Table.V2.share.activity;

namespace AscNet.GameServer.Game;

public readonly record struct ActivityScheduleEntry(long Id, long StartTime, long EndTime, string Source)
{
    public bool IsOpen(DateTimeOffset now)
    {
        long unixTime = now.ToUnixTimeSeconds();
        return (StartTime == 0 || unixTime >= StartTime)
            && (EndTime == 0 || unixTime < EndTime);
    }
}

/// <summary>Authoritative event availability derived from version tables and public notices.</summary>
public static class ActivityScheduleService
{
    private static readonly Lazy<IReadOnlyList<ActivityScheduleEntry>> Entries = new(() =>
        TableReaderV2.Parse<ActivityScheduleTable>()
            .Select(row => new ActivityScheduleEntry(row.Id, row.StartTime, row.EndTime, row.Source))
            .OrderBy(row => row.Id)
            .ToArray());

    public static IReadOnlyList<ActivityScheduleEntry> All => Entries.Value;

    public static bool IsOpen(long timeId, DateTimeOffset now) =>
        TryGet(timeId, out ActivityScheduleEntry entry) && entry.IsOpen(now);

    public static bool TryGet(long timeId, out ActivityScheduleEntry entry)
    {
        entry = Entries.Value.FirstOrDefault(row => row.Id == timeId);
        return entry.Id != 0;
    }
}
