using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AscNet.Common;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer.Handlers;
using MessagePack;

namespace AscNet.GameServer;

public static class GuildDormRoomService
{
    private sealed record Handoff(Session Actor, long PlayerId, long GuildId, int RoomId, int ChannelId, long IssuedAt);
    private static readonly object Gate = new();
    private static readonly Dictionary<long, Handoff> Grants = new();
    private static readonly Dictionary<string, Handoff> Tokens = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, GuildDormConnection> Players = new();
    private static readonly HashSet<GuildDormConnection> Sockets = new();
    internal const int HandshakeLimit = 16 * 1024;
    internal static readonly MessagePackSerializerOptions HandshakeOptions = Packet.InboundOptions
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumDecompressedSize(HandshakeLimit));
    private static TcpListener? listener;
    private static CancellationTokenSource? stopping;
    public static int Port { get { lock (Gate) return listener is null ? 0 : ((IPEndPoint)listener.LocalEndpoint).Port; } }
    public static GuildDormConnection[] Connections { get { lock (Gate) return Players.Values.ToArray(); } }

    public static void Start()
    {
        lock (Gate)
        {
            if (listener is not null) return;
            TcpListener server = new(IPAddress.Parse(Environment.GetEnvironmentVariable("ASCNET_GAME_BIND_ADDRESS") ?? "0.0.0.0"), 0);
            server.Start();
            listener = server;
            stopping = new();
            _ = AcceptAsync(server, stopping.Token);
        }
    }

    public static void Stop()
    {
        GuildDormConnection[] sockets;
        lock (Gate)
        {
            stopping?.Cancel();
            listener?.Stop();
            listener = null;
            stopping?.Dispose();
            stopping = null;
            Tokens.Clear();
            Grants.Clear();
            Players.Clear();
            sockets = Sockets.ToArray();
        }
        foreach (var socket in sockets) socket.Close();
    }

    private static async Task AcceptAsync(TcpListener server, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await server.AcceptTcpClientAsync(stop);
                GuildDormConnection connection;
                lock (Gate)
                {
                    if (stop.IsCancellationRequested) { client.Close(); return; }
                    if (Sockets.Count(socket => socket.PlayerId == 0) >= 64) { client.Close(); continue; }
                    connection = new(client);
                    Sockets.Add(connection);
                }
                _ = connection.RunAsync();
            }
        }
        catch (Exception exception) when (stop.IsCancellationRequested || exception is ObjectDisposedException) { }
        catch (Exception exception)
        {
            Server.log.Error("Guild dorm listener failed.", exception);
            Stop();
        }
    }

    public static string IssueToken(Session actor, int roomId, int channelId)
    {
        lock (GuildModule.MembershipLock)
        {
        Guild guild = GuildModule.RequireMembership(actor);
        long uid = actor.player.PlayerData.Id;
        if (!Authenticated(actor, uid)) throw new ServerCodeException("Guild dorm main session is not active", 20173016);
        var room = GuildDormModule.Room(roomId);
        if (channelId < 0 || channelId > room.ChannelCount) throw new ServerCodeException("Invalid guild dorm channel", 20173003);
        lock (Gate)
        {
            if (listener is null) throw new ServerCodeException("Guild dorm TCP is not active", 20173009);
            foreach (string key in Tokens.Where(pair => pair.Value.PlayerId == uid || Expired(pair.Value)).Select(pair => pair.Key).ToArray()) Tokens.Remove(key);
            foreach (long playerId in Grants.Where(pair => Expired(pair.Value)).Select(pair => pair.Key).ToArray()) Grants.Remove(playerId);
            if (channelId == 0)
                channelId = Enumerable.Range(1, room.ChannelCount)
                    .OrderBy(id => Players.Values.Count(player => player.GuildId == guild.Id && player.RoomId == roomId && player.ChannelId == id && player.PlayerId != uid)).First();
            if (Players.Values.Count(player => player.GuildId == guild.Id && player.RoomId == roomId && player.ChannelId == channelId && player.PlayerId != uid) >= room.ChannelMemberCount)
                throw new ServerCodeException("Guild dorm channel is full", 20173012);
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Tokens.Add(token, new(actor, uid, guild.Id, roomId, channelId, Stopwatch.GetTimestamp()));
            Grants[uid] = Tokens[token];
            return token;
        }
        }
    }

    internal static int TokenChannelId(string token)
    {
        lock (Gate)
            return Tokens.TryGetValue(token, out var handoff) && !Expired(handoff)
                ? handoff.ChannelId
                : throw new ServerCodeException("Invalid guild dorm token", 20173015);
    }

    private static bool Expired(Handoff handoff) => Stopwatch.GetElapsedTime(handoff.IssuedAt) >= TimeSpan.FromSeconds(60);
    private static bool Authenticated(Session actor, long uid) => actor.player?.PlayerData.Id == uid && actor.client.Connected
        && Server.Instance.Sessions.TryGetValue(actor.id, out Session? current) && ReferenceEquals(current, actor);

    public static List<GuildDormChannelData> GetChannels(long guildId, int roomId)
    {
        var room = GuildDormModule.Room(roomId);
        lock (Gate)
            return Enumerable.Range(1, room.ChannelCount).Select(id => new GuildDormChannelData
            {
                ChannelId = id,
                MemberCount = Players.Values.Count(player => player.GuildId == guildId && player.RoomId == roomId && player.ChannelId == id)
            }).ToList();
    }

    public static void Revoke(long uid) => ReconcileMembership(uid, null);

    public static void ReconcileMembership(long uid, long? guildId)
    {
        GuildDormConnection? connection = null;
        lock (Gate)
        {
            foreach (string key in Tokens.Where(pair => pair.Value.PlayerId == uid && pair.Value.GuildId != guildId).Select(pair => pair.Key).ToArray()) Tokens.Remove(key);
            if (Grants.TryGetValue(uid, out var grant) && grant.GuildId != guildId) Grants.Remove(uid);
            if (Players.TryGetValue(uid, out var current) && current.GuildId != guildId) Players.Remove(uid, out connection);
        }
        connection?.Close();
    }

    public static bool IsCurrent(GuildDormConnection connection)
    {
        lock (Gate) return !connection.IsClosed && Players.TryGetValue(connection.PlayerId, out var current) && ReferenceEquals(current, connection);
    }

    internal static void Enter(GuildDormConnection connection, Packet.Request packet)
    {
        var request = MessagePackSerializer.Deserialize<GuildDormEnterRoomRequest>(packet.Content, HandshakeOptions);
        Handoff? handoff;
        lock (Gate)
        {
            if (request.Token is null || request.Token.Length != 64 || !Tokens.Remove(request.Token, out handoff))
                throw new ServerCodeException("Invalid guild dorm token", 20173015);
        }
        if (Expired(handoff) || request.PlayerId != handoff.PlayerId)
            throw new ServerCodeException("Invalid guild dorm token", 20173015);
        lock (GuildModule.MembershipLock)
        lock (Session.GetPlayerOperationLock(handoff.PlayerId))
        {
            if (!Authenticated(handoff.Actor, handoff.PlayerId)) throw new ServerCodeException("Invalid guild dorm main connection", 20173016);
            Guild guild = GuildModule.RequireMembership(handoff.Actor);
            if (guild.Id != handoff.GuildId) throw new ServerCodeException("Invalid guild dorm membership", 20173010);
            var room = GuildDormModule.Room(handoff.RoomId);
            GuildDormConnection? old;
            lock (Gate)
            {
                if (listener is null || connection.IsClosed) throw new ServerCodeException("Guild dorm TCP is not active", 20173009);
                if (Expired(handoff) || !Grants.TryGetValue(handoff.PlayerId, out var grant) || !ReferenceEquals(grant, handoff))
                    throw new ServerCodeException("Revoked guild dorm token", 20173015);
                Grants.Remove(handoff.PlayerId);
                if (Players.Values.Count(player => player.GuildId == handoff.GuildId && player.RoomId == handoff.RoomId && player.ChannelId == handoff.ChannelId && player.PlayerId != handoff.PlayerId) >= room.ChannelMemberCount)
                    throw new ServerCodeException("Guild dorm channel is full", 20173012);
                connection.Bind(handoff.Actor, handoff.PlayerId, handoff.GuildId, handoff.RoomId, handoff.ChannelId);
                Players.Remove(handoff.PlayerId, out old);
                Players.Add(handoff.PlayerId, connection);
            }
            if (old is not null) { old.Close(); GuildDormRoomWorld.Exit(old); }
            GuildDormRoomData snapshot = GuildDormRoomWorld.Enter(connection);
            if (!IsCurrent(connection)) return;
            connection.Respond(new GuildDormEnterRoomResponse { RoomData = snapshot }, packet.Id);
        }
    }

    internal static void Remove(GuildDormConnection connection)
    {
        lock (Gate)
        {
            Sockets.Remove(connection);
            if (Players.TryGetValue(connection.PlayerId, out var current) && ReferenceEquals(current, connection)) Players.Remove(connection.PlayerId);
        }
        if (connection.PlayerId != 0)
            lock (GuildModule.MembershipLock)
            lock (Session.GetPlayerOperationLock(connection.PlayerId)) GuildDormRoomWorld.Exit(connection);
    }

    internal static void RequireCurrent(GuildDormConnection connection)
    {
        if (!IsCurrent(connection) || !Authenticated(connection.Actor, connection.PlayerId)
            || GuildModule.RequireMembership(connection.Actor).Id != connection.GuildId)
            throw new ServerCodeException("Guild dorm connection is no longer valid", 20173010);
    }
}

public sealed class GuildDormConnection
{
    private readonly TcpClient client;
    private readonly object outbound = new();
    private int closed;
    private int sequence;
    private bool entered;
    private List<Packet>? pendingPushes;
    private int pendingBytes;
    private readonly PacketWriter writer;
    private Task? entryWritten;
    public long PlayerId { get; private set; }
    public long GuildId { get; private set; }
    public int RoomId { get; private set; }
    public int ChannelId { get; private set; }
    public Session Actor { get; private set; } = null!;
    public bool IsClosed => Volatile.Read(ref closed) != 0;

    internal GuildDormConnection(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        writer = new PacketWriter(client, _ => Close(), encrypted: false);
    }
    internal void Bind(Session actor, long uid, long guildId, int roomId, int channelId)
    { Actor = actor; PlayerId = uid; GuildId = guildId; RoomId = roomId; ChannelId = channelId; }

    public void Send(object payload)
    {
        lock (outbound)
        {
            Packet packet = new() { No = ++sequence, Type = Packet.ContentType.Push, Content = MessagePackSerializer.Serialize(new Packet.Push
            { Name = payload.GetType().Name, Content = MessagePackSerializer.Serialize(payload.GetType(), payload) }) };
            if (entered) writer.Enqueue(packet);
            else
            {
                pendingBytes = checked(pendingBytes + packet.Content.Length);
                if (pendingBytes > PacketCodec.MaxFrameLength) { Close(); return; }
                (pendingPushes ??= new()).Add(packet);
            }
        }
    }

    public void Respond(object payload, int id)
    {
        lock (outbound)
        {
            Respond(payload.GetType().Name, MessagePackSerializer.Serialize(payload.GetType(), payload), id);
            if (payload is GuildDormEnterRoomResponse { Code: 0 })
            {
                entered = true;
                if (pendingPushes is not null)
                    foreach (Packet push in pendingPushes) writer.Enqueue(push);
                pendingPushes = null;
                pendingBytes = 0;
            }
        }
    }
    private void Respond(string name, byte[] content, int id)
    {
        lock (outbound)
        {
            Packet packet = new() { No = 0, Type = Packet.ContentType.Response, Content = MessagePackSerializer.Serialize(new Packet.Response { Id = id, Name = name, Content = content }) };
            if (name == nameof(GuildDormEnterRoomResponse)) entryWritten = writer.Enqueue(packet, trackCompletion: true);
            else writer.Enqueue(packet);
        }
    }


    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0)
        {
            writer.Close();
            client.Close();
        }
    }

    internal async Task RunAsync()
    {
        string phase = "opening stream";
        try
        {
            NetworkStream stream = client.GetStream();
            while (!IsClosed)
            {
                double timeout = PlayerId == 0 ? 10000 : Math.Max(GuildDormModule.Config.HeartbeatTimeout * 2.0, GuildDormModule.Config.MaxDisconnectTime * 1000.0);
                using CancellationTokenSource idle = new(TimeSpan.FromMilliseconds(timeout));
                phase = PlayerId == 0 ? "reading entry frame" : "reading room frame";
                Packet? packet = await PacketCodec.ReadAsync(stream, idle.Token,
                    PlayerId == 0 ? GuildDormRoomService.HandshakeLimit : PacketCodec.MaxFrameLength,
                    PlayerId == 0 ? GuildDormRoomService.HandshakeOptions : Packet.InboundOptions, encrypted: false);
                if (packet is null)
                {
                    if (PlayerId == 0) Server.log.Warn("Guild dorm peer disconnected before sending an entry frame.");
                    break;
                }
                if (PlayerId == 0)
                {
                    phase = "decoding entry request";
                    if (packet.Type != Packet.ContentType.Request)
                        throw new InvalidDataException("Expected guild dorm entry request packet.");
                    Packet.Request request = MessagePackSerializer.Deserialize<Packet.Request>(packet.Content, GuildDormRoomService.HandshakeOptions);
                    if (request.Name != nameof(GuildDormEnterRoomRequest))
                        throw new InvalidDataException("Expected guild dorm entry request name.");
                    phase = "validating entry";
                    try { GuildDormRoomService.Enter(this, request); }
                    catch (ServerCodeException exception)
                    {
                        Server.log.Warn($"Guild dorm entry rejected: code={exception.Code}.");
                        Respond(new GuildDormEnterRoomResponse { Code = exception.Code }, request.Id);
                        break;
                    }
                    if (entryWritten is null) break;
                    phase = "writing entry response";
                    await entryWritten;
                    phase = "joining room";
                    lock (GuildModule.MembershipLock)
                    lock (Session.GetPlayerOperationLock(PlayerId))
                        GuildDormRoomWorld.Joined(this);
                    continue;
                }
                phase = "processing room packet";
                lock (GuildModule.MembershipLock)
                lock (Session.GetPlayerOperationLock(PlayerId))
                {
                    GuildDormRoomService.RequireCurrent(this);
                    if (packet.Type == Packet.ContentType.Request)
                    {
                        Packet.Request request = MessagePackSerializer.Deserialize<Packet.Request>(packet.Content, Packet.InboundOptions);
                        try
                        {
                            switch (request.Name)
                            {
                                case nameof(GuildDormHeartbeatRequest): Respond(new GuildDormHeartbeatResponse(), request.Id); break;
                                case nameof(GuildDormKcpConfirmRequest): Respond(new GuildDormKcpConfirmResponse { Code = 20173017 }, request.Id); break;
                                case nameof(GuildDormExitRoomRequest): Close(); break;
                                case nameof(GuildDormEnterRoomRequest): throw new ServerCodeException("Room already entered", 20173011);
                                case nameof(GuildDormChangeCharacterRequest):
                                case nameof(GuildDormFurnitureInteractRequest):
                                case nameof(GuildDormNpcInteractRequest):
                                case nameof(GuildDormPlayNoteRequest):
                                    Respond(GuildDormRoomWorld.Handle(this, request), request.Id); break;
                                default: throw new InvalidDataException("Unknown guild dorm request.");
                            }
                        }
                        catch (ServerCodeException exception)
                        {
                            Respond(request.Name[..^7] + "Response", MessagePackSerializer.Serialize(new Dictionary<string, int> { ["Code"] = exception.Code }), request.Id);
                        }
                    }
                    else if (packet.Type == Packet.ContentType.Push)
                    {
                        Packet.Push message = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content, Packet.InboundOptions);
                        GuildDormRoomWorld.Message(this, message.Name, message.Content);
                    }
                    else throw new InvalidDataException("Invalid guild dorm packet type.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException or MessagePackSerializationException or InvalidDataException or ServerCodeException)
        {
            if (!IsClosed) Server.log.Warn($"Guild dorm connection closed during {phase}: {exception.GetType().Name}.");
        }
        catch (Exception exception) { Server.log.Error("Guild dorm connection failed.", exception); }
        finally
        {
            await writer.CompleteAsync();
            Close();
            GuildDormRoomService.Remove(this);
        }
    }
}
