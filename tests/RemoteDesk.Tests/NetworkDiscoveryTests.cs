using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NetworkDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsyncUsesDirectAddressAndParsesDiscoveryResponse()
    {
        using var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<IReadOnlyList<DiscoveredHost>> discoverTask = NetworkDiscoveryService.DiscoverAsync(
            TimeSpan.FromMilliseconds(500),
            timeout.Token,
            [IPAddress.Loopback.ToString()],
            discoveryPort);

        UdpReceiveResult requestPacket = await receiver.ReceiveAsync(timeout.Token);
        Assert.Equal("RemoteDesk.Discover.v1", Encoding.UTF8.GetString(requestPacket.Buffer));

        byte[] response = NetworkDiscoveryService.CreateResponse(new DiscoveryPresence(
            56565,
            "Android Screen",
            IsHostRunning: true,
            CanRemoteStart: false,
            RemoteDevicePlatforms.Android,
            RemoteDeviceCapabilities.RemoteDesktop |
            RemoteDeviceCapabilities.ClipboardText |
            RemoteDeviceCapabilities.FileReceive));
        await receiver.SendAsync(response, requestPacket.RemoteEndPoint, timeout.Token);

        IReadOnlyList<DiscoveredHost> hosts = await discoverTask;
        DiscoveredHost? host = hosts.FirstOrDefault(item =>
            item.Address == IPAddress.Loopback.ToString() &&
            item.Port == 56565);
        Assert.True(
            host is not null,
            "Expected loopback discovery response. Actual hosts: " +
            string.Join(", ", hosts.Select(item => $"{item.Address}:{item.Port}/{item.Platform}/{item.CaptureTarget}")));
        Assert.True(host.IsHostRunning);
        Assert.False(host.CanRemoteStart);
        Assert.Equal(RemoteDevicePlatforms.Android, host.Platform);
        Assert.Contains("Android Screen", host.CaptureTarget, StringComparison.Ordinal);
        Assert.True(host.Capabilities.HasFlag(RemoteDeviceCapabilities.RemoteDesktop));
        Assert.True(host.Capabilities.HasFlag(RemoteDeviceCapabilities.FileReceive));
        Assert.Equal(RemoteDeskBuildInfo.NormalizeBuildStamp(RemoteDeskBuildInfo.BuildStamp), host.BuildStamp);
    }

    [Fact]
    public async Task DiscoverAsyncUsesDirectTcpProbeWhenUdpDiscoveryIsSilent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task acceptTask = AcceptAndWriteAsync(listener, "RDK1", timeout.Token);

        using var udpSink = new UdpClient(AddressFamily.InterNetwork);
        udpSink.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)udpSink.Client.LocalEndPoint!).Port;

        IReadOnlyList<DiscoveredHost> hosts = await NetworkDiscoveryService.DiscoverAsync(
            TimeSpan.FromMilliseconds(150),
            timeout.Token,
            [IPAddress.Loopback.ToString()],
            discoveryPort,
            hostPort);

        await acceptTask;
        DiscoveredHost? host = hosts.FirstOrDefault(item =>
            item.Address == IPAddress.Loopback.ToString() &&
            item.Port == hostPort);
        Assert.True(
            host is not null,
            "Expected TCP fallback discovery response. Actual hosts: " +
            string.Join(", ", hosts.Select(item => $"{item.Address}:{item.Port}/{item.Platform}/{item.CaptureTarget}")));
        Assert.True(host.IsHostRunning);
        Assert.False(host.CanRemoteStart);
        Assert.Equal(RemoteDevicePlatforms.Unknown, host.Platform);
        Assert.Contains("TCP", host.CaptureTarget, StringComparison.Ordinal);
        Assert.True(host.Capabilities.HasFlag(RemoteDeviceCapabilities.RemoteDesktop));
    }

    [Fact]
    public async Task DiscoverAsyncUsesDirectTargetPortForTcpProbeWhenUdpDiscoveryIsSilent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int targetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task acceptTask = AcceptAndWriteAsync(listener, "RDK1", timeout.Token);

        using var udpSink = new UdpClient(AddressFamily.InterNetwork);
        udpSink.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)udpSink.Client.LocalEndPoint!).Port;

        IReadOnlyList<DiscoveredHost> hosts = await NetworkDiscoveryService.DiscoverAsync(
            TimeSpan.FromMilliseconds(150),
            timeout.Token,
            discoveryPort: discoveryPort,
            hostProbePort: targetPort + 1,
            directTargets: [new DiscoveryProbeTarget(IPAddress.Loopback.ToString(), targetPort)]);

        await acceptTask;
        Assert.Contains(hosts, item =>
            item.Address == IPAddress.Loopback.ToString() &&
            item.Port == targetPort &&
            item.IsHostRunning);
    }

    [Fact]
    public async Task DiscoverAsyncUsesResolvedDirectHostNameForTcpProbe()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task acceptTask = AcceptAndWriteAsync(listener, "RDK1", timeout.Token);

        using var udpSink = new UdpClient(AddressFamily.InterNetwork);
        udpSink.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)udpSink.Client.LocalEndPoint!).Port;

        // Test localhost resolution, not physical NIC enumeration/broadcast.
        // A 150 ms budget flakes during concurrent Release publishing before
        // DNS even starts, especially on machines with virtual adapters.
        IReadOnlyList<DiscoveredHost> hosts = await NetworkDiscoveryService.DiscoverAsync(
            TimeSpan.FromSeconds(1),
            timeout.Token,
            ["localhost"],
            discoveryPort,
            hostPort,
            includeDirectedTcpProbes: false,
            includeBroadcast: false);

        await acceptTask;
        Assert.Contains(hosts, item =>
            item.Address == IPAddress.Loopback.ToString() &&
            item.Port == hostPort &&
            item.IsHostRunning);
    }

    [Fact]
    public async Task DiscoverAsyncIgnoresDirectTcpProbeWithoutRemoteDeskMagic()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task acceptTask = AcceptAndWriteAsync(listener, "HTTP", timeout.Token);

        using var udpSink = new UdpClient(AddressFamily.InterNetwork);
        udpSink.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)udpSink.Client.LocalEndPoint!).Port;

        IReadOnlyList<DiscoveredHost> hosts = await NetworkDiscoveryService.DiscoverAsync(
            TimeSpan.FromMilliseconds(150),
            timeout.Token,
            [IPAddress.Loopback.ToString()],
            discoveryPort,
            hostPort);

        await acceptTask;
        Assert.DoesNotContain(hosts, item =>
            item.Address == IPAddress.Loopback.ToString() &&
            item.Port == hostPort);
    }

    [Fact]
    public async Task ProbeRemoteDeskTcpAsyncReportsOpenPortWithoutRemoteDeskMagic()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int hostPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task acceptTask = AcceptAndWriteAsync(listener, "HTTP", timeout.Token);

        RemoteDeskTcpProbeResult result = await NetworkDiscoveryService.ProbeRemoteDeskTcpAsync(
            IPAddress.Loopback,
            hostPort,
            TimeSpan.FromSeconds(3),
            timeout.Token);

        await acceptTask;
        Assert.Equal(RemoteDeskTcpProbeStatus.NotRemoteDesk, result.Status);
        Assert.Equal(IPAddress.Loopback.ToString(), result.Address);
        Assert.Equal(hostPort, result.Port);
    }

    [Fact]
    public async Task ResolveDirectProbeAddressesAsyncResolvesHostNamesToIPv4()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        IReadOnlyList<IPAddress> addresses = await NetworkDiscoveryService.ResolveDirectProbeAddressesAsync(
            ["localhost"],
            timeout.Token);

        Assert.Contains(IPAddress.Loopback, addresses);
    }

    [Fact]
    public async Task ResolveDirectProbeTargetsAsyncKeepsTargetPorts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        IReadOnlyList<IPEndPoint> endpoints = await NetworkDiscoveryService.ResolveDirectProbeTargetsAsync(
            [
                new DiscoveryProbeTarget("localhost", 56565),
                new DiscoveryProbeTarget("localhost", 56566),
                new DiscoveryProbeTarget(" ", 56567),
                new DiscoveryProbeTarget("localhost", 0)
            ],
            timeout.Token);

        Assert.Contains(endpoints, endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == 56565);
        Assert.Contains(endpoints, endpoint => endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == 56566);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Port == 56567);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Port == 0);
    }

    [Fact]
    public void CreateTcpProbeEndpointsAddsDirectedSubnetTargetsWhenEnabled()
    {
        IReadOnlyList<IPEndPoint> endpoints = NetworkDiscoveryService.CreateTcpProbeEndpoints(
            [IPAddress.Parse("192.0.2.249")],
            [new IPEndPoint(IPAddress.Parse("192.0.2.250"), 56566)],
            56565,
            includeDirectedTcpProbes: true,
            directedProbeAddresses:
            [
                IPAddress.Parse("192.0.2.250"),
                IPAddress.Parse("192.0.2.251"),
                IPAddress.Parse("::1")
            ]);

        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.249")) &&
            endpoint.Port == 56565);
        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.249")) &&
            endpoint.Port ==
                RemotePortPolicy
                    .PreferredFallbackHostPort);
        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.249")) &&
            endpoint.Port ==
                RemotePortPolicy
                    .AlternateFallbackHostPort);
        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.250")) &&
            endpoint.Port == 56566);
        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.251")) &&
            endpoint.Port == 56565);
        Assert.Contains(endpoints, endpoint =>
            endpoint.Address.Equals(IPAddress.Parse("192.0.2.251")) &&
            endpoint.Port ==
                RemotePortPolicy
                    .PreferredFallbackHostPort);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.AddressFamily != AddressFamily.InterNetwork);
    }

    [Fact]
    public void CreateTcpProbeEndpointsExpandsSavedLegacyTargetToSafePorts()
    {
        IPAddress address =
            IPAddress.Parse(
                "192.0.2.249");

        IReadOnlyList<IPEndPoint> endpoints =
            NetworkDiscoveryService
                .CreateTcpProbeEndpoints(
                    Array.Empty<IPAddress>(),
                    [
                        new IPEndPoint(
                            address,
                            Protocol.DefaultPort)
                    ],
                    Protocol.DefaultPort,
                    includeDirectedTcpProbes:
                        false);

        Assert.Equal(
            3,
            endpoints.Count);
        Assert.Contains(
            endpoints,
            endpoint =>
                endpoint.Address.Equals(
                    address) &&
                endpoint.Port ==
                    Protocol.DefaultPort);
        Assert.Contains(
            endpoints,
            endpoint =>
                endpoint.Address.Equals(
                    address) &&
                endpoint.Port ==
                    RemotePortPolicy
                        .PreferredFallbackHostPort);
        Assert.Contains(
            endpoints,
            endpoint =>
                endpoint.Address.Equals(
                    address) &&
                endpoint.Port ==
                    RemotePortPolicy
                        .AlternateFallbackHostPort);
    }

    [Fact]
    public void CreateTcpProbeEndpointsSkipsDirectedSubnetTargetsWhenDisabled()
    {
        IReadOnlyList<IPEndPoint> endpoints = NetworkDiscoveryService.CreateTcpProbeEndpoints(
            Array.Empty<IPAddress>(),
            Array.Empty<IPEndPoint>(),
            56565,
            includeDirectedTcpProbes: false,
            directedProbeAddresses: [IPAddress.Parse("192.0.2.251")]);

        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task RequestRemoteStartAsyncSendsValidSignedRequestAndParsesResponse()
    {
        using var receiver = new UdpClient(AddressFamily.InterNetwork);
        receiver.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int discoveryPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<RemoteStartResult> requestTask = NetworkDiscoveryService.RequestRemoteStartAsync(
            IPAddress.Loopback.ToString(),
            56565,
            "shared password",
            TimeSpan.FromSeconds(5),
            timeout.Token,
            discoveryPort);

        UdpReceiveResult requestPacket = await receiver.ReceiveAsync(timeout.Token);
        Assert.True(NetworkDiscoveryService.TryParseRemoteStartRequest(requestPacket.Buffer, out RemoteStartRequestData? request));
        Assert.NotNull(request);
        Assert.True(NetworkDiscoveryService.ValidateRemoteStartRequest(request, "shared password"));
        Assert.False(NetworkDiscoveryService.ValidateRemoteStartRequest(request, "wrong password"));

        byte[] response = NetworkDiscoveryService.CreateRemoteStartResponse(
            request.Nonce,
            new RemoteStartResult(true, "started", 56565));
        await receiver.SendAsync(response, requestPacket.RemoteEndPoint, timeout.Token);

        RemoteStartResult result = await requestTask;
        Assert.True(result.Success);
        Assert.Equal("started", result.Message);
        Assert.Equal(56565, result.Port);
    }

    [Fact]
    public void ValidateRemoteStartRequestRejectsExpiredTimestamp()
    {
        var request = new RemoteStartRequestData
        {
            Type = "RemoteDesk.RemoteStart.Request.v1",
            Port = 56565,
            UnixTimeSeconds = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            Nonce = Convert.ToBase64String(new byte[16]),
            Proof = Convert.ToBase64String(new byte[32])
        };

        Assert.False(NetworkDiscoveryService.ValidateRemoteStartRequest(request, "shared password"));
    }

    [Fact]
    public void TryParseRemoteStartRequestRejectsWrongType()
    {
        byte[] payload = """
            {"Type":"RemoteDesk.Discover.Response.v1","Port":56565}
            """u8.ToArray();

        Assert.False(NetworkDiscoveryService.TryParseRemoteStartRequest(payload, out RemoteStartRequestData? request));
        Assert.Null(request);
    }

    private static async Task AcceptAndWriteAsync(
        TcpListener listener,
        string text,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        await client.GetStream().WriteAsync(bytes, cancellationToken);
    }
}
