using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

[BsonIgnoreExtraElements]
public sealed class CourseStageState
{
    [BsonElement("id")] public int Id { get; set; }
    [BsonElement("stars_flag")] public int StarsFlag { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class CourseState
{
    [BsonElement("stages")] public List<CourseStageState> Stages { get; set; } = new();
    [BsonElement("max_total_lesson_point")] public int MaxTotalLessonPoint { get; set; }
    [BsonElement("reward_ids")] public List<int> RewardIds { get; set; } = new();
    [BsonElement("pending_result")] public CourseStageState? PendingResult { get; set; }
}

public partial class Player
{
    [BsonElement("course")] public CourseState Course { get; set; } = new();
}
