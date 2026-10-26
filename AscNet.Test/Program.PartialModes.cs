using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.config;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.miniactivity.musicgame.concertpreheating;
using AscNet.Table.V2.share.robot;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidatePartialModesCompatibility()
    {
        using MongoCollectionOverride mongo = MongoCollectionOverride.InstallForBossInshotCompatibility(out _, out _);
        // The 4.8 schedule has no window for the BossInshot TimeId; open it in the in-memory calendar for this test only.
        BossInshotActivityTable inshot = TableReaderV2.Parse<BossInshotActivityTable>()
            .Single(row => row.TimeId is > 0 && row.TowerConditions is > 0 && row.TowerBossIds.Count > 0);
        ActivityScheduleEntry[] calendar = (ActivityScheduleEntry[])ActivityScheduleService.All;
        ActivityScheduleEntry replaced = calendar[0];
        calendar[0] = new ActivityScheduleEntry(inshot.TimeId!.Value, 0, 32_503_680_000, "test-only:partial-modes");
        try { ValidatePartialModesBossInshot(); }
        finally { calendar[0] = replaced; }
        ValidatePartialModesConcert();

        const long playerId = 99_496;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, sessionId: "partial-modes-small");
        int id = 99_496_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }

        // Luosaita: unknown move id; the only authored move's section is never served -> client's CharacterInfoNotExist.
        AssertEqual(20315029, Call<MainLineLuosaitaCharacterMoveResponse>("MainLineLuosaitaCharacterMove", new MainLineLuosaitaCharacterMoveRequest { SectionId = 1, MoveId = int.MaxValue }).Code, "luosaita unknown move");
        MainLineLuosaitaCharacterMoveResponse move = Call<MainLineLuosaitaCharacterMoveResponse>("MainLineLuosaitaCharacterMove", new MainLineLuosaitaCharacterMoveRequest { SectionId = 4, MoveId = 1 });
        AssertEqual(20315030, move.Code, "luosaita unserved section fails closed");
        AssertEqual(true, move.SectionInfo is null, "luosaita rejection carries no section payload");

        // Maverick3: mode unsupported -> every exit fails closed with ActivityNotOpen.
        foreach (int stageId in new[] { 1, 2 })
            AssertEqual(20240001, Call<Maverick3ExitStageResponse>("Maverick3ExitStage", new Maverick3ExitStageRequest { StageId = stageId }).Code, $"maverick3 exit {stageId}");
    }

    private static void ValidatePartialModesConcert()
    {
        const long playerId = 99_497;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, sessionId: "partial-modes-concert");
        ConcertPreHeatingActivityTable activity = TableReaderV2.Parse<ConcertPreHeatingActivityTable>().Single(row => row.TimeId > 0);
        Dictionary<int, ConcertPreHeatingStageTable> stages = TableReaderV2.Parse<ConcertPreHeatingStageTable>().ToDictionary(row => row.Id);
        ActivityScheduleService.TryGet(activity.TimeId, out ActivityScheduleEntry window);
        DateTimeOffset open = DateTimeOffset.FromUnixTimeSeconds(window.StartTime);
        int first = activity.StageIds[0];
        int late = activity.StageIds[^1];
        // Stage windows have no calendar row in 4.8 data (open with the activity); schedule one for `late` in-memory.
        DateTimeOffset lateOpen = open.AddDays(1);
        ActivityScheduleEntry[] calendar = (ActivityScheduleEntry[])ActivityScheduleService.All;
        ActivityScheduleEntry replaced = calendar[0];
        calendar[0] = new ActivityScheduleEntry((int)stages[late].TimeId!, lateOpen.ToUnixTimeSeconds(), window.EndTime, "test-only:partial-modes");
        try { ValidatePartialModesConcertSettles(); } finally { calendar[0] = replaced; }

        void ValidatePartialModesConcertSettles()
        {
        ConcertPreHeatingSettleResponse Settle(int stageId, DateTimeOffset now) =>
            Version47EventModule.SettleConcertPreHeating(harness.Session, stageId, now);

        AssertEqual(20426001, Settle(first, open.AddSeconds(-1)).Code, "concert settle outside activity window");
        AssertEqual(20426002, Settle(int.MaxValue, open).Code, "concert settle unknown stage");
        AssertEqual(20426004, Settle(late, open).Code, "concert settle stage window not yet open");
        AssertEqual(0, player.ConcertPreHeating.CompletedStageIds.Count, "rejected settles persist nothing");

        ConcertPreHeatingSettleResponse a = Settle(first, open);
        AssertEqual(0, a.Code, "concert settle stage A");
        AssertEqual(activity.Id, a.ActivityId, "concert settle activity id");
        AssertEqual(true, a.StageFinish.Select(f => f.StageId).SequenceEqual([first]), "concert settle finished list A");
        int counterA = harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(140200);
        AssertEqual(1, counterA, "concert first settle credits the 1-stage task");
        ConcertPreHeatingSettleResponse again = Settle(first, open);
        AssertEqual(true, again.StageFinish.Select(f => f.StageId).SequenceEqual([first]), "concert repeated settle keeps list");
        AssertEqual(1, harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(140200), "concert repeated settle credits nothing twice");

        AssertEqual(0, harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(140204), "stage-pinned task untouched by another stage");
        ConcertPreHeatingSettleResponse b = Settle(late, lateOpen);
        AssertEqual(0, b.Code, "concert settle stage B");
        AssertEqual(2, harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(140200), "concert second distinct stage advances counter");

        AssertEqual(1, harness.Session.player.MissionProgress.ConditionCounters.GetValueOrDefault(140204), "stage-pinned task credited by its own stage");
        player = BsonSerializer.Deserialize<Player>(player.ToBson());
        harness.Session.player = player;
        AssertEqual(true, Version47EventModule.BuildConcertNotify(player, open)!.ConcertPreHeatingDataDb.StageFinish.Select(f => f.StageId).Order()
            .SequenceEqual(new[] { first, late }.Order()), "concert finished stages survive reload");
        }
    }

    private static void ValidatePartialModesBossInshot()
    {
        const long playerId = 99_498;
        BossInshotActivityTable activity = TableReaderV2.Parse<BossInshotActivityTable>()
            .Single(row => row.TimeId is > 0 && row.TowerConditions is > 0 && row.TowerBossIds.Count > 0);
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, sessionId: "partial-modes-inshot");
        int id = 99_498_000;
        T Call<T>(string name, object request) where T : class
        {
            InvokeRegisteredRequestHandler(name + "Request", harness.Session, ++id, request);
            return ReadResponsePayload<T>(harness, id, name + "Response", name, maxPacketsToRead: 16);
        }
        BossInshotState State() => harness.Session.player.BossInshot!;
        void Reload() { player = BsonSerializer.Deserialize<Player>(player.ToBson()); harness.Session.player = player; }

        // ---- Talents ----
        Type bossModule = typeof(BossInshotData).Assembly.GetType("AscNet.GameServer.Handlers.BossInshotModule", throwOnError: true)!;
        Dictionary<int, BossInshotCharacterTable> characterRows = TableReaderV2.Parse<BossInshotCharacterTable>().ToDictionary(row => row.Id);
        Dictionary<int, RobotTable> robots = TableReaderV2.Parse<RobotTable>().ToDictionary(row => row.Id);
        List<BossInshotTalentTable> talents = TableReaderV2.Parse<BossInshotTalentTable>();
        Dictionary<int, int> characterIds = activity.CharacterIds
            .Where(cfg => characterRows.TryGetValue(cfg, out var row) && robots.TryGetValue(row.RobotId, out var robot) && robot.CharacterId > 0)
            .ToDictionary(cfg => robots[characterRows[cfg].RobotId].CharacterId);
        int Condition(BossInshotTalentTable t) => (int?)t.UnlockConditionId ?? 0;
        int slotMax = int.Parse(TableReaderV2.Parse<ConfigTable>().Single(row => row.Key == "BosssInshotTalentSlotMax").Value);
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        // Character with two ungated hand-worn talents (two distinct inputs) and one with a score-gated one.
        int freeCharacter = characterIds.Keys.First(c => talents.Count(t => t.CharacterId == c && t.TalentType == 2 && Condition(t) == 0) >= 2);
        BossInshotTalentTable[] free = talents.Where(t => t.CharacterId == freeCharacter && t.TalentType == 2 && Condition(t) == 0).Take(2).ToArray();
        // Score-gated talents belong to characters of other activities, so the gate is checked on the rule itself.
        BossInshotTalentTable gated = talents.First(t => Condition(t) > 0 && conditions[Condition(t)].Type == 15309);
        ConditionTable gate = conditions[Condition(gated)];
        BossInshotSelectTalentRequest Select(int characterId, params int[] ids) => new() { CharacterId = characterId, SelectTalentIds = ids.ToList() };
        int Code(BossInshotSelectTalentRequest request) => Call<BossInshotSelectTalentResponse>("BossInshotSelectTalent", request).Code;

        AssertEqual(20215010, Code(Select(int.MaxValue, free[0].Id)), "talent unknown character");
        AssertEqual(20215017, Code(Select(freeCharacter, Enumerable.Repeat(free[0].Id, slotMax + 1).ToArray())), "talent over slot max");
        AssertEqual(20215014, Code(Select(freeCharacter, int.MaxValue)), "talent unknown id");
        int otherCharacterTalent = talents.First(t => t.CharacterId != freeCharacter && characterIds.ContainsKey(t.CharacterId) && t.TalentType == 2 && Condition(t) == 0).Id;
        AssertEqual(20215014, Code(Select(freeCharacter, otherCharacterTalent)), "talent of another character");
        AssertEqual(20215014, Code(Select(freeCharacter, gated.Id)), "gated talent of another activity's character rejected");
        AssertEqual(0, State().Characters.Count(c => c.SelectTalentIds.Count > 0), "rejected selections persist nothing");
        AssertEqual(0, Code(Select(freeCharacter, free[0].Id)), "talent A selected");
        AssertEqual(0, Code(Select(freeCharacter, free[0].Id)), "talent A reselect idempotent");
        Reload();
        AssertEqual(true, State().Characters.Single(c => c.CharacterId == freeCharacter).SelectTalentIds.SequenceEqual([free[0].Id]), "talent A survives reload");
        AssertEqual(0, Code(Select(freeCharacter, free[1].Id)), "talent B replaces A");
        AssertEqual(true, State().Characters.Single(c => c.CharacterId == freeCharacter).SelectTalentIds.SequenceEqual([free[1].Id]), "talent B persisted");
        // Fight setup reads the persisted selection (+ default talent).
        List<int> Events() => ((IEnumerable<int>)bossModule.GetMethod("TalentFightEvents", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [State(), freeCharacter])!).ToList();
        int EventOf(BossInshotTalentTable t) => (int?)t.FightEventId ?? 0;
        AssertEqual(true, Events().Contains(EventOf(free[1])) && !Events().Contains(EventOf(free[0])), "fight events follow the selected talent");
        AssertEqual(0, Call<BossInshotSelectTalentResponse>("BossInshotSelectTalent", new BossInshotSelectTalentRequest { CharacterId = freeCharacter, SelectTalentIds = null }).Code, "nil list clears");
        AssertEqual(true, State().Characters.Single(c => c.CharacterId == freeCharacter).SelectTalentIds.All(t => t == 0), "cleared selection stored as empty slots");
        AssertEqual(false, Events().Contains(EventOf(free[1])), "cleared talent no longer applied");

        // Gate opens only through the character's accumulated score (condition 15309: [characterId, score]).
        MethodInfo unlocked = bossModule.GetMethod("TalentUnlocked", BindingFlags.NonPublic | BindingFlags.Static)!;
        BossInshotState probe = new();
        bool Unlocked() => (bool)unlocked.Invoke(null, [probe, gated])!;
        AssertEqual(false, Unlocked(), "gated talent locked without character data");
        probe.Characters.Add(new BossInshotCharacterState { CharacterId = gated.CharacterId, TotalScore = gate.Params[1] - 1 });
        AssertEqual(false, Unlocked(), "gated talent locked below threshold");
        probe.Characters[0].TotalScore = gate.Params[1];
        AssertEqual(true, Unlocked(), "gated talent unlocked at threshold");
        probe.Characters[0].CharacterId = gated.CharacterId + 1;
        AssertEqual(false, Unlocked(), "another character's score does not unlock");

        // ---- Skill practice ----
        HashSet<int> activitySkills = TableReaderV2.Parse<BossInshotBossTable>()
            .Where(b => activity.BossIds.Contains(b.Id)).SelectMany(b => b.SkillIds).ToHashSet();
        List<BossInshotSkillTable> practice = TableReaderV2.Parse<BossInshotSkillTable>()
            .Where(s => (int?)s.PracticeStageId is > 0 && (int?)s.FightEventId is > 0).ToList();
        BossInshotSkillTable[] skills = practice.Where(s => activitySkills.Contains(s.Id)).Take(2).ToArray();
        BossInshotSkillTable foreignSkill = practice.First(s => !activitySkills.Contains(s.Id));
        int SkillStage(BossInshotSkillTable s) => (int)s.PracticeStageId!;
        int SkillEvent(BossInshotSkillTable s) => (int)s.FightEventId!;
        int SkillCode(int stageId, int eventId) => Call<BossInshotSelectSkillResponse>("BossInshotSelectSkill", new BossInshotSelectSkillRequest { StageId = stageId, EventId = eventId }).Code;
        State().IsPassTeach = false;
        AssertEqual(20215009, SkillCode(SkillStage(skills[0]), SkillEvent(skills[0])), "skill before tutorial");
        State().IsPassTeach = true;
        AssertEqual(20215016, SkillCode(SkillStage(skills[0]), int.MaxValue), "skill unknown event");
        AssertEqual(20215016, SkillCode(int.MaxValue, SkillEvent(skills[0])), "skill wrong practice stage");
        AssertEqual(20215016, SkillCode(SkillStage(foreignSkill), SkillEvent(foreignSkill)), "skill of a boss outside the activity");
        AssertEqual(true, harness.Session.SelectedBossInshotSkill is null, "rejected skill selection stores nothing");
        foreach (BossInshotSkillTable skill in skills)
            AssertEqual(0, SkillCode(SkillStage(skill), SkillEvent(skill)), $"skill {skill.Id} selected");
        AssertEqual(true, harness.Session.SelectedBossInshotSkill == (SkillStage(skills[1]), SkillEvent(skills[1])), "latest skill selection wins");

        // PreFight of the practice stage applies and consumes the selected skill event.
        int robotId = characterRows[characterIds.First().Value].RobotId;
        PreFightRequest preFight = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)SkillStage(skills[1])), ChallengeCount = 1, CardIds = [0, 0, 0], RobotIds = [0, robotId, 0]
            }
        };
        InvokeRegisteredRequestHandler(nameof(PreFightRequest), harness.Session, ++id, preFight);
        PreFightResponse fight = ReadResponsePayload<PreFightResponse>(harness, id, nameof(PreFightResponse), "BossInshot practice prefight", maxPacketsToRead: 16);
        AssertEqual(0, fight.Code, "practice prefight accepted");
        AssertEqual(true, fight.FightData.EventIds.Select(e => (int)e).Contains(SkillEvent(skills[1])), "practice fight carries the selected skill event");
        AssertEqual(true, harness.Session.SelectedBossInshotSkill is null, "selected skill consumed by prefight");
    }
}
