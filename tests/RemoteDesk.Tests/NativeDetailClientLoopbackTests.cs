using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using RemoteDesk.NativeDetail;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailClientLoopbackTests
{
    private const string Password = "native-detail-isolated-loopback";

    [Fact]
    public async Task AuthenticatedRequestBasePatchAndChineseInputUseTheRealClientAndLifecycle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var release = NewCompletion<bool>();
        var inputSeen = NewCompletion<int[]>();
        var frames = Channel.CreateUnbounded<RemoteFrame>();
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var write = new SemaphoreSlim(1, 1);
            using var session = await Protocol.AuthenticateServerAsync(stream, Password, timeout.Token);
            Assert.NotNull(session);
            var info = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            Assert.Equal(RemoteControlKind.ViewerInfo, RemoteMessageCodec.DecodeControl(info.PayloadMemory).Kind);
            var capabilities = RemoteMessageCodec.DecodeControl((await Protocol.ReadMessageAsync(stream, session, timeout.Token)).PayloadMemory);
            Assert.True(capabilities.Capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1));
            Assert.True(capabilities.Capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo));
            await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(
                new("native-loopback", "Windows", RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.NativeDetailV1)),
                session, write, timeout.Token);
            var first = await ReadRequest(stream, session, [], timeout.Token);
            DetailManifest manifest = new(first.Context, 1, [1, 1]);
            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                NativeDetailSessionProtocol.EncodeBase(manifest, NativeDetailSessionTests.BaseFrame()), session, write, timeout.Token);
            var transfer = DetailTransfer.Encode(manifest, 0, Enumerable.Repeat((byte)29, 128 * 128 * 4).ToArray());
            await Protocol.WriteMessageAsync(stream, MessageType.NativeDetailChunk, transfer.Chunk(0), session, write, timeout.Token);
            var inputs = new List<int>();
            var next = await ReadRequest(stream, session, inputs, timeout.Token);
            inputSeen.TrySetResult(inputs.ToArray());
            Assert.Equal(first.Context.Epoch, next.Context.Epoch);
            Assert.True(next.Context.Request > first.Context.Request);
            // While the new request is in flight, allow the old base WITHOUT
            // details. Stop that grace as soon as the new base is observed.
            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                NativeDetailSessionProtocol.EncodeBase(new(first.Context, 50, [1, 1]), NativeDetailSessionTests.BaseFrame()), session, write, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                NativeDetailSessionProtocol.EncodeBase(new(next.Context, 1, [1, 1]), NativeDetailSessionTests.BaseFrame()), session, write, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                NativeDetailSessionProtocol.EncodeBase(new(first.Context, 51, [1, 1]), NativeDetailSessionTests.BaseFrame()), session, write, timeout.Token);
            await release.Task.WaitAsync(timeout.Token);
        }
        Task host = Host();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true) { EnableNativeDetailReception = true };
        client.FrameReceived += frame => frames.Writer.TryWrite(frame);
        Exception? testFailure = null;
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Password, ViewerVideoMode.Automatic, timeout.Token);
            Assert.True(await client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128)));
            RemoteFrame first = await frames.Reader.ReadAsync(timeout.Token);
            Assert.NotNull(first.NativeDetails);
            while (client.NativeDetails.LastPresentedSequence == 0)
            {
                // Refusing a busy cache is intentional: production renders
                // the base immediately; the harness retries its presentation.
                client.NativeDetails.TryPresent(first.NativeDetails.BindDecoderSample(100), 100,
                    new(System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency));
                await Task.Delay(1, timeout.Token);
            }
            while (client.NativeDetails.AppliedChunks != 1) await Task.Delay(5, timeout.Token);
            Assert.Equal(3, client.SendTextInput("中文😀").SentCodePoints);
            Assert.False(client.NativeDetails.IsEnabled);
            Assert.True(await client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128)));
            RemoteFrame graceBase = await frames.Reader.ReadAsync(timeout.Token);
            Assert.Null(graceBase.NativeDetails);
            RemoteFrame second = await frames.Reader.ReadAsync(timeout.Token);
            Assert.NotNull(second.NativeDetails);
            Assert.Equal(1, second.NativeDetails.Manifest.Sequence);
            Assert.True(second.NativeDetails.Manifest.Context.Request > first.NativeDetails.Manifest.Context.Request);
            Assert.Equal(new[] { (int)'中', (int)'文', 0x1F600 }, await inputSeen.Task.WaitAsync(timeout.Token));
            Assert.False(frames.Reader.TryRead(out _));
            await client.DisconnectAsync();
            Assert.False(client.NativeDetails.IsEnabled);
        }
        catch (Exception ex) { testFailure = ex; throw; }
        finally
        {
            release.TrySetResult(true);
            await client.DisconnectAsync().WaitAsync(timeout.Token);
            try { await host.WaitAsync(timeout.Token); }
            catch when (testFailure is not null) { /* Preserve the original assertion, not cleanup EOF. */ }
        }
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public async Task UnnegotiatedPeersNeverGetNewRequestsOrUnsolicitedNativeFrames(bool optIn, bool hostSupport)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var release = NewCompletion<bool>();
        var received = NewCompletion<RemoteFrame>();
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var write = new SemaphoreSlim(1, 1);
            using var session = await Protocol.AuthenticateServerAsync(stream, Password, timeout.Token);
            Assert.NotNull(session);
            await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            var capabilities = RemoteMessageCodec.DecodeControl((await Protocol.ReadMessageAsync(stream, session, timeout.Token)).PayloadMemory);
            Assert.Equal(optIn, capabilities.Capabilities.HasFlag(RemoteDeviceCapabilities.NativeDetailV1));
            await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(
                new("old-or-unrequested", "Linux", hostSupport ? RemoteDeviceCapabilities.NativeDetailV1 : RemoteDeviceCapabilities.None)),
                session, write, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.NativeVideoFrame,
                NativeDetailSessionProtocol.EncodeBase(NativeDetailSessionTests.Manifest(), NativeDetailSessionTests.BaseFrame()), session, write, timeout.Token);
            byte[] legacy = new byte[RemoteMessageCodec.VideoFrameHeaderLength + 1];
            RemoteMessageCodec.WriteVideoFrameHeader(legacy, 99, 77, RemoteFrameEncoding.Jpeg, RemoteFrameFlags.KeyFrame, 0, 0);
            await Protocol.WriteMessageAsync(stream, MessageType.VideoFrame, legacy, session, write, timeout.Token);
            await release.Task.WaitAsync(timeout.Token);
            while (peer.Available > 0)
                Assert.NotEqual(MessageType.NativeDetailRequest,
                    (await Protocol.ReadMessageAsync(stream, session, timeout.Token)).Type);
        }
        Task host = Host();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true) { EnableNativeDetailReception = optIn };
        client.FrameReceived += frame => received.TrySetResult(frame);
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Password,
                ViewerVideoMode.Automatic, timeout.Token);
            Assert.False(await client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128)));
            RemoteFrame frame = await received.Task.WaitAsync(timeout.Token);
            Assert.Equal(99, frame.Width);
            Assert.Null(frame.NativeDetails);
            Assert.False(client.NativeDetails.IsEnabled);
        }
        finally
        {
            release.TrySetResult(true);
            await host.WaitAsync(timeout.Token);
            await client.DisconnectAsync().WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task LocalOffIsImmediateEvenWhenNoNetworkRequestCanBeSent()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true) { EnableNativeDetailReception = true };
        client.NativeDetails.BeginRequest(NativeDetailSessionTests.Request());
        Assert.False(await client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128), enabled: false));
        Assert.False(client.NativeDetails.IsEnabled);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StopSurvivesRevokedAvailabilityAndSupersedesAnEnableWaitingForTheWriter(bool queuedEnable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var revoke = NewCompletion<bool>();
        var stopped = NewCompletion<NativeDetailRequest>();
        var release = NewCompletion<bool>();
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = peer.GetStream();
            using var write = new SemaphoreSlim(1, 1);
            using var session = await Protocol.AuthenticateServerAsync(stream, Password, timeout.Token);
            Assert.NotNull(session);
            await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            await Protocol.ReadMessageAsync(stream, session, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(
                new("stop-test", "Windows", RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.NativeDetailV1)),
                session, write, timeout.Token);
            await Protocol.WriteMessageAsync(stream, MessageType.NativeDetailOffer,
                NativeDetailSessionProtocol.EncodeOffer(new(1, new(256, 128), true)), session, write, timeout.Token);
            if (!queuedEnable) Assert.True((await ReadRequest(stream, session, [], timeout.Token)).Enabled);
            await revoke.Task.WaitAsync(timeout.Token);
            // A failed target may offer a placeholder size, but stop must
            // retain the old request's real dimensions and advancing context.
            await Protocol.WriteMessageAsync(stream, MessageType.NativeDetailOffer,
                NativeDetailSessionProtocol.EncodeOffer(new(2, new(48, 48), false)), session, write, timeout.Token);
            stopped.TrySetResult(await ReadRequest(stream, session, [], timeout.Token));
            await release.Task.WaitAsync(timeout.Token);
        }
        Task host = Host();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Password,
                ViewerVideoMode.Automatic, timeout.Token);
            while (client.NativeOffer is not { Available: true }) await Task.Delay(5, timeout.Token);
            if (!queuedEnable) Assert.True(await client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128)));
            revoke.TrySetResult(true);
            while (client.NativeOffer is not { Available: false }) await Task.Delay(5, timeout.Token);
            if (queuedEnable)
            {
                var gate = (SemaphoreSlim)typeof(RemoteViewerClient).GetField("_nativeRequestLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
                await gate.WaitAsync(timeout.Token);
                Task<bool> enable = client.RequestNativeDetailsAsync(new(256, 128), new(0, 0, 256, 128));
                Task<bool> stop = client.StopNativeDetailsAsync();
                gate.Release();
                Assert.False(await enable.WaitAsync(timeout.Token));
                Assert.True(await stop.WaitAsync(timeout.Token));
            }
            else Assert.True(await client.StopNativeDetailsAsync());
            NativeDetailRequest request = await stopped.Task.WaitAsync(timeout.Token);
            Assert.False(request.Enabled);
            Assert.Equal(queuedEnable ? 48 : 256, request.Context.Width);
            Assert.Equal(queuedEnable ? 48 : 128, request.Context.Height);
            Assert.False(client.NativeDetails.IsEnabled);
        }
        finally
        {
            revoke.TrySetResult(true); release.TrySetResult(true);
            await client.DisconnectAsync();
            await host.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public void LateTcpCallbackFromRetiredConnectionCannotPublishLegacyOrNativeFrames()
    {
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var retired = new CancellationTokenSource();
        int published = 0;
        client.FrameReceived += _ => published++;
        MethodInfo publish = typeof(RemoteViewerClient).GetMethod("PublishTcpVideoFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        publish.Invoke(client, [retired, NativeDetailSessionTests.BaseFrame(), false]);
        publish.Invoke(client, [retired, NativeDetailSessionTests.BaseFrame(), true]);
        Assert.Equal(0, published);
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<NativeDetailRequest> ReadRequest(NetworkStream stream, SecureSession session,
        List<int> inputs, CancellationToken cancellation)
    {
        while (true)
        {
            var message = await Protocol.ReadMessageAsync(stream, session, cancellation);
            if (message.Type == MessageType.NativeDetailRequest)
                return NativeDetailSessionProtocol.DecodeRequest(message.PayloadSpan);
            if (message.Type == MessageType.NativeDetailFeedback)
            { _ = NativeDetailSessionProtocol.DecodeFeedback(message.PayloadSpan); continue; }
            Assert.Equal(MessageType.Input, message.Type);
            var input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
            Assert.Equal(RemoteInputKind.TextInput, input.Kind);
            inputs.Add(input.Data);
        }
    }
}
