using System.Diagnostics;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class StaticH264RecoveryTests
{
    [Theory]
    [InlineData(1, 0, 749, false)]
    [InlineData(1, 0, 750, true)]
    [InlineData(1, 0, 30000, true)]
    [InlineData(1, 1, 30000, false)]
    [InlineData(0, 0, 30000, false)]
    [InlineData(2, 1, 0, false)]
    public void OnlyUnservedRequestsOnSilentCaptureCauseRecovery(int request, int delivered, int silenceMs, bool expected)
    {
        long start = Stopwatch.Frequency;
        Assert.Equal(expected, RemoteHostServer.ShouldRecoverStaticH264KeyFrame(request, delivered,
            start, start + Stopwatch.Frequency * silenceMs / 1000));
    }

    [Fact]
    public void StartupWithoutFrameAndInvalidTimeNeverCountAsStaticRecovery()
    {
        Assert.False(RemoteHostServer.ShouldRecoverStaticH264KeyFrame(1, 0, 0, Stopwatch.Frequency * 20));
        Assert.False(RemoteHostServer.ShouldRecoverStaticH264KeyFrame(1, 0, 100, 99));
    }

    [Fact]
    public async Task StaticRecoveryUnblocksFrameReadWithoutCancellingTheSession()
    {
        using var session = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        async ValueTask<object?> Read(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }
        var pending = RemoteHostServer.ReadH264FrameUntilSelectionChangesAsync<object>(Read, capture.Token, session.Token).AsTask();
        var messages = new List<string>();
        await RemoteHostServer.MonitorStaticH264CaptureTargetAsync(() => false, () => true, capture.Cancel,
            messages.Add, capture.Token, (_, _) => Task.CompletedTask, () => true);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(result.SelectionChanged);
        Assert.Null(result.Frame);
        Assert.False(session.IsCancellationRequested);
        Assert.Contains("恢复帧请求", Assert.Single(messages));
    }

    [Fact]
    public async Task ObsoleteTargetNeverStartsRecoveryForAnotherScreen()
    {
        await RemoteHostServer.MonitorStaticH264CaptureTargetAsync(() => false, () => false,
            () => Assert.Fail("Stale target cancelled capture"), _ => { }, CancellationToken.None,
            (_, _) => Task.CompletedTask, () => throw new InvalidOperationException("Must not inspect old capture"));
    }
}
