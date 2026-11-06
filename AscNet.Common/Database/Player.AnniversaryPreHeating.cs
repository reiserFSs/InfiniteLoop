using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("anniversary_preheating")]
    public AnniversaryPreHeatingState AnniversaryPreHeating { get; set; } = new();
}

/// <summary>Durable 4.9 Time Trace (Anniversary pre-heating) state: per-group read contents and the one-time reward flag.</summary>
public sealed class AnniversaryPreHeatingState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("groups")]
    public List<AnniversaryPreHeatingGroupState> Groups { get; set; } = new();
}

public sealed class AnniversaryPreHeatingGroupState
{
    [BsonElement("group_id")]
    public int GroupId { get; set; }

    [BsonElement("read_content_ids")]
    public List<int> ReadContentIds { get; set; } = new();

    [BsonElement("reward_received")]
    public bool RewardReceived { get; set; }
}
