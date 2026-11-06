using AscNet.Common.Util;
using AscNet.Table.V2.share.activity;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.simulatetrain;
using AscNet.Table.V2.share.fuben.cerberusgame;
using AscNet.Table.V2.share.fuben.maintaineraction;
using AscNet.Table.V2.share.miniactivity.dyemerge;
using AscNet.Table.V2.share.samecolorgame;
using AscNet.Table.V2.share.theatre;
using AscNet.Table.V2.share.theatre3;
using AscNet.Table.V2.share.theatre4;
using AscNet.Table.V2.share.miniactivity.fangkuai;
using AscNet.Table.V2.share.fuben.transfinitetower;
using AscNet.Table.V2.client.activitybrief;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.client.fuben;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.punishaar;

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
    internal const long UnboundedEnd = 32503680000; // 3000-01-01 UTC; see the Theatre5 note below.

    private static readonly Lazy<IReadOnlyList<ActivityScheduleEntry>> Entries = new(() =>
        TableReaderV2.Parse<Theatre3ActivityTable>()
            .Where(row => row.TimeId > 0)
            .Select(row => new ActivityScheduleEntry(row.TimeId, 0, 0,
                $"local-policy:Theatre3:permanent-mode:Theatre3Activity:Id={row.Id}:TimeId={row.TimeId}"))
            // AscNet policy: Awakening Tundra remains available as a permanent roguelike.
            .Concat(TableReaderV2.Parse<Theatre4ActivityTable>()
                .Where(row => row.TimeId > 0)
                .Select(row => new ActivityScheduleEntry(row.TimeId, 0, 0,
                    $"local-policy:Theatre4:permanent-mode:Theatre4Activity:Id={row.Id}:TimeId={row.TimeId}")))
            .Concat(TheatreDecorationEntries())
            // AscNet policy: Circuit Connect is a permanent mode. Its client manager opens the mode
            // from the authored activity row's positive TimerId without a calendar bound, and no
            // authoritative retail window exists for AscNet; the authored table is the only source.
            .Concat(TableReaderV2.Parse<SameColorGameActivityTable>()
                .Where(row => row.TimerId is > 0)
                .Select(row => new ActivityScheduleEntry(row.TimerId!.Value, 0, 0,
                    $"local-policy:SameColorGame:permanent-mode:SameColorGameActivity:Id={row.Id}:"
                    + $"TimerId={row.TimerId}:user-approved")))
            // Godfall's PvP client requires a positive end. 3000-01-01 UTC stays within the
            // Windows _localtime64 range even after a local-time-zone adjustment.
            // https://learn.microsoft.com/cpp/c-runtime-library/reference/localtime-localtime32-localtime64
            .Concat(new[] { 34, 35, 46401 }.Select(timeId => new ActivityScheduleEntry(timeId, 0,
                UnboundedEnd,
                $"feature-window:Theatre5:unbounded-calendar:user-approved:TimeId={timeId}")))
            .Concat(TableReaderV2.Parse<ActivityScheduleTable>()
                .Select(row => new ActivityScheduleEntry(row.Id, row.StartTime, row.EndTime, row.Source)))
            .Concat(SimulateTrainWindowEntries())
            // Policy rows follow every authoritative source: DistinctBy keeps the first, so an
            // official window for any of these TimeIds always wins.
            .Concat(FangKuaiChapterEntries())
            .Concat(PunishaarStageEntries())
            .Concat(TransfiniteTowerEntries())
            .Concat(CerberusGameEntries())
            .Concat(MaintainerActionEntries())
            // AscNet policy (user-approved, not retail parity): the separate self-choice lottery
            // has no authoritative calendar. Its client requires a positive end time.
            .Concat(new[] { new ActivityScheduleEntry(49501, 0, UnboundedEnd,
                "local-policy:missing-calendar:unbounded-calendar:"
                + "LottoPrimary:Id=5(3.7 Choice self-choice coating lottery):TimeId=49501:user-approved") })
            .Concat(MissingCalendar49Entries())
            .Concat(ClientCalendarEntries())
            .DistinctBy(row => row.Id)
            .OrderBy(row => row.Id)
            .ToArray());

    /// <summary>
    /// AscNet policy (user-approved missing-calendar availability, AGENTS.md; not retail parity): the 4.9
    /// client tables reference these TimeIds and no authoritative window exists yet. Each opens unbounded
    /// (positive end keeps client countdowns valid) and any official or derived window for the same TimeId
    /// wins because DistinctBy keeps the earlier entry. Fate draws and client-only gates (ActivityBriefGroup,
    /// ActivityBtn, SkipFunctional, Draw*) are not opened. Listed in Docs/closed.md.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> MissingCalendar49Entries() =>
        MissingCalendar49.Select(row => new ActivityScheduleEntry(row.TimeId, 0, UnboundedEnd,
            $"local-policy:missing-calendar:unbounded-calendar:{row.Source}:TimeId={row.TimeId}:4.9:user-approved"));

    /// <summary>
    /// AscNet policy (user-approved missing-calendar availability, AGENTS.md; not retail parity): every TimeId the
    /// client's own entry tables reference gets an unbounded window unless an official or derived window exists
    /// (DistinctBy keeps the earlier entry). Derived from the tables, not a per-version list, so a re-import is the
    /// only step a new version needs: FubenClientConfig.MainPanelTimeId and FubenActivityTimeTips.TimeId drive the
    /// Battle Screen "Limited Event" button/caption, FubenActivity.TimeId the timed Battle Screen entries, and
    /// ActivityBriefGroup.TimeId the Events page entries listed by ActivityBrief.GroupIdList. An entry whose skip
    /// the server cannot serve stays closed: ActivityUrl skips (Origin 9) hand the player token to a retail web page,
    /// and Dreamhouse Tycoon (SkipToSkyTowerMain) issues 13 requests no handler answers. Fate draws are not here.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> ClientCalendarEntries()
    {
        Dictionary<int, SkipFunctionalTable> skips = TableReaderV2.Parse<SkipFunctionalTable>()
            .GroupBy(skip => skip.SkipId).ToDictionary(group => group.Key, group => group.First());
        bool Served(int? skipId) => skipId is not int id || !skips.TryGetValue(id, out SkipFunctionalTable? skip)
            || skip.Origin != 9 && skip.UiName != "SkipToSkyTowerMain";
        Dictionary<int, ActivityBriefGroupTable> groups = TableReaderV2.Parse<ActivityBriefGroupTable>()
            .ToDictionary(group => group.Id);

        IEnumerable<(string Source, int TimeId)> references =
            TableReaderV2.Parse<FubenClientConfigTable>()
                .Where(row => row.Key == "MainPanelTimeId")
                .Select(row => ("FubenClientConfig.MainPanelTimeId", int.Parse(row.Values[0])))
            .Concat(TableReaderV2.Parse<FubenActivityTimeTipsTable>()
                .Select(row => ("FubenActivityTimeTips.TimeId", row.TimeId)))
            .Concat(TableReaderV2.Parse<FubenActivityTable>()
                .Where(row => Served(row.SkipId))
                .Select(row => ("FubenActivity.TimeId", row.TimeId)))
            .Concat(TableReaderV2.Parse<ActivityBriefTable>()
                .SelectMany(brief => brief.GroupIdList)
                .Where(groupId => groups.ContainsKey(groupId) && Served(groups[groupId].SkipId))
                .Select(groupId => ("ActivityBriefGroup.TimeId", groups[groupId].TimeId ?? 0)));
        return references.Where(row => row.TimeId > 0).DistinctBy(row => row.TimeId)
            .Select(row => new ActivityScheduleEntry(row.TimeId, 0, UnboundedEnd,
                $"local-policy:missing-calendar:unbounded-calendar:{row.Source}:TimeId={row.TimeId}:user-approved"));
    }

    /// <summary>
    /// The window the client is told for a missing-calendar (0, UnboundedEnd) entry. The client has no "permanent"
    /// convention for window-driven entries (<c>RefreshAcitivityTime</c> needs end &gt; now; <c>MM.dd</c> and the
    /// "Remaining Time" banner format the raw bounds), so 0 / 3000-01-01 rendered as "01.01-01.01, 355436 days".
    /// The push instead carries the current UTC month and the next three, recomputed at every login; the server's
    /// own checks keep the unbounded entry. ponytail: a session left running over ~2 months expires client timers.
    /// </summary>
    public static (long Start, long End) ClientWindow(long startTime, long endTime, DateTimeOffset now)
    {
        if (startTime != 0 || endTime != UnboundedEnd)
            return (startTime, endTime);
        DateTime utc = now.UtcDateTime;
        DateTimeOffset month = new(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (month.ToUnixTimeSeconds(), month.AddMonths(3).ToUnixTimeSeconds());
    }

    private static readonly (int TimeId, string Source)[] MissingCalendar49 =
    [
        (730, "TransfiniteActivity.TimeId"),
        (731, "TransfiniteActivity.TimeId"),
        (1282, "MailCollectionBoxActivity.TimeId"),
        (1283, "MailCollectionBoxActivity.TimeId"),
        (1284, "MailCollectionBoxActivity.TimeId"),
        (1285, "MailCollectionBoxActivity.TimeId"),
        (1286, "MailCollectionBoxActivity.TimeId"),
        (1287, "MailCollectionBoxActivity.TimeId"),
        (1288, "MailCollectionBoxActivity.TimeId"),
        (1289, "MailCollectionBoxActivity.TimeId"),
        (1290, "MailCollectionBoxActivity.TimeId"),
        (1291, "MailCollectionBoxActivity.TimeId"),
        (14103, "TaskTimeLimit.TimeId"),
        (50200, "PunishaarActivity.TimeId"),
        (51101, "NewActivityCalendarActivity.MainTimeId"),
        (51102, "NewActivityCalendarPeriod.TimeId"),
        (51201, "BossActivity.ActivityTimeId+BossActivity.FightTimeId+NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId"),
        (51202, "ExperimentLevel.TimeId+NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId+TaskTimeLimit.TimeId"),
        (51203, "ExperimentLevel.TimeId+NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId+TaskTimeLimit.TimeId"),
        (51204, "TaskTimeLimit.TimeId"),
        (51205, "NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId+TaskTimeLimit.TimeId"),
        (51206, "ExperimentLevel.TimeId"),
        (51207, "ExperimentLevel.TimeId"),
        (51301, "NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId+TaskTimeLimit.TimeId"),
        (51401, "Lotto.TimeId+LottoPrimary.TimeId"),
        (51502, "TaskTimeLimit.TimeId"),
        (51503, "NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId"),
        (51701, "MainLine2Chapter.ActivityTimeId"),
        (51903, "TaskTimeLimit.TimeId"),
        (51909, "ActivityBrief.TimeId"),
        (51910, "SignIn.TimeId"),
        (51912, "PassportActivity.TimeId+PassportTaskGroup.TimeId"),
        (51920, "PassportTimeLimitTaskReward.ExTimeId"),
        (51942, "NewActivityCalendarActivity.MainTimeId+NewActivityCalendarPeriod.TimeId"),
    ];

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
    /// AscNet policy (missing-retail policy): Operation Guardians' MaintainerActionConfig.TimeId has no authored
    /// calendar row, so the mode is permanently available. A positive end keeps the client's countdown valid.
    /// An official ActivitySchedule row for the same TimeId precedes this one and wins.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> MaintainerActionEntries() =>
        TableReaderV2.Parse<MaintainerActionConfigTable>().Where(row => row.TimeId > 0)
            .Select(row => new ActivityScheduleEntry(row.TimeId, 0, UnboundedEnd,
                $"local-policy:MaintainerAction:missing-calendar:permanent-mode:MaintainerActionConfig:Id={row.Id}:TimeId={row.TimeId}"));

    /// <summary>
    /// AscNet policy (user-approved): missing Cerberus activity, chapter, and reachable condition
    /// calendars are permanent. Official entries precede these rows and win by TimeId.
    /// A positive end keeps the client's countdown valid; progression conditions remain intact.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> CerberusGameEntries()
    {
        foreach (CerberusGameActivityTable activity in TableReaderV2.Parse<CerberusGameActivityTable>()
            .Where(row => row.TimeId is > 0))
            yield return new ActivityScheduleEntry(activity.TimeId!.Value, 0, UnboundedEnd,
                $"local-policy:CerberusGame:missing-calendar:permanent-mode:"
                + $"CerberusGameActivity:Id={activity.Id}:TimeId={activity.TimeId}:user-approved");

        CerberusGameChapterTable[] chapters = TableReaderV2.Parse<CerberusGameChapterTable>().ToArray();
        foreach (CerberusGameChapterTable chapter in chapters.Where(row => row.TimeId > 0))
            yield return new ActivityScheduleEntry(chapter.TimeId, 0, UnboundedEnd,
                $"local-policy:CerberusGame:missing-calendar:permanent-mode:"
                + $"CerberusGameChapter:Id={chapter.Id}:TimeId={chapter.TimeId}:user-approved");

        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
            .ToDictionary(row => row.Id);
        // Communication rows have no OpenCondition; their owning story points supply the gates.
        Stack<int> pending = new(chapters.Select(row => row.OpenCondition)
            .Concat(TableReaderV2.Parse<CerberusGameStoryLineTable>().Select(row => row.OpenCondition))
            .Concat(TableReaderV2.Parse<CerberusGameStoryPointTable>().Select(row => row.OpenCondition))
            .Select(conditionId => conditionId.GetValueOrDefault())
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
            else if (condition.Type == 23001 && condition.Params.Count > 0 && condition.Params[0] > 0)
            {
                int timeId = condition.Params[0];
                yield return new ActivityScheduleEntry(timeId, 0, UnboundedEnd,
                    $"local-policy:CerberusGame:missing-calendar:permanent-mode:"
                    + $"Condition:Id={conditionId}:TimeId={timeId}:user-approved");
            }
        }
    }

    /// <summary>
    /// AscNet policy (user-approved): FangKuai chapter stage-group TimeIds with no authored window
    /// inherit their parent FangKuaiActivity window from ActivitySchedule.tsv; a parent without a
    /// window leaves the chapter permanent. Stage PreStageId progression still gates entry.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> FangKuaiChapterEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        Dictionary<int, FangKuaiChapterTable> chapters = TableReaderV2.Parse<FangKuaiChapterTable>().ToDictionary(row => row.Id);
        Dictionary<int, FangKuaiStageGroupTable> groups = TableReaderV2.Parse<FangKuaiStageGroupTable>().ToDictionary(row => row.Id);
        foreach (FangKuaiActivityTable activity in TableReaderV2.Parse<FangKuaiActivityTable>().Where(row => row.TimeId > 0))
        {
            bool inherited = official.TryGetValue(activity.TimeId, out ActivityScheduleTable? parent);
            foreach (int timeId in activity.ChapterIds
                .SelectMany(id => chapters.TryGetValue(id, out FangKuaiChapterTable? chapter) ? chapter.StageGroupIds : [])
                .Select(id => groups.TryGetValue(id, out FangKuaiStageGroupTable? group) ? group.TimeId : 0)
                .Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return new ActivityScheduleEntry(timeId, parent?.StartTime ?? 0, parent?.EndTime ?? 0,
                    $"local-policy:FangKuai:chapter-{(inherited ? "inherits-parent-window" : "permanent")}:"
                    + $"FangKuaiActivity:Id={activity.Id}:ParentTimeId={activity.TimeId}:TimeId={timeId}:user-approved");
        }
    }

    private static ActivityScheduleEntry InheritParent(Dictionary<long, ActivityScheduleTable> official,
        long parentTimeId, long timeId, string mode, string row)
    {
        bool inherited = official.TryGetValue(parentTimeId, out ActivityScheduleTable? parent);
        return new ActivityScheduleEntry(timeId, parent?.StartTime ?? 0, parent?.EndTime ?? 0,
            $"local-policy:{mode}-{(inherited ? "inherits-parent-window" : "permanent")}:"
            + $"{row}:ParentTimeId={parentTimeId}:TimeId={timeId}:user-approved");
    }

    /// <summary>
    /// AscNet policy (user-approved): Circuit Calculus stage TimeIds with no authored window
    /// inherit their PunishaarActivity parent window. PreStageId progression still gates entry.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> PunishaarStageEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        PunishaarStageGroupTable[] stages = TableReaderV2.Parse<PunishaarStageGroupTable>().ToArray();
        foreach (PunishaarActivityTable activity in TableReaderV2.Parse<PunishaarActivityTable>().Where(row => row.TimeId > 0))
            foreach (int timeId in stages.Where(stage => stage.GroupId == activity.StageGroup)
                .Select(stage => stage.TimeId).Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return InheritParent(official, activity.TimeId, timeId, "Punishaar:stage",
                    $"PunishaarActivity:Id={activity.Id}");
    }

    /// <summary>
    /// AscNet policy (user-approved): Overclock Simulation chapter/character unlock TimeIds with no
    /// authored window inherit the TransfiniteTowerActivity parent window. The rank-reward TimeId
    /// opens at the parent's end for the authored RankRewardExpireInterval; with no bounded parent
    /// end there is no ranking close, so no reward window is emitted.
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> TransfiniteTowerEntries()
    {
        var official = TableReaderV2.Parse<ActivityScheduleTable>().ToDictionary(row => (long)row.Id);
        Dictionary<int, TransfiniteTowerChapterTable> chapters = TableReaderV2.Parse<TransfiniteTowerChapterTable>()
            .ToDictionary(row => row.Id);
        int[] characterTimeIds = TableReaderV2.Parse<TransfiniteTowerCharacterTable>()
            .Select(row => row.UnLockTimeId).ToArray();
        long expireInterval = long.Parse(TableReaderV2.Parse<TransfiniteTowerConfigTable>()
            .Single(row => row.Key == "RankRewardExpireInterval").Values[0]);
        foreach (TransfiniteTowerActivityTable activity in TableReaderV2.Parse<TransfiniteTowerActivityTable>()
            .Where(row => row.TimeId > 0))
        {
            string row = $"TransfiniteTowerActivity:Id={activity.Id}";
            foreach (int timeId in activity.ChapterIds
                .Select(id => chapters.TryGetValue(id, out TransfiniteTowerChapterTable? chapter) ? chapter.UnLockTimeId : 0)
                .Concat(characterTimeIds)
                .Where(timeId => timeId > 0 && timeId != activity.TimeId).Distinct())
                yield return InheritParent(official, activity.TimeId, timeId, "TransfiniteTower:unlock", row);
            if (activity.RankRewardTimeId > 0 && official.TryGetValue(activity.TimeId, out ActivityScheduleTable? parent)
                && parent.EndTime > 0)
                yield return new ActivityScheduleEntry(activity.RankRewardTimeId, parent.EndTime,
                    parent.EndTime + expireInterval,
                    $"local-policy:TransfiniteTower:rank-reward-after-parent-end:{row}:ParentTimeId={activity.TimeId}:"
                    + $"RankRewardExpireInterval={expireInterval}:TimeId={activity.RankRewardTimeId}:user-approved");
        }
    }

    /// <summary>
    /// Simulated Trial practice bosses are a permanent catalog: their TimeId/ImpasseTimeId columns
    /// are retail rotation metadata with no authoritative AscNet schedule, so every id the monster
    /// table publishes stays available at all times (retail opened each from a rotation date).
    /// </summary>
    private static IEnumerable<ActivityScheduleEntry> SimulateTrainWindowEntries() =>
        TableReaderV2.Parse<SimulateTrainMonsterTable>()
            .SelectMany(row => new[]
            {
                (TimeId: row.TimeId, Column: "TimeId"),
                (TimeId: row.ImpasseTimeId, Column: "ImpasseTimeId")
            })
            .Where(entry => entry.TimeId > 0)
            .DistinctBy(entry => entry.TimeId)
            .OrderBy(entry => entry.TimeId)
            .Select(entry => new ActivityScheduleEntry(entry.TimeId, 0, 0,
                $"local-policy:SimulateTrain:permanent-boss-practice:SimulateTrainMonster:"
                + $"{entry.Column}={entry.TimeId}:user-approved"));

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
