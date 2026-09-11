using MessagePack;

namespace AscNet.Common.MsgPack;

public partial class NotifyGuildDormPlayerData
{
    public partial class NotifyGuildDormPlayerDataGuildDormData
    {
        public List<GuildDormPlayerFurnitureRandomBox> RandomBoxes { get; set; } = [];
        public List<long> OneTimeInteractReplyIds { get; set; } = [];
        public List<int> InteractedFurnitureIds { get; set; } = [];
    }
}

[MessagePackObject(true)]
public sealed class GuildDormPlayerFurnitureRandomBox { public int FurnitureId; public int RandomItemId; public int RandomTimes; }
[MessagePackObject(true)]
public sealed class GuildDormChannelData { public int ChannelId; public int MemberCount; }
[MessagePackObject(true)]
public sealed class GuildDormRoomChannelDataRequest { public int RoomId; }
[MessagePackObject(true)]
public sealed class GuildDormRoomChannelDataResponse : GuildResponse { public List<GuildDormChannelData> ChannelDatas = []; }
[MessagePackObject(true)]
public sealed class GuildDormPreEnterRequest { public int RoomId; public int ChannelId; }
[MessagePackObject(true)]
public sealed class GuildDormPreEnterResponse : GuildResponse { public GuildDormEnterRoomConnectData? ConnectData; }
[MessagePackObject(true)]
public sealed class GuildDormEnterRoomConnectData { public string IpAddress = ""; public int TcpPort; public string Token = ""; public string ChatChannelId = ""; }
[MessagePackObject(true)]
public sealed class GuildDormSetRoomBgmIdsRequest { public int RoomId; public List<int> BgmIds = []; }
[MessagePackObject(true)]
public sealed class GuildDormSetRoomBgmIdsResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class GuildDormGetDailyInteractRewardRequest { }
[MessagePackObject(true)]
public sealed class GuildDormGetDailyInteractRewardResponse : GuildResponse { public List<RewardGoods> RewardGoodsList = []; }
[MessagePackObject(true)]
public sealed class GuildDormGetOneTimeInteractRewardRequest { public int FurnitureId; public int ReplyIndex; }
[MessagePackObject(true)]
public sealed class GuildDormGetOneTimeInteractRewardResponse : GuildResponse { public List<RewardGoods> RewardGoodsList = []; }
[MessagePackObject(true)]
public sealed class GuildDormRecordInteractRequest { public int FurnitureId; }
[MessagePackObject(true)]
public sealed class GuildDormRecordInteractResponse : GuildResponse { }
[MessagePackObject(true)]
public sealed class GuildDormCallRandomBoxRequest { public int FurnitureId; }
[MessagePackObject(true)]
public sealed class GuildDormCallRandomBoxResponse : GuildResponse { public GuildDormPlayerFurnitureRandomBox? RandomBox; }
[MessagePackObject(true)]
public sealed class GuildDormSetRoomThemeRequest { public int RoomId; public int ThemeId; }
[MessagePackObject(true)]
public sealed class GuildDormSetRoomThemeResponse : GuildResponse { public long NextSetRoomThemeTime; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormThemeChanged { public int RoomId; public int ThemeId; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormBgmChanged { public List<int> BgmIds = []; }
