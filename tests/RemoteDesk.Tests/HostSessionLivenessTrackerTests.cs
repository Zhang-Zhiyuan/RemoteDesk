using Xunit;

namespace RemoteDesk.Tests;

public sealed class HostSessionLivenessTrackerTests
{
    [Fact]
    public void DefaultDeadlineIsThirtySeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            HostSessionLivenessTracker
                .DefaultInboundReadTimeout);
    }

    [Fact]
    public void DeadlineExistsOnlyWhileWaitingForInboundMessage()
    {
        var time = new ManualTimeProvider();
        var tracker = new HostSessionLivenessTracker(time);

        Assert.False(tracker.GetSnapshot().IsWaitingForInboundMessage);

        long generation = tracker.BeginInboundRead();
        time.Advance(TimeSpan.FromSeconds(29));
        HostSessionLivenessSnapshot waiting = tracker.GetSnapshot();

        Assert.True(waiting.IsWaitingForInboundMessage);
        Assert.Equal(generation, waiting.ReadGeneration);
        Assert.Equal(TimeSpan.FromSeconds(29), waiting.WaitDuration);
        Assert.False(HostSessionLivenessTracker.HasInboundReadTimedOut(
            waiting.IsWaitingForInboundMessage,
            waiting.WaitDuration,
            tracker.InboundReadTimeout));

        tracker.EndInboundRead(generation);
        time.Advance(TimeSpan.FromHours(1));
        HostSessionLivenessSnapshot processing = tracker.GetSnapshot();

        Assert.False(processing.IsWaitingForInboundMessage);
        Assert.Equal(TimeSpan.Zero, processing.WaitDuration);
        Assert.False(HostSessionLivenessTracker.HasInboundReadTimedOut(
            processing.IsWaitingForInboundMessage,
            processing.WaitDuration,
            tracker.InboundReadTimeout));
    }

    [Fact]
    public void NewReadGetsFreshDeadlineAndStaleCompletionCannotDisarmIt()
    {
        var time = new ManualTimeProvider();
        var tracker = new HostSessionLivenessTracker(time);

        long first = tracker.BeginInboundRead();
        time.Advance(TimeSpan.FromSeconds(25));
        long second = tracker.BeginInboundRead();
        tracker.EndInboundRead(first);
        time.Advance(TimeSpan.FromSeconds(10));

        HostSessionLivenessSnapshot snapshot = tracker.GetSnapshot();
        Assert.NotEqual(first, second);
        Assert.True(snapshot.IsWaitingForInboundMessage);
        Assert.Equal(second, snapshot.ReadGeneration);
        Assert.Equal(TimeSpan.FromSeconds(10), snapshot.WaitDuration);
    }

    [Fact]
    public async Task ActiveReadWatchdogCompletesAtInjectedDeadline()
    {
        var time = new ManualTimeProvider();
        var requestedDelays = new List<TimeSpan>();
        var tracker = new HostSessionLivenessTracker(
            time,
            TimeSpan.FromSeconds(30),
            (delay, _) =>
            {
                requestedDelays.Add(delay);
                time.Advance(delay);
                return Task.CompletedTask;
            });
        tracker.BeginInboundRead();

        await tracker.WaitForInboundReadTimeoutAsync(
            CancellationToken.None);

        Assert.Equal(30, requestedDelays.Count);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromTicks(
                requestedDelays.Sum(delay => delay.Ticks)));
        Assert.All(
            requestedDelays,
            delay => Assert.InRange(
                delay,
                TimeSpan.Zero,
                HostSessionLivenessTracker
                    .MaximumWatchdogPollInterval));
        Assert.True(
            HostSessionLivenessTracker.HasInboundReadTimedOut(
                isWaitingForInboundMessage: true,
                waitDuration: TimeSpan.FromSeconds(30),
                inboundReadTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task CompletedReadCancelsDeadlineAndProcessingDoesNotTimeout()
    {
        var time = new ManualTimeProvider();
        var delayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new HostSessionLivenessTracker(
            time,
            TimeSpan.FromSeconds(30),
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            });
        using var stop = new CancellationTokenSource();
        long generation = tracker.BeginInboundRead();
        Task watchdog = tracker.WaitForInboundReadTimeoutAsync(
            stop.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        tracker.EndInboundRead(generation);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();

        Assert.False(watchdog.IsCompleted);
        Assert.False(tracker.GetSnapshot().IsWaitingForInboundMessage);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => watchdog);
    }

    [Fact]
    public async Task SessionWaitDistinguishesTimeoutFromCanceledWatchdog()
    {
        var capture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RemoteHostServer.ClientSessionEndReason> wait =
            RemoteHostServer.WaitForFirstClientSessionTaskAsync(
                capture.Task,
                input.Task,
                watchdog.Task);

        watchdog.TrySetResult();

        Assert.Equal(
            RemoteHostServer.ClientSessionEndReason
                .InboundReadTimedOut,
            await wait);

        capture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        input = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        watchdog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        wait = RemoteHostServer.WaitForFirstClientSessionTaskAsync(
            capture.Task,
            input.Task,
            watchdog.Task);
        watchdog.TrySetCanceled();

        Assert.Equal(
            RemoteHostServer.ClientSessionEndReason
                .InboundWatchdogTaskEnded,
            await wait);
    }

    [Theory]
    [InlineData(
        0,
        (int)RemoteHostServer.ClientSessionEndReason.CaptureTaskEnded)]
    [InlineData(
        1,
        (int)RemoteHostServer.ClientSessionEndReason.InputTaskEnded)]
    [InlineData(
        2,
        (int)RemoteHostServer.ClientSessionEndReason.InboundReadTimedOut)]
    public async Task SessionSupervisorCancelsClosesAndJoinsAfterAnyTask(
        int completedTask,
        int expectedReason)
    {
        using var sessionCancellation =
            new CancellationTokenSource();
        var capture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancelCapture =
            sessionCancellation.Token.Register(
                () => capture.TrySetCanceled(
                    sessionCancellation.Token));
        using CancellationTokenRegistration cancelInput =
            sessionCancellation.Token.Register(
                () => input.TrySetCanceled(
                    sessionCancellation.Token));
        using CancellationTokenRegistration cancelWatchdog =
            sessionCancellation.Token.Register(
                () => watchdog.TrySetCanceled(
                    sessionCancellation.Token));
        int closeCount = 0;
        Task<RemoteHostServer.ClientSessionEndReason> supervision =
            RemoteHostServer.SuperviseClientSessionTasksAsync(
                capture.Task,
                input.Task,
                watchdog.Task,
                sessionCancellation,
                () => Interlocked.Increment(ref closeCount));

        TaskCompletionSource[] tasks =
            [capture, input, watchdog];
        tasks[completedTask].TrySetResult();
        RemoteHostServer.ClientSessionEndReason endReason =
            await supervision.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            (RemoteHostServer.ClientSessionEndReason)
                expectedReason,
            endReason);
        Assert.True(sessionCancellation.IsCancellationRequested);
        Assert.Equal(1, Volatile.Read(ref closeCount));
        Assert.True(capture.Task.IsCompleted);
        Assert.True(input.Task.IsCompleted);
        Assert.True(watchdog.Task.IsCompleted);
    }

    [Fact]
    public void NonPositiveDeadlineIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostSessionLivenessTracker(
                inboundReadTimeout: TimeSpan.Zero));
    }

    [Fact]
    public void SessionCompletionDiagnosticPreservesTheRootFailure()
    {
        Task failed = Task.FromException(
            new IOException("peer reset while reading"));

        string diagnostic =
            RemoteHostServer.FormatSessionTaskCompletion(failed);

        Assert.Contains(nameof(IOException), diagnostic);
        Assert.Contains("peer reset while reading", diagnostic);
        Assert.Contains(
            "正常关闭",
            RemoteHostServer.FormatSessionTaskCompletion(
                Task.CompletedTask));
    }

    [Theory]
    [InlineData(
        LowLatencyVideoFallbackReasons.PreserveUdpInput,
        true,
        LowLatencyVideoFallbackReasons.PreserveUdpInput)]
    [InlineData(
        LowLatencyVideoFallbackReasons.PreserveUdpInput,
        false,
        1)]
    [InlineData(2, false, 2)]
    public void VideoStoppedReasonReportsWhatTheHostActuallyCommitted(
        byte requestedReason,
        bool keptUdpInput,
        byte expectedReason)
    {
        Assert.Equal(
            expectedReason,
            RemoteHostServer.ResolveLowLatencyVideoStoppedReason(
                requestedReason,
                keptUdpInput));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = TimeSpan.FromHours(1).Ticks;

        public override long TimestampFrequency =>
            TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsed));
            }

            Interlocked.Add(ref _timestamp, elapsed.Ticks);
        }
    }
}
