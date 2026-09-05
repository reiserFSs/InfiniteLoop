using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.fuben.bosssingle;
using AscNet.Table.V2.share.fuben.bossactivity;
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

    [MessagePackObject(true)]
    public class BossActivityStarRewardRequest
    {
        public int Id { get; set; }
    }

    [MessagePackObject(true)]
    public class BossActivityStarRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
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
        private static readonly Lazy<List<BossSingleChallengeGradeTable>> ChallengeGrades = new(() =>
            TableReaderV2.Parse<BossSingleChallengeGradeTable>());
        private static readonly Lazy<List<BossSingleChallengeFeatureGroupTable>> ChallengeFeatureGroups = new(() =>
        {
            List<BossSingleChallengeFeatureTable> features = TableReaderV2.Parse<BossSingleChallengeFeatureTable>();
            List<BossSingleChallengeBuffGroupTable> buffs = TableReaderV2.Parse<BossSingleChallengeBuffGroupTable>();
            return TableReaderV2.Parse<BossSingleChallengeFeatureGroupTable>()
                .Where(row => row.FeatureIds.Count > 0
                    && row.FeatureIds.Count == row.BuffGroupIds.Count
                    // The client indexes these two columns by card position, then resolves each
                    // BuffGroupId through its BuffGroupIndex table.
                    && row.FeatureIds.Zip(row.BuffGroupIds).All(pair =>
                        features.Any(feature => feature.Id == pair.First)
                        && buffs.Any(buff => buff.BuffGroupId == pair.Second && buff.Index > 0)))
                .OrderBy(row => row.Id)
                .ToList();
        });
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
        private static readonly Lazy<List<BossActivityTable>> ActivityBossActivities = new(() =>
            TableReaderV2.Parse<BossActivityTable>());
        private static readonly Lazy<List<BossSectionTable>> ActivityBossSections = new(() =>
            TableReaderV2.Parse<BossSectionTable>());
        private static readonly Lazy<Dictionary<int, BossChallengeTable>> ActivityBossChallenges = new(() =>
            TableReaderV2.Parse<BossChallengeTable>().ToDictionary(row => row.Id));
        private static readonly Lazy<Dictionary<int, BossStarRewardTable>> ActivityBossStarRewards = new(() =>
            TableReaderV2.Parse<BossStarRewardTable>().ToDictionary(row => row.Id));

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

            HydrateBossStages(session, sendPushes: true);

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

            bool isFirstClear = !session.stage.Stages.TryGetValue((uint)pending.StageId, out StageDatum? previousStageData)
                || !previousStageData.Passed;
            if (!TryCommitScore(session, pending, false, out StageDatum? stageData))
            {
                session.SendResponse(new BossSingleSaveScoreResponse { Code = 1 }, packet.Id);
                return;
            }

            session.PendingBossSingleScore = null;
            SendRankPush(session);
            TaskModule.RecordStageClear(session, pending.StageId, 1, 0, isFirstClear);
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
            if (!CanConsumeAttempt(state, ResolveGrade(state.BossLevelType), sectionId, history.Characters, stageStatus))
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
            bool isFirstClear = !session.stage.Stages.TryGetValue((uint)request.StageId, out StageDatum? previousStageData)
                || !previousStageData.Passed;
            if (!TryCommitScore(session, pending, true, out StageDatum? stageData))
            {
                session.SendResponse(new BossSingleAutoFightResponse { Code = 1 }, packet.Id);
                return;
            }

            state.BossAutoFightCount++;
            TaskModule.RecordStageClear(session, request.StageId, 1, 0, isFirstClear);
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
            if (record is null
                || state.BossResetStageIds.Contains(request.StageId)
                || !TryResolveNormalStage(state, request.StageId, out _, out _))
            {
                session.SendResponse(new BossSingleResetStageResponse { Code = 1 }, packet.Id);
                return;
            }

            foreach (int characterId in record.Characters.Distinct())
            {
                int points = state.BossCharacterPoints.GetValueOrDefault(characterId);
                if (points <= 1)
                    state.BossCharacterPoints.Remove(characterId);
                else
                    state.BossCharacterPoints[characterId] = points - 1;
            }
            record.Score = 0;
            record.Characters.Clear();
            record.IsUseAutoFight = false;
            state.BossResetStageIds.Add(request.StageId);
            state.BossCurrentTotalScore = state.BossStageRecords.Sum(value => value.Score);
            session.PendingBossSingleScore = null;
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
            BossSingleChallengeRankInfoRequest request = packet.Deserialize<BossSingleChallengeRankInfoRequest>();
            RankSnapshot rank = BuildChallengeRankSnapshot(session.player, request.StageId);
            session.SendResponse(new BossSingleChallengeRankInfoResponse { Code = 0, Rank = rank.Rank, TotalRank = rank.Total }, packet.Id);
        }

        [RequestPacketHandler("BossSingleGetChallengeRankRequest")]
        public static void BossSingleGetChallengeRankRequestHandler(Session session, Packet.Request packet)
        {
            BossSingleGetChallengeRankRequest request = packet.Deserialize<BossSingleGetChallengeRankRequest>();
            RankSnapshot snapshot = BuildChallengeRankSnapshot(session.player, request.StageId);
            session.SendResponse(new BossSingleGetChallengeRankResponse
            {
                Code = 0, LeftTime = checked((int)RemainingTime(null)), RankNum = snapshot.Rank,
                Score = snapshot.Score, TotalCount = snapshot.Total,
                RankList = snapshot.Standings.Take(99).Select((entry, index) => (dynamic)new Dictionary<string, object>
                {
                    ["Id"] = entry.Player.PlayerData.Id, ["Name"] = entry.Player.PlayerData.Name,
                    ["RankNum"] = index + 1, ["Score"] = entry.Score
                }).ToList()
            }, packet.Id);
        }

        [RequestPacketHandler("GetActivityBossDataRequest")]
        public static void GetActivityBossDataRequestHandler(Session session, Packet.Request packet)
        {
            NotifyBossActivityData? data = BuildActivityLoginData(session);
            if (data is not null)
                session.SendPush(data);

            session.SendResponse(new GetActivityBossDataResponse { Code = data is null ? 1 : 0 }, packet.Id);
        }

        [RequestPacketHandler("BossActivityStarRewardRequest")]
        public static void BossActivityStarRewardRequestHandler(Session session, Packet.Request packet)
        {
            BossActivityStarRewardRequest request = packet.Deserialize<BossActivityStarRewardRequest>();
            NotifyBossActivityData? data = BuildActivityLoginData(session);
            BossSectionTable? section = data is null ? null : ActivityBossSections.Value
                .FirstOrDefault(row => row.Id == data.SectionId && row.ActivityId == data.ActivityId);
            if (section is null
                || !section.StarRewardId.Contains(request.Id)
                || !ActivityBossStarRewards.Value.TryGetValue(request.Id, out BossStarRewardTable? reward)
                || IsActivityRewardClaimed(session, data!.ActivityId, section.Id, request.Id))
            {
                session.SendResponse(new BossActivityStarRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            int stars = 0;
            foreach (int challengeId in section.ChallengeId.Where(id => id > 0))
            {
                int stageId = ActivityBossChallenges.Value[challengeId].StageId;
                if (session.stage.Stages.TryGetValue(stageId, out StageDatum? stage))
                    stars += System.Numerics.BitOperations.PopCount((uint)(stage.StarsMark & 7));
            }
            if (stars < reward.RequireStar)
            {
                session.SendResponse(new BossActivityStarRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(reward.RewardId);
            if (goods.Count == 0)
            {
                session.SendResponse(new BossActivityStarRewardResponse { Code = 1 }, packet.Id);
                return;
            }

            RewardApplicationResult application = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(ActivityRewardClaimKey(session, data!.ActivityId, section.Id, reward.Id), goods)], session);
            application.SendPushes(session);
            session.SendResponse(new BossActivityStarRewardResponse
            {
                Code = 0,
                RewardGoodsList = application.RewardGoods
            }, packet.Id);
        }

        private static string ActivityRewardClaimKey(Session session, int activityId, int sectionId, int rewardId) =>
            $"boss-activity:{activityId}:{sectionId}:{session.player.PlayerData.Id}:{rewardId}";

        private static bool IsActivityRewardClaimed(Session session, int activityId, int sectionId, int rewardId)
        {
            string key = ActivityRewardClaimKey(session, activityId, sectionId, rewardId);
            return session.inventory.AppliedRewardClaims?.Contains(key, StringComparer.Ordinal) == true
                && session.character.AppliedRewardClaims?.Contains(key, StringComparer.Ordinal) == true;
        }

        internal static void PushActivityProgress(Session session, long completedStageId)
        {
            NotifyBossActivityData? data = BuildActivityLoginData(session);
            if (data is null)
                return;

            BossSectionTable? section = ActivityBossSections.Value
                .FirstOrDefault(row => row.Id == data.SectionId && row.ActivityId == data.ActivityId);
            if (section is not null && section.ChallengeId.Any(id => id > 0
                && ActivityBossChallenges.Value.TryGetValue(id, out BossChallengeTable? challenge)
                && challenge.StageId == completedStageId))
                session.SendPush(data);
        }

        internal static bool IsStage(uint stageId) => Stages.Value.ContainsKey(checked((int)stageId));

        internal static bool ApplyPreFight(
            Session session,
            PreFightRequest.PreFightRequestPreFightData request,
            PreFightResponse response)
        {
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
                    return false;

                SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
                int stageStatus = DetermineStageStatus(state, stageId, characters);
                if (!CanConsumeAttempt(state, ResolveGrade(state.BossLevelType), sectionId, characters, stageStatus))
                    return false;

                state.BossNormalStageTeams[sectionId] = characters.ToList();
                session.player.Save();
            }
            else if (stageType == 3)
            {
                int ignoredBuffGroup;
                List<int> ignoredFeatureIds;
                Dictionary<int, int> ignoredBuffChoices;
                if (!TryGetChallengeBuffGroup(request.BossSingleChallengeBuffGroup,
                    session.player.SimulatedBattlefield, out ignoredBuffGroup, out ignoredFeatureIds, out ignoredBuffChoices))
                    return false;
            }
            ApplyChallengeFeatureEvents(request, response.FightData, session.player.SimulatedBattlefield);

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
                3 => 9,
                4 => 8,
                _ => session.player.SimulatedBattlefield.BossLevelType
            };
            bossResult = CalculateFightResult(
                settle,
                stage,
                ResolveScoreRule(stageId),
                levelType,
                DetermineStageStatus(session.player.SimulatedBattlefield, stageId, characters));
            int pendingBuffGroup;
            List<int> pendingFeatureIds;
            Dictionary<int, int> pendingBuffChoices;
            _ = TryGetChallengeBuffGroup(fight.PreFight.PreFightData.BossSingleChallengeBuffGroup,
                session.player.SimulatedBattlefield, out pendingBuffGroup, out pendingFeatureIds, out pendingBuffChoices);
            int buffGroup = pendingBuffGroup;
            session.PendingBossSingleScore = new BossSinglePendingScore
            {
                StageId = stageId,
                StageType = stageType,
                SectionId = sectionId,
                BuffGroup = buffGroup,
                BuffChoices = pendingBuffChoices,
                Result = bossResult,
                Characters = characters,
                Partners = partners
            };
            return true;
        }

        internal static void CancelFight(Session session) => session.PendingBossSingleScore = null;

        internal static NotifyFubenBossSingleData BuildLoginData(Player player, long? now = null)
        {
            if (Reconcile(player, now))
                player.Save();

            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            BossSingleGradeTable? grade = state.BossLevelType > 0 ? ResolveGrade(state.BossLevelType) : null;
            (int LevelType, int SectionId, int FeatureGroupId)? challenge = ResolveChallengeData(state, grade);
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
                    ChallengeLevelType = challenge?.LevelType ?? 0,
                    ChallengeSectionId = challenge?.SectionId ?? 0,
                    ChallengeFeatureGroupId = challenge?.FeatureGroupId ?? 0,
                    ChallengeTotalScore = ChallengeDisplayTotal(state, challenge?.SectionId ?? 0),
                    ChallengeStageHistoryList = state.BossChallengeHistory
                        .OrderBy(record => record.StageId)
                        .Select(record => new NotifyFubenBossSingleData.NotifyFubenBossSingleDataChallengeStageHistory
                        {
                            StageId = record.StageId, Score = record.Score,
                            Characters = record.Characters.ToList(), Partners = record.Partners.ToList(),
                            BuffGroup = record.BuffGroup > 0
                                ? new Dictionary<string, object>
                                {
                                    ["BuffGroupId"] = record.BuffGroup,
                                    ["BuffChoices"] = new Dictionary<int, int>(record.BuffChoices)
                                }
                                : null
                        }).ToList(),
                    ChallengeDeleteRecordTime = checked((int)state.BossChallengeDeleteRecordTime),
                    IsResetOpen = true,
                    StageRecordList = state.BossStageRecords
                        .Where(record => !state.BossResetStageIds.Contains(record.StageId))
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
        private static (int LevelType, int SectionId, int FeatureGroupId)? ResolveChallengeData(
            SimulatedBattlefieldState state,
            BossSingleGradeTable? normalGrade)
        {
            if (normalGrade is null || state.BossActivityNo <= 0)
                return null;

            BossSingleChallengeGradeTable? challengeGrade = ChallengeGrades.Value
                .Where(row => normalGrade.GradeType >= row.NeedGradeType
                    && state.BossTotalScore >= row.NeedScore)
                .OrderByDescending(row => row.LevelType)
                .FirstOrDefault();
            if (challengeGrade is null
                || !Groups.Value.TryGetValue(challengeGrade.BossGroupId, out BossSingleGroupTable? group))
                return null;

            List<(int LogicalId, int TableId)> sections = group.SectionId
                .Where(HasCurrentSection)
                .Select(logicalId => (logicalId, ResolveSection(logicalId).Id))
                .ToList();
            if (sections.Count == 0 || ChallengeFeatureGroups.Value.Count == 0)
                return null;

            int sectionIndex = checked((int)(StableHash(
                $"{state.BossActivityNo}:{challengeGrade.LevelType}:{challengeGrade.BossGroupId}") % (uint)sections.Count));
            int featureIndex = checked((int)(StableHash(
                $"{state.BossActivityNo}:{challengeGrade.LevelType}:feature") % (uint)ChallengeFeatureGroups.Value.Count));
            (int LogicalId, int TableId) selectedSection = sections.FirstOrDefault(candidate =>
                candidate.TableId == state.BossChallengeSelectedSection
                || candidate.LogicalId == state.BossChallengeSelectedSection);
            if (selectedSection == default)
                selectedSection = sections[sectionIndex];
            int featureGroupId = ChallengeFeatureGroups.Value.Any(row => row.Id == state.BossChallengeSelectedFeatureGroup)
                ? state.BossChallengeSelectedFeatureGroup : ChallengeFeatureGroups.Value[featureIndex].Id;
            state.BossChallengeSelectedSection = selectedSection.TableId;
            state.BossChallengeSelectedFeatureGroup = featureGroupId;
            return (challengeGrade.LevelType, selectedSection.TableId, featureGroupId);
        }
 
        private static int ChallengeDisplayTotal(SimulatedBattlefieldState state, int sectionId)
        {
            if (sectionId <= 0) return 0;
            HashSet<int> stageIds = ResolveChallengeSection(sectionId).StageId.ToHashSet();
            return state.BossChallengeHistory
                .Where(record => stageIds.Contains(record.StageId))
                .Sum(record => record.Score);
        }

        private static int ChallengeRankTotal(SimulatedBattlefieldState state, int levelType)
        {
            if (levelType <= 0) return 0;
            int take = ChallengeGrades.Value.FirstOrDefault(row => row.LevelType == levelType)?.RankStageNum ?? 0;
            return state.BossChallengeHistory.OrderByDescending(record => record.Score).Take(take).Sum(record => record.Score);
        }

        private static bool TryGetChallengeBuffGroup(
            object? value,
            SimulatedBattlefieldState state,
            out int buffGroup,
            out List<int> featureIds,
            out Dictionary<int, int> buffChoices)
        {
            buffGroup = 0;
            featureIds = [];
            buffChoices = new();
            if (value is null) return false;

            object? raw = value is int direct ? direct
                : value is IDictionary<string, object> strings
                    ? strings.FirstOrDefault(entry => string.Equals(entry.Key, "BuffGroup", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry.Key, "BuffGroupId", StringComparison.OrdinalIgnoreCase)).Value
                    : value is IDictionary<object, object> map
                        ? map.FirstOrDefault(entry => string.Equals(Convert.ToString(entry.Key), "BuffGroup", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Convert.ToString(entry.Key), "BuffGroupId", StringComparison.OrdinalIgnoreCase)).Value
                        : null;
            try { buffGroup = Convert.ToInt32(raw, CultureInfo.InvariantCulture); }
            catch { return false; }

            var challenge = ResolveChallengeData(state, state.BossLevelType > 0 ? ResolveGrade(state.BossLevelType) : null);
            if (challenge is null || challenge.Value.FeatureGroupId <= 0) return false;
            BossSingleChallengeFeatureGroupTable? group = ChallengeFeatureGroups.Value
                .SingleOrDefault(row => row.Id == challenge.Value.FeatureGroupId);
            int pairingIndex = group?.BuffGroupIds.IndexOf(buffGroup) ?? -1;
            if (group is null || pairingIndex < 0 || pairingIndex >= group.FeatureIds.Count)
                return false;
            featureIds.Add(group.FeatureIds[pairingIndex]);

            object? choices = value switch
            {
                IDictionary<string, object> stringChoices => stringChoices.FirstOrDefault(entry =>
                    string.Equals(entry.Key, "BuffChoices", StringComparison.OrdinalIgnoreCase)).Value,
                IDictionary<object, object> objectChoices => objectChoices.FirstOrDefault(entry =>
                    string.Equals(Convert.ToString(entry.Key), "BuffChoices", StringComparison.OrdinalIgnoreCase)).Value,
                _ => null
            };
            if (choices is null) return true;
            if (choices is not System.Collections.IDictionary choiceMap)
                return false;
            IEnumerable<KeyValuePair<object, object>> choiceEntries = choiceMap.Keys
                .Cast<object>()
                .Select(key => new KeyValuePair<object, object>(key, choiceMap[key]!));
            int selectedBuffGroup = buffGroup;
            List<BossSingleChallengeBuffGroupTable> buffRows = TableReaderV2
                .Parse<BossSingleChallengeBuffGroupTable>()
                .Where(row => row.BuffGroupId == selectedBuffGroup)
                .ToList();
            foreach ((object key, object selectedIndexValue) in choiceEntries)
            {
                if (!int.TryParse(Convert.ToString(key, CultureInfo.InvariantCulture), out int index)
                    || !int.TryParse(Convert.ToString(selectedIndexValue, CultureInfo.InvariantCulture), out int selectedIndex))
                    return false;
                BossSingleChallengeBuffGroupTable? row = buffRows.SingleOrDefault(candidate => candidate.Index == index);
                if (row is null || selectedIndex <= 0 || selectedIndex > row.Buff.Count)
                    return false;
                int selectedFeatureId = row.Buff[selectedIndex - 1];
                if (selectedFeatureId <= 0) return false;
                buffChoices[index] = selectedIndex;
                featureIds.Add(selectedFeatureId);
            }
            return true;
        }

        private static void ApplyChallengeFeatureEvents(
            PreFightRequest.PreFightRequestPreFightData request,
            PreFightResponse.PreFightResponseFightData fightData,
            SimulatedBattlefieldState state)
        {
            if (request.BossSingleStageType != 3) return;
            if (!TryGetChallengeBuffGroup(request.BossSingleChallengeBuffGroup, state,
                out int selectedBuffGroup, out List<int> featureIds, out Dictionary<int, int> _))
                return;
            foreach (int featureId in featureIds)
            {
                BossSingleChallengeFeatureTable? feature = TableReaderV2.Parse<BossSingleChallengeFeatureTable>()
                    .FirstOrDefault(row => row.Id == featureId);
                if (feature?.FightEventIds > 0) fightData.EventIds.Add(feature.FightEventIds);
            }
        }

        internal static NotifyBossActivityData? BuildActivityLoginData(Session session, DateTimeOffset? now = null)
        {
            DateTimeOffset effectiveNow = now ?? DateTimeOffset.UtcNow;
            BossActivityTable? activity = ActivityBossActivities.Value
                .Where(candidate => candidate.ActivityTimeId is > 0
                    && ActivityScheduleService.IsOpen(candidate.ActivityTimeId.Value, effectiveNow))
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefault();
            if (activity is null)
                return null;

            long playerLevel = session.player.PlayerData.Level;
            BossSectionTable? section = ActivityBossSections.Value
                .Where(candidate => candidate.ActivityId == activity.Id
 
                    && candidate.MinLevel <= playerLevel
                    && candidate.MaxLevel >= playerLevel)
                .OrderBy(candidate => candidate.OrderId)
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefault();
            if (section is null)
                return null;

            List<int> stageIds = new();
            foreach (int challengeId in section.ChallengeId.Where(id => id > 0))
            {
                if (!ActivityBossChallenges.Value.TryGetValue(challengeId, out BossChallengeTable? challenge)
                    || challenge.StageId <= 0)
                    return null;

                stageIds.Add(challenge.StageId);
            }

            Dictionary<long, StageDatum> persistedStages = session.stage.Stages;
            int schedule = 0;
            foreach (int stageId in stageIds)
            {
                if (!persistedStages.TryGetValue(stageId, out StageDatum? stage) || !stage.Passed)
                    break;

                schedule++;
            }

            return new NotifyBossActivityData
            {
                ActivityId = activity.Id,
                SectionId = section.Id,
                Schedule = schedule,
                StarRewardIds = section.StarRewardId
                    .Where(id => id > 0 && IsActivityRewardClaimed(session, activity.Id, section.Id, id))
                    .Select(id => (dynamic)id).ToList(),
                StageStarInfos = stageIds.Select(stageId =>
                {
                    persistedStages.TryGetValue(stageId, out StageDatum? stage);
                    return (dynamic)new Dictionary<string, object>
                    {
                        ["StageId"] = stageId,
                        ["StarsMark"] = checked((int)(stage?.StarsMark ?? 0))
                    };
                }).ToList(),
                DifficultyScoreRecord = stageIds
                    .Where(stageId => persistedStages.TryGetValue(stageId, out _))
                    .ToDictionary(stageId => stageId, stageId => checked((int)persistedStages[stageId].Score))
            };
        }

        internal static void PrepareLogin(Session session)
        {
            if (Reconcile(session.player, null))
                session.player.Save();
            if (session.stage is not null)
                HydrateBossStages(session, sendPushes: false);
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

            RewardApplicationResult rewardResult = RewardHandler.ApplyRewards(goods, session);
            state.BossClaimedRewardIds.AddRange(eligible.Select(row => row.Id));
            state.BossClaimedRewardIds = state.BossClaimedRewardIds.Distinct().OrderBy(id => id).ToList();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            rewardResult.SendPushes(session);
            session.SendPush(BuildLoginData(session.player));
            if (requestedId is null)
            {
                session.SendResponse(new BossSingleGetAllRewardResponse
                {
                    Code = 0,
                    RewardGoodsList = rewardResult.RewardGoods
                }, packetId);
            }
            else
            {
                session.SendResponse(new BossSingleGetRewardResponse
                {
                    Code = 0,
                    RewardGoodsList = rewardResult.RewardGoods
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
                Dictionary<int, int> scores = pending.StageType == 2 ? state.BossTrialScores : state.BossBestiaryScores;
                scores[pending.StageId] = Math.Max(scores.GetValueOrDefault(pending.StageId), pending.Result.TotalScore);
                stageData = UpdateStageDatum(session, pending, pending.Result.TotalScore);
                session.stage.Save();
                session.player.Save();
                return true;
            }
            if (pending.StageType == 3)
            {
                BossSingleChallengeHistoryRecordState? challengeRecord = state.BossChallengeHistory.Find(value => value.StageId == pending.StageId);
                if (challengeRecord is null)
                {
                    challengeRecord = new BossSingleChallengeHistoryRecordState { StageId = pending.StageId };
                    state.BossChallengeHistory.Add(challengeRecord);
                }
                if (pending.Result.TotalScore > challengeRecord.Score)
                {
                    challengeRecord.Score = pending.Result.TotalScore;
                    challengeRecord.Characters = pending.Characters.ToList();
                    challengeRecord.Partners = pending.Partners.ToList();
                    challengeRecord.BuffGroup = pending.BuffGroup;
                    challengeRecord.BuffChoices = new(pending.BuffChoices);
                    state.BossLastScoreTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                stageData = UpdateStageDatum(session, pending, challengeRecord.Score);
                session.player.Save();
                return true;
            }
            if (pending.StageType != 1)
                return false;
            BossSingleGradeTable grade = ResolveGrade(state.BossLevelType);
            int stageStatus = DetermineStageStatus(state, pending.StageId, pending.Characters);
            if (!CanConsumeAttempt(state, grade, pending.SectionId, pending.Characters, stageStatus))
                return false;
            ConsumeAttempt(state, pending.SectionId, pending.Characters, stageStatus);

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
            ArchiveRecord(state, record);

            state.BossResetStageIds.Remove(pending.StageId);
            state.BossNormalStageTeams[pending.SectionId] = pending.Characters.ToList();
            RecalculateNormalTotals(state);
            state.BossLastScoreTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            stageData = UpdateStageDatum(session, pending, record.MaxScore);
            session.stage.Save();
            return true;
        }

        private static bool RecalculateNormalTotals(SimulatedBattlefieldState state)
        {
            int currentTotal = state.BossStageRecords.Sum(value => value.Score);
            int total = state.BossStageRecords.Sum(value => value.MaxScore);
            bool changed = state.BossCurrentTotalScore != currentTotal
                || state.BossTotalScore != total;
            state.BossCurrentTotalScore = currentTotal;
            state.BossTotalScore = total;
            return changed;
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
            int coefficientIndex = Math.Clamp(levelType - 1, 0, rule.BossLoseHp.Count - 1);
            double bossStep = ParseCoefficient(rule.BossLoseHp, coefficientIndex);
            double bossPoints = rule.BossLoseHpScore[coefficientIndex];
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
            double bossLostHp = Math.Max(0, bossMaximumHp - bossCurrentHp);
            double bossLostRatio = bossMaximumHp <= 0
                ? (settle.IsWin ? 1d : 0d)
                : Math.Clamp(bossLostHp / bossMaximumHp, 0d, 1d);
            int bossDamagePer = checked((int)Math.Floor(bossLostRatio * 100d));
            int bossScore = ScoreBySteps(bossLostRatio, bossStep, bossPoints, stage.BossLoseHpScore);
            int passTime = Math.Max(1, stage.PassTimeLimit);
            int timeLeft = Math.Clamp(checked((int)settle.LeftTime), 0, passTime);
            int timeScore = Math.Min(stage.LeftTimeScore,
                checked((int)Math.Floor(timeLeft * timeCoefficient * passTime)));

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
            if (RecalculateNormalTotals(state))
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
            state.BossChallengeHistory ??= new();
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
            state.BossChallengeHistory.Clear();
            state.BossChallengeSelectedSection = 0;
            state.BossChallengeSelectedFeatureGroup = 0;
            state.BossChallengeDeleteRecordTime = 0;
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

        private static bool HydrateBossStages(Session session, bool sendPushes)
        {
            bool changed = HydrateNormalStages(session, sendPushes);
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield;
            HashSet<int> stageIds = new();

            foreach (BossSingleTrialGradeTable catalog in TrialGrades.Value)
            {
                if (catalog.LevelType is not (4 or 8)
                    || (catalog.IsBestiaryCfg != 0) != (catalog.LevelType == 8))
                    continue;
                foreach (int sectionId in catalog.SectionId)
                    if (sectionId > 0)
                        stageIds.UnionWith(ResolveSection(sectionId, false).StageId);
            }

            BossSingleGradeTable? grade = state.BossLevelType > 0 ? ResolveGrade(state.BossLevelType) : null;
            var challenge = ResolveChallengeData(state, grade);
            if (challenge is not null)
                stageIds.UnionWith(ResolveChallengeSection(challenge.Value.SectionId).StageId);

            List<StageDatum>? addedStages = sendPushes ? new() : null;
            foreach (int stageId in stageIds)
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
            if (changed)
                session.stage.Save();
            return changed;
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
                ArchiveRecord(state, record);
        }

        private static void ArchiveRecord(
            SimulatedBattlefieldState state,
            BossSingleStageRecordState record)
        {
            BossSingleHistoryRecordState? history = state.BossHistory.Find(value => value.StageId == record.StageId);
            if (history is not null && history.Score >= record.MaxScore)
                return;
            if (history is null)
            {
                history = new BossSingleHistoryRecordState { StageId = record.StageId };
                state.BossHistory.Add(history);
            }
            history.Score = record.MaxScore;
            history.Characters = record.MaxCharacters.ToList();
            history.Partners = record.MaxPartners.ToList();
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
            if (stageType == 3)
            {
                var challenge = ResolveChallengeData(state, state.BossLevelType > 0 ? ResolveGrade(state.BossLevelType) : null);
                if (challenge is not null && challenge.Value.LevelType == 9
                    && ResolveChallengeSection(challenge.Value.SectionId).StageId.Contains(stageId))
                {
                    sectionId = challenge.Value.SectionId;
                    return true;
                }
                return false;
            }
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
        private static BossSingleSectionTable ResolveChallengeSection(int tableId) =>
            Sections.Value.FirstOrDefault(row =>
                row.Id == tableId && row.AfreshId == CurrentAfreshId)
            ?? throw new InvalidDataException($"No Pain Cage challenge section table row {tableId} for AfreshId {CurrentAfreshId}.");


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
            if (state.BossResetStageIds.Contains(stageId))
                return 1;
            BossSingleStageRecordState? record = state.BossStageRecords.Find(value => value.StageId == stageId);
            if (record is null)
                return 0;
            return record.Characters.SequenceEqual(characters) ? 2 : 3;
        }

        private static bool CanConsumeAttempt(
            SimulatedBattlefieldState state,
            BossSingleGradeTable grade,
            int sectionId,
            IReadOnlyCollection<int> characters,
            int stageStatus)
        {
            if (stageStatus == 0
                && !HasSectionRecord(state, sectionId)
                && state.BossChallengeCount >= CurrentChallengeLimit(grade, null))
            {
                return false;
            }
            if (stageStatus is 0 or 1 or 3)
            {
                return characters.All(characterId =>
                    state.BossCharacterPoints.GetValueOrDefault(characterId) < grade.StaminaCount);
            }
            return true;
        }

        private static void ConsumeAttempt(
            SimulatedBattlefieldState state,
            int sectionId,
            IReadOnlyCollection<int> characters,
            int stageStatus)
        {
            if (stageStatus == 0 && !HasSectionRecord(state, sectionId))
                state.BossChallengeCount++;
            if (stageStatus is 0 or 1 or 3)
            {
                foreach (int characterId in characters)
                    state.BossCharacterPoints[characterId] = state.BossCharacterPoints.GetValueOrDefault(characterId) + 1;
            }
        }

        private static bool HasSectionRecord(SimulatedBattlefieldState state, int sectionId)
        {
            List<int> stageIds = ResolveSection(sectionId).StageId;
            return state.BossStageRecords.Any(record => stageIds.Contains(record.StageId));
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
        private static RankSnapshot BuildChallengeRankSnapshot(Player player, int stageId)
        {
            int Score(SimulatedBattlefieldState value) => stageId == 0 ? ChallengeRankTotal(value, 9) : value.BossChallengeHistory.FirstOrDefault(record => record.StageId == stageId)?.Score ?? 0;
            SimulatedBattlefieldState state = player.SimulatedBattlefield;
            List<Player> participants;
            try { participants = Player.collection.Find(candidate => candidate.SimulatedBattlefield.BossActivityNo == state.BossActivityNo).ToList(); }
            catch { participants = [player]; }
            if (participants.All(candidate => candidate.PlayerData.Id != player.PlayerData.Id)) participants.Add(player);
            List<(Player Player, int Score)> standings = participants.Select(candidate => (candidate, Score(candidate.SimulatedBattlefield)))
                .Where(entry => entry.Item2 > 0).OrderByDescending(entry => entry.Item2)
                .ThenBy(entry => entry.candidate.SimulatedBattlefield.BossLastScoreTime).ThenBy(entry => entry.candidate.PlayerData.Id).ToList();
            int score = Score(state);
            int rank = score <= 0 ? 0 : standings.FindIndex(entry => entry.Player.PlayerData.Id == player.PlayerData.Id) + 1;
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

        private static int ScoreBySteps(double value, double step, double points, int maximum)
        {
            if (step <= 0 || points <= 0 || value <= 0)
                return 0;
            return Math.Min(maximum, checked((int)Math.Floor(value / step * points)));
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
