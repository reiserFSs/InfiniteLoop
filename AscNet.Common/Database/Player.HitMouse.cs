using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace AscNet.Common.Database;

public sealed class HitMouseState
{
    [BsonElement("activity_id")]
    public int ActivityId { get; set; }

    [BsonElement("level_scores")]
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)]
    public Dictionary<int, int> LevelScores { get; set; } = new();

    [BsonElement("claimed_reward_indices")]
    public List<int> ClaimedRewardIndices { get; set; } = new();
}

public partial class Player
{
    [BsonElement("hit_mouse")]
    public HitMouseState? HitMouse { get; set; }
}
