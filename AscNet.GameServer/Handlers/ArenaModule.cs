using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.chat;
using AscNet.Table.V2.client.fuben.arena;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.fuben.arena;
using MessagePack;
using Newtonsoft.Json.Linq;

namespace AscNet.GameServer.Handlers
{
    #region MsgPackScheme
#pragma warning disable CS8618
    [MessagePackObject(true)]
    public class JoinActivityResponse { public int Code { get; set; } public int ChallengeId { get; set; } }
    [MessagePackObject(true)]
    public class ScoreQueryResponse
    {
        public int Code { get; set; }
        public double WaveRate { get; set; }
        public List<dynamic> GroupPlayerList { get; set; } = new();
        public List<dynamic> TeamPlayerList { get; set; } = new();
        public int ChallengeId { get; set; }
        public int ActivityNo { get; set; }
        public int ArenaLevel { get; set; }
        public int ContributeScore { get; set; }
    }
    [MessagePackObject(true)]
    public class AreaDataResponse
    {
        public int Code { get; set; }
        public int TotalPoint { get; set; }
        public List<dynamic>? AreaList { get; set; }
        public Dictionary<int, int>? AreaDistributeMaxPointDict { get; set; }
        public List<int>? GroupFightEvents { get; set; }
    }
    [MessagePackObject(true)]
    public class GroupMemberResponse
    {
        public int Code { get; set; }
        public double WaveRate { get; set; }
        public List<dynamic> GroupPlayerList { get; set; } = new();
        public dynamic PlayerInfo { get; set; }
    }

    [MessagePackObject(true)]
    public class ArenaChallengeGetRankRequest
    {
        public int ChallengeId { get; set; }
    }

    [MessagePackObject(true)]
    public class ArenaChallengeGetRankResponse
    {
        public int Code { get; set; }
        public int ChallengeId { get; set; }
        public int Ranking { get; set; }
        public int MemberCount { get; set; }
        public ArenaChallengeRank Rank { get; set; } = new();
        public long CacheGetRankResponseTime { get; set; }
        public long BeginTime { get; set; }
        public long EndTime { get; set; }
    }

    [MessagePackObject(true)]
    public class ArenaChallengeRank
    {
        public int Id { get; set; }
        public int ActivityId { get; set; }
        public int ChallengeId { get; set; }
        public List<ArenaChallengeRankPlayer> RankPlayer { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ArenaChallengeRankPlayer
    {
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Head { get; set; }
        public long Frame { get; set; }
        public long Level { get; set; }
        public string Sign { get; set; } = string.Empty;
        public int Score { get; set; }
        public int WinCount { get; set; }
        public int RoleId { get; set; }
        public string GuildName { get; set; } = string.Empty;
        public List<ArenaChallengeCharacterRecord> CharacterRecords { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class ArenaChallengeCharacterRecord
    {
        public int AreaId { get; set; }
        public List<int> CharacterList { get; set; } = new();
        public List<ArenaChallengeCharacterHeadInfo> CharacterHeadInfoList { get; set; } = new();
        public List<int> AbilityList { get; set; } = new();
        public List<int> PartnerList { get; set; } = new();
        public List<int> QualityList { get; set; } = new();
        public int Point { get; set; }
        public int SelectEnvironment { get; set; }
    }

    [MessagePackObject(true)]
    public class ArenaChallengeCharacterHeadInfo
    {
        public int HeadFashionId { get; set; }
        public int HeadFashionType { get; set; }
    }
#pragma warning restore CS8618
    #endregion

    internal class ArenaModule
    {
        private const string ConfigPath = "Configs/simulated_battlefield.json";
        private const int FightFramesPerSecond = 20;
        private static readonly Lazy<JObject> Config = new(() => JsonSnapshot.LoadObject(ConfigPath));
        private static readonly Lazy<List<ChallengeAreaTable>> Challenges = new(() => TableReaderV2.Parse<ChallengeAreaTable>());
        private static readonly Lazy<Dictionary<int, AreaStageTable>> AreaStages = new(() => TableReaderV2.Parse<AreaStageTable>().ToDictionary(x => x.Id));
        private static readonly Lazy<Dictionary<int, MarkTable>> Marks = new(() => TableReaderV2.Parse<MarkTable>().ToDictionary(x => x.MarkId));
        private static readonly Lazy<Dictionary<int, ChatBoardTable>> ChatBoards = new(() => TableReaderV2.Parse<ChatBoardTable>().ToDictionary(x => x.Id));
        private static readonly Lazy<List<ArenaGroupFightEventTable>> FightEventGroups = new(() =>
            TableReaderV2.Parse<ArenaGroupFightEventTable>()
                .OrderBy(group => group.Id)
                .ToList());

        [RequestPacketHandler("JoinActivityRequest")]
        public static void JoinActivityRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            var state = session.player.SimulatedBattlefield!;
            state.ArenaJoined = true;
            state.ArenaJoinActivity = state.ArenaActivityNo;
            session.player.Save();
            session.SendResponse(new JoinActivityResponse { Code = 0, ChallengeId = state.ArenaChallengeId }, packet.Id);
        }

        [RequestPacketHandler("ScoreQueryRequest")]
        public static void ScoreQueryRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            var state = session.player.SimulatedBattlefield!;
            Dictionary<string, object> info = BuildPlayerInfo(session);
            session.SendResponse(new ScoreQueryResponse
            {
                Code = 0, WaveRate = 0, GroupPlayerList = [info], TeamPlayerList = [],
                ChallengeId = state.ArenaChallengeId, ActivityNo = state.ArenaActivityNo,
                ArenaLevel = state.ArenaLevel, ContributeScore = state.ArenaContributeScore
            }, packet.Id);
        }

        [RequestPacketHandler("AreaDataRequest")]
        public static void AreaDataRequestHandler(Session session, Packet.Request packet) =>
            session.SendResponse(BuildAreaData(session), packet.Id);

        [RequestPacketHandler("GroupMemberRequest")]
        public static void GroupMemberRequestHandler(Session session, Packet.Request packet)
        {
            ReconcileLive(session);
            Dictionary<string, object> info = BuildPlayerInfo(session);
            session.SendResponse(new GroupMemberResponse { Code = 0, WaveRate = 0, GroupPlayerList = [info], PlayerInfo = info }, packet.Id);
        }

        [RequestPacketHandler("ArenaChallengeGetRankRequest")]
        public static void ArenaChallengeGetRankRequestHandler(Session session, Packet.Request packet)
        {
            ArenaChallengeGetRankRequest request = packet.Deserialize<ArenaChallengeGetRankRequest>();
            session.SendResponse(BuildChallengeRankResponse(session, request.ChallengeId), packet.Id);
        }

        internal static (ActivityResultNotify? Result, NotifyArenaActivity Activity) ReconcileLogin(Session session, long? now = null)
        {
            Player player = session.player;
            player.SimulatedBattlefield ??= new();
            var state = player.SimulatedBattlefield;
            int activity = CurrentActivity(now);
            ActivityResultNotify? result = null;
            if (state.ArenaActivityNo == 0)
            {
                ChallengeAreaTable initial = ResolveInitialChallenge((int)player.PlayerData.Level);
                state.ArenaLevel = initial.ArenaLv;
                state.ArenaChallengeId = initial.ChallengeId;
                state.ArenaProtectedScore = 0;
                state.ArenaActivityNo = activity;
                state.ArenaJoined = false;
                state.ArenaJoinActivity = 0;
                ResetPoints(state);
                TaskModule.ResetArenaTasks(session);
                player.Save();
            }
            else if (state.ArenaActivityNo != activity)
            {
                result = SettleCycle(session, state);
                state.ArenaActivityNo = activity;
                state.ArenaJoined = false;
                state.ArenaJoinActivity = 0;
                ResetPoints(state);
                TaskModule.ResetArenaTasks(session);
                player.Save();
            }
            return (result, BuildActivity(player, now));
        }

        private static bool ReconcileLive(Session session)
        {
            (ActivityResultNotify? result, NotifyArenaActivity activity) = ReconcileLogin(session);
            if (result is null)
                return false;
            session.SendPush(result);
            session.SendPush(activity);
            TaskModule.SendCurrentTaskBatch(session, CurrentTaskIds(session.player));
            return true;
        }

        internal static NotifyArenaActivity BuildLoginData(Player player, long? now = null)
        {
            EnsureCurrent(player, now);
            return BuildActivity(player, now);
        }

        internal static int[] CurrentTaskIds(Player player)
        {
            EnsureCurrent(player);
            int challengeId = player.SimulatedBattlefield!.ArenaChallengeId;
            ChallengeAreaTable challenge = Challenges.Value.SingleOrDefault(row => row.ChallengeId == challengeId)
                ?? throw new InvalidDataException($"No Arena challenge {challengeId} for current activity.");
            return challenge.TaskId.Where(taskId => taskId > 0).ToArray();
        }

        public static bool IsArenaStage(uint stageId) => AreaStages.Value.Values
            .Any(area => IsSupportedArea(area) && ResolveConfiguredStageId(area) == checked((int)stageId));

        public static ArenaResult? RecordFightResult(Session session, FightSettleResult result)
        {
            bool isDeath = !result.IsWin && IsAllTeamDead(result);
            if ((!result.IsWin && !isDeath) || result.IsForceExit || !IsArenaStage(result.StageId)
                || session.fight?.PreFight.PreFightData.StageId != result.StageId)
                return null;
            if (ReconcileLive(session))
                return null;
            var state = session.player.SimulatedBattlefield!;
            if (!state.ArenaJoined || state.ArenaJoinActivity != state.ArenaActivityNo)
                return null;
            int selectedAreaId = session.fight.PreFight.PreFightData.SelectAreaId;
            List<int> activeAreas = SelectedAreas(state.ArenaActivityNo, session.player.PlayerData.Id, ResolveChallenge(state.ArenaLevel, (int)session.player.PlayerData.Level));
            if (!activeAreas.Contains(selectedAreaId)
                || !AreaStages.Value.TryGetValue(selectedAreaId, out AreaStageTable? areaStage)
                || ResolveConfiguredStageId(areaStage) != checked((int)result.StageId)
                || !Marks.Value.TryGetValue(areaStage.MarkId, out MarkTable? mark))
                return null;
            int areaId = selectedAreaId;

            int passTimeLimit = ArenaStageDataset.PassTimeLimit(areaId, result.StageId, areaStage.MarkId, areaStage.Desc) ?? 0;
            long activeFrames = Math.Max(0, result.SettleFrame - result.StartFrame - result.PauseFrame);
            int fightTime = checked((int)Math.Min(int.MaxValue, activeFrames / FightFramesPerSecond));
            int timeLeft = isDeath ? 0 : Math.Max(0, passTimeLimit - fightTime);

            Dictionary<string, double> metrics = Metrics(result);
            metrics["LeftTime"] = timeLeft;
            int enemyPoint = Evaluate(mark.EnemyHpPoint, metrics, mark.MaxEnemyHpPoint);
            int myHpPoint = Evaluate(mark.MyHpPoint, metrics, mark.MaxMyHpPoint);
            int timePoint = Evaluate(mark.TimePoint, metrics, mark.MaxTimePoint);
            int npcPoint = Evaluate(mark.NpcGroupPoint, metrics, mark.MaxNpcGroupPoint);
            int point = Math.Clamp(enemyPoint + myHpPoint + timePoint + npcPoint, mark.MinPoint, mark.MaxPoint);
            int oldStage = state.ArenaStageMaxPoints.GetValueOrDefault(result.StageId);
            int oldArea = state.ArenaAreaMaxPoints.GetValueOrDefault(areaId);
            if (point > oldStage)
            {
                state.ArenaStageMaxPoints[result.StageId] = point;
                state.ArenaAreaMaxPoints[areaId] = point;
                state.ArenaPoint = state.ArenaAreaMaxPoints.Values.Sum();
                foreach (int type in activeAreas.SelectMany(activeAreaId => AreaStages.Value[activeAreaId].DistributeTypes).Distinct())
                    state.ArenaDistributeMaxPoints[type] = activeAreas
                        .Where(activeAreaId => AreaStages.Value[activeAreaId].DistributeTypes.Contains(type))
                        .Sum(activeAreaId => state.ArenaAreaMaxPoints.GetValueOrDefault(activeAreaId));
                state.ArenaLastPointTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                session.player.Save();
            }
            int areaPoint = state.ArenaAreaMaxPoints.GetValueOrDefault(areaId);
            return new ArenaResult
            {
                FightTime = fightTime, TimeLeft = timeLeft,
                KillEnemy = checked((int)metrics["KillNum"]), TimePoint = timePoint,
                Point = point, OldPoint = oldStage,
                EnemyHurt = mark.EnemyHpPoint.Contains("EnemyHp", StringComparison.Ordinal) ? checked((int)Math.Min(int.MaxValue, metrics["EnemyHp"])) : checked((int)metrics["KillNum"]),
                EnemyPoint = enemyPoint, MyHpLeft = checked((int)metrics["Hp"]), MyHpPoint = myHpPoint,
                NpcGroup = checked((int)metrics["NpcGroup"]), NpcGroupPoint = npcPoint,
                OldArenaMaxPoint = oldArea, ArenaMaxPoint = areaPoint
            };
        }

        private static ActivityResultNotify SettleCycle(Session session, SimulatedBattlefieldState state)
        {
            ChallengeAreaTable old = Challenges.Value.SingleOrDefault(row => row.ChallengeId == state.ArenaChallengeId)
                ?? throw new InvalidDataException($"No Arena challenge {state.ArenaChallengeId} for settled activity.");
            bool hasScore = state.ArenaPoint > 0;
            int rank = hasScore ? 1 : old.JoinNum;
            int maxContributeScore = RequiredInt(ActivityConfig, "MaxContributeScore");
            int maxProtectedScore = RequiredInt(ActivityConfig, "MaxProtectedScore");
            int currentContributeScore = Math.Clamp(state.ArenaContributeScore, 0, maxContributeScore);
            int currentProtectedScore = Math.Clamp(state.ArenaProtectedScore, 0, maxProtectedScore);
            List<int> arenaLevels = TableReaderV2.Parse<ArenaLevelTable>().Select(x => x.Id).Distinct().OrderBy(x => x).ToList();
            int oldLevelIndex = arenaLevels.IndexOf(old.ArenaLv);
            int highestArenaLevel = arenaLevels[^1];
            int promotionCost = Math.Max(0, old.DanUpRankCostContributeScore);
            bool promote = hasScore
                && old.DanUpRank > 0
                && rank <= old.DanUpRank
                && currentContributeScore >= promotionCost;
            bool canDemote = oldLevelIndex > 0 && old.DownRewardId > 0;
            bool keep = hasScore && rank <= old.DanKeepRank || !canDemote;
            bool demotionPending = !promote && !keep && canDemote;
            if (hasScore)
                currentProtectedScore = Math.Min(maxProtectedScore, checked(currentProtectedScore + Math.Max(0, old.ProtectScore)));
            bool protectedDemotion = demotionPending
                && old.ArenaLv < highestArenaLevel
                && currentProtectedScore >= maxProtectedScore;
            if (protectedDemotion)
                currentProtectedScore -= maxProtectedScore;
            bool demote = demotionPending && !protectedDemotion;
            int newLevelIndex = Math.Clamp(oldLevelIndex + (promote ? 1 : demote ? -1 : 0), 0, arenaLevels.Count - 1);
            int newLevel = arenaLevels[newLevelIndex];
            int rewardId = promote ? old.UpRewardId : demote ? old.DownRewardId : old.KeepRewardId;
            int earnedMerit = hasScore && rank <= old.ContributeScore.Count ? Math.Max(0, old.ContributeScore[rank - 1]) : 0;
            state.ArenaContributeScore = Math.Min(
                maxContributeScore,
                checked(currentContributeScore - (promote ? promotionCost : 0) + earnedMerit));
            state.ArenaProtectedScore = currentProtectedScore;
            ChallengeAreaTable next = ResolveChallenge(newLevel, (int)session.player.PlayerData.Level);
            state.ArenaLevel = newLevel;
            state.ArenaChallengeId = next.ChallengeId;
            var goods = RewardHandler.GetRewardGoods(rewardId);
            long rewardTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            bool inventoryChanged = false;
            foreach (var good in goods)
            {
                RewardType? rewardType = RewardHandler.GetRewardType(good);
                if (rewardType == RewardType.Item)
                {
                    session.inventory.Do(good.TemplateId, good.Count);
                    inventoryChanged = true;
                }
                else if (rewardType == RewardType.Medal)
                {
                    int medalId = good.Params.FirstOrDefault();
                    if (medalId <= 0)
                        throw new InvalidDataException($"Arena reward {rewardId} contains malformed medal goods {good.Id}.");
                    if (!session.player.UnlockedMedals.Any(medal => medal.Id == medalId))
                    {
                        session.player.UnlockedMedals.Add(new MedalUnlockState
                        {
                            Id = medalId,
                            Time = rewardTime,
                            KeepTime = 0
                        });
                    }
                }
                else if (rewardType == RewardType.ChatBoard)
                {
                    if (!ChatBoards.Value.TryGetValue(good.TemplateId, out ChatBoardTable? chatBoard))
                        throw new InvalidDataException($"Arena reward {rewardId} references unknown chat board {good.TemplateId}.");
                    ChatBoardUnlockState? existing = session.player.UnlockedChatBoards.Find(unlock => unlock.Id == good.TemplateId);
                    if (existing is null)
                    {
                        session.player.UnlockedChatBoards.Add(new ChatBoardUnlockState
                        {
                            Id = good.TemplateId,
                            GetTime = rewardTime,
                            EndTime = chatBoard.Duration > 0 ? checked(rewardTime + chatBoard.Duration) : 0
                        });
                    }
                    else if (existing.EndTime != 0 && chatBoard.Duration > 0)
                    {
                        existing.EndTime = checked(Math.Max(rewardTime, existing.EndTime) + chatBoard.Duration);
                    }
                }
                else
                {
                    throw new InvalidDataException($"Arena reward {rewardId} contains unsupported goods {good.Id} of type {rewardType?.ToString() ?? "<unknown>"}.");
                }
            }
            if (inventoryChanged)
                session.inventory.Save();
            return new ActivityResultNotify
            {
                ChallengeId = old.ChallengeId, GroupRank = rank, Point = state.ArenaPoint,
                OldArenaLevel = old.ArenaLv, NewArenaLevel = newLevel, IsProtected = protectedDemotion,
                ContributeScore = state.ArenaContributeScore,
                RewardGoodsList = goods.Select(x => new ActivityResultNotify.ActivityResultNotifyRewardGoods
                {
                    RewardType = (int)(RewardHandler.GetRewardType(x) ?? RewardType.Item),
                    TemplateId = RewardHandler.GetRewardType(x) == RewardType.Medal
                        ? x.Params.FirstOrDefault()
                        : x.TemplateId,
                    Count = x.Count,
                    Id = checked((uint)x.Id),
                    IsGift = false,
                    RewardMulti = 0
                }).ToList()
            };
        }

        internal static bool ApplyPreFight(
            Session session,
            PreFightRequest.PreFightRequestPreFightData request,
            PreFightResponse response)
        {
            if (ReconcileLive(session))
                return false;
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield!;
            if (!state.ArenaJoined || state.ArenaJoinActivity != state.ArenaActivityNo)
                return false;

            if (!AreaStages.Value.TryGetValue(request.SelectAreaId, out AreaStageTable? area)
                || ResolveConfiguredStageId(area) != checked((int)request.StageId))
            {
                return false;
            }

            ChallengeAreaTable challenge = ResolveChallenge(state.ArenaLevel, (int)session.player.PlayerData.Level);
            List<int> selectedAreas = SelectedAreas(state.ArenaActivityNo, session.player.PlayerData.Id, challenge);
            int selectedAreaIndex = selectedAreas.IndexOf(request.SelectAreaId);
            if (selectedAreaIndex < 0)
                return false;

            List<int> groupIds = SelectedGroupFightEventIds(
                state.ArenaActivityNo,
                session.player.PlayerData.Id,
                selectedAreas);
            ArenaGroupFightEventTable? group = selectedAreaIndex < groupIds.Count
                ? FightEventGroups.Value.FirstOrDefault(candidate => candidate.Id == groupIds[selectedAreaIndex])
                : null;
            if (group is null
                || request.ArenaSelectIndex < 0
                || request.ArenaSelectIndex >= group.FightEvents.Count)
            {
                return false;
            }

            response.FightData.EventIds = area.BuffIds
                .Where(eventId => eventId > 0)
                .Cast<dynamic>()
                .Append(group.FightEvents[request.ArenaSelectIndex])
                .ToList();
            response.FightData.FightEventsWithLevel = new List<dynamic>();
            if (!ArenaStageDataset.TryHydrate(request.SelectAreaId, request.StageId, area.MarkId, area.Desc, response.FightData))
                return false;
            int distributionType = area.DistributeTypes.FirstOrDefault();
            int distributionMaximum = state.ArenaDistributeMaxPoints.GetValueOrDefault(distributionType);
            Dictionary<string, object?> stageParams = response.FightData.StageParams as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
            stageParams["DistributeMaxScore"] = distributionMaximum.ToString();
            stageParams["MarkId"] = area.MarkId.ToString();
            response.FightData.StageParams = stageParams;
            return true;
        }

        private static AreaDataResponse BuildAreaData(Session session)
        {
            ReconcileLive(session);
            var state = session.player.SimulatedBattlefield!;
            if (!state.ArenaJoined || state.ArenaJoinActivity != state.ArenaActivityNo)
                return new AreaDataResponse { Code = 20044029, TotalPoint = 0, AreaList = null, AreaDistributeMaxPointDict = null, GroupFightEvents = null };
            ChallengeAreaTable challenge = ResolveChallenge(state.ArenaLevel, (int)session.player.PlayerData.Level);
            List<int> selected = SelectedAreas(state.ArenaActivityNo, session.player.PlayerData.Id, challenge);
            List<dynamic> areas = selected.Select(areaId => (dynamic)new Dictionary<string, object?>
            {
                ["AreaId"] = areaId, ["Lock"] = 0, ["Point"] = state.ArenaAreaMaxPoints.GetValueOrDefault(areaId),
                ["LordList"] = new[] { BuildAreaLeader(session, state.ArenaAreaMaxPoints.GetValueOrDefault(areaId)) },
                ["StageInfos"] = ResolveConfiguredStageId(AreaStages.Value.GetValueOrDefault(areaId)) is { } configuredStageId
                    && state.ArenaStageMaxPoints.TryGetValue((uint)configuredStageId, out int stagePoint)
                        ? new List<Dictionary<string, object>> { new() { ["StageId"] = (uint)configuredStageId, ["Point"] = stagePoint } }
                        : null,
            }).ToList();
            Dictionary<int, int> maxima = AreaStages.Value.Values
                .Where(candidate => candidate.IsAbandoned == 0)
                .SelectMany(candidate => candidate.DistributeTypes)
                .Where(type => type > 0)
                .Distinct()
                .ToDictionary(type => type, type => state.ArenaDistributeMaxPoints.GetValueOrDefault(type));
            List<int> events = SelectedGroupFightEventIds(
                state.ArenaActivityNo,
                session.player.PlayerData.Id,
                selected);
            return new AreaDataResponse { Code = 0, TotalPoint = state.ArenaPoint, AreaList = areas, AreaDistributeMaxPointDict = maxima, GroupFightEvents = events };
        }
        private static List<int> SelectedGroupFightEventIds(int activityNo, long playerId, IReadOnlyList<int> selectedAreas)
        {
            if (FightEventGroups.Value.Count == 0)
                return [];

            return selectedAreas.Select(areaId =>
            {
                int index = (int)(StableAreaHash(activityNo, playerId, areaId) % (uint)FightEventGroups.Value.Count);
                return FightEventGroups.Value[index].Id;
            }).ToList();
        }


        private static int? ResolveConfiguredStageId(AreaStageTable? area)
        {
            if (area is null)
                return null;

            int arenaIndex = ActivityConfig.Value<int>("ArenaIndex");
            int stageIndex = arenaIndex - 1;
            if (stageIndex < 0 || stageIndex >= area.StageId.Count)
                throw new InvalidDataException($"{ConfigPath}: ArenaIndex {arenaIndex} is invalid for AreaStage {area.Id}.");

            int stageId = area.StageId[stageIndex];
            if (stageId <= 0)
                throw new InvalidDataException($"AreaStage {area.Id} has invalid StageId {stageId} at ArenaIndex {arenaIndex}.");
            return stageId;
        }

        private static NotifyArenaActivity BuildActivity(Player player, long? now)
        {
            var state = player.SimulatedBattlefield!;
            long team = CycleTeamTime(now);
            long end = team + RequiredLong(ActivityConfig, "FightDurationSeconds");
            ChallengeAreaTable challenge = ResolveChallenge(state.ArenaLevel, (int)player.PlayerData.Level);
            return new NotifyArenaActivity
            {
                ActivityNo = state.ArenaActivityNo, ChallengeId = state.ArenaChallengeId,
                Status = ActivityConfig.Value<int>("Status"), NextStatusTime = checked((uint)end),
                ArenaLevel = state.ArenaLevel, JoinActivity = state.ArenaJoined ? 1 : 0,
                UnlockCount = challenge.DayUnlockNum,
                TeamTime = checked((uint)team), FightTime = checked((uint)team),
                ResultTime = checked((uint)end), MaxPointStageList = state.ArenaStageMaxPoints.Keys.Where(IsArenaStage).ToList(),
                ContributeScore = state.ArenaContributeScore, ProtectedScore = state.ArenaProtectedScore,
                ArenaIndex = ActivityConfig.Value<int>("ArenaIndex")
            };
        }

        private static Dictionary<string, object> BuildAreaLeader(Session session, int point)
        {
            Dictionary<string, object> info = BuildPlayerInfo(session);
            info["Point"] = point;
            return info;
        }

        private static Dictionary<string, object> BuildPlayerInfo(Session session)
        {
            var p = session.player.PlayerData;
            var s = session.player.SimulatedBattlefield!;
            return new Dictionary<string, object>
            {
                ["Id"] = p.Id, ["Name"] = p.Name, ["CurrHeadPortraitId"] = p.CurrHeadPortraitId,
                ["CurrHeadFrameId"] = p.CurrHeadFrameId, ["Point"] = s.ArenaPoint,
                ["ContributeScore"] = s.ArenaContributeScore, ["LastPointTime"] = s.ArenaLastPointTime,
                ["CurrMedalId"] = p.CurrMedalId
            };
        }

        private static ArenaChallengeGetRankResponse BuildChallengeRankResponse(Session session, int challengeId)
        {
            ReconcileLive(session);
            SimulatedBattlefieldState state = session.player.SimulatedBattlefield!;
            bool knownChallenge = Challenges.Value.Any(challenge => challenge.ChallengeId == challengeId);
            long beginTime = CycleTeamTime(null);
            ArenaChallengeGetRankResponse response = new()
            {
                Code = knownChallenge ? 0 : 20044036,
                ChallengeId = challengeId,
                Rank = new ArenaChallengeRank
                {
                    Id = knownChallenge ? checked(state.ArenaActivityNo * 10_000 + challengeId) : 0,
                    ActivityId = state.ArenaActivityNo,
                    ChallengeId = challengeId
                },
                CacheGetRankResponseTime = 0,
                BeginTime = beginTime,
                EndTime = checked(beginTime + RequiredLong(Config.Value, "CycleSeconds"))
            };
            if (!knownChallenge || challengeId != state.ArenaChallengeId || state.ArenaPoint <= 0)
                return response;

            PlayerData player = session.player.PlayerData;
            response.Ranking = 1;
            response.MemberCount = 1;
            response.Rank.RankPlayer.Add(new ArenaChallengeRankPlayer
            {
                PlayerId = player.Id,
                Name = player.Name ?? string.Empty,
                Head = player.CurrHeadPortraitId,
                Frame = player.CurrHeadFrameId,
                Level = player.Level,
                Sign = player.Sign ?? string.Empty,
                Score = state.ArenaPoint
            });
            return response;
        }

        private static void EnsureCurrent(Player player, long? now = null)
        {
            player.SimulatedBattlefield ??= new();
            var s = player.SimulatedBattlefield;
            s.ArenaAreaMaxPoints ??= new();
            s.ArenaStageMaxPoints ??= new();
            s.ArenaDistributeMaxPoints ??= new();
            if (s.ArenaActivityNo != 0)
                return;
            ChallengeAreaTable initial = ResolveInitialChallenge((int)player.PlayerData.Level);
            s.ArenaActivityNo = CurrentActivity(now); s.ArenaLevel = initial.ArenaLv; s.ArenaChallengeId = initial.ChallengeId; s.ArenaProtectedScore = 0;
        }

        private static ChallengeAreaTable ResolveInitialChallenge(int playerLevel)
        {
            string initialName = ActivityConfig.Value<string>("InitialArenaLevelName")
                ?? throw new InvalidDataException($"{ConfigPath}: InitialArenaLevelName missing.");
            ArenaLevelTable level = TableReaderV2.Parse<ArenaLevelTable>().Single(x => x.Name == initialName);
            return ResolveChallenge(level.Id, playerLevel);
        }

        private static ChallengeAreaTable ResolveChallenge(int arenaLevel, int playerLevel) => Challenges.Value
            .Where(x => x.ArenaLv == arenaLevel && playerLevel >= x.MinLv && playerLevel <= x.MaxLv)
            .OrderBy(x => x.ChallengeId).FirstOrDefault()
            ?? throw new InvalidDataException($"No Arena challenge for tier {arenaLevel}, player level {playerLevel}.");

        private static List<int> SelectedAreas(int activityNo, long playerId, ChallengeAreaTable challenge)
        {
            HashSet<int> groupedAreaIds = challenge.AreaIdGroup
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .SelectMany(group => group.Split('|', StringSplitOptions.RemoveEmptyEntries))
                .Select(value => int.TryParse(value, out int areaId) ? areaId : 0)
                .Where(areaId => areaId > 0)
                .ToHashSet();
            List<(string Key, List<int> Areas)> groups = challenge.AreaIdGroup
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct()
                .Select(group => (Key: group, Areas: group.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, out int areaId) ? areaId : 0)
                    .Where(areaId => areaId > 0 && AreaStages.Value.TryGetValue(areaId, out AreaStageTable? area) && IsSupportedArea(area))
                    .Distinct()
                    .ToList()))
                .Where(group => group.Areas.Count > 0)
                .OrderBy(group => StableGroupHash(activityNo, playerId, group.Key))
                .ToList();
            List<int> selected = groups
                .Select(group => group.Areas.OrderBy(areaId => StableAreaHash(activityNo, playerId, areaId) ^ StableStringHash(group.Key)).First())
                .Distinct()
                .Take(challenge.DayUnlockNum)
                .ToList();
            if (selected.Count != challenge.DayUnlockNum)
                throw new InvalidDataException(
                    $"Arena challenge {challenge.ChallengeId} requires {challenge.DayUnlockNum} distinct supported area groups, found {selected.Count}.");


            if (IsHeroOrHigher(challenge.ArenaLv))
            {
                List<int> specialAreas = challenge.AreaId
                    .Where(areaId => areaId > 0 && !groupedAreaIds.Contains(areaId))
                    .Distinct()
                    .Where(areaId => AreaStages.Value.TryGetValue(areaId, out AreaStageTable? area)
                        && IsSupportedArea(area))
                    .ToList();
                if (specialAreas.Count != 1)
                    throw new InvalidDataException(
                        $"Arena challenge {challenge.ChallengeId} requires exactly one supported ungrouped Entropic Distortion area, found {specialAreas.Count}.");
                selected.Add(specialAreas[0]);
            }
            return selected;
        }

        private static bool IsHeroOrHigher(int arenaLevel) =>
            arenaLevel >= RequiredInt(ActivityConfig, "HeroArenaLevel");

        private static bool IsSupportedArea(AreaStageTable area) =>
            area.IsAbandoned == 0
            && ResolveConfiguredStageId(area) is { } stageId
            && ArenaStageDataset.Supports(area.Id, checked((uint)stageId), area.MarkId, area.Desc);

        private static uint StableAreaHash(int activityNo, long playerId, int areaId) =>
            StablePlayerHash(activityNo, playerId) * 16777619u ^ (uint)areaId;
        private static uint StablePlayerHash(int activityNo, long playerId) =>
            (((uint)activityNo * 16777619u ^ (uint)playerId) * 16777619u) ^ (uint)((ulong)playerId >> 32);
        private static uint StableGroupHash(int activityNo, long playerId, string group) =>
            StablePlayerHash(activityNo, playerId) * 16777619u ^ StableStringHash(group);
        private static uint StableStringHash(string value)
        {
            uint hash = 2166136261u;
            foreach (char character in value)
                hash = (hash ^ character) * 16777619u;
            return hash;
        }

        private static bool IsAllTeamDead(FightSettleResult result)
        {
            NpcHp[] team = result.NpcHpInfo?.Values.Where(npc => npc.Type == 1).ToArray() ?? [];
            return team.Length > 0 && team.All(IsZeroHp);
        }

        private static bool IsZeroHp(NpcHp npc)
        {
            if (npc.AttrTable is null || !npc.AttrTable.TryGetValue(1, out dynamic? hp))
                return false;
            return HasDynamicMember(hp, "Value") && ReadDynamicNumber(hp, "Value") <= 0;
        }

        private static Dictionary<string, double> Metrics(FightSettleResult r)
        {
            double total = Math.Max(1, r.NpcHpInfo?.Values.Count(npc => npc.Type == 3) ?? 0);
            List<double> hpRatios = new();
            if (r.NpcHpInfo is not null)
            {
                foreach (NpcHp npc in r.NpcHpInfo.Values.Where(npc => npc.Type == 1))
                {
                    if (npc.AttrTable is not IDictionary<int, dynamic> attributes || !attributes.TryGetValue(1, out dynamic? hpAttribute))
                        continue;
                    double maximum = ReadDynamicNumber(hpAttribute, "MaxValue");
                    if (maximum > 0)
                        hpRatios.Add(Math.Max(0, ReadDynamicNumber(hpAttribute, "Value") * 100d / maximum));
                }
            }
            double hp = hpRatios.Count == 0 ? 0 : hpRatios.Average();
            double npcGroup = ReadStringMetric(r.StringToIntRecord, "NpcGroup");
            return new() { ["KillNum"] = r.DeathTotalEnemy, ["TotalNum"] = total, ["Hp"] = hp,
                ["LeftTime"] = Math.Max(0, r.LeftTime), ["EnemyHp"] = Math.Max(0, r.TotalDamage), ["NpcGroup"] = Math.Max(0, npcGroup) };
        }

        private static bool HasDynamicMember(object? value, string key) =>
            value is IDictionary<object, object> primitive
                ? primitive.Keys.Any(member => string.Equals(Convert.ToString(member), key, StringComparison.Ordinal))
                : value is IDictionary<string, object> objects
                    ? objects.ContainsKey(key)
                    : value is JObject json && json.ContainsKey(key);

        private static double ReadDynamicNumber(object? value, string key)
        {
            if (value is IDictionary<object, object> primitive
                && primitive.FirstOrDefault(entry => string.Equals(Convert.ToString(entry.Key), key, StringComparison.Ordinal)) is var entry
                && entry.Key is not null)
                return Convert.ToDouble(entry.Value);
            if (value is IDictionary<string, object> objects && objects.TryGetValue(key, out object? member))
                return Convert.ToDouble(member);
            if (value is JObject json && json.TryGetValue(key, out JToken? token))
                return token.Value<double>();
            return 0;
        }

        private static double ReadStringMetric(dynamic? record, string key)
        {
            if (record is IDictionary<string, int> ints && ints.TryGetValue(key, out int intValue))
                return intValue;
            if (record is IDictionary<string, long> longs && longs.TryGetValue(key, out long longValue))
                return longValue;
            if (record is IDictionary<string, object> objects && objects.TryGetValue(key, out object? value))
                return Convert.ToDouble(value);
            if (record is IDictionary<object, object> primitive)
            {
                KeyValuePair<object, object> entry = primitive.FirstOrDefault(candidate =>
                    string.Equals(Convert.ToString(candidate.Key), key, StringComparison.Ordinal));
                if (entry.Key is not null)
                    return Convert.ToDouble(entry.Value);
            }
            if (record is JObject json && json.TryGetValue(key, out JToken? token))
                return token.Value<double>();
            return 0;
        }

        private static int Evaluate(string? expression, Dictionary<string, double> values, int maximum)
        {
            if (string.IsNullOrWhiteSpace(expression)) return 0;
            string normalized = expression.Replace("+", " + ").Replace("-", " - ").Replace("*", " * ").Replace("/", " / ");
            string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double value = Term(tokens[0]);
            for (int i = 1; i + 1 < tokens.Length; i += 2)
            {
                double rhs = Term(tokens[i + 1]);
                value = tokens[i] switch { "+" => value + rhs, "-" => value - rhs, "*" => value * rhs, "/" => rhs == 0 ? 0 : value / rhs, _ => value };
            }
            int evaluated = value >= int.MaxValue ? int.MaxValue : (int)Math.Floor(value);
            return maximum > 0 ? Math.Clamp(evaluated, 0, maximum) : Math.Max(0, evaluated);
            double Term(string token) => values.TryGetValue(token, out double found) ? found : double.TryParse(token, out double number) ? number : 0;
        }

        private static void ResetPoints(SimulatedBattlefieldState s)
        {
            s.ArenaPoint = 0; s.ArenaLastPointTime = 0; s.ArenaAreaMaxPoints.Clear(); s.ArenaStageMaxPoints.Clear(); s.ArenaDistributeMaxPoints.Clear();
        }
        private static JObject ActivityConfig => Config.Value["ArenaActivity"] as JObject ?? throw new InvalidDataException($"{ConfigPath}: ArenaActivity missing.");
        private static int CurrentActivity(long? now) => ActivityConfig.Value<int>("BaseActivityNo")
            + checked((int)CycleIndex(now)) * ActivityConfig.Value<int>("ActivityStep");
        private static long CycleTeamTime(long? now) =>
            RequiredLong(ActivityConfig, "BaseTeamTime") + CycleIndex(now) * RequiredLong(Config.Value, "CycleSeconds");
        private static long CycleIndex(long? now)
        {
            long cycle = RequiredLong(Config.Value, "CycleSeconds");
            long fightDuration = RequiredLong(ActivityConfig, "FightDurationSeconds");
            long announcementGap = cycle - fightDuration;
            long elapsed = (now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - RequiredLong(ActivityConfig, "BaseTeamTime") + announcementGap;
            return Math.Max(0, elapsed / cycle);
        }
        private static int RequiredInt(JObject o, string key) { int value = o.Value<int>(key); return value > 0 ? value : throw new InvalidDataException($"{ConfigPath}: {key} must be positive."); }
        private static long RequiredLong(JObject o, string key) { long value = o.Value<long>(key); return value > 0 ? value : throw new InvalidDataException($"{ConfigPath}: {key} must be positive."); }
    }
}
