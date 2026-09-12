using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class AdaptiveH264ResolutionControllerTests
{
    [Fact]
    public void TcpWarmupOfAnUdpCapableSessionCannotLowerItsSpatialQuality()
    {
        Assert.False(RemoteHostServer.ShouldAdaptReliableH264Resolution(true,
            RemoteDeviceCapabilities.LowLatencyUdpVideo));
        Assert.False(RemoteHostServer.ShouldAdaptReliableH264Resolution(false, RemoteDeviceCapabilities.None));
        Assert.True(RemoteHostServer.ShouldAdaptReliableH264Resolution(true,
            RemoteViewerClient.RelayTransportViewerCapabilities));
    }

    private static AdaptiveH264ResolutionController Create(bool enabled = true) =>
        new(new Size(3840, 2160), 30, enabled);

    private static bool Observe(AdaptiveH264ResolutionController controller, double fps = 9.5,
        double writeMs = 102, bool tcp = true, double seconds = 1, double mbps = 19) =>
        controller.Observe(TimeSpan.FromSeconds(seconds), fps, writeMs, mbps, tcp);

    private static void Reduce(AdaptiveH264ResolutionController controller)
    {
        Assert.False(Observe(controller));
        Assert.False(Observe(controller));
        Assert.True(Observe(controller));
        Assert.Equal(new Size(1920, 1080), controller.OutputSize);
    }

    [Fact]
    public void RealGuestPressureReducesAfterThreeWindowsWithoutChangingTheCeiling()
    {
        var controller = Create();
        Reduce(controller);
        Assert.Equal(new Size(3840, 2160), controller.RequestedSize);
        for (int i = 0; i < 100; i++) Assert.False(Observe(controller));
        Assert.Equal(new Size(1920, 1080), controller.OutputSize);
    }

    [Theory]
    [InlineData(30, 0.1, true)] // Healthy LAN.
    [InlineData(2, 0.1, true)] // Static source or encoder-bound, not network-bound.
    [InlineData(9, 102, false)] // UDP enqueue is not reliable-video backpressure.
    [InlineData(30, 102, true)] // A brief/buffered write does not prove inadequate delivery.
    public void UnrelatedPressureDoesNotReduce(double fps, double writeMs, bool tcp)
    {
        var controller = Create();
        for (int i = 0; i < 20; i++) Assert.False(Observe(controller, fps, writeMs, tcp));
        Assert.False(controller.IsReduced);
    }

    [Fact]
    public void DisabledAdaptivePreservesRequestedSize()
    {
        var controller = Create(enabled: false);
        for (int i = 0; i < 20; i++) Assert.False(Observe(controller));
        Assert.False(controller.IsReduced);
    }

    [Fact]
    public void IsolatedSpikesAndOneLongBlockedWriteDoNotTriggerReduction()
    {
        var controller = Create();
        Assert.False(Observe(controller, seconds: 20));
        Assert.False(Observe(controller, fps: 30, writeMs: 0.1));
        Assert.False(Observe(controller));
        Assert.False(Observe(controller));
        Assert.False(Observe(controller, fps: 30, writeMs: 0.1));
        Assert.False(controller.IsReduced);
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(2160, 3840, 1080, 1920)]
    [InlineData(3440, 1440, 1920, 802)]
    [InlineData(2560, 1600, 1728, 1080)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(734, 1600, 734, 1600)]
    public void FullHdCeilingPreservesAspectOrientationAndNeverUpscales(int width, int height, int x, int y)
    {
        Assert.Equal(new Size(x, y), AdaptiveH264ResolutionController.FitFullHd(new Size(width, height)));
    }

    [Fact]
    public void AlreadySmallDesktopIsNeverReduced()
    {
        var controller = new AdaptiveH264ResolutionController(new Size(1280, 720), 30, true);
        for (int i = 0; i < 20; i++) Assert.False(Observe(controller));
    }

    [Fact]
    public void RecoveryRequiresNativeRateHeadroomNotMerelySmooth1080p()
    {
        var controller = Create();
        Reduce(controller);
        for (int i = 0; i < 120; i++) Assert.False(Observe(controller, fps: 30, writeMs: 10));
        Assert.True(controller.IsReduced);
        for (int i = 0; i < 9; i++) Assert.False(Observe(controller, fps: 30, writeMs: 1));
        Assert.True(Observe(controller, fps: 30, writeMs: 1));
        Assert.False(controller.IsReduced);
    }

    [Fact]
    public void FailedUpgradeDoesNotPeriodicallyCongestTheSameSessionAgain()
    {
        var controller = Create();
        Reduce(controller);
        for (int i = 0; i < 44; i++) Assert.False(Observe(controller, fps: 30, writeMs: 1));
        Assert.True(Observe(controller, fps: 30, writeMs: 1));
        Reduce(controller);
        for (int i = 0; i < 3600; i++) Assert.False(Observe(controller, fps: 30, writeMs: 1));
        Assert.True(controller.IsReduced);
        // A new session still starts with the user's configured native ceiling.
        Assert.False(Create().IsReduced);
    }

    [Fact]
    public void MissingOrNonFiniteMetricsResetEvidence()
    {
        var controller = Create();
        Assert.False(Observe(controller));
        Assert.False(Observe(controller));
        Assert.False(Observe(controller, fps: double.NaN));
        Assert.False(Observe(controller));
        Assert.False(Observe(controller));
        Assert.False(Observe(controller, mbps: 0));
        Assert.False(controller.IsReduced);
    }
}
