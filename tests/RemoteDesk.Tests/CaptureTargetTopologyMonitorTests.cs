using Xunit;

namespace RemoteDesk.Tests;

public sealed class CaptureTargetTopologyMonitorTests
{
    private static readonly CaptureTargetInfo DisplayA =
        new("display-a", "屏幕 A");
    private static readonly CaptureTargetInfo DisplayB =
        new("display-b", "屏幕 B");

    [Fact]
    public void DuplicateAvailabilityObservationDoesNotSpam()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayA, DisplayB],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: true,
                Generation: 4));

        CaptureTargetTopologyPublication unavailable =
            state.Observe(
                [DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: false,
                    Generation: 5));
        CaptureTargetTopologyPublication duplicate =
            state.Observe(
                [DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: false,
                    Generation: 5));
        CaptureTargetTopologyPublication recovered =
            state.Observe(
                [DisplayA, DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: true,
                    Generation: 6));

        Assert.True(unavailable.AvailabilityChanged);
        Assert.NotNull(unavailable.Targets);
        Assert.False(duplicate.HasChanges);
        Assert.True(recovered.AvailabilityChanged);
        Assert.NotNull(recovered.Targets);
    }

    [Fact]
    public void UnavailableAToAvailableBPublishesRecovery()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayB],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: false,
                Generation: 7));

        CaptureTargetTopologyPublication publication =
            state.Observe(
                [DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayB,
                    IsAvailable: true,
                    Generation: 8));

        Assert.True(publication.AvailabilityChanged);
        Assert.True(publication.Snapshot.IsAvailable);
    }

    [Fact]
    public void AvailableAToAvailableBDoesNotPublishAvailability()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayA, DisplayB],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: true,
                Generation: 7));

        CaptureTargetTopologyPublication publication =
            state.Observe(
                [DisplayA, DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayB,
                    IsAvailable: true,
                    Generation: 8));

        Assert.True(publication.HasChanges);
        Assert.False(publication.AvailabilityChanged);
        Assert.True(publication.GenerationChanged);
    }

    [Fact]
    public void SameTargetBoundsGenerationChangePublishesTransition()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayA, DisplayB],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: true,
                Generation: 12));

        CaptureTargetTopologyPublication publication =
            state.Observe(
                [DisplayA, DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: true,
                    Generation: 13));

        Assert.True(publication.HasChanges);
        Assert.True(publication.GenerationChanged);
        Assert.False(publication.AvailabilityChanged);
        Assert.Null(publication.Targets);
    }

    [Fact]
    public void UnrelatedDisplayListChangeDoesNotAdvanceGeneration()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayA],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: true,
                Generation: 21));

        CaptureTargetTopologyPublication publication =
            state.Observe(
                [DisplayA, DisplayB],
                new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: true,
                    Generation: 21));

        Assert.True(publication.HasChanges);
        Assert.NotNull(publication.Targets);
        Assert.False(publication.GenerationChanged);
        Assert.False(publication.AvailabilityChanged);
    }

    [Fact]
    public async Task PublicationFailureEndsTopologyMonitor()
    {
        var state = new CaptureTargetTopologyMonitorState(
            [DisplayA],
            new CaptureTargetStateSnapshot(
                DisplayA,
                IsAvailable: true,
                Generation: 1));
        int delayCount = 0;
        int publishCount = 0;
        var expected = new IOException("synthetic write failure");

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            RemoteHostServer.RunCaptureTargetTopologyMonitorAsync(
                state,
                () => [
                    new ScreenCaptureTarget(
                        DisplayB.Id,
                        DisplayB.DisplayName,
                        new Rectangle(0, 0, 1920, 1080))
                ],
                _ => new CaptureTargetStateSnapshot(
                    DisplayA,
                    IsAvailable: false,
                    Generation: 2),
                (_, _) =>
                {
                    Interlocked.Increment(ref publishCount);
                    return Task.FromException(expected);
                },
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None,
                (_, _) =>
                {
                    Interlocked.Increment(ref delayCount);
                    return Task.CompletedTask;
                }));

        Assert.Same(expected, actual);
        Assert.Equal(1, delayCount);
        Assert.Equal(1, publishCount);
    }

    [Fact]
    public async Task TopologyTaskFailureCancelsAndClosesSession()
    {
        using var cancellation =
            new CancellationTokenSource();
        Task capture = Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellation.Token);
        Task input = Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellation.Token);
        Task watchdog = Task.Delay(
            Timeout.InfiniteTimeSpan,
            cancellation.Token);
        Task topology = Task.FromException(
            new IOException("synthetic write failure"));
        int closeCount = 0;

        RemoteHostServer.ClientSessionEndReason reason =
            await RemoteHostServer
                .SuperviseClientSessionTasksAsync(
                    capture,
                    topology,
                    input,
                    watchdog,
                    cancellation,
                    () => Interlocked.Increment(
                        ref closeCount))
                .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            RemoteHostServer.ClientSessionEndReason
                .CaptureTargetTopologyTaskEnded,
            reason);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, closeCount);
        Assert.True(capture.IsCompleted);
        Assert.True(input.IsCompleted);
        Assert.True(watchdog.IsCompleted);
    }
}
