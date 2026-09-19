using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesk;

internal static class SelfConnectionGuard
{
    internal const string Message = "这是本机，不能连接自己。请选择另一台设备。";

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static bool IsLocalAddress(IPAddress remote, IEnumerable<IPAddress> localAddresses,
        IPAddress? transportLocalAddress = null)
    {
        remote = Normalize(remote);
        return IPAddress.IsLoopback(remote) || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any) ||
            (transportLocalAddress is not null && remote.Equals(Normalize(transportLocalAddress))) ||
            localAddresses.Any(local => remote.Equals(Normalize(local)));
    }

    internal static IReadOnlyList<IPAddress> ReadLocalAddresses()
    {
        // Include virtual/tunnel adapters and IPv6. Discovery's IPv4-only list
        // is a UI convenience, not a safe connection boundary. Enumeration
        // failures must not silently turn off the protection.
        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => Normalize(address.Address)).Distinct().ToArray();
    }

    internal static void RejectLocalPeer(Socket socket)
    {
        if (socket.RemoteEndPoint is not IPEndPoint peer || socket.LocalEndPoint is not IPEndPoint local)
            throw new InvalidOperationException("无法确认目标设备地址，未发送设备密钥，请重新连接。");
        if (IsLocalAddress(peer.Address, ReadLocalAddresses(), local.Address))
            throw new RemoteSessionRejectedException(Message);
    }

    internal static void RejectLocalDevice(string? deviceId)
    {
        if (RemoteDeviceIdentity.Same(deviceId, RemoteDeviceIdentity.LocalId))
            throw new RemoteSessionRejectedException(Message);
    }

    internal static async Task ValidateDirectHostAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<IPAddress> local = await Task.Run(ReadLocalAddresses, cancellationToken).ConfigureAwait(false);
        if (resolved.Any(address => IsLocalAddress(address, local)))
            throw new RemoteSessionRejectedException(Message);
    }
}
