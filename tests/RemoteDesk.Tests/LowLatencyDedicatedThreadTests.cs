using Xunit;

namespace RemoteDesk.Tests;

public sealed class LowLatencyDedicatedThreadTests
{
    [Fact]
    public async Task StartRunsSynchronousLoopOutsideThreadPool()
    {
        var observed =
            new TaskCompletionSource<ThreadObservation>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        Task worker = LowLatencyDedicatedThread.Start(
            "RemoteDesk test sender",
            () => observed.TrySetResult(
                new ThreadObservation(
                    Thread.CurrentThread.IsThreadPoolThread,
                    Thread.CurrentThread.Name,
                    Thread.CurrentThread.Priority)));

        ThreadObservation actual =
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(actual.IsThreadPoolThread);
        Assert.Equal("RemoteDesk test sender", actual.Name);
        Assert.Equal(ThreadPriority.Normal, actual.Priority);
    }

    private sealed record ThreadObservation(
        bool IsThreadPoolThread,
        string? Name,
        ThreadPriority Priority);
}
