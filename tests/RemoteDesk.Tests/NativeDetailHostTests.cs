using System.Drawing;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.NativeDetail;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailHostTests
{
    private static readonly DetailContext Context = new(1, 1, 3840, 2160);
    [Theory]
    [InlineData(3840, 2160, 1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 3840, 2160, 3840, 2160)]
    [InlineData(1920, 1080, 3840, 2160, 1920, 1080)]
    [InlineData(1920, 1080, 0, 0, 1920, 1080)]
    [InlineData(3840, 2160, 1080, 1920, 3840, 2160)]
    public void NativeOptInPreservesAnAlreadyReducedStreamWithoutUpsizingOrUsingAnotherAspect(
        int width, int height, int observedWidth, int observedHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight), RemoteHostServer.ChooseNativeCaptureOutputSize(
            new(width, height), new(observedWidth, observedHeight)));
    }

    [Fact]
    public void OfferAndFeedbackRoundTripAndRejectInvalidBounds()
    {
        var offer = new NativeDetailOffer(7, new(3840, 2160), true);
        Assert.Equal(offer, NativeDetailSessionProtocol.DecodeOffer(NativeDetailSessionProtocol.EncodeOffer(offer)));
        var ack = new NativeDetailFeedback(Context, 10, 9, 10000);
        Assert.Equal(ack, NativeDetailSessionProtocol.DecodeFeedback(NativeDetailSessionProtocol.EncodeFeedback(ack)));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeFeedback(ack with { PresentedSequence = 11 }));
        byte[] bytes = NativeDetailSessionProtocol.EncodeOffer(offer); bytes[23] = 1;
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeOffer(bytes));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeOffer(offer with { NativeSize = new(8192, 8192) }));
    }

    [Fact]
    public void LinkDoesNotConfuseFastLocalWritesWithEndToEndBandwidth()
    {
        var link = new NativeDetailLinkBudget();
        Assert.Equal(0, link.DetailBitsPerSecond(0));
        for (int sequence = 1; sequence <= 4; sequence++)
        {
            Assert.True(link.CanSendBase(sequence));
            link.RecordSent(Context, sequence, 1000, sequence);
            Assert.Equal(0, link.DetailBitsPerSecond(sequence));
        }
        Assert.False(link.CanSendBase(5));
        Assert.False(link.Observe(new(Context, 4, 4, 4001), 40));
        Assert.False(link.Observe(new(Context with { Request = 2 }, 4, 4, 4000), 40));
        Assert.True(link.Observe(new(Context, 4, 4, 4000), 40));
        Assert.True(link.CanSendBase(41));
        Assert.False(link.Observe(new(Context, 4, 4, 4000), 45));
    }

    [Fact]
    public void OptionalTrafficRequiresHealthyDeliveryAndStopsOnAckSilenceOrRenderBacklog()
    {
        var link = HealthyLink();
        Assert.InRange(link.DetailBitsPerSecond(700), 1, 1_000_000);
        Assert.Equal(0, link.DetailBitsPerSecond(1000));
        link.RecordSent(Context, 50, 500_000, 710);
        Assert.True(link.Observe(new(Context, 50, 4, link.SentBytes), 730));
        Assert.Equal(0, link.DetailBitsPerSecond(730));
    }

    [Fact]
    public void SlowLinkAndInflatedAckBurstsNeverGrantDetail()
    {
        var link = new NativeDetailLinkBudget();
        for (int i = 1; i <= 8; i++)
        {
            link.RecordSent(Context, i, 1000, i * 100);
            Assert.True(link.Observe(new(Context, i, i, link.SentBytes), i * 100 + 20));
        }
        Assert.Equal(0, link.DetailBitsPerSecond(830));
        link.RecordSent(Context, 9, 500_000, 831);
        Assert.True(link.Observe(new(Context, 9, 9, link.SentBytes), 832));
        // One burst is not a link capacity measurement.
        Assert.Equal(0, link.DetailBitsPerSecond(832));
    }

    [Fact]
    public void RestartSeedAdvancesEveryVersionIncludingFirstEmptyDamage()
    {
        var source = new NativeSurfaceDamageTracker(new(256, 128), initialSequence: 100);
        var first = source.Observe([]);
        Assert.Equal(101, first.Sequence); Assert.Equal(101, first[0]); Assert.Equal(101, first[1]);
        var second = source.Observe([]);
        Assert.Equal(102, second.Sequence); Assert.Equal(101, second[0]);
    }

    [Theory]
    [InlineData(3840, 2160, 3839, 2159)] [InlineData(257, 259, 256, 258)]
    [InlineData(8192, 1080, 0, 0)] [InlineData(48, 48, -5, 1000)]
    public void ViewportIsAlignedBoundedAndNeverRequestsMoreThanCache(int width, int height, int x, int y)
    {
        Rectangle viewport = RemoteViewerWindow.CalculateNativeDetailViewport(new(width, height), new(x, y));
        Assert.True(new Rectangle(0, 0, width, height).Contains(viewport));
        Assert.Equal(0, viewport.X % 128); Assert.Equal(0, viewport.Y % 128);
        var request = new NativeDetailRequest(new(1, 1, width, height),
            new(viewport.X, viewport.Y, viewport.Width, viewport.Height), true);
        Assert.InRange(NativeDetailCaptureWorker.SelectTiles(request).Length, 1, 64);
    }

    [Fact]
    public async Task HostRequestLifecycleRejectsReplayButNeverBlocksOnHardwareInitialization()
    {
        var activity = new RemoteInteractionActivity();
        await using var host = new NativeDetailHostSession(() => { }, activity, _ => { });
        var request = new NativeDetailRequest(Context, new(0, 0, 1024, 1024), true);
        Assert.True(host.Accept(request)); Assert.False(host.Accept(request));
        Assert.True(host.Accept(request with { Context = Context with { Request = 2 }, Enabled = false }));
        Assert.Throws<InvalidDataException>(() => host.Accept(request with { Context = Context with { Epoch = 0 } }));
    }

    [Fact]
    public void SmallFragmentsRoundTripOutOfOrderWithoutMixingTheLargeFragmentLayout()
    {
        var context = new DetailContext(1, 1, 128, 128);
        var manifest = new DetailManifest(context, 1, [1]);
        byte[] pixels = new byte[128 * 128 * 4]; new Random(715).NextBytes(pixels);
        var transfer = DetailTransfer.Encode(manifest, 0, pixels);
        var cache = new DetailCache(); cache.Reset(context, new(0, 0, 128, 128)); cache.Present(manifest);
        Assert.Equal(DetailReceiveResult.Partial, cache.Receive(transfer.Chunk(0, 512), 0));
        Assert.Equal(DetailReceiveResult.Invalid, cache.Receive(transfer.Chunk(0), 1));
        int count = (transfer.Encoded.Length + 511) / 512;
        for (int index = count - 1; index >= 1; index--)
        {
            byte[] chunk = transfer.Chunk(index * 512, 512);
            Assert.InRange(chunk.Length + DetailWire.OuterRecordBytes, 122, NativeDetailLinkBudget.SmallChunkWireBytes);
            Assert.Equal(index == 1 ? DetailReceiveResult.Applied : DetailReceiveResult.Partial, cache.Receive(chunk, 2));
        }
        Assert.Equal(pixels, Assert.Single(cache.Patches).Rgba.ToArray());
        Assert.Throws<InvalidDataException>(() => transfer.Chunk(1, 512));
        Assert.Throws<InvalidDataException>(() => transfer.Chunk(0, 513));
    }

    [Fact]
    public void RetiredCaptureAckCannotBlockBootstrapOrGrantAnUnmeasuredNewRate()
    {
        var link = new NativeDetailLinkBudget();
        for (int i = 1; i <= 4; i++) link.RecordSent(Context, i, 800_000, i);
        Assert.False(link.CanSendBase(20)); Assert.True(link.FeedbackStalled(2000));
        link.BeginCapture();
        Assert.True(link.CanSendBase(2100));
        Assert.False(link.Observe(new(Context, 4, 4, 3_200_000), 2101));
        link.RecordSent(Context, 10, 20_000, 2102);
        Assert.True(link.Observe(new(Context, 10, 10, 3_220_000), 2122));
        Assert.Equal(0, link.DetailBitsPerSecond(2123));
    }

    [Fact]
    public void FeedbackCoalescingDelayAndTileMaskAreStrictAndDoNotInventRtt()
    {
        var feedback = new NativeDetailFeedback(Context, 9, 8, 90_000, ulong.MaxValue, 30);
        Assert.Equal(feedback, NativeDetailSessionProtocol.DecodeFeedback(NativeDetailSessionProtocol.EncodeFeedback(feedback)));
        foreach (int delay in new[] { -1, 501 })
            Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeFeedback(feedback with { AcknowledgementDelayMilliseconds = delay }));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeFeedback(feedback with { PresentedSequence = 0 }));
        byte[] packet = NativeDetailSessionProtocol.EncodeFeedback(feedback); packet[68] = 1;
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeFeedback(packet));
        var link = new NativeDetailLinkBudget(); link.RecordSent(Context, 9, 90_000, 100);
        Assert.False(link.Observe(feedback, 120)); // peer claims 30 ms delay after only 20 ms elapsed
        Assert.Equal(0, link.AcknowledgedBytes);
        Assert.True(link.Observe(feedback, 140));
        Assert.Equal(0, link.DetailBitsPerSecond(141)); // one RTT is not a capacity measurement
    }

    [Fact]
    public async Task OptionalFragmentDoesNotWaitBehindControlOrConsumeANonceWhenDeclined()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token).AsTask();
        using var host = await listener.AcceptTcpClientAsync(timeout.Token); await connect;
        using var send = new SecureSession(new byte[32], Enumerable.Repeat((byte)7, 32).ToArray(), isServer: true);
        using var receive = new SecureSession(new byte[32], Enumerable.Repeat((byte)7, 32).ToArray(), isServer: false);
        using var gate = new SemaphoreSlim(0, 1);
        int recorded = 0;
        Task<bool> busy = Protocol.TryWriteNativeChunkAsync(host.GetStream(), new byte[] { 1 }, send, gate,
            () => true, () => recorded++, timeout.Token);
        Assert.True(busy.IsCompleted); Assert.False(await busy); Assert.Equal(0, recorded);
        gate.Release();
        int checks = 0;
        Assert.False(await Protocol.TryWriteNativeChunkAsync(host.GetStream(), new byte[] { 1 }, send, gate,
            () => ++checks == 1, () => recorded++, timeout.Token));
        Assert.Equal(2, checks); Assert.Equal(0, recorded); Assert.Equal(1, gate.CurrentCount);
        Assert.True(await Protocol.TryWriteNativeChunkAsync(host.GetStream(), new byte[] { 42 }, send, gate,
            () => true, () => recorded++, timeout.Token));
        ProtocolMessage message = await Protocol.ReadMessageAsync(client.GetStream(), receive, timeout.Token);
        Assert.Equal(MessageType.NativeDetailChunk, message.Type); Assert.Equal(new byte[] { 42 }, message.PayloadMemory.ToArray());
        Assert.Equal(1, recorded); Assert.Equal(1, gate.CurrentCount);
    }

    private static NativeDetailLinkBudget HealthyLink()
    {
        var link = new NativeDetailLinkBudget();
        for (int i = 1; i <= 6; i++)
        {
            link.RecordSent(Context, i, 500_000, i * 100);
            Assert.True(link.Observe(new(Context, i, i, link.SentBytes), i * 100 + 20));
        }
        return link;
    }
}
