using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeDetailPresentationTests
{
    private static NativeDetailTile Tile(int x = 0, int y = 0, int width = 128, int height = 128, long version = 1) =>
        new(new(x, y, width, height), version, new byte[width * height * 4]);

    [Fact]
    public void TilesAndPresentationOwnTheirInputs()
    {
        var bytes = new byte[128 * 128 * 4]; bytes[0] = 42;
        var tile = new NativeDetailTile(new(0, 0, 128, 128), 1, bytes); bytes[0] = 9;
        var list = new List<NativeDetailTile> { tile };
        var batch = new NativeDetailPresentation(1, 1, 1, 100, new(128, 128), new(0, 0, 128, 128), list);
        list.Clear();
        Assert.Equal(42, batch.Tiles[0].Rgba.Span[0]);
        Assert.True(batch.Matches(100));
        Assert.False(batch.Matches(101));
        Assert.False(batch.Matches(null));
    }

    [Theory]
    [InlineData(-128, 0, 128, 128, 1)]
    [InlineData(1, 0, 128, 128, 1)]
    [InlineData(8192, 0, 128, 128, 1)]
    [InlineData(0, 0, 129, 128, 1)]
    [InlineData(0, 0, 128, 0, 1)]
    [InlineData(0, 0, 128, 128, 0)]
    public void InvalidTileGeometryAndVersionAreRejected(int x, int y, int width, int height, long version) =>
        Assert.Throws<ArgumentException>(() => Tile(x, y, width, height, version));

    [Fact]
    public void ShortPixelBufferIsRejected() => Assert.Throws<ArgumentException>(() =>
        new NativeDetailTile(new(0, 0, 128, 128), 1, new byte[128]));

    [Fact]
    public void OddEdgeIsCroppedExactlyAndInteriorCannotBeShort()
    {
        var valid = new NativeDetailPresentation(1, 1, 1, 0, new(129, 129), new(0, 0, 129, 129), [Tile(128, 128, 1, 1)]);
        Assert.Single(valid.Tiles.ToArray());
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, 0,
            new(256, 256), new(0, 0, 256, 256), [Tile(0, 0, 127, 128)]));
    }

    [Fact]
    public void DuplicateOutsideAndFutureTilesAreRejected()
    {
        NativeDetailPresentation Batch(params NativeDetailTile[] tiles) =>
            new(1, 1, 1, 0, new(256, 256), new(0, 0, 128, 128), tiles);
        Assert.Throws<ArgumentException>(() => Batch(Tile(), Tile()));
        Assert.Throws<ArgumentException>(() => Batch(Tile(128)));
        Assert.Throws<ArgumentException>(() => Batch(Tile(version: 2)));
        Assert.Throws<ArgumentException>(() => Batch([null!]));
        Assert.Throws<ArgumentNullException>(() => Batch(null!));
    }

    [Fact]
    public void AllocationAndViewportAreBounded()
    {
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, 0, new(8192, 8192), new(0, 0, 128, 128), []));
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, 0, new(128, 128), new(int.MaxValue, 0, 128, 128), []));
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, 0, new(128, 128), Rectangle.Empty, []));
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, -1, new(128, 128), new(0, 0, 128, 128), []));
        var tiles = Enumerable.Range(0, 65).Select(i => Tile(i % 9 * 128, i / 9 * 128)).ToArray();
        Assert.Throws<ArgumentException>(() => new NativeDetailPresentation(1, 1, 1, 0, new(1152, 1024), new(0, 0, 1152, 1024), tiles));
        Assert.Equal(4 * 1024 * 1024, D3D11NativeDetailCompositor.AtlasBytes);
    }

    [Fact]
    public void NativeMappingUsesBaseCropAndLetterboxNotWindowDpi()
    {
        var batch = new NativeDetailPresentation(1, 1, 1, 0, new(3840, 2160), new(0, 0, 960, 640), []);
        var original = new Rectangle(16, 8, 1920, 1080);
        var geometry = D3D11HwndVideoPresenter.CalculateGeometry(original, new(800, 600), D3D11HwndVideoScaleMode.Fill);
        var c = D3D11NativeDetailCompositor.Constants(batch, original, geometry);
        Assert.Equal(480, c[0]); Assert.Equal(0, c[1]);
        Assert.Equal(3.6f, c[2]); Assert.Equal(3.6f, c[3]);
        var fit = D3D11HwndVideoPresenter.CalculateGeometry(original, new(3840, 2400), D3D11HwndVideoScaleMode.Fit);
        c = D3D11NativeDetailCompositor.Constants(batch, original, fit);
        Assert.Equal(0, c[0]); Assert.Equal(0, c[1]);
        Assert.Equal(1, c[2]); Assert.Equal(1, c[3]);
        Assert.Equal(0, c[4]); Assert.Equal(120, c[5]);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void FractionalOutputMappingKeepsNativeViewport(double scale)
    {
        var batch = new NativeDetailPresentation(1, 1, 1, 0, new(640, 480), new(19, 23, 300, 200), []);
        var source = new Rectangle(0, 0, 320, 240);
        var geometry = D3D11HwndVideoPresenter.CalculateGeometry(source, new((int)(640 * scale), (int)(480 * scale)), D3D11HwndVideoScaleMode.Fit);
        var c = D3D11NativeDetailCompositor.Constants(batch, source, geometry);
        Assert.Equal((float)(1 / scale), c[2]); Assert.Equal((float)(1 / scale), c[3]);
        Assert.Equal(new float[] { 19, 23, 319, 223 }, c.Skip(8).Take(4));
    }
}
