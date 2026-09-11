using MessagePack;

namespace AscNet.Common.Database;

public sealed partial class Guild
{
    public List<string> ChatHistory { get; set; } = [];
    public List<GuildNewsEntry> News { get; set; } = [];
    public long NextSocialSequence { get; set; }
    public long ContributionRankEnteredAt { get; set; }
}

[MessagePackObject(true)]
public sealed class GuildNewsEntry
{
    public long Id { get; set; }
    public int MsgId { get; set; }
    public long Time { get; set; }
    public List<string> Params { get; set; } = [];
}
