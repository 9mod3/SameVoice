using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SameVoice.Core.Protocol;

namespace SameVoice.Core.Net;

public static class NetworkUtil
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        result.Add(ua.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Keep diagnostics UI usable even if enumeration fails.
        }
        return result.Distinct().ToList();
    }

    public static IReadOnlyList<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null)
                        continue;
                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    if (ip.Length != 4 || mask.Length != 4) continue;
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    var addr = new IPAddress(broadcast);
                    if (!result.Contains(addr)) result.Add(addr);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the limited broadcast address below.
        }

        var limited = IPAddress.Broadcast;
        if (!result.Contains(limited)) result.Add(limited);
        return result;
    }

    public static string FirstLocalAddressText()
    {
        var addresses = GetLocalIPv4Addresses();
        return addresses.Count == 0 ? "未连接局域网" : string.Join(" · ", addresses.Select(a => a.ToString()));
    }

    public static string PlatformName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "macos";
        return "unknown";
    }
}

/// <summary>Broadcasts a discovery query and collects unicast offers until the timeout.</summary>
public static class DiscoveryClient
{
    public static async Task<IReadOnlyList<HostOffer>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var offers = new Dictionary<string, HostOffer>(StringComparer.Ordinal);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        byte[] query = Protocol.DiscoveryProtocol.CreateQuery();
        foreach (var address in NetworkUtil.GetBroadcastAddresses())
        {
            try
            {
                await udp.SendAsync(query, new IPEndPoint(address, ProtocolConstants.DiscoveryPort), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // One broken virtual adapter should not stop discovery on the others.
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                var receive = udp.ReceiveAsync(timeoutCts.Token);
                UdpReceiveResult result;
                try
                {
                    result = await receive.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                var offer = Protocol.DiscoveryProtocol.TryParseOffer(result.Buffer, result.RemoteEndPoint.Address);
                if (offer is null) continue;
                string key = $"{offer.RoomId}|{offer.Address}|{offer.TcpPort}";
                offers[key] = offer;
            }
        }
        finally
        {
            try { udp.Close(); } catch { /* ignore */ }
        }

        return offers.Values
            .OrderBy(o => o.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
