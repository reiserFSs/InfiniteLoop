using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.fuben.bosssingle;
using MessagePack;
using MongoDB.Bson;
using System.Globalization;

namespace AscNet.Test;

internal partial class Program
{
    // --ppc-command-only: /ppc intensive boss/weakness selection (listing, locked/unlocked state, push without
    // relogin, fight events, rank total) and /ppc attempts. Uses in-memory Mongo collections.
    private static void ValidateBossSingleIntensiveCommands()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
            out _,
            out _);
        const long playerId = 46_790;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.SimulatedBattlefield = new();
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "boss-intensive-commands");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        BossModule.PrepareLogin(harness.Session);
        Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");

        List<BossSingleGradeTable> grades = TableReaderV2.Parse<BossSingleGradeTable>();
        int currentAfreshId = grades.Max(row => row.AfreshId);
        BossSingleChallengeGradeTable challengeGrade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
        BossSingleGroupTable challengeGroup = TableReaderV2.Parse<BossSingleGroupTable>()
            .Single(row => row.Id == challengeGrade.BossGroupId);
        List<BossSingleSectionTable> sections = TableReaderV2.Parse<BossSingleSectionTable>();
        List<BossSingleSectionTable> intensiveSections = challengeGroup.SectionId
            .Select(sectionId => sections.Single(row => row.SectionId == sectionId && row.AfreshId == currentAfreshId))
            .ToList();
        Dictionary<int, BossSingleStageTable> stages = TableReaderV2.Parse<BossSingleStageTable>().ToDictionary(row => row.StageId);
        Dictionary<int, BossSingleChallengeFeatureTable> features = TableReaderV2.Parse<BossSingleChallengeFeatureTable>()
            .ToDictionary(row => row.Id);
        List<BossSingleChallengeFeatureGroupTable> usableGroups = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>()
            .Where(row => row.BuffGroupIds.Count > 0 && row.BuffGroupIds.Count == row.FeatureIds.Count)
            .ToList();
        BossSingleChallengeFeatureGroupTable unusableGroup = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>()
            .First(row => row.BuffGroupIds.Count == 0);
        string BossName(BossSingleSectionTable section) => stages[section.StageId[0]].BossName;
        string GroupNames(BossSingleChallengeFeatureGroupTable group) =>
            string.Join(" / ", group.FeatureIds.Select(id => features[id].Name));
        AssertEqual(true, intensiveSections.Count > 2 && intensiveSections.All(section => !string.IsNullOrWhiteSpace(BossName(section))),
            "Intensive sections resolve boss names from BossSingleStage.BossName");
        AssertEqual(true, features.Values.All(row => !string.IsNullOrWhiteSpace(row.Name)),
            "BossSingleChallengeFeature rows carry their client names");

        string Run(params string[] args)
        {
            AscNet.GameServer.Commands.PpcCommand command = new(harness.Session, args);
            try { command.Execute(); }
            catch (AscNet.GameServer.Commands.CommandMessageCallbackException ex) { return ex.Message; }
            throw new InvalidDataException($"/ppc {string.Join(' ', args)}: expected command feedback.");
        }

        NotifyFubenBossSingleData ReadBossPush(string name, out HashSet<int> pushedStageIds)
        {
            pushedStageIds = [];
            for (int index = 0; index < 16; index++)
            {
                Packet packet = harness.ReadPacket($"{name} packet {index + 1}");
                AssertEqual(Packet.ContentType.Push, packet.Type, $"{name} packet type");
                Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                if (push.Name == nameof(NotifyStageData))
                {
                    foreach (StageDatum datum in MessagePackSerializer.Deserialize<NotifyStageData>(push.Content).StageList)
                        pushedStageIds.Add(checked((int)datum.StageId));
                    continue;
                }
                if (push.Name == nameof(NotifyFubenBossSingleData))
                    return MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(push.Content);
            }
            throw new InvalidDataException($"{name}: expected {nameof(NotifyFubenBossSingleData)} push.");
        }

        void DrainAvailable(string name)
        {
            while (harness.TryReadAvailablePacket(name, out _)) { }
        }

        SimulatedBattlefieldState state = player.SimulatedBattlefield;
        state.BossLevelType = grades
            .Where(row => row.AfreshId == currentAfreshId && row.GradeType >= challengeGrade.NeedGradeType)
            .OrderBy(row => row.GradeType)
            .First()
            .LevelType;
        state.BossStageRecords = [];
        DrainAvailable("intensive setup");

        // Listing shows only the challenge group's sections, with their table boss names.
        string bossList = Run("intensive", "bosses");
        foreach (BossSingleSectionTable section in intensiveSections)
            AssertEqual(true, bossList.Contains($"{section.SectionId}: {BossName(section)}", StringComparison.Ordinal),
                $"/ppc intensive bosses lists {section.SectionId} {BossName(section)}");
        AssertEqual(false, bossList.Contains(" 101:", StringComparison.Ordinal),
            "/ppc intensive bosses does not list normal-tier sections");
        string weaknessList = Run("intensive", "weaknesses");
        foreach (BossSingleChallengeFeatureGroupTable group in usableGroups)
            AssertEqual(true, weaknessList.Contains($"{group.Id}: {GroupNames(group)}", StringComparison.Ordinal),
                $"/ppc intensive weaknesses lists group {group.Id} with feature names");
        AssertEqual(false, weaknessList.Contains($" {unusableGroup.Id}: ", StringComparison.Ordinal),
            "/ppc intensive weaknesses omits groups without client BuffGroup pairings");
        DrainAvailable("intensive listing");

        // Weekly default with the score gate open, computed on a detached copy.
        Player defaultProbe = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(player.ToBsonDocument());
        defaultProbe.SimulatedBattlefield.BossStageRecords =
        [
            new BossSingleStageRecordState { StageId = stages.Keys.First(), Score = challengeGrade.NeedScore, MaxScore = challengeGrade.NeedScore }
        ];
        NotifyFubenBossSingleData defaultLogin = BossModule.BuildLoginData(defaultProbe, null);
        int defaultSectionTableId = defaultLogin.FubenBossSingleData.ChallengeSectionId;
        int defaultFeatureGroupId = defaultLogin.FubenBossSingleData.ChallengeFeatureGroupId;
        AssertEqual(true, intensiveSections.Any(section => section.Id == defaultSectionTableId),
            "Intensive default is one of the challenge group's sections");
        BossSingleSectionTable firstChoice = intensiveSections.First(section => section.Id != defaultSectionTableId);
        BossSingleSectionTable secondChoice = intensiveSections.Last(section => section.Id != defaultSectionTableId && section.Id != firstChoice.Id);
        BossSingleChallengeFeatureGroupTable firstGroup = usableGroups.First(group => group.Id != defaultFeatureGroupId);
        BossSingleChallengeFeatureGroupTable secondGroup = usableGroups.Last(group => group.Id != defaultFeatureGroupId && group.Id != firstGroup.Id);

        // Locked: the selection is stored and the client still receives no challenge data.
        string lockedBoss = Run(["intensive", "boss", .. BossName(firstChoice).ToLowerInvariant().Split(' ')]);
        AssertEqual(true, lockedBoss.Contains($"set to {BossName(firstChoice)} (SectionId {firstChoice.SectionId})", StringComparison.Ordinal)
            && lockedBoss.Contains("locked", StringComparison.Ordinal),
            "/ppc intensive boss <name> while locked confirms and reports the score gate");
        AssertEqual(firstChoice.Id, state.BossChallengeSelectedSection, "/ppc intensive boss stores the section row id");
        NotifyFubenBossSingleData lockedPush = ReadBossPush("locked boss selection", out _);
        AssertEqual(0, lockedPush.FubenBossSingleData.ChallengeSectionId, "locked selection is not exposed to the client");
        DrainAvailable("locked boss selection");
        _ = Run("intensive", "weakness", firstGroup.Id.ToString(CultureInfo.InvariantCulture));
        AssertEqual(firstGroup.Id, state.BossChallengeSelectedFeatureGroup, "/ppc intensive weakness stores the group");
        _ = ReadBossPush("locked weakness selection", out _);
        DrainAvailable("locked weakness selection");

        // Invalid inputs leave the selection untouched.
        string notIntensive = Run("intensive", "boss", "101");
        string noMatch = Run("intensive", "boss", "zzzz");
        string badGroup = Run("intensive", "weakness", unusableGroup.Id.ToString(CultureInfo.InvariantCulture));
        AssertEqual(true, notIntensive.Contains("not an Intensive Battle boss", StringComparison.Ordinal)
            && noMatch.Contains("No Intensive Battle boss matches", StringComparison.Ordinal)
            && badGroup.Contains("not usable", StringComparison.Ordinal),
            "/ppc intensive rejects normal sections, unknown names and unpaired weakness groups");
        AssertEqual(firstChoice.Id, state.BossChallengeSelectedSection, "rejected boss input keeps the selection");
        AssertEqual(firstGroup.Id, state.BossChallengeSelectedFeatureGroup, "rejected weakness input keeps the selection");
        DrainAvailable("invalid intensive input");

        // Unlock: the stored selection is what the client gets.
        state.BossStageRecords =
        [
            new BossSingleStageRecordState { StageId = stages.Keys.First(), Score = challengeGrade.NeedScore, MaxScore = challengeGrade.NeedScore }
        ];
        NotifyFubenBossSingleData unlockedLogin = BossModule.BuildLoginData(player, null);
        AssertEqual(challengeGrade.LevelType, unlockedLogin.FubenBossSingleData.ChallengeLevelType, "unlocked Intensive level");
        AssertEqual(firstChoice.Id, unlockedLogin.FubenBossSingleData.ChallengeSectionId, "unlocked Intensive uses the selected boss");
        AssertEqual(firstGroup.Id, unlockedLogin.FubenBossSingleData.ChallengeFeatureGroupId, "unlocked Intensive uses the selected weakness");
        string status = Run("intensive");
        AssertEqual(true, status.Contains($"Intensive boss: {BossName(firstChoice)}", StringComparison.Ordinal)
            && status.Contains(features[firstGroup.FeatureIds[0]].Desc, StringComparison.Ordinal)
            && status.Contains("unlocked", StringComparison.Ordinal),
            "/ppc intensive shows boss, weakness descriptions and unlock state");
        DrainAvailable("intensive status");

        // A second, distinct boss/weakness by SectionId is pushed immediately with its stages hydrated.
        _ = Run("intensive", "boss", secondChoice.SectionId.ToString(CultureInfo.InvariantCulture));
        NotifyFubenBossSingleData secondBossPush = ReadBossPush("second boss selection", out HashSet<int> pushedStages);
        AssertEqual(secondChoice.Id, secondBossPush.FubenBossSingleData.ChallengeSectionId, "boss switch is pushed without relogin");
        AssertEqual(firstGroup.Id, secondBossPush.FubenBossSingleData.ChallengeFeatureGroupId, "boss switch keeps the selected weakness");
        AssertEqual(true, secondChoice.StageId.Where(id => id > 0).All(id => harness.Session.stage.Stages.ContainsKey((uint)id)),
            "boss switch hydrates the new section's StageDatum rows");
        DrainAvailable("second boss selection");
        _ = Run("intensive", "weakness", secondGroup.Id.ToString(CultureInfo.InvariantCulture));
        NotifyFubenBossSingleData secondGroupPush = ReadBossPush("second weakness selection", out _);
        AssertEqual(secondGroup.Id, secondGroupPush.FubenBossSingleData.ChallengeFeatureGroupId, "weakness switch is pushed without relogin");
        DrainAvailable("second weakness selection");

        // The fight applies the selected weakness's feature event for the chosen stage.
        int fightPacket = 46_791;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            fightPacket,
            new PreFightRequest
            {
                PreFightData = new PreFightRequest.PreFightRequestPreFightData
                {
                    StageId = checked((uint)secondChoice.StageId[0]),
                    ChallengeCount = 1,
                    CardIds = [1],
                    RobotIds = [],
                    BossSingleStageType = 3,
                    BossSingleChallengeBuffGroup = secondGroup.BuffGroupIds[0]
                }
            });
        PreFightResponse fight = ReadResponsePayload<PreFightResponse>(
            harness, fightPacket, nameof(PreFightResponse), "selected Intensive PreFight");
        AssertEqual(0, fight.Code, "selected Intensive boss stage is fightable");
        int expectedEventId = features[secondGroup.FeatureIds[0]].FightEventIds;
        AssertEqual(true, fight.FightData!.EventIds.Any(id => Convert.ToInt32((object)id, CultureInfo.InvariantCulture) == expectedEventId),
            "selected weakness feature event is applied to the fight");
        harness.Session.fight = null;
        harness.Session.PendingBossSingleScore = null;
        DrainAvailable("selected Intensive PreFight");

        // Rank total only counts the selected boss's records.
        state.BossChallengeHistory =
        [
            new BossSingleChallengeHistoryRecordState { StageId = secondChoice.StageId[0], Score = 300 },
            new BossSingleChallengeHistoryRecordState { StageId = firstChoice.StageId[0], Score = 5_000 },
            new BossSingleChallengeHistoryRecordState { StageId = firstChoice.StageId[1], Score = 4_000 }
        ];
        AssertEqual(300, InvokePrivateStaticWithArgs<int>(bossModule, "ChallengeRankTotal", [state, challengeGrade.LevelType]),
            "Intensive rank total ignores records of a previously selected boss");
        state.BossChallengeSelectedSection = firstChoice.Id;
        AssertEqual(9_000, InvokePrivateStaticWithArgs<int>(bossModule, "ChallengeRankTotal", [state, challengeGrade.LevelType]),
            "Intensive rank total follows the selected boss");
        state.BossChallengeHistory = [];

        // Reset returns to the weekly default.
        string reset = Run("intensive", "reset");
        AssertEqual(true, reset.Contains("weekly default", StringComparison.Ordinal), "/ppc intensive reset confirms");
        NotifyFubenBossSingleData resetPush = ReadBossPush("intensive reset", out _);
        AssertEqual(defaultSectionTableId, resetPush.FubenBossSingleData.ChallengeSectionId, "reset restores the weekly boss");
        AssertEqual(defaultFeatureGroupId, resetPush.FubenBossSingleData.ChallengeFeatureGroupId, "reset restores the weekly weakness");
        AssertEqual(true, Run("intensive", "boss").Contains("Usage: /ppc intensive", StringComparison.Ordinal),
            "/ppc intensive boss without a query shows usage");
    }

    private static void ValidatePpcAttemptCommands()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(out _, out _);
        const long playerId = 46_792;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.SimulatedBattlefield = new();
        using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(playerId), player, sessionId: "ppc-attempt-commands");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        BossModule.PrepareLogin(harness.Session);
        string Run(params string[] args)
        {
            AscNet.GameServer.Commands.PpcCommand command = new(harness.Session, args);
            try { command.Execute(); }
            catch (AscNet.GameServer.Commands.CommandMessageCallbackException ex) { return ex.Message; }
            return string.Empty;
        }
        void Drain() { while (harness.TryReadAvailablePacket("ppc attempts", out _)) { } }

        List<BossSingleGradeTable> grades = TableReaderV2.Parse<BossSingleGradeTable>();
        int currentAfreshId = grades.Max(row => row.AfreshId);
        int[] levelTypes = grades
            .Where(row => row.AfreshId == currentAfreshId && player.PlayerData.Level >= row.MinPlayerLevel && player.PlayerData.Level <= row.MaxPlayerLevel)
            .Select(row => row.LevelType).Distinct().Order().ToArray();
        AssertEqual(true, levelTypes.Length > 1, "PPC has several tiers for the test Commandant level");
        AssertEqual(true, Run("tiers").Contains(string.Join(", ", levelTypes), StringComparison.Ordinal), "/ppc tiers lists the valid LevelTypes");

        // /ppc tier ignores the PreGradeType gate and swaps the boss list; two distinct tiers give two distinct lists.
        int lowTier = levelTypes[0], highTier = levelTypes[^1];
        _ = Run("tier", highTier.ToString(CultureInfo.InvariantCulture));
        AssertEqual(highTier, player.SimulatedBattlefield.BossLevelType, "/ppc tier selects the highest tier");
        List<int> highBosses = player.SimulatedBattlefield.BossList.ToList();
        Drain();
        _ = Run("tier", lowTier.ToString(CultureInfo.InvariantCulture));
        AssertEqual(lowTier, player.SimulatedBattlefield.BossLevelType, "/ppc tier selects the lowest tier");
        AssertEqual(false, highBosses.SequenceEqual(player.SimulatedBattlefield.BossList), "/ppc tier changes the boss list");
        Drain();
        AssertEqual(true, Run("tier", "999").Contains("not valid", StringComparison.Ordinal), "/ppc tier rejects an unknown LevelType");
        AssertEqual(lowTier, player.SimulatedBattlefield.BossLevelType, "rejected /ppc tier keeps the tier");

        // /ppc attempts <n> and /ppc attempts reset set today's used attempts.
        BossSingleGradeTable grade = grades.First(row => row.AfreshId == currentAfreshId && row.LevelType == lowTier);
        _ = Run("attempts", "2");
        AssertEqual(2, player.SimulatedBattlefield.BossChallengeCount, "/ppc attempts 2");
        Drain();
        _ = Run("attempts", "reset");
        AssertEqual(0, player.SimulatedBattlefield.BossChallengeCount, "/ppc attempts reset");
        Drain();
        AssertEqual(true, Run("attempts", (grade.WeekChallengeCount + 1).ToString(CultureInfo.InvariantCulture)).Contains("must be between", StringComparison.Ordinal),
            "/ppc attempts rejects more than the weekly maximum");
        Console.WriteLine("PPC command compatibility passed.");
    }
}
