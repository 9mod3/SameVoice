using System.Buffers.Binary;

namespace SameVoice.Core.Protocol;

/// <summary>
/// 36-byte UDP audio/clock packet. All integers are little-endian.
/// </summary>
public sealed class AudioPacket
{
    public byte Type { get; init; }
    public byte Flags { get; init; }
    public uint SessionId { get; init; }
    public ulong Token { get; init; }
    public uint Sequence { get; init; }
    public long CaptureUs { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public bool IsMuted => (Flags & 1) != 0;

    public static byte[] Encode(
        byte type,
        uint sessionId,
        ulong token,
        uint sequence,
        long captureUs,
        ReadOnlySpan<byte> payload,
        byte flags = 0)
    {
        var packet = new byte[ProtocolConstants.AudioHeaderSize + payload.Length];
        WriteHeader(packet, type, flags, sessionId, token, sequence, captureUs, payload.Length);
        payload.CopyTo(packet.AsSpan(ProtocolConstants.AudioHeaderSize));
        return packet;
    }

    /// <summary>Encode into a caller-owned buffer when it is large enough.</summary>
    public static int EncodeInto(
        byte[] destination,
        byte type,
        uint sessionId,
        ulong token,
        uint sequence,
        long captureUs,
        ReadOnlySpan<byte> payload,
        byte flags = 0)
    {
        int required = ProtocolConstants.AudioHeaderSize + payload.Length;
        if (destination.Length < required)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        WriteHeader(destination, type, flags, sessionId, token, sequence, captureUs, payload.Length);
        payload.CopyTo(destination.AsSpan(ProtocolConstants.AudioHeaderSize));
        return required;
    }

    private static void WriteHeader(
        byte[] packet,
        byte type,
        byte flags,
        uint sessionId,
        ulong token,
        uint sequence,
        long captureUs,
        int payloadLength)
    {
        packet[0] = (byte)'S';
        packet[1] = (byte)'V';
        packet[2] = (byte)'A';
        packet[3] = (byte)'1';
        packet[4] = ProtocolConstants.ProtocolVersion;
        packet[5] = type;
        packet[6] = flags;
        packet[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), sessionId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(12), token);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), unchecked((ulong)captureUs));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32), checked((ushort)payloadLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(34), 0);
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out AudioPacket? packet)
    {
        packet = null;
        if (data.Length < ProtocolConstants.AudioHeaderSize)
            return false;
        if (data[0] != 'S' || data[1] != 'V' || data[2] != 'A' || data[3] != '1')
            return false;
        if (data[4] != ProtocolConstants.ProtocolVersion)
            return false;

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[32..]);
        if (payloadLength > data.Length - ProtocolConstants.AudioHeaderSize)
            return false;

        packet = new AudioPacket
        {
            Type = data[5],
            Flags = data[6],
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            Token = BinaryPrimitives.ReadUInt64LittleEndian(data[12..]),
            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            CaptureUs = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(data[24..])),
            Payload = data.Slice(ProtocolConstants.AudioHeaderSize, payloadLength).ToArray()
        };
        return true;
    }

    public static byte[] EncodePong(uint sessionId, ulong token, uint requestSequence, long t1, long t2, long t3)
    {
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, unchecked((ulong)t1));
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], unchecked((ulong)t2));
        return Encode(ProtocolConstants.PacketTypePong, sessionId, token, requestSequence, t3, payload);
    }

    public static bool TryReadPongTimes(AudioPacket packet, out long t1, out long t2)
    {
        t1 = 0;
        t2 = 0;
        if (packet.Payload.Length < 16)
            return false;
        t1 = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(packet.Payload));
        t2 = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(packet.Payload.AsSpan(8)));
        return true;
    }
}
