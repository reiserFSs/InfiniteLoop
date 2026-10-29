using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.passport;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace AscNet.Test;

internal static partial class Program
{
    // Passport tasks 80185/80254 (10202 login days), 80186/80255 (15223 Simulated Battlefield serum),
    // 80006/80020 (44001 Operation Guardians card plays) and season-rollover reset of type-51 claims/counters.
    private static void ValidatePassportTasks()
    {
        PassportActivityTable newest = AscNet.Common.Util.TableReaderV2.Parse<PassportActivityTable>().MaxBy(row => row.Id)!;
        ActivityScheduleEntry[] schedules = (ActivityScheduleEntry[])ActivityScheduleService.All;
        int seasonIndex = Array.FindIndex(schedules, entry => entry.Id == newest.TimeId);
        if (seasonIndex < 0) throw new InvalidDataException("Passport season has no authored schedule.");
        ActivityScheduleEntry authored = schedules[seasonIndex];
        long duration = authored.EndTime - authored.StartTime;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Shift(int weeksBack) => schedules[seasonIndex] = authored with
        {
            StartTime = now - 3_600 - weeksBack * 604_800L,
            EndTime = now + duration,
            Source = "synthetic-test:PassportTasks"
        };
        Shift(0);
        try { ValidatePassportTasksFlow(Shift); }
        finally { schedules[seasonIndex] = authored; }
    }

    private static void ValidatePassportTasksFlow(Action<int> shiftSeason)
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBiancaCompatibility(out _, out _, out _, out _);
        PacketFactory.LoadPacketHandlers();
        List<PassportActivityTable> seasons = AscNet.Common.Util.TableReaderV2.Parse<PassportActivityTable>().OrderBy(row => row.Id).ToList();
        int previousSeasonId = seasons[^2].Id;

        const long uid = 99_470;
        Player player = CreateDrawCompatibilityPlayer(uid);
        player.PlayerData.Level = 100;
        Inventory inventory = CreateDrawCompatibilityInventory(uid, [new Item { Id = Inventory.ActionPoint, Count = 3_000 }]);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), player, inventory, "passport-tasks-test");
        Session session = harness.Session;
        session.stage = CreateLoginAccountCompatibilityStage(uid);
        int packetId = 99_471;

        Type passportModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.PassportModule");
        MethodInfo login = RequiredMethod(passportModule, "ReconcileAndPushLogin", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session)]);
        MethodInfo isActive = RequiredMethod(passportModule, "IsActivePassportTask", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Session), typeof(int)]);
        MethodInfo buildTaskData = RequiredMethod(RequiredAscNetGameServerType("AscNet.GameServer.Handlers.TaskModule"),
            "BuildTaskData", BindingFlags.Static | BindingFlags.Public, [typeof(Session)]);
        PropertyInfo? lastLoginPeriod = typeof(PassportState).GetProperty("LastLoginPeriod");
        List<string> failures = [];
        void Section(string name, Action body)
        {
            try { body(); }
            catch (Exception exception) { failures.Add($"[{name}] {exception.Message}"); }
        }
        long Exp() => inventory.Items.FirstOrDefault(item => item.Id == Inventory.PassportExp)?.Count ?? 0;
        void Drain() { while (harness.TryReadAvailablePacket("passport tasks drain", out _)) { } }
        void Login() { login.Invoke(null, [session]); Drain(); }
        // A new daily-reset period for the next login: the ledger says the last counted login was the previous period.
        void NextDay()
        {
            if (lastLoginPeriod is null) throw new InvalidDataException("PassportState.LastLoginPeriod (season login-day ledger) is missing.");
            lastLoginPeriod.SetValue(player.Passport, (long)lastLoginPeriod.GetValue(player.Passport)! - 1);
        }
        JToken Task(int id) => JArray.FromObject(buildTaskData.Invoke(null, [session]) ?? throw new InvalidDataException("nil task data"))
            .SingleOrDefault(task => task.Value<int>("Id") == id) ?? throw new InvalidDataException($"Passport task {id} not exposed.");
        int Value(int id) => Task(id)["Schedule"]![0]!.Value<int>("Value");
        int State(int id) => Task(id).Value<int>("State");
        void Reload()
        {
            player.Passport = BsonSerializer.Deserialize<PassportState>(player.Passport.ToBson());
            player.MissionProgress = BsonSerializer.Deserialize<MissionProgressState>(player.MissionProgress.ToBson());
        }
        FinishMultiTaskResponse Claim(params int[] ids)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler("FinishMultiTaskRequest", session, id, new Dictionary<string, object> { ["TaskIds"] = ids });
            FinishMultiTaskResponse response = (FinishMultiTaskResponse)ReadResponsePayload(
                harness, id, nameof(FinishMultiTaskResponse), "Passport task claim", typeof(FinishMultiTaskResponse), 64);
            Drain();
            return response;
        }
        void ClaimGrantsExp(int taskId, string label)
        {
            AssertEqual(4 - 1, State(taskId), $"{label} claimable (Achieved)");
            long before = Exp();
            FinishMultiTaskResponse response = Claim(taskId);
            AssertEqual(0, response.Code, $"{label} claim Code");
            AssertEqual(true, response.SuccessTaskIds.Contains(taskId), $"{label} claim success");
            AssertEqual(true, Exp() > before, $"{label} claim grants Passport EXP");
            AssertEqual(4, State(taskId), $"{label} Finish after claim");
        }

        // ---- #4 rollover clears previous-season type-51 claims/counters; #2 first login day of the new season counts ----
        Section("rollover-and-first-login", () =>
        {
        player.Passport.ActivityId = previousSeasonId;
        player.MissionProgress.ClaimedTaskIds.AddRange([80_185, 80_186, 80_254]);
        player.MissionProgress.ConditionCounters[80_185] = 3;
        player.MissionProgress.ConditionCounters[80_186] = 700;
        player.MissionProgress.ConditionCounters[80_006] = 5;
        Login();
        AssertEqual(seasons[^1].Id, player.Passport.ActivityId, "Passport rolled into the live season");
        AssertEqual(false, player.MissionProgress.ClaimedTaskIds.Any(id => id is 80_185 or 80_186 or 80_254), "rollover clears previous-season Passport task claims");
        AssertEqual(false, player.MissionProgress.ConditionCounters.ContainsKey(80_186), "rollover clears Passport serum counter");
        AssertEqual(false, player.MissionProgress.ConditionCounters.ContainsKey(80_006), "rollover clears Passport Guardians counter");
        AssertEqual(1, Value(80_185), "first login day of the new season counts");
        AssertEqual(0, Value(80_186), "rollover keeps no stale serum progress");
        AssertEqual(1, State(80_186), "re-earnable task is Active again after rollover");

        });

        // ---- #2 distinct season login days; reconnects do not count; persisted across BSON reload ----
        Section("login-days", () =>
        {
        Login(); Login();
        AssertEqual(1, Value(80_185), "reconnect in the same reset period does not count");
        Reload();
        AssertEqual(1, Value(80_185), "login-day count survives BSON reload");
        NextDay(); Login();
        AssertEqual(2, Value(80_185), "second distinct login period counts");
        Login();
        AssertEqual(2, Value(80_185), "second reconnect still not counted");
        NextDay(); Login();
        AssertEqual(3, Value(80_185), "third distinct login period counts");
        Reload();
        AssertEqual(3, Value(80_185), "three login days survive BSON reload");
        ClaimGrantsExp(80_185, "login 3 days");
        for (int day = 4; day <= 7; day++) { NextDay(); Login(); }
        AssertEqual(7, Value(80_254), "seven login days reach the 7-day task");
        ClaimGrantsExp(80_254, "login 7 days");

        });

        // ---- #3 Simulated Battlefield serum (15223), only committed spends ----
        Section("sb-serum", () =>
        {
        _ = RepeatChallengeModule.BuildLoginData(player); // initialises/normalises the mode state before seeding it
        player.SimulatedBattlefield.RepeatChallengeCleared = true;
        player.SimulatedBattlefield.RepeatChallengeLevel = 25;
        int Sweep(int count)
        {
            player.SimulatedBattlefield.RepeatChallengeCleared = true;
            player.SimulatedBattlefield.RepeatChallengeLevel = 25; // sweep re-derives the level from EXP; keep the unlock level
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(SweepRequest), session, id, new SweepRequest { StageId = 30_090_802, Count = count });
            SweepResponse response = (SweepResponse)ReadResponsePayload(harness, id, nameof(SweepResponse), "SB sweep", typeof(SweepResponse), 64);
            Drain();
            return response.Code;
        }
        AssertEqual(1, Sweep(0), "empty sweep is rejected");
        AssertEqual(0, Value(80_186), "rejected sweep credits no serum");
        AssertEqual(0, Sweep(25), "SB sweep 25 (750 serum)");
        AssertEqual(750, Value(80_186), "750 serum credited to 80186");
        AssertEqual(750, Value(80_255), "750 serum counted by 80255 too");
        Reload();
        AssertEqual(750, Value(80_186), "serum progress survives BSON reload");
        ClaimGrantsExp(80_186, "SB 750 serum");
        long apBefore = inventory.Items.Single(item => item.Id == Inventory.ActionPoint).Count;
        inventory.Items.Single(item => item.Id == Inventory.ActionPoint).Count = 0;
        AssertEqual(1, Sweep(1), "unaffordable sweep rejected");
        inventory.Items.Single(item => item.Id == Inventory.ActionPoint).Count = apBefore;
        AssertEqual(750, Value(80_255), "rejected sweep credits no serum");
        AssertEqual(0, Sweep(25), "SB sweep 25 more (1500 total)");
        AssertEqual(1_500, Value(80_255), "1500 serum credited to 80255");
        ClaimGrantsExp(80_255, "SB 1500 serum");

        });

        // ---- #5 Operation Guardians participation (44001) = accepted card plays ----
        Section("guardians", () =>
        {
        int activeWeeks = Enumerable.Range(0, 7).FirstOrDefault(weeks =>
        {
            shiftSeason(weeks);
            return (bool)isActive.Invoke(null, [session, 80_006])!;
        }, -1);
        if (!(bool)isActive.Invoke(null, [session, 80_006])!)
            throw new InvalidDataException("No Passport round exposes task 80006.");
        _ = activeWeeks;
        Drain();
        MaintainerActionModule.BuildNotify(session);
        Drain();
        AssertEqual(0, Value(80_006), "no Guardians progress before a card play");
        int PlayCard(int card)
        {
            int id = packetId++;
            InvokeRegisteredRequestHandler(nameof(MaintainerActionPlayCardRequest), session, id, new MaintainerActionPlayCardRequest { Card = card });
            MaintainerActionPlayCardResponse response = (MaintainerActionPlayCardResponse)ReadResponsePayload(
                harness, id, nameof(MaintainerActionPlayCardResponse), "Guardians play card", typeof(MaintainerActionPlayCardResponse), 64);
            Drain();
            return response.Code;
        }
        AssertEqual(0, PlayCard(session.player.MaintainerAction.Cards[0]), "first card play accepted");
        AssertEqual(1, Value(80_006), "accepted card play counts as participation");
        AssertEqual(true, PlayCard(99) != 0, "card not in hand rejected");
        AssertEqual(1, Value(80_006), "rejected card play is not counted");
        session.player.MaintainerAction.Triggered = true;
        AssertEqual(0, PlayCard(session.player.MaintainerAction.Cards[0]), "second card play accepted");
        AssertEqual(2, Value(80_006), "second accepted play counted");
        Reload();
        AssertEqual(2, Value(80_006), "Guardians participation survives BSON reload");
        session.player.MissionProgress.ConditionCounters[80_006] = 19;
        session.player.MaintainerAction.Triggered = true;
        AssertEqual(0, PlayCard(session.player.MaintainerAction.Cards[0]), "twentieth card play accepted");
        AssertEqual(20, Value(80_006), "20 plays reach the target");
        ClaimGrantsExp(80_006, "Guardians 20 plays");

        });

        // ---- #4 again: next season rollover re-opens everything claimed above ----
        Section("second-rollover", () =>
        {
        player.Passport.ActivityId = previousSeasonId;
        Login();
        AssertEqual(false, player.MissionProgress.ClaimedTaskIds.Any(id => id is 80_185 or 80_186 or 80_254 or 80_255 or 80_006), "second rollover clears all Passport task claims");
        AssertEqual(1, Value(80_185), "login days restart at 1 on rollover");
        AssertEqual(0, Value(80_255), "serum restarts at 0 on rollover");
        AssertEqual(0, Value(80_006), "Guardians restarts at 0 on rollover");
        });
        if (failures.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }
}
