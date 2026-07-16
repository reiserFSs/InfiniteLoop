using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.reward;
using AscNet.Table.V2.share.equip;
using MessagePack;
using LoginTask = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask;
using LoginTaskSchedule = AscNet.Common.MsgPack.NotifyTaskData.NotifyTaskDataTaskData.NotifyTaskDataTaskDataTask.NotifyTaskDataTaskDataTaskSchedule;
using SyncTask = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask;
using SyncTaskSchedule = AscNet.Common.MsgPack.NotifyTask.NotifyTaskTasks.NotifyTaskTasksTask.NotifyTaskTasksTaskSchedule;

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
            FinishTaskResponse response = ClaimTaskReward(session, request.TaskId, pushSync: false);
            if (TableReaderV2.Parse<CurrentTaskTable>().Any(task => task.Id == request.TaskId))
            {
                SendCurrentTaskBatch(session, [request.TaskId]);
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

            foreach (int taskId in request.TaskIds.Distinct())
            {
                FinishTaskResponse taskResponse = ClaimTaskReward(session, taskId, pushSync: false);
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
            HashSet<int> currentTaskIds = TableReaderV2.Parse<CurrentTaskTable>().Select(task => task.Id).ToHashSet();
            int[] requestedCurrentTaskIds = requestedTaskIds.Where(currentTaskIds.Contains).ToArray();
            if (requestedCurrentTaskIds.Length > 0)
            {
                SendCurrentTaskBatch(session, requestedCurrentTaskIds);
            }
            if (requestedTaskIds.Any(taskId => !currentTaskIds.Contains(taskId)))
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

            List<RewardGoodsTable> rewardGoods = GetRewardGoods(courseTable.RewardId);
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
            HashSet<uint> storyIds = tasks.Select(x => x.Id).ToHashSet();
            tasks.AddRange(BuildCurrentTaskProgress(session, loginOnly: true)
                .Where(x => !storyIds.Contains((uint)x.TaskId))
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
            Dictionary<int, CurrentConditionTable> conditions = TableReaderV2.Parse<CurrentConditionTable>()
                .Where(condition => selectedTypes.Contains(condition.Type))
                .ToDictionary(condition => condition.Id);
            List<MissionTaskProgress> progress = TableReaderV2.Parse<CurrentTaskTable>()
                .Where(task => conditions.ContainsKey(task.Condition))
                .Select(task =>
                {
                    CurrentConditionTable condition = conditions[task.Condition];
                    int stored = session.player.MissionProgress.ConditionCounters.GetValueOrDefault(task.Condition);
                    int value = condition.Type is 28003 or 28006 ? stored : Math.Min(stored, task.Result);
                    int state = session.player.MissionProgress.ClaimedTaskIds.Contains(task.Id)
                        ? TaskStateFinish
                        : value >= task.Result ? TaskStateAchieved : TaskStateActive;
                    return new MissionTaskProgress(task.Id, task.Condition, value, state);
                })
                .ToList();
            session.SendPush(new NotifyTask
            {
                Tasks = new()
                {
                    Tasks = progress.Select(ToSyncTask).ToList()
                }
            });
        }


        private static FinishTaskResponse ClaimTaskReward(Session session, int taskId, bool pushSync)
        {
            CurrentTaskTable? currentTask = TableReaderV2.Parse<CurrentTaskTable>().FirstOrDefault(x => x.Id == taskId);
            if (currentTask is null)
            {
                return ClaimStoryTaskReward(session, taskId, pushSync);
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

            List<RewardGoodsTable> rewardGoods = GetRewardGoods(task.RewardId);
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

        private static List<MissionTaskProgress> BuildCurrentTaskProgress(Session session, bool loginOnly)
        {
            Dictionary<int, CurrentConditionTable> conditions = TableReaderV2.Parse<CurrentConditionTable>().ToDictionary(x => x.Id);
            List<CurrentTaskTable> allTasks = TableReaderV2.Parse<CurrentTaskTable>();
            IEnumerable<CurrentTaskTable> tasks = allTasks;
            if (loginOnly)
            {
                tasks = tasks.Where(x => x.LoginVisible == 1 || x.Type is 4 or 6 or 7 or 71 or 91);
            }

            return tasks
                .OrderByDescending(x => x.Priority)
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
                        || allTasks.All(candidate => candidate.Id != task.PreTaskId);
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
            Dictionary<uint, EquipTable> equipment = TableReaderV2.Parse<EquipTable>()
                .ToDictionary(equip => (uint)equip.Id);
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

        private static void EnsureMissionResets(Session session)
        {
            session.player.MissionProgress ??= new MissionProgressState();
            long day = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400;
            long week = (day + 3) / 7;
            bool changed = false;
            bool inventoryChanged = false;

            if (session.player.MissionProgress.DailyResetDay < 0)
            {
                session.player.MissionProgress.DailyResetDay = day;
                changed = true;
            }
            else if (session.player.MissionProgress.DailyResetDay != day)
            {
                ResetMissionType(session, 2);
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

        private static List<RewardGoodsTable> GetRewardGoods(int rewardId)
        {
            RewardTable? rewardTable = TableReaderV2.Parse<RewardTable>().FirstOrDefault(x => x.Id == rewardId);
            if (rewardTable is null)
            {
                return [];
            }

            HashSet<int> subIds = rewardTable.SubIds.ToHashSet();
            if (subIds.Count == 0)
            {
                return [];
            }

            return TableReaderV2.Parse<RewardGoodsTable>()
                .Where(x => subIds.Contains(x.Id))
                .ToList();
        }

        private const int NewPlayerActivenessItemId = 20;

        private const int TaskStateActive = 1;
        private const int TaskStateAchieved = 3;
        private const int TaskStateFinish = 4;

        private sealed record StoryTaskProgress(int TaskId, int ConditionId, int Value, int State);
        private sealed record MissionTaskProgress(int TaskId, int ConditionId, int Value, int State);


    }
}
