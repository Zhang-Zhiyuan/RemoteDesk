using Xunit;

namespace RemoteDesk.Tests;

public sealed class IdleH264RefreshTests
{
    [Fact]
    public void DependentTrafficAndPausedInputCannotClearDecoderWait()
    {
        using var owner = new CancellationTokenSource();
        var refresh = new IdleH264Refresh(owner);
        refresh.BeginDecoderWait(1000, TimeSpan.FromSeconds(3));
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 2800);
        refresh.CancelInput();
        refresh.BeginDecoderWait(3000, TimeSpan.FromSeconds(3));
        Assert.False(refresh.TakeDueDecoderRetry(3999));
        Assert.True(refresh.TakeDueDecoderRetry(4000));
        Assert.False(refresh.TakeDueDecoderRetry(5999));
        Assert.True(refresh.TakeDueDecoderRetry(6000));
        Assert.True(refresh.TakeDueDecoderRetry(10_000));
        Assert.True(refresh.TakeDueDecoderRetry(18_000));
        Assert.True(refresh.TakeDueDecoderRetry(26_000));
        refresh.EndDecoderWait();
        Assert.False(refresh.TakeDueDecoderRetry(90_000));
        refresh.BeginDecoderWait(91_000, TimeSpan.Zero);
        refresh.Reset();
        Assert.False(refresh.TakeDueDecoderRetry(100_000));
    }

    [Fact]
    public void StaticDesktopNeverRequestsUntilInputAndAnOrdinaryNewFrameCancelsTheWait()
    {
        using var owner = new CancellationTokenSource();
        var refresh = new IdleH264Refresh(owner);
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 0);
        Assert.Equal(0, refresh.TakeDueRequest(600_000));
        refresh.ObserveInput(600_001);
        Assert.Equal(0, refresh.TakeDueRequest(600_500));
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 600_600);
        Assert.Equal(0, refresh.TakeDueRequest(620_000));
    }

    [Fact]
    public void ContinuousMovesCannotDeferTheFirstRequestOrGenerateEndlessRetries()
    {
        using var owner = new CancellationTokenSource();
        var refresh = new IdleH264Refresh(owner);
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 1);
        var attempts = new List<(int time, int attempt)>();
        for (int t = 10_000; t <= 28_000; t += 10)
        {
            refresh.ObserveInput(t);
            if (refresh.TakeDueRequest(t) is var attempt && attempt > 0) attempts.Add((t, attempt));
        }
        Assert.Equal(new[] { (11_000, 1), (14_000, 2) }, attempts);
        refresh.ObserveInput(60_000); // A later genuine interaction can retry on the same session.
        Assert.Equal(1, refresh.TakeDueRequest(61_000));
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 61_010);
        Assert.Equal(0, refresh.TakeDueRequest(70_000));
    }

    [Fact]
    public void FirstLostRefreshRetriesSoonerOnlyWithRecentFastPongsAndAnIdleReceivePath()
    {
        using var owner = new CancellationTokenSource();
        foreach (string condition in new[] { "fast", "single", "slow", "stale", "in-progress", "long-gap" })
        {
            var refresh = new IdleH264Refresh(owner);
            refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 1);
            refresh.ObserveRoundTrip(TimeSpan.FromMilliseconds(30), 5000);
            if (condition != "single")
                refresh.ObserveRoundTrip(TimeSpan.FromMilliseconds(condition == "slow" ? 320 : 30), condition == "long-gap" ? 24_000 : 10_000);
            int input = condition is "stale" or "long-gap" ? 28_000 : 12_000;
            refresh.ObserveInput(input);
            Assert.Equal(0, refresh.TakeDueRequest(input + 999));
            Assert.Equal(1, refresh.TakeDueRequest(input + 1000));
            Assert.Equal(0, refresh.TakeDueRequest(input + 1999));
            Assert.Equal(condition == "fast" ? 2 : 0, refresh.TakeDueRequest(input + 2000, condition == "in-progress"));
            Assert.Equal(condition == "fast" ? 0 : 2, refresh.TakeDueRequest(input + 4000, condition == "in-progress"));
            Assert.Equal(0, refresh.TakeDueRequest(input + 10_000));
        }
    }

    [Fact]
    public void PauseTargetChangeAndOtherCodecsDoNotCarryStaleWork()
    {
        using var owner = new CancellationTokenSource();
        var refresh = new IdleH264Refresh(owner);
        refresh.ObserveFrame(RemoteFrameEncoding.H264AnnexB, 1);
        refresh.ObserveInput(8_000);
        refresh.CancelInput();
        Assert.Equal(0, refresh.TakeDueRequest(10_000));
        refresh.ObserveInput(11_000);
        Assert.Equal(1, refresh.TakeDueRequest(12_000));
        refresh.Reset();
        refresh.ObserveInput(20_000);
        Assert.Equal(0, refresh.TakeDueRequest(22_000));
        refresh.ObserveFrame(RemoteFrameEncoding.Jpeg, 23_000);
        refresh.ObserveInput(28_000);
        Assert.Equal(0, refresh.TakeDueRequest(30_000));
    }
}
