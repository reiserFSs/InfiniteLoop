using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class CourseChapterData
{
    public int Id { get; set; }
    public bool IsClear { get; set; }
    public int TotalPoint { get; set; }
}

[MessagePackObject(true)]
public sealed class CourseStageData
{
    public int Id { get; set; }
    public int StarsFlag { get; set; }
}

[MessagePackObject(true)] public sealed class CourseSaveResultRequest { }
[MessagePackObject(true)]
public sealed class CourseSaveResultResponse
{
    public int Code { get; set; }
    public CourseChapterData? ChapterData { get; set; }
    public CourseStageData? StageData { get; set; }
    public int TotalLessonPoint { get; set; }
    public int MaxTotalLessonPoint { get; set; }
}

[MessagePackObject(true)]
public sealed class CourseGetRewardRequest
{
    public List<int>? RewardIds { get; set; }
}

[MessagePackObject(true)]
public sealed class CourseGetRewardResponse
{
    public int Code { get; set; }
    public List<RewardGoods> RewardGoodsList { get; set; } = new();
    public List<int> SuccessRewardIds { get; set; } = new();
}
