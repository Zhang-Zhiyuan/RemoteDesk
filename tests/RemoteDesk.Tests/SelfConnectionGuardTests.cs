using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SelfConnectionGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.3.4")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    public void RejectsLoopbackAndUnspecified(string address) =>
        Assert.True(SelfConnectionGuard.IsLocalAddress(IPAddress.Parse(address), []));

    [Theory]
    [InlineData("192.0.2.4", "::ffff:192.0.2.4")]
    [InlineData("::ffff:192.0.2.4", "192.0.2.4")]
    [InlineData("2001:db8::4", "2001:db8::4")]
    public void MatchesEveryLocalAdapterAndMappedAddress(string peer, string adapter) =>
        Assert.True(SelfConnectionGuard.IsLocalAddress(IPAddress.Parse(peer),
            [IPAddress.Parse("192.0.2.9"), IPAddress.Parse(adapter)]));

    [Fact]
    public void ConnectedSourceAddressAlsoGuardsInterfaceEnumerationRaces() =>
        Assert.True(SelfConnectionGuard.IsLocalAddress(IPAddress.Parse("192.0.2.4"), [], IPAddress.Parse("192.0.2.4")));

    [Fact]
    public void RemoteAddressIsNotBlocked() =>
        Assert.False(SelfConnectionGuard.IsLocalAddress(IPAddress.Parse("192.0.2.4"),
            [IPAddress.Parse("192.0.2.9")], IPAddress.Parse("192.0.2.9")));

    [Fact]
    public void LinkLocalScopesRemainDistinct() =>
        Assert.False(SelfConnectionGuard.IsLocalAddress(IPAddress.Parse("fe80::4%2"), [IPAddress.Parse("fe80::4%3")]));

    [Fact]
    public async Task LocalhostNameIsRejectedBeforeUiRemoteStartOrDiscovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RemoteSessionRejectedException>(() =>
            SelfConnectionGuard.ValidateDirectHostAsync("localhost", timeout.Token));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task ProductionViewerClosesLocalSocketWithoutSendingAuthentication(string host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        using var client = new RemoteViewerClient(); // Deliberately production defaults.
        var failure = await Assert.ThrowsAsync<RemoteSessionRejectedException>(() =>
            client.ConnectAsync(host, ((IPEndPoint)listener.LocalEndpoint).Port, "unused-test-key",
                ViewerVideoMode.StableJpeg, timeout.Token));
        Assert.Equal(SelfConnectionGuard.Message, failure.Message);
        using TcpClient peer = await accept;
        // The real socket reached the guard, but no client hello/password proof
        // was sent. No RemoteHostServer, user desktop or takeover is involved.
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], timeout.Token));
        Assert.False(client.IsConnected);
        Assert.False(ViewerReconnectPolicy.IsRetryableConnectionFailure(failure));
        Assert.False(MainForm.IsConnectionFailureForRemoteStartRetry(failure));
    }

    [Fact]
    public void KnownLocalIdentityIsTerminal()
    {
        var failure = Assert.Throws<RemoteSessionRejectedException>(() =>
            SelfConnectionGuard.RejectLocalDevice(RemoteDeviceIdentity.LocalId));
        Assert.Equal(SelfConnectionGuard.Message, failure.Message);
        SelfConnectionGuard.RejectLocalDevice(Guid.NewGuid().ToString("D"));
        SelfConnectionGuard.RejectLocalDevice(null);
    }

    [Fact]
    public void DiscoveredAndSavedSelfRowsCannotReturnThroughAutomaticPortDetection()
    {
        DiscoveredHost[] hosts = [new("OtherName", "192.0.2.4", 56565, "Screen", true, false,
            RemoteDevicePlatforms.Windows, RemoteDeviceCapabilities.RemoteDesktop,
            DeviceId: RemoteDeviceIdentity.LocalId),
            new("Remote", "192.0.2.5", 56565, "Screen", true, false,
                RemoteDevicePlatforms.Windows, RemoteDeviceCapabilities.RemoteDesktop)];
        SavedRemoteDevice[] saved = [new() { Address = "192.0.2.9", Port = 56565,
            DeviceId = RemoteDeviceIdentity.LocalId }];
        var visible = MainForm.BuildRemoteDeviceList(hosts, saved, new HashSet<string>());
        Assert.Single(visible);
    }

    [Fact]
    public async Task RelayToOwnIdentityIsRejectedBeforeOpeningTunnel()
    {
        using var client = new RemoteViewerClient();
        var route = new RelayConnectionOptions("127.0.0.1", 1, new string('t', 32), new string('a', 64), RemoteDeviceIdentity.LocalId);
        var failure = await Assert.ThrowsAsync<RemoteSessionRejectedException>(() =>
            client.ConnectViaRelayAsync(route, "unused-test-key", ViewerVideoMode.StableJpeg));
        Assert.Equal(SelfConnectionGuard.Message, failure.Message);
    }
}
