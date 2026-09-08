using Xunit;

namespace RemoteDesk.Tests;

public sealed class FatalExitGuardTests
{
    [Fact]
    public void BeginFatalExitStartsOnlyOnce()
    {
        int exitStarted = 0;
        int applicationExitRequests = 0;

        bool first = FatalExitGuard.BeginFatalExit(
            () => applicationExitRequests++,
            _ => { },
            TimeSpan.FromMilliseconds(1),
            ref exitStarted);
        bool second = FatalExitGuard.BeginFatalExit(
            () => applicationExitRequests++,
            _ => { },
            TimeSpan.FromMilliseconds(1),
            ref exitStarted);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, applicationExitRequests);
    }

    [Fact]
    public async Task BeginFatalExitInvokesProcessExitAfterDelay()
    {
        int exitStarted = 0;
        int exitCode = int.MinValue;
        using var exitSignal = new SemaphoreSlim(0, 1);

        FatalExitGuard.BeginFatalExit(
            () => { },
            code =>
            {
                exitCode = code;
                exitSignal.Release();
            },
            TimeSpan.FromMilliseconds(1),
            ref exitStarted);

        Assert.True(await exitSignal.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void BeginFatalExitSwallowsApplicationExitCoordinationErrors()
    {
        int exitStarted = 0;

        Exception? exception = Record.Exception(() =>
            FatalExitGuard.BeginFatalExit(
                () => throw new InvalidOperationException("closing"),
                _ => { },
                TimeSpan.FromMilliseconds(1),
                ref exitStarted));

        Assert.Null(exception);
    }
}
