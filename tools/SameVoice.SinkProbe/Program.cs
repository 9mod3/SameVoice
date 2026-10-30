using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using SameVoice.App.Audio;
using SameVoice.Core;
using SameVoice.Core.Audio;
using SameVoice.Core.Host;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sender;

namespace SameVoice.SinkProbe;

/// <summary>Paced 200 packets/s synthetic source so the receiver timeline is exercised realistically.</summary>
internal sealed class PacedSineCapture : IAudioCapture
{
    private Thread? _thread;
    private volatile bool _running;
    private long _sampleIndex;
    private long _startUs;
    public long Callbacks;
    public long ElapsedMs;

    public event Action<short[], long>? DataAvailable;
    public event Action<string>? Faulted;
    public bool IsRunning => _running;
    public string? LastError { get; private set; }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    public void Start()
    {
        _running = true;
        _startUs = HighResClock.NowUs();
        _thread = new Thread(() =>
        {
            timeBeginPeriod(1);
            var output = new short[ProtocolConstants.PacketFrames * 2];
            long next = Stopwatch.GetTimestamp();
            try
            {
                while (_running)
                {
                    for (int f = 0; f < ProtocolConstants.PacketFrames; f++)
                    {
                        double t = _sampleIndex++ / 48_000.0;
                        output[f * 2] = (short)(Math.Sin(2 * Math.PI * 440 * t) * 7000);
                        output[f * 2 + 1] = (short)(Math.Sin(2 * Math.PI * 660 * t) * 7000);
                    }
                    long now = HighResClock.NowUs();
                    DataAvailable?.Invoke(output, now - 5000);
                    Interlocked.Increment(ref Callbacks);
                    ElapsedMs = now - _startUs;
                    next += Stopwatch.Frequency / 200;
                    while (Stopwatch.GetTimestamp() < next && _running)
                        Thread.Sleep(0);
                }
            }
            catch (Exception e) { LastError = e.Message; Faulted?.Invoke(e.Message); }
            finally { timeEndPeriod(1); _running = false; }
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "paced-sine" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(500); } catch { }
        _thread = null;
    }

    public void Dispose() => Stop();
}

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var sink = new WaveOutSink();
        using var host = new HostEngine(sink, "SinkProbeHost");
        var capture = new PacedSineCapture();
        SourceClient? source = null;
        try
        {
            host.Start();
            var offer = new HostOffer(host.RoomId, host.HostName, "probe", host.ControlPort,
                HostMode.Balanced, 0, false, IPAddress.Loopback);
            source = new SourceClient("SinkProbeSource", "probe", capture);
            source.Faulted += m => Console.WriteLine("SOURCE FAULT " + m);
            await source.ConnectAsync(offer);
            Console.WriteLine("connected");
            bool okay = true;
            for (int i = 0; i < 16; i++)
            {
                await Task.Delay(500);
                var snap = host.GetSnapshot();
                double buffer = snap.Sources.FirstOrDefault()?.BufferMs ?? 0;
                Console.WriteLine($"t={(i + 1) * 500}ms queued={sink.BlocksQueued} completed={sink.BlocksCompleted} " +
                                  $"sourceBuffer={buffer:0}ms peak={snap.Peak:0.000} callbacks={capture.Callbacks} " +
                                  $"elapsed={capture.ElapsedMs / 1000}ms sent={source.GetStatus().PacketsSent} " +
                                  $"error={sink.LastError?.Message ?? "-"}");
                if (buffer > 1200) okay = false;
            }
            await source.DisconnectAsync();
            return okay ? 0 : 2;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED " + e);
            return 1;
        }
        finally
        {
            if (source is not null) await source.DisposeAsync();
            host.Dispose();
            capture.Dispose();
        }
    }
}
