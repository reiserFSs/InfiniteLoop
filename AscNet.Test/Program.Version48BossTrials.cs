using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using System.Reflection;
using BossActivityTable = AscNet.Table.V2.share.fuben.bossactivity.BossActivityTable;
using BossSectionTable = AscNet.Table.V2.share.fuben.bossactivity.BossSectionTable;
using BossStarRewardTable = AscNet.Table.V2.share.fuben.bossactivity.BossStarRewardTable;
using TeachingActivityTable = AscNet.Table.V2.share.fuben.teaching.TeachingActivityTable;
using SkipFunctionalTable = AscNet.Table.V2.client.functional.SkipFunctionalTable;

namespace AscNet.Test;

internal static partial class Program
{
    // Real schedule windows (no overrides): the scheduled boss season opens only inside its own
    // window, every closed season stays closed, and both trial skip routes resolve to open controls.
    private static void ValidateVersion48BossTrialWindows()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(out _, out _);
        List<BossActivityTable> activities = TableReaderV2.Parse<BossActivityTable>();
        BossActivityTable season = activities
            .Where(row => row.ActivityTimeId is > 0 && ActivityScheduleService.TryGet(row.ActivityTimeId.Value, out ActivityScheduleEntry entry) && entry.EndTime > 0)
            .MaxBy(row => row.Id) ?? throw new InvalidDataException("No dated BossActivity season.");
        ActivityScheduleService.TryGet(season.ActivityTimeId!.Value, out ActivityScheduleEntry window);
        AssertEqual(season.ActivityTimeId, season.FightTimeId, "Boss season fight window follows activity window");

        const long playerId = 99_748;
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), CreateDrawCompatibilityPlayer(playerId),
            CreateDrawCompatibilityInventory(playerId, []), "boss-trials-48-test");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        MethodInfo build = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule"),
            "BuildActivityLoginData", BindingFlags.Static | BindingFlags.NonPublic, [typeof(AscNet.GameServer.Session), typeof(DateTimeOffset?)]);
        NotifyBossActivityData? At(long unix) => (NotifyBossActivityData?)build.Invoke(null, [harness.Session, DateTimeOffset.FromUnixTimeSeconds(unix)]);

        NotifyBossActivityData open = At(window.StartTime + 3600) ?? throw new InvalidDataException("Scheduled boss season is not visible inside its window.");
        AssertEqual(season.Id, open.ActivityId, "Boss season inside window");
        BossSectionTable section = TableReaderV2.Parse<BossSectionTable>().Single(row => row.Id == open.SectionId);
        AssertEqual(season.Id, section.ActivityId, "Boss section re-pointed to open season");
        HashSet<int> rewardIds = TableReaderV2.Parse<AscNet.Table.V2.share.reward.RewardTable>().Select(row => row.Id).ToHashSet();
        foreach (BossStarRewardTable tier in TableReaderV2.Parse<BossStarRewardTable>().Where(row => section.StarRewardId.Contains(row.Id)))
            AssertEqual(true, rewardIds.Contains(tier.RewardId), $"Boss star tier {tier.Id} reward {tier.RewardId} exists");
        // 4.9: the only season with a TimeId (51201) has an unbounded AscNet missing-calendar window, so there is no
        // "before" instant to probe; seasons whose retail TimeId is 0 (row 47) must stay closed instead.
        if (window.StartTime > 0)
            AssertEqual(null, At(window.StartTime - 1), "Boss before season window: earlier seasons stay closed");
        else
        {
            AssertEqual(true, window.Source.StartsWith("local-policy:missing-calendar:unbounded-calendar:"), "Undated boss season is missing-calendar policy");
            AssertEqual(false, activities.Where(row => (row.ActivityTimeId ?? 0) <= 0).Any(row => row.Id == open.ActivityId),
                "A boss season with retail TimeId 0 is never published");
        }
        AssertEqual(null, At(window.EndTime + 1), "Boss after season window");

        // Character trials: each OnOpenCharacterFileFubenActivity route's TeachingActivity TimeId is emitted with a real window.
        MethodInfo controls = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(DateTimeOffset), typeof(bool)]);
        Dictionary<int, TeachingActivityTable> teachings = TableReaderV2.Parse<TeachingActivityTable>().ToDictionary(row => row.Id);
        Dictionary<int, SkipFunctionalTable> skips = TableReaderV2.Parse<SkipFunctionalTable>().GroupBy(row => row.SkipId).ToDictionary(g => g.Key, g => g.First());
        foreach (int skipId in new[] { 20235, 20395 })
        {
            SkipFunctionalTable skip = skips[skipId];
            AssertEqual("OnOpenCharacterFileFubenActivity", skip.UiName, $"Skip {skipId} opens a character trial");
            TeachingActivityTable teaching = teachings[skip.CustomParams.Single(id => id > 0)];
            long probe = ActivityScheduleService.TryGet(teaching.TimeId!.Value, out ActivityScheduleEntry own)
                ? own.StartTime + 3600
                : window.StartTime + 3600;
            bool undated = (own.Id != 0 ? own.StartTime : window.StartTime) == 0; // 4.9 missing-calendar windows start at 0
            TimeLimitCtrlConfigList control = ((List<TimeLimitCtrlConfigList>)controls.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(probe), false])!)
                .SingleOrDefault(row => row.Id == teaching.TimeId)
                ?? throw new InvalidDataException($"Trial {teaching.Id} TimeId {teaching.TimeId} has no window (skip {skipId}).");
            AssertEqual(true, (control.StartTime > 0 || undated) && control.StartTime <= probe && probe < control.EndTime,
                $"Trial {teaching.Id} window contains probe");
        }
    }
}
