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
    public class Session
    {
        public readonly string id;
        public readonly TcpClient client;
        public Player player = default!;
        public Character character = default!;
        public Stage stage = default!;
        public Fight? fight;
        public Inventory inventory = default!;
        public readonly Logger log;
        private long lastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private ushort packetNo = 0;
        private readonly MessagePackSerializerOptions lz4Options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

        public Session(string id, TcpClient tcpClient)
        {
            this.id = id;
            client = tcpClient;
            // TODO: add session based configuration? maybe from database?
            log = new(typeof(Session), id, LogLevel.DEBUG, LogLevel.DEBUG);
            log.LogLevelColor[LogLevel.INFO] = ConsoleColor.Cyan;
            Task.Run(ClientLoop);
        }

        public async void ClientLoop()
        {
            NetworkStream stream = client.GetStream();
            int prevBuf = 0;
            byte[] msg = new byte[1 << 16];

            while (client.Connected)
            {
                try
                {
                    int len = 0;

                    read:
                    while (stream.DataAvailable)
                    {
                        len = stream.Read(msg, prevBuf, msg.Length - prevBuf);
                        prevBuf += len;
                    }

                    if (prevBuf > 0)
                    {
                        lastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        List<Packet> packets = new();

                        int readbytes = 0;
                        while (readbytes < prevBuf)
                        {
                            int packetLen = BinaryPrimitives.ReadInt32LittleEndian(msg.AsSpan()[readbytes..]);
                            if (prevBuf > 0)
                                readbytes += 4;
                            if (packetLen < 1)
                            {
                                prevBuf = 0;
                                break;
                            }
                            if (packetLen > prevBuf)
                            {
                                goto read;
                            }
                            else
                            {
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
                        }
                        prevBuf = 0;

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

                                        RequestPacketHandlerDelegate? requestPacketHandler = PacketFactory.GetRequestPacketHandler(request.Name);
                                        if (requestPacketHandler is not null)
                                        {
                                            // TODO: with new logger this will be unnecessary
                                            if (Common.Common.config.VerboseLevel > VerboseLevel.Silent)
                                                log.Info($"{request.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(request.Content)) : "")}");
                                            requestPacketHandler.Invoke(this, request);
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
                                        if (IsKnownClientPush(push.Name))
                                            log.Info(push.Name);
                                        else
                                            log.Warn($"{push.Name} client push ignored!{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(push.Content)) : "")}");
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

        public static bool IsKnownClientPush(string name)
        {
            return name switch
            {
                "BoardMutualRequest" => true,
                _ => false
            };
        }

        public void SendPush<T>(T push) where T : new()
        {
            Packet.Push packet = new()
            {
                Name = typeof(T).Name,
                Content = MessagePackSerializer.Serialize(push)
            };
            ProbeEquipmentPush(packet.Name, push);
            Send(new Packet()
            {
                No = ++packetNo,
                Type = Packet.ContentType.Push,
                Content = MessagePackSerializer.Serialize(packet)
            });
            log.Info($"{packet.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + JsonConvert.SerializeObject(push)) : "")}");
        }

        public void SendPush(string name, byte[] push)
        {
            Packet.Push packet = new()
            {
                Name = name,
                Content = push
            };
            ProbeEquipmentPush(name, push);
            Send(new Packet()
            {
                No = ++packetNo,
                Type = Packet.ContentType.Push,
                Content = MessagePackSerializer.Serialize(packet)
            });
            log.Info($"{name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + FormatMessagePackContent(push)) : "")}");
        }

        private static readonly object EquipmentPushProbeLock = new();
        private static readonly string EquipmentPushProbePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".runtime", "equip-push-live.log"));

        private void ProbeEquipmentPush<T>(string name, T push)
        {
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
            Send(new Packet()
            {
                No = 0,
                Type = Packet.ContentType.Response,
                Content = MessagePackSerializer.Serialize(packet)
            });
            log.Info($"{packet.Name}{(Common.Common.config.VerboseLevel >= VerboseLevel.Debug ? (", " + JsonConvert.SerializeObject(response)) : "")}");
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
            if (Server.Instance.Sessions.GetValueOrDefault(id) is null)
                return;

            // DB save on disconnect
            Save();

            log.Warn($"{id} disconnected");
            client.Close();
            Server.Instance.Sessions.Remove(id);
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
