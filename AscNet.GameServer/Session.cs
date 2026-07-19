using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection.Emit;
using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.GameServer.Game;
using AscNet.Logging;
using MessagePack;
using Newtonsoft.Json;
using Logger = AscNet.Logging.Logger;

namespace AscNet.GameServer
{
    public partial class Session
    {
        public readonly string id;
        public readonly TcpClient client;
        public Player player = default!;
        public Character character = default!;
        public Stage stage = default!;
        public Fight? fight;
        public BossSinglePendingScore? PendingBossSingleScore;
        public Inventory inventory = default!;
        public int? PendingEnterWorldChatRequestId;
        public int? PendingGetWorldChannelInfoRequestId;
        public bool PendingBigWorldLoadCompleteXRpc;
        public bool PendingBigWorldStartFightNotify;
        public readonly Dictionary<(uint EquipId, int Slot), ResonanceInfo> PendingEquipResonances = new();
        public readonly Logger log;
        private long lastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private int packetNo = 0;
        private int disconnectState;
        private readonly MessagePackSerializerOptions lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
        private const int InitialReceiveBufferLength = 1 << 16;
        private const int MaxReceivePacketLength = 1 << 22;
        private static readonly object BigWorldPacketDumpLock = new();
        private static readonly ConcurrentDictionary<long, object> PlayerOperationLocks = new();
        private static long BigWorldPacketDumpOrdinal;
        private static readonly string BigWorldPacketDumpRootDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".runtime"));
        private static readonly string DefaultBigWorldPacketDumpDirectory = Path.Combine(BigWorldPacketDumpRootDirectory, "bigworld-packet-dumps");


        public Session(string id, TcpClient tcpClient)
        {
            this.id = id;
            client = tcpClient;
            // TODO: add session based configuration? maybe from database?
            log = new(typeof(Session), id, LogLevel.DEBUG, LogLevel.DEBUG);
            log.LogLevelColor[LogLevel.INFO] = ConsoleColor.Cyan;
            Task.Run(ClientLoop);
        }

        internal static object GetPlayerOperationLock(long playerId) =>
            PlayerOperationLocks.GetOrAdd(playerId, static _ => new object());

        private void InvokeRequestHandler(
            RequestPacketHandlerDelegate requestPacketHandler,
            Packet.Request request)
        {
            Player? currentPlayer = player;
            if (currentPlayer is null)
            {
                if (Volatile.Read(ref disconnectState) == 0)
                    requestPacketHandler.Invoke(this, request);
                return;
            }

            lock (GetPlayerOperationLock(currentPlayer.PlayerData.Id))
            {
                if (Volatile.Read(ref disconnectState) == 0)
                    requestPacketHandler.Invoke(this, request);
            }
        }

        public async void ClientLoop()
        {
            NetworkStream stream;
            try
            {
                stream = client.GetStream();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException) when (!client.Connected)
            {
                return;
            }
            int prevBuf = 0;
            byte[] msg = new byte[InitialReceiveBufferLength];

            while (client.Connected)
            {
                try
                {
                    bool readAnyBytes = false;

                    while (stream.DataAvailable)
                    {
                        if (prevBuf == msg.Length)
                            break;

                        int len = stream.Read(msg, prevBuf, msg.Length - prevBuf);
                        if (len <= 0)
                            break;

                        prevBuf += len;
                        readAnyBytes = true;
                    }

                    if (readAnyBytes)
                        lastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (prevBuf > 0)
                    {
                        List<Packet> packets = new();

                        int readbytes = 0;
                        while (readbytes < prevBuf)
                        {
                            int remainingBytes = prevBuf - readbytes;
                            if (remainingBytes < 4)
                                break;

                            int packetLen = BinaryPrimitives.ReadInt32LittleEndian(msg.AsSpan(readbytes, 4));
                            if (packetLen < 1)
                            {
                                prevBuf = 0;
                                readbytes = 0;
                                break;
                            }

                            if (packetLen > MaxReceivePacketLength)
                            {
                                log.Error($"Packet length {packetLen} exceeds maximum receive packet length {MaxReceivePacketLength}");
                                throw new InvalidDataException($"Packet length {packetLen} exceeds maximum receive packet length {MaxReceivePacketLength}");
                            }

                            if (packetLen > msg.Length - sizeof(int))
                            {
                                GrowReceiveBufferForPacket(ref msg, packetLen);
                                break;
                            }

                            if (packetLen > remainingBytes - sizeof(int))
                                break;

                            readbytes += 4;
                            byte[] packet = GC.AllocateUninitializedArray<byte>(packetLen);
                            Array.Copy(msg, readbytes, packet, 0, packetLen);
                            readbytes += packetLen;
                            Crypto.HaruCrypt.Decrypt(packet);

                            try
                            {
                                packets.Add(MessagePackSerializer.Deserialize<Packet>(packet, lz4Options));
                            }
                            catch (Exception)
                            {
                                log.Debug(BitConverter.ToString(msg).Replace("-", ""));
                                log.Debug($"PacketLen = {packetLen}, ReadLen = {prevBuf}");
                                log.Error("Failed to deserialize packet: " + BitConverter.ToString(packet).Replace("-", ""));
                            }
                        }

                        if (readbytes > 0)
                        {
                            int unreadBytes = prevBuf - readbytes;
                            if (unreadBytes > 0)
                                Buffer.BlockCopy(msg, readbytes, msg, 0, unreadBytes);
                            prevBuf = unreadBytes;
                        }
                        else if (prevBuf == msg.Length)
                        {
                            int pendingPacketLen = prevBuf >= sizeof(int)
                                ? BinaryPrimitives.ReadInt32LittleEndian(msg.AsSpan(0, sizeof(int)))
                                : 0;
                            log.Error($"Receive buffer filled without a complete packet; bufferedBytes={prevBuf}, pendingPacketLen={pendingPacketLen}");
                            throw new InvalidDataException($"Receive buffer filled without a complete packet; bufferedBytes={prevBuf}, pendingPacketLen={pendingPacketLen}");
                        }

                        foreach (var packet in packets)
                        {
                            byte[] debugContent = packet.Content;
                            try
                            {
                                switch (packet.Type)
                                {
                                    case Packet.ContentType.Request:
                                        Packet.Request request = MessagePackSerializer.Deserialize<Packet.Request>(packet.Content);
                                        debugContent = request.Content;
                                        ProbeBigWorldPacket("request", request.Name, request.Content, request.Id, packet.No);
                                        RequestPacketHandlerDelegate? requestPacketHandler = PacketFactory.GetRequestPacketHandler(request.Name);
                                        if (requestPacketHandler is not null)
                                        {
                                            // TODO: with new logger this will be unnecessary
                                            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                                                log.Info($"{request.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(request.Content)) : "")}");
                                            InvokeRequestHandler(requestPacketHandler, request);
                                        }
                                        else
                                        {
                                            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                                                log.Warn($"{request.Name} handler not found!{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(request.Content)) : "")}");
                                        }
                                        break;

                                    case Packet.ContentType.Push:
                                        Packet.Push push = MessagePackSerializer.Deserialize<Packet.Push>(packet.Content);
                                        debugContent = push.Content;
                                        ProbeBigWorldPacket("client-push", push.Name, push.Content, null, packet.No);
                                        if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                                        {
                                            if (IsKnownClientPush(push.Name))
                                                log.Info(push.Name);
                                            else
                                                log.Warn($"{push.Name} client push ignored!{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(push.Content)) : "")}");
                                        }
                                        break;

                                    case Packet.ContentType.Exception:
                                        Packet.Exception exception = MessagePackSerializer.Deserialize<Packet.Exception>(packet.Content);
                                        log.Error($"Exception packet received: {exception.Code}, {exception.Message}");
                                        break;

                                    default:
                                        log.Error($"Unknown packet received: {packet}");
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Error("Failed to invoke handler: " + ex.ToString() + $", Raw {packet.Type} packet: " + BitConverter.ToString(debugContent).Replace("-", ""));
                            }
                        }
                    }

                }
                catch (Exception)
                {
                    break;
                }
                await Task.Delay(10);
                // 10 sec timeout
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastPacketTime > 10000)
                    break;
            }

            DisconnectProtocol();
        }

        private void GrowReceiveBufferForPacket(ref byte[] buffer, int packetLen)
        {
            int requiredLength = checked(packetLen + sizeof(int));
            int newLength = buffer.Length;
            while (newLength < requiredLength)
            {
                int doubledLength = newLength << 1;
                if (doubledLength <= 0 || doubledLength > MaxReceivePacketLength + sizeof(int))
                {
                    newLength = MaxReceivePacketLength + sizeof(int);
                    break;
                }

                newLength = doubledLength;
            }

            if (newLength < requiredLength)
                newLength = requiredLength;

            log.Debug($"Growing receive buffer from {buffer.Length} to {newLength} for packetLen={packetLen}");
            Array.Resize(ref buffer, newLength);
        }

        public static bool IsKnownClientPush(string name)
        {
            return name switch
            {
                "BoardMutualRequest" => true,
                "ReconnectAck" => true,
                "DormOutRequest" => true,
                _ => false
            };
        }

        public void ContinuePushSequenceFrom(int lastMsgSeqNo)
        {
            if (lastMsgSeqNo > packetNo)
                packetNo = lastMsgSeqNo;
        }

        public void SendPush<T>(T push) where T : new()
        {
            Packet.Push packet = new()
            {
                Name = typeof(T).Name,
                Content = MessagePackSerializer.Serialize(push)
            };
            ProbeBigWorldPacket("push", packet.Name, packet.Content, null, packetNo + 1);
            ProbeEquipmentPush(packet.Name, push);
            Send(new Packet()
            {
                No = ++packetNo,
                Type = Packet.ContentType.Push,
                Content = MessagePackSerializer.Serialize(packet)
            });
            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                log.Info($"{packet.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + JsonConvert.SerializeObject(push)) : "")}");
        }

        public void SendPush(string name, byte[] push)
        {
            Packet.Push packet = new()
            {
                Name = name,
                Content = push
            };
            ProbeBigWorldPacket("push", packet.Name, packet.Content, null, packetNo + 1);
            ProbeEquipmentPush(name, push);
            Send(new Packet()
            {
                No = ++packetNo,
                Type = Packet.ContentType.Push,
                Content = MessagePackSerializer.Serialize(packet)
            });
            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                log.Info($"{name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(push)) : "")}");
        }

        private static readonly object EquipmentPushProbeLock = new();
        private static readonly string EquipmentPushProbePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".runtime", "equip-push-live.log"));

        private void ProbeEquipmentPush<T>(string name, T push)
        {
            string? value = Environment.GetEnvironmentVariable("ASCNET_EQUIP_PUSH_LOG");
            if (!string.Equals(value, "1", StringComparison.Ordinal)
                && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
                return;
            string? summary = name switch
            {
                nameof(NotifyLogin) when push is NotifyLogin notifyLogin => DescribeEquipList(notifyLogin.EquipList),
                nameof(NotifyEquipDataList) when push is NotifyEquipDataList notifyEquipDataList => DescribeEquipList(notifyEquipDataList.EquipDataList),
                _ when name.StartsWith("NotifyEquip", StringComparison.Ordinal) => push is byte[] bytes ? $"rawBytes={bytes.Length}" : $"payloadType={typeof(T).FullName}",
                _ => null
            };

            if (summary is null)
                return;

            try
            {
                string? directory = Path.GetDirectoryName(EquipmentPushProbePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string line = $"{DateTimeOffset.Now:O} session={id} packetNo={packetNo + 1} push={name} {summary}{Environment.NewLine}";
                lock (EquipmentPushProbeLock)
                {
                    File.AppendAllText(EquipmentPushProbePath, line);
                }
            }
            catch
            {
                // Probe logging must never affect gameplay packet delivery.
            }
        }

        private void ProbeBigWorldPacket(string direction, string name, byte[] content, int? requestId, int outerPacketNo)
        {
            if (!ShouldDumpBigWorldPacket(name) || !IsBigWorldPacketDumpEnabled())
                return;

            try
            {
                string dumpDirectory = GetBigWorldPacketDumpDirectory();
                Directory.CreateDirectory(dumpDirectory);
                long ordinal = System.Threading.Interlocked.Increment(ref BigWorldPacketDumpOrdinal);
                string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss.fffffffZ");
                string baseName = $"{timestamp}-{ordinal:D6}-{SanitizeDumpFileSegment(id)}-{SanitizeDumpFileSegment(direction)}-{SanitizeDumpFileSegment(name)}";
                string msgpackPath = Path.Combine(dumpDirectory, baseName + ".msgpack");
                string jsonPath = Path.Combine(dumpDirectory, baseName + ".json");
                string indexPath = Path.Combine(dumpDirectory, "index.jsonl");
                string line = JsonConvert.SerializeObject(new
                {
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    SessionId = id,
                    Direction = direction,
                    Name = name,
                    RequestId = requestId,
                    OuterPacketNo = outerPacketNo,
                    PayloadBytes = content.Length,
                    MessagePackPath = msgpackPath,
                    JsonPath = jsonPath
                }) + Environment.NewLine;

                lock (BigWorldPacketDumpLock)
                {
                    File.WriteAllBytes(msgpackPath, content);
                    File.WriteAllText(jsonPath, FormatMessagePackContent(content));
                    File.AppendAllText(indexPath, line);
                }
            }
            catch (Exception ex)
            {
                log.Warn($"BigWorld packet dump failed: {ex.Message}");
            }
        }

        private static bool IsBigWorldPacketDumpEnabled()
        {
            string? value = Environment.GetEnvironmentVariable("ASCNET_DUMP_BIGWORLD_PACKETS")
                ?? Environment.GetEnvironmentVariable("ASCNET_DUMP_BIGWORLD");
            return string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetBigWorldPacketDumpDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable("ASCNET_DUMP_BIGWORLD_DIR");
            if (string.IsNullOrWhiteSpace(configured))
                return DefaultBigWorldPacketDumpDirectory;

            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(BigWorldPacketDumpRootDirectory, configured));
        }

        private static bool ShouldDumpBigWorldPacket(string name)
        {
            return name.Contains("BigWorld", StringComparison.Ordinal)
                || name.StartsWith("DlcWorld", StringComparison.Ordinal)
                || name.StartsWith("XRpc", StringComparison.Ordinal)
                || name is "NotifySgDormData"
                    or "StartFightNotify"
                    or "LoadCompleteRequest"
                    or "LoadCompleteResponse"
                    or "EnterInstLevelRequest"
                    or "EnterInstLevelResponse"
                    or "LeaveWorldRequest"
                    or "LeaveWorldResponse";
        }

        private static string SanitizeDumpFileSegment(string value)
        {
            return new string(value.Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        }

        private static string DescribeEquipList(IReadOnlyCollection<EquipData>? equips)
        {
            if (equips is null)
                return "equipCount=null";

            if (equips.Count == 0)
                return "equipCount=0";

            uint minId = equips.Min(equip => equip.Id);
            uint maxId = equips.Max(equip => equip.Id);
            int recycledCount = equips.Count(equip => equip.IsRecycle);
            string templates = string.Join(",", equips.Take(24).Select(equip => equip.TemplateId));
            if (equips.Count > 24)
                templates += ",...";

            return $"equipCount={equips.Count} minId={minId} maxId={maxId} recycled={recycledCount} templates={templates}";
        }

        public void SendResponse<T>(T response, int clientSeq = 0) where T : new()
        {
            Packet.Response packet = new()
            {
                Id = clientSeq,
                Name = typeof(T).Name,
                Content = MessagePackSerializer.Serialize(response)
            };
            ProbeBigWorldPacket("response", packet.Name, packet.Content, packet.Id, 0);
            Send(new Packet()
            {
                No = 0,
                Type = Packet.ContentType.Response,
                Content = MessagePackSerializer.Serialize(packet)
            });
            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                log.Info($"{packet.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + JsonConvert.SerializeObject(response)) : "")}");
        }

        public void SendResponse(string name, byte[] responseContent, int clientSeq = 0)
        {
            Packet.Response packet = new()
            {
                Id = clientSeq,
                Name = name,
                Content = responseContent
            };
            ProbeBigWorldPacket("response", packet.Name, packet.Content, packet.Id, 0);
            Send(new Packet()
            {
                No = 0,
                Type = Packet.ContentType.Response,
                Content = MessagePackSerializer.Serialize(packet)
            });
            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                log.Info($"{name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(responseContent)) : "")}");
        }

        private void Send(Packet packet)
        {
            byte[] serializedPacket = MessagePackSerializer.Serialize(packet, lz4Options);
            Crypto.HaruCrypt.Encrypt(serializedPacket);

            byte[] sendBytes = GC.AllocateUninitializedArray<byte>(serializedPacket.Length + 4);

            BinaryPrimitives.WriteInt32LittleEndian(sendBytes.AsSpan()[0..4], serializedPacket.Length);
            Array.Copy(serializedPacket, 0, sendBytes, 4, serializedPacket.Length);

            client.GetStream().Write(sendBytes);
        }


        private static string FormatMessagePackContent(byte[] content)
        {
            try
            {
                return MessagePackSerializer.ConvertToJson(content);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    error = ex.Message,
                    raw = Convert.ToBase64String(content)
                });
            }
        }

        public void DisconnectProtocol()
        {
            Player? currentPlayer = player;
            if (currentPlayer is null)
            {
                DisconnectCore();
                return;
            }

            lock (GetPlayerOperationLock(currentPlayer.PlayerData.Id))
                DisconnectCore();
        }

        private void DisconnectCore()
        {
            if (!Server.Instance.Sessions.TryGetValue(id, out Session? registered)
                || !ReferenceEquals(registered, this))
            {
                return;
            }

            // DB save on disconnect
            Save();
            Volatile.Write(ref disconnectState, 1);

            log.Warn($"{id} disconnected");
            client.Close();
            Server.Instance.Sessions.TryRemove(id, out _);
        }

        public void Save()
        {
            player?.Save();
            character?.Save();
            stage?.Save();
            inventory?.Save();
            
            log.Info($"Saving session state...");
        }
    }
}
