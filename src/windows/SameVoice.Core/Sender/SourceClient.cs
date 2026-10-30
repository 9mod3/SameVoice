using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SameVoice.Core.Audio;
using SameVoice.Core.Net;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sync;

namespace SameVoice.Core.Sender;

public enum SourceState
{
    Idle,
    Connecting,
    Syncing,
    Sharing,
    Error
}

public sealed record SourceClientStatus(
    SourceState State,
    string DeviceName,
    string HostName,
    string HostAddress,
    HostMode HostMode,
    string Message,
    double RttMs,
    long PacketsSent,
    long BytesSent,
    long DroppedPackets,
    bool IsSharing);

/// <summary>Connects to a SameVoice output device, captures local audio and streams it.</summary>
public sealed class SourceClient : IAsyncDisposable
{
    private readonly string _deviceName;
    private readonly string _platform;
    private readonly IAudioCapture _capture;
    private readonly ClockSynchronizer _clock = new();
    private readonly BlockingCollection<byte[]> _sendQueue = new(new ConcurrentQueue<byte[]>(), 96);
    private readonly object _captureGate = new();
    private readonly List<short> _pending = new(4096);
    private readonly SemaphoreSlim _controlWriteGate = new(1, 1);
    private readonly object _socketSendGate = new();

    private TcpClient? _tcp;
    private NetworkStream? _tcpStream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private UdpClient? _udp;
    private IPEndPoint? _audioEndpoint;
    private CancellationTokenSource? _cts;
    private Task? _controlTask;
    private Task? _pongTask;
    private Task? _senderTask;
    private Task? _heartbeatTask;
    private Task? _pingTask;

    private uint _sessionId;
    private ulong _token;
    private int _state = (int)SourceState.Idle;
    private string _hostName = "";
    private string _hostAddress = "";
    private string _message = "";
    private HostMode _hostMode = HostMode.Balanced;
    private long _nextPacketLocalUs;
    private long _packetsSent;
    private long _bytesSent;
    private long _dropped;
    private long _lastRttUs = -1;
    private uint _pingSequence;
    private uint _sequence;
    private bool _sharing;

    public SourceClient(string deviceName, string platform, IAudioCapture capture)
    {
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "SameVoice 音源" : deviceName.Trim();
        _platform = platform;
        _capture = capture;
        _capture.DataAvailable += OnCaptureData;
        _capture.Faulted += OnCaptureFaulted;
    }

    public string DeviceName => _deviceName;
    public bool IsActive => _state is (int)SourceState.Connecting or (int)SourceState.Syncing or (int)SourceState.Sharing;

    public event Action<SourceClientStatus>? StatusChanged;
    public event Action<string>? Faulted;

    public SourceClientStatus GetStatus() => new(
        (SourceState)Volatile.Read(ref _state),
        _deviceName,
        _hostName,
        _hostAddress,
        _hostMode,
        _message,
        _lastRttUs < 0 ? -1 : _lastRttUs / 1000.0,
        Interlocked.Read(ref _packetsSent),
        Interlocked.Read(ref _bytesSent),
        Interlocked.Read(ref _dropped),
        _sharing);

    public async Task ConnectAsync(HostOffer offer, CancellationToken cancellationToken = default)
    {
        if (IsActive) throw new InvalidOperationException("已经在共享中。");
        _hostName = offer.Name;
        _hostAddress = offer.AddressText;
        _hostMode = offer.Mode;
        SetState(SourceState.Connecting, "正在连接输出端…");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var setupCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        setupCts.CancelAfter(TimeSpan.FromSeconds(15));
        var token = setupCts.Token;
        var liveToken = _cts.Token;

        try
        {
            _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await _tcp.ConnectAsync(offer.Address, offer.TcpPort, token).ConfigureAwait(false);
            _tcpStream = _tcp.GetStream();
            _reader = new StreamReader(_tcpStream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_tcpStream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await SendControlAsync(ControlProtocol.Hello(_deviceName, _platform), token).ConfigureAwait(false);
            string? welcomeLine = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (welcomeLine is null) throw new IOException("输出端关闭了连接。");
            WelcomeInfo? welcome = ControlProtocol.ParseWelcome(welcomeLine);
            if (welcome is null)
            {
                string? err = ControlProtocol.ParseErrorMessage(welcomeLine);
                throw new InvalidOperationException(err ?? "输出端返回了无效的握手信息。");
            }

            _sessionId = welcome.SessionId;
            _token = welcome.Token;
            _hostMode = welcome.Mode;
            _hostName = welcome.HostName;
            _audioEndpoint = new IPEndPoint(offer.Address, welcome.AudioPort);
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.ReceiveBufferSize = 256 * 1024;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udp.Connect(_audioEndpoint);

            _controlTask = Task.Run(() => ControlLoopAsync(liveToken), CancellationToken.None);
            _pongTask = Task.Run(() => PongLoopAsync(liveToken), CancellationToken.None);
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(liveToken), CancellationToken.None);

            SetState(SourceState.Syncing, "正在校准设备时钟…");
            _pingTask = Task.Run(() => InitialClockSyncAsync(liveToken), CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while ((_clock.SampleCount < 4 || !_clock.IsReady) &&
                   DateTime.UtcNow < deadline && !token.IsCancellationRequested)
                await Task.Delay(40, token).ConfigureAwait(false);
            if (_clock.SampleCount < 4 || !_clock.IsReady)
                throw new TimeoutException("时钟校准超时，请检查 Wi-Fi 是否稳定。");

            _sharing = true;
            _capture.Start();
            _senderTask = Task.Run(() => SendLoop(liveToken), CancellationToken.None);
            await SendControlAsync(ControlProtocol.Start(), token).ConfigureAwait(false);
            SetState(SourceState.Sharing, "正在共享本机声音");
            _ = Task.Run(() => PingLoopAsync(liveToken), liveToken);
        }
        catch (Exception e)
        {
            _message = TranslateError(e);
            SetState(SourceState.Error, _message);
            await CleanupAsync().ConfigureAwait(false);
            Faulted?.Invoke(_message);
            throw;
        }
        finally
        {
            setupCts.Dispose();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_cts is null) return;
        _sharing = false;
        try
        {
            if (_writer is not null)
            {
                using var shortCts = new CancellationTokenSource(300);
                await SendControlAsync(ControlProtocol.Stop(), shortCts.Token).ConfigureAwait(false);
                await SendControlAsync(ControlProtocol.Bye(), shortCts.Token).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }

        SetState(SourceState.Idle, "已停止共享");
        await CleanupAsync().ConfigureAwait(false);
    }

    private async Task CleanupAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { _capture.Stop(); } catch { /* ignore */ }

        _sendQueue.CompleteAdding();
        foreach (var task in new[] { _controlTask, _pongTask, _senderTask, _heartbeatTask, _pingTask })
        {
            if (task is null) continue;
            try { await Task.WhenAny(task, Task.Delay(500)).ConfigureAwait(false); } catch { /* ignore */ }
        }
        _controlTask = null;
        _pongTask = null;
        _senderTask = null;
        _heartbeatTask = null;
        _pingTask = null;

        try { _udp?.Close(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Close(); } catch { /* ignore */ }
        _udp = null;
        _writer = null;
        _reader = null;
        _tcpStream = null;
        _tcp = null;
        _audioEndpoint = null;
        cts?.Dispose();

        // A fresh queue is needed if the client is reused.
        while (_sendQueue.TryTake(out _)) { }
        // BlockingCollection cannot be un-completed; SourceClient is single-use.
    }

    private void OnCaptureFaulted(string message)
    {
        if (!_sharing) return;
        Fail($"系统声音采集失败：{message}");
    }

    private void OnCaptureData(short[] samples, long captureStartLocalUs)
    {
        if (!_sharing || _udp is null || !_clock.IsReady) return;
        try
        {
            lock (_captureGate)
            {
                // Keep a continuous sample clock across capture callbacks. Only
                // re-anchor on the first block or on a real capture gap; doing it
                // on every callback injects callback scheduling jitter into the
                // timestamps and causes audible clicks at the receiver.
                if (_nextPacketLocalUs == 0 ||
                    captureStartLocalUs > _nextPacketLocalUs + 250_000 ||
                    captureStartLocalUs + 100_000 < _nextPacketLocalUs)
                {
                    if (_nextPacketLocalUs != 0) _pending.Clear();
                    _nextPacketLocalUs = captureStartLocalUs;
                }

                _pending.AddRange(samples);

                while (_pending.Count >= ProtocolConstants.PacketFrames * ProtocolConstants.Channels)
                {
                    var packetSamples = new short[ProtocolConstants.PacketFrames * ProtocolConstants.Channels];
                    for (int i = 0; i < packetSamples.Length; i++) packetSamples[i] = _pending[i];
                    _pending.RemoveRange(0, packetSamples.Length);

                    long localStart = _nextPacketLocalUs;
                    _nextPacketLocalUs += ProtocolConstants.PacketDurationMs * 1000L;
                    long hostCapture = _clock.ToHostTime(localStart);
                    byte[] payload = PcmCodec.EncodePcm16(packetSamples);
                    uint sequence = _sequence++;
                    byte[] packet = AudioPacket.Encode(
                        ProtocolConstants.PacketTypeAudio,
                        _sessionId,
                        _token,
                        sequence,
                        hostCapture,
                        payload);

                    if (!_sendQueue.TryAdd(packet))
                    {
                        Interlocked.Increment(ref _dropped);
                    }
                    else
                    {
                        Interlocked.Increment(ref _packetsSent);
                        Interlocked.Add(ref _bytesSent, packet.Length);
                    }
                }

                if (_pending.Count > ProtocolConstants.PacketFrames * ProtocolConstants.Channels * 50)
                    _pending.Clear();
            }
        }
        catch (Exception)
        {
            // Never let a bad capture callback take down the capture thread.
        }
    }

    private void SendLoop(CancellationToken token)
    {
        try
        {
            foreach (byte[] packet in _sendQueue.GetConsumingEnumerable(token))
            {
                if (_udp is null) break;
                try
                {
                    lock (_socketSendGate) _udp.Send(packet, packet.Length);
                }
                catch (SocketException)
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task InitialClockSyncAsync(CancellationToken token)
    {
        for (int i = 0; i < 8 && !token.IsCancellationRequested; i++)
        {
            SendPing();
            await Task.Delay(70, token).ConfigureAwait(false);
            if (_clock.SampleCount >= 5 && _clock.BestRttUs < 100_000) break;
        }
    }

    private async Task PingLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(4000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { SendPing(); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { }
            catch (ObjectDisposedException) { break; }
        }
    }

    private void SendPing()
    {
        if (_udp is null) return;
        uint seq = unchecked(++_pingSequence);
        long t1 = HighResClock.NowUs();
        byte[] ping = AudioPacket.Encode(
            ProtocolConstants.PacketTypePing,
            _sessionId,
            _token,
            seq,
            t1,
            ReadOnlySpan<byte>.Empty);
        try
        {
            lock (_socketSendGate) _udp.Send(ping, ping.Length);
        }
        catch (SocketException)
        {
            // The receive loop will surface a persistent failure.
        }
    }

    private async Task PongLoopAsync(CancellationToken token)
    {
        if (_udp is null) return;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(50, token).ConfigureAwait(false); continue; }

            if (!AudioPacket.TryDecode(result.Buffer, out var packet) || packet is null) continue;
            if (packet.Type != ProtocolConstants.PacketTypePong) continue;
            if (packet.SessionId != _sessionId || packet.Token != _token) continue;
            if (!AudioPacket.TryReadPongTimes(packet, out long t1, out long t2)) continue;

            long t3 = packet.CaptureUs;
            long t4 = HighResClock.NowUs();
            long rtt = (t4 - t1) - (t3 - t2);
            if (rtt < 0 || rtt > 1_000_000) continue;
            _clock.AddSample(t1, t2, t3, t4);
            _lastRttUs = rtt;
            RaiseStatus();
        }
    }

    private async Task ControlLoopAsync(CancellationToken token)
    {
        if (_reader is null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    if (_sharing) Fail("输出端已断开。");
                    return;
                }
                string? type = ControlProtocol.ReadType(line);
                switch (type)
                {
                    case "started":
                        _message = "正在共享本机声音";
                        RaiseStatus();
                        break;
                    case "mode":
                        HostMode? mode = ControlProtocol.ParseMode(line);
                        if (mode is not null)
                        {
                            _hostMode = mode.Value;
                            RaiseStatus();
                        }
                        break;
                    case "error":
                        string? err = ControlProtocol.ParseErrorMessage(line);
                        Fail(err ?? "输出端返回错误。");
                        return;
                    case "bye":
                        if (_sharing) Fail("输出端已停止。");
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { if (_sharing) Fail("与输出端的连接已断开。"); }
        catch (ObjectDisposedException) { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token).ConfigureAwait(false);
                await SendControlAsync(ControlProtocol.Heartbeat(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task SendControlAsync(byte[] json, CancellationToken token = default)
    {
        if (_writer is null) return;
        await _controlWriteGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(Encoding.UTF8.GetString(json).AsMemory(), token).ConfigureAwait(false);
        }
        finally
        {
            _controlWriteGate.Release();
        }
    }

    private void Fail(string message)
    {
        if ((SourceState)Volatile.Read(ref _state) == SourceState.Error) return;
        _message = message;
        _sharing = false;
        SetState(SourceState.Error, message);
        Faulted?.Invoke(message);
        _ = Task.Run(async () =>
        {
            try { await CleanupAsync().ConfigureAwait(false); } catch { /* ignore */ }
        });
    }

    private void SetState(SourceState state, string message)
    {
        Volatile.Write(ref _state, (int)state);
        _message = message;
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(GetStatus()); } catch { /* UI must not break audio */ }
    }

    private static string TranslateError(Exception e) => e switch
    {
        SocketException => "无法连接输出端，请确认两台设备在同一个 Wi-Fi。",
        TimeoutException => e.Message,
        OperationCanceledException => "连接超时。",
        IOException => "网络连接中断。",
        _ => e.Message
    };

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _capture.DataAvailable -= OnCaptureData;
        _capture.Faulted -= OnCaptureFaulted;
        _capture.Dispose();
        _sendQueue.Dispose();
    }
}
