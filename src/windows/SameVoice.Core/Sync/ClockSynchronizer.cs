namespace SameVoice.Core.Sync;

/// <summary>
/// NTP-style clock offset estimator. Inputs and outputs are microseconds.
/// Offset = hostMonotonic - localMonotonic.
/// </summary>
public sealed class ClockSynchronizer
{
    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private long _currentOffsetUs;
    private bool _initialized;
    private long _lastAdjustUs;
    private int _sampleCount;

    public long CurrentOffsetUs
    {
        get { lock (_gate) return _currentOffsetUs; }
    }

    public bool IsReady
    {
        get { lock (_gate) return _initialized; }
    }

    public long BestRttUs
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count == 0 ? -1 : _samples.Min(s => s.RttUs);
            }
        }
    }

    public int SampleCount
    {
        get { lock (_gate) return _sampleCount; }
    }

    public void AddSample(long t1Local, long t2Host, long t3Host, long t4Local)
    {
        long roundTrip = (t4Local - t1Local) - (t3Host - t2Host);
        if (roundTrip < 0) roundTrip = 0;
        long offset = ((t2Host - t1Local) + (t3Host - t4Local)) / 2;

        lock (_gate)
        {
            _samples.Enqueue(new Sample(offset, roundTrip));
            while (_samples.Count > 8) _samples.Dequeue();
            _sampleCount++;

            var best = _samples.OrderBy(s => s.RttUs).First();
            long now = HighResClock.NowUs();
            if (!_initialized)
            {
                _currentOffsetUs = best.OffsetUs;
                _initialized = true;
                _lastAdjustUs = now;
                return;
            }

            // Correct slowly so timestamps never jump mid-stream. 2 ms/s is much
            // faster than normal crystal drift yet inaudible as a timing change.
            long elapsed = Math.Max(0, now - _lastAdjustUs);
            long maxStep = Math.Max(200, elapsed * 2 / 1000);
            long delta = best.OffsetUs - _currentOffsetUs;
            if (delta > maxStep) delta = maxStep;
            else if (delta < -maxStep) delta = -maxStep;
            _currentOffsetUs += delta;
            _lastAdjustUs = now;
        }
    }

    public long ToHostTime(long localUs)
    {
        lock (_gate) return localUs + _currentOffsetUs;
    }

    private readonly record struct Sample(long OffsetUs, long RttUs);
}
