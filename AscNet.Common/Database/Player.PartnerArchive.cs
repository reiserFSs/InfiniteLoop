using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    [BsonElement("archive_partner_unlock_ids")]
    public List<int> ArchivePartnerUnlockIds { get; set; } = new();

    [BsonElement("archive_partner_settings")]
    public List<int> ArchivePartnerSettings { get; set; } = new();
}
