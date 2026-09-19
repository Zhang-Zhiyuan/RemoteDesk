using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardSnapshotSendGateTests
{
    [Fact]
    public async Task AQueuedSendTimesOutWithoutCancellingItOrStartingMoreQueuedSends()
    {
        var gate = new ClipboardSnapshotSendGate();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        int sends = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.TrySendAsync(() =>
        {
            sends++;
            return pending.Task;
        }, timeout.Token));
        Assert.True(gate.HasPendingSend);
        Assert.False(pending.Task.IsCompleted);
        for (int request = 0; request < 5; request++)
            Assert.False(await gate.TrySendAsync(() => { sends++; return Task.FromResult(true); }, CancellationToken.None));
        Assert.Equal(1, sends);
        pending.SetResult(true);
        Assert.True(SpinWait.SpinUntil(() => !gate.HasPendingSend, TimeSpan.FromSeconds(2)));
        Assert.True(await gate.TrySendAsync(() => { sends++; return Task.FromResult(true); }, CancellationToken.None));
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task LateSendFailureIsObservedAndReleasesTheGate()
    {
        var gate = new ClipboardSnapshotSendGate();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        Task<bool> request = gate.TrySendAsync(() => pending.Task, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        pending.SetException(new IOException("late transport failure"));
        Assert.True(SpinWait.SpinUntil(() => !gate.HasPendingSend, TimeSpan.FromSeconds(2)));
        Assert.True(await gate.TrySendAsync(() => Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentRequestDoesNotWaitBehindAnExistingSend()
    {
        var gate = new ClipboardSnapshotSendGate();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = gate.TrySendAsync(() => pending.Task, CancellationToken.None);
        bool invoked = false;
        Assert.False(await gate.TrySendAsync(() => { invoked = true; return Task.FromResult(true); }, CancellationToken.None));
        Assert.False(invoked);
        pending.SetResult(false);
        Assert.False(await first);
        Assert.False(gate.HasPendingSend);
    }

    [Fact]
    public async Task SynchronousSendFactoryFailureDoesNotLeaveTheGateOccupied()
    {
        var gate = new ClipboardSnapshotSendGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.TrySendAsync(
            () => throw new InvalidOperationException("cannot send"), CancellationToken.None));
        Assert.False(gate.HasPendingSend);
        Assert.True(await gate.TrySendAsync(() => Task.FromResult(true), CancellationToken.None));
    }

    [Fact]
    public async Task ImmediateAsynchronousFailureDoesNotLeaveTheGateOccupied()
    {
        var gate = new ClipboardSnapshotSendGate();
        await Assert.ThrowsAsync<IOException>(() => gate.TrySendAsync(
            () => Task.FromException<bool>(new IOException("failed")), CancellationToken.None));
        Assert.False(gate.HasPendingSend);
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverStartsASend()
    {
        var gate = new ClipboardSnapshotSendGate();
        bool invoked = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.TrySendAsync(
            () => { invoked = true; return Task.FromResult(true); }, new CancellationToken(true)));
        Assert.False(invoked);
        Assert.False(gate.HasPendingSend);
    }
}
