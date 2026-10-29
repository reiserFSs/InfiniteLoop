using AscNet.Common.Database;
using AscNet.GameServer.Handlers;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal static partial class Program
{
    // Config.tsv: DailyResetTimestamp 25200 (07:00 UTC), DayOfWeeklyReset 1 (Monday).
    private static void ValidateResetBoundary()
    {
        // Two distinct day boundaries: 2026-03-03 and 2026-03-04 07:00 UTC (Tue/Wed).
        foreach (long reset in new[] { 1772521200L, 1772607600L })
        {
            AssertEqual(TaskModule.CurrentDailyResetPeriod(reset - 60) + 1, TaskModule.CurrentDailyResetPeriod(reset + 60), "Daily period rolls only at 07:00 UTC");
            AssertEqual(TaskModule.CurrentDailyResetPeriod(reset - 7 * 3600), TaskModule.CurrentDailyResetPeriod(reset - 60), "No rollover at UTC midnight");
        }
        // Weekly: Monday 2026-03-02 and 2026-03-09 07:00 UTC.
        foreach (long monday in new[] { 1772434800L, 1773039600L })
        {
            AssertEqual(TaskModule.CurrentWeeklyResetPeriod(monday - 60) + 1, TaskModule.CurrentWeeklyResetPeriod(monday + 60), "Weekly rolls Monday 07:00 UTC");
            AssertEqual(TaskModule.CurrentWeeklyResetPeriod(monday - 7 * 3600), TaskModule.CurrentWeeklyResetPeriod(monday - 60), "No weekly rollover at Monday UTC midnight");
            AssertEqual(0, TaskModule.WeeklyResetDayIndex(monday + 60), "Weekly day index 0 after reset");
            AssertEqual(6, TaskModule.WeeklyResetDayIndex(monday - 60), "Weekly day index 6 before reset");
            AssertEqual(7 * 86400 - 60, TaskModule.RemainingSecondsInWeeklyResetPeriod(monday + 60), "Remaining weekly seconds");
        }

        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForShopCompatibility();
        foreach (long reset in new[] { 1772521200L, 1772607600L })
        {
            const long uid = 99791;
            Player player = CreateDrawCompatibilityPlayer(uid);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, CreateDrawCompatibilityInventory(uid, []), "reset-boundary");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(uid);
            TaskModule.EnsureMissionResets(harness.Session, reset - 3600);
            long before = player.MissionProgress.DailyResetDay;
            AssertEqual(TaskModule.CurrentDailyResetPeriod(reset - 3600), before, "Initial daily period");
            TaskModule.EnsureMissionResets(harness.Session, reset - 60);
            AssertEqual(before, player.MissionProgress.DailyResetDay, "No daily reset at 06:59 UTC");
            TaskModule.EnsureMissionResets(harness.Session, reset + 60);
            AssertEqual(before + 1, player.MissionProgress.DailyResetDay, "Daily reset at 07:01 UTC");

            // Persistence + legacy cutover: a UTC-midnight period stored for "today" is rebased, not reset.
            harness.Session.player = player = BsonSerializer.Deserialize<Player>(player.ToBson());
            AssertEqual(1, player.MissionProgress.ResetEpoch, "ResetEpoch persists");
            AssertEqual(before + 1, player.MissionProgress.DailyResetDay, "Daily period persists");
            player.MissionProgress.ResetEpoch = 0;
            player.MissionProgress.DailyResetDay = (reset - 3600) / 86_400; // legacy value written at 06:00 UTC
            player.MissionProgress.WeeklyResetWeek = ((reset - 3600) / 86_400 + 3) / 7;
            TaskModule.EnsureMissionResets(harness.Session, reset - 3600);
            AssertEqual(TaskModule.CurrentDailyResetPeriod(reset - 3600), player.MissionProgress.DailyResetDay, "Legacy daily rebased without reset");
            AssertEqual(TaskModule.CurrentWeeklyResetPeriod(reset - 3600), player.MissionProgress.WeeklyResetWeek, "Legacy weekly rebased without reset");
        }
    }
}
