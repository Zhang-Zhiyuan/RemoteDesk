using System.Drawing;

namespace RemoteDesk;

// Renderer-only immutable inputs. No wire capability is advertised yet. A
// session owner must validate native content versions before constructing a
// batch, and pair it with the EXACT decoded base sample, not an arrival time.
internal sealed class NativeDetailTile
{
    private readonly byte[] _rgba;
    internal Rectangle Bounds { get; }
    internal long Version { get; }
    internal ReadOnlyMemory<byte> Rgba => _rgba;

    internal NativeDetailTile(Rectangle bounds, long version, ReadOnlySpan<byte> rgba)
    {
        if (bounds.X < 0 || bounds.Y < 0 || bounds.X % 128 != 0 || bounds.Y % 128 != 0 ||
            bounds.Width is <= 0 or > 128 || bounds.Height is <= 0 or > 128 ||
            (long)bounds.X + bounds.Width > 8192 || (long)bounds.Y + bounds.Height > 8192 ||
            version <= 0 || rgba.Length != bounds.Width * bounds.Height * 4)
            throw new ArgumentException("Invalid native RGBA tile.");
        Bounds = bounds; Version = version; _rgba = rgba.ToArray();
    }
}

internal sealed class NativeDetailPresentation
{
    internal const int MaximumTiles = 64;
    private readonly NativeDetailTile[] _tiles;
    internal long Epoch { get; }
    internal long Request { get; }
    internal long Sequence { get; }
    internal long BaseSampleTime100Nanoseconds { get; }
    internal Size NativeSize { get; }
    internal Rectangle Viewport { get; }
    internal ReadOnlySpan<NativeDetailTile> Tiles => _tiles;

    internal NativeDetailPresentation(long epoch, long request, long sequence, long baseSampleTime100Nanoseconds,
        Size nativeSize, Rectangle viewport, IReadOnlyList<NativeDetailTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (epoch <= 0 || request <= 0 || sequence <= 0 || baseSampleTime100Nanoseconds < 0 ||
            nativeSize.Width is <= 0 or > 8192 || nativeSize.Height is <= 0 or > 8192 ||
            (long)nativeSize.Width * nativeSize.Height > 16_777_216 ||
            !Contains(new Rectangle(Point.Empty, nativeSize), viewport) || tiles.Count > MaximumTiles)
            throw new ArgumentException("Invalid native detail presentation.");
        _tiles = new NativeDetailTile[tiles.Count];
        var positions = new HashSet<Point>();
        for (int i = 0; i < _tiles.Length; i++)
        {
            NativeDetailTile tile = tiles[i] ?? throw new ArgumentException("Null native tile.");
            Rectangle expected = new(tile.Bounds.X, tile.Bounds.Y,
                Math.Min(128, nativeSize.Width - tile.Bounds.X), Math.Min(128, nativeSize.Height - tile.Bounds.Y));
            if (tile.Bounds != expected || !Contains(new Rectangle(Point.Empty, nativeSize), tile.Bounds) ||
                !tile.Bounds.IntersectsWith(viewport) || tile.Version > sequence || !positions.Add(tile.Bounds.Location))
                throw new ArgumentException("Native tile does not belong to this presentation.");
            _tiles[i] = tile;
        }
        Epoch = epoch; Request = request; Sequence = sequence; BaseSampleTime100Nanoseconds = baseSampleTime100Nanoseconds;
        NativeSize = nativeSize; Viewport = viewport;
    }

    internal bool Matches(long? explicitSampleTime) => explicitSampleTime == BaseSampleTime100Nanoseconds;

    private static bool Contains(Rectangle outer, Rectangle inner) => inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.Width > 0 && inner.Height > 0 && (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;
}
