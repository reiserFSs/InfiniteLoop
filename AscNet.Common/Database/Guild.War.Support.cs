using AscNet.Common.MsgPack;
using MongoDB.Bson.Serialization.Attributes;

namespace AscNet.Common.Database;

public sealed partial class GuildWarParticipation
{
    public int SupportCharacterId { get; set; }
    public long SupportLastRecvTime { get; set; }
    public List<GuildWarAssistTime> SupportTimes { get; set; } = [];
    public List<GuildWarAssistLog> SupportLogs { get; set; } = [];
    public List<GuildWarSupportUse> SupportUses { get; set; } = [];
}
public sealed class GuildWarSupportUse
{
    public long PlayerId { get; set; }
    public int CharacterId { get; set; }
    public long LastUseTime { get; set; }
}
public sealed partial class GuildWarPlayerRound
{
    public int SupportSupply { get; set; }
    public int ReceivedAssistSupply { get; set; }
    public int ReceivedTimeSupply { get; set; }
    public GuildWarTeamInfo? TeamInfo { get; set; }
    public List<GuildWarTeamInfo> HideAreaTeams { get; set; } = [];
    public List<GuildWarTeamInfo> LastHideAreaTeams { get; set; } = [];
    public long HiddenPoint { get; set; }
}
