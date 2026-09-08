using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ScreenCaptureServiceTests
{
    [Fact]
    public void LowLatencyStartupMovesOversizedAggregateToPrimaryScreen()
    {
        var allScreens = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (6000x3840)",
            new Rectangle(0, 0, 6000, 3840));
        var secondary = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 (2160x3840)",
            new Rectangle(-2160, 0, 2160, 3840));
        var primary = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2 主屏 (3840x2160)",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTarget selected =
            ScreenCaptureService
                .ChooseLowLatencyStartupTarget(
                    allScreens,
                    [allScreens, secondary, primary]);

        Assert.Same(primary, selected);
    }

    [Fact]
    public void LowLatencyStartupKeepsExplicitSingleScreen()
    {
        var screen = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 (5120x1440)",
            new Rectangle(0, 0, 5120, 1440),
            IsPrimary: true);

        ScreenCaptureTarget selected =
            ScreenCaptureService
                .ChooseLowLatencyStartupTarget(
                    screen,
                    [screen]);

        Assert.Same(screen, selected);
    }

    [Fact]
    public void LowLatencyStartupMovesSmallAggregateToPrimaryScreen()
    {
        var allScreens = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (3840x1080)",
            new Rectangle(0, 0, 3840, 1080));
        var primary = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏 (1920x1080)",
            new Rectangle(0, 0, 1920, 1080),
            IsPrimary: true);
        var secondary = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2 (1920x1080)",
            new Rectangle(1920, 0, 1920, 1080));

        ScreenCaptureTarget selected =
            ScreenCaptureService
                .ChooseLowLatencyStartupTarget(
                    allScreens,
                    [allScreens, primary, secondary]);

        Assert.Same(primary, selected);
    }

    [Theory]
    [InlineData(1920, 1080, 100, 1920, 1080)]
    [InlineData(1920, 1080, 50, 960, 540)]
    [InlineData(400, 200, 10, 100, 50)]
    [InlineData(400, 200, 150, 400, 200)]
    [InlineData(1, 1, 25, 1, 1)]
    public void CalculateFrameSizeUsesTheSameClampedScaleAsCapture(
        int width,
        int height,
        int scalePercent,
        int expectedWidth,
        int expectedHeight)
    {
        Size frameSize = ScreenCaptureService.CalculateFrameSize(
            new Rectangle(0, 0, width, height),
            scalePercent);

        Assert.Equal(new Size(expectedWidth, expectedHeight), frameSize);
    }

    [Fact]
    public void CalculateFrameSizeCapsOversizedMultiMonitorDesktop()
    {
        Size frameSize = ScreenCaptureService.CalculateFrameSize(
            new Rectangle(0, 0, 6000, 3840),
            scalePercent: 100);

        Assert.True(
            (long)frameSize.Width * frameSize.Height <=
            RemoteMessageCodec.MaxFramePixels);
        Assert.True(
            Math.Abs(
                frameSize.Width / (double)frameSize.Height -
                6000d / 3840d) < 0.001d);
        Assert.NotEqual(new Size(6000, 3840), frameSize);
    }

    [Fact]
    public void CalculateFrameSizeKeepsDualFourKDesktopNative()
    {
        Size frameSize = ScreenCaptureService.CalculateFrameSize(
            new Rectangle(0, 0, 7680, 2160),
            scalePercent: 100);

        Assert.Equal(new Size(7680, 2160), frameSize);
    }

    [Theory]
    [InlineData(3840, 2160, 2560, 1440)]
    [InlineData(5120, 2880, 2560, 1440)]
    [InlineData(2560, 1600, 2304, 1440)]
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1440, 2560, 1440, 2560)]
    [InlineData(2160, 3840, 1440, 2560)]
    public void QhdMaximumModeIsExactAndNeverUpscales(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        Size frameSize = ScreenCaptureService.CalculateFrameSize(
            new Rectangle(0, 0, width, height),
            ScreenCaptureService.QhdMaximumScaleMode);

        Assert.Equal(
            new Size(expectedWidth, expectedHeight),
            frameSize);
        Assert.Equal(
            "最高 1440p",
            ScreenCaptureService.FormatScaleMode(
                ScreenCaptureService.QhdMaximumScaleMode));
    }

    [Fact]
    public void ChooseDefaultTargetPrefersPrimaryScreenOverAllScreens()
    {
        ScreenCaptureTarget allScreens = new(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (6000x3840)",
            new Rectangle(0, 0, 6000, 3840));
        ScreenCaptureTarget secondary = new(
            "display-1",
            "屏幕 1 (2160x3840)",
            new Rectangle(-2160, 0, 2160, 3840));
        ScreenCaptureTarget primary = new(
            "display-2",
            "屏幕 2 主屏 (3840x2160)",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTarget target = ScreenCaptureService.ChooseDefaultTarget(
            [allScreens, secondary, primary]);

        Assert.Equal(primary, target);
    }

    [Fact]
    public void ChooseDefaultTargetFallsBackToSingleScreenBeforeAllScreens()
    {
        ScreenCaptureTarget allScreens = new(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (6000x3840)",
            new Rectangle(0, 0, 6000, 3840));
        ScreenCaptureTarget screen = new(
            "display-1",
            "屏幕 1 (3840x2160)",
            new Rectangle(0, 0, 3840, 2160));

        ScreenCaptureTarget target = ScreenCaptureService.ChooseDefaultTarget(
            [allScreens, screen]);

        Assert.Equal(screen, target);
    }

    [Fact]
    public void ChooseDefaultTargetUsesAllScreensWhenNoSingleScreenExists()
    {
        ScreenCaptureTarget allScreens = new(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (1x1)",
            new Rectangle(0, 0, 1, 1));

        ScreenCaptureTarget target = ScreenCaptureService.ChooseDefaultTarget([allScreens]);

        Assert.Equal(allScreens, target);
    }

    [Fact]
    public void FindSingleScreenEquivalentReturnsOnlyPhysicalTargetWithSameBounds()
    {
        ScreenCaptureTarget screen = new(
            "display-1",
            "屏幕 1 主屏 (3840x2160 @ 0,0)",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTarget? equivalent = ScreenCaptureService.FindSingleScreenEquivalent(
            [
                new ScreenCaptureTarget(
                    ScreenCaptureTarget.AllScreensId,
                    "所有屏幕 (3840x2160)",
                    new Rectangle(0, 0, 3840, 2160)),
                screen
            ],
            new Rectangle(0, 0, 3840, 2160));

        Assert.Equal(screen, equivalent);
    }

    [Fact]
    public void FindSingleScreenEquivalentIgnoresMultiScreenLayouts()
    {
        ScreenCaptureTarget? equivalent = ScreenCaptureService.FindSingleScreenEquivalent(
            [
                new ScreenCaptureTarget(
                    ScreenCaptureTarget.AllScreensId,
                    "所有屏幕 (7680x2160)",
                    new Rectangle(0, 0, 7680, 2160)),
                new ScreenCaptureTarget(
                    "display-1",
                    "屏幕 1 主屏 (3840x2160 @ 0,0)",
                    new Rectangle(0, 0, 3840, 2160),
                    IsPrimary: true),
                new ScreenCaptureTarget(
                    "display-2",
                    "屏幕 2 (3840x2160 @ 3840,0)",
                    new Rectangle(3840, 0, 3840, 2160))
            ],
            new Rectangle(0, 0, 7680, 2160));

        Assert.Null(equivalent);
    }

    [Fact]
    public void ResolveTargetOrDefaultMapsAllScreensToEquivalentSingleScreen()
    {
        ScreenCaptureTarget screen = new(
            "display-1",
            "屏幕 1 主屏 (3840x2160 @ 0,0)",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTarget target = ScreenCaptureService.ResolveTargetOrDefault(
            [
                new ScreenCaptureTarget(
                    ScreenCaptureTarget.AllScreensId,
                    "所有屏幕 (3840x2160)",
                    new Rectangle(0, 0, 3840, 2160)),
                screen
            ],
            ScreenCaptureTarget.AllScreensId);

        Assert.Equal(screen, target);
    }

    [Fact]
    public void ResolveTargetOrDefaultKeepsAllScreensForMultiScreenLayout()
    {
        ScreenCaptureTarget allScreens = new(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (7680x2160)",
            new Rectangle(0, 0, 7680, 2160));

        ScreenCaptureTarget target = ScreenCaptureService.ResolveTargetOrDefault(
            [
                allScreens,
                new ScreenCaptureTarget(
                    "display-1",
                    "屏幕 1 主屏 (3840x2160 @ 0,0)",
                    new Rectangle(0, 0, 3840, 2160),
                    IsPrimary: true),
                new ScreenCaptureTarget(
                    "display-2",
                    "屏幕 2 (3840x2160 @ 3840,0)",
                    new Rectangle(3840, 0, 3840, 2160))
            ],
            ScreenCaptureTarget.AllScreensId);

        Assert.Equal(allScreens, target);
    }

    [Fact]
    public void UnavailableTargetFallsBackToTheSolePhysicalScreen()
    {
        var remainingScreen = new ScreenCaptureTarget(
            "display-1",
            "屏幕 1 主屏",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTarget? fallback =
            ScreenCaptureService
                .ChooseUnambiguousFallbackTarget(
                    [
                        new ScreenCaptureTarget(
                            ScreenCaptureTarget.AllScreensId,
                            "所有屏幕",
                            remainingScreen.Bounds),
                        remainingScreen
                    ],
                    unavailableTargetId: "display-2");

        Assert.Equal(remainingScreen, fallback);
    }

    [Fact]
    public void UnavailableTargetDoesNotGuessBetweenPhysicalScreens()
    {
        ScreenCaptureTarget? fallback =
            ScreenCaptureService
                .ChooseUnambiguousFallbackTarget(
                    [
                        new ScreenCaptureTarget(
                            ScreenCaptureTarget.AllScreensId,
                            "所有屏幕",
                            new Rectangle(0, 0, 3840, 1080)),
                        new ScreenCaptureTarget(
                            "display-1",
                            "屏幕 1 主屏",
                            new Rectangle(0, 0, 1920, 1080),
                            IsPrimary: true),
                        new ScreenCaptureTarget(
                            "display-3",
                            "屏幕 3",
                            new Rectangle(1920, 0, 1920, 1080))
                    ],
                    unavailableTargetId: "display-2");

        Assert.Null(fallback);
    }

    [Fact]
    public void UnavailableTargetCanUseTheOnlyAggregateTarget()
    {
        var allScreens = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕",
            new Rectangle(0, 0, 1920, 1080));

        ScreenCaptureTarget? fallback =
            ScreenCaptureService
                .ChooseUnambiguousFallbackTarget(
                    [allScreens],
                    unavailableTargetId: "display-2");

        Assert.Equal(allScreens, fallback);
    }

    [Fact]
    public void MissingPhysicalTargetFailsClosedWithoutAggregateFallback()
    {
        Rectangle lastPhysicalBounds =
            new(3840, 0, 2560, 1440);
        var selected = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2",
            lastPhysicalBounds);
        var allScreens = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕",
            new Rectangle(0, 0, 6400, 1440));
        var remainingScreen = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        ScreenCaptureTargetAvailability availability =
            ScreenCaptureService.ResolveTargetAvailability(
                selected,
                [allScreens, remainingScreen]);

        Assert.False(availability.IsAvailable);
        Assert.Equal(
            lastPhysicalBounds,
            availability.Bounds);
        Assert.NotEqual(
            allScreens.Bounds,
            availability.Bounds);
        Assert.NotEqual(
            remainingScreen.Bounds,
            availability.Bounds);
    }

    [Fact]
    public void PhysicalTargetResolutionRecoversOnlyForTheSameDeviceId()
    {
        var selected = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2",
            new Rectangle(3840, 0, 2560, 1440));
        var reconnected = selected with
        {
            Bounds = new Rectangle(-2560, 0, 2560, 1440)
        };

        ScreenCaptureTargetAvailability wrongDisplay =
            ScreenCaptureService.ResolveTargetAvailability(
                selected,
                [
                    new ScreenCaptureTarget(
                        "\\\\.\\DISPLAY3",
                        "屏幕 3",
                        reconnected.Bounds)
                ]);
        ScreenCaptureTargetAvailability sameDisplay =
            ScreenCaptureService.ResolveTargetAvailability(
                selected,
                [reconnected]);

        Assert.False(wrongDisplay.IsAvailable);
        Assert.True(sameDisplay.IsAvailable);
        Assert.Equal(
            reconnected.Bounds,
            sameDisplay.Bounds);
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080, true)]
    [InlineData(3840, 2160, 3840, 2160, false)]
    [InlineData(0, 2160, 1920, 1080, false)]
    [InlineData(3840, 2160, 0, 1080, false)]
    public void ShouldUseDirectScaledDesktopCopyOnlyForValidScaledFrames(
        int sourceWidth,
        int sourceHeight,
        int frameWidth,
        int frameHeight,
        bool expected)
    {
        bool useScaledCopy = ScreenCaptureService.ShouldUseDirectScaledDesktopCopy(
            new Rectangle(0, 0, sourceWidth, sourceHeight),
            new Size(frameWidth, frameHeight));

        Assert.Equal(expected, useScaledCopy);
    }
}
