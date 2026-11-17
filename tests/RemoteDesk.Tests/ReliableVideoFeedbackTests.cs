using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ReliableVideoFeedbackTests
{
    [Fact]
    public void MissingReceiptsBoundInFlightDataWithoutQueuingOrChangingQuality()
    {
        var window = new ReliableVideoSendWindow();
        Assert.True(window.TryReserve(65536, 1, out var first));
        Assert.True(window.TryReserve(65536, 2, out var second));
        for (int i = 0; i < 10_000; i++) Assert.False(window.TryReserve(65536, i + 3, out _));
        Assert.Equal(1, first); Assert.Equal(2, second);
        Assert.Equal(2, window.PendingFrames); Assert.Equal(131072, window.PendingBytes);
        Assert.Equal(10_000, window.DroppedFrames);
        Assert.True(window.Observe(new(second, 131072, 20), 10025));
        Assert.True(window.TryReserve(65536, 10026, out var next));
        Assert.Equal(3, next);
    }

    [Fact]
    public void BootstrapCountIsBoundedEvenWhenLaterFramesAreMuchSmaller()
    {
        var window = new ReliableVideoSendWindow();
        Assert.True(window.TryReserve(1_000_000, 1, out _));
        Assert.True(window.TryReserve(1000, 2, out _));
        Assert.False(window.TryReserve(1000, 3, out _));
        Assert.Equal(2, window.PendingFrames);
    }

    [Fact]
    public void ReceiptsMustExactlyMatchAnOutstandingAuthenticatedRecord()
    {
        var window = new ReliableVideoSendWindow();
        Assert.True(window.TryReserve(10000, 100, out _));
        Assert.True(window.TryReserve(10000, 110, out _));
        foreach (var receipt in new[] {new ReliableVideoReceipt(99, 20000), new(2, 30000), new(1, 20000),
            new(2, 19999), new(2, 20000, 501), new(2, 20000, 400)})
            Assert.False(window.Observe(receipt, 300));
        Assert.Equal(20000, window.PendingBytes);
        Assert.True(window.Observe(new(2, 20000, 20), 330));
        Assert.Equal(200, window.LastRoundTripMilliseconds);
        Assert.Equal(0, window.PendingFrames);
        Assert.False(window.Observe(new(1, 10000), 400));
        Assert.False(window.Observe(new(2, 20000), 400));
    }

    [Fact]
    public void AgingOutstandingFramesStopFurtherAdmission()
    {
        var window = new ReliableVideoSendWindow();
        window.TryReserve(1000, 100, out _);
        Assert.True(window.Observe(new(1, 1000), 200));
        Assert.True(window.TryReserve(1000, 201, out _));
        Assert.False(window.TryReserve(1000, 362, out _));
        Assert.True(window.Observe(new(2, 2000), 400));
        Assert.True(window.TryReserve(1000, 401, out _));
    }

    [Fact]
    public void LargerRecoveryFrameDoesNotDeadlockASmallerCreditWindow()
    {
        var window = new ReliableVideoSendWindow();
        window.TryReserve(1000, 1, out _);
        window.Observe(new(1, 1000), 10);
        Assert.True(window.TryReserve(2_000_000, 11, out _));
        Assert.False(window.TryReserve(1000, 12, out _));
        Assert.True(window.Observe(new(2, 2_001_000), 500));
        Assert.True(window.TryReserve(1000, 501, out _));
    }

    [Fact]
    public void SceneChangeWaitsForPriorSmallFramesBeforeSendingOneOversizedFrame()
    {
        var window = new ReliableVideoSendWindow();
        Assert.True(window.TryReserve(1000, 1, out _));
        Assert.False(window.TryReserve(2_000_000, 2, out _));
        Assert.True(window.Observe(new(1, 1000), 100));
        Assert.True(window.TryReserve(2_000_000, 101, out _));
        Assert.False(window.TryReserve(2_000_000, 200, out _));
    }

    [Fact]
    public void ReceiptBurstDoesNotCreateASendBurstOrLeaveAFrameWaiting()
    {
        var window = new ReliableVideoSendWindow();
        Assert.True(window.TryReserve(1000, 100, out _));
        Assert.True(window.TryReserve(1000, 110, out _));
        Assert.True(window.Observe(new(2, 2000), 510));
        Assert.True(window.TryReserve(1000, 511, out _));
        Assert.False(window.TryReserve(1000, 512, out _));
        Assert.True(window.TryReserve(1000, 700, out var sequence));
        Assert.Equal(4, sequence); // The skipped candidate never enters the wire sequence.
    }

    [Fact]
    public void HardByteLimitCannotBeExceededByRoundingSoftCredit()
    {
        var window = new ReliableVideoSendWindow();
        int frameBytes = ReliableVideoSendWindow.MaximumWindowBytes / 2 + 1;
        Assert.True(window.TryReserve(frameBytes, 1, out _));
        Assert.False(window.TryReserve(frameBytes, 2, out _));
        Assert.Equal(frameBytes, window.PendingBytes);
    }

    [Fact]
    public void SlowerRouteRelearnsOnlyAfterThreeTransmissionsWithNoOtherVideoOutstanding()
    {
        var window = new ReliableVideoSendWindow();
        window.TryReserve(1000, 1, out _);
        window.Observe(new(1, 1000), 101);
        for (int i = 2; i <= 4; i++)
        {
            long sent = i * 1000;
            Assert.True(window.TryReserve(1000, sent, out _));
            Assert.True(window.Observe(new(i, i * 1000), sent + 500));
        }
        Assert.True(window.TryReserve(500, 5000, out _));
        // Old 100 ms baseline would wrongly refuse the second frame here.
        Assert.True(window.TryReserve(500, 5200, out _));
    }

    [Fact]
    public void WireReceiptValidatesLengthVersionAndReservedFields()
    {
        var expected = new ReliableVideoReceipt(42, 123456, 20);
        byte[] bytes = ReliableVideoFeedbackProtocol.EncodeReceipt(expected);
        Assert.Equal(expected, ReliableVideoFeedbackProtocol.DecodeReceipt(bytes));
        Assert.Throws<InvalidDataException>(() => ReliableVideoFeedbackProtocol.DecodeReceipt(bytes[..^1]));
        foreach (int index in new[] {0, 4, 28})
        {
            byte[] corrupt = (byte[])bytes.Clone(); corrupt[index] ^= 1;
            Assert.Throws<InvalidDataException>(() => ReliableVideoFeedbackProtocol.DecodeReceipt(corrupt));
        }
        Assert.Throws<InvalidDataException>(() => ReliableVideoFeedbackProtocol.EncodeReceipt(new(0, 123)));
    }

    [Fact]
    public void SignedCapabilityBitPreservesOldCapabilitiesAndRoundTrips()
    {
        var caps = RemoteDeviceCapabilities.ReliableVideoFeedback | RemoteDeviceCapabilities.ClipboardText |
            RemoteDeviceCapabilities.PrivacyScreen | RemoteDeviceCapabilities.LowLatencyUdpVideo;
        Assert.True((int)caps < 0);
        Assert.Equal(caps, RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeViewerCapabilities(caps)).Capabilities);
        var host = new RemoteDeviceDescriptor("fixture", "Windows", caps);
        Assert.Equal(caps, RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeDeviceInfo(host)).Capabilities);
        Assert.True(caps.HasFlag(RemoteDeviceCapabilities.ClipboardText));
        Assert.False(caps.HasFlag(RemoteDeviceCapabilities.ShortGopH264));
    }

    [Fact]
    public async Task ActualViewerAcknowledgesTrackedFramesAndResetsOnReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        int frames = 0;
        viewer.FrameReceived += frame => { Assert.Equal(RemoteFrameEncoding.H264AnnexB, frame.Encoding); Interlocked.Increment(ref frames); };
        for (int round = 0; round < 2; round++)
        {
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Host()
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = peer.GetStream();
                using var session = await Protocol.AuthenticateServerAsync(stream, "owned-feedback-fixture", timeout.Token);
                Assert.NotNull(session);
                using var gate = new SemaphoreSlim(1, 1);
                await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceInfo(new("fixture", "Windows",
                    RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.ReliableVideoFeedback)), session, gate, timeout.Token);
                bool ready = false;
                while (!ready)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                    if (message.Type != MessageType.Control) continue;
                    var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                    if (control.Kind == RemoteControlKind.ViewerCapabilities)
                        ready = control.Capabilities.HasFlag(RemoteDeviceCapabilities.ReliableVideoFeedback);
                }
                byte[] body = new byte[1000]; body[3] = 1; body[4] = 0x65;
                for (int n = 1; n <= 2; n++)
                    await Protocol.WriteTrackedVideoFrameAsync(stream, n, 640, 480, RemoteFrameFlags.KeyFrame,
                        0, 0, body, session, gate, timeout.Token);
                ReliableVideoReceipt? receipt = null;
                while (receipt?.Sequence != 2)
                {
                    var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                    if (message.Type == MessageType.ReliableVideoFeedback)
                        receipt = ReliableVideoFeedbackProtocol.DecodeReceipt(message.PayloadSpan);
                }
                Assert.Equal(2 * (1000 + ReliableVideoFeedbackProtocol.FrameOverhead), receipt.WireBytes);
                await complete.Task.WaitAsync(timeout.Token);
            }
            Task host = Host();
            try
            {
                await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                    "owned-feedback-fixture", ViewerVideoMode.Automatic, timeout.Token);
                while (Volatile.Read(ref frames) < (round + 1) * 2) await Task.Delay(10, timeout.Token);
                // Keep the connection alive long enough to exercise the coalescing ACK writer.
                await Task.Delay(100, timeout.Token);
                complete.TrySetResult(); await host;
            }
            finally { complete.TrySetResult(); await viewer.DisconnectAsync(); }
        }
        Assert.Equal(4, frames);
    }
}
