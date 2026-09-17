using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.condition;
using AscNet.Table.V2.share.fuben.bossinshot;
using AscNet.Table.V2.share.fuben.bosssingle;
using AscNet.Table.V2.share.robot;
using MessagePack;
using MongoDB.Bson;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateBossInshotClosure()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossInshotCompatibility(
            out RecordingMongoCollectionProxy<Player> playerCollection,
            out RecordingMongoCollectionProxy<BossInshotRankEntry> rankCollection);

        BossInshotActivityTable activity = TableReaderV2.Parse<BossInshotActivityTable>()
            .Single(row => row.TimeId is > 0 && row.TowerConditions is > 0 && row.TowerBossIds.Count > 0);
        Dictionary<int, BossInshotCharacterTable> characterRows = TableReaderV2.Parse<BossInshotCharacterTable>()
            .ToDictionary(row => row.Id);
        Dictionary<int, RobotTable> robotRows = TableReaderV2.Parse<RobotTable>()
            .ToDictionary(row => row.Id);
        int characterConfigId = activity.CharacterIds.First(configId =>
            characterRows.TryGetValue(configId, out BossInshotCharacterTable? character)
            && robotRows.TryGetValue(character.RobotId, out RobotTable? robot)
            && robot.CharacterId > 0);
        int characterId = robotRows[characterRows[characterConfigId].RobotId].CharacterId;
        Type bossModule = typeof(BossInshotData).Assembly.GetType(
            "AscNet.GameServer.Handlers.BossInshotModule",
            throwOnError: true)!;

        ValidateBossInshotEarlyLineupRejection(
            activity,
            characterRows[characterConfigId].RobotId);
        ValidateBossInshotBoundedRankQuery(
            activity,
            characterConfigId,
            characterId,
            rankCollection);
        ValidateBossInshotDuplicateNormalizationAndStableDraw(
            activity,
            characterId,
            bossModule);
        ValidateBossInshotAtomicTowerMutations(
            activity,
            playerCollection,
            bossModule);
        ValidateBossInshotPassedTowerReplay(
            activity,
            characterConfigId,
            characterId,
            bossModule);
        ValidateBossInshotHandlerRetry(
            activity,
            characterConfigId,
            characterId,
            playerCollection);
    }

    private static void ValidateBossSingleIntensiveStageHydration()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
            out _,
            out _);
        const long playerId = 46_780;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.SimulatedBattlefield = new();
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-intensive-hydration");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
        InvokePrivateStaticWithArgs<object?>(
            bossModule, "PrepareLogin", [harness.Session]);

        BossSingleChallengeGradeTable challengeGrade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
        BossSingleGroupTable challengeGroup = TableReaderV2.Parse<BossSingleGroupTable>()
            .Single(row => row.Id == challengeGrade.BossGroupId);
        BossSingleChallengeFeatureGroupTable featureGroup = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>()
            .First(row => row.BuffGroupIds.Count > 0);
        BossSingleSectionTable section = TableReaderV2.Parse<BossSingleSectionTable>()
            .First(row => row.AfreshId == TableReaderV2.Parse<BossSingleGradeTable>().Max(grade => grade.AfreshId)
                && challengeGroup.SectionId.Contains(row.SectionId));
        player.SimulatedBattlefield.BossLevelType = TableReaderV2.Parse<BossSingleGradeTable>()
            .Where(row => row.GradeType >= challengeGrade.NeedGradeType)
            .OrderBy(row => row.GradeType)
            .First()
            .LevelType;
        player.SimulatedBattlefield.BossTotalScore = challengeGrade.NeedScore;
        player.SimulatedBattlefield.BossStageRecords =
        [
            new BossSingleStageRecordState
            {
                StageId = TableReaderV2.Parse<BossSingleStageTable>().First().StageId,
                Score = challengeGrade.NeedScore,
                MaxScore = challengeGrade.NeedScore
            }
        ];
        player.SimulatedBattlefield.BossChallengeSelectedSection = section.Id;
        player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup = featureGroup.Id;
        InvokePrivateStaticWithArgs<object?>(
            bossModule, "PrepareLogin", [harness.Session]);
        section = TableReaderV2.Parse<BossSingleSectionTable>().Single(row =>
            row.Id == player.SimulatedBattlefield.BossChallengeSelectedSection);

        int[] stageIds = section.StageId.Take(2).ToArray();
        AssertEqual(2, stageIds.Length, "Intensive challenge section has two independently selected stages");
        AssertEqual(true, stageIds.All(stageId => harness.Session.stage.Stages.ContainsKey((uint)stageId)),
            "PrepareLogin hydrates current Intensive challenge stages");

        int packetId = 46_781;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            packetId,
            new PreFightRequest
            {
                PreFightData = new PreFightRequest.PreFightRequestPreFightData
                {
                    StageId = checked((uint)stageIds[0]),
                    ChallengeCount = 1,
                    CardIds = [1],
                    RobotIds = [],
                    BossSingleStageType = 3,
                    BossSingleChallengeBuffGroup = featureGroup.BuffGroupIds[0]
                }
            });
        PreFightResponse response = ReadResponsePayload<PreFightResponse>(
            harness, packetId, nameof(PreFightResponse), "Intensive challenge PreFight");
        AssertEqual(0, response.Code, "registered Intensive challenge PreFight is actionable");
    }

    private static void ValidateBossSingleCycleStageScoreSync()
    {
        using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
            out _,
            out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageCollection);
        const long playerId = 46_800;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-cycle-stage-score-sync");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
        InvokePrivateStaticWithArgs<object?>(bossModule, "PrepareLogin", [harness.Session]);

        List<BossSingleGradeTable> grades = TableReaderV2.Parse<BossSingleGradeTable>();
        int currentAfreshId = grades.Max(row => row.AfreshId);
        SimulatedBattlefieldState state = player.SimulatedBattlefield;
        AssertEqual(true, state.BossList.Count > 0,
            "fresh level-80 Pain Cage login selects one grade of sections");
        BossSingleSectionTable section = TableReaderV2.Parse<BossSingleSectionTable>().Single(row =>
            row.SectionId == state.BossList[0] && row.AfreshId == currentAfreshId);
        int knightStageId = section.StageId[0];
        int chaosStageId = section.StageId[1];
        AssertEqual(true,
            harness.Session.stage.Stages.ContainsKey(knightStageId)
            && harness.Session.stage.Stages.ContainsKey(chaosStageId),
            "PrepareLogin hydrates the selected section stages");

        List<Packet.Push> ReadPushes(
            int packetId,
            string responseName,
            string name,
            out Packet.Response response)
        {
            List<Packet.Push> pushes = [];
            for (int index = 0; index < 16; index++)
            {
                Packet packet = harness.ReadPacket($"{name} packet {index + 1}");
                if (packet.Type == Packet.ContentType.Push)
                {
                    pushes.Add(MessagePackSerializer.Deserialize<Packet.Push>(packet.Content));
                    continue;
                }

                response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                AssertEqual(packetId, response.Id, $"{name} response id");
                AssertEqual(responseName, response.Name, $"{name} response name");
                return pushes;
            }

            throw new InvalidDataException($"{name}: expected {responseName} response.");
        }

        List<Packet.Push> ReadRankInfo(int packetId, string name)
        {
            InvokeRegisteredRequestHandler(
                nameof(BossSingleRankInfoRequest),
                harness.Session,
                packetId,
                new BossSingleRankInfoRequest { SectionId = 0 });
            List<Packet.Push> pushes = ReadPushes(
                packetId, nameof(BossSingleRankInfoResponse), name, out _);
            return pushes;
        }

        NotifyStageData SingleStagePush(List<Packet.Push> pushes, string name)
        {
            AssertEqual(true,
                pushes.Select(push => push.Name).SequenceEqual([nameof(NotifyStageData)]),
                $"{name} pushes only the resynced stage data");
            return MessagePackSerializer.Deserialize<NotifyStageData>(pushes[0].Content);
        }

        harness.Session.stage.Stages[knightStageId] = new StageDatum
        {
            StageId = knightStageId,
            Passed = true,
            Score = 88_200
        };
        int savesBeforeHeal = stageCollection.ReplaceOneCalls;
        NotifyStageData healPush = SingleStagePush(
            ReadRankInfo(46_801, "Pain Cage stale stage score sync"),
            "Pain Cage stale stage score sync");
        AssertEqual(0L, harness.Session.stage.Stages[knightStageId].Score,
            "Pain Cage stale period stage datum heals to the current period");
        AssertEqual(savesBeforeHeal + 1, stageCollection.ReplaceOneCalls,
            "Pain Cage stale stage datum persists once");
        StageDatum healedStage = healPush.StageList.Single();
        AssertEqual(checked((long)knightStageId), healedStage.StageId,
            "Pain Cage stale sync push targets the stale stage");
        AssertEqual(0L, healedStage.Score, "Pain Cage stale sync push clears the stale score");

        state.BossStageRecords.Add(new BossSingleStageRecordState
        {
            StageId = chaosStageId,
            Score = 1_000,
            MaxScore = 87_300
        });
        harness.Session.stage.Stages[chaosStageId].Score = 1;
        int savesBeforeBest = stageCollection.ReplaceOneCalls;
        NotifyStageData bestPush = SingleStagePush(
            ReadRankInfo(46_802, "Pain Cage live stage score sync"),
            "Pain Cage live stage score sync");
        AssertEqual(87_300L, harness.Session.stage.Stages[chaosStageId].Score,
            "Pain Cage live stage datum follows the record best score");
        AssertEqual(savesBeforeBest + 1, stageCollection.ReplaceOneCalls,
            "Pain Cage live stage datum persists once");
        AssertEqual(87_300L, bestPush.StageList.Single().Score,
            "Pain Cage live sync push carries the record best score");

        // The codex "Current Threats" list (stageType 4, IsBestiaryCfg != 0, sections 2020..2044) uses the same
        // stage ids as the current Ultimate rotation. Its best is the value the client's settlement compares against
        // (GetMyTotalHistory reads the generic datum for non-Trial modes), so the datum carries the mode best while the
        // period data (records, totals) stays untouched - the *cards* are isolated by the projection instead.
        List<BossSingleSectionTable> sectionRows = TableReaderV2.Parse<BossSingleSectionTable>();
        int currentThreatsStageId = TableReaderV2.Parse<BossSingleTrialGradeTable>()
            .Where(catalog => catalog.IsBestiaryCfg != 0)
            .SelectMany(catalog => catalog.SectionId.Where(sectionId => sectionId > 0))
            .Distinct()
            .SelectMany(sectionId => sectionRows
                .Where(row => row.SectionId == sectionId && row.AfreshId == currentAfreshId)
                .SelectMany(row => row.StageId))
            .First(stageId => harness.Session.stage.Stages.ContainsKey(stageId));
        int cycleBestBeforeCodex = state.BossTotalScore;
        int cycleCurrentBeforeCodex = state.BossCurrentTotalScore;
        state.BossBestiaryScores[currentThreatsStageId] = 91_200;
        int savesBeforeCurrentThreats = stageCollection.ReplaceOneCalls;
        NotifyStageData codexHistoryPush = SingleStagePush(
            ReadRankInfo(46_805, "Pain Cage codex settlement history"),
            "Pain Cage codex settlement history");
        AssertEqual(91_200L, harness.Session.stage.Stages[currentThreatsStageId].Score,
            "Pain Cage codex best is the stage datum the settlement reads");
        AssertEqual(91_200L, codexHistoryPush.StageList.Single().Score,
            "Pain Cage codex best reaches the client as its settlement history");
        AssertEqual(savesBeforeCurrentThreats + 1, stageCollection.ReplaceOneCalls,
            "Pain Cage codex best datum persists once");
        List<Dictionary<string, object>> currentThreatsEntries = InvokePrivateStaticWithArgs<NotifyFubenBossSingleData>(
                bossModule, "BuildLoginData", [player, null])
            .FubenBossSingleData.BestiraryStageInfoList
            .Select(entry => (Dictionary<string, object>)entry!)
            .ToList();
        AssertEqual(true,
            currentThreatsEntries.Any(entry =>
                (int)entry["StageId"] == currentThreatsStageId && (int)entry["Score"] == 91_200),
            "Pain Cage Current Threats score is reported through the codex list");

        // The review's defect, server side: a rotation request must not blank the codex best out of the datum the
        // settlement reads, and it must raise a datum that sits below it. Any codex-only stage whose id is not part of
        // the current rotation is covered by the same rule.
        harness.Session.stage.Stages[currentThreatsStageId].Score = 0;
        int savesBeforeCodexRaise = stageCollection.ReplaceOneCalls;
        NotifyStageData codexRaisePush = SingleStagePush(
            ReadRankInfo(46_806, "Pain Cage rotation request keeps the codex best"),
            "Pain Cage rotation request keeps the codex best");
        AssertEqual(91_200L, harness.Session.stage.Stages[currentThreatsStageId].Score,
            "Pain Cage rotation request raises the stage datum to the codex best");
        AssertEqual(savesBeforeCodexRaise + 1, stageCollection.ReplaceOneCalls,
            "Pain Cage codex best raise persists once");
        AssertEqual(91_200L, codexRaisePush.StageList.Single().Score,
            "Pain Cage codex best raise pushes the mode best");
        int savesBeforeCodexHold = stageCollection.ReplaceOneCalls;
        AssertEqual(0, ReadRankInfo(46_807, "Pain Cage codex best steady state").Count,
            "Pain Cage codex best datum is stable across rotation requests");
        AssertEqual(savesBeforeCodexHold, stageCollection.ReplaceOneCalls,
            "Pain Cage stable codex best writes no stage save");
        AssertEqual(cycleBestBeforeCodex, state.BossTotalScore,
            "Pain Cage codex score never reaches the cycle best total");
        AssertEqual(cycleCurrentBeforeCodex, state.BossCurrentTotalScore,
            "Pain Cage codex score never reaches the cycle current total");

        // The codex "Ultimate Zone" list (stageType 2, LevelType 4 catalog) is the other half of the same rule: its
        // stage ids are not part of the current rotation, so the datum is the only place its settlement history lives.
        int trialCodexStageId = TableReaderV2.Parse<BossSingleTrialGradeTable>()
            .Where(catalog => catalog.LevelType == 4 && catalog.IsBestiaryCfg == 0)
            .SelectMany(catalog => catalog.SectionId.Where(sectionId => sectionId > 0))
            .Distinct()
            .SelectMany(sectionId => sectionRows
                .Where(row => row.SectionId == sectionId)
                .OrderByDescending(row => row.AfreshId == currentAfreshId)
                .Take(1)
                .SelectMany(row => row.StageId))
            .First(stageId => harness.Session.stage.Stages.ContainsKey(stageId));
        state.BossTrialScores[trialCodexStageId] = 123_456;
        int savesBeforeTrialCodex = stageCollection.ReplaceOneCalls;
        NotifyStageData trialCodexPush = SingleStagePush(
            ReadRankInfo(46_808, "Pain Cage trial codex settlement history"),
            "Pain Cage trial codex settlement history");
        AssertEqual(123_456L, harness.Session.stage.Stages[trialCodexStageId].Score,
            "Pain Cage trial codex best is the stage datum the settlement reads");
        AssertEqual(123_456L, trialCodexPush.StageList.Single().Score,
            "Pain Cage trial codex best reaches the client as its settlement history");
        AssertEqual(savesBeforeTrialCodex + 1, stageCollection.ReplaceOneCalls,
            "Pain Cage trial codex best persists once");

        // Wire projection: every current rotation stage is reported exactly once, synthetic entries carry no score,
        // no team and no auto-fight marker, and nothing is persisted by building the payload.
        List<int> rotationStageIds = state.BossList
            .Where(sectionId => sectionRows.Any(row => row.SectionId == sectionId && row.AfreshId == currentAfreshId))
            .SelectMany(sectionId => sectionRows
                .Single(row => row.SectionId == sectionId && row.AfreshId == currentAfreshId)
                .StageId)
            .Where(stageId => stageId > 0)
            .Distinct()
            .ToList();
        int persistedRecordsBeforeProjection = state.BossStageRecords.Count;
        int stageSavesBeforeProjection = stageCollection.ReplaceOneCalls;
        List<Dictionary<string, object>> projectedRecords = InvokePrivateStaticWithArgs<NotifyFubenBossSingleData>(
                bossModule, "BuildLoginData", [player, null])
            .FubenBossSingleData.StageRecordList
            .Select(entry => (Dictionary<string, object>)entry!)
            .ToList();
        List<int> projectedStageIds = projectedRecords.Select(entry => Convert.ToInt32(entry["StageId"])).ToList();
        AssertEqual(projectedStageIds.Distinct().Count(), projectedStageIds.Count,
            "Pain Cage stage record projection has no duplicate stage ids");
        AssertEqual(true, rotationStageIds.All(projectedStageIds.Contains),
            "Pain Cage stage record projection covers every current rotation stage");
        List<Dictionary<string, object>> syntheticRecords = projectedRecords
            .Where(entry => !state.BossStageRecords.Any(record => record.StageId == Convert.ToInt32(entry["StageId"])))
            .ToList();
        int genuineRotationRecords = state.BossStageRecords.Count(record =>
            rotationStageIds.Contains(record.StageId) && !state.BossResetStageIds.Contains(record.StageId));
        AssertEqual(rotationStageIds.Count - genuineRotationRecords, syntheticRecords.Count,
            "Pain Cage projection synthesizes only the uncleared rotation stages");
        AssertEqual(true, syntheticRecords.All(entry =>
                Convert.ToInt32(entry["Score"]) == 0
                && ((System.Collections.ICollection)entry["Characters"]!).Count == 0
                && ((System.Collections.ICollection)entry["Partners"]!).Count == 0
                && !Convert.ToBoolean(entry["IsUseAutoFight"])),
            "Pain Cage projected entries carry no score, no team and no auto-fight marker");
        AssertEqual(persistedRecordsBeforeProjection, state.BossStageRecords.Count,
            "Pain Cage projection persists no fictional clear");
        AssertEqual(stageSavesBeforeProjection, stageCollection.ReplaceOneCalls,
            "Pain Cage projection writes no stage save");

        int savesBeforeRepeat = stageCollection.ReplaceOneCalls;
        AssertEqual(0, ReadRankInfo(46_803, "Pain Cage repeated stage score sync").Count,
            "Pain Cage stage score sync is idempotent");
        AssertEqual(savesBeforeRepeat, stageCollection.ReplaceOneCalls,
            "Pain Cage idempotent sync writes no stage save");

        const int resetPacketId = 46_804;
        InvokeRegisteredRequestHandler(
            nameof(BossSingleResetStageRequest),
            harness.Session,
            resetPacketId,
            new BossSingleResetStageRequest { StageId = chaosStageId });
        List<Packet.Push> resetPushes = ReadPushes(
            resetPacketId, nameof(BossSingleResetStageResponse), "Pain Cage reset stage score sync",
            out Packet.Response resetResponse);
        AssertEqual(0, MessagePackSerializer.Deserialize<BossSingleResetStageResponse>(resetResponse.Content).Code,
            "Pain Cage reset stage score sync response code");
        BossSingleStageRecordState resetRecord = state.BossStageRecords.Single(value => value.StageId == chaosStageId);
        AssertEqual(0, resetRecord.Score, "Pain Cage reset clears current stage score");
        AssertEqual(87_300, resetRecord.MaxScore, "Pain Cage reset retains stage best for aggregate progress");
        AssertEqual(true, state.BossResetStageIds.Contains(chaosStageId), "Pain Cage reset marker persistence");
        AssertEqual(true,
            resetPushes.Select(push => push.Name)
                .SequenceEqual([nameof(NotifyFubenBossSingleData), nameof(NotifyStageData)]),
            "Pain Cage reset pushes login data then the reset stage datum");
        AssertEqual(0L, harness.Session.stage.Stages[chaosStageId].Score,
            "Pain Cage reset stage datum clears to zero");
        AssertEqual(0L, MessagePackSerializer.Deserialize<NotifyStageData>(resetPushes[1].Content).StageList.Single().Score,
            "Pain Cage reset sync push clears the stage datum score");

        BossSingleChallengeGradeTable challengeGrade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
        state.BossLevelType = grades
            .Where(row => row.AfreshId == currentAfreshId && row.GradeType >= challengeGrade.NeedGradeType)
            .OrderBy(row => row.GradeType)
            .First()
            .LevelType;
        state.BossResetStageIds = [];
        state.BossStageRecords =
        [
            new BossSingleStageRecordState { StageId = 30303255, Score = 87_300, MaxScore = 87_300 },
            new BossSingleStageRecordState { StageId = 30303256, Score = 178_200, MaxScore = 178_200 },
            new BossSingleStageRecordState { StageId = 30303257, Score = 346_800, MaxScore = 346_800 },
            new BossSingleStageRecordState { StageId = 30303249, Score = 89_250, MaxScore = 89_250 },
            new BossSingleStageRecordState { StageId = 30303250, Score = 175_800, MaxScore = 175_800 },
            new BossSingleStageRecordState { StageId = 30303251, Score = 348_240, MaxScore = 348_240 },
            new BossSingleStageRecordState { StageId = 30302905, Score = 360_600, MaxScore = 360_600 }
        ];
        AssertEqual(true,
            state.BossStageRecords.All(record => sectionRows.Any(row => row.StageId.Contains(record.StageId))),
            "Pain Cage period total fixture stages exist in the section table");
        NotifyFubenBossSingleData locked = InvokePrivateStaticWithArgs<NotifyFubenBossSingleData>(
            bossModule, "BuildLoginData", [player, null]);
        AssertEqual(1_586_190, locked.FubenBossSingleData.TotalScore,
            "reported Pain Cage period total stays below the challenge threshold");
        AssertEqual(0, locked.FubenBossSingleData.ChallengeLevelType,
            "reported Pain Cage period keeps Intensive Battle locked");
        AssertEqual(0, locked.FubenBossSingleData.ChallengeSectionId,
            "reported Pain Cage period exposes no challenge section");

        state.BossStageRecords.Add(new BossSingleStageRecordState { StageId = 30302903, Score = 88_200, MaxScore = 88_200 });
        state.BossStageRecords.Add(new BossSingleStageRecordState { StageId = 30302904, Score = 180_900, MaxScore = 180_900 });
        NotifyFubenBossSingleData unlocked = InvokePrivateStaticWithArgs<NotifyFubenBossSingleData>(
            bossModule, "BuildLoginData", [player, null]);
        AssertEqual(1_855_290, unlocked.FubenBossSingleData.TotalScore,
            "Pain Cage period total counts the newly cleared stage scores");
        AssertEqual(challengeGrade.LevelType, unlocked.FubenBossSingleData.ChallengeLevelType,
            "Pain Cage period total unlocks the level-9 challenge");
        AssertEqual(true, unlocked.FubenBossSingleData.ChallengeSectionId > 0,
            "Pain Cage unlocked challenge exposes a section");
    }


    private static void ValidateBossInshotEarlyLineupRejection(
        BossInshotActivityTable activity,
        int robotId)
    {
        const long playerId = 46_690;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-prefight-lineup");

        PreFightRequest invalidRequest = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)activity.TeachStageId),
                ChallengeCount = 1,
                CardIds = [0, 0, 0],
                RobotIds = Enumerable.Repeat(robotId, 32).ToList()
            }
        };
        const int invalidPacketId = 46_691;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            invalidPacketId,
            invalidRequest);
        PreFightResponse invalidResponse = ReadResponsePayload<PreFightResponse>(
            harness,
            invalidPacketId,
            nameof(PreFightResponse),
            "BossInshot oversized lineup response");
        AssertEqual(20215035, invalidResponse.Code,
            "BossInshot oversized robot lineup rejects");
        AssertEqual(0, invalidResponse.FightData.RoleData.Count,
            "BossInshot invalid lineup rejects before NPC expansion");
        AssertEqual(true, harness.Session.fight is null,
            "BossInshot invalid lineup does not authorize a fight");

        PreFightRequest paddedRequest = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)activity.TeachStageId),
                ChallengeCount = 1,
                CardIds = [0, 0, 0],
                RobotIds = [0, robotId, 0]
            }
        };
        const int paddedPacketId = 46_692;
        InvokeRegisteredRequestHandler(
            nameof(PreFightRequest),
            harness.Session,
            paddedPacketId,
            paddedRequest);
        PreFightResponse paddedResponse = ReadResponsePayload<PreFightResponse>(
            harness,
            paddedPacketId,
            nameof(PreFightResponse),
            "BossInshot padded retail lineup response");
        AssertEqual(0, paddedResponse.Code,
            "BossInshot zero-padded retail robot lineup succeeds");
        AssertEqual(1, paddedResponse.FightData.RoleData.Single().NpcData.Count,
            "BossInshot padded retail lineup expands one robot");
    }

    private static void ValidateBossInshotBoundedRankQuery(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        RecordingMongoCollectionProxy<BossInshotRankEntry> rankCollection)
    {
        const long playerId = 46_700;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-rank-bounded");

        int bossId = activity.TowerBossIds[0];
        string ownId = BossInshotRankEntry.BuildId(activity.Id, bossId, characterId, playerId);
        List<BossInshotRankEntry> leaders = Enumerable.Range(0, 150)
            .Select(index => new BossInshotRankEntry
            {
                Id = index == 0
                    ? ownId
                    : BossInshotRankEntry.BuildId(activity.Id, bossId, characterId, playerId + index),
                ActivityId = activity.Id,
                BossId = bossId,
                CharacterId = characterId,
                PlayerId = playerId + index,
                Name = $"rank-{index}",
                TowerId = 9 - index / 20,
                Score = 1_000_000 - index,
                AchievedAt = 1_000 + index
            })
            .ToList();
        rankCollection.FindResults = leaders;
        rankCollection.CountDocumentsResults.Enqueue(100_000_000L);
        rankCollection.CountDocumentsResults.Enqueue(42L);

        const int packetId = 46_701;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotTowerQueryRankRequest),
            harness.Session,
            packetId,
            new BossInshotTowerQueryRankRequest
            {
                CharacterCfgId = characterConfigId,
                BossId = bossId
            });
        BossInshotTowerQueryRankResponse response = ReadResponsePayload<BossInshotTowerQueryRankResponse>(
            harness,
            packetId,
            nameof(BossInshotTowerQueryRankResponse),
            "BossInshot bounded materialized rank response");
        AssertEqual(0, response.Code, "BossInshot bounded rank Code");
        AssertEqual(100_000_000, response.TotalCount, "BossInshot rank preserves 100M participant count");
        AssertEqual(43, response.Rank, "BossInshot self rank derives from indexed better-row count");
        AssertEqual(100, response.RankPlayerInfos.Count, "BossInshot leaderboard response is capped at 100 rows");
        AssertEqual(100, rankCollection.LastFindLimit ?? -1, "BossInshot leaderboard query applies server-side limit");
        AssertEqual(true, rankCollection.LastFindHadSort, "BossInshot leaderboard query applies server-side sort");
        string renderedSort = rankCollection.LastFindSort?.ToJson() ?? string.Empty;
        if (!renderedSort.Contains("tower_id", StringComparison.Ordinal)
            || !renderedSort.Contains("score", StringComparison.Ordinal)
            || !renderedSort.Contains("achieved_at", StringComparison.Ordinal)
            || !renderedSort.Contains("player_id", StringComparison.Ordinal))
            throw new InvalidDataException($"BossInshot rank sort is incomplete: {renderedSort}");
        AssertEqual(2, rankCollection.CountDocumentsCalls, "BossInshot rank uses bounded total/self count queries");
    }

    private static void ValidateBossInshotDuplicateNormalizationAndStableDraw(
        BossInshotActivityTable activity,
        int characterId,
        Type bossModule)
    {
        BossInshotTowerTable towerConfig = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.Stages.Count > 0);
        int bossId = TableReaderV2.Parse<BossInshotTowerStageTable>()
            .Single(row => row.StageId == towerConfig.Stages[0]).BossId;
        Player player = CreateDrawCompatibilityPlayer(46_710);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [towerConfig.Stages[0]],
                Scores =
                [
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 200,
                        AchievedAt = 30
                    }
                ]
            },
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [towerConfig.Stages[0]],
                Scores =
                [
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 100,
                        AchievedAt = 10
                    },
                    new BossInshotTowerScoreState
                    {
                        BossId = bossId,
                        CharacterId = characterId,
                        MaxScore = 200,
                        AchievedAt = 25
                    }
                ]
            }
        ];

        NotifyBossInshotData notify = InvokePrivateStatic<NotifyBossInshotData>(
            bossModule,
            "BuildNotifyBossInshotData",
            player);
        AssertEqual(1, player.BossInshot.Towers.Count, "BossInshot duplicate tower BSON rows normalize");
        BossInshotTowerScoreState normalized = player.BossInshot.Towers.Single().Scores.Single();
        AssertEqual(200, normalized.MaxScore, "BossInshot duplicate score rows retain maximum");
        AssertEqual(25L, normalized.AchievedAt, "BossInshot duplicate score ties retain earliest timestamp");
        AssertEqual(200,
            notify.BossInshotData.TowerDataDict[towerConfig.Id]
                .BossMaxScoreDict[bossId]
                .CharacterScoreDict[characterId],
            "BossInshot normalized score projects without duplicate-key failure");

        BossInshotTowerTable randomTower = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.SelectNum > 0 && row.Stages.Distinct().Count() > row.SelectNum);
        BossInshotTowerState first = InvokePrivateStatic<BossInshotTowerState>(
            bossModule,
            "InitializeTower",
            activity.Id,
            46_711L,
            randomTower);
        BossInshotTowerState repeat = InvokePrivateStatic<BossInshotTowerState>(
            bossModule,
            "InitializeTower",
            activity.Id,
            46_711L,
            randomTower);
        AssertIntegerList(
            first.DrawStageIds.Select(id => (long)id).ToArray(),
            repeat.DrawStageIds.Select(id => (long)id).ToArray(),
            "BossInshot tower draw is stable for identical persisted identity");
        AssertEqual(randomTower.SelectNum, first.DrawStageIds.Count, "BossInshot tower draw count derives from table");
        AssertEqual(true,
            first.DrawStageIds.All(randomTower.Stages.Contains),
            "BossInshot tower draw only selects table stages");
    }

    private static void ValidateBossInshotAtomicTowerMutations(
        BossInshotActivityTable activity,
        RecordingMongoCollectionProxy<Player> playerCollection,
        Type bossModule)
    {
        const long playerId = 46_720;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-tower-atomic");

        byte[] beforeEnter = player.BossInshot.ToBson();
        playerCollection.ThrowOnReplaceOne = true;
        const int failedEnterPacketId = 46_721;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotEnterNextTowerRequest),
            harness.Session,
            failedEnterPacketId,
            new BossInshotEnterNextTowerRequest());
        BossInshotEnterNextTowerResponse failedEnter = ReadResponsePayload<BossInshotEnterNextTowerResponse>(
            harness,
            failedEnterPacketId,
            nameof(BossInshotEnterNextTowerResponse),
            "BossInshot failed tower entry response");
        AssertEqual(20215023, failedEnter.Code, "BossInshot failed tower entry returns protocol error");
        AssertEqual(Convert.ToHexString(beforeEnter), Convert.ToHexString(player.BossInshot.ToBson()),
            "BossInshot failed tower entry restores live state");

        playerCollection.ThrowOnReplaceOne = false;
        const int retryEnterPacketId = 46_722;
        InvokeRegisteredRequestHandler(
            nameof(BossInshotEnterNextTowerRequest),
            harness.Session,
            retryEnterPacketId,
            new BossInshotEnterNextTowerRequest());
        BossInshotEnterNextTowerResponse retryEnter = ReadResponsePayload<BossInshotEnterNextTowerResponse>(
            harness,
            retryEnterPacketId,
            nameof(BossInshotEnterNextTowerResponse),
            "BossInshot tower entry retry response");
        AssertEqual(0, retryEnter.Code, "BossInshot tower entry retry succeeds after failed save");

        BossInshotTowerTable selectable = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.Type == 2 && row.Stages.Count > 0);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.CurrentTowerId = selectable.Id;
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = selectable.Id,
                DrawStageIds = selectable.Stages.Take(selectable.SelectNum).ToList()
            }
        ];
        int selectedStageId = player.BossInshot.Towers[0].DrawStageIds[0];
        byte[] beforeSelect = player.BossInshot.ToBson();
        playerCollection.ThrowOnReplaceOne = true;
        int failedSelect = InvokePrivateStatic<int>(
            bossModule,
            "SelectCore",
            harness.Session,
            selectable.Id,
            selectedStageId,
            false);
        AssertEqual(20215023, failedSelect, "BossInshot failed tower selection returns protocol error");
        AssertEqual(Convert.ToHexString(beforeSelect), Convert.ToHexString(player.BossInshot.ToBson()),
            "BossInshot failed tower selection restores live state");

        playerCollection.ThrowOnReplaceOne = false;
        int retrySelect = InvokePrivateStatic<int>(
            bossModule,
            "SelectCore",
            harness.Session,
            selectable.Id,
            selectedStageId,
            false);
        AssertEqual(0, retrySelect, "BossInshot tower selection retry succeeds after failed save");
        AssertEqual(selectedStageId,
            player.BossInshot.Towers.Single(row => row.TowerId == selectable.Id).SelectStageId,
            "BossInshot successful selection publishes persisted candidate");
    }

    private static void ValidateBossInshotPassedTowerReplay(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        Type bossModule)
    {
        BossInshotTowerTable towerConfig = TableReaderV2.Parse<BossInshotTowerTable>()
            .First(row => row.FailReBackToId >= 0 && row.ProtectCount > 0 && row.PassScore > 0);
        int stageId = towerConfig.Stages[0];
        Player player = CreateDrawCompatibilityPlayer(46_730);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        player.BossInshot.CurrentTowerId = towerConfig.Id;
        player.BossInshot.Towers =
        [
            new BossInshotTowerState
            {
                TowerId = towerConfig.Id,
                DrawStageIds = [stageId],
                SelectStageId = stageId,
                IsPass = true
            }
        ];
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(46_730),
            player,
            sessionId: "v46-boss-passed-replay");

        for (uint attempt = 1; attempt <= 2; attempt++)
        {
            uint fightId = 46_730 + attempt;
            harness.Session.PendingBossInshotFight = new PendingBossInshotFight
            {
                ActivityId = activity.Id,
                TowerId = towerConfig.Id,
                StageId = checked((uint)stageId),
                FightId = fightId,
                CharacterConfigId = characterConfigId,
                CharacterId = characterId,
                IsTower = true
            };
            object?[] settleArgs =
            [
                harness.Session,
                new FightSettleResult
                {
                    StageId = checked((uint)stageId),
                    FightId = fightId,
                    IsWin = true,
                    IntToIntRecord = new Dictionary<int, int>(),
                    LeftTime = 0
                },
                null
            ];
            AssertEqual(true,
                InvokePrivateStaticWithArgs<bool>(bossModule, "TrySettle", settleArgs),
                $"BossInshot passed tower replay {attempt} is claimed");
            AssertEqual(0,
                ((FightSettleResponse)settleArgs[2]!).Code,
                $"BossInshot passed tower replay {attempt} succeeds");
            AssertEqual(towerConfig.Id, player.BossInshot.CurrentTowerId,
                $"BossInshot passed tower replay {attempt} does not fall back current tower");
            AssertEqual(0,
                player.BossInshot.Towers.Single().TriggerProtectCount,
                $"BossInshot passed tower replay {attempt} does not consume protection");
        }
    }

    private static void ValidateBossInshotHandlerRetry(
        BossInshotActivityTable activity,
        int characterConfigId,
        int characterId,
        RecordingMongoCollectionProxy<Player> playerCollection)
    {
        BossInshotStageTable stage = TableReaderV2.Parse<BossInshotStageTable>()
            .First(row => row.StageId != activity.TeachStageId
                && activity.BossIds.Contains(row.BossId)
                && row.UnlockConditionId is null);
        BossInshotScoreTable scoreRow = TableReaderV2.Parse<BossInshotScoreTable>()
            .First(row => row.Type == 1 && row.Score is > 0);
        const long playerId = 46_740;
        const uint fightId = 46_741;
        Player player = CreateDrawCompatibilityPlayer(playerId);
        player.BossInshot = CreateAuthorizedBossInshotState(activity);
        using LoopbackSessionHarness harness = new(
            CreateDrawCompatibilityCharacter(playerId),
            player,
            sessionId: "v46-boss-handler-retry");
        harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
        PreFightRequest preFight = new()
        {
            PreFightData = new PreFightRequest.PreFightRequestPreFightData
            {
                StageId = checked((uint)stage.StageId),
                ChallengeCount = 1
            }
        };
        harness.Session.fight = new Fight(preFight, fightId);
        harness.Session.PendingBossInshotFight = new PendingBossInshotFight
        {
            ActivityId = activity.Id,
            StageId = checked((uint)stage.StageId),
            FightId = fightId,
            CharacterConfigId = characterConfigId,
            CharacterId = characterId,
            IsTower = false
        };
        FightSettleRequest request = new()
        {
            Result = new FightSettleResult
            {
                StageId = checked((uint)stage.StageId),
                FightId = fightId,
                IsWin = true,
                IntToIntRecord = new Dictionary<int, int> { [scoreRow.Id] = 1 },
                LeftTime = 0
            }
        };

        playerCollection.ThrowOnReplaceOne = true;
        const int failedPacketId = 46_742;
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, failedPacketId, request);
        FightSettleResponse failed = ReadResponsePayload<FightSettleResponse>(
            harness,
            failedPacketId,
            nameof(FightSettleResponse),
            "BossInshot handler persistence-failure response");
        AssertEqual(20215023, failed.Code, "BossInshot handler reports retryable persistence failure");
        AssertEqual(true, harness.Session.PendingBossInshotFight is not null,
            "BossInshot handler keeps pending context after persistence failure");
        AssertEqual(true, harness.Session.fight is not null,
            "BossInshot handler keeps authorization context after persistence failure");

        playerCollection.ThrowOnReplaceOne = false;
        const int retryPacketId = 46_743;
        InvokeRegisteredRequestHandler(nameof(FightSettleRequest), harness.Session, retryPacketId, request);
        string[] retryPushNames =
        [
            nameof(NotifyBossInshotData),
            nameof(NotifyArchiveMonsterRecord),
            nameof(NotifyTask)
        ];
        foreach (string expectedPushName in retryPushNames)
        {
            Packet pushPacket = harness.ReadPacket(
                $"BossInshot handler persistence retry {expectedPushName}");
            AssertEqual(Packet.ContentType.Push, pushPacket.Type,
                $"BossInshot handler persistence retry {expectedPushName} packet type");
            Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
            AssertEqual(expectedPushName, push.Name,
                $"BossInshot handler persistence retry {expectedPushName} packet name");
        }
        FightSettleResponse retry = ReadResponsePayload<FightSettleResponse>(
            harness,
            retryPacketId,
            nameof(FightSettleResponse),
            "BossInshot handler persistence retry response");
        AssertEqual(0, retry.Code, "BossInshot handler retry reaches settlement and succeeds");
        AssertEqual(true, harness.Session.PendingBossInshotFight is null,
            "BossInshot successful handler retry consumes pending context");
        AssertEqual(true, harness.Session.fight is null,
            "BossInshot successful handler retry consumes authorization context");
    }

    private static BossInshotState CreateAuthorizedBossInshotState(BossInshotActivityTable activity)
    {
        BossInshotState state = new()
        {
            ActivityId = activity.Id,
            AuthorizedActivityId = activity.Id,
            AuthorizedTimeIds = TableReaderV2.Parse<BossInshotTowerTable>()
                .Select(row => row.TimeId)
                .Where(id => id > 0)
                .Append(activity.TimeId!.Value)
                .Distinct()
                .ToList(),
            IsPassTeach = true,
            RankProjectionVersion = 2
        };
        ConditionTable condition = TableReaderV2.Parse<ConditionTable>()
            .Single(row => row.Id == activity.TowerConditions!.Value);
        if (condition.Type != 15306 || condition.Params.Count < 2)
            throw new InvalidDataException($"BossInshot tower condition {condition.Id} is not a supported score threshold.");
        state.PassStages.Add(new BossInshotPassStage
        {
            StageId = condition.Params[0],
            MaxScore = condition.Params[1]
        });
        return state;
    }
}
