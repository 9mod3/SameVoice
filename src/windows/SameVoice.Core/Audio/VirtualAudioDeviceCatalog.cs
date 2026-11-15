namespace SameVoice.Core.Audio;

/// <summary>Third-party virtual audio devices SameVoice knows how to route through.</summary>
public enum VirtualAudioDeviceKind
{
    None,
    VirtualCable,
    Scream,
    Voicemeeter,
    OtherVirtual
}

/// <summary>A render endpoint as presented to the user (Id, display name).</summary>
public sealed record AudioEndpointRef(string Id, string Name)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(Id);
}

/// <summary>
/// Helps the dual-route edition pick a virtual endpoint (B1) for capture and a
/// physical endpoint (A1) for local playback. Capturing and muting the same
/// endpoint is what makes "mute this PC" also mute the outgoing stream, so the
/// UI uses this catalog to detect that situation and guide the user through
/// installing a virtual sound card.
/// </summary>
public static class VirtualAudioDeviceCatalog
{
    public const string VirtualCableDownloadUrl = "https://vb-audio.com/Cable/";
    public const string VoicemeeterDownloadUrl = "https://vb-audio.com/Voicemeeter/";
    public const string ScreamDownloadUrl = "https://github.com/duncanthrax/scream/releases";

    /// <summary>Classifies a render endpoint display name.</summary>
    public static VirtualAudioDeviceKind Classify(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName)) return VirtualAudioDeviceKind.None;

        string name = endpointName!;
        if (Contains(name, "voicemeeter") || Contains(name, "voice meeter"))
            return VirtualAudioDeviceKind.Voicemeeter;
        if (Contains(name, "cable"))
            return VirtualAudioDeviceKind.VirtualCable;
        if (Contains(name, "scream"))
            return VirtualAudioDeviceKind.Scream;
        if (Contains(name, "virtual") || Contains(name, "vb-audio") || Contains(name, "streaming"))
            return VirtualAudioDeviceKind.OtherVirtual;
        return VirtualAudioDeviceKind.None;
    }

    public static bool IsVirtual(string? endpointName) =>
        Classify(endpointName) != VirtualAudioDeviceKind.None;

    /// <summary>Display name for a device kind.</summary>
    public static string DisplayName(VirtualAudioDeviceKind kind) => kind switch
    {
        VirtualAudioDeviceKind.VirtualCable => "VB-CABLE",
        VirtualAudioDeviceKind.Scream => "Scream",
        VirtualAudioDeviceKind.Voicemeeter => "Voicemeeter",
        VirtualAudioDeviceKind.OtherVirtual => "其他虚拟声卡",
        _ => "虚拟声卡"
    };

    /// <summary>Download page for a device kind, or an empty string when there is none.</summary>
    public static string DownloadUrl(VirtualAudioDeviceKind kind) => kind switch
    {
        VirtualAudioDeviceKind.VirtualCable => VirtualCableDownloadUrl,
        VirtualAudioDeviceKind.Scream => ScreamDownloadUrl,
        VirtualAudioDeviceKind.Voicemeeter => VoicemeeterDownloadUrl,
        _ => ""
    };

    /// <summary>All virtual render endpoints in the list, in the original order.</summary>
    public static IReadOnlyList<AudioEndpointRef> VirtualEndpoints(IEnumerable<AudioEndpointRef> endpoints)
    {
        var result = new List<AudioEndpointRef>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null || endpoint.IsDefault) continue;
            if (IsVirtual(endpoint.Name)) result.Add(endpoint);
        }
        return result;
    }

    /// <summary>
    /// Best capture endpoint: a B1 target (Scream / VB-CABLE) when available,
    /// otherwise any other virtual endpoint (Voicemeeter Input, generic virtual
    /// cable). Returns null when the machine has no virtual sound card at all.
    /// </summary>
    public static AudioEndpointRef? PickCaptureEndpoint(IEnumerable<AudioEndpointRef> endpoints)
    {
        AudioEndpointRef? fallback = null;
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null || endpoint.IsDefault) continue;
            switch (Classify(endpoint.Name))
            {
                case VirtualAudioDeviceKind.VirtualCable:
                case VirtualAudioDeviceKind.Scream:
                    return endpoint;
                case VirtualAudioDeviceKind.Voicemeeter:
                case VirtualAudioDeviceKind.OtherVirtual:
                    fallback ??= endpoint;
                    break;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Best local output endpoint: the first physical render endpoint that is
    /// not the capture device. Returns null when every endpoint is virtual.
    /// </summary>
    public static AudioEndpointRef? PickLocalOutputEndpoint(
        IEnumerable<AudioEndpointRef> endpoints, string? captureDeviceId)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null || endpoint.IsDefault) continue;
            if (IsVirtual(endpoint.Name)) continue;
            if (!string.IsNullOrWhiteSpace(captureDeviceId) &&
                string.Equals(endpoint.Id, captureDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            return endpoint;
        }
        return null;
    }

    /// <summary>
    /// True when two selections resolve to the same endpoint. An empty id means
    /// "the current default device", which is also what a null id means.
    /// </summary>
    public static bool SameEndpoint(string? first, string? second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? "" : id!.Trim();

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
