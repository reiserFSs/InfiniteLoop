using System.Globalization;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.guilddorm;
using AscNet.Table.V2.client.miniactivity.musicgame.musiccreation.instrumentsimulator;
using MessagePack;

namespace AscNet.GameServer;

public static class GuildDormRoomWorld
{
    private sealed class Presence
    {
        public required GuildDormConnection Connection;
        public required GuildDormPlayerData Data;
        public int FurnitureId;
        public int NpcId;
        public int ActionId = -1;
        public long ActionEnds;
        public long NextAction;
        public long InteractionStarted;
        public long InteractionStartedTick;
        public long? InteractionEndedTick;
        public long LastNote;
        public bool Joined;
    }

    private sealed class Npc
    {
        public required GuildDormNpcRefreshTable Config;
        public required GuildDormNpcData Data;
        public int ActionIndex;
        public int PointIndex;
        public long IdleUntil;
        public GuildDormNpcState BeforeInteract;
        public long PausedAt;
    }

    private sealed class RoomState
    {
        public required GuildDormRoomData Data;
        public readonly Dictionary<long, Presence> Players = new();
        public readonly Dictionary<int, GuildDormFurnitureData> Furniture = new();
        public readonly Dictionary<int, Npc> Npcs = new();
        public int ControlId;
        public long ControlPeriod;
        public long NextRefresh;
        public bool Reloading;
    }

    // ponytail: one transient-world gate; partition by room if room traffic becomes material.
    // Never acquire a guild/player/database lock while holding this gate.
    private static readonly object Gate = new();
    private static readonly Dictionary<(long Guild, int Room, int Channel), RoomState> Rooms = new();
    private static Timer? timer;
    private static List<T> Rows<T>() where T : ITable => TableReaderV2.Parse<T>();
    private static readonly Lazy<Dictionary<int, GuildDormRoomPosition[]>> Paths = new(() => Rows<GuildDormNpcActionMoveTable>()
        .ToDictionary(row => row.Id, row => row.PositionStrs.Where(value => !string.IsNullOrEmpty(value)).Select(ParsePosition).ToArray()));
    private static readonly Lazy<Dictionary<int, int>> NoteMasks = new(() => Rows<InstrumentKeyMapTable>()
        .GroupBy(row => row.FId / 1000).ToDictionary(group => group.Key,
            group => group.Aggregate(0, (mask, row) => mask | (1 << (row.FId % 1000 - 1)))));
    private static (long, int, int) Key(GuildDormConnection connection) => (connection.GuildId, connection.RoomId, connection.ChannelId);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static void Require(bool valid, int code)
    {
        if (!valid) throw new ServerCodeException("Guild dorm room request rejected", code);
    }

    public static GuildDormRoomData Enter(GuildDormConnection connection)
    {
        GuildDormRoomService.RequireCurrent(connection);
        Guild guild = GuildModule.RequireMembership(connection.Actor);
        GuildDormRoomData metadata = GuildDormModule.BuildRoomData(guild, connection.RoomId, connection.ChannelId);
        GuildDormRoomThemeTable theme = GuildDormModule.Theme(guild, connection.RoomId);
        int characterId = connection.Actor.player.GuildState.Dorm.CurrentCharacterId;
        Require(OwnsRole(connection.Actor, characterId), 20173004);
        lock (Gate)
        {
            Require(GuildDormRoomService.IsCurrent(connection), 20173010);
            if (!Rooms.TryGetValue(Key(connection), out RoomState? room) || room.Reloading)
            {
                room = new() { Data = metadata };
                Rooms[Key(connection)] = room;
                foreach (GuildDormDefaultFurnitureTable row in Rows<GuildDormDefaultFurnitureTable>().Where(row => row.ThemeId == theme.Id))
                {
                    Require(Rows<GuildDormFurnitureTable>().Any(item => item.Id == row.FurnitureId), 20173006);
                    // Furniture is scene-authored: RoomBuild resolves PrefabName, not a server transform.
                    room.Furniture.Add(row.Id, new() { Id = row.Id });
                }
                RefreshNpcs(room, theme, DateTimeOffset.UtcNow, false);
            }
            Require(room.Data.ThemeId == theme.Id, 20173020);
            if (room.Players.TryGetValue(connection.PlayerId, out Presence? old)) Release(room, old);
            room.Players[connection.PlayerId] = new()
            {
                Connection = connection,
                Data = new()
                {
                    PlayerId = checked((int)connection.PlayerId), PlayerName = connection.Actor.player.PlayerData.Name,
                    CharacterId = characterId,
                    Position = new() { X = (float)theme.EntryX, Z = (float)theme.EntryZ, Y = (float)theme.EntryY, Angle = (float)(theme.EntryAngle ?? 0) }
                }
            };
            timer ??= new Timer(Tick, null, 100, 100);
            return Snapshot(room, metadata);
        }
    }

    public static void Joined(GuildDormConnection connection)
    {
        lock (Gate)
        {
            if (!TryPresence(connection, out RoomState? room, out Presence? player)) return;
            player.Joined = true;
            Send(room, new NotifyGuildDormSyncEntities { PlayerDatas = new() { Delta(player.Data) } });
        }
    }

    public static object Handle(GuildDormConnection connection, Packet.Request packet)
    {
        GuildDormRoomService.RequireCurrent(connection);
        if (packet.Name == nameof(GuildDormChangeCharacterRequest))
        {
            int characterId = packet.Deserialize<GuildDormChangeCharacterRequest>().CharacterId;
            Require(OwnsRole(connection.Actor, characterId), 20173004);
            lock (Gate)
            {
                Presence player = PresenceFor(connection, out _);
                Require(player.FurnitureId == 0 && player.NpcId == 0 && player.ActionId == -1, 20173019);
            }
            GuildModule.Commit(connection.Actor, GuildModule.RequireMembership(connection.Actor), mutation =>
            {
                Require(OwnsRole(connection.Actor, characterId), 20173004);
                mutation.Player(connection.PlayerId).Dorm.CurrentCharacterId = characterId;
            });
            lock (Gate)
            {
                Presence player = PresenceFor(connection, out RoomState room);
                player.Data.CharacterId = characterId;
                Send(room, new NotifyGuildDormSyncEntities { PlayerDatas = new() { Delta(player.Data) } });
            }
            return new GuildDormChangeCharacterResponse();
        }
        lock (Gate)
        {
            Presence player = PresenceFor(connection, out RoomState room);
            switch (packet.Name)
            {
                case nameof(GuildDormFurnitureInteractRequest):
                    InteractFurniture(room, player, packet.Deserialize<GuildDormFurnitureInteractRequest>().FurnitureId);
                    return new GuildDormFurnitureInteractResponse();
                case nameof(GuildDormNpcInteractRequest):
                    InteractNpc(room, player, packet.Deserialize<GuildDormNpcInteractRequest>().NpcId);
                    return new GuildDormNpcInteractResponse();
                case nameof(GuildDormPlayNoteRequest):
                    PlayNote(room, player, packet.Deserialize<GuildDormPlayNoteRequest>().Note);
                    return new GuildDormPlayNoteResponse();
                default: throw new ServerCodeException("Unsupported guild dorm room request", 20173007);
            }
        }
    }

    public static void Message(GuildDormConnection connection, string name, byte[] content)
    {
        GuildDormRoomService.RequireCurrent(connection);
        try
        {
            lock (Gate)
            {
                Presence player = PresenceFor(connection, out RoomState room);
                Require(player.FurnitureId == 0 && player.NpcId == 0, 20173019);
                switch (name)
                {
                    case nameof(GuildDormSyncPlayerStateMessage):
                        GuildDormSyncPlayerStateMessage state = MessagePackSerializer.Deserialize<GuildDormSyncPlayerStateMessage>(content, Packet.InboundOptions);
                        Require(Finite(state.Position) && state.State is >= 0 and <= 2, 20173007);
                        if (player.ActionId != -1) StopAction(room, player);
                        player.Data.Position = state.Position!;
                        player.Data.State = state.State;
                        Send(room, new NotifyGuildDormSyncEntities { PlayerDatas = new() { Delta(player.Data) } });
                        break;
                    case nameof(GuildDormPlayActionMessage):
                        int actionId = MessagePackSerializer.Deserialize<GuildDormPlayActionMessage>(content, Packet.InboundOptions).ActionId;
                        if (actionId == -1) { StopAction(room, player); break; }
                        GuildDormPlayActionTable? action = Rows<GuildDormPlayActionTable>().FirstOrDefault(row => row.Id == actionId);
                        Require(action is not null && Environment.TickCount64 >= player.NextAction, 20173005);
                        player.ActionId = actionId;
                        player.ActionEnds = Environment.TickCount64 + checked((long)(action!.Duration * 1000));
                        player.NextAction = Environment.TickCount64 + checked((long)(action.CoolDown * 1000));
                        Send(room, new NotifyGuildDormPlayAction { PlayerId = player.Data.PlayerId, ActionId = actionId });
                        break;
                    default: throw new ServerCodeException("Unsupported guild dorm message", 20173007);
                }
            }
        }
        catch (ServerCodeException exception) { connection.Send(new NotifyGuildDormMessageFailed { Code = exception.Code }); }
    }

    public static void Exit(GuildDormConnection connection)
    {
        lock (Gate)
        {
            // A replaced socket's finally must not remove its successor or release its occupancy.
            if (!Rooms.TryGetValue(Key(connection), out RoomState? room)
                || !room.Players.TryGetValue(connection.PlayerId, out Presence? player)
                || !ReferenceEquals(player.Connection, connection)) return;
            Release(room, player);
            room.Players.Remove(connection.PlayerId);
            Send(room, new NotifyGuildDormPlayerExit { PlayerId = player.Data.PlayerId });
            if (room.Players.Count == 0) Rooms.Remove(Key(connection));
            if (Rooms.Count == 0) { timer?.Dispose(); timer = null; }
        }
    }

    public static void Broadcast(long guildId, int roomId, int channelId, object payload)
    {
        lock (Gate)
        {
            foreach (var pair in Rooms)
                if (pair.Key.Guild == guildId && pair.Key.Room == roomId && (channelId == 0 || pair.Key.Channel == channelId))
                {
                    if (payload is NotifyGuildDormThemeChanged) pair.Value.Reloading = true;
                    else if (payload is NotifyGuildDormBgmChanged bgm) pair.Value.Data.BgmIds = bgm.BgmIds.ToList();
                }
            foreach (GuildDormConnection connection in GuildDormRoomService.Connections)
                if (connection.GuildId == guildId && connection.RoomId == roomId && (channelId == 0 || connection.ChannelId == channelId))
                    connection.Send(payload);
        }
    }

    public static GuildDormFurnitureTable RequireFurniture(Session session, int furnitureId)
    {
        GuildDormConnection? connection = GuildDormRoomService.Connections.FirstOrDefault(item => ReferenceEquals(item.Actor, session));
        Require(connection is not null, 20173009);
        lock (Gate)
        {
            PresenceFor(connection!, out RoomState room);
            Require(room.Furniture.ContainsKey(furnitureId), 20173006);
            return FurnitureConfig(furnitureId);
        }
    }

    // Called inside the main claim's GuildModule.Commit; no transient consumed flag can lose a retry.
    public static void AccrueReward(Session session, GuildDormPlayerState state)
    {
        GuildDormConnection? connection = GuildDormRoomService.Connections.FirstOrDefault(item => ReferenceEquals(item.Actor, session));
        Require(connection is not null, 20173009);
        lock (Gate)
        {
            Presence player = PresenceFor(connection!, out _);
            Require(player.FurnitureId > 0 && FurnitureConfig(player.FurnitureId).IsGetReward == 1, 20173513);
            if (state.LastInteractRewardAt >= player.InteractionStarted) return;
            state.DailyInteractEarnedTimes = checked(state.DailyInteractEarnedTimes + 1);
            state.LastInteractRewardAt = player.InteractionStarted;
        }
    }

    private static bool OwnsRole(Session session, int id) => Rows<GuildDormRoleTable>().Any(row => row.Id == id)
        && session.character.Characters.Any(character => character.Id == id);
    private static GuildDormFurnitureTable FurnitureConfig(int id)
    {
        GuildDormDefaultFurnitureTable? placement = Rows<GuildDormDefaultFurnitureTable>().FirstOrDefault(row => row.Id == id);
        GuildDormFurnitureTable? furniture = Rows<GuildDormFurnitureTable>().FirstOrDefault(row => row.Id == placement?.FurnitureId);
        Require(furniture is not null, 20173006);
        return furniture!;
    }
    private static bool Finite(GuildDormRoomPosition? position) => position is not null
        && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) && float.IsFinite(position.Angle);
    private static bool TryPresence(GuildDormConnection connection, out RoomState room, out Presence player)
    {
        player = null!;
        return Rooms.TryGetValue(Key(connection), out room!) && room.Players.TryGetValue(connection.PlayerId, out player!)
            && ReferenceEquals(player.Connection, connection) && GuildDormRoomService.IsCurrent(connection);
    }
    private static Presence PresenceFor(GuildDormConnection connection, out RoomState room)
    {
        Require(TryPresence(connection, out room, out Presence player), 20173010);
        Require(!room.Reloading, 20173020);
        return player;
    }
    private static GuildDormSyncPlayerData Delta(GuildDormPlayerData player) => new()
    { PlayerId = player.PlayerId, PlayerName = player.PlayerName, CharacterId = player.CharacterId, Position = player.Position, State = player.State };
    private static GuildDormSyncNpcData Delta(GuildDormNpcData npc) => new()
    { Id = npc.Id, Position = npc.Position, PlayerId = npc.PlayerId, LastInteractTime = npc.LastInteractTime, State = npc.State, ActionId = npc.ActionId };
    private static GuildDormRoomData Snapshot(RoomState room, GuildDormRoomData metadata) => new()
    {
        DormId = metadata.DormId, RoomId = metadata.RoomId, ChannelId = metadata.ChannelId, ThemeId = metadata.ThemeId,
        NpcGroupId = room.Data.NpcGroupId, BgmIds = metadata.BgmIds, GuildCreateTime = metadata.GuildCreateTime,
        PlayerDatas = room.Players.Values.Where(player => GuildDormRoomService.IsCurrent(player.Connection)).Select(player => new GuildDormPlayerData
        { PlayerId = player.Data.PlayerId, PlayerName = player.Data.PlayerName, CharacterId = player.Data.CharacterId, Position = player.Data.Position, State = player.Data.State }).ToList(),
        FurnitureDatas = room.Furniture.Values.Select(furniture => new GuildDormFurnitureData
        { Id = furniture.Id, Position = furniture.Position, PlayerId = furniture.PlayerId, LastInteractTime = furniture.LastInteractTime }).ToList(),
        NpcDatas = room.Npcs.Values.Select(npc => new GuildDormNpcData
        { Id = npc.Data.Id, Position = npc.Data.Position, PlayerId = npc.Data.PlayerId, LastInteractTime = npc.Data.LastInteractTime, State = npc.Data.State, ActionId = npc.Data.ActionId }).ToList()
    };
    private static void Send(RoomState room, object payload)
    {
        foreach (Presence player in room.Players.Values)
            if (player.Joined && GuildDormRoomService.IsCurrent(player.Connection)) player.Connection.Send(payload);
    }
    private static void StopAction(RoomState room, Presence player)
    {
        player.ActionId = -1;
        player.ActionEnds = 0;
        Send(room, new NotifyGuildDormPlayAction { PlayerId = player.Data.PlayerId, ActionId = -1 });
    }
    private static void InteractFurniture(RoomState room, Presence player, int id)
    {
        if (id == -1)
        {
            Require(player.FurnitureId != 0, 20173006);
            EndFurniture(room, player);
            return;
        }
        Require(room.Furniture.TryGetValue(id, out GuildDormFurnitureData? furniture) && FurnitureConfig(id).InteractPos > 0, 20173006);
        Require(furniture!.PlayerId == 0, 20173018);
        Require(player.FurnitureId == 0 && player.NpcId == 0, 20173019);
        // XGuildDormRole:GetIsOverLastEndInteractTime gates a new interaction, not reward collection.
        Require(player.InteractionEndedTick is not long ended || Environment.TickCount64 - ended >= GuildDormModule.Config.InteractIntervalTime * 1000L, 20173019);
        if (player.ActionId != -1) StopAction(room, player);
        player.FurnitureId = id;
        player.InteractionStarted = Now;
        player.InteractionStartedTick = Environment.TickCount64;
        furniture.PlayerId = player.Data.PlayerId;
        furniture.LastInteractTime = Now;
        Send(room, new NotifyGuildDormSyncFurniture { FurnitureDatas = new() { furniture } });
    }
    private static void EndFurniture(RoomState room, Presence player)
    {
        GuildDormFurnitureData furniture = room.Furniture[player.FurnitureId];
        furniture.PlayerId = 0;
        furniture.LastInteractTime = Now;
        player.FurnitureId = 0;
        player.InteractionEndedTick = Environment.TickCount64;
        Send(room, new NotifyGuildDormSyncFurniture { FurnitureDatas = new() { furniture } });
    }
    private static void InteractNpc(RoomState room, Presence player, int id)
    {
        // XUiGuildDormMovie sends 0 on dialogue close; -1 is furniture-only.
        if (id == 0)
        {
            Require(player.NpcId != 0, 20173026);
            EndNpc(room, player);
            return;
        }
        Require(room.Npcs.TryGetValue(id, out Npc? npc), 20173026);
        Require(npc!.Data.State != GuildDormNpcState.Static, 20173027);
        Require(npc.Data.PlayerId == 0, 20173028);
        Require(player.FurnitureId == 0 && player.NpcId == 0, 20173019);
        Require(player.InteractionEndedTick is not long ended || Environment.TickCount64 - ended >= GuildDormModule.Config.InteractIntervalTime * 1000L, 20173019);
        Require(Now - npc.Data.LastInteractTime >= npc.Config.InteractCdTime, 20173027);
        if (player.ActionId != -1) StopAction(room, player);
        player.NpcId = id;
        player.InteractionStarted = Now;
        player.InteractionStartedTick = Environment.TickCount64;
        npc.Data.PlayerId = player.Data.PlayerId;
        npc.Data.LastInteractTime = Now;
        npc.BeforeInteract = npc.Data.State;
        npc.PausedAt = Environment.TickCount64;
        npc.Data.State = GuildDormNpcState.Interact;
        Send(room, new NotifyGuildDormSyncEntities { NpcDatas = new() { Delta(npc.Data) } });
    }
    private static void EndNpc(RoomState room, Presence player)
    {
        if (room.Npcs.TryGetValue(player.NpcId, out Npc? npc))
        {
            npc.Data.PlayerId = 0;
            npc.Data.LastInteractTime = Now;
            npc.Data.State = npc.BeforeInteract;
            if (npc.Data.State == GuildDormNpcState.Idle) npc.IdleUntil += Environment.TickCount64 - npc.PausedAt;
            Send(room, new NotifyGuildDormSyncEntities { NpcDatas = new() { Delta(npc.Data) } });
        }
        player.NpcId = 0;
        player.InteractionEndedTick = Environment.TickCount64;
    }
    private static void PlayNote(RoomState room, Presence player, int note)
    {
        Require(player.FurnitureId > 0, 20173029);
        GuildDormFurnitureTable furniture = FurnitureConfig(player.FurnitureId);
        Require(furniture.FurnitureType == 6 && NoteMasks.Value.TryGetValue(furniture.Id, out int mask) && note > 0 && (note & ~mask) == 0, 20173029);
        // Local abuse limit: at most one chord per 10ms; the wire contains a simultaneous-key bitmask.
        Require(Environment.TickCount64 - player.LastNote >= 10, 20173030);
        player.LastNote = Environment.TickCount64;
        Send(room, new NotifyGuildDormPlayNoteRequest { Note = note });
    }
    private static void Release(RoomState room, Presence player)
    {
        if (player.FurnitureId != 0) EndFurniture(room, player);
        if (player.NpcId != 0) EndNpc(room, player);
    }

    private static bool InTime(int? timeId, DateTimeOffset now) => timeId is null or 0 || timeId > 0 && ActivityScheduleService.IsOpen(timeId.Value, now);
    private static void RefreshNpcs(RoomState room, GuildDormRoomThemeTable theme, DateTimeOffset now, bool notify)
    {
        GuildDormNpcRefreshControlTable? control = Rows<GuildDormNpcRefreshControlTable>()
            .Where(row => row.GroupId == theme.NpcRefreshControlGroupId && InTime(row.TimeId, now)).OrderBy(row => row.Priority).FirstOrDefault();
        int groupId = 0;
        long period = control is null || control.RefreshType == 1 ? 0 : now.ToUnixTimeSeconds() / (Math.Max(1, control.RefreshInterval ?? 1) * 86400L);
        if (control is not null)
        {
            int[] ids = control.NpcGroupIds.Where(id => id > 0).ToArray();
            Require(ids.Length != 0, 20173024);
            if (control.Id == room.ControlId && period == room.ControlPeriod) groupId = room.Data.NpcGroupId;
            else if (control.RefreshType == 3) groupId = ids[(int)(period % ids.Length)];
            else
            {
                int total = control.NpcGroupWeights.Take(ids.Length).Sum();
                Require(total > 0, 20173024);
                int roll = Random.Shared.Next(total);
                for (int index = 0; index < ids.Length; index++)
                    if ((roll -= control.NpcGroupWeights[index]) < 0) { groupId = ids[index]; break; }
            }
        }
        HashSet<int>? selected = null;
        if (groupId > 0)
        {
            GuildDormNpcRefreshGroupTable? group = Rows<GuildDormNpcRefreshGroupTable>().FirstOrDefault(row => row.Id == groupId);
            Require(group is not null, 20173024);
            selected = group!.NpcRefreshIds.Where(id => id > 0).ToHashSet();
        }
        // Unknown positive TimeIds remain unavailable: no calendar is manufactured for past event NPCs.
        var configs = Rows<GuildDormNpcRefreshTable>().Where(row => row.ThemeId == theme.Id && InTime(row.RefreshTimeId, now)
            && (theme.NpcRefreshControlGroupId is null or 0 || selected?.Contains(row.Id) == true)).ToDictionary(row => row.Id);
        List<int> removed = room.Npcs.Keys.Where(id => !configs.ContainsKey(id) && room.Npcs[id].Data.PlayerId == 0).ToList();
        foreach (int id in removed) room.Npcs.Remove(id);
        List<GuildDormSyncNpcData> added = new();
        foreach (GuildDormNpcRefreshTable config in configs.Values)
        {
            if (room.Npcs.ContainsKey(config.Id)) continue;
            Npc npc = new() { Config = config, Data = new() { Id = config.Id } };
            SetNpcAction(npc, Environment.TickCount64);
            room.Npcs.Add(config.Id, npc);
            added.Add(Delta(npc.Data));
        }
        if (notify)
        {
            if (room.Data.NpcGroupId != groupId) Send(room, new NotifyGuildDormNpcGroupChanged { NpcGroupId = groupId });
            if (removed.Count != 0) Send(room, new NotifyGuildDormNpcRemoved { NpcIds = removed });
            if (added.Count != 0) Send(room, new NotifyGuildDormSyncEntities { NpcDatas = added });
        }
        room.Data.NpcGroupId = groupId;
        room.ControlId = control?.Id ?? 0;
        room.ControlPeriod = period;
        room.NextRefresh = now.ToUnixTimeSeconds() + 1;
    }

    private static GuildDormRoomPosition ParsePosition(string value)
    {
        string[] parts = value.Split('|');
        if (parts.Length != 4) throw new InvalidDataException("Guild dorm NPC path must contain X|Z|Y|Angle");
        GuildDormRoomPosition result = new()
        {
            X = float.Parse(parts[0], CultureInfo.InvariantCulture), Z = float.Parse(parts[1], CultureInfo.InvariantCulture),
            Y = float.Parse(parts[2], CultureInfo.InvariantCulture), Angle = float.Parse(parts[3], CultureInfo.InvariantCulture)
        };
        if (!Finite(result)) throw new InvalidDataException("Nonfinite guild dorm NPC path");
        return result;
    }
    private static void SetNpcAction(Npc npc, long now)
    {
        if (npc.Config.ActionGroupId is null or 0)
        {
            // Static NPCs are born at the authored InitPos scene transform by XGDNpcManagerComponent.
            npc.Data.State = GuildDormNpcState.Static;
            npc.Data.ActionId = 0;
            npc.Data.Position = null;
            return;
        }
        GuildDormNpcActionGroupTable? group = Rows<GuildDormNpcActionGroupTable>().FirstOrDefault(row => row.Id == npc.Config.ActionGroupId);
        Require(group is not null, 20173025);
        int count = group!.ActionTypes.TakeWhile(type => type > 0).Count();
        Require(count > 0 && group.ActionIds.Count >= count, 20173025);
        npc.ActionIndex %= count;
        npc.Data.ActionId = group.ActionIds[npc.ActionIndex];
        npc.PointIndex = 0;
        if (group.ActionTypes[npc.ActionIndex] == 1)
        {
            npc.Data.State = GuildDormNpcState.Move;
            Require(Paths.Value.TryGetValue(npc.Data.ActionId, out GuildDormRoomPosition[]? path) && path.Length > 0, 20173025);
            npc.Data.Position = path![0];
        }
        else
        {
            Require(group.ActionTypes[npc.ActionIndex] == 2, 20173025);
            npc.Data.State = GuildDormNpcState.Idle;
            GuildDormNpcActionIdleTable? idle = Rows<GuildDormNpcActionIdleTable>().FirstOrDefault(row => row.Id == npc.Data.ActionId);
            Require(idle is not null, 20173025);
            npc.IdleUntil = now + idle!.Duration;
        }
    }
    private static bool AdvanceNpc(Npc npc, long now)
    {
        if (npc.Data.State is GuildDormNpcState.Static or GuildDormNpcState.Interact) return false;
        if (npc.Data.State == GuildDormNpcState.Idle)
        {
            if (now < npc.IdleUntil) return false;
            npc.ActionIndex++;
            SetNpcAction(npc, now);
            return true;
        }
        GuildDormRoomPosition[] path = Paths.Value[npc.Data.ActionId];
        if (npc.PointIndex + 1 >= path.Length)
        {
            npc.ActionIndex++;
            SetNpcAction(npc, now);
            return true;
        }
        GuildDormRoomPosition from = npc.Data.Position!, to = path[npc.PointIndex + 1];
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        double step = Rows<GuildDormNpcActionMoveTable>().First(row => row.Id == npc.Data.ActionId).LerpSpace;
        Require(step > 0, 20173025);
        if (distance <= step) { npc.PointIndex++; npc.Data.Position = to; }
        else
        {
            float fraction = (float)(step / distance);
            npc.Data.Position = new() { X = from.X + dx * fraction, Y = from.Y + dy * fraction, Z = from.Z + dz * fraction, Angle = to.Angle };
        }
        return true;
    }
    private static void Tick(object? state)
    {
        // A slow peer must not queue overlapping timer callbacks behind the world gate.
        if (!Monitor.TryEnter(Gate)) return;
        try
        {
            long now = Environment.TickCount64;
            DateTimeOffset utc = DateTimeOffset.UtcNow;
            foreach (RoomState room in Rooms.Values)
            {
                if (room.Reloading) continue;
                try
                {
                    foreach (Presence player in room.Players.Values)
                    {
                        if (player.ActionId != -1 && now >= player.ActionEnds) StopAction(room, player);
                        if (player.FurnitureId != 0 && now - player.InteractionStartedTick >= GuildDormModule.Config.MaxInteractIntervalTime * 1000L) EndFurniture(room, player);
                        if (player.NpcId != 0 && room.Npcs.TryGetValue(player.NpcId, out Npc? npc)
                            && now - player.InteractionStartedTick >= npc.Config.InteractMaxTime * 1000L) EndNpc(room, player);
                    }
                    if (utc.ToUnixTimeSeconds() >= room.NextRefresh)
                        RefreshNpcs(room, Rows<GuildDormRoomThemeTable>().First(theme => theme.Id == room.Data.ThemeId), utc, true);
                    List<GuildDormSyncNpcData>? changed = null;
                    foreach (Npc npc in room.Npcs.Values)
                        if (AdvanceNpc(npc, now)) (changed ??= new()).Add(Delta(npc.Data));
                    if (changed is not null) Send(room, new NotifyGuildDormSyncEntities { NpcDatas = changed });
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError($"Guild dorm room {room.Data.RoomId}/{room.Data.ChannelId} failed: {exception}");
                    room.Reloading = true;
                    foreach (Presence player in room.Players.Values) player.Connection.Close();
                }
            }
        }
        finally { Monitor.Exit(Gate); }
    }
}
