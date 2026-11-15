using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SameVoice.App.Audio;
using SameVoice.Core.Audio;

namespace SameVoice.App;

/// <summary>
/// Explains why "mute this PC" also silences the outgoing stream and walks the
/// user through installing a virtual sound card (VB-CABLE / Voicemeeter /
/// Scream) so capture and local playback use two different endpoints.
/// </summary>
public partial class VirtualAudioGuideDialog : Window
{
    private static readonly Brush OkBackground = Frozen("#13221E");
    private static readonly Brush OkBorder = Frozen("#2F6B58");
    private static readonly Brush OkTitle = Frozen("#7DF3CD");
    private static readonly Brush OkDetail = Frozen("#9FB3AC");

    private static readonly Brush WarnBackground = Frozen("#241A12");
    private static readonly Brush WarnBorder = Frozen("#5C4326");
    private static readonly Brush WarnTitle = Frozen("#F0C083");
    private static readonly Brush WarnDetail = Frozen("#B9A78C");

    private readonly string? _captureId;
    private readonly string? _captureName;
    private readonly string? _localId;
    private readonly string? _localName;

    private IReadOnlyList<AudioEndpointRef> _endpoints = Array.Empty<AudioEndpointRef>();
    private AudioEndpointRef? _captureCandidate;
    private VirtualAudioDeviceKind _kind = VirtualAudioDeviceKind.VirtualCable;

    public string? AppliedCaptureId { get; private set; }
    public string? AppliedLocalOutputId { get; private set; }
    public bool ApplyRequested { get; private set; }

    public VirtualAudioGuideDialog(string? captureId, string? captureName, string? localId, string? localName)
    {
        InitializeComponent();
        _captureId = captureId;
        _captureName = captureName;
        _localId = localId;
        _localName = localName;
        CableOption.IsChecked = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Loaded += (_, _) => DetectAndRender();
    }

    // ---------------------------------------------------------------- detection

    private void DetectAndRender()
    {
        _endpoints = AudioEndpointEnumerator.GetRenderEndpoints()
            .Select(endpoint => new AudioEndpointRef(endpoint.Id, endpoint.Name))
            .ToList();
        _captureCandidate = VirtualAudioDeviceCatalog.PickCaptureEndpoint(_endpoints);
        var virtuals = VirtualAudioDeviceCatalog.VirtualEndpoints(_endpoints);

        if (_captureCandidate is not null)
        {
            var detected = VirtualAudioDeviceCatalog.Classify(_captureCandidate.Name);
            if (detected != VirtualAudioDeviceKind.OtherVirtual) SelectKind(detected);
        }

        bool sameEndpoint = VirtualAudioDeviceCatalog.SameEndpoint(_captureId, _localId);

        if (_captureCandidate is null)
        {
            SetStatus(false, "未检测到虚拟声卡",
                $"同声现在采集的是「{CaptureLabel()}」，本机静音操作的也是同一个端点。" +
                "Windows 在端点处静音时，采集流也会一起变成静音，所以输出端会同时没声。\n" +
                "装一个虚拟声卡，就能把「采集」和「本机播放」拆成两条互不影响的路。");
            DetectedText.Text = virtuals.Count == 0
                ? "播放设备里没有 Scream、VB-CABLE 或 Voicemeeter。"
                : "检测到虚拟端点：" + string.Join("、", virtuals.Select(v => v.Name));
            ApplyButton.IsEnabled = false;
            ApplyButton.Content = "设为采集设备";
        }
        else
        {
            bool inUse = string.Equals(_captureCandidate.Id, _captureId, StringComparison.OrdinalIgnoreCase);
            bool candidateIsLocal = string.Equals(_captureCandidate.Id, _localId, StringComparison.OrdinalIgnoreCase);
            var detail = new StringBuilder();
            if (inUse && sameEndpoint)
            {
                detail.Append($"采集已经走「{_captureCandidate.Name}」，但「本机输出」也指向同一个虚拟端点。");
                detail.Append("这样点「本机静音」会把采集流一起静音；把本机输出改成物理扬声器即可分开。");
            }
            else if (inUse)
            {
                detail.Append($"采集已经走「{_captureCandidate.Name}」，本机静音只会影响「{LocalLabel()}」，输出端不受影响。");
            }
            else
            {
                detail.Append($"把「采集设备」设为「{_captureCandidate.Name}」、「本机输出」设为物理扬声器，");
                detail.Append("本机音量与本机静音就只作用于本机，输出端继续播放。");
                if (sameEndpoint)
                {
                    detail.Append("\n当前「采集设备」和「本机输出」仍停在同一个端点");
                    detail.Append(candidateIsLocal ? "（都指向这个虚拟端点）" : "（都停在「默认播放设备」）");
                    detail.Append("，这种配置下静音一定会连带静音输出端。");
                }
            }
            SetStatus(true,
                inUse ? "已配置正确：" + _captureCandidate.Name : "已检测到虚拟声卡：" + _captureCandidate.Name,
                detail.ToString());
            DetectedText.Text = "检测到虚拟端点：" + string.Join("、", virtuals.Select(v => v.Name));
            ApplyButton.IsEnabled = !inUse || sameEndpoint;
            ApplyButton.Content = inUse
                ? (sameEndpoint ? "修正本机输出" : "已设为采集设备")
                : "设为采集设备";
        }

        UpdateSteps();
    }

    private void SelectKind(VirtualAudioDeviceKind kind)
    {
        switch (kind)
        {
            case VirtualAudioDeviceKind.Scream:
                ScreamOption.IsChecked = true;
                break;
            case VirtualAudioDeviceKind.Voicemeeter:
                VoicemeeterOption.IsChecked = true;
                break;
            default:
                CableOption.IsChecked = true;
                break;
        }
        _kind = kind;
    }

    private void SetStatus(bool ok, string title, string detail)
    {
        StatusBorder.Background = ok ? OkBackground : WarnBackground;
        StatusBorder.BorderBrush = ok ? OkBorder : WarnBorder;
        StatusTitle.Foreground = ok ? OkTitle : WarnTitle;
        StatusDetail.Foreground = ok ? OkDetail : WarnDetail;
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
    }

    private string CaptureLabel() =>
        string.IsNullOrWhiteSpace(_captureName) ? "默认播放设备" : _captureName!;

    private string LocalLabel() =>
        string.IsNullOrWhiteSpace(_localName) ? "默认播放设备" : _localName!;

    // ---------------------------------------------------------------- steps

    private void UpdateSteps()
    {
        var text = new StringBuilder();
        switch (_kind)
        {
            case VirtualAudioDeviceKind.Scream:
                text.AppendLine("Scream（虚拟声卡 + 局域网发送）");
                text.AppendLine("1. 点左下角「打开下载页面」，下载 Scream 并解压。");
                text.AppendLine("2. 右键解压目录里的安装脚本 → 以管理员身份运行，安装虚拟声卡驱动；Windows 11 可能需要在系统设置里临时开启测试签名。");
                text.AppendLine("3. Windows「设置 → 系统 → 声音 → 输出」把默认设备设为 Scream，格式建议 48 kHz / 16-bit / 立体声。");
                text.AppendLine("4. 回到同声：采集设备选 Scream，本机输出选你的物理扬声器。");
                text.AppendLine("5. 只让手机听时，也可以让手机使用同声的「Scream 虚拟声卡接收」模式，不经过同声协议。");
                break;
            case VirtualAudioDeviceKind.Voicemeeter:
                text.AppendLine("Voicemeeter（虚拟调音台）");
                text.AppendLine("1. 点左下角「打开下载页面」，下载 Voicemeeter Banana 或 Potato 并安装，然后重启电脑。");
                text.AppendLine("2. 打开 Voicemeeter：A1 选择物理扬声器/耳机，B1 选择 Scream 或 CABLE Input。");
                text.AppendLine("3. Windows 默认输出设为 Voicemeeter Input（也可以只把播放器的输出设过去）。");
                text.AppendLine("4. 同声：采集设备选 B1 对应的虚拟输出，本机输出选 A1 对应的物理扬声器。");
                text.AppendLine("5. 如果 B1 里没有可选设备，先按 VB-CABLE 或 Scream 的步骤装一个虚拟输出。");
                break;
            default:
                text.AppendLine("VB-CABLE（最轻量的虚拟声卡）");
                text.AppendLine("1. 点左下角「打开下载页面」，下载 VB-CABLE 压缩包并解压。");
                text.AppendLine("2. 右键 VBCABLE_Setup_x64.exe → 以管理员身份运行 → Install Driver，完成后重启或重新登录。");
                text.AppendLine("3. Windows「设置 → 系统 → 声音 → 输出」把默认设备设为 CABLE Input。");
                text.AppendLine("4. 想本机也能听：在「录音设备」里打开 CABLE Output 的属性 → 侦听 → 勾选「侦听此设备」，播放到你的扬声器。");
                text.AppendLine("5. 回到同声：采集设备选 CABLE Input，本机输出选物理扬声器，发送增益调回 100%。");
                break;
        }

        text.AppendLine();
        text.AppendLine("为什么需要这样做：Windows 的 WASAPI loopback 复制的是端点最终的输出流，");
        text.AppendLine("端点一旦静音，采集到的就全是 0。只有让「采集」和「本机播放」走两个不同的端点，");
        text.AppendLine("「本机静音」才只影响本机。");
        text.AppendLine();
        text.AppendLine("注意：");
        text.AppendLine("· 建议以管理员身份运行同声（右键图标 → 以管理员身份运行）：部分系统上非管理员进程无法调起浏览器，安装虚拟声卡驱动同样需要管理员权限。");
        text.AppendLine("·「采集设备」和「本机输出」不能是同一个端点，也尽量别都停在「默认播放设备」。");
        text.AppendLine("· 文件保存后，正在共享时需要停止再重新开始共享才会切换到新的采集设备。");
        text.AppendLine("· 装完虚拟声卡回到本窗口点「重新检测」，检测通过后可以一键设为采集设备。");

        StepsText.Text = text.ToString();
    }

    // ---------------------------------------------------------------- actions

    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<VirtualAudioDeviceKind>(tag, out var kind)) return;
        _kind = kind;
        if (StepsText is not null) UpdateSteps();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        string url = VirtualAudioDeviceCatalog.DownloadUrl(_kind);
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "同声",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开 Windows 设置：" + ex.Message, "同声",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => DetectAndRender();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_captureCandidate is null) return;
        var local = VirtualAudioDeviceCatalog.PickLocalOutputEndpoint(_endpoints, _captureCandidate.Id);
        AppliedCaptureId = _captureCandidate.Id;
        AppliedLocalOutputId = local?.Id;
        ApplyRequested = true;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
