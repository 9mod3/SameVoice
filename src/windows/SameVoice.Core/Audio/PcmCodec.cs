namespace SameVoice.Core.Audio;

public static class PcmCodec
{
    public static short[] DecodePcm16(ReadOnlySpan<byte> bytes)
    {
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]);
        return samples;
    }

    public static byte[] EncodePcm16(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    public static void EncodePcm16Into(ReadOnlySpan<short> samples, Span<byte> destination)
    {
        if (destination.Length < samples.Length * 2)
            throw new ArgumentException("Destination too small.", nameof(destination));
        for (int i = 0; i < samples.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], samples[i]);
    }
}
