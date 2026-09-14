using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeSurfaceDamageTrackerTests
{
    [Fact]
    public void InitialFrameAndMissingMetadataInvalidateEverything()
    {
        var tracker = new NativeSurfaceDamageTracker(new(257, 259));
        var first = tracker.Observe([]);
        Assert.Equal(9, first.Count);
        Assert.All(first.CopyVersions(), version => Assert.Equal(1, version));
        var unchanged = tracker.Observe([]);
        Assert.All(unchanged.CopyVersions(), version => Assert.Equal(1, version));
        Assert.All(tracker.Observe(null).CopyVersions(), version => Assert.Equal(3, version));
        Assert.All(first.CopyVersions(), version => Assert.Equal(1, version));
    }

    [Fact]
    public void TileBoundariesAreHalfOpenAndClippedEdgeTilesWork()
    {
        var tracker = new NativeSurfaceDamageTracker(new(257, 259));
        tracker.Observe([]);
        var second = tracker.Observe([new(128, 0, 128, 128), new(256, 256, 1, 3)]);
        Assert.Equal(new long[] { 1, 2, 1, 1, 1, 1, 1, 1, 2 }, second.CopyVersions());
        var third = tracker.Observe([new(127, 127, 2, 2)]);
        Assert.Equal(new long[] { 3, 3, 1, 3, 3, 1, 1, 1, 2 }, third.CopyVersions());
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, -1, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(256, 0, 2, 1)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 1)]
    [InlineData(0, int.MaxValue, 1, int.MaxValue)]
    public void MalformedDamageCannotKeepStaleText(int x, int y, int width, int height)
    {
        var tracker = new NativeSurfaceDamageTracker(new(257, 259));
        tracker.Observe([]);
        Assert.All(tracker.Observe([new(x, y, width, height)]).CopyVersions(), version => Assert.Equal(2, version));
    }

    [Fact]
    public void ExcessMetadataInvalidatesAllWithoutUnboundedIteration()
    {
        var tracker = new NativeSurfaceDamageTracker(new(257, 259));
        tracker.Observe([]);
        Assert.All(tracker.Observe(new Rectangle[4097]).CopyVersions(), version => Assert.Equal(2, version));
    }

    [Fact]
    public void PublicVersionsAreNotMutableAliases()
    {
        var tracker = new NativeSurfaceDamageTracker(new(128, 128));
        var first = tracker.Observe([]);
        first.CopyVersions()[0] = 99;
        Assert.Equal(1, first[0]);
        Assert.Equal(1, tracker.Observe([])[0]);
        long[] supplied = [3];
        var copied = new NativeSurfaceManifest(3, new(128, 128), supplied);
        supplied[0] = 99;
        Assert.Equal(3, copied[0]);
    }

    [Fact]
    public void SkippingAnEncodedFrameDoesNotLoseItsDamage()
    {
        var tracker = new NativeSurfaceDamageTracker(new(384, 128));
        var first = tracker.Observe([]);
        _ = tracker.Observe([new(0, 0, 128, 128)]); // Captured but never sent.
        var third = tracker.Observe([new(128, 0, 128, 128)]);
        Assert.Equal(new long[] { 2, 3, 1 }, third.CopyVersions());
        Assert.Equal(new long[] { 1, 1, 1 }, first.CopyVersions());
    }

    [Fact]
    public void InvalidManifestsCannotBePresentedAsSourceEvidence()
    {
        Assert.Throws<ArgumentException>(() => new NativeSurfaceManifest(0, new(128, 128), [1]));
        Assert.Throws<ArgumentException>(() => new NativeSurfaceManifest(1, new(128, 128), [2]));
        Assert.Throws<ArgumentException>(() => new NativeSurfaceManifest(1, new(128, 128), [0]));
        Assert.Throws<ArgumentException>(() => new NativeSurfaceManifest(1, new(128, 128), []));
        Assert.Throws<ArgumentException>(() => new NativeSurfaceManifest(1, new(8192, 8192), [1]));
    }

    [Theory]
    [InlineData(128, 128, 0, 0, 128, 128, true)]
    [InlineData(257, 259, 256, 256, 1, 3, true)]
    [InlineData(257, 259, 0, 0, 129, 1, false)]
    [InlineData(257, 259, 0, 0, 1, 0, false)]
    [InlineData(257, 259, -1, 0, 1, 1, false)]
    [InlineData(257, 259, int.MaxValue, 0, 128, 128, false)]
    [InlineData(257, 259, 256, 258, 2, 2, false)]
    [InlineData(8192, 8192, 0, 0, 1, 1, false)]
    public void ReadbackBoundsAreBoundedAndOverflowSafe(int nw, int nh, int x, int y, int w, int h, bool valid) =>
        Assert.Equal(valid, D3D11NativeRegionReadback.IsValidRegion(new(nw, nh), new(x, y, w, h)));
}
