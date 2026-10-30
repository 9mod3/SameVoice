using System.Diagnostics;
using SameVoice.App.Audio;

namespace SameVoice.AudioProbe;

internal static class Program
{
    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var sink = new WaveOutSink();
        var sw = Stopwatch.StartNew();
        long frames = 0;
        try
        {
            sink.Start((buffer, count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    double t = (frames + i) / 48000.0;
                    short sample = (short)(Math.Sin(2 * Math.PI * 440 * t) * 6000);
                    buffer[i * 2] = sample;
                    buffer[i * 2 + 1] = sample;
                }
                frames += count;
                return count;
            });
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(250);
                Console.WriteLine($"t={sw.ElapsedMilliseconds}ms queued={sink.BlocksQueued} completed={sink.BlocksCompleted} error={sink.LastError?.Message ?? "-"} frames={frames}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("START FAILED: " + e);
        }
        finally
        {
            sink.Stop();
        }
        Console.WriteLine($"final queued={sink.BlocksQueued} completed={sink.BlocksCompleted}");
    }
}
