using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsTimerResolutionTests
{
    [Fact]
    public void SuccessfulAcquisitionBalancesNativePeriodExactlyOnce()
    {
        uint begunPeriod = 0;
        uint endedPeriod = 0;
        int endCalls = 0;

        IDisposable lease = WindowsTimerResolution.TryAcquire(
            WindowsTimerResolution.InteractivePeriodMilliseconds,
            period =>
            {
                begunPeriod = period;
                return 0;
            },
            period =>
            {
                endedPeriod = period;
                endCalls++;
                return 0;
            });

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(
            WindowsTimerResolution.InteractivePeriodMilliseconds,
            begunPeriod);
        Assert.Equal(begunPeriod, endedPeriod);
        Assert.Equal(1, endCalls);
    }

    [Fact]
    public void FailedAcquisitionDoesNotEndUnstartedPeriod()
    {
        int endCalls = 0;

        using IDisposable lease = WindowsTimerResolution.TryAcquire(
            WindowsTimerResolution.InteractivePeriodMilliseconds,
            _ => 1,
            _ =>
            {
                endCalls++;
                return 0;
            });

        Assert.Equal(0, endCalls);
    }

    [Fact]
    public void ZeroPeriodIsRejectedBeforeNativeCall()
    {
        int beginCalls = 0;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => WindowsTimerResolution.TryAcquire(
                0,
                _ =>
                {
                    beginCalls++;
                    return 0;
                },
                _ => 0));

        Assert.Equal(0, beginCalls);
    }
}
