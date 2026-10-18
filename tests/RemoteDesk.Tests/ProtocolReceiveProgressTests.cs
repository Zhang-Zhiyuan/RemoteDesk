using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ProtocolReceiveProgressTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(18);

    [Fact]
    public void HeaderAloneIsNotProgressAndStalledBodyExpires()
    {
        var clock = new ManualClock();
        var progress = new ProtocolReceiveProgress(clock);
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        progress.Begin();
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        progress.Advance();
        Assert.True(progress.IsMakingProgress(IdleTimeout));
        clock.Advance(IdleTimeout);
        Assert.True(progress.IsMakingProgress(IdleTimeout));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(progress.IsMakingProgress(IdleTimeout));
    }

    [Fact]
    public void ContinuedTrickleCannotExtendMessageForever()
    {
        var clock = new ManualClock();
        var progress = new ProtocolReceiveProgress(clock);
        progress.Begin();
        for (int seconds = 0; seconds < 90; seconds++)
        {
            progress.Advance();
            Assert.True(progress.IsMakingProgress(IdleTimeout));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        progress.Advance();
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        progress.End();
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        progress.Begin();
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        progress.Advance();
        Assert.True(progress.IsMakingProgress(IdleTimeout));
    }

    [Fact]
    public void OldConnectionProgressCannotKeepNewConnectionAlive()
    {
        var clock = new ManualClock();
        var oldConnection = new ProtocolReceiveProgress(clock);
        var newConnection = new ProtocolReceiveProgress(clock);
        oldConnection.Begin();
        oldConnection.Advance();
        newConnection.Begin();
        Assert.True(oldConnection.IsMakingProgress(IdleTimeout));
        Assert.False(newConnection.IsMakingProgress(IdleTimeout));
        oldConnection.End();
        Assert.False(oldConnection.IsMakingProgress(IdleTimeout));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("bad-tag")]
    [InlineData("cancel")]
    [InlineData("close")]
    public async Task FragmentedCiphertextGetsBoundedGraceButMustStillAuthenticate(string outcome)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var receiver = await listener.AcceptTcpClientAsync(timeout.Token);
        using var writerSession = new SecureSession(new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), isServer: true);
        using var readerSession = new SecureSession(new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), isServer: false);
        byte[] payload = RandomNumberGenerator.GetBytes(128 * 1024);
        byte[] plain = new byte[payload.Length + 5];
        plain[0] = (byte)MessageType.Frame;
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(1), payload.Length);
        payload.CopyTo(plain, 5);
        byte[] cipher = writerSession.Encrypt(plain);
        if (outcome == "bad-tag") cipher[^1] ^= 1;
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, cipher.Length);
        var progress = new ProtocolReceiveProgress();
        Task<ProtocolMessage> read = Protocol.ReadMessageAsync(receiver.GetStream(), readerSession, readCancellation.Token, progress);

        await sender.GetStream().WriteAsync(header.AsMemory(0, 2), timeout.Token);
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        await sender.GetStream().WriteAsync(header.AsMemory(2), timeout.Token);
        Assert.False(progress.IsMakingProgress(IdleTimeout));
        await sender.GetStream().WriteAsync(cipher.AsMemory(0, 32), timeout.Token);
        while (!progress.IsMakingProgress(IdleTimeout)) await Task.Delay(5, timeout.Token);
        Assert.False(read.IsCompleted); // No partial plaintext may reach the UI.
        switch (outcome)
        {
            case "cancel":
                readCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
                break;
            case "close":
                sender.Close();
                await Assert.ThrowsAnyAsync<IOException>(async () => await read);
                break;
            default:
                await sender.GetStream().WriteAsync(cipher.AsMemory(32), timeout.Token);
                if (outcome == "bad-tag")
                    await Assert.ThrowsAnyAsync<CryptographicException>(async () => await read);
                else
                    Assert.Equal(payload, (await read).PayloadMemory.ToArray());
                break;
        }
        Assert.False(progress.IsMakingProgress(IdleTimeout));
    }

    [Fact]
    public async Task InvalidLengthCannotStartReceiveGrace()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var receiver = await listener.AcceptTcpClientAsync(timeout.Token);
        using var session = new SecureSession(new byte[32], new byte[32], isServer: false);
        var progress = new ProtocolReceiveProgress();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await sender.GetStream().WriteAsync(header, timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadMessageAsync(receiver.GetStream(), session, timeout.Token, progress));
        Assert.False(progress.IsMakingProgress(IdleTimeout));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan time) => _ticks += time.Ticks;
    }
}
