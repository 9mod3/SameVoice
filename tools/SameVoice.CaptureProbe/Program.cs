using System.Diagnostics;
using SameVoice.App.Audio;

// Measures the effective frame delivery rate of a WASAPI loopback endpoint.
// The SameVoice source timestamps audio from this frame stream, so a capture
// that silently drops frames makes every packet timestamp a little late and
// drains the receiver's jitter buffer.

string deviceId = args.Length > 0 ? args[0] : "";
int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
int bufferMs = args.Length > 2 ? int.Parse(args[2]) : 0;

using var capture = new WasapiLoopbackCapture(deviceId);
capture.Capture24Bit = true;
capture.BufferDurationMs = bufferMs;
long frames = 0;
long events = 0;
capture.DataAvailable += (samples, _) =>
{
    Interlocked.Add(ref frames, samples.Length / 2);
    Interlocked.Increment(ref events);
};
capture.DataAvailable24 += (payload, _) =>
{
    Interlocked.Add(ref frames, payload.Length / 6);
    Interlocked.Increment(ref events);
};
capture.Faulted += message => Console.WriteLine("FAULT " + message);

capture.Start();
Console.WriteLine($"format={capture.FormatDescription} bufferFrames={capture.BufferFrames} " +
                  $"requestedBufferMs={bufferMs}");
var watch = Stopwatch.StartNew();
long lastFrames = 0;
double lastSeconds = 0;
while (watch.Elapsed.TotalSeconds < seconds)
{
    Thread.Sleep(1000);
    long total = Interlocked.Read(ref frames);
    double elapsed = watch.Elapsed.TotalSeconds;
    double rate = total / elapsed;
    double windowRate = (total - lastFrames) / Math.Max(0.001, elapsed - lastSeconds);
    Console.WriteLine($"t={elapsed,5:F1}s frames={total,9} avg_rate={rate,9:F1} " +
                      $"window_rate={windowRate,9:F1} ppm={(rate / 48_000 - 1) * 1e6,7:F0} " +
                      $"events={Interlocked.Read(ref events)}");
    lastFrames = total;
    lastSeconds = elapsed;
}
capture.Stop();
Console.WriteLine($"RESULT seconds={watch.Elapsed.TotalSeconds:F3} frames={Interlocked.Read(ref frames)} " +
                  $"rate={Interlocked.Read(ref frames) / watch.Elapsed.TotalSeconds:F2} " +
                  $"ppm={(Interlocked.Read(ref frames) / watch.Elapsed.TotalSeconds / 48_000 - 1) * 1e6:F0} " +
                  $"error={capture.LastError ?? "-"}");
return 0;
