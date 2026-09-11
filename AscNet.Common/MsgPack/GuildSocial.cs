using AscNet.Common.Database;
using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildListNewsRequest { public int NewsType { get; set; } public int PageNo { get; set; } }
[MessagePackObject(true)]
public sealed class GuildListNewsResponse
{
    public int Code { get; set; }
    public int MaxPageNo { get; set; }
    public List<GuildNewsEntry> News { get; set; } = [];
}
[MessagePackObject(true)]
public sealed class GuildListRankRequest { public int Order { get; set; } }
[MessagePackObject(true)]
public sealed class GuildListRankResponse
{
    public int Code { get; set; }
    public List<GuildRankEntry> RankList { get; set; } = [];
    public double MyRankNum { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildRankEntry
{
    public uint GuildId { get; set; }
    public string GuildName { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int MemberCount { get; set; }
    public int Score { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildRecruitRecommendRequest { public int PageNo { get; set; } }
[MessagePackObject(true)]
public sealed class GuildRecruitRecommendResponse
{
    public int Code { get; set; }
    public List<GuildRecruitPlayer> RecommendData { get; set; } = [];
}
[MessagePackObject(true)]
public sealed class GuildRecruitPlayer
{
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Level { get; set; }
    public int HeadPortraitId { get; set; }
    public int HeadFrameId { get; set; }
    public long GuildCoin { get; set; }
    public long LastLoginTime { get; set; }
    public int OnlineFlag { get; set; }
}
