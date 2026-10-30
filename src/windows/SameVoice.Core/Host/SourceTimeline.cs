namespace SameVoice.Core.Host;

/// <summary>
/// One source's timestamped PCM queue. Packets are scheduled on the host's
/// absolute output-frame timeline, so several devices can be mixed sample-aligned.
/// </summary>
public sealed class SourceTimeline
{
    private sealed class Chunk
    {
        public long StartFrame;
        public short[] Samples = Array.Empty<short>();
        public int SampleOffsetFrames;
        public int Frames;
    }

    private readonly object _gate = new();
    private readonly List<Chunk> _chunks = new(24);
    private readonly int _sampleRate;

    public SourceTimeline(int sampleRate = ProtocolConstants.SampleRate)
    {
        _sampleRate = sampleRate;
    }

    public float Volume { get; set; } = 0.8f;
    public long TotalFramesArrived { get; private set; }
    public long DroppedChunks { get; private set; }
    public long LastArrivalTicks { get; private set; }

    public int ChunkCount
    {
        get { lock (_gate) return _chunks.Count; }
    }

    public void Clear()
    {
        lock (_gate) _chunks.Clear();
    }

    public void Push(long startFrame, short[] samples, int frames)
    {
        if (frames <= 0) return;
        if (samples.Length < frames * ProtocolConstants.Channels)
            frames = samples.Length / ProtocolConstants.Channels;
        if (frames <= 0) return;

        lock (_gate)
        {
            LastArrivalTicks = Environment.TickCount64;
            int sampleSkip = 0;

            if (_chunks.Count > 0)
            {
                Chunk tail = _chunks[^1];
                long tailStart = tail.StartFrame;
                long tailEnd = tail.StartFrame + tail.Frames;

                if (startFrame < tailStart)
                {
                    // Older packet arrived after a newer one. Dropping it keeps
                    // the aligned timeline deterministic.
                    DroppedChunks++;
                    return;
                }

                if (startFrame < tailEnd)
                {
                    if (startFrame > tailStart)
                    {
                        // Small overlap caused by timestamp rounding or a slight
                        // clock correction. Trim the old tail; the newer data wins.
                        int newTailFrames = (int)(startFrame - tailStart);
                        if (newTailFrames <= 0) _chunks.RemoveAt(_chunks.Count - 1);
                        else tail.Frames = newTailFrames;
                    }
                    else
                    {
                        // New packet starts inside the newest chunk. Skip the
                        // already buffered head.
                        sampleSkip = (int)(tailEnd - startFrame);
                        startFrame = tailEnd;
                        if (sampleSkip >= frames)
                        {
                            DroppedChunks++;
                            return;
                        }
                    }
                }

                while (_chunks.Count > 1)
                {
                    Chunk previous = _chunks[^2];
                    if (previous.StartFrame + previous.Frames > startFrame)
                        _chunks.RemoveAt(_chunks.Count - 2);
                    else
                        break;
                }
            }

            frames -= sampleSkip;
            if (frames <= 0)
            {
                DroppedChunks++;
                return;
            }

            _chunks.Add(new Chunk
            {
                StartFrame = startFrame,
                Samples = samples,
                SampleOffsetFrames = sampleSkip,
                Frames = frames
            });
            TotalFramesArrived += frames;

            if (_chunks.Count > 800)
            {
                int remove = _chunks.Count - 800;
                _chunks.RemoveRange(0, remove);
                DroppedChunks += remove;
            }
        }
    }

    /// <summary>Mix the chunk samples overlapping this output block.</summary>
    public bool MixInto(float[] accumulator, long blockStartFrame, int blockFrames, float gain)
    {
        bool contributed = false;
        lock (_gate)
        {
            long blockEnd = blockStartFrame + blockFrames;

            int remove = 0;
            while (remove < _chunks.Count && _chunks[remove].StartFrame + _chunks[remove].Frames <= blockStartFrame)
                remove++;
            if (remove > 0) _chunks.RemoveRange(0, remove);

            foreach (var chunk in _chunks)
            {
                long chunkEnd = chunk.StartFrame + chunk.Frames;
                if (chunk.StartFrame >= blockEnd) break;
                long overlapStart = Math.Max(chunk.StartFrame, blockStartFrame);
                long overlapEnd = Math.Min(chunkEnd, blockEnd);
                if (overlapEnd <= overlapStart) continue;

                int sourceOffset = (int)(overlapStart - chunk.StartFrame) + chunk.SampleOffsetFrames;
                int destOffset = (int)(overlapStart - blockStartFrame);
                int count = (int)(overlapEnd - overlapStart);
                for (int i = 0; i < count; i++)
                {
                    int si = (sourceOffset + i) * ProtocolConstants.Channels;
                    int di = (destOffset + i) * ProtocolConstants.Channels;
                    if (si + 1 >= chunk.Samples.Length || di + 1 >= accumulator.Length) break;
                    accumulator[di] += chunk.Samples[si] / 32768f * gain;
                    accumulator[di + 1] += chunk.Samples[si + 1] / 32768f * gain;
                }
                contributed = true;
            }
        }
        return contributed;
    }

    public double GetBufferAheadMs(long blockStartFrame)
    {
        lock (_gate)
        {
            if (_chunks.Count == 0) return 0;
            long newestEnd = _chunks[^1].StartFrame + _chunks[^1].Frames;
            return Math.Max(0, (newestEnd - blockStartFrame) * 1000.0 / _sampleRate);
        }
    }

    public bool HasDataAfter(long blockStartFrame)
    {
        lock (_gate)
        {
            if (_chunks.Count == 0) return false;
            return _chunks[^1].StartFrame + _chunks[^1].Frames > blockStartFrame;
        }
    }
}
