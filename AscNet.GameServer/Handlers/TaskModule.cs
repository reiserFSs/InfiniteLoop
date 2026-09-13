using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.passport;
using AscNet.Table.V2.share.equip;
using MessagePack;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using LoginTaskSchedule = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask.NotifyTaskDataTaskDataTaskSchedule;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;
using SyncTaskSchedule = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask.NotifyTaskTasksTaskSchedule;
using LifeTreeTask = AscNet.Table.V2.share.task.TaskTable;
using LifeTreeTaskCondition = AscNet.Table.V2.share.task.ConditionTable;
using AscNet.Table.V2.share.guild.boss;
using AscNet.Table.V2.share.fuben.mainline;
using AscNet.Table.V2.share.fuben.extrachapter;
using AscNet.Table.V2.share.fuben.shortstory;
using AscNet.Table.V2.share.fuben;
using AscNet.Table.V2.share.equip.equipguide;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class GetCourseRewardRequest
    {
        public int StageId;
    }
    
    [MessagePackObject(true)]
    public class GetCourseRewardResponse
    {
        public int Code;
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetNewPlayerRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetNewbieRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
        public List<int> NewbieRecvProgress { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetNewbieHonorRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetActivenessRewardRequest
    {
        public int StageIndex { get; set; }
        public int RewardId { get; set; }
        public int RewardType { get; set; }
    }

    [MessagePackObject(true)]
    public class GetActivenessRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class GetWeeklyActivenessRewardResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
        public List<int> WeeklyTaskActivenessProgress { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class FinishTaskRequest
    {
        public int TaskId { get; set; }
    }

    [MessagePackObject(true)]
    public class FinishTaskResponse : GuildResponse
    {
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class FinishMultiTaskRequest
    {
        public List<int> TaskIds { get; set; } = new();
    }

    [MessagePackObject(true)]
    public class FinishMultiTaskResponse
    {
        public int Code { get; set; }
        public List<RewardGoods> RewardGoodsList { get; set; } = new();
        public List<int> SuccessTaskIds { get; set; } = new();
        public List<int> NotDealTaskIds { get; set; } = new();
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class TaskModule
    {
        private const string CurrentTaskTimeFormat = "yyyy/M/d H:mm";
        private const int DormNormalTaskType = 12;
        private const int DormDailyTaskType = 13;
        private static readonly HashSet<int> SnapshotConditionTypes =
            [10101, 10102, 10202, 11201, 12201, 12208, 12209, 12211, 13101, 13102, 13104, 13105, 13106, 13107, 13213, 13214, 15101, 15201, 15207, 15220, 15225, 15226, 15227, 19002, 35002, 76100, 76101, 76102, 76103, 89001];
        private static readonly Lazy<IReadOnlyDictionary<int, CurrentConditionTable>> CurrentConditionsById = new(() =>
            TableReaderV2.Parse<CurrentConditionTable>().ToDictionary(condition => condition.Id));
        private static readonly Lazy<IReadOnlyList<CurrentTaskTable>> CurrentTasksByPriority = new(() =>
            TableReaderV2.Parse<CurrentTaskTable>().OrderByDescending(task => task.Priority).ToArray());
        private static readonly Lazy<IReadOnlyList<CurrentTaskTable>> SnapshotTasksByPriority = new(() =>
            CurrentTasksByPriority.Value.Where(task => CurrentConditionsById.Value.TryGetValue(task.Condition, out CurrentConditionTable? condition)
                && SnapshotConditionTypes.Contains(condition.Type)
                && (condition.Type != 15201 || condition.Params.Count > 1 && condition.Params[0] == 1)).ToArray());
        private static readonly Lazy<IReadOnlySet<int>> SnapshotTaskIds = new(() =>
            SnapshotTasksByPriority.Value.Select(task => task.Id).ToHashSet());
        private static readonly Lazy<IReadOnlySet<int>> CurrentTaskIds = new(() =>
            CurrentTasksByPriority.Value.Select(task => task.Id).ToHashSet());
        private static readonly Lazy<IReadOnlyDictionary<uint, EquipTable>> EquipRowsById = new(() =>
            TableReaderV2.Parse<EquipTable>().ToDictionary(equip => (uint)equip.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, EquipTargetTable>> EquipGuideTargetsById = new(() =>
            TableReaderV2.Parse<EquipTargetTable>().ToDictionary(target => target.Id));
        private static readonly Lazy<IReadOnlyDictionary<int, List<List<int>>>> StoryChapterStages = new(BuildStoryChapterStages);
        private static readonly Lazy<IReadOnlyDictionary<int, int>> StageTypesById = new(() =>
        {
            Dictionary<int, int> types = new();
            foreach (StageTable stage in TableReaderV2.Parse<StageTable>())
                if (stage.Type is int type and > 0)
                    types.Add(stage.StageId, type);
            return types;
        });
        private static readonly Lazy<IReadOnlyDictionary<int, int>> GuildBossStageTypes = new(() =>
            TableReaderV2.Parse<GuildBossStageCatalogTable>().ToDictionary(stage => stage.StageId, stage => stage.StageType));
        // WeeklyTwo activeness counts finished tasks tagged Weekly in Task.tab (client XTaskManager.GetWeeklyTaskActiveness).
        private static readonly Lazy<IReadOnlySet<int>> WeeklyTaggedTaskIds = new(() =>
            TableReaderV2.Parse<TaskTable>().Where(task => task.Tag == 1).Select(task => task.Id).ToHashSet());

        [RequestPacketHandler("DoClientTaskEventRequest")]
        public static void DoClientTaskEventRequestHandler(Session session, Packet.Request packet)
        {
            DoClientTaskEventRequest request = packet.Deserialize<DoClientTaskEventRequest>();
            EnsureMissionResets(session);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<CurrentTaskTable> tasks = CurrentTasksByPriority.Value.Where(task =>
                request.ClientTaskType > 0 && task.Result == 1 && IsCurrentTaskVisibleAtLogin(task, now)
                && (task.PreTaskId == 0 || session.player.MissionProgress.ClaimedTaskIds.Contains(task.PreTaskId))
                && CurrentConditionsById.Value.TryGetValue(task.Condition, out CurrentConditionTable? condition)
                && condition.Type == 45000 && condition.Params.Count == 2
                && condition.Params[0] == 1 && condition.Params[1] == request.ClientTaskType).ToList();
            if (tasks.Count == 0)
            {
                session.SendResponse(new DoClientTaskEventResponse { Code = 20026007 }, packet.Id);
                return;
            }
            Dictionary<int, int> counters = session.player.MissionProgress.ConditionCounters;
            Dictionary<int, int?> previous = tasks
                .Where(task => !session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id))
                .Select(task => task.Condition).Distinct()
                .Where(conditionId => counters.GetValueOrDefault(conditionId) != 1)
                .ToDictionary(conditionId => conditionId, conditionId =>
                    counters.TryGetValue(conditionId, out int value) ? (int?)value : null);
            if (previous.Count > 0)
            {
                foreach (int conditionId in previous.Keys)
                    counters[conditionId] = 1;
                try
                {
                    session.player.SaveChecked();
                }
                catch (Exception exception)
                {
                    foreach ((int conditionId, int? value) in previous)
                    {
                        if (value.HasValue)
                            counters[conditionId] = value.Value;
                        else
                            counters.Remove(conditionId);
                    }
                    session.log.Error($"Failed to persist client task event: {exception}");
                    session.SendResponse(new DoClientTaskEventResponse { Code = 2 }, packet.Id);
                    return;
                }
            }
            session.SendResponse(new DoClientTaskEventResponse(), packet.Id);
            SendCurrentTaskBatch(session, tasks.Select(task => task.Id).ToArray());
        }

        [RequestPacketHandler("FinishTaskRequest")]
        public static void FinishTaskRequestHandler(Session session, Packet.Request packet)
        {
            if (session.player.Theatre5.PendingMutation is { } pending
                && pending.ResponseName is not (nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                && !Theatre5Module.CanDispatchPendingRequest(session, packet))
            {
                session.SendResponse(new FinishTaskResponse { Code = 1 }, packet.Id);
                return;
            }
            FinishTaskRequest request = packet.Deserialize<FinishTaskRequest>();
            bool theatre5 = Theatre5Module.IsMetaTask(request.TaskId);
            bool theatre = TheatreModule.IsMetaTask(request.TaskId);
            bool theatre3 = Theatre3Module.IsMetaTask(request.TaskId);
            bool bianca = !theatre && !theatre3 && BiancaTheatreModule.MetaTasks().Any(task => task.Id == request.TaskId);
            PrepareTaskRequest(session, bianca, theatre, theatre5);
            if (theatre5)
            {
                Theatre5Module.Handle<FinishTaskRequest, FinishTaskResponse>(session, packet,
                    (mutation, body, result) => result.RewardGoodsList = Theatre5Module.ClaimMetaTask(mutation, body.TaskId),
                    static (result, code) => result.Code = code);
                return;
            }
            if (theatre)
            {
                TheatreModule.Handle<FinishTaskRequest, FinishTaskResponse>(session, packet,
                    (mutation, body, result) => result.RewardGoodsList = TheatreModule.Claim(mutation, body.TaskId),
                    static (result, code) => result.Code = code, afterCommit: SendTaskSync);
                return;
            }
            if (theatre3)
            {
                Theatre3Module.Handle<FinishTaskRequest, FinishTaskResponse>(session, packet,
                    (mutation, body, result) => result.RewardGoodsList = Theatre3Module.Claim(mutation, body.TaskId),
                    static (result, code) => result.Code = code, afterCommit: SendTaskSync);
                return;
            }
            if (GuildWarModule.TryGetTaskProgress(session, request.TaskId, out _, out _))
            {
                GuildModule.Handle<FinishTaskRequest, FinishTaskResponse>(session, packet, (mutation, body, result) =>
                {
                    result.RewardGoodsList = GuildWarModule.ClaimTask(mutation, body.TaskId);
                    mutation.Push(session.player.PlayerData.Id, new NotifyTask { Tasks = new() { Tasks = GuildWarModule.BuildTaskData(mutation) } });
                });
                return;
            }
            if (bianca)
            {
                BiancaTheatreModule.Handle<FinishTaskRequest, FinishTaskResponse>(session, packet, (mutation, body, result) =>
                {
                    FinishTaskResponse claim = BiancaTheatreModule.ClaimMetaTask(mutation, body.TaskId);
                    if (claim.Code != 0)
                        throw new AscNet.Common.ServerCodeException("Bianca task claim rejected.", claim.Code);
                    result.RewardGoodsList = claim.RewardGoodsList;
                }, static (result, code) => result.Code = code, committed => SendBiancaTaskClaimUpdates(committed, [request.TaskId]));
                return;
            }
            FinishTaskResponse response = ClaimTaskReward(session, request.TaskId, pushSync: false, out RewardApplicationResult? transfiniteApplication, out RewardApplicationResult? passportApplication);
            if (IsTransfiniteTask(session, request.TaskId))
            {
                SendTransfiniteTaskSync(session, TransfiniteTasks(session).Where(task => task.Id == request.TaskId));
                transfiniteApplication?.SendPushes(session);
            }
            else if (CurrentTaskIds.Value.Contains(request.TaskId))
            {
                SendCurrentTaskBatch(session, [request.TaskId]);
                SendPassportConditionTypeSync(session, 11203);
            }
            else if (IsDormTask(request.TaskId))
            {
                SendDormTaskBatch(session, [request.TaskId]);
            }
            else
            {
                SendTaskSync(session);
            }
            passportApplication?.SendPushes(session);
            if (response.Code == 0)
                WheelchairManualGuideManager.SendUpdate(session);
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("FinishMultiTaskRequest")]
        public static void FinishMultiTaskRequestHandler(Session session, Packet.Request packet)
        {
            if (session.player.Theatre5.PendingMutation is { } pending
                && pending.ResponseName is not (nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                && !Theatre5Module.CanDispatchPendingRequest(session, packet))
            {
                session.SendResponse(new FinishMultiTaskResponse { Code = 1 }, packet.Id);
                return;
            }
            FinishMultiTaskRequest request = packet.Deserialize<FinishMultiTaskRequest>();
            bool theatre5 = request.TaskIds.Any(Theatre5Module.IsMetaTask);
            bool theatre = request.TaskIds.Any(TheatreModule.IsMetaTask);
            bool theatre3 = request.TaskIds.Any(Theatre3Module.IsMetaTask);
            bool bianca = !theatre && !theatre3 && request.TaskIds.Any(taskId => BiancaTheatreModule.MetaTasks().Any(task => task.Id == taskId));
            PrepareTaskRequest(session, bianca, theatre, theatre5);
            if (theatre5)
            {
                Theatre5Module.Handle<FinishMultiTaskRequest, FinishMultiTaskResponse>(session, packet, (mutation, body, result) =>
                {
                    foreach (int taskId in body.TaskIds.Distinct())
                    {
                        if (!Theatre5Module.CanClaimMetaTask(mutation, taskId))
                        {
                            result.NotDealTaskIds.Add(taskId);
                            continue;
                        }
                        result.RewardGoodsList.AddRange(Theatre5Module.ClaimMetaTask(mutation, taskId));
                        result.SuccessTaskIds.Add(taskId);
                    }
                }, static (result, code) => result.Code = code);
                return;
            }
            if (theatre)
            {
                TheatreModule.Handle<FinishMultiTaskRequest, FinishMultiTaskResponse>(session, packet, (mutation, body, result) =>
                {
                    foreach (int taskId in body.TaskIds.Distinct())
                    {
                        if (!TheatreModule.IsMetaTask(taskId) || !TheatreModule.CanClaim(mutation, taskId))
                        {
                            result.NotDealTaskIds.Add(taskId);
                            continue;
                        }
                        result.RewardGoodsList.AddRange(TheatreModule.Claim(mutation, taskId));
                        result.SuccessTaskIds.Add(taskId);
                    }
                }, static (result, code) => result.Code = code, afterCommit: SendTaskSync);
                return;
            }
            if (theatre3)
            {
                HandleTheatre3MultiTaskRequest(session, packet, request.TaskIds);
                return;
            }
            if (bianca)
            {
                HandleBiancaMultiTaskRequest(session, packet, request.TaskIds);
                return;
            }
            FinishMultiTaskResponse response = new()
            {
                Code = 0
            };

            List<RewardApplicationResult> transfiniteApplications = [];
            List<RewardApplicationResult> passportApplications = [];
            foreach (int taskId in request.TaskIds.Distinct())
            {
                FinishTaskResponse taskResponse = ClaimTaskReward(session, taskId, pushSync: false, out RewardApplicationResult? transfiniteApplication, out RewardApplicationResult? passportApplication);
                if (transfiniteApplication is not null)
                {
                    transfiniteApplications.Add(transfiniteApplication);
                }
                if (passportApplication is not null)
                {
                    passportApplications.Add(passportApplication);
                }
                if (taskResponse.Code == 0)
                {
                    response.RewardGoodsList.AddRange(taskResponse.RewardGoodsList);
                    response.SuccessTaskIds.Add(taskId);
                }
                else
                {
                    response.NotDealTaskIds.Add(taskId);
                }
            }

            int[] requestedTaskIds = request.TaskIds.Distinct().ToArray();
            IReadOnlySet<int> currentTaskIds = CurrentTaskIds.Value;
            int[] requestedTransfiniteTaskIds = requestedTaskIds.Where(taskId => IsTransfiniteTask(session, taskId)).ToArray();
            int[] requestedCurrentTaskIds = requestedTaskIds.Where(taskId => currentTaskIds.Contains(taskId) && !requestedTransfiniteTaskIds.Contains(taskId)).ToArray();
            int[] requestedDormTaskIds = requestedTaskIds.Where(IsDormTask).ToArray();
            if (requestedCurrentTaskIds.Length > 0)
            {
                SendCurrentTaskBatch(session, requestedCurrentTaskIds);
                SendPassportConditionTypeSync(session, 11203);
            }
            if (requestedDormTaskIds.Length > 0)
            {
                SendDormTaskBatch(session, requestedDormTaskIds);
            }
            if (requestedTransfiniteTaskIds.Length > 0)
            {
                SendTransfiniteTaskSync(session, TransfiniteTasks(session).Where(task => requestedTransfiniteTaskIds.Contains(task.Id)));
                foreach (RewardApplicationResult application in transfiniteApplications)
                {
                    application.SendPushes(session);
                }
            }
            if (requestedTaskIds.Any(taskId => !currentTaskIds.Contains(taskId) && !IsDormTask(taskId) && !IsTransfiniteTask(session, taskId)))
            {
                SendTaskSync(session);
            }
            foreach (RewardApplicationResult application in passportApplications)
            {
                application.SendPushes(session);
            }
            if (response.SuccessTaskIds.Count > 0)
                WheelchairManualGuideManager.SendUpdate(session);
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetActivenessRewardRequest")]
        public static void GetActivenessRewardRequestHandler(Session session, Packet.Request packet)
        {
            GetActivenessRewardRequest request = packet.Deserialize<GetActivenessRewardRequest>();
            GetActivenessRewardResponse response = ClaimActivenessRewards(session, request.RewardType);
            if (response.Code == 0)
            {
                session.SendPush(BuildActivenessStatus(session));
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetWeeklyActivenessRewardRequest")]
        public static void GetWeeklyActivenessRewardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(ClaimWeeklyTwoRewards(session), packet.Id);
        }

        internal static List<int> BuildWeeklyTwoProgress(Session session)
        {
            EnsureMissionResets(session);
            return WeeklyTwoClaimedMilestones(session);
        }

        private static GetWeeklyActivenessRewardResponse ClaimWeeklyTwoRewards(Session session)
        {
            EnsureMissionResets(session);
            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == 5);
            if (rewards is null)
            {
                return new GetWeeklyActivenessRewardResponse { Code = 20026010 };
            }

            long week = session.player.MissionProgress.WeeklyResetWeek;
            int activeness = WeeklyTwoActiveness(session);
            List<int> rewardIndexes = rewards.Activeness
                .Select((milestone, index) => (milestone, index))
                .Where(entry => entry.milestone <= activeness && !WeeklyTwoClaimed(session, week, entry.milestone))
                .Select(entry => entry.index)
                .ToList();
            if (rewardIndexes.Count == 0)
            {
                bool hasReachedMilestone = rewards.Activeness.Any(milestone => milestone <= activeness);
                return new GetWeeklyActivenessRewardResponse { Code = hasReachedMilestone ? 20026012 : 20026010 };
            }
            if (rewardIndexes.Any(index => index >= rewards.RewardId.Count))
            {
                return new GetWeeklyActivenessRewardResponse { Code = 20026010 };
            }

            List<RewardGrant> grants = new();
            foreach (int index in rewardIndexes)
            {
                List<RewardGoodsTable> goods = RewardHandler.GetRewardGoods(rewards.RewardId[index]);
                if (goods.Count == 0)
                {
                    return new GetWeeklyActivenessRewardResponse { Code = 20026010 };
                }
                grants.Add(new RewardGrant(WeeklyTwoClaimKey(week, rewards.Activeness[index]), goods));
            }

            RewardApplicationResult application;
            try
            {
                application = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to persist weekly activeness rewards: {exception}");
                return new GetWeeklyActivenessRewardResponse { Code = 20026003 };
            }
            application.SendPushes(session);
            return new GetWeeklyActivenessRewardResponse
            {
                Code = 0,
                RewardGoodsList = application.RewardGoods,
                WeeklyTaskActivenessProgress = WeeklyTwoClaimedMilestones(session)
            };
        }

        private static int WeeklyTwoActiveness(Session session)
        {
            IReadOnlySet<int> tagged = WeeklyTaggedTaskIds.Value;
            return session.player.MissionProgress.ClaimedTaskIds
                .Concat(session.stage.FinishedTasks)
                .Distinct()
                .Count(tagged.Contains);
        }

        private static string WeeklyTwoClaimKey(long week, int milestone) => $"weekly-two:{week}:{milestone}";

        private static bool WeeklyTwoClaimed(Session session, long week, int milestone) =>
            session.inventory.AppliedRewardClaims.Contains(WeeklyTwoClaimKey(week, milestone), StringComparer.Ordinal)
            || session.character.AppliedRewardClaims.Contains(WeeklyTwoClaimKey(week, milestone), StringComparer.Ordinal);

        private static List<int> WeeklyTwoClaimedMilestones(Session session)
        {
            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == 5);
            if (rewards is null)
            {
                return [];
            }
            long week = session.player.MissionProgress.WeeklyResetWeek;
            return rewards.Activeness.Where(milestone => WeeklyTwoClaimed(session, week, milestone)).ToList();
        }

        [RequestPacketHandler("GetNewbieRewardRequest")]
        public static void GetNewbieRewardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(ClaimNewbieRewards(session), packet.Id);
        }

        [RequestPacketHandler("GetNewbieHonorRewardRequest")]
        public static void GetNewbieHonorRewardRequestHandler(Session session, Packet.Request packet)
        {
            session.SendResponse(ClaimNewbieHonorReward(session), packet.Id);
        }

        [RequestPacketHandler("GetNewPlayerRewardRequest")]
        public static void GetNewPlayerRewardRequestHandler(Session session, Packet.Request packet)
        {
            Dictionary<string, int>? request = packet.Deserialize<Dictionary<string, int>?>();
            int requestedValue = request?.Values.FirstOrDefault(value => value > 0) ?? 0;
            GetNewPlayerRewardResponse response = ClaimNewPlayerReward(session, requestedValue);
            session.SendResponse(response, packet.Id);
        }

        public static NotifyActivenessStatus BuildActivenessStatus(Session session)
        {
            return new NotifyActivenessStatus
            {
                DailyActivenessRewardStatus = (int)session.player.PlayerData.DailyActivenessRewardStatus,
                WeeklyActivenessRewardStatus = (int)session.player.PlayerData.WeeklyActivenessRewardStatus
            };
        }

        private static GetActivenessRewardResponse ClaimActivenessRewards(Session session, int rewardType)
        {
            if (rewardType is not 1 and not 2)
            {
                return new GetActivenessRewardResponse { Code = 20026011 };
            }

            EnsureMissionResets(session);
            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == rewardType);
            if (rewards is null)
            {
                return new GetActivenessRewardResponse { Code = 20026010 };
            }

            int itemId = rewardType == 1 ? Inventory.DailyActiveness : Inventory.WeeklyActiveness;
            long activeness = session.inventory.Items.FirstOrDefault(item => item.Id == itemId)?.Count ?? 0;
            long claimedStatus = rewardType == 1
                ? session.player.PlayerData.DailyActivenessRewardStatus
                : session.player.PlayerData.WeeklyActivenessRewardStatus;
            List<int> rewardIndexes = rewards.Activeness
                .Select((milestone, index) => (milestone, index))
                .Where(entry => entry.milestone <= activeness && (claimedStatus & (1L << entry.index)) == 0)
                .Select(entry => entry.index)
                .ToList();
            if (rewardIndexes.Count == 0)
            {
                bool hasReachedMilestone = rewards.Activeness.Any(milestone => milestone <= activeness);
                return new GetActivenessRewardResponse { Code = hasReachedMilestone ? 20026012 : 20026010 };
            }
            if (rewardIndexes.Any(index => index >= rewards.RewardId.Count))
            {
                return new GetActivenessRewardResponse { Code = 20026010 };
            }

            List<List<RewardGoodsTable>> configuredRewards = rewardIndexes
                .Select(index => GetCurrentRewardGoods(rewards.RewardId[index]))
                .ToList();
            if (configuredRewards.Any(rewardGoods => rewardGoods.Count == 0))
            {
                return new GetActivenessRewardResponse { Code = 20026010 };
            }

            List<RewardApplicationResult> applications = new();
            foreach ((int rewardIndex, List<RewardGoodsTable> rewardGoods) in rewardIndexes.Zip(configuredRewards))
            {
                claimedStatus |= 1L << rewardIndex;
                applications.Add(RewardHandler.ApplyRewards(rewardGoods, session));
            }
            if (rewardType == 1)
            {
                session.player.PlayerData.DailyActivenessRewardStatus = claimedStatus;
            }
            else
            {
                session.player.PlayerData.WeeklyActivenessRewardStatus = claimedStatus;
            }
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            foreach (RewardApplicationResult application in applications)
                application.SendPushes(session);
            return new GetActivenessRewardResponse
            {
                Code = 0,
                RewardGoodsList = applications.SelectMany(application => application.RewardGoods).ToList()
            };
        }

        private static GetNewPlayerRewardResponse ClaimNewPlayerReward(Session session, int requestedValue)
        {
            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == 3);
            if (rewards is null)
            {
                return new GetNewPlayerRewardResponse { Code = 20026003 };
            }

            session.player.MissionProgress ??= new MissionProgressState();
            long activeness = session.inventory.Items.FirstOrDefault(item => item.Id == NewPlayerActivenessItemId)?.Count ?? 0;
            List<int> rewardIndexes;
            if (requestedValue <= 0)
            {
                int rewardIndex = rewards.Activeness
                    .Select((milestone, index) => (milestone, index))
                    .Where(entry =>
                        entry.milestone <= activeness
                        && !session.player.MissionProgress.NewPlayerRewardRecords.Contains(entry.milestone))
                    .Select(entry => entry.index)
                    .LastOrDefault(-1);
                rewardIndexes = rewardIndex < 0 ? [] : [rewardIndex];
                if (rewardIndexes.Count == 0)
                {
                    bool hasReachedMilestone = rewards.Activeness.Any(milestone => milestone <= activeness);
                    return new GetNewPlayerRewardResponse { Code = hasReachedMilestone ? 20026006 : 20026007 };
                }
            }
            else
            {
                int rewardIndex = rewards.Activeness.IndexOf(requestedValue);
                if (rewardIndex < 0)
                {
                    rewardIndex = rewards.RewardId.IndexOf(requestedValue);
                }
                if (rewardIndex < 0 && requestedValue <= rewards.Activeness.Count)
                {
                    rewardIndex = requestedValue - 1;
                }
                if (rewardIndex < 0 || rewardIndex >= rewards.RewardId.Count)
                {
                    return new GetNewPlayerRewardResponse { Code = 20026003 };
                }

                int milestone = rewards.Activeness[rewardIndex];
                if (session.player.MissionProgress.NewPlayerRewardRecords.Contains(milestone))
                {
                    return new GetNewPlayerRewardResponse { Code = 20026006 };
                }
                if (activeness < milestone)
                {
                    return new GetNewPlayerRewardResponse { Code = 20026007 };
                }
                rewardIndexes = [rewardIndex];
            }

            List<List<RewardGoodsTable>> configuredRewards = rewardIndexes
                .Select(index => GetCurrentRewardGoods(rewards.RewardId[index]))
                .ToList();
            if (configuredRewards.Any(rewardGoods => rewardGoods.Count == 0))
            {
                return new GetNewPlayerRewardResponse { Code = 20026003 };
            }

            List<RewardApplicationResult> applications = new();
            for (int index = 0; index < rewardIndexes.Count; index++)
            {
                int rewardIndex = rewardIndexes[index];
                session.player.MissionProgress.NewPlayerRewardRecords.Add(rewards.Activeness[rewardIndex]);
                applications.Add(RewardHandler.ApplyRewards(configuredRewards[index], session));
            }
            session.player.MissionProgress.NewPlayerRewardRecords.Sort();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            foreach (RewardApplicationResult application in applications)
                application.SendPushes(session);
            return new GetNewPlayerRewardResponse
            {
                Code = 0,
                RewardGoodsList = applications.SelectMany(application => application.RewardGoods).ToList()
            };
        }

        private static GetNewbieRewardResponse ClaimNewbieRewards(Session session)
        {
            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == 4);
            if (rewards is null)
            {
                return new GetNewbieRewardResponse { Code = 20026025 };
            }

            session.player.MissionProgress ??= new MissionProgressState();
            session.player.MissionProgress.NewbieRewardRecords ??= new();
            HashSet<int> noviceTaskIds = TableReaderV2.Parse<CurrentTaskTable>()
                .Where(task => task.Type == 71)
                .Select(task => task.Id)
                .ToHashSet();
            int completedTaskCount = session.player.MissionProgress.ClaimedTaskIds
                .Concat(session.stage.FinishedTasks)
                .Where(noviceTaskIds.Contains)
                .Distinct()
                .Count();
            List<int> rewardIndexes = rewards.Activeness
                .Select((milestone, index) => (milestone, index))
                .Where(entry =>
                    entry.milestone <= completedTaskCount
                    && !session.player.MissionProgress.NewbieRewardRecords.Contains(entry.milestone))
                .Select(entry => entry.index)
                .ToList();
            if (rewardIndexes.Count == 0)
            {
                bool hasReachedMilestone = rewards.Activeness.Any(milestone => milestone <= completedTaskCount);
                return new GetNewbieRewardResponse { Code = hasReachedMilestone ? 20026024 : 20026027 };
            }
            if (rewardIndexes.Any(index => index >= rewards.RewardId.Count))
            {
                return new GetNewbieRewardResponse { Code = 20026025 };
            }

            List<List<RewardGoodsTable>> configuredRewards = rewardIndexes
                .Select(index => GetCurrentRewardGoods(rewards.RewardId[index]))
                .ToList();
            if (configuredRewards.Any(rewardGoods => rewardGoods.Count == 0))
            {
                return new GetNewbieRewardResponse { Code = 20026026 };
            }

            List<int> claimedMilestones = rewardIndexes.Select(index => rewards.Activeness[index]).ToList();
            List<RewardApplicationResult> applications = new();
            for (int index = 0; index < rewardIndexes.Count; index++)
            {
                session.player.MissionProgress.NewbieRewardRecords.Add(claimedMilestones[index]);
                applications.Add(RewardHandler.ApplyRewards(configuredRewards[index], session));
            }
            session.player.MissionProgress.NewbieRewardRecords.Sort();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            foreach (RewardApplicationResult application in applications)
                application.SendPushes(session);
            return new GetNewbieRewardResponse
            {
                Code = 0,
                RewardGoodsList = applications.SelectMany(application => application.RewardGoods).ToList(),
                NewbieRecvProgress = claimedMilestones
            };
        }

        private static GetNewbieHonorRewardResponse ClaimNewbieHonorReward(Session session)
        {
            session.player.MissionProgress ??= new MissionProgressState();
            if (session.player.MissionProgress.NewbieHonorReward)
            {
                return new GetNewbieHonorRewardResponse { Code = 20026028 };
            }

            CurrentTaskActivenessTable? rewards = TableReaderV2.Parse<CurrentTaskActivenessTable>().FirstOrDefault(x => x.Type == 4);
            if (rewards is null || rewards.HonorRewardId <= 0)
            {
                return new GetNewbieHonorRewardResponse { Code = 20026026 };
            }

            HashSet<int> finishedTaskIds = session.player.MissionProgress.ClaimedTaskIds
                .Concat(session.stage.FinishedTasks)
                .ToHashSet();
            int[] noviceTaskIds = TableReaderV2.Parse<CurrentTaskTable>()
                .Where(task => task.Type == 71)
                .Select(task => task.Id)
                .ToArray();
            bool allTasksFinished = noviceTaskIds.Length > 0 && noviceTaskIds.All(finishedTaskIds.Contains);
            bool allProgressRewardsClaimed = rewards.Activeness.All(session.player.MissionProgress.NewbieRewardRecords.Contains);
            if (!allTasksFinished || !allProgressRewardsClaimed)
            {
                return new GetNewbieHonorRewardResponse { Code = 20026029 };
            }

            List<RewardGoodsTable> configuredRewards = GetCurrentRewardGoods(rewards.HonorRewardId);
            if (configuredRewards.Count == 0)
            {
                return new GetNewbieHonorRewardResponse { Code = 20026026 };
            }

            session.player.MissionProgress.NewbieHonorReward = true;
            RewardApplicationResult application = RewardHandler.ApplyRewards(configuredRewards, session);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            application.SendPushes(session);
            return new GetNewbieHonorRewardResponse
            {
                Code = 0,
                RewardGoodsList = application.RewardGoods
            };
        }

        [RequestPacketHandler("GetCourseRewardRequest")]
        public static void GetCourseRewardRequestHandler(Session session, Packet.Request packet)
        {
            var request = packet.Deserialize<GetCourseRewardRequest>();
            GetCourseRewardResponse response = ClaimCourseReward(session, request.StageId);
            session.SendResponse(response, packet.Id);
            if (response.Code is 0 or 20026014)
                GuideModule.CompleteOpenedGuideOnCourseClaim(session, checked((uint)request.StageId));
        }

        private static GetCourseRewardResponse ClaimCourseReward(Session session, int stageId)
        {
            if (!session.stage.Stages.TryGetValue(stageId, out StageDatum? stageData) || !stageData.Passed)
            {
                return new GetCourseRewardResponse { Code = 20026013 };
            }

            CourseTable? courseTable = TableReaderV2.Parse<CourseTable>().FirstOrDefault(x => x.StageId == stageId);
            if (courseTable is null || courseTable.RewardId <= 0)
            {
                return new GetCourseRewardResponse { Code = 20026013 };
            }

            List<RewardGoodsTable> rewardGoods = RewardHandler.GetRewardGoods(courseTable.RewardId);
            if (rewardGoods.Count == 0)
            {
                return new GetCourseRewardResponse { Code = 20026013 };
            }

            if (!session.stage.AddCourse((uint)stageId))
            {
                return new GetCourseRewardResponse { Code = 20026014 };
            }

            RewardApplicationResult application = RewardHandler.ApplyRewards(rewardGoods, session);
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();
            if (application.DormFurnitureChanged || application.GatherRewardIds.Count > 0 || application.HeadPortraitData.Heads.Count > 0)
                session.player.Save();
            application.SendPushes(session);

            return new GetCourseRewardResponse
            {
                Code = 0,
                RewardGoodsList = application.RewardGoods
            };
        }

        public static List<LoginTask> BuildStoryTaskData(Session session)
        {
            return BuildStoryTaskProgress(session).Select(ToLoginTask).ToList();
        }

        public static void SendStoryTaskSync(Session session)
        {
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = BuildStoryTaskProgress(session).Select(ToSyncTask).ToList()
                }
            });
        }

        public static List<LoginTask> BuildTaskData(Session session)
        {
            EnsureMissionResets(session);
            List<LoginTask> tasks = BuildStoryTaskProgress(session)
                .Select(ToLoginTask)
                .ToList();
            HashSet<uint> existingIds = tasks.Select(x => x.Id).ToHashSet();
            tasks.AddRange(BuildCurrentTaskProgress(session, loginOnly: true)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(BuildLifeTreeTaskProgress(session)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(BuildDormTaskProgress(session)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(BuildTransfiniteTaskProgress(session)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(BuildPassportTaskProgress(session)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(BuildGuildAchievementProgress(session).Where(task => existingIds.Add((uint)task.TaskId)).Select(ToLoginTask));
            tasks.AddRange(GuildWarModule.BuildTaskData(session)
                .Where(task => existingIds.Add(task.Id))
                .Select(task => new LoginTask
                {
                    Id = task.Id, State = task.State, RecordTime = task.RecordTime,
                    Schedule = task.Schedule.Select(value => new LoginTaskSchedule { Id = value.Id, Value = value.Value }).ToList()
                }));
            tasks.AddRange(BuildBiancaTaskProgress(session.player.BiancaTheatre, session.player.MissionProgress.ClaimedTaskIds.Contains)
                .Where(x => existingIds.Add((uint)x.TaskId))
                .Select(ToLoginTask));
            tasks.AddRange(Theatre3Module.BuildTasks(session).Where(task => existingIds.Add(task.Id)));
            tasks.AddRange(TheatreModule.BuildTasks(session).Where(task => existingIds.Add(task.Id)));
            tasks.AddRange(Theatre5Module.BuildTasks(session).Where(task => existingIds.Add(task.Id)));
            session.TaskSnapshotProgress = tasks.Where(task => SnapshotTaskIds.Value.Contains((int)task.Id))
                .ToDictionary(task => (int)task.Id, task => (task.Schedule[0].Value, task.State));
            return tasks;
        }

        public static void SendTaskSync(Session session)
        {
            EnsureMissionResets(session);
            NotifyTask notification = new()
            {
                Tasks = new()
                {
                    Tasks = BuildStoryTaskProgress(session)
                        .Select(ToSyncTask)
                        .Concat(BuildDormTaskProgress(session).Select(ToSyncTask))
                        .Concat(BuildLifeTreeTaskProgress(session).Select(ToSyncTask))
                        .Concat(BuildCurrentTaskProgress(session, loginOnly: true).Select(ToSyncTask))
                        .Concat(BuildPassportTaskProgress(session).Select(ToSyncTask))
                        .Concat(BuildTransfiniteTaskProgress(session).Select(ToSyncTask))
                        .Concat(BuildGuildAchievementProgress(session).Select(ToSyncTask))
                        .Concat(GuildWarModule.BuildTaskData(session))
                        .Concat(BuildBiancaTaskProgress(session.player.BiancaTheatre, session.player.MissionProgress.ClaimedTaskIds.Contains).Select(ToSyncTask))
                        .Concat(Theatre3Module.BuildTasks(session).Select(task => new SyncTask
                        {
                            Id = task.Id, State = task.State, RecordTime = task.RecordTime,
                            Schedule = task.Schedule.Select(value => new SyncTaskSchedule { Id = value.Id, Value = value.Value }).ToList()
                        }))
                        .Concat(TheatreModule.BuildTasks(session).Select(task => new SyncTask
                        {
                            Id = task.Id, State = task.State, RecordTime = task.RecordTime,
                            Schedule = task.Schedule.Select(value => new SyncTaskSchedule { Id = value.Id, Value = value.Value }).ToList()
                        }))
                        .Concat(Theatre5Module.BuildTaskUpdates(new Theatre5Module.Mutation(session, newOperation: false)))
                        .GroupBy(x => x.Id)
                        .Select(x => x.First())
                        .ToList()
                }
            };
            session.SendPush(notification);
            session.TaskSnapshotProgress ??= new();
            foreach (SyncTask task in notification.Tasks.Tasks.Where(task => SnapshotTaskIds.Value.Contains((int)task.Id)))
                session.TaskSnapshotProgress[(int)task.Id] = (task.Schedule[0].Value, task.State);
        }

        internal static void SendSnapshotTaskSync(Session session)
        {
            if (session.player is null || session.character is null || session.inventory is null || session.stage is null
                || session.TaskSnapshotProgress is null)
                return;
            // A rejected task intent must not consume its owner's pending claim in the post-request snapshot pass.
            if (session.player.BiancaTheatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse)
                || session.player.Theatre3.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse)
                || session.player.Theatre5.PendingMutation is not null
                || session.player.Theatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse) or nameof(BuyResponse))
                return;
            EnsureMissionResets(session);
            List<SyncTask>? changed = null;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (CurrentTaskTable task in SnapshotTasksByPriority.Value)
            {
                if (!IsCurrentTaskVisibleAtLogin(task, now))
                    continue;
                (int conditionId, int count, int state) = EvaluateCurrentTask(session, task, now);
                (int Value, int State) value = (count, state);
                if (session.TaskSnapshotProgress.TryGetValue(task.Id, out var previous) && previous == value)
                    continue;
                session.TaskSnapshotProgress[task.Id] = value;
                (changed ??= []).Add(ToSyncTask(new MissionTaskProgress(task.Id, conditionId, count, state)));
            }
            if (changed is not null)
                session.SendPush(new NotifyTask { Tasks = new() { Tasks = changed } });
        }

        private static void RememberSnapshotTaskProgress(Session session, IEnumerable<MissionTaskProgress> progress)
        {
            if (session.TaskSnapshotProgress is null)
                return;
            foreach (MissionTaskProgress task in progress)
                if (SnapshotTaskIds.Value.Contains(task.TaskId))
                    session.TaskSnapshotProgress[task.TaskId] = (task.Value, task.State);
        }

        public static void SendCurrentTaskBatch(Session session, IReadOnlyCollection<int> taskIds)
        {
            EnsureMissionResets(session);
            HashSet<int> selectedIds = taskIds.ToHashSet();
            List<MissionTaskProgress> progress = BuildCurrentTaskProgress(session, loginOnly: false)
                .Where(x => selectedIds.Contains(x.TaskId))
                .ToList();
            HashSet<int> catalogIds = progress.Select(x => x.TaskId).ToHashSet();
            progress.AddRange(taskIds
                .Where(taskId => !catalogIds.Contains(taskId))
                .Select(taskId => new MissionTaskProgress(taskId, taskId, 0, TaskStateActive)));
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = progress.Select(ToSyncTask).ToList()
                }
            });
            RememberSnapshotTaskProgress(session, progress);
        }
        private static List<MissionTaskProgress> BuildBiancaTaskProgress(BiancaTheatreState state, Func<int, bool> claimed) =>
            BiancaTheatreModule.MetaTasks().Select(task =>
            {
                int target = task.Result ?? 1;
                int value = Math.Min(state.TaskProgress.GetValueOrDefault(task.Condition), target);
                return new MissionTaskProgress(task.Id, task.Condition, value,
                    claimed(task.Id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive);
            }).ToList();

        internal static NotifyTask BuildBiancaTaskSync(BiancaTheatreState state, Func<int, bool> claimed) =>
            new() { Tasks = new() { Tasks = BuildBiancaTaskProgress(state, claimed).Select(ToSyncTask).ToList() } };

        private static bool IsDormTask(int taskId) =>
            TableReaderV2.Parse<TaskTable>().Any(task => task.Id == taskId && IsDormTask(task));
        private static bool IsDormTask(TaskTable task) =>
            task.Type is DormNormalTaskType or DormDailyTaskType || task.Suffix == "Dormitory";

        private static void SendDormTaskBatch(Session session, IReadOnlyCollection<int> taskIds)
        {
            HashSet<int> selectedIds = taskIds.ToHashSet();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = BuildDormTaskProgress(session)
                        .Where(task => selectedIds.Contains(task.TaskId))
                        .Select(ToSyncTask)
                        .ToList()
                }
            });
        }
        internal static NotifyTask? RecordTransfiniteConfirmedProgress(Session session, int stageGroupId, int stageId, int spendTime, int? timeLimit, int winStreak)
        {
            List<TaskTable> tasks = TransfiniteTasks(session);
            HashSet<int> changedConditions = [];
            foreach (CurrentConditionTable condition in CurrentConditionsById.Value.Values
                .Where(condition => condition.Type == 103002 && condition.Params.Count == 1))
            {
                int current = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id);
                int value = Math.Max(current, Math.Min(winStreak, condition.Params[0]));
                if (value == current)
                    continue;
                session.player.MissionProgress.ConditionCounters[condition.Id] = value;
                changedConditions.Add(condition.Id);
            }
            foreach (TaskTable task in tasks.Where(task => task.Type == 79))
            {
                int target = task.Result ?? 1;
                int current = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition);
                int value = target < 14 && timeLimit is > 0 && spendTime < timeLimit
                    ? Math.Min(target, checked(current + 1))
                    : target == 14 ? Math.Max(current, Math.Min(winStreak, target)) : current;
                if (value != current)
                {
                    session.player.MissionProgress.ConditionCounters[task.Condition] = value;
                    changedConditions.Add(task.Condition);
                }
            }
            int? stageGroupType = TableReaderV2.Parse<TransfiniteStageGroupTable>()
                .SingleOrDefault(group => group.StageGroupId == stageGroupId)?.Type;
            HashSet<int> achievementTaskGroupIds = TableReaderV2.Parse<TransfiniteAchievementTable>()
                .Where(achievement => achievement.Type == stageGroupType && achievement.StageGroupId.Contains(stageGroupId))
                .Select(achievement => achievement.Id)
                .ToHashSet();
            HashSet<int> achievementTaskIds = TableReaderV2.Parse<TransfiniteTaskGroupTable>()
                .Where(group => achievementTaskGroupIds.Contains(group.Id))
                .SelectMany(group => group.TaskIds)
                .ToHashSet();
            HashSet<int> taskConditions = tasks
                .Where(task => achievementTaskIds.Contains(task.Id))
                .Select(task => task.Condition)
                .ToHashSet();
            foreach (ConditionTable condition in TableReaderV2.Parse<ConditionTable>()
                         .Where(condition => taskConditions.Contains(condition.Id)
                             && condition.Type == 103000
                             && condition.Params.Skip(1).Contains(stageId)))
            {
                session.player.MissionProgress.ConditionCounters[condition.Id] =
                    checked(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id) + 1);
                changedConditions.Add(condition.Id);
            }
            return changedConditions.Count == 0 ? null : new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = BuildTransfiniteTaskProgress(session)
                        .Concat(BuildCurrentTaskProgress(session, loginOnly: true, conditionTypes: new HashSet<int> { 103002 }))
                        .Where(task => changedConditions.Contains(task.ConditionId))
                        .Select(ToSyncTask)
                        .ToList()
                }
            };
        }
        private static List<MissionTaskProgress> BuildPassportTaskProgress(Session session) =>
            TableReaderV2.Parse<TaskTable>()
                .Where(task => task.Type == 51 && PassportModule.IsActivePassportTask(session, task.Id))
                .Select(task =>
                {
                    int value = PassportTaskValue(session, task);
                    int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                        ? TaskStateFinish
                        : value >= (task.Result ?? 1) ? TaskStateAchieved : TaskStateActive;
                    return new MissionTaskProgress(task.Id, task.Condition, value, state);
                }).ToList();

        private static int PassportTaskValue(Session session, TaskTable task, Func<int, long>? balance = null)
        {
            ConditionTable? condition = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(row => row.Id == task.Condition);
            return condition?.Type switch
            {
                10101 => 1,
                11203 => checked((int)Math.Min(int.MaxValue, balance?.Invoke(Inventory.DailyActiveness)
                    ?? session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.DailyActiveness)?.Count ?? 0)),
                _ => session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition)
            };
        }


        private static List<MissionTaskProgress> BuildTransfiniteTaskProgress(Session session) =>
            TransfiniteTasks(session).Select(task =>
            {
                int target = task.Result ?? 1;
                int value = Math.Min(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition), target);
                int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                    ? TaskStateFinish
                    : value >= target ? TaskStateAchieved : TaskStateActive;
                return new MissionTaskProgress(task.Id, task.Condition, value, state);
            }).ToList();

        private static List<TaskTable> TransfiniteTasks(Session session)
        {
            TransfiniteState? state = session.player.Transfinite;
            if (state is null
                || state.ActivityAuthorizedUntil < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || !TableReaderV2.Parse<TransfiniteActivityTable>().Any(activity => activity.Id == state.ActivityId))
                return [];

            TransfiniteRegionTable? region = TableReaderV2.Parse<TransfiniteRegionTable>()
                .SingleOrDefault(region => region.RegionId == state.RegionId);
            if (region is null)
                return [];

            HashSet<int> stageGroups = [state.StageGroupId];
            stageGroups.UnionWith(TableReaderV2.Parse<TransfiniteIslandTable>()
                .Where(island => island.Id == region.IslandId)
                .SelectMany(island => island.StageGroupId));
            Dictionary<int, int> stageGroupTypes = TableReaderV2.Parse<TransfiniteStageGroupTable>()
                .Where(group => stageGroups.Contains(group.StageGroupId))
                .ToDictionary(group => group.StageGroupId, group => group.Type);
            HashSet<int> taskGroups = [region.TaskGroupId];
            taskGroups.UnionWith(TableReaderV2.Parse<TransfiniteAchievementTable>()
                .Where(achievement => achievement.StageGroupId.Any(groupId =>
                    stageGroupTypes.GetValueOrDefault(groupId) == achievement.Type))
                .Select(achievement => achievement.Id));
            List<TransfiniteTaskGroupTable> groups = TableReaderV2.Parse<TransfiniteTaskGroupTable>()
                .Where(group => taskGroups.Contains(group.Id))
                .ToList();
            HashSet<int> taskIds = groups.SelectMany(group => group.TaskIds).ToHashSet();
            TransfiniteTaskGroupSpecialTreatmentTable? special = TableReaderV2.Parse<TransfiniteTaskGroupSpecialTreatmentTable>()
                .FirstOrDefault(row => row.TaskGroup == region.TaskGroupId
                    && ActivityScheduleService.TryGet(row.TimeId, out ActivityScheduleEntry schedule)
                    && schedule.IsOpen(DateTimeOffset.UtcNow));
            if (special is not null)
            {
                taskIds.ExceptWith(groups.Single(group => group.Id == region.TaskGroupId).TaskIds);
                taskIds.UnionWith(special.TaskIds);
            }
            return TableReaderV2.Parse<TaskTable>()
                .Where(task => taskIds.Contains(task.Id) && task.Type is 79 or 80)
                .ToList();
        }
        private static bool IsTransfiniteTask(Session session, int taskId) =>
            TransfiniteTasks(session).Any(task => task.Id == taskId);


        private static void SendTransfiniteTaskSync(Session session, IEnumerable<TaskTable>? tasks = null)
        {
            HashSet<int>? taskIds = tasks?.Select(task => task.Id).ToHashSet();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = BuildTransfiniteTaskProgress(session)
                        .Where(task => taskIds is null || taskIds.Contains(task.TaskId))
                        .Select(ToSyncTask)
                        .ToList()
                }
            });
        }





        internal static NotifyTask? ApplyLifeTreeUnlockProgress(Player player, int characterId, int status)
        {
            Dictionary<int, LifeTreeTaskCondition> conditions = TableReaderV2.Parse<LifeTreeTaskCondition>()
                .Where(condition => condition.Type == 137001
                    && condition.Params.Count >= 2
                    && condition.Params[0] == characterId
                    && condition.Params[1] == status)
                .ToDictionary(condition => condition.Id);
            List<LifeTreeTask> tasks = TableReaderV2.Parse<LifeTreeTask>()
                .Where(task => conditions.ContainsKey(task.Condition))
                .ToList();
            if (tasks.Count == 0)
                return null;

            uint now = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (LifeTreeTask task in tasks)
                player.MissionProgress.ConditionCounters[task.Condition] = Math.Max(1, task.Result ?? 1);
            return new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = tasks.Select(task => new SyncTask
                    {
                        Id = (uint)task.Id,
                        Schedule = [new SyncTaskSchedule { Id = (uint)task.Condition, Value = task.Result ?? 1 }],
                        State = TaskStateAchieved,
                        RecordTime = now,
                        ActivityId = 0,
                        ActivateTime = 0
                    }).ToList()
                }
            };
        }

        private static List<MissionTaskProgress> BuildLifeTreeTaskProgress(Session session)
        {
            HashSet<int> conditionIds = TableReaderV2.Parse<LifeTreeTaskCondition>()
                .Where(condition => condition.Type == 137001)
                .Select(condition => condition.Id)
                .ToHashSet();
            return TableReaderV2.Parse<LifeTreeTask>()
                .Where(task => conditionIds.Contains(task.Condition))
                .Select(task =>
                {
                    int result = Math.Max(1, task.Result ?? 1);
                    int value = Math.Min(
                        session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition),
                        result);
                    int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                        ? TaskStateFinish
                        : value >= result ? TaskStateAchieved : TaskStateActive;
                    return new MissionTaskProgress(task.Id, task.Condition, value, state);
                })
                .ToList();
        }
        private static List<MissionTaskProgress> BuildDormTaskProgress(Session session)
        {
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type is >= 29000 and < 29100)
                .ToDictionary(condition => condition.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return TableReaderV2.Parse<TaskTable>()
                .Where(task => IsDormTask(task)
                    && conditions.ContainsKey(task.Condition)
                    && IsTaskActive(task, now)
                    && (session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                        || task.ShowAfterTaskId is not > 0
                        || session.player.MissionProgress.ClaimedTaskIds.Contains(task.ShowAfterTaskId.Value)))
                .Select(task =>
                {
                    int result = task.Result ?? 1;
                    int value = Math.Min(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition), result);
                    bool claimed = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id);
                    bool prerequisiteSatisfied = task.ShowAfterTaskId is not > 0
                        || session.player.MissionProgress.ClaimedTaskIds.Contains(task.ShowAfterTaskId.Value);
                    return new MissionTaskProgress(task.Id, task.Condition, value,
                        claimed ? TaskStateFinish : prerequisiteSatisfied && value >= result ? TaskStateAchieved : TaskStateActive);
                })
                .Where(progress => progress.State != TaskStateActive || progress.Value > 0)
                .ToList();
        }


        public static void ResetArenaTasks(Session session)
        {
            session.player.MissionProgress ??= new MissionProgressState();
            ResetMissionType(session, 10);
        }

        public static void RecordStageClear(Session session, int stageId, int count = 1, int actionPointCost = 0) =>
            RecordStageClear(session, stageId, count, actionPointCost, true);

        public static void RecordStageClear(Session session, int stageId, int count, int actionPointCost, bool isFirstClear)
        {
            EnsureMissionResets(session);
            foreach (CurrentConditionTable condition in TableReaderV2.Parse<CurrentConditionTable>())
            {
                bool matches = MatchesStageClearCondition(condition.Type, condition.Params, stageId);
                if (matches)
                {
                    AddConditionProgress(session, condition.Id, count);
                }
            }
            HashSet<int> passportTaskIds = BuildPassportTaskProgress(session)
                .Select(task => task.TaskId)
                .ToHashSet();
            HashSet<int> passportConditionIds = TableReaderV2.Parse<TaskTable>()
                .Where(task => passportTaskIds.Contains(task.Id))
                .Select(task => task.Condition)
                .ToHashSet();
            foreach (ConditionTable condition in TableReaderV2.Parse<ConditionTable>()
                .Where(condition => passportConditionIds.Contains(condition.Id)))
            {
                bool matches = MatchesStageClearCondition(condition.Type, condition.Params, stageId);
                if (matches)
                    AddConditionProgress(session, condition.Id, count);
            }
            if (isFirstClear)
                RecordFirstClearProgress(session, stageId);
            if (actionPointCost > 0)
                AddConditionTypeProgress(session, 11202, actionPointCost, Inventory.ActionPoint);
            session.player.Save();
            SendTaskSync(session);
        }

        // Call only for a committed clear, with the server's accepted deployment (never settlement-uploaded DPS data).
        internal static void RecordStageParticipation(Session session, int stageId, IReadOnlyCollection<int> characterIds, int count = 1)
        {
            if (count <= 0 || characterIds.Count == 0)
                return;
            bool Matches(int? type, IReadOnlyList<int> parameters) => type switch
            {
                15210 => parameters.Count == 3 && characterIds.Contains(parameters[1])
                    && StageTypesById.Value.TryGetValue(stageId, out int stageType) && stageType == parameters[2],
                // The available PPC row has unrestricted middle filters; never silently discard a nonzero filter.
                25007 => parameters.Count == 5 && parameters[1] == 0 && parameters[2] == 0 && parameters[3] == 0
                    && BossModule.IsStage((uint)stageId) && characterIds.Contains(parameters[4]),
                _ => false
            };
            RecordConditionAmounts(session,
                TableReaderV2.Parse<ConditionTable>().Where(condition => Matches(condition.Type, condition.Params))
                    .ToDictionary(condition => condition.Id, _ => count),
                CurrentConditionsById.Value.Values.Where(condition => Matches(condition.Type, condition.Params))
                    .ToDictionary(condition => condition.Id, _ => count));
        }

        private static bool MatchesStageClearCondition(int? type, IReadOnlyList<int> parameters, int stageId) => type switch
        {
            15101 or 15220 or 15225 => parameters.Contains(stageId),
            15201 when parameters.Count > 1 => parameters.Skip(1).Contains(stageId),
            15201 or 15217 or 15227 => true,
            15202 => parameters.Count <= 1 || StageTypesById.Value.TryGetValue(stageId, out int stageType)
                && parameters.Skip(1).Contains(stageType),
            _ => false
        };

        private static void RecordFirstClearProgress(Session session, int stageId)
        {
            bool isGuildBossStage = GuildBossStageTypes.Value.TryGetValue(stageId, out int guildBossStageType);
            HashSet<int> conditionIds = [];
            foreach (CurrentConditionTable condition in TableReaderV2.Parse<CurrentConditionTable>()
                .Where(condition => condition.Type is 15216 or 25005))
            {
                bool matches = condition.Type == 25005
                    ? BossModule.IsStage((uint)stageId)
                    : isGuildBossStage && condition.Params.Count > 0 && guildBossStageType == condition.Params[0];
                if (matches) conditionIds.Add(condition.Id);
            }
            HashSet<int> passportTaskIds = BuildPassportTaskProgress(session)
                .Select(task => task.TaskId)
                .ToHashSet();
            HashSet<int> passportConditionIds = TableReaderV2.Parse<TaskTable>()
                .Where(task => passportTaskIds.Contains(task.Id))
                .Select(task => task.Condition)
                .ToHashSet();
            foreach (ConditionTable condition in TableReaderV2.Parse<ConditionTable>()
                .Where(condition => passportConditionIds.Contains(condition.Id)
                    && condition.Type is 15216 or 25005))
            {
                bool matches = condition.Type == 25005
                    ? BossModule.IsStage((uint)stageId)
                    : isGuildBossStage && condition.Params.Count > 0 && guildBossStageType == condition.Params[0];
                if (matches) conditionIds.Add(condition.Id);
            }
            foreach (int conditionId in conditionIds)
                AddConditionProgress(session, conditionId, 1);
        }
        public static void RecordArenaResult(Session session, int point) =>
            RecordArenaResult(session, point, false);

        public static void RecordArenaResult(Session session, int point, bool isFirstClear)
        {
            EnsureMissionResets(session);
            HashSet<int> currentArenaTaskIds = ArenaModule.CurrentTaskIds(session.player).ToHashSet();
            List<CurrentTaskTable> currentTasks = TableReaderV2.Parse<CurrentTaskTable>();
            HashSet<int> currentArenaConditionIds = currentTasks
                .Where(task => currentArenaTaskIds.Contains(task.Id))
                .Select(task => task.Condition)
                .ToHashSet();
            HashSet<int> countTypes = [28001, 28005];
            List<CurrentConditionTable> conditions = TableReaderV2.Parse<CurrentConditionTable>()
                .Where(condition =>
                    condition.Type is 28005 or 28006
                    || (condition.Type is 28001 or 28003 && currentArenaConditionIds.Contains(condition.Id)))
                .ToList();
            if (isFirstClear)
            {
                HashSet<int> passportTaskIds = BuildPassportTaskProgress(session).Select(task => task.TaskId).ToHashSet();
                foreach (int conditionId in TableReaderV2.Parse<TaskTable>()
                    .Where(task => passportTaskIds.Contains(task.Id))
                    .Join(TableReaderV2.Parse<ConditionTable>(),
                        task => task.Condition, condition => condition.Id,
                        (_, condition) => condition)
                    .Where(condition => condition.Type == 28005)
                    .Select(condition => condition.Id))
                    AddConditionProgress(session, conditionId, 1);
            }
            foreach (CurrentConditionTable condition in conditions)
            {
                if (countTypes.Contains(condition.Type))
                {
                    AddConditionProgress(session, condition.Id, 1);
                }
                else
                {
                    int current = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id);
                    session.player.MissionProgress.ConditionCounters[condition.Id] = Math.Max(current, point);
                }
            }
            if (conditions.Count == 0 && !isFirstClear)
                return;

            HashSet<int> affectedConditionIds = conditions.Select(condition => condition.Id).ToHashSet();
            int[] affectedTaskIds = currentTasks
                .Where(task => affectedConditionIds.Contains(task.Condition))
                .Select(task => task.Id)
                .ToArray();
            session.player.Save();
            SendCurrentTaskBatch(session, affectedTaskIds);
            if (isFirstClear)
                SendPassportConditionTypeSync(session, 28005);
        }


        public static void RecordConditionType(Session session, int conditionType, int amount = 1)
        {
            EnsureMissionResets(session);
            if (!AddConditionTypeProgress(session, conditionType, amount))
            {
                return;
            }

            session.player.Save();
            SendConditionTypeSync(session, conditionType);
        }

        internal static void RecordTableDrivenProgress(Session session, int taskTimeLimitId, int conditionType, int parameter)
        {
            EnsureMissionResets(session);
            HashSet<int> allowedTaskIds = TableReaderV2.Parse<TaskTimeLimitTable>()
                .FirstOrDefault(limit => limit.Id == taskTimeLimitId)?.TaskId.ToHashSet() ?? new();
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type == conditionType && condition.Params.Contains(parameter))
                .ToDictionary(condition => condition.Id);
            if (conditions.Count == 0)
            {
                return;
            }

            List<TaskTable> tasks = TableReaderV2.Parse<TaskTable>()
                .Where(task => allowedTaskIds.Contains(task.Id) && conditions.ContainsKey(task.Condition))
                .ToList();
            if (tasks.Count == 0)
            {
                return;
            }
            foreach (ConditionTable condition in tasks.Select(task => conditions[task.Condition]).DistinctBy(condition => condition.Id))
            {
                int target = tasks.Where(task => task.Condition == condition.Id)
                    .Select(task => (int)(task.Result ?? 0))
                    .DefaultIfEmpty(1)
                    .Max();
                session.player.MissionProgress.ConditionCounters[condition.Id] = Math.Max(
                    session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id),
                    target);
            }

            session.player.Save();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = tasks.Select(task =>
                    {
                        int result = task.Result ?? 0;
                        int value = Math.Min(
                            session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition),
                            result);
                        int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                            ? TaskStateFinish
                            : value >= result ? TaskStateAchieved : TaskStateActive;
                        return ToSyncTask(new MissionTaskProgress(task.Id, task.Condition, value, state));
                    }).ToList()
                }
            });
        }
        internal static void RecordTableDrivenProgress(GuildMutation mutation, int conditionType, int amount, int? parameter = null)
        {
            if (amount <= 0) return;
            Session session = mutation.ActorSession;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            HashSet<int> active = TableReaderV2.Parse<TaskTable>().Where(task => IsTaskActive(task, now)
                    && (task.Type != 51 || PassportModule.IsActivePassportTask(session, task.Id)))
                .Select(task => task.Condition)
                .Concat(CurrentTasksByPriority.Value.Where(task => IsCurrentTaskVisibleAtLogin(task, now)).Select(task => task.Condition))
                .ToHashSet();
            IEnumerable<int> conditions = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type == conditionType && condition.Params.Count <= 2
                    && (condition.Params.Count < 2 || condition.Params[1] == parameter))
                .Select(condition => condition.Id)
                .Concat(CurrentConditionsById.Value.Values.Where(condition => condition.Type == conditionType
                    && condition.Params.Count <= 2 && (condition.Params.Count < 2 || condition.Params[1] == parameter))
                    .Select(condition => condition.Id));
            HashSet<int> changed = conditions.Where(active.Contains).ToHashSet();
            foreach (int condition in changed)
                mutation.AddTaskConditionProgress(session.player.PlayerData.Id, condition, amount);
            if (changed.Count == 0) return;
            SyncTask Progress(int id, int condition, int target)
            {
                int value = Math.Min(target, checked(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition)
                    + mutation.Participant(session.player.PlayerData.Id).ConditionCounters.GetValueOrDefault(condition)));
                return ToSyncTask(new MissionTaskProgress(id, condition, value,
                    mutation.IsTaskClaimed(session.player.PlayerData.Id, id) ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive));
            }
            mutation.Push(session.player.PlayerData.Id, new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = TableReaderV2.Parse<TaskTable>().Where(task => changed.Contains(task.Condition) && IsTaskActive(task, now)
                            && (task.Type != 51 || PassportModule.IsActivePassportTask(session, task.Id)))
                        .Select(task => Progress(task.Id, task.Condition, task.Result ?? 1))
                        .Concat(CurrentTasksByPriority.Value.Where(task => changed.Contains(task.Condition) && IsCurrentTaskVisibleAtLogin(task, now))
                            .Select(task => Progress(task.Id, task.Condition, task.Result)))
                        .DistinctBy(task => task.Id).ToList()
                }
            });
        }

        internal static void RecordTableDrivenProgress(Session session, IEnumerable<(int ConditionType, int? Parameter, int Amount)> increments, bool sendNotification = true, TheatreModule.Mutation? theatre = null, Theatre5Module.Mutation? theatre5 = null)
        {
            Dictionary<(int ConditionType, int? Parameter), int> amounts = increments
                .Where(increment => increment.Amount > 0)
                .GroupBy(increment => (increment.ConditionType, increment.Parameter))
                .ToDictionary(group => group.Key, group => group.Sum(increment => increment.Amount));
            if (amounts.Count == 0) return;

            int Amount(int? type, IReadOnlyList<int> parameters)
            {
                if (type is not int conditionType || parameters.Count > 2)
                    return 0;
                if (parameters.Count >= 2)
                    return amounts.GetValueOrDefault((conditionType, parameters[1]));
                // Dorm producers supply an overall amount plus per-furniture-type subtotals.
                return amounts.TryGetValue((conditionType, null), out int total)
                    ? total
                    : amounts.Where(increment => increment.Key.ConditionType == conditionType).Sum(increment => increment.Value);
            }
            Dictionary<int, int> conditionAmounts = TableReaderV2.Parse<ConditionTable>()
                .Select(condition => (condition.Id, Amount: Amount(condition.Type, condition.Params)))
                .Where(condition => condition.Amount > 0)
                .ToDictionary(condition => condition.Id, condition => condition.Amount);
            Dictionary<int, int> currentAmounts = CurrentConditionsById.Value.Values
                .Select(condition => (condition.Id, Amount: Amount(condition.Type, condition.Params)))
                .Where(condition => condition.Amount > 0)
                .ToDictionary(condition => condition.Id, condition => condition.Amount);
            RecordConditionAmounts(session, conditionAmounts, currentAmounts, sendNotification, theatre, theatre5);
        }

        private static void RecordConditionAmounts(Session session, Dictionary<int, int> conditionAmounts, Dictionary<int, int> currentAmounts, bool sendNotification = true, TheatreModule.Mutation? theatre = null, Theatre5Module.Mutation? theatre5 = null)
        {
            if (conditionAmounts.Count == 0 && currentAmounts.Count == 0)
                return;
            if (theatre is null && theatre5 is null) EnsureMissionResets(session);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<TaskTable> tasks = TableReaderV2.Parse<TaskTable>()
                .Where(task => conditionAmounts.ContainsKey(task.Condition)
                    && IsTaskActive(task, now)
                    && (task.Type != 51 || PassportModule.IsActivePassportTask(session, task.Id)))
                .ToList();
            List<CurrentTaskTable> currentTasks = CurrentTasksByPriority.Value
                .Where(task => currentAmounts.ContainsKey(task.Condition) && IsCurrentTaskVisibleAtLogin(task, now)).ToList();
            HashSet<int> visibleCurrentConditions = currentTasks.Select(task => task.Condition).ToHashSet();
            foreach (int conditionId in currentAmounts.Keys.Where(id => !visibleCurrentConditions.Contains(id)).ToArray())
                currentAmounts.Remove(conditionId);
            if (tasks.Count == 0 && currentAmounts.Count == 0) return;

            HashSet<int> legacyConditions = tasks.Select(task => task.Condition).ToHashSet();
            if (theatre is not null || theatre5 is not null)
            {
                Action<int, int> add = theatre is not null ? theatre.AddTaskConditionProgress : theatre5!.AddTaskConditionProgress;
                Func<int, int> get = theatre is not null ? theatre.GetTaskConditionProgress : theatre5!.GetTaskConditionProgress;
                Action<NotifyTask> push = theatre is not null ? theatre.Push : theatre5!.Push;
                foreach (int conditionId in legacyConditions)
                    add(conditionId, conditionAmounts[conditionId]);
                foreach ((int conditionId, int amount) in currentAmounts)
                    if (!legacyConditions.Contains(conditionId))
                        add(conditionId, amount);
                SyncTask Progress(int id, int condition, int target)
                {
                    int value = Math.Min(get(condition), target);
                    int state = session.player.MissionProgress.ClaimedTaskIds.Contains(id)
                        ? TaskStateFinish : value >= target ? TaskStateAchieved : TaskStateActive;
                    return ToSyncTask(new MissionTaskProgress(id, condition, value, state));
                }
                push(new NotifyTask
                {
                    Tasks = new()
                    {
                        Tasks = tasks.Select(task => Progress(task.Id, task.Condition, task.Result ?? 1))
                            .Concat(currentTasks.Select(task => Progress(task.Id, task.Condition, task.Result)))
                            .DistinctBy(task => task.Id).ToList()
                    }
                });
                return;
            }
            Dictionary<int, int> counters = session.player.MissionProgress.ConditionCounters;
            Dictionary<int, int?> previous = legacyConditions.Concat(currentAmounts.Keys).Distinct()
                .ToDictionary(id => id, id => counters.TryGetValue(id, out int value) ? (int?)value : null);
            try
            {
                foreach (int conditionId in legacyConditions)
                    AddConditionProgress(session, conditionId, conditionAmounts[conditionId]);
                foreach ((int conditionId, int amount) in currentAmounts)
                    if (!legacyConditions.Contains(conditionId))
                        AddConditionProgress(session, conditionId, amount);
                session.player.SaveChecked();
            }
            catch
            {
                foreach ((int conditionId, int? value) in previous)
                    if (value.HasValue)
                        counters[conditionId] = value.Value;
                    else
                        counters.Remove(conditionId);
                throw;
            }
            if (!sendNotification) return;
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = tasks.Select(task =>
                    {
                        int result = task.Result ?? 1;
                        int value = Math.Min(session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition), result);
                        int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                            ? TaskStateFinish
                            : value >= result ? TaskStateAchieved : TaskStateActive;
                        return ToSyncTask(new MissionTaskProgress(task.Id, task.Condition, value, state));
                    }).Concat(currentTasks.Select(task =>
                    {
                        (int conditionId, int value, int state) = EvaluateCurrentTask(session, task, now);
                        return ToSyncTask(new MissionTaskProgress(task.Id, conditionId, value, state));
                    })).DistinctBy(task => task.Id).ToList()
                }
            });
        }

        internal static void RecordEquipmentProgress(Session session, int conditionType, IReadOnlyCollection<EquipData> equipment)
        {
            if (equipment.Count == 0)
                return;
            int Count(IReadOnlyList<int> parameters) => equipment.Count(equip =>
                EquipRowsById.Value.TryGetValue(equip.TemplateId, out EquipTable? row)
                && (parameters.Count < 2 || parameters[1] <= 0 || equip.TemplateId == parameters[1])
                && (parameters.Count < 3 || parameters[2] < 0 || parameters[2] == 0 && row.Site is >= 1 and <= 6)
                && (parameters.Count < 4 || parameters[3] < 0 || parameters[3] == 0 && row.Site == 0));
            Dictionary<int, int> conditionAmounts = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type == conditionType)
                .Select(condition => (condition.Id, Amount: Count(condition.Params)))
                .Where(condition => condition.Amount > 0)
                .ToDictionary(condition => condition.Id, condition => condition.Amount);
            Dictionary<int, int> currentAmounts = CurrentConditionsById.Value.Values
                .Where(condition => condition.Type == conditionType)
                .Select(condition => (condition.Id, Amount: Count(condition.Params)))
                .Where(condition => condition.Amount > 0)
                .ToDictionary(condition => condition.Id, condition => condition.Amount);
            RecordConditionAmounts(session, conditionAmounts, currentAmounts);
        }
        private static bool IsTaskActive(TaskTable task, DateTimeOffset now) =>
            (string.IsNullOrWhiteSpace(task.StartTime) || TryParseCurrentTaskTime(task.StartTime, out DateTimeOffset start) && now >= start)
            && (string.IsNullOrWhiteSpace(task.EndTime) || TryParseCurrentTaskTime(task.EndTime, out DateTimeOffset end) && now < end);

        private static void SendPassportConditionTypeSync(Session session, int conditionType)
        {
            HashSet<int> conditionIds = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type == conditionType)
                .Select(condition => condition.Id)
                .ToHashSet();
            List<SyncTask> tasks = BuildPassportTaskProgress(session)
                .Where(task => conditionIds.Contains(task.ConditionId))
                .Select(ToSyncTask)
                .ToList();
            if (tasks.Count > 0)
                session.SendPush(new NotifyTask { Tasks = new() { Tasks = tasks } });
        }

        internal static bool AddConditionTypeProgress(Session session, int conditionType, int amount, int? parameter = null)
        {
            List<int> conditionIds = TableReaderV2.Parse<CurrentConditionTable>()
                .Where(x => x.Type == conditionType
                    && (parameter is null || x.Params.Count > 1 && x.Params[1] == parameter))
                .Select(x => x.Id)
                .ToList();
            HashSet<int> activePassportConditions = BuildPassportTaskProgress(session)
                .Select(task => task.ConditionId)
                .ToHashSet();
            activePassportConditions.UnionWith(TableReaderV2.Parse<TaskTable>()
                .Where(task => IsDormTask(task) && IsTaskActive(task, DateTimeOffset.UtcNow))
                .Select(task => task.Condition));
            conditionIds.AddRange(TableReaderV2.Parse<ConditionTable>()
                .Where(condition => activePassportConditions.Contains(condition.Id)
                    && condition.Type == conditionType
                    && (parameter is null || condition.Params.Count > 1 && condition.Params[1] == parameter))
                .Select(condition => condition.Id));
            conditionIds = conditionIds.Distinct().ToList();
            foreach (int conditionId in conditionIds)
            {
                AddConditionProgress(session, conditionId, amount);
            }
            return conditionIds.Count > 0;
        }
        internal static void SendConditionTypeSync(Session session, int conditionType)
        {
            SendConditionTypesSync(session, [conditionType]);
        }
        private static void SendConditionTypesSync(Session session, IEnumerable<int> conditionTypes)
        {
            HashSet<int> selectedTypes = conditionTypes.ToHashSet();
            IReadOnlyDictionary<int, CurrentConditionTable> conditions = CurrentConditionsById.Value;
            HashSet<int> passportConditionIds = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type is int type && selectedTypes.Contains(type))
                .Select(condition => condition.Id)
                .ToHashSet();
            List<MissionTaskProgress> progress = BuildCurrentTaskProgress(session, loginOnly: true, conditionTypes: selectedTypes)
                .Where(task => conditions.TryGetValue(task.ConditionId, out CurrentConditionTable? condition)
                    && selectedTypes.Contains(condition.Type))
                .Concat(BuildPassportTaskProgress(session)
                    .Where(task => passportConditionIds.Contains(task.ConditionId)))
                .Concat(BuildDormTaskProgress(session)
                    .Where(task => passportConditionIds.Contains(task.ConditionId)))
                .DistinctBy(task => task.TaskId)
                .ToList();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = progress.Select(ToSyncTask).ToList()
                }
            });
            RememberSnapshotTaskProgress(session, progress);
        }

        private static IEnumerable<TaskTable> GuildAchievementTasks()
        {
            HashSet<int> conditions = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => condition.Type is >= 35000 and <= 35003
                    || condition.Type == 15216 && condition.Params.Count > 1 && condition.Params[0] == 28)
                .Select(condition => condition.Id).ToHashSet();
            return TableReaderV2.Parse<TaskTable>().Where(task => task.Type is 6 or 23 && conditions.Contains(task.Condition));
        }

        private static IEnumerable<MissionTaskProgress> BuildGuildAchievementProgress(Session session, Func<int, bool>? claimed = null)
        {
            claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
            Guild? guild = GuildModule.FindMembership(session.player.PlayerData.Id);
            bool member = guild is not null && GuildModule.Rank(guild, session.player.PlayerData.Id) is >= 1 and <= 4;
            Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(condition => condition.Id);
            return GuildAchievementTasks().Where(task => IsTaskActive(task, DateTimeOffset.UtcNow))
                .Select(task =>
                {
                    int value = !member ? 0 : conditions[task.Condition].Type switch
                    {
                        35000 => 1,
                        35001 => checked((int)Math.Max(0, GuildModule.DailyPeriod(DateTimeOffset.UtcNow)
                            - GuildModule.DailyPeriod(DateTimeOffset.FromUnixTimeSeconds(guild!.Members[session.player.PlayerData.Id].JoinedAt)) + 1)),
                        35002 => guild!.Level,
                        35003 => guild!.MemberIds.Count(uid => GuildModule.Rank(guild, uid) is >= 1 and <= 4),
                        _ => session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition)
                    };
                    value = Math.Min(value, task.Result ?? 1);
                    bool prerequisite = task.ShowAfterTaskId is not > 0 || claimed(task.ShowAfterTaskId.Value);
                    return new MissionTaskProgress(task.Id, task.Condition, value,
                        claimed(task.Id) ? TaskStateFinish : member && prerequisite && value >= (task.Result ?? 1) ? TaskStateAchieved : TaskStateActive);
                });
        }

        private enum TaskClaimKind { Current, Transfinite, Passport, Dorm, Story, LifeTree, GuildAchievement }

        private sealed record PreparedTaskClaim(int TaskId, TaskClaimKind Kind, string ClaimKey, List<RewardGoodsTable> Goods);

        // Preparation is read-only. Ordinary claims and mixed mode batches share this validation order.
        private static PreparedTaskClaim? PrepareTaskClaim(Session session, int taskId, out int code,
            Func<int, bool>? claimed = null, Func<int, bool>? storyClaimed = null, Func<int, long>? balance = null)
        {
            claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
            storyClaimed ??= session.stage.FinishedTasks.Contains;
            balance ??= itemId => session.inventory.Items.Where(item => item.Id == itemId).Sum(item => (long)item.Count);
            code = 0;
            TaskClaimKind kind;
            string key;
            bool achieved;
            List<RewardGoodsTable> goods;
            TaskTable? task = TransfiniteTasks(session).FirstOrDefault(row => row.Id == taskId);
            TaskTable? guildTask = GuildAchievementTasks().FirstOrDefault(row => row.Id == taskId);
            if (guildTask is not null)
            {
                kind = TaskClaimKind.GuildAchievement;
                key = $"guild-achievement:{taskId}";
                achieved = BuildGuildAchievementProgress(session, claimed).Any(row => row.TaskId == taskId && row.State == TaskStateAchieved);
                goods = RewardHandler.GetRewardGoods(guildTask.RewardId ?? 0);
            }
            else if (task is not null)
            {
                kind = TaskClaimKind.Transfinite;
                key = $"transfinite-task:{taskId}";
                achieved = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition) >= (task.Result ?? 1);
                goods = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            }
            else if (PassportModule.IsActivePassportTask(session, taskId))
            {
                task = TableReaderV2.Parse<TaskTable>().FirstOrDefault(row => row.Id == taskId && row.Type == 51);
                if (task is null) { code = 20026007; return null; }
                kind = TaskClaimKind.Passport;
                key = PassportModule.PassportTaskClaimKey(session, taskId);
                achieved = PassportTaskValue(session, task, balance) >= (task.Result ?? 1);
                goods = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            }
            else if (TableReaderV2.Parse<CurrentTaskTable>().FirstOrDefault(row => row.Id == taskId) is { } current)
            {
                kind = TaskClaimKind.Current;
                key = current.Type switch
                {
                    2 => $"current-task:{taskId}:{session.player.MissionProgress.DailyResetDay}",
                    3 => $"current-task:{taskId}:{session.player.MissionProgress.WeeklyResetWeek}",
                    10 => $"current-task:{taskId}:{session.player.SimulatedBattlefield.ArenaActivityNo}",
                    _ => $"current-task:{taskId}"
                };
                achieved = EvaluateCurrentTask(session, current, DateTimeOffset.UtcNow, claimed, balance).State == TaskStateAchieved;
                goods = GetCurrentRewardGoods(current.RewardId);
            }
            else if (TableReaderV2.Parse<TaskTable>().FirstOrDefault(row => row.Id == taskId && IsDormTask(row)) is { } dorm)
            {
                kind = TaskClaimKind.Dorm;
                key = dorm.Type == DormDailyTaskType ? $"dorm-task:{taskId}:{session.player.MissionProgress.DailyResetDay}" : $"dorm-task:{taskId}";
                ConditionTable? condition = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(row => row.Id == dorm.Condition);
                achieved = condition is not null && condition.Type is >= 29000 and < 29100
                    && IsTaskActive(dorm, DateTimeOffset.UtcNow)
                    && (dorm.ShowAfterTaskId is not > 0 || claimed(dorm.ShowAfterTaskId.Value))
                    && session.player.MissionProgress.ConditionCounters.GetValueOrDefault(dorm.Condition) >= (dorm.Result ?? 1);
                goods = RewardHandler.GetRewardGoods(dorm.RewardId ?? 0);
            }
            else if (TableReaderV2.Parse<StoryTaskTable>().FirstOrDefault(row => row.Id == taskId) is { } story)
            {
                kind = TaskClaimKind.Story;
                key = $"story-task:{taskId}";
                achieved = BuildStoryTaskProgress(session, storyClaimed).Any(row => row.TaskId == taskId && row.State == TaskStateAchieved);
                goods = RewardHandler.GetRewardGoods(story.RewardId);
            }
            else
            {
                task = TableReaderV2.Parse<LifeTreeTask>().FirstOrDefault(row => row.Id == taskId
                    && TableReaderV2.Parse<LifeTreeTaskCondition>().Any(condition => condition.Id == row.Condition && condition.Type == 137001));
                if (task is null) { code = 20026005; return null; }
                kind = TaskClaimKind.LifeTree;
                key = $"lifetree-task:{taskId}";
                achieved = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition) >= Math.Max(1, task.Result ?? 1);
                goods = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            }
            if (kind == TaskClaimKind.Story ? storyClaimed(taskId) : claimed(taskId)) code = 20026006;
            else if (!achieved) code = 20026007;
            else if (goods.Count == 0) code = 20026003;
            return code == 0 ? new(taskId, kind, key, goods) : null;
        }

        private static FinishTaskResponse ClaimTaskReward(Session session, int taskId, bool pushSync,
            out RewardApplicationResult? transfiniteApplication, out RewardApplicationResult? passportApplication)
        {
            transfiniteApplication = null;
            passportApplication = null;
            EnsureMissionResets(session);
            if (GuildWarModule.TryGetTaskProgress(session, taskId, out _, out _))
            {
                try
                {
                    List<RewardGoods> rewards = [];
                    GuildModule.Commit(session, GuildModule.RequireMembership(session), mutation =>
                        rewards = GuildWarModule.ClaimTask(mutation, taskId));
                    return new() { RewardGoodsList = rewards };
                }
                catch (AscNet.Common.ServerCodeException exception) { return new() { Code = exception.Code }; }
            }
            PreparedTaskClaim? plan = PrepareTaskClaim(session, taskId, out int code);
            if (plan is null) return new() { Code = code };
            RewardApplicationResult application;
            try
            {
                application = RewardHandler.ApplyRewardsOnceAndPersist([new RewardGrant(plan.ClaimKey, plan.Goods)], session);
                if (plan.Kind == TaskClaimKind.Story)
                {
                    session.stage.FinishedTasks.Add(taskId);
                    try { session.stage.SaveChecked(); }
                    catch { session.stage.FinishedTasks.Remove(taskId); throw; }
                }
                else
                {
                    session.player.MissionProgress.ClaimedTaskIds.Add(taskId);
                    try { session.player.SaveChecked(); }
                    catch { session.player.MissionProgress.ClaimedTaskIds.Remove(taskId); throw; }
                }
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to persist task reward {taskId}: {exception}");
                return new() { Code = 20026003 };
            }
            if (plan.Kind == TaskClaimKind.Transfinite) transfiniteApplication = application;
            else if (plan.Kind == TaskClaimKind.Passport)
            {
                passportApplication = application;
                if (plan.Goods.Any(good => good.TemplateId == Inventory.PassportExp))
                    session.SendPush(new NotifyPassportBaseInfo { BaseInfo = PassportModule.ReadBaseInfo(session, session.player.Passport.ActivityId) });
            }
            else
            {
                application.SendPushes(session);
                if (pushSync) SendTaskSync(session);
            }
            return new() { RewardGoodsList = application.RewardGoods };
        }

        private static void HandleTheatre3MultiTaskRequest(Session session, Packet.Request packet, IReadOnlyCollection<int> taskIds)
        {
            Theatre3Module.Handle<FinishMultiTaskRequest, FinishMultiTaskResponse>(session, packet, (mutation, body, result) =>
            {
                // Guild War claims need their own seasonal journal; never partially commit a cross-journal batch.
                if (body.TaskIds.Any(taskId => GuildWarModule.TryGetTaskProgress(session, taskId, out _, out _)))
                {
                    result.NotDealTaskIds.AddRange(body.TaskIds.Distinct());
                    return;
                }
                HashSet<int> biancaIds = BiancaTheatreModule.MetaTasks().Select(task => task.Id).ToHashSet();
                foreach (int taskId in body.TaskIds.Distinct())
                {
                    if (Theatre3Module.IsMetaTask(taskId))
                    {
                        if (!Theatre3Module.CanClaim(mutation, taskId)) { result.NotDealTaskIds.Add(taskId); continue; }
                        result.RewardGoodsList.AddRange(Theatre3Module.Claim(mutation, taskId));
                        result.SuccessTaskIds.Add(taskId);
                        continue;
                    }
                    if (biancaIds.Contains(taskId))
                    {
                        BiancaTheatreModule.Mutation bianca = new(session);
                        FinishTaskResponse claim = BiancaTheatreModule.ClaimMetaTask(bianca, taskId);
                        if (claim.Code != 0) { result.NotDealTaskIds.Add(taskId); continue; }
                        if (bianca.Goods.Count > 0) mutation.Grant(new RewardGrant($"bianca-task:{taskId}", bianca.Goods));
                        mutation.MarkTaskClaimed(taskId);
                        mutation.RecordBiancaTaskProgress(bianca.State.TaskProgress, bianca.State.ReachedChapterIds);
                        mutation.Push(BuildBiancaTaskSync(bianca.State, mutation.IsTaskClaimed));
                        result.SuccessTaskIds.Add(taskId);
                        result.RewardGoodsList.AddRange(claim.RewardGoodsList);
                        continue;
                    }
                    PreparedTaskClaim? plan = PrepareTaskClaim(session, taskId, out _, mutation.IsTaskClaimed, mutation.IsStoryTaskClaimed, mutation.Balance);
                    if (plan is null) { result.NotDealTaskIds.Add(taskId); continue; }
                    mutation.Grant(new RewardGrant(plan.ClaimKey, plan.Goods));
                    if (plan.Kind == TaskClaimKind.Story) mutation.MarkStoryTaskClaimed(taskId);
                    else mutation.MarkTaskClaimed(taskId);
                    result.SuccessTaskIds.Add(taskId);
                    result.RewardGoodsList.AddRange(plan.Goods.Select(row => new RewardGoods
                    {
                        Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                        RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidOperationException($"Unsupported task reward {row.Id}."))
                    }));
                }
            }, static (result, code) => result.Code = code, afterCommit: committed => SendBiancaTaskClaimUpdates(committed, taskIds));
        }

        private static void HandleBiancaMultiTaskRequest(Session session, Packet.Request packet, IReadOnlyCollection<int> taskIds)
        {
            BiancaTheatreModule.Handle<FinishMultiTaskRequest, FinishMultiTaskResponse>(session, packet, (mutation, body, result) =>
            {
                HashSet<int> biancaIds = BiancaTheatreModule.MetaTasks().Select(task => task.Id).ToHashSet();
                foreach (int taskId in body.TaskIds.Distinct())
                {
                    if (biancaIds.Contains(taskId))
                    {
                        FinishTaskResponse claim = BiancaTheatreModule.ClaimMetaTask(mutation, taskId);
                        if (claim.Code != 0) { result.NotDealTaskIds.Add(taskId); continue; }
                        result.SuccessTaskIds.Add(taskId);
                        result.RewardGoodsList.AddRange(claim.RewardGoodsList);
                        continue;
                    }
                    if (GuildWarModule.TryGetTaskProgress(session, taskId, out _, out _))
                    {
                        FinishTaskResponse claim = ClaimTaskReward(session, taskId, false, out _, out _);
                        if (claim.Code != 0) { result.NotDealTaskIds.Add(taskId); continue; }
                        result.SuccessTaskIds.Add(taskId);
                        result.RewardGoodsList.AddRange(claim.RewardGoodsList);
                        continue;
                    }
                    PreparedTaskClaim? plan = PrepareTaskClaim(session, taskId, out _, mutation.IsTaskClaimed, mutation.IsStoryTaskClaimed, mutation.Balance);
                    if (plan is null) { result.NotDealTaskIds.Add(taskId); continue; }
                    List<RewardGoods> goods = plan.Goods.Select(row => new RewardGoods
                    {
                        Id = row.Id, TemplateId = row.TemplateId, Count = row.Count,
                        RewardType = (int)(RewardHandler.GetRewardType(row) ?? throw new InvalidOperationException($"Unsupported task reward {row.Id}."))
                    }).ToList();
                    mutation.AddRewardGrant(plan.ClaimKey, plan.Goods);
                    if (plan.Kind == TaskClaimKind.Story) mutation.MarkStoryTaskClaimed(taskId);
                    else mutation.MarkTaskClaimed(taskId);
                    result.SuccessTaskIds.Add(taskId);
                    result.RewardGoodsList.AddRange(goods);
                }
            }, static (result, code) => result.Code = code, committed => SendBiancaTaskClaimUpdates(committed, taskIds));
        }

        private static void SendBiancaTaskClaimUpdates(Session session, IReadOnlyCollection<int> taskIds)
        {
            SendTaskSync(session);
            if (taskIds.Any(id => session.player.MissionProgress.ClaimedTaskIds.Contains(id) || session.stage.FinishedTasks.Contains(id)))
                WheelchairManualGuideManager.SendUpdate(session);
            if (taskIds.Any(id => PassportModule.IsActivePassportTask(session, id)))
                session.SendPush(new NotifyPassportBaseInfo { BaseInfo = PassportModule.ReadBaseInfo(session, session.player.Passport.ActivityId) });
        }

        private static List<StoryTaskProgress> BuildStoryTaskProgress(Session session, Func<int, bool>? claimed = null)
        {
            claimed ??= session.stage.FinishedTasks.Contains;
            Dictionary<int, StoryTaskTable> tasks = TableReaderV2.Parse<StoryTaskTable>().ToDictionary(x => x.Id);
            Dictionary<int, StoryTaskConditionTable> conditions = TableReaderV2.Parse<StoryTaskConditionTable>().ToDictionary(x => x.Id);
            Dictionary<int, int> progressCache = new();

            int GetProgress(StoryTaskTable task)
            {
                if (claimed(task.Id))
                {
                    return task.Result;
                }

                if (progressCache.TryGetValue(task.Id, out int cachedProgress))
                {
                    return cachedProgress;
                }

                int conditionId = task.Condition;
                int progress = 0;
                if (conditionId != 0 && conditions.TryGetValue(conditionId, out StoryTaskConditionTable? condition))
                {
                    progress = EvaluateStoryTaskCondition(session, condition, tasks, GetProgress);
                }

                progress = Math.Min(progress, task.Result);
                progressCache[task.Id] = progress;
                return progress;
            }

            return tasks.Values
                .OrderByDescending(x => x.Priority)
                .Select(task =>
                {
                    int progress = GetProgress(task);
                    int state = claimed(task.Id)
                        ? TaskStateFinish
                        : progress >= task.Result ? TaskStateAchieved : TaskStateActive;
                    return new StoryTaskProgress(task.Id, task.Condition, progress, state);
                })
                .ToList();
        }

        private static int EvaluateStoryTaskCondition(Session session, StoryTaskConditionTable condition, IReadOnlyDictionary<int, StoryTaskTable> tasks, Func<StoryTaskTable, int> getProgress)
        {
            return condition.Type switch
            {
                10202 => HasCompletedPrologue(session) ? 1 : 0,
                15201 or 15220 or 15222 => HasPassedEveryStageParam(session, condition) ? 1 : 0,
                15219 => HasPassedEveryStageParam(session, condition) ? 1 : 0,
                17203 => CountCompletedChildTasks(condition, tasks, getProgress),
                _ => 0
            };
        }

        private static bool HasCompletedPrologue(Session session)
        {
            return session.stage.Stages.Values.Any(x => x.Passed);
        }

        private static bool HasPassedEveryStageParam(Session session, StoryTaskConditionTable condition)
        {
            List<int> stageIds = condition.Params.Where(x => x >= 10_000_000).ToList();
            return stageIds.Count > 0 && stageIds.All(stageId => session.stage.Stages.TryGetValue(stageId, out StageDatum? stageData) && stageData.Passed);
        }

        private static int CountCompletedChildTasks(StoryTaskConditionTable condition, IReadOnlyDictionary<int, StoryTaskTable> tasks, Func<StoryTaskTable, int> getProgress)
        {
            return condition.Params
                .Skip(1)
                .Where(tasks.ContainsKey)
                .Count(taskId =>
                {
                    StoryTaskTable task = tasks[taskId];
                    return getProgress(task) >= task.Result;
                });
        }

        private static bool IsCurrentTaskVisibleAtLogin(CurrentTaskTable task, DateTimeOffset now) =>
            IsTaskActive(task, now)
            && (task.LoginVisible == 1 || task.Type is 4 or 5 or 6 or 7 or 71 or 91
                || !string.IsNullOrWhiteSpace(task.StartTime) || !string.IsNullOrWhiteSpace(task.EndTime));

        private static bool IsTaskActive(CurrentTaskTable task, DateTimeOffset now) =>
            (string.IsNullOrWhiteSpace(task.StartTime) || TryParseCurrentTaskTime(task.StartTime, out DateTimeOffset start) && now >= start)
            && (string.IsNullOrWhiteSpace(task.EndTime) || TryParseCurrentTaskTime(task.EndTime, out DateTimeOffset end) && now < end);

        private static bool TryParseCurrentTaskTime(string value, out DateTimeOffset result)
        {
            return DateTimeOffset.TryParseExact(
                value,
                CurrentTaskTimeFormat,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out result);
        }

        private static List<MissionTaskProgress> BuildCurrentTaskProgress(Session session, bool loginOnly, IReadOnlySet<int>? conditionTypes = null)
        {
            IReadOnlyDictionary<int, CurrentConditionTable> conditions = CurrentConditionsById.Value;
            IEnumerable<CurrentTaskTable> tasks = CurrentTasksByPriority.Value;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (conditionTypes is not null)
                tasks = tasks.Where(task => conditions.TryGetValue(task.Condition, out CurrentConditionTable? condition) && conditionTypes.Contains(condition.Type));
            if (loginOnly)
            {
                tasks = tasks.Where(task => IsCurrentTaskVisibleAtLogin(task, now));
            }

            return tasks
                .Select(task =>
                {
                    (int conditionId, int value, int state) = EvaluateCurrentTask(session, task, now);
                    return new MissionTaskProgress(task.Id, conditionId, value, state);
                })
                .ToList();
        }

        private static (int ConditionId, int Value, int State) EvaluateCurrentTask(Session session, CurrentTaskTable task, DateTimeOffset now,
            Func<int, bool>? claimed = null, Func<int, long>? balance = null)
        {
            claimed ??= session.player.MissionProgress.ClaimedTaskIds.Contains;
            CurrentConditionTable? condition = CurrentConditionsById.Value.GetValueOrDefault(task.Condition);
            int conditionId = condition?.Id ?? task.Id;
            int value = condition is null ? 0 : EvaluateCurrentCondition(session, condition, balance);
            if (condition?.Type is not (28003 or 28006))
                value = Math.Min(value, task.Result);
            bool prerequisiteSatisfied = task.PreTaskId == 0
                || claimed(task.PreTaskId)
                || !CurrentTaskIds.Value.Contains(task.PreTaskId);
            int state = claimed(task.Id)
                ? TaskStateFinish
                : IsTaskActive(task, now) && prerequisiteSatisfied && value >= task.Result ? TaskStateAchieved : TaskStateActive;
            return (conditionId, value, state);
        }

        private static int EvaluateCurrentCondition(Session session, CurrentConditionTable condition, Func<int, long>? balance = null)
        {
            List<int> parameters = condition.Params;
            int stored = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id);
            return condition.Type switch
            {
                10101 => (int)session.player.PlayerData.Level,
                10102 => 1,
                10202 => Math.Max(0, session.player.PlayerData.NewPlayerTaskActiveDay),
                35002 => GuildModule.FindMembership(session.player.PlayerData.Id) is { } guild
                    && GuildModule.Rank(guild, session.player.PlayerData.Id) is >= 1 and <= 4 ? guild.Level : 0,
                11201 => (int)Math.Min(int.MaxValue, balance?.Invoke(Inventory.Coin)
                    ?? session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.Coin)?.Count ?? 0),
                12201 => CountQualifyingEquipment(session, parameters),
                12208 => Math.Max(stored, session.player.EquipGuideData.FinishedTargets.Any(EquipGuideTargetsById.Value.ContainsKey)
                    || EquipGuideTargetsById.Value.TryGetValue(session.player.EquipGuideData.TargetId, out EquipTargetTable? target)
                        && target.CharacterId == session.player.EquipGuideData.CharacterId ? 1 : 0),
                12209 => session.player.EquipGuideData.FinishedTargets.Any(EquipGuideTargetsById.Value.ContainsKey)
                    || EquipModule.IsCurrentGoalComplete(session) ? 1 : 0,
                12211 when parameters.Count >= 3 => session.player.EquipGuideData.FinishedTargets.Any(targetId =>
                    parameters.Skip(2).Contains(targetId) && EquipGuideTargetsById.Value.TryGetValue(targetId, out EquipTargetTable? goal)
                        && goal.CharacterId == parameters[1]) ? 1 : 0,
                // Match the known requirement prefix, like 13102; trailing fields do not invalidate it.
                13101 when parameters.Count >= 3 => CharacterMeets(session, parameters[0], character =>
                    character.Quality >= parameters[1] && character.Level >= parameters[2]
                    && (parameters.Count < 4 || character.Grade >= parameters[3])
                    && (parameters.Count < 5 || parameters[4] <= 0 || CharacterPower.Calculate(session, character) >= parameters[4])),
                13102 => CountCharactersAtQuality(session, parameters),
                13104 => CharacterMeets(session, parameters[0], character => character.TrustLv >= parameters[1]),
                13105 => CountCharactersAtTrust(session, parameters),
                13106 => CharacterMeets(session, parameters[1], character => character.Quality >= parameters[0]),
                13107 => CharacterMeets(session, parameters[1], character => character.LiberateLv >= parameters[0]),
                13213 when parameters.Count > 1 => session.character.Characters.Sum(character =>
                    character.SkillList.Count(skill => skill.Level >= parameters[1])),
                13214 when parameters.Count > 1 => session.character.Characters.Any(character =>
                    character.EnhanceSkillList.Any(skill => parameters.Skip(1).Contains((int)skill.Id) && skill.Level >= parameters[0])) ? 1 : 0,
                15101 or 15220 or 15225 => parameters.Any(stageId => HasPassedStage(session, stageId)) ? 1 : stored,
                15201 when parameters.Count > 1 && parameters[0] == 1 => Math.Max(stored,
                    parameters.Skip(1).Any(stageId => HasPassedStage(session, stageId)) ? 1 : 0),
                15207 => parameters.Skip(1).Sum(stageId => session.stage.Stages.TryGetValue(stageId, out StageDatum? stage)
                    ? System.Numerics.BitOperations.PopCount((ulong)stage.StarsMark) : 0),
                15226 => StoryChapters(parameters).Count(stages => stages.Count > 0 && stages.All(stageId => HasPassedStage(session, stageId))),
                15227 => session.stage.Stages.Values.Count(stage => stage.Passed
                    && StageTypesById.Value.TryGetValue((int)stage.StageId, out int stageType)
                    && parameters.Skip(1).Contains(stageType)),
                19002 when parameters.Count > 1 => 0,
                19002 => session.character.Fashions.Count,
                76100 when parameters.Count >= 3 => session.character.Partners.Any(partner =>
                    partner.TemplateId == parameters[2] && (partner.BreakThrough > parameters[0]
                        || partner.BreakThrough == parameters[0] && partner.Level >= parameters[1])) ? 1 : 0,
                76101 when parameters.Count >= 2 => session.character.Partners.Count(partner => partner.Quality >= parameters[0]),
                76102 when parameters.Count >= 2 => session.character.Partners.Any(partner =>
                    partner.TemplateId == parameters[1] && partner.Quality >= parameters[0]) ? 1 : 0,
                76103 => session.character.Partners.Sum(partner =>
                    (partner.SkillList.FirstOrDefault(skill => skill.Type == 1)?.Level ?? 0)
                    + partner.SkillList.Where(skill => skill.Type == 2).Sum(skill => skill.Level)),
                89001 => parameters.Count > 0
                    && CourseModule.TryGetChapterComplete(session.player, parameters[0], out bool complete)
                    && complete ? 1 : 0,
                _ => stored
            };
        }

        private static int CountQualifyingEquipment(Session session, IReadOnlyList<int> parameters)
        {
            if (parameters.Count < 7)
            {
                return 0;
            }

            int memoryTypeFilter = parameters[2];
            int weaponTypeFilter = parameters[3];
            int requiredQuality = parameters[4];
            int requiredBreakthrough = parameters[5];
            int requiredLevel = parameters[6];
            IReadOnlyDictionary<uint, EquipTable> equipment = EquipRowsById.Value;
            return session.character.Equips.Count(equip =>
                !equip.IsRecycle
                && (parameters[1] <= 0 || equip.TemplateId == parameters[1])
                && equipment.TryGetValue(equip.TemplateId, out EquipTable? row)
                && (memoryTypeFilter < 0 || memoryTypeFilter == 0 && row.Site is >= 1 and <= 6)
                && (weaponTypeFilter < 0 || weaponTypeFilter == 0 && row.Site == 0)
                && row.Quality >= requiredQuality
                && (equip.Breakthrough > requiredBreakthrough
                    || equip.Breakthrough == requiredBreakthrough && equip.Level >= requiredLevel));
        }

        private static int CharacterMeets(
            Session session,
            int characterId,
            Func<CharacterData, bool> predicate)
        {
            CharacterData? character = session.character.Characters.FirstOrDefault(candidate => candidate.Id == characterId);
            return character is not null && predicate(character) ? 1 : 0;
        }

        private static int CountCharactersAtQuality(Session session, IReadOnlyList<int> parameters)
        {
            if (parameters.Count < 3)
            {
                return 0;
            }

            int requiredQuality = parameters[1];
            int requiredLevel = parameters[2];
            return session.character.Characters.Count(character =>
                character.Quality >= requiredQuality && character.Level >= requiredLevel
                && (parameters.Count < 4 || character.Grade >= parameters[3])
                && (parameters.Count < 5 || parameters[4] <= 0 || CharacterPower.Calculate(session, character) >= parameters[4]));
        }

        private static int CountCharactersAtTrust(Session session, IReadOnlyList<int> parameters)
        {
            if (parameters.Count < 2)
            {
                return 0;
            }

            int requiredTrust = parameters[1];
            return session.character.Characters.Count(character => character.TrustLv >= requiredTrust);
        }

        private static IReadOnlyDictionary<int, List<List<int>>> BuildStoryChapterStages()
        {
            Dictionary<int, ChapterTable> main = TableReaderV2.Parse<ChapterTable>().ToDictionary(chapter => chapter.ChapterId);
            Dictionary<int, ChapterExtraDetailsTable> extra = TableReaderV2.Parse<ChapterExtraDetailsTable>().ToDictionary(chapter => chapter.ChapterId);
            Dictionary<int, ShortStoryDetailsTable> shortStory = TableReaderV2.Parse<ShortStoryDetailsTable>().ToDictionary(chapter => chapter.ChapterId);
            return new Dictionary<int, List<List<int>>>
            {
                [1] = TableReaderV2.Parse<ChapterMainTable>().Select(chapter => chapter.ChapterId.FirstOrDefault())
                    .Where(main.ContainsKey).Select(chapterId => main[chapterId].StageId).ToList(),
                [25] = TableReaderV2.Parse<ChapterExtraTable>().Select(chapter => chapter.ChapterId.FirstOrDefault())
                    .Where(extra.ContainsKey).Select(chapterId => extra[chapterId].StageId).ToList(),
                [57] = TableReaderV2.Parse<ShortStoryChapterTable>().Select(chapter => chapter.ChapterId)
                    .Where(shortStory.ContainsKey).Select(chapterId => shortStory[chapterId].StageId).ToList()
            };
        }

        private static IEnumerable<List<int>> StoryChapters(IReadOnlyList<int> parameters) =>
            parameters.Skip(1).Distinct().Where(StoryChapterStages.Value.ContainsKey)
                .SelectMany(type => StoryChapterStages.Value[type]);

        private static bool HasPassedStage(Session session, int stageId)
        {
            return session.stage.Stages.TryGetValue((uint)stageId, out StageDatum? stage) && stage.Passed;
        }

        private static void AddConditionProgress(Session session, int conditionId, int increment)
        {
            int current = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(conditionId);
            session.player.MissionProgress.ConditionCounters[conditionId] = checked(current + Math.Max(0, increment));
        }

        internal static long CurrentDailyResetPeriod(long timestamp) => timestamp / 86_400;

        internal static long CurrentWeeklyResetPeriod(long timestamp) =>
            (CurrentDailyResetPeriod(timestamp) + 3) / 7;

        internal static int WeeklyResetDayIndex(long timestamp)
        {
            long day = CurrentDailyResetPeriod(timestamp);
            long weekStartDay = checked(CurrentWeeklyResetPeriod(timestamp) * 7 - 3);
            return checked((int)(day - weekStartDay));
        }

        internal static long RemainingSecondsInWeeklyResetPeriod(long timestamp)
        {
            long nextWeekStartDay = checked((CurrentWeeklyResetPeriod(timestamp) + 1) * 7 - 3);
            return checked(nextWeekStartDay * 86_400 - timestamp);
        }

        internal static void RecordLoginDay(Session session, long? timestamp = null)
        {
            long now = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long previousLogin = session.player.PlayerData.LastLoginTime;
            int previousCount = session.player.PlayerData.NewPlayerTaskActiveDay;
            int count = previousCount <= 0 ? 1 : previousCount;
            if (previousCount > 0 && previousLogin > 0
                && CurrentDailyResetPeriod(now) > CurrentDailyResetPeriod(previousLogin))
                count = checked(previousCount + 1);
            long lastLogin = Math.Max(previousLogin, now);
            if (count == previousCount && lastLogin == previousLogin)
                return;
            session.player.PlayerData.NewPlayerTaskActiveDay = count;
            session.player.PlayerData.LastLoginTime = lastLogin;
            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                session.player.PlayerData.NewPlayerTaskActiveDay = previousCount;
                session.player.PlayerData.LastLoginTime = previousLogin;
                throw;
            }
        }

        private static void PrepareTaskRequest(Session session, bool bianca, bool theatre, bool theatre5 = false)
        {
            // A foreign task journal must commit before cloning this mode; the owning handler keeps its intent check.
            if (!theatre && session.player.Theatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse) or nameof(BuyResponse))
                TheatreModule.ReplayPending(session);
            if (!bianca && session.player.BiancaTheatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                BiancaTheatreModule.ReplayPending(session);
            if (session.player.Theatre3.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                Theatre3Module.ReplayPending(session);
            if (!theatre5 && session.player.Theatre5.PendingMutation is not null)
                Theatre5Module.ReplayPending(session);
            if (!(bianca && session.player.BiancaTheatre.PendingMutation is not null)
                && !(theatre && session.player.Theatre.PendingMutation is not null)
                && !(theatre5 && session.player.Theatre5.PendingMutation is not null))
                EnsureMissionResets(session);
        }

        internal static void EnsureMissionResets(Session session)
        {
            // The owner must match/recover its pending intent before shared resets can mutate it.
            if (session.player.Theatre5.PendingMutation is not null)
                return;
            // Restore old-period claims before clearing them and recording new-period progress.
            if (session.player.Theatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse) or nameof(BuyResponse))
                TheatreModule.ReplayPending(session);
            if (session.player.BiancaTheatre.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                BiancaTheatreModule.ReplayPending(session);
            if (session.player.Theatre3.PendingMutation?.ResponseName is nameof(FinishTaskResponse) or nameof(FinishMultiTaskResponse))
                Theatre3Module.ReplayPending(session);
            session.player.MissionProgress ??= new MissionProgressState();
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long day = CurrentDailyResetPeriod(timestamp);
            long week = CurrentWeeklyResetPeriod(timestamp);
            bool changed = false;
            bool inventoryChanged = false;
            bool characterChanged = false;

            if (session.player.MissionProgress.DailyResetDay < 0)
            {
                session.player.MissionProgress.DailyResetDay = day;
                changed = true;
            }
            else if (session.player.MissionProgress.DailyResetDay != day)
            {
                ResetMissionType(session, 2);
                ResetPassportTaskType(session, 1);
                (bool dormInventoryChanged, bool dormCharacterChanged) = ResetDormMissionType(session, DormDailyTaskType);
                inventoryChanged |= dormInventoryChanged;
                characterChanged |= dormCharacterChanged;
                session.player.PlayerData.DailyActivenessRewardStatus = 0;
                Item? dailyActiveness = session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.DailyActiveness);
                if (dailyActiveness is not null && dailyActiveness.Count != 0)
                {
                    dailyActiveness.Count = 0;
                    inventoryChanged = true;
                }
                session.player.MissionProgress.DailyResetDay = day;
                changed = true;
            }

            if (session.player.MissionProgress.WeeklyResetWeek < 0)
            {
                session.player.MissionProgress.WeeklyResetWeek = week;
                changed = true;
            }
            else if (session.player.MissionProgress.WeeklyResetWeek != week)
            {
                ResetMissionType(session, 3);
                ResetPassportTaskType(session, 2);
                session.player.PlayerData.WeeklyActivenessRewardStatus = 0;
                Item? weeklyActiveness = session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.WeeklyActiveness);
                if (weeklyActiveness is not null && weeklyActiveness.Count != 0)
                {
                    weeklyActiveness.Count = 0;
                    inventoryChanged = true;
                }
                session.player.MissionProgress.WeeklyResetWeek = week;
                changed = true;
            }

            if (changed)
            {
                session.player.Save();
            }
            if (inventoryChanged)
            {
                session.inventory.Save();
            }
            if (characterChanged)
            {
                session.character.Save();
            }
        }

        private static void ResetMissionType(Session session, int taskType)
        {
            List<CurrentTaskTable> tasks = TableReaderV2.Parse<CurrentTaskTable>().Where(x => x.Type == taskType).ToList();
            HashSet<int> taskIds = tasks.Select(x => x.Id).ToHashSet();
            HashSet<int> conditionIds = tasks.Select(x => x.Condition).ToHashSet();
            session.player.MissionProgress.ClaimedTaskIds.RemoveAll(taskIds.Contains);
            foreach (int conditionId in conditionIds)
            {
                session.player.MissionProgress.ConditionCounters.Remove(conditionId);
            }
        }
        private static void ResetPassportTaskType(Session session, int groupType)
        {
            IReadOnlySet<int> taskIds = PassportModule.PassportTaskIdsByType(session, groupType);
            if (taskIds.Count == 0) return;
            List<TaskTable> tasks = TableReaderV2.Parse<TaskTable>()
                .Where(task => taskIds.Contains(task.Id))
                .ToList();
            session.player.MissionProgress.ClaimedTaskIds.RemoveAll(taskIds.Contains);
            foreach (int conditionId in tasks.Select(task => task.Condition).Distinct())
                session.player.MissionProgress.ConditionCounters.Remove(conditionId);
        }

        private static (bool Inventory, bool Character) ResetDormMissionType(Session session, int taskType)
        {
            List<TaskTable> tasks = TableReaderV2.Parse<TaskTable>().Where(task => task.Type == taskType).ToList();
            HashSet<int> taskIds = tasks.Select(task => task.Id).ToHashSet();
            session.player.MissionProgress.ClaimedTaskIds.RemoveAll(taskIds.Contains);
            foreach (int conditionId in tasks.Select(task => task.Condition).Distinct())
            {
                session.player.MissionProgress.ConditionCounters.Remove(conditionId);
            }

            HashSet<string> legacyKeys = taskIds.Select(taskId => $"dorm-task:{taskId}").ToHashSet(StringComparer.Ordinal);
            string[] prefixes = legacyKeys.Select(key => key + ":").ToArray();
            bool IsDailyClaim(string claim) => legacyKeys.Contains(claim)
                || prefixes.Any(prefix => claim.StartsWith(prefix, StringComparison.Ordinal));
            bool inventoryChanged = session.inventory.AppliedRewardClaims.RemoveAll(IsDailyClaim) > 0;
            bool characterChanged = session.character.AppliedRewardClaims.RemoveAll(IsDailyClaim) > 0;
            return (inventoryChanged, characterChanged);
        }


        private static LoginTask ToLoginTask(MissionTaskProgress progress)
        {
            return new LoginTask
            {
                Id = (uint)progress.TaskId,
                State = progress.State,
                RecordTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ActivityId = 0,
                Schedule =
                [
                    new LoginTaskSchedule
                    {
                        Id = (uint)progress.ConditionId,
                        Value = progress.Value
                    }
                ]
            };
        }

        private static SyncTask ToSyncTask(MissionTaskProgress progress)
        {
            return new SyncTask
            {
                Id = (uint)progress.TaskId,
                State = progress.State,
                RecordTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ActivityId = 0,
                Schedule =
                [
                    new SyncTaskSchedule
                    {
                        Id = (uint)progress.ConditionId,
                        Value = progress.Value
                    }
                ]
            };
        }

        private static List<RewardGoodsTable> GetCurrentRewardGoods(int rewardId)
        {
            CurrentRewardTable? reward = TableReaderV2.Parse<CurrentRewardTable>().FirstOrDefault(x => x.Id == rewardId);
            if (reward is null)
            {
                return [];
            }

            HashSet<int> subIds = reward.SubIds.ToHashSet();
            return TableReaderV2.Parse<CurrentRewardGoodsTable>()
                .Where(x => subIds.Contains(x.Id))
                .Select(x => new RewardGoodsTable
                {
                    Id = x.Id,
                    TemplateId = x.TemplateId,
                    Count = x.Count,
                    Params = x.Params
                })
                .ToList();
        }

        private static LoginTask ToLoginTask(StoryTaskProgress progress)
        {
            return new LoginTask
            {
                Id = (uint)progress.TaskId,
                State = progress.State,
                RecordTime = 0,
                ActivityId = 0,
                Schedule =
                [
                    new LoginTaskSchedule
                    {
                        Id = (uint)progress.ConditionId,
                        Value = progress.Value
                    }
                ]
            };
        }

        private static SyncTask ToSyncTask(StoryTaskProgress progress)
        {
            return new SyncTask
            {
                Id = (uint)progress.TaskId,
                State = progress.State,
                RecordTime = 0,
                ActivityId = 0,
                Schedule =
                [
                    new SyncTaskSchedule
                    {
                        Id = (uint)progress.ConditionId,
                        Value = progress.Value
                    }
                ]
            };
        }


        private const int NewPlayerActivenessItemId = 20;

        private const int TaskStateActive = 1;
        private const int TaskStateAchieved = 3;
        private const int TaskStateFinish = 4;

        private sealed record StoryTaskProgress(int TaskId, int ConditionId, int Value, int State);
        private sealed record MissionTaskProgress(int TaskId, int ConditionId, int Value, int State);


    }
}
