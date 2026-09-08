using System.Diagnostics;

namespace RemoteDesk;

/// <summary>
/// Records latency samples into a fixed set of sub-millisecond buckets.
/// The recording path performs no allocation and is safe for concurrent
/// producers. A snapshot allocates only when diagnostics explicitly request
/// percentile data.
/// </summary>
internal sealed class FixedLatencyHistogram
{
    internal const int FiniteBucketCount = 1024;
    internal const int OverflowBucketIndex = FiniteBucketCount;
    private const int BucketsPerSecond = 4000;
    private static readonly long BucketWidthStopwatchTicks =
        Math.Max(
            1,
            checked(
                (Stopwatch.Frequency + BucketsPerSecond - 1) /
                BucketsPerSecond));
    internal static readonly double BucketWidthMilliseconds =
        BucketWidthStopwatchTicks *
        1000d /
        Stopwatch.Frequency;

    private readonly long[] _buckets =
        new long[FiniteBucketCount + 1];
    private long _maximumStopwatchTicks;

    internal void RecordStopwatchTicks(long elapsedStopwatchTicks)
    {
        if (elapsedStopwatchTicks < 0)
        {
            return;
        }

        long bucketIndex =
            elapsedStopwatchTicks == 0
                ? 0
                : (elapsedStopwatchTicks - 1) /
                    BucketWidthStopwatchTicks;
        int boundedBucketIndex =
            bucketIndex >= OverflowBucketIndex
                ? OverflowBucketIndex
                : (int)bucketIndex;
        Interlocked.Increment(
            ref _buckets[boundedBucketIndex]);
        UpdateMaximum(
            ref _maximumStopwatchTicks,
            elapsedStopwatchTicks);
    }

    internal FixedLatencyHistogramSnapshot Snapshot()
    {
        var buckets = new long[_buckets.Length];
        for (int index = 0; index < buckets.Length; index++)
        {
            buckets[index] =
                Interlocked.Read(ref _buckets[index]);
        }

        return new(
            buckets,
            BucketWidthMilliseconds,
            Interlocked.Read(ref _maximumStopwatchTicks) *
                1000d /
                Stopwatch.Frequency);
    }

    private static void UpdateMaximum(
        ref long target,
        long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(
                ref target,
                candidate,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

internal readonly record struct FixedLatencyHistogramSnapshot(
    long[]? Buckets,
    double BucketWidthMilliseconds,
    double CumulativeMaximumMilliseconds)
{
    internal FixedLatencyHistogramSummary SummarizeSince(
        FixedLatencyHistogramSnapshot baseline)
    {
        if (Buckets is null ||
            Buckets.Length !=
                FixedLatencyHistogram.FiniteBucketCount + 1 ||
            !double.IsFinite(BucketWidthMilliseconds) ||
            BucketWidthMilliseconds <= 0)
        {
            return default;
        }

        long[]? baselineBuckets =
            baseline.Buckets is not null &&
            baseline.Buckets.Length == Buckets.Length &&
            Math.Abs(
                baseline.BucketWidthMilliseconds -
                BucketWidthMilliseconds) <
                    double.Epsilon
                ? baseline.Buckets
                : null;
        long sampleCount = 0;
        int maximumBucketIndex = -1;
        for (int index = 0; index < Buckets.Length; index++)
        {
            long previous =
                baselineBuckets is null
                    ? 0
                    : Volatile.Read(
                        ref baselineBuckets[index]);
            long current =
                Volatile.Read(ref Buckets[index]);
            long delta = Math.Max(0, current - previous);
            if (delta == 0)
            {
                continue;
            }

            sampleCount = checked(sampleCount + delta);
            maximumBucketIndex = index;
        }

        if (sampleCount == 0)
        {
            return default;
        }

        long p50Rank =
            checked((long)Math.Ceiling(sampleCount * 0.50d));
        long p95Rank =
            checked((long)Math.Ceiling(sampleCount * 0.95d));
        long p99Rank =
            checked((long)Math.Ceiling(sampleCount * 0.99d));
        long cumulative = 0;
        double p50Milliseconds = 0;
        double p95Milliseconds = 0;
        double p99Milliseconds = 0;
        for (int index = 0; index < Buckets.Length; index++)
        {
            long previous =
                baselineBuckets is null
                    ? 0
                    : Volatile.Read(
                        ref baselineBuckets[index]);
            long current =
                Volatile.Read(ref Buckets[index]);
            cumulative = checked(
                cumulative +
                Math.Max(0, current - previous));
            double upperBoundMilliseconds =
                GetBucketUpperBoundMilliseconds(index);
            if (p50Milliseconds == 0 &&
                cumulative >= p50Rank)
            {
                p50Milliseconds = upperBoundMilliseconds;
            }

            if (p95Milliseconds == 0 &&
                cumulative >= p95Rank)
            {
                p95Milliseconds = upperBoundMilliseconds;
            }

            if (p99Milliseconds == 0 &&
                cumulative >= p99Rank)
            {
                p99Milliseconds = upperBoundMilliseconds;
                break;
            }
        }

        double maximumMilliseconds =
            maximumBucketIndex ==
                FixedLatencyHistogram.OverflowBucketIndex
                ? Math.Max(
                    CumulativeMaximumMilliseconds,
                    GetBucketLowerBoundMilliseconds(
                        maximumBucketIndex))
                : GetBucketUpperBoundMilliseconds(
                    maximumBucketIndex);
        return new(
            sampleCount,
            p50Milliseconds,
            p95Milliseconds,
            p99Milliseconds,
            maximumMilliseconds);
    }

    private double GetBucketUpperBoundMilliseconds(
        int bucketIndex)
    {
        if (bucketIndex ==
            FixedLatencyHistogram.OverflowBucketIndex)
        {
            return Math.Max(
                CumulativeMaximumMilliseconds,
                GetBucketLowerBoundMilliseconds(bucketIndex));
        }

        return checked(bucketIndex + 1) *
            BucketWidthMilliseconds;
    }

    private double GetBucketLowerBoundMilliseconds(
        int bucketIndex) =>
        bucketIndex * BucketWidthMilliseconds;
}

internal readonly record struct FixedLatencyHistogramSummary(
    long SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds);
