namespace SameVoice.Core;

public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;

    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8; // 4
    public const int PacketFrames = 240;                          // 5 ms
    public const int PacketBytes = PacketFrames * BytesPerFrame;   // 960
    public const int PacketDurationMs = 5;

    public const int DefaultControlPort = 47_820;
    public const int DiscoveryPort = 47_821;
    public const int DefaultAudioPort = 47_822;

    public const string DiscoveryMagic = "SVD1";
    public const string AudioMagic = "SVA1";

    public const byte PacketTypeAudio = 1;
    public const byte PacketTypePing = 2;
    public const byte PacketTypePong = 3;

    public const int AudioHeaderSize = 36;

    /// <summary>同步优先：更大的抖动缓冲，音源之间严格对齐。</summary>
    public const int BalancedBufferMs = 240;

    /// <summary>最低延迟：仍然保留足够的 Wi-Fi 抖动余量，避免持续断音。</summary>
    public const int LowLatencyBufferMs = 120;
}

public enum HostMode
{
    Balanced = 0,
    LowLatency = 1
}

public static class HostModeExtensions
{
    public static string ToWire(this HostMode mode) =>
        mode == HostMode.LowLatency ? "low" : "balanced";

    public static HostMode FromWire(string? value) =>
        string.Equals(value, "low", StringComparison.OrdinalIgnoreCase)
            ? HostMode.LowLatency
            : HostMode.Balanced;

    public static int BufferMs(this HostMode mode) =>
        mode == HostMode.LowLatency
            ? ProtocolConstants.LowLatencyBufferMs
            : ProtocolConstants.BalancedBufferMs;

    public static string DisplayName(this HostMode mode) =>
        mode == HostMode.LowLatency ? "最低延迟" : "同步优先";
}

public static class HighResClock
{
    private static readonly long Frequency = System.Diagnostics.Stopwatch.Frequency;

    /// <summary>单调时钟，单位微秒。只在单台设备内部比较。</summary>
    public static long NowUs()
    {
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        return ticks / Frequency * 1_000_000L + ticks % Frequency * 1_000_000L / Frequency;
    }
}
