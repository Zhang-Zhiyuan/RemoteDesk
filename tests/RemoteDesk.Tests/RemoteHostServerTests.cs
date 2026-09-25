using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteHostServerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnchangedJpegIsSuppressedOnReliableVideoWithoutChangingUdpRecovery(bool udp, bool expected)
    {
        Assert.Equal(expected, RemoteHostServer.ShouldGateUnchangedReliableJpeg(udp));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void OnlyTcpStartupPreviewStopsAfterFirstSuccessfulFrame(
        bool startupPreviewOnly,
        bool udpRouteActive,
        bool expected)
    {
        Assert.Equal(expected, RemoteHostServer.ShouldFinishJpegStartupPreview(
            startupPreviewOnly, udpRouteActive));
    }

    [Fact]
    public async Task PostPreviewNegotiationWaitEndsWhenExplicitJpegSelectionArrives()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task waiting = state.WaitForInitialVideoSelectionAsync(
            RemoteHostServer.InitialViewerInfoPreviewGracePeriod, deadline.Token);
        Assert.False(waiting.IsCompleted);
        state.SetSupportedVideoCodecs(RemoteVideoCodecs.Jpeg);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(state.GetVideoSelection().Version > 0);
    }

    [Fact]
    public async Task PostPreviewNegotiationWaitPreservesCancellation()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        using var stop = new CancellationTokenSource();
        Task waiting = state.WaitForInitialVideoSelectionAsync(
            RemoteHostServer.InitialViewerInfoPreviewGracePeriod, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void SameCaptureTargetSelectionDoesNotRequirePipelineRestart()
    {
        var current = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);
        var refreshed = current with
        {
            DisplayName = "屏幕 1 主屏 (3840x2160)"
        };

        Assert.True(
            RemoteHostServer.ShouldReuseCaptureTarget(
                current,
                current.Bounds,
                refreshed));
        Assert.False(
            RemoteHostServer.ShouldReuseCaptureTarget(
                current,
                current.Bounds,
                refreshed with
                {
                    Bounds =
                        new Rectangle(
                            0,
                            0,
                            2560,
                            1440)
                }));
    }

    [Fact]
    public void PhysicalTargetUnplugAndSameIdReconnectEachAdvanceGeneration()
    {
        Rectangle physicalBounds =
            new(3840, 0, 2560, 1440);
        var unavailable =
            new ScreenCaptureTargetAvailability(
                IsAvailable: false,
                physicalBounds);
        var reconnected =
            new ScreenCaptureTargetAvailability(
                IsAvailable: true,
                physicalBounds);

        Assert.True(
            RemoteHostServer
                .ShouldAdvanceCaptureTargetGeneration(
                    wasAvailable: true,
                    physicalBounds,
                    unavailable));
        Assert.False(
            RemoteHostServer
                .ShouldAdvanceCaptureTargetGeneration(
                    wasAvailable: false,
                    physicalBounds,
                    unavailable));
        Assert.True(
            RemoteHostServer
                .ShouldAdvanceCaptureTargetGeneration(
                    wasAvailable: false,
                    physicalBounds,
                    reconnected));
        Assert.False(
            RemoteHostServer
                .ShouldAdvanceCaptureTargetGeneration(
                    wasAvailable: true,
                    physicalBounds,
                    reconnected));
    }

    [Fact]
    public void OnlyPointerCommandsRequireAnAvailableCaptureTarget()
    {
        Assert.True(
            RemoteHostServer.RequiresAvailableCaptureTarget(
                RemoteInputCommand.MouseMove(1, 2)));
        Assert.True(
            RemoteHostServer.RequiresAvailableCaptureTarget(
                RemoteInputCommand.MouseUp(
                    RemoteMouseButton.Left,
                    1,
                    2)));
        Assert.True(
            RemoteHostServer.IsTargetIndependentPointerRelease(
                RemoteInputCommand.MouseUp(
                    RemoteMouseButton.Left,
                    1,
                    2)));
        Assert.False(
            RemoteHostServer.IsTargetIndependentPointerRelease(
                RemoteInputCommand.MouseDown(
                    RemoteMouseButton.Left,
                    1,
                    2)));
        Assert.False(
            RemoteHostServer.RequiresAvailableCaptureTarget(
                RemoteInputCommand.KeyUp(
                    (int)Keys.ControlKey)));
        Assert.False(
            RemoteHostServer.RequiresAvailableCaptureTarget(
                RemoteInputCommand.TextInput('A')));
    }

    [Fact]
    public void UnavailableTargetMouseReleaseFailureRemainsOwnedForTeardown()
    {
        var tracker =
            new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(
            RemoteInputCommand.MouseDown(
                RemoteMouseButton.Left,
                10,
                20));
        var nativeFailure = new SendInputException(
            sentCount: 0,
            expectedCount: 1,
            nativeError: 5);

        SendInputException observed =
            Assert.Throws<SendInputException>(() =>
                RemoteHostServer
                    .ReleaseTargetIndependentPointerAndTrack(
                        RemoteInputCommand.MouseUp(
                            RemoteMouseButton.Left,
                            10,
                            20),
                        tracker,
                        _ => throw nativeFailure));

        Assert.Same(nativeFailure, observed);
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        var teardownReleases =
            new List<RemoteMouseButton>();
        RemoteHostServer.RemoteInputReleaseResult result =
            tracker.ReleaseAll(
                _ => { },
                teardownReleases.Add);
        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.ReleasedCount);
        Assert.Empty(result.Failures);
        Assert.Equal(
            [RemoteMouseButton.Left],
            teardownReleases);
        Assert.Equal(0, tracker.PressedMouseButtonCount);
    }

    [Fact]
    public void ActiveClientGateLetsNewestAuthenticatedClientReplaceOwner()
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        object first = new();
        object second = new();

        Assert.Null(gate.Activate(first));
        Assert.Same(first, gate.Current);
        Assert.Same(first, gate.Activate(second));
        Assert.Same(second, gate.Current);

        Assert.False(gate.Release(first));
        Assert.Same(second, gate.Current);
        Assert.True(gate.Release(second));
        Assert.Null(gate.Current);
    }

    [Fact]
    public async Task ReplacedClientReceivesTerminalReasonBeforeSocketCloses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        using var viewerClient = new TcpClient();
        await viewerClient.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient hostClient = await acceptTask;
        await using NetworkStream viewerStream = viewerClient.GetStream();
        await using NetworkStream hostStream = hostClient.GetStream();
        byte[] clientToServerKey = Enumerable.Repeat((byte)0x31, 32).ToArray();
        byte[] serverToClientKey = Enumerable.Repeat((byte)0x72, 32).ToArray();
        using var hostSession = new SecureSession(
            clientToServerKey,
            serverToClientKey,
            isServer: true);
        using var viewerSession = new SecureSession(
            clientToServerKey,
            serverToClientKey,
            isServer: false);
        using var writePriority = new RemoteHostServer.SessionWritePriority();
        using var cancellation = new CancellationTokenSource();
        var owner = new RemoteHostServer.ActiveClientConnection(
            hostClient,
            hostStream,
            hostSession,
            writePriority,
            cancellation);

        Task replacementTask = owner.DisconnectForReplacementAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ProtocolMessage message = await Protocol.ReadMessageAsync(
            viewerStream,
            viewerSession,
            timeout.Token);
        owner.MarkClosed();
        await replacementTask;

        RemoteControlMessage control =
            RemoteMessageCodec.DecodeControl(message.PayloadMemory);
        Assert.Equal(RemoteControlKind.SessionRejected, control.Kind);
        Assert.Contains("另一台查看端接管", control.StatusMessage);
        Assert.True(owner.ReplacementRequested);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void BoundedClientAdmissionGateRejectsBeyondLimitAndReleasesSlots()
    {
        var gate = new RemoteHostServer.BoundedClientAdmissionGate(2);

        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(2, gate.Current);

        gate.Exit();

        Assert.True(gate.TryEnter());
        Assert.Equal(2, gate.Current);
    }

    [Fact]
    public void ViewerSessionStateStartsWithLegacyJpegAndPublishesNegotiatedCodecs()
    {
        var state = new RemoteHostServer.ViewerSessionState();

        Assert.Equal(RemoteVideoCodecs.Jpeg, state.SupportedVideoCodecs);
        Assert.Equal(0, state.VideoCodecVersion);
        Assert.False(state.IsVideoSelectionReady);

        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB);

        Assert.Equal(
            RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB,
            state.SupportedVideoCodecs);
        Assert.Equal(1, state.VideoCodecVersion);
        Assert.True(state.IsVideoSelectionReady);
    }

    [Fact]
    public async Task ViewerSelectionWaitSupportsModernSignalAndLegacyGrace()
    {
        var modern = new RemoteHostServer.ViewerSessionState();
        Task wait = modern.WaitForInitialVideoSelectionAsync(
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        modern.SetSupportedVideoCodecs(
            RemoteVideoCodecs.H264AnnexB);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        var legacy = new RemoteHostServer.ViewerSessionState();
        await legacy.WaitForInitialVideoSelectionAsync(
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.False(legacy.IsVideoSelectionReady);
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            RemoteHostServer.InitialViewerInfoGracePeriod);
        Assert.Equal(
            TimeSpan.FromMilliseconds(10),
            RemoteHostServer.InitialViewerCapabilitiesGracePeriod);
    }

    [Fact]
    public async Task LateFeedbackCapabilitiesRepublishVideoSelectionAfterGrace()
    {
        const RemoteDeviceCapabilities feedbackUdpCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback;
        var state = new RemoteHostServer.ViewerSessionState();
        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.Jpeg |
            RemoteVideoCodecs.H264AnnexB);
        int initialSelectionVersion = state.VideoCodecVersion;

        // Model the capture loop exhausting its compatibility grace before
        // the second ordered control packet is scheduled/read.
        await state.WaitForInitialCapabilitiesAsync(
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.Equal(RemoteDeviceCapabilities.None, state.Capabilities);
        Assert.Equal(
            30,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    state.Capabilities));

        state.Capabilities = feedbackUdpCapabilities;

        Assert.Equal(
            initialSelectionVersion + 1,
            state.VideoCodecVersion);
        Assert.Equal(
            RemoteVideoCodecs.Jpeg |
                RemoteVideoCodecs.H264AnnexB,
            state.SupportedVideoCodecs);
        Assert.Equal(
            60,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    state.Capabilities));
    }

    [Fact]
    public void CapabilitiesBeforeViewerInfoDoNotPublishLegacyJpegSelection()
    {
        const RemoteDeviceCapabilities feedbackUdpCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback;
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = feedbackUdpCapabilities
        };

        Assert.False(state.IsVideoSelectionReady);
        Assert.Equal(0, state.VideoCodecVersion);

        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.H264AnnexB);

        Assert.True(state.IsVideoSelectionReady);
        Assert.Equal(1, state.VideoCodecVersion);
        Assert.Equal(
            RemoteVideoCodecs.H264AnnexB,
            state.SupportedVideoCodecs);
        Assert.Equal(
            60,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    state.Capabilities));
    }

    [Fact]
    public void DdaShortGopSelectsNinetyHertzOnlyAtConfiguredThirty()
    {
        const RemoteDeviceCapabilities shortGopFeedbackUdpCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
            RemoteDeviceCapabilities.ShortGopH264;
        const RemoteDeviceCapabilities feedbackUdpCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback;

        Assert.Equal(
            90,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    shortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: true));
        Assert.Equal(
            60,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    feedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: true));
        Assert.Equal(
            30,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 30,
                    adaptiveQuality: true,
                    shortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: false));
        Assert.Equal(
            60,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 40,
                    adaptiveQuality: true,
                    shortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: true));
        Assert.Equal(
            60,
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    configuredFramesPerSecond: 60,
                    adaptiveQuality: true,
                    shortGopFeedbackUdpCapabilities,
                    supportsGpuSurfaceCapture: true));
    }

    [Fact]
    public void IrrelevantOrDuplicateCapabilitiesDoNotRestartVideoSelection()
    {
        const RemoteDeviceCapabilities feedbackUdpCapabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback;
        var state = new RemoteHostServer.ViewerSessionState();
        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.H264AnnexB);
        int initialSelectionVersion = state.VideoCodecVersion;

        state.Capabilities =
            RemoteDeviceCapabilities.FileChecksum;
        Assert.Equal(
            initialSelectionVersion,
            state.VideoCodecVersion);

        state.Capabilities =
            feedbackUdpCapabilities |
            RemoteDeviceCapabilities.FileChecksum;
        Assert.Equal(
            initialSelectionVersion + 1,
            state.VideoCodecVersion);

        state.Capabilities =
            feedbackUdpCapabilities |
            RemoteDeviceCapabilities.FileChecksum |
            RemoteDeviceCapabilities.FileTransferCancel;
        Assert.Equal(
            initialSelectionVersion + 1,
            state.VideoCodecVersion);

        state.Capabilities =
            RemoteDeviceCapabilities.LowLatencyUdpVideo;
        Assert.Equal(
            initialSelectionVersion + 2,
            state.VideoCodecVersion);
    }

    [Fact]
    public void AuthenticatedHeartbeatCapabilityChangeRestartsCaptureSelection()
    {
        const RemoteDeviceCapabilities feedback =
            RemoteDeviceCapabilities.LowLatencyUdpVideo |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback;
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = feedback
        };
        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.H264AnnexB);
        int initialVersion = state.VideoCodecVersion;

        state.Capabilities =
            feedback |
            RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat;
        Assert.Equal(
            initialVersion + 1,
            state.VideoCodecVersion);

        state.Capabilities = feedback;
        Assert.Equal(
            initialVersion + 2,
            state.VideoCodecVersion);
    }

    [Fact]
    public void HighQualityJpegCapabilityChangeRestartsCaptureSelection()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        state.SetSupportedVideoCodecs(RemoteVideoCodecs.Jpeg);
        int initialVersion = state.VideoCodecVersion;

        state.Capabilities =
            RemoteDeviceCapabilities.HighQualityJpeg;
        Assert.Equal(
            initialVersion + 1,
            state.VideoCodecVersion);

        state.Capabilities = RemoteDeviceCapabilities.None;
        Assert.Equal(
            initialVersion + 2,
            state.VideoCodecVersion);
    }

    [Fact]
    public void ViewerSessionStateRejectsUnknownOrEmptyCodecAdvertisements()
    {
        var state = new RemoteHostServer.ViewerSessionState();

        state.SetSupportedVideoCodecs((RemoteVideoCodecs)(1 << 20));

        Assert.Equal(RemoteVideoCodecs.Jpeg, state.SupportedVideoCodecs);
    }

    [Fact]
    public async Task ViewerVideoSelectionPublishesCodecAndVersionAtomically()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        const int updateCount = 20_000;
        using var start = new ManualResetEventSlim(initialState: false);

        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int version = 1; version <= updateCount; version++)
            {
                state.SetSupportedVideoCodecs(
                    (version & 1) == 0
                        ? RemoteVideoCodecs.Jpeg
                        : RemoteVideoCodecs.H264AnnexB);
            }
        });

        start.Set();
        while (!writer.IsCompleted)
        {
            RemoteHostServer.ViewerVideoSelection selection =
                state.GetVideoSelection();
            RemoteVideoCodecs expected =
                (selection.Version & 1) == 0
                    ? RemoteVideoCodecs.Jpeg
                    : RemoteVideoCodecs.H264AnnexB;
            Assert.Equal(expected, selection.SupportedCodecs);
        }

        await writer;
        RemoteHostServer.ViewerVideoSelection finalSelection =
            state.GetVideoSelection();
        Assert.Equal(updateCount, finalSelection.Version);
        Assert.Equal(
            RemoteVideoCodecs.Jpeg,
            finalSelection.SupportedCodecs);
    }

    [Fact]
    public async Task VideoSelectionChangeCancelsAStaticH264FrameRead()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        state.SetSupportedVideoCodecs(
            RemoteVideoCodecs.H264AnnexB);
        int version = state.VideoCodecVersion;
        using var sessionCancellation =
            new CancellationTokenSource();
        using var captureSelectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation.Token,
                state.GetVideoSelectionChangeToken(version));
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<object?> ReadStaticFrameAsync(
            CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return new object();
        }

        Task<(object? Frame, bool SelectionChanged)> read =
            RemoteHostServer
                .ReadH264FrameUntilSelectionChangesAsync<object>(
                    ReadStaticFrameAsync,
                    captureSelectionCancellation.Token,
                    sessionCancellation.Token)
                .AsTask();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        state.SetSupportedVideoCodecs(RemoteVideoCodecs.Jpeg);
        (object? frame, bool selectionChanged) =
            await read.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(frame);
        Assert.True(selectionChanged);
        Assert.True(captureSelectionCancellation.IsCancellationRequested);
        Assert.False(
            state.GetVideoSelectionChangeToken(
                    state.VideoCodecVersion)
                .IsCancellationRequested);
    }

    [Fact]
    public async Task TargetGenerationChangeCancelsAStaticH264FrameRead()
    {
        var syncRoot = new object();
        using var targetChanged = new CancellationTokenSource();
        const int activeVersion = 7;
        using var sessionCancellation =
            new CancellationTokenSource();
        using var captureSelectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation.Token,
                RemoteHostServer.GetGenerationChangeToken(
                        syncRoot,
                        readCurrentVersion: () => activeVersion,
                        expectedVersion: activeVersion,
                        readChangeToken: () =>
                            targetChanged.Token));
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<object?> ReadStaticFrameAsync(
            CancellationToken cancellationToken)
        {
            readStarted.TrySetResult();
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            return new object();
        }

        Task<(object? Frame, bool SelectionChanged)> read =
            RemoteHostServer
                .ReadH264FrameUntilSelectionChangesAsync<object>(
                    ReadStaticFrameAsync,
                    captureSelectionCancellation.Token,
                    sessionCancellation.Token)
                .AsTask();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        targetChanged.Cancel();
        (object? frame, bool selectionChanged) =
            await read.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(frame);
        Assert.True(selectionChanged);
        Assert.True(captureSelectionCancellation.IsCancellationRequested);
        using var nextGenerationChanged =
            new CancellationTokenSource();
        Assert.True(
            RemoteHostServer.GetGenerationChangeToken(
                    syncRoot,
                    readCurrentVersion: () =>
                        activeVersion + 1,
                    expectedVersion: activeVersion,
                    readChangeToken: () =>
                        nextGenerationChanged.Token)
                .IsCancellationRequested);
    }

    [Fact]
    public async Task StaticH264TargetMonitorRecoversAfterTransientRefreshFailure()
    {
        int refreshAttempts = 0;
        int cancelCount = 0;
        var logs = new List<string>();

        await RemoteHostServer.MonitorStaticH264CaptureTargetAsync(
            refreshCaptureBounds: () =>
            {
                int attempt = Interlocked.Increment(
                    ref refreshAttempts);
                if (attempt == 1)
                {
                    throw new InvalidOperationException(
                        "temporary topology query failure");
                }

                return attempt == 2;
            },
            isSelectionCurrent: () => true,
            cancelSelection: () =>
                Interlocked.Increment(ref cancelCount),
            logs.Add,
            CancellationToken.None,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(2, Volatile.Read(ref refreshAttempts));
        Assert.Equal(0, Volatile.Read(ref cancelCount));
        Assert.Single(
            logs,
            message => message.Contains(
                "暂时失败",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaticH264TargetMonitorBoundsPersistentRefreshFailure()
    {
        int refreshAttempts = 0;
        int cancelCount = 0;
        var logs = new List<string>();

        await RemoteHostServer.MonitorStaticH264CaptureTargetAsync(
            refreshCaptureBounds: () =>
            {
                Interlocked.Increment(ref refreshAttempts);
                throw new System.ComponentModel.Win32Exception(
                    "persistent topology query failure");
            },
            isSelectionCurrent: () => true,
            cancelSelection: () =>
                Interlocked.Increment(ref cancelCount),
            logs.Add,
            CancellationToken.None,
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(
            RemoteHostServer
                .StaticH264CaptureTargetRefreshFailureLimit,
            Volatile.Read(ref refreshAttempts));
        Assert.Equal(1, Volatile.Read(ref cancelCount));
        Assert.Single(
            logs,
            message => message.Contains(
                "连续失败 3 次",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor,
        (int)(RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback),
        true)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor,
        (int)RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat,
        false)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor,
        (int)RemoteDeviceCapabilities.None,
        false)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0,
        (int)(RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback),
        false)]
    [InlineData(
        (int)FfmpegDesktopCaptureBackend.GdiGrabBounds,
        (int)(RemoteDeviceCapabilities.AuthenticatedUdpHeartbeat |
            RemoteDeviceCapabilities.UdpVideoCongestionFeedback),
        false)]
    public void AuthenticatedHeartbeatAllowsStaticSilenceOnlyForWgc(
        int backend,
        int capabilities,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteHostServer.ShouldAllowStaticWgcFrameSilence(
                (FfmpegDesktopCaptureBackend)backend,
                (RemoteDeviceCapabilities)capabilities));
    }

    [Fact]
    public void ViewerSessionStatePublishesEveryKeyFrameRequest()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        int initialVersion = state.KeyFrameRequestVersion;

        state.RequestVideoKeyFrame();
        state.RequestVideoKeyFrame();

        Assert.Equal(initialVersion + 2, state.KeyFrameRequestVersion);
    }

    [Fact]
    public void ViewerSessionStateDoesNotOverwritePendingClipboardReturnPlan()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        var first = new RemoteFilePastePlan([@"C:\first.txt"], 0, 0, false);
        var second = new RemoteFilePastePlan([@"C:\second.txt"], 0, 0, false);

        Assert.True(state.TryReserveClipboardFileReturnPlan(first));
        Assert.True(state.HasPendingClipboardFileReturnPlan);
        Assert.False(state.TryReserveClipboardFileReturnPlan(second));
        Assert.Same(first, state.TakePendingClipboardFileReturnPlan());
        Assert.False(state.HasPendingClipboardFileReturnPlan);
        Assert.Null(state.TakePendingClipboardFileReturnPlan());

        Assert.True(state.TryReserveClipboardFileReturnPlan(second));
        Assert.True(state.ClearPendingClipboardFileReturnPlan());
        Assert.False(state.HasPendingClipboardFileReturnPlan);
        Assert.False(state.ClearPendingClipboardFileReturnPlan());
    }

    [Fact]
    public async Task ViewerSessionStateAllowsOnlyOneCancellableClipboardReturnOperation()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(state.TryStartClipboardFileReturn(
            async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                }
            },
            CancellationToken.None));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.HasActiveClipboardFileReturn);
        Assert.False(state.TryStartClipboardFileReturn(_ => Task.CompletedTask, CancellationToken.None));
        Assert.False(state.TryReserveClipboardFileReturnPlan(
            new RemoteFilePastePlan([@"C:\second.txt"], 0, 0, false)));

        Assert.True(await state.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(state.HasActiveClipboardFileReturn);
        Assert.False(state.CancelActiveClipboardFileReturn());
        Assert.False(await state.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.TryStartClipboardFileReturn(_ => Task.CompletedTask, CancellationToken.None));
        Assert.True(SpinWait.SpinUntil(
            () => !state.HasActiveClipboardFileReturn,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ViewerSessionStateAdmitsExactlyOneConcurrentClipboardReturnStarter()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        var startGate = new ManualResetEventSlim(initialState: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool>[] contenders = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                return state.TryStartClipboardFileReturn(
                    async _ => await release.Task,
                    CancellationToken.None);
            }))
            .ToArray();

        startGate.Set();
        bool[] admitted = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, admitted.Count(result => result));
        Assert.True(state.HasActiveClipboardFileReturn);

        release.TrySetResult();
        Assert.True(SpinWait.SpinUntil(
            () => !state.HasActiveClipboardFileReturn,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BackgroundClipboardReturnPriorityLeaseStillAllowsPromptControlWrite()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        using var priority = new RemoteHostServer.SessionWritePriority();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(state.TryStartClipboardFileReturn(
            async cancellationToken =>
            {
                using IDisposable priorityLease = priority.BeginControlWritePriority();
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            },
            CancellationToken.None));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(priority.HasPendingControlWrite);

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await priority.Lock.WaitAsync(timeout.Token);
            priority.Lock.Release();
        }

        Assert.True(state.CancelActiveClipboardFileReturn());
        await state.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(priority.HasPendingControlWrite);
    }

    [Fact]
    public void ViewerSessionStateConsumesLatestClipboardInputSequenceOnce()
    {
        var state = new RemoteHostServer.ViewerSessionState();

        state.RecordClipboardInputSequence(17, nowMilliseconds: 1_000);
        state.RecordClipboardInputSequence(18, nowMilliseconds: 1_100);

        uint? sequence = state.TakePendingClipboardInputSequence(
            nowMilliseconds: 1_200,
            maxAgeMilliseconds: 500,
            out bool expired);

        Assert.Equal((uint)18, sequence);
        Assert.False(expired);
        Assert.Null(state.TakePendingClipboardInputSequence(
            nowMilliseconds: 1_200,
            maxAgeMilliseconds: 500,
            out expired));
        Assert.False(expired);
    }

    [Fact]
    public async Task ExpiredClipboardInputSequenceFailsSafelyWithoutReadingClipboard()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        state.RecordClipboardInputSequence(17, nowMilliseconds: 1_000);
        bool waitCalled = false;

        bool? ready = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 3_001,
            (baseline, _) =>
            {
                waitCalled = true;
                return Task.FromResult(true);
            });

        Assert.False(ready);
        Assert.False(waitCalled);
    }

    [Fact]
    public async Task TextClipboardWaitDoesNotConsumeFailureNeededByFollowingFileRequest()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        state.RecordClipboardInputSequence(29, nowMilliseconds: 1_000);
        int waitCalls = 0;

        bool? textReady = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 1_100,
            (baseline, _) =>
            {
                Assert.Equal((uint)29, baseline);
                waitCalls++;
                return Task.FromResult(false);
            },
            consumeMarker: false);
        bool? filesReady = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 1_200,
            (baseline, _) =>
            {
                Assert.Equal((uint)29, baseline);
                waitCalls++;
                return Task.FromResult(false);
            });
        bool? markerAfterFileRequest = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 1_300,
            (_, _) => Task.FromResult(true));

        Assert.False(textReady);
        Assert.False(filesReady);
        Assert.Null(markerAfterFileRequest);
        Assert.Equal(2, waitCalls);
    }

    [Fact]
    public async Task ClipboardMutationBaselineSurvivesApplyFailure()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        var tracker = new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ControlKey));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RemoteHostServer.ApplyInputAndTrackClipboardMutation(
                RemoteInputCommand.KeyDown((int)Keys.C),
                tracker,
                state,
                Rectangle.Empty,
                Size.Empty,
                static (_, _, _) => throw new InvalidOperationException("apply failed"),
                static () => 71,
                static () => 1_000));

        bool waitCalled = false;
        bool? ready = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 1_100,
            (baseline, _) =>
            {
                Assert.Equal((uint)71, baseline);
                waitCalled = true;
                return Task.FromResult(false);
            });

        Assert.Equal("apply failed", error.Message);
        Assert.True(waitCalled);
        Assert.False(ready);
        Assert.Equal(1, tracker.PressedKeyCount);
    }

    [Fact]
    public async Task SuccessfulClipboardMutationStillWaitsForNewClipboardSequence()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        var tracker = new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ControlKey));
        bool applied = false;

        RemoteHostServer.ApplyInputAndTrackClipboardMutation(
            RemoteInputCommand.KeyDown((int)Keys.C),
            tracker,
            state,
            Rectangle.Empty,
            Size.Empty,
            (_, _, _) => applied = true,
            static () => 41,
            static () => 1_000);

        bool? ready = await RemoteHostServer.WaitForPendingClipboardInputAsync(
            state,
            nowMilliseconds: 1_100,
            (baseline, _) => Task.FromResult(baseline == 41));

        Assert.True(applied);
        Assert.True(ready);
        Assert.Equal(2, tracker.PressedKeyCount);
    }

    [Theory]
    [InlineData(
        (int)RemoteControlKind.ClipboardGetText,
        true)]
    [InlineData(
        (int)RemoteControlKind.FileTransferRequestClipboardFiles,
        true)]
    [InlineData(
        (int)RemoteControlKind.ClipboardSetText,
        false)]
    [InlineData(
        (int)RemoteControlKind.FileTransferChunk,
        false)]
    public void OnlySlowReadOnlyClipboardControlsAllowInputOvertake(
        int kind,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteHostServer
                .CanInputOvertakeControl(
                    (RemoteControlKind)kind));
    }

    [Fact]
    public async Task InputIndependentControlQueueIsSerialAndRecoversAfterFailure()
    {
        var state =
            new RemoteHostServer.ViewerSessionState();
        var releaseFirst =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var firstStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var order = new List<int>();
        var failures = new List<string>();

        state.QueueInputIndependentControl(
            async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                order.Add(1);
                throw new InvalidOperationException(
                    "expected");
            },
            ex => failures.Add(ex.Message));
        state.QueueInputIndependentControl(
            () =>
            {
                order.Add(2);
                return Task.CompletedTask;
            },
            ex => failures.Add(ex.Message));

        await firstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.Empty(order);
        releaseFirst.TrySetResult();
        await state
            .WaitForInputIndependentControlsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([1, 2], order);
        Assert.Equal(["expected"], failures);
    }

    [Fact]
    public async Task InputIndependentControlQueueSkipsPendingWorkAfterSessionCancellation()
    {
        var state =
            new RemoteHostServer.ViewerSessionState();
        var releaseFirst =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var firstStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        using var cancellation =
            new CancellationTokenSource();
        int pendingRuns = 0;
        var failures = new List<Exception>();

        state.QueueInputIndependentControl(
            async () =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            },
            failures.Add);
        state.QueueInputIndependentControl(
            () =>
            {
                Interlocked.Increment(
                    ref pendingRuns);
                return Task.CompletedTask;
            },
            failures.Add,
            cancellation.Token);

        await firstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        releaseFirst.TrySetResult();
        await state
            .WaitForInputIndependentControlsAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, pendingRuns);
        Assert.Single(failures);
        Assert.IsType<OperationCanceledException>(
            failures[0]);
    }

    [Fact]
    public async Task NormalControlBarrierWaitsForEarlierInputIndependentRead()
    {
        var state =
            new RemoteHostServer.ViewerSessionState();
        var releaseRead =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var readStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var barrierStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        int normalControlRuns = 0;

        state.QueueInputIndependentControl(
            async () =>
            {
                readStarted.TrySetResult();
                await releaseRead.Task;
            },
            _ => { });
        await readStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        Task normalControl =
            RunNormalControlAfterBarrierAsync();
        await barrierStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.Equal(0, normalControlRuns);

        releaseRead.TrySetResult();
        await normalControl.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.Equal(1, normalControlRuns);

        async Task RunNormalControlAfterBarrierAsync()
        {
            barrierStarted.TrySetResult();
            await state
                .WaitForInputIndependentControlsAsync();
            Interlocked.Increment(
                ref normalControlRuns);
        }
    }

    [Fact]
    public void InputStateTrackerRecognizesClipboardCopyAndCutShortcuts()
    {
        var tracker = new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ControlKey));

        Assert.True(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyDown((int)Keys.C)));
        Assert.True(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyDown((int)Keys.X)));
        Assert.True(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyDown((int)Keys.Insert)));
        Assert.False(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyDown((int)Keys.V)));

        tracker.Observe(RemoteInputCommand.KeyUp((int)Keys.ControlKey));
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ShiftKey));
        Assert.True(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyDown((int)Keys.Delete)));
        Assert.False(tracker.IsClipboardMutationShortcut(RemoteInputCommand.KeyUp((int)Keys.Delete)));
    }

    [Fact]
    public void InputErrorLogThrottlerSuppressesBurstsAndReportsMergedCount()
    {
        var throttler = new RemoteHostServer.InputErrorLogThrottler(TimeSpan.FromSeconds(2));

        string? first = throttler.CreateMessage("键盘虚拟键值异常。", nowMilliseconds: 1000);
        string? second = throttler.CreateMessage("键盘虚拟键值异常。", nowMilliseconds: 1200);
        string? third = throttler.CreateMessage("输入坐标超出远程画面范围。", nowMilliseconds: 1500);
        string? fourth = throttler.CreateMessage("输入坐标超出远程画面范围。", nowMilliseconds: 3100);

        Assert.Equal("已忽略无效输入消息：键盘虚拟键值异常。", first);
        Assert.Null(second);
        Assert.Null(third);
        Assert.Equal("已忽略无效输入消息：输入坐标超出远程画面范围。（此前已合并 2 条）", fourth);
    }

    [Fact]
    public void InputErrorLogThrottlerNormalizesBlankErrorMessage()
    {
        var throttler = new RemoteHostServer.InputErrorLogThrottler(TimeSpan.FromSeconds(2));

        string? message = throttler.CreateMessage("  ", nowMilliseconds: 1000);

        Assert.Equal("已忽略无效输入消息：未知输入错误", message);
    }

    [Fact]
    public void RemoteInputStateTrackerReleasesOnlyStillPressedInputsOnDisconnect()
    {
        var tracker = new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ControlKey));
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.ControlKey));
        tracker.Observe(RemoteInputCommand.KeyDown((int)Keys.A));
        tracker.Observe(RemoteInputCommand.KeyUp((int)Keys.A));
        tracker.Observe(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 20));
        tracker.Observe(RemoteInputCommand.MouseDown(RemoteMouseButton.Right, 10, 20));
        tracker.Observe(RemoteInputCommand.MouseUp(RemoteMouseButton.Right, 10, 20));

        var releasedKeys =
            new List<RemoteInputCommand>();
        var releasedButtons = new List<RemoteMouseButton>();
        RemoteHostServer.RemoteInputReleaseResult releaseResult =
            tracker.ReleaseAll(
                releasedKeys.Add,
                releasedButtons.Add);

        Assert.Equal(2, releaseResult.AttemptedCount);
        Assert.Equal(2, releaseResult.ReleasedCount);
        Assert.Empty(releaseResult.Failures);
        Assert.Equal(
            [(int)Keys.ControlKey],
            releasedKeys
                .Select(key => key.Data)
                .ToArray());
        Assert.Equal([RemoteMouseButton.Left], releasedButtons);
        Assert.Equal(0, tracker.PressedKeyCount);
        Assert.Equal(0, tracker.PressedMouseButtonCount);
        RemoteHostServer.RemoteInputReleaseResult secondRelease =
            tracker.ReleaseAll(
                releasedKeys.Add,
                releasedButtons.Add);
        Assert.Equal(0, secondRelease.AttemptedCount);
        Assert.Equal(0, secondRelease.ReleasedCount);
        Assert.Empty(secondRelease.Failures);
    }

    [Fact]
    public void RemoteInputStateTrackerPreservesScanCodeForDisconnectRelease()
    {
        var tracker =
            new RemoteHostServer
                .RemoteInputStateTracker();
        RemoteInputCommand pressed =
            RemoteInputCommand.KeyDown(
                (int)Keys.RControlKey,
                0x1D,
                RemoteKeyboardFlags.HasScanCode |
                RemoteKeyboardFlags.Extended);
        tracker.Observe(pressed);
        var released =
            new List<RemoteInputCommand>();

        RemoteHostServer.RemoteInputReleaseResult result =
            tracker.ReleaseAll(
                released.Add,
                _ => { });

        Assert.Equal(1, result.AttemptedCount);
        Assert.Equal(1, result.ReleasedCount);
        Assert.Empty(result.Failures);
        RemoteInputCommand release =
            Assert.Single(released);
        Assert.Equal(
            RemoteInputKind.KeyDown,
            release.Kind);
        Assert.Equal(pressed.Data, release.Data);
        Assert.Equal(pressed.X, release.X);
        Assert.Equal(pressed.Y, release.Y);
    }

    [Fact]
    public void RemoteInputStateTrackerReleaseIsBestEffortAndIdempotentAfterFailures()
    {
        var tracker =
            new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(
            RemoteInputCommand.KeyDown(
                (int)Keys.ControlKey));
        tracker.Observe(
            RemoteInputCommand.KeyDown(
                (int)Keys.A));
        tracker.Observe(
            RemoteInputCommand.MouseDown(
                RemoteMouseButton.Left,
                10,
                20));
        tracker.Observe(
            RemoteInputCommand.MouseDown(
                RemoteMouseButton.Right,
                10,
                20));
        var attempts = new List<string>();

        RemoteHostServer.RemoteInputReleaseResult result =
            tracker.ReleaseAll(
                key =>
                {
                    attempts.Add($"key:{key.Data}");
                    if (key.Data == (int)Keys.A)
                    {
                        throw new InvalidOperationException(
                            "key failure");
                    }
                },
                button =>
                {
                    attempts.Add($"mouse:{button}");
                    if (button == RemoteMouseButton.Right)
                    {
                        throw new InvalidOperationException(
                            "mouse failure");
                    }
                });

        Assert.Equal(4, result.AttemptedCount);
        Assert.Equal(2, result.ReleasedCount);
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(
            result.Failures,
            failure => failure.Message.Contains(
                "mouse failure",
                StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Message.Contains(
                "key failure",
                StringComparison.Ordinal));
        Assert.Equal(
            [
                "mouse:Right",
                "mouse:Left",
                $"key:{(int)Keys.A}",
                $"key:{(int)Keys.ControlKey}"
            ],
            attempts);
        Assert.Equal(0, tracker.PressedKeyCount);
        Assert.Equal(0, tracker.PressedMouseButtonCount);

        RemoteHostServer.RemoteInputReleaseResult repeated =
            tracker.ReleaseAll(
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException());
        Assert.Equal(0, repeated.AttemptedCount);
        Assert.Equal(0, repeated.ReleasedCount);
        Assert.Empty(repeated.Failures);
    }

    [Fact]
    public void RemoteInputReleaseFailureDoesNotMaskOriginalSessionFailure()
    {
        var tracker =
            new RemoteHostServer.RemoteInputStateTracker();
        tracker.Observe(
            RemoteInputCommand.KeyDown(
                (int)Keys.ControlKey));
        var original =
            new IOException("session failure");

        Action run = () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                tracker.ReleaseAll(
                    _ => throw new InvalidOperationException(
                        "release failure"),
                    _ => { });
            }
        };

        IOException observed =
            Assert.Throws<IOException>(run);

        Assert.Same(original, observed);
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void SessionWritePriorityTracksControlWriteLease()
    {
        using var priority = new RemoteHostServer.SessionWritePriority();

        Assert.False(priority.HasPendingControlWrite);

        using (priority.BeginControlWritePriority())
        {
            Assert.True(priority.HasPendingControlWrite);
        }

        Assert.False(priority.HasPendingControlWrite);
    }

    [Fact]
    public void FileTransferFailurePolicyPreservesActiveTransferForMismatchedId()
    {
        string receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk.RemoteHostServerTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(receiveDirectory);
        try
        {
            using var receiver = new FileTransferReceiver(_ => { }, () => receiveDirectory);
            receiver.Start(RemoteMessageCodec.DecodeControl(
                RemoteMessageCodec.EncodeFileTransferStart("active", "active.bin", fileLength: 0)));

            Assert.Throws<InvalidDataException>(() =>
                receiver.Cancel(RemoteMessageCodec.DecodeControl(
                    RemoteMessageCodec.EncodeFileTransferCancel("stale", "late cancel"))));
            Assert.False(RemoteHostServer.ShouldClearPendingRemoteUpdateAfterTransferFailure(receiver));

            receiver.Cancel(RemoteMessageCodec.DecodeControl(
                RemoteMessageCodec.EncodeFileTransferCancel("active", "cancel active")));
            Assert.True(RemoteHostServer.ShouldClearPendingRemoteUpdateAfterTransferFailure(receiver));
        }
        finally
        {
            try
            {
                Directory.Delete(receiveDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void H264StartupMonitorRefreshesBoundsBeforeCheckingSelection()
    {
        var calls = new List<string>();

        bool shouldCancel =
            RemoteHostServer.ShouldCancelH264Startup(
                () =>
                {
                    calls.Add("refresh");
                    return false;
                },
                () =>
                {
                    calls.Add("selection");
                    return true;
                });

        Assert.False(shouldCancel);
        Assert.Equal(["refresh", "selection"], calls);
    }

    [Fact]
    public void H264StartupMonitorCancelsImmediatelyWhenBoundsChange()
    {
        bool selectionChecked = false;

        bool shouldCancel =
            RemoteHostServer.ShouldCancelH264Startup(
                refreshCaptureBounds: () => true,
                isSelectionCurrent: () =>
                {
                    selectionChecked = true;
                    return true;
                });

        Assert.True(shouldCancel);
        Assert.False(selectionChecked);
    }

    [Fact]
    public void AdaptiveCaptureControllerDropsFpsFirstWhenSeverelyOverloaded()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 60,
            maxQuality: 40,
            scalePercent: 100,
            enabled: true);

        string? message = controller.Update(
            actualFps: 8,
            averageFrameMilliseconds: 180,
            averageSendMilliseconds: 120);

        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(55, controller.CurrentFps);
        Assert.Contains("保文字清晰度", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerHonorsHighQualityJpegFloor()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 10,
            maxQuality: 85,
            scalePercent: 100,
            enabled: true,
            minimumQuality:
                RemoteHostServer.HighQualityJpegMinimumQuality);

        controller.Update(
            actualFps: 2,
            averageFrameMilliseconds: 500,
            averageSendMilliseconds: 300);
        controller.Update(
            actualFps: 2,
            averageFrameMilliseconds: 500,
            averageSendMilliseconds: 300);

        Assert.Equal(
            RemoteHostServer.HighQualityJpegMinimumQuality,
            controller.CurrentQuality);
        Assert.Equal(100, controller.CurrentScalePercent);
    }

    [Fact]
    public void AdaptiveCaptureControllerPreservesScaleWhenNetworkFirstBecomesBound()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 30,
            maxQuality: 80,
            scalePercent: 100,
            enabled: true);

        string? message = controller.Update(
            actualFps: 18,
            averageFrameMilliseconds: 80,
            averageSendMilliseconds: 70);

        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(80, controller.CurrentQuality);
        Assert.Equal(25, controller.CurrentFps);
        Assert.Contains("保文字清晰度", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerDropsFpsBeforeQualityForMildOverload()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 30,
            maxQuality: 80,
            scalePercent: 100,
            enabled: true);

        string? message = controller.Update(
            actualFps: 29,
            averageFrameMilliseconds: 30,
            averageSendMilliseconds: 5);

        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(80, controller.CurrentQuality);
        Assert.Equal(25, controller.CurrentFps);
        Assert.Contains("保文字清晰度", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerRespondsToUdpLossDespiteFastEnqueue()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 30,
            maxQuality: 80,
            scalePercent: 100,
            enabled: true);
        var network = new LowLatencyVideoNetworkSnapshot(
            HasFeedbackSample: true,
            PacketLossRatio: 0.10,
            FrameAbandonRatio: 0.20,
            SenderQueueDropRatio: 0,
            DeliveryMegabitsPerSecond: 20,
            TargetMegabitsPerSecond: 65,
            XorFecEnabled: false,
            OversizedFrameDrops: 0,
            AbortedFrameSends: 0);

        string? message = controller.Update(
            actualFps: 30,
            averageFrameMilliseconds: 10,
            averageSendMilliseconds: 0.1,
            network);

        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(80, controller.CurrentQuality);
        Assert.Equal(25, controller.CurrentFps);
        Assert.Contains("保文字清晰度", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureMetricsReactWithinOneSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), RemoteHostServer.AdaptiveMetricsWindow);
    }

    [Fact]
    public void AdaptiveCaptureControllerStartsAtInitialScaleWithinTargetRange()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 60,
            maxQuality: 75,
            scalePercent: 100,
            enabled: true,
            initialScalePercent: 75);

        Assert.Equal(75, controller.CurrentScalePercent);
        Assert.Equal(60, controller.CurrentFps);
        Assert.Equal(75, controller.CurrentQuality);
    }

    [Fact]
    public void AdaptiveCaptureControllerRecoversInitialLargeTargetScaleFirst()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 60,
            maxQuality: 75,
            scalePercent: 100,
            enabled: true,
            initialScalePercent: 75);

        for (int i = 0; i < 8; i++)
        {
            controller.Update(actualFps: 10, averageFrameMilliseconds: 70, averageSendMilliseconds: 2);
        }

        string? firstComfortableWindow = controller.Update(
            actualFps: 30,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);
        string? message = controller.Update(
            actualFps: 30,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);

        Assert.Null(firstComfortableWindow);
        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(20, controller.CurrentFps);
        Assert.Equal(75, controller.CurrentQuality);
        Assert.Contains("优先恢复分辨率", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6000, 3840, 100, true, 100)]
    [InlineData(3840, 2160, 100, true, 100)]
    [InlineData(1920, 1080, 100, true, 100)]
    [InlineData(6000, 3840, 50, true, 50)]
    [InlineData(6000, 3840, 100, false, 100)]
    public void ChooseInitialAdaptiveScaleHonorsExplicitTarget(
        int width,
        int height,
        int targetScalePercent,
        bool adaptiveQuality,
        int expectedScalePercent)
    {
        int scale = RemoteHostServer.ChooseInitialAdaptiveScale(
            new Rectangle(0, 0, width, height),
            targetScalePercent,
            adaptiveQuality);

        Assert.Equal(expectedScalePercent, scale);
    }

    [Theory]
    [InlineData(3840, 2160, 75, 30, 60, 75)]
    [InlineData(2560, 1440, 100, 30, 60, 100)]
    [InlineData(1920, 1080, 100, 30, 60, 100)]
    [InlineData(6000, 3840, 75, 30, 60, 75)]
    [InlineData(3840, 2160, 50, 30, 60, 50)]
    [InlineData(3840, 2160, 75, 30, 30, 75)]
    public void InteractiveH264ScaleNeverChangesExplicitSpatialScale(
        int width,
        int height,
        int sourceScalePercent,
        int configuredFramesPerSecond,
        int sourceFramesPerSecond,
        int expectedScalePercent)
    {
        int scale =
            RemoteHostServer.ChooseInteractiveH264Scale(
                new Rectangle(0, 0, width, height),
                sourceScalePercent,
                configuredFramesPerSecond,
                sourceFramesPerSecond);

        Assert.Equal(expectedScalePercent, scale);
    }

    [Theory]
    [InlineData(3840, 2160, 30, 60, 30)]
    [InlineData(2560, 1440, 30, 60, 30)]
    [InlineData(1920, 1080, 30, 60, 60)]
    [InlineData(1920, 1080, 30, 90, 90)]
    [InlineData(3840, 2160, 60, 60, 60)]
    public void HighResolutionH264UsesConfiguredFpsInsteadOfImplicitBoost(
        int width,
        int height,
        int configuredFramesPerSecond,
        int proposedSourceFramesPerSecond,
        int expectedFramesPerSecond)
    {
        int actual =
            RemoteHostServer
                .LimitInteractiveH264SourceFramesPerSecond(
                    new Size(width, height),
                    configuredFramesPerSecond,
                    proposedSourceFramesPerSecond);

        Assert.Equal(expectedFramesPerSecond, actual);
    }

    [Fact]
    public void AdaptiveCaptureControllerKeepsAutomaticScaleAtReadableFloor()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 30,
            maxQuality: 55,
            scalePercent: 100,
            enabled: true);

        for (int i = 0; i < 12; i++)
        {
            controller.Update(actualFps: 5, averageFrameMilliseconds: 180, averageSendMilliseconds: 120);
        }

        Assert.Equal(75, controller.CurrentScalePercent);
        Assert.Equal(55, controller.CurrentQuality);
        Assert.Equal(10, controller.CurrentFps);
    }

    [Fact]
    public void AdaptiveCaptureControllerRequiresConsecutiveSevereWindowsBeforeReducingScale()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 10,
            maxQuality: 55,
            scalePercent: 100,
            enabled: true);

        for (int i = 0; i < 2; i++)
        {
            controller.Update(
                actualFps: 10,
                averageFrameMilliseconds: 150,
                averageSendMilliseconds: 2);
        }

        Assert.Equal(100, controller.CurrentScalePercent);

        controller.Update(
            actualFps: 10,
            averageFrameMilliseconds: 90,
            averageSendMilliseconds: 2);
        for (int i = 0; i < 2; i++)
        {
            controller.Update(
                actualFps: 10,
                averageFrameMilliseconds: 150,
                averageSendMilliseconds: 2);
        }

        Assert.Equal(100, controller.CurrentScalePercent);

        string? message = controller.Update(
            actualFps: 10,
            averageFrameMilliseconds: 150,
            averageSendMilliseconds: 2);

        Assert.Equal(75, controller.CurrentScalePercent);
        Assert.Contains(
            "持续严重",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerRecoversScaleBeforeQualityAndFpsWhenComfortable()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 30,
            maxQuality: 80,
            scalePercent: 100,
            enabled: true);

        for (int i = 0; i < 8; i++)
        {
            controller.Update(actualFps: 5, averageFrameMilliseconds: 180, averageSendMilliseconds: 120);
        }

        string? firstComfortableWindow = controller.Update(
            actualFps: 30,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);
        string? message = controller.Update(
            actualFps: 30,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);

        Assert.Null(firstComfortableWindow);
        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(55, controller.CurrentQuality);
        Assert.Equal(10, controller.CurrentFps);
        Assert.Contains("优先恢复分辨率", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerRestoresScaleOnlyWithTargetFpsHeadroom()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 60,
            maxQuality: 55,
            scalePercent: 100,
            enabled: true,
            initialScalePercent: 75);

        for (int i = 0; i < 2; i++)
        {
            controller.Update(
                actualFps: 60,
                averageFrameMilliseconds: 10,
                averageSendMilliseconds: 2);
        }

        Assert.Equal(75, controller.CurrentScalePercent);

        string? firstHeadroomWindow = controller.Update(
            actualFps: 60,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);
        string? message = controller.Update(
            actualFps: 60,
            averageFrameMilliseconds: 6,
            averageSendMilliseconds: 2);

        Assert.Null(firstHeadroomWindow);
        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Contains(
            "优先恢复分辨率",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveCaptureControllerLeavesDisabledScaleUnchanged()
    {
        var controller = new RemoteHostServer.AdaptiveCaptureController(
            targetFps: 60,
            maxQuality: 40,
            scalePercent: 100,
            enabled: false);

        string? message = controller.Update(
            actualFps: 8,
            averageFrameMilliseconds: 180,
            averageSendMilliseconds: 120);

        Assert.Null(message);
        Assert.Equal(100, controller.CurrentScalePercent);
        Assert.Equal(60, controller.CurrentFps);
    }

    [Fact]
    public void CreateCaptureLatencyHintWarnsForLargeAllScreensTarget()
    {
        var target = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕 (6000x3840)",
            new Rectangle(0, 0, 6000, 3840));

        string? message = RemoteHostServer.CreateCaptureLatencyHint(target, scalePercent: 100);

        Assert.NotNull(message);
        Assert.Contains("所有屏幕", message, StringComparison.Ordinal);
        Assert.Contains("6000x3840", message, StringComparison.Ordinal);
        Assert.Contains("单屏", message, StringComparison.Ordinal);
        Assert.Contains("安全像素上限", message, StringComparison.Ordinal);
        Assert.Contains("100% 请求", message, StringComparison.Ordinal);
        Assert.DoesNotContain("50%", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCaptureLatencyHintExplainsExplicitDownscale()
    {
        var target = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏 (3840x2160 @ 0,0)",
            new Rectangle(0, 0, 3840, 2160),
            IsPrimary: true);

        string? message = RemoteHostServer.CreateCaptureLatencyHint(
            target,
            scalePercent: 75);

        Assert.NotNull(message);
        Assert.Contains("当前 75% 是明确选择的缩放", message, StringComparison.Ordinal);
        Assert.Contains("改为 100%", message, StringComparison.Ordinal);
        Assert.DoesNotContain("建议", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCaptureLatencyHintStaysQuietForSmallTarget()
    {
        var target = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏 (1920x1080 @ 0,0)",
            new Rectangle(0, 0, 1920, 1080),
            IsPrimary: true);

        string? message = RemoteHostServer.CreateCaptureLatencyHint(target, scalePercent: 100);

        Assert.Null(message);
    }

    [Fact]
    public void DesktopDuplicationUsesResolvedPhysicalDisplayMapping()
    {
        Rectangle bounds = new(1920, 0, 3840, 2160);
        var screen = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2",
            bounds);
        var allScreens = new ScreenCaptureTarget(
            ScreenCaptureTarget.AllScreensId,
            "所有屏幕",
            bounds);
        var resolved = new WindowsDesktopDuplicationTarget(
            AdapterIndex: 0,
            OutputIndex: 0,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "AMD Radeon",
            DeviceName: screen.Id,
            Bounds: bounds);

        Assert.True(RemoteHostServer.CanUseDesktopDuplication(
            screen,
            resolved));
        Assert.False(RemoteHostServer.CanUseDesktopDuplication(
            allScreens,
            resolved));
        Assert.False(RemoteHostServer.CanUseDesktopDuplication(
            screen,
            null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RotatedOrUnknownOutputSkipsUnrotatedDdaFallback(int rotation)
    {
        Rectangle bounds = new(-2160, 0, 2160, 3840);
        var screen = new ScreenCaptureTarget("\\\\.\\DISPLAY2", "Portrait", bounds);
        var dda = new WindowsDesktopDuplicationTarget(0, 1,
            FfmpegDesktopH264Capture.NvidiaVendorId, "NVIDIA", screen.Id, bounds,
            (Vortice.DXGI.ModeRotation)rotation);
        var wgc = new WindowsGraphicsCaptureTarget(0, 0,
            FfmpegDesktopH264Capture.NvidiaVendorId, "NVIDIA", screen.Id, bounds);
        Assert.False(RemoteHostServer.CanUseDesktopDuplication(screen, dda));
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor, bounds, bounds.Size, 30,
            GraphicsCaptureTarget: wgc, DesktopDuplicationTarget: dda);
        var attempts = RemoteHostServer.CreateHardwareH264StartupOptions(options, [wgc], 30);
        Assert.Equal(new[] { FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor,
            FfmpegDesktopCaptureBackend.GdiGrabBounds }, attempts.Select(a => a.Backend));
        Assert.All(attempts, a => Assert.Equal(bounds.Size, a.OutputSize));
        var ddaOptions = options with { Backend = FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0 };
        Assert.Single(RemoteHostServer.CreateHardwareH264StartupOptions(ddaOptions, [], 30),
            a => a.Backend == FfmpegDesktopCaptureBackend.GdiGrabBounds);
        Assert.Throws<ArgumentException>(() => FfmpegDesktopH264Capture.BuildArguments(
            ddaOptions, FfmpegH264Encoder.MediaFoundation));
    }

    [Fact]
    public void DesktopDuplicationRejectsStaleOrDifferentDisplayMapping()
    {
        Rectangle bounds = new(1920, 0, 1920, 1080);
        var secondary = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY2",
            "屏幕 2",
            bounds);
        var resolved = new WindowsDesktopDuplicationTarget(
            AdapterIndex: 0,
            OutputIndex: 0,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "AMD Radeon",
            DeviceName: secondary.Id,
            Bounds: bounds);

        Assert.False(RemoteHostServer.CanUseDesktopDuplication(
            secondary,
            resolved with
            {
                DeviceName = "\\\\.\\DISPLAY1"
            }));
        Assert.False(RemoteHostServer.CanUseDesktopDuplication(
            secondary,
            resolved with
            {
                Bounds = new Rectangle(0, 0, 1920, 1080)
            }));
        Assert.False(RemoteHostServer.CanUseDesktopDuplication(
            secondary,
            resolved with
            {
                OutputIndex = -1
            }));
    }

    [Fact]
    public void WindowsGraphicsCaptureRequiresResolvedNativePhysicalTarget()
    {
        Rectangle bounds = new(0, 0, 3840, 2160);
        var screen = new ScreenCaptureTarget(
            "\\\\.\\DISPLAY1",
            "屏幕 1 主屏",
            bounds,
            IsPrimary: true);
        var resolved = new WindowsGraphicsCaptureTarget(
            MonitorIndex: 0,
            AdapterIndex: 1,
            AdapterVendorId:
                FfmpegDesktopH264Capture.NvidiaVendorId,
            AdapterDescription: "NVIDIA GeForce RTX 5090",
            DeviceName: screen.Id,
            Bounds: bounds,
            IsMonitorOwningAdapter: false);

        Assert.True(
            RemoteHostServer
                .CanUseNativeWindowsGraphicsCapture(
                    screen,
                    sourceScalePercent: 100,
                    bounds.Size,
                    resolved));
        Assert.True(
            RemoteHostServer
                .CanUseNativeWindowsGraphicsCapture(
                    screen,
                    sourceScalePercent:
                        ScreenCaptureService
                            .QhdMaximumScaleMode,
                    new Size(2560, 1440),
                    resolved));
        Assert.False(
            RemoteHostServer
                .CanUseNativeWindowsGraphicsCapture(
                    screen,
                    sourceScalePercent: 75,
                    new Size(2880, 1620),
                    resolved));
        Assert.False(
            RemoteHostServer
                .CanUseNativeWindowsGraphicsCapture(
                    screen,
                    sourceScalePercent: 100,
                    new Size(1920, 1080),
                    resolved));
        Assert.True(RemoteHostServer.CanUseNativeWindowsGraphicsCapture(
            screen, 100, new Size(1920, 1080), resolved, bandwidthLimited: true));
        Assert.False(RemoteHostServer.CanUseNativeWindowsGraphicsCapture(
            screen, 100, new Size(1280, 720), resolved, bandwidthLimited: true));
        Assert.False(
            RemoteHostServer
                .CanUseNativeWindowsGraphicsCapture(
                    new ScreenCaptureTarget(
                        ScreenCaptureTarget.AllScreensId,
                        "所有屏幕",
                        bounds),
                    sourceScalePercent: 100,
                    bounds.Size,
                    resolved));
    }

    [Fact]
    public void
        HardwareStartupOrdersWgcAdapterCandidatesThenDdaThenGdi()
    {
        Rectangle bounds = new(0, 0, 3840, 2160);
        var nvidia = new WindowsGraphicsCaptureTarget(
            MonitorIndex: 0,
            AdapterIndex: 1,
            AdapterVendorId:
                FfmpegDesktopH264Capture.NvidiaVendorId,
            AdapterDescription: "NVIDIA GeForce RTX 5090",
            DeviceName: "\\\\.\\DISPLAY1",
            Bounds: bounds,
            IsMonitorOwningAdapter: false);
        var amdOwner = new WindowsGraphicsCaptureTarget(
            MonitorIndex: 0,
            AdapterIndex: 0,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "AMD Radeon",
            DeviceName: "\\\\.\\DISPLAY1",
            Bounds: bounds,
            IsMonitorOwningAdapter: true);
        var ddaTarget = new WindowsDesktopDuplicationTarget(
            AdapterIndex: 0,
            OutputIndex: 1,
            AdapterVendorId:
                FfmpegDesktopH264Capture.AmdVendorId,
            AdapterDescription: "AMD Radeon",
            DeviceName: "\\\\.\\DISPLAY1",
            Bounds: bounds);
        var options = new FfmpegDesktopH264CaptureOptions(
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor,
            bounds,
            bounds.Size,
            FramesPerSecond: 90,
            GraphicsCaptureTarget: nvidia,
            DesktopDuplicationTarget: ddaTarget,
            AllowStaticFrameSilence: true);

        IReadOnlyList<FfmpegDesktopH264CaptureOptions>
            attempts =
                RemoteHostServer
                    .CreateHardwareH264StartupOptions(
                        options,
                        [nvidia, amdOwner],
                        compatibilityFallbackFramesPerSecond:
                            30);

        Assert.Equal(
            [
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0,
                FfmpegDesktopCaptureBackend.GdiGrabBounds
            ],
            attempts.Select(attempt => attempt.Backend));
        Assert.Equal(
            [1, 0],
            attempts
                .Take(2)
                .Select(
                    attempt =>
                        attempt.GraphicsCaptureTarget!
                            .AdapterIndex));
        Assert.Null(attempts[2].GraphicsCaptureTarget);
        Assert.Equal(
            ddaTarget,
            attempts[2].DesktopDuplicationTarget);
        Assert.Null(attempts[3].GraphicsCaptureTarget);
        Assert.Null(attempts[3].DesktopDuplicationTarget);
        Assert.True(attempts[0].AllowStaticFrameSilence);
        Assert.True(attempts[1].AllowStaticFrameSilence);
        Assert.False(attempts[2].AllowStaticFrameSilence);
        Assert.False(attempts[3].AllowStaticFrameSilence);
        Assert.Equal(90, attempts[2].FramesPerSecond);
        Assert.Equal(30, attempts[3].FramesPerSecond);

        IReadOnlyList<FfmpegDesktopH264CaptureOptions>
            withoutDda =
                RemoteHostServer
                    .CreateHardwareH264StartupOptions(
                        options with
                        {
                            DesktopDuplicationTarget = null
                        },
                        [nvidia, amdOwner],
                        compatibilityFallbackFramesPerSecond:
                            30);
        Assert.Equal(
            [
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                FfmpegDesktopCaptureBackend.GdiGrabBounds
            ],
            withoutDda.Select(attempt => attempt.Backend));
    }

    [Fact]
    public void WgcRuntimeIsIsolatedFromDdaRecoveryAndCircuitState()
    {
        Assert.False(
            RemoteHostServer.ShouldStartDdaRecoveryProbe(
                recoveryProbeAlreadyActive: false,
                canUseDesktopDuplication: true,
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor));
        Assert.False(
            RemoteHostServer.ShouldStartDdaRecoveryProbe(
                recoveryProbeAlreadyActive: false,
                canUseDesktopDuplication: true,
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0));
        Assert.True(
            RemoteHostServer.ShouldStartDdaRecoveryProbe(
                recoveryProbeAlreadyActive: false,
                canUseDesktopDuplication: true,
                FfmpegDesktopCaptureBackend.GdiGrabBounds));
        Assert.False(
            RemoteHostServer.ShouldStartDdaRecoveryProbe(
                recoveryProbeAlreadyActive: true,
                canUseDesktopDuplication: true,
                FfmpegDesktopCaptureBackend.GdiGrabBounds));

        Assert.False(
            RemoteHostServer.ShouldRecordDdaRuntimeFailure(
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                TimeSpan.FromMilliseconds(100)));
        Assert.False(
            RemoteHostServer.ShouldRecordDdaRuntimeFailure(
                FfmpegDesktopCaptureBackend.GdiGrabBounds,
                TimeSpan.FromMilliseconds(100)));
        Assert.True(
            RemoteHostServer.ShouldRecordDdaRuntimeFailure(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0,
                TimeSpan.FromSeconds(4)));
        Assert.False(
            RemoteHostServer.ShouldRecordDdaRuntimeFailure(
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0,
                TimeSpan.FromSeconds(5)));

        Assert.True(
            RemoteHostServer
                .ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
                    FfmpegDesktopCaptureBackend
                        .WindowsGraphicsCaptureMonitor));
        Assert.False(
            RemoteHostServer
                .ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0));
        Assert.False(
            RemoteHostServer
                .ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
                    FfmpegDesktopCaptureBackend.GdiGrabBounds));
        Assert.False(
            RemoteHostServer
                .ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
                    null));
    }

    [Fact]
    public void
        TimedOutWgcSkipsRemainingWgcCandidates()
    {
        Rectangle bounds = new(0, 0, 3840, 2160);
        var target = new WindowsGraphicsCaptureTarget(
            MonitorIndex: 0,
            AdapterIndex: 1,
            AdapterVendorId:
                FfmpegDesktopH264Capture.NvidiaVendorId,
            AdapterDescription: "NVIDIA GeForce RTX 5090",
            DeviceName: "\\\\.\\DISPLAY1",
            Bounds: bounds,
            IsMonitorOwningAdapter: false);
        var wgcOptions =
            new FfmpegDesktopH264CaptureOptions(
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor,
                bounds,
                bounds.Size,
                FramesPerSecond: 60,
                GraphicsCaptureTarget: target);
        var timedOut =
            new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                "startup deadline expired",
                StartupDeadlineExpired: true);
        var hardFailure =
            new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                "encoder unavailable");

        Assert.True(
            RemoteHostServer
                .ShouldSkipRemainingWindowsGraphicsCaptureCandidates(
                    wgcOptions,
                    timedOut));
        Assert.False(
            RemoteHostServer
                .ShouldSkipRemainingWindowsGraphicsCaptureCandidates(
                    wgcOptions,
                    hardFailure));
        Assert.False(
            RemoteHostServer
                .ShouldSkipRemainingWindowsGraphicsCaptureCandidates(
                    wgcOptions with
                    {
                        Backend =
                            FfmpegDesktopCaptureBackend
                                .DesktopDuplicationOutput0,
                        GraphicsCaptureTarget = null
                    },
                    timedOut));
    }

    [Theory]
    [InlineData(1920, 1080, 100, 1920, 1080)]
    [InlineData(1919, 1079, 100, 1918, 1078)]
    [InlineData(3, 3, 25, 2, 2)]
    [InlineData(6000, 3840, 100, 3374, 2160)]
    [InlineData(5120, 1440, 100, 3840, 1080)]
    [InlineData(2160, 3840, 100, 2160, 3840)]
    [InlineData(3840, 6000, 100, 2160, 3374)]
    public void H264FrameSizeIsEvenForHardwareEncoderSurfaces(
        int width,
        int height,
        int scalePercent,
        int expectedWidth,
        int expectedHeight)
    {
        Size actual = RemoteHostServer.CalculateH264FrameSize(
            new Rectangle(0, 0, width, height),
            scalePercent);

        Assert.Equal(new Size(expectedWidth, expectedHeight), actual);
    }

    [Theory]
    [InlineData(30, 90, 30)]
    [InlineData(30, 60, 30)]
    [InlineData(30, 30, 30)]
    [InlineData(15, 30, 30)]
    [InlineData(60, 120, 30)]
    [InlineData(1, 1, 1)]
    public void CompatibilityFallbackCapsCpuCaptureAtThirtyFps(
        int configuredFramesPerSecond,
        int interactiveSourceFramesPerSecond,
        int expectedFramesPerSecond)
    {
        Assert.Equal(
            expectedFramesPerSecond,
            RemoteHostServer
                .CalculateCompatibilityFallbackFramesPerSecond(
                    configuredFramesPerSecond,
                    interactiveSourceFramesPerSecond));
    }

    [Theory]
    [InlineData(60, 0, 30)]
    [InlineData(
        60,
        (int)RemoteDeviceCapabilities.HighFrameRateH264,
        60)]
    [InlineData(30, 0, 30)]
    [InlineData(15, 0, 15)]
    public void HighFrameRateRequiresExplicitViewerCapability(
        int configuredFramesPerSecond,
        int viewerCapabilities,
        int expectedFramesPerSecond)
    {
        Assert.Equal(
            expectedFramesPerSecond,
            RemoteHostServer
                .ResolveNegotiatedH264FramesPerSecond(
                    configuredFramesPerSecond,
                    (RemoteDeviceCapabilities)
                        viewerCapabilities));
    }

    [Theory]
    [InlineData(60, 0, 60)]
    [InlineData(
        60,
        (int)RemoteDeviceCapabilities.HighQualityJpeg,
        85)]
    [InlineData(
        90,
        (int)RemoteDeviceCapabilities.HighQualityJpeg,
        90)]
    [InlineData(
        5,
        (int)RemoteDeviceCapabilities.HighQualityJpeg,
        85)]
    [InlineData(
        99,
        (int)RemoteDeviceCapabilities.HighQualityJpeg,
        90)]
    public void HighQualityJpegCapabilityRaisesSessionQualityWithoutExceedingCodecLimit(
        int configuredQuality,
        int viewerCapabilities,
        int expectedQuality)
    {
        Assert.Equal(
            expectedQuality,
            RemoteHostServer.ResolveNegotiatedJpegQuality(
                configuredQuality,
                (RemoteDeviceCapabilities)viewerCapabilities));
    }

    [Theory]
    [InlineData(120, 50)]
    [InlineData(60, 50)]
    [InlineData(30, 66.6666666667)]
    [InlineData(15, 133.3333333333)]
    public void H264SourceAgeBudgetUsesTwoFramesWithFiftyMillisecondFloor(
        int framesPerSecond,
        double expectedMilliseconds)
    {
        TimeSpan budget =
            RemoteHostServer.CalculateMaximumH264SourceAge(
                framesPerSecond);

        Assert.Equal(
            expectedMilliseconds,
            budget.TotalMilliseconds,
            precision: 3);
    }

    [Theory]
    [InlineData(49, 50, true, false, false)]
    [InlineData(52.9, 50, true, true, false)]
    [InlineData(53.1, 50, true, true, false)]
    [InlineData(58.1, 50, true, true, false)]
    [InlineData(63.4, 50, true, true, false)]
    [InlineData(100, 50, true, true, false)]
    [InlineData(100.1, 50, true, true, true)]
    [InlineData(500, 50, true, true, true)]
    [InlineData(58, 50, true, false, true)]
    [InlineData(58, 50, false, true, true)]
    [InlineData(150, 133.3333333333, true, true, false)]
    public void InitialRecoveryFrameHasOneBoundedSourceAgeException(
        double sourceAgeMilliseconds,
        double maximumSourceAgeMilliseconds,
        bool recoveryFrame,
        bool awaitingRecoveryHandoff,
        bool expectedDrop)
    {
        Assert.Equal(
            expectedDrop,
            RemoteHostServer.ShouldDropH264SourceFrame(
                TimeSpan.FromMilliseconds(sourceAgeMilliseconds),
                TimeSpan.FromMilliseconds(
                    maximumSourceAgeMilliseconds),
                recoveryFrame,
                awaitingRecoveryHandoff));
    }

    [Theory]
    [InlineData(
        true,
        (int)(RemoteFrameFlags.KeyFrame |
            RemoteFrameFlags.CodecConfig),
        true)]
    [InlineData(
        false,
        (int)(RemoteFrameFlags.KeyFrame |
            RemoteFrameFlags.CodecConfig),
        false)]
    [InlineData(true, (int)RemoteFrameFlags.KeyFrame, true)]
    [InlineData(true, (int)RemoteFrameFlags.None, true)]
    public void AdaptiveH264CanDropEitherGopHalfInsteadOfFallingBackToTcp(
        bool adaptiveQuality,
        int flags,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteHostServer
                .ShouldAllowH264LatencyBudgetDrop(
                    adaptiveQuality,
                    (RemoteFrameFlags)flags));
    }

    [Fact]
    public void InitialRecoveryHandoffCannotBeReportedAsAnAdaptiveUdpDrop()
    {
        RemoteFrameFlags recovery =
            RemoteFrameFlags.KeyFrame |
            RemoteFrameFlags.CodecConfig;

        Assert.False(
            RemoteHostServer.ShouldAllowH264LatencyBudgetDrop(
                adaptiveQuality: true,
                recovery,
                awaitingRecoveryHandoff: true));
        Assert.True(
            RemoteHostServer.ShouldAllowH264LatencyBudgetDrop(
                adaptiveQuality: true,
                recovery,
                awaitingRecoveryHandoff: false));
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]
    public void PendingControlCannotDiscardOnlyRecoveryHandoff(
        bool hasPendingControlWrite,
        bool recoveryFrame,
        bool awaitingRecoveryHandoff,
        bool expectedDefer)
    {
        Assert.Equal(
            expectedDefer,
            RemoteHostServer
                .ShouldDeferH264FrameForPendingControl(
                    hasPendingControlWrite,
                    recoveryFrame,
                    awaitingRecoveryHandoff));
    }

    [Theory]
    [InlineData(30, 30, true)]
    [InlineData(30, 60, true)]
    [InlineData(30, 90, true)]
    [InlineData(30, 91, false)]
    [InlineData(30, 120, false)]
    [InlineData(40, 60, false)]
    [InlineData(40, 90, false)]
    [InlineData(60, 60, true)]
    public void ShortGopRequiresCapabilityAndSafeSourceRatio(
        int configuredFramesPerSecond,
        int sourceFramesPerSecond,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteHostServer.ShouldUseShortGopH264(
                RemoteDeviceCapabilities.ShortGopH264 |
                    RemoteDeviceCapabilities.LowLatencyUdpVideo |
                    RemoteDeviceCapabilities
                        .UdpVideoCongestionFeedback,
                configuredFramesPerSecond,
                sourceFramesPerSecond));
        Assert.False(
            RemoteHostServer.ShouldUseShortGopH264(
                RemoteDeviceCapabilities.LowLatencyUdpVideo |
                    RemoteDeviceCapabilities
                        .UdpVideoCongestionFeedback,
                configuredFramesPerSecond,
                sourceFramesPerSecond));
    }

    [Fact]
    public void HardwareH264ImmediateRetryRequiresTargetOrCodecSelectionChange()
    {
        const RemoteVideoCodecs h264AndJpeg =
            RemoteVideoCodecs.H264AnnexB | RemoteVideoCodecs.Jpeg;

        Assert.True(RemoteHostServer.ShouldAttemptHardwareH264(
            h264AndJpeg,
            targetVersion: 4,
            codecVersion: 7,
            failedTargetVersion: int.MinValue,
            failedCodecVersion: int.MinValue));
        Assert.False(RemoteHostServer.ShouldAttemptHardwareH264(
            h264AndJpeg,
            targetVersion: 4,
            codecVersion: 7,
            failedTargetVersion: 4,
            failedCodecVersion: 7));
        Assert.True(RemoteHostServer.ShouldAttemptHardwareH264(
            h264AndJpeg,
            targetVersion: 5,
            codecVersion: 7,
            failedTargetVersion: 4,
            failedCodecVersion: 7));
        Assert.True(RemoteHostServer.ShouldAttemptHardwareH264(
            h264AndJpeg,
            targetVersion: 4,
            codecVersion: 8,
            failedTargetVersion: 4,
            failedCodecVersion: 7));
        Assert.False(RemoteHostServer.ShouldAttemptHardwareH264(
            RemoteVideoCodecs.Jpeg,
            targetVersion: 5,
            codecVersion: 8,
            failedTargetVersion: 4,
            failedCodecVersion: 7));
    }

    [Fact]
    public void HardwareH264CooldownRetryIsBoundedAndRequiresSameFailedSelection()
    {
        const RemoteVideoCodecs h264AndJpeg =
            RemoteVideoCodecs.H264AnnexB |
            RemoteVideoCodecs.Jpeg;

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            RemoteHostServer
                .CalculateHardwareH264RetryDelay(1));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            RemoteHostServer
                .CalculateHardwareH264RetryDelay(2));
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            RemoteHostServer
                .CalculateHardwareH264RetryDelay(8));
        Assert.False(
            RemoteHostServer.ShouldRetryHardwareH264(
                h264AndJpeg,
                targetVersion: 4,
                codecVersion: 7,
                failedTargetVersion: 4,
                failedCodecVersion: 7,
                nowMilliseconds: 29_999,
                retryAtMilliseconds: 30_000));
        Assert.True(
            RemoteHostServer.ShouldRetryHardwareH264(
                h264AndJpeg,
                targetVersion: 4,
                codecVersion: 7,
                failedTargetVersion: 4,
                failedCodecVersion: 7,
                nowMilliseconds: 30_000,
                retryAtMilliseconds: 30_000));
        Assert.False(
            RemoteHostServer.ShouldRetryHardwareH264(
                RemoteVideoCodecs.Jpeg,
                targetVersion: 4,
                codecVersion: 7,
                failedTargetVersion: 4,
                failedCodecVersion: 7,
                nowMilliseconds: 30_000,
                retryAtMilliseconds: 30_000));
    }

    [Fact]
    public void HardwareH264ProbeAndRuntimeRestartsHaveBoundedLatency()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(18),
            RemoteHostServer.HardwareH264ProbeDeadline);
        Assert.Equal(
            TimeSpan.FromMilliseconds(16_500),
            RemoteHostServer
                .HardwareH264ProbeExecutionDeadline);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            DesktopDuplicationCircuitBreaker
                .InitialBreakDuration);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            DesktopDuplicationCircuitBreaker
                .MaximumBreakDuration);
        Assert.Equal(
            2,
            RemoteHostServer.MaximumH264RuntimeRestarts);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            RemoteHostServer
                .H264RuntimeRestartResetDuration);
        Assert.False(
            RemoteHostServer
                .ShouldResetH264RuntimeRestartCount(
                    TimeSpan.FromSeconds(29.999)));
        Assert.True(
            RemoteHostServer
                .ShouldResetH264RuntimeRestartCount(
                    TimeSpan.FromSeconds(30)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            RemoteHostServer.CalculateH264RuntimeRestartDelay(1));
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            RemoteHostServer.CalculateH264RuntimeRestartDelay(2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RemoteHostServer
                .CalculateH264RuntimeRestartDelay(0));
    }

    [Fact]
    public void DesktopDuplicationCircuitUsesBoundedHalfOpenBackoff()
    {
        const long frequency = 1000;
        long now = 10_000;
        var circuit =
            new DesktopDuplicationCircuitBreaker(
                () => now,
                frequency);

        Assert.True(circuit.TryBeginProbe(
            out TimeSpan retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
        Assert.False(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            circuit.RecordFailure());
        Assert.Equal(1, circuit.ConsecutiveFailures);
        Assert.False(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            retryAfter);

        now += 30 * frequency;
        Assert.True(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            circuit.RecordFailure());
        Assert.Equal(2, circuit.ConsecutiveFailures);

        now += 60 * frequency;
        Assert.True(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            circuit.RecordFailure());
        Assert.Equal(3, circuit.ConsecutiveFailures);

        now += 120 * frequency;
        Assert.True(circuit.TryBeginProbe(
            out retryAfter));
        circuit.CancelProbe();
        Assert.Equal(3, circuit.ConsecutiveFailures);
        Assert.True(circuit.TryBeginProbe(
            out retryAfter));
        circuit.RecordSuccess();
        Assert.Equal(0, circuit.ConsecutiveFailures);

        Assert.True(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            circuit.RecordFailure());
    }

    [Fact]
    public void DesktopDuplicationRuntimeFailureTripsCircuitAfterSuccess()
    {
        const long frequency = 1000;
        long now = 50_000;
        var circuit =
            new DesktopDuplicationCircuitBreaker(
                () => now,
                frequency);

        Assert.True(circuit.TryBeginProbe(out _));
        circuit.RecordStartupSuccess();
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            circuit.RecordRuntimeFailure());
        Assert.Equal(1, circuit.ConsecutiveFailures);
        Assert.False(circuit.TryBeginProbe(
            out TimeSpan retryAfter));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            retryAfter);

        now += 30 * frequency;
        Assert.True(circuit.TryBeginProbe(out _));
        circuit.RecordStartupSuccess();
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            circuit.RecordRuntimeFailure());
        Assert.Equal(2, circuit.ConsecutiveFailures);
        Assert.False(circuit.TryBeginProbe(
            out retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);

        now += 60 * frequency;
        Assert.True(circuit.TryBeginProbe(out _));
        circuit.RecordStartupSuccess();
        Assert.True(
            circuit.RecordStableRuntimeSuccess());
        Assert.Equal(0, circuit.ConsecutiveFailures);
    }

    [Fact]
    public async Task DdaRecoveryProbeRetainsRecoveryBeforeOwnershipTransfer()
    {
        long now = 10_000;
        var circuit = new DesktopDuplicationCircuitBreaker(
            () => now,
            timestampFrequency: 1000);
        var candidate = new FakeDdaProbeCapture();
        var recoveryFrame = new FakeDdaProbeFrame(
            IsIndependent: true);
        var events = new List<string>();
        Assert.True(circuit.TryBeginProbe(out _));

        RemoteHostServer.DdaRecoveryProbeAttempt<
            FakeDdaProbeCapture,
            FakeDdaProbeFrame> attempt =
                await RemoteHostServer
                    .CompleteDdaRecoveryProbeAsync<
                        FakeDdaProbeCapture,
                        FakeDdaProbeFrame>(
                        circuit,
                        _ => Task.FromResult(
                            new RemoteHostServer
                                .DdaRecoveryProbeStart<
                                    FakeDdaProbeCapture>(
                                    candidate,
                                    FailureDetail:
                                        string.Empty)),
                        (capture, _) =>
                        {
                            Assert.Same(
                                candidate,
                                capture);
                            Assert.False(capture.Disposed);
                            events.Add("recovery-retained");
                            return ValueTask.FromResult<
                                FakeDdaProbeFrame?>(
                                recoveryFrame);
                        },
                        frame => frame.IsIndependent,
                        frame => frame.Dispose(),
                        capture =>
                        {
                            events.Add("disposed");
                            capture.Dispose();
                        },
                        CancellationToken.None,
                        CancellationToken.None);

        Assert.True(attempt.Recovered);
        Assert.Same(candidate, attempt.Capture);
        Assert.Same(
            recoveryFrame,
            attempt.RecoveryFrame);
        Assert.Equal(["recovery-retained"], events);
        Assert.False(candidate.Disposed);
        Assert.False(recoveryFrame.Disposed);
        Assert.True(circuit.TryBeginProbe(out _));
        circuit.CancelProbe();

        // Ownership transfers to the active-capture loop only after the
        // recovery frame has been retained.
        attempt.RecoveryFrame!.Dispose();
        attempt.Capture!.Dispose();
        Assert.True(recoveryFrame.Disposed);
        Assert.True(candidate.Disposed);
    }

    [Fact]
    public async Task DdaRecoveryProbeRejectsUnsafeFirstFrameAndBacksOff()
    {
        long now = 20_000;
        var circuit = new DesktopDuplicationCircuitBreaker(
            () => now,
            timestampFrequency: 1000);
        var candidate = new FakeDdaProbeCapture();
        var unsafeFrame = new FakeDdaProbeFrame(
            IsIndependent: false);
        Assert.True(circuit.TryBeginProbe(out _));

        RemoteHostServer.DdaRecoveryProbeAttempt<
            FakeDdaProbeCapture,
            FakeDdaProbeFrame> attempt =
                await RemoteHostServer
                    .CompleteDdaRecoveryProbeAsync<
                        FakeDdaProbeCapture,
                        FakeDdaProbeFrame>(
                        circuit,
                        _ => Task.FromResult(
                            new RemoteHostServer
                                .DdaRecoveryProbeStart<
                                    FakeDdaProbeCapture>(
                                    candidate,
                                    FailureDetail:
                                        string.Empty)),
                        (_, _) => ValueTask.FromResult<
                            FakeDdaProbeFrame?>(
                            unsafeFrame),
                        frame => frame.IsIndependent,
                        frame => frame.Dispose(),
                        capture => capture.Dispose(),
                        CancellationToken.None,
                        CancellationToken.None);

        Assert.False(attempt.Recovered);
        Assert.Null(attempt.Capture);
        Assert.True(candidate.Disposed);
        Assert.True(unsafeFrame.Disposed);
        Assert.Contains(
            "不是可独立解码",
            attempt.FailureDetail);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            attempt.RetryAfter);
        Assert.Equal(1, circuit.ConsecutiveFailures);
        Assert.False(circuit.TryBeginProbe(
            out TimeSpan retryAfter));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            retryAfter);
    }

    [Fact]
    public async Task DdaRecoveryProbeTimeoutDisposesCandidateAndAdvancesBackoff()
    {
        long now = 30_000;
        var circuit = new DesktopDuplicationCircuitBreaker(
            () => now,
            timestampFrequency: 1000);
        var candidate = new FakeDdaProbeCapture();
        using var probeCancellation =
            new CancellationTokenSource();
        var readStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        Assert.True(circuit.TryBeginProbe(out _));

        Task<RemoteHostServer.DdaRecoveryProbeAttempt<
            FakeDdaProbeCapture,
            FakeDdaProbeFrame>> probe =
                RemoteHostServer
                    .CompleteDdaRecoveryProbeAsync<
                        FakeDdaProbeCapture,
                        FakeDdaProbeFrame>(
                        circuit,
                        _ => Task.FromResult(
                            new RemoteHostServer
                                .DdaRecoveryProbeStart<
                                    FakeDdaProbeCapture>(
                                    candidate,
                                    FailureDetail:
                                        string.Empty)),
                        async (_, token) =>
                        {
                            readStarted.TrySetResult();
                            await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                token);
                            return null;
                        },
                        frame => frame.IsIndependent,
                        frame => frame.Dispose(),
                        capture => capture.Dispose(),
                        probeCancellation.Token,
                        CancellationToken.None);

        await readStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        probeCancellation.Cancel();
        RemoteHostServer.DdaRecoveryProbeAttempt<
            FakeDdaProbeCapture,
            FakeDdaProbeFrame> attempt =
                await probe.WaitAsync(
                    TimeSpan.FromSeconds(1));

        Assert.False(attempt.Recovered);
        Assert.True(candidate.Disposed);
        Assert.Contains(
            "执行时限",
            attempt.FailureDetail);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            attempt.RetryAfter);
        Assert.Equal(1, circuit.ConsecutiveFailures);
    }

    [Fact]
    public async Task DdaRecoveryProbeSessionCancellationCleansWithoutFailure()
    {
        long now = 40_000;
        var circuit = new DesktopDuplicationCircuitBreaker(
            () => now,
            timestampFrequency: 1000);
        var candidate = new FakeDdaProbeCapture();
        using var sessionCancellation =
            new CancellationTokenSource();
        using var probeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                sessionCancellation.Token);
        var readStarted =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        Assert.True(circuit.TryBeginProbe(out _));

        Task<RemoteHostServer.DdaRecoveryProbeAttempt<
            FakeDdaProbeCapture,
            FakeDdaProbeFrame>> probe =
                RemoteHostServer
                    .CompleteDdaRecoveryProbeAsync<
                        FakeDdaProbeCapture,
                        FakeDdaProbeFrame>(
                        circuit,
                        _ => Task.FromResult(
                            new RemoteHostServer
                                .DdaRecoveryProbeStart<
                                    FakeDdaProbeCapture>(
                                    candidate,
                                    FailureDetail:
                                        string.Empty)),
                        async (_, token) =>
                        {
                            readStarted.TrySetResult();
                            await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                token);
                            return null;
                        },
                        frame => frame.IsIndependent,
                        frame => frame.Dispose(),
                        capture => capture.Dispose(),
                        probeCancellation.Token,
                        sessionCancellation.Token);

        await readStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        sessionCancellation.Cancel();
        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => probe);

        Assert.True(candidate.Disposed);
        Assert.Equal(0, circuit.ConsecutiveFailures);
        Assert.True(circuit.TryBeginProbe(out _));
        circuit.CancelProbe();
    }

    [Fact]
    public async Task RetiringSupersededCaptureDoesNotBlockRecoveryHandoff()
    {
        var capture = new FakeDdaProbeCapture();
        var cleanupEntered =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        var allowCleanup =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        Task cleanup =
            RemoteHostServer.RetireH264CaptureAsync(
                capture,
                retired =>
                {
                    cleanupEntered.TrySetResult();
                    allowCleanup.Task
                        .GetAwaiter()
                        .GetResult();
                    retired.Dispose();
                });

        try
        {
            await cleanupEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(1));
            Assert.False(cleanup.IsCompleted);
            Assert.False(capture.Disposed);
        }
        finally
        {
            allowCleanup.TrySetResult();
        }

        await cleanup.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(capture.Disposed);
    }

    [Theory]
    [InlineData(true, "文件已保存到本机：C:\\Users\\me\\Downloads\\RemoteDeskReceived\\a.txt", "查看端文件状态：文件已保存到本机")]
    [InlineData(false, "接收远端文件失败：磁盘空间不足", "查看端文件状态异常：接收远端文件失败")]
    public void FormatPeerFileTransferStatusKeepsViewerReturnReceiptVisible(
        bool success,
        string statusMessage,
        string expectedPrefix)
    {
        var control = new RemoteControlMessage(
            RemoteControlKind.FileTransferStatus,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            Success: success,
            StatusMessage: statusMessage);

        string message = RemoteHostServer.FormatPeerFileTransferStatus(control);

        Assert.StartsWith(expectedPrefix, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeerFileTransferFailureCancelsOnlyActiveReturnForSameViewer()
    {
        var currentViewer = new RemoteHostServer.ViewerSessionState();
        var otherViewer = new RemoteHostServer.ViewerSessionState();
        var currentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(currentViewer.TryStartClipboardFileReturn(
            cancellationToken => WaitForCancellationAsync(cancellationToken, currentStarted, currentCancelled),
            CancellationToken.None));
        Assert.True(otherViewer.TryStartClipboardFileReturn(
            cancellationToken => WaitForCancellationAsync(cancellationToken, otherStarted, otherCancelled),
            CancellationToken.None));
        await Task.WhenAll(currentStarted.Task, otherStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var success = new RemoteControlMessage(
            RemoteControlKind.FileTransferStatus,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            Success: true,
            StatusMessage: "普通进度状态");
        var failure = success with
        {
            Success = false,
            StatusMessage = "接收远端文件失败：无法绑定回传清单"
        };
        var logs = new List<string>();

        Assert.False(RemoteHostServer.HandlePeerFileTransferStatus(success, currentViewer, logs.Add));
        Assert.False(currentCancelled.Task.IsCompleted);
        Assert.False(otherCancelled.Task.IsCompleted);

        Assert.True(RemoteHostServer.HandlePeerFileTransferStatus(failure, currentViewer, logs.Add));
        await currentCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await currentViewer.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(currentViewer.HasActiveClipboardFileReturn);
        Assert.False(otherCancelled.Task.IsCompleted);
        Assert.False(RemoteHostServer.HandlePeerFileTransferStatus(failure, currentViewer, logs.Add));
        Assert.Equal(3, logs.Count);

        Assert.True(otherViewer.CancelActiveClipboardFileReturn());
        await otherCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await otherViewer.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeDdaProbeCapture : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeDdaProbeFrame : IDisposable
    {
        public FakeDdaProbeFrame(bool IsIndependent)
        {
            this.IsIndependent = IsIndependent;
        }

        public bool IsIndependent { get; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource started,
        TaskCompletionSource cancelled)
    {
        started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled.TrySetResult();
        }
    }
}
