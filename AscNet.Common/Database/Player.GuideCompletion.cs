using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class GuideCompletionEntry
{
    public int GuideId { get; set; }

    [BsonIgnoreIfNull]
    public List<GuideRewardGoods>? Goods { get; set; }
}

public sealed class GuideRewardGoods
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Count { get; set; }
}

public partial class Player
{
    // Frozen before any reward document can be changed. Finalized in the same write as GuideData.
    [BsonElement("pending_guide_completion")]
    [BsonIgnoreIfNull]
    public List<GuideCompletionEntry>? PendingGuideCompletion { get; set; }

    // Reconnect keeps the client's existing caches. Retain the ids and granted templates for replay.
    [BsonElement("guide_reconnect_sync")]
    [BsonIgnoreIfNull]
    public List<GuideCompletionEntry>? GuideReconnectSync { get; set; }
}
