using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class DualStackNetworkTests
{
    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, true, false)]
    public void BackgroundDiscoveryRunsOnlyOnIdleVisibleDirectPage(bool visible, bool minimized, bool direct, bool busy, bool expected) =>
        Assert.Equal(expected, MainForm.ShouldPollDirectDevices(visible, minimized, direct, busy));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task OneListenerAuthenticatesAndTransfersEncryptedDataOnBothFamilies(string address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = NetworkUtils.StartDualStackListener(0);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        using TcpClient viewer = await DualStackConnector.ConnectAsync(address, port, client => client.NoDelay = true, timeout.Token);
        using TcpClient host = await accepted;
        Task<SecureSession?> hostAuth = Protocol.AuthenticateServerAsync(host.GetStream(), "isolated-ipv6-fixture", timeout.Token);
        using SecureSession viewerSession = await Protocol.AuthenticateClientAsync(viewer.GetStream(), "isolated-ipv6-fixture", timeout.Token);
        using SecureSession hostSession = Assert.IsType<SecureSession>(await hostAuth);
        using var writeLock = new SemaphoreSlim(1, 1);
        byte[] text = Encoding.UTF8.GetBytes("IPv6 中文剪贴板 / IPv4 fallback 😀");
        await Protocol.WriteMessageAsync(viewer.GetStream(), MessageType.Control, text, viewerSession, writeLock, timeout.Token);
        ProtocolMessage received = await Protocol.ReadMessageAsync(host.GetStream(), hostSession, timeout.Token);
        Assert.Equal(text, received.PayloadMemory.ToArray());
        byte[] data = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        await Protocol.WriteMessageAsync(host.GetStream(), MessageType.Control, data, hostSession, writeLock, timeout.Token);
        ProtocolMessage returned = await Protocol.ReadMessageAsync(viewer.GetStream(), viewerSession, timeout.Token);
        Assert.Equal(data, returned.PayloadMemory.ToArray());
    }

    [Fact]
    public async Task FailedIpv6CandidateDoesNotPreventIpv4Connection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TcpClient client = await DualStackConnector.ConnectAsync([IPAddress.IPv6Loopback, IPAddress.Loopback],
            port, _ => { }, timeout.Token);
        using TcpClient server = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
    }

    [Fact]
    public async Task CancellationDoesNotLeaveConnectionAttemptsRunning()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DualStackConnector.ConnectAsync(
            [IPAddress.IPv6Loopback, IPAddress.Loopback], 1, _ => Assert.Fail("Cancelled dial must not open a socket"), cancelled.Token));
    }

    [Fact]
    public void AddressCandidatesAreBoundedAndInterleaved()
    {
        var addresses = DualStackConnector.Interleave(Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"2001:db8::{i}"))
            .Append(IPAddress.Parse("192.0.2.1")));
        Assert.Equal(8, addresses.Count);
        Assert.Equal(AddressFamily.InterNetwork, addresses[1].AddressFamily);
    }

    [Fact]
    public void ManyVirtualAdaptersCannotCrowdOutTheOtherFamily()
    {
        var report = RelayAddressReport.Normalize(Enumerable.Range(1, 24).Select(i => $"192.0.2.{i}").Append("2001:db8::1"));
        Assert.Equal(8, report.Count);
        Assert.Equal("2001:db8::1", report[^1]);
    }

    [Fact]
    public async Task ExplicitIpv6PortProbeReadsProductBanner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<RemoteDeskTcpProbeResult> probe = NetworkDiscoveryService.ProbeRemoteDeskTcpAsync(
            IPAddress.IPv6Loopback, port, TimeSpan.FromSeconds(3), timeout.Token);
        using TcpClient server = await listener.AcceptTcpClientAsync(timeout.Token);
        await server.GetStream().WriteAsync("RDK1"u8.ToArray(), timeout.Token);
        Assert.Equal(RemoteDeskTcpProbeStatus.RemoteDesk, (await probe).Status);
    }

    [Fact]
    public async Task ExplicitIpv6UdpDiscoveryPreservesIdentityAndCustomPort()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var responder = Ipv6Discovery.OpenResponder(0);
        Assert.NotNull(responder);
        int port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        Task<IReadOnlyList<DiscoveredHost>> scan = NetworkDiscoveryService.DiscoverAsync(TimeSpan.FromMilliseconds(400),
            timeout.Token, ["::1"], discoveryPort: port, hostProbePort: 12345, includeBroadcast: false);
        UdpReceiveResult request = await responder.ReceiveAsync(timeout.Token);
        Assert.Equal("RemoteDesk.Discover.v1", Encoding.UTF8.GetString(request.Buffer));
        await responder.SendAsync(Encoding.UTF8.GetBytes("{\"Type\":\"RemoteDesk.Discover.Response.v1\",\"MachineName\":\"IPv6 fixture\",\"Port\":12345,\"Platform\":\"Linux\",\"DeviceId\":\"80a98dda-5f32-492a-8bed-1b50883d5247\"}"), request.RemoteEndPoint, timeout.Token);
        Assert.Contains(await scan, host => host.Address == "::1" && host.Port == 12345 && host.Platform == "Linux");
    }

    [Theory]
    [InlineData("2001:0DB8:0:0:0:0:0:1", "2001:db8::1")]
    [InlineData("fd12:3456::10", "fd12:3456::10")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2:3:4:5:6")]
    public void ReportCanonicalizesIpv6WithoutChangingPort(string value, string expected)
    {
        Assert.Equal(expected, Assert.Single(RelayAddressReport.Normalize([value, expected])));
        Assert.Equal($"[{expected}]:40565", NetworkUtils.FormatEndpoint(expected, 40565));
    }

    [Theory]
    [InlineData("::")][InlineData("::1")][InlineData("::2")][InlineData("::ffff:192.0.2.1")]
    [InlineData("fe80::1")][InlineData("fe80::1%19")][InlineData("2001:db8::1%19")]
    [InlineData("ff02::1")][InlineData("fec0::1")][InlineData("[2001:db8::1]")][InlineData("2001:db8::1\n")]
    public void ReportRejectsScopesAndNonPortableAddresses(string address) => Assert.Empty(RelayAddressReport.Normalize([address]));
}
