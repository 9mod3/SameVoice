using System.Diagnostics;
using System.Net;
using SameVoice.Core;
using SameVoice.Core.Audio;
using SameVoice.Core.Net;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sender;

namespace SameVoice.Probe;

internal sealed class SineCapture : IAudioCapture
{
    private readonly int _seconds;
    private CancellationTokenSource? _cts;
    private double _phase;
    private int _sampleIndex;

    public SineCapture(int seconds) => _seconds = seconds;

    public event Action<short[], long>? DataAvailable;
    public event Action<string>? Faulted;
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(async () =>
        {
            var buffer = new short[480 * 2];
            var sw = Stopwatch.StartNew();
            long nextUs = 0;
            while (!(_cts?.IsCancellationRequested ?? true))
            {
                for (int f = 0; f < 480; f++)
                {
                    double t = _sampleIndex++ / 48_000.0;
                    double left = Math.Sin(2 * Math.PI * 440 * t) * 0.18;
                    double right = Math.Sin(2 * Math.PI * 660 * t) * 0.18;
                    buffer[f * 2] = (short)(left * 32767);
                    buffer[f * 2 + 1] = (short)(right * 32767);
                }
                long now = HighResClock.NowUs();
                nextUs = now - 10_000;
                try { DataAvailable?.Invoke(buffer, nextUs); } catch (Exception e) { LastError = e.Message; }
                try { await Task.Delay(10, _cts!.Token); } catch (OperationCanceledException) { break; }
            }
            IsRunning = false;
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        IsRunning = false;
    }

    public void Dispose() => Stop();
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 10;
        string name = args.Length > 1 ? args[1] : "Probe-Sine";
        Console.WriteLine($"Discovering SameVoice host as {name}...");
        var offers = await DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(4));
        if (offers.Count == 0)
        {
            Console.WriteLine("No host found.");
            return 2;
        }

        foreach (var offer in offers)
            Console.WriteLine($"Found {offer.Name} at {offer.EndpointText}, mode={offer.Mode.ToWire()}");

        var host = offers[0];
        if (args.Length > 2 && IPAddress.TryParse(args[2], out var directIp))
            host = new HostOffer("", "Direct", "probe", 47820, HostMode.Balanced, 0, false, directIp);
        var capture = new SineCapture(seconds);
        await using var client = new SourceClient(name, "probe", capture);
        client.StatusChanged += status =>
            Console.WriteLine($"[{status.State}] rtt={status.RttMs:0.0}ms sent={status.PacketsSent} dropped={status.DroppedPackets} {status.Message}");
        client.Faulted += message => Console.WriteLine("[FAULT] " + message);

        try
        {
            await client.ConnectAsync(host);
            Console.WriteLine("Sharing. Waiting...");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var final = client.GetStatus();
            Console.WriteLine($"Packets sent: {final.PacketsSent}, bytes: {final.BytesSent}, rtt: {final.RttMs:0.0}ms");
            await client.DisconnectAsync();
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e);
            return 1;
        }
    }
}
