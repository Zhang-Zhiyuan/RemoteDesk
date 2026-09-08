using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FixedLatencyHistogramTests
{
    [Fact]
    public void SnapshotDeltaReportsP95P99AndMaximumWithoutBaselineSamples()
    {
        var histogram = new FixedLatencyHistogram();
        histogram.RecordStopwatchTicks(ToStopwatchTicks(100));
        FixedLatencyHistogramSnapshot baseline =
            histogram.Snapshot();

        RecordRepeated(histogram, milliseconds: 1, count: 90);
        RecordRepeated(histogram, milliseconds: 10, count: 5);
        RecordRepeated(histogram, milliseconds: 19, count: 4);
        RecordRepeated(histogram, milliseconds: 25, count: 1);

        FixedLatencyHistogramSummary summary =
            histogram.Snapshot().SummarizeSince(baseline);

        Assert.Equal(100, summary.SampleCount);
        AssertBucketUpperBound(1, summary.P50Milliseconds);
        AssertBucketUpperBound(10, summary.P95Milliseconds);
        AssertBucketUpperBound(19, summary.P99Milliseconds);
        AssertBucketUpperBound(25, summary.MaximumMilliseconds);
    }

    [Fact]
    public void NegativeSamplesAreIgnoredAndOverflowRemainsFinite()
    {
        var histogram = new FixedLatencyHistogram();
        histogram.RecordStopwatchTicks(-1);
        histogram.RecordStopwatchTicks(ToStopwatchTicks(600));

        FixedLatencyHistogramSummary summary =
            histogram.Snapshot().SummarizeSince(default);

        Assert.Equal(1, summary.SampleCount);
        Assert.InRange(
            summary.P95Milliseconds,
            600,
            600 + FixedLatencyHistogram
                .BucketWidthMilliseconds);
        Assert.InRange(
            summary.P99Milliseconds,
            600,
            600 + FixedLatencyHistogram
                .BucketWidthMilliseconds);
        Assert.InRange(
            summary.MaximumMilliseconds,
            600,
            600 + FixedLatencyHistogram
                .BucketWidthMilliseconds);
    }

    [Fact]
    public void RecordingPathDoesNotAllocatePerSample()
    {
        var histogram = new FixedLatencyHistogram();
        histogram.RecordStopwatchTicks(1);
        long allocatedBefore =
            GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 10_000; index++)
        {
            histogram.RecordStopwatchTicks(index + 1);
        }

        long allocatedAfter =
            GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(allocatedBefore, allocatedAfter);
    }

    private static void RecordRepeated(
        FixedLatencyHistogram histogram,
        double milliseconds,
        int count)
    {
        long ticks = ToStopwatchTicks(milliseconds);
        for (int index = 0; index < count; index++)
        {
            histogram.RecordStopwatchTicks(ticks);
        }
    }

    private static long ToStopwatchTicks(
        double milliseconds) =>
        checked(
            (long)Math.Round(
                milliseconds *
                Stopwatch.Frequency /
                1000d));

    private static void AssertBucketUpperBound(
        double expectedMilliseconds,
        double actualMilliseconds)
    {
        Assert.InRange(
            actualMilliseconds,
            expectedMilliseconds,
            expectedMilliseconds +
                FixedLatencyHistogram
                    .BucketWidthMilliseconds);
    }
}
