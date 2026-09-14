using System.Diagnostics;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

// One bounded background worker inflates patches. Neither the receive loop nor
// the render/input path waits for inflation. All rendering access is try-lock;
// losing admission for a frame means base-only, never a queued presentation.
internal sealed class NativeDetailViewerSession : IDisposable
{
    internal const int MaximumQueuedChunks = 8;
    private readonly object _cacheGate = new();
    private readonly object _queueGate = new();
    private readonly Queue<QueuedChunk> _queue = new();
    private readonly List<QueuedChunk> _future = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly DetailCache _cache = new();
    private readonly Dictionary<int, (DetailPatch Patch, NativeDetailTile Tile)> _tiles = new();
    private RequestState? _active;
    private RequestState? _requested;
    private BaseOnlyGrace? _previousBaseOnly;
    private Task? _worker;
    private bool _disposed;
    private DetailContext _lastContext;
    private long _appliedChunks;
    private long _lastPresented;

    private sealed record RequestState(DetailContext Context, DetailRect Viewport, bool Enabled)
    {
        internal int NativeBaseObserved;
        internal int[] TileOrder { get; } = NativeDetailCaptureWorker.SelectTiles(new(Context, Viewport, true));
    }
    private sealed record QueuedChunk(RequestState Owner, byte[] Bytes, long EnqueuedAt);
    private sealed record BaseOnlyGrace(RequestState Previous, RequestState Pending);

    internal bool IsEnabled => Volatile.Read(ref _active) is not null;
    internal DetailContext? RequestedContext => Volatile.Read(ref _requested)?.Context;
    internal bool IsStopRequest(DetailContext context) => Volatile.Read(ref _requested) is { Enabled: false } request && request.Context == context;
    internal long AppliedChunks => Interlocked.Read(ref _appliedChunks);
    internal long LastPresentedSequence => Interlocked.Read(ref _lastPresented);
    internal (long Sequence, ulong Tiles) ReadReceipt(DetailContext expected)
    {
        if (!Monitor.TryEnter(_cacheGate)) return default;
        try
        {
            RequestState? state = Volatile.Read(ref _active);
            if (state is null || state.Context != expected || _lastPresented == 0) return default;
            ulong mask = 0;
            for (int i = 0; i < state.TileOrder.Length; i++)
                if (_tiles.ContainsKey(state.TileOrder[i])) mask |= 1UL << i;
            return (_lastPresented, mask);
        }
        finally { Monitor.Exit(_cacheGate); }
    }
    internal Task Completion { get { lock (_cacheGate) return _worker ?? Task.CompletedTask; } }

    // Only the local authenticated request lifecycle may activate/reset. A
    // packet with another epoch, source size or request never enables itself.
    internal void BeginRequest(NativeDetailRequest request)
    {
        request.Context.Validate(); request.Viewport.Validate(request.Context);
        lock (_cacheGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lastContext.Epoch != 0 && (request.Context.Epoch < _lastContext.Epoch ||
                (request.Context.Epoch == _lastContext.Epoch && request.Context.Request <= _lastContext.Request)))
                throw new InvalidDataException("Native requests must advance their local context.");
            Volatile.Write(ref _active, null);
            _cache.Reset(request.Context, request.Viewport);
            _tiles.Clear(); _future.Clear();
            lock (_queueGate) _queue.Clear();
            _lastContext = request.Context;
            var state = new RequestState(request.Context, request.Viewport, request.Enabled);
            RequestState? previous = Volatile.Read(ref _requested);
            Volatile.Write(ref _previousBaseOnly, previous is not null &&
                previous.Context.Epoch == request.Context.Epoch &&
                previous.Context.Width == request.Context.Width && previous.Context.Height == request.Context.Height &&
                Volatile.Read(ref previous.NativeBaseObserved) != 0 ? new(previous, state) : null);
            Volatile.Write(ref _requested, state);
            Interlocked.Exchange(ref _lastPresented, 0);
            if (request.Enabled)
            {
                Volatile.Write(ref _active, state);
                _worker ??= Task.Run(ProcessChunksAsync);
            }
            else _cache.Disable();
        }
        Wake();
    }

    // Lock-free invalidation is intentionally stronger than "displayed + 1":
    // frames already in flight may predate input. Resume needs a NEW request,
    // ordered after input has flushed, not a guessed source-sequence cutoff.
    internal void Invalidate()
    {
        Interlocked.Exchange(ref _active, null);
        Wake();
    }

    internal void ResetConnection()
    {
        Invalidate();
        Interlocked.Exchange(ref _requested, null);
        Interlocked.Exchange(ref _previousBaseOnly, null);
    }

    internal bool Accepts(NativeDetailFrameIdentity? identity) => identity is not null &&
        Volatile.Read(ref _active)?.Context == identity.Manifest.Context;

    internal bool Requested(NativeDetailFrameIdentity? identity) => identity is not null &&
        (Volatile.Read(ref _requested)?.Context == identity.Manifest.Context ||
         Volatile.Read(ref _previousBaseOnly)?.Previous.Context == identity.Manifest.Context);

    internal void ObserveNativeBase(NativeDetailFrameIdentity identity)
    {
        RequestState? owner = Volatile.Read(ref _requested);
        if (owner?.Context == identity.Manifest.Context)
        {
            Interlocked.Exchange(ref owner.NativeBaseObserved, 1);
            BaseOnlyGrace? grace = Volatile.Read(ref _previousBaseOnly);
            if (grace is not null && ReferenceEquals(grace.Pending, owner))
                Interlocked.CompareExchange(ref _previousBaseOnly, null, grace);
        }
    }

    internal void ObserveLegacyBase()
    {
        RequestState? owner = Volatile.Read(ref _active);
        // A legacy frame can already be in flight when a request is sent.
        // Keep displaying it without patches; it is NOT a failed negotiation.
        if (owner is not null && Volatile.Read(ref owner.NativeBaseObserved) != 0 &&
            ReferenceEquals(Interlocked.CompareExchange(ref _active, null, owner), owner)) Wake();
    }

    internal bool TryQueueChunk(ReadOnlySpan<byte> payload)
    {
        RequestState? owner = Volatile.Read(ref _active);
        if (owner is null || payload.Length < DetailWire.ChunkHeaderBytes ||
            payload.Length > NativeDetailSessionProtocol.MaximumChunkPayloadBytes) return false;
        lock (_queueGate)
        {
            if (_queue.Count >= MaximumQueuedChunks || !ReferenceEquals(owner, Volatile.Read(ref _active))) return false;
            _queue.Enqueue(new(owner, payload.ToArray(), Environment.TickCount64));
        }
        Wake(); return true;
    }

    internal NativeDetailPresentation? TryPresent(NativeDetailFrameIdentity? identity,
        long? explicitDecoderSampleTime, NativeDetailRenderBudget budget)
    {
        RequestState? owner = Volatile.Read(ref _active);
        if (owner is null || identity?.MatchDecoderOutput(explicitDecoderSampleTime) is null ||
            identity.Manifest.Context != owner.Context || !Monitor.TryEnter(_cacheGate)) return null;
        try
        {
            if (!ReferenceEquals(owner, Volatile.Read(ref _active)) || !_cache.Present(identity.Manifest)) return null;
            Interlocked.Exchange(ref _lastPresented, identity.Manifest.Sequence);
            // Even if this frame has no enhancement headroom, invalidate the
            // changed content now. Never leave a cache associated with an older
            // base and never make future chunks wait on the render thread.
            foreach (int tile in _tiles.Keys.ToArray())
                if (_tiles[tile].Patch.Version != identity.Manifest[tile]) _tiles.Remove(tile);
            Wake();
            if (!budget.Allows(Stopwatch.GetTimestamp())) return null;
            var result = new NativeDetailPresentation(owner.Context.Epoch, owner.Context.Request,
                identity.Manifest.Sequence, explicitDecoderSampleTime!.Value,
                new(owner.Context.Width, owner.Context.Height),
                new(owner.Viewport.X, owner.Viewport.Y, owner.Viewport.Width, owner.Viewport.Height),
                _tiles.Values.Select(value => value.Tile).ToArray());
            return ReferenceEquals(owner, Volatile.Read(ref _active)) ? result : null;
        }
        finally { Monitor.Exit(_cacheGate); }
    }

    private async Task ProcessChunksAsync()
    {
        while (true)
        {
            await _wake.WaitAsync().ConfigureAwait(false);
            lock (_cacheGate)
            {
                if (_disposed) return;
                RequestState? owner = Volatile.Read(ref _active);
                if (owner is null)
                {
                    _cache.Disable(); _tiles.Clear(); _future.Clear();
                    lock (_queueGate) _queue.Clear();
                    continue;
                }
                long now = Environment.TickCount64;
                var work = new List<QueuedChunk>(_future);
                _future.Clear();
                lock (_queueGate)
                    while (_queue.TryDequeue(out QueuedChunk? chunk)) work.Add(chunk);
                foreach (QueuedChunk chunk in work)
                {
                    if (!ReferenceEquals(owner, Volatile.Read(ref _active))) break;
                    if (!ReferenceEquals(owner, chunk.Owner) || now - chunk.EnqueuedAt > DetailCache.AssemblyLifetimeMilliseconds)
                        continue;
                    DetailReceiveResult result = _cache.Receive(chunk.Bytes, now);
                    // Base decode may lag reception. Keep only a tiny bounded
                    // lookahead and retry on actual base presentation, no wait.
                    if ((result == DetailReceiveResult.Future ||
                         (result == DetailReceiveResult.Inactive && _lastPresented == 0)) &&
                        _future.Count < MaximumQueuedChunks)
                        _future.Add(chunk);
                    if (result == DetailReceiveResult.Applied)
                    {
                        Interlocked.Increment(ref _appliedChunks);
                        SynchronizeTiles();
                    }
                }
            }
        }
    }

    private void SynchronizeTiles()
    {
        var patches = _cache.Patches;
        var current = patches.Select(patch => patch.Tile).ToHashSet();
        foreach (int tile in _tiles.Keys.ToArray())
            if (!current.Contains(tile)) _tiles.Remove(tile);
        foreach (DetailPatch patch in patches)
        {
            if (_tiles.TryGetValue(patch.Tile, out var existing) && ReferenceEquals(existing.Patch, patch)) continue;
            var rect = patch.Rect;
            _tiles[patch.Tile] = (patch, new(new(rect.X, rect.Y, rect.Width, rect.Height), patch.Version, patch.Rgba.Span));
        }
    }

    private void Wake()
    {
        // This signal outlives the worker to keep racing input invalidations
        // and late receiver callbacks safe during disconnect/disposal.
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public void Dispose()
    {
        Invalidate();
        lock (_cacheGate)
        {
            _disposed = true;
            _cache.Disable(); _tiles.Clear(); _future.Clear();
            lock (_queueGate) _queue.Clear();
        }
        Wake();
    }
}
