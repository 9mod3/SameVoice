param(
    [string]$DeviceId = "{0.0.0.00000000}.{810466f1-b8fd-4db3-82ad-ded9396b0a5b}",
    [int]$Seconds = 15,
    [int]$BufferMs = 0,
    [int]$SleepMs = 5,
    [switch]$Timer1ms
)

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class WasapiLoopProbe
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out uint bufferFrames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint paddingFrames);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormat);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint frames);
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

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsCtx,
        ref Guid iid, out IntPtr instance);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    public static int Run(string deviceId, int seconds, int bufferMs, int sleepMs, bool timer1ms)
    {
        if (timer1ms) timeBeginPeriod(1);
        CoInitializeEx(IntPtr.Zero, 0);
        IAudioClient audio = null;
        IAudioCaptureClient capture = null;
        IMMDevice device = null;
        try
        {
            Guid clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Guid iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            IntPtr ptr;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iidEnum, out ptr);
            if (hr < 0) { Console.WriteLine("CoCreateInstance 0x" + hr.ToString("X8")); return 2; }
            var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);

            hr = enumerator.GetDevice(deviceId, out device);
            if (hr < 0) { Console.WriteLine("GetDevice 0x" + hr.ToString("X8")); return 3; }

            Guid iidAudio = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            object obj;
            hr = device.Activate(ref iidAudio, 23, IntPtr.Zero, out obj);
            if (hr < 0) { Console.WriteLine("Activate 0x" + hr.ToString("X8")); return 4; }
            audio = (IAudioClient)obj;

            IntPtr fmt;
            hr = audio.GetMixFormat(out fmt);
            if (hr < 0) { Console.WriteLine("GetMixFormat 0x" + hr.ToString("X8")); return 5; }
            WaveFormatEx w = (WaveFormatEx)Marshal.PtrToStructure(fmt, typeof(WaveFormatEx));
            Console.WriteLine("format=" + w.nSamplesPerSec + " Hz, " + w.nChannels + " ch, " +
                              w.wBitsPerSample + " bit, tag=" + w.wFormatTag);

            long duration = (long)bufferMs * 10000L;
            hr = audio.Initialize(0, 0x00020000, duration, 0, fmt, IntPtr.Zero);
            if (hr < 0)
            {
                Console.WriteLine("Initialize(" + bufferMs + "ms) 0x" + hr.ToString("X8") +
                                  ", retrying with default buffer");
                hr = audio.Initialize(0, 0x00020000, 0, 0, fmt, IntPtr.Zero);
            }
            Marshal.FreeCoTaskMem(fmt);
            if (hr < 0) { Console.WriteLine("Initialize 0x" + hr.ToString("X8")); return 6; }

            uint bufferFrames;
            audio.GetBufferSize(out bufferFrames);
            Console.WriteLine("bufferFrames=" + bufferFrames + " (" +
                              (bufferFrames * 1000.0 / w.nSamplesPerSec).ToString("F1") + " ms)");

            Guid iidCapture = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
            object capObj;
            hr = audio.GetService(ref iidCapture, out capObj);
            if (hr < 0) { Console.WriteLine("GetService 0x" + hr.ToString("X8")); return 7; }
            capture = (IAudioCaptureClient)capObj;

            hr = audio.Start();
            if (hr < 0) { Console.WriteLine("Start 0x" + hr.ToString("X8")); return 8; }

            Stopwatch watch = Stopwatch.StartNew();
            long frames = 0;
            long lastFrames = 0;
            double lastSeconds = 0;
            double nextPrint = 1.0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                uint packet;
                if (capture.GetNextPacketSize(out packet) < 0) break;
                if (packet == 0)
                {
                    Thread.Sleep(sleepMs);
                }
                else
                {
                    while (packet > 0)
                    {
                        IntPtr data;
                        uint n;
                        uint flags;
                        ulong pos;
                        ulong qpc;
                        if (capture.GetBuffer(out data, out n, out flags, out pos, out qpc) < 0) break;
                        frames += n;
                        capture.ReleaseBuffer(n);
                        capture.GetNextPacketSize(out packet);
                    }
                }

                double elapsed = watch.Elapsed.TotalSeconds;
                if (elapsed >= nextPrint)
                {
                    double rate = frames / elapsed;
                    double window = (frames - lastFrames) / Math.Max(0.001, elapsed - lastSeconds);
                    Console.WriteLine("t=" + elapsed.ToString("F1") + "s frames=" + frames +
                                      " avg=" + rate.ToString("F1") + " window=" + window.ToString("F1") +
                                      " ppm=" + ((rate / 48000.0 - 1.0) * 1e6).ToString("F0"));
                    lastFrames = frames;
                    lastSeconds = elapsed;
                    nextPrint += 1.0;
                }
            }
            audio.Stop();
            Console.WriteLine("RESULT frames=" + frames + " rate=" +
                              (frames / watch.Elapsed.TotalSeconds).ToString("F2"));
            return 0;
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (audio != null) Marshal.ReleaseComObject(audio);
            if (device != null) Marshal.ReleaseComObject(device);
            CoUninitialize();
            if (timer1ms) timeEndPeriod(1);
        }
    }
}
'@ -ErrorAction Stop

$result = [WasapiLoopProbe]::Run($DeviceId, $Seconds, $BufferMs, $SleepMs, [bool]$Timer1ms)
exit $result
