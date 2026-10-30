using System.Runtime.InteropServices;
using SameVoice.Core;
using SameVoice.Core.Audio;

namespace SameVoice.App.Audio;

/// <summary>
/// Captures the Windows default playback endpoint in WASAPI loopback shared mode.
/// This captures system audio from every application, not just SameVoice.
/// </summary>
public sealed class WasapiLoopbackCapture : IAudioCapture
{
    // Simple fixed source boost. The per-source volume on the output side can
    // attenuate it again; the soft limiter prevents hard digital clipping.
    private const float SourceBoost = 4.0f;
    private const float BoostKnee = 0.85f;

    public event Action<short[], long>? DataAvailable;
    public event Action<string>? Faulted;

    public bool IsRunning => _running;
    public string? LastError { get; private set; }

    public float ExtraGain
    {
        get => _extraGain;
        set => _extraGain = Math.Clamp(value, 0f, 2f);
    }

    private readonly string _deviceId;
    private volatile float _extraGain = 1f;
    private volatile bool _running;
    private Thread? _thread;
    private ManualResetEventSlim? _started;
    private Exception? _startError;
    private int _lastPacketAtMs;

    public WasapiLoopbackCapture(string? deviceId = null)
    {
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? "" : deviceId;
    }

    public void Start()
    {
        if (_running) return;
        LastError = null;
        _startError = null;
        _started = new ManualResetEventSlim(false);
        _thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = "SameVoice WASAPI loopback",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        if (!_started.Wait(4000))
        {
            _running = false;
            throw new InvalidOperationException("WASAPI 采集启动超时。");
        }
        if (_startError is not null)
        {
            _running = false;
            throw new InvalidOperationException("无法采集系统声音：" + _startError.Message, _startError);
        }
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1500); } catch { /* ignore */ }
        _thread = null;
        _started?.Dispose();
        _started = null;
    }

    private void CaptureThread()
    {
        bool comInitialized = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, 0);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            comInitialized = true;

            Guid clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Guid iidEnumerator = typeof(IMMDeviceEnumerator).GUID;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iidEnumerator, out IntPtr enumeratorPtr);
            if (hr < 0 || enumeratorPtr == IntPtr.Zero) Marshal.ThrowExceptionForHR(hr);
            try { enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumeratorPtr); }
            finally { Marshal.Release(enumeratorPtr); }

            if (!string.IsNullOrEmpty(_deviceId))
                hr = enumerator.GetDevice(_deviceId, out device);
            else
                hr = enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device);
            if (hr < 0 || device is null) Marshal.ThrowExceptionForHR(hr);

            Guid iidAudioClient = typeof(IAudioClient).GUID;
            hr = device.Activate(ref iidAudioClient, 23, IntPtr.Zero, out object audioObject);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            audioClient = (IAudioClient)audioObject;

            hr = audioClient.GetMixFormat(out IntPtr formatPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var info = ParseFormat(formatPtr);

            hr = audioClient.Initialize(
                0 /* shared */,
                0x00020000 /* loopback */,
                0,
                0,
                formatPtr,
                IntPtr.Zero);
            Marshal.FreeCoTaskMem(formatPtr);
            formatPtr = IntPtr.Zero;
            if (hr < 0)
                throw new InvalidOperationException($"WASAPI Initialize 失败 (0x{hr:X8})");

            Guid iidCapture = typeof(IAudioCaptureClient).GUID;
            hr = audioClient.GetService(ref iidCapture, out object captureObject);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            captureClient = (IAudioCaptureClient)captureObject;

            hr = audioClient.Start();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            _running = true;
            _started?.Set();

            var converter = new StereoPcmConverter48k(
                info.SampleRate,
                info.Channels,
                info.BitsPerSample,
                info.IsFloat);

            var byteBuffer = new byte[info.Channels * (info.BitsPerSample / 8) * 4096];
            long nextTimestampUs = 0;
            _lastPacketAtMs = 0;

            while (_running)
            {
                uint packetFrames = 0;
                hr = captureClient.GetNextPacketSize(out packetFrames);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                if (packetFrames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                while (packetFrames > 0 && _running)
                {
                    hr = captureClient.GetBuffer(
                        out IntPtr data,
                        out uint frames,
                        out uint flags,
                        out _,
                        out _);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                    int frameBytes = info.Channels * (info.BitsPerSample / 8);
                    int totalBytes = checked((int)frames * frameBytes);
                    if (byteBuffer.Length < totalBytes) byteBuffer = new byte[totalBytes];

                    bool silent = (flags & 0x2) != 0;
                    if (silent || data == IntPtr.Zero)
                        Array.Clear(byteBuffer, 0, totalBytes);
                    else
                        Marshal.Copy(data, byteBuffer, 0, totalBytes);

                    int releaseHr = captureClient.ReleaseBuffer(frames);
                    if (releaseHr < 0) Marshal.ThrowExceptionForHR(releaseHr);

                    long durationUs = frames * 1_000_000L / info.SampleRate;
                    long now = HighResClock.NowUs();
                    if (nextTimestampUs == 0 || Environment.TickCount - _lastPacketAtMs > 250)
                        nextTimestampUs = now - durationUs;
                    long timestamp = nextTimestampUs;
                    nextTimestampUs += durationUs;
                    _lastPacketAtMs = Environment.TickCount;

                    short[] pcm = converter.Convert(byteBuffer.AsSpan(0, totalBytes));
                    if (pcm.Length > 0)
                        RaiseData(pcm, timestamp);

                    hr = captureClient.GetNextPacketSize(out packetFrames);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
            }

            try { audioClient.Stop(); } catch { /* ignore */ }
        }
        catch (Exception e)
        {
            LastError = e.Message;
            if (_started is { IsSet: false })
            {
                _startError = e;
                _started.Set();
            }
            else
            {
                Faulted?.Invoke(e.Message);
            }
            _running = false;
        }
        finally
        {
            if (captureClient is not null) Marshal.ReleaseComObject(captureClient);
            if (audioClient is not null) Marshal.ReleaseComObject(audioClient);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (comInitialized) CoUninitialize();
        }
    }

    private void RaiseData(short[] pcm, long timestamp)
    {
        ApplyFixedBoost(pcm);
        try { DataAvailable?.Invoke(pcm, timestamp); }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    private void ApplyFixedBoost(short[] pcm)
    {
        float gain = SourceBoost * _extraGain;
        for (int i = 0; i < pcm.Length; i++)
        {
            float sample = pcm[i] * gain / 32768f;
            float abs = Math.Abs(sample);
            if (abs > BoostKnee)
            {
                float over = (abs - BoostKnee) / (1f - BoostKnee);
                float limited = BoostKnee + (1f - BoostKnee) * MathF.Tanh(over);
                sample = MathF.CopySign(limited, sample);
            }
            pcm[i] = (short)Math.Clamp((int)MathF.Round(sample * 32768f), -32768, 32767);
        }
    }

    private static AudioFormatInfo ParseFormat(IntPtr pointer)
    {
        var basic = Marshal.PtrToStructure<WaveFormatEx>(pointer);
        bool isFloat = basic.wFormatTag == 3;
        if (basic.wFormatTag == 0xFFFE)
        {
            var ext = Marshal.PtrToStructure<WaveFormatExtensible>(pointer);
            Guid sub = ext.SubFormat;
            Guid pcm = new("00000001-0000-0010-8000-00AA00389B71");
            Guid ieee = new("00000003-0000-0010-8000-00AA00389B71");
            if (sub == pcm) isFloat = false;
            else if (sub == ieee) isFloat = true;
            else throw new NotSupportedException($"不支持的音频子格式 {sub}");
        }
        else if (basic.wFormatTag != 1 && basic.wFormatTag != 3)
        {
            throw new NotSupportedException($"不支持的音频格式 0x{basic.wFormatTag:X4}");
        }

        if (basic.nChannels < 1 || basic.nSamplesPerSec < 8000)
            throw new NotSupportedException("无效的音频设备格式。");
        int bits = basic.wBitsPerSample;
        if (bits is not (16 or 24 or 32))
            throw new NotSupportedException($"不支持的位深 {bits} bit。");

        return new AudioFormatInfo(
            basic.nChannels,
            (int)basic.nSamplesPerSec,
            bits,
            isFloat);
    }

    private sealed record AudioFormatInfo(int Channels, int SampleRate, int BitsPerSample, bool IsFloat);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object @interface);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outerUnknown,
        uint clsCtx,
        ref Guid iid,
        out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public void Dispose()
    {
        Stop();
    }
}
