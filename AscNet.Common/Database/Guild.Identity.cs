using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    [BsonElement("rank_names")]
    public string RankNames { get; set; } = string.Empty;

    [BsonElement("notice")]
    public string Notice { get; set; } = string.Empty;

    // EN XGuildManager: granted only after a forced rename, not on creation.
    [BsonElement("free_change_guild_name_count")]
    public int FreeChangeGuildNameCount { get; set; }
}

public partial class GuildPlayerState
{
    // EN welcome choices are personal; selection/autochat remain client-owned.
    [BsonElement("scripts")]
    public List<string> Scripts { get; set; } = [];
}
