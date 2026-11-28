namespace RemoteDesk;

/// <summary>
/// A session-local spatial fallback for a bandwidth-bound reliable video stream.
/// Socket backpressure is not RTT. Low FPS alone (e.g. a static desktop or a slow
/// encoder) must never trigger a resolution change. The configured size remains
/// the ceiling; neither the Windows display mode nor the saved preference changes.
/// </summary>
internal sealed class AdaptiveH264ResolutionController
{
    internal const double PressureSecondsBeforeReduction = 3;
    internal const double ComfortableSecondsBeforeRecovery = 10;
    internal const double MinimumReducedHoldSeconds = 45;

    private readonly bool _enabled;
    private readonly int _targetFps;
    private readonly double _nativeRateGrowth;
    private readonly double _nativeBitrate;
    private double _confirmedSeconds;
    private bool _usedConfirmedRetry, _receiverConfirmedProfile;
    private double _pressureSeconds;
    private double _comfortableSeconds;
    private double _profileSeconds;
    private bool _hasRecovered;
    private bool _recoveryFailed;

    public AdaptiveH264ResolutionController(Size requestedSize, int targetFps, bool enabled, bool startReduced = false)
    {
        RequestedSize = requestedSize;
        ReducedSize = FitFullHd(requestedSize);
        OutputSize = requestedSize;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _enabled = enabled && requestedSize != ReducedSize;
        if (_enabled && startReduced) OutputSize = ReducedSize;
        _nativeBitrate = FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(requestedSize, _targetFps);
        _nativeRateGrowth = Math.Max(1d, _nativeBitrate /
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(ReducedSize, _targetFps));
    }

    internal Size RequestedSize { get; }
    internal Size ReducedSize { get; }
    internal Size OutputSize { get; private set; }
    internal bool IsReduced => OutputSize != RequestedSize;

    internal static Size FitFullHd(Size requested) => RemoteHostServer.FitEvenSizeWithin(
        requested, requested.Height > requested.Width ? new Size(1080, 1920) : new Size(1920, 1080));

    /// <returns>True only when the encoder should restart at OutputSize.</returns>
    internal bool Observe(TimeSpan elapsed, double actualFps, double averageSocketWriteMilliseconds,
        double streamMegabitsPerSecond, bool reliableVideo, double confirmedDeliveryBitsPerSecond = 0,
        bool receiverQueueDelayed = false)
    {
        if (!_enabled) return false;
        double seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0 ||
            !double.IsFinite(actualFps) || actualFps <= 0 ||
            !double.IsFinite(averageSocketWriteMilliseconds) || averageSocketWriteMilliseconds < 0 ||
            !double.IsFinite(streamMegabitsPerSecond) || streamMegabitsPerSecond <= 0)
        {
            _pressureSeconds = _comfortableSeconds = _confirmedSeconds = 0;
            return false;
        }

        _profileSeconds += seconds;
        // A single blocked write cannot masquerade as several independent
        // observation windows. Normal samples span roughly one second.
        double evidenceSeconds = Math.Min(seconds, 1.5);
        double frameBudget = 1000d / _targetFps;
        bool pressure = reliableVideo && actualFps < _targetFps * 0.80 &&
            (averageSocketWriteMilliseconds > frameBudget * 1.10 || (_receiverConfirmedProfile && receiverQueueDelayed));
        if (!IsReduced)
        {
            _pressureSeconds = pressure ? _pressureSeconds + evidenceSeconds : 0;
            if (_pressureSeconds < PressureSecondsBeforeReduction) return false;
            if (_hasRecovered)
                _recoveryFailed = true;
            _receiverConfirmedProfile = false;
            OutputSize = ReducedSize;
            ResetEvidence();
            return true;
        }

        // Extrapolate the cost of the native bitrate, not the deliberately
        // smaller current payload. This guides a bounded recovery trial, not
        // a measurement of unused link capacity.
        bool comfortable = reliableVideo && actualFps >= _targetFps * 0.95 &&
            averageSocketWriteMilliseconds * _nativeRateGrowth < frameBudget * 0.60;
        _comfortableSeconds = comfortable ? _comfortableSeconds + evidenceSeconds : 0;
        bool confirmed = reliableVideo && seconds <= 2 && double.IsFinite(confirmedDeliveryBitsPerSecond) &&
            confirmedDeliveryBitsPerSecond >= _nativeBitrate * 1.25 && actualFps >= Math.Min(8, _targetFps * .9) &&
            averageSocketWriteMilliseconds * _nativeRateGrowth < frameBudget * .60;
        _confirmedSeconds = confirmed ? _confirmedSeconds + evidenceSeconds : 0;
        // Fresh receiver evidence can promote earlier, or justify one additional
        // trial after a previous failure. Local write timing alone cannot do so.
        bool confirmedTrial = _confirmedSeconds >= 8 && (!_recoveryFailed ||
            (!_usedConfirmedRetry && _profileSeconds >= MinimumReducedHoldSeconds));
        if (!confirmedTrial && (_recoveryFailed || _profileSeconds < MinimumReducedHoldSeconds ||
            _comfortableSeconds < ComfortableSecondsBeforeRecovery)) return false;
        if (_recoveryFailed) _usedConfirmedRetry = true;
        _receiverConfirmedProfile = confirmedTrial;
        OutputSize = RequestedSize;
        _hasRecovered = true;
        ResetEvidence();
        return true;
    }

    private void ResetEvidence()
    {
        _pressureSeconds = _comfortableSeconds = _profileSeconds = _confirmedSeconds = 0;
    }
}
