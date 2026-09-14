using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using RemoteDesk.NativeDetail;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailSessionTests
{
    internal static readonly DetailContext Context = new(1, 1, 256, 128);
    internal static DetailManifest Manifest(long sequence = 1, long firstVersion = 1,
        DetailContext? context = null) => new(context ?? Context, sequence, [firstVersion, 1]);
    internal static NativeDetailRequest Request(DetailContext? context = null, bool enabled = true) =>
        new(context ?? Context, new(0, 0, 256, 128), enabled);
    internal static RemoteFrame BaseFrame() => new(128, 64, RemoteFrameEncoding.H264AnnexB,
        RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
        [88, 0, 0, 0, 1, 0x65, 7, 99], 1, 6, 0.1, 0.2);
    private static NativeDetailRenderBudget Budget => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
    private static NativeDetailFrameIdentity Identity(long sequence = 1, long firstVersion = 1,
        DetailContext? context = null) => new(Manifest(sequence, firstVersion, context), sequence * 100);
    private static byte[] TileBytes(byte color = 37) => Enumerable.Repeat(color, 128 * 128 * 4).ToArray();

    [Fact]
    public void AtomicBaseRoundTripRetainsPayloadSliceAndSurvivesPoolOwnership()
    {
        RemoteFrame frame = BaseFrame();
        byte[] envelope = NativeDetailSessionProtocol.EncodeBase(Manifest(), frame);
        byte[] transportBuffer = new byte[envelope.Length + 31];
        envelope.CopyTo(transportBuffer, 17);
        RemoteFrame decoded = NativeDetailSessionProtocol.DecodeBase(transportBuffer.AsMemory(17, envelope.Length));
        Assert.Same(transportBuffer, decoded.EncodedBuffer);
        Assert.Equal(frame.EncodedBuffer.AsSpan(1, 6).ToArray(),
            decoded.EncodedBuffer.AsSpan(decoded.EncodedOffset, decoded.EncodedLength).ToArray());
        Assert.Equal(Context, decoded.NativeDetails!.Manifest.Context);
        Assert.Null(decoded.NativeDetails.DecoderSampleTime);
        using var pooled = PooledRemoteFrame.CopyFrom(decoded, ArrayPool<byte>.Shared, 14);
        Assert.Same(decoded.NativeDetails, pooled.Frame.NativeDetails);
        Assert.Same(decoded.NativeDetails, pooled.Metadata.NativeDetails);
        Assert.Equal(14, pooled.PresentationGeneration);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MaxValue)]
    public void BaseEnvelopeRejectsInvalidManifestLengthsBeforeAllocation(int length)
    {
        byte[] envelope = NativeDetailSessionProtocol.EncodeBase(Manifest(), BaseFrame());
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(4), length);
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeBase(envelope));
    }

    [Fact]
    public void BaseRejectsTruncationTrailingBytesDependentFrameAndInvalidMetrics()
    {
        byte[] envelope = NativeDetailSessionProtocol.EncodeBase(Manifest(), BaseFrame());
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeBase(envelope.AsMemory(0, envelope.Length - 1)));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeBase(envelope.Concat(new byte[1]).ToArray()));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeBase(Manifest(), BaseFrame() with { Flags = 0 }));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeBase(Manifest(), BaseFrame() with { Width = 258 }));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeBase(Manifest(), BaseFrame() with { EncodeMilliseconds = double.NaN }));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RequestHasExactBoundedLayoutAndStrictReservedBytes(bool enabled)
    {
        NativeDetailRequest request = Request(enabled: enabled);
        byte[] wire = NativeDetailSessionProtocol.EncodeRequest(request);
        Assert.Equal(48, wire.Length);
        Assert.Equal(request, NativeDetailSessionProtocol.DecodeRequest(wire));
        wire[7] = 1;
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeRequest(wire));
        wire[7] = 0; wire[4] = 2;
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.DecodeRequest(wire));
        Assert.Throws<InvalidDataException>(() => NativeDetailSessionProtocol.EncodeRequest(request with { Viewport = new(int.MaxValue, 0, 1, 1) }));
    }

    [Theory]
    [InlineData(null, false)] [InlineData(99L, false)] [InlineData(100L, true)] [InlineData(101L, false)]
    public void DecoderStatisticsFallbackNeverAssociatesNearbyNativeText(long? outputTime, bool exact)
    {
        RemoteFrameMetadata metadata = RemoteFrameMetadata.FromFrame(BaseFrame()) with { NativeDetails = Identity() };
        var result = RemoteViewerWindow.MatchNativeDetailDecoderOutput(metadata, outputTime);
        Assert.Equal(exact, result.NativeDetails is not null);
        Assert.Equal(metadata.CaptureMilliseconds, result.CaptureMilliseconds);
        Assert.Null(RemoteViewerWindow.MatchNativeDetailDecoderOutput(metadata with
            { NativeDetails = new(Manifest()) }, outputTime).NativeDetails);
    }

    [Fact]
    public void RequestTransitionNeverWaitsForNetworkToKeepShowingTheKnownBase()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        receiver.ObserveNativeBase(Identity());
        var next = Context with { Request = 2 };
        receiver.BeginRequest(Request(next));
        Assert.True(receiver.Requested(Identity(20)));
        Assert.False(receiver.Accepts(Identity(20)));
        receiver.ObserveNativeBase(Identity(context: next));
        Assert.False(receiver.Requested(Identity(20)));
        receiver.ResetConnection();
        Assert.False(receiver.Requested(Identity(context: next)));
    }

    [Fact]
    public void DisconnectForgetsEvenTheBaseOnlyPermissionOfAnOldRequest()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        receiver.Invalidate();
        Assert.True(receiver.Requested(Identity()));
        receiver.ResetConnection();
        Assert.False(receiver.Requested(Identity()));
        Assert.False(receiver.Accepts(Identity()));
        receiver.ObserveNativeBase(Identity());
        Assert.False(receiver.IsEnabled);
    }

    [Fact]
    public void InFlightLegacyBaseDoesNotCancelPendingRequestButLaterBackendFallbackDoes()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        receiver.ObserveLegacyBase();
        Assert.True(receiver.IsEnabled);
        receiver.ObserveNativeBase(Identity());
        receiver.ObserveLegacyBase();
        Assert.False(receiver.IsEnabled);
        receiver.BeginRequest(Request(Context with { Request = 2 }));
        receiver.ObserveNativeBase(Identity()); // An old request cannot arm fallback for the new one.
        receiver.ObserveLegacyBase();
        Assert.True(receiver.IsEnabled);
    }

    [Fact]
    public async Task IncomingDataCannotActivateSessionAndAllTransitionsRequireANewLocalRequest()
    {
        using var receiver = new NativeDetailViewerSession();
        Assert.False(receiver.TryQueueChunk(DetailTransfer.Encode(Manifest(), 0, TileBytes()).Chunk(0)));
        Assert.Null(receiver.TryPresent(Identity(), 100, Budget));
        receiver.BeginRequest(Request());
        Assert.True(receiver.IsEnabled);
        Assert.Throws<InvalidDataException>(() => receiver.BeginRequest(Request()));
        receiver.Invalidate();
        Assert.False(receiver.IsEnabled);
        Assert.True(receiver.Requested(Identity())); // Base may continue; overlays cannot.
        Assert.Null(receiver.TryPresent(Identity(100), 10000, Budget));
        receiver.BeginRequest(Request(Context with { Request = 2 }));
        Assert.False(receiver.Requested(Identity(100)));
        Assert.Null(receiver.TryPresent(Identity(100), 10000, Budget));
        receiver.Dispose();
        await receiver.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Throws<ObjectDisposedException>(() => receiver.BeginRequest(Request(Context with { Request = 3 })));
    }

    [Fact]
    public async Task ChunksArrivingBeforeDecodeAreBoundedAndRetriedOnlyAfterPresentation()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        var transfer = DetailTransfer.Encode(Manifest(), 0, TileBytes());
        Assert.True(receiver.TryQueueChunk(transfer.Chunk(0)));
        await Task.Delay(20); // Exercise the receive-before-present ordering.
        Assert.Equal(0, receiver.AppliedChunks);
        await PresentWhenReady(receiver, Identity());
        await Until(() => receiver.AppliedChunks == 1);
        var batch = await PresentWhenReady(receiver, Identity(2));
        Assert.Equal(1, batch.Tiles.Length);
        Assert.Equal(TileBytes(), batch.Tiles[0].Rgba.ToArray());
        var invalidated = await PresentWhenReady(receiver, Identity(3, 3));
        Assert.Equal(0, invalidated.Tiles.Length);
    }

    [Fact]
    public async Task CongestedBaseUpdatesVersionsButDoesNoNativePresentation()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        await AdvanceWithoutDetails(receiver, Identity(), default);
        Assert.Equal(1, receiver.LastPresentedSequence);
        Assert.True(receiver.TryQueueChunk(DetailTransfer.Encode(Manifest(), 0, TileBytes()).Chunk(0)));
        await Until(() => receiver.AppliedChunks == 1);
        await AdvanceWithoutDetails(receiver, Identity(2, 2), Budget with { BaseFrameBacklogged = true });
        var next = await PresentWhenReady(receiver, Identity(3, 2));
        Assert.Equal(0, next.Tiles.Length);
    }

    [Fact]
    public async Task FuturePatchDoesNotReplaceCurrentTextUntilItsExactVersionIsDisplayed()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        await PresentWhenReady(receiver, Identity());
        Assert.True(receiver.TryQueueChunk(DetailTransfer.Encode(Manifest(3, 3), 0, TileBytes(61)).Chunk(0)));
        await Task.Delay(20);
        Assert.Equal(0, receiver.AppliedChunks);
        await PresentWhenReady(receiver, Identity(2));
        Assert.Equal(0, receiver.AppliedChunks);
        await AdvanceWithoutDetails(receiver, Identity(3, 3), default);
        await Until(() => receiver.AppliedChunks == 1);
        var batch = await PresentWhenReady(receiver, Identity(4, 3));
        Assert.Equal(TileBytes(61), batch.Tiles[0].Rgba.ToArray());
    }

    [Fact]
    public async Task RendererAndInputNeverWaitBehindInflationAndReceiveQueueIsBounded()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        object gate = typeof(NativeDetailViewerSession).GetField("_cacheGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        Task? action = null;
        try
        {
            byte[] chunk = DetailTransfer.Encode(Manifest(), 0, TileBytes()).Chunk(0);
            for (int i = 0; i < NativeDetailViewerSession.MaximumQueuedChunks; i++) Assert.True(receiver.TryQueueChunk(chunk));
            Assert.False(receiver.TryQueueChunk(chunk));
            action = Task.Run(() =>
            {
                Assert.Null(receiver.TryPresent(Identity(), 100, Budget));
                receiver.Invalidate();
                Assert.False(receiver.IsEnabled);
            });
            Assert.Same(action, await Task.WhenAny(action, Task.Delay(1000)));
        }
        finally { release.Set(); await holder; if (action is not null) await action; }
    }

    [Fact]
    public async Task MalformedChunkDoesNotFaultWorkerOrDelayLaterBase()
    {
        using var receiver = new NativeDetailViewerSession();
        receiver.BeginRequest(Request());
        await PresentWhenReady(receiver, Identity());
        Assert.True(receiver.TryQueueChunk(new byte[100]));
        Assert.True(receiver.TryQueueChunk(DetailTransfer.Encode(Manifest(), 0, TileBytes()).Chunk(0)));
        await Until(() => receiver.AppliedChunks == 1);
        await PresentWhenReady(receiver, Identity(2));
        Assert.False(receiver.Completion.IsFaulted);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static async Task<NativeDetailPresentation> PresentWhenReady(NativeDetailViewerSession receiver,
        NativeDetailFrameIdentity identity)
    {
        NativeDetailPresentation? result = null;
        // Test synchronization only. The renderer's try-lock refusal is a
        // valid base-only result and must NOT become a production wait.
        await Until(() => (result = receiver.TryPresent(identity, identity.DecoderSampleTime, Budget)) is not null);
        return result!;
    }

    private static async Task AdvanceWithoutDetails(NativeDetailViewerSession receiver,
        NativeDetailFrameIdentity identity, NativeDetailRenderBudget budget)
    {
        await Until(() =>
        {
            Assert.Null(receiver.TryPresent(identity, identity.DecoderSampleTime, budget));
            return receiver.LastPresentedSequence == identity.Manifest.Sequence;
        });
    }
}
