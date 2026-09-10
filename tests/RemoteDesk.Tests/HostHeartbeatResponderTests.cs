using Xunit;

namespace RemoteDesk.Tests;

public sealed class HostHeartbeatResponderTests
{
    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task BlockedWriterDoesNotBlockRequestsAndFloodKeepsOnePendingReply()
    {
        var entered = Signal();
        var release = Signal();
        var second = Signal();
        int sends = 0;
        await using var responder = new HostHeartbeatResponder(async token =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            else
            {
                second.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        }, CancellationToken.None);

        Assert.True(responder.Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(() =>
        {
            for (int i = 0; i < 10_000; i++) Assert.True(responder.Request());
        }).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref sends));
        release.SetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task DisposeCancelsBlockedWriteAndDiscardsPendingReplies()
    {
        var entered = Signal();
        int sends = 0;
        var responder = new HostHeartbeatResponder(async token =>
        {
            Interlocked.Increment(ref sends);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, CancellationToken.None);
        responder.Request();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        responder.Request();
        await responder.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sends);
        Assert.False(responder.Request());
        Assert.Null(responder.Failure);
    }

    [Fact]
    public async Task FailedWriteCancelsSessionReaderAndPreservesFailure()
    {
        var failure = new IOException("synthetic blocked uplink failure");
        await using var responder = new HostHeartbeatResponder(
            _ => Task.FromException(failure), CancellationToken.None);
        var interrupted = Signal();
        using var registration = responder.Token.Register(() => interrupted.TrySetResult());
        responder.Request();
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(failure, responder.Failure);
        Assert.False(responder.Request());
    }

    [Fact]
    public async Task ParentCancellationStopsIdleWorkerWithoutSendingOrFailure()
    {
        using var parent = new CancellationTokenSource();
        int sends = 0;
        var responder = new HostHeartbeatResponder(_ =>
        {
            Interlocked.Increment(ref sends);
            return Task.CompletedTask;
        }, parent.Token);
        parent.Cancel();
        Assert.False(responder.Request());
        await responder.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, sends);
        Assert.Null(responder.Failure);
    }
}
