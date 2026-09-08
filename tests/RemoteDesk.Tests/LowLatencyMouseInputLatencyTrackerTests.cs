using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class LowLatencyMouseInputLatencyTrackerTests
{
    [Fact]
    public void SnapshotKeepsWholeSessionTailCountersBeyondRollingWindow()
    {
        var tracker =
            new LowLatencyMouseInputLatencyTracker();
        long sentAt = Stopwatch.GetTimestamp();

        for (ulong sequence = 1;
            sequence <= 5000;
            sequence++)
        {
            tracker.RecordSent(sequence, sentAt);
            double milliseconds =
                sequence == 1 ? 200 : 5;
            long receivedAt = checked(
                sentAt +
                (long)Math.Round(
                    milliseconds *
                    Stopwatch.Frequency /
                    1000d));
            Assert.True(
                tracker.TryRecordAcknowledged(
                    sequence,
                    receivedAt));
        }

        LowLatencyMouseInputLatencySnapshot snapshot =
            tracker.CollectSnapshot();

        Assert.Equal(5000, snapshot.MatchedLatencySampleCount);
        Assert.InRange(
            snapshot.P99RoundTripMilliseconds,
            4,
            6);
        Assert.InRange(
            snapshot.MaximumRoundTripMilliseconds,
            199,
            201);
        Assert.Equal(1UL, snapshot.MaximumRoundTripSequence);
        Assert.Equal(1, snapshot.RoundTripsAbove25Milliseconds);
        Assert.Equal(1, snapshot.RoundTripsAbove50Milliseconds);
        Assert.Equal(1, snapshot.RoundTripsAbove100Milliseconds);
        Assert.Equal(1, snapshot.RoundTripsAbove150Milliseconds);
    }

    [Fact]
    public void SnapshotReportsTailPercentilesAcrossSessionHistory()
    {
        var tracker =
            new LowLatencyMouseInputLatencyTracker();
        long sentAt = Stopwatch.GetTimestamp();

        for (ulong sequence = 1;
            sequence <= 100;
            sequence++)
        {
            tracker.RecordSent(sequence, sentAt);
            long receivedAt = checked(
                sentAt +
                (long)Math.Round(
                    (double)sequence *
                    Stopwatch.Frequency /
                    1000d));
            Assert.True(
                tracker.TryRecordAcknowledged(
                    sequence,
                    receivedAt));
        }

        LowLatencyMouseInputLatencySnapshot snapshot =
            tracker.CollectSnapshot();

        Assert.Equal(100, snapshot.MatchedLatencySampleCount);
        Assert.InRange(
            snapshot.P95RoundTripMilliseconds,
            94,
            96);
        Assert.InRange(
            snapshot.P99RoundTripMilliseconds,
            98,
            100);
        Assert.InRange(
            snapshot.MaximumRoundTripMilliseconds,
            99,
            101);
        Assert.Equal(100UL, snapshot.MaximumRoundTripSequence);
        Assert.Equal(75, snapshot.RoundTripsAbove25Milliseconds);
        Assert.Equal(50, snapshot.RoundTripsAbove50Milliseconds);
        Assert.Equal(0, snapshot.RoundTripsAbove100Milliseconds);
        Assert.Equal(0, snapshot.RoundTripsAbove150Milliseconds);
    }
}
