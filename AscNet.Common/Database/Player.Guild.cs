using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public partial class Player
{
    // Historical join/create event receipt; pending applications never change this field.
    [BsonElement("guild_progress_recorded_id")]
    public uint GuildProgressRecordedId { get; set; }

    [BsonElement("guild_state")]
    public GuildPlayerState GuildState { get; set; } = new();
}

public sealed partial class GuildPlayerState
{
    public long JoinCdEnd { get; set; }
    public long RequestEpoch { get; set; }
    public List<GuildRequestReceipt> RequestReceipts { get; set; } = [];
    public HashSet<string> AppliedOperationIds { get; set; } = [];
    public HashSet<string> AppliedRewardClaims { get; set; } = [];
}
