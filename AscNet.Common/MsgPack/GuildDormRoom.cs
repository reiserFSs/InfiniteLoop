using MessagePack;

namespace AscNet.Common.MsgPack;

// EN native XGuildDorm* metadata: named maps, int32 identities, float32 transforms.
[MessagePackObject(true)]
public sealed class GuildDormRoomPosition
{
    public float X;
    public float Z;
    public float Y;
    public float Angle;
}

[MessagePackObject(true)]
public sealed class GuildDormRoomData
{
    public int DormId;
    public int RoomId;
    public int ChannelId;
    public int ThemeId;
    public int NpcGroupId;
    public List<GuildDormPlayerData> PlayerDatas = new();
    public List<GuildDormNpcData> NpcDatas = new();
    public List<GuildDormFurnitureData> FurnitureDatas = new();
    public List<int> BgmIds = new();
    public long GuildCreateTime;
}

[MessagePackObject(true)]
public sealed class GuildDormPlayerData
{
    public int PlayerId;
    public string PlayerName = string.Empty;
    public int CharacterId;
    public GuildDormRoomPosition Position = new();
    public int State;
}

[MessagePackObject(true)]
public sealed class GuildDormSyncPlayerData
{
    public int PlayerId;
    public string PlayerName = string.Empty;
    public int? CharacterId;
    public GuildDormRoomPosition? Position;
    public int? State;
}

[MessagePackObject(true)]
public sealed class GuildDormFurnitureData
{
    public int Id;
    public GuildDormRoomPosition? Position;
    public int PlayerId;
    public long LastInteractTime;
}

public enum GuildDormNpcState { Static = 0, Move = 1, Idle = 2, Interact = 3 }

[MessagePackObject(true)]
public sealed class GuildDormNpcData
{
    public int Id;
    public GuildDormRoomPosition? Position;
    public int PlayerId;
    public long LastInteractTime;
    public GuildDormNpcState State;
    public int ActionId;
}

[MessagePackObject(true)]
public sealed class GuildDormSyncNpcData
{
    public int Id;
    public GuildDormRoomPosition? Position;
    public int? PlayerId;
    public long? LastInteractTime;
    public GuildDormNpcState? State;
    public int? ActionId;
}

[MessagePackObject(true)]
public sealed class GuildDormEnterRoomRequest { public int PlayerId; public string Token = string.Empty; }
[MessagePackObject(true)]
public sealed class GuildDormEnterRoomResponse { public int Code; public int Port; public uint Conv; public GuildDormRoomData? RoomData; }
[MessagePackObject(true)]
public sealed class GuildDormKcpConfirmRequest { }
[MessagePackObject(true)]
public sealed class GuildDormKcpConfirmResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormHeartbeatRequest { }
[MessagePackObject(true)]
public sealed class GuildDormHeartbeatResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormExitRoomRequest { }
[MessagePackObject(true)]
public sealed class GuildDormExitRoomResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormChangeCharacterRequest { public int CharacterId; }
[MessagePackObject(true)]
public sealed class GuildDormChangeCharacterResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormFurnitureInteractRequest { public int FurnitureId; }
[MessagePackObject(true)]
public sealed class GuildDormFurnitureInteractResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormNpcInteractRequest { public int NpcId; }
[MessagePackObject(true)]
public sealed class GuildDormNpcInteractResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormPlayNoteRequest { public int Note; }
[MessagePackObject(true)]
public sealed class GuildDormPlayNoteResponse { public int Code; }
[MessagePackObject(true)]
public sealed class GuildDormSyncPlayerStateMessage { public GuildDormRoomPosition? Position; public int State; }
[MessagePackObject(true)]
public sealed class GuildDormPlayActionMessage { public int ActionId; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormMessageFailed { public int Code; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormSyncEntities
{
    public List<GuildDormSyncPlayerData> PlayerDatas = new();
    public List<GuildDormSyncNpcData> NpcDatas = new();
}
[MessagePackObject(true)]
public sealed class NotifyGuildDormSyncFurniture { public List<GuildDormFurnitureData> FurnitureDatas = new(); }
[MessagePackObject(true)]
public sealed class NotifyGuildDormPlayAction { public int PlayerId; public int ActionId; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormPlayerExit { public int PlayerId; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormPlayNoteRequest { public int Note; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormNpcGroupChanged { public int NpcGroupId; }
[MessagePackObject(true)]
public sealed class NotifyGuildDormNpcRemoved { public List<int> NpcIds = new(); }
