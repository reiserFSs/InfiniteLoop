using AscNet.Common.Database;

using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using AscNet.GameServer.Handlers;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.bosssingle;
using MessagePack;
using Newtonsoft.Json.Linq;
using MongoDB.Driver;
using MongoDB.Bson;
using System.Globalization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
        private static void ValidateBossSingleCompatibility()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Player> playerCollection,
                out RecordingMongoCollectionProxy<AscNet.Common.Database.Stage> stageCollection);
            const long playerId = 99_701;
            const uint characterId = 1_021_001;
            uint[] historyCharacterIds = [1_021_002, 1_021_003, 1_021_004];
            AscNet.Common.Database.Player player = CreateDrawCompatibilityPlayer(playerId);
            player.SimulatedBattlefield = new() { BossRankPlatform = 2 };
            AscNet.Common.Database.Character character = CreateDrawCompatibilityCharacter(playerId);
            character.Characters.Add(CreateLoginAccountCompatibilityCharacter(characterId, 3_021_001));
            foreach (uint historyCharacterId in historyCharacterIds)
                character.Characters.Add(CreateLoginAccountCompatibilityCharacter(historyCharacterId, 3_021_001));
            AscNet.Common.Database.Inventory inventory = CreateDrawCompatibilityInventory(playerId, []);
            using LoopbackSessionHarness harness = new(character, player, inventory, "boss-single-compat-test");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
            MethodInfo buildLoginData = RequiredMethod(
                bossModule,
                "BuildLoginData",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(AscNet.Common.Database.Player), typeof(long?)]);
            NotifyFubenBossSingleData BuildLogin(AscNet.Common.Database.Player target, long? now) =>
                buildLoginData.Invoke(null, [target, now]) as NotifyFubenBossSingleData
                ?? throw new InvalidDataException("BossModule.BuildLoginData returned nil.");
            MethodInfo buildNotifyLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "BuildNotifyLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            NotifyLogin BuildAccountLogin() =>
                buildNotifyLogin.Invoke(null, [harness.Session]) as NotifyLogin
                ?? throw new InvalidDataException("AccountModule.BuildNotifyLogin returned nil.");

            TResponse ReadAfterPushes<TResponse>(
                int packetId,
                string responseName,
                string name,
                out List<string> pushNames,
                int maxPackets = 64,
                Action<Packet.Push>? onPush = null)
            {
                pushNames = [];
                for (int packetIndex = 0; packetIndex < maxPackets; packetIndex++)
                {
                    Packet packet = harness.ReadPacket($"{name} packet {packetIndex + 1}");
                    if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                        onPush?.Invoke(push);
                        pushNames.Add(push.Name);
                        continue;
                    }

                    AssertEqual(Packet.ContentType.Response, packet.Type, $"{name} response packet type");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(packetId, response.Id, $"{name} response packet id");
                    AssertEqual(responseName, response.Name, $"{name} response packet name");
                    return MessagePackSerializer.Deserialize<TResponse>(response.Content);
                }
                throw new InvalidDataException($"{name}: expected {responseName} within {maxPackets} packets.");
            }

            PreFightResponse StartFight(int packetId, int stageId, int stageType,
                IReadOnlyList<uint>? cardIds = null, object? buffGroup = null)
            {
                InvokeRegisteredRequestHandler(
                    nameof(PreFightRequest),
                    harness.Session,
                    packetId,
                    new PreFightRequest
                    {
                        PreFightData = new PreFightRequest.PreFightRequestPreFightData
                        {
                            StageId = checked((uint)stageId),
                            ChallengeCount = 1,
                            CardIds = cardIds?.ToList() ?? [characterId],
                            RobotIds = [],
                            BossSingleStageType = stageType,
                            BossSingleChallengeBuffGroup = buffGroup
                        }
                    });
                return ReadResponsePayload<PreFightResponse>(
                    harness,
                    packetId,
                    nameof(PreFightResponse),
                    $"Pain Cage stage {stageId} PreFightResponse");
            }

            FightSettleResponse SettleFight(
                int packetId,
                PreFightResponse preFight,
                BossSingleStageTable stage,
                int characterHp,
                int bossHp,
                int fightSeconds = 20,
                bool isWin = true,
                bool isForceExit = false)
            {
                InvokeRegisteredRequestHandler(
                    nameof(FightSettleRequest),
                    harness.Session,
                    packetId,
                    new FightSettleRequest
                    {
                        Result = new FightSettleResult
                        {
                            IsWin = isWin,
                            IsForceExit = isForceExit,
                            StageId = checked((uint)stage.StageId),
                            FightId = preFight.FightData.FightId,
                            StartFrame = 1,
                            SettleFrame = 1 + fightSeconds * 20,
                            PauseFrame = 0,
                            LeftTime = Math.Max(0, stage.PassTimeLimit - fightSeconds),
                            NpcHpInfo = new()
                            {
                                [1] = new NpcHp
                                {
                                    CharacterId = checked((int)characterId),
                                    Type = 1,
                                    AttrTable = new()
                                    {
                                        [1] = new Dictionary<object, object>
                                        {
                                            ["Value"] = characterHp,
                                            ["MaxValue"] = 100
                                        }
                                    },
                                    BuffIds = []
                                },
                                [2] = new NpcHp
                                {
                                    Type = 2,
                                    AttrTable = new()
                                    {
                                        [1] = new Dictionary<object, object>
                                        {
                                            ["Value"] = bossHp * 10_000,
                                            ["MaxValue"] = 1_000_000
                                        }
                                    },
                                    BuffIds = []
                                }
                            }
                        }
                    });
                return ReadResponsePayload<FightSettleResponse>(
                    harness,
                    packetId,
                    nameof(FightSettleResponse),
                    $"Pain Cage stage {stage.StageId} FightSettleResponse");
            }

            BossSingleSaveScoreResponse SaveScore(
                int packetId,
                int stageId,
                string name,
                out List<string> pushNames,
                Action<Packet.Push>? onPush = null)
            {
                InvokeRegisteredRequestHandler(
                    nameof(BossSingleSaveScoreRequest),
                    harness.Session,
                    packetId,
                    new BossSingleSaveScoreRequest { StageId = stageId });
                return ReadAfterPushes<BossSingleSaveScoreResponse>(
                    packetId,
                    nameof(BossSingleSaveScoreResponse),
                    name,
                    out pushNames,
                    onPush: onPush);
            }

            BossSingleFightResult RequiredBossResult(FightSettleResponse response, string name)
            {
                object? raw = response.Settle?.BossSingleFightResult;
                if (raw is null)
                    throw new InvalidDataException($"{name}: BossSingleFightResult is nil.");
                if (raw is BossSingleFightResult typed)
                    return typed;
                return JObject.FromObject(raw).ToObject<BossSingleFightResult>()
                    ?? throw new InvalidDataException($"{name}: BossSingleFightResult could not be decoded.");
            }

            List<BossSingleGradeTable> grades = TableReaderV2.Parse<BossSingleGradeTable>();
            List<BossSingleGroupTable> groups = TableReaderV2.Parse<BossSingleGroupTable>();
            List<BossSingleSectionTable> sections = TableReaderV2.Parse<BossSingleSectionTable>();
            List<BossSingleStageTable> stages = TableReaderV2.Parse<BossSingleStageTable>();
            List<BossSingleScoreRuleTable> scoreRules = TableReaderV2.Parse<BossSingleScoreRuleTable>();
            List<BossSingleScoreRewardTable> scoreRewards = TableReaderV2.Parse<BossSingleScoreRewardTable>();
            List<BossSingleRewardGoodsTable> rewardGoods = TableReaderV2.Parse<BossSingleRewardGoodsTable>();
            List<BossSingleTrialGradeTable> trialGrades = TableReaderV2.Parse<BossSingleTrialGradeTable>();
            BossSingleConfigTable runtimeConfig = TableReaderV2.Parse<BossSingleConfigTable>().Single();
            AssertEqual(6, runtimeConfig.AutoFightCount, "Pain Cage EN-config auto-fight limit");
            AssertEqual(100, runtimeConfig.AutoFightRebate, "Pain Cage EN-config auto-fight rebate");
            AssertEqual(301, stages.Count, "Pain Cage generated stage count");
            AssertEqual(stages.Count, scoreRules.Count, "Pain Cage one score rule per stage");
            if (groups.Count == 0 || sections.Count == 0 || scoreRewards.Count == 0 || rewardGoods.Count == 0)
                throw new InvalidDataException("Pain Cage generated runtime tables are incomplete.");

            int playerLevel = checked((int)player.PlayerData.Level);
            int currentAfreshId = grades.Max(row => row.AfreshId);
            List<BossSingleGradeTable> freshEligibleGrades = grades
                .Where(row => row.AfreshId == currentAfreshId
                    && playerLevel >= row.MinPlayerLevel
                    && playerLevel <= row.MaxPlayerLevel
                    && row.PreGradeType == 0)
                .ToList();
            AssertEqual(1, freshEligibleGrades.Count,
                "Pain Cage fresh level-80 table eligibility has one grade");

            NotifyFubenBossSingleData initialLogin = BuildLogin(player, null);
            AssertEqual(true, initialLogin.BossListDict is null,
                "Pain Cage fresh direct-entry login omits BossListDict");
            AssertEqual(true, initialLogin.FubenBossSingleData.LevelType > 0,
                "Pain Cage fresh direct-entry login auto-selects its sole eligible grade");
            AssertEqual(true, initialLogin.FubenBossSingleData.BossList.Count > 0,
                "Pain Cage fresh direct-entry login commits bosses");
            AssertEqual(true, initialLogin.FubenBossSingleData.RemainTime > 0, "Pain Cage live remaining time");
            AssertEqual(2, initialLogin.FubenBossSingleData.RankPlatform,
                "Pain Cage persisted login platform rank partition");
            AssertEqual(grades.Max(row => row.AfreshId), initialLogin.FubenBossSingleData.AfreshId,
                "Pain Cage current refresh id comes from grade tables");

            int directEntryLevel = initialLogin.FubenBossSingleData.LevelType;
            AssertEqual(freshEligibleGrades.Single().LevelType, directEntryLevel,
                "Pain Cage fresh direct-entry selected table-eligible grade");
            BossSingleGradeTable directEntryGrade = grades.Single(row => row.LevelType == directEntryLevel);
            int[] directEntrySections = initialLogin.FubenBossSingleData.BossList.ToArray();
            AssertEqual(directEntryGrade.GroupId.Count(groupId => groupId > 0), directEntrySections.Length,
                "Pain Cage direct-entry one selected section per configured group");
            AssertEqual(directEntrySections.Length, directEntrySections.Distinct().Count(),
                "Pain Cage direct-entry selected sections are unique");

            int currentActivityNo = player.SimulatedBattlefield.BossActivityNo;
            player.SimulatedBattlefield.BossLevelType = 0;
            player.SimulatedBattlefield.BossList.Clear();
            player.SimulatedBattlefield.BossListOptions.Clear();
            player.SimulatedBattlefield.BossListOptions[directEntryLevel] = directEntrySections.ToList();
            NotifyFubenBossSingleData repairedLogin = BuildLogin(player, null);
            AssertEqual(currentActivityNo, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage persisted one-option repair stays in the current activity");
            AssertEqual(directEntryLevel, repairedLogin.FubenBossSingleData.LevelType,
                "Pain Cage persisted one-option repair selects the sole grade");
            if (!repairedLogin.FubenBossSingleData.BossList.SequenceEqual(directEntrySections))
                throw new InvalidDataException("Pain Cage persisted one-option repair did not commit the sole offered boss list.");
            AssertEqual(true, repairedLogin.BossListDict is null,
                "Pain Cage persisted one-option repair omits BossListDict");
            int[] directEntryStageIds = directEntrySections
                .SelectMany(sectionId => sections.Single(row => row.SectionId == sectionId && row.AfreshId == 1).StageId)
                .Distinct()
                .ToArray();
            AssertEqual(true, directEntryStageIds.Length > 0,
                "Pain Cage direct-entry committed sections contain stages");
            NotifyLogin accountLogin = MessagePackSerializer.Deserialize<NotifyLogin>(
                MessagePackSerializer.Serialize(BuildAccountLogin()));
            Dictionary<long, StageDatum> loginStages = accountLogin.FubenData?.StageData
                ?? throw new InvalidDataException("Pain Cage AccountModule.BuildNotifyLogin omitted FubenData.StageData.");
            foreach (int stageId in directEntryStageIds)
            {
                AssertEqual(true, loginStages.ContainsKey(stageId),
                    $"Pain Cage direct-entry NotifyLogin contains committed stage {stageId}");
            }

            List<BossSingleGradeTable> levelEligibleGrades = grades
                .Where(row => row.AfreshId == currentAfreshId
                    && playerLevel >= row.MinPlayerLevel
                    && playerLevel <= row.MaxPlayerLevel)
                .ToList();
            (BossSingleGradeTable Previous, int Score, List<BossSingleGradeTable> Options) chooserFixture =
                (from previous in grades
                 from score in levelEligibleGrades.Select(row => row.NeedScore).Append(0).Distinct()
                 let options = levelEligibleGrades.Where(row =>
                     row.PreGradeType == 0
                     || (previous.GradeType >= row.PreGradeType && score >= row.NeedScore)).ToList()
                 where options.Count == 2
                 select (previous, score, options)).FirstOrDefault();
            if (chooserFixture.Previous is null)
                throw new InvalidDataException("Pain Cage grade tables do not provide a table-qualified two-option fixture.");

            foreach (int stageId in directEntryStageIds)
                harness.Session.stage.Stages.Remove(checked((uint)stageId));

            player.SimulatedBattlefield.BossOldLevelType = chooserFixture.Previous.LevelType;
            player.SimulatedBattlefield.BossListOptions.Clear();
            player.SimulatedBattlefield.BossMaxScore = chooserFixture.Score;
            player.SimulatedBattlefield.BossLevelType = 0;
            player.SimulatedBattlefield.BossList.Clear();
            NotifyFubenBossSingleData chooserLogin = BuildLogin(player, null);
            Dictionary<int, List<int>> initialOptions = chooserLogin.BossListDict
                ?? throw new InvalidDataException("Pain Cage table-qualified chooser login omitted BossListDict.");
            AssertEqual(0, chooserLogin.FubenBossSingleData.LevelType,
                "Pain Cage two-option login retains chooser state");
            AssertEqual(0, chooserLogin.FubenBossSingleData.BossList.Count,
                "Pain Cage two-option login has no prematurely committed bosses");
            AssertEqual(2, initialOptions.Count, "Pain Cage chooser exposes high/extreme option maps");
            AssertIntegerSetContainsAll(
                chooserFixture.Options.Select(row => (long)row.LevelType).ToArray(),
                initialOptions.Keys.Select(levelType => (long)levelType).ToArray(),
                "Pain Cage chooser table-qualified grade options");

            int selectedLevel = initialOptions.Keys.Max();
            BossSingleGradeTable selectedGrade = grades.Single(row => row.LevelType == selectedLevel);
            int[] selectedSections = initialOptions[selectedLevel].ToArray();
            AssertEqual(selectedGrade.GroupId.Count(groupId => groupId > 0), selectedSections.Length,
                "Pain Cage one selected section per configured group");
            AssertEqual(selectedSections.Length, selectedSections.Distinct().Count(),
                "Pain Cage selected sections are unique");
            foreach (int sectionId in selectedSections)
            {
                BossSingleSectionTable section = sections.Single(row => row.SectionId == sectionId && row.AfreshId == 1);
                if (section.StageId.Count == 0)
                    throw new InvalidDataException($"Pain Cage selected section {sectionId} has no stages.");
            }

            const int invalidSelectPacketId = 82_000;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleSelectLevelTypeRequest),
                harness.Session,
                invalidSelectPacketId,
                new BossSingleSelectLevelTypeRequest { LevelId = int.MaxValue });
            BossSingleSelectLevelTypeResponse invalidSelect =
                ReadResponsePayload<BossSingleSelectLevelTypeResponse>(
                    harness,
                    invalidSelectPacketId,
                    nameof(BossSingleSelectLevelTypeResponse),
                    "Pain Cage invalid level selection");
            AssertEqual(1, invalidSelect.Code, "Pain Cage invalid level selection code");

            const int selectPacketId = 82_001;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleSelectLevelTypeRequest),
                harness.Session,
                selectPacketId,
                new BossSingleSelectLevelTypeRequest { LevelId = selectedLevel });
            BossSingleSelectLevelTypeResponse selectResponse =
                ReadAfterPushes<BossSingleSelectLevelTypeResponse>(
                    selectPacketId,
                    nameof(BossSingleSelectLevelTypeResponse),
                    "Pain Cage level selection",
                    out List<string> selectPushes);
            AssertEqual(0, selectResponse.Code, "Pain Cage level selection code");
            AssertEqual(selectedLevel, player.SimulatedBattlefield.BossLevelType,
                "Pain Cage selected level persistence");
            if (!player.SimulatedBattlefield.BossList.SequenceEqual(selectedSections))
                throw new InvalidDataException("Pain Cage selected boss list differs from the offered table-derived option.");
            AssertEqual(0, selectPushes.Count(name => name == nameof(NotifyStageData)),
                "Pain Cage selection does not duplicate login stage pushes");
            AssertEqual(true, selectPushes.Contains(nameof(NotifyWheelchairManualActivityUpdate)),
                "Pain Cage selection refreshes the manual guide subtype");
            foreach (int stageId in selectedSections
                         .SelectMany(sectionId => sections.Single(row => row.SectionId == sectionId && row.AfreshId == 1).StageId))
            {
                AssertEqual(true, harness.Session.stage.Stages.ContainsKey(checked((uint)stageId)),
                    $"Pain Cage selected stage {stageId} unlocked");
            }

            int AuxiliaryStageId(bool bestiary)
            {
                BossSingleTrialGradeTable catalog = trialGrades.Single(row => (row.IsBestiaryCfg != 0) == bestiary);
                return catalog.SectionId
                    .Where(sectionId => sectionId > 0)
                    .SelectMany(sectionId => sections
                        .Where(row => row.SectionId == sectionId)
                        .OrderByDescending(row => row.AfreshId == 1)
                        .Take(1)
                        .SelectMany(row => row.StageId))
                    .First(stageId => stages.Any(row => row.StageId == stageId));
            }

            List<(int Remaining, int Score, int Cap)> auxiliaryTimeScores = [];
            int FirstClearTaskProgress() =>
                TableReaderV2.Parse<AscNet.Table.V2.share.task.CurrentConditionTable>()
                    .Where(condition => condition.Type == 25005)
                    .Select(condition => condition.Id)
                    .Distinct()
                    .Sum(conditionId => player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId));
            void ExerciseAuxiliaryStage(bool bestiary, int stageType, int fightSeconds, int packetBase)
            {
                int stageId = AuxiliaryStageId(bestiary);
                BossSingleStageTable stage = stages.Single(row => row.StageId == stageId);
                int challengeCountBefore = player.SimulatedBattlefield.BossChallengeCount;
                int cycleBestBefore = player.SimulatedBattlefield.BossTotalScore;
                int cycleCurrentBefore = player.SimulatedBattlefield.BossCurrentTotalScore;
                PreFightResponse preFight = StartFight(packetBase, stageId, stageType);
                AssertEqual(0, preFight.Code, $"Pain Cage {(bestiary ? "bestiary" : "trial")} pre-fight code");
                AssertEqual(stage.PassTimeLimit, preFight.FightData.PassTimeLimit,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} table time limit");
                FightSettleResponse settle = SettleFight(
                    packetBase + 1,
                    preFight,
                    stage,
                    characterHp: 100,
                    bossHp: 0,
                    fightSeconds: fightSeconds);
                BossSingleFightResult result = RequiredBossResult(
                    settle,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} result");
                AssertEqual(stage.PassTimeLimit - fightSeconds, result.TimeLeft,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} remaining time");
                BossSingleScoreRuleTable scoreRule = scoreRules.Single(row => row.Id == stageId);
                int coefficientIndex = (stageType == 4 ? 8 : 4) - 1;
                double timeCoefficient = scoreRule.LeftTimeScore[coefficientIndex];
                int expectedTimeScore = Math.Min(stage.LeftTimeScore, checked((int)Math.Floor(
                    (stage.PassTimeLimit - fightSeconds) * timeCoefficient * stage.PassTimeLimit)));
                AssertEqual(expectedTimeScore, result.TimeScore,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} table-derived remaining-time score");
                auxiliaryTimeScores.Add((result.TimeLeft, result.TimeScore, stage.LeftTimeScore));
                AssertEqual(true, result.TimeScore > 0 && result.TimeScore <= stage.LeftTimeScore,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} remaining-time score is positive and table-capped");
                StageDatum codexStageDatum = harness.Session.stage.Stages[checked((uint)stageId)];
                codexStageDatum.Passed = false;
                codexStageDatum.PassTimesTotal = 0;
                codexStageDatum.LastPassTime = 0;
                codexStageDatum.BestRecordTime = 456;
                codexStageDatum.LastRecordTime = 123;
                codexStageDatum.BestCardIds = [987_654_321];
                codexStageDatum.LastCardIds = [123_456_789];
                StageDatum expectedCodexStageDatum = MessagePackSerializer.Deserialize<StageDatum>(
                    MessagePackSerializer.Serialize(codexStageDatum));
                int firstClearTaskProgressBefore = FirstClearTaskProgress();
                BossSingleSaveScoreResponse save = SaveScore(
                    packetBase + 2,
                    stageId,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} save",
                    out _);
                AssertEqual(0, save.Code, $"Pain Cage {(bestiary ? "bestiary" : "trial")} save code");
                Dictionary<int, int> scores = bestiary
                    ? player.SimulatedBattlefield.BossBestiaryScores
                    : player.SimulatedBattlefield.BossTrialScores;
                AssertEqual(result.TotalScore, scores[stageId],
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} score persistence");
                expectedCodexStageDatum.Score = result.TotalScore;
                AssertEqual(true, MessagePackSerializer.Serialize(expectedCodexStageDatum)
                    .SequenceEqual(MessagePackSerializer.Serialize(harness.Session.stage.Stages[checked((uint)stageId)])),
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} score projection preserves non-score stage fields");
                AssertEqual(result.TotalScore, harness.Session.stage.Stages[stageId].Score,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} clear exposes the mode best to the settlement");
                AssertEqual(cycleBestBefore, player.SimulatedBattlefield.BossTotalScore,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} clear leaves the cycle best total unchanged");
                AssertEqual(cycleCurrentBefore, player.SimulatedBattlefield.BossCurrentTotalScore,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} clear leaves the cycle current total unchanged");
                AssertEqual(challengeCountBefore, player.SimulatedBattlefield.BossChallengeCount,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} does not consume normal attempts");
                int firstClearTaskProgressAfterFirstClear = FirstClearTaskProgress();
                AssertEqual(true, firstClearTaskProgressAfterFirstClear > firstClearTaskProgressBefore,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} first clear advances first-clear tasks");
                player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                    playerCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Pain Cage Codex save did not persist Player task progress."));
                harness.Session.player = player;
                harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Pain Cage Codex save did not persist Stage score."));
                PreFightResponse repeatPreFight = StartFight(82_350 + packetBase, stageId, stageType);
                AssertEqual(0, repeatPreFight.Code,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} repeat pre-fight code");
                _ = SettleFight(
                    82_351 + packetBase,
                    repeatPreFight,
                    stage,
                    characterHp: 100,
                    bossHp: 0,
                    fightSeconds: fightSeconds);
                BossSingleSaveScoreResponse repeatSave = SaveScore(
                    82_352 + packetBase,
                    stageId,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} repeated save after reload",
                    out _);
                AssertEqual(0, repeatSave.Code,
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} repeated save code");
                AssertEqual(firstClearTaskProgressAfterFirstClear, FirstClearTaskProgress(),
                    $"Pain Cage {(bestiary ? "bestiary" : "trial")} repeated save does not regrant first-clear tasks");
            }

            ExerciseAuxiliaryStage(bestiary: false, stageType: 2, fightSeconds: 100, packetBase: 82_010);
            ExerciseAuxiliaryStage(bestiary: true, stageType: 4, fightSeconds: 12, packetBase: 82_020);
            AssertEqual(true, auxiliaryTimeScores[0].Score < auxiliaryTimeScores[0].Cap
                && auxiliaryTimeScores[1].Score < auxiliaryTimeScores[1].Cap,
                "Pain Cage partial remaining-time scores are below their caps");
            AssertEqual(true, auxiliaryTimeScores[0].Score != auxiliaryTimeScores[1].Score,
                "Pain Cage remaining-time scores vary proportionally with distinct durations");

            // A saved codex best followed by a lower run must keep the settlement history at the best, otherwise
            // the client offers to discard the worse score as though it were a new record.
            {
                int codexStageId = AuxiliaryStageId(bestiary: true);
                BossSingleStageTable codexStage = stages.Single(row => row.StageId == codexStageId);
                int codexBest = player.SimulatedBattlefield.BossBestiaryScores[codexStageId];
                PreFightResponse lowerCodexPreFight = StartFight(82_023, codexStageId, stageType: 4);
                AssertEqual(0, lowerCodexPreFight.Code, "Pain Cage bestiary lower-run pre-fight code");
                FightSettleResponse lowerCodexSettle = SettleFight(
                    82_024,
                    lowerCodexPreFight,
                    codexStage,
                    characterHp: 100,
                    bossHp: 0,
                    fightSeconds: 240);
                BossSingleFightResult lowerCodexResult = RequiredBossResult(
                    lowerCodexSettle,
                    "Pain Cage bestiary lower-run result");
                AssertEqual(true, lowerCodexResult.TotalScore < codexBest,
                    "Pain Cage bestiary lower-run fixture scores below the saved best");
                BossSingleSaveScoreResponse lowerCodexSave = SaveScore(
                    82_025,
                    codexStageId,
                    "Pain Cage bestiary lower-run save",
                    out List<string> lowerCodexPushes);
                AssertEqual(0, lowerCodexSave.Code, "Pain Cage bestiary lower-run save code");
                AssertEqual(true, lowerCodexPushes.Contains(nameof(NotifyStageData)),
                    "Pain Cage bestiary lower-run pushes the stage datum");
                AssertEqual(codexBest, player.SimulatedBattlefield.BossBestiaryScores[codexStageId],
                    "Pain Cage bestiary lower run keeps the saved best");
                AssertEqual(codexBest, harness.Session.stage.Stages[codexStageId].Score,
                    "Pain Cage bestiary lower run keeps the settlement history at the saved best");
            }
            List<BossSingleChallengeGradeTable> challengeGrades = TableReaderV2.Parse<BossSingleChallengeGradeTable>();
            List<BossSingleChallengeFeatureGroupTable> challengeGroups = TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>();
            int preIntensiveLevelType = player.SimulatedBattlefield.BossLevelType;
            List<int> preIntensiveBossList = player.SimulatedBattlefield.BossList.ToList();
            List<AscNet.Common.Database.BossSingleStageRecordState> preIntensiveRecords = player.SimulatedBattlefield.BossStageRecords.ToList();
            int preIntensiveTotal = player.SimulatedBattlefield.BossTotalScore;
            int preIntensiveCurrent = player.SimulatedBattlefield.BossCurrentTotalScore;
            int preIntensiveMax = player.SimulatedBattlefield.BossMaxScore;
            int preIntensiveSection = player.SimulatedBattlefield.BossChallengeSelectedSection;
            int preIntensiveFeatureGroup = player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup;
            List<AscNet.Common.Database.BossSingleChallengeHistoryRecordState> preIntensiveHistory = player.SimulatedBattlefield.BossChallengeHistory.ToList();
            player.SimulatedBattlefield.BossLevelType = grades.Where(row => row.GradeType >= challengeGrades.Single().NeedGradeType).OrderBy(row => row.GradeType).First().LevelType;
            player.SimulatedBattlefield.BossList = groups.Single(row => row.Id == grades.Single(value => value.LevelType == player.SimulatedBattlefield.BossLevelType).GroupId.First()).SectionId.ToList();
            player.SimulatedBattlefield.BossStageRecords =
            [
                new AscNet.Common.Database.BossSingleStageRecordState
                {
                    StageId = stages.First().StageId,
                    Score = challengeGrades.Single().NeedScore,
                    MaxScore = challengeGrades.Single().NeedScore
                }
            ];
            player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup = challengeGroups.First(row => row.BuffGroupIds.Any(id => id > 0)).Id;
            NotifyFubenBossSingleData challengeLogin = BuildLogin(player, null);
            BossSingleSectionTable challengeSection = sections
                .Single(row => row.Id == challengeLogin.FubenBossSingleData.ChallengeSectionId
                    && row.AfreshId == sections.Max(section => section.AfreshId));
            int challengeStageId = challengeSection.StageId.First();
            BossSingleChallengeFeatureGroupTable challengeFeatureGroup = challengeGroups
                .Single(row => row.Id == challengeLogin.FubenBossSingleData.ChallengeFeatureGroupId);
            AssertEqual(3, challengeSection.StageId.Count,
                "Pain Cage intensive current section has three stages");
            AssertEqual(3, challengeFeatureGroup.FeatureIds.Count,
                "Pain Cage intensive feature group has three affixes");
            AssertEqual(true, challengeSection.StageId.Zip(challengeFeatureGroup.FeatureIds).All(pair =>
                    challengeSection.StageId.Contains(pair.First)
                    && challengeFeatureGroup.FeatureIds.Contains(pair.Second)),
                "Pain Cage intensive stage and affix join preserves table order");
            int challengeBuffGroup = challengeGroups.Single(row => row.Id == challengeLogin.FubenBossSingleData.ChallengeFeatureGroupId).BuffGroupIds.First(id => id > 0);
            PreFightResponse intensivePreFight = StartFight(82_030, challengeStageId, stageType: 3, buffGroup: challengeBuffGroup);
            AssertEqual(0, intensivePreFight.Code, "Pain Cage intensive type3 pre-fight");
            int challengeFeatureEvent = TableReaderV2.Parse<BossSingleChallengeFeatureTable>()
                .Single(row => row.Id == challengeFeatureGroup.FeatureIds[
                    challengeFeatureGroup.BuffGroupIds.IndexOf(challengeBuffGroup)])
                .FightEventIds;
            AssertEqual(true, challengeFeatureEvent <= 0
                || intensivePreFight.FightData.EventIds.Contains(challengeFeatureEvent),
                "Pain Cage intensive module applies its table-derived fight event");
            BossSingleStageTable intensiveStage = stages.Single(row => row.StageId == challengeStageId);
            FightSettleResponse intensiveSettle = SettleFight(82_031, intensivePreFight, intensiveStage, 100, 0, fightSeconds: 8);
            BossSingleFightResult intensiveResult = RequiredBossResult(intensiveSettle, "Pain Cage intensive result");
            BossSingleSaveScoreResponse intensiveSave = SaveScore(82_032, challengeStageId, "Pain Cage intensive save", out List<string> intensivePushes);
            AssertEqual(0, intensiveSave.Code, "Pain Cage intensive save");
            AssertEqual(true, intensivePushes.Contains(nameof(NotifyBossSingleRankInfo)), "Pain Cage intensive rank push");
            AssertEqual(intensiveResult.TotalScore, player.SimulatedBattlefield.BossChallengeHistory.Single(row => row.StageId == challengeStageId).Score, "Pain Cage intensive history");
            dynamic intensiveHistoryBuffGroup = BuildLogin(player, null).FubenBossSingleData
                .ChallengeStageHistoryList.Single(row => row.StageId == challengeStageId).BuffGroup
                ?? throw new InvalidDataException("Pain Cage intensive history BuffGroup is nil.");
            AssertEqual(challengeBuffGroup, (int)intensiveHistoryBuffGroup["BuffGroupId"],
                "Pain Cage intensive history emits the client BuffGroup object");
            AssertEqual(0, ((Dictionary<int, int>)intensiveHistoryBuffGroup["BuffChoices"]).Count,
                "Pain Cage direct BuffGroup keeps empty choice map");
            player.SimulatedBattlefield.BossChallengeHistory.Add(new AscNet.Common.Database.BossSingleChallengeHistoryRecordState { StageId = challengeSection.StageId[1], Score = intensiveResult.TotalScore + 1 });
            player.SimulatedBattlefield.BossChallengeHistory.Add(new AscNet.Common.Database.BossSingleChallengeHistoryRecordState { StageId = challengeSection.StageId[2], Score = intensiveResult.TotalScore - 1 });
            int unrelatedChallengeStageId = sections
                .SelectMany(row => row.StageId)
                .First(stageId => !challengeSection.StageId.Contains(stageId));
            player.SimulatedBattlefield.BossChallengeHistory.Add(
                new AscNet.Common.Database.BossSingleChallengeHistoryRecordState
                {
                    StageId = unrelatedChallengeStageId,
                    Score = intensiveResult.TotalScore + 100
                });
            int[] intensiveScores = player.SimulatedBattlefield.BossChallengeHistory
                .Where(row => challengeSection.StageId.Contains(row.StageId))
                .Select(row => row.Score)
                .ToArray();
            AssertEqual(3, intensiveScores.Distinct().Count(), "Pain Cage intensive regression uses three distinct scores");
            AssertEqual(intensiveScores.Sum(), BuildLogin(player, null).FubenBossSingleData.ChallengeTotalScore,
                "Pain Cage intensive display total sums only current-section stages");
            player.SimulatedBattlefield.BossChallengeHistory.RemoveAll(
                row => row.StageId == unrelatedChallengeStageId);
            const int intensiveRankPacketId = 82_036;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetChallengeRankRequest),
                harness.Session,
                intensiveRankPacketId,
                new BossSingleGetChallengeRankRequest { StageId = 0 });
            BossSingleGetChallengeRankResponse intensiveRank =
                ReadResponsePayload<BossSingleGetChallengeRankResponse>(
                    harness,
                    intensiveRankPacketId,
                    nameof(BossSingleGetChallengeRankResponse),
                    "Pain Cage intensive aggregate rank");
            AssertEqual(0, intensiveRank.Code, "Pain Cage intensive aggregate rank code");
            int rankStageCount = challengeGrades.Single(row => row.LevelType == challengeLogin.FubenBossSingleData.ChallengeLevelType).RankStageNum;
            AssertEqual(intensiveScores.OrderByDescending(score => score).Take(rankStageCount).Sum(), intensiveRank.Score,
                "Pain Cage intensive aggregate rank retains table top-N total");
            AscNet.Common.Database.Player intensiveReload = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBsonDocument());
            AssertEqual(3, intensiveReload.SimulatedBattlefield.BossChallengeHistory.Count, "Pain Cage intensive relog history");
            BossSingleChallengeBuffGroupTable challengeBuffChoice = TableReaderV2
                .Parse<BossSingleChallengeBuffGroupTable>()
                .First(row => row.BuffGroupId == challengeBuffGroup && row.Index > 0 && row.Buff.Count > 0);
            int choiceFeatureEvent = TableReaderV2.Parse<BossSingleChallengeFeatureTable>()
                .Single(row => row.Id == challengeBuffChoice.Buff[0]).FightEventIds;
            PreFightResponse clientShapedPreFight = StartFight(
                82_033,
                challengeStageId,
                stageType: 3,
                buffGroup: new Dictionary<string, object>
                {
                    ["BuffGroupId"] = challengeBuffGroup,
                    ["BuffChoices"] = new Dictionary<int, int> { [challengeBuffChoice.Index] = 1 }
                });
            AssertEqual(0, clientShapedPreFight.Code,
                "Pain Cage client-shaped intensive module pre-fight");
            AssertEqual(true, clientShapedPreFight.FightData.EventIds.Contains(challengeFeatureEvent)
                && clientShapedPreFight.FightData.EventIds.Contains(choiceFeatureEvent),
                "Pain Cage client-shaped module applies base and selected table-derived events");
            player.SimulatedBattlefield.BossChallengeHistory.RemoveAll(row => row.StageId == challengeStageId);
            _ = SettleFight(82_034, clientShapedPreFight, intensiveStage, 100, 0, fightSeconds: 8);
            _ = SaveScore(82_035, challengeStageId, "Pain Cage client-shaped intensive save", out _);
            dynamic persistedChoiceHistory = BuildLogin(player, null).FubenBossSingleData
                .ChallengeStageHistoryList.Single(row => row.StageId == challengeStageId).BuffGroup
                ?? throw new InvalidDataException("Pain Cage intensive choice history BuffGroup is nil.");
            Dictionary<int, int> persistedChoices = (Dictionary<int, int>)persistedChoiceHistory["BuffChoices"];
            AssertEqual(1, persistedChoices[challengeBuffChoice.Index],
                "Pain Cage intensive history persists selected BuffChoices");
            AscNet.Common.Database.Player choiceReload = MongoDB.Bson.Serialization.BsonSerializer
                .Deserialize<AscNet.Common.Database.Player>(player.ToBsonDocument());
            AssertEqual(1, choiceReload.SimulatedBattlefield.BossChallengeHistory
                .Single(row => row.StageId == challengeStageId).BuffChoices[challengeBuffChoice.Index],
                "Pain Cage intensive BuffChoices BSON round-trip");
            player.SimulatedBattlefield.BossLevelType = preIntensiveLevelType;
            player.SimulatedBattlefield.BossList = preIntensiveBossList;
            player.SimulatedBattlefield.BossStageRecords = preIntensiveRecords;
            player.SimulatedBattlefield.BossTotalScore = preIntensiveTotal;
            player.SimulatedBattlefield.BossCurrentTotalScore = preIntensiveCurrent;
            player.SimulatedBattlefield.BossMaxScore = preIntensiveMax;
            player.SimulatedBattlefield.BossChallengeSelectedSection = preIntensiveSection;
            player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup = preIntensiveFeatureGroup;
            player.SimulatedBattlefield.BossChallengeHistory = preIntensiveHistory;


            List<int> selectedStageIds = selectedSections
                .SelectMany(sectionId => sections.Single(row => row.SectionId == sectionId && row.AfreshId == 1).StageId)
                .Distinct()
                .ToList();
            BossSingleStageTable normalStage = stages
                .Where(row => selectedStageIds.Contains(row.StageId) && row.AutoFight != 0)
                .OrderBy(row => row.StageId)
                .First();
            int normalSectionId = selectedSections.Single(sectionId =>
                sections.Single(row => row.SectionId == sectionId && row.AfreshId == 1).StageId.Contains(normalStage.StageId));

            List<(int SectionId, BossSingleStageTable Opening, BossSingleStageTable Final, uint CharacterId)> historyStages =
                selectedSections
                    .Take(3)
                    .Select((sectionId, index) =>
                    {
                        List<int> stageIds = sections
                            .Single(row => row.SectionId == sectionId && row.AfreshId == 1)
                            .StageId;
                        return (
                            sectionId,
                            stages.Single(row => row.StageId == stageIds.First()),
                            stages.Single(row => row.StageId == stageIds.Last()),
                            historyCharacterIds[index]);
                    })
                    .ToList();
            AssertEqual(3, historyStages.Count, "Pain Cage table-selected three-section history fixture");
            AssertEqual(true, historyStages.All(fixture => fixture.Opening.StageId != fixture.Final.StageId),
                "Pain Cage table-selected sections have distinct opening and final stages");

            // The five table-selected stage clears below must fit the grade's
            // table-defined attempt limit for this compatibility scenario.
            BossSingleGradeTable stageHistoryGrade = grades
                .Where(row => row.ChallengeCount >= historyStages.Count + 2)
                .OrderBy(row => row.LevelType)
                .First();
            player.SimulatedBattlefield.BossLevelType = stageHistoryGrade.LevelType;
            foreach (var historyStage in historyStages)
            {
                int packetBase = 82_100 + historyStages.FindIndex(fixture => fixture.CharacterId == historyStage.CharacterId) * 10;
                PreFightResponse openingPreFight = StartFight(
                    packetBase,
                    historyStage.Opening.StageId,
                    stageType: 1,
                    [historyStage.CharacterId]);
                AssertEqual(0, openingPreFight.Code,
                    $"Pain Cage section opening {historyStage.Opening.StageId} pre-fight code");
                FightSettleResponse openingSettle = SettleFight(
                    packetBase + 1,
                    openingPreFight,
                    historyStage.Opening,
                    100,
                    0);
                _ = RequiredBossResult(openingSettle, $"Pain Cage section opening {historyStage.Opening.StageId} result");
                BossSingleSaveScoreResponse openingSave = SaveScore(
                    packetBase + 2,
                    historyStage.Opening.StageId,
                    $"Pain Cage section opening {historyStage.Opening.StageId} save",
                    out _);
                AssertEqual(0, openingSave.Code, $"Pain Cage section opening {historyStage.Opening.StageId} save code");
            }
            AssertEqual(3, player.SimulatedBattlefield.BossChallengeCount,
                "Retail PPC Attempts count each first clear of a distinct stage");

            foreach (var historyStage in historyStages.Take(2))
            {
                int packetBase = 82_140 + historyStages.FindIndex(fixture => fixture.CharacterId == historyStage.CharacterId) * 10;
                PreFightResponse finalPreFight = StartFight(
                    packetBase,
                    historyStage.Final.StageId,
                    stageType: 1,
                    [historyStage.CharacterId]);
                AssertEqual(0, finalPreFight.Code, $"Pain Cage final stage {historyStage.Final.StageId} pre-fight code");
                FightSettleResponse finalSettle = SettleFight(
                    packetBase + 1,
                    finalPreFight,
                    historyStage.Final,
                    100,
                    0);
                _ = RequiredBossResult(finalSettle, $"Pain Cage final stage {historyStage.Final.StageId} result");
                BossSingleSaveScoreResponse finalSave = SaveScore(
                    packetBase + 2,
                    historyStage.Final.StageId,
                    $"Pain Cage final stage {historyStage.Final.StageId} save",
                    out _);
                AssertEqual(0, finalSave.Code, $"Pain Cage final stage {historyStage.Final.StageId} save code");
            }
            AssertEqual(5, player.SimulatedBattlefield.BossChallengeCount,
                "Retail PPC Attempts count multiple distinct stage clears in the same boss");
            var retriedFinalStage = historyStages[0];
            uint lowerScoreCharacterId = historyStages[2].CharacterId;
            int attemptsBeforeStageReplay = player.SimulatedBattlefield.BossChallengeCount;
            PreFightResponse lowerScorePreFight = StartFight(
                82_170,
                retriedFinalStage.Final.StageId,
                stageType: 1,
                [lowerScoreCharacterId]);
            AssertEqual(0, lowerScorePreFight.Code,
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower-score pre-fight code");
            FightSettleResponse lowerScoreSettle = SettleFight(
                82_171,
                lowerScorePreFight,
                retriedFinalStage.Final,
                characterHp: 0,
                bossHp: 100,
                isWin: false);
            BossSingleFightResult lowerScoreResult = RequiredBossResult(
                lowerScoreSettle,
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower-score result");
            AscNet.Common.Database.BossSingleStageRecordState bestFinalRecord = player.SimulatedBattlefield.BossStageRecords
                .Single(record => record.StageId == retriedFinalStage.Final.StageId);
            AssertEqual(true, lowerScoreResult.TotalScore < bestFinalRecord.MaxScore,
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower-score fixture");
            BossSingleSaveScoreResponse lowerScoreSave = SaveScore(
                82_172,
                retriedFinalStage.Final.StageId,
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower-score save",
                out _);
            AssertEqual(0, lowerScoreSave.Code,
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower-score save code");
            AssertEqual(attemptsBeforeStageReplay, player.SimulatedBattlefield.BossChallengeCount,
                "Retail PPC replay of a cleared stage consumes no additional Attempt");
            AssertIntegerList(
                [retriedFinalStage.CharacterId],
                bestFinalRecord.MaxCharacters.Select(Convert.ToInt64).ToArray(),
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower score preserves best team");
            AssertIntegerList(
                [lowerScoreCharacterId],
                bestFinalRecord.Characters.Select(Convert.ToInt64).ToArray(),
                $"Pain Cage final stage {retriedFinalStage.Final.StageId} lower score retains latest team");

            void AssertFinalStageHistory(AscNet.Common.Database.Player target, string name)
            {
                JObject data = RequiredValue<JObject>(
                    JObject.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(BuildLogin(target, null)))),
                    "FubenBossSingleData",
                    JTokenType.Object,
                    name);
                JArray stageRecords = RequiredValue<JArray>(data, "StageRecordList", JTokenType.Array, name);
                foreach (var historyStage in historyStages.Take(2))
                {
                    JObject record = stageRecords
                        .OfType<JObject>()
                        .Single(value => RequiredValue<int>(value, "StageId", JTokenType.Integer, name)
                            == historyStage.Final.StageId);
                    uint expectedCurrentCharacterId = historyStage.Final.StageId == retriedFinalStage.Final.StageId
                        ? lowerScoreCharacterId
                        : historyStage.CharacterId;
                    AssertIntegerList(
                        [expectedCurrentCharacterId],
                        RequiredValue<JArray>(record, "Characters", JTokenType.Array, name)
                            .Select(value => value.Value<long>())
                            .ToArray(),
                        $"{name} final stage {historyStage.Final.StageId} current team");
                    AssertIntegerList(
                        [historyStage.CharacterId],
                        RequiredValue<JArray>(record, "MaxCharacters", JTokenType.Array, name)
                            .Select(value => value.Value<long>())
                            .ToArray(),
                        $"{name} final stage {historyStage.Final.StageId} best team");
                }
            }

            AssertFinalStageHistory(player, "Pain Cage saved final-stage Team History");
            AscNet.Common.Database.Player historyReloaded =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(player.ToBsonDocument());
            AssertFinalStageHistory(historyReloaded, "Pain Cage relogged final-stage Team History");

            player.SimulatedBattlefield.BossChallengeCount = 0;
            player.SimulatedBattlefield.BossAutoFightCount = 0;
            player.SimulatedBattlefield.BossCharacterPoints.Clear();
            player.SimulatedBattlefield.BossHistory.Clear();
            player.SimulatedBattlefield.BossStageRecords.Clear();
            player.SimulatedBattlefield.BossResetStageIds.Clear();
            player.SimulatedBattlefield.BossNormalStageTeams.Clear();
            player.SimulatedBattlefield.BossTotalScore = 0;
            player.SimulatedBattlefield.BossCurrentTotalScore = 0;
            player.SimulatedBattlefield.BossMaxScore = 0;
            player.SimulatedBattlefield.BossLastScoreTime = 0;
            player.SimulatedBattlefield.BossLevelType = selectedLevel;
            player.SimulatedBattlefield.BossList = selectedSections.ToList();
            BossSingleSectionTable attemptSection = selectedSections
                .Select(sectionId => sections.Single(row =>
                    row.SectionId == sectionId && row.AfreshId == currentAfreshId))
                .First(section => section.StageId.Count >= 3
                    && section.StageId.Count(stageId =>
                        stages.Single(stage => stage.StageId == stageId).AutoFight != 0) >= 2);
            List<BossSingleStageTable> attemptStages = attemptSection.StageId
                .Select(stageId => stages.Single(stage => stage.StageId == stageId))
                .ToList();
            int staleCycleStageId = historyStages
                .SelectMany(historyStage => new[]
                {
                    historyStage.Opening.StageId,
                    historyStage.Final.StageId
                })
                .First(stageId =>
                    !attemptStages.Any(stage => stage.StageId == stageId)
                    && !player.SimulatedBattlefield.BossTrialScores.ContainsKey(stageId)
                    && !player.SimulatedBattlefield.BossBestiaryScores.ContainsKey(stageId)
                    && !player.SimulatedBattlefield.BossChallengeHistory.Any(record => record.StageId == stageId)
                    && harness.Session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? datum)
                    && datum.Score > 0);
            StageDatum staleCycleStage = harness.Session.stage.Stages[(uint)staleCycleStageId];
            bool staleCyclePassed = staleCycleStage.Passed;

            List<BossSingleStageTable> autoAttemptStages = attemptStages
                .Where(stage => stage.AutoFight != 0)
                .Take(2)
                .ToList();
            BossSingleStageTable manualAttemptStage = attemptStages
                .First(stage => autoAttemptStages.All(autoStage => autoStage.StageId != stage.StageId));
            player.SimulatedBattlefield.BossList = [attemptSection.SectionId];
            byte[] preEligibilityPlayer = player.ToBson();
            byte[] preEligibilityStage = harness.Session.stage.ToBson();
            // A newly saved manual clear is current-cycle state, not Auto Clear history.
            BossSingleStageTable firstArchivedStage = autoAttemptStages[0];
            StageDatum firstArchivedDatum = harness.Session.stage.Stages[(uint)firstArchivedStage.StageId];
            firstArchivedDatum.Passed = false;
            firstArchivedDatum.LastPassTime = 0;
            PreFightResponse firstArchivedPreFight = StartFight(82_280, firstArchivedStage.StageId, 1, [characterId]);
            AssertEqual(0, firstArchivedPreFight.Code, "Pain Cage first-ever clear pre-fight");
            _ = SettleFight(82_281, firstArchivedPreFight, firstArchivedStage, 100, 0);
            AssertEqual(0, SaveScore(82_282, firstArchivedStage.StageId, "Pain Cage first-ever clear", out _).Code,
                "Pain Cage first-ever manual clear saves");
            AssertEqual(false, player.SimulatedBattlefield.BossHistory.Any(record =>
                record.StageId == firstArchivedStage.StageId), "Pain Cage manual clear is not archived immediately");
            int firstClearAttempts = player.SimulatedBattlefield.BossChallengeCount;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                82_283,
                new BossSingleAutoFightRequest { StageId = firstArchivedStage.StageId });
            AssertEqual(1, ReadResponsePayload<BossSingleAutoFightResponse>(
                harness, 82_283, nameof(BossSingleAutoFightResponse), "Pain Cage unarchived Auto Clear rejection").Code,
                "Pain Cage unarchived Auto Clear request rejects");
            AssertEqual(firstClearAttempts, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage rejected unarchived Auto Clear leaves Attempts unchanged");
            AssertEqual(false, player.SimulatedBattlefield.BossHistory.Any(record =>
                record.StageId == firstArchivedStage.StageId), "Pain Cage rejected Auto Clear does not create history");

            int currentBossActivity = player.SimulatedBattlefield.BossActivityNo;
            if (currentBossActivity <= 1)
                throw new InvalidDataException("Pain Cage weekly rollover fixture requires a prior activity.");
            player.SimulatedBattlefield.BossActivityNo = currentBossActivity - 1;
            harness.Session.stage.BossSingleActivityNo = currentBossActivity - 1;
            MethodInfo reconcileBoss = RequiredMethod(
                bossModule,
                "ReconcileLive",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            reconcileBoss.Invoke(null, [harness.Session]);
            bool sawRolloverStagePush = false;
            for (int packetIndex = 0; packetIndex < 16; packetIndex++)
            {
                Packet rolloverPacket = harness.ReadPacket($"Pain Cage rollover packet {packetIndex + 1}");
                if (rolloverPacket.Type != Packet.ContentType.Push)
                    throw new InvalidDataException("Pain Cage rollover emitted an unexpected response packet.");
                Packet.Push rolloverPush = MessagePackSerializer.Deserialize<Packet.Push>(rolloverPacket.Content);
                if (rolloverPush.Name == nameof(NotifyStageData))
                    sawRolloverStagePush = true;
                if (sawRolloverStagePush)
                    break;
            }
            AssertEqual(true, sawRolloverStagePush, "Pain Cage rollover notifies the stage ownership reset");
            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Pain Cage rollover did not persist Player."));
            harness.Session.player = player;
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Pain Cage rollover did not persist Stage."));
            AssertEqual(currentBossActivity, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage rollover reload advances activity");
            AssertEqual(true, player.SimulatedBattlefield.BossHistory.Any(record =>
                record.StageId == firstArchivedStage.StageId), "Pain Cage rollover archives the manual clear");
            AssertEqual(false, player.SimulatedBattlefield.BossStageRecords.Any(record =>
                record.StageId == firstArchivedStage.StageId), "Pain Cage rollover resets current-cycle records");
            player.SimulatedBattlefield.BossLevelType = stageHistoryGrade.LevelType;
            player.SimulatedBattlefield.BossList = [attemptSection.SectionId];
            player.Save();
            _ = BuildLogin(player, null);
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                82_284,
                new BossSingleAutoFightRequest { StageId = firstArchivedStage.StageId });
            AssertEqual(0, ReadAfterPushes<BossSingleAutoFightResponse>(
                82_284,
                nameof(BossSingleAutoFightResponse),
                "Pain Cage archived Auto Clear after reload",
                out _).Code, "Pain Cage archived Auto Clear succeeds after reload");
            AssertEqual(1, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage new-week first Auto Clear consumes one Attempt");
            int autoClearAttempts = player.SimulatedBattlefield.BossChallengeCount;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                82_285,
                new BossSingleAutoFightRequest { StageId = firstArchivedStage.StageId });
            AssertEqual(1, ReadResponsePayload<BossSingleAutoFightResponse>(
                harness, 82_285, nameof(BossSingleAutoFightResponse), "Pain Cage repeated archived Auto Clear").Code,
                "Pain Cage replay of an already-cleared stage rejects");
            AssertEqual(autoClearAttempts, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage replay consumes no Attempt");

            // A better current score must not replace the prior-period Auto Clear source before rollover.
            BossSingleStageTable betterCurrentStage = autoAttemptStages[1];
            StageDatum betterCurrentDatum = harness.Session.stage.Stages[(uint)betterCurrentStage.StageId];
            betterCurrentDatum.Passed = false;
            betterCurrentDatum.LastPassTime = 0;
            player.SimulatedBattlefield.BossHistory.Add(new AscNet.Common.Database.BossSingleHistoryRecordState
            {
                StageId = betterCurrentStage.StageId,
                Score = 1,
                Characters = [checked((int)characterId)],
                Partners = [0]
            });
            PreFightResponse betterCurrentPreFight = StartFight(82_286, betterCurrentStage.StageId, 1, [characterId]);
            AssertEqual(0, betterCurrentPreFight.Code, "Pain Cage archived-history manual pre-fight");
            _ = SettleFight(82_287, betterCurrentPreFight, betterCurrentStage, 100, 0);
            AssertEqual(0, SaveScore(82_288, betterCurrentStage.StageId, "Pain Cage improved current-week score", out _).Code,
                "Pain Cage improved current-week score saves");
            AssertEqual(true, player.SimulatedBattlefield.BossStageRecords.Single(record =>
                record.StageId == betterCurrentStage.StageId).MaxScore > 1,
                "Pain Cage current-week record improves on archived score");
            AssertEqual(1, player.SimulatedBattlefield.BossHistory.Single(record =>
                record.StageId == betterCurrentStage.StageId).Score,
                "Pain Cage current-week improvement leaves archived Auto Clear source unchanged");
            int betterScoreAttempts = player.SimulatedBattlefield.BossChallengeCount;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                82_289,
                new BossSingleAutoFightRequest { StageId = betterCurrentStage.StageId });
            AssertEqual(1, ReadResponsePayload<BossSingleAutoFightResponse>(
                harness, 82_289, nameof(BossSingleAutoFightResponse), "Pain Cage outdated Auto Clear rejection").Code,
                "Pain Cage stale archived score cannot overwrite a better current score");
            AssertEqual(betterScoreAttempts, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage stale archived Auto Clear rejection leaves Attempts unchanged");

            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(preEligibilityPlayer);
            harness.Session.player = player;
            harness.Session.player.Save();
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(preEligibilityStage);
            harness.Session.stage.Save();
            player.SimulatedBattlefield.BossHistory.AddRange(autoAttemptStages.Select(stage =>
                new AscNet.Common.Database.BossSingleHistoryRecordState
                {
                    StageId = stage.StageId,
                    Score = 1_000_000,
                    Characters = [checked((int)characterId)],
                    Partners = [0]
                }));

            for (int index = 0; index < autoAttemptStages.Count; index++)
            {
                int autoFightPacketId = 82_300 + index * 3;
                byte[]? firstStageReplacementBson = null;
                if (index == 0)
                    stageCollection.BeforeReplaceOne = replacement =>
                        firstStageReplacementBson ??= replacement.ToBson();
                try
                {
                    InvokeRegisteredRequestHandler(
                        nameof(BossSingleAutoFightRequest),
                        harness.Session,
                        autoFightPacketId,
                        new BossSingleAutoFightRequest { StageId = autoAttemptStages[index].StageId });
                }
                finally
                {
                    if (index == 0)
                        stageCollection.BeforeReplaceOne = null;
                }
                if (index == 0)
                {
                    AscNet.Common.Database.Stage reconciledStageSnapshot =
                        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                            firstStageReplacementBson
                            ?? throw new InvalidDataException("Pain Cage reconciliation did not replace Stage."));
                    AssertEqual(0L, reconciledStageSnapshot.Stages[(uint)staleCycleStageId].Score,
                        "Pain Cage first Auto Clear Stage replacement persists the reconciled cycle score");
                    AssertEqual(staleCyclePassed, reconciledStageSnapshot.Stages[(uint)staleCycleStageId].Passed,
                        "Pain Cage cycle score reconciliation preserves Stage Passed");
                    AscNet.Common.Database.Stage committedAutoStage =
                        MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                            stageCollection.LastSuccessfulReplacementBson
                            ?? throw new InvalidDataException("Pain Cage Auto Clear did not persist Stage."));
                    AssertEqual(0L, committedAutoStage.Stages[(uint)staleCycleStageId].Score,
                        "Pain Cage committed Auto Clear retains the persisted cycle score repair");
                }
                BossSingleAutoFightResponse autoResponse = ReadAfterPushes<BossSingleAutoFightResponse>(
                    autoFightPacketId,
                    nameof(BossSingleAutoFightResponse),
                    $"Retail PPC Auto Clear for stage {autoAttemptStages[index].StageId}",
                    out _);
                AssertEqual(0, autoResponse.Code,
                    $"Retail PPC Auto Clear stage {autoAttemptStages[index].StageId} succeeds");
                AssertEqual(index + 1, player.SimulatedBattlefield.BossChallengeCount,
                    $"Retail PPC first clear of Auto Clear stage {index + 1} consumes one Attempt");
            }

            PreFightResponse manualAttemptPreFight = StartFight(
                82_306,
                manualAttemptStage.StageId,
                stageType: 1,
                [characterId]);
            AssertEqual(0, manualAttemptPreFight.Code, "Pain Cage stage-scoped manual pre-fight");
            _ = SettleFight(82_307, manualAttemptPreFight, manualAttemptStage, 100, 0);
            BossSingleSaveScoreResponse manualAttemptSave = SaveScore(
                82_308,
                manualAttemptStage.StageId,
                "Pain Cage stage-scoped manual clear",
                out _);
            AssertEqual(0, manualAttemptSave.Code, "Pain Cage stage-scoped manual save");
            AssertEqual(3, player.SimulatedBattlefield.BossChallengeCount,
                "Retail contract: first clear of each distinct stage consumes an Attempt");
            const int autoReplayPacketId = 82_312;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                autoReplayPacketId,
                new BossSingleAutoFightRequest { StageId = autoAttemptStages[0].StageId });
            BossSingleAutoFightResponse autoReplay = ReadResponsePayload<BossSingleAutoFightResponse>(
                harness,
                autoReplayPacketId,
                nameof(BossSingleAutoFightResponse),
                "Retail PPC Auto Clear replay");
            AssertEqual(1, autoReplay.Code, "Retail PPC completed Auto Clear replay is rejected");
            AssertEqual(3, player.SimulatedBattlefield.BossChallengeCount,
                "Retail PPC Auto Clear replay consumes no additional Attempt");

            PreFightResponse replayAttemptPreFight = StartFight(
                82_309,
                autoAttemptStages[0].StageId,
                stageType: 1,
                [characterId]);
            AssertEqual(0, replayAttemptPreFight.Code, "Pain Cage cleared-stage replay pre-fight");
            _ = SettleFight(82_310, replayAttemptPreFight, autoAttemptStages[0], 100, 0);
            BossSingleSaveScoreResponse replayAttemptSave = SaveScore(
                82_311,
                autoAttemptStages[0].StageId,
                "Pain Cage cleared-stage replay",
                out _);
            AssertEqual(0, replayAttemptSave.Code, "Pain Cage cleared-stage replay save");
            AssertEqual(3, player.SimulatedBattlefield.BossChallengeCount,
                "Retail contract: first clear of each distinct stage consumes an Attempt; replay does not");
            AssertEqual(3, player.SimulatedBattlefield.BossCharacterPoints[checked((int)characterId)],
                "Pain Cage cleared-stage replay does not consume another character use");

            player.SimulatedBattlefield.BossChallengeCount = 0;
            player.SimulatedBattlefield.BossAutoFightCount = 0;
            player.SimulatedBattlefield.BossCharacterPoints.Clear();
            player.SimulatedBattlefield.BossHistory.Clear();
            player.SimulatedBattlefield.BossStageRecords.Clear();
            player.SimulatedBattlefield.BossNormalStageTeams.Clear();
            player.SimulatedBattlefield.BossTotalScore = 0;
            player.SimulatedBattlefield.BossCurrentTotalScore = 0;
            foreach (BossSingleStageTable attemptStage in attemptStages)
                harness.Session.stage.Stages.Remove(checked((uint)attemptStage.StageId));
            player.SimulatedBattlefield.BossList = selectedSections.ToList();

            PreFightResponse discardedPreFight = StartFight(82_024, normalStage.StageId, stageType: 1);
            AssertEqual(0, discardedPreFight.Code, "Pain Cage discarded-score pre-fight code");
            _ = SettleFight(82_025, discardedPreFight, normalStage, characterHp: 100, bossHp: 0);
            AssertEqual(normalStage.StageId, harness.Session.PendingBossSingleScore?.StageId ?? 0,
                "Pain Cage successful settlement remains provisional");
            InvokeRegisteredRequestHandler(
                nameof(LeaveFightRequest),
                harness.Session,
                82_026,
                new LeaveFightRequest());
            _ = ReadResponsePayload<LeaveFightResponse>(
                harness,
                82_026,
                nameof(LeaveFightResponse),
                "Pain Cage discard LeaveFightResponse");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage leaving without save discards provisional score");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage leaving without save leaves attempt count unchanged");
            AssertEqual(false, player.SimulatedBattlefield.BossCharacterPoints.ContainsKey(checked((int)characterId)),
                "Pain Cage leaving without save leaves character stamina unchanged");

            PreFightResponse retreatPreFight = StartFight(82_028, normalStage.StageId, stageType: 1);
            AssertEqual(0, retreatPreFight.Code, "Pain Cage retreat pre-fight code");
            FightSettleResponse retreatSettle = SettleFight(
                82_029,
                retreatPreFight,
                normalStage,
                characterHp: 100,
                bossHp: 100,
                isWin: false,
                isForceExit: true);
            AssertEqual(false, retreatSettle.Settle?.IsWin ?? true, "Pain Cage retreat settle result");
            AssertEqual(0, retreatSettle.Settle?.ChallengeCount ?? -1,
                "Pain Cage retreat settlement consumes no attempts");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage retreat leaves attempt count unchanged");
            AssertEqual(false, player.SimulatedBattlefield.BossCharacterPoints.ContainsKey(checked((int)characterId)),
                "Pain Cage retreat leaves character stamina unchanged");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage retreat leaves no provisional score");

            PreFightResponse deathPreFight = StartFight(82_027, normalStage.StageId, stageType: 1);
            AssertEqual(0, deathPreFight.Code, "Pain Cage death pre-fight code");
            FightSettleResponse deathSettle = SettleFight(
                82_028,
                deathPreFight,
                normalStage,
                characterHp: 0,
                bossHp: 100,
                isWin: false);
            AssertEqual(0, deathSettle.Code, "Pain Cage death settle code");
            AssertEqual(true, deathSettle.Settle?.IsWin ?? false, "Pain Cage death settle result");
            _ = RequiredBossResult(deathSettle, "Pain Cage death FightSettleResponse");
            AssertEqual(normalStage.StageId, harness.Session.PendingBossSingleScore?.StageId ?? 0,
                "Pain Cage death settlement remains provisional for save-score");
            InvokeRegisteredRequestHandler(
                nameof(LeaveFightRequest),
                harness.Session,
                82_029,
                new LeaveFightRequest());
            _ = ReadResponsePayload<LeaveFightResponse>(
                harness,
                82_029,
                nameof(LeaveFightResponse),
                "Pain Cage death discard LeaveFightResponse");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage death discard clears provisional score");

            const int normalPreFightPacketId = 82_030;
            PreFightResponse normalPreFight = StartFight(normalPreFightPacketId, normalStage.StageId, stageType: 1);
            AssertEqual(0, normalPreFight.Code, "Pain Cage normal PreFightResponse code");
            AssertEqual(checked((uint)normalStage.StageId), normalPreFight.FightData.StageId,
                "Pain Cage normal PreFightResponse StageId");
            AssertEqual(1, normalPreFight.FightData.FightCheckType,
                "Pain Cage normal PreFightResponse fight check type");
            AssertEqual(normalStage.PassTimeLimit, normalPreFight.FightData.PassTimeLimit,
                "Pain Cage normal table time limit");
            AssertIntegerList(
                [characterId],
                player.SimulatedBattlefield.BossNormalStageTeams[normalSectionId].Select(Convert.ToInt64).ToArray(),
                "Pain Cage pre-fight team persistence");

            const int normalFightSeconds = 20;
            const int normalSettlePacketId = 82_031;
            FightSettleResponse normalSettle = SettleFight(
                normalSettlePacketId,
                normalPreFight,
                normalStage,
                characterHp: 100,
                bossHp: 0,
                fightSeconds: normalFightSeconds);
            BossSingleFightResult normalResult = RequiredBossResult(
                normalSettle,
                "Pain Cage normal FightSettleResponse");
            BossSingleScoreRuleTable normalRule = scoreRules.Single(row => row.Id == normalStage.StageId);
            int coefficientIndex = selectedLevel - 1;
            int expectedBossScore = Math.Min(
                normalStage.BossLoseHpScore,
                checked((int)Math.Floor(
                    1d / normalRule.BossLoseHp[coefficientIndex]
                    * normalRule.BossLoseHpScore[coefficientIndex])));
            double timeCoefficient = normalRule.LeftTimeScore[coefficientIndex];
            int expectedTimeScore = Math.Min(normalStage.LeftTimeScore,
                checked((int)Math.Floor((normalStage.PassTimeLimit - normalFightSeconds) * timeCoefficient * normalStage.PassTimeLimit)));
            double hpCoefficient = normalRule.CharLeftHpSocre[coefficientIndex];
            int expectedHpScore = normalRule.BaseScore
                + Math.Min(normalStage.LeftHpScore, checked((int)Math.Floor(100 * hpCoefficient)));
            int expectedTotalScore = Math.Min(
                normalStage.Score + normalRule.BaseScore,
                expectedBossScore + expectedTimeScore + expectedHpScore);
            AssertEqual(20, normalResult.FightTime, "Pain Cage frame-derived fight time");
            AssertEqual(100, normalResult.BossDamagePer, "Pain Cage boss damage percentage");
            AssertEqual(expectedBossScore, normalResult.BossDamageScore, "Pain Cage boss damage score");
            AssertEqual(expectedTimeScore, normalResult.TimeScore, "Pain Cage remaining-time score");
            AssertEqual(expectedHpScore, normalResult.HpScore, "Pain Cage character-HP score");
            AssertEqual(expectedTotalScore, normalResult.TotalScore, "Pain Cage total score");
            AssertEqual(0, normalSettle.Settle?.ChallengeCount ?? -1,
                "Pain Cage settlement does not consume client-side stamina before save");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage settlement does not consume an attempt before save");
            AssertEqual(false, player.SimulatedBattlefield.BossCharacterPoints.ContainsKey(checked((int)characterId)),
                "Pain Cage settlement does not consume character stamina before save");
            AssertEqual(0, player.SimulatedBattlefield.BossStageRecords.Count,
                "Pain Cage settle is provisional before save-score");
            AssertEqual(normalStage.StageId, harness.Session.PendingBossSingleScore?.StageId ?? 0,
                "Pain Cage provisional score session state");
            AssertEqual(0L, harness.Session.stage.Stages[(uint)staleCycleStageId].Score,
                "Pain Cage stale cycle datum remains synchronized before normal SaveScore");
            AssertEqual(staleCyclePassed, harness.Session.stage.Stages[(uint)staleCycleStageId].Passed,
                "Pain Cage pre-save cycle synchronization preserves Stage Passed");
            long normalStageScoreBeforeSave = harness.Session.stage.Stages.TryGetValue(
                (uint)normalStage.StageId,
                out StageDatum? normalStageDatumBeforeSave)
                    ? normalStageDatumBeforeSave.Score
                    : 0;


            int priorArchivedScore = Math.Max(1, normalResult.TotalScore / 2);
            player.SimulatedBattlefield.BossHistory.Add(new AscNet.Common.Database.BossSingleHistoryRecordState
            {
                StageId = normalStage.StageId,
                Score = priorArchivedScore,
                Characters = [checked((int)characterId)],
                Partners = [0]
            });
            int playerSavesBeforeNormalScore = playerCollection.ReplaceOneCalls;
            int stageSavesBeforeNormalScore = stageCollection.ReplaceOneCalls;
            const int normalSavePacketId = 82_032;
            BossSingleSaveScoreResponse normalSave = SaveScore(
                normalSavePacketId,
                normalStage.StageId,
                "Pain Cage normal save",
                out List<string> savePushes);
            AssertEqual(0, normalSave.Code, "Pain Cage normal save-score code");
            AssertEqual(playerSavesBeforeNormalScore + 1, playerCollection.ReplaceOneCalls,
                "Pain Cage normal save persists Player once");
            AssertEqual(stageSavesBeforeNormalScore + 1, stageCollection.ReplaceOneCalls,
                "Pain Cage normal SaveScore persists the committed Stage once");
            AscNet.Common.Database.Stage persistedNormalStage =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Pain Cage normal SaveScore did not persist Stage."));
            StageDatum persistedNormalStageDatum = persistedNormalStage.Stages.GetValueOrDefault((uint)normalStage.StageId)
                ?? throw new InvalidDataException("Pain Cage normal SaveScore replacement omitted the committed Stage datum.");
            AssertEqual(Math.Max(normalStageScoreBeforeSave, (long)normalResult.TotalScore),
                persistedNormalStageDatum.Score,
                "Pain Cage normal SaveScore persists the best Stage score");
            AssertEqual(true, persistedNormalStageDatum.Passed,
                "Pain Cage normal SaveScore persists Passed");

            int rankPushIndex = savePushes.IndexOf(nameof(NotifyBossSingleRankInfo));
            int stagePushIndex = savePushes.IndexOf(nameof(NotifyStageData), rankPushIndex + 1);
            int loginPushIndex = savePushes.IndexOf(nameof(NotifyFubenBossSingleData));
            if (rankPushIndex < 0 || stagePushIndex <= rankPushIndex || loginPushIndex <= stagePushIndex)
                throw new InvalidDataException(
                    $"Pain Cage save push order: expected rank, stage, login; got {string.Join(",", savePushes)}.");
            AscNet.Common.Database.BossSingleStageRecordState savedRecord =
                player.SimulatedBattlefield.BossStageRecords.Single(record => record.StageId == normalStage.StageId);
            AssertEqual(normalResult.TotalScore, savedRecord.Score, "Pain Cage current stage score persistence");
            AssertEqual(normalResult.TotalScore, savedRecord.MaxScore, "Pain Cage stage best score persistence");
            AssertEqual(normalResult.TotalScore, player.SimulatedBattlefield.BossCurrentTotalScore,
                "Pain Cage current total score");
            AssertEqual(normalResult.TotalScore, player.SimulatedBattlefield.BossTotalScore,
                "Pain Cage period best total score");
            AssertEqual(1, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage first-clear attempt consumption");
            AssertEqual(1, player.SimulatedBattlefield.BossCharacterPoints[checked((int)characterId)],
                "Pain Cage character stamina consumption");
            AscNet.Common.Database.BossSingleHistoryRecordState savedHistory =
                player.SimulatedBattlefield.BossHistory.Single(record => record.StageId == normalStage.StageId);
            AssertEqual(priorArchivedScore, savedHistory.Score,
                "Pain Cage current clear preserves prior archived score");
            AssertIntegerList(
                [characterId],
                savedHistory.Characters.Select(Convert.ToInt64).ToArray(),
                "Pain Cage current clear preserves prior archived team");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage save clears provisional session score");
            int stageSavesAfterNormalScore = stageCollection.ReplaceOneCalls;

            const int duplicateSavePacketId = 82_033;
            BossSingleSaveScoreResponse duplicateSave = SaveScore(
                duplicateSavePacketId,
                normalStage.StageId,
                "Pain Cage duplicate save",
                out List<string> duplicateSavePushes);
            AssertEqual(1, duplicateSave.Code, "Pain Cage duplicate save rejected");
            AssertEqual(0, duplicateSavePushes.Count, "Pain Cage duplicate save emits no pushes");
            AssertEqual(stageSavesAfterNormalScore, stageCollection.ReplaceOneCalls,
                "Pain Cage duplicate SaveScore does not persist Stage again");
            AssertEqual(1, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage duplicate save does not consume attempt");

            const int rankInfoPacketId = 82_034;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleRankInfoRequest),
                harness.Session,
                rankInfoPacketId,
                new BossSingleRankInfoRequest { SectionId = normalSectionId });
            BossSingleRankInfoResponse rankInfo = ReadResponsePayload<BossSingleRankInfoResponse>(
                harness,
                rankInfoPacketId,
                nameof(BossSingleRankInfoResponse),
                "Pain Cage personal rank response");
            AssertEqual(0, rankInfo.Code, "Pain Cage personal rank code");
            AssertEqual(1, rankInfo.Rank, "Pain Cage personal rank");
            AssertEqual(true, rankInfo.TotalRank >= 1, "Pain Cage personal rank population");

            const int rankListPacketId = 82_035;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetRankRequest),
                harness.Session,
                rankListPacketId,
                new BossSingleGetRankRequest { Level = selectedLevel, SectionId = normalSectionId });
            BossSingleGetRankResponse rankList = ReadResponsePayload<BossSingleGetRankResponse>(
                harness,
                rankListPacketId,
                nameof(BossSingleGetRankResponse),
                "Pain Cage rank list response");
            AssertEqual(0, rankList.Code, "Pain Cage rank list code");
            AssertEqual(1, rankList.RankNum, "Pain Cage rank list personal rank");
            AssertEqual(normalResult.TotalScore, rankList.Score, "Pain Cage rank list section score");
            AssertEqual(true, rankList.RankList.Count >= 1, "Pain Cage rank list contains participant");

            int pointsBeforeConstraintChecks =
                player.SimulatedBattlefield.BossCharacterPoints[checked((int)characterId)];
            int challengeCountBeforeConstraintChecks = player.SimulatedBattlefield.BossChallengeCount;
            int constraintStageId = selectedStageIds.First(stageId => stageId != normalStage.StageId);
            player.SimulatedBattlefield.BossCharacterPoints[checked((int)characterId)] = selectedGrade.StaminaCount;
            PreFightResponse staminaRejected = StartFight(82_036, constraintStageId, stageType: 1);
            AssertEqual(1, staminaRejected.Code, "Pain Cage exhausted character stamina rejection");
            AssertEqual(null, harness.Session.fight, "Pain Cage stamina rejection creates no fight");
            player.SimulatedBattlefield.BossCharacterPoints[checked((int)characterId)] = pointsBeforeConstraintChecks;
            int constraintSectionId = selectedSections.Single(sectionId =>
                sections.Single(row => row.SectionId == sectionId && row.AfreshId == currentAfreshId)
                    .StageId.Contains(constraintStageId));
            HashSet<int> constraintSectionStageIds = sections
                .Single(row => row.SectionId == constraintSectionId && row.AfreshId == currentAfreshId)
                .StageId
                .ToHashSet();
            List<AscNet.Common.Database.BossSingleStageRecordState> constraintSectionRecords =
                player.SimulatedBattlefield.BossStageRecords
                    .Where(record => constraintSectionStageIds.Contains(record.StageId))
                    .ToList();
            player.SimulatedBattlefield.BossStageRecords.RemoveAll(
                record => constraintSectionStageIds.Contains(record.StageId));
            player.SimulatedBattlefield.BossChallengeCount = int.MaxValue;
            PreFightResponse attemptsRejected = StartFight(82_037, constraintStageId, stageType: 1);
            AssertEqual(1, attemptsRejected.Code, "Pain Cage exhausted challenge-count rejection");
            AssertEqual(null, harness.Session.fight, "Pain Cage challenge-count rejection creates no fight");
            player.SimulatedBattlefield.BossStageRecords.AddRange(constraintSectionRecords);
            player.SimulatedBattlefield.BossChallengeCount = challengeCountBeforeConstraintChecks;

            int attemptsBeforeReset = player.SimulatedBattlefield.BossChallengeCount;
            const int resetPacketId = 82_038;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleResetStageRequest),
                harness.Session,
                resetPacketId,
                new BossSingleResetStageRequest { StageId = normalStage.StageId });
            BossSingleResetStageResponse reset = ReadAfterPushes<BossSingleResetStageResponse>(
                resetPacketId,
                nameof(BossSingleResetStageResponse),
                "Pain Cage stage reset",
                out List<string> resetPushes);
            AssertEqual(0, reset.Code, "Pain Cage reset code");
            AssertEqual(attemptsBeforeReset, player.SimulatedBattlefield.BossChallengeCount,
                "Retail PPC Reset Challenge does not refund Attempts");
            int resetStageCodexBest = Math.Max(
                player.SimulatedBattlefield.BossTrialScores.GetValueOrDefault(normalStage.StageId),
                player.SimulatedBattlefield.BossBestiaryScores.GetValueOrDefault(normalStage.StageId));
            AssertEqual(true,
                resetPushes.SequenceEqual(resetStageCodexBest > 0
                    ? [nameof(NotifyFubenBossSingleData)]
                    : [nameof(NotifyFubenBossSingleData), nameof(NotifyStageData)]),
                $"Pain Cage reset push ordering: got {string.Join(",", resetPushes)} (codex best {resetStageCodexBest})");
            AssertEqual(0, player.SimulatedBattlefield.BossCurrentTotalScore,
                "Pain Cage reset removes current score");
            AssertEqual(normalResult.TotalScore, player.SimulatedBattlefield.BossTotalScore,
                "Pain Cage reset preserves period best score");
            AssertEqual(true, player.SimulatedBattlefield.BossResetStageIds.Contains(normalStage.StageId),
                "Pain Cage reset marker persistence");
            AssertEqual(false,
                player.SimulatedBattlefield.BossCharacterPoints.ContainsKey(checked((int)characterId)),
                "Pain Cage reset refunds character stamina");
            AscNet.Common.Database.BossSingleStageRecordState resetRecord =
                player.SimulatedBattlefield.BossStageRecords.Single(record => record.StageId == normalStage.StageId);
            AssertEqual(0, resetRecord.Score, "Pain Cage reset clears current stage score");
            AssertEqual(normalResult.TotalScore, resetRecord.MaxScore,
                "Pain Cage reset retains stage best for aggregate progress");
            AssertEqual(normalResult.TotalScore,
                player.SimulatedBattlefield.BossStageRecords.Sum(record => record.MaxScore),
                "Pain Cage reset retains aggregate best progress");
            JObject resetLoginPayload = JObject.Parse(MessagePackSerializer.ConvertToJson(
                MessagePackSerializer.Serialize(BuildLogin(player, null))));
            JObject resetLoginData = RequiredValue<JObject>(
                resetLoginPayload, "FubenBossSingleData", JTokenType.Object, "Pain Cage reset login");
            JArray resetProjectedRecords = RequiredValue<JArray>(
                resetLoginData, "StageRecordList", JTokenType.Array, "Pain Cage reset login");
            AssertEqual(resetProjectedRecords.OfType<JObject>().Count(),
                resetProjectedRecords.OfType<JObject>().Select(value => RequiredValue<int>(
                    value, "StageId", JTokenType.Integer, "Pain Cage reset login")).Distinct().Count(),
                "Pain Cage reset login projects each stage once");
            JObject? resetStageEntry = resetProjectedRecords.OfType<JObject>().SingleOrDefault(value =>
                RequiredValue<int>(value, "StageId", JTokenType.Integer, "Pain Cage reset login")
                    == normalStage.StageId);
            AssertEqual(true, resetStageEntry is not null,
                "Pain Cage reset login projects the reset stage");
            AssertEqual(0,
                RequiredValue<int>(resetStageEntry!, "Score", JTokenType.Integer, "Pain Cage reset login"),
                "Pain Cage reset reports no current score for the reset stage");
            AssertEqual(0,
                RequiredValue<JArray>(
                    resetStageEntry!, "Characters", JTokenType.Array, "Pain Cage reset login").Count,
                "Pain Cage reset reports no current team for the reset stage");
            AssertEqual(1,
                RequiredValue<JArray>(
                    resetLoginData, "HistoryList", JTokenType.Array, "Pain Cage reset login").Count,
                "Pain Cage reset preserves team history");

            int savesBeforeDuplicateReset = playerCollection.ReplaceOneCalls;
            const int duplicateResetPacketId = 82_138;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleResetStageRequest),
                harness.Session,
                duplicateResetPacketId,
                new BossSingleResetStageRequest { StageId = normalStage.StageId });
            BossSingleResetStageResponse duplicateReset =
                ReadResponsePayload<BossSingleResetStageResponse>(
                    harness,
                    duplicateResetPacketId,
                    nameof(BossSingleResetStageResponse),
                    "Pain Cage duplicate stage reset");
            AssertEqual(1, duplicateReset.Code, "Pain Cage duplicate reset rejected");
            AssertEqual(savesBeforeDuplicateReset, playerCollection.ReplaceOneCalls,
                "Pain Cage duplicate reset does not persist");
            AssertEqual(false,
                player.SimulatedBattlefield.BossCharacterPoints.ContainsKey(checked((int)characterId)),
                "Pain Cage duplicate reset does not refund twice");

            int playerSavesBeforeAutoFight = playerCollection.ReplaceOneCalls;
            int stageSavesBeforeAutoFight = stageCollection.ReplaceOneCalls;
            const int autoPacketId = 82_039;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                autoPacketId,
                new BossSingleAutoFightRequest { StageId = normalStage.StageId });
            BossSingleAutoFightResponse auto = ReadAfterPushes<BossSingleAutoFightResponse>(
                autoPacketId,
                nameof(BossSingleAutoFightResponse),
                "Pain Cage auto-fight",
                out List<string> autoPushes);
            AssertEqual(0, auto.Code, "Pain Cage auto-fight code");
            AssertEqual(playerSavesBeforeAutoFight + 1, playerCollection.ReplaceOneCalls,
                "Pain Cage auto-fight persists Player once");
            AssertEqual(stageSavesBeforeAutoFight + 1, stageCollection.ReplaceOneCalls,
                "Pain Cage auto-fight persists Stage once");
            AssertEqual(1, player.SimulatedBattlefield.BossAutoFightCount,
                "Pain Cage auto-fight count");
            AscNet.Common.Database.BossSingleStageRecordState autoRecord =
                player.SimulatedBattlefield.BossStageRecords.Single(record => record.StageId == normalStage.StageId);
            AssertEqual(true, autoRecord.IsUseAutoFight, "Pain Cage auto-fight record marker");
            AssertEqual(savedHistory.Score, autoRecord.Score, "Pain Cage EN-config auto-fight rebate score");
            int autoRankPushIndex = autoPushes.IndexOf(nameof(NotifyBossSingleRankInfo));
            int autoLoginPushIndex = autoPushes.IndexOf(nameof(NotifyFubenBossSingleData));
            int autoStagePushIndex = autoPushes.IndexOf(nameof(NotifyStageData));
            if (autoRankPushIndex < 0 || autoLoginPushIndex <= autoRankPushIndex || autoStagePushIndex <= autoLoginPushIndex)
                throw new InvalidDataException(
                    $"Pain Cage auto-fight push order: expected rank, login, stage; got {string.Join(",", autoPushes)}.");

            const int duplicateAutoPacketId = 82_040;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleAutoFightRequest),
                harness.Session,
                duplicateAutoPacketId,
                new BossSingleAutoFightRequest { StageId = normalStage.StageId });
            BossSingleAutoFightResponse duplicateAuto =
                ReadResponsePayload<BossSingleAutoFightResponse>(
                    harness,
                    duplicateAutoPacketId,
                    nameof(BossSingleAutoFightResponse),
                    "Pain Cage duplicate auto-fight response");
            AssertEqual(1, duplicateAuto.Code, "Pain Cage duplicate auto-fight rejected");
            AssertEqual(1, player.SimulatedBattlefield.BossAutoFightCount,
                "Pain Cage duplicate auto-fight does not consume quota");

            player.SimulatedBattlefield.BossTotalScore = Math.Max(
                player.SimulatedBattlefield.BossTotalScore,
                scoreRewards
                    .Where(row => row.LevelType == selectedLevel
                        && row.RewardGroupId == selectedGrade.RewardGroupId)
                    .Min(row => row.Score));

            const int allRewardPacketId = 82_041;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetAllRewardRequest),
                harness.Session,
                allRewardPacketId,
                new BossSingleGetAllRewardRequest());
            BossSingleGetAllRewardResponse allRewards =
                ReadAfterPushes<BossSingleGetAllRewardResponse>(
                    allRewardPacketId,
                    nameof(BossSingleGetAllRewardResponse),
                    "Pain Cage claim-all rewards",
                    out List<string> rewardPushes);
            AssertEqual(0, allRewards.Code, "Pain Cage claim-all reward code");
            AssertEqual(true, allRewards.RewardGoodsList.Count > 0, "Pain Cage claim-all reward goods");
            AssertEqual(true, player.SimulatedBattlefield.BossClaimedRewardIds.Count > 0,
                "Pain Cage claimed reward IDs persistence");
            AssertEqual(true, rewardPushes.Contains(nameof(NotifyFubenBossSingleData)),
                "Pain Cage reward claim state push");
            HashSet<int> expectedClaimedRewardIds = scoreRewards
                .Where(row => row.LevelType == selectedLevel
                    && row.RewardGroupId == selectedGrade.RewardGroupId
                    && row.Score <= player.SimulatedBattlefield.BossTotalScore)
                .Select(row => row.Id)
                .ToHashSet();
            if (!player.SimulatedBattlefield.BossClaimedRewardIds.ToHashSet().SetEquals(expectedClaimedRewardIds))
                throw new InvalidDataException("Pain Cage claim-all persisted IDs differ from eligible table score rewards.");
            int expectedRewardGoodsCount = rewardGoods.Count(row =>
                expectedClaimedRewardIds.Contains(row.ScoreRewardId));
            AssertEqual(expectedRewardGoodsCount, allRewards.RewardGoodsList.Count,
                "Pain Cage claim-all table reward goods count");

            int claimedCount = player.SimulatedBattlefield.BossClaimedRewardIds.Count;
            const int duplicateAllRewardPacketId = 82_042;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetAllRewardRequest),
                harness.Session,
                duplicateAllRewardPacketId,
                new BossSingleGetAllRewardRequest());
            BossSingleGetAllRewardResponse duplicateAllRewards =
                ReadAfterPushes<BossSingleGetAllRewardResponse>(
                    duplicateAllRewardPacketId,
                    nameof(BossSingleGetAllRewardResponse),
                    "Pain Cage duplicate claim-all",
                    out _);
            AssertEqual(0, duplicateAllRewards.Code, "Pain Cage duplicate claim-all code");
            AssertEqual(0, duplicateAllRewards.RewardGoodsList.Count,
                "Pain Cage duplicate claim-all grants nothing");
            AssertEqual(claimedCount, player.SimulatedBattlefield.BossClaimedRewardIds.Count,
                "Pain Cage duplicate claim-all preserves claims");

            int claimedRewardId = player.SimulatedBattlefield.BossClaimedRewardIds.First();
            const int duplicateSingleRewardPacketId = 82_043;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetRewardRequest),
                harness.Session,
                duplicateSingleRewardPacketId,
                new BossSingleGetRewardRequest { Id = claimedRewardId });
            BossSingleGetRewardResponse duplicateSingleReward =
                ReadResponsePayload<BossSingleGetRewardResponse>(
                    harness,
                    duplicateSingleRewardPacketId,
                    nameof(BossSingleGetRewardResponse),
                    "Pain Cage duplicate single reward");
            AssertEqual(1, duplicateSingleReward.Code, "Pain Cage duplicate single reward rejected");

            const int challengeRankInfoPacketId = 82_044;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleChallengeRankInfoRequest),
                harness.Session,
                challengeRankInfoPacketId,
                new BossSingleChallengeRankInfoRequest { StageId = normalStage.StageId });
            BossSingleChallengeRankInfoResponse challengeRankInfo =
                ReadResponsePayload<BossSingleChallengeRankInfoResponse>(
                    harness,
                    challengeRankInfoPacketId,
                    nameof(BossSingleChallengeRankInfoResponse),
                    "Pain Cage challenge rank info response");
            AssertEqual(0, challengeRankInfo.Code, "Pain Cage challenge rank info code");

            const int challengeRankPacketId = 82_045;
            InvokeRegisteredRequestHandler(
                nameof(BossSingleGetChallengeRankRequest),
                harness.Session,
                challengeRankPacketId,
                new BossSingleGetChallengeRankRequest { StageId = normalStage.StageId });
            BossSingleGetChallengeRankResponse challengeRank =
                ReadResponsePayload<BossSingleGetChallengeRankResponse>(
                    harness,
                    challengeRankPacketId,
                    nameof(BossSingleGetChallengeRankResponse),
                    "Pain Cage challenge rank response");
            AssertEqual(0, challengeRank.Code, "Pain Cage challenge rank code");

            BossSingleChallengeGradeTable challengeGrade = TableReaderV2.Parse<BossSingleChallengeGradeTable>().Single();
            BossSingleGradeTable challengeNormalGrade = grades
                .Where(row => row.AfreshId == currentAfreshId
                    && row.GradeType >= challengeGrade.NeedGradeType)
                .OrderByDescending(row => row.GradeType)
                .First();
            player.SimulatedBattlefield.BossLevelType = challengeNormalGrade.LevelType;
            int lockedTotal = challengeGrade.NeedScore - 1;
            int firstBossScore = lockedTotal / 2;
            int secondBossScore = lockedTotal / 3;
            int thirdBossScore = lockedTotal - firstBossScore - secondBossScore;
            // Keep the real normal-stage best out of the synthetic gate records: the weekly
            // StableHash rotation can place it among the first three, and rollover would then
            // archive the synthetic score over the settled one.
            player.SimulatedBattlefield.BossStageRecords = selectedStageIds
                .Where(stageId => stageId != normalStage.StageId)
                .Take(3)
                .Select((stageId, index) => new AscNet.Common.Database.BossSingleStageRecordState
                {
                    StageId = stageId,
                    Score = index == 0 ? firstBossScore : index == 1 ? secondBossScore : thirdBossScore,
                    MaxScore = index == 0 ? firstBossScore : index == 1 ? secondBossScore : thirdBossScore
                })
                .ToList();
            player.SimulatedBattlefield.BossTotalScore = int.MaxValue;
            NotifyFubenBossSingleData lockedChallengeLogin = BuildLogin(player, null);
            AssertEqual(0, lockedChallengeLogin.FubenBossSingleData.ChallengeLevelType,
                "Pain Cage below normal-score gate has no intensive metadata");
            AssertEqual(0, lockedChallengeLogin.FubenBossSingleData.ChallengeSectionId,
                "Pain Cage below normal-score gate has no intensive section");
            AssertEqual(0, lockedChallengeLogin.FubenBossSingleData.ChallengeFeatureGroupId,
                "Pain Cage below normal-score gate has no intensive feature group");

            player.SimulatedBattlefield.BossStageRecords[2].Score++;
            player.SimulatedBattlefield.BossStageRecords[2].MaxScore++;
            player.SimulatedBattlefield.BossTotalScore = 0;
            player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup = challengeGroups.First(row => row.BuffGroupIds.Any(id => id > 0)).Id;
            NotifyFubenBossSingleData eligibleChallengeLogin = BuildLogin(player, null);
            AssertEqual(challengeGrade.LevelType, eligibleChallengeLogin.FubenBossSingleData.ChallengeLevelType,
                "Pain Cage normal total score unlocks table-backed intensive level");
            AssertEqual(challengeGrade.NeedScore, eligibleChallengeLogin.FubenBossSingleData.TotalScore,
                "Pain Cage login repairs total score from persisted stage bests");
            AssertEqual(challengeNormalGrade.RewardGroupId,
                grades.Single(row => row.LevelType == player.SimulatedBattlefield.BossLevelType).RewardGroupId,
                "Pain Cage intensive rank rewards share the selected normal reward group");
            BossSingleSectionTable eligibleChallengeSection = sections.Single(row =>
                row.Id == eligibleChallengeLogin.FubenBossSingleData.ChallengeSectionId
                && row.AfreshId == sections.Max(section => section.AfreshId));
            BossSingleChallengeFeatureGroupTable eligibleChallengeFeatureGroup = challengeGroups.Single(row =>
                row.Id == eligibleChallengeLogin.FubenBossSingleData.ChallengeFeatureGroupId);
            AssertEqual(true, eligibleChallengeSection.StageId.Count > 0
                && eligibleChallengeFeatureGroup.FeatureIds.Count > 0,
                "Pain Cage intensive PK section and feature joins are nonempty");
            AssertEqual(true,
                eligibleChallengeFeatureGroup.FeatureIds.Count == eligibleChallengeFeatureGroup.BuffGroupIds.Count
                && eligibleChallengeFeatureGroup.BuffGroupIds.All(id => id > 0),
                "Pain Cage intensive features map positionally to client BuffGroup ids");
            AssertEqual(0, eligibleChallengeLogin.FubenBossSingleData.ChallengeTotalScore,
                "Pain Cage intensive score does not reuse normal total score");
            NotifyFubenBossSingleData repeatedChallengeLogin = BuildLogin(player, null);
            AssertEqual(eligibleChallengeLogin.FubenBossSingleData.ChallengeSectionId,
                repeatedChallengeLogin.FubenBossSingleData.ChallengeSectionId,
                "Pain Cage intensive section is stable within an activity");
            AssertEqual(eligibleChallengeLogin.FubenBossSingleData.ChallengeFeatureGroupId,
                repeatedChallengeLogin.FubenBossSingleData.ChallengeFeatureGroupId,
                "Pain Cage intensive feature group is stable within an activity");

            int ultimateStageId = sections
                .Where(row => row.Id == eligibleChallengeLogin.FubenBossSingleData.ChallengeSectionId
                    && row.AfreshId == sections.Max(section => section.AfreshId))
                .SelectMany(row => row.StageId)
                .First(stageId => stages.Any(row => row.StageId == stageId
                    && row.PassTimeLimit == 300
                    && row.LeftTimeScore == 180000));
            BossSingleStageTable ultimateStage = stages.Single(row => row.StageId == ultimateStageId);
            BossSingleScoreRuleTable ultimateRule = scoreRules.Single(row => row.Id == ultimateStage.StageId);
            double ultimateTimeCoefficient = ultimateRule.LeftTimeScore[8 - 1];
            AssertEqual(2d, ultimateTimeCoefficient,
                "Pain Cage Ultimate Zone table time coefficient");
            const int ultimateFightSeconds = 19;
            int ultimateBuffGroup = challengeGroups
                .Single(row => row.Id == eligibleChallengeLogin.FubenBossSingleData.ChallengeFeatureGroupId)
                .BuffGroupIds.First(id => id > 0);
            PreFightResponse ultimatePreFight = StartFight(
                82_046,
                ultimateStage.StageId,
                stageType: 3,
                buffGroup: ultimateBuffGroup);
            AssertEqual(ultimateStage.PassTimeLimit, ultimatePreFight.FightData.PassTimeLimit,
                "Pain Cage Ultimate Zone table time limit");
            FightSettleResponse ultimateSettle = SettleFight(
                82_047,
                ultimatePreFight,
                ultimateStage,
                characterHp: 100,
                bossHp: 0,
                fightSeconds: ultimateFightSeconds);
            BossSingleFightResult ultimateResult = RequiredBossResult(
                ultimateSettle,
                "Pain Cage Ultimate Zone result");
            int expectedUltimateTimeLeft = ultimateStage.PassTimeLimit - ultimateFightSeconds;
            AssertEqual(expectedUltimateTimeLeft, ultimateResult.TimeLeft,
                "Pain Cage Ultimate Zone remaining time from fight input");
            int expectedUltimateTimeScore = Math.Min(
                ultimateStage.LeftTimeScore,
                checked((int)Math.Floor(
                    expectedUltimateTimeLeft * ultimateTimeCoefficient * ultimateStage.PassTimeLimit)));
            AssertEqual(true, expectedUltimateTimeScore < ultimateStage.LeftTimeScore,
                "Pain Cage Ultimate Zone 19-second fight remains below table time-score cap");
            AssertEqual(expectedUltimateTimeScore, ultimateResult.TimeScore,
                "Pain Cage Ultimate Zone table-derived remaining-time score");

            player.SimulatedBattlefield.BossStageRecords.Add(new AscNet.Common.Database.BossSingleStageRecordState
            {
                StageId = normalStage.StageId,
                Score = 0,
                MaxScore = normalResult.TotalScore,
                MaxCharacters = [checked((int)characterId)],
                MaxPartners = [0]
            });
            int previousActivity = player.SimulatedBattlefield.BossActivityNo;
            long rolloverTime = DateTimeOffset.UtcNow.AddDays(8).ToUnixTimeSeconds();
            NotifyFubenBossSingleData rolloverLogin = BuildLogin(player, rolloverTime);
            AssertEqual(true, rolloverLogin.FubenBossSingleData.ActivityNo > previousActivity,
                "Pain Cage weekly activity rollover");
            AssertEqual(0, rolloverLogin.FubenBossSingleData.LevelType,
                "Pain Cage rollover requires level selection");
            AssertEqual(0, player.SimulatedBattlefield.BossStageRecords.Count,
                "Pain Cage rollover clears current stage records");
            AssertEqual(0, player.SimulatedBattlefield.BossClaimedRewardIds.Count,
                "Pain Cage rollover clears reward claims");
            AssertEqual(true, player.SimulatedBattlefield.BossHistory.Any(record =>
                    record.StageId == normalStage.StageId && record.Score == normalResult.TotalScore),
                "Pain Cage rollover archives stage best");
            AssertEqual(true, player.SimulatedBattlefield.BossMaxScore >= normalResult.TotalScore,
                "Pain Cage rollover preserves promotion score");
            AssertEqual(true, rolloverLogin.BossListDict is { Count: > 0 },
                "Pain Cage rollover rebuilds table-derived boss options");

            AscNet.Common.Database.Player reloaded =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                    player.ToBsonDocument());
            NotifyFubenBossSingleData reloadLogin = BuildLogin(reloaded, rolloverTime);
            AssertEqual(player.SimulatedBattlefield.BossActivityNo,
                reloadLogin.FubenBossSingleData.ActivityNo,
                "Pain Cage BSON reload activity persistence");
            AssertEqual(true, reloaded.SimulatedBattlefield.BossHistory.Any(record =>
                    record.StageId == normalStage.StageId && record.Score == normalResult.TotalScore),
                "Pain Cage BSON reload history persistence");
            AssertEqual(0, reloaded.SimulatedBattlefield.BossClaimedRewardIds.Count,
                "Pain Cage BSON reload reset claim persistence");
            NotifyFubenBossSingleData currentActivityLogin = BuildLogin(player, null);
            int currentActivity = currentActivityLogin.FubenBossSingleData.ActivityNo;
            int[] oldRotationStages = selectedSections
                .SelectMany(sectionId => sections.Single(row =>
                    row.SectionId == sectionId && row.AfreshId == currentAfreshId).StageId)
                .Where(stageId => stageId > 0)
                .ToArray();
            int oldRotationStageId = normalStage.StageId;
            AssertEqual(true, oldRotationStages.Contains(oldRotationStageId),
                "Pain Cage rollover fixture stage belongs to the outgoing weekly rotation");
            StageDatum oldRotationDatum = harness.Session.stage.Stages[checked((uint)oldRotationStageId)];
            player.SimulatedBattlefield.BossTrialScores[oldRotationStageId] = normalResult.TotalScore;
            oldRotationDatum.Passed = true;
            oldRotationDatum.Score = normalResult.TotalScore;
            oldRotationDatum.PassTimesTotal = 9;
            oldRotationDatum.LastPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int alternateOptionStageId = player.SimulatedBattlefield.BossListOptions.Values
                .SelectMany(option => option.Where(sectionId => !selectedSections.Contains(sectionId)))
                .SelectMany(sectionId => sections.Single(row =>
                    row.SectionId == sectionId && row.AfreshId == currentAfreshId).StageId)
                .First(stageId => stageId > 0);
            StageDatum alternateOptionDatum = new() { StageId = alternateOptionStageId, Passed = true };
            harness.Session.stage.AddStage(alternateOptionDatum);

            int intensiveStageId = trialGrades
                .Where(row => row.LevelType is 4 or 8
                    && (row.IsBestiaryCfg != 0) == (row.LevelType == 8))
                .SelectMany(row => row.SectionId)
                .SelectMany(sectionId => sections.First(row =>
                    row.SectionId == sectionId && row.AfreshId == currentAfreshId).StageId)
                .First(stageId => stageId > 0
                    && stageId != oldRotationStageId
                    && stageId != alternateOptionStageId);
            StageDatum intensiveDatum = new() { StageId = intensiveStageId, Passed = true };
            harness.Session.stage.AddStage(intensiveDatum);

            const int unrelatedRotationStageId = 999_999_999;
            StageDatum unrelatedRotationDatum = new() { StageId = unrelatedRotationStageId, Passed = true };
            harness.Session.stage.AddStage(unrelatedRotationDatum);
            player.SimulatedBattlefield.BossActivityNo = currentActivity;
            harness.Session.stage.BossSingleActivityNo = currentActivity;
            player.Save();
            harness.Session.stage.Save();

            Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
            MethodInfo reconcileLive = RequiredMethod(
                module,
                "ReconcileLive",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session)]);
            reconcileLive.Invoke(null, [harness.Session]);
            AssertEqual(true, harness.Session.stage.Stages[(uint)oldRotationStageId].Passed,
                "Pain Cage current Stage ownership preserves current weekly completion");
            MethodInfo buildBossListOptions = RequiredMethod(
                module,
                "BuildBossListOptions",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(AscNet.Common.Database.SimulatedBattlefieldState), typeof(int), typeof(int)]);
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            long originalPlayerLevel = player.PlayerData.Level;
            int originalBossLevelType = state.BossLevelType;
            int originalBossOldLevelType = state.BossOldLevelType;
            List<int> originalBossList = state.BossList.ToList();
            Dictionary<int, List<int>> originalBossListOptions = state.BossListOptions
                .ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
            BossSingleGradeTable[] levelBands = grades
                .Where(row => row.AfreshId == currentAfreshId && row.PreGradeType == 0)
                .OrderBy(row => row.MinPlayerLevel)
                .ToArray();
            BossSingleGradeTable previousGrade = levelBands[0];
            BossSingleGradeTable currentGrade = levelBands.First(row =>
                row.MinPlayerLevel > previousGrade.MaxPlayerLevel);
            state.BossOldLevelType = previousGrade.LevelType;
            player.PlayerData.Level = checked((uint)previousGrade.MaxPlayerLevel);
            Dictionary<int, List<int>> previousRotationOptions =
                (Dictionary<int, List<int>>?)buildBossListOptions.Invoke(null, [
                    state,
                    checked((int)player.PlayerData.Level),
                    currentActivity - 1
                ]) ?? throw new InvalidDataException("Previous Pain Cage rotation options were not generated.");
            player.PlayerData.Level = checked((uint)currentGrade.MinPlayerLevel);
            Dictionary<int, List<int>> currentRotationOptions =
                (Dictionary<int, List<int>>?)buildBossListOptions.Invoke(null, [
                    state,
                    checked((int)player.PlayerData.Level),
                    currentActivity
                ]) ?? throw new InvalidDataException("Current Pain Cage rotation options were not generated.");
            AssertEqual(true, previousRotationOptions.ContainsKey(previousGrade.LevelType),
                "Pain Cage prior grade was selectable before the level-band change");
            AssertEqual(true, currentRotationOptions.ContainsKey(currentGrade.LevelType),
                "Pain Cage new grade is selectable after the level-band change");
            int previousRotationSectionId = previousRotationOptions[previousGrade.LevelType][0];
            int currentRotationSectionId = currentRotationOptions[currentGrade.LevelType][0];
            AssertEqual(true, previousRotationSectionId != currentRotationSectionId,
                "Pain Cage previous and current grades select different normal sections");
            int previousRotationStageId = sections.Single(row =>
                    row.SectionId == previousRotationSectionId && row.AfreshId == currentAfreshId)
                .StageId.First(stageId => stageId > 0);
            int currentRotationStageId = sections.Single(row =>
                    row.SectionId == currentRotationSectionId && row.AfreshId == currentAfreshId)
                .StageId.First(stageId => stageId > 0);
            AssertEqual(true, previousRotationStageId != currentRotationStageId,
                "Pain Cage distinct normal sections use distinct Stage IDs");
            state.BossLevelType = currentGrade.LevelType;
            state.BossListOptions = currentRotationOptions;
            state.BossList = currentRotationOptions[currentGrade.LevelType].ToList();
            if (!harness.Session.stage.Stages.TryGetValue(previousRotationStageId, out StageDatum? previousRotationDatum))
            {
                previousRotationDatum = new StageDatum { StageId = previousRotationStageId };
                harness.Session.stage.AddStage(previousRotationDatum);
            }
            if (harness.Session.stage.Stages.TryGetValue(currentRotationStageId, out StageDatum? currentRotationDatum))
                currentRotationDatum.Passed = false;
            previousRotationDatum.Passed = true;
            AssertEqual(currentActivity, state.BossActivityNo,
                "Pain Cage prior-epoch Stage fixture keeps Player already current");
            harness.Session.stage.BossSingleActivityNo = currentActivity - 1;
            MethodInfo persistStageOwnership = RequiredMethod(
                module,
                "PersistStageOwnershipReconciliation",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(int), typeof(long)]);
            persistStageOwnership.Invoke(null, [
                harness.Session,
                currentActivity,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            ]);
            AssertEqual(false, harness.Session.stage.Stages[previousRotationStageId].Passed,
                "Pain Cage cleanup clears the prior grade's section after Player level-up");
            AssertEqual(currentActivity, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage cleanup advances Stage epoch after clearing the prior rotation");
            player.PlayerData.Level = originalPlayerLevel;
            state.BossLevelType = originalBossLevelType;
            state.BossOldLevelType = originalBossOldLevelType;
            state.BossList = originalBossList;
            state.BossListOptions = originalBossListOptions;
            oldRotationDatum.Passed = true;

            harness.Session.stage.BossSingleActivityNo = null;
            oldRotationDatum.LastPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            alternateOptionDatum.LastPassTime = 0;
            reconcileLive.Invoke(null, [harness.Session]);
            AssertEqual(true, harness.Session.stage.Stages[(uint)oldRotationStageId].Passed,
                "Pain Cage legacy LastPassTime in current weekly reset period proves current completion");
            AssertEqual(false, harness.Session.stage.Stages[(uint)alternateOptionStageId].Passed,
                "Pain Cage legacy zero LastPassTime is ambiguous and clears Passed");
            AssertEqual(currentActivity, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage legacy migration stamps the current Stage ownership epoch");

            oldRotationDatum.Passed = true;
            oldRotationDatum.LastPassTime = 12_345;
            alternateOptionDatum.Passed = true;
            intensiveDatum.Passed = true;
            unrelatedRotationDatum.Passed = true;
            player.SimulatedBattlefield.BossActivityNo = currentActivity - 1;
            player.SimulatedBattlefield.BossLevelType = selectedLevel;
            player.SimulatedBattlefield.BossList = selectedSections.ToList();
            player.SimulatedBattlefield.BossStageRecords.Clear();
            player.SimulatedBattlefield.BossChallengeCount = 0;
            harness.Session.stage.BossSingleActivityNo = currentActivity - 1;
            harness.Session.PendingBossSingleScore = new BossSinglePendingScore
            {
                StageId = oldRotationStageId,
                StageType = 1,
                SectionId = normalSectionId,
                IsWin = true,
                Result = new BossSingleFightResult { TotalScore = normalResult.TotalScore + 1 }
            };
            player.Save();
            harness.Session.stage.Save();
            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Boss Single fixture Player was not persisted."));
            harness.Session.player = player;
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Boss Single fixture Stage was not persisted."));

            byte[] oldPlayerBson = playerCollection.LastSuccessfulReplacementBson!;
            byte[] oldStageBson = stageCollection.LastSuccessfulReplacementBson!;
            stageCollection.FindResults = [
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(oldStageBson)
            ];
            BsonDocument legacyStageDocument = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(oldStageBson);
            legacyStageDocument.Remove("boss_single_activity_no");
            AscNet.Common.Database.Stage legacyStage =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(legacyStageDocument);
            AssertEqual(null, legacyStage.BossSingleActivityNo,
                "Pain Cage legacy BSON without Stage ownership marker loads as unknown");
            stageCollection.ReplaceOneMatchedCount = 0;
            bool noMatchSaveFailed = false;
            try
            {
                reconcileLive.Invoke(null, [harness.Session]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoException)
            {
                noMatchSaveFailed = true;
            }
            AssertEqual(true, noMatchSaveFailed, "Pain Cage zero-match Stage save fails reconciliation");
            oldRotationDatum = harness.Session.stage.Stages[checked((uint)oldRotationStageId)];
            AssertEqual(true, oldRotationDatum.Passed,
                "Pain Cage zero-match Stage reload retains outgoing Passed in memory");
            AssertEqual(currentActivity - 1, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage zero-match Stage save leaves ownership epoch unchanged");
            AssertEqual(true, stageCollection.LastSuccessfulReplacementBson!.SequenceEqual(oldStageBson),
                "Pain Cage zero-match Stage save leaves the last successful Stage replacement unchanged");
            AscNet.Common.Database.Player reloadedNoMatchPlayer =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                    playerCollection.LastSuccessfulReplacementBson!);
            AscNet.Common.Database.Stage reloadedNoMatchStage =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(oldStageBson);
            AssertEqual(currentActivity - 1, reloadedNoMatchPlayer.SimulatedBattlefield.BossActivityNo,
                "Pain Cage zero-match Player replacement snapshot reload retains the old activity");
            AssertEqual(true, reloadedNoMatchStage.Stages[checked((uint)oldRotationStageId)].Passed,
                "Pain Cage zero-match Stage replacement snapshot reload retains outgoing Passed");
            stageCollection.ReplaceOneMatchedCount = 1;
            stageCollection.FindResults = [
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(oldStageBson)
            ];
            AssertEqual(true, stageCollection.FindResults!.Single()
                .Stages[checked((uint)oldRotationStageId)].Passed,
                "Pain Cage failed Stage fixture reload starts from durable outgoing completion");

            stageCollection.ThrowOnReplaceOne = true;
            bool stageSaveFailed = false;
            try
            {
                reconcileLive.Invoke(null, [harness.Session]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoException)
            {
                stageSaveFailed = true;
            }
            AssertEqual(true, stageSaveFailed, "Pain Cage failed Stage save aborts reconciliation");
            oldRotationDatum = harness.Session.stage.Stages[checked((uint)oldRotationStageId)];
            AssertEqual(currentActivity - 1, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage failed Stage save leaves old Player activity in memory");
            AssertEqual(true, oldRotationDatum.Passed,
                "Pain Cage failed Stage reload restores outgoing Passed state");
            AssertEqual(currentActivity - 1, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage failed Stage save does not advance ownership epoch");
            AssertEqual(true, stageCollection.LastSuccessfulReplacementBson!.SequenceEqual(oldStageBson),
                "Pain Cage failed Stage save leaves the last successful Stage replacement unchanged");
            AscNet.Common.Database.Player reloadedOldActivity =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                    playerCollection.LastSuccessfulReplacementBson!);
            AscNet.Common.Database.Stage reloadedOldStages =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(oldStageBson);
            AssertEqual(currentActivity - 1, reloadedOldActivity.SimulatedBattlefield.BossActivityNo,
                "Pain Cage failed Stage save Player replacement snapshot reload retains old activity");
            AssertEqual(true, reloadedOldStages.Stages[checked((uint)oldRotationStageId)].Passed,
                "Pain Cage failed Stage save Stage replacement snapshot reload retains outgoing Passed");
            AssertEqual(true, reloadedOldStages.Stages[checked((uint)unrelatedRotationStageId)].Passed,
                "Pain Cage failed Stage save Stage replacement snapshot reload preserves unrelated Passed");
            AssertEqual(currentActivity - 1, reloadedOldStages.BossSingleActivityNo,
                "Pain Cage failed Stage persistence reload retains old ownership epoch");
            harness.Session.player = reloadedOldActivity;
            harness.Session.stage = reloadedOldStages;
            player = reloadedOldActivity;
            stageCollection.ThrowOnReplaceOne = false;
            stageCollection.BeforeReplaceOne = replacement =>
                stageCollection.FindResults = [
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(replacement.ToBson())
                ];
            stageCollection.ThrowAfterReplaceOne = true;
            bool stageAckLost = false;
            try
            {
                reconcileLive.Invoke(null, [harness.Session]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoException)
            {
                stageAckLost = true;
            }
            stageCollection.BeforeReplaceOne = null;
            AssertEqual(true, stageAckLost, "Pain Cage rollover observes Stage acknowledgement loss");
            AssertEqual(currentActivity, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage acknowledgement-loss reload observes committed ownership epoch");
            AssertEqual(false, harness.Session.stage.Stages[(uint)oldRotationStageId].Passed,
                "Pain Cage acknowledgement-loss reload observes committed Passed cleanup");
            AssertEqual(false, harness.Session.stage.Stages[(uint)intensiveStageId].Passed,
                "Pain Cage acknowledgement-loss reload clears the intensive stage");
            AssertEqual(currentActivity - 1, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage Stage acknowledgement loss leaves Player rollover pending");
            AssertEqual(oldRotationStageId, harness.Session.PendingBossSingleScore?.StageId ?? 0,
                "Pain Cage Stage acknowledgement loss preserves provisional score until Player rollover retry");
            intensiveDatum = harness.Session.stage.Stages[checked((uint)intensiveStageId)];
            unrelatedRotationDatum = harness.Session.stage.Stages[checked((uint)unrelatedRotationStageId)];
            List<NotifyStageData> rolloverStageUpdates = [];
            BossSingleSaveScoreResponse staleRolloverSave = SaveScore(
                82_400,
                oldRotationStageId,
                "Pain Cage stale provisional score at weekly rollover",
                out _,
                push =>
                {
                    if (push.Name == nameof(NotifyStageData))
                        rolloverStageUpdates.Add(MessagePackSerializer.Deserialize<NotifyStageData>(push.Content));
                });
            oldRotationDatum = harness.Session.stage.Stages[checked((uint)oldRotationStageId)];
            alternateOptionDatum = harness.Session.stage.Stages[checked((uint)alternateOptionStageId)];
            AssertEqual(currentActivity, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage reconciliation retries rollover after Stage persistence failure");
            AssertEqual(false, harness.Session.stage.Stages[(uint)intensiveStageId].Passed,
                "Pain Cage retry clears the intensive stage in the current Stage object");
            AssertEqual(false, oldRotationDatum.Passed,
                "Pain Cage retry persists outgoing Passed cleanup");
            AssertEqual(true, unrelatedRotationDatum.Passed,
                "Pain Cage retry leaves unrelated Passed state untouched");
            AssertEqual(false, alternateOptionDatum.Passed,
                "Pain Cage rollover clears completion from an unselected BossListOptions alternative");
            AssertEqual(false, intensiveDatum.Passed,
                "Pain Cage rollover clears completion from Intensive BossSingle stages");
            AssertEqual(1, staleRolloverSave.Code,
                "Pain Cage weekly rollover rejects the stale provisional score");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage weekly rollover clears the stale provisional score");
            AssertEqual(normalResult.TotalScore, oldRotationDatum.Score,
                "Pain Cage rollover retains the outgoing Trial codex score in the Stage datum");
            AssertEqual(9L, oldRotationDatum.PassTimesTotal,
                "Pain Cage rollover preserves the outgoing stage pass count");
            AssertEqual(12_345L, oldRotationDatum.LastPassTime,
                "Pain Cage rollover preserves the outgoing stage pass time");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage stale score save consumes no new-cycle Attempt");
            AssertEqual(3, rolloverStageUpdates.Count,
                "Pain Cage acknowledgement-loss retry sends stage cleanup notifications");
            List<StageDatum> notifiedRolloverStages = rolloverStageUpdates
                .SelectMany(update => update.StageList)
                .ToList();
            List<StageDatum> notifiedOldRotationStages = notifiedRolloverStages
                .Where(stage => stage.StageId == oldRotationStageId)
                .ToList();
            AssertEqual(true, notifiedOldRotationStages.Count > 0,
                "Pain Cage acknowledgement-loss retry notifies the outgoing stage");
            AssertEqual(false, notifiedOldRotationStages.Last().Passed,
                "Pain Cage acknowledgement-loss retry notifies the committed cleared state");
            AssertEqual(false, harness.Session.PendingBossSingleRolloverStageIds.Contains(oldRotationStageId),
                "Pain Cage acknowledgement-loss retry clears the session notification after sending it");
            AssertEqual(0, player.SimulatedBattlefield.BossStageRecords.Count,
                "Pain Cage stale score save commits no new-cycle record");
            AscNet.Common.Database.Player persistedRolloverPlayer =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                    playerCollection.LastSuccessfulReplacementBson!);
            AscNet.Common.Database.Stage persistedRolloverStage =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                    stageCollection.LastSuccessfulReplacementBson!);
            AssertEqual(currentActivity, persistedRolloverPlayer.SimulatedBattlefield.BossActivityNo,
                "Pain Cage successful Stage cleanup permits Player replacement with the rolled-over activity");
            AssertEqual(false, persistedRolloverStage.Stages[checked((uint)oldRotationStageId)].Passed,
                "Pain Cage successful rollover replacement persists outgoing Stage cleanup");
            AssertEqual(currentActivity, persistedRolloverStage.BossSingleActivityNo,
                "Pain Cage successful Stage cleanup persists the new ownership epoch");
            AssertEqual(true, persistedRolloverStage.Stages[checked((uint)unrelatedRotationStageId)].Passed,
                "Pain Cage successful rollover replacement preserves unrelated Stage Passed");

            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(oldPlayerBson);
            harness.Session.player = player;
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(oldStageBson);
            oldRotationDatum = harness.Session.stage.Stages[checked((uint)oldRotationStageId)];
            harness.Session.PendingBossSingleScore = new BossSinglePendingScore
            {
                StageId = oldRotationStageId,
                StageType = 1,
                SectionId = normalSectionId,
                IsWin = true,
                Result = new BossSingleFightResult { TotalScore = normalResult.TotalScore + 1 }
            };
            playerCollection.ThrowOnReplaceOne = true;
            bool playerSaveFailed = false;
            try
            {
                reconcileLive.Invoke(null, [harness.Session]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MongoException)
            {
                playerSaveFailed = true;
            }
            playerCollection.ThrowOnReplaceOne = false;
            AssertEqual(true, playerSaveFailed, "Pain Cage rollover propagates Player persistence failure");
            AssertEqual(currentActivity, player.SimulatedBattlefield.BossActivityNo,
                "Pain Cage Player persistence failure follows successful Stage rollover cleanup");
            AssertEqual(false, oldRotationDatum.Passed,
                "Pain Cage Player persistence failure leaves outgoing Stage cleanup in memory");
            AscNet.Common.Database.Stage persistedPlayerFailureStage =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("Pain Cage Player failure did not preserve Stage cleanup."));
            AssertEqual(false, persistedPlayerFailureStage.Stages[checked((uint)oldRotationStageId)].Passed,
                "Pain Cage Player persistence failure preserves outgoing Stage cleanup");
            AssertEqual(null, harness.Session.PendingBossSingleScore,
                "Pain Cage Player persistence failure clears the outgoing provisional score");
            int newCycleAttempts = player.SimulatedBattlefield.BossChallengeCount;
            int newCycleRecords = player.SimulatedBattlefield.BossStageRecords.Count;
            Dictionary<int, List<int>> newCycleTeams = player.SimulatedBattlefield.BossNormalStageTeams
                .ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
            BossSingleSaveScoreResponse playerFailureStaleSave = SaveScore(
                82_401,
                oldRotationStageId,
                "Pain Cage stale provisional score after Player persistence failure",
                out _);
            AssertEqual(1, playerFailureStaleSave.Code,
                "Pain Cage Player persistence failure rejects the stale provisional score");
            AssertEqual(newCycleAttempts, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage stale score after Player failure consumes no new-cycle Attempt");
            AssertEqual(newCycleRecords, player.SimulatedBattlefield.BossStageRecords.Count,
                "Pain Cage stale score after Player failure commits no new-cycle record");
            AssertEqual(true,
                newCycleTeams.Count == player.SimulatedBattlefield.BossNormalStageTeams.Count
                && newCycleTeams.All(entry => player.SimulatedBattlefield.BossNormalStageTeams.TryGetValue(
                    entry.Key, out List<int>? team) && entry.Value.SequenceEqual(team)),
                "Pain Cage stale score after Player failure leaves new-cycle teams unchanged");
            BossSingleChallengeGradeTable stageThreeGrade = challengeGrades.Single();
            BossSingleGroupTable stageThreeGroup = groups.Single(row => row.Id == stageThreeGrade.BossGroupId);
            int stageThreeStageId = sections
                .Where(row => row.AfreshId == currentAfreshId && stageThreeGroup.SectionId.Contains(row.SectionId))
                .SelectMany(row => row.StageId)
                .First(stageId => stageId > 0);
            player.SimulatedBattlefield.BossActivityNo = currentActivity;
            player.SimulatedBattlefield.BossLevelType = 0;
            player.SimulatedBattlefield.BossTotalScore = 0;
            player.SimulatedBattlefield.BossChallengeHistory.Clear();
            player.SimulatedBattlefield.BossChallengeSelectedSection = 0;
            player.SimulatedBattlefield.BossChallengeSelectedFeatureGroup = 0;
            player.SimulatedBattlefield.BossChallengeCount = 2;
            player.Save();
            if (!harness.Session.stage.Stages.TryGetValue(stageThreeStageId, out StageDatum? stageThreeDatum)
                || stageThreeDatum is null)
            {
                stageThreeDatum = new() { StageId = stageThreeStageId };
                harness.Session.stage.AddStage(stageThreeDatum);
            }
            stageThreeDatum.Passed = true;
            stageThreeDatum.PassTimesTotal = 4;
            stageThreeDatum.LastPassTime = 1_700_000_000;
            stageThreeDatum.Score = 987_654;
            harness.Session.stage.BossSingleActivityNo = currentActivity - 1;
            harness.Session.stage.Save();
            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("StageType 3 rollover Player fixture was not persisted."));
            harness.Session.player = player;
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("StageType 3 rollover Stage fixture was not persisted."));
            stageThreeDatum = harness.Session.stage.Stages[stageThreeStageId];
            AssertEqual(0, player.SimulatedBattlefield.BossLevelType,
                "StageType 3 rollover fixture has reset Player grade");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeSelectedSection,
                "StageType 3 rollover fixture has no selected challenge section");
            AssertEqual(0, player.SimulatedBattlefield.BossChallengeHistory.Count,
                "StageType 3 rollover fixture has no challenge history");
            persistStageOwnership.Invoke(null, [
                harness.Session,
                currentActivity,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            ]);
            AssertEqual(false, stageThreeDatum.Passed,
                "Pain Cage rollover clears table-derived StageType 3 completion after Player reset");
            AssertEqual(currentActivity, harness.Session.stage.BossSingleActivityNo,
                "Pain Cage StageType 3 cleanup advances Stage epoch");
            AssertEqual(4L, stageThreeDatum.PassTimesTotal,
                "Pain Cage StageType 3 cleanup preserves pass count");
            AssertEqual(1_700_000_000L, stageThreeDatum.LastPassTime,
                "Pain Cage StageType 3 cleanup preserves last pass time");
            AssertEqual(987_654L, stageThreeDatum.Score,
                "Pain Cage StageType 3 cleanup preserves score");
            AssertEqual(2, player.SimulatedBattlefield.BossChallengeCount,
                "Pain Cage StageType 3 cleanup preserves current attempt accounting");
            AscNet.Common.Database.Stage persistedStageThree =
                MongoDB.Bson.Serialization.BsonSerializer.Deserialize<AscNet.Common.Database.Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                        ?? throw new InvalidDataException("StageType 3 cleanup replacement was not persisted."));
            StageDatum persistedStageThreeDatum = persistedStageThree.Stages[stageThreeStageId];
            AssertEqual(currentActivity, persistedStageThree.BossSingleActivityNo,
                "Pain Cage StageType 3 cleanup persists the new Stage epoch");
            AssertEqual(false, persistedStageThreeDatum.Passed,
                "Pain Cage StageType 3 cleanup persists Passed=false");
            AssertEqual(4L, persistedStageThreeDatum.PassTimesTotal,
                "Pain Cage StageType 3 cleanup persists pass count unchanged");
            AssertEqual(1_700_000_000L, persistedStageThreeDatum.LastPassTime,
                "Pain Cage StageType 3 cleanup persists last pass time unchanged");
            AssertEqual(987_654L, persistedStageThreeDatum.Score,
                "Pain Cage StageType 3 cleanup persists score unchanged");
        }


        private static void ValidateBossSingleLoginRollover()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
                out RecordingMongoCollectionProxy<Player> playerCollection,
                out RecordingMongoCollectionProxy<Stage> stageCollection);
            const long playerId = 46_810;
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.SimulatedBattlefield = new();
            using LoopbackSessionHarness harness = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "boss-single-login-rollover");
            harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            int currentAfreshId = TableReaderV2.Parse<BossSingleGradeTable>().Max(row => row.AfreshId);
            Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
            MethodInfo buildBossLogin = RequiredMethod(
                bossModule,
                "BuildLoginData",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Player), typeof(long?)]);
            NotifyFubenBossSingleData currentLogin = (NotifyFubenBossSingleData?)buildBossLogin.Invoke(null, [player, null])
                ?? throw new InvalidDataException("BossModule.BuildLoginData returned nil.");
            int currentActivity = currentLogin.FubenBossSingleData.ActivityNo;
            int selectedSectionId = player.SimulatedBattlefield.BossList.First();
            BossSingleSectionTable section = TableReaderV2.Parse<BossSingleSectionTable>().Single(row =>
                row.AfreshId == currentAfreshId && row.SectionId == selectedSectionId);
            int outgoingStageId = section.StageId.First(stageId => stageId > 0);
            const long codexScore = 73_100;
            player.SimulatedBattlefield.BossTrialScores[outgoingStageId] = checked((int)codexScore);
            harness.Session.stage.AddStage(new StageDatum
            {
                StageId = outgoingStageId,
                Passed = true,
                Score = codexScore,
                PassTimesTotal = 4,
                LastPassTime = 12_345
            });
            player.SimulatedBattlefield.BossActivityNo = currentActivity - 1;
            harness.Session.stage.BossSingleActivityNo = currentActivity - 1;
            player.Save();
            harness.Session.stage.Save();

            player = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Pre-rollover Player fixture was not persisted."));
            harness.Session.player = player;
            harness.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Outgoing Stage fixture was not persisted."));
            AssertEqual(currentActivity - 1, player.SimulatedBattlefield.BossActivityNo,
                "Account login starts from reloaded pre-rollover Player activity");
            AssertEqual(true, harness.Session.stage.Stages[(uint)outgoingStageId].Passed,
                "Account login starts with persisted outgoing Passed state");
            AssertEqual(true, currentActivity - 1 > 0 && player.SimulatedBattlefield.BossList.Contains(section.SectionId),
                "Account login rollover fixture has a noninitial activity and selected outgoing stage");

            MethodInfo accountLogin = RequiredMethod(
                RequiredAscNetGameServerType("AscNet.GameServer.Handlers.AccountModule"),
                "DoLogin",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(bool)]);
            _ = accountLogin.Invoke(null, [harness.Session, false]);

            AssertEqual(currentActivity, harness.Session.player.SimulatedBattlefield.BossActivityNo,
                "Account login advances Boss Single activity");
            AssertEqual(false, harness.Session.stage.Stages[(uint)outgoingStageId].Passed,
                "Account login cleans outgoing Stage Passed");
            Player persistedPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Account login did not persist the rolled-over Player."));
            Stage persistedStage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Account login did not persist outgoing Stage cleanup."));
            AssertEqual(currentActivity, persistedStage.BossSingleActivityNo,
                "Account login persisted the advanced Stage ownership epoch");
            AssertEqual(currentActivity, persistedPlayer.SimulatedBattlefield.BossActivityNo,
                "Account login persisted the advanced Player activity");
            AssertEqual(false, persistedStage.Stages[(uint)outgoingStageId].Passed,
                "Account login persisted outgoing Stage cleanup");
        }
        private static void ValidateBossSingleSharedTimestampBoundary()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
                out RecordingMongoCollectionProxy<Player> playerCollection,
                out RecordingMongoCollectionProxy<Stage> stageCollection);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long WeeklyPeriod(long timestamp) => (timestamp / 86_400 + 3) / 7;
            long boundary = checked((WeeklyPeriod(now) + 1) * 7 - 3) * 86_400;
            long beforeBoundary = boundary - 1;
            long atBoundary = boundary;
            int beforeActivity = checked((int)WeeklyPeriod(beforeBoundary));
            int atActivity = checked((int)WeeklyPeriod(atBoundary));
            AssertEqual(beforeActivity + 1, atActivity, "weekly boundary advances exactly one activity");

            Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
            MethodInfo reconcileLive = RequiredMethod(
                bossModule,
                "ReconcileLive",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Session), typeof(long), typeof(bool)]);
            MethodInfo buildLoginData = RequiredMethod(
                bossModule,
                "BuildLoginData",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Player), typeof(long?)]);

            void ReconcileAt(Session session, long timestamp) =>
                reconcileLive.Invoke(null, [session, timestamp, false]);
            NotifyFubenBossSingleData BuildAt(Player player, long timestamp) =>
                (NotifyFubenBossSingleData?)buildLoginData.Invoke(null, [player, timestamp])
                ?? throw new InvalidDataException("BossModule.BuildLoginData returned nil.");
            LoopbackSessionHarness Harness(long playerId, string name)
            {
                Player player = CreateDrawCompatibilityPlayer(playerId);
                player.SimulatedBattlefield = new() { BossActivityNo = beforeActivity - 1 };
                LoopbackSessionHarness harness = new(
                    CreateDrawCompatibilityCharacter(playerId),
                    player,
                    CreateDrawCompatibilityInventory(playerId, []),
                    name);
                harness.Session.stage = CreateLoginAccountCompatibilityStage(playerId);
                harness.Session.stage.BossSingleActivityNo = beforeActivity - 1;
                return harness;
            }

            using (LoopbackSessionHarness oldPath = Harness(46_812, "boss-single-mixed-week-old-path"))
            {
                ReconcileAt(oldPath.Session, beforeBoundary);
                AssertEqual(beforeActivity, oldPath.Session.player.SimulatedBattlefield.BossActivityNo,
                    "first reconciliation uses pre-boundary Player epoch");
                AssertEqual(beforeActivity, oldPath.Session.stage.BossSingleActivityNo,
                    "first reconciliation uses pre-boundary Stage epoch");

                NotifyFubenBossSingleData snapshot = BuildAt(oldPath.Session.player, atBoundary);
                AssertEqual(atActivity, snapshot.FubenBossSingleData.ActivityNo,
                    "independent post-boundary snapshot advances its Player epoch");
                AssertEqual(atActivity, oldPath.Session.player.SimulatedBattlefield.BossActivityNo,
                    "old path persists the post-boundary Player epoch");
                AssertEqual(beforeActivity, oldPath.Session.stage.BossSingleActivityNo,
                    "old path leaves Stage at its first reconciliation epoch");
                Player persistedPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                    playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Old path did not persist Player."));
                Stage persistedStage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                    stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Old path did not persist Stage."));
                AssertEqual(atActivity, persistedPlayer.SimulatedBattlefield.BossActivityNo,
                    "old path Player epoch survives BSON reload");
                AssertEqual(beforeActivity, persistedStage.BossSingleActivityNo,
                    "old path Stage epoch survives BSON reload");
            }

            using (LoopbackSessionHarness sharedPath = Harness(46_813, "boss-single-shared-week-boundary"))
            {
                ReconcileAt(sharedPath.Session, beforeBoundary);
                NotifyFubenBossSingleData snapshot = BuildAt(sharedPath.Session.player, beforeBoundary);
                AssertEqual(beforeActivity, sharedPath.Session.player.SimulatedBattlefield.BossActivityNo,
                    "shared timestamp keeps Player in reconciliation epoch");
                AssertEqual(beforeActivity, sharedPath.Session.stage.BossSingleActivityNo,
                    "shared timestamp keeps Stage in reconciliation epoch");
                AssertEqual(beforeActivity, snapshot.FubenBossSingleData.ActivityNo,
                    "shared timestamp keeps snapshot in reconciliation epoch");
            }
        }



        private static void ValidateBossSingleReconnectStageSnapshot()
        {
            using MongoCollectionOverride mongoOverride = MongoCollectionOverride.InstallForBossCompatibility(
                out RecordingMongoCollectionProxy<Player> playerCollection,
                out RecordingMongoCollectionProxy<Stage> stageCollection);
            const long playerId = 46_811;
            const string reconnectToken = "boss-single-reconnect-token";
            Player player = CreateDrawCompatibilityPlayer(playerId);
            player.Token = reconnectToken;
            player.SimulatedBattlefield = new();
            using LoopbackSessionHarness seed = new(
                CreateDrawCompatibilityCharacter(playerId),
                player,
                CreateDrawCompatibilityInventory(playerId, []),
                "boss-single-reconnect-seed");
            seed.Session.stage = CreateLoginAccountCompatibilityStage(playerId);

            Type bossModule = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.BossModule");
            MethodInfo buildLoginData = RequiredMethod(
                bossModule,
                "BuildLoginData",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(Player), typeof(long?)]);
            NotifyFubenBossSingleData login = (NotifyFubenBossSingleData?)buildLoginData.Invoke(null, [player, null])
                ?? throw new InvalidDataException("BossModule.BuildLoginData returned nil.");
            int currentActivity = login.FubenBossSingleData.ActivityNo;
            int stageId = TableReaderV2.Parse<BossSingleSectionTable>()
                .Where(row => row.AfreshId == login.FubenBossSingleData.AfreshId
                    && player.SimulatedBattlefield.BossList.Contains(row.SectionId))
                .SelectMany(row => row.StageId)
                .First(id => id > 0);
            player.SimulatedBattlefield.BossChallengeCount = 9;
            player.SimulatedBattlefield.BossActivityNo = currentActivity - 1;
            seed.Session.stage.BossSingleActivityNo = currentActivity - 1;
            seed.Session.stage.AddStage(new StageDatum { StageId = stageId, Passed = true });
            player.Save();
            seed.Session.stage.Save();

            Player persistedPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Reconnect fixture Player was not persisted."));
            Stage persistedStage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Reconnect fixture Stage was not persisted."));

            void AssertReconnectSnapshot(int lastMsgSeqNo, string name)
            {
                using LoopbackSessionHarness reconnect = new(
                    CreateDrawCompatibilityCharacter(playerId),
                    MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(persistedPlayer.ToBson()),
                    CreateDrawCompatibilityInventory(playerId, []),
                    name);
                reconnect.Session.stage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(persistedStage.ToBson());
                InvokeRegisteredRequestHandler(
                    nameof(ReconnectRequest),
                    reconnect.Session,
                    lastMsgSeqNo + 1,
                    new ReconnectRequest
                    {
                        Token = reconnectToken,
                        PlayerId = checked((uint)playerId),
                        LastMsgSeqNo = lastMsgSeqNo
                    });
                Packet responsePacket = reconnect.ReadPacket($"{name} reconnect response");
                AssertEqual(Packet.ContentType.Response, responsePacket.Type, $"{name} response packet type");
                Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(responsePacket.Content);
                AssertEqual(nameof(ReconnectResponse), response.Name, $"{name} response packet name");
                AssertEqual(0, MessagePackSerializer.Deserialize<ReconnectResponse>(response.Content).Code,
                    $"{name} reconnect response code");

                bool sawStageData = false;
                for (int packetIndex = 0; packetIndex < 32; packetIndex++)
                {
                    Packet pushPacket = reconnect.ReadPacket($"{name} push {packetIndex + 1}");
                    AssertEqual(Packet.ContentType.Push, pushPacket.Type, $"{name} push packet type");
                    AssertEqual(true, pushPacket.No > lastMsgSeqNo, $"{name} continues acknowledged push sequence");
                    Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(pushPacket.Content);
                    if (push.Name == nameof(NotifyStageData))
                    {
                        NotifyStageData update = MessagePackSerializer.Deserialize<NotifyStageData>(push.Content);
                        AssertEqual(false, update.StageList.Single(stage => stage.StageId == stageId).Passed,
                            $"{name} resends the cleared Boss stage");
                        sawStageData = true;
                        continue;
                    }
                    if (push.Name != nameof(NotifyFubenBossSingleData))
                        continue;
                    AssertEqual(true, sawStageData, $"{name} refreshes mode state after Stage synchronization");
                    NotifyFubenBossSingleData updateBoss = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(push.Content);
                    AssertEqual(currentActivity, updateBoss.FubenBossSingleData.ActivityNo,
                        $"{name} refreshes the Boss ActivityNo cache");
                    AssertEqual(reconnect.Session.stage.BossSingleActivityNo,
                        updateBoss.FubenBossSingleData.ActivityNo,
                        $"{name} snapshot uses the reconciled Stage ownership epoch");
                    AssertEqual(0, updateBoss.FubenBossSingleData.ChallengeCount,
                        $"{name} refreshes the reconciled Boss Attempts cache");
                    return;
                }

                throw new InvalidDataException($"{name}: reconnect did not send Boss Single Stage data.");
            }

            AssertReconnectSnapshot(57, "boss-single-rollover-reconnect");
            persistedPlayer = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Player>(
                playerCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Reconnect rollover Player was not persisted."));
            AssertEqual(0, persistedPlayer.SimulatedBattlefield.BossChallengeCount,
                "Reconnect persists the reset Boss Attempts");
            persistedStage = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<Stage>(
                stageCollection.LastSuccessfulReplacementBson
                    ?? throw new InvalidDataException("Reconnect rollover Stage was not persisted."));
            AssertEqual(currentActivity, persistedStage.BossSingleActivityNo,
                "Reconnect persists the Boss Single ownership epoch");
            AssertReconnectSnapshot(93, "boss-single-notification-retry-reconnect");
        }


        private static void ValidateBossSingleLoginCompatibilityShape()
        {
            NotifyFubenBossSingleData notification = new()
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = 260,
                    TotalScore = 0,
                    MaxScore = 0,
                    OldLevelType = 8,
                    LevelType = 8,
                    ChallengeCount = 0,
                    RemainTime = 3600 * 24,
                    AutoFightCount = 0,
                    RankPlatform = 1,
                    TrialStageInfoList =
                    [
                        BuildBossSingleStageInfo(30302803),
                        BuildBossSingleStageInfo(30302804),
                        BuildBossSingleStageInfo(30302805)
                    ],
                    AfreshId = 1,
                    ChallengeLevelType = 0,
                    IsResetOpen = true,
                    NormalStageTeamInfos =
                    [
                        BuildBossSingleTeamInfo(2030),
                        BuildBossSingleTeamInfo(2034),
                        BuildBossSingleTeamInfo(2038)
                    ]
                },
                BossListDict = new()
                {
                    [7] = new() { 102, 104, 109 },
                    [8] = new() { 2030, 2034, 2038 }
                }
            };

            NotifyFubenBossSingleData roundTrip = MessagePackSerializer.Deserialize<NotifyFubenBossSingleData>(
                MessagePackSerializer.Serialize(notification));

            NotifyFubenBossSingleData.NotifyFubenBossSingleDataFubenBossSingleData bossSingleData = roundTrip.FubenBossSingleData
                ?? throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData serialized as nil.");
            AssertEmptyList(bossSingleData.CharacterPoints, "NotifyFubenBossSingleData FubenBossSingleData.CharacterPoints");
            AssertEmptyList(bossSingleData.HistoryList, "NotifyFubenBossSingleData FubenBossSingleData.HistoryList");
            AssertEmptyList(bossSingleData.RewardIds, "NotifyFubenBossSingleData FubenBossSingleData.RewardIds");
            AssertEmptyList(bossSingleData.BossList, "NotifyFubenBossSingleData FubenBossSingleData.BossList");
            AssertEmptyList(bossSingleData.BestiraryStageInfoList, "NotifyFubenBossSingleData FubenBossSingleData.BestiraryStageInfoList");
            AssertEmptyList(bossSingleData.ChallengeStageHistoryList, "NotifyFubenBossSingleData FubenBossSingleData.ChallengeStageHistoryList");
            AssertEmptyList(bossSingleData.StageRecordList, "NotifyFubenBossSingleData FubenBossSingleData.StageRecordList");
            AssertEqual(3, bossSingleData.TrialStageInfoList.Count, "NotifyFubenBossSingleData FubenBossSingleData.TrialStageInfoList count");
            AssertEqual(3, bossSingleData.NormalStageTeamInfos.Count, "NotifyFubenBossSingleData FubenBossSingleData.NormalStageTeamInfos count");
            AssertEqual(true, bossSingleData.IsResetOpen, "NotifyFubenBossSingleData FubenBossSingleData.IsResetOpen");
            AssertEqual(1, bossSingleData.AfreshId, "NotifyFubenBossSingleData FubenBossSingleData.AfreshId");
            AssertEqual(8, bossSingleData.LevelType, "NotifyFubenBossSingleData FubenBossSingleData.LevelType");
            AssertEqual(8, bossSingleData.OldLevelType, "NotifyFubenBossSingleData FubenBossSingleData.OldLevelType");
            if (roundTrip.BossListDict is null)
                throw new InvalidDataException("NotifyFubenBossSingleData BossListDict serialized as nil.");
            AssertEqual(2, roundTrip.BossListDict.Count, "NotifyFubenBossSingleData BossListDict section count");
            AssertBossListDictValues(roundTrip.BossListDict, 7, [102, 104, 109]);
            AssertBossListDictValues(roundTrip.BossListDict, 8, [2030, 2034, 2038]);
            if (!roundTrip.BossListDict.ContainsKey(bossSingleData.LevelType))
                throw new InvalidDataException("NotifyFubenBossSingleData BossListDict: expected a section list for FubenBossSingleData.LevelType.");
            if (bossSingleData.RemainTime == 0)
                throw new InvalidDataException("NotifyFubenBossSingleData FubenBossSingleData.RemainTime: expected a positive value.");

            static Dictionary<string, object> BuildBossSingleStageInfo(int stageId)
            {
                return new()
                {
                    ["StageId"] = stageId,
                    ["Score"] = 0
                };
            }

            static Dictionary<string, object> BuildBossSingleTeamInfo(int sectionId)
            {
                return new()
                {
                    ["SectionId"] = sectionId,
                    ["CharacterIds"] = Array.Empty<int>()
                };
            }

            static void AssertBossListDictValues(
                IReadOnlyDictionary<int, List<int>> bossListDict,
                int sectionId,
                int[] expectedBossIds)
            {
                if (!bossListDict.TryGetValue(sectionId, out List<int>? actualBossIds))
                    throw new InvalidDataException($"NotifyFubenBossSingleData BossListDict: expected section {sectionId}.");
                if (!actualBossIds.SequenceEqual(expectedBossIds))
                    throw new InvalidDataException($"NotifyFubenBossSingleData BossListDict section {sectionId}: expected {string.Join(",", expectedBossIds)}, got {string.Join(",", actualBossIds)}.");
            }
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
        AssertEqual(true, response.FightData!.Restartable,
            "Intensive challenge PreFight reports the authored stage restart permission");
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

}
