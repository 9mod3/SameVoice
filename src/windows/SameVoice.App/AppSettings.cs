using System.IO;
using System.Text.Json;
using SameVoice.Core;

namespace SameVoice.App;

public sealed class AppSettings
{
    public string DeviceName { get; set; } = DefaultDeviceName();
    public HostMode Mode { get; set; } = HostMode.Balanced;
    public double MasterVolume { get; set; } = 80;
    public string CaptureDeviceId { get; set; } = "";
    public string CaptureDeviceName { get; set; } = "默认播放设备";
    public string LocalOutputDeviceId { get; set; } = "";
    public string LocalOutputDeviceName { get; set; } = "默认播放设备";
    public double DualRouteLocalVolume { get; set; } = 80;
    public double DualRouteRemoteGain { get; set; } = 100;

    public static string DefaultDeviceName() =>
        string.IsNullOrWhiteSpace(Environment.MachineName) ? "我的电脑" : Environment.MachineName;

    private static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SameVoice");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.DeviceName))
                        settings.DeviceName = DefaultDeviceName();
                    settings.MasterVolume = Math.Clamp(settings.MasterVolume, 0, 100);
                    settings.DualRouteLocalVolume = Math.Clamp(settings.DualRouteLocalVolume, 0, 100);
                    settings.DualRouteRemoteGain = Math.Clamp(settings.DualRouteRemoteGain, 0, 200);
                    if (string.IsNullOrWhiteSpace(settings.CaptureDeviceName))
                        settings.CaptureDeviceName = "默认播放设备";
                    if (string.IsNullOrWhiteSpace(settings.LocalOutputDeviceName))
                        settings.LocalOutputDeviceName = "默认播放设备";
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt settings should never prevent the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Read-only profiles or full disks should not crash audio playback.
        }
    }
}
