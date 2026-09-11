using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardAcknowledgementLoopbackTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClipboardWriteWaitsForAuthenticatedRemoteAcknowledgement(bool accepted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string fixturePassword = "clipboard-fixture-only";
        const string text = "中文😀\r\n第二行\t缩进";
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keepAlive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using NetworkStream stream = peer.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, fixturePassword, timeout.Token);
            Assert.True(auth.IsAuthenticated);
            using var session = auth.Session!;
            while (true)
            {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Control) continue;
                var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                if (control.Kind != RemoteControlKind.ClipboardSetText) continue;
                Assert.Equal(text, control.Text);
                received.TrySetResult(true);
                await release.Task.WaitAsync(timeout.Token);
                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                    RemoteMessageCodec.EncodeClipboardStatus(accepted, accepted ? "Updated" : "Unavailable"),
                    session, writeLock, timeout.Token);
                await keepAlive.Task.WaitAsync(timeout.Token);
                return;
            }
        }, timeout.Token);
        using var viewer = new RemoteViewerClient();
        await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            fixturePassword, ViewerVideoMode.StableJpeg);
        Task<bool> sent = viewer.SendClipboardTextToRemoteAsync(text);
        await received.Task.WaitAsync(timeout.Token);
        Assert.False(sent.IsCompleted);
        release.TrySetResult(true);
        Assert.Equal(accepted, await sent.WaitAsync(timeout.Token));
        keepAlive.TrySetResult(true);
        await viewer.DisconnectAsync();
        await host;
    }
}
