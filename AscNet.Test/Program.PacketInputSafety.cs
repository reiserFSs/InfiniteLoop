using System.Buffers;
using System.Buffers.Binary;
using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer;
using MessagePack;
using AscNet.GameServer.Handlers;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static void ValidateReportBanChatCompatibility()
        {
            // EN XChatManager.lua:1121-1130 consumes only Code; validation/log-only behavior is local policy.
            RequestPacketHandlerDelegate handler = GetRegisteredRequestHandler(nameof(ReportBanChatRequest));
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(88_092), sessionId: "chat-report");
            byte[] playerBefore = harness.Session.player.ToBson();
            byte[] characterBefore = harness.Session.character.ToBson();
            byte[] inventoryBefore = harness.Session.inventory.ToBson();
            TextWriter originalOutput = Console.Out;
            using StringWriter output = new();
            try
            {
                Console.SetOut(output);
                int packetId = 1;
                foreach (int times in new[] { 3, 4 })
                {
                    string previousLog = output.ToString();
                    Check(MessagePackSerializer.Serialize(new ReportBanChatRequest { Times = times }), 0);
                    string reportLog = output.ToString()[previousLog.Length..];
                    AssertEqual(true, reportLog.Contains($"uid={harness.Session.player.PlayerData.Id} Times={times}", StringComparison.Ordinal),
                        "accepted report records player and supplied count");
                }

                // Invalid counts and malformed shapes must not be acknowledged or recorded as accepted reports.
                foreach (byte[] invalid in new byte[][]
                {
                    MessagePackSerializer.Serialize(new Dictionary<string, int>()),
                    MessagePackSerializer.Serialize(new ReportBanChatRequest { Times = 0 }),
                    MessagePackSerializer.Serialize(new ReportBanChatRequest { Times = -1 }),
                    MessagePackSerializer.Serialize(new { Times = "not-an-integer" }),
                    MessagePackSerializer.Serialize(new { Times = (long)int.MaxValue + 1 }),
                    [0xc0],
                    [0xc1]
                })
                {
                    string previousLog = output.ToString();
                    Check(invalid, 5);
                    AssertEqual(false, output.ToString()[previousLog.Length..].Contains("Repeated chat report", StringComparison.Ordinal),
                        "invalid report is not logged as accepted");
                }

                AssertEqual(true, playerBefore.AsSpan().SequenceEqual(harness.Session.player.ToBson()), "reports leave player unchanged");
                AssertEqual(true, characterBefore.AsSpan().SequenceEqual(harness.Session.character.ToBson()), "reports leave character unchanged");
                AssertEqual(true, inventoryBefore.AsSpan().SequenceEqual(harness.Session.inventory.ToBson()), "reports leave inventory unchanged");

                void Check(byte[] content, int expectedCode)
                {
                    handler(harness.Session, new Packet.Request { Id = packetId, Name = nameof(ReportBanChatRequest), Content = content });
                    Packet packet = harness.ReadPacket("chat report response");
                    AssertEqual(Packet.ContentType.Response, packet.Type, "chat report emits response without preceding push");
                    Packet.Response response = MessagePackSerializer.Deserialize<Packet.Response>(packet.Content);
                    AssertEqual(packetId++, response.Id, "chat report response correlation");
                    AssertEqual(nameof(ReportBanChatResponse), response.Name, "chat report response name");
                    JObject payload = JObject.Parse(MessagePackSerializer.ConvertToJson(response.Content));
                    AssertEqual("Code", string.Join(",", payload.Properties().Select(property => property.Name)), "chat report named-key response");
                    AssertEqual(expectedCode, payload.Value<int>("Code"), "chat report result code");
                    AssertNoAvailablePacket(harness, "chat report emits no unsolicited push");
                }
            }
            finally
            {
                harness.Dispose();
                harness.Session.Completion.Wait(TimeSpan.FromSeconds(5));
                Console.SetOut(originalOutput);
            }
        }

        private static void ValidatePacketInputSafety()
        {
            MessagePackSerializerOptions compressed = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block).WithCompressionMinLength(1);
            byte[] encoded = MessagePackSerializer.Serialize(new Packet { No = 7, Type = Packet.ContentType.Request, Content = new byte[4096] }, compressed);
            AssertEqual(4096, MessagePackSerializer.Deserialize<Packet>(encoded, Packet.InboundOptions).Content.Length, "valid compressed inbound packet");
            MessagePackSerializerOptions smallLimit = Packet.InboundOptions.WithSecurity(Packet.InboundOptions.Security.WithMaximumDecompressedSize(1024));
            AssertPacketDecodeRejected(() => MessagePackSerializer.Deserialize<Packet>(encoded, smallLimit), "compressed decoded-size limit");

            // An advertised oversized decoded length must fail before trying to allocate/decompress it.
            ArrayBufferWriter<byte> extensionBody = new();
            MessagePackWriter bodyWriter = new(extensionBody);
            bodyWriter.Write(Packet.InboundOptions.Security.MaximumDecompressedSize + 1);
            bodyWriter.Flush();
            ArrayBufferWriter<byte> oversized = new();
            MessagePackWriter extensionWriter = new(oversized);
            extensionWriter.WriteExtensionFormat(new ExtensionResult(ReservedExtensionTypeCodes.Lz4Block, extensionBody.WrittenSpan.ToArray()));
            extensionWriter.Flush();
            AssertPacketDecodeRejected(() => MessagePackSerializer.Deserialize<Packet>(oversized.WrittenMemory, Packet.InboundOptions), "production advertised decoded-size limit");

            byte[] deep = Enumerable.Repeat((byte)0x91, Packet.InboundOptions.Security.MaximumObjectGraphDepth + 1).Append((byte)0xc0).ToArray();
            AssertPacketDecodeRejected(() => new Packet.Request { Content = deep }.Deserialize<object>(), "nested request depth protection");
            AssertEqual("valid", new Packet.Request { Content = MessagePackSerializer.Serialize("valid") }.Deserialize<string>(), "valid uncompressed request body");
            AssertEqual(new string('x', 2048), new Packet.Request { Content = MessagePackSerializer.Serialize(new string('x', 2048), compressed) }.Deserialize<string>(), "valid compressed request body");

            const string requestName = "PacketInputSafetyRequest";
            const string failureName = "PacketInputSafetyFailureRequest";
            const string secret = "packet-secret-never-log";
            const string missingName = "PacketInputSafetyMissingRequest";
            const string injectedName = "Missing\r\nforged-entry\u001b[31m\u0085\u2028";
            string oversizedName = new('z', 16 * 1024);
            Dictionary<string, RequestPacketHandlerDelegate> handlers = new(PacketFactory.ReqHandlers);
            TextWriter originalOutput = Console.Out;
            VerboseLevel originalVerbosity = AscNet.Common.Common.config.VerboseLevel;
            using StringWriter output = new();
            LoopbackSessionHarness? harness = null;
            try
            {
                PacketFactory.ReqHandlers[requestName] = (session, request) =>
                {
                    AssertEqual(secret, request.Deserialize<string>(), "live session request payload");
                    session.SendResponse(new HeartbeatResponse { UtcServerTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, request.Id);
                };
                PacketFactory.ReqHandlers[failureName] = (_, _) => throw new InvalidOperationException(secret);
                AscNet.Common.Common.config.VerboseLevel = VerboseLevel.Debug;
                Console.SetOut(output);
                harness = new(CreateDrawCompatibilityCharacter(88_091), sessionId: "packet-input-safety");
                for (int index = 0; index < 2; index++)
                {
                    Packet request = new()
                    {
                        Type = Packet.ContentType.Request,
                        Content = MessagePackSerializer.Serialize(new Packet.Request { Name = requestName, Id = index + 1, Content = MessagePackSerializer.Serialize(secret) })
                    };
                    harness.WriteClientBytes(FramePacketInput(MessagePackSerializer.Serialize(request, index == 0 ? MessagePackSerializerOptions.Standard : compressed)));
                    AssertHeartbeatResponse(harness, index + 1, "live compressed/uncompressed request");
                }

                // Both small and grown receive buffers must produce bounded diagnostics, not raw hex dumps.
                harness.WriteClientBytes(FramePacketInput([0xc1]));
                byte[] malformed = new byte[80 * 1024];
                malformed[0] = 0xc1;
                System.Text.Encoding.UTF8.GetBytes(secret).CopyTo(malformed, 1);
                harness.WriteClientBytes(FramePacketInput(malformed));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(failureName, 3, new string('x', 4096) + secret));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(missingName, 4, secret));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(injectedName, 6, secret));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(oversizedName, 7, secret));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(null!, 8, secret));
                harness.WriteClientBytes(FramePacketInput(MessagePackSerializer.Serialize(new Packet
                {
                    Type = Packet.ContentType.Exception,
                    Content = MessagePackSerializer.Serialize(new Packet.Exception { Code = 1, Message = secret })
                })));
                harness.WriteClientBytes(LoopbackSessionHarness.SerializeClientRequestFrame(requestName, 5, secret));
                AssertHeartbeatResponse(harness, 5, "malformed packets and handler failures preserve continuation");

                byte[] excessiveWireLength = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(excessiveWireLength, (1 << 22) + 1);
                harness.WriteClientBytes(excessiveWireLength);
                if (!harness.Session.Completion.Wait(TimeSpan.FromSeconds(5)))
                    throw new InvalidDataException("Oversized wire frame did not stop the receive loop.");
                string diagnostics = output.ToString();
                if (diagnostics.Length > 8192 || diagnostics.Contains(secret, StringComparison.Ordinal)
                    || diagnostics.Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(secret)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Inbound diagnostics disclosed payload data or exceeded their bound.");
                if (!diagnostics.Contains(nameof(MessagePackSerializationException), StringComparison.Ordinal)
                    || !diagnostics.Contains(nameof(InvalidOperationException), StringComparison.Ordinal))
                    throw new InvalidDataException("Inbound diagnostics omitted sanitized error types.");
                AssertEqual(true, diagnostics.Contains($"name=\"{missingName}\"", StringComparison.Ordinal), "missing handler name is visible");
                AssertEqual(true, diagnostics.Contains("Missing\\r\\nforged-entry\\u001B[31m\\u0085\\u2028", StringComparison.Ordinal), "missing handler controls are escaped");
                AssertEqual(false, diagnostics.Contains(injectedName, StringComparison.Ordinal), "missing handler cannot inject raw controls");
                AssertEqual(false, diagnostics.Contains(new string('z', 129), StringComparison.Ordinal), "missing handler name is bounded");
            }
            finally
            {
                harness?.Dispose();
                harness?.Session.Completion.Wait(TimeSpan.FromSeconds(5));
                Console.SetOut(originalOutput);
                AscNet.Common.Common.config.VerboseLevel = originalVerbosity;
                PacketFactory.ReqHandlers.Clear();
                foreach (var handler in handlers)
                    PacketFactory.ReqHandlers.Add(handler.Key, handler.Value);
            }
        }

        private static byte[] FramePacketInput(byte[] payload)
        {
            Crypto.HaruCrypt.Encrypt(payload);
            byte[] frame = new byte[sizeof(int) + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame, sizeof(int));
            return frame;
        }

        private static void AssertPacketDecodeRejected(Action decode, string name)
        {
            try { decode(); }
            catch (MessagePackSerializationException) { return; }
            throw new InvalidDataException($"{name}: untrusted input was accepted.");
        }
    }
}
