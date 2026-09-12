using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayHostRecoveryTests
{
    [Fact]
    public async Task AutomaticReconnectPreparesRouteAgain()
    {
        await using var relay = new RelayStub(dropFirst: true);
        int preparations = 0;
        using var connector = new RelayHostConnector(prepareConnection: (_, _) =>
        {
            Interlocked.Increment(ref preparations);
            return Task.CompletedTask;
        });
        await connector.StartAsync(relay.Options, 56565, relay.Token);
        Assert.Equal(1, await relay.Registered.Reader.ReadAsync(relay.Token));
        Assert.Equal(2, await relay.Registered.Reader.ReadAsync(relay.Token));
        Assert.Equal(2, preparations);
    }

    [Fact]
    public async Task OptionalPreparationFailureDoesNotBlockPinnedRegistration()
    {
        await using var relay = new RelayStub();
        var statuses = new ConcurrentQueue<string>();
        using var connector = new RelayHostConnector(prepareConnection: (_, _) =>
            throw new InvalidOperationException("Optional optimizer unavailable"));
        connector.StatusChanged += statuses.Enqueue;
        await connector.StartAsync(relay.Options, 56565, relay.Token);
        Assert.Equal(1, await relay.Registered.Reader.ReadAsync(relay.Token));
        Assert.Contains(statuses, status => status.Contains("线路优化暂不可用"));
    }

    [Fact]
    public async Task StoppingDuringPreparationDoesNotOpenRegistration()
    {
        await using var relay = new RelayStub();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connector = new RelayHostConnector(prepareConnection: async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await connector.StartAsync(relay.Options, 56565, relay.Token);
        await entered.Task.WaitAsync(relay.Token);
        await connector.StopAsync();
        Assert.False(connector.IsRunning);
        Assert.False(relay.Registered.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ChangedAddressRepreparesAnIdleRegistration()
    {
        await using var relay = new RelayStub();
        string address = "192.0.2.1";
        int preparations = 0;
        using var connector = new RelayHostConnector(addressProvider: () => [Volatile.Read(ref address)],
            prepareConnection: (_, _) => { Interlocked.Increment(ref preparations); return Task.CompletedTask; });
        await connector.StartAsync(relay.Options, 56565, relay.Token);
        Assert.Equal(1, await relay.Registered.Reader.ReadAsync(relay.Token));
        Volatile.Write(ref address, "192.0.2.2");
        Assert.True(connector.RequestAddressRefresh());
        Assert.Equal(2, await relay.Registered.Reader.ReadAsync(relay.Token));
        Assert.Equal(2, preparations);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AddressChangeWaitsForTheRealSessionToFinish(bool active, bool expected) =>
        Assert.Equal(expected, RelayHostConnector.ShouldRefreshNetwork(["192.0.2.1"], ["192.0.2.2"], active));

    [Fact]
    public void EnumerationOrderAndTransientEmptyAddressesDoNotReconnect()
    {
        Assert.False(RelayHostConnector.ShouldRefreshNetwork(["192.0.2.1", "192.0.2.2"], ["192.0.2.2", "192.0.2.1"], false));
        Assert.False(RelayHostConnector.ShouldRefreshNetwork(["192.0.2.1"], [], false));
        Assert.True(RelayHostConnector.ShouldRefreshNetwork([], ["192.0.2.1"], false));
    }

    // Real local TLS and framing; no public server, desktop, route changes or files.
    private sealed class RelayStub : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        private readonly bool _dropFirst;
        private int _registrations;
        internal Channel<int> Registered { get; } = Channel.CreateUnbounded<int>();
        internal CancellationToken Token => _stop.Token;
        internal RelayConnectionOptions Options { get; }

        internal RelayStub(bool dropFirst = false)
        {
            _dropFirst = dropFirst;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            _certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx));
            _listener.Start();
            Options = new("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port,
                new string('a', 64), RelayTls.GetSha256Fingerprint(_certificate.RawData), Guid.NewGuid().ToString());
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                    _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(Token)));
            }
            catch (Exception error) when (Token.IsCancellationRequested && error is OperationCanceledException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var tls = new SslStream(client.GetStream(), false))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12
                    }, Token);
                    using (var hello = await RelayTls.ReadJsonAsync(tls, Token))
                        Assert.Equal("host-control", hello.RootElement.GetProperty("role").GetString());
                    int registration = Interlocked.Increment(ref _registrations);
                    await RelayTls.WriteJsonAsync(tls, new { ok = true, heartbeatAck = true }, Token);
                    Registered.Writer.TryWrite(registration);
                    if (_dropFirst && registration == 1) return;
                    while (!Token.IsCancellationRequested)
                    {
                        using var heartbeat = await RelayTls.ReadJsonAsync(tls, Token);
                        await RelayTls.WriteJsonAsync(tls, new { ok = true, type = "heartbeat-ack" }, Token);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _accept; await Task.WhenAll(_clients); }
            finally { _certificate.Dispose(); _stop.Dispose(); }
        }
    }
}
