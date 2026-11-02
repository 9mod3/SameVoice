using System.Runtime.InteropServices;
using SameVoice.Core;
using SameVoice.Core.Audio;

namespace SameVoice.App.Audio;

/// <summary>
/// Shared-mode Windows playback via winmm/waveOut. Shared mode means Windows
/// mixes SameVoice with every other application's audio; no exclusive access is taken.
/// </summary>
public sealed class WaveOutSink : IAudioOutput
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const ushort WaveFormatPcm = 1;
    private const uint WhdrDone = 0x00000001;
    private const int BufferCount = 8;

    private readonly object _gate = new();
    private readonly int _blockFrames = 480;
    private IntPtr _device;
    private IntPtr _headers;
    private IntPtr[] _buffers = Array.Empty<IntPtr>();
    private int _headerSize;
    private ManualResetEvent? _bufferEvent;
    private Thread? _thread;
    private volatile bool _running;
    private Func<short[], int, int>? _fill;
    private Exception? _lastError;
    private long _blocksQueued;
    private long _blocksCompleted;

    public int BlockFrames => _blockFrames;
    public float Volume { get; private set; } = 1f;
    public Exception? LastError => _lastError;
    public long BlocksQueued => Interlocked.Read(ref _blocksQueued);
    public long BlocksCompleted => Interlocked.Read(ref _blocksCompleted);

    public void Start(Func<short[], int, int> fill)
    {
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("播放已经启动。");
            _fill = fill;
            _lastError = null;
            Open();
            _running = true;
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => PlaybackLoop(ready))
            {
                IsBackground = true,
                Name = "SameVoice WaveOut",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            if (!ready.Wait(3000))
            {
                Stop();
                throw new InvalidOperationException("音频播放设备启动超时。");
            }
            if (_lastError is not null)
            {
                var error = _lastError;
                Stop();
                throw new InvalidOperationException($"无法打开默认播放设备：{error.Message}", error);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running && _device == IntPtr.Zero) return;
            _running = false;
            try { _bufferEvent?.Set(); } catch { /* ignore */ }
            try { _thread?.Join(1200); } catch { /* ignore */ }
            _thread = null;
            Close();
        }
    }

    public void SetVolume(float volume)
    {
        // The mix engine owns gain, so that per-source and master volume share one
        // consistent control path on every platform.
        Volume = Math.Clamp(volume, 0f, 2f);
    }

    private void PlaybackLoop(ManualResetEventSlim ready)
    {
        try
        {
            if (_device == IntPtr.Zero)
                throw new InvalidOperationException("waveOut device is not open.");
            if (_bufferEvent is null)
                throw new InvalidOperationException("waveOut event is not ready.");

            var buffer = new short[_blockFrames * ProtocolConstants.Channels];
            for (int i = 0; i < BufferCount; i++)
            {
                FillAndQueue(i, buffer);
            }
            ready.Set();

            while (_running)
            {
                _bufferEvent.WaitOne(20);
                for (int i = 0; i < BufferCount && _running; i++)
                {
                    IntPtr header = HeaderPtr(i);
                    var hdr = Marshal.PtrToStructure<WaveHeader>(header);
                    if ((hdr.dwFlags & WhdrDone) != 0)
                    {
                        Interlocked.Increment(ref _blocksCompleted);
                        FillAndQueue(i, buffer);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _lastError = e;
            ready.Set();
        }
    }

    private void FillAndQueue(int index, short[] buffer)
    {
        if (_fill is null) return;
        Array.Clear(buffer);
        int frames = _fill(buffer, _blockFrames);
        if (frames > _blockFrames) frames = _blockFrames;

        IntPtr destination = _buffers[index];
        int sampleCount = frames * ProtocolConstants.Channels;
        Marshal.Copy(buffer, 0, destination, sampleCount);

        IntPtr header = HeaderPtr(index);
        Interlocked.Increment(ref _blocksQueued);
        int result = waveOutWrite(_device, header, _headerSize);
        if (result != 0)
            throw new InvalidOperationException($"waveOutWrite failed: {result}");
    }

    private void Open()
    {
        var format = new WaveFormatEx
        {
            wFormatTag = WaveFormatPcm,
            nChannels = ProtocolConstants.Channels,
            nSamplesPerSec = ProtocolConstants.SampleRate,
            wBitsPerSample = ProtocolConstants.BitsPerSample,
            nBlockAlign = (ushort)ProtocolConstants.BytesPerFrame,
            nAvgBytesPerSec = ProtocolConstants.SampleRate * ProtocolConstants.BytesPerFrame,
            cbSize = 0
        };
        _bufferEvent = new ManualResetEvent(false);
        int result = waveOutOpen(
            out _device,
            WaveMapper,
            ref format,
            _bufferEvent.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            CallbackEvent);
        if (result != 0)
            throw new InvalidOperationException($"waveOutOpen failed: {result}");

        _headerSize = Marshal.SizeOf<WaveHeader>();
        int blockSamples = _blockFrames * ProtocolConstants.Channels;
        _buffers = new IntPtr[BufferCount];
        _headers = Marshal.AllocHGlobal(_headerSize * BufferCount);
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(blockSamples * sizeof(short));
            var header = new WaveHeader
            {
                lpData = _buffers[i],
                dwBufferLength = (uint)(blockSamples * sizeof(short)),
                dwFlags = 0,
                dwLoops = 0
            };
            Marshal.StructureToPtr(header, HeaderPtr(i), false);
            int prep = waveOutPrepareHeader(_device, HeaderPtr(i), _headerSize);
            if (prep != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader failed: {prep}");
        }
    }

    private void Close()
    {
        if (_device != IntPtr.Zero)
        {
            try { waveOutReset(_device); } catch { /* ignore */ }
            if (_headers != IntPtr.Zero)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    try { waveOutUnprepareHeader(_device, HeaderPtr(i), _headerSize); }
                    catch { /* ignore */ }
                }
            }
            try { waveOutClose(_device); } catch { /* ignore */ }
            _device = IntPtr.Zero;
        }

        if (_headers != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_headers);
            _headers = IntPtr.Zero;
        }
        foreach (IntPtr buffer in _buffers)
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
        _buffers = Array.Empty<IntPtr>();
        _bufferEvent?.Dispose();
        _bufferEvent = null;
    }

    private IntPtr HeaderPtr(int index) => IntPtr.Add(_headers, index * _headerSize);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutOpen(
        out IntPtr hWaveOut,
        uint uDeviceID,
        ref WaveFormatEx lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint dwFlags);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern int waveOutClose(IntPtr hWaveOut);

    public void Dispose()
    {
        Stop();
    }
}
