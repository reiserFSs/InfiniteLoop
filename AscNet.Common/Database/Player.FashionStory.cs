using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class FashionStoryState
{
    [BsonElement("activity_id")]
    public int AuthorizedActivityId { get; set; }

    [BsonElement("time_ids")]
    public List<int> AuthorizedTimeIds { get; set; } = new();
}

public partial class Player
{
    [BsonElement("fashion_story")]
    public FashionStoryState? FashionStory { get; set; }
}
