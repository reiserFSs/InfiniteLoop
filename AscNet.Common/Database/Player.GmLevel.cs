using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public enum GmLevelSyncMode
{
    Notify,
    Relog
}

public partial class Player
{
    [BsonElement("gm_level_sync")]
    [BsonIgnoreIfNull]
    public GmLevelSyncMode? GmLevelSync { get; set; }
}
