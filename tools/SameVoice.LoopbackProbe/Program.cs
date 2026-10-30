using System.Diagnostics;
using System.Net;
using SameVoice.App.Audio;
using SameVoice.Core;
using SameVoice.Core.Audio;
using SameVoice.Core.Host;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sender;

namespace SameVoice.LoopbackProbe;

internal sealed class MeterCapture : IAudioCapture
{
    private readonly WasapiLoopbackCapture _inner = new();
    public long Frames;
    public double Peak;
    public long Events;

    public MeterCapture()
    {
        _inner.DataAvailable += (samples, timestamp) =>
        {
            Interlocked.Add(ref Frames, samples.Length / 2);
            Interlocked.Increment(ref Events);
            double peak = 0;
            foreach (short s in samples) peak = Math.Max(peak, Math.Abs(s) / 32768.0);
            if (peak > Peak) Peak = peak;
            DataAvailable?.Invoke(samples, timestamp);
        };
        _inner.Faulted += message => Faulted?.Invoke(message);
    }

    public event Action<short[], long>? DataAvailable;
    public event Action<string>? Faulted;
    public bool IsRunning => _inner.IsRunning;
    public string? LastError => _inner.LastError;
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() => _inner.Dispose();
}

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var tone = new WaveOutSink();
        using var host = new HostEngine(new NullAudioOutput(), "LoopbackHost");
        var capture = new MeterCapture();
        SourceClient? source = null;
        float originalVolume = 1f;
        bool hasOriginalVolume = SystemAudioMute.TryGetVolumeScalar(out originalVolume);
        Console.WriteLine($"Original endpoint volume={originalVolume:0.00} readable={hasOriginalVolume}");
        try
        {
            host.Start();
            Console.WriteLine($"Host listening: TCP {host.ControlPort}, UDP {host.AudioPort}");
            var offer = new HostOffer(host.RoomId, host.HostName, "windows",
                host.ControlPort, HostMode.Balanced, 0, false, IPAddress.Loopback);
            source = new SourceClient("LoopbackProbe", "windows", capture);
            source.StatusChanged += s => Console.WriteLine($"Source {s.State} rtt={s.RttMs:0.0}ms sent={s.PacketsSent}");
            source.Faulted += m => Console.WriteLine("SOURCE FAULT: " + m);
            await source.ConnectAsync(offer);
            Console.WriteLine("Source connected. Playing a 440 Hz system tone...");

            long frame = 0;
            tone.Start((buffer, frames) =>
            {
                for (int i = 0; i < frames; i++)
                {
                    double t = (frame + i) / 48000.0;
                    short v = (short)(Math.Sin(2 * Math.PI * 440 * t) * 9000);
                    buffer[i * 2] = v;
                    buffer[i * 2 + 1] = v;
                }
                frame += frames;
                return frames;
            });

            for (int i = 0; i < 8; i++)
            {
                if (i == 2)
                {
                    bool ok = SystemAudioMute.TrySetVolumeScalar(1.0f, out var volumeError);
                    Console.WriteLine($"ENDPOINT VOLUME 1.00 ok={ok} error={volumeError ?? "-"}");
                }
                if (i == 4)
                {
                    bool ok = SystemAudioMute.TrySetVolumeScalar(0.50f, out var volumeError);
                    Console.WriteLine($"ENDPOINT VOLUME 0.50 ok={ok} error={volumeError ?? "-"}");
                }
                if (i == 6)
                {
                    bool ok = SystemAudioMute.TrySetVolumeScalar(originalVolume, out var volumeError);
                    Console.WriteLine($"ENDPOINT VOLUME RESTORE ok={ok} error={volumeError ?? "-"}");
                }

                await Task.Delay(500);
                var snap = host.GetSnapshot();
                Console.WriteLine($"t={i * 500}ms captureEvents={Interlocked.Read(ref capture.Events)} " +
                                  $"frames={Interlocked.Read(ref capture.Frames)} capturePeak={capture.Peak:0.000} " +
                                  $"hostPeak={snap.Peak:0.000} sources={snap.SourceCount}");
            }

            tone.Stop();
            if (source is not null) await source.DisconnectAsync();
            Console.WriteLine($"RESULT captureFrames={Interlocked.Read(ref capture.Frames)} capturePeak={capture.Peak:0.000} " +
                              $"hostPeak={host.GetSnapshot().Peak:0.000}");
            return Interlocked.Read(ref capture.Frames) > 0 && capture.Peak > 0.001 ? 0 : 3;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e);
            return 1;
        }
        finally
        {
            try { SystemAudioMute.TrySetMuted(false, out _); } catch { }
            try { SystemAudioMute.TrySetVolumeScalar(originalVolume, out _); } catch { }
            try { tone.Stop(); } catch { }
            if (source is not null) await source.DisposeAsync();
            capture.Dispose();
            host.Dispose();
        }
    }
}
