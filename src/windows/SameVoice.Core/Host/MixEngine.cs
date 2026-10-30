using SameVoice.Core.Audio;

namespace SameVoice.Core.Host;

public sealed record SourceMixInfo(uint SourceId, double BufferAheadMs, float Volume, bool HasData);

/// <summary>
/// Sample-aligned mixer. Source packets carry host-clock capture timestamps and
/// are scheduled at captureTime + targetBuffer on a single output-frame timeline.
/// </summary>
public sealed class MixEngine
{
    private readonly object _sourcesGate = new();
    private readonly Dictionary<uint, SourceTimeline> _sources = new();
    private readonly object _renderGate = new();
    private float[] _accumulator = new float[ProtocolConstants.SampleRate * 2];
    private long _startUs;
    private long _renderedFrames;
    private int _targetBufferMs = ProtocolConstants.BalancedBufferMs;
    private float _masterVolume = 0.9f;
    private bool _running;
    private long _lastReanchorTicks;
    private long _reanchorCount;

    public long StartHostUs => _startUs;
    public long RenderedFrames => _renderedFrames;
    public double Peak { get; private set; }
    public long ReanchorCount => Interlocked.Read(ref _reanchorCount);
    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    public int TargetBufferMs => _targetBufferMs;

    public void Start()
    {
        lock (_renderGate)
        {
            _startUs = HighResClock.NowUs();
            _renderedFrames = 0;
            _running = true;
            Peak = 0;
        }
    }

    public void Stop()
    {
        lock (_renderGate) _running = false;
    }

    public SourceTimeline AddSource(uint sourceId)
    {
        var timeline = new SourceTimeline();
        lock (_sourcesGate) _sources[sourceId] = timeline;
        return timeline;
    }

    public void RemoveSource(uint sourceId)
    {
        lock (_sourcesGate) _sources.Remove(sourceId);
    }

    public void ClearBuffers()
    {
        SourceTimeline[] timelines;
        lock (_sourcesGate) timelines = _sources.Values.ToArray();
        foreach (var timeline in timelines) timeline.Clear();
    }

    public void SetMode(int targetBufferMs)
    {
        lock (_renderGate)
        {
            _targetBufferMs = Math.Clamp(targetBufferMs, 20, 2000);
        }
        ClearBuffers();
    }

    /// <summary>Push one PCM packet captured at <paramref name="captureHostUs"/>.</summary>
    public void PushAudio(uint sourceId, long captureHostUs, short[] samples, int frames)
    {
        SourceTimeline? timeline;
        lock (_sourcesGate) _sources.TryGetValue(sourceId, out timeline);
        if (timeline is null) return;

        long startUs;
        long targetMs;
        long rendered;
        bool reanchor = false;
        lock (_renderGate)
        {
            if (!_running) return;
            // If the output device/thread falls behind wall-clock time, blindly
            // scheduling by absolute host time would grow the latency forever.
            // Move the scheduling origin forward and drop the outdated queue so
            // the live feed returns to the target latency.
            long renderedTimeUs = _startUs + _renderedFrames * 1_000_000L / ProtocolConstants.SampleRate;
            long lagUs = HighResClock.NowUs() - renderedTimeUs;
            long nowTicks = Environment.TickCount64;
            if (lagUs > 300_000 && nowTicks - _lastReanchorTicks > 800)
            {
                _lastReanchorTicks = nowTicks;
                _startUs += lagUs;
                Interlocked.Increment(ref _reanchorCount);
                reanchor = true;
            }

            startUs = _startUs;
            targetMs = _targetBufferMs;
            rendered = _renderedFrames;
        }

        if (reanchor) ClearBuffers();

        double relativeUs = captureHostUs + targetMs * 1000.0 - startUs;
        long startFrame = (long)Math.Round(relativeUs * ProtocolConstants.SampleRate / 1_000_000.0);
        long endFrame = startFrame + frames;

        if (endFrame <= rendered - ProtocolConstants.SampleRate / 4)
            return; // far too late
        if (startFrame > rendered + ProtocolConstants.SampleRate * 10L)
            return; // more than 10 seconds in the future: corrupt timestamp

        timeline.Push(startFrame, samples, frames);
    }

    /// <summary>Render a block. Called only from the platform audio thread.</summary>
    public int Render(short[] destination, int frames)
    {
        lock (_renderGate)
        {
            if (destination.Length < frames * ProtocolConstants.Channels)
                throw new ArgumentException("Destination buffer is too small.", nameof(destination));

            Array.Clear(destination, 0, frames * ProtocolConstants.Channels);
            if (!_running)
            {
                Peak = 0;
                return frames;
            }

            int needed = frames * ProtocolConstants.Channels;
            if (_accumulator.Length < needed) _accumulator = new float[needed];
            Array.Clear(_accumulator, 0, needed);

            SourceTimeline[] timelines;
            lock (_sourcesGate)
            {
                timelines = _sources.Values.ToArray();
            }

            int activeCount = CountActive(timelines, _renderedFrames);
            float normalization = activeCount > 1 ? 1f / activeCount : 1f;
            for (int i = 0; i < timelines.Length; i++)
            {
                // Keep a constant output headroom while more sources join. Without
                // normalization two or three loud sources can push the limiter
                // constantly, which is audible as harsh distortion.
                timelines[i].MixInto(_accumulator, _renderedFrames, frames, timelines[i].Volume * normalization);
            }

            float peak = 0;
            for (int i = 0; i < needed; i++)
            {
                float sample = _accumulator[i] * _masterVolume;
                float abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
                sample = SoftLimit(sample);
                destination[i] = (short)Math.Clamp((int)MathF.Round(sample * 32767f), -32768, 32767);
            }

            Peak = peak;
            _renderedFrames += frames;
            return frames;
        }
    }

    public IReadOnlyList<SourceMixInfo> GetSourceInfo()
    {
        SourceTimeline[] timelines;
        uint[] ids;
        lock (_sourcesGate)
        {
            timelines = _sources.Values.ToArray();
            ids = _sources.Keys.ToArray();
        }

        long rendered;
        lock (_renderGate) rendered = _renderedFrames;
        var result = new SourceMixInfo[timelines.Length];
        for (int i = 0; i < timelines.Length; i++)
        {
            result[i] = new SourceMixInfo(
                ids[i],
                timelines[i].GetBufferAheadMs(rendered),
                timelines[i].Volume,
                timelines[i].HasDataAfter(rendered));
        }
        return result;
    }

    public double GetBufferAheadMs(uint sourceId)
    {
        SourceTimeline? timeline;
        lock (_sourcesGate) _sources.TryGetValue(sourceId, out timeline);
        if (timeline is null) return 0;
        long rendered;
        lock (_renderGate) rendered = _renderedFrames;
        return timeline.GetBufferAheadMs(rendered);
    }

    public void SetSourceVolume(uint sourceId, float volume)
    {
        SourceTimeline? timeline;
        lock (_sourcesGate) _sources.TryGetValue(sourceId, out timeline);
        if (timeline is not null) timeline.Volume = Math.Clamp(volume, 0f, 1f);
    }

    private static int CountActive(SourceTimeline[] timelines, long frame)
    {
        int count = 0;
        foreach (var timeline in timelines)
        {
            if (timeline.HasDataAfter(frame)) count++;
        }
        return count;
    }

    private static float SoftLimit(float x)
    {
        // Leave most of the normal listening range untouched. The limiter only
        // engages near full scale, so a single quiet source stays transparent
        // and multiple sources are protected without constant compression.
        const float knee = 0.90f;
        float abs = Math.Abs(x);
        if (abs <= knee) return x;
        float over = (abs - knee) / (1f - knee);
        float limited = knee + (1f - knee) * MathF.Tanh(over);
        return MathF.CopySign(limited, x);
    }
}
