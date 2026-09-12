using AscNet.Common.Util;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.theatre;
using AscNet.Table.V2.share.theatre3;

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

/// <summary>Event availability derived from version tables, public notices, and documented local mode policies.</summary>
public static class ActivityScheduleService
{
    private static readonly Lazy<IReadOnlyList<ActivityScheduleEntry>> Entries = new(() =>
        TableReaderV2.Parse<Theatre3ActivityTable>()
            .Where(row => row.TimeId > 0)
            .Select(row => new ActivityScheduleEntry(row.TimeId, 0, 0,
                $"local-policy:Theatre3:permanent-mode:Theatre3Activity:Id={row.Id}:TimeId={row.TimeId}"))
            .Concat(TheatreDecorationEntries())
            // Godfall's PvP client requires a positive end. 3000-01-01 UTC stays within the
            // Windows _localtime64 range even after a local-time-zone adjustment.
            // https://learn.microsoft.com/cpp/c-runtime-library/reference/localtime-localtime32-localtime64
            .Concat(new[] { 34, 35, 46401 }.Select(timeId => new ActivityScheduleEntry(timeId, 0,
                32503680000,
                $"feature-window:Theatre5:unbounded-calendar:user-approved:TimeId={timeId}")))
            .Concat(TableReaderV2.Parse<ActivityScheduleTable>()
                .Select(row => new ActivityScheduleEntry(row.Id, row.StartTime, row.EndTime, row.Source)))
            .DistinctBy(row => row.Id)
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

    private static IEnumerable<ActivityScheduleEntry> TheatreDecorationEntries()
    {
        // Only the approved decoration calendars are permanent; progression and costs remain intact.
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        Stack<int> pending = new(TableReaderV2.Parse<TheatreDecorationTable>()
            .Where(row => row.DecorationId is 20003 or 20004 or 20005)
            .Select(row => row.ConditionId.GetValueOrDefault())
            .Where(conditionId => conditionId > 0));
        HashSet<int> seen = [];
        while (pending.TryPop(out int conditionId))
        {
            if (!seen.Add(conditionId))
                continue;
            ConditionTable condition = conditions[conditionId];
            if (!string.IsNullOrWhiteSpace(condition.Formula))
            {
                foreach (System.Text.RegularExpressions.Match reference in
                    System.Text.RegularExpressions.Regex.Matches(condition.Formula, @"\d+"))
                    pending.Push(int.Parse(reference.Value));
            }
            else if (condition.Type == 23001 && condition.Params.Count > 0
                && condition.Params[0] is 803 or 804 or 805)
            {
                int timeId = condition.Params[0];
                yield return new ActivityScheduleEntry(timeId, 0, 0,
                    $"feature-window:Theatre:permanent-decoration-release:user-approved:"
                    + $"TheatreDecoration:DecorationId=20003,20004,20005:Condition:Id={conditionId}:TimeId={timeId}");
            }
        }
    }

    /// <summary>
    /// Maps an ordinary event stage back to its activity TimeId from the fuben/miniactivity
    /// tables that enumerate stages alongside a TimeId and are not otherwise gated by a
    /// PreFight module. Stages absent from the index (permanent mainline, normal stages,
    /// module-gated event stages) return null and are left to their own gates.
    /// </summary>
    private static readonly Lazy<Dictionary<int, int>> StageTimeIds = new(BuildStageTimeIds);

    public static int? StageTimeId(int stageId) =>
        StageTimeIds.Value.TryGetValue(stageId, out int timeId) ? timeId : null;

    private static Dictionary<int, int> BuildStageTimeIds()
    {
        Dictionary<int, int> stageToTimeId = new();

        void AddStage(int stageId, int? timeId)
        {
            if (stageId > 0 && timeId is > 0)
                stageToTimeId.TryAdd(stageId, timeId.Value);
        }


        foreach (DyeMergeChapterTable chapter in TableReaderV2.Parse<DyeMergeChapterTable>())
            foreach (int stageId in chapter.StageIds)
                AddStage(stageId, chapter.TimeId);


        return stageToTimeId;
    }
}
