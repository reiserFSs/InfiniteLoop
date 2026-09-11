using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed partial class GuildPlayerState
{
    [BsonElement("sign_period")]
    public long SignPeriod { get; set; } = long.MinValue;

    [BsonElement("sign_guarantee_threshold")]
    public int SignGuaranteeThreshold { get; set; }

    [BsonElement("sign_guarantee_count")]
    public int SignGuaranteeCount { get; set; }

    [BsonElement("sign_info")]
    public GuildSignInfo SignInfo { get; set; } = new();
}
