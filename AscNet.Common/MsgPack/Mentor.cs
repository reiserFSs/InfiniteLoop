using MessagePack;
using MentorStudent = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataStudent;
using MentorTask = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataStudent.NotifyMentorDataStudentSystemTask;
using MentorTeacher = AscNet.Common.MsgPack.NotifyMentorData.NotifyMentorDataTeacher;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class StudentReceiveDailyTaskRequest
{
    public int TaskId { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentReceiveDailyTaskResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentDeleteDailyTaskRequest
{
    public int TaskId { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentDeleteDailyTaskResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentGetTaskProgressRewardRequest
{
    public int Index { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentGetTaskProgressRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyStudentDailyTaskChange
{
    public List<MentorTask> SystemTask { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyStudentWeeklyTaskChange
{
    public List<MentorTask> WeeklyTask { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyStudentWeeklyTaskProgress
{
    public int WeeklyTaskCompleteCount { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyStudentDailyReset
{
    public List<MentorTask> SystemTask { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyMentorMemberWeeklyReset
{
    public int WeeklyLevel { get; set; }
}

[MessagePackObject(true)]
public sealed class GetMentorRecommendPlayerListRequest { }

[MessagePackObject(true)]
public sealed class GetMentorRecommendPlayerListResponse
{
    public int Code { get; set; }
    public List<MentorTeacher> RecommendList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class GetMentorSpecifyPlayerInfoRequest
{
    public long Id { get; set; }
}

[MessagePackObject(true)]
public sealed class GetMentorSpecifyPlayerInfoResponse
{
    public int Code { get; set; }
    public MentorTeacher? PlayerInfo { get; set; }
}

[MessagePackObject(true)]
public sealed class ApplyMentorRequest
{
    public List<long> TargetPlayerId { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class ApplyMentorResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class TickMentorRequest
{
    public long TargetPlayerId { get; set; }
}

[MessagePackObject(true)]
public sealed class TickMentorResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyAddTeacher
{
    public MentorTeacher Teacher { get; set; } = new();
    public List<MentorStudent> Students { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyDoTickMentor { }

[MessagePackObject(true)]
public sealed class MentorGraduateRequest { }

[MessagePackObject(true)]
public sealed class MentorGraduateResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class NotifyIGraduate
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class StudentReceiveEquipRequest
{
    public int TaskId { get; set; }
}

[MessagePackObject(true)]
public sealed class StudentReceiveEquipResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class MentorStudentSendRewardRequest
{
    public long MentorId { get; set; }
}

[MessagePackObject(true)]
public sealed class MentorStudentSendRewardResponse
{
    public int Code { get; set; }
}

[MessagePackObject(true)]
public sealed class MentorGetNameListRequest { }

[MessagePackObject(true)]
public sealed class MentorGetNameListResponse
{
    public int Code { get; set; }
    public MentorNameList StudentNameList { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class MentorNameList
{
    public MentorTeacher? MyTeacher { get; set; }
    public List<MentorStudent> MySchoolmate { get; set; } = [];
    public List<MentorStudent> MyStudents { get; set; } = [];
}

[MessagePackObject(true)]
public sealed class MentorRefreshGraduateTaskResponse
{
    public int Code { get; set; }
    public List<MentorStudent> Students { get; set; } = [];
}
