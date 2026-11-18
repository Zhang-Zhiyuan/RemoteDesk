using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

public sealed class H264RecoveryWireTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SilentHealthyConnectionRefreshesAfterRealInputWithoutReconnectOrJpeg()
    {
        bool soak = int.TryParse(Environment.GetEnvironmentVariable("REMOTEDESK_IDLE_SOAK_SECONDS"), out int seconds);
        int idleSeconds = soak ? Math.Clamp(seconds, 5, 3600) : 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(idleSeconds + 25));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string key = "idle-wire-fixture";
        // Two independent 16x16 Annex B frames, blue and green. No desktop or OS input is used.
        var blue = Convert.FromBase64String("AAAAAWdCwArd7ARAAAADAEAAAAMAo8SJ4AAAAAFozg/IAAABZYiEOhGKAAIxccAAQ8o4AAgF4A==");
        var green = Convert.FromBase64String("AAAAAWdCwArd7ARAAAADAEAAAAMAo8SJ4AAAAAFozg/IAAABZYiEOhGKAAIGUcAAQlY4AAhj4A==");
        int requests = 0, pings = 0, input = 0;
        var received = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        async Task Host()
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            using var session = await Protocol.AuthenticateServerAsync(stream, key, timeout.Token);
            Assert.NotNull(session);
            using var write = new SemaphoreSlim(1, 1);
            Task Frame(byte[] accessUnit) => Protocol.WriteVideoFrameMessageAsync(stream, 16, 16,
                RemoteFrameEncoding.H264AnnexB, RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
                0, 0, accessUnit, session, write, timeout.Token);
            await Frame(blue);
            while (!timeout.IsCancellationRequested)
            {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type == MessageType.Ping)
                {
                    await Protocol.WriteMessageAsync(stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty, session, write, timeout.Token);
                    Interlocked.Increment(ref pings);
                }
                if (message.Type == MessageType.Input) Interlocked.Increment(ref input);
                if (message.Type == MessageType.Control && RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind == RemoteControlKind.VideoKeyFrameRequest)
                {
                    // Deliberately lose the first refresh at the capture source, not the TCP stream.
                    if (Interlocked.Increment(ref requests) == 2) await Frame(green);
                }
            }
        }
        Task host = Host();
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        viewer.FrameReceived += f => { if (f.Encoding == RemoteFrameEncoding.H264AnnexB) received.Writer.TryWrite(f.EncodedBuffer.AsSpan(f.EncodedOffset, f.EncodedLength).ToArray()); };
        try
        {
            await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, key, ViewerVideoMode.Automatic, timeout.Token);
            using var decoder = soak ? FfmpegH264Decoder.TryCreate(new Size(16, 16)) : null;
            async Task CheckPicture(int id, byte[] expected)
            {
                byte[] bytes = await received.Reader.ReadAsync(timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(9));
                Assert.Equal(expected, bytes);
                if (!soak) return;
                Assert.NotNull(decoder);
                var result = await decoder.DecodeAsync(id, bytes).WaitAsync(timeout.Token);
                using var picture = result.Bitmap;
                Assert.NotNull(picture);
                var pixel = picture.GetPixel(8, 8);
                Assert.True(id == 1 ? pixel.B > 200 && pixel.G < 50 : pixel.G > 200 && pixel.B < 50, $"Unexpected pixel {pixel}");
            }
            await CheckPicture(1, blue);
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds), timeout.Token);
            while (Volatile.Read(ref pings) == 0) await Task.Delay(30, timeout.Token);
            Assert.Equal(0, Volatile.Read(ref requests)); // Heartbeats alone do not restart capture.
            var resume = Stopwatch.StartNew();
            await viewer.SendInputAsync(RemoteInputCommand.MouseWheel(120, 1, 1));
            await CheckPicture(2, green);
            Assert.Equal(1, Volatile.Read(ref input)); // No synthetic key or repeated gesture.
            Assert.Equal(2, Volatile.Read(ref requests));
            Assert.True(viewer.IsConnected);
            output.WriteLine($"Idle {idleSeconds}s, {pings} pings, {requests} recovery requests, refreshed in {resume.Elapsed.TotalSeconds:F2}s, decoded pixels verified: {soak}.");
        }
        finally
        {
            timeout.Cancel();
            await viewer.DisconnectAsync();
            try { await host; } catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }
    }

    [Fact]
    public async Task DecoderRecoveryRequestsKeyFramesWithoutRenegotiatingToJpeg()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var controls = new ConcurrentQueue<RemoteControlMessage>();
        var sawRecoveryAndBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Host()
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            using var session = await Protocol.AuthenticateServerAsync(stream, "recovery-fixture", timeout.Token);
            Assert.NotNull(session);
            int requests = 0;
            while (requests < 2)
            {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Control) continue;
                var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                controls.Enqueue(control);
                if (control.Kind == RemoteControlKind.VideoKeyFrameRequest) requests++;
            }
            sawRecoveryAndBarrier.TrySetResult();
            await releaseHost.Task.WaitAsync(timeout.Token);
        }
        Task host = Host();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWindow = new ManualResetEventSlim();
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                "recovery-fixture", ViewerVideoMode.Automatic, timeout.Token);
            // Keep initial host JPEG previews supported without requesting a
            // switch away from the established H.264 stream.
            await client.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB);
            var thread = new Thread(() =>
            {
                try
                {
                    // Hidden fixture: no OS input, screen capture or clipboard.
                    using var window = new RemoteViewerWindow(client, "recovery-wire", false, false, false, false, false, false);
                    typeof(RemoteViewerWindow).GetMethod("ScheduleH264DecoderRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, ["H.264 解码失步 test"]);
                    scheduled.TrySetResult();
                    releaseWindow.Wait(timeout.Token);
                    disposed.TrySetResult();
                }
                catch (Exception error) { scheduled.TrySetException(error); disposed.TrySetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await scheduled.Task.WaitAsync(timeout.Token);
            await client.RequestVideoKeyFrameAsync();
            await sawRecoveryAndBarrier.Task.WaitAsync(timeout.Token);
            Assert.True(client.IsConnected);
            var advertisements = controls.Where(value => value.Kind == RemoteControlKind.ViewerInfo).ToArray();
            Assert.NotEmpty(advertisements);
            // After the explicit initial advertisement, recovery is key-frame
            // requests only, never a JPEG-only ViewerInfo.
            int establishedIndex = Array.FindIndex(controls.ToArray(), value =>
                value.Kind == RemoteControlKind.ViewerInfo && value.SupportedVideoCodecs.HasFlag(RemoteVideoCodecs.H264AnnexB));
            Assert.True(establishedIndex >= 0);
            Assert.DoesNotContain(controls.Skip(establishedIndex), value =>
                value.Kind == RemoteControlKind.ViewerInfo && !value.SupportedVideoCodecs.HasFlag(RemoteVideoCodecs.H264AnnexB));
        }
        finally
        {
            releaseWindow.Set();
            releaseHost.TrySetResult();
            if (scheduled.Task.IsCompleted) await disposed.Task.WaitAsync(timeout.Token);
            await client.DisconnectAsync();
            await host.WaitAsync(timeout.Token);
        }
    }
}
