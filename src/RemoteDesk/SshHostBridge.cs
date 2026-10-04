using System.Net;
using System.Net.Sockets;

namespace RemoteDesk;

internal static class SshHostBridge
{
    internal static async Task RunAsync(Stream relay, IDisposable relayOwner, RelaySshHostOptions options,
        string deviceId, CancellationToken cancellationToken)
    {
        if (!options.IsAvailable) throw new InvalidOperationException("本机未开启 SSH 中继。");
        using (relayOwner)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (!await SshGateProtocol.AuthenticateServerAsync(relay, options.DeviceKey, deviceId, deadline.Token).ConfigureAwait(false)) return;
            TcpClient? local = null;
            try
            {
                try
                {
                    // No address or port from the requesting peer is accepted.
                    local = await ConnectLoopbackAsync(options.Port, deadline.Token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    await relay.WriteAsync(new byte[] { 2 }, deadline.Token).ConfigureAwait(false);
                    return;
                }
                await relay.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                await RelayStreamBridge.RunAsync(relay, local.GetStream(), relayOwner, local, cancellationToken).ConfigureAwait(false);
            }
            finally { local?.Dispose(); }
        }
    }

    private static async Task<TcpClient> ConnectLoopbackAsync(int port, CancellationToken token)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            client.NoDelay = true;
            await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            return client;
        }
        catch (SocketException) when (Socket.OSSupportsIPv6)
        {
            client.Dispose();
            client = new TcpClient(AddressFamily.InterNetworkV6);
            try
            {
                client.NoDelay = true;
                await client.ConnectAsync(IPAddress.IPv6Loopback, port, token).ConfigureAwait(false);
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        catch { client.Dispose(); throw; }
    }
}
