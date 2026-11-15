using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SameVoice.App.DualRoute;

namespace SameVoice.App;

public partial class VirtualAudioGuideWindow : Window
{
    private readonly AppSettings _settings;

    public VirtualAudioGuideWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = DualRouteDetector.Detect(_settings);
        StatusTitle.Text = status.Title;
        StatusDetail.Text = status.Detail;
        switch (status.State)
        {
            case DualRouteState.Ready:
                StatusCard.Background = Brush("#12261E");
                StatusCard.BorderBrush = Brush("#2E5C4A");
                StatusTitle.Foreground = Brush("#8FE0BB");
                StatusDetail.Foreground = Brush("#9FC9B8");
                break;
            case DualRouteState.NoVirtualDevice:
                StatusCard.Background = Brush("#241414");
                StatusCard.BorderBrush = Brush("#6B3535");
                StatusTitle.Foreground = Brush("#FF9A9A");
                StatusDetail.Foreground = Brush("#D9AAAA");
                break;
            default:
                StatusCard.Background = Brush("#241B10");
                StatusCard.BorderBrush = Brush("#6B5230");
                StatusTitle.Foreground = Brush("#FFD18A");
                StatusDetail.Foreground = Brush("#C6B28D");
                break;
        }
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private void DownloadVoicemeeter_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://vb-audio.com/Voicemeeter/banana.htm");

    private void DownloadCable_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://vb-audio.com/Cable/");

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("ms-settings:sound");

    private void Recheck_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser/settings page is best-effort.
        }
    }
}
