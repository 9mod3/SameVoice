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

    /// <summary>Pack normalized float samples as signed 24-bit little-endian PCM.</summary>
    public static byte[] EncodePcm24(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 3];
        EncodePcm24Into(samples, bytes);
        return bytes;
    }

    public static void EncodePcm24Into(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        if (destination.Length < samples.Length * 3)
            throw new ArgumentException("Destination too small.", nameof(destination));
        for (int i = 0; i < samples.Length; i++)
        {
            int value = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 8_388_607f);
            destination[i * 3] = (byte)value;
            destination[i * 3 + 1] = (byte)(value >> 8);
            destination[i * 3 + 2] = (byte)(value >> 16);
        }
    }

    public static float[] DecodePcm24ToFloat(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / 3];
        for (int i = 0; i < samples.Length; i++)
        {
            int value = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            samples[i] = value / 8_388_608f;
        }
        return samples;
    }
}
