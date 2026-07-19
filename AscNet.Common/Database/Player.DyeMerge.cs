using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("dye_merge")]
    public DyeMergeState DyeMerge { get; set; } = new();
}

public sealed class DyeMergeState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("completed_stage_ids")]
    public List<int> CompletedStageIds { get; set; } = new();
}
