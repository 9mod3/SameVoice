using System.Net;
using System.Text.Json;

namespace SameVoice.Core.Protocol;

public sealed record HostOffer(
    string RoomId,
    string Name,
    string Platform,
    int TcpPort,
    HostMode Mode,
    int Sources,
    bool PinRequired,
    IPAddress Address)
{
    public string AddressText => Address.ToString();
    public string EndpointText => $"{Address}:{TcpPort}";
}

public sealed record WelcomeInfo(
    uint SessionId,
    ulong Token,
    int AudioPort,
    string HostName,
    HostMode Mode,
    int SampleRate);

public static class DiscoveryProtocol
{
    public static byte[] CreateQuery() =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "query",
            ["proto"] = ProtocolConstants.ProtocolVersion
        });

    public static bool IsQuery(ReadOnlySpan<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var t)
                   && t.GetString() == "query"
                   && (!root.TryGetProperty("proto", out var p) || p.GetInt32() == ProtocolConstants.ProtocolVersion);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] CreateOffer(
        string roomId,
        string name,
        string platform,
        int tcpPort,
        HostMode mode,
        int sources,
        bool pinRequired) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "offer",
            ["proto"] = ProtocolConstants.ProtocolVersion,
            ["roomId"] = roomId,
            ["name"] = name,
            ["platform"] = platform,
            ["tcpPort"] = tcpPort,
            ["mode"] = mode.ToWire(),
            ["sources"] = sources,
            ["pinRequired"] = pinRequired
        });

    public static HostOffer? TryParseOffer(ReadOnlySpan<byte> data, IPAddress remoteAddress)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "offer") return null;
            if (!root.TryGetProperty("proto", out var proto) || proto.GetInt32() != 1) return null;

            string roomId = root.TryGetProperty("roomId", out var r) ? r.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "SameVoice" : "SameVoice";
            string platform = root.TryGetProperty("platform", out var p) ? p.GetString() ?? "" : "";
            int tcpPort = root.TryGetProperty("tcpPort", out var t) ? t.GetInt32() : ProtocolConstants.DefaultControlPort;
            HostMode mode = root.TryGetProperty("mode", out var m) ? HostModeExtensions.FromWire(m.GetString()) : HostMode.Balanced;
            int sources = root.TryGetProperty("sources", out var s) ? s.GetInt32() : 0;
            bool pin = root.TryGetProperty("pinRequired", out var pinEl) && pinEl.ValueKind == JsonValueKind.True;

            if (tcpPort is <= 0 or > 65535) return null;
            return new HostOffer(roomId, name, platform, tcpPort, mode, sources, pin, remoteAddress);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class ControlProtocol
{
    public static byte[] Hello(string name, string platform, string pin = "") =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["proto"] = ProtocolConstants.ProtocolVersion,
            ["name"] = name,
            ["platform"] = platform,
            ["pin"] = pin
        });

    public static byte[] Welcome(
        uint sessionId,
        ulong token,
        int audioPort,
        string hostName,
        HostMode mode) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "welcome",
            ["proto"] = ProtocolConstants.ProtocolVersion,
            ["session"] = sessionId,
            ["token"] = token.ToString("x16"),
            ["audioPort"] = audioPort,
            ["name"] = hostName,
            ["mode"] = mode.ToWire(),
            ["sampleRate"] = ProtocolConstants.SampleRate
        });

    public static byte[] Start() => Simple("start");
    public static byte[] Heartbeat() => Simple("heartbeat");
    public static byte[] Stop() => Simple("stop");
    public static byte[] Started() => Simple("started");
    public static byte[] Bye() => Simple("bye");

    public static byte[] Mode(HostMode mode) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "mode",
            ["mode"] = mode.ToWire()
        });

    public static byte[] Error(string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = message
        });

    private static byte[] Simple(string type) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["type"] = type
        });

    public static string? ReadType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static WelcomeInfo? ParseWelcome(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "welcome") return null;
            if (!root.TryGetProperty("proto", out var proto) || proto.GetInt32() != 1) return null;
            if (!root.TryGetProperty("session", out var s)) return null;
            if (!root.TryGetProperty("token", out var tokenEl)) return null;
            if (!root.TryGetProperty("audioPort", out var portEl)) return null;
            if (!ulong.TryParse(tokenEl.GetString(), System.Globalization.NumberStyles.HexNumber, null, out var token))
                return null;
            int audioPort = portEl.GetInt32();
            if (audioPort is <= 0 or > 65535) return null;
            string hostName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "SameVoice" : "SameVoice";
            HostMode mode = root.TryGetProperty("mode", out var m) ? HostModeExtensions.FromWire(m.GetString()) : HostMode.Balanced;
            int rate = root.TryGetProperty("sampleRate", out var sr) ? sr.GetInt32() : ProtocolConstants.SampleRate;
            if (rate != ProtocolConstants.SampleRate) return null;
            return new WelcomeInfo(s.GetUInt32(), token, audioPort, hostName, mode, rate);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public static HostMode? ParseMode(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "mode") return null;
            return root.TryGetProperty("mode", out var mode)
                ? HostModeExtensions.FromWire(mode.GetString())
                : HostMode.Balanced;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ParseErrorMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.TryGetProperty("type", out var t) && t.GetString() == "error"
                ? (root.TryGetProperty("message", out var m) ? m.GetString() : "远端返回错误")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ParseName(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
