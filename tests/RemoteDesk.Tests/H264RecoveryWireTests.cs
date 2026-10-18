using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class H264RecoveryWireTests
{
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
