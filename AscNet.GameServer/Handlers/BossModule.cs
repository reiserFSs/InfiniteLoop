using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.bosssingle;
using AscNet.Table.V2.share.reward;
using MessagePack;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class BossSingleRankInfoRequest
    {
        public int SectionId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleRankInfoResponse
    {
        public int Code { get; set; }
        public int Rank { get; set; }
        public int TotalRank { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSelectLevelTypeRequest
    {
        public int LevelId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSelectLevelTypeResponse
    {
        public int Code { get; set; }
        public NotifyFubenBossSingleData.NotifyFubenBossSingleDataFubenBossSingleData FubenBossSingleData { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSaveScoreRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleSaveScoreResponse
    {
        public int Code { get; set; }
        public int Supply { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleAutoFightRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleAutoFightResponse
    {
        public int Code { get; set; }
        public int Supply { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleResetStageRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleResetStageResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleGetRewardRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleGetRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BossSingleGetAllRewardRequest
    {
    }

    [MessagePackObject(true)]
    public class BossSingleGetAllRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BossSingleGetRankRequest
    {
        public int Level { get; set; }
        public int SectionId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleGetChallengeRankRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleGetRankResponse
    {
        public int Code { get; set; }
        public int LeftTime { get; set; }
        public int RankNum { get; set; }
        public int Score { get; set; }
        public int HistoryNum { get; set; }
        public int TotalCount { get; set; }
        public List<dynamic> RankList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BossSingleGetChallengeRankResponse
    {
        public int Code { get; set; }
        public int LeftTime { get; set; }
        public int RankNum { get; set; }
        public int Score { get; set; }
        public int HistoryNum { get; set; }
        public int TotalCount { get; set; }
        public List<dynamic> RankList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class BossSingleChallengeRankInfoRequest
    {
        public int StageId { get; set; }
    }

    [MessagePackObject(true)]
    public class BossSingleChallengeRankInfoResponse
    {
        public int Code { get; set; }
        public int Rank { get; set; }
        public int TotalRank { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyBossSingleRankInfo
    {
        public int RankType { get; set; }
        public int Rank { get; set; }
        public int TotalRank { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyBossSingleChallengeCount
    {
        public int ChallengeCount { get; set; }
    }

    [MessagePackObject(true)]
    public class GetActivityBossDataRequest
    {
    }

    [MessagePackObject(true)]
    public class GetActivityBossDataResponse
    {
        public int Code { get; set; }
    }
#pragma warning restore CS8618
    #endregion

    internal class BossModule
    {
        private const int FightFramesPerSecond = 20;
        private static int CurrentAfreshId => Grades.Value.Max(row => row.AfreshId);

        private static readonly Lazy<List<BossSingleGradeTable>> Grades = new(() =>
            TableReaderV2.Parse<BossSingleGradeTable>().OrderBy(row => row.LevelType).ToList());
        private static readonly Lazy<Dictionary<int, BossSingleGroupTable>> Groups = new(() =>
            TableReaderV2.Parse<BossSingleGroupTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<List<BossSingleSectionTable>> Sections = new(() =>
            TableReaderV2.Parse<BossSingleSectionTable>());
        private static readonly Lazy<Dictionary<int, BossSingleStageTable>> Stages = new(() =>
            TableReaderV2.Parse<BossSingleStageTable>().ToDictionary(row => row.StageId));
        private static readonly Lazy<Dictionary<int, BossSingleScoreRuleTable>> ScoreRules = new(() =>
            TableReaderV2.Parse<BossSingleScoreRuleTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<List<BossSingleScoreRewardTable>> ScoreRewards = new(() =>
            TableReaderV2.Parse<BossSingleScoreRewardTable>()
                .OrderBy(row => row.Score)
                .ThenBy(row => row.Id)
                .ToList());
        private static readonly Lazy<List<BossSingleTrialGradeTable>> TrialGrades = new(() =>
            TableReaderV2.Parse<BossSingleTrialGradeTable>());
        private static readonly Lazy<List<BossSingleRewardGoodsTable>> ScoreRewardGoods = new(() =>
            TableReaderV2.Parse<BossSingleRewardGoodsTable>());
        private static readonly Lazy<BossSingleConfigTable> RuntimeConfig = new(() =>
            TableReaderV2.Parse<BossSingleConfigTable>().Single(row => row.Id == 1));

        [RequestPacketHandler("BossSingleRankInfoRequest")]
        public static void BossSingleRankInfoRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            BossSingleRankInfoRequest request = packet.Deserialize<BossSingleRankInfoRequest>();
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            if (request.SectionId != 0 && !state.BossList.Contains(request.SectionId))
            {
                session.SendResponse(new BossSingleRankInfoResponse { Code = 1 }, packet.Id);
                return;
            }

            RankSnapshot rank = BuildRankSnapshot(session.player, request.SectionId);
            session.SendResponse(new BossSingleRankInfoResponse
            {
                Code = 0,
                Rank = rank.Rank,
                TotalRank = rank.Total
            }, packet.Id);
        }

        [RequestPacketHandler("BossSingleSelectLevelTypeRequest")]
        public static void BossSingleSelectLevelTypeRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            BossSingleSelectLevelTypeRequest request = packet.Deserialize<BossSingleSelectLevelTypeRequest>();
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            if (!state.BossListOptions.TryGetValue(request.LevelId, out List<int>? bossList)
                || (state.BossLevelType != 0 && state.BossLevelType != request.LevelId))
            {
                session.SendResponse(new BossSingleSelectLevelTypeResponse { Code = 1 }, packet.Id);
                return;
            }

            state.BossLevelType = request.LevelId;
            state.BossList = bossList.ToList();
            session.player.Save();

            HydrateNormalStages(session, sendPushes: true);

            session.SendResponse(new BossSingleSelectLevelTypeResponse
            {
                Code = 0,
                FubenBossSingleData = BuildLoginData(session.player).FubenBossSingleData
            }, packet.Id);
        }

        [RequestPacketHandler("BossSingleSaveScoreRequest")]
        public static void BossSingleSaveScoreRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleSaveScoreRequest request = packet.Deserialize<BossSingleSaveScoreRequest>();
            BossSinglePendingScore? pending = session.PendingBossSingleScore;
            if (pending is null || pending.StageId != request.StageId)
            {
                session.SendResponse(new BossSingleSaveScoreResponse { Code = 1 }, packet.Id);
                return;
            }

            ReconcileLive(session);
            if (!TryCommitScore(session, pending, false, out StageDatum? stageData))
            {
                session.SendResponse(new BossSingleSaveScoreResponse { Code = 1 }, packet.Id);
                return;
            }

            session.PendingBossSingleScore = null;
            TaskModule.RecordStageClear(session, pending.StageId, 1);
            SendRankPush(session);
            if (stageData is not null)
                session.SendPush(new NotifyStageData { StageList = [stageData] });
            session.SendPush(BuildLoginData(session.player));
            session.SendResponse(new BossSingleSaveScoreResponse { Code = 0, Supply = 0 }, packet.Id);
        }

        [RequestPacketHandler("BossSingleAutoFightRequest")]
        public static void BossSingleAutoFightRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            BossSingleAutoFightRequest request = packet.Deserialize<BossSingleAutoFightRequest>();
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            if (!TryResolveNormalStage(state, request.StageId, out int sectionId, out BossSingleStageTable? stage)
                || stage is null
                || stage.AutoFight == 0
                || state.BossAutoFightCount >= RuntimeConfig.Value.AutoFightCount)
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            BossSingleHistoryRecordState? history = state.BossHistory.Find(record => record.StageId == request.StageId);
            BossSingleStageRecordState? current = state.BossStageRecords.Find(record => record.StageId == request.StageId);
            int score = checked((int)Math.Floor((history?.Score ?? 0) * RuntimeConfig.Value.AutoFightRebate / 100d));
            if (history is null || score <= (current?.Score ?? 0))
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            int stageStatus = DetermineStageStatus(state, request.StageId, history.Characters);
            if (!CanConsumeAttempt(state, ResolveGrade(state.BossLevelType), history.Characters, stageStatus))
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            BossSinglePendingScore pending = new()
            {
                StageId = request.StageId,
                StageType = 1,
                SectionId = sectionId,
                Characters = history.Characters.ToList(),
                Partners = history.Partners.ToList(),
                Result = new BossSingleFightResult
                {
                    TotalScore = score,
                    StageStatus = stageStatus
                }
            };
            if (!TryCommitScore(session, pending, true, out StageDatum? stageData))
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            state.BossAutoFightCount++;
            session.player.Save();
            TaskModule.RecordStageClear(session, request.StageId, 1);
            SendRankPush(session);
            session.SendPush(BuildLoginData(session.player));
            if (stageData is not null)
                session.SendPush(new NotifyStageData { StageList = [stageData] });
            session.SendResponse(new BossSingleAutoFightResponse { Code = 0, Supply = 0 }, packet.Id);
        }

        [RequestPacketHandler("BossSingleGetRewardRequest")]
        public static void BossSingleGetRewardRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleGetRewardRequest request = packet.Deserialize<BossSingleGetRewardRequest>();
            ClaimRewards(session, packet.Id, request.Id);
        }

        [RequestPacketHandler("BossSingleGetAllRewardRequest")]
        public static void BossSingleGetAllRewardRequestHandler(Session session, Packet.Request packet) =>
            ClaimRewards(session, packet.Id, null);

        [RequestPacketHandler("BossSingleResetStageRequest")]
        public static void BossSingleResetStageRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            BossSingleResetStageRequest request = packet.Deserialize<BossSingleResetStageRequest>();
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            BossSingleStageRecordState? record = state.BossStageRecords.Find(value => value.StageId == request.StageId);
            if (record is null || !TryResolveNormalStage(state, request.StageId, out _, out _))
            {
                session.SendResponse(new BossSingleResetStageResponse { Code = 1 }, packet.Id);
                return;
            }

            state.BossStageRecords.Remove(record);
            if (!state.BossResetStageIds.Contains(request.StageId))
                state.BossResetStageIds.Add(request.StageId);
            state.BossCurrentTotalScore = state.BossStageRecords.Sum(value => value.Score);
            session.player.Save();
            session.SendPush(BuildLoginData(session.player));
            session.SendResponse(new BossSingleResetStageResponse { Code = 0 }, packet.Id);
        }

        [RequestPacketHandler("BossSingleGetRankRequest")]
        public static void BossSingleGetRankRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            BossSingleGetRankRequest request = packet.Deserialize<BossSingleGetRankRequest>();
            session.SendResponse(BuildRankListResponse(session.player, request.Level, request.SectionId), packet.Id);
        }

        [RequestPacketHandler("BossSingleChallengeRankInfoRequest")]
        public static void BossSingleChallengeRankInfoRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<BossSingleChallengeRankInfoRequest>();
            session.SendResponse(new BossSingleChallengeRankInfoResponse { Code = 0, Rank = 0, TotalRank = 0 }, packet.Id);
        }

        [RequestPacketHandler("BossSingleGetChallengeRankRequest")]
        public static void BossSingleGetChallengeRankRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<BossSingleGetChallengeRankRequest>();
            session.SendResponse(new BossSingleGetChallengeRankResponse
            {
                Code = 0,
                LeftTime = checked((int)RemainingTime(null))
            }, packet.Id);
        }

        [RequestPacketHandler("GetActivityBossDataRequest")]
        public static void GetActivityBossDataRequestHandler(Session session, Packet.Request packet) =>
            session.SendResponse(new GetActivityBossDataResponse { Code = 1 }, packet.Id);

        internal static bool IsStage(uint stageId) => Stages.Value.ContainsKey(checked((int)stageId));

        internal static bool ApplyPreFight(
            Session session,
            PreFightRequest.PreFightRequestPreFightData request,
            PreFightResponse response)
        {
            ReconcileLive(session);
            int stageId = checked((int)request.StageId);
            int stageType = request.BossSingleStageType == 0 ? 1 : request.BossSingleStageType;
            if (!TryResolveFightStage(session.player.SimulatedBattlefield, stageId, stageType, out int sectionId, out BossSingleStageTable? stage)
                || stage is null)
                return false;

            List<int> characters = request.CardIds?
                .Where(id => id > 0)
                .Select(checkedId => checked((int)checkedId))
                .Distinct()
                .ToList() ?? [];
            if (stageType == 1)
            {
                if (characters.Count == 0
                    || characters.Any(characterId => session.character.Characters.All(character => character.Id != characterId)))
                {
                    return false;
                }

                SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
                int stageStatus = DetermineStageStatus(state, stageId, characters);
                if (!CanConsumeAttempt(state, ResolveGrade(state.BossLevelType), characters, stageStatus))
                    return false;

                state.BossNormalStageTeams[sectionId] = characters.ToList();
                session.player.Save();
            }

            session.PendingBossSingleScore = null;
            response.FightData.FightCheckType = 1;
            response.FightData.PassTimeLimit = stage.PassTimeLimit;
            return true;
        }

        internal static bool TryBuildFightSettle(
            Session session,
            FightSettleResult settle,
            out BossSingleFightResult? bossResult)
        {
            bossResult = null;
            Fight? fight = session.fight;
            if (fight is null || !IsStage(settle.StageId))
                return false;

            int stageId = checked((int)settle.StageId);
            int stageType = fight.PreFight.PreFightData.BossSingleStageType == 0
                ? 1
                : fight.PreFight.PreFightData.BossSingleStageType;
            if (!TryResolveFightStage(
                    session.player.SimulatedBattlefield,
                    stageId,
                    stageType,
                    out int sectionId,
                    out BossSingleStageTable? stage)
                || stage is null)
            {
                return false;
            }

            List<int> characters = fight.PreFight.PreFightData.CardIds?
                .Where(id => id > 0)
                .Select(id => checked((int)id))
                .Distinct()
                .ToList() ?? [];
            List<int> partners = characters
                .Select(characterId => session.character.Partners
                    .FirstOrDefault(partner => partner.CharacterId == characterId)?.Id ?? 0)
                .ToList();
            int levelType = stageType switch
            {
                2 => 4,
                4 => 8,
                _ => session.player.SimulatedBattlefield.BossLevelType
            };
            bossResult = CalculateFightResult(
                settle,
                stage,
                ResolveScoreRule(stageId),
                levelType,
                DetermineStageStatus(session.player.SimulatedBattlefield, stageId, characters));
            session.PendingBossSingleScore = new BossSinglePendingScore
            {
                StageId = stageId,
                StageType = stageType,
                SectionId = sectionId,
                Result = bossResult,
                Characters = characters,
                Partners = partners
            };
            return true;
        }

        internal static void CancelFight(Session session)
        {
            if (session.fight is not null && IsStage(session.fight.PreFight.PreFightData.StageId))
                session.PendingBossSingleScore = null;
        }

        internal static NotifyFubenBossSingleData BuildLoginData(Player player, long? now = null)
        {
            if (Reconcile(player, now))
                player.Save();

            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            BossSingleGradeTable? grade = state.BossLevelType > 0 ? ResolveGrade(state.BossLevelType) : null;
            return new NotifyFubenBossSingleData
            {
                FubenBossSingleData = new()
                {
                    ActivityNo = state.BossActivityNo,
                    TotalScore = state.BossTotalScore,
                    MaxScore = state.BossMaxScore,
                    OldLevelType = state.BossOldLevelType,
                    LevelType = state.BossLevelType,
                    ChallengeCount = state.BossChallengeCount,
                    RemainTime = checked((uint)RemainingTime(now)),
                    AutoFightCount = state.BossAutoFightCount,
                    CharacterPoints = new Dictionary<int, int>(state.BossCharacterPoints),
                    HistoryList = state.BossHistory
                        .OrderBy(record => record.StageId)
                        .Select(record => (dynamic)new Dictionary<string, object>
                        {
                            ["StageId"] = record.StageId,
                            ["Score"] = record.Score,
                            ["Characters"] = record.Characters.ToArray(),
                            ["Partners"] = record.Partners.ToArray()
                        })
                        .ToList(),
                    RewardIds = state.BossClaimedRewardIds.OrderBy(id => id).Cast<dynamic>().ToList(),
                    RewardGroupId = grade?.RewardGroupId ?? 0,
                    RankPlatform = state.BossRankPlatform,
                    BossList = state.BossList.ToList(),
                    TrialStageInfoList = BuildStageScoreList(state.BossTrialScores),
                    BestiraryStageInfoList = BuildStageScoreList(state.BossBestiaryScores),
                    AfreshId = CurrentAfreshId,
                    ChallengeLevelType = 0,
                    ChallengeSectionId = 0,
                    ChallengeFeatureGroupId = 0,
                    ChallengeTotalScore = 0,
                    ChallengeStageHistoryList = [],
                    ChallengeDeleteRecordTime = 0,
                    IsResetOpen = true,
                    StageRecordList = state.BossStageRecords
                        .OrderBy(record => record.StageId)
                        .Select(record => (dynamic)new Dictionary<string, object>
                        {
                            ["StageId"] = record.StageId,
                            ["Score"] = record.Score,
                            ["Characters"] = record.Characters.ToArray(),
                            ["IsUseAutoFight"] = record.IsUseAutoFight,
                            ["MaxScore"] = record.MaxScore,
                            ["MaxCharacters"] = record.MaxCharacters.ToArray(),
                            ["MaxPartners"] = record.MaxPartners.ToArray()
                        })
                        .ToList(),
                    CurTotalScore = state.BossCurrentTotalScore,
                    NormalStageTeamInfos = state.BossNormalStageTeams
                        .OrderBy(entry => entry.Key)
                        .Select(entry => (dynamic)new Dictionary<string, object>
                        {
                            ["SectionId"] = entry.Key,
                            ["CharacterIds"] = entry.Value.ToArray()
                        })
                        .ToList()
                },
                BossListDict = state.BossLevelType == 0
                    ? state.BossListOptions.ToDictionary(entry => entry.Key, entry => entry.Value.ToList())
                    : null
            };
        }

        internal static void PrepareLogin(Session session)
        {
            if (Reconcile(session.player, null))
                session.player.Save();
            if (session.stage is not null)
                HydrateNormalStages(session, sendPushes: false);
        }

        private static void ClaimRewards(Session session, int packetId, int? requestedId)
        {
            ReconcileLive(session);
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            BossSingleGradeTable grade = ResolveGrade(state.BossLevelType);
            List<BossSingleScoreRewardTable> eligible = ScoreRewards.Value
                .Where(row => row.LevelType == state.BossLevelType
                    && row.RewardGroupId == grade.RewardGroupId
                    && row.Score <= state.BossTotalScore
                    && !state.BossClaimedRewardIds.Contains(row.Id)
                    && (requestedId is null || row.Id == requestedId.Value))
                .ToList();
            if (requestedId is not null && eligible.Count != 1)
            {
                session.SendResponse(new BossSingleGetRewardResponse { Code = 1 }, packetId);
                return;
            }

            HashSet<int> rewardIds = eligible.Select(row => row.Id).ToHashSet();
            List<RewardGoodsTable> goods = ScoreRewardGoods.Value
                .Where(row => rewardIds.Contains(row.ScoreRewardId))
                .Select(row => new RewardGoodsTable
                {
                    Id = row.GoodsId,
                    TemplateId = row.TemplateId,
                    Count = row.Count
                })
                .ToList();
            if (eligible.Count > 0 && goods.Count == 0)
                throw new InvalidDataException($"Pain Cage reward rows {string.Join(",", rewardIds)} resolve to no goods.");

            List<RewardGoods> responseGoods = RewardHandler.GiveRewards(goods, session);
            state.BossClaimedRewardIds.AddRange(eligible.Select(row => row.Id));
            state.BossClaimedRewardIds = state.BossClaimedRewardIds.Distinct().OrderBy(id => id).ToList();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            session.SendPush(BuildLoginData(session.player));
            if (requestedId is null)
            {
                session.SendResponse(new BossSingleGetAllRewardResponse
                {
                    Code = 0,
                    RewardGoodsList = responseGoods
                }, packetId);
            }
            else
            {
                session.SendResponse(new BossSingleGetRewardResponse
                {
                    Code = 0,
                    RewardGoodsList = responseGoods
                }, packetId);
            }
        }

        private static bool TryCommitScore(
            Session session,
            BossSinglePendingScore pending,
            bool isAutoFight,
            out StageDatum? stageData)
        {
            stageData = null;
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            if (!TryResolveFightStage(state, pending.StageId, pending.StageType, out _, out _))
                return false;

            if (pending.StageType == 2 || pending.StageType == 4)
            {
                Dictionary<int, int> scores = pending.StageType == 2
                    ? state.BossTrialScores
                    : state.BossBestiaryScores;
                scores[pending.StageId] = Math.Max(scores.GetValueOrDefault(pending.StageId), pending.Result.TotalScore);
                stageData = UpdateStageDatum(session, pending, pending.Result.TotalScore);
                session.player.Save();
                session.stage.Save();
                return true;
            }
            if (pending.StageType != 1)
                return false;

            BossSingleGradeTable grade = ResolveGrade(state.BossLevelType);
            int stageStatus = DetermineStageStatus(state, pending.StageId, pending.Characters);
            if (!CanConsumeAttempt(state, grade, pending.Characters, stageStatus))
                return false;
            ConsumeAttempt(state, pending.Characters, stageStatus);

            BossSingleStageRecordState? record = state.BossStageRecords.Find(value => value.StageId == pending.StageId);
            if (record is null)
            {
                record = new BossSingleStageRecordState { StageId = pending.StageId };
                state.BossStageRecords.Add(record);
            }
            record.Score = pending.Result.TotalScore;
            record.Characters = pending.Characters.ToList();
            record.IsUseAutoFight = isAutoFight;
            if (pending.Result.TotalScore > record.MaxScore)
            {
                record.MaxScore = pending.Result.TotalScore;
                record.MaxCharacters = pending.Characters.ToList();
                record.MaxPartners = pending.Partners.ToList();
            }

            state.BossResetStageIds.Remove(pending.StageId);
            state.BossNormalStageTeams[pending.SectionId] = pending.Characters.ToList();
            state.BossCurrentTotalScore = state.BossStageRecords.Sum(value => value.Score);
            int bestCurrentTotal = state.BossStageRecords.Sum(value => value.MaxScore);
            state.BossTotalScore = Math.Max(state.BossTotalScore, bestCurrentTotal);
            state.BossMaxScore = Math.Max(state.BossMaxScore, state.BossTotalScore);
            state.BossLastScoreTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            stageData = UpdateStageDatum(session, pending, record.MaxScore);
            session.player.Save();
            session.stage.Save();
            return true;
        }

        private static StageDatum UpdateStageDatum(Session session, BossSinglePendingScore pending, int bestScore)
        {
            uint stageId = checked((uint)pending.StageId);
            bool exists = session.stage.Stages.TryGetValue(stageId, out StageDatum? stage);
            stage ??= NewStageDatum(pending.StageId);
            bool newBest = bestScore > stage.Score;
            stage.Passed = true;
            stage.Score = Math.Max(stage.Score, bestScore);
            stage.PassTimesTotal = Math.Max(1, stage.PassTimesTotal);
            stage.LastPassTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            stage.LastRecordTime = pending.Result.FightTime;
            stage.LastCardIds = pending.Characters.Select(id => (long)id).ToList();
            if (newBest)
            {
                stage.BestRecordTime = pending.Result.FightTime;
                stage.BestCardIds = pending.Characters.Select(id => (long)id).ToList();
            }
            if (!exists)
                session.stage.AddStage(stage);
            return stage;
        }

        private static BossSingleFightResult CalculateFightResult(
            FightSettleResult settle,
            BossSingleStageTable stage,
            BossSingleScoreRuleTable rule,
            int levelType,
            int stageStatus)
        {
            int coefficientIndex = Math.Clamp(levelType - 1, 0, rule.BossLoseHpScore.Count - 1);
            double bossCoefficient = rule.BossLoseHpScore[coefficientIndex];
            double timeCoefficient = ParseCoefficient(rule.LeftTimeScore, coefficientIndex);
            double hpCoefficient = ParseCoefficient(rule.CharLeftHpSocre, coefficientIndex);

            NpcHp? boss = settle.NpcHpInfo?.Values
                .Where(npc => npc.Type == 2)
                .OrderByDescending(npc => AttributeValue(npc, "MaxValue"))
                .FirstOrDefault()
                ?? settle.NpcHpInfo?.Values
                    .Where(npc => npc.Type == 3)
                    .OrderByDescending(npc => AttributeValue(npc, "MaxValue"))
                    .FirstOrDefault();
            double bossMaximumHp = boss is null ? 0 : AttributeValue(boss, "MaxValue");
            double bossCurrentHp = boss is null ? 0 : AttributeValue(boss, "Value");
            int bossDamagePer = bossMaximumHp <= 0
                ? (settle.IsWin ? 100 : 0)
                : Math.Clamp((int)Math.Floor((bossMaximumHp - bossCurrentHp) * 100d / bossMaximumHp), 0, 100);
            int bossScore = Math.Min(stage.BossLoseHpScore, checked((int)Math.Floor(bossDamagePer * bossCoefficient)));

            int passTime = Math.Max(1, stage.PassTimeLimit);
            int timeLeft = Math.Clamp(checked((int)settle.LeftTime), 0, passTime);
            double timeRatio = timeLeft / (double)passTime;
            int timeScore = timeCoefficient > 0 && timeCoefficient <= 1
                ? checked((int)Math.Floor(stage.LeftTimeScore
                    * Math.Clamp((timeRatio - (1d - timeCoefficient)) / timeCoefficient, 0d, 1d)))
                : Math.Min(stage.LeftTimeScore, checked((int)Math.Floor(timeLeft * timeCoefficient)));

            List<double> characterHp = settle.NpcHpInfo?.Values
                .Where(npc => npc.Type == 1)
                .Select(npc =>
                {
                    double maximum = AttributeValue(npc, "MaxValue");
                    return maximum > 0 ? Math.Clamp(AttributeValue(npc, "Value") * 100d / maximum, 0, 100) : 0;
                })
                .ToList() ?? [];
            int hpLeftPer = characterHp.Count == 0 ? 0 : checked((int)Math.Floor(characterHp.Average()));
            int hpVariableScore = Math.Min(stage.LeftHpScore, checked((int)Math.Floor(hpLeftPer * hpCoefficient)));
            int maxHpScore = checked(rule.BaseScore + stage.LeftHpScore);
            int hpScore = checked(rule.BaseScore + hpVariableScore);
            int totalScore = Math.Min(
                checked(stage.Score + rule.BaseScore),
                checked(bossScore + timeScore + hpScore));
            int activeFrames = checked((int)Math.Max(
                0,
                settle.SettleFrame - settle.StartFrame - settle.PauseFrame));

            return new BossSingleFightResult
            {
                FightTime = activeFrames / FightFramesPerSecond,
                BossDamagePer = bossDamagePer,
                BossDamageScore = bossScore,
                MaxBossDamageScore = stage.BossLoseHpScore,
                TimeLeft = timeLeft,
                TimeScore = timeScore,
                MaxTimeScore = stage.LeftTimeScore,
                HpLeftPer = hpLeftPer,
                HpScore = hpScore,
                MaxHpScore = maxHpScore,
                TotalScore = totalScore,
                StageStatus = stageStatus
            };
        }

        private static bool Reconcile(Player player, long? now)
        {
            player.SimulatedBattlefield ??= new SimulatedBattlefieldState();
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            Normalize(state);
            int activity = CurrentActivity(now);
            bool changed = false;
            if (state.BossActivityNo == 0)
            {
                state.BossActivityNo = activity;
                state.BossOldLevelType = ResolveInitialLevelType(checked((int)player.PlayerData.Level));
                ResetCycle(state, checked((int)player.PlayerData.Level), activity);
                changed = true;
            }
            else if (state.BossActivityNo != activity)
            {
                ArchiveCurrentRecords(state);
                state.BossMaxScore = Math.Max(state.BossMaxScore, state.BossTotalScore);
                if (state.BossLevelType > 0)
                    state.BossOldLevelType = state.BossLevelType;
                state.BossActivityNo = activity;
                ResetCycle(state, checked((int)player.PlayerData.Level), activity);
                changed = true;
            }

            long dailyReset = CurrentResetDay(now);
            if (state.BossChallengeResetDay != dailyReset)
            {
                state.BossChallengeResetDay = dailyReset;
                state.BossChallengeCount = 0;
                changed = true;
            }


            if (state.BossListOptions.Count == 0)
            {
                state.BossListOptions = BuildBossListOptions(state, checked((int)player.PlayerData.Level), state.BossActivityNo);
                changed = true;
            }

            if (TrySelectOnlyOption(state))
                changed = true;
            return changed;
        }

        private static void ReconcileLive(Session session)
        {
            int previousActivity = session.player.SimulatedBattlefield?.BossActivityNo ?? 0;
            if (Reconcile(session.player, null))
                session.player.Save();
            if (previousActivity != 0 && previousActivity != session.player.SimulatedBattlefield!.BossActivityNo)
                session.PendingBossSingleScore = null;
        }

        private static void Normalize(SimulatedBattlefieldState state)
        {
            state.BossListOptions ??= new();
            state.BossList ??= new();
            state.BossCharacterPoints ??= new();
            state.BossHistory ??= new();
            state.BossStageRecords ??= new();
            state.BossResetStageIds ??= new();
            state.BossNormalStageTeams ??= new();
            state.BossTrialScores ??= new();
            state.BossBestiaryScores ??= new();
            state.BossClaimedRewardIds ??= new();
        }

        private static void ResetCycle(SimulatedBattlefieldState state, int playerLevel, int activity)
        {
            state.BossLevelType = 0;
            state.BossList = [];
            state.BossListOptions = BuildBossListOptions(state, playerLevel, activity);
            TrySelectOnlyOption(state);
            state.BossTotalScore = 0;
            state.BossCurrentTotalScore = 0;
            state.BossChallengeCount = 0;
            state.BossAutoFightCount = 0;
            state.BossCharacterPoints.Clear();
            state.BossStageRecords.Clear();
            state.BossResetStageIds.Clear();
            state.BossClaimedRewardIds.Clear();
            state.BossLastScoreTime = 0;
        }

        private static bool TrySelectOnlyOption(SimulatedBattlefieldState state)
        {
            if (state.BossLevelType != 0 || state.BossListOptions.Count != 1)
                return false;

            KeyValuePair<int, List<int>> option = state.BossListOptions.Single();
            state.BossLevelType = option.Key;
            state.BossList = option.Value.ToList();
            return true;
        }

        private static bool HydrateNormalStages(Session session, bool sendPushes)
        {
            if (session.player.SimulatedBattlefield.BossLevelType == 0)
                return false;

            bool changed = false;
            foreach (int sectionId in session.player.SimulatedBattlefield.BossList)
            {
                List<StageDatum>? addedStages = sendPushes ? new() : null;
                foreach (int stageId in ResolveSection(sectionId).StageId)
                {
                    if (session.stage.Stages.ContainsKey((uint)stageId))
                        continue;

                    StageDatum stage = NewStageDatum(stageId);
                    session.stage.AddStage(stage);
                    addedStages?.Add(stage);
                    changed = true;
                }

                if (addedStages?.Count > 0)
                    session.SendPush(new NotifyStageData { StageList = addedStages });
            }

            if (changed)
                session.stage.Save();
            return changed;
        }

        private static void ArchiveCurrentRecords(SimulatedBattlefieldState state)
        {
            foreach (BossSingleStageRecordState record in state.BossStageRecords)
            {
                BossSingleHistoryRecordState? history = state.BossHistory.Find(value => value.StageId == record.StageId);
                if (history is not null && history.Score >= record.MaxScore)
                    continue;
                if (history is null)
                {
                    history = new BossSingleHistoryRecordState { StageId = record.StageId };
                    state.BossHistory.Add(history);
                }
                history.Score = record.MaxScore;
                history.Characters = record.MaxCharacters.ToList();
                history.Partners = record.MaxPartners.ToList();
            }
        }

        private static Dictionary<int, List<int>> BuildBossListOptions(
            SimulatedBattlefieldState state,
            int playerLevel,
            int activity)
        {
            Dictionary<int, List<int>> result = new();
            int previousGrade = state.BossOldLevelType > 0
                ? ResolveGrade(state.BossOldLevelType).GradeType
                : 0;
            foreach (BossSingleGradeTable grade in Grades.Value.Where(row =>
                         row.AfreshId == CurrentAfreshId
                         && playerLevel >= row.MinPlayerLevel
                         && playerLevel <= row.MaxPlayerLevel
                         && (row.PreGradeType == 0
                             || (previousGrade >= row.PreGradeType && state.BossMaxScore >= row.NeedScore))))
            {
                HashSet<int> selected = new();
                List<int> bossList = new();
                foreach (int groupId in grade.GroupId)
                {
                    if (!Groups.Value.TryGetValue(groupId, out BossSingleGroupTable? group))
                        throw new InvalidDataException($"Pain Cage grade {grade.LevelType} references missing group {groupId}.");
                    List<int> candidates = group.SectionId
                        .Where(sectionId => HasCurrentSection(sectionId))
                        .ToList();
                    if (candidates.Count == 0)
                        throw new InvalidDataException($"Pain Cage group {groupId} has no current sections.");

                    int start = checked((int)(StableHash($"{activity}:{grade.LevelType}:{groupId}") % (uint)candidates.Count));
                    int choice = Enumerable.Range(0, candidates.Count)
                        .Select(offset => candidates[(start + offset) % candidates.Count])
                        .FirstOrDefault(sectionId => !selected.Contains(sectionId));
                    if (choice == 0)
                        throw new InvalidDataException($"Pain Cage grade {grade.LevelType} cannot choose unique section for group {groupId}.");
                    selected.Add(choice);
                    bossList.Add(choice);
                }
                result[grade.LevelType] = bossList;
            }
            return result;
        }

        private static bool TryResolveFightStage(
            SimulatedBattlefieldState state,
            int stageId,
            int stageType,
            out int sectionId,
            out BossSingleStageTable? stage)
        {
            sectionId = 0;
            stage = null;
            if (!Stages.Value.TryGetValue(stageId, out stage))
                return false;
            if (stageType == 1)
                return TryResolveNormalStage(state, stageId, out sectionId, out stage);
            if (stageType == 2)
                return TryResolveCatalogStage(4, stageId, false, out sectionId);
            if (stageType == 4)
                return TryResolveCatalogStage(8, stageId, true, out sectionId);
            return false;
        }

        private static bool TryResolveNormalStage(
            SimulatedBattlefieldState state,
            int stageId,
            out int sectionId,
            out BossSingleStageTable? stage)
        {
            stage = Stages.Value.GetValueOrDefault(stageId);
            foreach (int selectedSection in state.BossList)
            {
                if (ResolveSection(selectedSection).StageId.Contains(stageId))
                {
                    sectionId = selectedSection;
                    return stage is not null;
                }
            }
            sectionId = 0;
            return false;
        }

        private static bool TryResolveCatalogStage(int levelType, int stageId, bool bestiary, out int sectionId)
        {
            BossSingleTrialGradeTable? catalog = TrialGrades.Value.FirstOrDefault(row =>
                row.LevelType == levelType && (row.IsBestiaryCfg != 0) == bestiary);
            if (catalog is not null)
            {
                foreach (int candidateSection in catalog.SectionId)
                {
                    if (ResolveSection(candidateSection, false).StageId.Contains(stageId))
                    {
                        sectionId = candidateSection;
                        return true;
                    }
                }
            }
            sectionId = 0;
            return false;
        }

        private static BossSingleSectionTable ResolveSection(int sectionId, bool currentOnly = true)
        {
            BossSingleSectionTable? current = Sections.Value.FirstOrDefault(row =>
                row.SectionId == sectionId && row.AfreshId == CurrentAfreshId);
            if (current is not null)
                return current;
            if (!currentOnly)
            {
                BossSingleSectionTable? legacy = Sections.Value.FirstOrDefault(row => row.SectionId == sectionId);
                if (legacy is not null)
                    return legacy;
            }
            throw new InvalidDataException($"No Pain Cage section {sectionId} for AfreshId {CurrentAfreshId}.");
        }

        private static bool HasCurrentSection(int sectionId) => Sections.Value.Any(row =>
            row.SectionId == sectionId && row.AfreshId == CurrentAfreshId);

        private static BossSingleGradeTable ResolveGrade(int levelType) =>
            Grades.Value.SingleOrDefault(row => row.LevelType == levelType)
            ?? throw new InvalidDataException($"No Pain Cage grade {levelType}.");

        private static BossSingleScoreRuleTable ResolveScoreRule(int stageId) =>
            ScoreRules.Value.GetValueOrDefault(stageId)
            ?? throw new InvalidDataException($"No Pain Cage score rule for stage {stageId}.");

        private static int ResolveInitialLevelType(int playerLevel) =>
            Grades.Value
                .Where(row => row.AfreshId == CurrentAfreshId
                    && row.PreGradeType == 0
                    && playerLevel >= row.MinPlayerLevel
                    && playerLevel <= row.MaxPlayerLevel)
                .Select(row => row.LevelType)
                .DefaultIfEmpty(5)
                .Max();

        private static int DetermineStageStatus(
            SimulatedBattlefieldState state,
            int stageId,
            IReadOnlyCollection<int> characters)
        {
            BossSingleStageRecordState? record = state.BossStageRecords.Find(value => value.StageId == stageId);
            if (record is null)
                return state.BossResetStageIds.Contains(stageId) ? 1 : 0;
            return record.Characters.SequenceEqual(characters) ? 2 : 3;
        }

        private static bool CanConsumeAttempt(
            SimulatedBattlefieldState state,
            BossSingleGradeTable grade,
            IReadOnlyCollection<int> characters,
            int stageStatus)
        {
            if (stageStatus == 0 && state.BossChallengeCount >= CurrentChallengeLimit(grade, null))
                return false;
            if (stageStatus is 0 or 1 or 3)
            {
                return characters.All(characterId =>
                    state.BossCharacterPoints.GetValueOrDefault(characterId) < grade.StaminaCount);
            }
            return true;
        }

        private static void ConsumeAttempt(
            SimulatedBattlefieldState state,
            IReadOnlyCollection<int> characters,
            int stageStatus)
        {
            if (stageStatus == 0)
                state.BossChallengeCount++;
            if (stageStatus is 0 or 1 or 3)
            {
                foreach (int characterId in characters)
                    state.BossCharacterPoints[characterId] = state.BossCharacterPoints.GetValueOrDefault(characterId) + 1;
            }
        }

        private static int CurrentChallengeLimit(BossSingleGradeTable grade, long? now)
        {
            long timestamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int resetDayIndex = TaskModule.WeeklyResetDayIndex(timestamp);
            return resetDayIndex >= 5 ? grade.WeekChallengeCount : grade.ChallengeCount;
        }

        private static StageDatum NewStageDatum(int stageId) => new()
        {
            StageId = stageId,
            CreateTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        private static List<dynamic> BuildStageScoreList(Dictionary<int, int> scores) =>
            scores.OrderBy(entry => entry.Key)
                .Select(entry => (dynamic)new Dictionary<string, object>
                {
                    ["StageId"] = entry.Key,
                    ["Score"] = entry.Value
                })
                .ToList();

        private static void SendRankPush(Session session)
        {
            RankSnapshot rank = BuildRankSnapshot(session.player, 0);
            session.SendPush(new NotifyBossSingleRankInfo
            {
                RankType = 1,
                Rank = rank.Rank,
                TotalRank = rank.Total
            });
        }

        private static RankSnapshot BuildRankSnapshot(Player player, int sectionId)
        {
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            List<Player> participants;
            try
            {
                participants = Player.collection.Find(candidate =>
                    candidate.SimulatedBattlefield.BossActivityNo == state.BossActivityNo
                    && candidate.SimulatedBattlefield.BossLevelType == state.BossLevelType).ToList();
            }
            catch
            {
                participants = [player];
            }
            if (participants.All(candidate => candidate.PlayerData.Id != player.PlayerData.Id))
                participants.Add(player);

            List<(Player Player, int Score)> standings = participants
                .Select(candidate => (candidate, RankScore(candidate.SimulatedBattlefield, sectionId)))
                .OrderByDescending(entry => entry.Item2)
                .ThenBy(entry => entry.candidate.SimulatedBattlefield.BossLastScoreTime)
                .ThenBy(entry => entry.candidate.PlayerData.Id)
                .Select(entry => (entry.candidate, entry.Item2))
                .ToList();
            int score = RankScore(state, sectionId);
            int rank = score <= 0
                ? 0
                : standings.FindIndex(entry => entry.Player.PlayerData.Id == player.PlayerData.Id) + 1;
            return new RankSnapshot(rank, standings.Count, score, standings);
        }

        private static int RankScore(SimulatedBattlefieldState state, int sectionId)
        {
            if (sectionId == 0)
                return state.BossTotalScore;
            HashSet<int> stageIds;
            try
            {
                stageIds = ResolveSection(sectionId).StageId.ToHashSet();
            }
            catch (InvalidDataException)
            {
                return 0;
            }
            return state.BossStageRecords
                .Where(record => stageIds.Contains(record.StageId))
                .Sum(record => record.MaxScore);
        }

        private static BossSingleGetRankResponse BuildRankListResponse(Player player, int level, int sectionId)
        {
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            if (level != 0 && level != state.BossLevelType)
                return new BossSingleGetRankResponse { Code = 1 };
            RankSnapshot snapshot = BuildRankSnapshot(player, sectionId);
            return new BossSingleGetRankResponse
            {
                Code = 0,
                LeftTime = checked((int)RemainingTime(null)),
                RankNum = snapshot.Rank,
                Score = snapshot.Score,
                HistoryNum = 0,
                TotalCount = snapshot.Total,
                RankList = snapshot.Standings
                    .Take(99)
                    .Select((entry, index) => (dynamic)new Dictionary<string, object>
                    {
                        ["Id"] = entry.Player.PlayerData.Id,
                        ["Name"] = entry.Player.PlayerData.Name,
                        ["HeadPortraitId"] = entry.Player.PlayerData.CurrHeadPortraitId,
                        ["HeadFrameId"] = entry.Player.PlayerData.CurrHeadFrameId,
                        ["RankNum"] = index + 1,
                        ["Score"] = entry.Score,
                        ["CharacterList"] = BuildRankCharacters(entry.Player.SimulatedBattlefield, sectionId)
                    })
                    .ToList()
            };
        }

        private static List<dynamic> BuildRankCharacters(SimulatedBattlefieldState state, int sectionId)
        {
            IEnumerable<BossSingleStageRecordState> records = state.BossStageRecords;
            if (sectionId != 0)
            {
                HashSet<int> sectionStages = ResolveSection(sectionId).StageId.ToHashSet();
                records = records.Where(record => sectionStages.Contains(record.StageId));
            }
            return records
                .OrderByDescending(record => record.MaxScore)
                .SelectMany(record => record.MaxCharacters)
                .Distinct()
                .Take(3)
                .Select(characterId => (dynamic)new Dictionary<string, object>
                {
                    ["Id"] = characterId,
                    ["LiberateLv"] = 0
                })
                .ToList();
        }

        private static double AttributeValue(NpcHp npc, string key)
        {
            if (npc.AttrTable is null || !npc.AttrTable.TryGetValue(1, out dynamic? attribute))
                return 0;
            if (attribute is IDictionary<object, object> primitive)
            {
                KeyValuePair<object, object> entry = primitive.FirstOrDefault(value =>
                    string.Equals(Convert.ToString(value.Key), key, StringComparison.Ordinal));
                return entry.Key is null ? 0 : Convert.ToDouble(entry.Value, CultureInfo.InvariantCulture);
            }
            if (attribute is IDictionary<string, object> objects && objects.TryGetValue(key, out object? member))
                return Convert.ToDouble(member, CultureInfo.InvariantCulture);
            if (attribute is JObject json && json.TryGetValue(key, out JToken? token))
                return token.Value<double>();
            return 0;
        }

        private static double ParseCoefficient(IReadOnlyList<string> values, int index)
        {
            if (index < 0 || index >= values.Count
                || !double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new InvalidDataException($"Pain Cage score coefficient index {index} is invalid.");
            }
            return value;
        }


        private static int CurrentActivity(long? now)
        {
            long timestamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return checked((int)TaskModule.CurrentWeeklyResetPeriod(timestamp));
        }

        private static long CurrentResetDay(long? now)
        {
            long timestamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return TaskModule.CurrentDailyResetPeriod(timestamp);
        }

        private static long RemainingTime(long? now)
        {
            long timestamp = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return TaskModule.RemainingSecondsInWeeklyResetPeriod(timestamp);
        }

        private static uint StableHash(string value)
        {
            uint hash = 2_166_136_261;
            foreach (char character in value)
                hash = (hash ^ character) * 16_777_619;
            return hash;
        }

        private sealed record RankSnapshot(
            int Rank,
            int Total,
            int Score,
            List<(Player Player, int Score)> Standings);
    }
}
