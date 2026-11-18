using Xunit;

namespace RemoteDesk.Tests;

public sealed class IdleH264RefreshTests
{
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
