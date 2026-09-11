using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildWarTeamCharacterInfo
{
    public int Id { get; set; }
    public long PlayerId { get; set; }
    public int RobotId { get; set; }
    public int Pos { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarTeamInfo
{
    public int NodeId { get; set; }
    public int CaptainPos { get; set; }
    public int FirstFightPos { get; set; }
    public List<GuildWarTeamCharacterInfo> CharacterInfos { get; set; } = [];
    public long CurPoint { get; set; }
    public long LastPoint { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarAssistRecord
{
    public int RoundId { get; set; }
    public int AssistSupply { get; set; }
    public int TimeSupply { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarAssistTime
{
    public long AssistTime { get; set; }
    public long EndTime { get; set; }
    [IgnoreMember]
    public long ClaimedThrough { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarAssistLog
{
    public long UserId { get; set; }
    public long UserTime { get; set; }
    public int Supply { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarSupportDetail
{
    public int CharacterId { get; set; }
    public int SupportSupply { get; set; }
    public List<GuildWarAssistRecord> ToAssistRecords { get; set; } = [];
    public List<GuildWarAssistRecord> GetAssistRecords { get; set; } = [];
    public List<GuildWarAssistTime> MyAssistRecords { get; set; } = [];
    public List<GuildWarAssistLog> MyLogs { get; set; } = [];
    public long LastRecvTime { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarFightNpcData
{
    public CharacterData Character { get; set; } = new();
    public List<EquipData> Equips { get; set; } = [];
    public PartnerData? Partner { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarAssistCharacter
{
    public long PlayerId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public long LastUseTime { get; set; }
    public GuildWarFightNpcData FightNpcData { get; set; } = new();
}
[MessagePackObject(true)] public sealed class GuildWarOpenSupportPanelRequest { }
[MessagePackObject(true)] public sealed class GuildWarOpenSupportPanelResponse : GuildResponse { public GuildWarSupportDetail SupportDetail { get; set; } = new(); }
[MessagePackObject(true)] public sealed class GuildWarEndSupportRequest { public int CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarEndSupportResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarSupportCharacterRequest { public int CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarSupportCharacterResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarAssistCharacterListRequest { }
[MessagePackObject(true)] public sealed class GuildWarAssistCharacterListResponse : GuildResponse { public List<GuildWarAssistCharacter> CharacterList { get; set; } = []; }
[MessagePackObject(true)] public sealed class GuildWarSetTeamRequest { public GuildWarTeamInfo TeamInfo { get; set; } = new(); }
[MessagePackObject(true)] public sealed class GuildWarSetTeamResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarReceivedSupportRequest { }
[MessagePackObject(true)] public sealed class GuildWarReceivedSupportResponse : GuildResponse { public int TotalSupply { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarSetHideAreaTeamRequest { public List<GuildWarTeamInfo> TeamInfos { get; set; } = []; }
[MessagePackObject(true)] public sealed class GuildWarSetHideAreaTeamResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarResetHideAreaTeamRequest { public int NodeId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWarResetHideAreaTeamResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildWarUploadHideNodePointRequest { }
[MessagePackObject(true)] public sealed class GuildWarUploadHideNodePointResponse : GuildResponse { }
public partial class NotifyGuildWarActivityData
{
    public List<GuildWarTeamInfo> HideAreaTeamInfos { get; set; } = [];
    public List<GuildWarTeamInfo> LastHideAreaTeamInfos { get; set; } = [];
}
