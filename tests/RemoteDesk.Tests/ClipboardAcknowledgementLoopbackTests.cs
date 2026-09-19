using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardAcknowledgementLoopbackTests
{
    [Fact]
    public async Task ClipboardReadCanWaitForReplyBeforeTheNextPasteStarts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keepAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "read-fixture", timeout.Token);
            using var session = auth.Session!;
            while (true)
            {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Control ||
                    RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind != RemoteControlKind.ClipboardGetText) continue;
                received.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
                // An empty reply exercises completion without touching the test runner's clipboard.
                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                    RemoteMessageCodec.EncodeClipboardText(""), session, writeLock, timeout.Token);
                await keepAlive.Task.WaitAsync(timeout.Token);
                return;
            }
        }, timeout.Token);
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "read-fixture", ViewerVideoMode.StableJpeg);
        Task<bool> read = viewer.ReadRemoteClipboardAndWaitAsync();
        await received.Task.WaitAsync(timeout.Token);
        Assert.False(read.IsCompleted);
        release.TrySetResult();
        Assert.False(await read.WaitAsync(timeout.Token));
        keepAlive.TrySetResult();
        await viewer.DisconnectAsync();
        await host;
    }

    [Fact]
    public async Task DelayedPasteBatchCannotEnterAnotherConnectionGeneration()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var delivered = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keepAlive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "generation-fixture", timeout.Token);
            using var session = auth.Session!;
            var keys = new List<int>();
            while (true)
            {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Input) continue;
                var input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
                keys.Add(input.Data);
                if (input.Data != 0x42 || input.Kind != RemoteInputKind.KeyUp) continue;
                delivered.TrySetResult(keys.ToArray());
                await keepAlive.Task.WaitAsync(timeout.Token);
                return;
            }
        }, timeout.Token);
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "generation-fixture", ViewerVideoMode.StableJpeg);
        long generation = viewer.InputConnectionGeneration;
        await viewer.SendInputsAsync([RemoteInputCommand.KeyDown(0x56), RemoteInputCommand.KeyUp(0x56)], generation - 1);
        await viewer.SendInputsAsync([RemoteInputCommand.KeyDown(0x42), RemoteInputCommand.KeyUp(0x42)], generation);
        await viewer.FlushInputAsync(timeout.Token);
        Assert.Equal(new[] { 0x42, 0x42 }, await delivered.Task.WaitAsync(timeout.Token));
        keepAlive.TrySetResult();
        await viewer.DisconnectAsync();
        await host;
    }

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
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
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
