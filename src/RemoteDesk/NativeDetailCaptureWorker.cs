using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

internal sealed record NativeCaptureProfile(WindowsDesktopDuplicationTarget Target, int TargetGeneration,
    Size OutputSize, int FramesPerSecond, int BitrateBitsPerSecond, Rectangle? FixedCrop = null)
{
    internal Size NativeSize => FixedCrop?.Size ?? Target.Bounds.Size;
}

internal sealed record NativeCapturedFrame(DetailManifest Manifest, HardwareEncodedSourceFrame Video,
    long CapturedAt);

// All driver calls (including startup, shader compilation and shutdown) have
// one independent owner. The sender sees a capacity-one latest-frame mailbox.
// No compression, socket write or input handler runs on the GPU owner.
internal sealed class NativeDetailCaptureWorker
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<NativeDetailRequest?> _request;
    private readonly Func<bool> _allowDetail;
    private readonly Func<bool> _allowContentTracking;
    private readonly RemoteInteractionActivity _input;
    private readonly Func<NativeDetailFeedback?> _receipt;
    private readonly Action<long> _observeSequence;
    private readonly Action _ready;
    private readonly Channel<(DetailManifest Manifest, int Tile, byte[] Rgba)> _raw =
        Channel.CreateBounded<(DetailManifest, int, byte[])>(new BoundedChannelOptions(2)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentQueue<DetailTransfer> _compressed = new();
    private int _compressedCount;
    private bool _sendQueueFull;
    internal void ObserveSendBacklog(int count, int bytes, long bitsPerSecond) =>
        Volatile.Write(ref _sendQueueFull, count >= 3 || (count > 0 && bytes * 8000L > bitsPerSecond * 350));
    private NativeCapturedFrame? _latest;
    private readonly SemaphoreSlim _frameReady = new(0, 1);
    private DetailManifest? _latestManifest;
    private int _readySignaled;
    private string? _failure;
    private long _copies, _readbacks, _comparisons, _unready, _contentChanges;
    internal string Diagnostics => $"复制/读回 {Interlocked.Read(ref _copies)}/{Interlocked.Read(ref _readbacks)}，" +
        $"内容变化 {Interlocked.Read(ref _contentChanges)}，GPU 校验/未就绪 {Interlocked.Read(ref _comparisons)}/{Interlocked.Read(ref _unready)}";

    internal NativeDetailCaptureWorker(NativeCaptureProfile profile, long sequenceSeed,
        Func<NativeDetailRequest?> request, Func<bool> allowDetail, Func<bool> allowContentTracking,
        Func<NativeDetailFeedback?> receipt, Action<long> observeSequence, Action ready, RemoteInteractionActivity input)
    {
        Profile = profile; _request = request; _allowDetail = allowDetail;
        _allowContentTracking = allowContentTracking;
        _input = input;
        _receipt = receipt;
        _observeSequence = observeSequence; _ready = ready;
        Completion = Task.Run(async () =>
        {
            Task compression = CompressAsync();
            try
            {
                await Task.Factory.StartNew(() => Capture(sequenceSeed), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
            finally { _raw.Writer.TryComplete(); await compression.ConfigureAwait(false); _stop.Dispose(); }
        });
    }

    internal NativeCaptureProfile Profile { get; }
    internal Task Completion { get; }
    internal bool IsReady => Volatile.Read(ref _readySignaled) != 0 && !_stop.IsCancellationRequested && !Completion.IsCompleted && Failure is null;
    internal string? Failure => Volatile.Read(ref _failure);
    internal DetailManifest? LatestManifest => Volatile.Read(ref _latestManifest);
    internal long LastSequence { get; private set; }
    internal void Stop()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { /* The owner already completed shutdown. */ }
    }
    internal NativeCapturedFrame? TakeLatest() => Interlocked.Exchange(ref _latest, null);
    internal Task<bool> WaitForFrameAsync(int milliseconds, CancellationToken token) =>
        _frameReady.WaitAsync(milliseconds, token);
    internal bool TryTakeTransfer(out DetailTransfer? transfer)
    {
        if (!_compressed.TryDequeue(out transfer)) return false;
        Interlocked.Decrement(ref _compressedCount); return true;
    }

    private async Task CompressAsync()
    {
        await foreach (var work in _raw.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_stop.IsCancellationRequested || !_allowDetail() || !IsCurrent(work.Manifest, work.Tile) ||
                Volatile.Read(ref _compressedCount) >= 4) continue;
            try
            {
                DetailTransfer transfer = DetailTransfer.Encode(work.Manifest, work.Tile, work.Rgba);
                if (IsCurrent(work.Manifest, work.Tile) && !_stop.IsCancellationRequested)
                { Interlocked.Increment(ref _compressedCount); _compressed.Enqueue(transfer); }
            }
            catch (InvalidDataException) { /* Optional tile is not worth delaying a base. */ }
        }
    }

    private bool IsCurrent(DetailManifest manifest, int tile) => _request()?.Context == manifest.Context &&
        LatestManifest is { } current && current.Context == manifest.Context && current[tile] == manifest[tile];

    private void Capture(long sequenceSeed)
    {
        NativeDesktopSurface? pending = null;
        try
        {
            if (!D3D11DesktopSource.TryCreate(Profile.Target, Profile.FixedCrop, out var sourceResult,
                    out var sourceFailure, sequenceSeed)) throw new IOException(sourceFailure);
            using var source = sourceResult!;
            using var device = source.AcquireDeviceLease();
            if (!MediaFoundationD3D11H264Encoder.TryCreate(device,
                    new(Profile.NativeSize, Profile.OutputSize, Profile.FramesPerSecond, Profile.BitrateBitsPerSecond),
                    out var encoderResult, out var encoderFailure)) throw new IOException(encoderFailure);
            using var encoder = encoderResult!;
            using var refiner = new D3D11NativeDamageRefiner(device);
            using var readback = new D3D11NativeRegionReadback(device);
            using var timer = new WindowsHighResolutionPacingWaiter();
            var manifests = new Dictionary<long, DetailManifest>();
            var stability = new Dictionary<int, (long Version, long At, long QueuedAt)>();
            var confirmed = new Dictionary<int, long>();
            NativeDetailRequest? active = null, submittedRequest = null;
            NativeDetailFeedback? processedReceipt = null;
            int[] tiles = [];
            long nextFrameAt = 0, nextIdlePollAt = 0, lastSubmitAt = 0, handledInputAt = 0, startedAt = Stopwatch.GetTimestamp();
            long period = Stopwatch.Frequency / Profile.FramesPerSecond;

            while (!_stop.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                NativeDetailRequest? request = _request();
                if (request is not { Enabled: true } || request.Context.Width != Profile.NativeSize.Width ||
                    request.Context.Height != Profile.NativeSize.Height) break;
                if (active?.Context != request.Context)
                {
                    active = request; tiles = SelectTiles(request);
                    stability.Clear(); manifests.Clear(); confirmed.Clear(); readback.Clear();
                    while (TryTakeTransfer(out _)) { }
                }
                bool allowed = _allowDetail();
                if (_receipt() is { } receipt && !ReferenceEquals(receipt, processedReceipt) && receipt.Context == active.Context &&
                    receipt.PresentedSequence <= long.MaxValue / 166_667L &&
                    manifests.TryGetValue(receipt.PresentedSequence * 166_667L, out var acknowledged))
                {
                    processedReceipt = receipt;
                    for (int i = receipt.TilePage * 64; i < Math.Min(tiles.Length, (receipt.TilePage + 1) * 64); i++)
                        if ((receipt.CachedTileMask & (1UL << (i % 64))) != 0) confirmed[tiles[i]] = acknowledged[tiles[i]];
                }
                long deadline = pending is null ? nextFrameAt : pending.CapturedAtTimestamp + period;
                var budget = new NativeDetailRenderBudget(deadline, TransportCongested: !allowed);
                // Sender congestion suppresses NEW bytes/readback, not the
                // bounded proof required to retain already delivered pixels.
                // Otherwise ordinary ACK jitter invalidates all 64 tiles and
                // prevents any region from reaching its 250 ms stable age.
                var trackingBudget = new NativeDetailRenderBudget(deadline, InputPending: !_allowContentTracking());
                if (pending is not null) refiner.TryPoll(trackingBudget);
                HardwareEncodedSourceFrame? encoded = encoder.TryRead();
                if (encoder.IsFailed) throw new IOException(encoder.Failure);
                if (encoded is not null)
                {
                    using var surface = pending ?? throw new IOException("Native encoder lost its source.");
                    pending = null;
                    if (encoded.SourceTime100Nanoseconds != surface.Manifest.Sequence * 166_667L)
                        throw new IOException("Native encoder changed source identity.");
                    NativeSurfaceManifest refined = refiner.Complete(surface.Manifest.Sequence, trackingBudget);
                    Interlocked.Exchange(ref _comparisons, refiner.CompletedComparisons);
                    Interlocked.Exchange(ref _unready, refiner.UnreadyComparisons);
                    if (refiner.IsFailed) throw new IOException(refiner.Failure);
                    // A request may have advanced while the encoder was busy.
                    // Drop that old optional envelope, never re-label it.
                    if (submittedRequest?.Context == active.Context)
                    {
                        var manifest = new DetailManifest(active.Context, refined.Sequence, refined.CopyVersions());
                        Volatile.Write(ref _latestManifest, manifest);
                        Interlocked.Exchange(ref _latest, new(manifest, encoded, surface.CapturedAtTimestamp));
                        // One capture producer and one bounded wakeup. A finished input
                        // frame can leave immediately, even between network timer ticks.
                        if (_frameReady.CurrentCount == 0) _frameReady.Release();
                        if (Interlocked.Exchange(ref _readySignaled, 1) == 0) _ready();
                        manifests[encoded.SourceTime100Nanoseconds] = manifest;
                        while (manifests.Count > 64) manifests.Remove(manifests.Keys.Min());
                        int copies = 0;
                        foreach (int tile in tiles)
                        {
                            if (!stability.TryGetValue(tile, out var stable) || stable.Version != manifest[tile])
                            { stability[tile] = stable = (manifest[tile], now, 0); Interlocked.Increment(ref _contentChanges); }
                            if (confirmed.TryGetValue(tile, out long confirmedVersion) && confirmedVersion == manifest[tile]) continue;
                            if (!allowed || Volatile.Read(ref _sendQueueFull) || Volatile.Read(ref _compressedCount) >= 2 ||
                                Stopwatch.GetElapsedTime(stable.At, now).TotalMilliseconds < 250 ||
                                (stable.QueuedAt != 0 && Stopwatch.GetElapsedTime(stable.QueuedAt, now).TotalSeconds < 2)) continue;
                            DetailRect r = active.Context.Tile(tile);
                            Rectangle bounds = new(r.X, r.Y, r.Width, r.Height);
                            if (!readback.HasPendingRegion(bounds) &&
                                readback.TryEnqueue(surface.Texture, encoded.SourceTime100Nanoseconds, bounds, budget))
                            {
                                Interlocked.Increment(ref _copies);
                                stability[tile] = (stable.Version, stable.At, now);
                                if (++copies == 2) break;
                            }
                        }
                    }
                }
                NativeRegionPixels? pixels = readback.TryRead(budget);
                if (readback.IsFailed) throw new IOException(readback.Failure);
                if (pixels is not null && manifests.TryGetValue(pixels.SourceTime100Nanoseconds, out var pixelManifest))
                {
                    Interlocked.Increment(ref _readbacks);
                    int tile = pixels.Bounds.Y / 128 * pixelManifest.Context.Columns + pixels.Bounds.X / 128;
                    if (allowed && IsCurrent(pixelManifest, tile))
                        _raw.Writer.TryWrite((pixelManifest, tile, pixels.Rgba));
                }
                long inputAt = _input.LastActivityAt;
                bool scheduled = now >= nextFrameAt;
                // Seek the first actual desktop change after input. An empty early poll must
                // neither consume the input trigger nor move the regularly scheduled frame.
                bool earlyInput = active.Extended && inputAt > handledInputAt && now - inputAt < Stopwatch.Frequency / 10 &&
                    now - lastSubmitAt >= Math.Max(Stopwatch.Frequency / 60, period / 2);
                if (pending is null && ((scheduled && now >= nextIdlePollAt) || earlyInput))
                {
                    // A static desktop still needs periodic base presentations for later patches.
                    // Poll for real desktop changes independently of these duplicate IDRs.
                    bool repeatDue = !active.Extended || now - lastSubmitAt >= Stopwatch.Frequency / 11;
                    var surface = source.TryAcquire(repeatUnchanged: scheduled && repeatDue);
                    if (surface is null && scheduled) nextIdlePollAt = now + Stopwatch.Frequency / 120;
                    if (source.IsFailed) throw new IOException(source.Failure);
                    if (surface is not null)
                    {
                        LastSequence = surface.Manifest.Sequence; _observeSequence(LastSequence);
                        try
                        {
                            if (encoder.TrySubmit(surface.Texture, surface.Manifest.Sequence * 166_667L))
                            {
                                lastSubmitAt = Stopwatch.GetTimestamp();
                                nextFrameAt = lastSubmitAt + period;
                                if (surface.DesktopPresentedAtTimestamp >= inputAt) handledInputAt = inputAt;
                                refiner.Begin(surface.Texture, surface.Manifest, tiles,
                                    new(surface.CapturedAtTimestamp + period, InputPending: !_allowContentTracking()),
                                    repeatedImmutableSource: surface.DesktopPresentedAtTimestamp == 0);
                                submittedRequest = active; pending = surface; surface = null;
                            }
                        }
                        finally { surface?.Dispose(); }
                    }
                }
                if (!IsReady && Stopwatch.GetElapsedTime(startedAt).TotalSeconds > 5)
                    throw new IOException("Native hardware startup produced no usable frame.");
                timer.Wait(TimeSpan.FromMilliseconds(pending is not null ? .5 : 2), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Volatile.Write(ref _failure, ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            pending?.Dispose(); Interlocked.Exchange(ref _latest, null);
        }
    }

    internal static int[] SelectTiles(NativeDetailRequest request)
    {
        request.Context.Validate(); request.Viewport.Validate(request.Context);
        // The first receipt page keeps the original center-first order for older viewers.
        double x = request.Viewport.X + request.Viewport.Width / 2d;
        double y = request.Viewport.Y + request.Viewport.Height / 2d;
        return Enumerable.Range(0, request.Context.TileCount)
            .Where(index => request.Context.Tile(index).Intersects(request.Viewport))
            .OrderBy(index => { var r = request.Context.Tile(index); return Math.Abs(r.X + r.Width / 2d - x) + Math.Abs(r.Y + r.Height / 2d - y); })
            .Take(request.Extended ? DetailCache.ExtendedMaxCachedTiles : DetailCache.MaxCachedTiles).ToArray();
    }
}
