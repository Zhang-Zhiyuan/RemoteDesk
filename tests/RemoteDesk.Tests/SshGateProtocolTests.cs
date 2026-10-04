using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshGateProtocolTests
{
    private const string Id = "01234567-89ab-cdef-0123-456789abcdef";

    [Theory]
    [InlineData("server", "1f3698cd93105499cee01ff797ef4a2e5b67f88602361194c42e7e818003d145")]
    [InlineData("client", "2cd865b69b18ae31b0848a8cf9d76c5ce72a013a2844355f8514bfbeca4da792")]
    public void SharedCrossPlatformProofVector(string role, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(SshGateProtocol.ComputeProof("test-device-key", role, Id,
            Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())).ToLowerInvariant());

    [Fact]
    public void ProofBindsRoleDeviceNonceAndDoesNotReuseDesktopProof()
    {
        byte[] nonce = new byte[32];
        byte[] proof = SshGateProtocol.ComputeProof("key", "server", Id, nonce);
        Assert.NotEqual(proof, SshGateProtocol.ComputeProof("key", "client", Id, nonce));
        Assert.NotEqual(proof, SshGateProtocol.ComputeProof("key", "server", Guid.NewGuid().ToString("D"), nonce));
        Assert.NotEqual(proof, SshGateProtocol.ComputeProof("key", "server", Id, RandomNumberGenerator.GetBytes(32)));
        Assert.NotEqual(proof, HMACSHA256.HashData(SHA256.HashData("key"u8), nonce));
    }

    [Fact]
    public async Task ServerRejectsReflectedServerProof()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var host = await listener.AcceptTcpClientAsync(timeout.Token);
        Task<bool> authentication = SshGateProtocol.AuthenticateServerAsync(host.GetStream(), "key", Id, timeout.Token);
        byte[] challenge = new byte[SshGateProtocol.ChallengeLength];
        await client.GetStream().ReadExactlyAsync(challenge, timeout.Token);
        await client.GetStream().WriteAsync(challenge.AsMemory(72, 32), timeout.Token);
        Assert.False(await authentication);
        byte[] status = new byte[1];
        await client.GetStream().ReadExactlyAsync(status, timeout.Token);
        Assert.Equal(0, status[0]);
    }

    [Theory]
    [InlineData(0, typeof(UnauthorizedAccessException))]
    [InlineData(2, typeof(IOException))]
    [InlineData(7, typeof(InvalidDataException))]
    public async Task ClientExplainsFailureWithoutStartingSsh(int status, Type expected)
    {
        byte[] nonce = new byte[32];
        using var stream = new ScriptedStream("RDS1"u8.ToArray().Concat(nonce)
            .Concat(Encoding.ASCII.GetBytes(Id)).Concat(SshGateProtocol.ComputeProof("key", "server", Id, nonce))
            .Append((byte)status).ToArray());
        Exception? error = await Record.ExceptionAsync(() => SshGateProtocol.AuthenticateClientAsync(stream, "key", Id, CancellationToken.None));
        Assert.IsType(expected, error);
        Assert.Equal(32, stream.Written.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongKeyOrIdentityNeverSendsClientProof(bool wrongKey)
    {
        byte[] nonce = new byte[32];
        using var stream = new ScriptedStream("RDS1"u8.ToArray().Concat(nonce)
            .Concat(Encoding.ASCII.GetBytes(Id)).Concat(SshGateProtocol.ComputeProof("key", "server", Id, nonce)).ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => SshGateProtocol.AuthenticateClientAsync(stream,
            wrongKey ? "wrong" : "key", wrongKey ? Id : Guid.NewGuid().ToString("D"), CancellationToken.None));
        Assert.Empty(stream.Written.ToArray());
    }

    [Theory]
    [InlineData("{\"ok\":true,\"type\":\"paired\"}")]
    [InlineData("{\"ok\":true,\"type\":\"paired\",\"service\":\"desktop\"}")]
    [InlineData("{\"ok\":true,\"type\":false,\"service\":\"ssh\"}")]
    public void NoImplicitDesktopOrLegacyPairFallback(string value)
    {
        using var document = JsonDocument.Parse(value);
        Assert.Throws<InvalidDataException>(() => SshRelayClient.EnsureSshPair(document.RootElement));
    }

    [Fact]
    public void DisabledHostDefaultsAndSecretsAreRedacted()
    {
        Assert.False(RelaySshHostOptions.Disabled.IsAvailable);
        Assert.False(new RelaySshHostOptions(true, 0, "key").IsAvailable);
        Assert.False(new RelaySshHostOptions(true, 22, "").IsAvailable);
        Assert.DoesNotContain("device-secret", new RelaySshHostOptions(true, 22, "device-secret").ToString());
    }

    private sealed class ScriptedStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        internal MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => _input.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => Written.WriteAsync(buffer, token);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _input.Dispose(); Written.Dispose(); } base.Dispose(disposing); }
    }
}
