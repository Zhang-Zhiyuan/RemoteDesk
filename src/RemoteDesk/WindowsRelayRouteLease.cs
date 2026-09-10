using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RemoteDesk;

internal interface IRelayRouteLease : IDisposable
{
    RelayNetworkPath Path { get; }
    bool Renew();
}

internal interface IRelayRouteBackend
{
    bool Available { get; }
    bool CanChange(IPAddress destination);
    IRelayRouteLease? TryCreate(RelayNetworkPath path, uint lifetimeSeconds);
}

/// <summary>Owns only newly created, expiring /32 routes. Never edits an existing route.</summary>
internal sealed class WindowsRelayRouteBackend : IRelayRouteBackend
{
    public bool Available => OperatingSystem.IsWindows() && WindowsProcessElevation.IsCurrentProcessElevated();

    public bool CanChange(IPAddress destination)
    {
        if (!Available || !RelayNetworkPathSelector.IsEligibleRemoteAddress(destination)) return false;
        try
        {
            IReadOnlyList<NativeRoute> routes = ReadRoutes();
            if (routes.Any(row => row.Matches(destination) && row.Destination.Length > 0) ||
                routes.Any(row => row.Destination.Length == 0 && !IsPhysicalInterface(row.InterfaceIndex))) return false;
            // This includes other programs and other RemoteDesk processes. A
            // handshake trial must not change the path of an existing session.
            return !IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(connection =>
                connection.RemoteEndPoint.Address.Equals(destination) &&
                connection.State is TcpState.Established or TcpState.SynSent or TcpState.SynReceived or TcpState.CloseWait);
        }
        catch { return false; }
    }

    public IRelayRouteLease? TryCreate(RelayNetworkPath path, uint lifetimeSeconds)
    {
        if (lifetimeSeconds is < 5 or > 120 || !CanChange(path.RemoteAddress) || !IsPathCurrent(path) ||
            !IPAddress.TryParse(path.Gateway, out IPAddress? gateway) ||
            gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        NativeRoute row = NewRow(path, gateway, lifetimeSeconds);
        if (CreateIpForwardEntry2(ref row) != 0) return null; // Including a concurrent creator: never take ownership.
        NativeRoute created = row;
        if (GetIpForwardEntry2(ref created) == 0 && OwnedBy(row, created, lifetimeSeconds))
            return new Lease(this, path, created, lifetimeSeconds);
        // Query once more on disposal, and remove only an unchanged marker.
        RemoveOwned(row, lifetimeSeconds);
        return null;
    }

    private sealed class Lease(WindowsRelayRouteBackend owner, RelayNetworkPath path,
        NativeRoute created, uint lifetimeSeconds) : IRelayRouteLease
    {
        private int _disposed;
        public RelayNetworkPath Path { get; } = path;
        public bool Renew()
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsPathCurrent(Path)) return false;
            try
            {
                NativeRoute current = created;
                if (GetIpForwardEntry2(ref current) != 0 || !OwnedBy(created, current, lifetimeSeconds)) return false;
                // Respect a VPN or administrator adding a more specific policy
                // after our lease was created. Dispose still touches only ours.
                if (owner.ReadRoutes().Any(row =>
                    row.Destination.Length == 0 && !IsPhysicalInterface(row.InterfaceIndex) ||
                    row.Destination.Length > 0 && row.Matches(Path.RemoteAddress) &&
                    !OwnedBy(created, row, lifetimeSeconds))) return false;
                current.ValidLifetime = current.PreferredLifetime = lifetimeSeconds;
                current.Immortal = 0;
                return SetIpForwardEntry2(ref current) == 0;
            }
            catch { return false; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) RemoveOwned(created, lifetimeSeconds);
        }
    }

    internal static bool IsPathCurrent(RelayNetworkPath path)
    {
        try
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            if (all.Any(nic => nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)) return false;
            NetworkInterface? match = all.FirstOrDefault(nic => nic.Id == path.InterfaceId &&
                nic.OperationalStatus == OperationalStatus.Up);
            if (match is null) return false;
            IPInterfaceProperties properties = match.GetIPProperties();
            return properties.GetIPv4Properties().Index == path.InterfaceIndex &&
                IsPhysicalInterface(checked((uint)path.InterfaceIndex)) &&
                properties.UnicastAddresses.Any(address => address.Address.Equals(path.LocalAddress)) &&
                properties.GatewayAddresses.Any(gateway => gateway.Address.ToString() == path.Gateway);
        }
        catch { return false; }
    }

    private static void RemoveOwned(NativeRoute created, uint lifetime)
    {
        try
        {
            NativeRoute current = created;
            if (GetIpForwardEntry2(ref current) == 0 && OwnedBy(created, current, lifetime))
                _ = DeleteIpForwardEntry2(ref current);
        }
        catch { /* The finite kernel lifetime also covers loss of privilege or a process crash. */ }
    }

    internal static bool IsPhysicalInterface(uint index)
    {
        try
        {
            var row = new NativeInterface { Index = index };
            return GetIfEntry2(ref row) == 0 && row.IsPhysical;
        }
        catch { return false; }
    }

    // MIB_IF_ROW2 includes 257-WCHAR alias/description arrays, two 32-byte
    // hardware addresses and 20 UInt64 counters. Explicit offsets retain the
    // full native buffer while exposing only the read-only policy fields.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    internal struct NativeInterface
    {
        [FieldOffset(8)] internal uint Index;
        [FieldOffset(12)] internal Guid Id;
        [FieldOffset(1128)] internal uint Type;
        [FieldOffset(1132)] internal uint Tunnel;
        [FieldOffset(1152)] internal byte Flags;
        [FieldOffset(1156)] internal uint OperationalStatus;
        internal bool IsPhysical => Type is 6 or 71 && Tunnel == 0 && OperationalStatus == 1 &&
            (Flags & 1) != 0 && (Flags & (2 | 8 | 16 | 32)) == 0;
    }

    internal static bool OwnedBy(NativeRoute created, NativeRoute current, uint lifetime) =>
        current.Destination.Length == 32 && current.Destination.Address.Family == 2 &&
        current.Destination.Address.IPv4 == created.Destination.Address.IPv4 &&
        current.InterfaceIndex == created.InterfaceIndex &&
        (created.InterfaceLuid == 0 || current.InterfaceLuid == created.InterfaceLuid) &&
        current.NextHop.Family == 2 && current.NextHop.IPv4 == created.NextHop.IPv4 &&
        current.Metric == created.Metric && current.Protocol == 3 && current.Origin == 0 &&
        current.Immortal == 0 && current.Publish == 0 && current.Loopback == 0 && current.Autoconfigure == 0 &&
        current.ValidLifetime is > 0 && current.ValidLifetime <= lifetime &&
        current.PreferredLifetime <= lifetime;

    internal static NativeRoute NewRow(RelayNetworkPath path, IPAddress gateway, uint lifetime)
    {
        InitializeIpForwardEntry(out NativeRoute row);
        row.InterfaceIndex = checked((uint)path.InterfaceIndex);
        row.Destination = new AddressPrefix { Address = InetAddress.From(path.RemoteAddress), Length = 32 };
        row.NextHop = InetAddress.From(gateway);
        row.SitePrefixLength = 32;
        row.ValidLifetime = row.PreferredLifetime = lifetime;
        // A random marker distinguishes this lease from an externally replaced
        // route; /32 specificity, not this metric, selects it over default routes.
        row.Metric = checked((uint)RandomNumberGenerator.GetInt32(20_000, 60_000));
        row.Protocol = 3; // MIB_IPPROTO_NETMGMT
        row.Loopback = row.Autoconfigure = row.Publish = row.Immortal = 0;
        return row;
    }

    internal IReadOnlyList<NativeRoute> ReadRoutes()
    {
        uint error = GetIpForwardTable2(2, out nint table);
        if (error != 0) throw new NetworkInformationException(checked((int)error));
        try
        {
            int count = Marshal.ReadInt32(table);
            if (count is < 0 or > 100_000) throw new InvalidOperationException("Invalid route table size.");
            var rows = new List<NativeRoute>(count);
            int size = Marshal.SizeOf<NativeRoute>();
            for (int index = 0; index < count; index++)
                rows.Add(Marshal.PtrToStructure<NativeRoute>(table + 8 + index * size));
            return rows;
        }
        finally { FreeMibTable(table); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct InetAddress
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(4)] internal uint IPv4;
        internal static InetAddress From(IPAddress value) => new() { Family = 2, IPv4 = BitConverter.ToUInt32(value.GetAddressBytes()) };
        internal IPAddress Address => new(BitConverter.GetBytes(IPv4));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AddressPrefix
    {
        internal InetAddress Address;
        internal byte Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRoute
    {
        internal ulong InterfaceLuid;
        internal uint InterfaceIndex;
        internal AddressPrefix Destination;
        internal InetAddress NextHop;
        internal byte SitePrefixLength;
        internal uint ValidLifetime, PreferredLifetime, Metric, Protocol;
        internal byte Loopback, Autoconfigure, Publish, Immortal;
        internal uint Age, Origin;
        internal bool Matches(IPAddress address)
        {
            if (Destination.Address.Family != 2 || Destination.Length > 32 || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            uint mask = Destination.Length == 0 ? 0 : uint.MaxValue << (32 - Destination.Length);
            uint expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Destination.Address.Address.GetAddressBytes());
            uint actual = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
            return (expected & mask) == (actual & mask);
        }
    }

    [DllImport("iphlpapi.dll")] private static extern void InitializeIpForwardEntry(out NativeRoute row);
    [DllImport("iphlpapi.dll")] private static extern uint CreateIpForwardEntry2(ref NativeRoute row);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardEntry2(ref NativeRoute row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpForwardEntry2(ref NativeRoute row);
    [DllImport("iphlpapi.dll")] private static extern uint DeleteIpForwardEntry2(ref NativeRoute row);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out nint table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(nint memory);
    [DllImport("iphlpapi.dll")] private static extern uint GetIfEntry2(ref NativeInterface row);
}
