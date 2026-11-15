using System.IO;
using SameVoice.App.Audio;

namespace SameVoice.App.DualRoute;

public enum DualRouteState
{
    NoVirtualDevice,
    VirtualDeviceFound,
    LocalOutputMissing,
    SameDevice,
    LocalOutputVirtual,
    Ready
}

public sealed record DualRouteStatus(
    DualRouteState State,
    string Title,
    string Detail,
    string? CaptureName,
    string? LocalName,
    bool HasVirtualDevice,
    bool VoicemeeterInstalled,
    string VirtualKind)
{
    public bool IsReady => State == DualRouteState.Ready;
}

/// <summary>
/// Read-only health check for the dual-route setup. It deliberately does not
/// block sharing when the setup is incomplete; the UI warns and offers a guide,
/// while same-device mute remains available.
/// </summary>
public static class DualRouteDetector
{
    public static DualRouteStatus Detect(AppSettings settings)
    {
        var endpoints = AudioEndpointEnumerator.GetRenderEndpoints();
        var capture = endpoints.FirstOrDefault(e => e.Id == settings.CaptureDeviceId);
        var local = endpoints.FirstOrDefault(e => e.Id == settings.LocalOutputDeviceId);

        var virtualEndpoints = endpoints
            .Where(e => !e.IsDefault && e.LooksVirtual)
            .ToList();
        bool hasVirtual = virtualEndpoints.Count > 0;
        string kind = VirtualKind(virtualEndpoints);
        bool voicemeeterInstalled = VoicemeeterInstalled();

        if (!hasVirtual)
        {
            string detail = voicemeeterInstalled
                ? "检测到 Voicemeeter 安装，但没有可用的虚拟音频端点。请重启电脑，或在 Windows 声音设置中检查驱动是否启用。"
                : "未检测到虚拟声卡。当前采集和本机输出可能使用同一设备，本机静音会连带切断 Android 输出。";
            return new(DualRouteState.NoVirtualDevice, "双路由尚未启用", detail,
                capture?.Name, local?.Name, false, voicemeeterInstalled, kind);
        }

        if (capture is null || capture.IsDefault || !capture.LooksVirtual)
        {
            return new(DualRouteState.VirtualDeviceFound, "检测到虚拟声卡，但采集没有选它",
                $"请把采集设备设为虚拟声卡的输出端点（如 CABLE Output 或 Voicemeeter Out B1）。",
                capture?.Name, local?.Name, true, voicemeeterInstalled, kind);
        }

        if (local is null || local.IsDefault)
        {
            return new(DualRouteState.LocalOutputMissing, "请选择本机物理输出",
                "采集已经指向虚拟设备，但本机输出还是默认设备。请选择 A1 对应的扬声器或耳机。",
                capture.Name, local?.Name, true, voicemeeterInstalled, kind);
        }

        if (string.Equals(capture.Id, local.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new(DualRouteState.SameDevice, "采集和本机输出是同一设备",
                "双路由没有真正生效。本机静音可能同时切断 Android 输出；请让采集走 B1、本机输出走 A1。",
                capture.Name, local.Name, true, voicemeeterInstalled, kind);
        }

        if (local.LooksVirtual)
        {
            return new(DualRouteState.LocalOutputVirtual, "本机输出还是虚拟设备",
                "本机输出应选择 A1 对应的物理扬声器或耳机，否则本机可能听不到声音。",
                capture.Name, local.Name, true, voicemeeterInstalled, kind);
        }

        return new(DualRouteState.Ready, "双路由已就绪",
            $"采集：{capture.Name}\n本机：{local.Name}",
            capture.Name, local.Name, true, voicemeeterInstalled, kind);
    }

    private static string VirtualKind(IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        if (endpoints.Any(e => e.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)))
            return "Voicemeeter";
        if (endpoints.Any(e => e.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)))
            return "VB-CABLE";
        if (endpoints.Any(e => e.Name.Contains("Scream", StringComparison.OrdinalIgnoreCase)))
            return "Scream";
        return "虚拟声卡";
    }

    private static bool VoicemeeterInstalled()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(programFilesX86, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(programFiles, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(programFilesX86, "VB", "Voicemeeter Banana", "VoicemeeterRemote64.dll")
        }.Any(File.Exists);
    }
}
