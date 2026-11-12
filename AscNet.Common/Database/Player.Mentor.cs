using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("mentor")]
    public MentorProgressState Mentor { get; set; } = new();

    [BsonElement("temporary_instructor")]
    public TemporaryInstructorState TemporaryInstructor { get; set; } = new();
}

public sealed record TemporaryInstructorState(long JoinedAt = 0, long GraduatedAt = 0, bool SentGift = false);

public sealed class MentorProgressState
{
    public long Week { get; set; }
    public long Day { get; set; }
    public int WeeklyLevel { get; set; }
    public long LastLoginDay { get; set; }
    public int LoginDays { get; set; }
    public List<int> DailyOffers { get; set; } = [];
    public List<MentorAcceptedTask> Tasks { get; set; } = [];

    public MentorProgressState Copy() => new()
    {
        Week = Week,
        Day = Day,
        WeeklyLevel = WeeklyLevel,
        LastLoginDay = LastLoginDay,
        LoginDays = LoginDays,
        DailyOffers = [.. DailyOffers],
        Tasks = [.. Tasks]
    };
}

public sealed record MentorAcceptedTask(int TaskId, long AcceptedDay, int Progress);
