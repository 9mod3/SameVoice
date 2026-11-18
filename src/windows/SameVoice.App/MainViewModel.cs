using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SameVoice.Core;
using SameVoice.Core.Host;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sender;

namespace SameVoice.App;

public sealed class SourceRowViewModel : INotifyPropertyChanged
{
    private static readonly Brush ActiveBrush = Frozen("#71EBC4");
    private static readonly Brush WarningBrush = Frozen("#FFBF69");
    private static readonly Brush IdleBrush = Frozen("#5D6880");

    public uint Id { get; init; }
    public string Name { get; private set; } = "";
    public string PlatformLabel { get; private set; } = "";
    public string Address { get; private set; } = "";
    public string Initial { get; private set; } = "?";
    public string StatusText { get; private set; } = "";
    public string BufferText { get; private set; } = "";
    public string DetailText { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = IdleBrush;
    public double ActivityOpacity { get; private set; }
    public bool IsSharing { get; private set; }

    private double _volumePercent = 80;
    private bool _volumeInitialized;

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _volumePercent) < 0.01) return;
            _volumePercent = clamped;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(HostSourceInfo info)
    {
        Name = info.Name;
        PlatformLabel = info.Platform switch
        {
            "windows" => "Windows",
            "android" => "Android",
            _ => "设备"
        };
        Address = info.Address;
        Initial = string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
        IsSharing = info.IsSharing;
        bool stale = info.IsSharing && info.LastPacketAgeMs > 3000;
        StatusText = info.IsSharing ? (stale ? "信号中断" : "正在混音") : "已连接";
        StatusBrush = info.IsSharing ? (stale ? WarningBrush : ActiveBrush) : IdleBrush;
        ActivityOpacity = info.IsSharing && !stale ? 1.0 : 0.35;
        BufferText = info.IsSharing ? $"{info.BufferMs:0} ms 缓冲" : "等待开始共享";
        DetailText = $"{PlatformLabel} · {Address}";
        if (!_volumeInitialized)
        {
            _volumePercent = Math.Clamp(info.Volume * 100, 0, 100);
            _volumeInitialized = true;
        }
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed class DiscoveredHostViewModel : INotifyPropertyChanged
{
    private bool _isConnected;
    private bool _isBusy;

    public HostOffer Offer { get; }
    public string Name { get; }
    public string Details { get; }
    public string Mode { get; }
    public string SourceText { get; }
    public string Initial { get; }
    public string Address { get; }
    public string DetailsLine => $"{Details} · {SourceText}";
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectText)));
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectText)));
        }
    }
    public string SelectText => IsBusy ? "连接中…" : IsConnected ? "已选" : "选择";

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiscoveredHostViewModel(HostOffer offer)
    {
        Offer = offer;
        Name = offer.Name;
        Address = offer.AddressText;
        Details = $"{offer.AddressText} · {offer.TcpPort}";
        Mode = offer.Mode.DisplayName();
        SourceText = offer.Sources <= 0 ? "尚无声源" : $"{offer.Sources} 路音源";
        Initial = string.IsNullOrWhiteSpace(Name) ? "S" : Name[..1].ToUpperInvariant();
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush MintBrush = Frozen("#71EBC4");
    private static readonly Brush WarningBrush = Frozen("#FFBF69");
    private static readonly Brush DangerBrush = Frozen("#FF7C8C");
    private static readonly Brush MutedBrush = Frozen("#5D6880");

    private bool _isHostRunning;
    private string _hostStatusTitle = "输出端未启动";
    private string _hostStatusDetail = "启动后，同一 Wi-Fi 下的手机和其他电脑可以发现这台设备，把声音送到这里。";
    private string _hostAddressText = "正在读取本机地址…";
    private string _hostPortsText = "TCP 47820 · UDP 47822";
    private string _hostModeText = "同步优先";
    private string _hostSourceText = "0 路音源";
    private string _hostBadge = "离线";
    private string _hostButtonText = "启动输出";
    private Brush _hostStatusBrush = MutedBrush;
    private double _masterVolume = 80;
    private string _masterVolumeText = "80%";
    private string _sourceCountText = "等待接入";
    private bool _hasSources;
    private bool _isShareActive;
    private bool _isShareBusy;
    private bool _isScanning;
    private string _shareStatusTitle = "未共享本机声音";
    private string _shareStatusDetail = "先扫描并选择一台或多台输出设备，然后授权捕获本机正在播放的声音。";
    private Brush _shareStatusBrush = MutedBrush;
    private string _shareButtonText = "扫描输出设备";
    private string _shareHostName = "尚未连接";
    private string _shareNetworkText = "—";
    private string _sharePacketText = "—";
    private string _shareBadge = "待机";
    private string _hostCountText = "未扫描";
    private string _lastMessage = "同一 Wi-Fi · 无需登录 · 不占用独占音频设备";
    private bool _hasHosts;

    public ObservableCollection<SourceRowViewModel> Sources { get; } = new();
    public ObservableCollection<DiscoveredHostViewModel> Hosts { get; } = new();

    public bool IsHostRunning
    {
        get => _isHostRunning;
        private set { if (Set(ref _isHostRunning, value)) { OnPropertyChanged(nameof(HostButtonText)); } }
    }

    public string HostStatusTitle { get => _hostStatusTitle; private set => Set(ref _hostStatusTitle, value); }
    public string HostStatusDetail { get => _hostStatusDetail; private set => Set(ref _hostStatusDetail, value); }
    public string HostAddressText { get => _hostAddressText; set => Set(ref _hostAddressText, value); }
    public string HostPortsText { get => _hostPortsText; private set => Set(ref _hostPortsText, value); }
    public string HostModeText { get => _hostModeText; private set => Set(ref _hostModeText, value); }
    public string HostSourceText { get => _hostSourceText; private set => Set(ref _hostSourceText, value); }
    public string HostBadge { get => _hostBadge; private set => Set(ref _hostBadge, value); }
    public string HostButtonText { get => _hostButtonText; private set => Set(ref _hostButtonText, value); }
    public Brush HostStatusBrush { get => _hostStatusBrush; private set => Set(ref _hostStatusBrush, value); }
    public string SourceCountText { get => _sourceCountText; private set => Set(ref _sourceCountText, value); }
    public bool HasSources { get => _hasSources; private set => Set(ref _hasSources, value); }
    public bool IsShareActive { get => _isShareActive; private set => Set(ref _isShareActive, value); }
    public bool IsShareBusy { get => _isShareBusy; private set => Set(ref _isShareBusy, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public string ShareStatusTitle { get => _shareStatusTitle; private set => Set(ref _shareStatusTitle, value); }
    public string ShareStatusDetail { get => _shareStatusDetail; private set => Set(ref _shareStatusDetail, value); }
    public Brush ShareStatusBrush { get => _shareStatusBrush; private set => Set(ref _shareStatusBrush, value); }
    public string ShareButtonText { get => _shareButtonText; private set => Set(ref _shareButtonText, value); }
    public string ShareHostName { get => _shareHostName; private set => Set(ref _shareHostName, value); }
    public string ShareNetworkText { get => _shareNetworkText; private set => Set(ref _shareNetworkText, value); }
    public string SharePacketText { get => _sharePacketText; private set => Set(ref _sharePacketText, value); }
    public string ShareBadge { get => _shareBadge; private set => Set(ref _shareBadge, value); }
    public string HostCountText { get => _hostCountText; private set => Set(ref _hostCountText, value); }
    public string LastMessage { get => _lastMessage; private set => Set(ref _lastMessage, value); }
    public bool HasHosts { get => _hasHosts; private set => Set(ref _hasHosts, value); }

    public double MasterVolume
    {
        get => _masterVolume;
        private set
        {
            if (Set(ref _masterVolume, value))
            {
                MasterVolumeText = $"{value:0}%";
            }
        }
    }

    public string MasterVolumeText { get => _masterVolumeText; private set => Set(ref _masterVolumeText, value); }

    public void SetHostSnapshot(HostSnapshot snapshot)
    {
        IsHostRunning = snapshot.IsRunning;
        HostStatusTitle = snapshot.IsRunning ? "输出端正在运行" : "输出端未启动";
        HostStatusDetail = snapshot.IsRunning
            ? snapshot.SourceCount > 0
                ? "所有已开启共享的设备声音，会在这里同步混音并从默认扬声器播放。"
                : "设备已经就绪，请在其他设备的「共享声音」页连接这台电脑。"
            : "启动后，同一 Wi-Fi 下的手机和其他电脑可以发现这台设备，把声音送到这里。";
        HostAddressText = snapshot.LocalAddresses;
        HostPortsText = snapshot.ControlPort > 0
            ? $"TCP {snapshot.ControlPort} · UDP {snapshot.AudioPort}"
            : "端口未分配";
        HostModeText = snapshot.Mode.DisplayName();
        HostSourceText = snapshot.SourceCount == 1 ? "1 路音源" : $"{snapshot.SourceCount} 路音源";
        HostBadge = snapshot.IsRunning ? "输出中" : "离线";
        HostButtonText = snapshot.IsRunning ? "停止输出" : "启动输出";
        HostStatusBrush = snapshot.IsRunning ? MintBrush : MutedBrush;
        SourceCountText = snapshot.SourceCount == 0 ? "等待接入" : $"{snapshot.SourceCount} 路";
        MasterVolume = snapshot.MasterVolume * 100;
        UpdateSources(snapshot.Sources);
    }

    public void SetHostStopped()
    {
        IsHostRunning = false;
        HostStatusTitle = "输出端未启动";
        HostStatusDetail = "启动后，同一 Wi-Fi 下的手机和其他电脑可以发现这台设备，把声音送到这里。";
        HostBadge = "离线";
        HostStatusBrush = MutedBrush;
        HostButtonText = "启动输出";
        HostSourceText = "0 路音源";
        SourceCountText = "等待接入";
        Sources.Clear();
        HasSources = false;
    }

    public void SetMasterVolume(double percent)
    {
        MasterVolume = Math.Clamp(percent, 0, 200);
    }

    private void UpdateSources(IReadOnlyList<HostSourceInfo> infos)
    {
        var existing = Sources.ToDictionary(s => s.Id);
        var seen = new HashSet<uint>();
        foreach (var info in infos)
        {
            seen.Add(info.Id);
            if (existing.TryGetValue(info.Id, out var row))
            {
                row.Update(info);
            }
            else
            {
                var added = new SourceRowViewModel { Id = info.Id };
                added.Update(info);
                Sources.Add(added);
            }
        }
        for (int i = Sources.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Sources[i].Id))
                Sources.RemoveAt(i);
        }
        HasSources = Sources.Count > 0;
    }

    public void SetShareStatus(SourceClientStatus status)
    {
        IsShareActive = status.State is SourceState.Connecting or SourceState.Syncing or SourceState.Sharing;
        IsShareBusy = status.State is SourceState.Connecting or SourceState.Syncing;
        ShareStatusTitle = status.State switch
        {
            SourceState.Connecting => "正在连接输出端",
            SourceState.Syncing => "正在校准时钟",
            SourceState.Sharing => "正在共享本机声音",
            SourceState.Error => "共享中断",
            _ => "未共享本机声音"
        };
        ShareStatusDetail = status.State switch
        {
            SourceState.Connecting => "正在和输出设备建立加密随机会话…",
            SourceState.Syncing => "通过 NTP 式往返校准，让多台设备的声音对齐。",
            SourceState.Sharing => $"声音已送往 {status.HostName}，关闭窗口后仍会在托盘继续。",
            SourceState.Error => status.Message,
            _ => "先扫描并选择一台或多台输出设备，然后授权捕获本机正在播放的声音。"
        };
        ShareStatusBrush = status.State switch
        {
            SourceState.Sharing => MintBrush,
            SourceState.Error => DangerBrush,
            SourceState.Connecting or SourceState.Syncing => WarningBrush,
            _ => MutedBrush
        };
        ShareBadge = status.State switch
        {
            SourceState.Sharing => "共享中",
            SourceState.Connecting or SourceState.Syncing => "连接中",
            SourceState.Error => "异常",
            _ => "待机"
        };
        ShareHostName = string.IsNullOrEmpty(status.HostName) ? "尚未连接" : status.HostName;
        ShareNetworkText = status.RttMs >= 0 ? $"{status.RttMs:0} ms 往返" : "时钟同步中";
        SharePacketText = status.PacketsSent > 0
            ? $"{status.PacketsSent:N0} 包 · {status.BytesSent / 1024.0 / 1024.0:0.0} MB"
            : "—";
        ShareButtonText = IsShareActive ? "停止共享" : "扫描输出设备";
    }

    public void SetShareIdle()
    {
        IsShareActive = false;
        IsShareBusy = false;
        ShareStatusTitle = "未共享本机声音";
        ShareStatusDetail = "先扫描并选择一台或多台输出设备，然后授权捕获本机正在播放的声音。";
        ShareStatusBrush = MutedBrush;
        ShareHostName = "尚未连接";
        ShareNetworkText = "—";
        SharePacketText = "—";
        ShareBadge = "待机";
        ShareButtonText = "扫描输出设备";
    }

    public void SetScanning(bool scanning)
    {
        IsScanning = scanning;
        if (scanning) LastMessage = "正在扫描同一 Wi-Fi 下的输出设备…";
    }

    public void SetHosts(IEnumerable<HostOffer> offers)
    {
        Hosts.Clear();
        foreach (var offer in offers)
            Hosts.Add(new DiscoveredHostViewModel(offer));
        HasHosts = Hosts.Count > 0;
        HostCountText = Hosts.Count == 0 ? "未发现输出端" : $"发现 {Hosts.Count} 台设备";
        LastMessage = Hosts.Count == 0
            ? "没有发现输出端，请确认对方已启动「输出中心」，或手动输入 IP。"
            : $"发现 {Hosts.Count} 台输出设备。";
    }

    public void SetMessage(string message)
    {
        LastMessage = message;
    }

    public void SetBusy(bool busy) => IsShareBusy = busy;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
