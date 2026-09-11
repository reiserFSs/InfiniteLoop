using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)] public sealed class GuildReleaseWishRequest { public int ItemId { get; set; } }
[MessagePackObject(true)] public sealed class GuildReleaseWishResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildListWishRequest { }
[MessagePackObject(true)] public sealed class GuildListWishResponse : GuildResponse
{
    public List<GuildWishData> WishesData { get; set; } = [];
    public int WishCount { get; set; }
    public int WishContributeCount { get; set; }
}
[MessagePackObject(true)] public sealed class GuildWishData
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RankLevel { get; set; }
    public int ItemId { get; set; }
    public int Seq { get; set; }
    public int GotCount { get; set; }
    public int MaxCount { get; set; }
}
[MessagePackObject(true)] public sealed class GuildWishContributeRequest { public long PlayerId { get; set; } public int Seq { get; set; } public int ItemId { get; set; } }
[MessagePackObject(true)] public sealed class GuildWishContributeResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildGiveLikeRequest { public long OtherId { get; set; } public List<int> ItemId { get; set; } = []; public List<int> ItemCount { get; set; } = []; }
[MessagePackObject(true)] public sealed class GuildGiveLikeResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildLevelUpResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildPayMaintainResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildGetGiftRequest { public int GiftLevel { get; set; } }
[MessagePackObject(true)] public sealed class GuildGetGiftResponse : GuildResponse { public List<int> GiftLevels { get; set; } = []; }
[MessagePackObject(true)] public sealed class GuildGetContributeRewardResponse : GuildResponse { public int AddGuildCoin { get; set; } }
[MessagePackObject(true)] public sealed class GuildBuyIconRequest { public int IconId { get; set; } }
[MessagePackObject(true)] public sealed class GuildBuyIconResponse : GuildResponse { }
[MessagePackObject(true)] public sealed class GuildListTalentResponse : GuildResponse { public int Point { get; set; } public Dictionary<int,int> Talents { get; set; } = []; }
[MessagePackObject(true)] public sealed class GuildUpgradeTalentRequest { public int Id { get; set; } }
[MessagePackObject(true)] public sealed class GuildUpgradeTalentResponse : GuildResponse { public int Point { get; set; } }
[MessagePackObject(true)] public sealed class NotifyGuildGoodsChange
{
    public int ShopCoin { get; set; }
    public List<int> HeadPortraits { get; set; } = [];
    public List<int> DormThemes { get; set; } = [];
    public List<int> DormBgms { get; set; } = [];
}
[MessagePackObject(true)] public sealed class NotifyGuildMaintain { public int MaintainState { get; set; } public long EmergenceTime { get; set; } }
[MessagePackObject(true)] public sealed class NotifyGuildShopCoinReachLimit { public int Code { get; set; } }
