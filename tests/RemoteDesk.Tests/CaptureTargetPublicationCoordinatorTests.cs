using Xunit;
using System.Net.Sockets;

namespace RemoteDesk.Tests;

public sealed class CaptureTargetPublicationCoordinatorTests
{
    private static readonly CaptureTargetInfo DisplayA =
        new("display-a", "屏幕 A");
    private static readonly CaptureTargetInfo DisplayB =
        new("display-b", "屏幕 B");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectionPublicationNetworkFailureEndsSessionWithoutRecoverableStatus(
        bool failDuringBarrier)
    {
        int recoverableStatusWrites = 0;
        Task inputTask = RemoteHostServer
            .ProcessControlOperationAsync(
                () => RemoteHostServer
                    .PublishCaptureTargetSelectionAsync(
                        async () =>
                        {
                            if (failDuringBarrier)
                            {
                                throw new IOException(
                                    "synthetic target barrier write failure");
                            }

                            await Task.Yield();
                            throw new IOException(
                                "synthetic target bundle write failure");
                        }),
                _ =>
                {
                    Interlocked.Increment(
                        ref recoverableStatusWrites);
                    // Prove that even a status write which would succeed is
                    // not used to hide a partially published target switch.
                    return Task.CompletedTask;
                });
        using var sessionCancellation =
            new CancellationTokenSource();
        Task captureTask = Task.Delay(
            Timeout.InfiniteTimeSpan,
            sessionCancellation.Token);
        Task watchdogTask = Task.Delay(
            Timeout.InfiniteTimeSpan,
            sessionCancellation.Token);
        int closeCount = 0;

        RemoteHostServer.ClientSessionEndReason endReason =
            await RemoteHostServer
                .SuperviseClientSessionTasksAsync(
                    captureTask,
                    inputTask,
                    watchdogTask,
                    sessionCancellation,
                    () => Interlocked.Increment(
                        ref closeCount))
                .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            RemoteHostServer.ClientSessionEndReason.InputTaskEnded,
            endReason);
        Assert.True(sessionCancellation.IsCancellationRequested);
        Assert.Equal(1, Volatile.Read(ref closeCount));
        Assert.Equal(
            0,
            Volatile.Read(ref recoverableStatusWrites));
        RemoteHostServer.CaptureTargetPublicationTransportException
            failure = Assert.IsType<
                RemoteHostServer
                    .CaptureTargetPublicationTransportException>(
                        inputTask.Exception?.GetBaseException());
        Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task OrdinaryInvalidControlIoErrorStillUsesRecoverableStatus()
    {
        int statusWrites = 0;

        await RemoteHostServer.ProcessControlOperationAsync(
            () => Task.FromException(
                new EndOfStreamException(
                    "synthetic truncated control")),
            _ =>
            {
                Interlocked.Increment(ref statusWrites);
                return Task.CompletedTask;
            });

        Assert.Equal(1, Volatile.Read(ref statusWrites));
    }

    [Fact]
    public async Task SelectionPublicationWrapsEofAndSocketFailuresAsSessionFatal()
    {
        Exception[] failures =
        [
            new EndOfStreamException("synthetic eof"),
            new SocketException(
                (int)SocketError.ConnectionReset)
        ];

        foreach (Exception failure in failures)
        {
            RemoteHostServer
                .CaptureTargetPublicationTransportException wrapped =
                await Assert.ThrowsAsync<
                    RemoteHostServer
                        .CaptureTargetPublicationTransportException>(
                    () => RemoteHostServer
                        .PublishCaptureTargetSelectionAsync(
                            () => Task.FromException(
                                failure)));
            Assert.Same(failure, wrapped.InnerException);
        }
    }

    [Fact]
    public async Task NewGenerationStaysClosedUntilItsBundleFinishes()
    {
        using var coordinator =
            new CaptureTargetPublicationCoordinator();
        var snapshot = new CaptureTargetStateSnapshot(
            DisplayB,
            IsAvailable: true,
            Generation: 2);
        var publishEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.AdvanceGeneration(snapshot.Generation);

        Task<bool> publication =
            coordinator.PublishIfCurrentAsync(
                snapshot,
                _ => true,
                async _ =>
                {
                    publishEntered.TrySetResult();
                    await releasePublish.Task;
                },
                CancellationToken.None);
        await publishEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        Task<bool> earlyFrame = coordinator
            .AdmitFrameIfCurrentAsync(
                snapshot.Generation,
                _ => true,
                _ => Task.CompletedTask,
                CancellationToken.None);
        await Task.Delay(25);
        Assert.False(earlyFrame.IsCompleted);

        releasePublish.TrySetResult();
        Assert.True(await publication.WaitAsync(
            TimeSpan.FromSeconds(1)));
        Assert.True(await earlyFrame.WaitAsync(
            TimeSpan.FromSeconds(1)));
        Assert.True(await coordinator
            .AdmitFrameIfCurrentAsync(
                snapshot.Generation,
                _ => true,
                _ => Task.CompletedTask,
                CancellationToken.None));
    }

    [Fact]
    public async Task FrameArrivingBeforePublicationWaitsAndThenAdmits()
    {
        using var coordinator =
            new CaptureTargetPublicationCoordinator();
        var snapshot = new CaptureTargetStateSnapshot(
            DisplayB,
            IsAvailable: true,
            Generation: 3);
        coordinator.AdvanceGeneration(snapshot.Generation);

        bool sent = false;
        Task<bool> frame = coordinator.AdmitFrameIfCurrentAsync(
            snapshot.Generation,
            _ => true,
            _ =>
            {
                sent = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await Task.Delay(25);
        Assert.False(frame.IsCompleted);
        Assert.False(sent);

        Assert.True(await coordinator.PublishIfCurrentAsync(
            snapshot,
            _ => true,
            _ => Task.CompletedTask,
            CancellationToken.None));
        Assert.True(await frame.WaitAsync(
            TimeSpan.FromSeconds(1)));
        Assert.True(sent);
    }

    [Fact]
    public async Task QueuedOldPublicationCannotWriteAfterNewGeneration()
    {
        using var coordinator =
            new CaptureTargetPublicationCoordinator();
        var oldSnapshot = new CaptureTargetStateSnapshot(
            DisplayA,
            IsAvailable: true,
            Generation: 1);
        var newSnapshot = new CaptureTargetStateSnapshot(
            DisplayB,
            IsAvailable: true,
            Generation: 2);
        Assert.True(await coordinator.PublishIfCurrentAsync(
            oldSnapshot,
            _ => true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        var frameEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFrame = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        Task<bool> inFlightOldFrame =
            coordinator.AdmitFrameIfCurrentAsync(
                oldSnapshot.Generation,
                _ => true,
                async _ =>
                {
                    frameEntered.TrySetResult();
                    await releaseFrame.Task;
                    writes.Add("frame-a");
                },
                CancellationToken.None);
        await frameEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        Task<bool> oldPublication =
            coordinator.PublishIfCurrentAsync(
                oldSnapshot,
                _ => true,
                _ =>
                {
                    writes.Add("old-publication");
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        Task<bool> newPublication =
            coordinator.PublishIfCurrentAsync(
                newSnapshot,
                _ => true,
                _ =>
                {
                    writes.Add("target-b");
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        releaseFrame.TrySetResult();
        Assert.True(await inFlightOldFrame);
        Assert.False(await oldPublication);
        Assert.True(await newPublication);
        Assert.Equal(
            ["frame-a", "target-b"],
            writes);
    }

    [Theory]
    [InlineData("jpeg-udp")]
    [InlineData("jpeg-tcp")]
    [InlineData("h264-udp")]
    [InlineData("h264-tcp")]
    public async Task OldFrameCannotEnterAnyTransportAfterAdvance(
        string transportPath)
    {
        using var coordinator =
            new CaptureTargetPublicationCoordinator();
        var oldSnapshot = new CaptureTargetStateSnapshot(
            DisplayA,
            IsAvailable: true,
            Generation: 7);
        Assert.True(await coordinator.PublishIfCurrentAsync(
            oldSnapshot,
            _ => true,
            _ => Task.CompletedTask,
            CancellationToken.None));

        coordinator.AdvanceGeneration(8);
        var admitted = new List<string>();
        bool oldAdmitted = await coordinator
            .AdmitFrameIfCurrentAsync(
                oldSnapshot.Generation,
                _ => true,
                _ =>
                {
                    admitted.Add(transportPath);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

        Assert.False(oldAdmitted);
        Assert.Empty(admitted);
    }

    [Fact]
    public async Task FailedPublicationDoesNotOpenNewGeneration()
    {
        using var coordinator =
            new CaptureTargetPublicationCoordinator();
        var snapshot = new CaptureTargetStateSnapshot(
            DisplayB,
            IsAvailable: true,
            Generation: 11);

        coordinator.AdvanceGeneration(snapshot.Generation);
        using var frameCancellation =
            new CancellationTokenSource();
        Task<bool> frame = coordinator
            .AdmitFrameIfCurrentAsync(
                snapshot.Generation,
                _ => true,
                _ => Task.CompletedTask,
                frameCancellation.Token);

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.PublishIfCurrentAsync(
                snapshot,
                _ => true,
                _ => Task.FromException(
                    new IOException("synthetic write failure")),
                CancellationToken.None));

        Assert.False(frame.IsCompleted);
        frameCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => frame.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
