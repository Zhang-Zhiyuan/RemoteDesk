using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ReliableH264FramePacerTests
{
    private static bool Observe(ReliableH264FramePacer p, double fps = 18, double writeMs = 48,
        double seconds = 1, bool tcp = true) => p.Observe(TimeSpan.FromSeconds(seconds), fps, writeMs, tcp);

    private static ReliableH264FramePacer Reduced()
    {
        var p = new ReliableH264FramePacer(30, true);
        Assert.False(Observe(p));
        Assert.False(Observe(p));
        Assert.True(Observe(p));
        Assert.Equal(15, p.FramesPerSecond);
        return p;
    }

    [Fact]
    public void MeasuredRedmiBackpressureLeavesHeadroomWithoutRestartingEncoder() => Reduced();

    [Theory]
    [InlineData(30, 0.1, true)]
    [InlineData(2, 200, true)] // Static desktop; only 40% writing.
    [InlineData(18, 0.1, true)] // Slow encoder, not a slow socket.
    [InlineData(30, 48, true)] // Full rate; one buffered write is not capacity.
    [InlineData(18, 48, false)]
    public void NoReductionWithoutSustainedReliableSocketPressure(double fps, double writeMs, bool tcp)
    {
        var p = new ReliableH264FramePacer(30, true);
        for (int i = 0; i < 30; i++) Assert.False(Observe(p, fps, writeMs, tcp: tcp));
        Assert.False(p.IsLimited);
    }

    [Fact]
    public void DisabledAndHealthyModesDoNotDoublePaceTheCaptureClock()
    {
        var disabled = new ReliableH264FramePacer(30, false);
        var healthy = new ReliableH264FramePacer(30, true);
        for (int i = 0; i < 30; i++)
        {
            Assert.False(Observe(disabled));
            Assert.True(disabled.ShouldSendFrame(1));
            Assert.True(healthy.ShouldSendFrame(1));
        }
    }

    [Fact]
    public void OneLongWriteAndIsolatedSpikesDoNotReduce()
    {
        var p = new ReliableH264FramePacer(30, true);
        Assert.False(Observe(p, seconds: 20));
        Assert.False(Observe(p, 30, 0.1));
        Assert.False(Observe(p));
        Assert.False(Observe(p));
        Assert.False(Observe(p, 30, 0.1));
        Assert.False(p.IsLimited);
    }

    [Fact]
    public void AdmissionDropsOldFramesWithoutSleepingOrQueuingAndRecoveryIsImmediate()
    {
        var p = Reduced();
        long start = Stopwatch.Frequency;
        long interval = Stopwatch.Frequency / p.FramesPerSecond;
        Assert.True(p.ShouldSendFrame(start));
        Assert.False(p.ShouldSendFrame(start + interval / 2));
        Assert.True(p.ShouldSendFrame(start + interval / 2, recoveryRequested: true));
        Assert.False(p.ShouldSendFrame(start + interval));
        Assert.True(p.ShouldSendFrame(start + interval * 20));
        Assert.False(p.ShouldSendFrame(start + interval * 20 + 1));
    }

    [Fact]
    public void SlowRecoveryRequiresTenComfortableWindowsPerOneFps()
    {
        var p = Reduced();
        for (int i = 0; i < 9; i++) Assert.False(Observe(p, 15, 0.1));
        Assert.True(Observe(p, 15, 0.1));
        Assert.Equal(16, p.FramesPerSecond);
        Assert.False(Observe(p, 16, 0.1));
        Assert.False(Observe(p, 1, 0.1)); // Source silence breaks evidence.
        for (int i = 0; i < 9; i++) Assert.False(Observe(p, 16, 0.1));
        Assert.True(Observe(p, 16, 0.1));
        Assert.Equal(17, p.FramesPerSecond);
    }

    [Fact]
    public void RecoveryProbeCannotKeepQueuesFullJustBecauseNearlyAllFramesArrive()
    {
        var p = Reduced();
        for (int i = 0; i < 20; i++) Observe(p, p.FramesPerSecond, 0.1);
        Assert.Equal(17, p.FramesPerSecond);
        Assert.False(Observe(p, 17, 45));
        Assert.False(Observe(p, 17, 45));
        Assert.True(Observe(p, 17, 45));
        Assert.Equal(14, p.FramesPerSecond);
    }

    [Theory]
    [InlineData(double.NaN, 48)]
    [InlineData(18, double.PositiveInfinity)]
    [InlineData(0, 48)]
    [InlineData(18, -1)]
    public void InvalidSampleBreaksPressureEvidence(double fps, double writeMs)
    {
        var p = new ReliableH264FramePacer(30, true);
        Assert.False(Observe(p));
        Assert.False(Observe(p));
        Assert.False(Observe(p, fps, writeMs));
        Assert.False(Observe(p));
        Assert.False(p.IsLimited);
    }
}
