using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayFailureTests
{
    [Fact]
    public async Task DirectoryDeadlineReturnsTimeoutAndAllowsNextRequest()
    {
        using var server = new TestRelay();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task stalled = HoldHandshakeAsync(server, stop.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => RelayTunnelClient.ListDevicesAsync(
            server.Options, stop.Token, TimeSpan.FromMilliseconds(250)));
        await stalled;

        Task healthy = ReplyWithDirectoryAsync(server, stop.Token);
        IReadOnlyList<RelayOnlineDevice> devices = await RelayTunnelClient.ListDevicesAsync(
            server.Options, stop.Token);
        Assert.Equal("reachable", Assert.Single(devices).MachineName);
        await healthy;
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var server = new TestRelay();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        Task stalled = HoldHandshakeAsync(server, stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RelayTunnelClient.ListDevicesAsync(server.Options, caller.Token));
        await stalled;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task MalformedMessageRootBecomesProtocolError(string json)
    {
        using var wire = new MemoryStream();
        using JsonDocument input = JsonDocument.Parse(json);
        await RelayTls.WriteJsonAsync(wire, input.RootElement, CancellationToken.None);
        wire.Position = 0;
        await Assert.ThrowsAsync<RelayProtocolException>(() =>
            RelayTls.ReadJsonAsync(wire, CancellationToken.None));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("silent-heartbeat")]
    [InlineData("silent-registration")]
    public async Task HostRecoversWithoutWaitingForOldHeartbeat(string failure)
    {
        using var server = new TestRelay();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var connector = new RelayHostConnector(
            handshakeTimeout: TimeSpan.FromMilliseconds(250),
            heartbeatTimeout: TimeSpan.FromMilliseconds(250));
        Task initial = FailFirstControlAsync(server, failure, stop.Token);
        await connector.StartAsync(server.Options, 56565, stop.Token);
        await initial;
        using TcpClient next = await server.Listener.AcceptTcpClientAsync(stop.Token);
        using SslStream tls = await server.AuthenticateAsync(next, stop.Token);
        using JsonDocument hello = await RelayTls.ReadJsonAsync(tls, stop.Token);
        Assert.Equal("host-control", hello.RootElement.GetProperty("role").GetString());
        await RelayTls.WriteJsonAsync(tls, new { ok = true, heartbeatAck = true }, stop.Token);
        await connector.StopAsync();
        Assert.False(connector.IsRunning);
    }

    [Theory]
    [InlineData(443)]
    [InlineData(8443)]
    public void RelayPortSurvivesSettingsNormalization(int port)
    {
        var settings = new RemoteDeskSettings { Relay = new RelaySettings { RelayPort = port } };
        Assert.Equal(port, AppSettingsService.NormalizeSettings(settings).Relay.RelayPort);
    }

    private static async Task HoldHandshakeAsync(TestRelay server, CancellationToken stop)
    {
        using TcpClient client = await server.Listener.AcceptTcpClientAsync(stop);
        // Accept TCP but never speak TLS. Drain until the timed-out client closes.
        var buffer = new byte[4096];
        while (await client.GetStream().ReadAsync(buffer, stop) != 0) { }
    }

    private static async Task ReplyWithDirectoryAsync(TestRelay server, CancellationToken stop)
    {
        using TcpClient client = await server.Listener.AcceptTcpClientAsync(stop);
        using SslStream tls = await server.AuthenticateAsync(client, stop);
        using JsonDocument request = await RelayTls.ReadJsonAsync(tls, stop);
        // A malformed entry/optional integer must not crash the UI's timer callback.
        await RelayTls.WriteJsonAsync(tls, new
        {
            ok = true,
            devices = new object[]
            {
                1,
                new { deviceId = server.Options.DeviceId, machineName = "reachable", lastSeenSeconds = "invalid" }
            }
        }, stop);
    }

    private static async Task FailFirstControlAsync(TestRelay server, string failure, CancellationToken stop)
    {
        using TcpClient client = await server.Listener.AcceptTcpClientAsync(stop);
        using SslStream tls = await server.AuthenticateAsync(client, stop);
        using JsonDocument hello = await RelayTls.ReadJsonAsync(tls, stop);
        if (failure != "silent-registration")
        {
            await RelayTls.WriteJsonAsync(tls, new { ok = true, heartbeatAck = true }, stop);
        }
        if (failure != "close")
        {
            byte[] buffer = new byte[1024];
            Assert.Equal(0, await tls.ReadAsync(buffer, stop));
        }
    }

    private sealed class TestRelay : IDisposable
    {
        private readonly X509Certificate2 _certificate;
        public TcpListener Listener { get; } = new(IPAddress.Loopback, 0);
        public RelayConnectionOptions Options { get; }

        public TestRelay()
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Relay Failure Test", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            Listener.Start();
            Options = new RelayConnectionOptions("127.0.0.1", ((IPEndPoint)Listener.LocalEndpoint).Port,
                new string('a', 64), RelayTls.GetSha256Fingerprint(_certificate.RawData), Guid.NewGuid().ToString("D"));
        }

        public async Task<SslStream> AuthenticateAsync(TcpClient client, CancellationToken stop)
        {
            var tls = new SslStream(client.GetStream(), false);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate
                }, stop);
                return tls;
            }
            catch
            {
                tls.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Listener.Stop();
            _certificate.Dispose();
        }
    }
}
