using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using RemoteDesk.NativeDetail;
using Xunit;

public sealed class NativeDetailTests
{
    private static readonly DetailContext Context = new(1, 1, 257, 129);
    private static DetailManifest Manifest(DetailContext context, long sequence = 1, long version = 1) =>
        new(context, sequence, Enumerable.Repeat(version, context.TileCount).ToArray());
    private static byte[] Pixels(DetailContext context, bool random = false)
    {
        byte[] pixels = new byte[context.PixelBytes];
        if (random) new Random(41).NextBytes(pixels); else Array.Fill(pixels, (byte)255);
        return pixels;
    }
    private static DetailTransfer Transfer(DetailManifest manifest, int tile = 0, bool random = false)
    {
        var rect = manifest.Context.Tile(tile);
        byte[] rgba = new byte[rect.Width * rect.Height * 4];
        if (random) new Random(tile + 77).NextBytes(rgba); else Array.Fill(rgba, (byte)255);
        return DetailTransfer.Encode(manifest, tile, rgba);
    }
    private static DetailCache Cache(DetailManifest manifest)
    {
        var cache = new DetailCache();
        cache.Reset(manifest.Context, new(0, 0, manifest.Context.Width, manifest.Context.Height));
        Assert.True(cache.Present(manifest)); return cache;
    }
    private static DetailReceiveResult Deliver(DetailCache cache, DetailTransfer transfer, long now = 300, bool reverse = false)
    {
        var offsets = Enumerable.Range(0, (transfer.Encoded.Length + 4095) / 4096).Select(index => index * 4096);
        if (reverse) offsets = offsets.Reverse();
        var result = DetailReceiveResult.Invalid;
        foreach (int offset in offsets) result = cache.Receive(transfer.Chunk(offset), now);
        return result;
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(127, 129)] [InlineData(257, 129)] [InlineData(3840, 2160)]
    public void EveryNativePixelBelongsToOneTileIncludingOddEdges(int width, int height)
    {
        var context = new DetailContext(1, 1, width, height); context.Validate();
        long area = 0;
        for (int tile = 0; tile < context.TileCount; tile++)
        {
            var rect = context.Tile(tile); rect.Validate(context);
            Assert.InRange(rect.Width, 1, 128); Assert.InRange(rect.Height, 1, 128);
            area += rect.Width * rect.Height;
        }
        Assert.Equal((long)width * height, area);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 50)] [InlineData(int.MaxValue, 1)]
    [InlineData(8192, 8192)] [InlineData(1, 8193)]
    public void MalformedGeometryFailsBeforeAllocation(int width, int height) =>
        Assert.Throws<InvalidDataException>(() => new DetailSource(new(1, 1, width, height)));

    [Fact]
    public void ManifestRleIsSelfContainedAndOwnsItsVersions()
    {
        long[] versions = [1, 1, 3, 3, 3, 2];
        var manifest = new DetailManifest(Context, 8, versions); versions[0] = 7;
        byte[] encoded = DetailWire.EncodeManifest(manifest);
        Assert.Equal(74, encoded.Length);
        var decoded = DetailWire.DecodeManifest(encoded);
        Assert.Equal(Context, decoded.Context); Assert.Equal(8, decoded.Sequence);
        Assert.Equal(new long[] { 1, 1, 3, 3, 3, 2 }, Enumerable.Range(0, 6).Select(tile => decoded[tile]));
        Assert.Equal(54, DetailWire.EncodeManifest(Manifest(new(1, 1, 3840, 2160))).Length);
    }

    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(5)] [InlineData(8)] [InlineData(16)]
    [InlineData(24)] [InlineData(28)] [InlineData(32)] [InlineData(40)] [InlineData(44)] [InlineData(46)]
    public void MalformedManifestFieldsAreRejected(int offset)
    {
        byte[] payload = DetailWire.EncodeManifest(Manifest(Context));
        payload.AsSpan(offset, offset is 8 or 16 or 32 ? 8 : Math.Min(4, payload.Length - offset)).Fill(0xFF);
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeManifest(payload));
    }

    [Fact]
    public void TruncatedOrAppendedRecordsAreRejected()
    {
        byte[] payload = DetailWire.EncodeManifest(Manifest(Context));
        for (int length = 0; length < payload.Length; length++)
            Assert.Throws<InvalidDataException>(() => DetailWire.DecodeManifest(payload.AsSpan(0, length)));
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeManifest([..payload, 0]));
        byte[] chunk = Transfer(Manifest(Context), random: true).Chunk(0);
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeChunk(chunk.AsSpan(0, chunk.Length - 1)));
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeChunk([..chunk, 0]));
    }

    [Fact]
    public void StabilityAndChangesUseNativePixelsNotInputInactivity()
    {
        byte[] input = Pixels(Context);
        var source = new DetailSource(Context);
        var first = source.Observe(input, 0);
        Assert.Null(source.Build(first, 0, 249));
        var patch = source.Build(first, 0, 250); Assert.NotNull(patch);
        input[0] = 1; // Mutation without Observe must not mutate the owned capture.
        Assert.Equal((byte)255, DetailWire.DecodeRgba(patch.Encoded.Span, 128 * 128 * 4)[0]);
        var changed = source.Observe(input, 300);
        Assert.Equal(2, changed[0]); Assert.Equal(1, changed[1]);
        Assert.Null(source.Build(first, 0, 550));
        Assert.NotNull(source.Build(first, 1, 550));
        Assert.NotNull(source.Build(changed, 0, 550));
        Assert.False(source.IsCurrent(patch));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReorderedChunksReconstructExactNativeRgba(bool reverse)
    {
        var manifest = Manifest(Context); var cache = Cache(manifest);
        var transfer = Transfer(manifest, random: true);
        Assert.True(transfer.Encoded.Length > 4096);
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, transfer, reverse: reverse));
        var patch = Assert.Single(cache.Patches);
        Assert.Equal(DetailWire.DecodeRgba(transfer.Encoded.Span, 65536), patch.Rgba.ToArray());
        Assert.Equal(DetailReceiveResult.Duplicate, cache.Receive(transfer.Chunk(0), 301));
        Assert.Equal(0, cache.PendingBytes);
    }

    [Fact]
    public void ChangedRegionDisappearsAtomicallyButUnchangedSurvivesNewVideo()
    {
        var first = Manifest(Context); var cache = Cache(first);
        var left = Transfer(first, 0); var right = Transfer(first, 1);
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, left));
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, right));
        Assert.True(cache.Present(Manifest(Context, 2))); Assert.Equal(2, cache.Patches.Count);
        var changed = new DetailManifest(Context, 3, [2, 1, 1, 1, 1, 1]);
        Assert.True(cache.Present(changed)); Assert.Equal(1, Assert.Single(cache.Patches).Tile);
        Assert.Equal(DetailReceiveResult.Stale, Deliver(cache, left, 310));
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, Transfer(changed), 311));
        Assert.False(cache.Present(first)); Assert.Equal(2, cache.Patches.Count);
    }

    [Fact]
    public void VersionRollbackRejectsWholeManifestWithoutPartialMutation()
    {
        var current = new DetailManifest(Context, 10, [2, 2, 2, 2, 2, 2]);
        var cache = Cache(current); Deliver(cache, Transfer(current));
        Assert.False(cache.Present(new(Context, 11, [3, 2, 2, 2, 2, 1])));
        Assert.Single(cache.Patches);
    }

    [Fact]
    public void FuturePatchCannotAppearBeforeItsBaseFrame()
    {
        var cache = Cache(Manifest(Context)); var future = Manifest(Context, 2, 2); var tile = Transfer(future);
        Assert.Equal(DetailReceiveResult.Future, Deliver(cache, tile)); Assert.Empty(cache.Patches);
        Assert.True(cache.Present(future)); Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, tile));
    }

    [Fact]
    public void SwitchResizeAndDisableCannotBeReenabledByOldPackets()
    {
        var first = Manifest(Context); var cache = Cache(first); var old = Transfer(first);
        Deliver(cache, old); cache.Disable();
        Assert.Equal(DetailReceiveResult.Inactive, Deliver(cache, old)); Assert.False(cache.Present(first));
        Assert.Throws<InvalidDataException>(() => cache.Reset(Context, new(0, 0, 257, 129)));
        var next = Context with { Request = 2, Width = 128, Height = 128 };
        cache.Reset(next, new(0, 0, 128, 128)); Assert.True(cache.Present(Manifest(next)));
        Assert.Equal(DetailReceiveResult.Stale, Deliver(cache, old)); Assert.Empty(cache.Patches);
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, Transfer(Manifest(next))));
    }

    [Fact]
    public void InteractionDiscardsOutstandingOldResponseUntilFreshReference()
    {
        var first = Manifest(Context); var cache = Cache(first); var tile = Transfer(first);
        Deliver(cache, tile); cache.Interaction(); Assert.Empty(cache.Patches);
        Assert.Equal(DetailReceiveResult.Stale, Deliver(cache, tile));
        var next = Manifest(Context, 2); Assert.True(cache.Present(next));
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, Transfer(next)));
    }

    [Fact]
    public void MaximumSequenceDoesNotOverflowOnInput()
    {
        var manifest = Manifest(Context, long.MaxValue); var cache = Cache(manifest);
        cache.Interaction(); Assert.Equal(DetailReceiveResult.Inactive, Deliver(cache, Transfer(manifest)));
    }

    [Fact]
    public void PartialTransfersAndCacheMemoryAreBounded()
    {
        var first = Manifest(Context); var cache = Cache(first);
        for (int tile = 0; tile < 3; tile++)
        {
            // Tile 2 is only one pixel wide; use a wider context for all three.
            var context = new DetailContext(1, 1, 512, 128);
            if (tile == 0) cache = Cache(Manifest(context));
            var transfer = Transfer(Manifest(context), tile, random: true);
            Assert.Equal(tile < 2 ? DetailReceiveResult.Partial : DetailReceiveResult.Limited, cache.Receive(transfer.Chunk(0), 300));
        }
        Assert.InRange(cache.PendingBytes, 1, 2 * DetailWire.MaxCompressedBytes);
        var wide = new DetailContext(2, 1, 8192, 256); cache.Reset(wide, new(0, 0, 8192, 256));
        var manifest = Manifest(wide); cache.Present(manifest);
        for (int tile = 0; tile < 80; tile++) Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, Transfer(manifest, tile), 400));
        Assert.Equal(64, cache.Patches.Count); Assert.InRange(cache.CachedBytes, 1, 4 * 1024 * 1024);
        Assert.Equal(0, cache.PendingBytes);
    }

    [Fact]
    public void ExpiredPartialTransferReleasesBudget()
    {
        var manifest = Manifest(new(1, 1, 512, 128)); var cache = Cache(manifest);
        cache.Receive(Transfer(manifest, 0, true).Chunk(0), 0);
        cache.Receive(Transfer(manifest, 1, true).Chunk(0), 0);
        Assert.Equal(DetailReceiveResult.Partial, cache.Receive(Transfer(manifest, 2, true).Chunk(0), 1501));
        Assert.InRange(cache.PendingBytes, 1, DetailWire.MaxCompressedBytes);
    }

    [Theory]
    [InlineData(40)] [InlineData(44)] [InlineData(52)] [InlineData(56)] [InlineData(60)]
    public void MalformedChunkAllocationFieldsAreRejected(int offset)
    {
        var manifest = Manifest(Context); var cache = Cache(manifest);
        byte[] chunk = Transfer(manifest, random: true).Chunk(0);
        chunk.AsSpan(offset, 4).Fill(255);
        Assert.Equal(DetailReceiveResult.Invalid, cache.Receive(chunk, 0)); Assert.Equal(0, cache.PendingBytes);
    }

    [Fact]
    public void ConflictingDuplicateAndBadDigestNeverProducePatch()
    {
        var manifest = Manifest(Context); var cache = Cache(manifest); var transfer = Transfer(manifest, random: true);
        byte[] chunk = transfer.Chunk(0); cache.Receive(chunk, 0); chunk[^1] ^= 1;
        Assert.Equal(DetailReceiveResult.Invalid, cache.Receive(chunk, 1)); Assert.Equal(0, cache.PendingBytes);
        var compact = Transfer(manifest); chunk = compact.Chunk(0); chunk[64] ^= 1;
        Assert.Equal(DetailReceiveResult.Invalid, cache.Receive(chunk, 2)); Assert.Empty(cache.Patches);
    }

    [Fact]
    public void NewerTransferSurvivesLateOlderChunks()
    {
        var manifest = Manifest(Context, 3); var cache = Cache(manifest);
        var old = Transfer(Manifest(Context, 2), random: true); var fresh = Transfer(manifest, random: true);
        cache.Receive(old.Chunk(0), 0);
        Assert.Equal(DetailReceiveResult.Partial, cache.Receive(fresh.Chunk(0), 1));
        Assert.Equal(DetailReceiveResult.Stale, cache.Receive(old.Chunk(4096), 2));
        Assert.Equal(DetailReceiveResult.Applied, Deliver(cache, fresh, 3));
    }

    [Fact]
    public void OutsideViewportAndOversizedInflationCannotAllocateDisplayImages()
    {
        var manifest = Manifest(Context); var cache = new DetailCache();
        cache.Reset(Context, new(0, 0, 128, 128)); cache.Present(manifest);
        Assert.Equal(DetailReceiveResult.OutsideViewport, Deliver(cache, Transfer(manifest, 1)));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(new byte[65537]);
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeRgba(compressed.ToArray(), 65536));
        Assert.ThrowsAny<IOException>(() => DetailWire.DecodeRgba(Transfer(manifest).Encoded.Span[..5], 65536));
        Assert.Equal(0, cache.CachedBytes);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MissingZlibTailIsRejectedEvenWhenAllPixelsWereProduced(int missing)
    {
        var transfer = Transfer(Manifest(Context));
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeRgba(transfer.Encoded.Span[..^missing], 65536));
    }

    [Fact]
    public void TrailingBytesAndConcatenatedZlibMembersAreRejected()
    {
        byte[] compressed = Transfer(Manifest(Context)).Encoded.ToArray();
        for (int suffix = 1; suffix <= 128; suffix++)
            Assert.Throws<InvalidDataException>(() => DetailWire.DecodeRgba([..compressed, ..new byte[suffix]], 65536));
        Assert.Throws<InvalidDataException>(() => DetailWire.DecodeRgba([..compressed, ..compressed], 65536));
    }

    [Fact]
    public void QueueStopsForInputControlAndCongestionWithoutAccumulatingBurst()
    {
        var queue = new DetailSendQueue(); var transfer = Transfer(Manifest(Context), random: true);
        Assert.True(queue.Enqueue(transfer, 0)); Assert.False(queue.Enqueue(transfer, 0));
        Assert.Null(queue.TryTake(100, 2_000_000, true, false, 0, _ => true));
        Assert.NotNull(queue.TryTake(101, 2_000_000, false, false, 0, _ => true));
        Assert.Null(queue.TryTake(101, 2_000_000, false, false, 0, _ => true));
        Assert.Null(queue.TryTake(200, 2_000_000, false, false, 50, _ => true));
        Assert.Null(queue.TryTake(201, 2_000_000, false, false, 0, _ => true));
        Assert.NotNull(queue.TryTake(220, 2_000_000, false, false, 0, _ => true));
        Assert.Null(queue.TryTake(220, 2_000_000, false, false, 0, _ => true));
        Assert.Null(queue.TryTake(221, 2_000_000, false, true, 0, _ => true)); Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void FrequentPriorityControlWithCoarseTimersCannotStarveDetails()
    {
        var queue = new DetailSendQueue(); queue.Enqueue(Transfer(Manifest(Context), random: true), 0);
        int chunks = 0;
        for (int now = 0; now <= 1200 && queue.Count > 0; now += 16)
        {
            bool control = now % 32 == 0;
            var chunk = queue.TryTake(now, 2_000_000, control, false, 0, _ => true);
            if (control) Assert.Null(chunk);
            if (chunk is not null) chunks++;
        }
        Assert.True(chunks > 10); Assert.Equal(0, queue.Count); Assert.Equal(0, queue.ExpiredTiles);
    }

    [Fact]
    public void QueueUsesWireBytesNotOnlyCompressedPixelsAndDropsStaleRemainder()
    {
        var source = new DetailSource(Context); byte[] pixels = Pixels(Context, random: true);
        var manifest = source.Observe(pixels, 0); var transfer = source.Build(manifest, 0, 250)!;
        var queue = new DetailSendQueue(); queue.Enqueue(transfer, 250);
        long bytes = 0;
        for (long now = 250; now < 450; now++)
        {
            var chunk = queue.TryTake(now, 500_000, false, false, 0, source.IsCurrent);
            if (chunk is not null) bytes += chunk.Length + DetailWire.OuterRecordBytes;
        }
        Assert.True(bytes <= 200 * 500_000 / 8000d + DetailWire.MaxWireChunkBytes);
        pixels[0] ^= 1; source.Observe(pixels, 450);
        Assert.Null(queue.TryTake(451, 500_000, false, false, 0, source.IsCurrent)); Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void PendingLookupTracksPartialCompletionExpiryAndClear()
    {
        var queue = new DetailSendQueue(); var transfer = Transfer(Manifest(Context), random: true);
        Assert.False(queue.ContainsTile(0)); Assert.True(queue.Enqueue(transfer, 0));
        long now = 100;
        while (queue.Count > 0)
        {
            Assert.True(queue.ContainsTile(0));
            Assert.NotNull(queue.TryTake(now, 2_000_000, false, false, 0, _ => true)); now += 20;
        }
        Assert.False(queue.ContainsTile(0)); Assert.Equal(0, queue.ExpiredTiles);
        Assert.True(queue.Enqueue(transfer, now));
        Assert.Null(queue.TryTake(now + 1501, 2_000_000, false, false, 0, _ => true));
        Assert.False(queue.ContainsTile(0));
        Assert.True(queue.Enqueue(transfer, now + 1501)); queue.Clear(); Assert.False(queue.ContainsTile(0));
    }

    [Fact]
    public void PreparingOnlyASmallLookAheadAvoidsExpiringUnsentViewportTiles()
    {
        var manifest = Manifest(new(1, 1, 1024, 640)); var queue = new DetailSendQueue();
        int nextTile = 0, sent = 0, now = 0;
        while (nextTile < manifest.Context.TileCount || queue.Count > 0)
        {
            while (queue.Count < 2 && nextTile < manifest.Context.TileCount)
                Assert.True(queue.Enqueue(Transfer(manifest, nextTile++), now));
            if (queue.TryTake(now, 2_000_000, false, false, 0, _ => true) is not null) sent++;
            now += 80;
            Assert.True(now < 5000);
        }
        Assert.Equal(40, sent); Assert.True(now > DetailCache.AssemblyLifetimeMilliseconds);
        Assert.Equal(0, queue.ExpiredTiles);
    }

    [Fact]
    public void QueueMemoryAndClocksAreBounded()
    {
        var manifest = Manifest(new(1, 1, 1024, 128)); var queue = new DetailSendQueue();
        for (int tile = 0; tile < 8; tile++) queue.Enqueue(Transfer(manifest, tile, true), 0);
        Assert.InRange(queue.QueuedBytes, 1, DetailSendQueue.MaxQueuedBytes); Assert.True(queue.Count < 8);
        Assert.Null(queue.TryTake(100, 0, false, false, 0, _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(Transfer(manifest), 99));
        Assert.Null(queue.TryTake(1601, 2_000_000, false, false, 0, _ => true)); Assert.Equal(0, queue.Count);
    }
}
