namespace SameVoice.Core.Audio;

/// <summary>
/// Platform capture. Emits interleaved 48 kHz stereo PCM16 blocks.
/// The timestamp is a local monotonic value in microseconds at which the first
/// sample in the block was captured.
/// </summary>
public interface IAudioCapture : IDisposable
{
    event Action<short[], long>? DataAvailable;
    event Action<string>? Faulted;
    bool IsRunning { get; }
    string? LastError { get; }
    void Start();
    void Stop();
}

/// <summary>A platform playback sink. The callback fills a block of PCM16 frames.</summary>
public interface IAudioOutput : IDisposable
{
    int BlockFrames { get; }
    void Start(Func<short[], int, int> fill);
    void Stop();
    void SetVolume(float volume);
}

public delegate int AudioFillCallback(short[] buffer, int frames);

public sealed class NullAudioOutput : IAudioOutput
{
    public int BlockFrames => 480;
    private CancellationTokenSource? _cts;
    private float _volume = 1f;

    public void Start(Func<short[], int, int> fill)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var buffer = new short[BlockFrames * ProtocolConstants.Channels];
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                fill(buffer, BlockFrames);
                try { await Task.Delay(10, token); } catch (OperationCanceledException) { break; }
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void SetVolume(float volume) => _volume = Math.Clamp(volume, 0f, 1f);

    public void Dispose() => Stop();
}

/// <summary>
/// Converts arbitrary PCM byte layouts into interleaved float stereo at 48 kHz.
/// Supports PCM16, PCM24, PCM32, float32, mono/stereo/multichannel, and linear resampling.
/// </summary>
public sealed class StereoPcmConverter48k
{
    private readonly int _sourceRate;
    private readonly int _sourceChannels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly bool _isPcm24;
    private readonly LinearResampler _resampler;
    private readonly List<float> _stereo = new(16_384);

    public StereoPcmConverter48k(int sourceRate, int sourceChannels, int bitsPerSample, bool isFloat)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (sourceChannels <= 0) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
        if (bitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException($"Unsupported bit depth: {bitsPerSample}");
        _sourceRate = sourceRate;
        _sourceChannels = sourceChannels;
        _bytesPerSample = bitsPerSample / 8;
        _isFloat = isFloat;
        _isPcm24 = !isFloat && bitsPerSample == 24;
        _resampler = new LinearResampler(sourceRate, ProtocolConstants.SampleRate, ProtocolConstants.Channels);
    }

    /// <returns>Interleaved stereo samples at 48 kHz.</returns>
    public short[] Convert(ReadOnlySpan<byte> input)
    {
        int frameSize = _sourceChannels * _bytesPerSample;
        if (frameSize <= 0) return Array.Empty<short>();
        int frames = input.Length / frameSize;
        _stereo.Clear();
        _stereo.EnsureCapacity(frames * 2);

        for (int frame = 0; frame < frames; frame++)
        {
            ReadOnlySpan<byte> data = input.Slice(frame * frameSize, frameSize);
            float left = ReadChannel(data, 0);
            float right = _sourceChannels >= 2 ? ReadChannel(data, 1) : left;
            _stereo.Add(left);
            _stereo.Add(right);
        }

        if (_stereo.Count == 0) return Array.Empty<short>();
        return _resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stereo));
    }

    private float ReadChannel(ReadOnlySpan<byte> data, int channel)
    {
        int offset = channel * _bytesPerSample;
        if (_isFloat)
            return Math.Clamp(BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..])), -1f, 1f);
        if (_isPcm24)
        {
            int v = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v / 8_388_608f;
        }
        if (_bytesPerSample == 2)
            return System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32_768f;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) / 2_147_483_648f;
    }
}

/// <summary>Streaming linear resampler for interleaved stereo floats.</summary>
public sealed class LinearResampler
{
    private readonly double _ratio;
    private readonly int _channels;
    private float[] _pending = new float[8192];
    private int _pendingCount;
    private double _position;

    public LinearResampler(int inputRate, int outputRate, int channels)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException();
        _ratio = (double)inputRate / outputRate;
        _channels = channels;
    }

    public short[] Process(ReadOnlySpan<float> input)
    {
        if (_ratio == 1.0 && _pendingCount == 0)
            return FloatsToPcm16(input);

        Append(input);
        int inputFrames = _pendingCount / _channels;
        if (inputFrames < 2)
            return Array.Empty<short>();

        int estimated = Math.Max(0, (int)((inputFrames - 1 - _position) / _ratio));
        var output = new List<short>(estimated * _channels);

        while (_position + 1.0 < inputFrames)
        {
            int i = (int)_position;
            double frac = _position - i;
            for (int c = 0; c < _channels; c++)
            {
                float a = _pending[i * _channels + c];
                float b = _pending[(i + 1) * _channels + c];
                float v = a + (b - a) * (float)frac;
                output.Add(ToPcm16(v));
            }
            _position += _ratio;
        }

        int consumedFrames = Math.Min((int)_position, inputFrames - 1);
        if (consumedFrames > 0)
        {
            int consumed = consumedFrames * _channels;
            int remaining = _pendingCount - consumed;
            Array.Copy(_pending, consumed, _pending, 0, remaining);
            _pendingCount = remaining;
            _position -= consumedFrames;
        }

        return output.ToArray();
    }

    private void Append(ReadOnlySpan<float> input)
    {
        int needed = _pendingCount + input.Length;
        if (needed > _pending.Length)
        {
            int size = _pending.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref _pending, size);
        }
        input.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount += input.Length;
    }

    private static short[] FloatsToPcm16(ReadOnlySpan<float> input)
    {
        var result = new short[input.Length];
        for (int i = 0; i < input.Length; i++) result[i] = ToPcm16(input[i]);
        return result;
    }

    private static short ToPcm16(float value)
    {
        float clamped = Math.Clamp(value, -1f, 1f);
        return (short)Math.Round(clamped * 32767f);
    }
}
