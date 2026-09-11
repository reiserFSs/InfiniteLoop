using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("archive_unlocked_cgs")]
    public HashSet<int> ArchiveUnlockedCgs { get; set; } = new();
}
