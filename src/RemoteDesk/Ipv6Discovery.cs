using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesk;

// Transient, link-scoped multicast. Never enumerate an IPv6 subnet or cross a router.
internal static class Ipv6Discovery
{
    internal const string Group = "ff12::5244:4b31";

    internal static IEnumerable<long> InterfaceIndexes()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv6))
                .Select(n => (long)(n.GetIPProperties().GetIPv6Properties()?.Index ?? 0))
                .Where(index => index > 0).Distinct().Take(32).ToArray();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or NotSupportedException) { return []; }
    }

    internal static UdpClient? OpenResponder(int port)
    {
        if (!Socket.OSSupportsIPv6) return null;
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(AddressFamily.InterNetworkV6);
            socket.Client.DualMode = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            foreach (long index in InterfaceIndexes())
                try { socket.JoinMulticastGroup((int)index, IPAddress.Parse(Group)); }
                catch (SocketException) { /* An absent/VPN route must not disable unicast discovery. */ }
            return socket;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        { socket?.Dispose(); return null; }
    }

    internal static IEnumerable<IPAddress> MulticastTargets() => InterfaceIndexes()
        .Select(index => new IPAddress(IPAddress.Parse(Group).GetAddressBytes(), index));
}
