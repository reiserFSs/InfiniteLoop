using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.passport;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    // 4.9 Passport weekly rounds (PassportTaskGroup TimeIds 51913-51919): the season window (51912, policy start = end of the previous
    // season) is split into rounds that open one after another, the server exposes/claims only the round in time, and the client gets
    // the same windows as TimeLimitCtrl rows.
    private static void ValidatePassportWeekly()
    {
        const long week = 604_800;
        PassportActivityTable season = ScheduledPassportSeasons().Last();
        PassportActivityTable previous = ScheduledPassportSeasons()[^2];
        Type passportModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PassportModule");
        Type taskModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule");
        MethodInfo remaining = RequiredMethod(taskModule, "RemainingSecondsInWeeklyResetPeriod", BindingFlags.Static | BindingFlags.NonPublic, [typeof(long)]);
        MethodInfo currentRound = RequiredMethod(passportModule, "CurrentRound", BindingFlags.Static | BindingFlags.NonPublic, [typeof(PassportActivityTable), typeof(DateTimeOffset)]);
        MethodInfo buildControls = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
            "BuildTimeLimitControlConfigList", BindingFlags.Static | BindingFlags.NonPublic, [typeof(DateTimeOffset), typeof(bool)]);

        // ---- policy: season window starts when the previous season ends (no retail 4.9 calendar) ----
        ActivityScheduleEntry window = ActivityScheduleService.All.Single(entry => entry.Id == season.TimeId);
        ActivityScheduleEntry previousWindow = ActivityScheduleService.All.Single(entry => entry.Id == previous.TimeId);
        AssertEqual(previousWindow.EndTime, window.StartTime, "Passport season starts when the previous season ends");
        AssertEqual(true, window.Source.StartsWith("local-policy:missing-calendar:season-after-previous:", StringComparison.Ordinal),
            "Passport season window carries the AscNet policy label");

        long[] roundIds = AscNet.Common.Util.TableReaderV2.Parse<PassportTaskGroupTable>()
            .Where(group => group.Group == season.WeekTaskGroup && group.Type == 2 && group.TimeId is > 0)
            .Select(group => (long)group.TimeId!.Value).OrderBy(id => id).ToArray();
        AssertEqual(7, roundIds.Length, "Passport season has seven weekly rounds");
        foreach (long id in roundIds)
            AssertEqual(false, ActivityScheduleService.TryGet(id, out _), $"weekly round TimeId {id} is not opened as a schedule entry");

        // Expected windows: week 1 = season start..first weekly reset, week N = reset + (N-2) weeks.
        long firstReset = window.StartTime + (long)remaining.Invoke(null, [window.StartTime])!;
        long[] starts = Enumerable.Range(0, 7).Select(i => i == 0 ? window.StartTime : firstReset + (i - 1) * week).ToArray();
        long[] ends = Enumerable.Range(0, 7).Select(i => i == 6 ? window.EndTime : firstReset + i * week).ToArray();
        AssertEqual(7, starts.Distinct().Count(), "weekly round start dates are distinct");
        for (int i = 0; i < 6; i++)
            AssertEqual(ends[i], starts[i + 1], $"round {i + 1} ends where round {i + 2} starts");

        JToken[] ControlsAt(long unix) => JArray.FromObject(buildControls.Invoke(null, [DateTimeOffset.FromUnixTimeSeconds(unix), false])!)
            .Where(control => roundIds.Contains(control.Value<long>("Id"))).ToArray();
        void ExpectRound(long unix, int index, string label)
        {
            JToken[] controls = ControlsAt(unix);
            AssertEqual(7, controls.Length, $"{label}: every round has a time control");
            JToken[] inTime = controls.Where(control => control.Value<long>("StartTime") <= unix && unix < control.Value<long>("EndTime")).ToArray();
            AssertEqual(1, inTime.Length, $"{label}: exactly one round is in time");
            AssertEqual(roundIds[index], inTime[0].Value<long>("Id"), $"{label}: client round");
            AssertEqual(index, (int)currentRound.Invoke(null, [season, DateTimeOffset.FromUnixTimeSeconds(unix)])!, $"{label}: server round");
        }
        AssertEqual(0, ControlsAt(window.StartTime - 1).Length, "no round control before the season opens");
        for (int i = 0; i < 7; i++)
        {
            ExpectRound(starts[i], i, $"round {i + 1} first second");
            ExpectRound(i == 6 ? starts[i] + 30 * week : ends[i] - 1, i, $"round {i + 1} last second");
        }
        JToken[] all = ControlsAt(window.StartTime);
        AssertEqual(7, all.Select(control => control.Value<long>("StartTime")).Distinct().Count(), "round controls carry distinct start dates");
        for (int i = 0; i < 7; i++)
            AssertEqual(starts[i], all.Single(control => control.Value<long>("Id") == roundIds[i]).Value<long>("StartTime"), $"round {i + 1} control start");

        // ---- exposure and claim gating on a season shifted so the clock sits in each round ----
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int seasonIndex = Array.FindIndex(schedules, entry => entry.Id == season.TimeId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long nextReset = now + (long)remaining.Invoke(null, [now])!;
        void PlaceClockInRound(int index) => schedules[seasonIndex] = window with
        {
            // Round 1: clock one second after the start (first reset is still ahead); round N: first reset = nextReset - (N-1) weeks.
            StartTime = index == 0 ? now - 1 : nextReset - index * week - 3_600,
            Source = "synthetic-test:PassportWeekly"
        };
        try
        {
            using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out _, out _, out _, out _);
            PacketFactory.LoadPacketHandlers();
            const long uid = 99_472;
            Player player = CreateDrawCompatibilityPlayer(uid);
            player.PlayerData.Level = 100;
            Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.ActionPoint, Count = 3_000 }]);
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "passport-weekly-test");
            Session session = harness.Session;
            session.stage = CreateLoginAccountCompatibilityStage(uid);
            int packetId = 99_473;
            player.Passport.ActivityId = previous.Id;
            PlaceClockInRound(0);
            void Drain() { while (harness.TryReadAvailablePacket("passport weekly drain", out _)) { } }
            RequiredMethod(passportModule, "ReconcileAndPushLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]).Invoke(null, [session]);
            Drain();
            AssertEqual(season.Id, player.Passport.ActivityId, "Passport rolled into the live season");

            MethodInfo isActive = RequiredMethod(passportModule, "IsActivePassportTask", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(int)]);
            MethodInfo buildTaskData = RequiredMethod(taskModule, "BuildTaskData", BindingFlags.Static | BindingFlags.Public, [typeof(Session)]);
            // Group 364 (round 1) alone has 80014, group 370 (round 7) alone has 80017, groups 365-369 (rounds 2-6) share 80003.
            int[] probeTasks = [80_014, 80_003, 80_017];
            bool[] ExposedAndActive(out bool[] exposed)
            {
                HashSet<int> ids = JArray.FromObject(buildTaskData.Invoke(null, [session])!).Select(task => task.Value<int>("Id")).ToHashSet();
                exposed = probeTasks.Select(ids.Contains).ToArray();
                return probeTasks.Select(id => (bool)isActive.Invoke(null, [session, id])!).ToArray();
            }
            for (int index = 0; index < 7; index++)
            {
                PlaceClockInRound(index);
                bool[] expected = [index == 0, index is >= 1 and <= 5, index == 6];
                bool[] active = ExposedAndActive(out bool[] exposed);
                AssertEqual(string.Join(",", expected), string.Join(",", active), $"round {index + 1} server-active tasks 80014/80003/80017");
                AssertEqual(string.Join(",", expected), string.Join(",", exposed), $"round {index + 1} exposed tasks 80014/80003/80017");
            }

            FinishMultiTaskResponse Claim(params int[] ids)
            {
                int id = packetId++;
                InvokeRegisteredRequestHandler("FinishMultiTaskRequest", session, id, new Dictionary<string, object> { ["TaskIds"] = ids });
                FinishMultiTaskResponse response = (FinishMultiTaskResponse)ReadResponsePayload(
                    harness, id, nameof(FinishMultiTaskResponse), "Passport weekly claim", typeof(FinishMultiTaskResponse), 64);
                Drain();
                return response;
            }
            // Every probe task is fully progressed, so only the round gate decides what the claim grants.
            session.player.MissionProgress.ConditionCounters[80_014] = 200;
            session.player.MissionProgress.ConditionCounters[80_003] = 6;
            session.player.MissionProgress.ConditionCounters[80_017] = 3;
            int[] Succeeded(int index, params int[] claim)
            {
                PlaceClockInRound(index);
                return Claim(claim).SuccessTaskIds.Where(claim.Contains).OrderBy(id => id).ToArray();
            }
            AssertIntegerList([80_014], Succeeded(0, 80_014, 80_003, 80_017).Select(id => (long)id).ToArray(), "round 1 claim gates to round-1 tasks");
            AssertIntegerList([80_003], Succeeded(2, 80_014, 80_003, 80_017).Select(id => (long)id).ToArray(), "round 3 claim gates to the shared round tasks");
            AssertIntegerList([80_017], Succeeded(6, 80_014, 80_003, 80_017).Select(id => (long)id).ToArray(), "round 7 claim gates to round-7 tasks");
        }
        finally { schedules[seasonIndex] = window; }
    }
}
