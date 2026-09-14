using System.Security.Cryptography;

namespace RemoteDesk.NativeDetail;

// Each state object has one owning thread. The integration must marshal frame
// presentation, patches, resets and input invalidation onto that thread.
public sealed class DetailSource
{
    public const long StableMilliseconds = 250;
    private readonly DetailContext _context;
    private readonly byte[] _pixels;
    private readonly long[] _versions, _changedAt;
    private long _sequence, _now;
    public DetailSource(DetailContext context)
    {
        context.Validate(); _context = context; _pixels = new byte[context.PixelBytes];
        _versions = new long[context.TileCount]; _changedAt = new long[context.TileCount];
    }
    public DetailManifest Observe(ReadOnlySpan<byte> rgba, long nowMilliseconds)
    {
        CheckClock(nowMilliseconds);
        if (rgba.Length != _pixels.Length) throw new InvalidDataException("Native frame size mismatch.");
        long sequence = checked(_sequence + 1);
        for (int tile = 0; tile < _versions.Length; tile++)
        {
            var rect = _context.Tile(tile);
            bool different = _sequence == 0;
            for (int row = rect.Y; row < rect.Y + rect.Height && !different; row++)
            {
                int offset = (row * _context.Width + rect.X) * 4;
                different = !rgba.Slice(offset, rect.Width * 4).SequenceEqual(_pixels.AsSpan(offset, rect.Width * 4));
            }
            if (different) { _versions[tile] = checked(_versions[tile] + 1); _changedAt[tile] = nowMilliseconds; }
        }
        rgba.CopyTo(_pixels); _sequence = sequence;
        // The caller MUST attach this manifest to video made from precisely
        // this Observe input. A second independent capture is not equivalent.
        return new(_context, _sequence, _versions);
    }
    public bool IsCurrent(DetailTransfer transfer) => transfer.Context == _context &&
        transfer.ReferenceSequence <= _sequence && transfer.Tile >= 0 && transfer.Tile < _versions.Length &&
        transfer.Version == _versions[transfer.Tile];

    public DetailTransfer? Build(DetailManifest reference, int tile, long nowMilliseconds)
    {
        CheckClock(nowMilliseconds);
        var rect = _context.Tile(tile);
        if (reference.Context != _context || reference.Sequence > _sequence || reference[tile] != _versions[tile] ||
            nowMilliseconds - _changedAt[tile] < StableMilliseconds) return null;
        var rgba = new byte[rect.Width * rect.Height * 4];
        for (int row = 0; row < rect.Height; row++)
            _pixels.AsSpan(((rect.Y + row) * _context.Width + rect.X) * 4, rect.Width * 4)
                .CopyTo(rgba.AsSpan(row * rect.Width * 4));
        return DetailTransfer.Encode(reference, tile, rgba);
    }
    private void CheckClock(long now)
    {
        if (now < _now) throw new ArgumentOutOfRangeException(nameof(now));
        _now = now;
    }
}

public enum DetailReceiveResult { Inactive, Stale, Future, OutsideViewport, Limited, Invalid, Partial, Duplicate, Applied }
public sealed record DetailPatch(int Tile, long Version, DetailRect Rect, ReadOnlyMemory<byte> Rgba);

public sealed class DetailCache
{
    public const int MaxCachedTiles = 64, MaxAssemblies = 2;
    public const long AssemblyLifetimeMilliseconds = 1500;
    private DetailContext _context;
    private DetailRect _viewport;
    private DetailManifest? _displayed;
    private bool _enabled;
    private long _minimumReference, _now;
    private readonly Dictionary<int, Assembly> _assemblies = new();
    private readonly Dictionary<int, DetailPatch> _patches = new();
    private readonly Queue<int> _insertionOrder = new();
    public int PendingBytes => _assemblies.Values.Sum(item => item.Data.Length);
    public int CachedBytes => _patches.Values.Sum(item => item.Rgba.Length);
    public IReadOnlyCollection<DetailPatch> Patches => _patches.Values.ToArray();

    // Context changes originate in the local authenticated session/viewport
    // lifecycle, never from an unsolicited patch or incoming frame header.
    public void Reset(DetailContext context, DetailRect viewport)
    {
        context.Validate(); viewport.Validate(context);
        if (_context.Epoch != 0 && (context.Epoch < _context.Epoch ||
            (context.Epoch == _context.Epoch && context.Request <= _context.Request)))
            throw new InvalidDataException("A reset must advance the epoch or request token.");
        Clear(); _context = context; _viewport = viewport; _enabled = true;
        _displayed = null; _minimumReference = 0;
    }
    public void Disable() { Clear(); _enabled = false; _displayed = null; }
    public void Interaction()
    {
        if (_displayed?.Sequence == long.MaxValue) { Disable(); return; }
        Clear(); _minimumReference = _displayed is null ? 1 : checked(_displayed.Sequence + 1);
    }
    // Invoke at base-frame presentation, not network arrival. This operation
    // and composition form one rendering transaction: changed tiles disappear
    // before a newer base can be visible; unchanged tiles survive GOP refresh.
    public bool CanPresent(DetailManifest manifest)
    {
        if (!_enabled || manifest.Context != _context || (_displayed is not null && manifest.Sequence <= _displayed.Sequence)) return false;
        if (_displayed is not null)
            for (int tile = 0; tile < _context.TileCount; tile++)
                if (manifest[tile] < _displayed[tile]) return false; // Validate all before mutation.
        return true;
    }
    public bool Present(DetailManifest manifest)
    {
        if (!CanPresent(manifest)) return false;
        foreach (int tile in _patches.Keys.ToArray())
            if (_patches[tile].Version != manifest[tile]) _patches.Remove(tile);
        foreach (int tile in _assemblies.Keys.ToArray())
            if (_assemblies[tile].Header.Version != manifest[tile]) _assemblies.Remove(tile);
        // Compact the bounded eviction order; removed entries must never evict
        // a newer patch that reuses the same tile index.
        var kept = _insertionOrder.Where(_patches.ContainsKey).Distinct().ToArray();
        _insertionOrder.Clear(); foreach (int tile in kept) _insertionOrder.Enqueue(tile);
        _displayed = manifest; return true;
    }

    public DetailReceiveResult Receive(ReadOnlySpan<byte> payload, long nowMilliseconds)
    {
        if (nowMilliseconds < _now) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        _now = nowMilliseconds;
        foreach (int tile in _assemblies.Keys.ToArray())
            if (nowMilliseconds - _assemblies[tile].StartedAt > AssemblyLifetimeMilliseconds) _assemblies.Remove(tile);
        if (!_enabled || _displayed is null) return DetailReceiveResult.Inactive;
        DetailChunk chunk;
        try { chunk = DetailWire.DecodeChunk(payload); }
        catch (InvalidDataException) { return DetailReceiveResult.Invalid; }
        if (chunk.Context != _context || chunk.ReferenceSequence < _minimumReference) return DetailReceiveResult.Stale;
        if (chunk.ReferenceSequence > _displayed.Sequence) return DetailReceiveResult.Future;
        if (chunk.Version != _displayed[chunk.Tile]) return DetailReceiveResult.Stale;
        var rect = _context.Tile(chunk.Tile);
        if (!rect.Intersects(_viewport)) return DetailReceiveResult.OutsideViewport;
        if (_patches.ContainsKey(chunk.Tile)) return DetailReceiveResult.Duplicate;
        if (!_assemblies.TryGetValue(chunk.Tile, out var assembly))
        {
            if (_assemblies.Count >= MaxAssemblies) return DetailReceiveResult.Limited;
            assembly = new(chunk, nowMilliseconds); _assemblies.Add(chunk.Tile, assembly);
        }
        if (!assembly.Matches(chunk))
        {
            // A late older transfer must not tear down a newer assembly.
            if (chunk.ReferenceSequence < assembly.Header.ReferenceSequence) return DetailReceiveResult.Stale;
            if (chunk.ReferenceSequence > assembly.Header.ReferenceSequence && chunk.Offset == 0)
            { assembly = new(chunk, nowMilliseconds); _assemblies[chunk.Tile] = assembly; }
            else { return DetailReceiveResult.Invalid; }
        }
        int index = chunk.Offset / chunk.ChunkDataBytes;
        if (assembly.Received[index])
        {
            if (assembly.Data.AsSpan(chunk.Offset, chunk.Data.Length).SequenceEqual(chunk.Data.Span)) return DetailReceiveResult.Duplicate;
            _assemblies.Remove(chunk.Tile); return DetailReceiveResult.Invalid;
        }
        chunk.Data.Span.CopyTo(assembly.Data.AsSpan(chunk.Offset)); assembly.Received[index] = true;
        if (assembly.Received.Any(received => !received)) return DetailReceiveResult.Partial;
        _assemblies.Remove(chunk.Tile);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(assembly.Data), chunk.Digest.Span)) return DetailReceiveResult.Invalid;
        byte[] rgba;
        try { rgba = DetailWire.DecodeRgba(assembly.Data, rect.Width * rect.Height * 4); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return DetailReceiveResult.Invalid; }
        while (_patches.Count >= MaxCachedTiles) _patches.Remove(_insertionOrder.Dequeue());
        _patches.Add(chunk.Tile, new(chunk.Tile, chunk.Version, rect, rgba)); _insertionOrder.Enqueue(chunk.Tile);
        return DetailReceiveResult.Applied;
    }
    private void Clear() { _assemblies.Clear(); _patches.Clear(); _insertionOrder.Clear(); }
    private sealed class Assembly(DetailChunk header, long startedAt)
    {
        public DetailChunk Header { get; } = header;
        public long StartedAt { get; } = startedAt;
        public byte[] Data { get; } = new byte[header.TotalBytes];
        public bool[] Received { get; } = new bool[(header.TotalBytes + header.ChunkDataBytes - 1) / header.ChunkDataBytes];
        public bool Matches(DetailChunk other) => Header.ReferenceSequence == other.ReferenceSequence &&
            Header.Version == other.Version && Header.TotalBytes == other.TotalBytes && Header.ChunkDataBytes == other.ChunkDataBytes &&
            Header.Digest.Span.SequenceEqual(other.Digest.Span);
    }
}

public sealed class DetailSendQueue
{
    private readonly int _chunkBytes;
    public DetailSendQueue(int chunkBytes = DetailWire.ChunkBytes)
    {
        if (chunkBytes is not (512 or DetailWire.ChunkBytes)) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        _chunkBytes = chunkBytes;
    }
    public const int MaxQueuedBytes = 256 * 1024, MaxQueuedTiles = 64;
    private readonly Queue<Pending> _queue = new();
    private double _credit;
    private long _now;
    private long _lastObserved;
    public int QueuedBytes { get; private set; }
    public int Count => _queue.Count;
    public int ExpiredTiles { get; private set; }
    public bool ContainsTile(int tile) => _queue.Any(item => item.Transfer.Tile == tile);
    public bool Enqueue(DetailTransfer transfer, long nowMilliseconds)
    {
        if (nowMilliseconds < _lastObserved) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        _lastObserved = nowMilliseconds;
        if (_queue.Count >= MaxQueuedTiles || transfer.Encoded.Length > MaxQueuedBytes - QueuedBytes ||
            ContainsTile(transfer.Tile)) return false;
        _queue.Enqueue(new(transfer, nowMilliseconds)); QueuedBytes += transfer.Encoded.Length; return true;
    }
    public void Clear() { _queue.Clear(); QueuedBytes = 0; _credit = 0; }
    public byte[]? TryTake(long nowMilliseconds, long availableBitsPerSecond, bool controlPending, bool interacting,
        double sendQueueMilliseconds, Func<DetailTransfer, bool> isCurrent)
    {
        if (nowMilliseconds < _lastObserved || !double.IsFinite(sendQueueMilliseconds) || sendQueueMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        _lastObserved = nowMilliseconds;
        long elapsed = nowMilliseconds - _now; _now = nowMilliseconds;
        if (interacting) { Clear(); return null; }
        if (sendQueueMilliseconds >= 50 || availableBitsPerSecond <= 0)
        { _credit = 0; return null; }
        _credit = Math.Min(DetailWire.MaxWireChunkBytes, _credit + elapsed * (Math.Min(availableBitsPerSecond, 1_000_000_000) / 8000d));
        // Let priority control go first, but keep at most one chunk's credit.
        // Erasing credit on every small control message starves detail forever
        // when the timer quantum exceeds the gap between control messages.
        if (controlPending) return null;
        while (_queue.TryPeek(out var item))
        {
            if (!isCurrent(item.Transfer) || nowMilliseconds - item.CreatedAt > DetailCache.AssemblyLifetimeMilliseconds)
            { ExpiredTiles++; Remove(); continue; }
            int wireBytes = DetailWire.ChunkHeaderBytes + Math.Min(_chunkBytes, item.Transfer.Encoded.Length - item.Offset) + DetailWire.OuterRecordBytes;
            if (_credit < wireBytes) return null;
            byte[] chunk = item.Transfer.Chunk(item.Offset, _chunkBytes); _credit -= wireBytes;
            item.Offset += chunk.Length - DetailWire.ChunkHeaderBytes;
            if (item.Offset == item.Transfer.Encoded.Length) Remove();
            return chunk;
        }
        return null;
    }
    private void Remove() => QueuedBytes -= _queue.Dequeue().Transfer.Encoded.Length;
    private sealed class Pending(DetailTransfer transfer, long createdAt)
    {
        public DetailTransfer Transfer { get; } = transfer;
        public long CreatedAt { get; } = createdAt;
        public int Offset { get; set; }
    }
}
