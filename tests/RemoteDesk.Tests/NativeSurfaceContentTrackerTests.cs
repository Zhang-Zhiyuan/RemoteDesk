using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class NativeSurfaceContentTrackerTests
{
    [Fact]
    public void CoarseDamageCanOnlyBeRefinedWithAnExactSurfacePair()
    {
        var tracker = new NativeSurfaceContentTracker();
        var first = tracker.Commit(Manifest(1, 1, 1, 1), null);
        var second = tracker.Commit(Manifest(2, 2, 2, 2), new(2, 1, [0, 2]));
        Assert.Equal(new long[] { 1, 2, 1 }, second.CopyVersions());
        Assert.Equal(new long[] { 1, 1, 1 }, first.CopyVersions());
        // A changed tile must advance. Equality may keep, never roll back, a version.
        Assert.Equal(new long[] { 1, 2, 3 }, tracker.Commit(Manifest(3, 3, 3, 3), new(3, 2, [0, 1])).CopyVersions());
        Assert.Equal(new long[] { 1, 2, 3 }, tracker.Commit(Manifest(4, 3, 3, 3), null).CopyVersions());
    }

    [Fact]
    public void MissingResultImmediatelyInvalidatesAndAChangedBackPixelCannotResurrectAnOldVersion()
    {
        var tracker = new NativeSurfaceContentTracker();
        tracker.Commit(Manifest(1, 1, 1, 1), null);
        tracker.Commit(Manifest(2, 2, 2, 2), new(2, 1, [0, 1, 2]));
        Assert.Equal(new long[] { 3, 3, 3 }, tracker.Commit(Manifest(3, 3, 3, 3), null).CopyVersions());
        Assert.Equal(new long[] { 3, 3, 3 }, tracker.Commit(Manifest(4, 4, 4, 4), new(4, 3, [0, 1, 2])).CopyVersions());
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public void WrongOrLateSurfaceProofIsIgnored(long source, long reference)
    {
        var tracker = new NativeSurfaceContentTracker();
        tracker.Commit(Manifest(1, 1, 1, 1), null);
        Assert.Equal(new long[] { 2, 2, 2 }, tracker.Commit(Manifest(2, 2, 2, 2), new(source, reference, [0, 1, 2])).CopyVersions());
    }

    [Fact]
    public void MalformedAndExcessProofsFailClosed()
    {
        foreach (int[] invalid in new int[][] { [-1], [3], [0, 0], Enumerable.Range(0, 65).ToArray() })
        {
            var tracker = new NativeSurfaceContentTracker();
            tracker.Commit(Manifest(1, 1, 1, 1), null);
            Assert.Equal(new long[] { 2, 2, 2 }, tracker.Commit(Manifest(2, 2, 2, 2), new(2, 1, invalid)).CopyVersions());
        }
        foreach (var invalid in new NativeSurfaceComparison[] { new(2, 1, [0], [0]), new(2, 1, [0], [-1]),
            new(2, 1, [0], [3]), new(2, 1, [0], [1, 1]), new(2, 1, [0], new int[64]) })
        {
            var tracker = new NativeSurfaceContentTracker(); tracker.Commit(Manifest(1, 1, 1, 1), null);
            Assert.Equal(new long[] { 2, 2, 2 }, tracker.Commit(Manifest(2, 2, 2, 2), invalid).CopyVersions());
        }
    }

    [Fact]
    public void DroppedAcquisitionsDoNotLoseDamageOrRequireAdjacentFrameNumbers()
    {
        var tracker = new NativeSurfaceContentTracker();
        tracker.Commit(Manifest(1, 1, 1, 1), null);
        Assert.Equal(new long[] { 5, 1, 1 }, tracker.Commit(Manifest(5, 2, 1, 1), null).CopyVersions());
        Assert.Equal(new long[] { 5, 1, 10 }, tracker.Commit(Manifest(10, 9, 8, 7), new(10, 5, [0, 1])).CopyVersions());
    }

    [Fact]
    public void RequestedTilesWithoutProofAreInvalidatedEvenWhenMetadataReportsNoDamage()
    {
        var tracker = new NativeSurfaceContentTracker();
        tracker.Commit(Manifest(1, 1, 1, 1), null);
        Assert.Equal(new long[] { 2, 1, 2 }, tracker.Commit(Manifest(2, 1, 1, 1), null, [0, 2]).CopyVersions());
        Assert.Equal(new long[] { 2, 1, 3 }, tracker.Commit(Manifest(3, 1, 1, 1), new(3, 2, [0]), [0, 2]).CopyVersions());
        // A mismatched proof cannot remove the explicit requirement.
        Assert.Equal(new long[] { 4, 1, 4 }, tracker.Commit(Manifest(4, 1, 1, 1), new(3, 2, [0, 2]), [0, 2]).CopyVersions());
        Assert.Throws<ArgumentException>(() => tracker.Commit(Manifest(5, 1, 1, 1), null, [3]));
        Assert.Throws<ArgumentException>(() => tracker.Commit(Manifest(5, 1, 1, 1), null, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Commit(Manifest(5, 1, 1, 1), null, new int[65]));
        Assert.Equal(4, tracker.Current!.Sequence);
    }

    [Fact]
    public void RandomDamageDroppedFramesSparseProofsAndBudgetMissesNeverPreserveChangedPixels()
    {
        var random = new Random(914);
        var raw = new NativeSurfaceDamageTracker(new(384, 128));
        var tracker = new NativeSurfaceContentTracker();
        int[] content = [0, 0, 0], previousContent = [0, 0, 0];
        NativeSurfaceManifest? previous = null;
        for (int step = 0; step < 1500; step++)
        {
            var damage = new List<Rectangle>();
            for (int tile = 0; tile < 3; tile++)
                if (random.Next(5) == 0)
                { content[tile] = random.Next(4); damage.Add(new(tile * 128, 0, 128, 128)); }
            if (random.Next(2) == 0) damage.Add(new(0, 0, 384, 128));
            var manifest = raw.Observe(damage);
            if (random.Next(3) == 0) continue; // Never sent; damage still accumulates.
            NativeSurfaceComparison? proof = null;
            if (previous is not null && random.Next(3) != 0)
            {
                int[] selected = Enumerable.Range(0, 3).Where(_ => random.Next(2) == 0).ToArray();
                proof = new(manifest.Sequence, previous.Sequence,
                    selected.Where(tile => content[tile] == previousContent[tile]).ToArray(),
                    selected.Where(tile => content[tile] != previousContent[tile]).ToArray());
            }
            var current = tracker.Commit(manifest, proof);
            for (int tile = 0; tile < 3 && previous is not null; tile++)
            {
                Assert.True(current[tile] >= previous[tile]);
                if (current[tile] == previous[tile]) Assert.Equal(previousContent[tile], content[tile]);
            }
            previous = current; content.CopyTo(previousContent, 0);
        }
    }

    [Fact]
    public void ExactChangedPixelsOverrideIncorrectUnchangedDamageMetadata()
    {
        var tracker = new NativeSurfaceContentTracker();
        tracker.Commit(Manifest(1, 1, 1, 1), null);
        Assert.Equal(new long[] { 1, 2, 1 }, tracker.Commit(Manifest(2, 1, 1, 1), new(2, 1, [0, 2], [1])).CopyVersions());
        Assert.Equal(new long[] { 1, 2, 1 }, tracker.Commit(Manifest(3, 1, 1, 1), new(3, 2, [0, 1, 2])).CopyVersions());
    }

    [Fact]
    public void RollbackAndSizeChangeAreRejectedWithoutChangingTheCurrentState()
    {
        var tracker = new NativeSurfaceContentTracker();
        var first = tracker.Commit(Manifest(5, 2, 3, 5), null);
        Assert.Throws<ArgumentException>(() => tracker.Commit(Manifest(5, 5, 5, 5), null));
        Assert.Throws<ArgumentException>(() => tracker.Commit(Manifest(6, 2, 2, 5), null));
        Assert.Throws<ArgumentException>(() => tracker.Commit(new(6, new Size(128, 128), [6]), null));
        Assert.Same(first, tracker.Current);
    }

    private static NativeSurfaceManifest Manifest(long sequence, params long[] versions) => new(sequence, new(384, 128), versions);
}
