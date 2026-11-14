using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SameVoice.App.Audio;
using SameVoice.Core;
using SameVoice.Core.Audio;
using SameVoice.Core.Host;
using SameVoice.Core.Net;
using SameVoice.Core.Protocol;
using SameVoice.Core.Sender;
using Forms = System.Windows.Forms;

namespace SameVoice.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer;
    private HostEngine? _host;
    private SourceClient? _source;
    private IAudioCapture? _capture;
    private NativeTrayIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private bool _ready;
    private bool _reallyClosing;
    private bool _hasScanned;
    private bool _connectingShare;
    private bool _disposingShare;
    private bool _localMuteApplied;
    private bool _localMuteWasMuted;
    private bool _localMuteViaVoicemeeter;
    private bool _localMuteViaVolume;
    private float _localMuteVolume;
    private string? _localMuteDeviceId;
    private bool _dualVolumeUpdating;
    private bool _trayHintShown;
    private int _currentPage;
    private bool _bufferUpdating;
    private readonly List<QualityOption> _qualityOptions = new()
    {
        new(AudioQuality.Standard, AudioQuality.Standard.DisplayName()),
        new(AudioQuality.High, AudioQuality.High.DisplayName())
    };

    private sealed record QualityOption(AudioQuality Value, string Name);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/SameVoice.ico"));
        Application.Current.SessionEnding += OnSessionEnding;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += Timer_Tick;

        DeviceNameBox.Text = _settings.DeviceName;
        _vm.HostAddressText = NetworkUtil.FirstLocalAddressText();
        _vm.SetMasterVolume(_settings.MasterVolume);

        _bufferUpdating = true;
        BufferSlider.Value = _settings.BufferMs;
        BufferMsText.Text = $"{_settings.BufferMs} ms";
        _bufferUpdating = false;

        // Set the preset radio before _ready so it does not overwrite the
        // user's persisted manual buffer value.
        if (_settings.Mode == HostMode.LowLatency) LowModeRadio.IsChecked = true;
        else BalancedModeRadio.IsChecked = true;
        _ready = true;

        QualityCombo.ItemsSource = _qualityOptions;
        QualityCombo.DisplayMemberPath = nameof(QualityOption.Name);
        QualityCombo.SelectedValuePath = nameof(QualityOption.Value);
        QualityCombo.SelectedValue = _settings.PreferredQuality;

        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => ApplyWindowAppearance();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _timer.Start();
        ShowPage(0);
        InitializeEdition();
        _vm.SetMessage("同一 Wi-Fi · 无需登录 · 不占用独占音频设备");
        CreateTrayIcon();
    }

    private void InitializeEdition()
    {
        EditionText.Text = $"SameVoice 1.4.5 · {BuildEdition.DisplayName}";
        DualRoutePanel.Visibility = BuildEdition.DualRoute ? Visibility.Visible : Visibility.Collapsed;
        LocalMuteButton.Visibility = Visibility.Collapsed;
        if (BuildEdition.DualRoute)
        {
            RefreshCaptureDevices();
            InitializeDualVolumes();
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_host is not null)
        {
            try { _vm.SetHostSnapshot(_host.GetSnapshot()); }
            catch (Exception ex) { _vm.SetMessage("刷新状态失败：" + ex.Message); }
        }
        if (_source is not null)
        {
            try
            {
                var status = _source.GetStatus();
                _vm.SetShareStatus(status);
                UpdateSourceControls(status.State);
            }
            catch { /* ignore */ }
        }
        else
        {
            UpdateSourceControls(SourceState.Idle);
        }
    }

    private void UpdateHostSnapshot()
    {
        if (_host is null) return;
        _vm.SetHostSnapshot(_host.GetSnapshot());
    }

    // ---------------------------------------------------------------- navigation

    private void NavHost_Click(object sender, RoutedEventArgs e) => ShowPage(0);
    private void NavShare_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(1);
        if (!_hasScanned && _source is null && _host is null)
        {
            _hasScanned = true;
            _ = ScanHostsAsync();
        }
    }
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPage(2);

    private void ShowPage(int index)
    {
        _currentPage = index;
        HostPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SharePage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        NavHostButton.IsChecked = index == 0;
        NavShareButton.IsChecked = index == 1;
        NavSettingsButton.IsChecked = index == 2;
        (PageTitleText.Text, PageSubtitleText.Text) = index switch
        {
            1 => ("共享声音", "把本机正在播放的声音，实时送到选定的输出设备。"),
            2 => ("设置", "设备名称、音频采集方式与网络端口。"),
            _ => ("输出中心", "把同一网络里的多台设备，变成一台同步播放器。")
        };
    }

    // ---------------------------------------------------------------- host

    private async void HostToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            StopHost();
            return;
        }

        if (_source is not null || _connectingShare)
        {
            MessageBox.Show(this, "请先停止共享本机声音，再启动输出中心。", "同声", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        HostToggleButton.IsEnabled = false;
        HostErrorText.Text = "";
        HostEngine? candidate = null;
        try
        {
            _settings.Save();
            var sink = new WaveOutSink();
            candidate = new HostEngine(sink, _settings.DeviceName, SelectedMode(), _settings.BufferMs);
            candidate.SourcesChanged += Host_SourcesChanged;
            candidate.Log += Host_Log;
            candidate.ErrorOccurred += Host_Error;
            candidate.SetMasterVolume((float)(_settings.MasterVolume / 100.0));
            await Task.Run(candidate.Start);
            _host = candidate;
            _vm.SetHostSnapshot(_host.GetSnapshot());
            _vm.SetMessage("输出端已启动。现在可以在手机或其他电脑上连接这台设备。");
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            HostErrorText.Text = FriendlyAudioError(ex);
            _vm.SetMessage("输出启动失败：" + ex.Message);
            MessageBox.Show(this, FriendlyAudioError(ex), "无法启动输出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            HostToggleButton.IsEnabled = true;
        }
    }

    private void Host_SourcesChanged()
    {
        Dispatcher.BeginInvoke(UpdateHostSnapshot);
    }

    private void Host_Log(string message)
    {
        Dispatcher.BeginInvoke(() => _vm.SetMessage(message));
    }

    private void Host_Error(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _vm.SetMessage("输出错误：" + message);
            ShowTrayBalloon("输出端出现问题", message);
        });
    }

    private void StopHost()
    {
        var host = _host;
        _host = null;
        try { host?.Stop(); } catch { /* ignore */ }
        try { host?.Dispose(); } catch { /* ignore */ }
        _vm.SetHostStopped();
        _vm.SetMessage("输出已停止，其他设备现在无法连接到这台电脑。");
        UpdateTrayText();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        HostMode mode = SelectedMode();
        _settings.Mode = mode;
        _settings.BufferMs = mode.BufferMs();
        SetBufferSlider(mode.BufferMs());
        _settings.Save();
        if (_host is not null)
        {
            _host.SetMode(mode);
            _vm.SetMessage($"播放策略已切换为{mode.DisplayName()}。");
        }
    }

    private void Buffer_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _bufferUpdating) return;
        int ms = (int)Math.Round(e.NewValue);
        _settings.BufferMs = ms;
        _settings.Save();
        BufferMsText.Text = $"{ms} ms";
        if (_host is not null)
        {
            _host.SetBufferMs(ms);
            _vm.SetMessage($"缓冲延迟已调整为 {ms} ms。");
        }
    }

    private void SetBufferSlider(int ms)
    {
        _bufferUpdating = true;
        BufferSlider.Value = Math.Clamp(ms, 40, 1000);
        BufferMsText.Text = $"{BufferSlider.Value:0} ms";
        _bufferUpdating = false;
    }

    private HostMode SelectedMode() =>
        LowModeRadio?.IsChecked == true ? HostMode.LowLatency : HostMode.Balanced;

    private void VolumeDown_Click(object sender, RoutedEventArgs e) => ChangeVolume(-5);
    private void VolumeUp_Click(object sender, RoutedEventArgs e) => ChangeVolume(+5);

    private void SourceVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        if (sender is System.Windows.Controls.Slider { DataContext: SourceRowViewModel row })
            _host?.SetSourceVolume(row.Id, (float)(e.NewValue / 100.0));
    }

    private void ChangeVolume(int delta)
    {
        _settings.MasterVolume = Math.Clamp(_settings.MasterVolume + delta, 0, 200);
        _settings.Save();
        _vm.SetMasterVolume(_settings.MasterVolume);
        _host?.SetMasterVolume((float)(_settings.MasterVolume / 100.0));
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var addresses = NetworkUtil.GetLocalIPv4Addresses();
            string text = addresses.Count > 0 ? addresses[0].ToString() : _vm.HostAddressText;
            Clipboard.SetText(text);
            _vm.SetMessage($"已复制本机地址 {text}。请在另一台设备的手动连接中输入。");
        }
        catch (Exception ex)
        {
            _vm.SetMessage("复制失败：" + ex.Message);
        }
    }

    private static string FriendlyAudioError(Exception ex)
    {
        string message = ex.Message;
        if (ex.InnerException is not null) message += "\n" + ex.InnerException.Message;
        if (message.Contains("waveOut", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("音频", StringComparison.Ordinal))
        {
            return "无法打开默认扬声器。请确认系统有可用的播放设备，并且没有被独占。\n\n" + message;
        }
        return "无法启动输出中心：\n" + message;
    }

    // ---------------------------------------------------------------- sharing

    private async void SharePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not null || _connectingShare)
        {
            await StopShareAsync();
            return;
        }
        if (_host is not null)
        {
            _vm.SetMessage("当前正在作为输出端，不能同时共享本机声音。请先停止输出。");
            return;
        }
        await ScanHostsAsync();
    }

    private async void ScanHosts_Click(object sender, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            _vm.SetMessage("当前正在作为输出端，不能同时共享本机声音。请先停止输出。");
            return;
        }
        await ScanHostsAsync();
    }

    private async Task ScanHostsAsync()
    {
        if (_vm.IsScanning || _connectingShare || _source is not null || _host is not null) return;
        _hasScanned = true;
        _vm.SetScanning(true);
        try
        {
            var offers = await Task.Run(() => DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(3)));
            _vm.SetHosts(offers);
        }
        catch (Exception ex)
        {
            _vm.SetMessage("扫描失败：" + ex.Message);
        }
        finally
        {
            _vm.SetScanning(false);
        }
    }

    private async void ConnectHost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DiscoveredHostViewModel row }) return;
        await ConnectToHostAsync(row.Offer);
    }

    private async void ConnectManualIp_Click(object sender, RoutedEventArgs e)
    {
        string raw = ManualIpBox.Text.Trim();
        if (!IPAddress.TryParse(raw, out IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            MessageBox.Show(this, "请输入有效的 IPv4 地址，例如 192.168.1.100。", "同声", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var offer = new HostOffer("manual", raw, "unknown", ProtocolConstants.DefaultControlPort,
            HostMode.Balanced, 0, false, ip);
        await ConnectToHostAsync(offer);
    }

    private async Task ConnectToHostAsync(HostOffer offer)
    {
        if (_connectingShare || _source is not null) return;
        if (_host is not null)
        {
            MessageBox.Show(this, "当前设备正在作为输出端。请先停止输出，再共享本机声音。",
                "同声", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _connectingShare = true;
        _vm.SetBusy(true);
        HostToggleButton.IsEnabled = false;
        SharePrimaryButton.IsEnabled = false;
        _vm.SetShareStatus(new SourceClientStatus(
            SourceState.Connecting, _settings.DeviceName, offer.Name, offer.AddressText,
            offer.Mode, "正在连接输出端…", -1, 0, 0, 0, false));

        SourceClient? client = null;
        IAudioCapture? capture = null;
        try
        {
            capture = CreateCapture();
            client = new SourceClient(_settings.DeviceName, "windows", capture, _settings.PreferredQuality);
            client.StatusChanged += Source_StatusChanged;
            client.Faulted += Source_Faulted;
            await client.ConnectAsync(offer);
            _source = client;
            _capture = capture;
            _vm.SetShareStatus(client.GetStatus());
            _vm.SetMessage($"正在把本机声音送到 {offer.Name}。窗口关闭后会继续在托盘共享。");
            UpdateTrayText();
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                client.StatusChanged -= Source_StatusChanged;
                client.Faulted -= Source_Faulted;
                try { await client.DisposeAsync(); } catch { /* ignore */ }
            }
            capture?.Dispose();
            _vm.SetShareStatus(new SourceClientStatus(
                SourceState.Error, _settings.DeviceName, offer.Name, offer.AddressText,
                offer.Mode, ex.Message, -1, 0, 0, 0, false));
            _vm.SetMessage("共享失败：" + ex.Message);
        }
        finally
        {
            _connectingShare = false;
            _vm.SetBusy(false);
            HostToggleButton.IsEnabled = true;
            SharePrimaryButton.IsEnabled = true;
        }
    }

    private void Source_StatusChanged(SourceClientStatus status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_source is null || _disposingShare) return;
            _vm.SetShareStatus(status);
        });
    }

    private void Source_Faulted(string message)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_disposingShare || _source is null) return;
            _vm.SetMessage("共享中断：" + message);
            await StopShareAsync(userRequested: false);
        });
    }

    private async Task StopShareAsync(bool userRequested = true)
    {
        if (_disposingShare) return;
        var client = _source;
        var capture = _capture;
        _source = null;
        _capture = null;
        _disposingShare = true;
        try
        {
            if (client is not null)
            {
                client.StatusChanged -= Source_StatusChanged;
                client.Faulted -= Source_Faulted;
                try { await client.DisconnectAsync(); } catch { /* ignore */ }
                try { await client.DisposeAsync(); } catch { /* ignore */ }
            }
            capture?.Dispose();
        }
        finally
        {
            _disposingShare = false;
        }
        _vm.SetShareIdle();
        UpdateSourceControls(SourceState.Idle);
        if (userRequested) _vm.SetMessage("已停止共享本机声音。");
        UpdateTrayText();
    }

    private IAudioCapture CreateCapture()
    {
        var capture = new WasapiLoopbackCapture(BuildEdition.DualRoute ? _settings.CaptureDeviceId : null);
        // DualRoute has no fixed source boost; all make-up gain is user
        // controlled from 0% to 600% on the send-gain slider.
        capture.FixedBoost = BuildEdition.DualRoute ? 1f : 4f;
        capture.ExtraGain = BuildEdition.DualRoute
            ? (float)(_settings.DualRouteRemoteGain / 100.0)
            : 1f;
        return capture;
    }

    private void RefreshCaptureDevices()
    {
        if (!BuildEdition.DualRoute || CaptureDeviceCombo is null) return;
        var endpoints = AudioEndpointEnumerator.GetRenderEndpoints();
        string selectedId = _settings.CaptureDeviceId;
        CaptureDeviceCombo.ItemsSource = endpoints;
        CaptureDeviceCombo.DisplayMemberPath = nameof(AudioEndpointInfo.Name);
        CaptureDeviceCombo.SelectedValuePath = nameof(AudioEndpointInfo.Id);
        var selected = endpoints.FirstOrDefault(e => e.Id == selectedId)
            ?? endpoints.FirstOrDefault(e => e.LooksVirtual)
            ?? endpoints[0];
        CaptureDeviceCombo.SelectedItem = selected;
        _settings.CaptureDeviceId = selected.Id;
        _settings.CaptureDeviceName = selected.Name;

        LocalOutputCombo.ItemsSource = endpoints;
        LocalOutputCombo.DisplayMemberPath = nameof(AudioEndpointInfo.Name);
        LocalOutputCombo.SelectedValuePath = nameof(AudioEndpointInfo.Id);
        var local = endpoints.FirstOrDefault(e => e.Id == _settings.LocalOutputDeviceId)
            ?? endpoints.FirstOrDefault(e => !e.IsDefault && !e.LooksVirtual)
            ?? endpoints[0];
        LocalOutputCombo.SelectedItem = local;
        _settings.LocalOutputDeviceId = local.Id;
        _settings.LocalOutputDeviceName = local.Name;
        _settings.Save();
    }

    private void RefreshCaptureDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshCaptureDevices();
        _vm.SetMessage("采集设备列表已刷新。");
    }

    private void CaptureDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!BuildEdition.DualRoute || CaptureDeviceCombo.SelectedItem is not AudioEndpointInfo info)
            return;
        _settings.CaptureDeviceId = info.Id;
        _settings.CaptureDeviceName = info.Name;
        _settings.Save();
        if (_source is not null)
            _vm.SetMessage("采集设备已保存，下次开始共享时生效。");
    }

    private void LocalOutput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!BuildEdition.DualRoute || LocalOutputCombo.SelectedItem is not AudioEndpointInfo info)
            return;
        _settings.LocalOutputDeviceId = info.Id;
        _settings.LocalOutputDeviceName = info.Name;
        _settings.Save();
        InitializeDualVolumes();
    }

    private void InitializeDualVolumes()
    {
        if (!BuildEdition.DualRoute) return;
        _dualVolumeUpdating = true;
        DualRemoteGainSlider.Value = _settings.DualRouteRemoteGain;
        DualRemoteGainText.Text = $"{_settings.DualRouteRemoteGain:0}%";
        if (SystemAudioMute.TryGetVolumeScalar(_settings.LocalOutputDeviceId, out float volume))
        {
            DualLocalVolumeSlider.Value = volume * 100;
            DualLocalVolumeText.Text = $"{volume * 100:0}%";
        }
        _dualVolumeUpdating = false;
    }

    private void DualLocalVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dualVolumeUpdating) return;

        string? deviceId = _settings.LocalOutputDeviceId;
        bool wasMuted = SystemAudioMute.TryGetMuted(deviceId, out bool muted) && muted;
        bool keepMuted = _localMuteApplied || wasMuted;

        if (SystemAudioMute.TrySetVolumeScalar(
                deviceId, (float)(e.NewValue / 100.0), out string? error))
        {
            _settings.DualRouteLocalVolume = e.NewValue;
            _settings.Save();
            DualLocalVolumeText.Text = $"{e.NewValue:0}%";

            // Some drivers unmute on a volume change. Restore the mute state
            // without touching the "本机静音" button or _localMuteApplied.
            if (keepMuted && !SystemAudioMute.TrySetMuted(deviceId, true, out string? muteError))
                _vm.SetMessage("保持本机静音失败：" + (muteError ?? "未知错误"));
        }
        else
        {
            _vm.SetMessage("调整本机音量失败：" + (error ?? "未知错误"));
        }
    }

    private void DualRemoteGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dualVolumeUpdating) return;
        _settings.DualRouteRemoteGain = e.NewValue;
        _settings.Save();
        DualRemoteGainText.Text = $"{e.NewValue:0}%";
        if (_capture is WasapiLoopbackCapture capture)
            capture.ExtraGain = (float)(e.NewValue / 100.0);
    }

    private void UpdateSourceControls(SourceState state)
    {
        bool sharing = _source is not null && state == SourceState.Sharing;
        LocalMuteButton.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        LocalMuteButton.IsEnabled = sharing;
        LocalMuteButton.Content = _localMuteApplied ? "恢复本机声音" : "本机静音";
        if (!sharing && _localMuteApplied) RestoreLocalMute();
    }

    private void LocalMute_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null) return;
        try
        {
            if (_source.GetStatus().State != SourceState.Sharing) return;
        }
        catch { return; }

        if (_localMuteApplied)
        {
            RestoreLocalMute();
            _vm.SetMessage("已恢复本机声音；输出端音量没有改变。");
            return;
        }

        string? deviceId = BuildEdition.DualRoute ? _settings.LocalOutputDeviceId : null;

        if (BuildEdition.DualRoute &&
            VoicemeeterRemote.TryGetA1Mute(out bool voicemeeterWasMuted) &&
            VoicemeeterRemote.TrySetA1Mute(true))
        {
            _localMuteWasMuted = voicemeeterWasMuted;
            _localMuteDeviceId = null;
            _localMuteViaVoicemeeter = true;
            _localMuteApplied = true;
            LocalMuteButton.Content = "恢复本机声音";
            _vm.SetMessage("已静音 Voicemeeter A1；B1 采集和 Android 输出继续工作。");
            return;
        }

        if (!SystemAudioMute.TryGetMuted(deviceId, out bool wasMuted))
        {
            _vm.SetMessage("无法读取系统静音状态，本机静音失败。");
            return;
        }
        if (!SystemAudioMute.TrySetMuted(deviceId, true, out string? error))
        {
            // HDMI/monitor endpoints such as NVIDIA Mi Monitor can reject
            // SetMute with E_HANDLE even though their volume control works.
            // Fall back to volume 0 and remember the old level for restore.
            if (!SystemAudioMute.TryGetVolumeScalar(deviceId, out float volume))
            {
                _vm.SetMessage("本机静音失败：" + (error ?? "无法读取音量"));
                return;
            }
            if (!SystemAudioMute.TrySetVolumeScalar(deviceId, 0f, out string? volumeError))
            {
                _vm.SetMessage("本机静音失败：" + (volumeError ?? error ?? "无法调节音量"));
                return;
            }

            _localMuteWasMuted = wasMuted;
            _localMuteVolume = volume;
            _localMuteDeviceId = deviceId;
            _localMuteViaVoicemeeter = false;
            _localMuteViaVolume = true;
            _localMuteApplied = true;
            LocalMuteButton.Content = "恢复本机声音";
            _vm.SetMessage("该端点不支持静音，已用 0 音量静音；采集和输出端继续工作。");
            return;
        }

        _localMuteWasMuted = wasMuted;
        _localMuteDeviceId = deviceId;
        _localMuteViaVoicemeeter = false;
        _localMuteViaVolume = false;
        _localMuteApplied = true;
        LocalMuteButton.Content = "恢复本机声音";
        _vm.SetMessage("本机已静音；采集和输出端继续工作。");
    }

    private void RestoreLocalMute()
    {
        if (!_localMuteApplied) return;
        if (_localMuteViaVoicemeeter)
            VoicemeeterRemote.TrySetA1Mute(_localMuteWasMuted);
        else if (_localMuteViaVolume)
            SystemAudioMute.TrySetVolumeScalar(_localMuteDeviceId, _localMuteVolume, out _);
        else
            SystemAudioMute.TrySetMuted(_localMuteDeviceId, _localMuteWasMuted, out _);
        _localMuteApplied = false;
        _localMuteViaVoicemeeter = false;
        _localMuteViaVolume = false;
        _localMuteDeviceId = null;
        if (LocalMuteButton is not null)
            LocalMuteButton.Content = "本机静音";
    }

    // ---------------------------------------------------------------- settings

    private void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (QualityCombo.SelectedItem is not QualityOption option) return;
        _settings.PreferredQuality = option.Value;
        _settings.Save();
        if (_source is not null)
            _vm.SetMessage("传输音质已保存，下次开始共享时生效。");
    }

    private void SaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        string name = DeviceNameBox.Text.Trim();
        if (name.Length is < 1 or > 40)
        {
            MessageBox.Show(this, "设备名称需要在 1–40 个字符之间。", "同声", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _settings.DeviceName = name;
        _settings.Save();
        _vm.SetMessage("设备名称已保存，对下一次启动输出或共享生效。");
    }

    private void OpenFirewallSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsdefender") { UseShellExecute = true }); }
        catch (Exception ex) { _vm.SetMessage("无法打开设置：" + ex.Message); }
    }

    private void OpenNetworkSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true }); }
        catch (Exception ex) { _vm.SetMessage("无法打开设置：" + ex.Message); }
    }

    // ---------------------------------------------------------------- window / tray

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (MaximizeButton is null) return;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        // Windows shutdown/logoff must not wait for the close-choice dialog.
        _reallyClosing = true;
        try { ShutdownAll(); } catch { /* ignore */ }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {        if (_reallyClosing) return;

        bool trayAvailable = _tray is not null && _tray.IsAdded;
        CloseChoice choice;
        try
        {
            choice = CloseChoiceDialog.Ask(this, trayAvailable);
        }
        catch (Exception ex)
        {
            LogTray("close dialog failed: " + ex.Message);
            e.Cancel = true;
            return;
        }

        if (choice == CloseChoice.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (choice == CloseChoice.Exit)
        {
            _reallyClosing = true;
            ShutdownAll();
            Application.Current.Shutdown();
            return;
        }

        // Keep the window alive, then hide it (tray) or minimize it (no tray).
        e.Cancel = true;
        if (trayAvailable)
        {
            HideToTray();
        }
        else
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = true;
            _vm.SetMessage("窗口已最小化到任务栏，播放继续。");
        }
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            ShowTrayBalloon("同声仍在后台运行", _host is not null
                ? "输出中心正在托盘继续混音。如果任务栏右下角看不到图标，请点开 ^ 隐藏图标区。双击图标可以重新打开。"
                : "正在后台共享本机声音。如果任务栏右下角看不到图标，请点开 ^ 隐藏图标区。双击图标可以重新打开。");
        }
        UpdateTrayText();
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void CreateTrayIcon()
    {
        if (_tray is not null) return;
        try
        {
            System.Drawing.Icon? appIcon = null;
            try
            {
                var resource = Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/SameVoice.ico"));
                if (resource?.Stream is { } stream)
                {
                    using (stream)
                    using (var source = new System.Drawing.Icon(stream, 32, 32))
                        appIcon = (System.Drawing.Icon)source.Clone();
                }
            }
            catch (Exception ex)
            {
                LogTray("embedded icon failed: " + ex.Message);
            }

            if (appIcon is null)
            {
                try
                {
                    string? path = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(path))
                        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                }
                catch (Exception ex)
                {
                    LogTray("exe icon failed: " + ex.Message);
                }
            }

            appIcon ??= System.Drawing.SystemIcons.Application;

            var menu = new Forms.ContextMenuStrip();
            var open = new Forms.ToolStripMenuItem("打开同声");
            open.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
            var stop = new Forms.ToolStripMenuItem("停止所有播放");
            stop.Click += (_, _) => Dispatcher.BeginInvoke(StopAllFromTray);
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += (_, _) => Dispatcher.BeginInvoke(ExitFromTray);
            menu.Items.Add(open);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(stop);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            _trayMenu = menu;

            _tray = new NativeTrayIcon(appIcon, LogTray, ShowFromTray, ShowTrayMenu);
            LogTray("created, added=" + _tray.IsAdded);
            if (!_tray.IsAdded)
            {
                _vm.SetMessage(
                    $"系统托盘注册失败（Win32 {_tray.LastError}）。窗口关闭时会最小化到任务栏；详情见 tray.log。");
            }
        }
        catch (Exception ex)
        {
            LogTray("create failed: " + ex);
            try { _tray?.Dispose(); } catch { /* ignore */ }
            try { _trayMenu?.Dispose(); } catch { /* ignore */ }
            _tray = null;
            _trayMenu = null;
        }
    }

    private void ShowTrayMenu()
    {
        try
        {
            _trayMenu?.Show(Forms.Cursor.Position);
        }
        catch (Exception ex)
        {
            LogTray("menu failed: " + ex.Message);
        }
    }

    private static void LogTray(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SameVoice");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "tray.log"), line);
        }
        catch
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tray.log"), line);
            }
            catch
            {
                // Diagnostics must never prevent the tray from being created.
            }
        }
    }

    private void StopAllFromTray()
    {
        if (_host is not null) StopHost();
        if (_source is not null) _ = StopShareAsync();
        _vm.SetMessage("已停止所有播放。");
    }

    private void ExitFromTray()
    {
        _reallyClosing = true;
        ShutdownAll();
        Close();
        Application.Current.Shutdown();
    }

    private void ShutdownAll()
    {
        _timer.Stop();
        RestoreLocalMute();
        try { _host?.Stop(); } catch { /* ignore */ }
        try { _host?.Dispose(); } catch { /* ignore */ }
        _host = null;
        if (_source is not null)
        {
            try { _source.DisposeAsync().AsTask().Wait(600); } catch { /* ignore */ }
            _source = null;
        }
        _capture?.Dispose();
        _capture = null;
        _settings.Save();
        try
        {
            _tray?.Dispose();
        }
        catch { /* ignore */ }
        _tray = null;
        try { _trayMenu?.Dispose(); } catch { /* ignore */ }
        _trayMenu = null;
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        string text = _host is not null
            ? "同声 · 输出端运行中"
            : _source is not null
                ? "同声 · 正在共享本机声音"
                : "同声 SameVoice";
        _tray.SetTooltip(text.Length > 63 ? text[..63] : text);
    }

    private void ShowTrayBalloon(string title, string message)
    {
        try
        {
            _tray?.ShowBalloon(title, message);
        }
        catch { /* ignore */ }
    }

    private void ApplyWindowAppearance()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int corner = 2;
            _ = DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        }
        catch { /* only available on Windows 11 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
