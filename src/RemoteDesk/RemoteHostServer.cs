using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemoteDesk;

internal sealed class RemoteHostServer : IDisposable
{
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AuthenticationFailureDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SessionRejectionWriteTimeout =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SessionReplacementDrainTimeout =
        TimeSpan.FromSeconds(2);
    private const string SessionReplacedMessage =
        "此连接已被另一台查看端接管；已停止自动重连。";
    private static readonly TimeSpan DisposeStopTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MetricsWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InputErrorLogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ControlWritePriorityDelay = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan CaptureTargetUnavailablePollInterval =
        TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan HardwareH264ProbeDeadline =
        TimeSpan.FromSeconds(18);
    internal static readonly TimeSpan
        HardwareH264ProbeExecutionDeadline =
            TimeSpan.FromMilliseconds(16_500);
    internal static readonly TimeSpan InitialViewerInfoGracePeriod =
        TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan InitialViewerInfoPreviewGracePeriod =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan
        InitialViewerCapabilitiesGracePeriod =
            TimeSpan.FromMilliseconds(10);
    internal const int MaximumH264RuntimeRestarts = 2;
    private static readonly TimeSpan StableH264RunDuration =
        TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan
        H264RuntimeRestartResetDuration =
            TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan
        InitialHardwareH264RetryDelay =
            TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan
        MaximumHardwareH264RetryDelay =
            TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan
        StaticH264CaptureTargetPollInterval =
            TimeSpan.FromMilliseconds(500);
    internal const int
        StaticH264CaptureTargetRefreshFailureLimit = 3;
    private const int InputReceiveBufferBytes = 32 * 1024;
    internal const int MaxConcurrentClientHandlers = 4;
    internal const int FrameSendBufferBytes = 128 * 1024;
    private const int MaxClipboardFileReturnCount = 32;
    private const long ClipboardInputSequenceMarkerMaxAgeMs = 2_000;
    private const long LargeCaptureWarningPixels = 8_000_000;
    private const long InteractiveH264BoostMaximumPixels =
        1920L * 1080L;
    internal const int HighQualityJpegTargetQuality = 85;
    internal const int HighQualityJpegMinimumQuality = 70;

    internal static TimeSpan AdaptiveMetricsWindow => MetricsWindow;

    private TcpListener? _listener;
    private int _listeningPort;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _acceptLoopTask;
    private readonly object _clientStateLock = new();
    private readonly HashSet<Task> _clientTasks = new();
    private readonly HashSet<TcpClient> _activeClients = new();
    private readonly DesktopDuplicationCircuitBreaker
        _desktopDuplicationCircuitBreaker = new();

    public event Action<string>? Log;
    public event Action<bool>? RunningChanged;
    public event Action<string>? ClientStatusChanged;

    public bool IsRunning => _listener is not null;

    public int ListeningPort =>
        Volatile.Read(ref _listeningPort);

    public Task StartAsync(
        int port,
        string password,
        int fps,
        int jpegQuality,
        int scalePercent,
        ScreenCaptureTarget captureTarget,
        bool adaptiveQuality)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("本机设备密钥不能为空。");
        }

        try
        {
            int clampedFps = Math.Clamp(fps, 1, 60);
            int clampedJpegQuality = Math.Clamp(jpegQuality, 30, 90);
            int clampedScalePercent = Math.Clamp(scalePercent, 25, 100);
            _cancellationTokenSource = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Volatile.Write(ref _listeningPort, port);
            _acceptLoopTask = AcceptLoopAsync(
                password,
                clampedFps,
                clampedJpegQuality,
                clampedScalePercent,
                captureTarget,
                adaptiveQuality,
                _cancellationTokenSource.Token);
        }
        catch
        {
            _listener?.Stop();
            _listener = null;
            Volatile.Write(ref _listeningPort, 0);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _acceptLoopTask = null;
            throw;
        }

        int loggedScalePercent = Math.Clamp(scalePercent, 25, 100);
        Log?.Invoke(
            $"被控端已启动，监听 0.0.0.0:{port}，" +
            $"捕获：{captureTarget.DisplayName}，" +
            $"分辨率：{ScreenCaptureService.FormatScaleMode(loggedScalePercent)}，" +
            $"自适应：{(adaptiveQuality ? "开启" : "关闭")}");
        string? captureLatencyHint = CreateCaptureLatencyHint(captureTarget, loggedScalePercent, adaptiveQuality);
        if (captureLatencyHint is not null)
        {
            Log?.Invoke(captureLatencyHint);
        }

        ClientStatusChanged?.Invoke(
            $"正在监听 0.0.0.0:{port}");
        RunningChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    internal static string? CreateCaptureLatencyHint(ScreenCaptureTarget captureTarget, int scalePercent, bool adaptiveQuality = false)
    {
        long capturePixels = Math.Max(0, (long)captureTarget.Bounds.Width) * Math.Max(0, (long)captureTarget.Bounds.Height);
        if (capturePixels < LargeCaptureWarningPixels)
        {
            return null;
        }

        Size frameSize = ScreenCaptureService.CalculateFrameSize(captureTarget.Bounds, scalePercent);
        string targetKind = captureTarget.IsAllScreens ? "所有屏幕" : "高分辨率屏幕";
        string clarityGuidance;
        if (scalePercent >= 100)
        {
            bool safetyScaled =
                frameSize.Width != captureTarget.Bounds.Width ||
                frameSize.Height != captureTarget.Bounds.Height;
            if (safetyScaled)
            {
                clarityGuidance = captureTarget.IsAllScreens
                    ? "100% 请求已按安全像素上限等比缩放；如需单屏原生细节，请切到对应屏幕"
                    : "100% 请求已按安全像素上限等比缩放";
            }
            else
            {
                clarityGuidance = captureTarget.IsAllScreens
                    ? "100% 会保持原生分辨率；若实测持续掉帧，优先切到单屏，确认硬编或带宽仍不足时再手动选择 75%"
                    : "100% 会保持原生分辨率；仅在实测硬编或带宽持续不足时再手动选择 75%";
            }
        }
        else if (scalePercent ==
                 ScreenCaptureService.QhdMaximumScaleMode)
        {
            clarityGuidance =
                "最高 1440p 会在 4K/更高分辨率屏幕上精确限制到 " +
                "1440p，并在较低分辨率屏幕上保持原生；" +
                "如需完整 4K 细节，请改为 100%";
        }
        else
        {
            clarityGuidance =
                $"当前 {scalePercent}% 是明确选择的缩放；如需最清晰画面，请改为 100%";
        }

        if (adaptiveQuality)
            clarityGuidance = "所选传输尺寸是清晰度上限；H.264 / TCP 持续受带宽限制时自动切到最高 1080p，稳定后尝试恢复一次，失败则本次连接保持流畅档。关闭自适应可固定所选尺寸，系统分辨率不变";
        return $"低延迟提示：当前选择{targetKind}，采集区域 {captureTarget.Bounds.Width}x{captureTarget.Bounds.Height}，发送约 {frameSize.Width}x{frameSize.Height}。{clarityGuidance}。";
    }

    internal static bool CanUseDesktopDuplication(
        ScreenCaptureTarget target,
        WindowsDesktopDuplicationTarget? resolvedTarget)
    {
        ArgumentNullException.ThrowIfNull(target);
        return !target.IsAllScreens &&
            resolvedTarget is not null &&
            resolvedTarget.AdapterIndex >= 0 &&
            resolvedTarget.OutputIndex >= 0 &&
            string.Equals(
                target.Id,
                resolvedTarget.DeviceName,
                StringComparison.OrdinalIgnoreCase) &&
            target.Bounds == resolvedTarget.Bounds;
    }

    internal static bool CanUseNativeWindowsGraphicsCapture(
        ScreenCaptureTarget target,
        int sourceScalePercent,
        Size outputSize,
        WindowsGraphicsCaptureTarget? resolvedTarget,
        bool bandwidthLimited = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        return !target.IsAllScreens &&
            IsWindowsGraphicsCaptureScaleMode(
                sourceScalePercent) &&
            resolvedTarget is not null &&
            string.Equals(
            target.Id,
                resolvedTarget.DeviceName,
                StringComparison.OrdinalIgnoreCase) &&
            target.Bounds == resolvedTarget.Bounds &&
            outputSize == (bandwidthLimited
                ? AdaptiveH264ResolutionController.FitFullHd(CalculateH264FrameSize(target.Bounds, sourceScalePercent))
                : CalculateH264FrameSize(
                target.Bounds,
                sourceScalePercent));
    }

    internal static bool IsWindowsGraphicsCaptureScaleMode(
        int scalePercent) =>
        scalePercent == 100 ||
        scalePercent ==
            ScreenCaptureService.QhdMaximumScaleMode;

    internal static bool ShouldAllowStaticWgcFrameSilence(
        FfmpegDesktopCaptureBackend backend,
        RemoteDeviceCapabilities viewerCapabilities) =>
        backend ==
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor &&
        LowLatencyVideoFeatureNegotiation
            .FromCapabilities(viewerCapabilities)
            .HasFlag(
                LowLatencyVideoFeatures
                    .AuthenticatedHeartbeat);

    internal static IReadOnlyList<FfmpegDesktopH264CaptureOptions>
        CreateHardwareH264StartupOptions(
            FfmpegDesktopH264CaptureOptions options,
            IReadOnlyList<WindowsGraphicsCaptureTarget>
                graphicsCaptureTargets,
            int compatibilityFallbackFramesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(graphicsCaptureTargets);
        WindowsDesktopDuplicationTarget?
            desktopDuplicationTarget =
                options.DesktopDuplicationTarget;
        var attempts =
            new List<FfmpegDesktopH264CaptureOptions>();

        switch (options.Backend)
        {
            case FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor:
                IReadOnlyList<WindowsGraphicsCaptureTarget>
                    candidates = graphicsCaptureTargets.Count > 0
                        ? graphicsCaptureTargets
                        : options.GraphicsCaptureTarget is { } target
                            ? [target]
                            : [];
                foreach (WindowsGraphicsCaptureTarget candidate in
                         candidates)
                {
                    attempts.Add(
                        options with
                        {
                            GraphicsCaptureTarget = candidate,
                            DesktopDuplicationTarget =
                                desktopDuplicationTarget
                        });
                }

                if (desktopDuplicationTarget is not null)
                {
                    attempts.Add(
                        options with
                        {
                            Backend =
                                FfmpegDesktopCaptureBackend
                                    .DesktopDuplicationOutput0,
                            GraphicsCaptureTarget = null,
                            DesktopDuplicationTarget =
                                desktopDuplicationTarget,
                            AllowStaticFrameSilence = false
                        });
                }

                break;

            case FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0:
                if (desktopDuplicationTarget is not null)
                {
                    attempts.Add(
                        options with
                        {
                            GraphicsCaptureTarget = null,
                            DesktopDuplicationTarget =
                                desktopDuplicationTarget,
                            AllowStaticFrameSilence = false
                        });
                }
                break;

            case FfmpegDesktopCaptureBackend.GdiGrabBounds:
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options));
        }

        attempts.Add(
            options with
            {
                Backend =
                    FfmpegDesktopCaptureBackend.GdiGrabBounds,
                GraphicsCaptureTarget = null,
                DesktopDuplicationTarget = null,
                AllowStaticFrameSilence = false,
                FramesPerSecond =
                    CalculateCompatibilityFallbackFramesPerSecond(
                        compatibilityFallbackFramesPerSecond,
                        options.FramesPerSecond),
                BitrateFramesPerSecond =
                    options.BitrateFramesPerSecond is int bitrateFps
                        ? Math.Min(
                            bitrateFps,
                            CalculateCompatibilityFallbackFramesPerSecond(
                                compatibilityFallbackFramesPerSecond,
                                options.FramesPerSecond))
                        : null
            });
        return attempts;
    }

    internal static bool ShouldStartDdaRecoveryProbe(
        bool recoveryProbeAlreadyActive,
        bool canUseDesktopDuplication,
        FfmpegDesktopCaptureBackend activeBackend)
    {
        return !recoveryProbeAlreadyActive &&
            canUseDesktopDuplication &&
            activeBackend ==
                FfmpegDesktopCaptureBackend.GdiGrabBounds;
    }

    internal static bool ShouldRecordDdaRuntimeFailure(
        FfmpegDesktopCaptureBackend backend,
        TimeSpan activeDuration)
    {
        return backend ==
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0 &&
            activeDuration < StableH264RunDuration;
    }

    internal static bool
        ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
            FfmpegDesktopCaptureBackend? backend)
    {
        return backend ==
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor;
    }

    internal static bool
        ShouldSkipRemainingWindowsGraphicsCaptureCandidates(
            FfmpegDesktopH264CaptureOptions options,
            FfmpegDesktopH264CaptureStartResult start)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(start);
        return options.Backend ==
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor &&
            !start.Started &&
            start.StartupDeadlineExpired;
    }

    internal static bool ShouldResetH264RuntimeRestartCount(
        TimeSpan activeDuration)
    {
        return activeDuration >=
            H264RuntimeRestartResetDuration;
    }

    internal static Size CalculateH264FrameSize(
        Rectangle captureBounds,
        int scalePercent)
    {
        Size requested = ScreenCaptureService.CalculateFrameSize(
            captureBounds,
            scalePercent);
        bool portrait =
            requested.Height > requested.Width;
        return FitEvenSizeWithin(
            requested,
            portrait
                ? new Size(
                    ScreenCaptureService
                        .LowLatencyMaximumHeight,
                    ScreenCaptureService
                        .LowLatencyMaximumWidth)
                : new Size(
                    ScreenCaptureService
                        .LowLatencyMaximumWidth,
                    ScreenCaptureService
                        .LowLatencyMaximumHeight));
    }

    internal static Size FitEvenSizeWithin(
        Size requested,
        Size maximum)
    {
        if (requested.Width <= 0 ||
            requested.Height <= 0)
        {
            return new Size(2, 2);
        }

        if (maximum.Width < 2 ||
            maximum.Height < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximum));
        }

        double scale = Math.Min(
            1d,
            Math.Min(
                maximum.Width /
                    (double)requested.Width,
                maximum.Height /
                    (double)requested.Height));
        int width = Math.Max(
            2,
            (int)Math.Floor(
                requested.Width * scale) & ~1);
        int height = Math.Max(
            2,
            (int)Math.Floor(
                requested.Height * scale) & ~1);
        return new Size(width, height);
    }

    internal static bool ShouldReuseCaptureTarget(
        ScreenCaptureTarget current,
        Rectangle currentBounds,
        ScreenCaptureTarget requested)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(requested);
        return string.Equals(
                current.Id,
                requested.Id,
                StringComparison.OrdinalIgnoreCase) &&
            currentBounds == requested.Bounds;
    }

    internal static int
        CalculateCompatibilityFallbackFramesPerSecond(
            int configuredFramesPerSecond,
            int interactiveSourceFramesPerSecond)
    {
        int configured = Math.Clamp(
            configuredFramesPerSecond,
            1,
            60);
        int interactiveSource = Math.Clamp(
            interactiveSourceFramesPerSecond,
            1,
            120);
        return Math.Min(
            30,
            Math.Max(configured, interactiveSource));
    }

    internal static int ResolveNegotiatedH264FramesPerSecond(
        int configuredFramesPerSecond,
        RemoteDeviceCapabilities viewerCapabilities)
    {
        int configured = Math.Clamp(
            configuredFramesPerSecond,
            1,
            60);
        return configured <= 30 ||
            viewerCapabilities.HasFlag(
                RemoteDeviceCapabilities.HighFrameRateH264)
                ? configured
                : 30;
    }

    internal static int ResolveNegotiatedJpegQuality(
        int configuredQuality,
        RemoteDeviceCapabilities viewerCapabilities)
    {
        int configured = Math.Clamp(configuredQuality, 30, 90);
        return viewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.HighQualityJpeg)
                ? Math.Max(HighQualityJpegTargetQuality, configured)
                : configured;
    }

    internal static TimeSpan CalculateMaximumH264SourceAge(
        int framesPerSecond)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond));
        }

        return TimeSpan.FromMilliseconds(
            Math.Max(
                50d,
                2_000d / framesPerSecond));
    }

    internal static bool ShouldDropH264SourceFrame(
        TimeSpan sourceAge,
        TimeSpan maximumSourceAge,
        bool recoveryFrame,
        bool awaitingRecoveryHandoff)
    {
        if (sourceAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceAge));
        }

        if (maximumSourceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSourceAge));
        }

        if (sourceAge <= maximumSourceAge)
        {
            return false;
        }

        // WGC is change-driven on a static desktop. Its only startup IDR can
        // occasionally spend just over the ordinary 50 ms latest-frame
        // budget in the local RTP/parser pipeline. Dropping that frame leaves
        // the viewer unable to display anything or generate the first visual
        // change, and the healthy capture is then misdiagnosed as stalled.
        // Permit only the first independently decodable frame, and keep the
        // exception bounded so an actually old mailbox item cannot add an
        // unbounded startup delay. Once one frame has been sent, the normal
        // latest-only latency budget applies to every GOP.
        long doubledBudgetTicks = maximumSourceAge.Ticks >
                TimeSpan.MaxValue.Ticks / 2
            ? TimeSpan.MaxValue.Ticks
            : maximumSourceAge.Ticks * 2;
        TimeSpan initialRecoveryBudget = TimeSpan.FromTicks(
            Math.Max(
                maximumSourceAge.Ticks,
                Math.Min(
                    TimeSpan.FromMilliseconds(250).Ticks,
                    doubledBudgetTicks)));
        return !awaitingRecoveryHandoff ||
            !recoveryFrame ||
            sourceAge > initialRecoveryBudget;
    }

    internal static bool ShouldAllowH264LatencyBudgetDrop(
        bool adaptiveQuality,
        RemoteFrameFlags flags,
        bool awaitingRecoveryHandoff = false)
    {
        _ = flags;
        // A GOP=2 dependent frame is useful only with its immediately
        // preceding recovery frame. Under congestion it is safer to drop
        // either half and recover at the next IDR than to tear down UDP and
        // splice an orphan P frame onto TCP.
        // The first independently decodable frame is also the handoff from
        // the startup JPEG stream. A congestion-budget drop is reported as a
        // successful queue operation by the transport, so allowing it here
        // could close the one-shot handoff gate without delivering anything.
        return adaptiveQuality && !awaitingRecoveryHandoff;
    }

    internal static bool ShouldDeferH264FrameForPendingControl(
        bool hasPendingControlWrite,
        bool recoveryFrame,
        bool awaitingRecoveryHandoff) =>
        hasPendingControlWrite &&
        (!recoveryFrame || !awaitingRecoveryHandoff);

    internal static bool ShouldUseShortGopH264(
        RemoteDeviceCapabilities viewerCapabilities,
        int configuredFramesPerSecond,
        int sourceFramesPerSecond)
    {
        if (!viewerCapabilities.HasFlag(
                RemoteDeviceCapabilities.ShortGopH264))
        {
            return false;
        }

        if (viewerCapabilities.HasFlag(
                RemoteDeviceCapabilities.LowLatencyUdpVideo) &&
            !viewerCapabilities.HasFlag(
                RemoteDeviceCapabilities
                    .UdpVideoCongestionFeedback))
        {
            return false;
        }

        int configured = Math.Clamp(
            configuredFramesPerSecond,
            1,
            60);
        if (sourceFramesPerSecond < configured)
        {
            return false;
        }

        return sourceFramesPerSecond == configured ||
            sourceFramesPerSecond == configured * 2 ||
            (configured == 30 &&
                sourceFramesPerSecond == configured * 3);
    }

    internal static bool ShouldAttemptHardwareH264(
        RemoteVideoCodecs supportedCodecs,
        int targetVersion,
        int codecVersion,
        int failedTargetVersion,
        int failedCodecVersion)
    {
        return supportedCodecs.HasFlag(
                RemoteVideoCodecs.H264AnnexB) &&
            (targetVersion != failedTargetVersion ||
                codecVersion != failedCodecVersion);
    }

    internal static TimeSpan
        CalculateHardwareH264RetryDelay(
            int retryNumber)
    {
        if (retryNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryNumber));
        }

        int exponent =
            Math.Min(retryNumber - 1, 4);
        double seconds =
            InitialHardwareH264RetryDelay
                .TotalSeconds *
            (1 << exponent);
        return TimeSpan.FromSeconds(
            Math.Min(
                seconds,
                MaximumHardwareH264RetryDelay
                    .TotalSeconds));
    }

    internal static bool ShouldRetryHardwareH264(
        RemoteVideoCodecs supportedCodecs,
        int targetVersion,
        int codecVersion,
        int failedTargetVersion,
        int failedCodecVersion,
        long nowMilliseconds,
        long retryAtMilliseconds)
    {
        return supportedCodecs.HasFlag(
                RemoteVideoCodecs.H264AnnexB) &&
            targetVersion == failedTargetVersion &&
            codecVersion == failedCodecVersion &&
            retryAtMilliseconds > 0 &&
            nowMilliseconds >= retryAtMilliseconds;
    }

    internal static int ChooseInitialAdaptiveScale(Rectangle captureBounds, int targetScalePercent, bool adaptiveQuality)
    {
        _ = captureBounds;
        _ = adaptiveQuality;
        int clampedTargetScale = Math.Clamp(targetScalePercent, 25, 100);
        // Scale is an explicit spatial-quality choice. Starting a large
        // target below that choice makes a healthy LAN session permanently
        // look soft until several recovery windows happen to run. Begin at
        // the requested resolution and let sustained overload reduce temporal
        // rate and JPEG quality before spatial resolution.
        return clampedTargetScale;
    }

    internal static int ChooseInteractiveH264Scale(
        Rectangle captureBounds,
        int sourceScalePercent,
        int configuredFramesPerSecond,
        int sourceFramesPerSecond)
    {
        _ = captureBounds;
        _ = configuredFramesPerSecond;
        _ = sourceFramesPerSecond;
        int clampedSourceScale =
            Math.Clamp(sourceScalePercent, 25, 100);
        // Interactive boost is temporal: it may encode a faster source clock
        // and transmit extra fresh frames while input is active, but it must
        // never silently turn a user-selected 4K/1440p stream into 1080p for
        // the entire session.
        return clampedSourceScale;
    }

    internal static bool ShouldAdaptReliableH264Resolution(bool adaptiveQuality,
        RemoteDeviceCapabilities viewerCapabilities) =>
        adaptiveQuality && !viewerCapabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo);

    internal static int LimitInteractiveH264SourceFramesPerSecond(
        Size outputSize,
        int configuredFramesPerSecond,
        int proposedSourceFramesPerSecond)
    {
        int configured = Math.Clamp(
            configuredFramesPerSecond,
            1,
            60);
        int proposed = Math.Clamp(
            proposedSourceFramesPerSecond,
            configured,
            90);
        long outputPixels =
            Math.Max(0L, outputSize.Width) *
            Math.Max(0L, outputSize.Height);
        if (outputPixels >
                InteractiveH264BoostMaximumPixels &&
            proposed > configured)
        {
            // On high-resolution desktops the explicit spatial choice wins
            // over the optional 750 ms temporal boost. The local cursor and
            // UDP input/ack path stay immediate; users who explicitly select
            // 60 FPS still receive native-resolution 60 FPS.
            return configured;
        }

        return proposed;
    }

    public async Task StopAsync()
    {
        TcpListener? listener = _listener;
        CancellationTokenSource? cancellationTokenSource = _cancellationTokenSource;
        Task? acceptLoopTask = _acceptLoopTask;

        _listener = null;
        Volatile.Write(ref _listeningPort, 0);
        _cancellationTokenSource = null;
        _acceptLoopTask = null;

        if (listener is null && cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource?.Cancel();
            listener?.Stop();
            CloseActiveClients();
            if (acceptLoopTask is not null)
            {
                await IgnoreStopExceptionAsync(acceptLoopTask).ConfigureAwait(false);
            }

            Task[] clientTasks = SnapshotClientTasks();
            if (clientTasks.Length > 0)
            {
                await IgnoreStopExceptionAsync(Task.WhenAll(clientTasks)).ConfigureAwait(false);
            }
        }
        finally
        {
            cancellationTokenSource?.Dispose();
            _desktopDuplicationCircuitBreaker.Reset();
        }

        Log?.Invoke("被控端已停止。");
        ClientStatusChanged?.Invoke("未启动");
        RunningChanged?.Invoke(false);
    }

    public void Dispose()
    {
        try
        {
            Task stopTask = StopAsync();
            if (!stopTask.Wait(DisposeStopTimeout))
            {
                Log?.Invoke("被控端停止超时，退出时将强制结束残留连接。");
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(IsStopException))
        {
        }
        catch (Exception ex) when (IsStopException(ex))
        {
        }
    }

    private async Task AcceptLoopAsync(
        string password,
        int fps,
        int jpegQuality,
        int scalePercent,
        ScreenCaptureTarget captureTarget,
        bool adaptiveQuality,
        CancellationToken cancellationToken)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("监听器未启动。");
        var activeClientGate =
            new ActiveClientGate<ActiveClientConnection>();
        var clientAdmissionGate = new BoundedClientAdmissionGate(
            MaxConcurrentClientHandlers);

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log?.Invoke($"接受客户端连接失败：{ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                continue;
            }

            string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "未知地址";
            if (!clientAdmissionGate.TryEnter())
            {
                Log?.Invoke($"认证队列已满，已拒绝 {remoteEndpoint}。");
                client.Dispose();
                continue;
            }

            TrackActiveClient(client);
            Task clientTask = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(
                        client,
                        password,
                        fps,
                        jpegQuality,
                        scalePercent,
                        captureTarget,
                        adaptiveQuality,
                        activeClientGate,
                        cancellationToken);
                }
                finally
                {
                    UntrackActiveClient(client);
                    clientAdmissionGate.Exit();
                }
            }, CancellationToken.None);
            TrackClientTask(clientTask);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        string password,
        int fps,
        int jpegQuality,
        int scalePercent,
        ScreenCaptureTarget captureTarget,
        bool adaptiveQuality,
        ActiveClientGate<ActiveClientConnection> activeClientGate,
        CancellationToken serverCancellationToken)
    {
        string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "未知地址";
        ActiveClientConnection? activeClientOwner = null;

        using (client)
        await using (NetworkStream stream = client.GetStream())
        {
            NetworkUtils.ConfigureLowLatencyTcpClient(
                client,
                InputReceiveBufferBytes,
                RelayLoopbackPolicy.HostSendBufferBytes(client.Client.RemoteEndPoint, FrameSendBufferBytes));

            try
            {
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
                authenticationTimeout.CancelAfter(AuthenticationTimeout);

                ServerAuthenticationResult authentication;
                try
                {
                    authentication = await Protocol.AuthenticateServerDetailedAsync(stream, password, authenticationTimeout.Token);
                }
                catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
                {
                    Log?.Invoke($"认证超时：{remoteEndpoint}");
                    return;
                }

                if (!authentication.IsAuthenticated)
                {
                    if (!authentication.IsIncomplete)
                    {
                        Log?.Invoke($"认证失败：{remoteEndpoint}");
                        await Task.Delay(
                            AuthenticationFailureDelay,
                            serverCancellationToken);
                    }

                    return;
                }

                SecureSession session = authentication.Session!;
                using (session)
                {
                    using var clientCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            serverCancellationToken);
                    using var writePriority = new SessionWritePriority();
                    var candidateOwner = new ActiveClientConnection(
                        client,
                        stream,
                        session,
                        writePriority,
                        clientCancellation);
                    ActiveClientConnection? replacedOwner =
                        activeClientGate.Activate(candidateOwner);
                    activeClientOwner = candidateOwner;
                    if (replacedOwner is not null)
                    {
                        Log?.Invoke(
                            $"新的已认证查看端 {remoteEndpoint} 正在接管当前会话。");
                        await replacedOwner
                            .DisconnectForReplacementAsync()
                            .ConfigureAwait(false);
                    }

                    using IDisposable wlanMediaStreaming =
                        WindowsWlanMediaStreaming.TryAcquireInteractive(
                            client.Client.LocalEndPoint,
                            message => Log?.Invoke(message));
                    using var captureTargetPublicationCoordinator =
                        new CaptureTargetPublicationCoordinator();
                    using var captureState = new CaptureSessionState(
                        captureTarget,
                        scalePercent,
                        captureTargetPublicationCoordinator
                            .AdvanceGeneration);
                    using var sessionPower = WindowsRemoteSessionPowerRequest.TryAcquire(message => Log?.Invoke(message));
                    using var inputInjectionDispatcher =
                        new InputInjectionDispatcher();
                    using var fileTransferReceiver = new FileTransferReceiver(
                        message => Log?.Invoke(message),
                        FileTransferReceiver.GetReceiveDirectory,
                        sendPasteShortcut:
                            inputInjectionDispatcher.SendPasteShortcut);
                    var viewerState = new ViewerSessionState();
                    var interactionActivity =
                        new RemoteInteractionActivity();
                    await using var lowLatencyVideo = new LowLatencyVideoHostTransport(
                        message => Log?.Invoke(message),
                        clientCancellation.Token,
                        async (channelId, epoch, reason) =>
                        {
                            byte[] stoppedPayload =
                                RemoteMessageCodec.EncodeLowLatencyVideoStopped(
                                    channelId,
                                    epoch,
                                    reason);
                            using (writePriority.BeginControlWritePriority())
                            {
                                await Protocol.WriteMessageAsync(
                                    stream,
                                    MessageType.Control,
                                    stoppedPayload,
                                    session,
                                    writePriority.Lock,
                                    clientCancellation.Token);
                            }
                        },
                        applyUdpMouseMove: input =>
                        {
                            if (!captureState.TryGetInputMappingSnapshot(
                                    input,
                                    out Rectangle captureBounds,
                                    out Size frameSize))
                            {
                                return;
                            }

                            inputInjectionDispatcher.Apply(
                                input,
                                captureBounds,
                                frameSize);
                            interactionActivity.Record();
                        });
                    IPAddress peerAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    IPEndPoint hostTcpEndpoint =
                        (IPEndPoint)client.Client.LocalEndPoint!;

                    Log?.Invoke($"客户端已加密连接：{remoteEndpoint}");
                    ClientStatusChanged?.Invoke($"客户端已连接：{remoteEndpoint}");
                    await SendDeviceInfoAsync(stream, session, writePriority.Lock, clientCancellation.Token);
                    IReadOnlyList<ScreenCaptureTarget> initialCaptureTargets =
                        ScreenCaptureService.GetAvailableTargets();
                    captureState.RefreshCaptureTopologyWithAutomaticFallback(
                        initialCaptureTargets);
                    CaptureTargetStateSnapshot initialCaptureTargetSnapshot =
                        captureState.GetPublicationSnapshot();
                    CaptureTargetInfo[] initialCaptureTargetInfos =
                        initialCaptureTargets
                            .Select(ScreenCaptureService.ToInfo)
                            .ToArray();
                    await captureTargetPublicationCoordinator
                        .PublishIfCurrentAsync(
                            initialCaptureTargetSnapshot,
                            captureState
                                .IsCurrentPublicationSnapshot,
                            async token =>
                            {
                                await SendCaptureTargetsAsync(
                                    stream,
                                    session,
                                    writePriority.Lock,
                                    initialCaptureTargetInfos,
                                    token);
                                await SendCaptureTargetChangedAsync(
                                    stream,
                                    session,
                                    writePriority.Lock,
                                    captureState.CurrentTarget,
                                    token);
                                // Seed the viewer with the host capture
                                // generation even when the initial target is
                                // healthy. Later same-id bounds changes can
                                // then fence the old presentation chain.
                                await SendCaptureTargetAvailabilityAsync(
                                    stream,
                                    session,
                                    writePriority.Lock,
                                    initialCaptureTargetSnapshot,
                                    token);
                            },
                            clientCancellation.Token);

                    var captureTargetTopologyState =
                        new CaptureTargetTopologyMonitorState(
                            initialCaptureTargetInfos,
                            initialCaptureTargetSnapshot);

                    var inboundLiveness =
                        new HostSessionLivenessTracker();
                    Task inboundWatchdogTask =
                        inboundLiveness
                            .WaitForInboundReadTimeoutAsync(
                                clientCancellation.Token);
                    Task inputTask = RunInputLoopAsync(
                        stream,
                        session,
                        writePriority,
                        captureState,
                        captureTargetTopologyState,
                        captureTargetPublicationCoordinator,
                        fileTransferReceiver,
                        viewerState,
                        lowLatencyVideo,
                        peerAddress,
                        hostTcpEndpoint,
                        inboundLiveness,
                        inputInjectionDispatcher,
                        interactionActivity,
                        target => Log?.Invoke($"客户端切换捕获屏幕：{target.DisplayName}"),
                        message => Log?.Invoke(message),
                        clientCancellation.Token);

                    Task captureTask = RunCaptureLoopAsync(
                        stream,
                        session,
                        writePriority,
                        captureState,
                        captureTargetPublicationCoordinator,
                        viewerState,
                        scalePercent,
                        fps,
                        jpegQuality,
                        adaptiveQuality,
                        lowLatencyVideo,
                        interactionActivity,
                        _desktopDuplicationCircuitBreaker,
                        message => { viewerState.VideoDiagnostics.Record(message); Log?.Invoke(message); },
                        clientCancellation.Token);
                    Task captureTargetTopologyTask =
                        RunCaptureTargetTopologyMonitorAsync(
                            captureTargetTopologyState,
                            ScreenCaptureService.GetAvailableTargets,
                            captureState.RefreshCaptureTopologyWithAutomaticFallback,
                            (publication, token) =>
                                PublishCaptureTargetTopologyAsync(
                                    stream,
                                    session,
                                    writePriority,
                                    captureState,
                                    captureTargetPublicationCoordinator,
                                    lowLatencyVideo,
                                    publication,
                                    message => Log?.Invoke(message),
                                    token),
                            CaptureTargetUnavailablePollInterval,
                            clientCancellation.Token);

                    try
                    {
                        await SuperviseClientSessionTasksAsync(
                            captureTask,
                            captureTargetTopologyTask,
                            inputTask,
                            inboundWatchdogTask,
                            clientCancellation,
                            client.Close,
                            endReason =>
                            {
                                switch (endReason)
                                {
                                    case ClientSessionEndReason
                                        .InboundReadTimedOut:
                                        Log?.Invoke(
                                            $"客户端入站消息等待超时：{remoteEndpoint}，" +
                                            $"连续 {inboundLiveness.InboundReadTimeout.TotalSeconds:0} 秒" +
                                            "未收到完整消息；已主动关闭连接。");
                                        break;
                                    case ClientSessionEndReason
                                        .InputTaskEnded:
                                        Log?.Invoke(
                                            "客户端输入/控制通道先结束：" +
                                            FormatSessionTaskCompletion(
                                                inputTask));
                                        break;
                                    case ClientSessionEndReason
                                        .CaptureTaskEnded:
                                        Log?.Invoke(
                                            "客户端画面发送通道先结束：" +
                                            FormatSessionTaskCompletion(
                                                captureTask));
                                        break;
                                    case ClientSessionEndReason
                                        .CaptureTargetTopologyTaskEnded:
                                        Log?.Invoke(
                                            "客户端屏幕拓扑通知通道先结束：" +
                                            FormatSessionTaskCompletion(
                                                captureTargetTopologyTask));
                                        break;
                                    case ClientSessionEndReason
                                        .InboundWatchdogTaskEnded:
                                        Log?.Invoke(
                                            "客户端入站监督任务异常结束：" +
                                            FormatSessionTaskCompletion(
                                                inboundWatchdogTask));
                                        break;
                                }
                            });
                    }
                    finally
                    {
                        // Slow input-independent control reads share the session and write-priority
                        // lock. Always join them before either shared resource leaves this scope,
                        // including when the capture/input aggregate completed unexpectedly.
                        await IgnoreStopExceptionAsync(
                            viewerState
                                .WaitForInputIndependentControlsAsync());
                        await IgnoreStopExceptionAsync(viewerState.CancelAndWaitForClipboardFileReturnAsync());
                    }
                }
            }
            catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (activeClientOwner?.ReplacementRequested == true)
                {
                    Log?.Invoke($"旧查看端已被新会话接管：{remoteEndpoint}");
                }
                else
                {
                    Log?.Invoke($"客户端连接中断：{remoteEndpoint}，{ex.Message}");
                }
            }
            finally
            {
                activeClientOwner?.MarkClosed();
                if (activeClientOwner is not null &&
                    activeClientGate.Release(activeClientOwner))
                {
                    if (!serverCancellationToken.IsCancellationRequested)
                    {
                        int listeningPort =
                            ListeningPort;
                        ClientStatusChanged?.Invoke(
                            listeningPort > 0
                                ? $"正在监听 0.0.0.0:{listeningPort}"
                                : "正在监听");
                    }
                }

                // Authentication failures and replaced owners do not own the
                // displayed session status. Their delayed cleanup must not
                // overwrite the newest authenticated viewer.
                Log?.Invoke($"客户端已断开：{remoteEndpoint}");
            }
        }
    }

    internal sealed class ActiveClientGate<T>
        where T : class
    {
        private readonly object _sync = new();
        private T? _activeClient;

        public T? Activate(T client)
        {
            ArgumentNullException.ThrowIfNull(client);
            lock (_sync)
            {
                T? replaced = _activeClient;
                _activeClient = client;
                return replaced;
            }
        }

        public bool Release(T client)
        {
            ArgumentNullException.ThrowIfNull(client);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeClient, client))
                {
                    return false;
                }

                _activeClient = null;
                return true;
            }
        }

        public T? Current
        {
            get
            {
                lock (_sync)
                {
                    return _activeClient;
                }
            }
        }
    }

    internal sealed class ActiveClientConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SecureSession _session;
        private readonly SessionWritePriority _writePriority;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _replacementRequested;

        public ActiveClientConnection(
            TcpClient client,
            NetworkStream stream,
            SecureSession session,
            SessionWritePriority writePriority,
            CancellationTokenSource cancellation)
        {
            _client = client;
            _stream = stream;
            _session = session;
            _writePriority = writePriority;
            _cancellation = cancellation;
        }

        public bool ReplacementRequested =>
            Volatile.Read(ref _replacementRequested) != 0;

        public void MarkClosed()
        {
            _closed.TrySetResult();
        }

        public async Task DisconnectForReplacementAsync()
        {
            if (Interlocked.Exchange(
                    ref _replacementRequested,
                    1) != 0)
            {
                return;
            }

            try
            {
                using var timeout =
                    new CancellationTokenSource(
                        SessionRejectionWriteTimeout);
                using (_writePriority.BeginControlWritePriority())
                {
                    await Protocol.WriteMessageAsync(
                            _stream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeSessionRejected(
                                SessionReplacedMessage),
                            _session,
                            _writePriority.Lock,
                            timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or
                    IOException or
                    SocketException or
                    ObjectDisposedException or
                    CryptographicException)
            {
            }
            finally
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _client.Close();
            }

            try
            {
                await _closed.Task
                    .WaitAsync(SessionReplacementDrainTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    internal sealed class BoundedClientAdmissionGate
    {
        private readonly int _maximum;
        private int _current;

        public BoundedClientAdmissionGate(int maximum)
        {
            if (maximum <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximum));
            }

            _maximum = maximum;
        }

        public int Current => Volatile.Read(ref _current);

        public bool TryEnter()
        {
            while (true)
            {
                int current = Volatile.Read(ref _current);
                if (current >= _maximum)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref _current,
                        current + 1,
                        current) == current)
                {
                    return true;
                }
            }
        }

        public void Exit()
        {
            int remaining = Interlocked.Decrement(ref _current);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _current, 0);
                throw new InvalidOperationException("客户端准入计数不能为负数。");
            }
        }
    }

    internal enum ClientSessionEndReason
    {
        CaptureTaskEnded,
        CaptureTargetTopologyTaskEnded,
        InputTaskEnded,
        InboundWatchdogTaskEnded,
        InboundReadTimedOut
    }

    internal static async Task<ClientSessionEndReason>
        WaitForFirstClientSessionTaskAsync(
            Task captureTask,
            Task inputTask,
            Task inboundWatchdogTask)
    {
        return await WaitForFirstClientSessionTaskCoreAsync(
                captureTask,
                captureTargetTopologyTask: null,
                inputTask,
                inboundWatchdogTask)
            .ConfigureAwait(false);
    }

    internal static async Task<ClientSessionEndReason>
        WaitForFirstClientSessionTaskAsync(
            Task captureTask,
            Task captureTargetTopologyTask,
            Task inputTask,
            Task inboundWatchdogTask)
    {
        ArgumentNullException.ThrowIfNull(
            captureTargetTopologyTask);
        return await WaitForFirstClientSessionTaskCoreAsync(
                captureTask,
                captureTargetTopologyTask,
                inputTask,
                inboundWatchdogTask)
            .ConfigureAwait(false);
    }

    private static async Task<ClientSessionEndReason>
        WaitForFirstClientSessionTaskCoreAsync(
            Task captureTask,
            Task? captureTargetTopologyTask,
            Task inputTask,
            Task inboundWatchdogTask)
    {
        ArgumentNullException.ThrowIfNull(captureTask);
        ArgumentNullException.ThrowIfNull(inputTask);
        ArgumentNullException.ThrowIfNull(inboundWatchdogTask);

        Task completed = captureTargetTopologyTask is null
            ? await Task.WhenAny(
                    captureTask,
                    inputTask,
                    inboundWatchdogTask)
                .ConfigureAwait(false)
            : await Task.WhenAny(
                    captureTask,
                    captureTargetTopologyTask,
                    inputTask,
                    inboundWatchdogTask)
                .ConfigureAwait(false);
        if (ReferenceEquals(completed, inboundWatchdogTask))
        {
            return inboundWatchdogTask.Status ==
                    TaskStatus.RanToCompletion
                ? ClientSessionEndReason.InboundReadTimedOut
                : ClientSessionEndReason
                    .InboundWatchdogTaskEnded;
        }

        if (ReferenceEquals(
                completed,
                captureTargetTopologyTask))
        {
            return ClientSessionEndReason
                .CaptureTargetTopologyTaskEnded;
        }

        return ReferenceEquals(completed, inputTask)
            ? ClientSessionEndReason.InputTaskEnded
            : ClientSessionEndReason.CaptureTaskEnded;
    }

    internal static async Task<ClientSessionEndReason>
        SuperviseClientSessionTasksAsync(
            Task captureTask,
            Task inputTask,
            Task inboundWatchdogTask,
            CancellationTokenSource clientCancellation,
            Action closeClient,
            Action<ClientSessionEndReason>? sessionEnding = null)
    {
        return await SuperviseClientSessionTasksCoreAsync(
                captureTask,
                captureTargetTopologyTask: null,
                inputTask,
                inboundWatchdogTask,
                clientCancellation,
                closeClient,
                sessionEnding)
            .ConfigureAwait(false);
    }

    internal static async Task<ClientSessionEndReason>
        SuperviseClientSessionTasksAsync(
            Task captureTask,
            Task captureTargetTopologyTask,
            Task inputTask,
            Task inboundWatchdogTask,
            CancellationTokenSource clientCancellation,
            Action closeClient,
            Action<ClientSessionEndReason>? sessionEnding = null)
    {
        ArgumentNullException.ThrowIfNull(
            captureTargetTopologyTask);
        return await SuperviseClientSessionTasksCoreAsync(
                captureTask,
                captureTargetTopologyTask,
                inputTask,
                inboundWatchdogTask,
                clientCancellation,
                closeClient,
                sessionEnding)
            .ConfigureAwait(false);
    }

    private static async Task<ClientSessionEndReason>
        SuperviseClientSessionTasksCoreAsync(
            Task captureTask,
            Task? captureTargetTopologyTask,
            Task inputTask,
            Task inboundWatchdogTask,
            CancellationTokenSource clientCancellation,
            Action closeClient,
            Action<ClientSessionEndReason>? sessionEnding)
    {
        ArgumentNullException.ThrowIfNull(clientCancellation);
        ArgumentNullException.ThrowIfNull(closeClient);

        ClientSessionEndReason endReason =
            await WaitForFirstClientSessionTaskCoreAsync(
                    captureTask,
                    captureTargetTopologyTask,
                    inputTask,
                    inboundWatchdogTask)
                .ConfigureAwait(false);
        clientCancellation.Cancel();
        try
        {
            // Cancellation alone does not reliably interrupt every in-flight
            // NetworkStream read on Windows. Close before joining so the read
            // exits and the caller can release its active-client gate.
            closeClient();
        }
        catch (Exception ex) when (
            ex is ObjectDisposedException or
                IOException or
                SocketException)
        {
        }

        try
        {
            sessionEnding?.Invoke(endReason);
        }
        finally
        {
            Task[] tasks = captureTargetTopologyTask is null
                ? [
                    captureTask,
                    inputTask,
                    inboundWatchdogTask
                ]
                : [
                    captureTask,
                    captureTargetTopologyTask,
                    inputTask,
                    inboundWatchdogTask
                ];
            await IgnoreStopExceptionAsync(
                    Task.WhenAll(tasks))
                .ConfigureAwait(false);
        }

        return endReason;
    }

    internal static string FormatSessionTaskCompletion(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Exception? failure = task.Exception?.GetBaseException();
        if (failure is not null)
        {
            return $"{failure.GetType().Name}: {failure.Message}";
        }

        return task.IsCanceled
            ? "任务已取消。"
            : "对端正常关闭或任务已完成。";
    }

    internal static byte ResolveLowLatencyVideoStoppedReason(
        byte requestedReason,
        bool keptUdpInput)
    {
        if (requestedReason !=
            LowLatencyVideoFallbackReasons.PreserveUdpInput)
        {
            return requestedReason;
        }

        // The acknowledgement describes what the host actually committed,
        // not merely what the viewer requested.  If preserving the route lost
        // a race with feedback expiry or socket shutdown, force the viewer to
        // tear down UDP instead of promoting a dead route to state 5.
        return keptUdpInput
            ? LowLatencyVideoFallbackReasons.PreserveUdpInput
            : (byte)1;
    }

    internal readonly record struct ViewerVideoSelection(
        RemoteVideoCodecs SupportedCodecs,
        int Version);

    internal sealed class ViewerSessionState
    {
        internal HostVideoDiagnostics VideoDiagnostics { get; } = new();
        private readonly object _clipboardFileReturnLock = new();
        private readonly object _clipboardInputSequenceLock = new();
        private readonly object
            _inputIndependentControlLock = new();
        private readonly TaskCompletionSource _videoSelectionReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _capabilitiesReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _videoSelectionChangeLock = new();
        private CancellationTokenSource _videoSelectionChanged = new();
        private RemoteFilePastePlan? _pendingClipboardFileReturnPlan;
        private ClipboardFileReturnOperation? _activeClipboardFileReturn;
        private ClipboardInputSequenceMarker? _pendingClipboardInputSequence;
        private Task _inputIndependentControlTail =
            Task.CompletedTask;
        private long _videoSelection = PackVideoSelection(
            RemoteVideoCodecs.Jpeg,
            version: 0);
        private int _keyFrameRequestVersion;
        private int _capabilities;

        public RemoteDeviceCapabilities Capabilities
        {
            get => (RemoteDeviceCapabilities)Volatile.Read(
                ref _capabilities);
            set
            {
                RemoteDeviceCapabilities previous =
                    (RemoteDeviceCapabilities)Interlocked.Exchange(
                        ref _capabilities,
                        (int)value);
                _capabilitiesReady.TrySetResult();

                // ViewerInfo and ViewerCapabilities are ordered on the TCP
                // stream, but the capture and input loops run independently.
                // The short legacy grace period is therefore an optimization,
                // not a correctness boundary: if capabilities arrive after a
                // 30 FPS H.264 probe has started, republish the unchanged
                // codec selection so the existing selection monitor cancels
                // and restarts it with the feedback-controlled high-rate
                // source.
                // Do not publish the legacy JPEG default when capabilities
                // happen to precede ViewerInfo.
                if (_videoSelectionReady.Task.IsCompleted &&
                    (InteractiveH264FrameController
                        .SupportsFeedbackControlledUdp(previous) !=
                     InteractiveH264FrameController
                        .SupportsFeedbackControlledUdp(value) ||
                     previous.HasFlag(
                         RemoteDeviceCapabilities.HighFrameRateH264) !=
                     value.HasFlag(
                         RemoteDeviceCapabilities.HighFrameRateH264) ||
                     previous.HasFlag(
                         RemoteDeviceCapabilities.HighQualityJpeg) !=
                     value.HasFlag(
                         RemoteDeviceCapabilities.HighQualityJpeg) ||
                     LowLatencyVideoFeatureNegotiation
                         .FromCapabilities(previous)
                         .HasFlag(
                             LowLatencyVideoFeatures
                                 .AuthenticatedHeartbeat) !=
                     LowLatencyVideoFeatureNegotiation
                         .FromCapabilities(value)
                         .HasFlag(
                             LowLatencyVideoFeatures
                                 .AuthenticatedHeartbeat)))
                {
                    SetSupportedVideoCodecs(
                        GetVideoSelection().SupportedCodecs);
                }
            }
        }

        public RemoteVideoCodecs SupportedVideoCodecs =>
            GetVideoSelection().SupportedCodecs;

        public int KeyFrameRequestVersion =>
            Volatile.Read(ref _keyFrameRequestVersion);

        public int VideoCodecVersion =>
            GetVideoSelection().Version;

        public bool IsVideoSelectionReady =>
            _videoSelectionReady.Task.IsCompleted;

        public ViewerVideoSelection GetVideoSelection()
        {
            long packed = Volatile.Read(ref _videoSelection);
            return new ViewerVideoSelection(
                (RemoteVideoCodecs)(uint)packed,
                unchecked((int)(uint)(packed >> 32)));
        }

        public CancellationToken GetVideoSelectionChangeToken(
            int expectedVersion)
        {
            lock (_videoSelectionChangeLock)
            {
                if (GetVideoSelection().Version != expectedVersion)
                {
                    return new CancellationToken(canceled: true);
                }

                return _videoSelectionChanged.Token;
            }
        }

        public void SetSupportedVideoCodecs(RemoteVideoCodecs codecs)
        {
            const RemoteVideoCodecs knownCodecs =
                RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB;
            RemoteVideoCodecs normalized = codecs & knownCodecs;
            normalized = normalized == RemoteVideoCodecs.None
                ? RemoteVideoCodecs.Jpeg
                : normalized;

            CancellationTokenSource changed;
            lock (_videoSelectionChangeLock)
            {
                long observed = Volatile.Read(ref _videoSelection);
                int observedVersion =
                    unchecked((int)(uint)(observed >> 32));
                long updated = PackVideoSelection(
                    normalized,
                    unchecked(observedVersion + 1));
                Volatile.Write(ref _videoSelection, updated);
                changed = _videoSelectionChanged;
                _videoSelectionChanged = new();
            }

            // Do not dispose here: a capture loop can have obtained the old
            // token immediately before this publication and may still be
            // registering it with a linked CTS. Once that linked CTS is
            // released, the canceled source is eligible for collection.
            changed.Cancel();
            _videoSelectionReady.TrySetResult();
        }

        public async Task WaitForInitialVideoSelectionAsync(
            TimeSpan legacyGracePeriod,
            CancellationToken cancellationToken)
        {
            if (legacyGracePeriod < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(legacyGracePeriod));
            }

            Task gracePeriod = Task.Delay(
                legacyGracePeriod,
                cancellationToken);
            await Task.WhenAny(
                _videoSelectionReady.Task,
                gracePeriod).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task WaitForInitialCapabilitiesAsync(
            TimeSpan legacyGracePeriod,
            CancellationToken cancellationToken)
        {
            if (legacyGracePeriod < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(legacyGracePeriod));
            }

            Task gracePeriod = Task.Delay(
                legacyGracePeriod,
                cancellationToken);
            await Task.WhenAny(
                _capabilitiesReady.Task,
                gracePeriod).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void RequestVideoKeyFrame()
        {
            Interlocked.Increment(ref _keyFrameRequestVersion);
        }

        private static long PackVideoSelection(
            RemoteVideoCodecs codecs,
            int version)
        {
            return ((long)(uint)version << 32) | (uint)codecs;
        }

        public bool HasPendingClipboardFileReturnPlan
        {
            get
            {
                lock (_clipboardFileReturnLock)
                {
                    return _pendingClipboardFileReturnPlan is not null;
                }
            }
        }

        public bool HasActiveClipboardFileReturn
        {
            get
            {
                lock (_clipboardFileReturnLock)
                {
                    return _activeClipboardFileReturn is not null;
                }
            }
        }

        public string? PendingRemoteUpdateTransferId { get; set; }

        public void RecordClipboardInputSequence(uint sequence, long nowMilliseconds)
        {
            lock (_clipboardInputSequenceLock)
            {
                _pendingClipboardInputSequence = new ClipboardInputSequenceMarker(sequence, nowMilliseconds);
            }
        }

        public void QueueInputIndependentControl(
            Func<Task> operation,
            Action<Exception> logFailure,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(logFailure);
            lock (_inputIndependentControlLock)
            {
                Task previous =
                    _inputIndependentControlTail;
                _inputIndependentControlTail =
                    RunInputIndependentControlAsync(
                        previous,
                        operation,
                        logFailure,
                        cancellationToken);
            }
        }

        public Task
            WaitForInputIndependentControlsAsync()
        {
            lock (_inputIndependentControlLock)
            {
                return _inputIndependentControlTail;
            }
        }

        private static async Task
            RunInputIndependentControlAsync(
                Task previous,
                Func<Task> operation,
                Action<Exception> logFailure,
                CancellationToken cancellationToken)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // Every queued operation observes and logs its own failure.
                // A previous failure must not poison later clipboard work.
            }

            try
            {
                // Even when the previous tail is already complete, leave the socket read loop.
                // These explicitly classified read-only requests may be passed by later input,
                // so their slow clipboard access cannot hold KeyUp, mouse, or ping messages.
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logFailure(ex);
            }
        }

        public uint? TakePendingClipboardInputSequence(
            long nowMilliseconds,
            long maxAgeMilliseconds,
            out bool expired)
        {
            return ReadPendingClipboardInputSequence(
                nowMilliseconds,
                maxAgeMilliseconds,
                consume: true,
                out expired);
        }

        public uint? PeekPendingClipboardInputSequence(
            long nowMilliseconds,
            long maxAgeMilliseconds,
            out bool expired)
        {
            return ReadPendingClipboardInputSequence(
                nowMilliseconds,
                maxAgeMilliseconds,
                consume: false,
                out expired);
        }

        private uint? ReadPendingClipboardInputSequence(
            long nowMilliseconds,
            long maxAgeMilliseconds,
            bool consume,
            out bool expired)
        {
            if (maxAgeMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAgeMilliseconds));
            }

            lock (_clipboardInputSequenceLock)
            {
                expired = false;
                ClipboardInputSequenceMarker? marker = _pendingClipboardInputSequence;
                if (consume)
                {
                    _pendingClipboardInputSequence = null;
                }

                if (marker is null)
                {
                    return null;
                }

                long age = nowMilliseconds - marker.Value.RecordedAtMilliseconds;
                expired = age < 0 || age > maxAgeMilliseconds;
                return marker.Value.Sequence;
            }
        }

        private readonly record struct ClipboardInputSequenceMarker(
            uint Sequence,
            long RecordedAtMilliseconds);

        public bool TryReserveClipboardFileReturnPlan(RemoteFilePastePlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            lock (_clipboardFileReturnLock)
            {
                if (_pendingClipboardFileReturnPlan is not null || _activeClipboardFileReturn is not null)
                {
                    return false;
                }

                _pendingClipboardFileReturnPlan = plan;
                return true;
            }
        }

        public RemoteFilePastePlan? TakePendingClipboardFileReturnPlan()
        {
            lock (_clipboardFileReturnLock)
            {
                RemoteFilePastePlan? plan = _pendingClipboardFileReturnPlan;
                _pendingClipboardFileReturnPlan = null;
                return plan;
            }
        }

        public bool ClearPendingClipboardFileReturnPlan()
        {
            lock (_clipboardFileReturnLock)
            {
                bool cleared = _pendingClipboardFileReturnPlan is not null;
                _pendingClipboardFileReturnPlan = null;
                return cleared;
            }
        }

        public bool TryStartClipboardFileReturn(
            Func<CancellationToken, Task> operation,
            CancellationToken sessionCancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);

            lock (_clipboardFileReturnLock)
            {
                if (_activeClipboardFileReturn is not null)
                {
                    return false;
                }

                var activeOperation = new ClipboardFileReturnOperation(
                    CancellationTokenSource.CreateLinkedTokenSource(sessionCancellationToken));
                _activeClipboardFileReturn = activeOperation;
                activeOperation.Task = RunClipboardFileReturnOperationAsync(activeOperation, operation);
                return true;
            }
        }

        public bool CancelActiveClipboardFileReturn()
        {
            ClipboardFileReturnOperation? activeOperation;
            lock (_clipboardFileReturnLock)
            {
                activeOperation = _activeClipboardFileReturn;
            }

            return TryCancelClipboardFileReturn(activeOperation);
        }

        public async Task<bool> CancelAndWaitForClipboardFileReturnAsync()
        {
            ClipboardFileReturnOperation? activeOperation;
            lock (_clipboardFileReturnLock)
            {
                activeOperation = _activeClipboardFileReturn;
                if (activeOperation is null)
                {
                    return false;
                }
            }

            TryCancelClipboardFileReturn(activeOperation);
            await activeOperation.Task.ConfigureAwait(false);
            return true;
        }

        private static bool TryCancelClipboardFileReturn(
            ClipboardFileReturnOperation? activeOperation)
        {
            if (activeOperation is null)
            {
                return false;
            }

            try
            {
                activeOperation.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race after the active operation was observed.
            }

            return true;
        }

        private async Task RunClipboardFileReturnOperationAsync(
            ClipboardFileReturnOperation activeOperation,
            Func<CancellationToken, Task> operation)
        {
            // Make sure Task is assigned before the continuation is able to clear the active slot.
            await Task.Yield();
            try
            {
                await operation(activeOperation.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_clipboardFileReturnLock)
                {
                    if (ReferenceEquals(_activeClipboardFileReturn, activeOperation))
                    {
                        _activeClipboardFileReturn = null;
                    }
                }

                activeOperation.Cancellation.Dispose();
            }
        }

        private sealed class ClipboardFileReturnOperation(CancellationTokenSource cancellation)
        {
            public CancellationTokenSource Cancellation { get; } = cancellation;

            public Task Task { get; set; } = Task.CompletedTask;
        }
    }

    private enum H264CaptureLoopExit
    {
        SelectionChanged,
        ResolutionChanged,
        StartupUnavailable,
        RuntimeFailed
    }

    private readonly record struct H264CaptureLoopResult(
        H264CaptureLoopExit Exit,
        TimeSpan ActiveDuration,
        FfmpegDesktopCaptureBackend? FailedBackend = null);

    internal readonly record struct DdaRecoveryProbeStart<TCapture>(
        TCapture? Capture,
        string FailureDetail)
        where TCapture : class;

    internal readonly record struct DdaRecoveryProbeAttempt<
        TCapture,
        TFrame>(
        TCapture? Capture,
        TFrame? RecoveryFrame,
        string FailureDetail,
        TimeSpan RetryAfter)
        where TCapture : class
        where TFrame : class
    {
        public bool Recovered =>
            Capture is not null &&
            RecoveryFrame is not null;
    }

    private static async Task RunCaptureLoopAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        CaptureSessionState captureState,
        CaptureTargetPublicationCoordinator
            captureTargetPublicationCoordinator,
        ViewerSessionState viewerState,
        int scalePercent,
        int fps,
        int jpegQuality,
        bool adaptiveQuality,
        LowLatencyVideoHostTransport lowLatencyVideo,
        RemoteInteractionActivity interactionActivity,
        DesktopDuplicationCircuitBreaker
            desktopDuplicationCircuitBreaker,
        Action<string> captureLog,
        CancellationToken cancellationToken)
    {
        int failedTargetVersion = int.MinValue;
        int failedCodecVersion = int.MinValue;
        int runtimeRestartTargetVersion = int.MinValue;
        int runtimeRestartCodecVersion = int.MinValue;
        int runtimeRestartCount = 0;
        int hardwareRetryCount = 0;
        long hardwareRetryAtMilliseconds =
            long.MaxValue;
        bool suppressWindowsGraphicsCapture = false;
        long ffmpegAvailabilityVersion = FfmpegH264Decoder.AvailabilityVersion;
        AdaptiveH264ResolutionController? resolutionController = null;
        int lastLoggedEffectiveFramesPerSecond = -1;
        long interactiveDesktopCheckedAt = 0;
        WindowsInteractiveDesktopAvailability
            interactiveDesktopAvailability =
                new(
                    IsAvailable: true,
                    "not checked");
        bool? lastReportedInteractiveDesktopAvailability =
            null;

        bool CanAttemptHardwareH264OnCurrentDesktop(
            RemoteVideoCodecs supportedCodecs)
        {
            if (!SupportsSecureDesktopJpegRecovery(
                    supportedCodecs, viewerState.Capabilities))
            {
                return true;
            }

            long now = Stopwatch.GetTimestamp();
            if (interactiveDesktopCheckedAt == 0 ||
                Stopwatch.GetElapsedTime(
                    interactiveDesktopCheckedAt,
                    now) >= TimeSpan.FromSeconds(1))
            {
                interactiveDesktopAvailability =
                    WindowsInteractiveDesktopProbe
                        .InspectCurrent();
                interactiveDesktopCheckedAt = now;
                if (lastReportedInteractiveDesktopAvailability !=
                    interactiveDesktopAvailability.IsAvailable)
                {
                    lastReportedInteractiveDesktopAvailability =
                        interactiveDesktopAvailability.IsAvailable;
                    captureLog(
                        interactiveDesktopAvailability.IsAvailable
                            ? "Windows 交互桌面已恢复，允许重新探测 H.264。"
                            : "Windows 当前为锁屏、UAC 或安全桌面；" +
                              "已暂停 H.264 编码探测并保留 JPEG 会话，" +
                              "避免后台反复启动编码器。详情：" +
                              interactiveDesktopAvailability.Diagnostic);
                }
            }

            return interactiveDesktopAvailability.IsAvailable;
        }

        await viewerState.WaitForInitialVideoSelectionAsync(
            InitialViewerInfoGracePeriod,
            cancellationToken);
        await viewerState.WaitForInitialCapabilitiesAsync(
            InitialViewerCapabilitiesGracePeriod,
            cancellationToken);
        bool initialVideoSelectionPreviewCompleted = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            // The capture service already rate-limits topology enumeration.
            // Do not force Screen.AllScreens onto the 60 FPS hot path; a
            // missing target is still rechecked within the 500 ms cache bound.
            captureState.RefreshCaptureBounds();
            if (!captureState.IsTargetAvailable)
            {
                await Task.Delay(
                    CaptureTargetUnavailablePollInterval,
                    cancellationToken);
                continue;
            }

            int targetVersion = captureState.TargetVersion;
            ViewerVideoSelection selection =
                viewerState.GetVideoSelection();
            int codecVersion = selection.Version;
            RemoteVideoCodecs codecs = selection.SupportedCodecs;
            int effectiveFramesPerSecond =
                ResolveNegotiatedH264FramesPerSecond(
                    fps,
                    viewerState.Capabilities);
            if (effectiveFramesPerSecond !=
                lastLoggedEffectiveFramesPerSecond)
            {
                lastLoggedEffectiveFramesPerSecond =
                    effectiveFramesPerSecond;
                if (fps > effectiveFramesPerSecond)
                {
                    captureLog(
                        $"查看端尚未声明 H.264 60 FPS 能力；" +
                        $"本次安全限制为 {effectiveFramesPerSecond} FPS。");
                }
                else if (effectiveFramesPerSecond > 30)
                {
                    captureLog(
                        $"{effectiveFramesPerSecond} FPS 高帧率能力已协商；" +
                        "仅在 GPU 捕获、硬编和低延迟显示链路可用时保持该档位。");
                }
            }

            if (runtimeRestartTargetVersion != targetVersion ||
                runtimeRestartCodecVersion != codecVersion)
            {
                runtimeRestartTargetVersion = targetVersion;
                runtimeRestartCodecVersion = codecVersion;
                runtimeRestartCount = 0;
                hardwareRetryCount = 0;
                hardwareRetryAtMilliseconds =
                    long.MaxValue;
                suppressWindowsGraphicsCapture = false;
                resolutionController = null;
            }

            if (ShouldAttemptHardwareH264(
                    codecs,
                    targetVersion,
                    codecVersion,
                    failedTargetVersion,
                    failedCodecVersion) &&
                CanAttemptHardwareH264OnCurrentDesktop(
                    codecs))
            {
                WindowsFfmpegDependency.StartIfMissing(captureLog);
                resolutionController ??= new AdaptiveH264ResolutionController(
                    CalculateH264FrameSize(captureState.LastCaptureBounds, scalePercent),
                    effectiveFramesPerSecond,
                    ShouldAdaptReliableH264Resolution(adaptiveQuality, viewerState.Capabilities));
                H264CaptureLoopResult captureResult =
                    await RunHardwareH264CaptureLoopAsync(
                        stream,
                        session,
                        writePriority,
                        captureState,
                        captureTargetPublicationCoordinator,
                        viewerState,
                        targetVersion,
                        codecVersion,
                        scalePercent,
                        effectiveFramesPerSecond,
                        jpegQuality,
                        adaptiveQuality,
                        resolutionController,
                        lowLatencyVideo,
                        interactionActivity,
                        desktopDuplicationCircuitBreaker,
                        suppressWindowsGraphicsCapture,
                        captureLog,
                        cancellationToken);
                H264CaptureLoopExit exit =
                    captureResult.Exit;
                if (exit is H264CaptureLoopExit.SelectionChanged or H264CaptureLoopExit.ResolutionChanged)
                {
                    continue;
                }

                ViewerVideoSelection currentSelection =
                    viewerState.GetVideoSelection();
                if (captureState.TargetVersion != targetVersion ||
                    currentSelection.Version != codecVersion)
                {
                    continue;
                }

                // A desktop transition is not an encoder defect. Recover
                // immediately, including legacy RemoteDesk viewers which
                // advertised JPEG capability but selected the old H.264-only
                // preference. Genuine H.264-only peers are not sent JPEG.
                interactiveDesktopCheckedAt = 0;
                if (!CanAttemptHardwareH264OnCurrentDesktop(currentSelection.SupportedCodecs))
                {
                    failedTargetVersion = failedCodecVersion = int.MinValue;
                    hardwareRetryAtMilliseconds = long.MaxValue;
                    runtimeRestartCount = hardwareRetryCount = 0;
                    continue;
                }

                if (exit == H264CaptureLoopExit.RuntimeFailed)
                {
                    if (!suppressWindowsGraphicsCapture &&
                        ShouldSuppressWindowsGraphicsCaptureAfterRuntimeFailure(
                            captureResult.FailedBackend))
                    {
                        suppressWindowsGraphicsCapture = true;
                        captureLog(
                            "Windows Graphics Capture 运行时中断；" +
                            "当前屏幕与编码选择的下一次重启将直接使用 " +
                            "DXGI Desktop Duplication 或精确坐标硬编码，" +
                            "避免周期性重复进入同一停帧链路。");
                    }

                    if (ShouldResetH264RuntimeRestartCount(
                            captureResult.ActiveDuration))
                    {
                        runtimeRestartCount = 0;
                    }

                    if (runtimeRestartCount <
                        MaximumH264RuntimeRestarts)
                    {
                        runtimeRestartCount++;
                        TimeSpan restartDelay =
                            CalculateH264RuntimeRestartDelay(
                                runtimeRestartCount);
                        captureLog(
                            "Windows H.264 运行时中断，" +
                            $"将在 {restartDelay.TotalMilliseconds:0}ms 后" +
                            $"重启硬编码（{runtimeRestartCount}/" +
                            $"{MaximumH264RuntimeRestarts}）。");
                        await Task.Delay(
                            restartDelay,
                            cancellationToken);
                        continue;
                    }

                    captureLog(
                        "Windows H.264 连续运行时重启失败，" +
                        "当前屏幕与编码选择将回退兼容路径。");
                }

                if (ShouldResetH264RuntimeRestartCount(
                        captureResult.ActiveDuration))
                {
                    hardwareRetryCount = 0;
                }

                failedTargetVersion = targetVersion;
                failedCodecVersion = codecVersion;
                hardwareRetryCount++;
                TimeSpan hardwareRetryDelay =
                    CalculateHardwareH264RetryDelay(
                        hardwareRetryCount);
                hardwareRetryAtMilliseconds =
                    Environment.TickCount64 +
                    (long)hardwareRetryDelay
                        .TotalMilliseconds;
                captureLog(
                    "当前先使用 JPEG 兼容画面；" +
                    $"{hardwareRetryDelay.TotalSeconds:0} 秒后将自动重探 GPU H.264，" +
                    "无需断开连接。");
                codecs = currentSelection.SupportedCodecs;
                if (!codecs.HasFlag(RemoteVideoCodecs.Jpeg))
                {
                    throw new InvalidOperationException(
                        "查看端仅允许 H.264，但当前 Windows 硬件编码链路不可用。");
                }
            }

            await RunJpegCaptureLoopAsync(
                stream,
                session,
                writePriority,
                captureState,
                captureTargetPublicationCoordinator,
                scalePercent,
                effectiveFramesPerSecond,
                jpegQuality,
                viewerState.Capabilities,
                adaptiveQuality,
                lowLatencyVideo,
                () =>
                {
                    ViewerVideoSelection currentSelection =
                        viewerState.GetVideoSelection();
                    if (ffmpegAvailabilityVersion != FfmpegH264Decoder.AvailabilityVersion)
                    {
                        ffmpegAvailabilityVersion = FfmpegH264Decoder.AvailabilityVersion;
                        failedTargetVersion = failedCodecVersion = int.MinValue;
                        hardwareRetryAtMilliseconds = long.MaxValue;
                        hardwareRetryCount = 0;
                    }
                    if (currentSelection.Version != codecVersion)
                    {
                        return true;
                    }

                    if (ShouldAttemptHardwareH264(
                        currentSelection.SupportedCodecs,
                        captureState.TargetVersion,
                        currentSelection.Version,
                        failedTargetVersion,
                        failedCodecVersion) &&
                        CanAttemptHardwareH264OnCurrentDesktop(
                            currentSelection
                                .SupportedCodecs))
                    {
                        return true;
                    }

                    if (!ShouldRetryHardwareH264(
                            currentSelection
                                .SupportedCodecs,
                            captureState.TargetVersion,
                            currentSelection.Version,
                            failedTargetVersion,
                            failedCodecVersion,
                            Environment.TickCount64,
                            hardwareRetryAtMilliseconds))
                    {
                        return false;
                    }

                    captureLog(
                        "JPEG 兼容期结束，正在自动重探 Windows GPU H.264 链路。");
                    failedTargetVersion =
                        int.MinValue;
                    failedCodecVersion =
                        int.MinValue;
                    hardwareRetryAtMilliseconds =
                        long.MaxValue;
                    return true;
                },
                captureLog,
                cancellationToken,
                startupPreviewOnly:
                    !initialVideoSelectionPreviewCompleted && codecVersion == 0);

            if (!initialVideoSelectionPreviewCompleted && codecVersion == 0)
            {
                initialVideoSelectionPreviewCompleted = true;
                // The first full-quality preview remains immediate. Do not
                // flood a relay's loopback buffers while the distant viewer's
                // selection is in flight. Legacy JPEG-only peers which never
                // send ViewerInfo resume their normal cadence after the grace.
                await viewerState.WaitForInitialVideoSelectionAsync(
                    InitialViewerInfoPreviewGracePeriod,
                    cancellationToken);
            }
        }
    }

    internal static TimeSpan CalculateH264RuntimeRestartDelay(
        int restartNumber)
    {
        if (restartNumber is < 1 or > MaximumH264RuntimeRestarts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(restartNumber));
        }

        return TimeSpan.FromMilliseconds(
            50 * (1 << (restartNumber - 1)));
    }

    private static async Task<FfmpegDesktopH264CaptureStartResult>
        StartHardwareH264CaptureWithFallbackAsync(
            FfmpegDesktopH264CaptureOptions options,
            IReadOnlyList<WindowsGraphicsCaptureTarget>
                graphicsCaptureTargets,
            int compatibilityFallbackFramesPerSecond,
            DesktopDuplicationCircuitBreaker
                desktopDuplicationCircuitBreaker,
            Action<string> captureLog,
            CancellationToken cancellationToken)
    {
        using var probeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        probeCancellation.CancelAfter(
            HardwareH264ProbeExecutionDeadline);
        try
        {
            IReadOnlyList<FfmpegDesktopH264CaptureOptions>
                startupOptions =
                    CreateHardwareH264StartupOptions(
                        options,
                        graphicsCaptureTargets,
                        compatibilityFallbackFramesPerSecond);
            FfmpegDesktopH264CaptureStartResult? lastFailure =
                null;
            bool skipRemainingWindowsGraphicsCaptureCandidates =
                false;
            for (int attemptIndex = 0;
                 attemptIndex < startupOptions.Count;
                 attemptIndex++)
            {
                FfmpegDesktopH264CaptureOptions attemptOptions =
                    startupOptions[attemptIndex];
                if (skipRemainingWindowsGraphicsCaptureCandidates &&
                    attemptOptions.Backend ==
                        FfmpegDesktopCaptureBackend
                            .WindowsGraphicsCaptureMonitor)
                {
                    continue;
                }

                if (attemptOptions.Backend ==
                    FfmpegDesktopCaptureBackend
                        .WindowsGraphicsCaptureMonitor)
                {
                    long wgcProbeStartedAt =
                        Stopwatch.GetTimestamp();
                    FfmpegDesktopH264CaptureStartResult start =
                        await FfmpegDesktopH264Capture
                            .TryStartAsync(
                                attemptOptions,
                                probeCancellation.Token)
                            .ConfigureAwait(false);
                    if (start.Started)
                    {
                        return start;
                    }

                    WindowsGraphicsCaptureTarget? target =
                        attemptOptions.GraphicsCaptureTarget;
                    string adapterDescription = target is null
                        ? "未解析适配器"
                        : $"adapter{target.AdapterIndex} " +
                            target.AdapterDescription;
                    lastFailure = start;
                    if (WindowsFfmpegDependency.NeedsGraphicsCaptureUpgrade(start.FailureDetail))
                        WindowsFfmpegDependency.StartIfMissing(captureLog, requireGraphicsCapture: true);
                    bool skipRemainingWgc =
                        ShouldSkipRemainingWindowsGraphicsCaptureCandidates(
                            attemptOptions,
                            start);
                    skipRemainingWindowsGraphicsCaptureCandidates |=
                        skipRemainingWgc;
                    int nextAttemptIndex = attemptIndex + 1;
                    while (skipRemainingWgc &&
                           nextAttemptIndex < startupOptions.Count &&
                           startupOptions[nextAttemptIndex].Backend ==
                               FfmpegDesktopCaptureBackend
                                   .WindowsGraphicsCaptureMonitor)
                    {
                        nextAttemptIndex++;
                    }

                    string nextPath = nextAttemptIndex <
                            startupOptions.Count
                        ? startupOptions[nextAttemptIndex].Backend switch
                        {
                            FfmpegDesktopCaptureBackend
                                .WindowsGraphicsCaptureMonitor =>
                                "下一个 WGC 编码适配器",
                            FfmpegDesktopCaptureBackend
                                .DesktopDuplicationOutput0 =>
                                "DXGI Desktop Duplication",
                            _ => "精确坐标硬编码"
                        }
                        : "兼容路径";
                    captureLog(
                        "Windows Graphics Capture " +
                        (start.UsedVerifiedEncoderFastPath
                            ? "已验证快路"
                            : "首帧验证") +
                        $"失败（{adapterDescription}），" +
                        (skipRemainingWgc
                            ? "首帧截止已到，跳过其余 WGC 候选并"
                            : string.Empty) +
                        $"立即切换{nextPath}；" +
                        $"耗时 {Stopwatch.GetElapsedTime(wgcProbeStartedAt).TotalMilliseconds:F0}ms。" +
                        $" 详情：{start.FailureDetail}");
                    continue;
                }

                if (attemptOptions.Backend ==
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0)
                {
                    if (desktopDuplicationCircuitBreaker
                        .TryBeginProbe(
                            out TimeSpan retryAfter))
                    {
                        long ddaProbeStartedAt =
                            Stopwatch.GetTimestamp();
                        FfmpegDesktopH264CaptureStartResult start;
                        try
                        {
                            start =
                                await FfmpegDesktopH264Capture
                                    .TryStartAsync(
                                        attemptOptions,
                                        probeCancellation.Token)
                                    .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (probeCancellation
                                    .IsCancellationRequested &&
                                !cancellationToken
                                    .IsCancellationRequested)
                        {
                            _ = desktopDuplicationCircuitBreaker
                                .RecordFailure();
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            desktopDuplicationCircuitBreaker
                                .CancelProbe();
                            throw;
                        }
                        catch
                        {
                            desktopDuplicationCircuitBreaker
                                .CancelProbe();
                            throw;
                        }

                        if (start.Started)
                        {
                            desktopDuplicationCircuitBreaker
                                .RecordStartupSuccess();
                            return start;
                        }

                        lastFailure = start;
                        TimeSpan breakDuration =
                            desktopDuplicationCircuitBreaker
                                .RecordFailure();
                        captureLog(
                            "DXGI Desktop Duplication " +
                            (start.UsedVerifiedEncoderFastPath
                                ? "已验证快路"
                                : "首次硬编码发现") +
                            "失败，立即切换精确坐标硬编码；" +
                            $"耗时 {Stopwatch.GetElapsedTime(ddaProbeStartedAt).TotalMilliseconds:F0}ms，" +
                            $"DDA 恢复探测将在 {breakDuration.TotalSeconds:F0}s 后开放。" +
                            $" 详情：{start.FailureDetail}");
                    }
                    else
                    {
                        string retryDescription =
                            retryAfter > TimeSpan.Zero
                                ? $"约 {Math.Ceiling(retryAfter.TotalMilliseconds):F0}ms 后"
                                : "当前探测结束后";
                        captureLog(
                            "DXGI Desktop Duplication 暂处于快速熔断，" +
                            $"将在 {retryDescription}恢复探测；" +
                            "本次直接使用精确坐标硬编码，避免重连黑屏。");
                    }

                    continue;
                }

                FfmpegDesktopH264CaptureStartResult compatibilityStart =
                    await FfmpegDesktopH264Capture.TryStartAsync(
                        attemptOptions,
                        probeCancellation.Token)
                    .ConfigureAwait(false);
                return compatibilityStart;
            }

            return lastFailure ??
                new FfmpegDesktopH264CaptureStartResult(
                    Capture: null,
                    "No Windows H.264 capture path was eligible.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                "硬编码执行探测超过 " +
                $"{HardwareH264ProbeExecutionDeadline.TotalMilliseconds:0}ms；" +
                "已预留快速进程回收时间，" +
                $"总墙钟预算为 {HardwareH264ProbeDeadline.TotalMilliseconds:0}ms。");
        }
    }

    private static bool IsH264SelectionCurrent(
        CaptureSessionState captureState,
        ViewerSessionState viewerState,
        int targetVersion,
        int codecVersion)
    {
        ViewerVideoSelection selection =
            viewerState.GetVideoSelection();
        return captureState.TargetVersion == targetVersion &&
            selection.Version == codecVersion &&
            selection.SupportedCodecs.HasFlag(
                RemoteVideoCodecs.H264AnnexB);
    }

    internal static bool ShouldCancelH264Startup(
        Func<bool> refreshCaptureBounds,
        Func<bool> isSelectionCurrent)
    {
        ArgumentNullException.ThrowIfNull(refreshCaptureBounds);
        ArgumentNullException.ThrowIfNull(isSelectionCurrent);

        // Display topology can change while FFmpeg is still probing hardware.
        // Refresh first so a stale target version cannot keep an old-size
        // H.264 process alive for the full startup deadline. Short-circuiting
        // also avoids consulting a selection snapshot after the refresh has
        // already made it obsolete.
        return refreshCaptureBounds() || !isSelectionCurrent();
    }

    internal static async ValueTask<(TFrame? Frame, bool SelectionChanged)>
        ReadH264FrameUntilSelectionChangesAsync<TFrame>(
            Func<CancellationToken, ValueTask<TFrame?>> readFrameAsync,
            CancellationToken captureSelectionToken,
            CancellationToken sessionCancellationToken)
        where TFrame : class
    {
        ArgumentNullException.ThrowIfNull(readFrameAsync);
        try
        {
            TFrame? frame = await readFrameAsync(
                    captureSelectionToken)
                .ConfigureAwait(false);
            return (frame, SelectionChanged: false);
        }
        catch (OperationCanceledException)
            when (captureSelectionToken.IsCancellationRequested &&
                !sessionCancellationToken.IsCancellationRequested)
        {
            return (Frame: null, SelectionChanged: true);
        }
    }

    internal static CancellationToken GetGenerationChangeToken(
        object syncRoot,
        Func<int> readCurrentVersion,
        int expectedVersion,
        Func<CancellationToken> readChangeToken)
    {
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(readCurrentVersion);
        ArgumentNullException.ThrowIfNull(readChangeToken);
        lock (syncRoot)
        {
            return readCurrentVersion() == expectedVersion
                ? readChangeToken()
                : new CancellationToken(canceled: true);
        }
    }

    private static async Task
        CancelH264StartupWhenSelectionChangesAsync(
            CaptureSessionState captureState,
            ViewerSessionState viewerState,
            int targetVersion,
            int codecVersion,
            CancellationTokenSource startupCancellation,
            CancellationToken stopToken)
    {
        Func<bool> refreshCaptureBounds =
            captureState.RefreshCaptureBounds;
        Func<bool> isSelectionCurrent = () =>
            IsH264SelectionCurrent(
                captureState,
                viewerState,
                targetVersion,
                codecVersion);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                if (ShouldCancelH264Startup(
                        refreshCaptureBounds,
                        isSelectionCurrent))
                {
                    startupCancellation.Cancel();
                    return;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    stopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (stopToken.IsCancellationRequested)
        {
        }
    }

    internal static async Task MonitorStaticH264CaptureTargetAsync(
        Func<bool> refreshCaptureBounds,
        Func<bool> isSelectionCurrent,
        Action cancelSelection,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(refreshCaptureBounds);
        ArgumentNullException.ThrowIfNull(isSelectionCurrent);
        ArgumentNullException.ThrowIfNull(cancelSelection);
        ArgumentNullException.ThrowIfNull(log);
        delayAsync ??= static (delay, token) =>
            Task.Delay(delay, token);

        int consecutiveRefreshFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                isSelectionCurrent())
            {
                await delayAsync(
                        StaticH264CaptureTargetPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested ||
                    !isSelectionCurrent())
                {
                    return;
                }

                try
                {
                    if (refreshCaptureBounds())
                    {
                        return;
                    }

                    if (consecutiveRefreshFailures != 0)
                    {
                        consecutiveRefreshFailures = 0;
                        TryLogStaticH264CaptureTargetMonitor(
                            log,
                            "静态 WGC 目标刷新已恢复。");
                    }
                }
                catch (Exception ex) when (
                    IsRecoverableStaticH264CaptureTargetException(ex))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    consecutiveRefreshFailures++;
                    if (consecutiveRefreshFailures <
                        StaticH264CaptureTargetRefreshFailureLimit)
                    {
                        if (consecutiveRefreshFailures == 1)
                        {
                            TryLogStaticH264CaptureTargetMonitor(
                                log,
                                "静态 WGC 目标刷新暂时失败，" +
                                "将在监控周期内重试：" +
                                ex.Message);
                        }

                        continue;
                    }

                    CancelStaticH264CaptureSelection(
                        cancelSelection,
                        log,
                        "静态 WGC 目标刷新连续失败 " +
                        $"{consecutiveRefreshFailures} 次，" +
                        "已取消当前采集并重建：" +
                        ex.Message);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A monitor fault must never be left unobserved while the main
            // loop remains blocked waiting for a changed WGC frame. Force the
            // selection read to unwind so the outer capture loop can rebuild.
            CancelStaticH264CaptureSelection(
                cancelSelection,
                log,
                "静态 WGC 目标监控异常，已取消当前采集并重建：" +
                ex.Message);
        }
    }

    private static bool
        IsRecoverableStaticH264CaptureTargetException(Exception ex) =>
            ex is ArgumentException or
                InvalidOperationException or
                OutOfMemoryException or
                System.ComponentModel.Win32Exception or
                System.Runtime.InteropServices.ExternalException;

    private static void CancelStaticH264CaptureSelection(
        Action cancelSelection,
        Action<string> log,
        string message)
    {
        try
        {
            cancelSelection();
        }
        catch (Exception ex)
        {
            message += "；取消选择时发生异常：" + ex.Message;
        }

        TryLogStaticH264CaptureTargetMonitor(log, message);
    }

    private static void TryLogStaticH264CaptureTargetMonitor(
        Action<string> log,
        string message)
    {
        try
        {
            log(message);
        }
        catch
        {
            // Diagnostics cannot be allowed to orphan the static-source
            // monitor or prevent the capture-selection cancellation above.
        }
    }

    internal static async Task<DdaRecoveryProbeAttempt<
        TCapture,
        TFrame>> CompleteDdaRecoveryProbeAsync<TCapture, TFrame>(
            DesktopDuplicationCircuitBreaker
                desktopDuplicationCircuitBreaker,
            Func<CancellationToken,
                    Task<DdaRecoveryProbeStart<TCapture>>>
                startCaptureAsync,
            Func<TCapture, CancellationToken, ValueTask<TFrame?>>
                readRecoveryFrameAsync,
            Func<TFrame, bool> isIndependentRecoveryFrame,
            Action<TFrame> disposeFrame,
            Action<TCapture> disposeCapture,
            CancellationToken probeCancellationToken,
            CancellationToken sessionCancellationToken)
        where TCapture : class
        where TFrame : class
    {
        ArgumentNullException.ThrowIfNull(
            desktopDuplicationCircuitBreaker);
        ArgumentNullException.ThrowIfNull(startCaptureAsync);
        ArgumentNullException.ThrowIfNull(
            readRecoveryFrameAsync);
        ArgumentNullException.ThrowIfNull(
            isIndependentRecoveryFrame);
        ArgumentNullException.ThrowIfNull(disposeFrame);
        ArgumentNullException.ThrowIfNull(disposeCapture);

        TCapture? candidate = null;
        TFrame? recoveryFrame = null;
        bool captureOwnershipTransferred = false;
        bool frameOwnershipTransferred = false;
        try
        {
            DdaRecoveryProbeStart<TCapture> start =
                await startCaptureAsync(
                        probeCancellationToken)
                    .ConfigureAwait(false);
            candidate = start.Capture;
            if (candidate is null)
            {
                TimeSpan retryAfter =
                    desktopDuplicationCircuitBreaker
                        .RecordFailure();
                return new DdaRecoveryProbeAttempt<
                    TCapture,
                    TFrame>(
                    Capture: null,
                    RecoveryFrame: null,
                    string.IsNullOrWhiteSpace(
                        start.FailureDetail)
                        ? "DDA 硬编码探针未返回可用捕获。"
                        : start.FailureDetail,
                    retryAfter);
            }

            recoveryFrame =
                await readRecoveryFrameAsync(
                        candidate,
                        probeCancellationToken)
                    .ConfigureAwait(false);
            if (recoveryFrame is null ||
                !isIndependentRecoveryFrame(
                    recoveryFrame))
            {
                TimeSpan retryAfter =
                    desktopDuplicationCircuitBreaker
                        .RecordFailure();
                return new DdaRecoveryProbeAttempt<
                    TCapture,
                    TFrame>(
                    Capture: null,
                    RecoveryFrame: null,
                    recoveryFrame is null
                        ? "DDA 探针在启动后未保留首个恢复帧。"
                        : "DDA 探针首帧不是可独立解码的恢复帧。",
                    retryAfter);
            }

            desktopDuplicationCircuitBreaker
                .RecordStartupSuccess();
            captureOwnershipTransferred = true;
            frameOwnershipTransferred = true;
            return new DdaRecoveryProbeAttempt<
                TCapture,
                TFrame>(
                candidate,
                recoveryFrame,
                FailureDetail: string.Empty,
                RetryAfter: TimeSpan.Zero);
        }
        catch (OperationCanceledException)
            when (!sessionCancellationToken
                .IsCancellationRequested)
        {
            TimeSpan retryAfter =
                desktopDuplicationCircuitBreaker
                    .RecordFailure();
            return new DdaRecoveryProbeAttempt<
                TCapture,
                TFrame>(
                Capture: null,
                RecoveryFrame: null,
                "DDA 恢复探针超过单次执行时限。",
                retryAfter);
        }
        catch
        {
            desktopDuplicationCircuitBreaker.CancelProbe();
            throw;
        }
        finally
        {
            try
            {
                if (!frameOwnershipTransferred &&
                    recoveryFrame is not null)
                {
                    disposeFrame(recoveryFrame);
                }
            }
            finally
            {
                if (!captureOwnershipTransferred &&
                    candidate is not null)
                {
                    disposeCapture(candidate);
                }
            }
        }
    }

    internal static Task RetireH264CaptureAsync<TCapture>(
        TCapture capture,
        Action<TCapture> disposeCapture)
        where TCapture : class
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(disposeCapture);

        // FFmpeg cleanup can legitimately spend several seconds waiting for a
        // graceful exit and a forced process-tree reap. Run it on a dedicated
        // worker so a retained DDA recovery frame enters the send path without
        // waiting for the superseded GDI process to exit.
        return Task.Factory.StartNew(
            () => disposeCapture(capture),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach |
                TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static async Task<H264CaptureLoopResult>
        RunHardwareH264CaptureLoopAsync(
            NetworkStream stream,
            SecureSession session,
            SessionWritePriority writePriority,
            CaptureSessionState captureState,
            CaptureTargetPublicationCoordinator
                captureTargetPublicationCoordinator,
            ViewerSessionState viewerState,
            int expectedTargetVersion,
            int expectedCodecVersion,
            int scalePercent,
            int fps,
            int jpegQuality,
            bool adaptiveQuality,
            AdaptiveH264ResolutionController resolutionController,
            LowLatencyVideoHostTransport lowLatencyVideo,
            RemoteInteractionActivity interactionActivity,
            DesktopDuplicationCircuitBreaker
                desktopDuplicationCircuitBreaker,
            bool suppressWindowsGraphicsCapture,
            Action<string> captureLog,
            CancellationToken cancellationToken)
    {
        var snapshot = captureState.GetTargetSnapshot();
        ViewerVideoSelection sourceSelection =
            viewerState.GetVideoSelection();
        if (snapshot.Version != expectedTargetVersion ||
            !snapshot.IsAvailable ||
            sourceSelection.Version != expectedCodecVersion)
        {
            return new H264CaptureLoopResult(
                H264CaptureLoopExit.SelectionChanged,
                TimeSpan.Zero);
        }

        int sourceCodecVersion = sourceSelection.Version;
        int sourceScalePercent = ChooseInitialAdaptiveScale(
            snapshot.Bounds,
            scalePercent,
            adaptiveQuality);
        Size requestedOutputSize = resolutionController.OutputSize;
        IReadOnlyList<WindowsGraphicsCaptureTarget>
            graphicsCaptureTargets = [];
        WindowsDesktopDuplicationTarget?
            desktopDuplicationTarget = null;
        if (!snapshot.Target.IsAllScreens)
        {
            if (!WindowsGraphicsCaptureTargetResolver
                .TryResolveCandidates(
                    snapshot.Target.Id,
                    snapshot.Bounds,
                    out graphicsCaptureTargets,
                    out desktopDuplicationTarget,
                    out string graphicsCaptureFailure))
            {
                captureLog(
                    "Windows 显示器 DXGI 映射不可用，" +
                    "本次使用兼容捕获链路。" +
                    $" 详情：{graphicsCaptureFailure}");
            }
        }

        bool canUseDesktopDuplication =
            CanUseDesktopDuplication(
                snapshot.Target,
                desktopDuplicationTarget);
        bool canUseWindowsGraphicsCapture =
            !suppressWindowsGraphicsCapture &&
            IsWindowsGraphicsCaptureScaleMode(
                sourceScalePercent) &&
            graphicsCaptureTargets.Count > 0 &&
            CanUseNativeWindowsGraphicsCapture(
                snapshot.Target,
                sourceScalePercent,
                requestedOutputSize,
                graphicsCaptureTargets[0], resolutionController.IsReduced);
        int sourceFramesPerSecond =
            InteractiveH264FrameController
                .CalculateSourceFramesPerSecond(
                    fps,
                    adaptiveQuality,
                    viewerState.Capabilities,
                    supportsGpuSurfaceCapture:
                        canUseWindowsGraphicsCapture ||
                        canUseDesktopDuplication);
        sourceFramesPerSecond =
            LimitInteractiveH264SourceFramesPerSecond(
                requestedOutputSize,
                fps,
                sourceFramesPerSecond);
        bool useShortGop = ShouldUseShortGopH264(
            viewerState.Capabilities,
            fps,
            sourceFramesPerSecond);
        sourceScalePercent = ChooseInteractiveH264Scale(
            snapshot.Bounds,
            sourceScalePercent,
            fps,
            sourceFramesPerSecond);
        Size outputSize = resolutionController.OutputSize;
        canUseWindowsGraphicsCapture =
            canUseWindowsGraphicsCapture &&
            CanUseNativeWindowsGraphicsCapture(
                snapshot.Target,
                sourceScalePercent,
                outputSize,
                graphicsCaptureTargets[0], resolutionController.IsReduced);
        if (!canUseWindowsGraphicsCapture)
        {
            graphicsCaptureTargets = [];
        }

        FfmpegDesktopCaptureBackend backend =
            canUseWindowsGraphicsCapture
                ? FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor
                : canUseDesktopDuplication
                    ? FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0
                    : FfmpegDesktopCaptureBackend
                        .GdiGrabBounds;
        var options = new FfmpegDesktopH264CaptureOptions(
            backend,
            snapshot.Bounds,
            outputSize,
            sourceFramesPerSecond,
            useShortGop
                ? FfmpegDesktopH264Capture.ShortGopLength
                : 1,
            BitrateFramesPerSecond:
                useShortGop
                    ? fps
                    : null,
            GraphicsCaptureTarget:
                backend == FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor
                    ? graphicsCaptureTargets[0]
                    : null,
            DesktopDuplicationTarget:
                canUseDesktopDuplication
                    ? desktopDuplicationTarget
                    : null,
            // WGC is change-driven and may legitimately emit no encoded
            // frames while the desktop is completely static. A negotiated,
            // independently authenticated UDP heartbeat lets the viewer
            // distinguish that source silence from route failure, so keep
            // monitoring the FFmpeg process/pipe without killing a healthy
            // WGC source solely because no pixels changed.
            AllowStaticFrameSilence:
                ShouldAllowStaticWgcFrameSilence(
                    backend,
                    viewerState.Capabilities));
        int nominalEncoderBitrate =
            FfmpegDesktopH264Capture
                .CalculateBitrateBitsPerSecond(
                    outputSize,
                    options.BitrateFramesPerSecond ??
                        options.FramesPerSecond);
        lowLatencyVideo.ConfigureVideoRateBudget(
            nominalEncoderBitrate,
            options.FramesPerSecond);

        using var startupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        using var startupMonitorStop =
            new CancellationTokenSource();
        Task startupMonitor =
            CancelH264StartupWhenSelectionChangesAsync(
                captureState,
                viewerState,
                snapshot.Version,
                sourceCodecVersion,
                startupCancellation,
                startupMonitorStop.Token);
        Task<FfmpegDesktopH264CaptureStartResult> startupTask =
            StartHardwareH264CaptureWithFallbackAsync(
                options,
                graphicsCaptureTargets,
                fps,
                desktopDuplicationCircuitBreaker,
                captureLog,
                startupCancellation.Token);

        FfmpegDesktopH264CaptureStartResult start;
        try
        {
            if (sourceSelection.SupportedCodecs.HasFlag(
                    RemoteVideoCodecs.Jpeg))
            {
                captureLog(
                    "正在后台探测 Windows H.264 硬编码；TCP 先发送一张 JPEG 预览，等待硬编码出图。");
                await RunJpegCaptureLoopAsync(
                    stream,
                    session,
                    writePriority,
                    captureState,
                    captureTargetPublicationCoordinator,
                    scalePercent,
                    fps,
                    jpegQuality,
                    viewerState.Capabilities,
                    adaptiveQuality,
                    lowLatencyVideo,
                    () =>
                    {
                        return startupTask.IsCompleted ||
                            !IsH264SelectionCurrent(
                                captureState,
                                viewerState,
                                snapshot.Version,
                                sourceCodecVersion);
                    },
                    captureLog,
                    cancellationToken,
                    startupPreviewOnly: true);
            }

            start = await startupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                !IsH264SelectionCurrent(
                    captureState,
                    viewerState,
                    snapshot.Version,
                    sourceCodecVersion))
        {
            return new H264CaptureLoopResult(
                H264CaptureLoopExit.SelectionChanged,
                TimeSpan.Zero);
        }
        catch
        {
            startupCancellation.Cancel();
            if (!startupTask.IsCompleted)
            {
                try
                {
                    await startupTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            throw;
        }
        finally
        {
            startupMonitorStop.Cancel();
            await startupMonitor.ConfigureAwait(false);
        }

        if (start.Capture is not { } capture)
        {
            captureLog(
                "Windows H.264 硬编码不可用，回退 JPEG：" +
                start.FailureDetail);
            return new H264CaptureLoopResult(
                H264CaptureLoopExit.StartupUnavailable,
                TimeSpan.Zero);
        }

        FfmpegDesktopH264Capture activeCapture = capture;
        CancellationTokenSource? ddaProbeLifetimeCancellation =
            null;
        CancellationTokenSource? ddaProbeExecutionCancellation =
            null;
        Task<DdaRecoveryProbeAttempt<
            FfmpegDesktopH264Capture,
            FfmpegDesktopH264Frame>>? ddaRecoveryProbeTask =
                null;
        bool ownsDdaRecoveryProbe = false;
        var retiredCaptureCleanupTasks =
            new List<Task>();
        FfmpegDesktopH264Frame? retainedRecoveryFrame = null;
        CancellationTokenSource? captureSelectionCancellation = null;
        Task? staticCaptureTargetMonitor = null;
        try
        {
            long activeRunStartedAt =
                Stopwatch.GetTimestamp();
            bool stableDdaRunRecorded =
                activeCapture.Backend !=
                    FfmpegDesktopCaptureBackend
                        .DesktopDuplicationOutput0;
            int activeSourceFramesPerSecond =
                activeCapture.FramesPerSecond;
            lowLatencyVideo.ConfigureVideoRateBudget(
                FfmpegDesktopH264Capture
                    .CalculateBitrateBitsPerSecond(
                        outputSize,
                        options.BitrateFramesPerSecond is int
                            bitrateFramesPerSecond
                                ? Math.Min(
                                    bitrateFramesPerSecond,
                                    activeSourceFramesPerSecond)
                                : activeSourceFramesPerSecond),
                activeSourceFramesPerSecond);
            if (!IsH264SelectionCurrent(
                    captureState,
                    viewerState,
                    snapshot.Version,
                    sourceCodecVersion) ||
                !captureState.TryObserveEncodedFrame(
                    snapshot.Version,
                    snapshot.Bounds,
                    outputSize,
                    sourceScalePercent))
            {
                return new H264CaptureLoopResult(
                    H264CaptureLoopExit.SelectionChanged,
                    Stopwatch.GetElapsedTime(
                        activeRunStartedAt));
            }

            captureLog(
                "Windows 低延迟 H.264 已启用：" +
                $"{activeCapture.BackendName} → {activeCapture.EncoderName}，" +
                $"{outputSize.Width}x{outputSize.Height} @ " +
                $"{activeSourceFramesPerSecond} FPS；FFmpeg " +
                $"{activeCapture.ExecutablePath}；" +
                (options.GopLength ==
                    FfmpegDesktopH264Capture.ShortGopLength
                        ? "严格 GOP2（IDR/P）、无 B 帧、无 lookahead、清晰关键帧 VBV。"
                        : "兼容 GOP1（全 IDR）、无 B 帧、无 lookahead、清晰关键帧 VBV。"));
            var interactiveFrameController =
                new InteractiveH264FrameController(
                    Math.Min(
                        fps,
                        activeSourceFramesPerSecond),
                    activeSourceFramesPerSecond);
            if (interactiveFrameController.CanBoost)
            {
                captureLog(
                    "交互升帧已待命：空闲按配置 " +
                    $"{fps} FPS，输入期间最高 " +
                    $"{activeSourceFramesPerSecond} FPS；仅在 UDP 反馈健康时升帧，" +
                    "拥塞立即回落。");
            }

            int sourceTargetVersion = snapshot.Version;
            captureSelectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    captureState.GetTargetChangeToken(
                        sourceTargetVersion),
                    viewerState.GetVideoSelectionChangeToken(
                        sourceCodecVersion));
            if (options.AllowStaticFrameSilence)
            {
                staticCaptureTargetMonitor =
                    MonitorStaticH264CaptureTargetAsync(
                        captureState.RefreshCaptureBounds,
                        () =>
                            captureState.TargetVersion ==
                            sourceTargetVersion,
                        captureSelectionCancellation.Cancel,
                        captureLog,
                        captureSelectionCancellation.Token);
            }
            int observedKeyFrameRequestVersion =
                viewerState.KeyFrameRequestVersion;
            bool dependentFrameAllowed = false;
            bool tcpDependentFrameAllowed = false;
            bool awaitingRecoveryHandoff = true;
            int framesInWindow = 0;
            long bytesInWindow = 0;
            double sourceAgeMillisecondsInWindow = 0;
            double sendMillisecondsInWindow = 0;
            var tcpWriteTimings = new ProtocolFrameWriteTimingsWindow();
            var sourceIntervalMillisecondsInWindow =
                new List<double>(
                    Math.Max(16, activeSourceFramesPerSecond * 2));
            long lastSourceProducedAt = 0;
            long lastStaleFrameLogAt = 0;
            TimeSpan maximumSourceAge =
                CalculateMaximumH264SourceAge(
                    activeSourceFramesPerSecond);
            long metricsWindowStartedAt = Stopwatch.GetTimestamp();
            long networkObservationStartedAt =
                metricsWindowStartedAt;
            LowLatencyVideoNetworkSnapshot
                latestNetworkSnapshot = default;
            long inputDesktopCheckedAt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (inputDesktopCheckedAt == 0 || Stopwatch.GetElapsedTime(inputDesktopCheckedAt) >= TimeSpan.FromMilliseconds(200))
                {
                    inputDesktopCheckedAt = Stopwatch.GetTimestamp();
                    if (!WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable)
                    {
                        // DDA/WGC remains attached to Default. Re-enter the outer
                        // loop so the protected desktop can use its JPEG bridge.
                        return new H264CaptureLoopResult(H264CaptureLoopExit.SelectionChanged,
                            Stopwatch.GetElapsedTime(activeRunStartedAt));
                    }
                }
                if (!IsH264SelectionCurrent(
                        captureState,
                        viewerState,
                        sourceTargetVersion,
                        sourceCodecVersion))
                {
                    return new H264CaptureLoopResult(
                        H264CaptureLoopExit.SelectionChanged,
                        Stopwatch.GetElapsedTime(
                            activeRunStartedAt));
                }

                if (captureState.RefreshCaptureBounds())
                {
                    return new H264CaptureLoopResult(
                        H264CaptureLoopExit.SelectionChanged,
                        Stopwatch.GetElapsedTime(
                            activeRunStartedAt));
                }

                if (ShouldStartDdaRecoveryProbe(
                        ddaRecoveryProbeTask is not null,
                        canUseDesktopDuplication,
                        activeCapture.Backend) &&
                    desktopDuplicationCircuitBreaker
                        .TryBeginProbe(out _))
                {
                    ownsDdaRecoveryProbe = true;
                    ddaProbeLifetimeCancellation =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                cancellationToken);
                    ddaProbeExecutionCancellation =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(
                                ddaProbeLifetimeCancellation
                                    .Token);
                    ddaProbeExecutionCancellation.CancelAfter(
                        HardwareH264ProbeExecutionDeadline);
                    CancellationToken probeToken =
                        ddaProbeExecutionCancellation.Token;
                    CancellationToken probeLifetimeToken =
                        ddaProbeLifetimeCancellation.Token;
                    FfmpegDesktopH264CaptureOptions
                        ddaRecoveryOptions = options with
                        {
                            Backend =
                                FfmpegDesktopCaptureBackend
                                    .DesktopDuplicationOutput0,
                            GraphicsCaptureTarget = null
                        };
                    ddaRecoveryProbeTask =
                        CompleteDdaRecoveryProbeAsync<
                            FfmpegDesktopH264Capture,
                            FfmpegDesktopH264Frame>(
                            desktopDuplicationCircuitBreaker,
                            async token =>
                            {
                                FfmpegDesktopH264CaptureStartResult
                                    probeStart =
                                        await FfmpegDesktopH264Capture
                                            .TryStartAsync(
                                                ddaRecoveryOptions,
                                                token)
                                            .ConfigureAwait(false);
                                return new DdaRecoveryProbeStart<
                                    FfmpegDesktopH264Capture>(
                                    probeStart.Capture,
                                    probeStart.FailureDetail);
                            },
                            static (candidate, token) =>
                                candidate.ReadLatestFrameAsync(
                                    token),
                            frame =>
                                frame.Flags.HasFlag(
                                    RemoteFrameFlags.KeyFrame) &&
                                frame.Flags.HasFlag(
                                    RemoteFrameFlags
                                        .CodecConfig) &&
                                frame.Width ==
                                    outputSize.Width &&
                                frame.Height ==
                                    outputSize.Height,
                            static frame =>
                                frame.Dispose(),
                            static candidate =>
                                candidate.Dispose(),
                            probeToken,
                            probeLifetimeToken);
                    captureLog(
                        "精确坐标 H.264 保持连续出帧；" +
                        "后台开始 DXGI Desktop Duplication 恢复探测，" +
                        "探测不会中断当前画面。");
                }

                if (ddaRecoveryProbeTask?.IsCompleted == true)
                {
                    DdaRecoveryProbeAttempt<
                        FfmpegDesktopH264Capture,
                        FfmpegDesktopH264Frame> probeAttempt;
                    try
                    {
                        probeAttempt =
                            await ddaRecoveryProbeTask
                                .ConfigureAwait(false);
                    }
                    catch
                    {
                        // The helper releases the circuit lease before
                        // propagating an unexpected/caller cancellation.
                        // Clear local ownership before this method's finally
                        // runs so it cannot cancel a newer session's probe.
                        ownsDdaRecoveryProbe = false;
                        throw;
                    }

                    ownsDdaRecoveryProbe = false;
                    ddaRecoveryProbeTask = null;
                    ddaProbeExecutionCancellation?.Dispose();
                    ddaProbeExecutionCancellation = null;
                    ddaProbeLifetimeCancellation?.Dispose();
                    ddaProbeLifetimeCancellation = null;

                    if (probeAttempt.Recovered)
                    {
                        FfmpegDesktopH264Capture recoveredCapture =
                            probeAttempt.Capture!;
                        FfmpegDesktopH264Frame retainedDdaFrame =
                            probeAttempt.RecoveryFrame!;
                        FfmpegDesktopH264Capture previousCapture =
                            activeCapture;
                        string previousPath =
                            $"{previousCapture.BackendName} → " +
                            previousCapture.EncoderName;

                        // Preserve ownership of the independently decodable
                        // frame before releasing the GDI process. The next
                        // loop iteration sends this exact recovery point first,
                        // so neither UDP nor TCP can observe a dependent frame
                        // across the capture-source handoff.
                        retainedRecoveryFrame = retainedDdaFrame;
                        activeCapture = recoveredCapture;
                        activeSourceFramesPerSecond =
                            recoveredCapture.FramesPerSecond;
                        activeRunStartedAt =
                            Stopwatch.GetTimestamp();
                        stableDdaRunRecorded = false;
                        interactiveFrameController =
                            new InteractiveH264FrameController(
                                Math.Min(
                                    fps,
                                    activeSourceFramesPerSecond),
                                activeSourceFramesPerSecond);
                        lowLatencyVideo.ConfigureVideoRateBudget(
                            FfmpegDesktopH264Capture
                                .CalculateBitrateBitsPerSecond(
                                    outputSize,
                                    options.BitrateFramesPerSecond ??
                                        activeSourceFramesPerSecond),
                            activeSourceFramesPerSecond);
                        maximumSourceAge =
                            CalculateMaximumH264SourceAge(
                                activeSourceFramesPerSecond);
                        dependentFrameAllowed = false;
                        tcpDependentFrameAllowed = false;
                        awaitingRecoveryHandoff = true;
                        framesInWindow = 0;
                        bytesInWindow = 0;
                        sourceAgeMillisecondsInWindow = 0;
                        sendMillisecondsInWindow = 0;
                        tcpWriteTimings.Reset();
                        sourceIntervalMillisecondsInWindow.Clear();
                        lastSourceProducedAt = 0;
                        lastStaleFrameLogAt = 0;
                        metricsWindowStartedAt =
                            activeRunStartedAt;
                        networkObservationStartedAt =
                            activeRunStartedAt;
                        latestNetworkSnapshot = default;

                        retiredCaptureCleanupTasks.Add(
                            RetireH264CaptureAsync(
                                previousCapture,
                                static retired =>
                                    retired.Dispose()));
                        captureLog(
                            "后台 DDA 已取得并保留首个独立恢复帧；" +
                            $"已顺序切换到 {recoveredCapture.BackendName} → " +
                            $"{recoveredCapture.EncoderName}，" +
                            $"{activeSourceFramesPerSecond} FPS，" +
                            $"旧路径 {previousPath} 正在后台清理，" +
                            "不会阻塞恢复帧发送。");
                    }
                    else
                    {
                        captureLog(
                            "后台 DDA 恢复探测未就绪；" +
                            "精确坐标 H.264 继续连续出帧，" +
                            $"将在 {probeAttempt.RetryAfter.TotalSeconds:F0}s 后重试。" +
                            $" 详情：{probeAttempt.FailureDetail}");
                    }
                }

                int keyFrameRequestVersion =
                    viewerState.KeyFrameRequestVersion;
                if (keyFrameRequestVersion !=
                    observedKeyFrameRequestVersion)
                {
                    observedKeyFrameRequestVersion =
                        keyFrameRequestVersion;
                    captureLog(
                        options.GopLength ==
                            FfmpegDesktopH264Capture.ShortGopLength
                            ? "已收到 H.264 恢复帧请求；严格 GOP2 将在不超过两帧内自动恢复。"
                            : "已收到 H.264 恢复帧请求；当前全 IDR 模式的下一帧可独立解码。");
                }

                FfmpegDesktopH264Frame? frame;
                if (retainedRecoveryFrame is not null)
                {
                    frame = retainedRecoveryFrame;
                    retainedRecoveryFrame = null;
                }
                else
                {
                    (FfmpegDesktopH264Frame? readFrame,
                        bool selectionChanged) =
                        await ReadH264FrameUntilSelectionChangesAsync(
                            activeCapture.ReadLatestFrameAsync,
                            captureSelectionCancellation.Token,
                            cancellationToken);
                    if (selectionChanged)
                    {
                        return new H264CaptureLoopResult(
                            H264CaptureLoopExit.SelectionChanged,
                            Stopwatch.GetElapsedTime(
                                activeRunStartedAt));
                    }

                    frame = readFrame;
                }

                if (frame is null)
                {
                    if (!IsH264SelectionCurrent(
                            captureState,
                            viewerState,
                            sourceTargetVersion,
                            sourceCodecVersion))
                    {
                        return new H264CaptureLoopResult(
                            H264CaptureLoopExit.SelectionChanged,
                            Stopwatch.GetElapsedTime(
                                activeRunStartedAt));
                    }

                    string detail = string.IsNullOrWhiteSpace(
                        activeCapture.FailureDetail)
                            ? "ffmpeg 捕获进程已停止或画面输出超时。"
                            : activeCapture.FailureDetail;
                    captureLog(
                        "Windows H.264 硬编码中断，回退 JPEG：" +
                        detail);
                    return CreateH264RuntimeFailureResult(
                        activeCapture,
                        activeRunStartedAt,
                        desktopDuplicationCircuitBreaker,
                        captureLog);
                }

                using FfmpegDesktopH264Frame ownedFrame =
                    frame;
                if (lastSourceProducedAt != 0 &&
                    frame.ProducedAtTimestamp > lastSourceProducedAt)
                {
                    sourceIntervalMillisecondsInWindow.Add(
                        Stopwatch.GetElapsedTime(
                            lastSourceProducedAt,
                            frame.ProducedAtTimestamp)
                            .TotalMilliseconds);
                }

                lastSourceProducedAt = frame.ProducedAtTimestamp;

                if (!IsH264SelectionCurrent(
                        captureState,
                        viewerState,
                        sourceTargetVersion,
                        sourceCodecVersion))
                {
                    return new H264CaptureLoopResult(
                        H264CaptureLoopExit.SelectionChanged,
                        Stopwatch.GetElapsedTime(
                            activeRunStartedAt));
                }

                if (activeCapture.Backend ==
                        FfmpegDesktopCaptureBackend
                            .DesktopDuplicationOutput0 &&
                    !stableDdaRunRecorded &&
                    Stopwatch.GetElapsedTime(
                        activeRunStartedAt) >=
                        StableH264RunDuration)
                {
                    stableDdaRunRecorded =
                        desktopDuplicationCircuitBreaker
                            .RecordStableRuntimeSuccess();
                    if (stableDdaRunRecorded)
                    {
                        captureLog(
                            "DXGI Desktop Duplication 已连续稳定出帧 5 秒，" +
                            "清除历史熔断计数。");
                    }
                }

                bool recoveryFrame =
                    frame.Flags.HasFlag(
                        RemoteFrameFlags.KeyFrame) &&
                    frame.Flags.HasFlag(
                        RemoteFrameFlags.CodecConfig);
                if (recoveryFrame)
                {
                    dependentFrameAllowed =
                        options.GopLength ==
                            FfmpegDesktopH264Capture
                                .ShortGopLength;
                }
                else if (options.GopLength !=
                            FfmpegDesktopH264Capture
                                .ShortGopLength ||
                         !dependentFrameAllowed)
                {
                    captureLog(
                        "Windows H.264 编码器违反了协商的 GOP 序列，" +
                        "已停止该候选以保护参考链。");
                    return CreateH264RuntimeFailureResult(
                        activeCapture,
                        activeRunStartedAt,
                        desktopDuplicationCircuitBreaker,
                        captureLog);
                }
                else
                {
                    dependentFrameAllowed = false;
                }

                TimeSpan sourceAge = frame.Age;
                if (ShouldDropH264SourceFrame(
                        sourceAge,
                        maximumSourceAge,
                        recoveryFrame,
                        awaitingRecoveryHandoff))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (lastStaleFrameLogAt == 0 ||
                        Stopwatch.GetElapsedTime(
                            lastStaleFrameLogAt) >=
                            MetricsWindow)
                    {
                        lastStaleFrameLogAt = now;
                        captureLog(
                            "丢弃超龄 H.264 源帧：" +
                            $"{sourceAge.TotalMilliseconds:F1}ms > " +
                            $"{maximumSourceAge.TotalMilliseconds:F1}ms。");
                    }

                    continue;
                }

                bool transmitFrame =
                    options.GopLength ==
                        FfmpegDesktopH264Capture
                            .ShortGopLength
                        ? interactiveFrameController
                            .ShouldTransmitGop2Frame(
                                frame.ProducedAtTimestamp,
                                interactionActivity
                                    .LastActivityAt,
                                lowLatencyVideo.IsRouteActive,
                                recoveryFrame)
                        : interactiveFrameController
                            .ShouldTransmitFrame(
                                frame.ProducedAtTimestamp,
                                interactionActivity
                                    .LastActivityAt,
                                lowLatencyVideo.IsRouteActive);
                if (!transmitFrame)
                {
                    continue;
                }

                if (!captureState.TryObserveEncodedFrame(
                        sourceTargetVersion,
                        snapshot.Bounds,
                        new Size(frame.Width, frame.Height),
                        sourceScalePercent))
                {
                    return new H264CaptureLoopResult(
                        H264CaptureLoopExit.SelectionChanged,
                        Stopwatch.GetElapsedTime(
                            activeRunStartedAt));
                }

                if (recoveryFrame)
                {
                    tcpDependentFrameAllowed = false;
                }

                long sendStartedAt = Stopwatch.GetTimestamp();
                bool queuedForUdp = false;
                bool frameSent = false;
                bool deferForControl = false;
                bool admitted =
                    await captureTargetPublicationCoordinator
                        .AdmitFrameIfCurrentAsync(
                            sourceTargetVersion,
                            captureState
                                .IsCurrentTargetGeneration,
                            async token =>
                            {
                                queuedForUdp =
                                    lowLatencyVideo
                                        .TryQueueVideoFrame(
                                            frame.Width,
                                            frame.Height,
                                            RemoteFrameEncoding
                                                .H264AnnexB,
                                            frame.Flags,
                                            captureMilliseconds: 0,
                                            encodeMilliseconds: 0,
                                            frame.AnnexBBytes,
                                            allowLatencyBudgetDrop:
                                                ShouldAllowH264LatencyBudgetDrop(
                                                    adaptiveQuality,
                                                    frame.Flags,
                                                    awaitingRecoveryHandoff));
                                if (queuedForUdp)
                                {
                                    frameSent = true;
                                    return;
                                }

                                if (options.GopLength ==
                                        FfmpegDesktopH264Capture
                                            .ShortGopLength &&
                                    !recoveryFrame &&
                                    !tcpDependentFrameAllowed)
                                {
                                    return;
                                }

                                if (writePriority
                                        .HasPendingControlWrite)
                                {
                                    deferForControl = true;
                                    return;
                                }

                                ProtocolFrameWriteTimings writeTimings = await Protocol
                                    .WriteVideoFrameMessageAsync(
                                        stream,
                                        frame.Width,
                                        frame.Height,
                                        RemoteFrameEncoding
                                            .H264AnnexB,
                                        frame.Flags,
                                        captureMilliseconds: 0,
                                        encodeMilliseconds: 0,
                                        frame.AnnexBBytes,
                                        session,
                                        writePriority.Lock,
                                        token);
                                tcpWriteTimings.Record(writeTimings);
                                frameSent = true;
                            },
                            cancellationToken);
                if (!admitted)
                {
                    return new H264CaptureLoopResult(
                        H264CaptureLoopExit.SelectionChanged,
                        Stopwatch.GetElapsedTime(
                            activeRunStartedAt));
                }

                if (!frameSent)
                {
                    if (deferForControl)
                    {
                        await Task.Delay(
                            ControlWritePriorityDelay,
                            cancellationToken);
                    }

                    continue;
                }

                if (!queuedForUdp)
                {
                    tcpDependentFrameAllowed =
                        recoveryFrame &&
                        options.GopLength ==
                            FfmpegDesktopH264Capture
                                .ShortGopLength;
                }
                else
                {
                    tcpDependentFrameAllowed = false;
                }

                awaitingRecoveryHandoff = false;

                double sendMilliseconds =
                    Stopwatch.GetElapsedTime(
                        sendStartedAt).TotalMilliseconds;
                framesInWindow++;
                bytesInWindow += frame.AnnexBBytes.Length;
                sourceAgeMillisecondsInWindow +=
                    sourceAge.TotalMilliseconds;
                sendMillisecondsInWindow += sendMilliseconds;

                if (Stopwatch.GetElapsedTime(
                        networkObservationStartedAt) >=
                    InteractiveH264FrameController
                        .NetworkObservationInterval)
                {
                    latestNetworkSnapshot =
                        lowLatencyVideo
                            .CollectNetworkSnapshot();
                    bool highFrameRateWasReduced =
                        interactiveFrameController
                            .IsHighFrameRateReduced;
                    interactiveFrameController
                        .ObserveNetworkSnapshot(
                            latestNetworkSnapshot);
                    if (highFrameRateWasReduced !=
                        interactiveFrameController
                            .IsHighFrameRateReduced)
                    {
                        captureLog(
                            interactiveFrameController
                                .IsHighFrameRateReduced
                                ? "高帧率网络连续承压，发送已从 " +
                                    $"{activeSourceFramesPerSecond} FPS " +
                                    "降到 30 FPS 保持清晰度和输入响应；" +
                                    "捕获与硬编仍保持热态。"
                                : "网络已连续稳定 2 秒，" +
                                    "高帧率发送恢复到 " +
                                    $"{activeSourceFramesPerSecond} FPS。");
                    }
                    networkObservationStartedAt =
                        Stopwatch.GetTimestamp();
                }

                TimeSpan metricsElapsed =
                    Stopwatch.GetElapsedTime(
                        metricsWindowStartedAt);
                if (metricsElapsed < MetricsWindow ||
                    framesInWindow == 0)
                {
                    continue;
                }

                double actualFps =
                    framesInWindow /
                    metricsElapsed.TotalSeconds;
                double megabitsPerSecond =
                    bytesInWindow * 8d /
                    metricsElapsed.TotalSeconds /
                    1_000_000d;
                double averageSendMilliseconds =
                    sendMillisecondsInWindow /
                    framesInWindow;
                double averageSourceAgeMilliseconds =
                    sourceAgeMillisecondsInWindow /
                    framesInWindow;
                sourceIntervalMillisecondsInWindow.Sort();
                double sourceIntervalP95Milliseconds =
                    sourceIntervalMillisecondsInWindow.Count == 0
                        ? 0
                        : sourceIntervalMillisecondsInWindow[
                            Math.Clamp(
                                (int)Math.Ceiling(
                                    sourceIntervalMillisecondsInWindow.Count *
                                    0.95d) - 1,
                                0,
                                sourceIntervalMillisecondsInWindow.Count - 1)];
                double sourceIntervalMaximumMilliseconds =
                    sourceIntervalMillisecondsInWindow.Count == 0
                        ? 0
                        : sourceIntervalMillisecondsInWindow[^1];
                LowLatencyVideoNetworkSnapshot networkSnapshot =
                    latestNetworkSnapshot;
                LowLatencyVideoSendSnapshot sendSnapshot =
                    lowLatencyVideo.CollectSendSnapshot();
                string networkMetrics =
                    networkSnapshot.HasFeedbackSample ||
                    networkSnapshot.SenderQueueDropRatio > 0 ||
                    networkSnapshot.OversizedFrameDrops > 0 ||
                    networkSnapshot.AbortedFrameSends > 0
                        ? (
                            $"，UDP 到达 " +
                            $"{networkSnapshot.DeliveryMegabitsPerSecond:F1}Mbps" +
                            $"，包丢 {networkSnapshot.PacketLossRatio:P1}" +
                            $"，帧弃 {networkSnapshot.FrameAbandonRatio:P1}" +
                            $"，发送槽丢 " +
                            $"{networkSnapshot.SenderQueueDropRatio:P1}" +
                            $"，抢占/超时 " +
                            $"{networkSnapshot.AbortedFrameSends}" +
                            $"，pacing " +
                            $"{networkSnapshot.TargetMegabitsPerSecond:F0}Mbps" +
                            (networkSnapshot.XorFecEnabled
                                ? "，XOR FEC 开"
                                : "，XOR FEC 关"))
                        : string.Empty;
                string shortGopMetrics =
                    options.GopLength ==
                        FfmpegDesktopH264Capture.ShortGopLength
                        ? (
                            $"，短GOP 已发 IDR/P " +
                            $"{sendSnapshot.SerializedShortGopRecoveryFrames}/" +
                            $"{sendSnapshot.SerializedShortGopDependentFrames}" +
                            $"，抑制 P " +
                            $"{sendSnapshot.SuppressedShortGopDependentFrames}" +
                            $"，槽替换 {sendSnapshot.ReplacedPendingFrames}")
                        : string.Empty;
                captureLog(
                    "H.264 统计：" +
                    $"{actualFps:F1} FPS，" +
                    $"{frame.Width}x{frame.Height}，" +
                    $"编码 {megabitsPerSecond:F1}Mbps，" +
                    $"源队列 {averageSourceAgeMilliseconds:F1}ms，" +
                    $"源间隔 p95/max " +
                    $"{sourceIntervalP95Milliseconds:F1}/" +
                    $"{sourceIntervalMaximumMilliseconds:F1}ms，" +
                    $"{(networkSnapshot.HasFeedbackSample ? "入队" : "发")} " +
                    $"{averageSendMilliseconds:F1}ms" +
                    (interactiveFrameController.CanBoost
                        ? "，交互档 " +
                            $"{interactiveFrameController.CurrentTransmitFramesPerSecond} FPS"
                        : string.Empty) +
                    networkMetrics +
                    shortGopMetrics +
                    tcpWriteTimings.DescribeAverage());

                if (resolutionController.Observe(metricsElapsed, actualFps,
                        tcpWriteTimings.AverageSocketWriteMilliseconds, megabitsPerSecond,
                        reliableVideo: !lowLatencyVideo.IsRouteActive && tcpWriteTimings.Count == framesInWindow))
                {
                    Size nextSize = resolutionController.OutputSize;
                    captureLog(resolutionController.IsReduced
                        ? $"带宽自适应：持续发送积压，切换 {nextSize.Width}x{nextSize.Height} 高质量 H.264；保持硬编与目标帧率，未修改系统分辨率。"
                        : $"带宽自适应：发送积压已持续缓解，尝试恢复 {nextSize.Width}x{nextSize.Height}；若再次拥塞，本次连接将保持流畅档，避免反复卡顿。");
                    return new H264CaptureLoopResult(H264CaptureLoopExit.ResolutionChanged,
                        Stopwatch.GetElapsedTime(activeRunStartedAt));
                }

                framesInWindow = 0;
                bytesInWindow = 0;
                sourceAgeMillisecondsInWindow = 0;
                sendMillisecondsInWindow = 0;
                tcpWriteTimings.Reset();
                sourceIntervalMillisecondsInWindow.Clear();
                metricsWindowStartedAt =
                    Stopwatch.GetTimestamp();
            }
        }
        finally
        {
            try
            {
                captureSelectionCancellation?.Cancel();
                if (staticCaptureTargetMonitor is not null)
                {
                    await staticCaptureTargetMonitor
                        .ConfigureAwait(false);
                }

                ddaProbeLifetimeCancellation?.Cancel();
                if (ddaRecoveryProbeTask is not null)
                {
                    try
                    {
                        DdaRecoveryProbeAttempt<
                            FfmpegDesktopH264Capture,
                            FfmpegDesktopH264Frame> abandonedProbe =
                                await ddaRecoveryProbeTask
                                    .ConfigureAwait(false);
                        abandonedProbe.RecoveryFrame?.Dispose();
                        abandonedProbe.Capture?.Dispose();
                    }
                    catch (OperationCanceledException)
                        when (ddaProbeLifetimeCancellation?
                                .IsCancellationRequested ==
                            true)
                    {
                    }
                    finally
                    {
                        // A completed helper has already closed this lease
                        // through success, failure, or cancellation. Clear
                        // local ownership before another session can acquire
                        // the shared circuit's half-open probe.
                        ownsDdaRecoveryProbe = false;
                    }
                }
            }
            finally
            {
                if (ownsDdaRecoveryProbe)
                {
                    desktopDuplicationCircuitBreaker.CancelProbe();
                }

                ddaProbeExecutionCancellation?.Dispose();
                ddaProbeLifetimeCancellation?.Dispose();
                captureSelectionCancellation?.Dispose();
                retainedRecoveryFrame?.Dispose();
                retainedRecoveryFrame = null;
                activeCapture.Dispose();
                if (retiredCaptureCleanupTasks.Count > 0)
                {
                    await Task.WhenAll(retiredCaptureCleanupTasks)
                        .ConfigureAwait(false);
                }
            }
        }

        return new H264CaptureLoopResult(
            H264CaptureLoopExit.SelectionChanged,
            TimeSpan.Zero);
    }

    private static H264CaptureLoopResult
        CreateH264RuntimeFailureResult(
            FfmpegDesktopH264Capture capture,
            long activeRunStartedAt,
            DesktopDuplicationCircuitBreaker
                desktopDuplicationCircuitBreaker,
            Action<string> captureLog)
    {
        TimeSpan activeDuration =
            Stopwatch.GetElapsedTime(activeRunStartedAt);
        if (ShouldRecordDdaRuntimeFailure(
                capture.Backend,
                activeDuration))
        {
            TimeSpan breakDuration =
                desktopDuplicationCircuitBreaker
                    .RecordRuntimeFailure();
            captureLog(
                "DXGI Desktop Duplication 在稳定门限前中断；" +
                $"有效运行 {activeDuration.TotalMilliseconds:F0}ms，" +
                "下一次硬编码重启将直接使用精确坐标路径，" +
                $"DDA 恢复探测将在 {breakDuration.TotalSeconds:F0}s 后开放。");
        }

        return new H264CaptureLoopResult(
            H264CaptureLoopExit.RuntimeFailed,
            activeDuration,
            capture.Backend);
    }

    internal static bool SupportsSecureDesktopJpegRecovery(
        RemoteVideoCodecs codecs, RemoteDeviceCapabilities capabilities) =>
        codecs.HasFlag(RemoteVideoCodecs.Jpeg) ||
        // Older Windows clients retain a JPEG decoder even in their removed
        // "H.264 only" UI mode and explicitly advertise HighQualityJpeg.
        // Use that declaration only for a non-interactive/secure desktop;
        // the normal desktop still honors their H.264 preference.
        (codecs.HasFlag(RemoteVideoCodecs.H264AnnexB) &&
         capabilities.HasFlag(RemoteDeviceCapabilities.HighQualityJpeg));

    internal static bool ShouldGateUnchangedReliableJpeg(bool udpRouteActive) => !udpRouteActive;

    internal static bool ShouldFinishJpegStartupPreview(
        bool startupPreviewOnly,
        bool udpRouteActive) =>
        startupPreviewOnly && !udpRouteActive;

    private static async Task RunJpegCaptureLoopAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        CaptureSessionState captureState,
        CaptureTargetPublicationCoordinator
            captureTargetPublicationCoordinator,
        int scalePercent,
        int fps,
        int jpegQuality,
        RemoteDeviceCapabilities viewerCapabilities,
        bool adaptiveQuality,
        LowLatencyVideoHostTransport lowLatencyVideo,
        Func<bool> shouldSwitchToH264,
        Action<string> captureLog,
        CancellationToken cancellationToken,
        bool startupPreviewOnly = false)
    {
        int initialScalePercent = ChooseInitialAdaptiveScale(
            captureState.CurrentTarget.Bounds,
            scalePercent,
            adaptiveQuality);
        int negotiatedJpegQuality = ResolveNegotiatedJpegQuality(
            jpegQuality,
            viewerCapabilities);
        bool highQualityJpeg = viewerCapabilities.HasFlag(
            RemoteDeviceCapabilities.HighQualityJpeg);
        var adaptiveController = new AdaptiveCaptureController(
            fps,
            negotiatedJpegQuality,
            scalePercent,
            adaptiveQuality,
            initialScalePercent,
            minimumQuality: highQualityJpeg
                ? HighQualityJpegMinimumQuality
                : null);
        int consecutiveCaptureFailures = 0;
        bool captureUnavailablePublished = false;
        var unchangedFrames = new UnchangedJpegFrameGate();
        int suppressedFramesInWindow = 0;
        int framesInWindow = 0;
        long bytesInWindow = 0;
        double captureMillisecondsInWindow = 0;
        double encodeMillisecondsInWindow = 0;
        double sendMillisecondsInWindow = 0;
        double frameMillisecondsInWindow = 0;
        var tcpWriteTimings = new ProtocolFrameWriteTimingsWindow();
        long metricsWindowStartedAt = Stopwatch.GetTimestamp();

        if (adaptiveQuality)
        {
            captureLog(
                $"自适应画质/帧率已开启，目标 {fps} FPS，" +
                $"最高 JPEG {negotiatedJpegQuality}，分辨率上限 " +
                $"{ScreenCaptureService.FormatScaleMode(scalePercent)}。");
        }

        if (highQualityJpeg && negotiatedJpegQuality > jpegQuality)
        {
            captureLog(
                $"查看端已启用 JPEG 清晰优先：" +
                $"本次会话画质 {jpegQuality} → " +
                $"{negotiatedJpegQuality}，自适应下限 " +
                $"{HighQualityJpegMinimumQuality}。");
        }

        await Task.Yield();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (shouldSwitchToH264())
            {
                return;
            }

            if (writePriority.HasPendingControlWrite &&
                !lowLatencyVideo.IsRouteActive)
            {
                await Task.Delay(ControlWritePriorityDelay, cancellationToken);
                continue;
            }

            long frameStartedAt = Stopwatch.GetTimestamp();
            TimeSpan frameInterval = TimeSpan.FromMilliseconds(1000d / adaptiveController.CurrentFps);
            bool secureDesktop = WindowsSecureDesktopClient.IsRequired;
            if (secureDesktop && frameInterval < TimeSpan.FromMilliseconds(100))
                frameInterval = TimeSpan.FromMilliseconds(100);
            ScreenCaptureResult capture;
            int sourceTargetGeneration;
            try
            {
                capture = captureState.CaptureJpeg(
                    adaptiveController.CurrentQuality,
                    adaptiveController.CurrentScalePercent,
                    out sourceTargetGeneration);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
            {
                consecutiveCaptureFailures++;
                // Recovery clears/requalifies the viewer's surface. Its first
                // image must be resent even if the locked desktop is unchanged.
                unchangedFrames.Reset();
                if (consecutiveCaptureFailures == 1 || consecutiveCaptureFailures % 10 == 0)
                {
                    captureLog($"屏幕采集暂时失败，稍后重试：{ex.Message}");
                }

                if (consecutiveCaptureFailures >= 2 &&
                    !captureUnavailablePublished)
                {
                    WindowsInteractiveDesktopAvailability desktop =
                        WindowsInteractiveDesktopProbe
                            .InspectCurrent();
                    string displayMessage = desktop.IsAvailable
                        ? "远端屏幕采集暂时不可用；连接仍保持，" +
                          "恢复后画面和操作会自动继续。"
                        : "远端 Windows 当前处于锁屏、UAC 或安全桌面；" +
                          "请在被控端启用“锁屏控制”并以管理员运行 RemoteDesk。" +
                          "辅助服务恢复或本机解锁后，连接会自动继续。";
                    using (writePriority
                        .BeginControlWritePriority())
                    {
                        await SendCaptureTargetAvailabilityAsync(
                            stream,
                            session,
                            writePriority.Lock,
                            captureState.GetPublicationSnapshot(),
                            isAvailable: false,
                            displayMessage,
                            cancellationToken);
                    }

                    captureUnavailablePublished = true;
                    captureLog(
                        displayMessage +
                        " 详情：" + desktop.Diagnostic);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                continue;
            }

            if (consecutiveCaptureFailures > 0)
            {
                captureLog("屏幕采集已恢复。");
                consecutiveCaptureFailures = 0;
                if (captureUnavailablePublished)
                {
                    const string displayMessage =
                        "远端交互桌面已恢复；画面和操作正在继续。";
                    using (writePriority
                        .BeginControlWritePriority())
                    {
                        await SendCaptureTargetAvailabilityAsync(
                            stream,
                            session,
                            writePriority.Lock,
                            captureState.GetPublicationSnapshot(),
                            isAvailable: true,
                            displayMessage,
                            cancellationToken);
                    }

                    captureUnavailablePublished = false;
                }
            }

            if (writePriority.HasPendingControlWrite &&
                !lowLatencyVideo.IsRouteActive)
            {
                await Task.Delay(ControlWritePriorityDelay, cancellationToken);
                continue;
            }

            // An unchanged desktop (not only a lock screen) does not need the
            // same full JPEG queued repeatedly. Compare exact encoded bytes:
            // every changed frame is still sent at its original quality, and
            // the five-second refresh preserves static-session liveness.
            // Leave UDP's negotiated feedback/recovery cadence untouched.
            bool gateUnchangedFrame = ShouldGateUnchangedReliableJpeg(lowLatencyVideo.IsRouteActive);
            if (!gateUnchangedFrame)
            {
                unchangedFrames.Reset();
            }
            else if (!unchangedFrames.ShouldSend(capture.JpegBytes.Span, capture.FrameSize,
                         sourceTargetGeneration, Environment.TickCount64))
            {
                suppressedFramesInWindow++;
                TimeSpan idleDelay = frameInterval - Stopwatch.GetElapsedTime(frameStartedAt);
                if (idleDelay > TimeSpan.Zero)
                    await Task.Delay(idleDelay, cancellationToken);
                continue;
            }

            long sendStartedAt = Stopwatch.GetTimestamp();
            bool frameSent = false;
            bool sentViaTcp = false;
            bool admitted = await captureTargetPublicationCoordinator
                .AdmitFrameIfCurrentAsync(
                    sourceTargetGeneration,
                    captureState.IsCurrentTargetGeneration,
                    async token =>
                    {
                        bool queuedForUdp =
                            lowLatencyVideo.TryQueueJpegFrame(
                                capture.FrameSize.Width,
                                capture.FrameSize.Height,
                                capture.CaptureMilliseconds,
                                capture.EncodeMilliseconds,
                                capture.JpegBytes,
                                allowLatencyBudgetDrop:
                                    adaptiveQuality);
                        if (queuedForUdp)
                        {
                            frameSent = true;
                            return;
                        }

                        // A UDP liveness check inside TryQueueJpegFrame can
                        // have just started the LowLatencyVideoStopped TCP
                        // barrier. Never let this captured frame overtake it.
                        if (writePriority.HasPendingControlWrite)
                        {
                            return;
                        }

                        ProtocolFrameWriteTimings writeTimings = await Protocol.WriteFrameMessageAsync(
                            stream,
                            capture.FrameSize.Width,
                            capture.FrameSize.Height,
                            capture.CaptureMilliseconds,
                            capture.EncodeMilliseconds,
                            capture.JpegBytes,
                            session,
                            writePriority.Lock,
                            token);
                        tcpWriteTimings.Record(writeTimings);
                        sentViaTcp = true;
                        frameSent = true;
                    },
                    cancellationToken);
            if (!admitted || !frameSent)
            {
                if (admitted &&
                    writePriority.HasPendingControlWrite)
                {
                    await Task.Delay(ControlWritePriorityDelay, cancellationToken);
                }

                continue;
            }

            if (gateUnchangedFrame && sentViaTcp)
                unchangedFrames.MarkSent(Environment.TickCount64);

            // Count only a successfully admitted/sent preview, never capture
            // failures or a frame rejected by a target/control publication
            // barrier. Normal JPEG and UDP's bounded latest-frame route keep
            // running; the caller awaits the existing cancellable H.264 probe.
            if (ShouldFinishJpegStartupPreview(
                    startupPreviewOnly,
                    lowLatencyVideo.IsRouteActive))
            {
                return;
            }

            double sendMilliseconds = Stopwatch.GetElapsedTime(sendStartedAt).TotalMilliseconds;

            framesInWindow++;
            bytesInWindow += capture.JpegBytes.Length;
            captureMillisecondsInWindow += capture.CaptureMilliseconds;
            encodeMillisecondsInWindow += capture.EncodeMilliseconds;
            sendMillisecondsInWindow += sendMilliseconds;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(frameStartedAt);
            frameMillisecondsInWindow += elapsed.TotalMilliseconds;

            TimeSpan metricsElapsed = Stopwatch.GetElapsedTime(metricsWindowStartedAt);
            if (metricsElapsed >= MetricsWindow && framesInWindow > 0)
            {
                double actualFps = framesInWindow / metricsElapsed.TotalSeconds;
                double megabitsPerSecond = bytesInWindow * 8d / metricsElapsed.TotalSeconds / 1_000_000d;
                double averageCaptureMilliseconds = captureMillisecondsInWindow / framesInWindow;
                double averageEncodeMilliseconds = encodeMillisecondsInWindow / framesInWindow;
                double averageSendMilliseconds = sendMillisecondsInWindow / framesInWindow;
                double averageFrameMilliseconds = frameMillisecondsInWindow / framesInWindow;
                LowLatencyVideoNetworkSnapshot networkSnapshot =
                    lowLatencyVideo.CollectNetworkSnapshot();
                string sendMetricName = networkSnapshot.HasFeedbackSample
                    ? "入队"
                    : "发";
                string networkMetrics = networkSnapshot.HasFeedbackSample ||
                    networkSnapshot.SenderQueueDropRatio > 0 ||
                    networkSnapshot.OversizedFrameDrops > 0 ||
                    networkSnapshot.AbortedFrameSends > 0
                        ? (
                            $"，UDP 到达 {networkSnapshot.DeliveryMegabitsPerSecond:F1}Mbps" +
                            $"，包丢 {networkSnapshot.PacketLossRatio:P1}" +
                            $"，帧弃 {networkSnapshot.FrameAbandonRatio:P1}" +
                            $"，发送槽丢 {networkSnapshot.SenderQueueDropRatio:P1}" +
                            $"，pacing {networkSnapshot.TargetMegabitsPerSecond:F0}Mbps" +
                            (networkSnapshot.XorFecEnabled
                                ? "，XOR FEC 开"
                                : string.Empty))
                        : string.Empty;
                captureLog(
                    $"画面统计：{actualFps:F1} FPS，JPEG {adaptiveController.CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(adaptiveController.CurrentScalePercent)}，采 {averageCaptureMilliseconds:F1}ms，编 {averageEncodeMilliseconds:F1}ms，{sendMetricName} {averageSendMilliseconds:F1}ms，编码 {megabitsPerSecond:F1}Mbps{networkMetrics}" +
                    (suppressedFramesInWindow > 0 ? $"，省略 {suppressedFramesInWindow} 张重复画面" : string.Empty) +
                    tcpWriteTimings.DescribeAverage());

                string? adaptiveMessage = adaptiveController.Update(
                    actualFps,
                    averageFrameMilliseconds,
                    averageSendMilliseconds,
                    networkSnapshot,
                    sourceWasIdle: suppressedFramesInWindow > 0);
                if (adaptiveMessage is not null)
                {
                    captureLog(adaptiveMessage);
                }

                framesInWindow = 0;
                suppressedFramesInWindow = 0;
                bytesInWindow = 0;
                captureMillisecondsInWindow = 0;
                encodeMillisecondsInWindow = 0;
                sendMillisecondsInWindow = 0;
                frameMillisecondsInWindow = 0;
                tcpWriteTimings.Reset();
                metricsWindowStartedAt = Stopwatch.GetTimestamp();
            }

            TimeSpan remaining = frameInterval - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }
        }
    }

    private static async Task RunInputLoopAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        CaptureSessionState captureState,
        CaptureTargetTopologyMonitorState
            captureTargetTopologyState,
        CaptureTargetPublicationCoordinator
            captureTargetPublicationCoordinator,
        FileTransferReceiver fileTransferReceiver,
        ViewerSessionState viewerState,
        LowLatencyVideoHostTransport lowLatencyVideo,
        IPAddress peerAddress,
        IPEndPoint hostTcpEndpoint,
        HostSessionLivenessTracker inboundLiveness,
        InputInjectionDispatcher inputInjectionDispatcher,
        RemoteInteractionActivity interactionActivity,
        Action<ScreenCaptureTarget> captureTargetChanged,
        Action<string> clipboardLog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inboundLiveness);
        var inputErrorLog = new InputErrorLogThrottler(InputErrorLogInterval);
        var inputState = new RemoteInputStateTracker();
        await using var heartbeat = new HostHeartbeatResponder(
            async pongToken =>
            {
                using (writePriority.BeginControlWritePriority())
                {
                    await Protocol.WriteMessageAsync(
                        stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty,
                        session, writePriority.Lock, pongToken);
                }
            }, cancellationToken);
        cancellationToken = heartbeat.Token;

        async Task ProcessControlAsync(
            ReadOnlyMemory<byte> payload,
            bool prioritizeEntireOperation)
        {
            await ProcessControlOperationAsync(
                async () =>
                {
                    if (prioritizeEntireOperation)
                    {
                        using (writePriority
                            .BeginControlWritePriority())
                        {
                            await HandleControlMessageAsync(
                                payload,
                                stream,
                                session,
                                writePriority,
                                writePriority.Lock,
                                captureState,
                                captureTargetTopologyState,
                                captureTargetPublicationCoordinator,
                                fileTransferReceiver,
                                viewerState,
                                lowLatencyVideo,
                                peerAddress,
                                hostTcpEndpoint,
                                captureTargetChanged,
                                clipboardLog,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        await HandleControlMessageAsync(
                            payload,
                            stream,
                            session,
                            writePriority,
                            writePriority.Lock,
                            captureState,
                            captureTargetTopologyState,
                            captureTargetPublicationCoordinator,
                            fileTransferReceiver,
                            viewerState,
                            lowLatencyVideo,
                            peerAddress,
                            hostTcpEndpoint,
                            captureTargetChanged,
                            clipboardLog,
                            cancellationToken);
                    }
                },
                async ex =>
                {
                    clipboardLog(
                        $"已忽略无效控制消息：{ex.Message}");
                    byte[] statusPayload =
                        RemoteMessageCodec.EncodeClipboardStatus(
                            false,
                            $"控制消息无效：{ex.Message}");
                    using (writePriority.BeginControlWritePriority())
                    {
                        await Protocol.WriteMessageAsync(
                            stream,
                            MessageType.Control,
                            statusPayload,
                            session,
                            writePriority.Lock,
                            cancellationToken);
                    }
                });
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long inboundReadGeneration =
                    inboundLiveness.BeginInboundRead();
                ProtocolMessage message;
                try
                {
                    message = await Protocol.ReadMessageAsync(
                        stream,
                        session,
                        cancellationToken);
                }
                finally
                {
                    // A deadline covers only the blocking network read. Once
                    // a complete message is available, potentially long
                    // control, clipboard and file operations are processing,
                    // not an idle peer, and must not be timed out.
                    inboundLiveness.EndInboundRead(
                        inboundReadGeneration);
                }

                switch (message.Type)
                {
                    case MessageType.Input:
                        try
                        {
                            RemoteInputCommand input = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
                            if (!captureState.TryGetInputMappingSnapshot(
                                    input,
                                    out Rectangle captureBounds,
                                    out Size frameSize))
                            {
                                if (IsTargetIndependentPointerRelease(input))
                                {
                                    // The selected monitor may disappear after
                                    // MouseDown. Release the physical button
                                    // without moving through stale coordinates,
                                    // otherwise it can remain held until the
                                    // monitor returns or the session closes.
                                    ReleaseTargetIndependentPointerAndTrack(
                                        input,
                                        inputState,
                                        inputInjectionDispatcher
                                            .ReleaseMouseButton);
                                    interactionActivity.Record();
                                    break;
                                }

                                throw new InvalidOperationException(
                                    "指定屏幕暂不可用；已拒绝指针输入，" +
                                    "避免坐标映射到所有屏幕。");
                            }

                            ApplyInputAndTrackClipboardMutation(
                                input,
                                inputState,
                                viewerState,
                                captureBounds,
                                frameSize,
                                inputInjectionDispatcher.Apply,
                                ClipboardTextService.ReadClipboardSequenceNumber,
                                static () => Environment.TickCount64);
                            interactionActivity.Record();
                        }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
                        {
                            string? logMessage = inputErrorLog.CreateMessage(ex.Message, Environment.TickCount64);
                            if (logMessage is not null)
                            {
                                clipboardLog(logMessage);
                                using (writePriority.BeginControlWritePriority())
                                {
                                    await SendInputStatusAsync(stream, session, writePriority.Lock, logMessage, cancellationToken);
                                }
                            }
                        }

                        break;
                    case MessageType.Control:
                        bool canInputOvertake =
                            false;
                        try
                        {
                            canInputOvertake =
                                CanInputOvertakeControl(
                                    RemoteMessageCodec
                                        .DecodeControl(
                                            message.PayloadMemory)
                                        .Kind);
                        }
                        catch (Exception ex) when (
                            ex is InvalidDataException or
                                EndOfStreamException or
                                IOException or
                                ArgumentException or
                                System.Text.DecoderFallbackException)
                        {
                            // Let the shared handler produce the canonical
                            // invalid-control status below.
                        }

                        if (canInputOvertake)
                        {
                            ReadOnlyMemory<byte> controlPayload =
                                message.PayloadMemory;
                            viewerState.QueueInputIndependentControl(
                                () => ProcessControlAsync(
                                    controlPayload,
                                    prioritizeEntireOperation:
                                        false),
                                ex =>
                                {
                                    if (!IsStopException(ex))
                                    {
                                        clipboardLog(
                                            "后台剪贴板控制已停止：" +
                                            ex.Message);
                                    }
                                },
                                cancellationToken);
                        }
                        else
                        {
                            // A normal control cannot overtake an earlier slow read: file-return
                            // confirmation/update state must remain in TCP control order. Once the
                            // barrier drains, handle it inline so a later input (for example
                            // ClipboardSetText followed by Ctrl+V) cannot overtake this mutation.
                            await viewerState
                                .WaitForInputIndependentControlsAsync();
                            await ProcessControlAsync(
                                message.PayloadMemory,
                                prioritizeEntireOperation:
                                    true);
                        }

                        break;
                    case MessageType.Ping:
                        heartbeat.Request();
                        break;
                }
            }
        }
        finally
        {
            RemoteInputReleaseResult releaseResult =
                inputState.ReleaseAll(
                inputInjectionDispatcher.ReleaseKey,
                inputInjectionDispatcher.ReleaseMouseButton);
            try
            {
                if (heartbeat.Failure is { } heartbeatFailure)
                {
                    clipboardLog($"心跳回复失败：{heartbeatFailure.Message}");
                }
                if (releaseResult.AttemptedCount > 0)
                {
                    clipboardLog(
                        $"连接结束时已尝试释放 " +
                        $"{releaseResult.AttemptedCount} 个残留远程按键/鼠标状态；" +
                        $"成功 {releaseResult.ReleasedCount} 个。" +
                        (releaseResult.Failures.Count == 0
                            ? string.Empty
                            : $"失败 {releaseResult.Failures.Count} 个：" +
                              string.Join(
                                  "；",
                                  releaseResult.Failures.Select(
                                      failure => failure.Message))));
                }
            }
            catch (Exception)
            {
                // Session teardown must preserve an exception already in
                // flight even if the diagnostic sink itself is unavailable.
            }
        }
    }

    internal static async Task ProcessControlOperationAsync(
        Func<Task> operation,
        Func<Exception, Task> reportRecoverableError)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(
            reportRecoverableError);
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            IsRecoverableControlOperationException(ex))
        {
            await reportRecoverableError(ex)
                .ConfigureAwait(false);
        }
    }

    internal static bool IsRecoverableControlOperationException(
        Exception exception) =>
        exception is not
                CaptureTargetPublicationTransportException &&
        (exception is InvalidDataException or
                EndOfStreamException or
                IOException or
                ArgumentException or
                System.Text.DecoderFallbackException);

    internal static async Task PublishCaptureTargetSelectionAsync(
        Func<Task> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        try
        {
            await publish().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or SocketException)
        {
            throw new CaptureTargetPublicationTransportException(
                "捕获目标切换状态写入失败；当前会话必须终止后重新同步。",
                ex);
        }
    }

    internal sealed class CaptureTargetPublicationTransportException(
        string message,
        Exception innerException) :
        IOException(message, innerException);

    internal static bool
        CanInputOvertakeControl(
            RemoteControlKind kind) =>
        kind is
            RemoteControlKind.ClipboardGetText or
            RemoteControlKind.HostVideoDiagnosticsRequest or
            RemoteControlKind
                .FileTransferRequestClipboardFiles;

    internal static bool RequiresAvailableCaptureTarget(
        RemoteInputCommand command) =>
        command.Kind is
            RemoteInputKind.MouseMove or
            RemoteInputKind.MouseDown or
            RemoteInputKind.MouseUp or
            RemoteInputKind.MouseWheel;

    internal static bool IsTargetIndependentPointerRelease(
        RemoteInputCommand command) =>
        command.Kind == RemoteInputKind.MouseUp;

    internal static void
        ReleaseTargetIndependentPointerAndTrack(
            RemoteInputCommand command,
            RemoteInputStateTracker inputState,
            Action<RemoteMouseButton> releaseMouseButton)
    {
        ArgumentNullException.ThrowIfNull(inputState);
        ArgumentNullException.ThrowIfNull(releaseMouseButton);
        if (!IsTargetIndependentPointerRelease(command))
        {
            throw new ArgumentException(
                "命令不是可脱离采集目标执行的鼠标释放事件。",
                nameof(command));
        }

        // Preserve ownership until the strict native release succeeds. If it
        // fails, the session-level catch reports the error and teardown can
        // still retry the held button through ReleaseAll.
        releaseMouseButton(command.Button);
        inputState.Observe(command);
    }

    internal static bool ShouldAdvanceCaptureTargetGeneration(
        bool wasAvailable,
        Rectangle previousBounds,
        ScreenCaptureTargetAvailability availability) =>
        wasAvailable != availability.IsAvailable ||
        (availability.IsAvailable &&
            previousBounds != availability.Bounds);

    internal static void ApplyInputAndTrackClipboardMutation(
        RemoteInputCommand input,
        RemoteInputStateTracker inputState,
        ViewerSessionState viewerState,
        Rectangle captureBounds,
        Size frameSize,
        Action<RemoteInputCommand, Rectangle, Size> applyInput,
        Func<uint> readClipboardSequence,
        Func<long> readTimestamp)
    {
        ArgumentNullException.ThrowIfNull(inputState);
        ArgumentNullException.ThrowIfNull(viewerState);
        ArgumentNullException.ThrowIfNull(applyInput);
        ArgumentNullException.ThrowIfNull(readClipboardSequence);
        ArgumentNullException.ThrowIfNull(readTimestamp);

        uint? clipboardSequenceBeforeInput = inputState.IsClipboardMutationShortcut(input)
            ? readClipboardSequence()
            : null;
        try
        {
            applyInput(input, captureBounds, frameSize);
            inputState.Observe(input);
        }
        finally
        {
            if (clipboardSequenceBeforeInput is { } sequence)
            {
                viewerState.RecordClipboardInputSequence(sequence, readTimestamp());
            }
        }
    }

    internal readonly record struct RemoteInputReleaseResult(
        int AttemptedCount,
        int ReleasedCount,
        IReadOnlyList<Exception> Failures);

    internal sealed class RemoteInputStateTracker
    {
        private readonly List<RemoteInputCommand>
            _pressedKeys = [];
        private readonly HashSet<RemotePhysicalKey>
            _pressedKeySet = [];
        private readonly Dictionary<int, int>
            _pressedVirtualKeyCounts = [];
        private readonly List<RemoteMouseButton> _pressedMouseButtons = [];
        private readonly HashSet<RemoteMouseButton> _pressedMouseButtonSet = [];

        public int PressedKeyCount => _pressedKeySet.Count;

        public int PressedMouseButtonCount => _pressedMouseButtonSet.Count;

        public bool IsClipboardMutationShortcut(RemoteInputCommand command)
        {
            if (command.Kind != RemoteInputKind.KeyDown)
            {
                return false;
            }

            bool controlPressed =
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.ControlKey) ||
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.LControlKey) ||
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.RControlKey);
            if (controlPressed && command.Data is (int)Keys.C or (int)Keys.X or (int)Keys.Insert)
            {
                return true;
            }

            bool shiftPressed =
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.ShiftKey) ||
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.LShiftKey) ||
                _pressedVirtualKeyCounts.ContainsKey(
                    (int)Keys.RShiftKey);
            return shiftPressed && command.Data == (int)Keys.Delete;
        }

        public void Observe(RemoteInputCommand command)
        {
            switch (command.Kind)
            {
                case RemoteInputKind.KeyDown:
                    RemotePhysicalKey pressedKey =
                        ToPhysicalKey(command);
                    if (_pressedKeySet.Add(pressedKey))
                    {
                        _pressedKeys.Add(command);
                        IncrementVirtualKey(
                            command.Data);
                    }

                    break;
                case RemoteInputKind.KeyUp:
                    int pressedIndex =
                        _pressedKeys.FindLastIndex(
                            pressed =>
                                ToPhysicalKey(pressed) ==
                                ToPhysicalKey(command));
                    if (pressedIndex < 0)
                    {
                        pressedIndex =
                            _pressedKeys.FindLastIndex(
                                pressed =>
                                    pressed.Data ==
                                    command.Data);
                    }

                    if (pressedIndex >= 0)
                    {
                        RemoteInputCommand pressed =
                            _pressedKeys[pressedIndex];
                        _pressedKeys.RemoveAt(
                            pressedIndex);
                        _pressedKeySet.Remove(
                            ToPhysicalKey(pressed));
                        DecrementVirtualKey(
                            pressed.Data);
                    }

                    break;
                case RemoteInputKind.MouseDown:
                    if (_pressedMouseButtonSet.Add(command.Button))
                    {
                        _pressedMouseButtons.Add(command.Button);
                    }

                    break;
                case RemoteInputKind.MouseUp:
                    _pressedMouseButtonSet.Remove(command.Button);
                    _pressedMouseButtons.Remove(command.Button);
                    break;
            }
        }

        public RemoteInputReleaseResult ReleaseAll(
            Action<RemoteInputCommand> releaseKey,
            Action<RemoteMouseButton> releaseMouseButton)
        {
            ArgumentNullException.ThrowIfNull(releaseKey);
            ArgumentNullException.ThrowIfNull(releaseMouseButton);

            RemoteMouseButton[] mouseButtons =
                _pressedMouseButtons
                    .Where(_pressedMouseButtonSet.Contains)
                    .Reverse()
                    .ToArray();
            RemoteInputCommand[] keys =
                _pressedKeys
                    .Where(key =>
                        _pressedKeySet.Contains(
                            ToPhysicalKey(key)))
                    .Reverse()
                    .ToArray();

            // Clear ownership before invoking external release callbacks. A
            // callback can fail or re-enter ReleaseAll, but this generation's
            // state must still be consumed exactly once.
            _pressedMouseButtons.Clear();
            _pressedMouseButtonSet.Clear();
            _pressedKeys.Clear();
            _pressedKeySet.Clear();
            _pressedVirtualKeyCounts.Clear();

            int released = 0;
            List<Exception>? failures = null;
            foreach (RemoteMouseButton button in mouseButtons)
            {
                try
                {
                    releaseMouseButton(button);
                    released++;
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(
                        new InvalidOperationException(
                            $"释放鼠标按键 {button} 失败：{ex.Message}",
                            ex));
                }
            }

            foreach (RemoteInputCommand key in keys)
            {
                try
                {
                    releaseKey(key);
                    released++;
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(
                        new InvalidOperationException(
                            $"释放键盘按键 {key.Data} 失败：{ex.Message}",
                            ex));
                }
            }

            return new RemoteInputReleaseResult(
                mouseButtons.Length + keys.Length,
                released,
                failures ?? []);
        }

        private static RemotePhysicalKey ToPhysicalKey(
            RemoteInputCommand command) =>
            new(
                command.Data,
                command.X,
                (RemoteKeyboardFlags)command.Y);

        private void IncrementVirtualKey(
            int virtualKey)
        {
            _pressedVirtualKeyCounts.TryGetValue(
                virtualKey,
                out int count);
            _pressedVirtualKeyCounts[virtualKey] =
                count + 1;
        }

        private void DecrementVirtualKey(
            int virtualKey)
        {
            if (!_pressedVirtualKeyCounts.TryGetValue(
                    virtualKey,
                    out int count))
            {
                return;
            }

            if (count <= 1)
            {
                _pressedVirtualKeyCounts.Remove(
                    virtualKey);
            }
            else
            {
                _pressedVirtualKeyCounts[virtualKey] =
                    count - 1;
            }
        }
    }

    internal sealed class SessionWritePriority : IDisposable
    {
        private int _pendingControlWrites;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public bool HasPendingControlWrite => Volatile.Read(ref _pendingControlWrites) > 0;

        public IDisposable BeginControlWritePriority()
        {
            Interlocked.Increment(ref _pendingControlWrites);
            return new PriorityLease(this);
        }

        public void Dispose()
        {
            Lock.Dispose();
        }

        private void EndControlWritePriority()
        {
            Interlocked.Decrement(ref _pendingControlWrites);
        }

        private sealed class PriorityLease(SessionWritePriority owner) : IDisposable
        {
            private SessionWritePriority? _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.EndControlWritePriority();
            }
        }
    }

    internal sealed class InputErrorLogThrottler
    {
        private readonly long _intervalMilliseconds;
        private long? _lastLoggedAt;
        private int _suppressedCount;

        public InputErrorLogThrottler(TimeSpan interval)
        {
            _intervalMilliseconds = Math.Max(1, (long)interval.TotalMilliseconds);
        }

        public string? CreateMessage(string errorMessage, long nowMilliseconds)
        {
            string normalizedMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? "未知输入错误"
                : errorMessage.Trim();

            if (_lastLoggedAt is null || nowMilliseconds - _lastLoggedAt.Value >= _intervalMilliseconds)
            {
                string message = _suppressedCount > 0
                    ? $"已忽略无效输入消息：{normalizedMessage}（此前已合并 {_suppressedCount} 条）"
                    : $"已忽略无效输入消息：{normalizedMessage}";
                _lastLoggedAt = nowMilliseconds;
                _suppressedCount = 0;
                return message;
            }

            _suppressedCount++;
            return null;
        }
    }

    private static async Task SendInputStatusAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        string message,
        CancellationToken cancellationToken)
    {
        byte[] payload = RemoteMessageCodec.EncodeClipboardStatus(false, $"远程输入失败：{message}");
        await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
    }

    private static async Task HandleControlMessageAsync(
        ReadOnlyMemory<byte> payload,
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        SemaphoreSlim writeLock,
        CaptureSessionState captureState,
        CaptureTargetTopologyMonitorState
            captureTargetTopologyState,
        CaptureTargetPublicationCoordinator
            captureTargetPublicationCoordinator,
        FileTransferReceiver fileTransferReceiver,
        ViewerSessionState viewerState,
        LowLatencyVideoHostTransport lowLatencyVideo,
        IPAddress peerAddress,
        IPEndPoint hostTcpEndpoint,
        Action<ScreenCaptureTarget> captureTargetChanged,
        Action<string> clipboardLog,
        CancellationToken cancellationToken)
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);
        bool sendFileReceipts = viewerState.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferReceipt) &&
            viewerState.PendingRemoteUpdateTransferId != control.TransferId;
        switch (control.Kind)
        {
            case RemoteControlKind.SelectCaptureTarget:
                if (string.IsNullOrWhiteSpace(control.TargetId))
                {
                    return;
                }

                int previousTargetGeneration =
                    captureState.TargetVersion;
                ScreenCaptureTarget target = captureState.ChangeTarget(control.TargetId);
                if (!string.Equals(
                        target.Id,
                        control.TargetId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    clipboardLog(
                        string.Equals(
                            control.TargetId,
                            ScreenCaptureTarget.AllScreensId,
                            StringComparison.OrdinalIgnoreCase)
                            ? $"低延迟保护：所有屏幕无法使用单屏 WGC/D3D11 GPU 捕获链，" +
                              $"已切换为 {target.DisplayName}，避免多屏 GDI 捕获退化为高延迟 JPEG。"
                            : $"低延迟保护：捕获目标 {control.TargetId} 当前不可用，" +
                              $"已保持 {target.DisplayName}，不会回退到所有屏幕。");
                }

                captureTargetChanged(target);
                IReadOnlyList<ScreenCaptureTarget> selectedTargetList =
                    ScreenCaptureService.GetAvailableTargets();
                CaptureTargetStateSnapshot selectedTargetSnapshot =
                    captureState.RefreshCaptureTopology(
                        selectedTargetList);
                CaptureTargetInfo[] selectedTargetInfos =
                    selectedTargetList
                        .Select(ScreenCaptureService.ToInfo)
                        .ToArray();
                CaptureTargetTopologyPublication selectionPublication =
                    captureTargetTopologyState.Observe(
                        selectedTargetInfos,
                        selectedTargetSnapshot);
                await PublishCaptureTargetSelectionAsync(
                    () => captureTargetPublicationCoordinator
                        .PublishIfCurrentAsync(
                        selectedTargetSnapshot,
                        captureState
                            .IsCurrentPublicationSnapshot,
                        async token =>
                        {
                            IReadOnlyList<ScreenCaptureTarget>
                                currentTargets =
                                    ScreenCaptureService
                                        .GetAvailableTargets();
                            CaptureTargetStateSnapshot
                                currentSnapshot =
                                    captureState
                                        .RefreshCaptureTopology(
                                            currentTargets);
                            if (!SameCaptureTargetPublicationSnapshot(
                                    selectedTargetSnapshot,
                                    currentSnapshot))
                            {
                                return;
                            }

                            if (currentSnapshot.Generation !=
                                previousTargetGeneration)
                            {
                                await lowLatencyVideo
                                    .StopVideoForCaptureTargetChangeAsync()
                                    .ConfigureAwait(false);
                            }
                            await SendCaptureTargetsAsync(
                                stream,
                                session,
                                writeLock,
                                currentTargets.Select(
                                    ScreenCaptureService.ToInfo),
                                token);
                            await SendCaptureTargetChangedAsync(
                                stream,
                                session,
                                writeLock,
                                captureState.CurrentTarget,
                                token);
                            if (selectionPublication
                                    .AvailabilityChanged ||
                                currentSnapshot.Generation !=
                                    previousTargetGeneration)
                            {
                                await SendCaptureTargetAvailabilityAsync(
                                    stream,
                                    session,
                                    writeLock,
                                    currentSnapshot,
                                    token);
                            }
                        },
                        cancellationToken));
                break;
            case RemoteControlKind.ClipboardSetText:
                await SetRemoteClipboardAsync(control.Text ?? string.Empty, stream, session, writeLock, clipboardLog, cancellationToken);
                break;
            case RemoteControlKind.ClipboardGetText:
                // A modern viewer can issue this immediately after forwarding Ctrl+C/Ctrl+X.
                // This branch runs on the session's serial background clipboard queue so the
                // bounded clipboard wait can never hold KeyUp or later input messages.
                bool? clipboardInputReady =
                    await WaitForPendingClipboardInputAsync(
                    viewerState,
                    cancellationToken,
                    consumeMarker: false);
                if (clipboardInputReady == false)
                {
                    clipboardLog(
                        "等待远程复制更新剪贴板超时，未返回旧文本。");
                    using (writePriority.BeginControlWritePriority())
                    {
                        await Protocol.WriteMessageAsync(
                            stream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeClipboardStatus(
                                false,
                                "等待远程复制更新剪贴板超时，未使用旧剪贴板内容。"),
                            session,
                            writeLock,
                            cancellationToken);
                    }
                }
                else
                {
                    await SendClipboardTextAsync(
                        stream,
                        session,
                        writePriority,
                        writeLock,
                        clipboardLog,
                        cancellationToken);
                }

                break;
            case RemoteControlKind.FileTransferStart:
                await HandleFileTransferOperationAsync(
                    () => Task.FromResult<string?>(BeginRegularFileTransfer(control, fileTransferReceiver, viewerState)),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null,
                    sendFileReceipts ? control : null);
                break;
            case RemoteControlKind.RemoteUpdateStart:
                if (viewerState.HasPendingClipboardFileReturnPlan ||
                    viewerState.HasActiveClipboardFileReturn)
                {
                    await SendFileTransferStatusAsync(
                        stream,
                        session,
                        writeLock,
                        false,
                        "远程更新已拒绝：远端文件回传尚未结束，请稍后重试。",
                        cancellationToken);
                    break;
                }

                await HandleFileTransferOperationAsync(
                    () => Task.FromResult<string?>(BeginRemoteUpdateTransfer(control, fileTransferReceiver, viewerState)),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null);
                break;
            case RemoteControlKind.FileTransferChunk:
                await HandleFileTransferOperationAsync(
                    async () =>
                    {
                        await fileTransferReceiver.WriteChunkAsync(control, cancellationToken);
                        return null;
                    },
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null,
                    sendFileReceipts ? control : null);
                break;
            case RemoteControlKind.FileTransferChecksum:
                await HandleFileTransferOperationAsync(
                    () =>
                    {
                        fileTransferReceiver.SetExpectedChecksum(control);
                        return Task.FromResult<string?>(null);
                    },
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null,
                    sendFileReceipts ? control : null);
                break;
            case RemoteControlKind.FileTransferCancel:
                if (string.Equals(viewerState.PendingRemoteUpdateTransferId, control.TransferId, StringComparison.Ordinal))
                {
                    viewerState.PendingRemoteUpdateTransferId = null;
                }

                await HandleFileTransferOperationAsync(
                    () => Task.FromResult<string?>(fileTransferReceiver.Cancel(control)),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null,
                    sendFileReceipts ? control : null);
                break;
            case RemoteControlKind.FileTransferComplete:
                await HandleFileTransferOperationAsync(
                    async () => await CompleteFileTransferOrRemoteUpdateAsync(
                        control,
                        fileTransferReceiver,
                        viewerState,
                        clipboardLog,
                        cancellationToken),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken,
                    () => viewerState.PendingRemoteUpdateTransferId = null,
                    sendFileReceipts ? control : null);
                break;
            case RemoteControlKind.FileDropPasteBegin:
                await HandleFileTransferOperationAsync(
                    () => Task.FromResult<string?>(fileTransferReceiver.BeginClipboardFilePasteBatch()),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken);
                break;
            case RemoteControlKind.FileDropPasteCommit:
                await HandleFileTransferOperationAsync(
                    async () => await fileTransferReceiver.CommitClipboardFilePasteBatchAsync(),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken);
                break;
            case RemoteControlKind.FileDropPasteCancel:
                fileTransferReceiver.CancelClipboardFilePasteBatch();
                break;
            case RemoteControlKind.FileTransferRequestClipboardFiles:
                await PrepareClipboardFilesToViewerAsync(
                    stream,
                    session,
                    writePriority,
                    writeLock,
                    clipboardLog,
                    viewerState,
                    cancellationToken);
                break;
            case RemoteControlKind.FileTransferConfirmClipboardFiles:
                await SendPendingClipboardFilesToViewerAsync(
                    stream,
                    session,
                    writePriority,
                    writeLock,
                    clipboardLog,
                    viewerState,
                    cancellationToken);
                break;
            case RemoteControlKind.FileTransferRejectClipboardFiles:
                await RejectClipboardFilesToViewerAsync(
                    stream,
                    session,
                    writeLock,
                    viewerState,
                    cancellationToken);
                break;
            case RemoteControlKind.RemoteUpdatePackageRequest:
                if (viewerState.HasPendingClipboardFileReturnPlan ||
                    viewerState.HasActiveClipboardFileReturn ||
                    !string.IsNullOrWhiteSpace(viewerState.PendingRemoteUpdateTransferId))
                {
                    await SendFileTransferStatusAsync(
                        stream,
                        session,
                        writeLock,
                        false,
                        "更新包回传已拒绝：当前还有文件回传清单或更新传输尚未结束。",
                        cancellationToken);
                    break;
                }

                await HandleFileTransferOperationAsync(
                    async () => await SendRemoteUpdatePackageToViewerAsync(
                        stream,
                        session,
                        writeLock,
                        viewerState,
                        cancellationToken),
                    fileTransferReceiver,
                    stream,
                    session,
                    writeLock,
                    cancellationToken);
                break;
            case RemoteControlKind.FileTransferStatus:
            case RemoteControlKind.FileTransferReceipt:
                HandlePeerFileTransferStatus(control, viewerState, clipboardLog);
                break;
            case RemoteControlKind.ViewerInfo:
                viewerState.SetSupportedVideoCodecs(
                    control.SupportedVideoCodecs);
                clipboardLog($"查看端支持编码：{FormatVideoCodecs(control.SupportedVideoCodecs)}");
                break;
            case RemoteControlKind.DeviceIdentityRequest:
                // Sent only on request from a client that saw the capability bit;
                // old clients with strict control decoders never receive this.
                await Protocol.WriteMessageAsync(stream, MessageType.Control,
                    RemoteMessageCodec.EncodeDeviceIdentity(RemoteDeviceIdentity.LocalId),
                    session, writeLock, cancellationToken);
                break;
            case RemoteControlKind.HostVideoDiagnosticsRequest:
                // Opt-in only: strict old clients never receive new controls.
                string? diagnostics = viewerState.VideoDiagnostics.TryRead(Environment.TickCount64);
                if (diagnostics is not null)
                {
                    string dependencyStatus = WindowsFfmpegDependency.Status;
                    if (dependencyStatus.Length != 0) diagnostics += "\n" + dependencyStatus;
                    await Protocol.WriteMessageAsync(stream, MessageType.Control,
                        RemoteMessageCodec.EncodeHostVideoDiagnostics(diagnostics), session, writeLock, cancellationToken);
                }
                break;
            case RemoteControlKind.VideoKeyFrameRequest:
                viewerState.RequestVideoKeyFrame();
                break;
            case RemoteControlKind.ViewerCapabilities:
                viewerState.Capabilities = control.Capabilities;
                fileTransferReceiver.RequireChecksum = control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum);
                clipboardLog($"查看端能力：{RemoteDeviceCapabilityInfo.Format(control.Capabilities)}");
                if (control.Capabilities.HasFlag(RemoteDeviceCapabilities.LowLatencyUdpVideo))
                {
                    LowLatencyVideoOffer? offer;
                    try
                    {
                        LowLatencyVideoFeatures features =
                            LowLatencyVideoFeatureNegotiation.FromCapabilities(
                                control.Capabilities);
                        offer = lowLatencyVideo.TryCreateOffer(
                            peerAddress,
                            features,
                            hostTcpEndpoint);
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException or ArgumentException)
                    {
                        clipboardLog($"低延迟 UDP 画面通道不可用，继续使用 TCP：{ex.Message}");
                        break;
                    }

                    if (offer is not null)
                    {
                        byte[] offerPayload = RemoteMessageCodec.EncodeLowLatencyVideoOffer(offer);
                        try
                        {
                            await Protocol.WriteMessageAsync(
                                stream,
                                MessageType.Control,
                                offerPayload,
                                session,
                                writeLock,
                                cancellationToken);
                            lowLatencyVideo.MarkOfferSent();
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(offerPayload);
                            lowLatencyVideo.ClearOfferSecrets();
                        }
                    }
                }

                break;
            case RemoteControlKind.LowLatencyVideoReady:
                if (!lowLatencyVideo.TryMarkReady(
                    control.LowLatencyVideoChannelId,
                    control.LowLatencyVideoEpoch))
                {
                    clipboardLog("已忽略过期或未完成握手的 UDP 画面就绪消息。");
                }

                break;
            case RemoteControlKind.LowLatencyVideoStop:
                if (lowLatencyVideo.Matches(
                    control.LowLatencyVideoChannelId,
                    control.LowLatencyVideoEpoch))
                {
                    bool keptUdpInput =
                        control.LowLatencyVideoStopReason ==
                            LowLatencyVideoFallbackReasons
                                .PreserveUdpInput &&
                        lowLatencyVideo
                            .TryDisableVideoRouteKeepingUdpInput();
                    byte stoppedReason =
                        ResolveLowLatencyVideoStoppedReason(
                            control.LowLatencyVideoStopReason,
                            keptUdpInput);
                    if (!keptUdpInput)
                    {
                        lowLatencyVideo.DisableRoute(
                            notifyViewer: false);
                    }

                    byte[] stoppedPayload = RemoteMessageCodec.EncodeLowLatencyVideoStopped(
                        control.LowLatencyVideoChannelId,
                        control.LowLatencyVideoEpoch,
                        stoppedReason);
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        stoppedPayload,
                        session,
                        writeLock,
                        cancellationToken);
                }

                break;
        }
    }

    private static string BeginRemoteUpdateTransfer(
        RemoteControlMessage control,
        FileTransferReceiver fileTransferReceiver,
        ViewerSessionState viewerState)
    {
        if (!RemoteUpdater.CanApplyRemoteUpdate)
        {
            throw new InvalidOperationException("当前被控端不支持远程自更新。");
        }

        RemoteUpdater.ValidateRemoteUpdatePackageName(control.FileName);
        RemoteUpdater.ValidateRemoteUpdatePackageLength(control.FileLength);
        string message = fileTransferReceiver.Start(control);
        viewerState.PendingRemoteUpdateTransferId = control.TransferId;
        return $"{message}；更新包接收完成后将自动重启 RemoteDesk。";
    }

    private static string BeginRegularFileTransfer(
        RemoteControlMessage control,
        FileTransferReceiver fileTransferReceiver,
        ViewerSessionState viewerState)
    {
        string message = fileTransferReceiver.Start(control);
        viewerState.PendingRemoteUpdateTransferId = null;
        return message;
    }

    private static async Task<string?> CompleteFileTransferOrRemoteUpdateAsync(
        RemoteControlMessage control,
        FileTransferReceiver fileTransferReceiver,
        ViewerSessionState viewerState,
        Action<string> clipboardLog,
        CancellationToken cancellationToken)
    {
        string message = await fileTransferReceiver.CompleteAsync(control, cancellationToken);
        if (!string.Equals(viewerState.PendingRemoteUpdateTransferId, control.TransferId, StringComparison.Ordinal))
        {
            return message;
        }

        viewerState.PendingRemoteUpdateTransferId = null;
        string packagePath = fileTransferReceiver.LastCompletedFilePath
            ?? throw new InvalidOperationException("更新包保存路径不可用。");
        try
        {
            RemoteUpdater.ScheduleApplyAndRestart(packagePath, clipboardLog);
        }
        catch
        {
            RemoteUpdater.DeleteRejectedReceivedPackage(packagePath);
            throw;
        }
        return $"{message}；正在应用远程更新，连接会断开，RemoteDesk 会自动重启被控端。";
    }

    internal static string FormatPeerFileTransferStatus(RemoteControlMessage control)
    {
        string message = string.IsNullOrWhiteSpace(control.StatusMessage)
            ? "文件传输状态已更新。"
            : control.StatusMessage.Trim();
        return control.Success
            ? $"查看端文件状态：{message}"
            : $"查看端文件状态异常：{message}";
    }

    internal static bool HandlePeerFileTransferStatus(
        RemoteControlMessage control,
        ViewerSessionState viewerState,
        Action<string> clipboardLog)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(viewerState);
        ArgumentNullException.ThrowIfNull(clipboardLog);

        clipboardLog(FormatPeerFileTransferStatus(control));
        return !control.Success && viewerState.CancelActiveClipboardFileReturn();
    }

    private static string FormatVideoCodecs(RemoteVideoCodecs codecs)
    {
        List<string> names = [];
        if (codecs.HasFlag(RemoteVideoCodecs.Jpeg))
        {
            names.Add("JPEG");
        }

        if (codecs.HasFlag(RemoteVideoCodecs.H264AnnexB))
        {
            names.Add("H.264");
        }

        return names.Count == 0 ? "未知" : string.Join("/", names);
    }

    internal static async Task RunCaptureTargetTopologyMonitorAsync(
        CaptureTargetTopologyMonitorState monitorState,
        Func<IReadOnlyList<ScreenCaptureTarget>> enumerateTargets,
        Func<IReadOnlyList<ScreenCaptureTarget>, CaptureTargetStateSnapshot>
            refreshCaptureState,
        Func<CaptureTargetTopologyPublication, CancellationToken, Task>
            publish,
        TimeSpan pollInterval,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(monitorState);
        ArgumentNullException.ThrowIfNull(enumerateTargets);
        ArgumentNullException.ThrowIfNull(refreshCaptureState);
        ArgumentNullException.ThrowIfNull(publish);
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval));
        }

        delayAsync ??= Task.Delay;
        while (!cancellationToken.IsCancellationRequested)
        {
            await delayAsync(
                    pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<ScreenCaptureTarget> targets =
                enumerateTargets();
            CaptureTargetStateSnapshot snapshot =
                refreshCaptureState(targets);
            CaptureTargetTopologyPublication publication =
                monitorState.Observe(
                    targets
                        .Select(ScreenCaptureService.ToInfo)
                        .ToArray(),
                    snapshot);
            if (publication.HasChanges)
            {
                await publish(
                        publication,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task PublishCaptureTargetTopologyAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        CaptureSessionState captureState,
        CaptureTargetPublicationCoordinator
            publicationCoordinator,
        LowLatencyVideoHostTransport lowLatencyVideo,
        CaptureTargetTopologyPublication publication,
        Action<string> captureLog,
        CancellationToken cancellationToken)
    {
        if (!publication.HasChanges)
        {
            return;
        }

        await publicationCoordinator.PublishIfCurrentAsync(
            publication.Snapshot,
            captureState.IsCurrentPublicationSnapshot,
            async token =>
            {
                IReadOnlyList<ScreenCaptureTarget> currentTargets =
                    ScreenCaptureService.GetAvailableTargets();
                CaptureTargetStateSnapshot currentSnapshot =
                    captureState.RefreshCaptureTopology(
                        currentTargets);
                if (!SameCaptureTargetPublicationSnapshot(
                        publication.Snapshot,
                        currentSnapshot))
                {
                    return;
                }

                if (publication.GenerationChanged)
                {
                    await lowLatencyVideo
                        .StopVideoForCaptureTargetChangeAsync()
                        .ConfigureAwait(false);
                }
                using (writePriority.BeginControlWritePriority())
                {
                    if (publication.Targets is not null)
                    {
                        await SendCaptureTargetsAsync(
                            stream,
                            session,
                            writePriority.Lock,
                            currentTargets.Select(
                                ScreenCaptureService.ToInfo),
                            token);
                    }

                    if (publication.GenerationChanged)
                    {
                        await SendCaptureTargetChangedAsync(
                            stream,
                            session,
                            writePriority.Lock,
                            captureState.CurrentTarget,
                            token);
                    }

                    if (publication.AvailabilityChanged ||
                        publication.GenerationChanged)
                    {
                        await SendCaptureTargetAvailabilityAsync(
                            stream,
                            session,
                            writePriority.Lock,
                            currentSnapshot,
                            token);
                        captureLog(
                            currentSnapshot.IsAvailable
                                ? $"指定屏幕 {currentSnapshot.Target.Id} 已重新连接；" +
                                  "正在按新的捕获 generation 恢复画面。"
                                : $"指定屏幕 {currentSnapshot.Target.Id} 暂不可用；" +
                                  "画面和指针输入已暂停；当前没有唯一可用的物理屏幕可自动切换。" +
                                  "请重新连接该屏幕或由查看端选择其他屏幕。");
                    }
                }
            },
            cancellationToken);
    }

    internal static bool SameCaptureTargetPublicationSnapshot(
        CaptureTargetStateSnapshot left,
        CaptureTargetStateSnapshot right) =>
        left.Generation == right.Generation &&
        left.IsAvailable == right.IsAvailable &&
        string.Equals(
            left.Target.Id,
            right.Target.Id,
            StringComparison.OrdinalIgnoreCase);

    private static async Task SendCaptureTargetsAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        await SendCaptureTargetsAsync(
            stream,
            session,
            writeLock,
            ScreenCaptureService.GetAvailableTargets()
                .Select(ScreenCaptureService.ToInfo),
            cancellationToken);
    }

    private static async Task SendCaptureTargetsAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        IEnumerable<CaptureTargetInfo> targets,
        CancellationToken cancellationToken)
    {
        byte[] payload =
            RemoteMessageCodec.EncodeCaptureTargetList(targets);

        await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
    }

    private static async Task SendCaptureTargetAvailabilityAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CaptureTargetStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await SendCaptureTargetAvailabilityAsync(
            stream,
            session,
            writeLock,
            snapshot,
            snapshot.IsAvailable,
            displayMessage: null,
            cancellationToken);
    }

    private static async Task SendCaptureTargetAvailabilityAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CaptureTargetStateSnapshot snapshot,
        bool isAvailable,
        string? displayMessage,
        CancellationToken cancellationToken)
    {
        CaptureTargetAvailabilityStatusData status =
            string.IsNullOrWhiteSpace(displayMessage)
                ? CaptureTargetAvailabilityStatusCodec.Create(
                    isAvailable,
                    snapshot.Target,
                    snapshot.Generation)
                : new CaptureTargetAvailabilityStatusData(
                    isAvailable,
                    snapshot.Target,
                    snapshot.Generation,
                    displayMessage);
        byte[] payload = RemoteMessageCodec.EncodeClipboardStatus(
            isAvailable,
            CaptureTargetAvailabilityStatusCodec.Encode(status));
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            payload,
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task SendDeviceInfoAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        await Protocol.WriteMessageAsync(
            stream,
            MessageType.Control,
            RemoteMessageCodec.EncodeDeviceBuildInfo(RemoteDeskBuildInfo.BuildStamp),
            session,
            writeLock,
            cancellationToken);

        byte[] payload = RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
            Environment.MachineName,
            RemoteDevicePlatforms.Current,
            RemoteDeviceCapabilityInfo.LocalWindows(canRemoteStart: false)));

        await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
    }

    private static async Task SendCaptureTargetChangedAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        ScreenCaptureTarget target,
        CancellationToken cancellationToken)
    {
        byte[] payload = RemoteMessageCodec.EncodeCaptureTargetChanged(ScreenCaptureService.ToInfo(target));
        await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
    }

    private static async Task HandleFileTransferOperationAsync(
        Func<Task<string?>> operation,
        FileTransferReceiver fileTransferReceiver,
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken,
        Action? onActiveTransferAborted = null,
        RemoteControlMessage? receiptControl = null)
    {
        try
        {
            string? message = await operation();
            if (!string.IsNullOrWhiteSpace(message))
            {
                byte[] payload = receiptControl is { Kind: RemoteControlKind.FileTransferComplete or RemoteControlKind.FileTransferCancel, TransferId: { } id }
                    ? RemoteMessageCodec.EncodeFileTransferReceipt(id, receiptControl.Kind == RemoteControlKind.FileTransferComplete, message)
                    : RemoteMessageCodec.EncodeFileTransferStatus(true, message);
                await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
            }
        }
        catch (Exception ex) when (RemoteFileTransfer.IsRecoverableTransferException(ex))
        {
            if (ShouldClearPendingRemoteUpdateAfterTransferFailure(fileTransferReceiver))
            {
                onActiveTransferAborted?.Invoke();
            }

            string error = $"文件传输失败：{ex.Message}";
            byte[] payload = receiptControl?.TransferId is { } id
                ? RemoteMessageCodec.EncodeFileTransferReceipt(id, false, error)
                : RemoteMessageCodec.EncodeFileTransferStatus(false, error);
            await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
        }
    }

    internal static bool ShouldClearPendingRemoteUpdateAfterTransferFailure(
        FileTransferReceiver fileTransferReceiver)
    {
        ArgumentNullException.ThrowIfNull(fileTransferReceiver);
        return !fileTransferReceiver.HasActiveTransfer;
    }

    private static async Task SendFileTransferStatusAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        byte[] payload = RemoteMessageCodec.EncodeFileTransferStatus(success, message);
        await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
    }

    private static async Task TrySendFileTransferStatusAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                success,
                message,
                cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    internal static async Task RejectClipboardFilesToViewerAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        ViewerSessionState viewerState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(writeLock);
        ArgumentNullException.ThrowIfNull(viewerState);

        bool pendingPlanRejected = viewerState.ClearPendingClipboardFileReturnPlan();

        // The input loop awaits this method before reading another request. For an active return,
        // wait for its worker to write the canonical terminal status and clear the active slot;
        // writing another status here would duplicate the worker's terminal notification.
        if (await viewerState.CancelAndWaitForClipboardFileReturnAsync().ConfigureAwait(false))
        {
            return;
        }

        if (pendingPlanRejected)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "远端文件回传已取消。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PrepareClipboardFilesToViewerAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        SemaphoreSlim writeLock,
        Action<string> clipboardLog,
        ViewerSessionState viewerState,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(viewerState.PendingRemoteUpdateTransferId))
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "远端文件回传已拒绝：远程更新传输尚未结束。",
                cancellationToken);
            return;
        }

        if (viewerState.HasPendingClipboardFileReturnPlan || viewerState.HasActiveClipboardFileReturn)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                viewerState.HasActiveClipboardFileReturn
                    ? "远端文件回传进行中，请等待完成或先取消当前请求。"
                    : "已有远端文件回传清单等待确认，请先确认或取消当前请求。",
                cancellationToken);
            return;
        }

        bool? clipboardInputReady = await WaitForPendingClipboardInputAsync(
            viewerState,
            cancellationToken);
        if (clipboardInputReady == false)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "等待远程复制更新剪贴板超时，本次文件回传已终止，未使用旧剪贴板内容。",
                cancellationToken);
            return;
        }

        IReadOnlyList<string> clipboardFiles;
        try
        {
            clipboardFiles = await ClipboardTextService.GetFileDropListAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                $"读取远端文件剪贴板失败：{ex.Message}",
                cancellationToken);
            return;
        }

        if (clipboardFiles.Count == 0)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "远端剪贴板没有可回传的文件。请先在远端资源管理器复制文件或文件夹，再点击拉取文件。",
                cancellationToken);
            return;
        }

        RemoteFilePastePlan plan = RemoteFileTransfer.CreatePastePlan(
            clipboardFiles,
            MaxClipboardFileReturnCount,
            File.Exists,
            Directory.Exists,
            includeDirectories: true);

        if (plan.Files.Count == 0)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                FormatEmptyClipboardFileReturnResult(plan),
                cancellationToken);
            return;
        }

        if (viewerState.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferPreview))
        {
            if (!viewerState.TryReserveClipboardFileReturnPlan(plan))
            {
                await SendFileTransferStatusAsync(
                    stream,
                    session,
                    writeLock,
                    false,
                    "已有远端文件回传清单等待确认，请先确认或取消当前请求。",
                    cancellationToken);
                return;
            }

            IReadOnlyList<FileTransferConfirmationItem> items = FileTransferConfirmation.CreateItems(
                plan,
                (_item, transferName) => FileTransferConfirmation.FormatLocalReceiveDestination(transferName),
                calculateDirectorySizes: false);
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview(
                    items,
                    FileTransferConfirmation.BuildPlanNote(plan, directorySizesIncluded: false)),
                session,
                writeLock,
                cancellationToken);
            clipboardLog($"已发送远端剪贴板文件清单，等待查看端确认：{plan.Files.Count} 个。");
            return;
        }

        if (!TryStartClipboardFilesToViewer(
                plan,
                stream,
                session,
                writePriority,
                clipboardLog,
                viewerState,
                cancellationToken))
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "远端文件回传进行中，请等待完成或先取消当前请求。",
                cancellationToken);
        }
    }

    private static async Task SendPendingClipboardFilesToViewerAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        SemaphoreSlim writeLock,
        Action<string> clipboardLog,
        ViewerSessionState viewerState,
        CancellationToken cancellationToken)
    {
        RemoteFilePastePlan? plan = viewerState.TakePendingClipboardFileReturnPlan();
        if (plan is null)
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "没有等待确认的远端文件回传。",
                cancellationToken);
            return;
        }

        if (!TryStartClipboardFilesToViewer(
                plan,
                stream,
                session,
                writePriority,
                clipboardLog,
                viewerState,
                cancellationToken))
        {
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                false,
                "远端文件回传进行中，请等待完成或先取消当前请求。",
                cancellationToken);
        }
    }

    private static bool TryStartClipboardFilesToViewer(
        RemoteFilePastePlan plan,
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        Action<string> clipboardLog,
        ViewerSessionState viewerState,
        CancellationToken sessionCancellationToken)
    {
        RemoteDeviceCapabilities viewerCapabilities = viewerState.Capabilities;
        return viewerState.TryStartClipboardFileReturn(
            operationCancellationToken => RunClipboardFilesToViewerAsync(
                plan,
                stream,
                session,
                writePriority,
                clipboardLog,
                viewerCapabilities,
                operationCancellationToken,
                sessionCancellationToken),
            sessionCancellationToken);
    }

    internal static async Task RunClipboardFilesToViewerAsync(
        RemoteFilePastePlan plan,
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        Action<string> clipboardLog,
        RemoteDeviceCapabilities viewerCapabilities,
        CancellationToken operationCancellationToken,
        CancellationToken sessionCancellationToken)
    {
        try
        {
            await SendClipboardFilesToViewerAsync(
                plan,
                stream,
                session,
                writePriority.Lock,
                clipboardLog,
                viewerCapabilities,
                operationCancellationToken,
                sessionCancellationToken);
        }
        catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested)
        {
            if (!sessionCancellationToken.IsCancellationRequested)
            {
                await TrySendFileTransferStatusAsync(
                    stream,
                    session,
                    writePriority.Lock,
                    false,
                    "远端文件回传已取消。",
                    sessionCancellationToken);
                clipboardLog("查看端已取消远端剪贴板文件回传。");
            }
        }
        catch (Exception ex)
        {
            if (!sessionCancellationToken.IsCancellationRequested)
            {
                await TrySendFileTransferStatusAsync(
                    stream,
                    session,
                    writePriority.Lock,
                    false,
                    $"远端文件回传已取消：{ex.Message}",
                    sessionCancellationToken);
            }

            clipboardLog($"远端剪贴板文件后台回传已停止：{ex.Message}");
        }
    }

    private static async Task<string?> SendRemoteUpdatePackageToViewerAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        ViewerSessionState viewerState,
        CancellationToken cancellationToken)
    {
        if (!RemoteUpdater.CanApplyRemoteUpdate)
        {
            throw new InvalidOperationException("当前被控端不是可自更新的 RemoteDesk.exe。");
        }

        if (!viewerState.Capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum))
        {
            throw new InvalidOperationException("查看端未声明 SHA-256 文件校验能力，无法回传更新包。");
        }

        if (!viewerState.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel))
        {
            throw new InvalidOperationException("查看端未声明传输取消能力，无法安全回传更新包。");
        }

        string packagePath = RemoteUpdater.GetCurrentPackagePath();
        await SendFileToViewerAsync(
            packagePath,
            stream,
            session,
            writeLock,
            cancellationToken,
            sendChecksum: true,
            sendCancel: true,
            transferFileName: "RemoteDesk.exe",
            remoteUpdate: true);

        return "已把当前 RemoteDesk.exe 回传给查看端用于本机更新。";
    }

    private static async Task SendClipboardFilesToViewerAsync(
        RemoteFilePastePlan plan,
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        Action<string> clipboardLog,
        RemoteDeviceCapabilities viewerCapabilities,
        CancellationToken operationCancellationToken,
        CancellationToken sessionCancellationToken = default)
    {
        CancellationToken wireCancellationToken = sessionCancellationToken.CanBeCanceled
            ? sessionCancellationToken
            : operationCancellationToken;
        clipboardLog($"查看端请求回传远端剪贴板文件：{plan.Files.Count} 个。");
        operationCancellationToken.ThrowIfCancellationRequested();
        await SendFileTransferStatusAsync(
            stream,
            session,
            writeLock,
            true,
            $"正在回传远端剪贴板文件：{plan.Files.Count} 个",
            wireCancellationToken);

        int sentFiles = 0;
        int failedFiles = 0;
        int archivedDirectories = 0;
        foreach (RemoteFilePasteItem item in plan.TransferItems)
        {
            operationCancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool archivedDirectory = await SendTransferItemToViewerAsync(
                    item,
                    stream,
                    session,
                    writeLock,
                    viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum),
                    viewerCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel),
                    operationCancellationToken,
                    wireCancellationToken);
                operationCancellationToken.ThrowIfCancellationRequested();
                sentFiles++;
                if (archivedDirectory)
                {
                    archivedDirectories++;
                }
            }
            catch (Exception ex) when (RemoteFileTransfer.IsRecoverableTransferException(ex))
            {
                failedFiles++;
                string fileName = RemoteFileTransfer.GetTransferDisplayName(item.Path);
                operationCancellationToken.ThrowIfCancellationRequested();
                await SendFileTransferStatusAsync(
                    stream,
                    session,
                    writeLock,
                    false,
                    $"回传远端文件失败：{(string.IsNullOrWhiteSpace(fileName) ? item.Path : fileName)} - {ex.Message}",
                    wireCancellationToken);
            }
        }

        operationCancellationToken.ThrowIfCancellationRequested();
        await SendFileTransferStatusAsync(
            stream,
            session,
            writeLock,
            failedFiles == 0 && sentFiles > 0,
            FormatClipboardFileReturnResult(plan, sentFiles, failedFiles, archivedDirectories),
            wireCancellationToken);
    }

    private static async Task<bool> SendTransferItemToViewerAsync(
        RemoteFilePasteItem item,
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        bool sendChecksum,
        bool sendCancel,
        CancellationToken operationCancellationToken,
        CancellationToken wireCancellationToken)
    {
        string? temporaryArchivePath = null;
        try
        {
            string sourcePath = item.Path;
            string transferFileName = Path.GetFileName(sourcePath);
            bool archivedDirectory = item.Kind == RemoteFilePasteItemKind.Directory;
            if (archivedDirectory)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException("文件夹不存在。");
                }

                transferFileName = RemoteFileTransfer.CreateDirectoryArchiveFileName(sourcePath);
                operationCancellationToken.ThrowIfCancellationRequested();
                await SendFileTransferStatusAsync(
                    stream,
                    session,
                    writeLock,
                    true,
                    $"正在打包回传文件夹：{transferFileName}",
                    wireCancellationToken);
                operationCancellationToken.ThrowIfCancellationRequested();
                temporaryArchivePath = RemoteFileTransfer.CreateTemporaryDirectoryArchive(
                    sourcePath,
                    operationCancellationToken);
                sourcePath = temporaryArchivePath;
            }

            operationCancellationToken.ThrowIfCancellationRequested();
            await SendFileToViewerAsync(
                sourcePath,
                stream,
                session,
                writeLock,
                operationCancellationToken,
                sendChecksum,
                sendCancel,
                transferFileName,
                transferCancelNotificationToken: wireCancellationToken);
            operationCancellationToken.ThrowIfCancellationRequested();
            return archivedDirectory;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryArchivePath))
            {
                RemoteFileTransfer.TryDeleteTemporaryFile(temporaryArchivePath);
            }
        }
    }

    internal static async Task SendFileToViewerAsync(
        string path,
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken,
        bool sendChecksum,
        bool sendCancel,
        string? transferFileName = null,
        bool remoteUpdate = false,
        CancellationToken transferCancelNotificationToken = default)
    {
        CancellationToken wireCancellationToken = transferCancelNotificationToken.CanBeCanceled
            ? transferCancelNotificationToken
            : cancellationToken;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        string fileName = string.IsNullOrWhiteSpace(transferFileName)
            ? Path.GetFileName(path)
            : transferFileName.Trim();
        if (remoteUpdate)
        {
            RemoteUpdater.ValidateRemoteUpdatePackageName(fileName);
        }

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            RemoteMessageCodec.RecommendedFileTransferChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        RemoteFileTransferSourceSnapshot sourceSnapshot = RemoteFileTransfer.CaptureSourceSnapshot(path, input);
        long fileLength = sourceSnapshot.Length;
        if (fileLength > RemoteMessageCodec.MaxFileTransferBytes)
        {
            throw new InvalidOperationException($"文件超过传输上限：{RemoteFileTransfer.FormatBytes(RemoteMessageCodec.MaxFileTransferBytes)}。");
        }

        if (remoteUpdate)
        {
            RemoteUpdater.ValidateRemoteUpdatePackageLength(fileLength);
        }

        string transferId = Guid.NewGuid().ToString("N");
        bool transferStarted = false;
        using IncrementalHash? checksum = sendChecksum
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendFileTransferStatusAsync(
                stream,
                session,
                writeLock,
                true,
                remoteUpdate
                    ? $"正在回传远端更新包：{fileName} ({RemoteFileTransfer.FormatBytes(fileLength)})"
                    : $"正在回传文件：{fileName} ({RemoteFileTransfer.FormatBytes(fileLength)})",
                wireCancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                remoteUpdate
                    ? RemoteMessageCodec.EncodeRemoteUpdateStart(transferId, fileName, fileLength)
                    : RemoteMessageCodec.EncodeFileTransferStart(transferId, fileName, fileLength),
                session,
                writeLock,
                wireCancellationToken);

            transferStarted = true;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(RemoteMessageCodec.RecommendedFileTransferChunkBytes);
            byte[] chunkPayload = ArrayPool<byte>.Shared.Rent(
                RemoteMessageCodec.GetFileTransferChunkPayloadLength(
                    transferId,
                    RemoteMessageCodec.RecommendedFileTransferChunkBytes));
            long offset = 0;
            int lastReportedPercent = -1;
            try
            {
                while (offset < fileLength)
                {
                    int bytesToRead = (int)Math.Min(
                        RemoteMessageCodec.RecommendedFileTransferChunkBytes,
                        fileLength - offset);
                    int read = await input.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("文件在回传过程中被截断。");
                    }

                    checksum?.AppendData(buffer.AsSpan(0, read));
                    int payloadLength = RemoteMessageCodec.WriteFileTransferChunkPayload(
                        transferId,
                        offset,
                        buffer.AsSpan(0, read),
                        chunkPayload);
                    cancellationToken.ThrowIfCancellationRequested();
                    await Protocol.WriteMessageAsync(
                        stream,
                        MessageType.Control,
                        chunkPayload.AsMemory(0, payloadLength),
                        session,
                        writeLock,
                        wireCancellationToken);

                    offset += read;
                    int percent = fileLength == 0 ? 100 : (int)Math.Min(100, offset * 100 / fileLength);
                    if (percent >= lastReportedPercent + 10 || offset == fileLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await SendFileTransferStatusAsync(
                            stream,
                            session,
                            writeLock,
                            true,
                            remoteUpdate
                                ? $"正在回传远端更新包：{fileName} {percent}%"
                                : $"正在回传文件：{fileName} {percent}%",
                            wireCancellationToken);
                        lastReportedPercent = percent;
                    }

                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkPayload);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            RemoteFileTransfer.EnsureSourceUnchanged(path, sourceSnapshot);

            if (checksum is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Protocol.WriteMessageAsync(
                    stream,
                    MessageType.Control,
                    RemoteMessageCodec.EncodeFileTransferChecksum(
                        transferId,
                        Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant()),
                    session,
                    writeLock,
                    wireCancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferComplete(transferId),
                session,
                writeLock,
                wireCancellationToken);
        }
        catch (Exception ex) when (transferStarted &&
            (ex is OperationCanceledException || RemoteFileTransfer.IsRecoverableTransferException(ex)))
        {
            await TrySendFileTransferCancelAsync(
                stream,
                session,
                writeLock,
                sendCancel,
                transferId,
                $"回传端中止：{ex.Message}",
                wireCancellationToken);
            throw;
        }
    }

    private static async Task TrySendFileTransferCancelAsync(
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        bool sendCancel,
        string transferId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!sendCancel)
        {
            return;
        }

        try
        {
            await Protocol.WriteMessageAsync(
                stream,
                MessageType.Control,
                RemoteMessageCodec.EncodeFileTransferCancel(transferId, reason),
                session,
                writeLock,
                cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or CryptographicException)
        {
        }
    }

    private static string FormatEmptyClipboardFileReturnResult(RemoteFilePastePlan plan)
    {
        if (plan.SkippedDirectories > 0 && plan.SkippedMissing == 0)
        {
            return "远端剪贴板包含文件夹，但文件夹当前不可访问。";
        }

        if (plan.SkippedMissing > 0)
        {
            return "远端剪贴板文件不存在或不可访问。";
        }

        return "远端剪贴板没有可回传的文件。";
    }

    private static string FormatClipboardFileReturnResult(RemoteFilePastePlan plan, int sentFiles, int failedFiles, int archivedDirectories)
    {
        var parts = new List<string>();
        parts.Add(sentFiles > 0 ? $"远端文件回传完成：{sentFiles} 个" : "远端文件回传失败");
        if (failedFiles > 0)
        {
            parts.Add($"{failedFiles} 个失败");
        }

        if (archivedDirectories > 0)
        {
            parts.Add($"已打包 {archivedDirectories} 个文件夹为 zip");
        }

        if (plan.SkippedDirectories > 0)
        {
            parts.Add($"跳过 {plan.SkippedDirectories} 个文件夹");
        }

        if (plan.SkippedMissing > 0)
        {
            parts.Add($"跳过 {plan.SkippedMissing} 个不可访问项");
        }

        if (plan.Truncated)
        {
            parts.Add($"一次最多回传 {MaxClipboardFileReturnCount} 个文件");
        }

        return string.Join("，", parts);
    }

    private static async Task SetRemoteClipboardAsync(
        string text,
        NetworkStream stream,
        SecureSession session,
        SemaphoreSlim writeLock,
        Action<string> clipboardLog,
        CancellationToken cancellationToken)
    {
        try
        {
            await ClipboardTextService.SetTextAsync(text, () => !cancellationToken.IsCancellationRequested);
            clipboardLog("控制端已写入远程文本剪贴板。");
            byte[] payload = RemoteMessageCodec.EncodeClipboardStatus(true, "已写入远程剪贴板");
            await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
        }
        catch (Exception ex)
        {
            byte[] payload = RemoteMessageCodec.EncodeClipboardStatus(false, $"写入远程剪贴板失败：{ex.Message}");
            await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
        }
    }

    private static async Task SendClipboardTextAsync(
        NetworkStream stream,
        SecureSession session,
        SessionWritePriority writePriority,
        SemaphoreSlim writeLock,
        Action<string> clipboardLog,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = await ClipboardTextService.GetTextAsync();
            clipboardLog("控制端读取了远程文本剪贴板。");
            payload = RemoteMessageCodec.EncodeClipboardText(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            payload = RemoteMessageCodec.EncodeClipboardStatus(
                false,
                $"读取远程剪贴板失败：{ex.Message}");
        }

        using (writePriority.BeginControlWritePriority())
        {
            await Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, cancellationToken);
        }
    }

    private static Task<bool?> WaitForPendingClipboardInputAsync(
        ViewerSessionState viewerState,
        CancellationToken cancellationToken,
        bool consumeMarker = true)
    {
        return WaitForPendingClipboardInputAsync(
            viewerState,
            Environment.TickCount64,
            static (baseline, token) => ClipboardTextService.WaitForClipboardSequenceChangeAsync(baseline, token),
            cancellationToken,
            consumeMarker);
    }

    internal static async Task<bool?> WaitForPendingClipboardInputAsync(
        ViewerSessionState viewerState,
        long nowMilliseconds,
        Func<uint, CancellationToken, Task<bool>> waitForSequenceChangeAsync,
        CancellationToken cancellationToken = default,
        bool consumeMarker = true)
    {
        ArgumentNullException.ThrowIfNull(viewerState);
        ArgumentNullException.ThrowIfNull(waitForSequenceChangeAsync);

        uint? baseline = consumeMarker
            ? viewerState.TakePendingClipboardInputSequence(
                nowMilliseconds,
                ClipboardInputSequenceMarkerMaxAgeMs,
                out bool expired)
            : viewerState.PeekPendingClipboardInputSequence(
                nowMilliseconds,
                ClipboardInputSequenceMarkerMaxAgeMs,
                out expired);
        if (baseline is null)
        {
            return null;
        }

        if (expired)
        {
            return false;
        }

        return await waitForSequenceChangeAsync(
            baseline.Value,
            cancellationToken);
    }

    private static async Task IgnoreStopExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
        }
    }

    private static bool IsStopException(Exception ex)
    {
        return ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException;
    }

    private void TrackClientTask(Task task)
    {
        lock (_clientStateLock)
        {
            _clientTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_clientStateLock)
                {
                    _clientTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task[] SnapshotClientTasks()
    {
        lock (_clientStateLock)
        {
            return _clientTasks.ToArray();
        }
    }

    private void TrackActiveClient(TcpClient client)
    {
        lock (_clientStateLock)
        {
            _activeClients.Add(client);
        }
    }

    private void UntrackActiveClient(TcpClient client)
    {
        lock (_clientStateLock)
        {
            _activeClients.Remove(client);
        }
    }

    private void CloseActiveClients()
    {
        TcpClient[] clients;
        lock (_clientStateLock)
        {
            clients = _activeClients.ToArray();
        }

        foreach (TcpClient client in clients)
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal sealed class AdaptiveCaptureController
    {
        private const int QualityStep = 5;
        private const int SevereQualityStep = 10;
        private const int FpsStep = 5;
        private const int ScaleStep = 25;
        private const int ReadableMinQuality = 55;
        private const int ReadableMinScalePercent = 75;
        private const int ComfortableWindowsBeforeRecovery = 2;
        private const int SevereWindowsBeforeScaleReduction = 3;

        private readonly bool _enabled;
        private readonly int _targetFps;
        private readonly int _maxQuality;
        private readonly int _minQuality;
        private readonly int _minFps;
        private readonly int _targetScalePercent;
        private readonly int _minScalePercent;
        private int _comfortableWindows;
        private int _consecutiveSevereWindows;

        public AdaptiveCaptureController(
            int targetFps,
            int maxQuality,
            int scalePercent,
            bool enabled,
            int? initialScalePercent = null,
            int? minimumQuality = null)
        {
            _enabled = enabled;
            _targetFps = Math.Clamp(targetFps, 1, 60);
            _maxQuality = Math.Clamp(maxQuality, 30, 90);
            _minQuality = Math.Min(
                _maxQuality,
                Math.Max(30, minimumQuality ?? ReadableMinQuality));
            _minFps = Math.Min(_targetFps, Math.Max(10, _targetFps / 3));
            _targetScalePercent = Math.Clamp(scalePercent, 25, 100);
            _minScalePercent = Math.Min(_targetScalePercent, ReadableMinScalePercent);
            CurrentFps = _targetFps;
            CurrentQuality = _maxQuality;
            CurrentScalePercent = Math.Clamp(initialScalePercent ?? _targetScalePercent, _minScalePercent, _targetScalePercent);
        }

        public int CurrentFps { get; private set; }

        public int CurrentQuality { get; private set; }

        public int CurrentScalePercent { get; private set; }

        public string? Update(
            double actualFps,
            double averageFrameMilliseconds,
            double averageSendMilliseconds,
            LowLatencyVideoNetworkSnapshot networkSnapshot = default,
            bool sourceWasIdle = false)
        {
            if (sourceWasIdle)
            {
                // Low output FPS is intentional while identical frames are
                // suppressed, not evidence of congestion or insufficient GPU.
                _comfortableWindows = 0;
                _consecutiveSevereWindows = 0;
                return null;
            }

            if (!_enabled)
            {
                return null;
            }

            double frameBudgetMilliseconds = 1000d / CurrentFps;
            bool severelyOverloaded =
                networkSnapshot.IsSeverelyCongested ||
                averageFrameMilliseconds > frameBudgetMilliseconds * 1.35 ||
                averageSendMilliseconds > frameBudgetMilliseconds * 0.9;
            bool networkBound =
                networkSnapshot.IsCongested ||
                averageSendMilliseconds > frameBudgetMilliseconds * 1.45;
            bool overloaded = severelyOverloaded ||
                networkSnapshot.IsCongested ||
                averageFrameMilliseconds > frameBudgetMilliseconds * 0.88 ||
                actualFps < CurrentFps * 0.88;
            bool comfortable = averageFrameMilliseconds < frameBudgetMilliseconds * 0.75 &&
                averageSendMilliseconds < frameBudgetMilliseconds * 0.55 &&
                actualFps > CurrentFps * 0.9 &&
                (!networkSnapshot.HasFeedbackSample ||
                    networkSnapshot.IsComfortable);

            _consecutiveSevereWindows = severelyOverloaded
                ? Math.Min(
                    SevereWindowsBeforeScaleReduction,
                    _consecutiveSevereWindows + 1)
                : 0;

            if (overloaded)
            {
                _comfortableWindows = 0;
                if (CurrentFps > _minFps)
                {
                    CurrentFps = Math.Max(_minFps, CurrentFps - FpsStep);
                    return $"自适应调整：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS（优先降低帧率保文字清晰度）";
                }

                if (CurrentQuality > _minQuality)
                {
                    int step = severelyOverloaded ? SevereQualityStep : QualityStep;
                    CurrentQuality = Math.Max(_minQuality, CurrentQuality - step);
                    return $"自适应调整：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS（降低画质保帧率与延迟）";
                }

                if (severelyOverloaded &&
                    _consecutiveSevereWindows >=
                        SevereWindowsBeforeScaleReduction &&
                    CurrentScalePercent > _minScalePercent)
                {
                    CurrentScalePercent = Math.Max(_minScalePercent, CurrentScalePercent - ScaleStep);
                    _consecutiveSevereWindows = 0;
                    return networkBound
                        ? $"自适应调整：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS（持续严重拥塞，最后降低分辨率保网络延迟）"
                        : $"自适应调整：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS（持续严重压力，最后降低分辨率）";
                }

                return null;
            }

            if (!comfortable)
            {
                _comfortableWindows = 0;
                return null;
            }

            _comfortableWindows++;
            if (_comfortableWindows < ComfortableWindowsBeforeRecovery)
            {
                return null;
            }

            _comfortableWindows = 0;
            if (CurrentScalePercent < _targetScalePercent &&
                CanRestoreScale(
                    averageFrameMilliseconds,
                    averageSendMilliseconds))
            {
                CurrentScalePercent = Math.Min(_targetScalePercent, CurrentScalePercent + ScaleStep);
                return $"自适应恢复：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS（优先恢复分辨率）";
            }

            if (CurrentQuality < _maxQuality)
            {
                CurrentQuality = Math.Min(_maxQuality, CurrentQuality + QualityStep);
                return $"自适应恢复：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS";
            }

            if (CurrentFps < _targetFps)
            {
                CurrentFps = Math.Min(_targetFps, CurrentFps + FpsStep);
                return $"自适应恢复：JPEG {CurrentQuality}，分辨率 {ScreenCaptureService.FormatScaleMode(CurrentScalePercent)}，{CurrentFps} FPS";
            }

            return null;
        }

        private bool CanRestoreScale(double averageFrameMilliseconds, double averageSendMilliseconds)
        {
            int nextScalePercent = Math.Min(
                _targetScalePercent,
                CurrentScalePercent + ScaleStep);
            double scaleRatio =
                nextScalePercent /
                (double)CurrentScalePercent;
            double estimatedPixelCostGrowth =
                scaleRatio * scaleRatio;
            double targetFrameBudgetMilliseconds =
                1000d / _targetFps;

            // A low current FPS creates a deliberately loose frame budget.
            // Using it to restore 75% -> 100% can accept a frame that is cheap
            // only at 10 FPS, then immediately overload at the requested rate.
            // Evaluate the next scale against the target FPS and conservatively
            // project capture/encode/send cost by the increase in pixel area.
            return averageFrameMilliseconds *
                    estimatedPixelCostGrowth <
                    targetFrameBudgetMilliseconds * 0.75 &&
                averageSendMilliseconds *
                    estimatedPixelCostGrowth <
                    targetFrameBudgetMilliseconds * 0.55;
        }
    }

    private sealed class CaptureSessionState : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly object _captureServiceLock = new();
        private CancellationTokenSource _targetChanged = new();
        private ScreenCaptureTarget _target;
        private ScreenCaptureService _captureService;
        private Rectangle _lastCaptureBounds;
        private Size _lastFrameSize;
        private bool _isTargetAvailable;
        private int _lastScalePercent;
        private int _targetVersion;
        private readonly Action<int>?
            _targetGenerationChanged;

        public CaptureSessionState(
            ScreenCaptureTarget target,
            int scalePercent,
            Action<int>? targetGenerationChanged = null)
        {
            _targetGenerationChanged =
                targetGenerationChanged;
            _target = target;
            _lastScalePercent = Math.Clamp(scalePercent, 25, 100);
            _captureService = new ScreenCaptureService(target);
            ScreenCaptureTargetAvailability availability =
                _captureService.GetTargetAvailability(
                    forceRefresh: true);
            _isTargetAvailable = availability.IsAvailable;
            _lastCaptureBounds = availability.Bounds;
            if (_isTargetAvailable)
            {
                _target = RefreshTargetMetadata(
                    _target,
                    _lastCaptureBounds);
            }

            _lastFrameSize = ScreenCaptureService.CalculateFrameSize(_lastCaptureBounds, _lastScalePercent);
            _targetGenerationChanged?.Invoke(
                _targetVersion);
        }

        public ScreenCaptureTarget CurrentTarget
        {
            get
            {
                lock (_syncRoot)
                {
                    return _target;
                }
            }
        }

        public Rectangle LastCaptureBounds
        {
            get
            {
                lock (_syncRoot)
                {
                    return _lastCaptureBounds;
                }
            }
        }

        public Size LastFrameSize
        {
            get
            {
                lock (_syncRoot)
                {
                    return _lastFrameSize;
                }
            }
        }

        public bool IsTargetAvailable
        {
            get
            {
                lock (_syncRoot)
                {
                    return _isTargetAvailable;
                }
            }
        }

        public bool TryGetInputMappingSnapshot(
            RemoteInputCommand command,
            out Rectangle bounds,
            out Size frameSize)
        {
            lock (_syncRoot)
            {
                if (!_isTargetAvailable &&
                    RequiresAvailableCaptureTarget(command))
                {
                    bounds = default;
                    frameSize = default;
                    return false;
                }

                // Even while unavailable, non-pointer input remains safe and
                // lets KeyUp clean up a key pressed before the unplug. The
                // retained bounds are always the last physical bounds, never
                // the aggregate desktop.
                bounds = _lastCaptureBounds;
                frameSize = _lastFrameSize;
                return true;
            }
        }

        public int TargetVersion => Volatile.Read(ref _targetVersion);

        public bool IsCurrentTargetGeneration(
            int generation) =>
            Volatile.Read(ref _targetVersion) ==
                generation;

        public CancellationToken GetTargetChangeToken(
            int expectedVersion) =>
            GetGenerationChangeToken(
                _syncRoot,
                () => _targetVersion,
                expectedVersion,
                () => _targetChanged.Token);

        public (
            ScreenCaptureTarget Target,
            Rectangle Bounds,
            bool IsAvailable,
            int Version) GetTargetSnapshot()
        {
            lock (_syncRoot)
            {
                return (
                    _target,
                    _lastCaptureBounds,
                    _isTargetAvailable,
                    _targetVersion);
            }
        }

        public CaptureTargetStateSnapshot
            GetPublicationSnapshot()
        {
            lock (_syncRoot)
            {
                return new CaptureTargetStateSnapshot(
                    ScreenCaptureService.ToInfo(_target),
                    _isTargetAvailable,
                    _targetVersion);
            }
        }

        public bool IsCurrentPublicationSnapshot(
            CaptureTargetStateSnapshot snapshot)
        {
            lock (_syncRoot)
            {
                return _targetVersion == snapshot.Generation &&
                    _isTargetAvailable == snapshot.IsAvailable &&
                    string.Equals(
                        _target.Id,
                        snapshot.Target.Id,
                        StringComparison.OrdinalIgnoreCase);
            }
        }

        public ScreenCaptureResult CaptureJpeg(
            int jpegQuality,
            int scalePercent,
            out int targetGeneration)
        {
            int clampedScalePercent = Math.Clamp(scalePercent, 25, 100);
            lock (_captureServiceLock)
            {
                ScreenCaptureTargetAvailability availability =
                    _captureService.GetTargetAvailability();
                ApplyTargetAvailability(availability);
                if (!availability.IsAvailable)
                {
                    throw new ScreenCaptureTargetUnavailableException(
                        _target.Id);
                }

                ScreenCaptureResult capture = _captureService.CaptureJpeg(
                    jpegQuality,
                    clampedScalePercent);

                lock (_syncRoot)
                {
                    if (_lastCaptureBounds != capture.Bounds)
                    {
                        PublishTargetChangeLocked();
                        _target = RefreshTargetMetadata(
                            _target,
                            capture.Bounds);
                    }

                    _lastCaptureBounds = capture.Bounds;
                    _lastFrameSize = capture.FrameSize;
                    _lastScalePercent = clampedScalePercent;
                    targetGeneration =
                        _targetVersion;
                }

                return capture;
            }
        }

        public bool RefreshCaptureBounds() =>
            RefreshCaptureBounds(
                forceRefresh: false);

        public bool RefreshCaptureBounds(
            bool forceRefresh)
        {
            lock (_captureServiceLock)
            {
                return ApplyTargetAvailability(
                    _captureService.GetTargetAvailability(
                        forceRefresh));
            }
        }

        public CaptureTargetStateSnapshot RefreshCaptureTopology(
            IReadOnlyList<ScreenCaptureTarget> availableTargets)
        {
            ArgumentNullException.ThrowIfNull(availableTargets);
            lock (_captureServiceLock)
            {
                ApplyTargetAvailability(
                    _captureService.GetTargetAvailability(
                        availableTargets),
                    availableTargets);
                return GetPublicationSnapshot();
            }
        }

        public CaptureTargetStateSnapshot
            RefreshCaptureTopologyWithAutomaticFallback(
                IReadOnlyList<ScreenCaptureTarget> availableTargets)
        {
            CaptureTargetStateSnapshot snapshot =
                RefreshCaptureTopology(availableTargets);
            if (snapshot.IsAvailable)
            {
                return snapshot;
            }

            ScreenCaptureTarget? fallback =
                ScreenCaptureService
                    .ChooseUnambiguousFallbackTarget(
                        availableTargets,
                        snapshot.Target.Id);
            if (fallback is null)
            {
                return snapshot;
            }

            ChangeTarget(fallback.Id);
            return GetPublicationSnapshot();
        }

        public bool TryObserveEncodedFrame(
            int expectedTargetVersion,
            Rectangle captureBounds,
            Size frameSize,
            int scalePercent)
        {
            lock (_syncRoot)
            {
                if (_targetVersion != expectedTargetVersion)
                {
                    return false;
                }

                _lastCaptureBounds = captureBounds;
                _lastFrameSize = frameSize;
                _lastScalePercent = Math.Clamp(scalePercent, 25, 100);
                return true;
            }
        }

        public ScreenCaptureTarget ChangeTarget(string targetId)
        {
            IReadOnlyList<ScreenCaptureTarget> targets =
                ScreenCaptureService.GetAvailableTargets();
            ScreenCaptureTarget? requestedTarget =
                targets.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Id,
                        targetId,
                        StringComparison.OrdinalIgnoreCase));
            if (requestedTarget is null)
            {
                // A target list can become stale between publication and the
                // viewer's selection. Keep the current explicit target (and
                // its unavailable state) instead of silently choosing the
                // primary or aggregate desktop.
                lock (_syncRoot)
                {
                    return _target;
                }
            }

            ScreenCaptureTarget target =
                ScreenCaptureService.ChooseLowLatencyStartupTarget(
                    requestedTarget,
                    targets);
            lock (_syncRoot)
            {
                if (ShouldReuseCaptureTarget(
                        _target,
                        _lastCaptureBounds,
                        target))
                {
                    return _target;
                }
            }

            var captureService = new ScreenCaptureService(target);
            ScreenCaptureTargetAvailability availability =
                captureService.GetTargetAvailability(
                    forceRefresh: true);
            ScreenCaptureService oldCaptureService;

            lock (_captureServiceLock)
            {
                lock (_syncRoot)
                {
                    oldCaptureService = _captureService;
                    _target = target;
                    _captureService = captureService;
                    _isTargetAvailable =
                        availability.IsAvailable;
                    _lastCaptureBounds =
                        availability.Bounds;
                    _lastFrameSize = ScreenCaptureService.CalculateFrameSize(
                        availability.Bounds,
                        _lastScalePercent);
                    PublishTargetChangeLocked();
                }
            }

            oldCaptureService.Dispose();
            return target;
        }

        private bool ApplyTargetAvailability(
            ScreenCaptureTargetAvailability availability,
            IReadOnlyList<ScreenCaptureTarget>? availableTargets = null)
        {
            lock (_syncRoot)
            {
                if (!ShouldAdvanceCaptureTargetGeneration(
                        _isTargetAvailable,
                        _lastCaptureBounds,
                        availability))
                {
                    return false;
                }

                _isTargetAvailable =
                    availability.IsAvailable;
                if (availability.IsAvailable)
                {
                    _lastCaptureBounds =
                        availability.Bounds;
                    _lastFrameSize =
                        ScreenCaptureService.CalculateFrameSize(
                            availability.Bounds,
                            _lastScalePercent);
                    _target = RefreshTargetMetadata(
                        _target,
                        availability.Bounds,
                        availableTargets);
                }

                PublishTargetChangeLocked();
                return true;
            }
        }

        private static ScreenCaptureTarget RefreshTargetMetadata(
            ScreenCaptureTarget target,
            Rectangle bounds,
            IReadOnlyList<ScreenCaptureTarget>? availableTargets = null)
        {
            ScreenCaptureTarget? current =
                (availableTargets ??
                    ScreenCaptureService.GetAvailableTargets())
                    .FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.Id,
                            target.Id,
                            StringComparison.OrdinalIgnoreCase));
            return (current ?? target) with
            {
                Bounds = bounds
            };
        }

        private void PublishTargetChangeLocked()
        {
            int generation =
                Interlocked.Increment(
                    ref _targetVersion);
            _targetGenerationChanged?.Invoke(
                generation);
            CancellationTokenSource changed = _targetChanged;
            _targetChanged = new();
            // Cancellation is synchronous, but linked selection tokens do not
            // call back into CaptureSessionState. Swapping under the same
            // lock prevents a newly current run from accidentally acquiring
            // the previous generation's token.
            changed.Cancel();
        }

        public void Dispose()
        {
            CancellationTokenSource targetChanged;
            lock (_syncRoot)
            {
                targetChanged = _targetChanged;
                _targetChanged = new();
            }

            targetChanged.Cancel();

            lock (_captureServiceLock)
            {
                _captureService.Dispose();
            }
        }
    }
}
