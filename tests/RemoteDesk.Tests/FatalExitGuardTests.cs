using Xunit;

namespace RemoteDesk.Tests;

public sealed class FatalExitGuardTests
{
    [Fact]
    public void BackgroundTaskFailureIsObservedAndReportedWithoutExiting()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("private input")));
        string? report = null;
        FatalExitGuard.ObserveBackgroundTaskFailure(args, message =>
        {
            Assert.True(args.Observed);
            report = message;
        });
        Assert.True(args.Observed);
        Assert.Contains("application kept running", report);
        Assert.Contains("System.IO.IOException", report);
        Assert.DoesNotContain("private input", report);
    }

    [Fact]
    public void BackgroundTaskFailureStaysObservedIfDiagnosticWriterFails()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("fixture")));
        Assert.Null(Record.Exception(() => FatalExitGuard.ObserveBackgroundTaskFailure(args,
            _ => throw new IOException("logging unavailable"))));
        Assert.True(args.Observed);
    }

    [Fact]
    public void ExceptionSummaryIncludesCodeIdentityButNotMessagesPathsOrData()
    {
        Exception error = Record.Exception(ThrowPrivateDiagnosticFixture)!;
        error.Data["private"] = "do-not-log-this-secret";
        string summary = FatalExitGuard.DescribeException(error);
        Assert.Contains("System.IO.IOException", summary);
        Assert.Contains(nameof(ThrowPrivateDiagnosticFixture), summary);
        Assert.Contains("HRESULT=", summary);
        Assert.DoesNotContain("sensitive", summary);
        Assert.DoesNotContain("do-not-log-this-secret", summary);
        Assert.DoesNotContain("C:\\", summary);
    }

    [Fact]
    public void ExceptionSummaryBoundsAggregateAndHandlesUnknownException()
    {
        var error = new AggregateException(Enumerable.Range(0, 100).Select(_ => new IOException("fixture")));
        string summary = FatalExitGuard.DescribeException(error);
        Assert.InRange(summary.Length, 1, 4096);
        Assert.True(summary.Split("HRESULT=").Length <= 9);
        Assert.Equal("unknown exception", FatalExitGuard.DescribeException(null));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowPrivateDiagnosticFixture() =>
        throw new IOException(@"sensitive C:\private\clipboard.txt");

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
