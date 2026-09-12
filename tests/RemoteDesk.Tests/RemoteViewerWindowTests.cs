using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerWindowTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("已连接", "", "已连接")]
    [InlineData(
        "",
        "59.9 FPS | 输入落地 7.0ms",
        "59.9 FPS | 输入落地 7.0ms")]
    public void BuildCopyableStatusHandlesMissingSections(
        string status,
        string details,
        string expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.BuildCopyableStatus(
                status,
                details));
    }

    [Fact]
    public void BuildCopyableStatusIncludesPerformanceDetails()
    {
        Assert.Equal(
            "已加密连接" +
            Environment.NewLine +
            "H.264/MF（硬解） 1920x1080 | 59.9 FPS | 收→显 0.7ms | 输入落地 7.0ms",
            RemoteViewerWindow.BuildCopyableStatus(
                "已加密连接",
                "H.264/MF（硬解） 1920x1080 | 59.9 FPS | 收→显 0.7ms | 输入落地 7.0ms"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownFrameTimingIsNotIncludedInAverage(double milliseconds)
    {
        Assert.False(RemoteViewerWindow.IsKnownFrameTiming(milliseconds));
    }

    [Fact]
    public void FrameTimingAverageDistinguishesUnknownFromZero()
    {
        Assert.Equal(
            "—",
            RemoteViewerWindow.FormatFrameTimingAverage(
                totalMilliseconds: 0,
                sampleCount: 0));
        Assert.EndsWith(
            "ms",
            RemoteViewerWindow.FormatFrameTimingAverage(
                totalMilliseconds: 25,
                sampleCount: 2),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, double.NaN, "")]
    [InlineData(1, -1, "")]
    [InlineData(3, 7.25, " | 输入落地 7.2ms")]
    public void MouseInputAppliedLatencyRequiresMatchedFiniteSample(
        long matchedSamples,
        double smoothedMilliseconds,
        string expected)
    {
        var snapshot =
            new LowLatencyMouseInputLatencySnapshot(
                SentMouseMoveCount: 4,
                AcknowledgedMouseMoveCount: matchedSamples,
                MatchedLatencySampleCount: matchedSamples,
                LatestSentSequence: 4,
                LatestAcknowledgedSequence:
                    checked((ulong)Math.Max(0, matchedSamples)),
                LatestRoundTripMilliseconds:
                    smoothedMilliseconds,
                SmoothedRoundTripMilliseconds:
                    smoothedMilliseconds,
                P95RoundTripMilliseconds:
                    smoothedMilliseconds,
                P99RoundTripMilliseconds:
                    smoothedMilliseconds,
                MaximumRoundTripMilliseconds:
                    smoothedMilliseconds,
                MaximumRoundTripSequence: 0,
                RoundTripsAbove25Milliseconds: 0,
                RoundTripsAbove50Milliseconds: 0,
                RoundTripsAbove100Milliseconds: 0,
                RoundTripsAbove150Milliseconds: 0);

        Assert.Equal(
            expected,
            RemoteViewerWindow
                .FormatMouseInputAppliedLatency(snapshot));
    }

    [Fact]
    public void ViewerPipelineTimingMeasuresOnlyLocalFreshnessStages()
    {
        long receivedAt = Stopwatch.GetTimestamp();
        long tenMilliseconds =
            Stopwatch.Frequency / 100;
        long twoMilliseconds =
            Stopwatch.Frequency / 500;
        long fiveMilliseconds =
            Stopwatch.Frequency / 200;
        long decodeStartedAt =
            receivedAt + tenMilliseconds;
        long decodedAt =
            decodeStartedAt + twoMilliseconds;
        long presentedAt =
            decodedAt + fiveMilliseconds;
        var frame =
            new RemoteFrame(
                64,
                64,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                [1],
                0,
                1,
                0,
                0,
                receivedAt);

        bool measured =
            RemoteViewerWindow
                .TryCalculateViewerFramePipelineTiming(
                    frame,
                    decodeStartedAt,
                    decodedAt,
                    presentedAt,
                    out RemoteViewerWindow
                        .ViewerFramePipelineTiming timing);

        Assert.True(measured);
        Assert.InRange(
            timing.ReceiveToDecodeMilliseconds,
            9.9,
            10.1);
        Assert.InRange(
            timing.DecodeToPresentMilliseconds,
            4.9,
            5.1);
        Assert.InRange(
            timing.ReceiveToPresentMilliseconds,
            16.9,
            17.1);
    }

    [Fact]
    public void ViewerPipelineTimingRejectsMissingOrOutOfOrderTimestamps()
    {
        var frame =
            new RemoteFrame(
                64,
                64,
                RemoteFrameEncoding.Jpeg,
                RemoteFrameFlags.KeyFrame,
                [1],
                0,
                1,
                0,
                0);

        Assert.False(
            RemoteViewerWindow
                .TryCalculateViewerFramePipelineTiming(
                    frame,
                    decodeStartedAtTimestamp: 30,
                    decodedAtTimestamp: 20,
                    presentedAtTimestamp: 10,
                    out _));
    }

    [Theory]
    [InlineData(MouseButtons.Right, Keys.Escape)]
    [InlineData(MouseButtons.Middle, Keys.Home)]
    public void AndroidMouseShortcutCreatesBalancedKeyPair(MouseButtons button, Keys expectedKey)
    {
        bool created = RemoteViewerWindow.TryCreateAndroidMouseShortcutCommands(
            button,
            out RemoteInputCommand keyDown,
            out RemoteInputCommand keyUp);

        Assert.True(created);
        Assert.Equal(RemoteInputKind.KeyDown, keyDown.Kind);
        Assert.Equal((int)expectedKey, keyDown.Data);
        Assert.Equal(RemoteInputKind.KeyUp, keyUp.Kind);
        Assert.Equal((int)expectedKey, keyUp.Data);
    }

    [Fact]
    public void AndroidMouseShortcutIgnoresLeftButton()
    {
        bool created = RemoteViewerWindow.TryCreateAndroidMouseShortcutCommands(
            MouseButtons.Left,
            out RemoteInputCommand keyDown,
            out RemoteInputCommand keyUp);

        Assert.False(created);
        Assert.Equal(default, keyDown);
        Assert.Equal(default, keyUp);
    }

    [Theory]
    [InlineData(
        RemoteViewerWindow.WmKeyDown,
        (int)Keys.A,
        0x001E0000,
        (int)RemoteInputKind.KeyDown,
        0x1E,
        (int)RemoteKeyboardFlags.HasScanCode)]
    [InlineData(
        RemoteViewerWindow.WmKeyUp,
        (int)Keys.Insert,
        0xC1520000,
        (int)RemoteInputKind.KeyUp,
        0x52,
        (int)(RemoteKeyboardFlags.HasScanCode |
              RemoteKeyboardFlags.Extended))]
    [InlineData(
        RemoteViewerWindow.WmSysKeyDown,
        (int)Keys.Menu,
        0x20380000,
        (int)RemoteInputKind.KeyDown,
        0x38,
        (int)RemoteKeyboardFlags.HasScanCode)]
    public void NativeKeyboardMessagePreservesPhysicalScanCode(
        int messageId,
        int virtualKey,
        long lParam,
        int expectedKind,
        int expectedScanCode,
        int expectedFlags)
    {
        bool parsed =
            RemoteViewerWindow.TryCreateRawKeyboardCommand(
                messageId,
                virtualKey,
                (nint)lParam,
                out RemoteInputCommand command);

        Assert.True(parsed);
        Assert.Equal(
            (RemoteInputKind)expectedKind,
            command.Kind);
        Assert.Equal(virtualKey, command.Data);
        Assert.Equal(expectedScanCode, command.X);
        Assert.Equal(expectedFlags, command.Y);
    }

    [Fact]
    public void NativeKeyboardMessageRejectsNonKeyboardMessages()
    {
        Assert.False(
            RemoteViewerWindow.TryCreateRawKeyboardCommand(
                messageId: 0x0200,
                virtualKey: (int)Keys.A,
                lParam: 0,
                out _));
    }

    [Theory]
    [InlineData(
        RemoteViewerWindow.WmKeyDown,
        (int)Keys.RControlKey,
        0x1D,
        0x01u,
        (int)RemoteInputKind.KeyDown,
        (int)(RemoteKeyboardFlags.HasScanCode |
              RemoteKeyboardFlags.Extended))]
    [InlineData(
        RemoteViewerWindow.WmSysKeyUp,
        (int)Keys.LMenu,
        0x38,
        0u,
        (int)RemoteInputKind.KeyUp,
        (int)RemoteKeyboardFlags.HasScanCode)]
    public void LowLevelKeyboardMessagePreservesPhysicalScanCode(
        int messageId,
        int virtualKey,
        int scanCode,
        uint nativeFlags,
        int expectedKind,
        int expectedFlags)
    {
        Assert.True(
            RemoteViewerWindow.TryCreateLowLevelKeyboardCommand(
                messageId,
                virtualKey,
                scanCode,
                nativeFlags,
                out RemoteInputCommand command));
        Assert.Equal(
            (RemoteInputKind)expectedKind,
            command.Kind);
        Assert.Equal(virtualKey, command.Data);
        Assert.Equal(scanCode, command.X);
        Assert.Equal(expectedFlags, command.Y);
    }

    [Fact]
    public void KeyboardHookConsumesOnlyRemoteDeskInjectedInput()
    {
        const uint injectedFlag = 0x10;

        Assert.True(
            RemoteViewerWindow
                .ShouldConsumeOwnInjectedKeyboardEvent(
                    injectedFlag,
                    InputInjector.InjectedInputMarker));
        Assert.False(
            RemoteViewerWindow
                .ShouldConsumeOwnInjectedKeyboardEvent(
                    injectedFlag,
                    extraInfo: 0));
        Assert.False(
            RemoteViewerWindow
                .ShouldConsumeOwnInjectedKeyboardEvent(
                    injectedFlag,
                    InputInjector.InjectedInputMarker,
                    allowOwnInjectedEventsForTests: true));
        Assert.False(
            RemoteViewerWindow
                .ShouldConsumeOwnInjectedKeyboardEvent(
                    flags: 0,
                    InputInjector.InjectedInputMarker));
    }

    [Theory]
    [InlineData(
        (int)Keys.ControlKey,
        (int)Keys.LControlKey,
        true)]
    [InlineData(
        (int)Keys.LControlKey,
        (int)Keys.RControlKey,
        true)]
    [InlineData(
        (int)Keys.ShiftKey,
        (int)Keys.RShiftKey,
        true)]
    [InlineData(
        (int)Keys.Menu,
        (int)Keys.RMenu,
        true)]
    [InlineData(
        (int)Keys.LControlKey,
        (int)Keys.LMenu,
        false)]
    [InlineData(
        (int)Keys.A,
        (int)Keys.B,
        false)]
    public void ModifierFamiliesAvoidDuplicateSyntheticKeyDown(
        int left,
        int right,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .AreEquivalentModifierVirtualKeys(
                    left,
                    right));
    }

    [Fact]
    public void StatusBarLayoutReservesStableDetailsRegionWhenWide()
    {
        (Rectangle statusBounds, Rectangle detailsBounds) = RemoteViewerWindow.CalculateStatusBarLayout(
            new Size(800, 30),
            new Padding(10, 0, 10, 0),
            hasDetails: true);

        Assert.Equal(new Rectangle(10, 0, 374, 30), statusBounds);
        Assert.Equal(new Rectangle(400, 0, 390, 30), detailsBounds);
    }

    [Fact]
    public void StatusBarLayoutUsesFullContentWidthWithoutDetails()
    {
        (Rectangle statusBounds, Rectangle detailsBounds) = RemoteViewerWindow.CalculateStatusBarLayout(
            new Size(800, 30),
            new Padding(10, 0, 10, 0),
            hasDetails: false);

        Assert.Equal(new Rectangle(10, 0, 780, 30), statusBounds);
        Assert.Equal(Rectangle.Empty, detailsBounds);
    }

    [Fact]
    public void StatusBarLayoutStacksDetailsWhenNarrow()
    {
        (Rectangle statusBounds, Rectangle detailsBounds) = RemoteViewerWindow.CalculateStatusBarLayout(
            new Size(360, 52),
            new Padding(10, 0, 10, 0),
            hasDetails: true);

        Assert.Equal(new Rectangle(10, 0, 340, 25), statusBounds);
        Assert.Equal(new Rectangle(10, 27, 340, 25), detailsBounds);
        Assert.Equal(
            52,
            RemoteViewerWindow.CalculateStatusBarPreferredHeight(
                360,
                new Padding(10, 0, 10, 0),
                hasDetails: true));
    }

    [Theory]
    [InlineData(575, 52)]
    [InlineData(576, 30)]
    [InlineData(1150, 104)]
    [InlineData(1152, 60)]
    public void StatusBarHeightUsesLogicalSideBySideBreakpoint(
        int physicalClientWidth,
        int expectedHeight)
    {
        int dpi = physicalClientWidth >= 1000 ? 192 : 96;
        int horizontalPadding = ResponsiveWindowLayout.ScaleLogical(10, dpi);

        Assert.Equal(
            expectedHeight,
            RemoteViewerWindow.CalculateStatusBarPreferredHeight(
                physicalClientWidth,
                new Padding(horizontalPadding, 0, horizontalPadding, 0),
                hasDetails: true,
                dpi));
    }

    [Fact]
    public void StatusBarLayoutAndHeightShareFractionalDpiBreakpoint()
    {
        const int dpi = 106;
        int padding = ResponsiveWindowLayout.ScaleLogical(10, dpi);
        int breakpoint = RemoteViewerWindow.CalculateStatusBarSideBySideMinimumWidth(dpi);
        int clientWidth = padding * 2 + breakpoint;
        int preferredHeight = RemoteViewerWindow.CalculateStatusBarPreferredHeight(
            clientWidth,
            new Padding(padding, 0, padding, 0),
            hasDetails: true,
            dpi);

        (Rectangle statusBounds, Rectangle detailsBounds) = RemoteViewerWindow.CalculateStatusBarLayout(
            new Size(clientWidth, preferredHeight),
            new Padding(padding, 0, padding, 0),
            hasDetails: true,
            dpi);

        Assert.Equal(ResponsiveWindowLayout.ScaleLogical(30, dpi), preferredHeight);
        Assert.Equal(ResponsiveWindowLayout.ScaleLogical(180, dpi), statusBounds.Width);
        Assert.Equal(ResponsiveWindowLayout.ScaleLogical(360, dpi), detailsBounds.Width);
    }

    [Theory]
    [InlineData(96, 296, 30, 52)]
    [InlineData(144, 444, 45, 78)]
    [InlineData(192, 592, 60, 104)]
    public void StatusFooterKeepsStackedStatusDetailsTallEnoughBesideActions(
        int dpi,
        int statusWidth,
        int actionHeight,
        int expectedHeight)
    {
        int padding = ResponsiveWindowLayout.ScaleLogical(10, dpi);

        Assert.Equal(
            expectedHeight,
            RemoteViewerWindow.CalculateStatusFooterPreferredHeight(
                statusWidth,
                new Padding(padding, 0, padding, 0),
                hasDetails: true,
                actionPreferredHeight: actionHeight,
                dpi: dpi));
    }

    [Theory]
    [InlineData(96, 32)]
    [InlineData(144, 48)]
    [InlineData(192, 64)]
    public void StatusFooterNeverClipsActionButtons(int dpi, int actionHeight)
    {
        int padding = ResponsiveWindowLayout.ScaleLogical(10, dpi);

        Assert.Equal(
            actionHeight,
            RemoteViewerWindow.CalculateStatusFooterPreferredHeight(
                ResponsiveWindowLayout.ScaleLogical(800, dpi),
                new Padding(padding, 0, padding, 0),
                hasDetails: false,
                actionPreferredHeight: actionHeight,
                dpi: dpi));
    }

    [Fact]
    public void StatusFooterDockingKeepsStatusAndActionsInSeparateBounds()
    {
        using var footer = new Panel { ClientSize = new Size(464, 52) };
        using var status = new Panel { Dock = DockStyle.Fill };
        using var actions = new Panel { Dock = DockStyle.Right, Width = 177 };

        RemoteViewerWindow.ConfigureStatusFooterChildren(footer, status, actions);
        footer.PerformLayout();

        Assert.Equal(new Rectangle(0, 0, 287, 52), status.Bounds);
        Assert.Equal(new Rectangle(287, 0, 177, 52), actions.Bounds);
        Assert.Equal(status.Right, actions.Left);
    }

    [Theory]
    [InlineData(true, Keys.Control | Keys.Shift | Keys.R, true)]
    [InlineData(true, Keys.Control | Keys.R, false)]
    [InlineData(false, Keys.Control | Keys.Shift | Keys.R, false)]
    public void RemoteFilePullShortcutIsReservedWheneverViewerIsConnected(
        bool connected,
        Keys keys,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.ShouldConsumeRemoteFilePullShortcut(
                connected,
                new KeyEventArgs(keys)));
    }

    [Theory]
    [InlineData(96, 8)]
    [InlineData(144, 12)]
    [InlineData(192, 16)]
    public void DragDropEdgeToleranceTracksMonitorDpi(int dpi, int expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.GetDragDropEdgeTolerance(dpi));
    }

    [Theory]
    [InlineData(96, 12)]
    [InlineData(144, 18)]
    [InlineData(192, 24)]
    public void RemoteDragOutEdgeToleranceTracksMonitorDpi(int dpi, int expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.GetRemoteDragOutEdgeExitTolerance(dpi));
    }

    [Theory]
    [InlineData(true, true, true, false, false, MouseButtons.Left, true)]
    [InlineData(false, true, true, false, false, MouseButtons.Left, false)]
    [InlineData(true, false, true, false, false, MouseButtons.Left, false)]
    [InlineData(true, true, false, false, false, MouseButtons.Left, false)]
    [InlineData(true, true, true, true, false, MouseButtons.Left, false)]
    [InlineData(true, true, true, false, true, MouseButtons.Left, false)]
    [InlineData(true, true, true, false, false, MouseButtons.Right, false)]
    public void RemoteDragOutArmsOnlyForSupportedIdleLeftDrag(
        bool connected,
        bool inputEnabled,
        bool remoteFilePullEnabled,
        bool transferInProgress,
        bool isAndroidRemote,
        MouseButtons button,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.CanArmRemoteDragOut(
                connected,
                inputEnabled,
                remoteFilePullEnabled,
                transferInProgress,
                isAndroidRemote,
                button));
    }

    [Fact]
    public void RemoteDragOutStartsOnlyBeyondViewerBoundaryAndHysteresis()
    {
        Rectangle viewerBounds = new(0, 0, 200, 130);
        Point pressPoint = new(100, 50);
        Size dragSize = new(8, 8);

        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            new Point(100, 120),
            viewerBounds,
            dragSize,
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            new Point(100, 141),
            viewerBounds,
            dragSize,
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
        Assert.True(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            new Point(100, 142),
            viewerBounds,
            dragSize,
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
    }

    [Fact]
    public void RemoteDragOutDoesNotStartInViewerLetterboxOrFooterArea()
    {
        Rectangle viewerBounds = new(0, 0, 200, 130);
        Rectangle zoomedImageBounds = new(0, 25, 200, 80);
        Point pressPoint = new(100, 50);
        Point pointerBelowImage = new(100, zoomedImageBounds.Bottom + 7);

        Assert.False(zoomedImageBounds.Contains(pointerBelowImage));
        Assert.True(viewerBounds.Contains(pointerBelowImage));
        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            pointerBelowImage,
            viewerBounds,
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
    }

    [Fact]
    public void RemoteDragOutRejectsReleasedButtonAndPressOutsideViewer()
    {
        Rectangle viewerBounds = new(0, 0, 200, 130);

        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            new Point(100, 50),
            new Point(250, 50),
            viewerBounds,
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: false));
        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            new Point(-1, 50),
            new Point(250, 50),
            viewerBounds,
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
    }

    [Fact]
    public void RemoteDragOutRequiresSystemDragThreshold()
    {
        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            new Point(0, 0),
            new Point(-2, 0),
            new Rectangle(0, 0, 100, 100),
            new Size(8, 8),
            edgeExitTolerancePixels: 0,
            leftButtonDown: true));
    }

    [Fact]
    public void RemoteDragOutBoundaryMathSupportsNegativeMonitorCoordinates()
    {
        Rectangle viewerBounds = new(-1920, -400, 1280, 900);
        Point pressPoint = new(-1800, -300);

        Assert.False(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            new Point(-1932, -300),
            viewerBounds,
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
        Assert.True(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            pressPoint,
            new Point(-1933, -300),
            viewerBounds,
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
    }

    [Fact]
    public void RemoteDragOutBoundaryMathDoesNotOverflowAtExtremePointerCoordinate()
    {
        Assert.True(RemoteViewerWindow.ShouldBeginRemoteDragOut(
            new Point(0, 0),
            new Point(int.MinValue, 0),
            new Rectangle(0, 0, 100, 100),
            new Size(8, 8),
            edgeExitTolerancePixels: 12,
            leftButtonDown: true));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void EscapeCancelsOnlyPreOleRemoteDragOutStages(
        int stageValue,
        bool expected)
    {
        var stage = (RemoteDragOutStage)stageValue;
        Assert.Equal(
            expected,
            RemoteViewerWindow.ShouldCancelRemoteDragOutWithEscape(Keys.Escape, stage));
        Assert.False(RemoteViewerWindow.ShouldCancelRemoteDragOutWithEscape(Keys.Enter, stage));
    }

    [Theory]
    [InlineData(0, false, false, 0)]
    [InlineData(1, false, false, 1)]
    [InlineData(1, false, true, 2)]
    [InlineData(2, false, false, 3)]
    [InlineData(2, false, true, 3)]
    [InlineData(3, false, true, 0)]
    [InlineData(1, true, true, 0)]
    [InlineData(2, true, false, 0)]
    public void CaptureLossConservativelyEndsRemoteGestureOrPreventsOleHandoff(
        int stageValue,
        bool pictureHasCapture,
        bool leftButtonDown,
        int expectedActionValue)
    {
        var stage = (RemoteDragOutStage)stageValue;
        var expected = (RemoteDragOutCaptureLossAction)expectedActionValue;
        Assert.Equal(
            expected,
            RemoteViewerWindow.GetRemoteDragOutCaptureLossAction(
                stage,
                pictureHasCapture,
                leftButtonDown));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, true)]
    [InlineData(false, 3, false)]
    public void PullProgressStaysVisibleOnlyWhileClientOrDragPullIsPending(
        bool clientPending,
        int stageValue,
        bool expected)
    {
        var stage = (RemoteDragOutStage)stageValue;
        Assert.Equal(
            expected,
            RemoteViewerWindow.ShouldShowRemoteFilePullPending(clientPending, stage));
    }

    [Fact]
    public void RemoteDragOutGenerationRejectsStaleOrCancelledContinuations()
    {
        Assert.True(RemoteViewerWindow.IsRemoteDragOutOperationCurrent(
            currentGeneration: 8,
            operationGeneration: 8,
            ownerMatches: true,
            cancellationRequested: false,
            RemoteDragOutStage.Pulling,
            isClosing: false,
            isDisposed: false));

        Assert.False(RemoteViewerWindow.IsRemoteDragOutOperationCurrent(
            currentGeneration: 9,
            operationGeneration: 8,
            ownerMatches: true,
            cancellationRequested: false,
            RemoteDragOutStage.Pulling,
            isClosing: false,
            isDisposed: false));
        Assert.False(RemoteViewerWindow.IsRemoteDragOutOperationCurrent(
            currentGeneration: 8,
            operationGeneration: 8,
            ownerMatches: true,
            cancellationRequested: true,
            RemoteDragOutStage.Pulling,
            isClosing: false,
            isDisposed: false));
        Assert.False(RemoteViewerWindow.IsRemoteDragOutOperationCurrent(
            currentGeneration: 8,
            operationGeneration: 8,
            ownerMatches: true,
            cancellationRequested: false,
            RemoteDragOutStage.None,
            isClosing: false,
            isDisposed: false));
    }

    [Theory]
    [InlineData(true, false, true, 1, true)]
    [InlineData(false, false, true, 1, false)]
    [InlineData(true, true, true, 1, false)]
    [InlineData(true, false, false, 1, false)]
    [InlineData(true, false, true, 0, false)]
    public void RemoteDragOutStartsLocalFileDropOnlyForCurrentHeldGesture(
        bool isCurrentOperation,
        bool mouseReleased,
        bool leftButtonDown,
        int localPathCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.ShouldStartRemoteDragOutFileDrop(
                isCurrentOperation,
                mouseReleased,
                leftButtonDown,
                localPathCount));
    }

    [Theory]
    [InlineData(true, "放入本机剪贴板")]
    [InlineData(false, "剪贴板暂不可用")]
    public void ReleasedRemoteDragOutStatusDoesNotOverstateClipboardResult(
        bool clipboardUpdated,
        string expectedText)
    {
        string status = RemoteViewerWindow.FormatReleasedRemoteDragOutStatus(
            localPathCount: 2,
            clipboardUpdated);

        Assert.Contains("已取回 2 个文件", status, StringComparison.Ordinal);
        Assert.Contains(expectedText, status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DragDropEffects.Copy, true, "已将 2 个远端文件拖放")]
    [InlineData(DragDropEffects.None, true, "已取回 2 个文件并放入本机剪贴板")]
    [InlineData(DragDropEffects.None, false, "已取回 2 个文件并保存在接收目录")]
    public void RemoteDragOutCompletionStatusDistinguishesDropAndFallback(
        DragDropEffects effect,
        bool clipboardUpdated,
        string expectedText)
    {
        string status = RemoteViewerWindow.FormatRemoteDragOutCompletionStatus(
            localPathCount: 2,
            effect,
            clipboardUpdated);

        Assert.Contains(expectedText, status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "放入本机剪贴板")]
    [InlineData(false, "保存在接收目录")]
    public void RemoteDragOutOleFailureStatusPreservesReceivedFileRecoveryGuidance(
        bool clipboardUpdated,
        string expectedText)
    {
        string status = RemoteViewerWindow.FormatRemoteDragOutOleFailureStatus(
            localPathCount: 3,
            clipboardUpdated,
            errorMessage: "OLE unavailable");

        Assert.Contains("无法启动本机拖放：OLE unavailable", status, StringComparison.Ordinal);
        Assert.Contains("已取回 3 个文件", status, StringComparison.Ordinal);
        Assert.Contains(expectedText, status, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteDragOutOleExceptionFilterCoversExpectedInteropFailuresOnly()
    {
        Assert.True(RemoteViewerWindow.IsRemoteDragOutOleException(new InvalidOperationException()));
        Assert.True(RemoteViewerWindow.IsRemoteDragOutOleException(new System.Runtime.InteropServices.COMException()));
        Assert.True(RemoteViewerWindow.IsRemoteDragOutOleException(new ThreadStateException()));
        Assert.False(RemoteViewerWindow.IsRemoteDragOutOleException(new IOException()));
    }

    [Fact]
    public void RemoteDragOutCancelSequenceCancelsBeforeRemoteMouseUp()
    {
        RemoteInputCommand[] commands = RemoteViewerWindow.CreateRemoteDragOutCancelCommands(
            new Point(1919, 1079));

        Assert.Collection(
            commands,
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyDown, command.Kind);
                Assert.Equal((int)Keys.Escape, command.Data);
            },
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyUp, command.Kind);
                Assert.Equal((int)Keys.Escape, command.Data);
            },
            command =>
            {
                Assert.Equal(RemoteInputKind.MouseUp, command.Kind);
                Assert.Equal(RemoteMouseButton.Left, command.Button);
                Assert.Equal(1919, command.X);
                Assert.Equal(1079, command.Y);
            });
    }

    [Fact]
    public void RemoteDragOutCopySequenceBalancesControlAndC()
    {
        RemoteInputCommand[] commands = RemoteViewerWindow.CreateRemoteDragOutCopyCommands();

        Assert.Equal(
            [
                (RemoteInputKind.KeyDown, (int)Keys.ControlKey),
                (RemoteInputKind.KeyDown, (int)Keys.C),
                (RemoteInputKind.KeyUp, (int)Keys.C),
                (RemoteInputKind.KeyUp, (int)Keys.ControlKey)
            ],
            commands.Select(command => (command.Kind, command.Data)).ToArray());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 550)]
    public void ClipboardSynchronizationDelayUsesSequenceTrackingFastPath(
        bool supportsSequenceTracking,
        int expectedDelayMs)
    {
        Assert.Equal(
            expectedDelayMs,
            RemoteViewerWindow.GetClipboardSynchronizationDelayMs(
                supportsSequenceTracking,
                compatibilityDelayMs: 550));
    }

    [Fact]
    public void RemoteDragOutDataObjectCarriesFileDropAndSelfDropMarker()
    {
        string[] paths = [Path.Combine(Path.GetTempPath(), "remote-drag-out.txt")];

        DataObject dataObject = RemoteViewerWindow.CreateRemoteDragOutDataObject(paths);

        Assert.True(dataObject.GetDataPresent(DataFormats.FileDrop));
        Assert.Equal(paths, dataObject.GetFileDropList().Cast<string>().ToArray());
        Assert.True(RemoteViewerWindow.IsRemoteDragOutData(dataObject));
        Assert.False(RemoteViewerWindow.IsRemoteDragOutData(new DataObject()));
    }

    [Fact]
    public void RemoteDragOutFiltersFilesThatDisappearedBeforeOleDragStarts()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"RemoteDesk-window-test-{Guid.NewGuid():N}");
        string existingFile = Path.Combine(directory, "available.txt");
        string missingFile = Path.Combine(directory, "missing.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(existingFile, "ready");
        try
        {
            Assert.Equal(
                [existingFile],
                RemoteViewerWindow.GetUsableRemoteDragOutPaths([existingFile, missingFile]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CalculateZoomedImageRectanglePreservesAspectRatioInsideClient()
    {
        Rectangle imageRect = RemoteViewerWindow.CalculateZoomedImageRectangle(
            new Rectangle(0, 0, 800, 600),
            new Size(1920, 1080));

        Assert.Equal(new Rectangle(0, 75, 800, 450), imageRect);
    }

    [Fact]
    public void NativePixelDisplayCentersSmallerFrameWithoutUpscaling()
    {
        Rectangle imageRect =
            RemoteViewerWindow.CalculateDisplayedImageRectangle(
                new Rectangle(10, 20, 2560, 1440),
                new Size(1920, 1080),
                allowUpscaling: false);

        Assert.Equal(
            new Rectangle(330, 200, 1920, 1080),
            imageRect);
        Assert.Equal(
            " | 显示 1:1",
            RemoteViewerWindow.FormatDisplayScale(
                new Size(1920, 1080),
                new Rectangle(0, 0, 2560, 1440),
                allowUpscaling: false));
    }

    [Fact]
    public void NativePixelDisplayStillDownscalesOversizedFrame()
    {
        Rectangle imageRect =
            RemoteViewerWindow.CalculateDisplayedImageRectangle(
                new Rectangle(0, 0, 800, 600),
                new Size(1920, 1080),
                allowUpscaling: false);

        Assert.Equal(
            new Rectangle(0, 75, 800, 450),
            imageRect);
    }

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(1920, 1080, 3840, 2160, 1920, 1080)]
    [InlineData(3840, 2160, 1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 1366, 768, 1365, 768)]
    [InlineData(734, 1600, 1920, 1080, 495, 1080)]
    [InlineData(3440, 1440, 1920, 1080, 1920, 804)]
    public void MixedResolutionDisplayAndHardwareInputGeometryStayAligned(
        int sourceWidth, int sourceHeight, int viewWidth, int viewHeight,
        int expectedWidth, int expectedHeight)
    {
        Size source = new(sourceWidth, sourceHeight);
        Size viewport = new(viewWidth, viewHeight);
        Rectangle displayed = RemoteViewerWindow.CalculateDisplayedImageRectangle(
            new Rectangle(Point.Empty, viewport), source, allowUpscaling: false);
        var hardware = D3D11HwndVideoPresenter.CalculateGeometry(
            new Rectangle(Point.Empty, source), viewport,
            D3D11HwndVideoScaleMode.FitWithoutUpscaling);

        Assert.Equal(new Size(expectedWidth, expectedHeight), displayed.Size);
        Assert.Equal(displayed, hardware.Destination);
        Assert.True(displayed.Width <= sourceWidth && displayed.Height <= sourceHeight);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, " | 显示 1:1")]
    [InlineData(1920, 1080, 1900, 1069, " | 显示 0.99x 缩小")]
    [InlineData(1920, 1080, 1940, 1091, " | 显示 1.01x 放大")]
    [InlineData(1920, 1080, 3840, 2160, " | 显示 2.00x 放大")]
    [InlineData(
        3840,
        2160,
        1920,
        1080,
        " | 显示 0.50x 缩小（4K 缩小会损失细节，按 F11 全屏）")]
    [InlineData(0, 1080, 1920, 1080, "")]
    public void DisplayScaleMakesUpscalingVisibleInStatus(
        int frameWidth,
        int frameHeight,
        int clientWidth,
        int clientHeight,
        string expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.FormatDisplayScale(
                new Size(
                    frameWidth,
                    frameHeight),
                new Rectangle(
                    0,
                    0,
                    clientWidth,
                    clientHeight)));
    }

    [Theory]
    [InlineData((int)Keys.F11, true)]
    [InlineData((int)(Keys.F11 | Keys.Control), false)]
    [InlineData((int)Keys.F10, false)]
    public void FullScreenShortcutConsumesOnlyUnmodifiedF11(
        int keyData,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.IsFullScreenShortcut(
                (Keys)keyData));
    }

    [Theory]
    [InlineData(RemoteDevicePlatforms.Windows, true, true)]
    [InlineData(RemoteDevicePlatforms.Windows, false, false)]
    [InlineData(RemoteDevicePlatforms.Linux, true, false)]
    [InlineData(RemoteDevicePlatforms.Android, true, false)]
    [InlineData(RemoteDevicePlatforms.Unknown, true, false)]
    [InlineData(null, true, false)]
    public void RemoteInputMethodButtonRequiresWindowsInputControl(
        string? platform,
        bool inputEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .ShouldShowRemoteInputMethodButton(
                    platform,
                    inputEnabled));
    }

    [Fact]
    public void RemoteInputMethodSwitchUsesPhysicalWinSpaceChord()
    {
        RemoteInputCommand[] commands =
            RemoteViewerWindow
                .CreateRemoteInputMethodSwitchCommands();
        RemoteKeyboardFlags windowsKeyFlags =
            RemoteKeyboardFlags.HasScanCode |
            RemoteKeyboardFlags.Extended;
        RemoteKeyboardFlags spaceKeyFlags =
            RemoteKeyboardFlags.HasScanCode;

        Assert.Collection(
            commands,
            command => AssertPhysicalKey(
                command,
                RemoteInputKind.KeyDown,
                Keys.LWin,
                scanCode: 0x5B,
                windowsKeyFlags),
            command => AssertPhysicalKey(
                command,
                RemoteInputKind.KeyDown,
                Keys.Space,
                scanCode: 0x39,
                spaceKeyFlags),
            command => AssertPhysicalKey(
                command,
                RemoteInputKind.KeyUp,
                Keys.Space,
                scanCode: 0x39,
                spaceKeyFlags),
            command => AssertPhysicalKey(
                command,
                RemoteInputKind.KeyUp,
                Keys.LWin,
                scanCode: 0x5B,
                windowsKeyFlags));
    }

    [Fact]
    public void FullScreenRestoreUsesNormalBoundsForMaximizedWindow()
    {
        var maximizedBounds =
            new Rectangle(0, 0, 3840, 2160);
        var normalBounds =
            new Rectangle(320, 180, 1180, 760);

        Assert.Equal(
            normalBounds,
            RemoteViewerWindow.SelectWindowedRestoreBounds(
                FormWindowState.Maximized,
                maximizedBounds,
                normalBounds));
        Assert.Equal(
            normalBounds,
            RemoteViewerWindow.SelectWindowedRestoreBounds(
                FormWindowState.Normal,
                normalBounds,
                maximizedBounds));
    }

    [Fact]
    public void FullScreenToggleRestoresBorderBoundsAndWindowState()
    {
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "fullscreen-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false)
            {
                StartPosition = FormStartPosition.Manual,
                FormBorderStyle = FormBorderStyle.Sizable,
                Bounds = new Rectangle(120, 90, 1180, 760)
            };
        Rectangle windowedBounds = window.Bounds;

        window.ToggleFullScreen();

        Assert.Equal(FormBorderStyle.None, window.FormBorderStyle);
        Assert.Equal(
            Screen.FromControl(window).Bounds,
            window.Bounds);

        window.ToggleFullScreen();

        Assert.Equal(FormBorderStyle.Sizable, window.FormBorderStyle);
        Assert.Equal(FormWindowState.Normal, window.WindowState);
        Assert.Equal(windowedBounds, window.Bounds);
    }

    [Fact]
    public void DisplayScaleDefaultsToNoUpscalingAndCanOptIntoWindowFit()
    {
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "display-scale-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false);

        Assert.False(
            window.AllowDisplayUpscalingForEntityTests);
        Assert.Equal(
            "允许放大",
            window.DisplayScaleButtonForEntityTests.Text);

        window.ToggleDisplayScaleMode();

        Assert.True(
            window.AllowDisplayUpscalingForEntityTests);
        Assert.Equal(
            "禁止放大",
            window.DisplayScaleButtonForEntityTests.Text);

        window.ToggleDisplayScaleMode();

        Assert.False(
            window.AllowDisplayUpscalingForEntityTests);
        Assert.Equal(
            "允许放大",
            window.DisplayScaleButtonForEntityTests.Text);
    }

    [Fact]
    public void CaptureTargetSwitchCyclesThroughPublishedTargets()
    {
        CaptureTargetInfo[] targets =
        [
            new(
                ScreenCaptureTarget.AllScreensId,
                "所有屏幕"),
            new("display-1", "显示器 1"),
            new("display-2", "显示器 2")
        ];

        Assert.Equal(
            "display-1",
            RemoteViewerWindow.FindNextCaptureTarget(
                targets,
                ScreenCaptureTarget.AllScreensId)?.Id);
        Assert.Equal(
            "display-2",
            RemoteViewerWindow.FindNextCaptureTarget(
                targets,
                "DISPLAY-1")?.Id);
        Assert.Equal(
            "display-1",
            RemoteViewerWindow.FindNextCaptureTarget(
                targets,
                "display-2")?.Id);
        Assert.Equal(
            "display-1",
            RemoteViewerWindow.FindNextCaptureTarget(
                targets,
                "missing")?.Id);
        Assert.Null(
            RemoteViewerWindow.FindNextCaptureTarget(
                targets[..2],
                ScreenCaptureTarget.AllScreensId));
    }

    [Fact]
    public void CaptureTargetSwitchButtonIsPartOfViewerFooter()
    {
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "capture-target-switch-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false);

        Button button =
            window.CaptureTargetSwitchButtonForEntityTests;
        window.SetCaptureTargets(
            [
                new(
                    ScreenCaptureTarget.AllScreensId,
                    "所有屏幕"),
                new("display-1", "显示器 1")
            ],
            ScreenCaptureTarget.AllScreensId);
        window.SetCaptureTargetSelectionEnabled(true);

        Assert.Equal("切换屏幕", button.Text);
        Assert.NotNull(button.Parent);
        Assert.False(button.Enabled);
    }

    [Fact]
    public async Task RemoteInputMethodButtonTracksPlatformInputAndFullScreen()
    {
        var completed =
            new TaskCompletionSource<(
                bool UnknownHidden,
                bool WindowsVisible,
                bool LinuxHidden,
                bool InputDisabledHidden,
                bool ReenabledVisible,
                bool FullScreenHidden,
                bool RestoredVisible,
                bool CaptureTargetSwitchVisible,
                bool UniformActionHeights,
                bool FitsNarrowFooter,
                string Text)>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    using var client =
                        new RemoteViewerClient();
                    using var window =
                        new RemoteViewerWindow(
                            client,
                            "remote-ime-button-sta-test",
                            inputEnabled: true,
                            clipboardTextEnabled: false,
                            filePasteEnabled: false,
                            fileDropPasteEnabled: false,
                            remoteFilePullEnabled: false,
                            isAndroidRemote: false)
                        {
                            ShowInTaskbar = false
                        };
                    window.Show();
                    Application.DoEvents();
                    Button button =
                        window
                            .RemoteInputMethodButtonForEntityTests;
                    bool unknownHidden =
                        !button.Visible;
                    window.SetRemotePlatform(
                        RemoteDevicePlatforms.Windows);
                    window.SetCaptureTargets(
                        [
                            new(
                                ScreenCaptureTarget.AllScreensId,
                                "所有屏幕"),
                            new("display-1", "显示器 1"),
                            new("display-2", "显示器 2")
                        ],
                        ScreenCaptureTarget.AllScreensId);
                    window.SetCaptureTargetSelectionEnabled(
                        true);
                    Application.DoEvents();
                    bool windowsVisible =
                        button.Visible;
                    bool captureTargetSwitchVisible =
                        window
                            .CaptureTargetSwitchButtonForEntityTests
                            .Visible;
                    window.ClientSize =
                        new Size(480, 320);
                    window.PerformLayout();
                    Application.DoEvents();
                    Control actions =
                        Assert.IsAssignableFrom<Control>(
                            button.Parent);
                    Control footer =
                        Assert.IsAssignableFrom<Control>(
                            actions.Parent);
                    bool uniformActionHeights =
                        actions.Controls
                            .Cast<Control>()
                            .Where(control =>
                                control.Visible)
                            .Select(control =>
                                control.Height)
                            .Distinct()
                            .Count() == 1;
                    bool fitsNarrowFooter =
                        actions.Left >= 0 &&
                        actions.Right <=
                            footer.ClientSize.Width;

                    window.SetRemotePlatform(
                        RemoteDevicePlatforms.Linux);
                    Application.DoEvents();
                    bool linuxHidden =
                        !button.Visible;

                    window.SetRemotePlatform(
                        RemoteDevicePlatforms.Windows);
                    window.SetInputEnabled(false);
                    Application.DoEvents();
                    bool inputDisabledHidden =
                        !button.Visible;

                    window.SetInputEnabled(true);
                    Application.DoEvents();
                    bool reenabledVisible =
                        button.Visible;

                    window.ToggleFullScreen();
                    Application.DoEvents();
                    bool fullScreenHidden =
                        !button.Visible;
                    window.ToggleFullScreen();
                    Application.DoEvents();
                    bool restoredVisible =
                        button.Visible;

                    completed.TrySetResult(
                        (
                            unknownHidden,
                            windowsVisible,
                            linuxHidden,
                            inputDisabledHidden,
                            reenabledVisible,
                            fullScreenHidden,
                            restoredVisible,
                            captureTargetSwitchVisible,
                            uniformActionHeights,
                            fitsNarrowFooter,
                            button.Text));
                    window.Close();
                    Application.DoEvents();
                }
                catch (Exception ex)
                {
                    completed.TrySetException(ex);
                }
            })
        {
            IsBackground = true,
            Name =
                "RemoteDesk remote IME button STA smoke"
        };
        thread.SetApartmentState(
            ApartmentState.STA);
        thread.Start();

        var result =
            await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.True(result.UnknownHidden);
        Assert.True(result.WindowsVisible);
        Assert.True(result.LinuxHidden);
        Assert.True(result.InputDisabledHidden);
        Assert.True(result.ReenabledVisible);
        Assert.True(result.FullScreenHidden);
        Assert.True(result.RestoredVisible);
        Assert.True(
            result.CaptureTargetSwitchVisible);
        Assert.True(result.UniformActionHeights);
        Assert.True(result.FitsNarrowFooter);
        Assert.Equal(
            "远端输入法",
            result.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FileDropRegistrationContainsExpectedPlatformFailures(
        int failureKind)
    {
        using var target = new Panel();
        Exception expected =
            failureKind == 0
                ? new InvalidOperationException(
                    "DragDrop registration did not succeed.")
                : new ExternalException(
                    "OLE rejected drag/drop registration.");

        bool registered =
            RemoteViewerWindow.TrySetFileDropRegistration(
                target,
                enabled: true,
                (_, _) => throw expected,
                out string failure);

        Assert.False(registered);
        Assert.Equal(expected.Message, failure);
        Assert.False(target.AllowDrop);
    }

    [Fact]
    public async Task FileDropRegistersOnlyPictureBoxOnRealStaWindow()
    {
        var completed =
            new TaskCompletionSource<(bool Form, bool Picture)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    (bool Form, bool Picture) result;
                    using (var client = new RemoteViewerClient())
                    using (var window =
                        new RemoteViewerWindow(
                            client,
                            "drag-drop-sta-test",
                            inputEnabled: false,
                            clipboardTextEnabled: false,
                            filePasteEnabled: false,
                            fileDropPasteEnabled: true,
                            remoteFilePullEnabled: false,
                            isAndroidRemote: false)
                        {
                            ShowInTaskbar = false
                        })
                    {
                        window.Show();
                        Application.DoEvents();
                        PictureBox picture =
                            window.Controls
                                .OfType<PictureBox>()
                                .Single();
                        result =
                            (window.AllowDrop, picture.AllowDrop);
                        window.Close();
                        Application.DoEvents();
                    }

                    completed.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    completed.TrySetException(ex);
                }
            })
        {
            IsBackground = true,
            Name = "RemoteDesk drag/drop STA smoke"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        (bool form, bool picture) =
            await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.False(form);
        Assert.True(picture);
    }

    [Fact]
    public async Task RemoteSurfaceCanReceiveKeyboardFocusOnRealStaWindow()
    {
        var completed =
            new TaskCompletionSource<(
                bool CanSelect,
                bool CanFocus,
                bool Focused,
                ImeMode ImeMode)>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    using var client =
                        new RemoteViewerClient();
                    using var window =
                        new RemoteViewerWindow(
                            client,
                            "keyboard-focus-sta-test",
                            inputEnabled: true,
                            clipboardTextEnabled: true,
                            filePasteEnabled: false,
                            fileDropPasteEnabled: false,
                            remoteFilePullEnabled: false,
                            isAndroidRemote: false)
                        {
                            ShowInTaskbar = false
                        };
                    window.Show();
                    window.Activate();
                    Application.DoEvents();
                    PictureBox picture =
                        window.Controls
                            .OfType<PictureBox>()
                            .Single();
                    picture.Select();
                    bool focused = picture.Focus();
                    Application.DoEvents();
                    completed.TrySetResult(
                        (
                            picture.CanSelect,
                            picture.CanFocus,
                            focused &&
                            picture.ContainsFocus,
                            picture.ImeMode));
                    window.Close();
                    Application.DoEvents();
                }
                catch (Exception ex)
                {
                    completed.TrySetException(ex);
                }
            })
        {
            IsBackground = true,
            Name =
                "RemoteDesk keyboard focus STA smoke"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var result =
            await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

        Assert.True(result.CanSelect);
        Assert.True(result.CanFocus);
        Assert.True(result.Focused);
        Assert.Equal(
            ImeMode.Disable,
            result.ImeMode);
    }

    [Fact]
    public async Task RealWinFormsMessageRoutingKeepsF11AsLocalViewerShortcut()
    {
        var completed =
            new TaskCompletionSource<bool>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    using var client =
                        new RemoteViewerClient();
                    using var window =
                        new RemoteViewerWindow(
                            client,
                            "keyboard-route-sta-test",
                            inputEnabled: true,
                            clipboardTextEnabled: true,
                            filePasteEnabled: false,
                            fileDropPasteEnabled: false,
                            remoteFilePullEnabled: false,
                            isAndroidRemote: false)
                        {
                            ShowInTaskbar = false
                        };
                    window.Show();
                    window.Activate();
                    Application.DoEvents();
                    PictureBox picture =
                        window.Controls
                            .OfType<PictureBox>()
                            .Single();
                    picture.Focus();
                    Application.DoEvents();

                    Message keyDown = Message.Create(
                        picture.Handle,
                        RemoteViewerWindow.WmKeyDown,
                        (nint)Keys.F11,
                        (nint)0x00570001);
                    PreProcessControlState downState =
                        picture.PreProcessControlMessage(
                            ref keyDown);
                    Application.DoEvents();
                    Assert.Equal(
                        PreProcessControlState
                            .MessageProcessed,
                        downState);
                    Assert.Equal(
                        FormBorderStyle.None,
                        window.FormBorderStyle);
                    completed.TrySetResult(true);
                    window.Close();
                    Application.DoEvents();
                }
                catch (Exception ex)
                {
                    completed.TrySetException(ex);
                }
            })
        {
            IsBackground = true,
            Name =
                "RemoteDesk keyboard route STA smoke"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            await completed.Task.WaitAsync(
                TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TryMapZoomedImagePointMapsInclusiveImageEdges()
    {
        Rectangle imageRect = new(0, 75, 800, 450);
        Size remoteSize = new(1920, 1080);

        Assert.True(RemoteViewerWindow.TryMapZoomedImagePoint(
            new Point(0, 75),
            remoteSize,
            imageRect,
            edgeTolerancePixels: 0,
            out Point topLeft));
        Assert.Equal(new Point(0, 0), topLeft);

        Assert.True(RemoteViewerWindow.TryMapZoomedImagePoint(
            new Point(799, 524),
            remoteSize,
            imageRect,
            edgeTolerancePixels: 0,
            out Point bottomRight));
        Assert.Equal(new Point(1919, 1079), bottomRight);
    }

    [Fact]
    public void TryMapZoomedImagePointClampsSmallDragEdgeTolerance()
    {
        Rectangle imageRect = new(0, 75, 800, 450);
        Size remoteSize = new(1920, 1080);

        Assert.False(RemoteViewerWindow.TryMapZoomedImagePoint(
            new Point(800, 524),
            remoteSize,
            imageRect,
            edgeTolerancePixels: 0,
            out _));

        Assert.True(RemoteViewerWindow.TryMapZoomedImagePoint(
            new Point(800, 524),
            remoteSize,
            imageRect,
            edgeTolerancePixels: 8,
            out Point clamped));
        Assert.Equal(new Point(1919, 1079), clamped);
    }

    [Fact]
    public void ShouldSendMouseMoveRejectsSameRemotePoint()
    {
        Assert.False(RemoteViewerWindow.ShouldSendMouseMove(
            new Point(10, 20),
            new Point(10, 20)));
    }

    [Fact]
    public void ShortMouseMoveBurstQueuesFinalCoordinateWithoutUiTimeGate()
    {
        var queue = new RemoteInputQueue();
        Point? lastQueuedPoint = null;
        Point[] burst =
        [
            new Point(10, 20),
            new Point(11, 20),
            new Point(12, 21),
            new Point(12, 21),
            new Point(13, 22)
        ];

        foreach (Point point in burst)
        {
            if (!RemoteViewerWindow.ShouldSendMouseMove(point, lastQueuedPoint))
            {
                continue;
            }

            lastQueuedPoint = point;
            queue.Enqueue(
                RemoteInputCommand.MouseMove(point.X, point.Y),
                maxQueuedInputs: 8);
        }

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out RemoteInputCommand command));
        Assert.Equal(RemoteInputKind.MouseMove, command.Kind);
        Assert.Equal(burst[^1].X, command.X);
        Assert.Equal(burst[^1].Y, command.Y);
    }

    [Fact]
    public void ShouldRefreshRemoteDropPointSkipsFreshMatchingMove()
    {
        Assert.False(RemoteViewerWindow.ShouldRefreshRemoteDropPoint(
            new Point(20, 30),
            new Point(20, 30),
            nowMilliseconds: 1100,
            lastSentMilliseconds: 1000,
            freshnessMilliseconds: 150));
    }

    [Theory]
    [InlineData(20, 31, 1100, 1000)]
    [InlineData(20, 30, 1200, 1000)]
    public void ShouldRefreshRemoteDropPointRefreshesChangedOrStaleMove(
        int lastX,
        int lastY,
        long nowMilliseconds,
        long lastSentMilliseconds)
    {
        Assert.True(RemoteViewerWindow.ShouldRefreshRemoteDropPoint(
            new Point(20, 30),
            new Point(lastX, lastY),
            nowMilliseconds,
            lastSentMilliseconds,
            freshnessMilliseconds: 150));
    }

    [Theory]
    [InlineData(Keys.Control | Keys.V, Keys.V)]
    [InlineData(Keys.Shift | Keys.Insert, Keys.Insert)]
    public void RemotePasteTriggerCommandsUseShortcutMainKey(Keys shortcut, Keys expectedKey)
    {
        bool created = RemoteViewerWindow.TryCreateRemotePasteTriggerCommands(
            new KeyEventArgs(shortcut),
            out RemoteInputCommand keyDown,
            out RemoteInputCommand keyUp);

        Assert.True(created);
        Assert.Equal(RemoteInputKind.KeyDown, keyDown.Kind);
        Assert.Equal((int)expectedKey, keyDown.Data);
        Assert.Equal(RemoteInputKind.KeyUp, keyUp.Kind);
        Assert.Equal((int)expectedKey, keyUp.Data);
    }

    [Fact]
    public void RemotePasteTriggerCommandsIgnoreNonPasteShortcut()
    {
        bool created = RemoteViewerWindow.TryCreateRemotePasteTriggerCommands(
            new KeyEventArgs(Keys.Control | Keys.C),
            out RemoteInputCommand keyDown,
            out RemoteInputCommand keyUp);

        Assert.False(created);
        Assert.Equal(default, keyDown);
        Assert.Equal(default, keyUp);
    }

    [Theory]
    [InlineData(Keys.Control | Keys.V, Keys.ControlKey, Keys.V)]
    [InlineData(Keys.Shift | Keys.Insert, Keys.ShiftKey, Keys.Insert)]
    public void RemotePasteTriggerCommandSequenceSendsModifierAndMainKey(
        Keys shortcut,
        Keys expectedModifier,
        Keys expectedKey)
    {
        bool created = RemoteViewerWindow.TryCreateRemotePasteTriggerCommandSequence(
            new KeyEventArgs(shortcut),
            out RemoteInputCommand[] commands);

        Assert.True(created);
        Assert.Collection(
            commands,
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyDown, command.Kind);
                Assert.Equal((int)expectedModifier, command.Data);
            },
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyDown, command.Kind);
                Assert.Equal((int)expectedKey, command.Data);
            },
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyUp, command.Kind);
                Assert.Equal((int)expectedKey, command.Data);
            },
            command =>
            {
                Assert.Equal(RemoteInputKind.KeyUp, command.Kind);
                Assert.Equal((int)expectedModifier, command.Data);
            });
    }

    [Theory]
    [InlineData(Keys.Control | Keys.C)]
    [InlineData(Keys.Control | Keys.Alt | Keys.V)]
    [InlineData(Keys.Shift | Keys.Alt | Keys.Insert)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Insert)]
    [InlineData(Keys.V)]
    public void RemotePasteTriggerCommandSequenceIgnoresNonPasteShortcut(Keys keys)
    {
        bool created = RemoteViewerWindow.TryCreateRemotePasteTriggerCommandSequence(
            new KeyEventArgs(keys),
            out RemoteInputCommand[] commands);

        Assert.False(created);
        Assert.Empty(commands);
        Assert.False(RemoteViewerWindow.TryCreateRemotePasteTriggerCommands(
            new KeyEventArgs(keys), out _, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RemotePastePreservesTerminalShiftAndPhysicallyHeldModifiers(bool controlHeld, bool shiftHeld)
    {
        Assert.True(RemoteViewerWindow.TryCreateRemotePasteTriggerCommandSequence(
            new KeyEventArgs(Keys.Control | Keys.Shift | Keys.V), out var commands, controlHeld, shiftHeld));
        var expected = new List<(RemoteInputKind, int)>();
        if (!controlHeld) expected.Add((RemoteInputKind.KeyDown, (int)Keys.ControlKey));
        if (!shiftHeld) expected.Add((RemoteInputKind.KeyDown, (int)Keys.ShiftKey));
        expected.Add((RemoteInputKind.KeyDown, (int)Keys.V));
        expected.Add((RemoteInputKind.KeyUp, (int)Keys.V));
        if (!shiftHeld) expected.Add((RemoteInputKind.KeyUp, (int)Keys.ShiftKey));
        if (!controlHeld) expected.Add((RemoteInputKind.KeyUp, (int)Keys.ControlKey));
        Assert.Equal(expected, commands.Select(command => (command.Kind, command.Data)));
    }

    [Theory]
    [InlineData(Keys.Control | Keys.C, "复制")]
    [InlineData(Keys.Control | Keys.Insert, "复制")]
    [InlineData(Keys.Control | Keys.X, "剪切")]
    [InlineData(Keys.Shift | Keys.Delete, "剪切")]
    public void ClipboardPullShortcutRecognizesCopyAndCut(Keys shortcut, string expectedReason)
    {
        bool recognized = RemoteViewerWindow.TryGetClipboardPullReason(
            new KeyEventArgs(shortcut),
            out string reason);

        Assert.True(recognized);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void ClipboardPullShortcutIgnoresPasteShortcut()
    {
        bool recognized = RemoteViewerWindow.TryGetClipboardPullReason(
            new KeyEventArgs(Keys.Control | Keys.V),
            out string reason);

        Assert.False(recognized);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void AutomaticRemoteFilePullSuppressesOnlyRecentEmptyFileStatus()
    {
        Assert.True(RemoteViewerWindow.ShouldSuppressAutomaticEmptyRemoteFileStatus(
            success: false,
            message: "远端剪贴板没有可回传的文件。",
            nowMilliseconds: 2000,
            lastAutomaticRemoteFilePullAt: 1000));

        Assert.False(RemoteViewerWindow.ShouldSuppressAutomaticEmptyRemoteFileStatus(
            success: false,
            message: "远端剪贴板没有可回传的文件。",
            nowMilliseconds: 8000,
            lastAutomaticRemoteFilePullAt: 1000));

        Assert.False(RemoteViewerWindow.ShouldSuppressAutomaticEmptyRemoteFileStatus(
            success: true,
            message: "远端剪贴板没有可回传的文件。",
            nowMilliseconds: 2000,
            lastAutomaticRemoteFilePullAt: 1000));
    }

    [Fact]
    public void AutomaticClipboardFilePullRevalidatesOwnerAndDragStateAfterTextRead()
    {
        Assert.True(RemoteViewerWindow.ShouldRequestAutomaticRemoteClipboardFiles(
            ownerMatches: true,
            cancellationRequested: false,
            connected: true,
            remoteFilePullEnabled: true,
            RemoteDragOutStage.None));

        Assert.False(RemoteViewerWindow.ShouldRequestAutomaticRemoteClipboardFiles(
            ownerMatches: false,
            cancellationRequested: false,
            connected: true,
            remoteFilePullEnabled: true,
            RemoteDragOutStage.None));
        Assert.False(RemoteViewerWindow.ShouldRequestAutomaticRemoteClipboardFiles(
            ownerMatches: true,
            cancellationRequested: true,
            connected: true,
            remoteFilePullEnabled: true,
            RemoteDragOutStage.None));
        Assert.False(RemoteViewerWindow.ShouldRequestAutomaticRemoteClipboardFiles(
            ownerMatches: true,
            cancellationRequested: false,
            connected: true,
            remoteFilePullEnabled: true,
            RemoteDragOutStage.Pulling));
    }

    [Theory]
    [InlineData("已取消从远程画面拖出文件。", true)]
    [InlineData("拖出操作已取消，已拒绝远端文件回传。", true)]
    [InlineData("接收远端文件失败：连接中断。", false)]
    public void CancelledFileTransferStatusIsRecognizedAsNeutral(string message, bool expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.IsCancelledFileTransferStatus(message));
    }

    [Theory]
    [InlineData(true, true, false, true, DragDropEffects.Copy)]
    [InlineData(false, true, false, true, DragDropEffects.None)]
    [InlineData(true, false, false, true, DragDropEffects.None)]
    [InlineData(true, true, true, true, DragDropEffects.None)]
    [InlineData(true, true, false, false, DragDropEffects.None)]
    public void FileDragDropEffectRequiresConnectedFileReceiveAndFileDropData(
        bool connected,
        bool filePasteEnabled,
        bool transferInProgress,
        bool hasFileDropData,
        DragDropEffects expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.GetFileDragDropEffect(
            connected,
            filePasteEnabled,
            transferInProgress,
            hasFileDropData));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void ShouldInspectFileDragDataOnlyWhenDropCanStart(
        bool connected,
        bool filePasteEnabled,
        bool transferInProgress,
        bool expected)
    {
        Assert.Equal(expected, RemoteViewerWindow.ShouldInspectFileDragData(
            connected,
            filePasteEnabled,
            transferInProgress));
    }

    [Fact]
    public void FormatDroppedFileTransferStatusReportsSuccessfulBatch()
    {
        string status = RemoteViewerWindow.FormatDroppedFileTransferStatus(
            new RemoteFilePasteResult(
                SentFiles: 2,
                FailedFiles: 0,
                SkippedDirectories: 1,
                SkippedMissing: 1,
                Truncated: true,
                ArchivedDirectories: 1),
            maxFiles: 32);

        Assert.Contains("已拖放发送 2 个文件到被控端接收目录", status, StringComparison.Ordinal);
        Assert.Contains("已打包 1 个文件夹为 zip", status, StringComparison.Ordinal);
        Assert.Contains("跳过 1 个文件夹", status, StringComparison.Ordinal);
        Assert.Contains("跳过 1 个不可访问项", status, StringComparison.Ordinal);
        Assert.Contains("一次最多拖放 32 个文件", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDroppedFileTransferStatusReportsRequestedRemoteTargetPaste()
    {
        string status = RemoteViewerWindow.FormatDroppedFileTransferStatus(
            new RemoteFilePasteResult(
                SentFiles: 1,
                FailedFiles: 0,
                SkippedDirectories: 0,
                SkippedMissing: 0,
                Truncated: false),
            maxFiles: 32,
            requestedRemoteTargetPaste: true);

        Assert.Contains("请求远端当前位置粘贴", status, StringComparison.Ordinal);
        Assert.DoesNotContain("接收目录", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatClipboardFilePasteStatusReportsRequestedRemoteTargetPaste()
    {
        string status = RemoteViewerWindow.FormatClipboardFilePasteStatus(
            new RemoteFilePasteResult(
                SentFiles: 1,
                FailedFiles: 0,
                SkippedDirectories: 0,
                SkippedMissing: 0,
                Truncated: false),
            maxFiles: 32,
            requestedRemoteTargetPaste: true);

        Assert.Contains("请求远端当前位置粘贴", status, StringComparison.Ordinal);
        Assert.DoesNotContain("接收目录", status, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDroppedFileTransferStatusReportsDirectoryOnlyDrop()
    {
        string status = RemoteViewerWindow.FormatDroppedFileTransferStatus(
            new RemoteFilePasteResult(
                SentFiles: 0,
                FailedFiles: 0,
                SkippedDirectories: 1,
                SkippedMissing: 0,
                Truncated: false),
            maxFiles: 32);

        Assert.Contains("文件夹当前不可访问或未能打包", status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, true, false)]
    public void MediaFoundationDirectDecodeStartsOnlyAtSafeRecoveryBoundary(
        bool disabled,
        bool hardwareDecoderExists,
        bool ffmpegDecoderExists,
        bool recoveryFrame,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .ShouldAttemptMediaFoundationDirectDecode(
                    disabled,
                    hardwareDecoderExists,
                    ffmpegDecoderExists,
                    recoveryFrame));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void DirectPresentationActivatesOnlyAfterQualificationWindow(
        int validatedPresentedFrames,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow.ShouldActivateDirectPresentation(
                validatedPresentedFrames));
    }

    [Fact]
    public void DirectPresentationQualificationRejectsInvalidThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteViewerWindow.ShouldActivateDirectPresentation(
                validatedPresentedFrames: 1,
                requiredFrames: 0));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(2, true)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(11, false)]
    public void DirectPresentationTreatsOcclusionAndMinimizeAsNonFatal(
        int statusValue,
        bool expected)
    {
        var status =
            (D3D11HwndVideoPresenterStatus)statusValue;
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .IsNonFatalPresentationResult(status));
    }

    [Theory]
    [InlineData(12, 7, 8, true, true)]
    [InlineData(12, 7, 7, false, true)]
    [InlineData(12, 7, 7, true, false)]
    [InlineData(7, 7, 8, false, false)]
    [InlineData(11, 7, 8, false, false)]
    public void RenderThreadPresentationRecoversOnlyFromHwndLifecycleRace(
        int statusValue,
        long expectedHandleGeneration,
        long currentHandleGeneration,
        bool targetReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .IsNonFatalRenderThreadPresentationResult(
                    (D3D11HwndVideoPresenterStatus)
                        statusValue,
                    expectedHandleGeneration,
                    currentHandleGeneration,
                    targetReady));
    }

    [Fact]
    public void DecodedRemoteFrameDisposesPendingBitmapIdempotently()
    {
        var bitmap = new Bitmap(2, 2);
        var frame =
            new RemoteViewerWindow.DecodedRemoteFrame(
                new RemoteFrame(
                    2,
                    2,
                    RemoteFrameEncoding.Jpeg,
                    RemoteFrameFlags.None,
                    [1],
                    0,
                    1,
                    0,
                    0),
                bitmap,
                hardwareFrame: null,
                hardwareDecoderGeneration: 0,
                decodeMilliseconds: 0,
                decoderBackendName: null,
                decoderUsesHardwareAcceleration: false);

        frame.Dispose();
        frame.Dispose();

        Assert.Throws<ArgumentException>(
            () => _ = bitmap.Width);
    }

    [Fact]
    public void PendingFrameReplacementAndShutdownReturnViewerPoolLeases()
    {
        var pool = new TrackingByteArrayPool();
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "pooled-frame-replacement-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false,
                encodedFramePool: pool);
        SetPrivateField(
            window,
            "_waitingForViewerHandle",
            true);

        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.Jpeg,
                [1, 2, 3]));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(1, pool.ActiveCount);

        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.Jpeg,
                [4, 5, 6, 7]));
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(1, pool.ActiveCount);

        InvokePrivate(
            window,
            "ClearPendingVideoFramesForShutdown");
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void IndependentH264BacklogKeepsOnlyNewestPooledFrame()
    {
        Assert.True(
            RemoteViewerClient
                .LocalH264SamplesAreAllIndependent);
        var pool = new TrackingByteArrayPool();
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "pooled-independent-latest-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false,
                encodedFramePool: pool);
        SetPrivateField(
            window,
            "_waitingForViewerHandle",
            true);
        const RemoteFrameFlags independentFlags =
            RemoteFrameFlags.KeyFrame |
            RemoteFrameFlags.CodecConfig;

        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.H264AnnexB,
                [0, 0, 0, 1, 0x65],
                independentFlags));
        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.H264AnnexB,
                [0, 0, 0, 1, 0x65, 1],
                independentFlags));
        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.H264AnnexB,
                [0, 0, 0, 1, 0x65, 2],
                independentFlags));

        Assert.Equal(3, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.Equal(1, pool.ActiveCount);

        InvokePrivate(
            window,
            "ClearPendingVideoFramesForShutdown");
        Assert.Equal(3, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void RecoveryWaitDropReturnsViewerPoolLease()
    {
        var pool = new TrackingByteArrayPool();
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "pooled-frame-drop-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false,
                encodedFramePool: pool);
        SetPrivateField(
            window,
            "_waitingForViewerHandle",
            true);
        SetPrivateField(
            window,
            "_waitingForH264RecoveryFrame",
            true);

        InvokeFrameReceived(
            window,
            CreateEncodedTestFrame(
                RemoteFrameEncoding.H264AnnexB,
                [0, 0, 0, 1, 0x41]));

        Assert.Equal(1, pool.RentCount);
        Assert.Equal(1, pool.ReturnCount);
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void ClosingViewerDoesNotAcceptDeferredFrameOwnership()
    {
        var pool = new TrackingByteArrayPool();
        using var client = new RemoteViewerClient();
        using var window =
            new RemoteViewerWindow(
                client,
                "pooled-frame-defer-test",
                inputEnabled: false,
                clipboardTextEnabled: false,
                filePasteEnabled: false,
                fileDropPasteEnabled: false,
                remoteFilePullEnabled: false,
                isAndroidRemote: false,
                encodedFramePool: pool);
        using PooledRemoteFrame owned =
            PooledRemoteFrame.CopyFrom(
                CreateEncodedTestFrame(
                    RemoteFrameEncoding.H264AnnexB,
                    [0, 0, 0, 1, 0x65]),
                pool);
        SetPrivateField(window, "_isClosing", true);

        bool accepted = Assert.IsType<bool>(
            InvokePrivate(
                window,
                "DeferFrameUntilViewerHandle",
                owned));

        Assert.False(accepted);
        Assert.Equal(1, pool.ActiveCount);
    }

    [Fact]
    public void AlreadyPresentedFrameCarriesMetadataWithoutGpuPayload()
    {
        long receivedAt = Stopwatch.GetTimestamp();
        var frame =
            new RemoteViewerWindow.DecodedRemoteFrame(
                new RemoteFrame(
                    64,
                    64,
                    RemoteFrameEncoding.H264AnnexB,
                    RemoteFrameFlags.KeyFrame |
                        RemoteFrameFlags.CodecConfig,
                    [1],
                    0,
                    1,
                    0,
                    0,
                    receivedAt),
                bitmap: null,
                hardwareFrame: null,
                hardwareDecoderGeneration: 1,
                decodeMilliseconds: 1,
                decoderBackendName: "MF/D3D11",
                decoderUsesHardwareAcceleration: true,
                decodeStartedAtTimestamp:
                    receivedAt + 1,
                decodedAtTimestamp:
                    receivedAt + 2,
                alreadyPresented: true,
                presentedAtTimestamp:
                    receivedAt + 3);

        Assert.True(frame.AlreadyPresented);
        Assert.Null(frame.DetachBitmap());
        Assert.Null(frame.DetachHardwareFrame());
        Assert.Equal(
            receivedAt + 3,
            frame.PresentedAtTimestamp);
        frame.Dispose();
    }

    [Fact]
    public void DecodersReceiveOnlyDeclaredAccessUnitSliceWithoutCopy()
    {
        byte[] transportBuffer =
            [90, 91, 0, 0, 0, 1, 0x65, 92];
        var frame =
            new RemoteFrame(
                64,
                64,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame |
                    RemoteFrameFlags.CodecConfig,
                transportBuffer,
                EncodedOffset: 2,
                EncodedLength: 5,
                CaptureMilliseconds: 0,
                EncodeMilliseconds: 0);

        ReadOnlyMemory<byte> accessUnit =
            RemoteViewerWindow.GetFramePayload(
                frame);

        Assert.Equal(
            [0, 0, 0, 1, 0x65],
            accessUnit.ToArray());
        Assert.True(
            MemoryMarshal.TryGetArray(
                accessUnit,
                out ArraySegment<byte> segment));
        Assert.Same(
            transportBuffer,
            segment.Array);
        Assert.Equal(2, segment.Offset);
        Assert.Equal(5, segment.Count);
    }

    [Fact]
    public void SoftwareFallbackNeverBootstrapsFromDependentAccessUnit()
    {
        Assert.False(
            RemoteViewerWindow
                .CanFallbackSameAccessUnitToSoftware(
                    isRecoveryFrame: false));
        Assert.True(
            RemoteViewerWindow
                .CanFallbackSameAccessUnitToSoftware(
                    isRecoveryFrame: true));
    }

    [Theory]
    [InlineData(0, 720, false)]
    [InlineData(1280, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(1280, 720, true)]
    public void DirectPresenterWaitsForPositiveClientArea(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerWindow
                .IsD3DClientAreaReady(
                    new Size(
                        width,
                        height)));
    }

    private static RemoteFrame CreateEncodedTestFrame(
        RemoteFrameEncoding encoding,
        byte[] payload,
        RemoteFrameFlags flags = RemoteFrameFlags.None) =>
        new(
            64,
            64,
            encoding,
            flags,
            payload,
            EncodedOffset: 0,
            EncodedLength: payload.Length,
            CaptureMilliseconds: 0,
            EncodeMilliseconds: 0);

    private static void AssertPhysicalKey(
        RemoteInputCommand command,
        RemoteInputKind kind,
        Keys virtualKey,
        int scanCode,
        RemoteKeyboardFlags flags)
    {
        Assert.Equal(
            kind,
            command.Kind);
        Assert.Equal(
            (int)virtualKey,
            command.Data);
        Assert.Equal(
            scanCode,
            command.X);
        Assert.Equal(
            flags,
            (RemoteKeyboardFlags)command.Y);
    }

    private static void InvokeFrameReceived(
        RemoteViewerWindow window,
        RemoteFrame frame)
    {
        InvokePrivate(
            window,
            "OnFrameReceived",
            frame);
    }

    private static object? InvokePrivate(
        RemoteViewerWindow window,
        string methodName,
        params object[] arguments)
    {
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(
            typeof(RemoteViewerWindow).GetMethod(
                methodName,
                BindingFlags.Instance |
                    BindingFlags.NonPublic));
        return method.Invoke(window, arguments);
    }

    private static void SetPrivateField(
        RemoteViewerWindow window,
        string fieldName,
        object value)
    {
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(RemoteViewerWindow).GetField(
                fieldName,
                BindingFlags.Instance |
                    BindingFlags.NonPublic));
        field.SetValue(window, value);
    }
}
