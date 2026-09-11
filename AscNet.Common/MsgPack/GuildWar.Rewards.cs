using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildWarOpenRankRequest { public int RankType { get; set; } public int Uid { get; set; } }
[MessagePackObject(true)]
public sealed class GuildWarOpenRankResponse : GuildResponse
{
    public List<GuildWarRankInfo> RankList { get; set; } = [];
    public GuildWarRankInfo MyRankInfo { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class GuildWarRankInfo
{
    public long Uid { get; set; }
    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public long HeadPortraitId { get; set; }
    public long HeadFrameId { get; set; }
    public long Point { get; set; }
    public int Activation { get; set; }
    public int Rank { get; set; }
    public int MemberCount { get; set; }
    public int DragonRageLevel { get; set; }
    public List<GuildWarHideAreaRankMeta> HideAreaMetas { get; set; } = [];
    public List<int> TeamLeaders { get; set; } = [];
    [IgnoreMember] public long FirstPointAt { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarHideAreaRankMeta
{
    public int NodeId { get; set; }
    public long Point { get; set; }
    public List<GuildWarHideAreaCharacterMeta> Characters { get; set; } = [];
}
[MessagePackObject(true)]
public sealed class GuildWarHideAreaCharacterMeta
{
    public int PlayerId { get; set; }
    public int IsAssist { get; set; }
}
[MessagePackObject(true)]
public sealed class GuildWarPopupRequest { }
[MessagePackObject(true)]
public sealed class GuildWarPopupResponse : GuildResponse
{
    public NotifyGuildWarActivityData.NotifyGuildWarActivityDataPopupRecord PopupRecord { get; set; } = new();
}
[MessagePackObject(true)]
public sealed class GuildWarPopupActionRequest { public List<int> ActionPlayed { get; set; } = []; }
[MessagePackObject(true)]
public sealed class GuildWarPopupActionResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class XGuildWarGetBossRewardRequest { public int Id { get; set; } public int NodeUid { get; set; } }
[MessagePackObject(true)]
public sealed class XGuildWarGetBossRewardResponse : GuildResponse { public List<RewardGoods> RewardGoodsList { get; set; } = []; }

[MessagePackObject(true)]
public sealed class NotifyGuildWarRoundSettle { }
public partial class NotifyGuildWarActivityData
{
    public partial class NotifyGuildWarActivityDataMyRoundData
    {
        public List<int> GetBossRewards { get; set; } = [];
    }
}
