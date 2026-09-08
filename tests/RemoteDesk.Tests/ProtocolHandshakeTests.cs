using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ProtocolHandshakeTests
{
    [Fact]
    public async Task MatchingPasswordsAuthenticateAndExchangeEncryptedMessages()
    {
        using var fixture = await LoopbackConnection.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<SecureSession?> serverAuthTask = Protocol.AuthenticateServerAsync(
            fixture.ServerStream,
            "correct horse battery staple",
            timeout.Token);
        Task<SecureSession> clientAuthTask = Protocol.AuthenticateClientAsync(
            fixture.ClientStream,
            "correct horse battery staple",
            timeout.Token);

        using SecureSession clientSession = await clientAuthTask;
        using SecureSession? serverSession = await serverAuthTask;
        Assert.NotNull(serverSession);

        using var clientWriteLock = new SemaphoreSlim(1, 1);
        using var serverWriteLock = new SemaphoreSlim(1, 1);

        Task<ProtocolMessage> serverReadTask = Protocol.ReadMessageAsync(
            fixture.ServerStream,
            serverSession,
            timeout.Token);
        await Protocol.WriteMessageAsync(
            fixture.ClientStream,
            MessageType.Control,
            RemoteMessageCodec.EncodeClipboardGetText(),
            clientSession,
            clientWriteLock,
            timeout.Token);

        ProtocolMessage serverMessage = await serverReadTask;
        Assert.Equal(MessageType.Control, serverMessage.Type);
        Assert.Equal(RemoteControlKind.ClipboardGetText, RemoteMessageCodec.DecodeControl(serverMessage.PayloadMemory).Kind);

        Task<ProtocolMessage> clientReadTask = Protocol.ReadMessageAsync(
            fixture.ClientStream,
            clientSession,
            timeout.Token);
        await Protocol.WriteMessageAsync(
            fixture.ServerStream,
            MessageType.Pong,
            ReadOnlyMemory<byte>.Empty,
            serverSession,
            serverWriteLock,
            timeout.Token);

        ProtocolMessage clientMessage = await clientReadTask;
        Assert.Equal(MessageType.Pong, clientMessage.Type);
        Assert.Equal(0, clientMessage.PayloadLength);
    }

    [Fact]
    public async Task MismatchedPasswordsRejectAuthentication()
    {
        using var fixture = await LoopbackConnection.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<SecureSession?> serverAuthTask = Protocol.AuthenticateServerAsync(
            fixture.ServerStream,
            "server password",
            timeout.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Protocol.AuthenticateClientAsync(
                fixture.ClientStream,
                "client password",
                timeout.Token));

        Assert.Null(await serverAuthTask);
    }

    [Fact]
    public async Task ServerAuthenticationKeepsCredentialFailureDistinctFromIncompleteProbe()
    {
        using var fixture = await LoopbackConnection.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<ServerAuthenticationResult> serverAuthTask = Protocol.AuthenticateServerDetailedAsync(
            fixture.ServerStream,
            "server password",
            timeout.Token);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Protocol.AuthenticateClientAsync(
                fixture.ClientStream,
                "client password",
                timeout.Token));

        ServerAuthenticationResult result = await serverAuthTask;
        Assert.False(result.IsAuthenticated);
        Assert.False(result.IsIncomplete);
    }

    [Fact]
    public async Task ServerAuthenticationMarksEarlyProbeDisconnectAsIncomplete()
    {
        using var fixture = await LoopbackConnection.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<ServerAuthenticationResult> serverAuthTask = Protocol.AuthenticateServerDetailedAsync(
            fixture.ServerStream,
            "server password",
            timeout.Token);

        Assert.True(await Protocol.ReadServerMagicAsync(fixture.ClientStream, timeout.Token));
        fixture.CloseClient();

        ServerAuthenticationResult result = await serverAuthTask;
        Assert.False(result.IsAuthenticated);
        Assert.True(result.IsIncomplete);
    }

    private sealed class LoopbackConnection : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _server;

        private LoopbackConnection(TcpListener listener, TcpClient client, TcpClient server)
        {
            _listener = listener;
            _client = client;
            _server = server;
            ClientStream = client.GetStream();
            ServerStream = server.GetStream();
        }

        public NetworkStream ClientStream { get; }

        public NetworkStream ServerStream { get; }

        public void CloseClient()
        {
            _client.Dispose();
        }

        public static async Task<LoopbackConnection> CreateAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
                var client = new TcpClient
                {
                    NoDelay = true
                };
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                TcpClient server = await acceptTask;
                server.NoDelay = true;
                return new LoopbackConnection(listener, client, server);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
            _listener.Dispose();
        }
    }
}
