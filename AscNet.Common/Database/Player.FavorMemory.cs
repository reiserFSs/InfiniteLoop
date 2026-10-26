using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

[BsonIgnoreExtraElements]
public sealed class StageMemoryClaimState
{
    [BsonElement("activity_id")] public int ActivityId { get; set; }
    /// <summary>1-based indices into StageMemoryActivity.RewardIds already claimed.</summary>
    [BsonElement("indices")] public List<int> Indices { get; set; } = new();
}

public partial class Player
{
    /// <summary>ViewBirthdaySingleStoryRequest: SingleStory ids already viewed ("invited").</summary>
    [BsonElement("birthday_single_story_ids")] public List<int> BirthdaySingleStoryIds { get; set; } = new();
    [BsonElement("stage_memory_claims")] public List<StageMemoryClaimState> StageMemoryClaims { get; set; } = new();
    /// <summary>BoardEffectTriggerRequest: unix seconds of the last accepted trigger.</summary>
    [BsonElement("board_effect_last_trigger")] public long BoardEffectLastTriggerTime { get; set; }
}
