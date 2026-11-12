using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.task;
using MessagePack;
using MentorStudent = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataStudent;
using MentorTask = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataStudent.NotifyMentorDataStudentSystemTask;

namespace AscNet.GameServer.Handlers;

internal static partial class MentorModule
{
    private const int Teacher = 1, Student = 2;
    private const int MentorWeeklyTaskType = 30;
    private const int Available = 0, Accepted = 1, Completed = 2;
    private const int NoAuthority = 20106022, RewardNotReady = 20106042, RewardAlreadyClaimed = 20106041;
    private const int TaskNotFound = 20106050, AlreadyAccepted = 20106051, AlreadyCompleted = 20106052;
    private const int DailyLimitReached = 20106054, WeeklyLimitReached = 20106055;

    internal static Func<long> Clock = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed record Objective(int Id, int Target, ConditionTable Condition, int FunctionId);
    private static (int? Type, int Target, string Parameters) ObjectiveKey(Objective objective) =>
        (objective.Condition.Type, objective.Target, string.Join(',', objective.Condition.Params));

    private static readonly Lazy<Dictionary<int, Objective>> Objectives = new(BuildObjectives);
    private static readonly Lazy<Dictionary<int, List<int>>> ModeGates = new(() =>
        TableReaderV2.Parse<FunctionalOpenTable>().Where(row => row.Id is 10203 or 10204 or 10309)
            .ToDictionary(row => row.Id, row => row.Condition));
    private static Dictionary<int, Objective> BuildObjectives()
    {
        var conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(condition => condition.Id);
        Dictionary<int, Objective> objectives = [];
        // AscNet policy: select supported mentor-weekly objectives from the existing task and condition tables.
        foreach (TaskTable task in TableReaderV2.Parse<TaskTable>().Where(task => task.Type == MentorWeeklyTaskType))
        {
            ConditionTable condition = conditions[task.Condition];
            int functionId = condition.Type switch
            {
                11202 when condition.Params.Count == 2 && condition.Params[1] == Inventory.ActionPoint => 0,
                48002 => 0,
                28005 => 10204,
                25005 => 10203,
                15202 when condition.Params.Count >= 2 => 10309,
                _ => -1
            };
            if (functionId >= 0)
                objectives.Add(task.Id, new(task.Id, task.Result ?? throw new InvalidDataException(
                    $"Mentor task {task.Id} has no target."), condition, functionId));
        }
        return objectives;
    }

    private static IEnumerable<SoloGuidanceMilestone> Rewards(int weeklyLevel) =>
        Rules.WeeklyRewards.GroupBy(row => row.Level).OrderByDescending(tier => tier.Key)
            .First(tier => tier.Key <= weeklyLevel);

    private static string ClaimKey(long week, int index) => $"mentor-weekly:{week}:{index}";

    private static bool HasClaimed(Session session, long week, int index)
    {
        return HasRewardReceipt(session, ClaimKey(week, index));
    }

    private static bool HasRewardReceipt(Session session, string key)
    {
        return session.inventory.AppliedRewardClaims?.Contains(key) == true
            && session.character.AppliedRewardClaims?.Contains(key) == true;
    }

    private static int Identity(Player player, long week)
    {
        long level = player.PlayerData.Level;
        if (ReadRules() is not { } rules || level < rules.StudentLevel)
            return 0;
        if (player.TemporaryInstructor.GraduatedAt != 0)
            return Teacher;
        if (HasTemporaryInstructor(player))
            return Student;
        // A learner who levels into the instructor tier can finish this week's tasks and claim their rewards.
        return level < rules.InstructorLevel || player.Mentor.Week == week ? Student : Teacher;
    }

    private static void Save(Player player, MentorProgressState updated)
    {
        MentorProgressState previous = player.Mentor;
        player.Mentor = updated;
        try { player.SaveChecked(); }
        catch
        {
            player.Mentor = previous;
            throw;
        }
    }

    internal static void Refresh(Session session, bool login = false)
    {
        if (!login && session.MentorIdentity is null)
            return;
        long now = Clock();
        long week = TaskModule.CurrentWeeklyResetPeriod(now);
        long day = TaskModule.CurrentDailyResetPeriod(now);
        RewardApplicationResult? graduation = null;
        if (ReadRules() is { } rules && HasTemporaryInstructor(session.player)
            && session.player.PlayerData.Level >= rules.AutoGraduationLevel)
            graduation = Graduate(session, now);
        int role = Identity(session.player, week);
        MentorProgressState previous = session.player.Mentor;
        bool newWeek = role == Student && previous.Week != week;
        bool newDay = role == Student && (newWeek || previous.Day != day);
        bool countLogin = role == Student && (newWeek || previous.LastLoginDay != day)
            && session.player.PlayerData.LastLoginTime > 0
            && TaskModule.CurrentDailyResetPeriod(session.player.PlayerData.LastLoginTime) == day;

        if (newDay || countLogin)
        {
            MentorProgressState updated = newWeek
                ? new() { Week = week, WeeklyLevel = checked((int)session.player.PlayerData.Level) }
                : previous.Copy();
            if (countLogin)
            {
                updated.LastLoginDay = day;
                updated.LoginDays++;
            }
            if (newDay)
            {
                updated.Day = day;
                // AscNet policy: prioritize Serum and login objectives, then unlocked modes. Refresh the
                // daily list with the objectives that the learner has yet to accept this week.
                var regionTasks = ClientRegionTables.TaskIds(session.player.ClientRegion);
                updated.DailyOffers = Objectives.Value.Values
                    .Where(objective => regionTasks?.Contains(objective.Id) != false
                        && (objective.FunctionId == 0 || ModeGates.Value[objective.FunctionId]
                            .All(id => GuildModule.ConditionSatisfied(session, id, []))))
                    .OrderBy(objective => objective.FunctionId != 0)
                    .ThenBy(objective => objective.Condition.Type != 11202)
                    .ThenBy(objective => objective.Target)
                    .ThenBy(objective => objective.Id)
                    .DistinctBy(ObjectiveKey)
                    .Where(objective => updated.Tasks.All(task => ObjectiveKey(Objectives.Value[task.TaskId]) != ObjectiveKey(objective)))
                    .Take(Rules.DailyOfferLimit).Select(objective => objective.Id).ToList();
            }
            Save(session.player, updated);
        }

        if (login || session.MentorIdentity != role)
        {
            NotifyMentorData payload = BuildSnapshot(session, role);
            if (login)
                session.SendPush(payload);
            else
                session.SendPush("NotifyMentorChangeData", MessagePackSerializer.Serialize(payload));
            session.MentorIdentity = role;
        }
        else if (role == Student)
        {
            MentorProgressState state = session.player.Mentor;
            if (newWeek)
                session.SendPush(new NotifyMentorMemberWeeklyReset { WeeklyLevel = state.WeeklyLevel });
            if (newDay)
                session.SendPush(new NotifyStudentDailyReset { SystemTask = DailyTasks(session, state) });
            if (countLogin && !newWeek)
                SendWeeklyTasks(session, previous);
        }
        if (graduation is not null)
        {
            graduation.SendPushes(session);
            session.SendPush(new NotifyIGraduate { RewardGoodsList = graduation.RewardGoods });
        }
    }

    private static NotifyMentorData BuildSnapshot(Session session, int role)
    {
        PlayerData player = session.player.PlayerData;
        NotifyMentorData payload = new()
        {
            PlayerType = role, Teacher = new(), Tag = [], OnlineTag = [], Announcement = string.Empty
        };
        if (role == Teacher)
        {
            payload.Teacher.PlayerId = checked((int)player.Id);
            payload.Teacher.PlayerName = player.Name;
            payload.Teacher.Level = checked((int)player.Level);
            payload.Teacher.HeadPortraitId = checked((int)player.CurrHeadPortraitId);
            payload.Teacher.HeadFrameId = checked((int)player.CurrHeadFrameId);
            payload.Teacher.IsOnline = true;
        }
        if (role == Student)
        {
            MentorProgressState state = session.player.Mentor;
            if (HasTemporaryInstructor(session.player))
                payload.Teacher = TemporaryInstructor(session.player);
            payload.Students = [BuildLearner(session)];
            payload.WeeklyLevel = state.WeeklyLevel;
            payload.WeeklyTaskCompleteCount = CompletedCount(state);
            payload.WeeklyTaskReward = Rewards(state.WeeklyLevel).Where(row => HasClaimed(session, state.Week, row.Count))
                .Select(row => row.Count).ToList();
        }
        return payload;
    }

    private static MentorStudent BuildLearner(Session session)
    {
        PlayerData player = session.player.PlayerData;
        MentorProgressState state = session.player.Mentor;
        return new()
        {
            PlayerId = checked((uint)player.Id), PlayerName = player.Name, Level = checked((int)player.Level),
            HeadPortraitId = checked((uint)player.CurrHeadPortraitId), HeadFrameId = checked((int)player.CurrHeadFrameId),
            IsOnline = true, SystemTask = DailyTasks(session, state), WeeklyTask = WeeklyTasks(session, state),
            SendGiftCount = session.player.TemporaryInstructor.SentGift ? 1 : 0
        };
    }

    private static int Progress(MentorProgressState state, MentorAcceptedTask task) =>
        Objectives.Value[task.TaskId].Condition.Type == 48002 ? state.LoginDays : task.Progress;

    private static int CompletedCount(MentorProgressState state) =>
        state.Tasks.Count(task => Progress(state, task) >= Objectives.Value[task.TaskId].Target);

    private static MentorTask ToTask(Session session, MentorProgressState state, int id, MentorAcceptedTask? task)
    {
        Objective objective = Objectives.Value[id];
        int value = task is null ? 0 : Math.Min(Progress(state, task), objective.Target);
        int status = task is null ? Available : value >= objective.Target ? Completed : Accepted;
        // The temporary instructor has no online session. Publish an available gift with the completion update.
        if (status == Completed && HasTemporaryInstructor(session.player))
        {
            if (HasRewardReceipt(session, GiftClaimKey(state.Week, id)))
                status = GiftReceived;
            else if (HasGiftAllowance(session, state))
                status = GiftAvailable;
        }
        return new()
        {
            TaskId = checked((uint)id), Status = status, State = status >= Completed ? 3 : 1,
            RewardId = status is GiftAvailable or GiftReceived ? Rules.ActivationRewardId : 0,
            Schedule = [new() { Id = checked((uint)objective.Condition.Id), Value = value }]
        };
    }

    private static List<MentorTask> DailyTasks(Session session, MentorProgressState state)
    {
        if (state.DailyOffers.Count == 0 && state.Tasks.Count > 0)
            // The stock main page needs a nonempty SystemTask list to open the weekly rewards.
            return [ToTask(session, state, state.Tasks[0].TaskId, state.Tasks[0])];
        return state.DailyOffers.Select(id => ToTask(session, state, id, state.Tasks.Find(task => task.TaskId == id))).ToList();
    }

    private static List<MentorTask> WeeklyTasks(Session session, MentorProgressState state) =>
        state.Tasks.Select(task => ToTask(session, state, task.TaskId, task)).ToList();

    internal static void SendWeeklyTasks(Session session, MentorProgressState previous)
    {
        MentorProgressState state = session.player.Mentor;
        int count = CompletedCount(state);
        if (count != CompletedCount(previous))
            session.SendPush(new NotifyStudentWeeklyTaskProgress { WeeklyTaskCompleteCount = count });
        session.SendPush(new NotifyStudentWeeklyTaskChange { WeeklyTask = WeeklyTasks(session, state) });
    }

    internal static void RecordStageClear(Session session, int stageId, int stageType, int count, int serum, bool firstClear) =>
        SaveGameplayProgress(session, BuildProgress(session, condition => condition.Type switch
        {
            11202 => serum,
            15202 when condition.Params.Skip(1).Contains(stageType) => count,
            25005 when firstClear && BossModule.IsStage((uint)stageId) => 1,
            _ => 0
        }));

    internal static void SaveArenaProgress(Session session, bool firstClear) =>
        SaveGameplayProgress(session, firstClear ? BuildProgress(session, condition => condition.Type == 28005 ? 1 : 0) : null);

    internal static MentorProgressState? PrepareSerumProgress(Session session, int serum) =>
        serum > 0 ? BuildProgress(session, condition => condition.Type == 11202 ? serum : 0) : null;

    private static MentorProgressState? BuildProgress(Session session, Func<ConditionTable, int> amount)
    {
        MentorProgressState previous = session.player.Mentor;
        if (previous.Tasks.Count == 0 || previous.Week != TaskModule.CurrentWeeklyResetPeriod(Clock()) || ReadRules() is null)
            return null;
        MentorProgressState? updated = null;
        for (int i = 0; i < previous.Tasks.Count; i++)
        {
            MentorAcceptedTask task = previous.Tasks[i];
            Objective objective = Objectives.Value[task.TaskId];
            int increment = Math.Min(objective.Target - task.Progress, amount(objective.Condition));
            if (increment > 0)
                (updated ??= previous.Copy()).Tasks[i] = task with { Progress = task.Progress + increment };
        }
        return updated;
    }

    private static void SaveGameplayProgress(Session session, MentorProgressState? updated)
    {
        if (updated is null)
        {
            session.player.Save();
            return;
        }
        MentorProgressState previous = session.player.Mentor;
        try { Save(session.player, updated); }
        catch
        {
            session.DisconnectProtocol(persistState: false);
            throw;
        }
        SendWeeklyTasks(session, previous);
    }

    [RequestPacketHandler(nameof(StudentReceiveDailyTaskRequest))]
    public static void ReceiveTask(Session session, Packet.Request packet)
    {
        int id = packet.Deserialize<StudentReceiveDailyTaskRequest>().TaskId;
        Refresh(session);
        MentorProgressState state = session.player.Mentor;
        int code = session.MentorIdentity != Student ? NoAuthority
            : state.Tasks.Any(task => task.TaskId == id) ? AlreadyAccepted
            : !state.DailyOffers.Contains(id) ? TaskNotFound
            : state.Tasks.Count >= Rules.WeeklyAcceptLimit ? WeeklyLimitReached
            : state.Tasks.Count(task => task.AcceptedDay == state.Day) >= Rules.DailyAcceptLimit ? DailyLimitReached
            : 0;
        if (code == 0)
        {
            MentorProgressState updated = state.Copy();
            updated.Tasks.Add(new(id, state.Day, 0));
            Save(session.player, updated);
            session.SendPush(new NotifyStudentDailyTaskChange { SystemTask = DailyTasks(session, updated) });
            SendWeeklyTasks(session, state);
        }
        session.SendResponse(new StudentReceiveDailyTaskResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler(nameof(StudentDeleteDailyTaskRequest))]
    public static void DeleteTask(Session session, Packet.Request packet)
    {
        int id = packet.Deserialize<StudentDeleteDailyTaskRequest>().TaskId;
        Refresh(session);
        MentorProgressState state = session.player.Mentor;
        MentorAcceptedTask? task = state.Tasks.Find(task => task.TaskId == id);
        int code = session.MentorIdentity != Student ? NoAuthority
            : task is null ? TaskNotFound
            : Progress(state, task) >= Objectives.Value[id].Target ? AlreadyCompleted
            : 0;
        if (code == 0)
        {
            MentorProgressState updated = state.Copy();
            updated.Tasks.Remove(task!);
            Save(session.player, updated);
            session.SendPush(new NotifyStudentDailyTaskChange { SystemTask = DailyTasks(session, updated) });
            SendWeeklyTasks(session, state);
        }
        session.SendResponse(new StudentDeleteDailyTaskResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler(nameof(StudentGetTaskProgressRewardRequest))]
    public static void ClaimMilestone(Session session, Packet.Request packet)
    {
        int index = packet.Deserialize<StudentGetTaskProgressRewardRequest>().Index;
        session.RecoverPendingCommandantLevel();
        Refresh(session);
        MentorProgressState state = session.player.Mentor;
        StudentGetTaskProgressRewardResponse response = new();
        if (session.MentorIdentity != Student)
            response.Code = NoAuthority;
        else if (Rewards(state.WeeklyLevel).FirstOrDefault(row => row.Count == index) is not { } row
            || index > CompletedCount(state))
            response.Code = RewardNotReady;
        else if (HasClaimed(session, state.Week, index))
            response.Code = RewardAlreadyClaimed;
        else
        {
            var result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(ClaimKey(state.Week, index), RewardHandler.GetRewardGoods(row.RewardId))
                    { UpdateCommandantLevel = true }], session);
            if (session.CommitPendingCommandantLevel())
                session.SendPush(new NotifyPlayerLevel { Level = (int)session.player.PlayerData.Level });
            result.SendPushes(session);
            response.RewardGoodsList = result.RewardGoods;
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler("MentorRefreshGraduateTaskRequest")]
    public static void RefreshGraduateTasks(Session session, Packet.Request packet) =>
        session.SendResponse(new MentorRefreshGraduateTaskResponse
        {
            Code = session.MentorIdentity == Teacher ? 0 : 20106048
        }, packet.Id);
}
