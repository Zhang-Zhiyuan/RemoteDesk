using System.Diagnostics;

namespace RemoteDesk;

/// <summary>
/// Records successful remote-input injection without coupling the input path
/// to the capture implementation.
/// </summary>
internal sealed class RemoteInteractionActivity
{
    private long _lastActivityAt;

    public long LastActivityAt =>
        Volatile.Read(ref _lastActivityAt);

    public void Record()
    {
        Record(Stopwatch.GetTimestamp());
    }

    internal void Record(long timestamp)
    {
        Volatile.Write(
            ref _lastActivityAt,
            Math.Max(1, timestamp));
    }
}

/// <summary>
/// Keeps the hardware capture clock fast enough for responsive interaction
/// while preserving the configured idle network frame rate. The high-rate
/// transmit path is available only on a healthy, feedback-controlled UDP
/// route and only for a short window after successfully injected input.
/// </summary>
internal sealed class InteractiveH264FrameController
{
    internal static readonly TimeSpan ActivityBoostWindow =
        TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan NetworkObservationInterval =
        TimeSpan.FromMilliseconds(100);
    internal const int RecoveryComfortableWindows = 2;
    internal const int HighFrameRateCongestedWindowsBeforeReduction = 2;
    internal const int
        HighFrameRateSenderPressureWindowsBeforeReduction = 10;
    internal const int HighFrameRateComfortableWindowsBeforeRecovery = 20;
    internal const int HighFrameRateWindowsBeforeAdaptation = 10;

    private const RemoteDeviceCapabilities RequiredViewerCapabilities =
        RemoteDeviceCapabilities.LowLatencyUdpVideo |
        RemoteDeviceCapabilities.UdpVideoCongestionFeedback;

    private readonly int _baseFramesPerSecond;
    private readonly int _sourceFramesPerSecond;
    private readonly long _baseFrameIntervalTicks;
    private readonly long _reducedFrameIntervalTicks;
    private long _nextBaseFrameAt;
    private int _hasTransmitted;
    private int _networkAllowsBoost;
    private int _boostActive;
    private int _shortGopRecoveryTransmitted;
    private int _recoveryComfortableWindows;
    private int _highFrameRateCongestedWindows;
    private int _highFrameRateSenderPressureWindows;
    private int _highFrameRateComfortableWindows;
    private int _highFrameRateReduced;
    private int _highFrameRateObservationWindows;
    private bool _recoveringFromUncomfortableNetwork;

    public InteractiveH264FrameController(
        int configuredFramesPerSecond,
        int sourceFramesPerSecond)
    {
        _baseFramesPerSecond =
            Math.Clamp(configuredFramesPerSecond, 1, 60);
        _sourceFramesPerSecond = Math.Clamp(
            sourceFramesPerSecond,
            _baseFramesPerSecond,
            90);
        _baseFrameIntervalTicks = Math.Max(
            1,
            checked((long)Math.Round(
                Stopwatch.Frequency /
                (double)_baseFramesPerSecond)));
        _reducedFrameIntervalTicks = Math.Max(
            _baseFrameIntervalTicks,
            checked((long)Math.Round(
                Stopwatch.Frequency /
                (double)Math.Max(
                    30,
                    _baseFramesPerSecond / 2))));
    }

    public int BaseFramesPerSecond => _baseFramesPerSecond;

    public int SourceFramesPerSecond => _sourceFramesPerSecond;

    public bool CanBoost =>
        _sourceFramesPerSecond > _baseFramesPerSecond;

    public bool NetworkAllowsBoost =>
        Volatile.Read(ref _networkAllowsBoost) != 0;

    public bool IsBoostActive =>
        Volatile.Read(ref _boostActive) != 0;

    public bool IsHighFrameRateReduced =>
        Volatile.Read(ref _highFrameRateReduced) != 0;

    public int CurrentTransmitFramesPerSecond =>
        IsHighFrameRateReduced
            ? Math.Max(30, _baseFramesPerSecond / 2)
            : IsBoostActive
            ? _sourceFramesPerSecond
            : _baseFramesPerSecond;

    public static int CalculateSourceFramesPerSecond(
        int configuredFramesPerSecond,
        bool adaptiveQuality,
        RemoteDeviceCapabilities viewerCapabilities,
        bool supportsGpuSurfaceCapture = true)
    {
        int configured = Math.Clamp(
            configuredFramesPerSecond,
            1,
            60);
        if (!adaptiveQuality ||
            !supportsGpuSurfaceCapture ||
            !SupportsFeedbackControlledUdp(viewerCapabilities) ||
            configured < 30 ||
            configured >= 60)
        {
            return configured;
        }

        if (configured == 30 &&
            viewerCapabilities.HasFlag(
                RemoteDeviceCapabilities.ShortGopH264))
        {
            return 90;
        }

        return Math.Min(60, configured * 2);
    }

    internal static bool SupportsFeedbackControlledUdp(
        RemoteDeviceCapabilities viewerCapabilities)
    {
        return (viewerCapabilities & RequiredViewerCapabilities) ==
            RequiredViewerCapabilities;
    }

    public void ObserveNetworkSnapshot(
        LowLatencyVideoNetworkSnapshot snapshot)
    {
        ObserveHighFrameRateNetworkSnapshot(snapshot);
        if (!CanBoost)
        {
            Volatile.Write(ref _networkAllowsBoost, 0);
            return;
        }

        if (!snapshot.AllowsInteractiveBoost)
        {
            Volatile.Write(ref _networkAllowsBoost, 0);
            _recoveryComfortableWindows = 0;
            if (snapshot.HasFeedbackSample)
            {
                _recoveringFromUncomfortableNetwork = true;
            }

            return;
        }

        if (!_recoveringFromUncomfortableNetwork)
        {
            Volatile.Write(ref _networkAllowsBoost, 1);
            return;
        }

        _recoveryComfortableWindows++;
        if (_recoveryComfortableWindows <
            RecoveryComfortableWindows)
        {
            return;
        }

        _recoveringFromUncomfortableNetwork = false;
        _recoveryComfortableWindows = 0;
        Volatile.Write(ref _networkAllowsBoost, 1);
    }

    public bool ShouldTransmitFrame(
        long frameProducedAt,
        long lastInputActivityAt,
        bool udpRouteActive)
    {
        if (frameProducedAt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameProducedAt));
        }

        bool boostActive = UpdateBoostState(
            frameProducedAt,
            lastInputActivityAt,
            udpRouteActive);

        // The capture source is already clocked at the configured rate when
        // no higher interactive source rate exists. Applying the same
        // deadline a second time drops a valid frame whenever the hardware
        // clock arrives a fraction early, turning small 30 Hz jitter into a
        // visible 66 ms gap.
        if (IsHighFrameRateReduced)
        {
            return ShouldTransmitAtInterval(
                frameProducedAt,
                _reducedFrameIntervalTicks);
        }

        if (!CanBoost)
        {
            RecordTransmitted(frameProducedAt);
            return true;
        }

        if (boostActive)
        {
            RecordTransmitted(frameProducedAt);
            return true;
        }

        if (Volatile.Read(ref _hasTransmitted) == 0)
        {
            RecordTransmitted(frameProducedAt);
            return true;
        }

        long nextBaseFrameAt =
            Volatile.Read(ref _nextBaseFrameAt);
        if (frameProducedAt < nextBaseFrameAt)
        {
            return false;
        }

        AdvanceBaseDeadline(
            nextBaseFrameAt,
            frameProducedAt,
            _baseFrameIntervalTicks);
        return true;
    }

    public bool ShouldTransmitGop2Frame(
        long frameProducedAt,
        long lastInputActivityAt,
        bool udpRouteActive,
        bool recoveryFrame)
    {
        if (frameProducedAt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameProducedAt));
        }

        int sourceRatio;
        if (_sourceFramesPerSecond == _baseFramesPerSecond)
        {
            sourceRatio = 1;
        }
        else if (_sourceFramesPerSecond ==
            _baseFramesPerSecond * 2)
        {
            sourceRatio = 2;
        }
        else if (_sourceFramesPerSecond ==
            _baseFramesPerSecond * 3)
        {
            sourceRatio = 3;
        }
        else
        {
            throw new InvalidOperationException(
                "GOP=2 pairing requires a source clock equal to, twice, " +
                "or three times the configured idle frame rate.");
        }

        bool boostActive = UpdateBoostState(
            frameProducedAt,
            lastInputActivityAt,
            udpRouteActive);
        if (recoveryFrame)
        {
            bool transmitRecovery =
                sourceRatio <= 2 ||
                boostActive ||
                ShouldTransmitBaseFrame(frameProducedAt);
            Volatile.Write(
                ref _shortGopRecoveryTransmitted,
                transmitRecovery ? 1 : 0);
            if (transmitRecovery &&
                (sourceRatio <= 2 || boostActive))
            {
                // At 1x/2x every IDR is one independently decodable idle
                // update. At 3x a healthy interaction takes every pair.
                RecordTransmitted(frameProducedAt);
            }

            return transmitRecovery;
        }

        bool pairedRecoveryWasTransmitted =
            Interlocked.Exchange(
                ref _shortGopRecoveryTransmitted,
                0) != 0;
        if (!pairedRecoveryWasTransmitted ||
            (sourceRatio > 1 && !boostActive))
        {
            return false;
        }

        RecordTransmitted(frameProducedAt);
        return true;
    }

    private bool ShouldTransmitBaseFrame(long frameProducedAt)
    {
        return ShouldTransmitAtInterval(
            frameProducedAt,
            _baseFrameIntervalTicks);
    }

    private bool ShouldTransmitAtInterval(
        long frameProducedAt,
        long intervalTicks)
    {
        if (Volatile.Read(ref _hasTransmitted) == 0)
        {
            RecordTransmitted(
                frameProducedAt,
                intervalTicks);
            return true;
        }

        long nextBaseFrameAt =
            Volatile.Read(ref _nextBaseFrameAt);
        if (frameProducedAt < nextBaseFrameAt)
        {
            return false;
        }

        AdvanceBaseDeadline(
            nextBaseFrameAt,
            frameProducedAt,
            intervalTicks);
        return true;
    }

    private void ObserveHighFrameRateNetworkSnapshot(
        LowLatencyVideoNetworkSnapshot snapshot)
    {
        if (_baseFramesPerSecond <= 30 ||
            _sourceFramesPerSecond !=
                _baseFramesPerSecond)
        {
            return;
        }

        if (_highFrameRateObservationWindows <
            HighFrameRateWindowsBeforeAdaptation)
        {
            _highFrameRateObservationWindows++;
            return;
        }

        if (IsHighFrameRateReduced)
        {
            if (!snapshot.IsComfortable)
            {
                _highFrameRateComfortableWindows = 0;
                return;
            }

            _highFrameRateComfortableWindows++;
            if (_highFrameRateComfortableWindows <
                HighFrameRateComfortableWindowsBeforeRecovery)
            {
                return;
            }

            _highFrameRateComfortableWindows = 0;
            _highFrameRateCongestedWindows = 0;
            _highFrameRateSenderPressureWindows = 0;
            Volatile.Write(ref _highFrameRateReduced, 0);
            Volatile.Write(ref _hasTransmitted, 0);
            return;
        }

        bool receiverIsSeverelyCongested =
            snapshot.OversizedFrameDrops > 0 ||
            snapshot.PacketLossRatio >= 0.08 ||
            snapshot.FrameAbandonRatio >= 0.30;
        bool receiverIsCongested =
            receiverIsSeverelyCongested ||
            snapshot.PacketLossRatio >= 0.02 ||
            snapshot.FrameAbandonRatio >= 0.10;
        bool senderIsPressured =
            snapshot.AbortedFrameSends > 0 ||
            snapshot.SenderQueueDropRatio >= 0.08;

        if (receiverIsSeverelyCongested)
        {
            _highFrameRateCongestedWindows =
                HighFrameRateCongestedWindowsBeforeReduction;
        }
        else if (receiverIsCongested)
        {
            _highFrameRateCongestedWindows++;
        }
        else
        {
            _highFrameRateCongestedWindows = 0;
        }

        // A latest-only sender deliberately replaces an old pending frame
        // when a short socket or Wi-Fi stall outlives one capture interval.
        // With 4K frames, one such replacement can make a single 100 ms
        // sample look 25-100% "severely congested" even when receiver loss
        // and incomplete-frame feedback remain zero. Treat that isolated
        // freshness action as a transient, while still reducing after one
        // continuous second of sender pressure. Receiver loss/abandonment
        // retains the fast two-window (or immediate severe) response.
        _highFrameRateSenderPressureWindows = senderIsPressured
            ? Math.Min(
                HighFrameRateSenderPressureWindowsBeforeReduction,
                _highFrameRateSenderPressureWindows + 1)
            : 0;

        if (_highFrameRateCongestedWindows <
                HighFrameRateCongestedWindowsBeforeReduction &&
            _highFrameRateSenderPressureWindows <
                HighFrameRateSenderPressureWindowsBeforeReduction)
        {
            return;
        }

        _highFrameRateComfortableWindows = 0;
        _highFrameRateCongestedWindows = 0;
        _highFrameRateSenderPressureWindows = 0;
        Volatile.Write(ref _highFrameRateReduced, 1);
        Volatile.Write(ref _hasTransmitted, 0);
    }

    private bool UpdateBoostState(
        long frameProducedAt,
        long lastInputActivityAt,
        bool udpRouteActive)
    {
        bool boostActive =
            CanBoost &&
            udpRouteActive &&
            NetworkAllowsBoost &&
            IsRecentInput(
                frameProducedAt,
                lastInputActivityAt);
        Volatile.Write(
            ref _boostActive,
            boostActive ? 1 : 0);
        return boostActive;
    }

    private static bool IsRecentInput(
        long frameProducedAt,
        long lastInputActivityAt)
    {
        if (lastInputActivityAt <= 0)
        {
            return false;
        }

        // Input and capture are produced on independent threads. During
        // sustained high-rate mouse motion the latest input timestamp will
        // commonly be a few milliseconds newer than the frame that is being
        // considered. Treat that as recent interaction instead of
        // oscillating the 60 FPS tier off for almost every P frame.
        long earlier = Math.Min(
            frameProducedAt,
            lastInputActivityAt);
        long later = Math.Max(
            frameProducedAt,
            lastInputActivityAt);
        return Stopwatch.GetElapsedTime(
            earlier,
            later) <= ActivityBoostWindow;
    }

    private void RecordTransmitted(long timestamp)
    {
        RecordTransmitted(
            timestamp,
            _baseFrameIntervalTicks);
    }

    private void RecordTransmitted(
        long timestamp,
        long intervalTicks)
    {
        Volatile.Write(
            ref _nextBaseFrameAt,
            SaturatingAdd(
                timestamp,
                intervalTicks));
        Volatile.Write(ref _hasTransmitted, 1);
    }

    private void AdvanceBaseDeadline(
        long currentDeadline,
        long transmittedAt,
        long intervalTicks)
    {
        long nextDeadline = currentDeadline;
        do
        {
            long advanced = SaturatingAdd(
                nextDeadline,
                intervalTicks);
            if (advanced == nextDeadline)
            {
                break;
            }

            nextDeadline = advanced;
        }
        while (nextDeadline <= transmittedAt);

        Volatile.Write(
            ref _nextBaseFrameAt,
            nextDeadline);
    }

    private static long SaturatingAdd(
        long value,
        long increment)
    {
        return long.MaxValue - value < increment
            ? long.MaxValue
            : value + increment;
    }
}
