using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SameVoice.Core.Audio;
using SameVoice.Core.Net;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sync;

namespace SameVoice.Core.Host;

public sealed record HostSourceInfo(
    uint Id,
    string Name,
    string Platform,
    string Address,
    bool IsSharing,
    double BufferMs,
    float Volume,
    long PacketsReceived,
    long BytesReceived,
    long MissingPackets,
    long LastPacketAgeMs);

public sealed record HostSnapshot(
    bool IsRunning,
    string HostName,
    string RoomId,
    string LocalAddresses,
    int ControlPort,
    int AudioPort,
    HostMode Mode,
    int SourceCount,
    double Peak,
    float MasterVolume,
    IReadOnlyList<HostSourceInfo> Sources);

public sealed class HostEngine : IDisposable
{
    private readonly IAudioOutput _output;
    private readonly MixEngine _mix = new();
    private readonly ConcurrentDictionary<uint, SourceConnection> _sessions = new();
    private readonly ConcurrentDictionary<ulong, SourceConnection> _byToken = new();
    private readonly string _hostName;
    private readonly string _roomId;
    private readonly object _lifecycleGate = new();

    private TcpListener? _tcpListener;
    private UdpClient? _audioUdp;
    private HostDiscoveryResponder? _discovery;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _audioTask;
    private Task? _maintenanceTask;
    private HostMode _mode = HostMode.Balanced;
    private uint _nextSessionId;
    private bool _running;
    private string? _fatalError;

    public HostEngine(IAudioOutput output, string hostName, HostMode mode = HostMode.Balanced)
    {
        _output = output;
        _hostName = string.IsNullOrWhiteSpace(hostName) ? "SameVoice 输出端" : hostName.Trim();
        _mode = mode;
        _mix.SetMode(_mode.BufferMs());
        _roomId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _nextSessionId = (uint)RandomNumberGenerator.GetInt32(1000, 1_000_000);
    }

    public string HostName => _hostName;
    public string RoomId => _roomId;
    public bool IsRunning => _running;
    public HostMode Mode => _mode;
    public int ControlPort => _tcpListener?.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;
    public int AudioPort => _audioUdp?.Client.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;
    public string? FatalError => _fatalError;

    public event Action? SourcesChanged;
    public event Action<string>? Log;
    public event Action<string>? ErrorOccurred;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _mix.SetMode(_mode.BufferMs());
            _mix.Start();

            try
            {
                _tcpListener = BindControlListener();
                _audioUdp = BindAudioSocket();
                _discovery = new HostDiscoveryResponder(ProtocolConstants.DiscoveryPort, BuildOffer);
                _output.Start(RenderBlock);
                _running = true;

                _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _audioTask = Task.Run(() => AudioReceiveLoopAsync(_cts.Token));
                _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_cts.Token));

                Log?.Invoke($"输出已启动，控制端口 TCP {ControlPort}，音频端口 UDP {AudioPort}");
                SourcesChanged?.Invoke();
            }
            catch
            {
                Stop();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (!_running && _cts is null) return;
            _running = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _output.Stop(); } catch { /* ignore */ }
            _mix.Stop();

            foreach (var session in _sessions.Values)
            {
                try { _ = session.SendAsync(ControlProtocol.Bye()); } catch { /* ignore */ }
                session.Close();
            }
            _sessions.Clear();
            _byToken.Clear();

            try { _discovery?.Dispose(); } catch { /* ignore */ }
            try { _tcpListener?.Stop(); } catch { /* ignore */ }
            try { _audioUdp?.Close(); } catch { /* ignore */ }
            _discovery = null;
            _tcpListener = null;
            _audioUdp = null;

            try { _acceptTask?.Wait(300); } catch { /* ignore */ }
            try { _audioTask?.Wait(300); } catch { /* ignore */ }
            try { _maintenanceTask?.Wait(300); } catch { /* ignore */ }
            _acceptTask = null;
            _audioTask = null;
            _maintenanceTask = null;
            _cts?.Dispose();
            _cts = null;
            SourcesChanged?.Invoke();
        }
    }

    public void SetMode(HostMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _mix.SetMode(mode.BufferMs());
        BroadcastMode(mode);
        SourcesChanged?.Invoke();
        Log?.Invoke($"已切换为{mode.DisplayName()}，缓冲目标 {mode.BufferMs()} ms");
    }

    public void SetMasterVolume(float volume)
    {
        _mix.MasterVolume = volume;
        _output.SetVolume(volume);
    }

    public void SetSourceVolume(uint id, float volume)
    {
        _mix.SetSourceVolume(id, volume);
    }

    public HostSnapshot GetSnapshot()
    {
        var mixInfo = _mix.GetSourceInfo().ToDictionary(i => i.SourceId);
        var sources = _sessions.Values
            .OrderBy(s => s.ConnectedAtTicks)
            .Select(s =>
            {
                double buffer = mixInfo.TryGetValue(s.Id, out var mi) ? mi.BufferAheadMs : 0;
                float volume = mixInfo.TryGetValue(s.Id, out var mi2) ? mi2.Volume : 0.8f;
                long now = Environment.TickCount64;
                return new HostSourceInfo(
                    s.Id,
                    s.Name,
                    s.Platform,
                    s.Address,
                    s.IsSharing,
                    buffer,
                    volume,
                    Interlocked.Read(ref s.PacketsReceived),
                    Interlocked.Read(ref s.BytesReceived),
                    Interlocked.Read(ref s.MissingPackets),
                    Math.Max(0, now - s.LastSeenTicks));
            })
            .ToList();

        return new HostSnapshot(
            _running,
            _hostName,
            _roomId,
            NetworkUtil.FirstLocalAddressText(),
            ControlPort,
            AudioPort,
            _mode,
            _sessions.Count,
            _mix.Peak,
            _mix.MasterVolume,
            sources);
    }

    private TcpListener BindControlListener()
    {
        Exception? last = null;
        for (int port = ProtocolConstants.DefaultControlPort; port < ProtocolConstants.DefaultControlPort + 10; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(16);
                return listener;
            }
            catch (SocketException e)
            {
                last = e;
            }
        }
        throw new InvalidOperationException("控制端口 47820–47829 均被占用，无法启动输出。", last);
    }

    private UdpClient BindAudioSocket()
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, ProtocolConstants.DefaultAudioPort));
            udp.Client.ReceiveBufferSize = 512 * 1024;
            return udp;
        }
        catch (SocketException)
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            udp.Client.ReceiveBufferSize = 512 * 1024;
            return udp;
        }
    }

    private byte[] BuildOffer() => DiscoveryProtocol.CreateOffer(
        _roomId,
        _hostName,
        NetworkUtil.PlatformName(),
        ControlPort,
        _mode,
        _sessions.Count,
        false);

    private int RenderBlock(short[] buffer, int frames) => _mix.Render(buffer, frames);

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tcpListener is not null)
        {
            TcpClient client;
            try
            {
                client = await _tcpListener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        SourceConnection? session = null;
        try
        {
            if (_sessions.Count >= 8)
            {
                client.Close();
                return;
            }

            client.NoDelay = true;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            string? helloLine = await ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(6), token)
                .ConfigureAwait(false);
            if (helloLine is null) { client.Close(); return; }

            string? type = ControlProtocol.ReadType(helloLine);
            if (type != "hello") { client.Close(); return; }
            (string name, string platform) = ParseHello(helloLine);
            if (string.IsNullOrWhiteSpace(name)) name = "未命名设备";
            if (name.Length > 60) name = name[..60];
            if (string.IsNullOrWhiteSpace(platform)) platform = "unknown";
            if (platform.Length > 20) platform = platform[..20];

            uint id = unchecked(Interlocked.Increment(ref _nextSessionId));
            ulong tokenValue = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            var remote = (IPEndPoint?)client.Client.RemoteEndPoint;
            session = new SourceConnection(id, tokenValue, name, platform, remote?.Address.ToString() ?? "?", client, stream, writer, reader);
            _sessions[id] = session;
            _byToken[tokenValue] = session;
            _mix.AddSource(id);
            _mix.SetSourceVolume(id, 0.8f);

            await session.SendAsync(ControlProtocol.Welcome(id, tokenValue, AudioPort, _hostName, _mode), token)
                .ConfigureAwait(false);
            Log?.Invoke($"{name} ({session.Address}) 已连接");
            SourcesChanged?.Invoke();

            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                session.LastSeenTicks = Environment.TickCount64;
                string? msgType = ControlProtocol.ReadType(line);
                switch (msgType)
                {
                    case "start":
                        session.IsSharing = true;
                        await session.SendAsync(ControlProtocol.Started(), token).ConfigureAwait(false);
                        SourcesChanged?.Invoke();
                        Log?.Invoke($"{session.Name} 开始共享声音");
                        break;
                    case "stop":
                        session.IsSharing = false;
                        _mix.ClearBuffers();
                        SourcesChanged?.Invoke();
                        break;
                    case "heartbeat":
                        break;
                    case "renamed":
                        string? newName = ControlProtocol.ParseName(line);
                        if (!string.IsNullOrWhiteSpace(newName))
                        {
                            session.Name = newName.Length > 60 ? newName[..60] : newName;
                            SourcesChanged?.Invoke();
                        }
                        break;
                    case "bye":
                        return;
                    case null:
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception e)
        {
            _fatalError = e.Message;
            ErrorOccurred?.Invoke(e.Message);
        }
        finally
        {
            if (session is not null) RemoveSession(session);
            else client.Close();
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try { return await reader.ReadLineAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
    }

    private static (string Name, string Platform) ParseHello(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string platform = root.TryGetProperty("platform", out var p) ? p.GetString() ?? "" : "";
            return (name, platform);
        }
        catch (JsonException) { return ("", ""); }
    }

    private void RemoveSession(SourceConnection session)
    {
        if (!session.TryClose()) return;
        _sessions.TryRemove(session.Id, out _);
        _byToken.TryRemove(session.Token, out _);
        _mix.RemoveSource(session.Id);
        try { session.Close(); } catch { /* ignore */ }
        SourcesChanged?.Invoke();
        Log?.Invoke($"{session.Name} 已断开");
    }

    private async Task AudioReceiveLoopAsync(CancellationToken token)
    {
        if (_audioUdp is null) return;
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _audioUdp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (!AudioPacket.TryDecode(result.Buffer, out var packet) || packet is null) continue;
            if (!_byToken.TryGetValue(packet.Token, out var session)) continue;
            if (packet.SessionId != session.Id) continue;

            session.LastSeenTicks = Environment.TickCount64;
            session.RemoteAudioEndpoint = result.RemoteEndPoint;

            if (packet.Type == ProtocolConstants.PacketTypePing)
            {
                long t2 = HighResClock.NowUs();
                byte[] pong = AudioPacket.EncodePong(
                    session.Id,
                    session.Token,
                    packet.Sequence,
                    packet.CaptureUs,
                    t2,
                    HighResClock.NowUs());
                try { await _audioUdp.SendAsync(pong, result.RemoteEndPoint, token).ConfigureAwait(false); }
                catch (SocketException) { }
                continue;
            }

            if (packet.Type != ProtocolConstants.PacketTypeAudio) continue;
            if (packet.Payload.Length != ProtocolConstants.PacketBytes) continue;

            RecordPacket(session, packet.Sequence);
            Interlocked.Add(ref session.BytesReceived, packet.Payload.Length);
            if (packet.IsMuted) continue;

            short[] samples = PcmCodec.DecodePcm16(packet.Payload);
            _mix.PushAudio(session.Id, packet.CaptureUs, samples, ProtocolConstants.PacketFrames);
        }
    }

    private static void RecordPacket(SourceConnection session, uint sequence)
    {
        Interlocked.Increment(ref session.PacketsReceived);
        uint previous = session.LastSequence;
        if (session.HasSequence)
        {
            int diff = unchecked((int)(sequence - previous));
            if (diff > 1 && diff < 1000)
                Interlocked.Add(ref session.MissingPackets, diff - 1);
        }
        session.HasSequence = true;
        session.LastSequence = sequence;
    }

    private async Task MaintenanceLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            long now = Environment.TickCount64;
            foreach (var session in _sessions.Values)
            {
                if (now - session.LastSeenTicks > 15_000)
                    RemoveSession(session);
            }
        }
    }

    private void BroadcastMode(HostMode mode)
    {
        byte[] message = ControlProtocol.Mode(mode);
        foreach (var session in _sessions.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await session.SendAsync(message).ConfigureAwait(false); } catch { /* ignore */ }
            });
        }
    }

    public void Dispose()
    {
        Stop();
        _output.Dispose();
    }

    private sealed class SourceConnection
    {
        private int _closed;
        public SourceConnection(
            uint id,
            ulong token,
            string name,
            string platform,
            string address,
            TcpClient client,
            NetworkStream stream,
            StreamWriter writer,
            StreamReader reader)
        {
            Id = id;
            Token = token;
            Name = name;
            Platform = platform;
            Address = address;
            Client = client;
            Stream = stream;
            Writer = writer;
            Reader = reader;
            ConnectedAtTicks = Environment.TickCount64;
            LastSeenTicks = ConnectedAtTicks;
        }

        public uint Id { get; }
        public ulong Token { get; }
        public string Name { get; set; }
        public string Platform { get; }
        public string Address { get; }
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public StreamWriter Writer { get; }
        public StreamReader Reader { get; }
        public long ConnectedAtTicks { get; }
        public long LastSeenTicks { get; set; }
        public volatile bool IsSharing;
        public uint LastSequence;
        public bool HasSequence;
        public long PacketsReceived;
        public long BytesReceived;
        public long MissingPackets;
        public IPEndPoint? RemoteAudioEndpoint;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly object _closeGate = new();

        public async Task SendAsync(byte[] json, CancellationToken token = default)
        {
            string line = Encoding.UTF8.GetString(json);
            await SendAsync(line, token).ConfigureAwait(false);
        }

        public async Task SendAsync(string line, CancellationToken token = default)
        {
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _writeGate.Release();
            }
        }

        public bool TryClose() => Interlocked.Exchange(ref _closed, 1) == 0;

        public void Close()
        {
            try { Client.Close(); } catch { /* ignore */ }
            try { Writer.Dispose(); } catch { /* ignore */ }
            try { Reader.Dispose(); } catch { /* ignore */ }
            try { Stream.Dispose(); } catch { /* ignore */ }
        }
    }
}
