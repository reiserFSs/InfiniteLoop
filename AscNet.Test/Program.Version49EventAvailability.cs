using System.Globalization;
using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.client.activitybrief;
using AscNet.Table.V2.client.draw;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.client.fuben;
using AscNet.Table.V2.share.fuben;

namespace AscNet.Test;

internal static partial class Program
{
    private const string MissingCalendarSource = "local-policy:missing-calendar:unbounded-calendar:";

    /// <summary>
    /// 4.9 event availability, derived from the client tables so a new version needs only a re-import: the Battle Screen
    /// button/caption/timed entries (FubenClientConfig, FubenActivityTimeTips, FubenActivity), the Events page groups
    /// (ActivityBrief/ActivityBriefGroup), the client-visible window rendering, the 4.9 draw catalog and DrawPredict windows.
    /// </summary>
    public static void ValidateVersion49EventAvailability()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1794000000); // 2026-11-06, inside 4.9
        Dictionary<int, SkipFunctionalTable> skips = TableReaderV2.Parse<SkipFunctionalTable>()
            .GroupBy(row => row.SkipId).ToDictionary(group => group.Key, group => group.First());
        bool Unserved(int? skipId) => skipId is int id && skips.TryGetValue(id, out SkipFunctionalTable? skip)
            && skip.Origin == 9;
        void AssertOpenByPolicy(int timeId, string source, string what)
        {
            AssertEqual(true, ActivityScheduleService.TryGet(timeId, out ActivityScheduleEntry entry), $"{what}: TimeId {timeId} has a window");
            AssertEqual(true, entry.IsOpen(now), $"{what}: TimeId {timeId} is open");
            AssertEqual(true, entry.Source.StartsWith(MissingCalendarSource + source, StringComparison.Ordinal),
                $"{what}: TimeId {timeId} is derived from {source}, not a per-version list ({entry.Source})");
        }

        // Battle Screen: the "Limited Event" button (XUiPanelMain.RefreshTimeId) was the empty "Coming soon" panel.
        int mainPanel = int.Parse(TableReaderV2.Parse<FubenClientConfigTable>().Single(row => row.Key == "MainPanelTimeId").Values[0]);
        AssertOpenByPolicy(mainPanel, "FubenClientConfig.MainPanelTimeId", "Battle Screen main panel");
        foreach (FubenActivityTimeTipsTable tip in TableReaderV2.Parse<FubenActivityTimeTipsTable>())
            AssertOpenByPolicy(tip.TimeId, "FubenActivityTimeTips.TimeId", $"Battle Screen caption {tip.Id}");
        foreach (FubenActivityTable activity in TableReaderV2.Parse<FubenActivityTable>().Where(row => row.TimeId > 0))
        {
            if (Unserved(activity.SkipId))
                AssertEqual(false, ActivityScheduleService.IsOpen(activity.TimeId, now), $"unserved Battle Screen entry {activity.Name} stays closed");
            else
                AssertOpenByPolicy(activity.TimeId, "", $"Battle Screen entry {activity.Name}");
        }

        // Events page: every grouped entry the server can serve is open; web-page entries are not.
        Dictionary<int, ActivityBriefGroupTable> groups = TableReaderV2.Parse<ActivityBriefGroupTable>().ToDictionary(row => row.Id);
        int[] grouped = TableReaderV2.Parse<ActivityBriefTable>().SelectMany(row => row.GroupIdList).Where(groups.ContainsKey).ToArray();
        foreach (ActivityBriefGroupTable group in grouped.Select(id => groups[id]).Where(row => row.TimeId is > 0))
        {
            if (Unserved(group.SkipId))
                AssertEqual(false, ActivityScheduleService.IsOpen(group.TimeId!.Value, now), $"unserved Events entry {group.Id} stays closed");
            else if (group.Id == 159)
                AssertEqual(false, ActivityScheduleService.IsOpen(group.TimeId!.Value, now), "Time Trace (Events group 159) stays closed: 4.9.1 tables are placeholder text");
            else
                AssertEqual(true, ActivityScheduleService.IsOpen(group.TimeId!.Value, now), $"Events entry {group.Id} TimeId {group.TimeId} is open");
        }
        AssertEqual(true, ActivityScheduleService.IsOpen(51651, now), "Journal of Promise (Leia affection story, group 143)");
        AssertEqual(true, ActivityScheduleService.IsOpen(51501, now), "Simulated Battlefield (Multi Clear, group 8)");
        AssertEqual(false, ActivityScheduleService.IsOpen(51021, now), "Homeward-Bound Voyage opens a retail H5 page with the player token");
        AssertEqual(true, ActivityScheduleService.IsOpen(51011, now), "Dreamhouse Tycoon: SkyTower handlers exist, so its window is open");

        // Window rendering: the client formats the raw bounds ("MM.dd", "Remaining Time: N day(s)").
        MethodInfo build = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(DateTimeOffset), typeof(bool)]);
        foreach (DateTimeOffset at in new[] { now, DateTimeOffset.UnixEpoch })
        {
            List<TimeLimitCtrlConfigList> controls = (List<TimeLimitCtrlConfigList>)build.Invoke(null, [at, false])!;
            AssertEqual(false, controls.Any(row => row.EndTime == 32503680000), "no 3000-01-01 end is pushed to the client");
            foreach (int timeId in new[] { mainPanel, 51909, 51651, 51501 })
            {
                TimeLimitCtrlConfigList control = controls.Single(row => row.Id == timeId);
                long unix = at.ToUnixTimeSeconds();
                AssertEqual(true, control.StartTime <= unix && unix < control.EndTime, $"pushed window {timeId} contains now");
                AssertEqual(true, control.EndTime - control.StartTime is > 59 * 86400 and < 93 * 86400, $"pushed window {timeId} is a few months, not centuries");
                AssertEqual(true, DateTimeOffset.FromUnixTimeSeconds(control.StartTime).Day == 1, $"pushed window {timeId} starts on a month start");
                AssertEqual(false, string.IsNullOrEmpty(control.StartTimeStr) && control.StartTime != 0, $"pushed window {timeId} start string");
            }
        }

        ValidateVersion49DrawCatalog(now);
        ValidateDrawPredictWindowsMatchShowUpTime();
    }

    private static void ValidateVersion49DrawCatalog(DateTimeOffset now)
    {
        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        FieldInfo clock = manager.GetField("UtcNow", BindingFlags.NonPublic | BindingFlags.Static)!;
        Func<DateTimeOffset> prior = (Func<DateTimeOffset>)clock.GetValue(null)!;
        MethodInfo groupInfos = RequiredMethod(manager, "GetDrawGroupInfos", BindingFlags.Static | BindingFlags.Public, [typeof(Player)]);
        MethodInfo drawInfo = RequiredMethod(manager, "GetDrawInfoById", BindingFlags.Static | BindingFlags.Public, [typeof(int), typeof(Player)]);
        MethodInfo rewardReady = manager.GetMethod("HasRewardConfiguration", BindingFlags.Public | BindingFlags.Static)!;
        Player player = CreateDrawCompatibilityPlayer(49_090);
        try
        {
            clock.SetValue(null, (Func<DateTimeOffset>)(() => now));
            List<DrawGroupInfo> groups = (List<DrawGroupInfo>)groupInfos.Invoke(null, [player])!;
            // Leia limited (1431003), Arrival weapon (2706001), new CUB (16440000): the 4.9 banner of each family.
            foreach ((int groupId, int drawId, int target) in new[] { (11, 1526, 1431003), (4, 384, 2706001), (22, 7073, 16440000) })
            {
                DrawGroupInfo group = groups.Single(row => row.Id == groupId);
                AssertEqual(true, group.OptionalDrawIdList.Contains(drawId), $"4.9 draw {drawId} is offered in group {groupId}");
                AssertEqual(false, group.OptionalDrawIdList.Any(id => id is 1516 or 383 or 7071), $"4.8 draws expired in group {groupId}");
                AssertEqual(0L, group.EndTime, $"4.9 group {groupId} has no countdown (missing calendar)");
                DrawInfo? draw = (DrawInfo?)drawInfo.Invoke(null, [drawId, player]);
                AssertEqual(true, draw is not null && draw.ResourceIds[1] == target, $"draw {drawId} targets {target}");
                AssertEqual(true, (bool)rewardReady.Invoke(null, [drawId])!, $"4.9 draw {drawId} has rewards");
            }
            AssertEqual(false, groups.Any(row => row.Id == 15), "Leia Fate group stays closed (no threshold law)");
            // 4.9 DrawTabs gives the Laya art to tab 2 (Current Season); tab 9 keeps Adelaide's 4.8 Crucible art.
            AssertEqual(2, groups.Single(row => row.Id == 11).Tag, "Leia themed banner is on the 4.9 Current Season tab");
            AssertEqual(true, drawInfo.Invoke(null, [2520, player]) is null, "Leia Fate draw 2520 is not served");
        }
        finally
        {
            clock.SetValue(null, prior);
        }
    }

    /// <summary>DrawPredict.StartTime/EndTime are local columns derived from the retail ShowUpTime text (UTC, end is 06:59 of the last day).</summary>
    private static void ValidateDrawPredictWindowsMatchShowUpTime()
    {
        foreach (DrawPredictTable row in TableReaderV2.Parse<DrawPredictTable>())
        {
            string[] parts = row.ShowUpTime.Split('-');
            string start = DateTimeOffset.FromUnixTimeSeconds(row.StartTime).ToString("MM/dd H:mm", CultureInfo.InvariantCulture);
            string end = DateTimeOffset.FromUnixTimeSeconds(row.EndTime - 60).ToString("MM/dd H:mm", CultureInfo.InvariantCulture);
            AssertEqual(parts[0], start, $"DrawPredict {row.Id} start matches ShowUpTime");
            AssertEqual(parts[1], end, $"DrawPredict {row.Id} end matches ShowUpTime");
        }
    }
}
