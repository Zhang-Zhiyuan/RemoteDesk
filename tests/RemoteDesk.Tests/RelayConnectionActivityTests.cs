using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayConnectionActivityTests
{
    [Fact]
    public async Task OnlyReceivedBytesCountAsRecentActivity()
    {
        long tick = 100;
        var activity = new RelayConnectionActivity(() => tick);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        activity.Track(client, stream);
        Assert.False(activity.HasRecentTraffic(IPAddress.Loopback));
        activity.Find(stream)!.Touch();
        Assert.True(activity.HasRecentTraffic(IPAddress.Loopback));
        Assert.False(activity.HasRecentTraffic(IPAddress.Parse("8.138.5.232")));
        tick += 30_001;
        Assert.False(activity.HasRecentTraffic(IPAddress.Loopback));
    }

    [Fact]
    public async Task UnrelatedSocketsAndUntrackedDirectSessionsSurviveDisconnect()
    {
        var activity = new RelayConnectionActivity();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var relay = new TcpClient();
        await relay.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var relayPeer = await listener.AcceptTcpClientAsync();
        using var direct = new TcpClient();
        await direct.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var directPeer = await listener.AcceptTcpClientAsync();
        activity.Track(relay, relay.GetStream());
        activity.Disconnect(IPAddress.Parse("8.138.5.232"));
        Assert.True(relay.Connected);
        activity.Disconnect(IPAddress.Loopback);
        Assert.False(relay.Connected);
        Assert.True(direct.Connected);
        await direct.GetStream().WriteAsync(new byte[] { 37 });
        var received = new byte[1];
        await directPeer.GetStream().ReadExactlyAsync(received);
        Assert.Equal(37, received[0]);
    }

    [Fact]
    public async Task ValidControlFrameTouchesActivityButInvalidJsonDoesNot()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync();
        using var stream = new MemoryStream();
        RelayConnectionActivity.Shared.Track(client, stream);
        RelayConnectionActivity.Entry entry = RelayConnectionActivity.Shared.Find(stream)!;
        await RelayTls.WriteJsonAsync(stream, new { type = "heartbeat" }, default);
        stream.Position = 0;
        using JsonDocument parsed = await RelayTls.ReadJsonAsync(stream, default);
        Assert.True(entry.IsRecent(Environment.TickCount64));
        using var invalid = new MemoryStream(new byte[] { 0, 0, 0, 1, (byte)'!' });
        RelayConnectionActivity.Shared.Track(client, invalid);
        var invalidEntry = RelayConnectionActivity.Shared.Find(invalid)!;
        await Assert.ThrowsAsync<RelayProtocolException>(() => RelayTls.ReadJsonAsync(invalid, default));
        Assert.False(invalidEntry.IsRecent(Environment.TickCount64));
    }
}
