using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsHighResolutionPacingWaiterTests
{
    [Theory]
    [InlineData(1, -1)]
    [InlineData(10_000, -10_000)]
    [InlineData(200_000, -200_000)]
    public void RelativeDueTimeUsesNegativeHundredNanosecondUnits(
        long ticks,
        long expectedDueTime)
    {
        Assert.Equal(
            expectedDueTime,
            WindowsHighResolutionPacingWaiter
                .ToRelativeDueTime100Nanoseconds(
                    TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void TimerIsCreatedOnceAndReusedAcrossWaits()
    {
        var api = new RecordingWaitableTimerApi();
        var sleeps = new List<int>();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            sleeps.Add);

        waiter.Wait(
            TimeSpan.FromMilliseconds(1.25),
            CancellationToken.None);
        waiter.Wait(
            TimeSpan.FromMilliseconds(2),
            CancellationToken.None);

        Assert.Equal(1, api.CreateCalls);
        Assert.Equal(2, api.SetDueTimes.Count);
        Assert.Equal(-12_500, api.SetDueTimes[0]);
        Assert.Equal(-20_000, api.SetDueTimes[1]);
        Assert.Equal(2, api.WaitCalls);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void LongWaitIsSplitIntoAtMostTwentyMillisecondSlices()
    {
        var api = new RecordingWaitableTimerApi();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            _ => throw new InvalidOperationException(
                "Native timer should be used."));

        waiter.Wait(
            TimeSpan.FromMilliseconds(45),
            CancellationToken.None);

        Assert.Equal(
            new long[] { -200_000, -200_000, -50_000 },
            api.SetDueTimes);
        Assert.Equal(3, api.WaitCalls);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1_001, 1)]
    [InlineData(9_999, 1)]
    [InlineData(10_001, 2)]
    public void FallbackSleepRoundsUpToWholeMilliseconds(
        long ticks,
        int expectedMilliseconds)
    {
        Assert.Equal(
            expectedMilliseconds,
            WindowsHighResolutionPacingWaiter
                .ToFallbackSleepMilliseconds(
                    TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void CreateFailureFallsBackWithoutRetryingCreation()
    {
        var api = new RecordingWaitableTimerApi
        {
            CreateResult = false
        };
        var sleeps = new List<int>();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            sleeps.Add);

        waiter.Wait(
            TimeSpan.FromTicks(10_001),
            CancellationToken.None);
        waiter.Wait(
            TimeSpan.FromMilliseconds(3),
            CancellationToken.None);

        Assert.Equal(1, api.CreateCalls);
        Assert.Empty(api.SetDueTimes);
        Assert.Equal(new[] { 2, 3 }, sleeps);
        Assert.Equal(0, api.CloseCalls);
    }

    [Fact]
    public void SetFailureClosesTimerAndSafelyFallsBack()
    {
        var api = new RecordingWaitableTimerApi
        {
            SetResult = false
        };
        var sleeps = new List<int>();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            sleeps.Add);

        waiter.Wait(
            TimeSpan.FromMilliseconds(1.1),
            CancellationToken.None);
        waiter.Wait(
            TimeSpan.FromMilliseconds(2.1),
            CancellationToken.None);

        Assert.Single(api.SetDueTimes);
        Assert.Equal(0, api.WaitCalls);
        Assert.Equal(1, api.CloseCalls);
        Assert.Equal(new[] { 2, 3 }, sleeps);
    }

    [Fact]
    public void WaitFailureClosesTimerAndSafelyFallsBack()
    {
        var api = new RecordingWaitableTimerApi
        {
            WaitResult = 0xffffffff
        };
        var sleeps = new List<int>();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            sleeps.Add);

        waiter.Wait(
            TimeSpan.FromMilliseconds(1.1),
            CancellationToken.None);

        Assert.Single(api.SetDueTimes);
        Assert.Equal(1, api.WaitCalls);
        Assert.Equal(1, api.CloseCalls);
        Assert.Equal(new[] { 2 }, sleeps);
    }

    [Fact]
    public void CancellationIsCheckedBeforeAndAfterNativeWait()
    {
        var api = new RecordingWaitableTimerApi();
        var sleeps = new List<int>();
        using var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            sleeps.Add);
        using var beforeCancellation = new CancellationTokenSource();
        beforeCancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => waiter.Wait(
                TimeSpan.FromMilliseconds(1),
                beforeCancellation.Token));
        Assert.Empty(api.SetDueTimes);

        using var duringCancellation = new CancellationTokenSource();
        api.OnWait = duringCancellation.Cancel;
        Assert.Throws<OperationCanceledException>(
            () => waiter.Wait(
                TimeSpan.FromMilliseconds(1),
                duringCancellation.Token));
        Assert.Single(api.SetDueTimes);
        Assert.Empty(sleeps);
    }

    [Fact]
    public void DisposeClosesNativeHandleExactlyOnce()
    {
        var api = new RecordingWaitableTimerApi();
        var waiter = new WindowsHighResolutionPacingWaiter(
            api,
            _ => { });

        waiter.Dispose();
        waiter.Dispose();

        Assert.Equal(1, api.CreateCalls);
        Assert.Equal(1, api.CloseCalls);
        Assert.Equal(new nint(73), api.ClosedHandle);
    }

    private sealed class RecordingWaitableTimerApi :
        IWindowsHighResolutionWaitableTimerApi
    {
        public bool IsAvailable { get; set; } = true;

        public bool CreateResult { get; set; } = true;

        public bool SetResult { get; set; } = true;

        public uint WaitResult { get; set; } =
            WindowsHighResolutionPacingWaiter.WaitObject0;

        public Action? OnWait { get; set; }

        public int CreateCalls { get; private set; }

        public List<long> SetDueTimes { get; } = [];

        public int WaitCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public nint ClosedHandle { get; private set; }

        public bool TryCreate(out nint timerHandle)
        {
            CreateCalls++;
            timerHandle = new nint(73);
            return CreateResult;
        }

        public bool TrySetRelative(
            nint timerHandle,
            long dueTime100Nanoseconds)
        {
            Assert.Equal(new nint(73), timerHandle);
            SetDueTimes.Add(dueTime100Nanoseconds);
            return SetResult;
        }

        public uint Wait(nint timerHandle)
        {
            Assert.Equal(new nint(73), timerHandle);
            WaitCalls++;
            OnWait?.Invoke();
            return WaitResult;
        }

        public bool TryClose(nint timerHandle)
        {
            CloseCalls++;
            ClosedHandle = timerHandle;
            return true;
        }
    }
}
