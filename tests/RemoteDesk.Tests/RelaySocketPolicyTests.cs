using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelaySocketPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.7.163.74", false)]
    [InlineData("8.138.5.232", false)]
    [InlineData("::ffff:10.7.163.74", false)]
    public void SmallHostQueueAppliesOnlyToLoopback(string address, bool loopback)
    {
        const int direct = 128 * 1024;
        Assert.Equal(loopback ? 16384 : direct,
            RelayLoopbackPolicy.HostSendBufferBytes(new IPEndPoint(IPAddress.Parse(address), 56565), direct));
        Assert.Equal(direct, RelayLoopbackPolicy.HostSendBufferBytes(null, direct));
    }

    [Fact]
    public void ExplicitLoopbackAdapterUsesSmallWindowsAndCopies()
    {
        using var client = new TcpClient();
        RelayLoopbackPolicy.Configure(client);
        Assert.Equal(16384, client.ReceiveBufferSize);
        Assert.Equal(16384, client.SendBufferSize);
        Assert.Equal(16384, RelayStreamBridge.CopyBufferBytes);
        Assert.True(client.NoDelay);
    }

    [Fact]
    public void DataTunnelLeavesReceiveAutotuningUntouched()
    {
        using var client = new TcpClient();
        int initialReceiveBuffer = client.ReceiveBufferSize;
        RelayTls.ConfigureTcpClient(client, dataTunnel: true);
        Assert.Equal(initialReceiveBuffer, client.ReceiveBufferSize);
        Assert.Equal(RelayTls.DataSendBufferBytes, client.SendBufferSize);
        Assert.True(client.NoDelay);
    }

    [Fact]
    public void ControlConnectionsRetainSmallBoundedBuffers()
    {
        using var client = new TcpClient();
        RelayTls.ConfigureTcpClient(client, dataTunnel: false);
        Assert.Equal(64 * 1024, client.ReceiveBufferSize);
        Assert.Equal(32 * 1024, client.SendBufferSize);
        Assert.True(client.NoDelay);
    }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("中文 relay test")]
    public async Task JsonPrefixAndPayloadUseOneWriteWithoutChangingWireBytes(string value)
    {
        using var stream = new CountingStream();
        await RelayTls.WriteJsonAsync(stream, new { type = value }, CancellationToken.None);
        Assert.Equal(1, stream.Writes);
        Assert.Equal(1, stream.Flushes);
        byte[] frame = stream.ToArray();
        Assert.Equal(frame.Length - 4, BinaryPrimitives.ReadInt32BigEndian(frame));
        stream.Position = 0;
        using JsonDocument parsed = await RelayTls.ReadJsonAsync(stream, CancellationToken.None);
        Assert.Equal(value, parsed.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OversizedJsonDoesNotWriteAnyBytes()
    {
        using var stream = new CountingStream();
        await Assert.ThrowsAsync<RelayProtocolException>(() => RelayTls.WriteJsonAsync(
            stream, new { text = new string('x', 65536) }, CancellationToken.None));
        Assert.Equal(0, stream.Writes);
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Writes++;
            return base.WriteAsync(buffer, token);
        }
        public override Task FlushAsync(CancellationToken token)
        {
            Flushes++;
            return base.FlushAsync(token);
        }
    }
}
