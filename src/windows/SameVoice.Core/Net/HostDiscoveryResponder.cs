using System.Net;
using System.Net.Sockets;
using SameVoice.Core.Protocol;

namespace SameVoice.Core.Net;

public sealed class HostDiscoveryResponder : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Func<byte[]> _offerFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public HostDiscoveryResponder(int port, Func<byte[]> offerFactory)
    {
        _offerFactory = offerFactory;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udp.EnableBroadcast = true;
        _loop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (!DiscoveryProtocol.IsQuery(result.Buffer)) continue;
                byte[] offer = _offerFactory();
                await _udp.SendAsync(offer, result.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested) break;
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { /* ignore */ }
        try { _loop.Wait(500); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
