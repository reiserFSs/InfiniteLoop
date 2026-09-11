using MessagePack;

namespace AscNet.Common.MsgPack;

[MessagePackObject(true)]
public sealed class GuildSignInfo
{
    public int Id { get; set; }
    public List<int> SignEventIds { get; set; } = [];
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
    public bool IsAward { get; set; }
}

[MessagePackObject(true)]
public sealed class NotifyGuildSignPlayerData
{
    public GuildSignInfo GuildSignInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildSignResponse : GuildResponse
{
    public GuildSignInfo GuildSignInfo { get; set; } = new();
}

[MessagePackObject(true)]
public sealed class GuildSignRewardResponse : GuildResponse
{
    public List<RewardGoods> RewardGoodsList { get; set; } = [];
}
