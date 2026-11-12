using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.headportrait;
using MentorTeacher = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataTeacher;

namespace AscNet.GameServer.Handlers;

internal static partial class MentorModule
{
    // AscNet's solo recruitment option uses the stock temporary instructor's place in the UI.
    // This id is reserved for guidance packets and never used to resolve a player account.
    internal const int TemporaryInstructorId = int.MaxValue;
    private const int GiftAvailable = 4, GiftReceived = 5;
    private const int MentorNotFound = 20106002, AlreadyBound = 20106028;
    private const int GraduationNotReady = 20106038, AlreadyGraduated = 20106049;
    private const int GiftUnavailable = 20106061, GiftAlreadySent = 20106078;
    private const string GraduationClaim = "mentor-temporary-instructor-graduation";
    private static readonly Lazy<int> InstructorPortrait = new(() => TableReaderV2.Parse<HeadPortraitTable>()
        .Where(head => head is { IsInit: 1, Type: 1 }).MaxBy(head => head.Priority)!.Id);

    private static bool HasTemporaryInstructor(Player player) =>
        player.TemporaryInstructor is { JoinedAt: > 0, GraduatedAt: 0 };

    private static MentorTeacher TemporaryInstructor(Player player)
    {
        string name = player.ClientRegion switch
        {
            "cn" => "法奥斯临时教官",
            "tw" => "法奧斯臨時教官",
            _ => "F.O.S. Temporary Instructor"
        };
        return new()
        {
            PlayerId = TemporaryInstructorId, PlayerName = name, HeadPortraitId = InstructorPortrait.Value,
            Level = Config.Value["BeMentorLv"], Announcement = string.Empty,
            JoinTime = checked((int)player.TemporaryInstructor.JoinedAt),
            Tag = Array.Empty<int>(), OnlineTag = Array.Empty<int>()
        };
    }

    private static bool CanBindTemporaryInstructor(Session session) =>
        session.MentorIdentity == Student && session.player.PlayerData.Level < Config.Value["GraduateLv"]
        && session.player.TemporaryInstructor.GraduatedAt == 0;

    private static void SaveInstructor(Player player, TemporaryInstructorState state)
    {
        TemporaryInstructorState previous = player.TemporaryInstructor;
        player.TemporaryInstructor = state;
        try { player.SaveChecked(); }
        catch
        {
            player.TemporaryInstructor = previous;
            throw;
        }
    }

    [RequestPacketHandler(nameof(GetMentorRecommendPlayerListRequest))]
    public static void GetRecommendations(Session session, Packet.Request packet)
    {
        Refresh(session);
        session.SendResponse(new GetMentorRecommendPlayerListResponse
        {
            RecommendList = CanBindTemporaryInstructor(session) && !HasTemporaryInstructor(session.player)
                ? [TemporaryInstructor(session.player)] : []
        }, packet.Id);
    }

    [RequestPacketHandler(nameof(GetMentorSpecifyPlayerInfoRequest))]
    public static void SearchTemporaryInstructor(Session session, Packet.Request packet)
    {
        long id = packet.Deserialize<GetMentorSpecifyPlayerInfoRequest>().Id;
        Refresh(session);
        bool found = id == TemporaryInstructorId && CanBindTemporaryInstructor(session);
        session.SendResponse(new GetMentorSpecifyPlayerInfoResponse
        {
            Code = found ? 0 : MentorNotFound, PlayerInfo = found ? TemporaryInstructor(session.player) : null
        }, packet.Id);
    }

    [RequestPacketHandler(nameof(ApplyMentorRequest))]
    public static void BindTemporaryInstructor(Session session, Packet.Request packet)
    {
        var request = packet.Deserialize<ApplyMentorRequest>();
        Refresh(session);
        bool alreadyBound = HasTemporaryInstructor(session.player);
        int code = request.TargetPlayerId is not [TemporaryInstructorId] ? MentorNotFound
            : alreadyBound ? AlreadyBound
            : CanBindTemporaryInstructor(session) ? 0 : NoAuthority;
        if (code == 0)
        {
            SaveInstructor(session.player, session.player.TemporaryInstructor with { JoinedAt = Clock() });
            session.SendPush(new NotifyAddTeacher
            {
                Teacher = TemporaryInstructor(session.player), Students = [BuildLearner(session)]
            });
        }
        session.SendResponse(new ApplyMentorResponse { Code = code }, packet.Id);
    }

    [RequestPacketHandler(nameof(TickMentorRequest))]
    public static void UnbindTemporaryInstructor(Session session, Packet.Request packet)
    {
        long id = packet.Deserialize<TickMentorRequest>().TargetPlayerId;
        Refresh(session);
        bool bound = id == TemporaryInstructorId && HasTemporaryInstructor(session.player);
        if (bound)
        {
            SaveInstructor(session.player, session.player.TemporaryInstructor with { JoinedAt = 0 });
            session.SendPush(new NotifyDoTickMentor());
            var state = session.player.Mentor;
            SendWeeklyTasks(session, state);
        }
        session.SendResponse(new TickMentorResponse { Code = bound ? 0 : MentorNotFound }, packet.Id);
    }

    [RequestPacketHandler(nameof(MentorGetNameListRequest))]
    public static void GetNames(Session session, Packet.Request packet)
    {
        Refresh(session);
        var self = BuildLearner(session);
        self.IsGraduate = session.player.TemporaryInstructor.GraduatedAt > 0;
        session.SendResponse(new MentorGetNameListResponse
        {
            StudentNameList = new()
            {
                MyTeacher = session.player.TemporaryInstructor.JoinedAt > 0 ? TemporaryInstructor(session.player) : null,
                MySchoolmate = [self]
            }
        }, packet.Id);
    }

    private static string GiftClaimKey(long week, int taskId) => $"mentor-temporary-gift:{week}:{taskId}";

    // AscNet policy: the temporary instructor gives authored guidance gifts for completed objectives.
    private static bool HasGiftAllowance(Session session, MentorProgressState state) =>
        state.Tasks.Count(task => HasRewardReceipt(session, GiftClaimKey(state.Week, task.TaskId)))
            < Config.Value["MentorGiveEquipCount"];

    [RequestPacketHandler(nameof(StudentReceiveEquipRequest))]
    public static void ClaimTemporaryInstructorGift(Session session, Packet.Request packet)
    {
        int id = packet.Deserialize<StudentReceiveEquipRequest>().TaskId;
        Refresh(session);
        MentorProgressState state = session.player.Mentor;
        MentorAcceptedTask? task = state.Tasks.Find(task => task.TaskId == id);
        string key = GiftClaimKey(state.Week, id);
        StudentReceiveEquipResponse response = new();
        if (!HasTemporaryInstructor(session.player))
            response.Code = NoAuthority;
        else if (task is null)
            response.Code = TaskNotFound;
        else if (Progress(state, task) < Objectives.Value[id].Target || HasRewardReceipt(session, key)
            || !HasGiftAllowance(session, state))
            response.Code = GiftUnavailable;
        else
        {
            RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(
                [new RewardGrant(key, RewardHandler.GetRewardGoods(Config.Value["ActivationRewardId"]))], session);
            result.SendPushes(session);
            response.RewardGoodsList = result.RewardGoods;
            SendWeeklyTasks(session, state);
        }
        session.SendResponse(response, packet.Id);
    }

    [RequestPacketHandler(nameof(MentorStudentSendRewardRequest))]
    public static void SendThankYouGift(Session session, Packet.Request packet)
    {
        long id = packet.Deserialize<MentorStudentSendRewardRequest>().MentorId;
        Refresh(session);
        TemporaryInstructorState instructor = session.player.TemporaryInstructor;
        int code = id != TemporaryInstructorId || !HasTemporaryInstructor(session.player) ? MentorNotFound
            : session.player.PlayerData.Level < Config.Value["GraduateLv"] ? GraduationNotReady
            : instructor.SentGift ? GiftAlreadySent : 0;
        if (code == 0)
            SaveInstructor(session.player, instructor with { SentGift = true });
        session.SendResponse(new MentorStudentSendRewardResponse { Code = code }, packet.Id);
    }

    private static RewardApplicationResult Graduate(Session session, long now)
    {
        MentorProgressState state = session.player.Mentor;
        List<RewardGrant> grants = [new(GraduationClaim, RewardHandler.GetRewardGoods(Config.Value["GraduateRewardId"]))];
        if (state.Week == TaskModule.CurrentWeeklyResetPeriod(now))
        {
            int complete = CompletedCount(state);
            grants.AddRange(Rewards(state.WeeklyLevel)
                .Where(row => row.Count <= complete && !HasClaimed(session, state.Week, row.Count))
                .Select(row => new RewardGrant(ClaimKey(state.Week, row.Count), RewardHandler.GetRewardGoods(row.RewardId))
                    { UpdateCommandantLevel = true }));
        }
        RewardApplicationResult result = RewardHandler.ApplyRewardsOnceAndPersist(grants, session);
        if (session.CommitPendingCommandantLevel())
            session.SendPush(new NotifyPlayerLevel { Level = (int)session.player.PlayerData.Level });
        SaveInstructor(session.player, session.player.TemporaryInstructor with { GraduatedAt = now });
        return result;
    }

    [RequestPacketHandler(nameof(MentorGraduateRequest))]
    public static void GraduateTemporaryInstructor(Session session, Packet.Request packet)
    {
        MentorGraduateResponse response = new();
        if (session.player.TemporaryInstructor.GraduatedAt > 0)
            response.Code = AlreadyGraduated;
        else if (!HasTemporaryInstructor(session.player) || session.player.PlayerData.Level < Config.Value["GraduateLv"])
            response.Code = GraduationNotReady;
        else
        {
            RewardApplicationResult result = Graduate(session, Clock());
            result.SendPushes(session);
            response.RewardGoodsList = result.RewardGoods;
            Refresh(session);
        }
        session.SendResponse(response, packet.Id);
    }
}
