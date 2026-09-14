namespace RemoteDesk;

internal sealed class NativeSurfaceManifest
{
    private readonly long[] _versions;
    public NativeSurfaceManifest(long sequence, Size size, long[] versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (sequence <= 0 || size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192 ||
            (long)size.Width * size.Height > 16_777_216 ||
            versions.Length > 2048 ||
            versions.Length != ((size.Width + 127) / 128) * ((size.Height + 127) / 128) ||
            versions.Any(version => version <= 0 || version > sequence))
            throw new ArgumentException("Invalid native source manifest.");
        Sequence = sequence; Size = size; _versions = (long[])versions.Clone();
    }
    public long Sequence { get; }
    public Size Size { get; }
    public int Count => _versions.Length;
    public long this[int index] => _versions[index];
    public long[] CopyVersions() => (long[])_versions.Clone();
}

/// <summary>
/// Conservative tile invalidation from same-frame DXGI damage. Unlike the
/// CPU prototype it never downloads/scans the whole desktop. Missing, invalid
/// or over-budget metadata invalidates everything; false positives cost detail
/// reuse, whereas a false negative could display an obsolete character.
/// Single capture owner. Reset by creating a new session/epoch tracker.
/// </summary>
internal sealed class NativeSurfaceDamageTracker
{
    internal const int TileEdge = 128;
    internal const int MaximumDamageRectangles = 4096;
    private readonly Size _size;
    private readonly int _columns;
    private readonly long[] _versions;
    private long _sequence;
    private bool _observed;

    public NativeSurfaceDamageTracker(Size size, long initialSequence = 0)
    {
        if (initialSequence < 0 || initialSequence == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(initialSequence));
        _sequence = initialSequence;
        if (size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192 ||
            (long)size.Width * size.Height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(size));
        _size = size; _columns = (size.Width + TileEdge - 1) / TileEdge;
        int count = _columns * ((size.Height + TileEdge - 1) / TileEdge);
        if (count > 2048) throw new ArgumentOutOfRangeException(nameof(size));
        _versions = new long[count];
    }

    public NativeSurfaceManifest Observe(IReadOnlyList<Rectangle>? damage)
    {
        long sequence = checked(++_sequence);
        bool invalidateAll = !_observed || damage is null || damage.Count > MaximumDamageRectangles;
        _observed = true;
        if (!invalidateAll)
        {
            foreach (Rectangle rectangle in damage!)
            {
                // Empty rectangles are harmless only if still on the surface.
                if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width < 0 || rectangle.Height < 0 ||
                    (long)rectangle.X + rectangle.Width > _size.Width ||
                    (long)rectangle.Y + rectangle.Height > _size.Height)
                { invalidateAll = true; break; }
            }
        }
        if (invalidateAll) Array.Fill(_versions, sequence);
        else
        {
            foreach (Rectangle rectangle in damage!)
            {
                if (rectangle.Width == 0 || rectangle.Height == 0) continue;
                int firstX = rectangle.X / TileEdge, lastX = (rectangle.Right - 1) / TileEdge;
                int firstY = rectangle.Y / TileEdge, lastY = (rectangle.Bottom - 1) / TileEdge;
                for (int y = firstY; y <= lastY; y++)
                    for (int x = firstX; x <= lastX; x++) _versions[y * _columns + x] = sequence;
            }
        }
        return new(sequence, _size, _versions);
    }
}
