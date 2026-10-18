using Xunit;

namespace RemoteDesk.Tests;

public sealed class H264DecoderRecoveryTests
{
    [Fact]
    public void RetryWaitsForBothBackoffAndIndependentFrame()
    {
        var clock = new Clock();
        var recovery = new H264DecoderRecovery(clock);
        Assert.False(recovery.IsWaiting);
        Assert.False(recovery.TryBeginRetry(true));
        Assert.Equal(TimeSpan.FromSeconds(1), recovery.RecordFailure());
        Assert.False(recovery.TryBeginRetry(true));
        clock.Advance(1);
        Assert.False(recovery.TryBeginRetry(false));
        Assert.True(recovery.TryBeginRetry(true));
        Assert.False(recovery.IsWaiting);
    }

    [Fact]
    public void RepeatedFailureIsBoundedAndCannotExtendAnExistingWait()
    {
        var clock = new Clock();
        var recovery = new H264DecoderRecovery(clock);
        foreach (int seconds in new[] { 1, 2, 4, 8, 8, 8 })
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), recovery.RecordFailure());
            clock.Advance(seconds);
            Assert.Equal(TimeSpan.FromSeconds(seconds), recovery.RecordFailure());
            Assert.True(recovery.TryBeginRetry(true));
        }
    }

    [Fact]
    public void SuccessfulH264ResetsBackoffWithoutDelayingHealthyFrames()
    {
        var recovery = new H264DecoderRecovery();
        recovery.RecordFailure();
        recovery.RecordSuccess();
        Assert.False(recovery.IsWaiting);
        Assert.Equal(TimeSpan.FromSeconds(1), recovery.RecordFailure());
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
