using System.Buffers;

namespace RemoteDesk;

internal sealed class H264FrameBufferLease : IDisposable
{
    private byte[]? _buffer;
    private readonly ArrayPool<byte>? _pool;

    private H264FrameBufferLease(
        byte[] buffer,
        int length,
        ArrayPool<byte>? pool)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (length < 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _buffer = buffer;
        Length = length;
        _pool = pool;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            byte[] buffer =
                Volatile.Read(ref _buffer) ??
                throw new ObjectDisposedException(
                    nameof(H264FrameBufferLease));
            return buffer.AsMemory(0, Length);
        }
    }

    internal Span<byte> WritableSpan
    {
        get
        {
            byte[] buffer =
                Volatile.Read(ref _buffer) ??
                throw new ObjectDisposedException(
                    nameof(H264FrameBufferLease));
            return buffer.AsSpan(0, Length);
        }
    }

    internal bool IsPooled => _pool is not null;

    internal bool IsDisposed =>
        Volatile.Read(ref _buffer) is null;

    internal static H264FrameBufferLease Wrap(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new H264FrameBufferLease(
            bytes,
            bytes.Length,
            pool: null);
    }

    internal static H264FrameBufferLease Allocate(
        int length,
        ArrayPool<byte> pool,
        bool pooled)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ArgumentNullException.ThrowIfNull(pool);
        if (!pooled)
        {
            return new H264FrameBufferLease(
                new byte[length],
                length,
                pool: null);
        }

        byte[] rented = pool.Rent(length);
        if (rented.Length < length)
        {
            pool.Return(rented);
            throw new InvalidOperationException(
                "The H.264 frame buffer pool returned an undersized array.");
        }

        return new H264FrameBufferLease(
            rented,
            length,
            pool);
    }

    internal static H264FrameBufferLease CopyFrom(
        ReadOnlySpan<byte> bytes,
        ArrayPool<byte> pool,
        bool pooled)
    {
        H264FrameBufferLease lease = Allocate(
            bytes.Length,
            pool,
            pooled);
        try
        {
            bytes.CopyTo(lease.WritableSpan);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(
            ref _buffer,
            null);
        if (buffer is not null && _pool is not null)
        {
            _pool.Return(buffer);
        }
    }
}

internal sealed class AnnexBH264AccessUnit : IDisposable
{
    private H264FrameBufferLease? _buffer;

    public AnnexBH264AccessUnit(
        byte[] bytes,
        RemoteFrameFlags flags,
        bool IsIdr,
        bool HasSps,
        bool HasPps)
        : this(
            H264FrameBufferLease.Wrap(bytes),
            flags,
            IsIdr,
            HasSps,
            HasPps)
    {
    }

    internal AnnexBH264AccessUnit(
        H264FrameBufferLease buffer,
        RemoteFrameFlags flags,
        bool IsIdr,
        bool HasSps,
        bool HasPps)
    {
        _buffer =
            buffer ??
            throw new ArgumentNullException(nameof(buffer));
        Flags = flags;
        this.IsIdr = IsIdr;
        this.HasSps = HasSps;
        this.HasPps = HasPps;
    }

    public ReadOnlyMemory<byte> Bytes =>
        GetBuffer().Memory;

    public RemoteFrameFlags Flags { get; }

    public bool IsIdr { get; }

    public bool HasSps { get; }

    public bool HasPps { get; }

    internal bool OwnsPooledBuffer =>
        Volatile.Read(ref _buffer)?.IsPooled == true;

    internal bool IsDisposed =>
        Volatile.Read(ref _buffer) is null;

    internal H264FrameBufferLease DetachBufferOwnership()
    {
        return Interlocked.Exchange(
                ref _buffer,
                null) ??
            throw new ObjectDisposedException(
                nameof(AnnexBH264AccessUnit));
    }

    public void Dispose()
    {
        Interlocked.Exchange(
                ref _buffer,
                null)
            ?.Dispose();
    }

    private H264FrameBufferLease GetBuffer()
    {
        return Volatile.Read(ref _buffer) ??
            throw new ObjectDisposedException(
                nameof(AnnexBH264AccessUnit));
    }
}

/// <summary>
/// Incrementally splits a raw H.264 Annex-B byte stream into access units.
/// The producer is required to insert an Access Unit Delimiter (NAL type 9)
/// before every access unit. This makes framing independent of pipe read
/// boundaries, which never correspond to FFmpeg packet boundaries.
/// </summary>
internal sealed class AnnexBH264AccessUnitParser
{
    internal const int DefaultMaxAccessUnitBytes = 8 * 1024 * 1024;
    private const int AppendSliceBytes = 64 * 1024;

    private readonly int _maxAccessUnitBytes;
    private byte[] _buffer;
    private int _count;
    private int _scanIndex;
    private int _scanFloor;
    private bool _hasCurrentAud;
    private bool _completed;
    private byte[]? _cachedSps;
    private byte[]? _cachedPps;

    public AnnexBH264AccessUnitParser(
        int maxAccessUnitBytes = DefaultMaxAccessUnitBytes)
    {
        if (maxAccessUnitBytes <= 0 ||
            maxAccessUnitBytes > int.MaxValue - 5)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAccessUnitBytes));
        }

        _maxAccessUnitBytes = maxAccessUnitBytes;
        _buffer = new byte[Math.Min(64 * 1024, maxAccessUnitBytes + 5)];
    }

    public IReadOnlyList<AnnexBH264AccessUnit> Append(
        ReadOnlySpan<byte> bytes)
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "Annex-B parser has already been completed.");
        }

        if (bytes.IsEmpty)
        {
            return Array.Empty<AnnexBH264AccessUnit>();
        }

        List<AnnexBH264AccessUnit>? completedUnits = null;
        int consumed = 0;
        while (consumed < bytes.Length)
        {
            int length = Math.Min(
                Math.Min(
                    AppendSliceBytes,
                    Math.Max(5, _maxAccessUnitBytes)),
                bytes.Length - consumed);
            AppendBytes(bytes.Slice(consumed, length));
            consumed += length;
            ScanForAccessUnitBoundaries(ref completedUnits);
            EnsurePendingAccessUnitIsBounded();
        }

        return completedUnits is null
            ? Array.Empty<AnnexBH264AccessUnit>()
            : completedUnits;
    }

    public IReadOnlyList<AnnexBH264AccessUnit> Complete()
    {
        if (_completed)
        {
            return Array.Empty<AnnexBH264AccessUnit>();
        }

        _completed = true;
        List<AnnexBH264AccessUnit>? completedUnits = null;
        ScanForAccessUnitBoundaries(ref completedUnits);
        if (_hasCurrentAud && _count > 0)
        {
            AnnexBH264AccessUnit? unit = BuildAccessUnit(
                _buffer.AsSpan(0, _count));
            if (unit is not null)
            {
                (completedUnits ??= []).Add(unit);
            }
        }
        else if (_count > 0)
        {
            CacheParameterSets(_buffer.AsSpan(0, _count));
        }

        ClearPendingBytes();
        return completedUnits is null
            ? Array.Empty<AnnexBH264AccessUnit>()
            : completedUnits;
    }

    private void AppendBytes(ReadOnlySpan<byte> bytes)
    {
        int required = checked(_count + bytes.Length);
        if (required > _buffer.Length)
        {
            int maximumCapacity = checked(_maxAccessUnitBytes + 5);
            int nextCapacity = Math.Min(
                maximumCapacity,
                Math.Max(required, checked(_buffer.Length * 2)));
            if (nextCapacity < required)
            {
                ThrowOversizedAccessUnit();
            }

            Array.Resize(ref _buffer, nextCapacity);
        }

        bytes.CopyTo(_buffer.AsSpan(_count));
        _count = required;
    }

    private void ScanForAccessUnitBoundaries(
        ref List<AnnexBH264AccessUnit>? completedUnits)
    {
        int index = _scanIndex;
        while (index <= _count - 4)
        {
            int startCodeLength = GetStartCodeLength(
                _buffer.AsSpan(0, _count),
                index);
            if (startCodeLength == 0)
            {
                index++;
                continue;
            }

            int nalHeaderIndex = index + startCodeLength;
            if (nalHeaderIndex >= _count)
            {
                break;
            }

            int nalType = _buffer[nalHeaderIndex] & 0x1F;
            if (nalType != 9)
            {
                index = nalHeaderIndex + 1;
                continue;
            }

            if (!_hasCurrentAud)
            {
                if (index > 0)
                {
                    CacheParameterSets(_buffer.AsSpan(0, index));
                    RemovePrefix(index);
                }

                _hasCurrentAud = true;
                _scanFloor = startCodeLength + 1;
                index = startCodeLength + 1;
                continue;
            }

            AnnexBH264AccessUnit? unit = BuildAccessUnit(
                _buffer.AsSpan(0, index));
            if (unit is not null)
            {
                (completedUnits ??= []).Add(unit);
            }

            RemovePrefix(index);
            _hasCurrentAud = true;
            _scanFloor = startCodeLength + 1;
            index = startCodeLength + 1;
        }

        _scanIndex = Math.Max(
            _scanFloor,
            Math.Max(0, Math.Min(index, _count - 4)));
    }

    private AnnexBH264AccessUnit? BuildAccessUnit(
        ReadOnlySpan<byte> accessUnitBytes)
    {
        if (accessUnitBytes.Length > _maxAccessUnitBytes)
        {
            ThrowOversizedAccessUnit();
        }

        List<NalUnit> nalUnits = FindNalUnits(accessUnitBytes);
        if (nalUnits.Count == 0)
        {
            return null;
        }

        bool isIdr = false;
        bool hasVcl = false;
        bool hasSps = false;
        bool hasPps = false;
        foreach (NalUnit nalUnit in nalUnits)
        {
            switch (nalUnit.Type)
            {
                case >= 1 and <= 5:
                    hasVcl = true;
                    isIdr |= nalUnit.Type == 5;
                    break;
                case 7:
                    hasSps = true;
                    _cachedSps = accessUnitBytes
                        .Slice(nalUnit.Offset, nalUnit.Length)
                        .ToArray();
                    break;
                case 8:
                    hasPps = true;
                    _cachedPps = accessUnitBytes
                        .Slice(nalUnit.Offset, nalUnit.Length)
                        .ToArray();
                    break;
            }
        }

        if (!hasVcl)
        {
            return null;
        }

        if (!isIdr)
        {
            RemoteFrameFlags flags =
                hasSps || hasPps
                    ? RemoteFrameFlags.CodecConfig
                    : RemoteFrameFlags.None;
            return new AnnexBH264AccessUnit(
                accessUnitBytes.ToArray(),
                flags,
                IsIdr: false,
                HasSps: hasSps,
                HasPps: hasPps);
        }

        // Normalize every IDR to AUD + latest SPS + latest PPS + the remaining
        // NAL units. This keeps both legacy GOP=1 and negotiated GOP=2 recovery
        // points independently decodable after a queue or network drop. If
        // either parameter set has never been observed, silently withhold the
        // unsafe IDR and let the capture startup/watchdog path reject it.
        if (_cachedSps is null || _cachedPps is null)
        {
            return null;
        }

        int normalizedCapacity = (int)Math.Min(
            _maxAccessUnitBytes,
            (long)accessUnitBytes.Length +
                _cachedSps.Length +
                _cachedPps.Length);
        var normalized = new ArrayBufferWriter<byte>(
            normalizedCapacity);
        bool wroteAud = false;
        foreach (NalUnit nalUnit in nalUnits)
        {
            if (nalUnit.Type == 9 && !wroteAud)
            {
                normalized.Write(accessUnitBytes.Slice(
                    nalUnit.Offset,
                    nalUnit.Length));
                normalized.Write(_cachedSps);
                normalized.Write(_cachedPps);
                wroteAud = true;
                continue;
            }

            if (nalUnit.Type is 7 or 8 or 9)
            {
                continue;
            }

            normalized.Write(accessUnitBytes.Slice(
                nalUnit.Offset,
                nalUnit.Length));
        }

        if (!wroteAud)
        {
            return null;
        }

        if (normalized.WrittenCount > _maxAccessUnitBytes)
        {
            ThrowOversizedAccessUnit();
        }

        return new AnnexBH264AccessUnit(
            normalized.WrittenSpan.ToArray(),
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
            IsIdr: true,
            HasSps: true,
            HasPps: true);
    }

    private void CacheParameterSets(ReadOnlySpan<byte> bytes)
    {
        foreach (NalUnit nalUnit in FindNalUnits(bytes))
        {
            if (nalUnit.Type == 7)
            {
                _cachedSps = bytes
                    .Slice(nalUnit.Offset, nalUnit.Length)
                    .ToArray();
            }
            else if (nalUnit.Type == 8)
            {
                _cachedPps = bytes
                    .Slice(nalUnit.Offset, nalUnit.Length)
                    .ToArray();
            }
        }
    }

    private static List<NalUnit> FindNalUnits(ReadOnlySpan<byte> bytes)
    {
        var starts = new List<NalStart>();
        int index = 0;
        while (index <= bytes.Length - 4)
        {
            int startCodeLength = GetStartCodeLength(bytes, index);
            if (startCodeLength == 0)
            {
                index++;
                continue;
            }

            int headerIndex = index + startCodeLength;
            if (headerIndex >= bytes.Length)
            {
                break;
            }

            starts.Add(new NalStart(
                index,
                bytes[headerIndex] & 0x1F));
            index = headerIndex + 1;
        }

        var nalUnits = new List<NalUnit>(starts.Count);
        for (int nalIndex = 0; nalIndex < starts.Count; nalIndex++)
        {
            NalStart start = starts[nalIndex];
            int end = nalIndex + 1 < starts.Count
                ? starts[nalIndex + 1].Offset
                : bytes.Length;
            if (end > start.Offset)
            {
                nalUnits.Add(new NalUnit(
                    start.Offset,
                    end - start.Offset,
                    start.Type));
            }
        }

        return nalUnits;
    }

    private static int GetStartCodeLength(
        ReadOnlySpan<byte> bytes,
        int index)
    {
        if (index < 0 || index + 3 >= bytes.Length)
        {
            return 0;
        }

        if (bytes[index] != 0 || bytes[index + 1] != 0)
        {
            return 0;
        }

        if (bytes[index + 2] == 1)
        {
            return 3;
        }

        return index + 4 < bytes.Length &&
            bytes[index + 2] == 0 &&
            bytes[index + 3] == 1
                ? 4
                : 0;
    }

    private void EnsurePendingAccessUnitIsBounded()
    {
        if (_count > checked(_maxAccessUnitBytes + 5))
        {
            ThrowOversizedAccessUnit();
        }
    }

    private void ThrowOversizedAccessUnit()
    {
        ClearPendingBytes();
        throw new InvalidDataException(
            $"H.264 access unit exceeds the {_maxAccessUnitBytes}-byte limit.");
    }

    private void RemovePrefix(int length)
    {
        if (length <= 0)
        {
            return;
        }

        int remaining = _count - length;
        if (remaining > 0)
        {
            Buffer.BlockCopy(
                _buffer,
                length,
                _buffer,
                0,
                remaining);
        }

        _count = remaining;
        _scanIndex = 0;
        _scanFloor = 0;
    }

    private void ClearPendingBytes()
    {
        _count = 0;
        _scanIndex = 0;
        _scanFloor = 0;
        _hasCurrentAud = false;
    }

    private readonly record struct NalStart(int Offset, int Type);

    private readonly record struct NalUnit(
        int Offset,
        int Length,
        int Type);
}
