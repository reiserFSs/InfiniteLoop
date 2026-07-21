using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.fuben.transfinite;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.equip;
using MessagePack;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using LoginTaskSchedule = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask.NotifyTaskDataTaskDataTaskSchedule;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;
using SyncTaskSchedule = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask.NotifyTaskTasksTaskSchedule;
using LifeTreeTask = AscNet.Table.V2.share.task.TaskTable;
using LifeTreeTaskCondition = AscNet.Table.V2.share.task.ConditionTable;

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
    public class FinishTaskRequest
    {
        public int TaskId { get; set; }
    }

    [MessagePackObject(true)]
    public class FinishTaskResponse
    {
        public int Code { get; set; }
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
        private static readonly Lazy<IReadOnlyDictionary<int, CurrentConditionTable>> CurrentConditionsById = new(() =>
            TableReaderV2.Parse<CurrentConditionTable>().ToDictionary(condition => condition.Id));
        private static readonly Lazy<IReadOnlyList<CurrentTaskTable>> CurrentTasksByPriority = new(() =>
            TableReaderV2.Parse<CurrentTaskTable>().OrderByDescending(task => task.Priority).ToArray());
        private static readonly Lazy<IReadOnlySet<int>> CurrentTaskIds = new(() =>
            CurrentTasksByPriority.Value.Select(task => task.Id).ToHashSet());
        private static readonly Lazy<IReadOnlyDictionary<uint, EquipTable>> EquipRowsById = new(() =>
            TableReaderV2.Parse<EquipTable>().ToDictionary(equip => (uint)equip.Id));

        [RequestPacketHandler("DoClientTaskEventRequest")]
        public static void DoClientTaskEventRequestHandler(Session session, Packet.Request packet)
        {
            _ = MessagePackSerializer.Deserialize<DoClientTaskEventRequest>(packet.Content);
            EnsureMissionResets(session);
            session.SendResponse(new DoClientTaskEventResponse(), packet.Id);
            SendTaskSync(session);
        }

        [RequestPacketHandler("FinishTaskRequest")]
        public static void FinishTaskRequestHandler(Session session, Packet.Request packet)
        {
            FinishTaskRequest request = MessagePackSerializer.Deserialize<FinishTaskRequest>(packet.Content);
            FinishTaskResponse response = ClaimTaskReward(session, request.TaskId, pushSync: false, out RewardApplicationResult? transfiniteApplication);
            if (IsTransfiniteTask(session, request.TaskId))
            {
                SendTransfiniteTaskSync(session, TransfiniteTasks(session).Where(task => task.Id == request.TaskId));
                transfiniteApplication?.SendPushes(session);
            }
            else if (CurrentTaskIds.Value.Contains(request.TaskId))
            {
                SendCurrentTaskBatch(session, [request.TaskId]);
            }
            else if (IsDormTask(request.TaskId))
            {
                SendDormTaskBatch(session, [request.TaskId]);
            }
            else
            {
                SendTaskSync(session);
            }
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("FinishMultiTaskRequest")]
        public static void FinishMultiTaskRequestHandler(Session session, Packet.Request packet)
        {
            FinishMultiTaskRequest request = MessagePackSerializer.Deserialize<FinishMultiTaskRequest>(packet.Content);
            FinishMultiTaskResponse response = new()
            {
                Code = 0
            };

            List<RewardApplicationResult> transfiniteApplications = [];
            foreach (int taskId in request.TaskIds.Distinct())
            {
                FinishTaskResponse taskResponse = ClaimTaskReward(session, taskId, pushSync: false, out RewardApplicationResult? transfiniteApplication);
                if (transfiniteApplication is not null)
                {
                    transfiniteApplications.Add(transfiniteApplication);
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
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("GetActivenessRewardRequest")]
        public static void GetActivenessRewardRequestHandler(Session session, Packet.Request packet)
        {
            GetActivenessRewardRequest request = MessagePackSerializer.Deserialize<GetActivenessRewardRequest>(packet.Content);
            GetActivenessRewardResponse response = ClaimActivenessRewards(session, request.RewardType);
            if (response.Code == 0)
            {
                session.SendPush(BuildActivenessStatus(session));
            }
            session.SendResponse(response, packet.Id);
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
            Dictionary<string, int>? request = MessagePackSerializer.Deserialize<Dictionary<string, int>?>(packet.Content);
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

            List<RewardGoods> grantedRewards = new();
            foreach ((int rewardIndex, List<RewardGoodsTable> rewardGoods) in rewardIndexes.Zip(configuredRewards))
            {
                claimedStatus |= 1L << rewardIndex;
                grantedRewards.AddRange(RewardHandler.GiveRewards(rewardGoods, session));
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
            return new GetActivenessRewardResponse
            {
                Code = 0,
                RewardGoodsList = grantedRewards
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

            List<RewardGoods> grantedRewards = new();
            for (int index = 0; index < rewardIndexes.Count; index++)
            {
                int rewardIndex = rewardIndexes[index];
                session.player.MissionProgress.NewPlayerRewardRecords.Add(rewards.Activeness[rewardIndex]);
                grantedRewards.AddRange(RewardHandler.GiveRewards(configuredRewards[index], session));
            }
            session.player.MissionProgress.NewPlayerRewardRecords.Sort();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            return new GetNewPlayerRewardResponse
            {
                Code = 0,
                RewardGoodsList = grantedRewards
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
            List<RewardGoods> grantedRewards = new();
            for (int index = 0; index < rewardIndexes.Count; index++)
            {
                session.player.MissionProgress.NewbieRewardRecords.Add(claimedMilestones[index]);
                grantedRewards.AddRange(RewardHandler.GiveRewards(configuredRewards[index], session));
            }
            session.player.MissionProgress.NewbieRewardRecords.Sort();
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            return new GetNewbieRewardResponse
            {
                Code = 0,
                RewardGoodsList = grantedRewards,
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
            List<RewardGoods> grantedRewards = RewardHandler.GiveRewards(configuredRewards, session);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            return new GetNewbieHonorRewardResponse
            {
                Code = 0,
                RewardGoodsList = grantedRewards
            };
        }

        [RequestPacketHandler("GetCourseRewardRequest")]
        public static void GetCourseRewardRequestHandler(Session session, Packet.Request packet)
        {
            var request = MessagePackSerializer.Deserialize<GetCourseRewardRequest>(packet.Content);
            GetCourseRewardResponse response = ClaimCourseReward(session, request.StageId);
            session.SendResponse(response, packet.Id);
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

            List<RewardGoods> rewardGoodsList = RewardHandler.GiveRewards(rewardGoods, session);
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();

            return new GetCourseRewardResponse
            {
                Code = 0,
                RewardGoodsList = rewardGoodsList
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
            return tasks;
        }

        public static void SendTaskSync(Session session)
        {
            EnsureMissionResets(session);
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = BuildStoryTaskProgress(session)
                        .Select(ToSyncTask)
                        .Concat(BuildCurrentTaskProgress(session, loginOnly: true).Select(ToSyncTask))
                        .Concat(BuildLifeTreeTaskProgress(session).Select(ToSyncTask))
                        .Concat(BuildTransfiniteTaskProgress(session).Select(ToSyncTask))
                        .GroupBy(x => x.Id)
                        .Select(x => x.First())
                        .ToList()
                }
            });
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
        }
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
                        .Where(task => changedConditions.Contains(task.ConditionId))
                        .Select(ToSyncTask)
                        .ToList()
                }
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


        private static FinishTaskResponse? ClaimTransfiniteTaskReward(Session session, int taskId, out RewardApplicationResult? application)
        {
            application = null;
            TaskTable? task = TransfiniteTasks(session).FirstOrDefault(task => task.Id == taskId);
            if (task is null)
            {
                return null;
            }
            if (session.player.MissionProgress.ClaimedTaskIds.Contains(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }
            MissionTaskProgress? progress = BuildTransfiniteTaskProgress(session)
                .FirstOrDefault(progress => progress.TaskId == taskId);
            if (progress is null || progress.State != TaskStateAchieved)
            {
                return new FinishTaskResponse { Code = 20026007 };
            }
            List<RewardGoodsTable> rewards = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            if (rewards.Count == 0)
            {
                return new FinishTaskResponse { Code = 20026003 };
            }
            try
            {
                RewardApplicationResult applied = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"transfinite-task:{taskId}", rewards)], session);
                session.player.MissionProgress.ClaimedTaskIds.Add(taskId);
                try
                {
                    session.player.SaveChecked();
                }
                catch
                {
                    session.player.MissionProgress.ClaimedTaskIds.Remove(taskId);
                    throw;
                }
                application = applied;
                return new FinishTaskResponse { Code = 0, RewardGoodsList = applied.RewardGoods };
            }
            catch
            {
                return new FinishTaskResponse { Code = 20026003 };
            }
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

        public static void RecordStageClear(Session session, int stageId, int count = 1, int actionPointCost = 0)
        {
            EnsureMissionResets(session);
            foreach (CurrentConditionTable condition in TableReaderV2.Parse<CurrentConditionTable>())
            {
                bool matches = condition.Type switch
                {
                    15101 or 15220 or 15225 => condition.Params.Contains(stageId),
                    15201 when condition.Params.Count > 1 => condition.Params.Skip(1).Contains(stageId),
                    15201 or 15217 or 15227 => true,
                    15202 => condition.Params.Count <= 1
                        || condition.Params[1] == 1 && stageId is >= 10_000_000 and < 20_000_000,
                    25005 => stageId is >= 30_300_000 and < 30_310_000,
                    _ => false
                };
                if (!matches)
                {
                    continue;
                }

                int increment = condition.Type == 15201 && condition.Params.Count > 1 ? 1 : count;
                AddConditionProgress(session, condition.Id, increment);
            }

            if (actionPointCost > 0)
            {
                AddConditionTypeProgress(session, 11202, actionPointCost);
            }
            session.player.Save();
            SendTaskSync(session);
        }
        public static void RecordArenaResult(Session session, int point)
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
            if (conditions.Count == 0)
            {
                return;
            }

            HashSet<int> affectedConditionIds = conditions.Select(condition => condition.Id).ToHashSet();
            int[] affectedTaskIds = currentTasks
                .Where(task => affectedConditionIds.Contains(task.Condition))
                .Select(task => task.Id)
                .ToArray();
            session.player.Save();
            SendCurrentTaskBatch(session, affectedTaskIds);
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
        internal static void RecordTableDrivenProgress(Session session, IEnumerable<(int ConditionType, int? Parameter, int Amount)> increments)
        {
            EnsureMissionResets(session);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Dictionary<(int ConditionType, int? Parameter), int> amounts = increments
                .Where(increment => increment.Amount > 0)
                .GroupBy(increment => (increment.ConditionType, increment.Parameter))
                .ToDictionary(group => group.Key, group => group.Sum(increment => increment.Amount));
            if (amounts.Count == 0) return;

            Dictionary<int, int> conditionAmounts = TableReaderV2.Parse<ConditionTable>()
                .Where(condition => amounts.Any(increment => condition.Type == increment.Key.ConditionType
                    && (increment.Key.Parameter is null ? condition.Params.Count < 2 : condition.Params.Count > 1 && condition.Params[1] == increment.Key.Parameter)))
                .ToDictionary(condition => condition.Id, condition => amounts.Single(increment => condition.Type == increment.Key.ConditionType
                    && (increment.Key.Parameter is null ? condition.Params.Count < 2 : condition.Params.Count > 1 && condition.Params[1] == increment.Key.Parameter)).Value);
            List<TaskTable> tasks = TableReaderV2.Parse<TaskTable>()
                .Where(task => conditionAmounts.ContainsKey(task.Condition) && IsTaskActive(task, now))
                .ToList();
            if (tasks.Count == 0) return;

            foreach ((int conditionId, int amount) in conditionAmounts.Where(entry => tasks.Any(task => task.Condition == entry.Key)))
                AddConditionProgress(session, conditionId, amount);

            session.player.Save();
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
                    }).ToList()
                }
            });
        }
        private static bool IsTaskActive(TaskTable task, DateTimeOffset now) =>
            (string.IsNullOrWhiteSpace(task.StartTime) || TryParseCurrentTaskTime(task.StartTime, out DateTimeOffset start) && now >= start)
            && (string.IsNullOrWhiteSpace(task.EndTime) || TryParseCurrentTaskTime(task.EndTime, out DateTimeOffset end) && now < end);

        private static bool AddConditionTypeProgress(Session session, int conditionType, int amount)
        {
            List<int> conditionIds = TableReaderV2.Parse<CurrentConditionTable>()
                .Where(x => x.Type == conditionType)
                .Select(x => x.Id)
                .ToList();
            foreach (int conditionId in conditionIds)
            {
                AddConditionProgress(session, conditionId, amount);
            }
            return conditionIds.Count > 0;
        }
        private static void SendConditionTypeSync(Session session, int conditionType)
        {
            SendConditionTypesSync(session, [conditionType]);
        }
        private static void SendConditionTypesSync(Session session, IEnumerable<int> conditionTypes)
        {
            HashSet<int> selectedTypes = conditionTypes.ToHashSet();
            IReadOnlyDictionary<int, CurrentConditionTable> conditions = CurrentConditionsById.Value;
            List<MissionTaskProgress> progress = BuildCurrentTaskProgress(session, loginOnly: true, conditionTypes: selectedTypes)
                .Where(task => conditions.TryGetValue(task.ConditionId, out CurrentConditionTable? condition)
                    && selectedTypes.Contains(condition.Type))
                .ToList();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = progress.Select(ToSyncTask).ToList()
                }
            });
        }


        private static FinishTaskResponse ClaimTaskReward(Session session, int taskId, bool pushSync, out RewardApplicationResult? transfiniteApplication)
        {
            transfiniteApplication = null;
            FinishTaskResponse? transfiniteTaskResponse = ClaimTransfiniteTaskReward(session, taskId, out transfiniteApplication);
            if (transfiniteTaskResponse is not null)
            {
                return transfiniteTaskResponse;
            }
            CurrentTaskTable? currentTask = TableReaderV2.Parse<CurrentTaskTable>().FirstOrDefault(x => x.Id == taskId);
            if (currentTask is null)
            {
                FinishTaskResponse? dormTaskResponse = ClaimDormTaskReward(session, taskId, pushSync);
                if (dormTaskResponse is not null)
                {
                    return dormTaskResponse;
                }
                if (TableReaderV2.Parse<StoryTaskTable>().Any(task => task.Id == taskId))
                {
                    return ClaimStoryTaskReward(session, taskId, pushSync);
                }
                return ClaimLifeTreeTaskReward(session, taskId, pushSync);
            }

            EnsureMissionResets(session);
            if (session.player.MissionProgress.ClaimedTaskIds.Contains(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }

            MissionTaskProgress? progress = BuildCurrentTaskProgress(session, loginOnly: false).FirstOrDefault(x => x.TaskId == taskId);
            if (progress is null || progress.State != TaskStateAchieved)
            {
                return new FinishTaskResponse { Code = 20026007 };
            }

            List<RewardGoodsTable> rewardGoods = GetCurrentRewardGoods(currentTask.RewardId);
            if (rewardGoods.Count == 0)
            {
                return new FinishTaskResponse { Code = 20026003 };
            }

            session.player.MissionProgress.ClaimedTaskIds.Add(taskId);
            List<RewardGoods> rewardGoodsList = RewardHandler.GiveRewards(rewardGoods, session);
            session.inventory.Save();
            session.character.Save();
            session.player.Save();
            if (pushSync)
            {
                SendTaskSync(session);
            }

            return new FinishTaskResponse
            {
                Code = 0,
                RewardGoodsList = rewardGoodsList
            };
        }

        private static FinishTaskResponse? ClaimDormTaskReward(Session session, int taskId, bool pushSync)
        {
            TaskTable? task = TableReaderV2.Parse<TaskTable>().FirstOrDefault(candidate =>
                candidate.Id == taskId && IsDormTask(candidate));
            if (task is null)
            {
                return null;
            }

            EnsureMissionResets(session);
            if (session.player.MissionProgress.ClaimedTaskIds.Contains(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }

            ConditionTable? condition = TableReaderV2.Parse<ConditionTable>().FirstOrDefault(candidate => candidate.Id == task.Condition);
            if (condition is null
                || condition.Type is < 29000 or >= 29100
                || !IsTaskActive(task, DateTimeOffset.UtcNow)
                || task.ShowAfterTaskId is > 0 && !session.player.MissionProgress.ClaimedTaskIds.Contains(task.ShowAfterTaskId.Value)
                || session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition) < (task.Result ?? 1))
            {
                return new FinishTaskResponse { Code = 20026007 };
            }

            List<RewardGoodsTable> rewardGoods = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            if (rewardGoods.Count == 0)
            {
                return new FinishTaskResponse { Code = 20026003 };
            }

            RewardApplicationResult rewardApplication;
            try
            {
                string claimKey = task.Type == DormDailyTaskType
                    ? $"dorm-task:{taskId}:{session.player.MissionProgress.DailyResetDay}"
                    : $"dorm-task:{taskId}";
                rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant(claimKey, rewardGoods)],
                    session);
                session.player.MissionProgress.ClaimedTaskIds.Add(taskId);
                try
                {
                    session.player.SaveChecked();
                }
                catch
                {
                    session.player.MissionProgress.ClaimedTaskIds.Remove(taskId);
                    throw;
                }
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to persist Dorm task reward {taskId}: {exception}");
                return new FinishTaskResponse { Code = 20026003 };
            }

            rewardApplication.SendPushes(session);
            if (pushSync)
            {
                SendTaskSync(session);
            }
            return new FinishTaskResponse
            {
                Code = 0,
                RewardGoodsList = rewardApplication.RewardGoods
            };
        }

        private static FinishTaskResponse ClaimLifeTreeTaskReward(Session session, int taskId, bool pushSync)
        {
            EnsureMissionResets(session);
            LifeTreeTask? task = TableReaderV2.Parse<LifeTreeTask>().FirstOrDefault(candidate =>
                candidate.Id == taskId
                && TableReaderV2.Parse<LifeTreeTaskCondition>().Any(condition =>
                    condition.Id == candidate.Condition && condition.Type == 137001));
            if (task is null)
            {
                return new FinishTaskResponse { Code = 20026005 };
            }
            if (session.player.MissionProgress.ClaimedTaskIds.Contains(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }

            MissionTaskProgress? progress = BuildLifeTreeTaskProgress(session)
                .FirstOrDefault(candidate => candidate.TaskId == taskId);
            if (progress is null || progress.State != TaskStateAchieved)
            {
                return new FinishTaskResponse { Code = 20026007 };
            }

            List<RewardGoodsTable> rewardGoods = RewardHandler.GetRewardGoods(task.RewardId ?? 0);
            if (rewardGoods.Count == 0)
            {
                return new FinishTaskResponse { Code = 20026003 };
            }

            RewardApplicationResult rewardApplication;
            try
            {
                rewardApplication = RewardHandler.ApplyRewardsOnceAndPersist(
                    [new RewardGrant($"lifetree-task:{taskId}", rewardGoods)],
                    session);
                session.player.MissionProgress.ClaimedTaskIds.Add(taskId);
                try
                {
                    session.player.SaveChecked();
                }
                catch
                {
                    session.player.MissionProgress.ClaimedTaskIds.Remove(taskId);
                    throw;
                }
            }
            catch (Exception exception)
            {
                session.log.Error($"Failed to persist LifeTree task reward {taskId}: {exception}");
                return new FinishTaskResponse { Code = 20026003 };
            }
            rewardApplication.SendPushes(session);
            if (pushSync)
            {
                SendTaskSync(session);
            }

            return new FinishTaskResponse
            {
                Code = 0,
                RewardGoodsList = rewardApplication.RewardGoods
            };
        }

        private static FinishTaskResponse ClaimStoryTaskReward(Session session, int taskId, bool pushSync)
        {
            StoryTaskTable? task = TableReaderV2.Parse<StoryTaskTable>().FirstOrDefault(x => x.Id == taskId);
            if (task is null)
            {
                return new FinishTaskResponse { Code = 20026005 };
            }

            if (session.stage.FinishedTasks.Contains(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }

            StoryTaskProgress? progress = BuildStoryTaskProgress(session).FirstOrDefault(x => x.TaskId == taskId);
            if (progress is null || progress.State != TaskStateAchieved)
            {
                return new FinishTaskResponse { Code = 20026007 };
            }

            List<RewardGoodsTable> rewardGoods = RewardHandler.GetRewardGoods(task.RewardId);
            if (rewardGoods.Count == 0)
            {
                return new FinishTaskResponse { Code = 20026003 };
            }

            if (!session.stage.AddFinishedTask(taskId))
            {
                return new FinishTaskResponse { Code = 20026006 };
            }

            List<RewardGoods> rewardGoodsList = RewardHandler.GiveRewards(rewardGoods, session);
            session.inventory.Save();
            session.character.Save();
            session.stage.Save();

            if (pushSync)
            {
                SendTaskSync(session);
            }

            return new FinishTaskResponse
            {
                Code = 0,
                RewardGoodsList = rewardGoodsList
            };
        }

        private static List<StoryTaskProgress> BuildStoryTaskProgress(Session session)
        {
            Dictionary<int, StoryTaskTable> tasks = TableReaderV2.Parse<StoryTaskTable>().ToDictionary(x => x.Id);
            Dictionary<int, StoryTaskConditionTable> conditions = TableReaderV2.Parse<StoryTaskConditionTable>().ToDictionary(x => x.Id);
            Dictionary<int, int> progressCache = new();

            int GetProgress(StoryTaskTable task)
            {
                if (session.stage.FinishedTasks.Contains(task.Id))
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
                    int state = session.stage.FinishedTasks.Contains(task.Id)
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

        private static bool IsCurrentTaskVisibleAtLogin(CurrentTaskTable task, DateTimeOffset now)
        {
            if (task.LoginVisible == 1 || task.Type is 4 or 6 or 7 or 71 or 91)
                return true;
            if (string.IsNullOrWhiteSpace(task.StartTime) && string.IsNullOrWhiteSpace(task.EndTime))
                return false;
            if (!string.IsNullOrWhiteSpace(task.StartTime)
                && (!TryParseCurrentTaskTime(task.StartTime, out DateTimeOffset startTime) || now < startTime))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(task.EndTime)
                && (!TryParseCurrentTaskTime(task.EndTime, out DateTimeOffset endTime) || now >= endTime))
            {
                return false;
            }
            return true;
        }

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
            if (conditionTypes is not null)
                tasks = tasks.Where(task => conditions.TryGetValue(task.Condition, out CurrentConditionTable? condition) && conditionTypes.Contains(condition.Type));
            if (loginOnly)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                tasks = tasks.Where(task => IsCurrentTaskVisibleAtLogin(task, now));
            }

            return tasks
                .Select(task =>
                {
                    CurrentConditionTable? condition = conditions.GetValueOrDefault(task.Condition);
                    int conditionId = condition?.Id ?? task.Id;
                    int value = condition is null ? 0 : EvaluateCurrentCondition(session, condition);
                    if (condition?.Type is not (28003 or 28006))
                    {
                        value = Math.Min(value, task.Result);
                    }
                    bool prerequisiteSatisfied = task.PreTaskId == 0
                        || session.player.MissionProgress.ClaimedTaskIds.Contains(task.PreTaskId)
                        || !CurrentTaskIds.Value.Contains(task.PreTaskId);
                    int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                        ? TaskStateFinish
                        : prerequisiteSatisfied && value >= task.Result ? TaskStateAchieved : TaskStateActive;
                    return new MissionTaskProgress(task.Id, conditionId, value, state);
                })
                .ToList();
        }

        private static int EvaluateCurrentCondition(Session session, CurrentConditionTable condition)
        {
            List<int> parameters = condition.Params;
            int stored = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(condition.Id);
            return condition.Type switch
            {
                10101 => (int)session.player.PlayerData.Level,
                10102 => 1,
                10202 => Math.Max(1, session.player.PlayerData.NewPlayerTaskActiveDay),
                11201 => (int)Math.Min(int.MaxValue, session.inventory.Items.FirstOrDefault(item => item.Id == Inventory.Coin)?.Count ?? 0),
                12201 => CountQualifyingEquipment(session, parameters),
                13101 => CharacterMeets(session, parameters[0], character => character.Level >= parameters[2]),
                13102 => CountCharactersAtQuality(session, parameters),
                13104 => CharacterMeets(session, parameters[0], character => character.TrustLv >= parameters[1]),
                13105 => CountCharactersAtTrust(session, parameters),
                13106 => CharacterMeets(session, parameters[1], character => character.Quality >= parameters[0]),
                13107 => CharacterMeets(session, parameters[1], character => character.LiberateLv >= parameters[0]),
                15101 or 15220 or 15225 => parameters.Any(stageId => HasPassedStage(session, stageId)) ? 1 : stored,
                15201 when parameters.Count > 1 => parameters.Skip(1).Count(stageId => HasPassedStage(session, stageId)),
                15202 when parameters.Count > 1 && parameters[1] == 1 => Math.Max(stored, CountPassedMainStoryStages(session)),
                15226 => CountPassedMainStoryChapters(session),
                15227 => CountPassedMainStoryStages(session),
                19002 => session.character.Fashions.Count,
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
            int progressionMode = parameters[5];
            int requiredLevel = parameters[6];
            IReadOnlyDictionary<uint, EquipTable> equipment = EquipRowsById.Value;
            return session.character.Equips.Count(equip =>
                !equip.IsRecycle
                && equipment.TryGetValue(equip.TemplateId, out EquipTable? row)
                && (memoryTypeFilter < 0 || memoryTypeFilter == 0 && row.Type == 0)
                && (weaponTypeFilter < 0 || weaponTypeFilter == 0 && row.Type == 1)
                && row.Quality >= requiredQuality
                && equip.Level >= requiredLevel
                && (progressionMode != 1 || equip.Breakthrough > 0));
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
                character.Quality >= requiredQuality && character.Level >= requiredLevel);
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

        private static int CountPassedMainStoryStages(Session session)
        {
            return session.stage.Stages.Values.Count(x => x.Passed && x.StageId is >= 10_000_000 and < 20_000_000);
        }

        private static int CountPassedMainStoryChapters(Session session)
        {
            return session.stage.Stages.Values
                .Where(x => x.Passed && x.StageId is >= 10_000_000 and < 20_000_000)
                .Select(x => x.StageId / 10_000)
                .Distinct()
                .Count();
        }

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

        private static void EnsureMissionResets(Session session)
        {
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
