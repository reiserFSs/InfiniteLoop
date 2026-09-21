using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed class PartnerDecomposePendingOperation
{
    public string ClaimKey { get; set; } = string.Empty;
    public List<int> PartnerIds { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}

public partial class Player
{
    [BsonElement("pending_partner_decompose")]
    [BsonIgnoreIfNull]
    public PartnerDecomposePendingOperation? PendingPartnerDecompose { get; set; }

    [BsonElement("archive_partner_unlock_ids")]
    public List<int> ArchivePartnerUnlockIds { get; set; } = new();

    [BsonElement("archive_partner_settings")]
    public List<int> ArchivePartnerSettings { get; set; } = new();
}
