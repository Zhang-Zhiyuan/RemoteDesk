using System.Net;
using System.Runtime.InteropServices;
using Xunit;
using static RemoteDesk.WindowsRelayRouteBackend;

namespace RemoteDesk.Tests;

public sealed class WindowsRelayRouteLeaseTests
{
    [Fact]
    public void NativeLayoutMatchesWindowsAbi()
    {
        Assert.Equal(28, Marshal.SizeOf<InetAddress>());
        Assert.Equal(32, Marshal.SizeOf<AddressPrefix>());
        Assert.Equal(104, Marshal.SizeOf<NativeRoute>());
        Assert.Equal(12, Marshal.OffsetOf<NativeRoute>("Destination").ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<NativeRoute>("NextHop").ToInt32());
        Assert.Equal(76, Marshal.OffsetOf<NativeRoute>("ValidLifetime").ToInt32());
        Assert.Equal(84, Marshal.OffsetOf<NativeRoute>("Metric").ToInt32());
        Assert.Equal(92, Marshal.OffsetOf<NativeRoute>("Loopback").ToInt32());
        Assert.Equal(1352, Marshal.SizeOf<NativeInterface>());
    }

    [Theory]
    [InlineData(6u, 1, 1u, true)]
    [InlineData(71u, 5, 1u, true)]
    [InlineData(6u, 0, 1u, false)]
    [InlineData(6u, 3, 1u, false)]
    [InlineData(131u, 1, 1u, false)]
    [InlineData(6u, 1, 2u, false)]
    public void VirtualEthernetAndDisconnectedAdaptersCannotBecomeUplinks(uint type, byte flags, uint status, bool expected) =>
        Assert.Equal(expected, new NativeInterface { Type = type, Flags = flags, OperationalStatus = status }.IsPhysical);

    [Fact]
    public void RouteTableCanBeReadWithoutMutations()
    {
        var routes = new WindowsRelayRouteBackend().ReadRoutes();
        Assert.NotEmpty(routes);
        Assert.All(routes, row =>
        {
            Assert.Equal(2, row.Destination.Address.Family);
            Assert.InRange(row.Destination.Length, (byte)0, (byte)32);
            Assert.True(row.InterfaceIndex > 0);
        });
        Assert.Contains(routes, row => row.Destination.Address.Address.Equals(IPAddress.Loopback) && row.Destination.Length == 32);
    }

    [Theory]
    [InlineData("0.0.0.0", 0, "8.138.5.232", true)]
    [InlineData("8.0.0.0", 8, "8.138.5.232", true)]
    [InlineData("8.138.0.0", 16, "8.138.5.232", true)]
    [InlineData("8.138.5.0", 24, "8.138.6.232", false)]
    [InlineData("8.138.5.232", 32, "8.138.5.232", true)]
    [InlineData("8.138.5.232", 32, "8.138.5.231", false)]
    public void PrefixMatchingUsesNetworkByteOrder(string network, byte length, string remote, bool expected)
    {
        var row = new NativeRoute { Destination = new AddressPrefix { Address = InetAddress.From(IPAddress.Parse(network)), Length = length } };
        Assert.Equal(expected, row.Matches(IPAddress.Parse(remote)));
    }

    private static NativeRoute Row() => NewRow(new RelayNetworkPath("test", "test", 3,
        IPAddress.Parse("10.0.0.2"), IPAddress.Parse("8.138.5.232"), "10.0.0.1"), IPAddress.Parse("10.0.0.1"), 90);

    [Fact]
    public void NewRowsHaveFiniteLifetimeAndNoInheritedNativeFlags()
    {
        NativeRoute row = Row();
        Assert.Equal(90u, row.ValidLifetime);
        Assert.Equal(90u, row.PreferredLifetime);
        Assert.Equal(0, row.Immortal | row.Autoconfigure | row.Publish | row.Loopback);
        Assert.Equal(3u, row.Protocol);
        Assert.True(OwnedBy(row, row, 90));
    }

    [Theory]
    [InlineData("metric")]
    [InlineData("interface")]
    [InlineData("gateway")]
    [InlineData("infinite")]
    [InlineData("expired")]
    [InlineData("immortal")]
    [InlineData("protocol")]
    [InlineData("prefix")]
    public void ExternallyReplacedRoutesAreNotOwned(string change)
    {
        NativeRoute created = Row(), current = created;
        switch (change)
        {
            case "metric": current.Metric++; break;
            case "interface": current.InterfaceIndex++; break;
            case "gateway": current.NextHop = InetAddress.From(IPAddress.Parse("10.0.0.254")); break;
            case "infinite": current.ValidLifetime = uint.MaxValue; break;
            case "expired": current.ValidLifetime = 0; break;
            case "immortal": current.Immortal = 1; break;
            case "protocol": current.Protocol = 4; break;
            case "prefix": current.Destination.Length = 24; break;
        }
        Assert.False(OwnedBy(created, current, 90));
    }

    [Fact]
    public void LocalAddressAndFabricatedInterfacesFailClosed()
    {
        var backend = new WindowsRelayRouteBackend();
        Assert.False(backend.CanChange(IPAddress.Loopback));
        Assert.False(IsPathCurrent(new RelayNetworkPath("not-an-adapter", "fake", 3,
            IPAddress.Parse("10.0.0.2"), IPAddress.Parse("8.138.5.232"), "10.0.0.1")));
    }
}
