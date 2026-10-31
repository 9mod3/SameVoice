using System.Net;
using SameVoice.Core;
using SameVoice.Core.Audio;
using SameVoice.Core.Host;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sync;

namespace SameVoice.Core.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(bool condition, string message)
    {
        if (condition) { _passed++; return; }
        _failed++;
        Console.WriteLine("FAIL: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (expected {expected}, got {actual})");

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        TestAudioPacket();
        TestDiscovery();
        TestControl();
        TestClockSync();
        TestMixer();
        TestPcm();
        TestPcm24();
        TestQualityHandshake();
        TestResampler();

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestAudioPacket()
    {
        var payload = new byte[ProtocolConstants.PacketBytes];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        byte[] encoded = AudioPacket.Encode(
            ProtocolConstants.PacketTypeAudio, 12345, 0xDEADBEEFCAFEBABE, 77, 1234567890123, payload);
        Equal("SVA1", System.Text.Encoding.ASCII.GetString(encoded, 0, 4), "audio magic");
        Equal(ProtocolConstants.AudioHeaderSize + payload.Length, encoded.Length, "audio packet length");
        Check(AudioPacket.TryDecode(encoded, out var decoded), "decode audio packet");
        Check(decoded is not null, "decoded packet present");
        if (decoded is not null)
        {
            Equal((uint)12345, decoded.SessionId, "session");
            Equal(0xDEADBEEFCAFEBABEUL, decoded.Token, "token");
            Equal((uint)77, decoded.Sequence, "sequence");
            Equal(1234567890123L, decoded.CaptureUs, "capture time");
            Equal(ProtocolConstants.PacketBytes, decoded.Payload.Length, "payload length");
            Check(decoded.Payload.SequenceEqual(payload), "payload data");
        }

        Check(!AudioPacket.TryDecode(encoded.AsSpan(0, 20).ToArray(), out _), "truncated packet rejected");
        byte[] bad = encoded.ToArray();
        bad[0] = (byte)'X';
        Check(!AudioPacket.TryDecode(bad, out _), "bad magic rejected");

        byte[] pong = AudioPacket.EncodePong(5, 0x1122334455667788, 9, 1000, 1200, 1300);
        Check(AudioPacket.TryDecode(pong, out var pongPacket) && pongPacket is not null, "decode pong");
        if (pongPacket is not null)
        {
            Equal((byte)ProtocolConstants.PacketTypePong, pongPacket.Type, "pong type");
            Equal((uint)5, pongPacket.SessionId, "pong session");
            Equal(1300L, pongPacket.CaptureUs, "pong t3");
            Check(AudioPacket.TryReadPongTimes(pongPacket, out long t1, out long t2), "read pong times");
            Equal(1000L, t1, "pong t1");
            Equal(1200L, t2, "pong t2");
        }
    }

    private static void TestDiscovery()
    {
        byte[] query = DiscoveryProtocol.CreateQuery();
        Check(DiscoveryProtocol.IsQuery(query), "query recognized");
        byte[] offerBytes = DiscoveryProtocol.CreateOffer(
            "room1", "客厅电脑", "windows", 47820, HostMode.LowLatency, 2, false);
        var offer = DiscoveryProtocol.TryParseOffer(offerBytes, IPAddress.Parse("192.168.1.7"));
        Check(offer is not null, "offer parsed");
        if (offer is not null)
        {
            Equal("room1", offer.RoomId, "room id");
            Equal("客厅电脑", offer.Name, "offer name");
            Equal(HostMode.LowLatency, offer.Mode, "offer mode");
            Equal(2, offer.Sources, "offer sources");
            Equal("192.168.1.7", offer.AddressText, "offer source address");
        }
    }

    private static void TestControl()
    {
        string hello = System.Text.Encoding.UTF8.GetString(ControlProtocol.Hello("手机", "android"));
        Equal("hello", ControlProtocol.ReadType(hello), "hello type");
        byte[] welcomeBytes = ControlProtocol.Welcome(42, 0xABCDEF0123456789, 47999, "电脑", HostMode.LowLatency);
        var welcome = ControlProtocol.ParseWelcome(System.Text.Encoding.UTF8.GetString(welcomeBytes));
        Check(welcome is not null, "welcome parsed");
        if (welcome is not null)
        {
            Equal((uint)42, welcome.SessionId, "welcome session");
            Equal(0xABCDEF0123456789UL, welcome.Token, "welcome token");
            Equal(47999, welcome.AudioPort, "welcome port");
            Equal(HostMode.LowLatency, welcome.Mode, "welcome mode");
        }
        Equal("started", ControlProtocol.ReadType(System.Text.Encoding.UTF8.GetString(ControlProtocol.Started())), "started JSON");
        Equal("heartbeat", ControlProtocol.ReadType(System.Text.Encoding.UTF8.GetString(ControlProtocol.Heartbeat())), "heartbeat JSON");
    }

    private static void TestClockSync()
    {
        var clock = new ClockSynchronizer();
        clock.AddSample(1_000_000, 2_000_000, 2_000_100, 1_000_100);
        Equal(1_000_000L, clock.CurrentOffsetUs, "first clock offset");
        Equal(1_000_000L + 50, clock.ToHostTime(50), "to host time");
        clock.AddSample(2_000_000, 3_000_000, 3_000_100, 2_000_100);
        clock.AddSample(3_000_000, 4_000_000, 4_000_100, 3_000_100);
        Check(clock.SampleCount >= 3, "clock samples counted");
        Check(Math.Abs(clock.CurrentOffsetUs - 1_000_000) < 100, "clock offset stable");
    }

    private static void TestMixer()
    {
        var timeline = new SourceTimeline();
        var pcm = new short[ProtocolConstants.PacketFrames * 2];
        Array.Fill(pcm, (short)16384);
        timeline.Push(0, pcm, ProtocolConstants.PacketFrames);
        Equal(5.0, Math.Round(timeline.GetBufferAheadMs(0), 3), "timeline buffer 5ms");

        var accumulator = new float[480 * 2];
        bool mixed = timeline.MixInto(accumulator, 0, 480, 0.8f);
        Check(mixed, "timeline mixed");
        Check(accumulator[0] > 0.3f && accumulator[0] < 0.5f, "timeline gain applied");
        Check(Math.Abs(accumulator[239 * 2]) > 0.1f, "timeline last sample");
        Check(Math.Abs(accumulator[240 * 2]) < 1e-6f, "timeline does not overrun chunk");

        var mix = new MixEngine();
        var source = mix.AddSource(1);
        source.Volume = 1.0f;
        mix.MasterVolume = 1.0f;
        mix.SetMode(20);
        mix.Start();
        mix.PushAudio(1, mix.StartHostUs, pcm, ProtocolConstants.PacketFrames);
        var block = new short[480 * 2];
        mix.Render(block, 480);
        Check(block.All(s => s == 0), "mixer waits for target buffer");
        mix.Render(block, 480);
        Check(block.All(s => s == 0), "mixer still waiting");
        mix.Render(block, 480);
        Check(block.Any(s => s != 0), "mixer emits scheduled audio at target");
        Check(mix.Peak > 0.1, "mixer peak tracked");
    }

    private static void TestPcm()
    {
        short[] source = { -32768, -1000, 0, 1000, 32767, 123 };
        byte[] bytes = PcmCodec.EncodePcm16(source);
        Equal(source.Length * 2, bytes.Length, "pcm byte length");
        short[] decoded = PcmCodec.DecodePcm16(bytes);
        Check(source.SequenceEqual(decoded), "pcm roundtrip");

        var stereo = new float[480 * 2];
        for (int i = 0; i < 480; i++)
        {
            stereo[i * 2] = 0.5f;
            stereo[i * 2 + 1] = -0.5f;
        }
        var converter = new StereoPcmConverter48k(48000, 2, 16, false);
        byte[] pcm16 = new byte[480 * 4];
        for (int i = 0; i < 480 * 2; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2), (short)(stereo[i] * 32767));
        short[] converted = converter.Convert(pcm16);
        Equal(480 * 2, converted.Length, "48k pcm converter length");
        Check(Math.Abs(converted[0] - 16384) < 3, "left sample conversion");
        Check(Math.Abs(converted[1] + 16384) < 3, "right sample conversion");
    }

    private static void TestPcm24()
    {
        float[] source = { -0.5f, 0f, 0.5f, 0.999f };
        byte[] encoded = PcmCodec.EncodePcm24(source);
        Equal(source.Length * 3, encoded.Length, "pcm24 byte length");
        float[] decoded = PcmCodec.DecodePcm24ToFloat(encoded);
        for (int i = 0; i < source.Length; i++)
            Check(Math.Abs(source[i] - decoded[i]) < 0.00001f, "pcm24 roundtrip");

        Equal(ProtocolConstants.HighPacketFrames * 6,
            ProtocolConstants.HighPacketBytes, "pcm24 packet size");
        Check(ProtocolConstants.HighPacketBytes + ProtocolConstants.AudioHeaderSize + 28 <= 1500,
            "pcm24 packet fits Ethernet MTU");
    }

    private static void TestQualityHandshake()
    {
        byte[] welcomeBytes = ControlProtocol.Welcome(42, 0xABCDEF0123456789, 47999, "电脑",
            HostMode.Balanced, bits: 24);
        WelcomeInfo? welcome = ControlProtocol.ParseWelcome(
            System.Text.Encoding.UTF8.GetString(welcomeBytes));
        Check(welcome is not null && welcome.BitsPerSample == 24, "welcome 24-bit negotiation");
        Check(AudioQuality.High.Bits() == 24 && AudioQuality.Standard.Bits() == 16,
            "quality bits");
    }

    private static void TestResampler()
    {
        var resampler = new LinearResampler(44100, 48000, 2);
        var input = new float[44100 * 2];
        for (int i = 0; i < 44100; i++)
        {
            input[i * 2] = 0.25f;
            input[i * 2 + 1] = -0.25f;
        }
        short[] output = resampler.Process(input);
        Check(Math.Abs(output.Length / 2 - 48000) < 50, $"resampler output length ({output.Length / 2})");
        Check(output.Length > 0 && output[0] > 8000 && output[1] < -8000, "resampler values");
    }
}
