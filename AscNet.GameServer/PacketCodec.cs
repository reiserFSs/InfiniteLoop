using System.Buffers.Binary;
using System.Net.Sockets;
using AscNet.Common.Util;
using MessagePack;

namespace AscNet.GameServer;

public static class PacketCodec
{
    public const int MaxFrameLength = 1 << 22;
    private static readonly MessagePackSerializerOptions OutboundOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
    // Native Dorm frames contain ordinary MessagePack, without Haru encryption or LZ4.
    private static readonly MessagePackSerializerOptions PlaintextOutboundOptions = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.None);

    public static byte[] Encode(Packet packet, bool encrypted = true)
    {
        byte[] body = MessagePackSerializer.Serialize(packet, encrypted ? OutboundOptions : PlaintextOutboundOptions);
        if (encrypted) Crypto.HaruCrypt.Encrypt(body);
        byte[] frame = GC.AllocateUninitializedArray<byte>(checked(body.Length + sizeof(int)));
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    // The caller owns the buffer; encrypted transport decrypts it in place.
    public static Packet Decode(byte[] body, MessagePackSerializerOptions? options = null, bool encrypted = true)
    {
        ValidateLength(body.Length);
        if (encrypted) Crypto.HaruCrypt.Decrypt(body);
        return MessagePackSerializer.Deserialize<Packet>(body, options ?? Packet.InboundOptions);
    }

    public static int ReadLength(ReadOnlySpan<byte> header)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        ValidateLength(length);
        return length;
    }

    private static void ValidateLength(int length)
    {
        if (length < 1 || length > MaxFrameLength)
            throw new InvalidDataException($"Invalid packet length {length}.");
    }

    public static async Task<Packet?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken,
        int maxFrameLength = MaxFrameLength, MessagePackSerializerOptions? options = null, bool encrypted = true)
    {
        byte[] header = new byte[sizeof(int)];
        int first = await stream.ReadAsync(header, cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(first), cancellationToken);
        int length = ReadLength(header);
        if (length > maxFrameLength) throw new InvalidDataException("Packet exceeds connection frame limit.");
        byte[] body = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(body, cancellationToken);
        return Decode(body, options, encrypted);
    }
}
