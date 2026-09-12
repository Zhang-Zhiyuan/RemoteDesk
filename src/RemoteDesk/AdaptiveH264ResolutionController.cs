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
    private double _pressureSeconds;
    private double _comfortableSeconds;
    private double _profileSeconds;
    private bool _hasRecovered;
    private bool _recoveryFailed;

    public AdaptiveH264ResolutionController(Size requestedSize, int targetFps, bool enabled)
    {
        RequestedSize = requestedSize;
        ReducedSize = FitFullHd(requestedSize);
        OutputSize = requestedSize;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _enabled = enabled && requestedSize != ReducedSize;
        _nativeRateGrowth = Math.Max(1d,
            FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(requestedSize, _targetFps) /
            (double)FfmpegDesktopH264Capture.CalculateBitrateBitsPerSecond(ReducedSize, _targetFps));
    }

    internal Size RequestedSize { get; }
    internal Size ReducedSize { get; }
    internal Size OutputSize { get; private set; }
    internal bool IsReduced => OutputSize != RequestedSize;

    internal static Size FitFullHd(Size requested) => RemoteHostServer.FitEvenSizeWithin(
        requested, requested.Height > requested.Width ? new Size(1080, 1920) : new Size(1920, 1080));

    /// <returns>True only when the encoder should restart at OutputSize.</returns>
    internal bool Observe(TimeSpan elapsed, double actualFps, double averageSocketWriteMilliseconds,
        double streamMegabitsPerSecond, bool reliableVideo)
    {
        if (!_enabled) return false;
        double seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0 ||
            !double.IsFinite(actualFps) || actualFps <= 0 ||
            !double.IsFinite(averageSocketWriteMilliseconds) || averageSocketWriteMilliseconds < 0 ||
            !double.IsFinite(streamMegabitsPerSecond) || streamMegabitsPerSecond <= 0)
        {
            _pressureSeconds = _comfortableSeconds = 0;
            return false;
        }

        _profileSeconds += seconds;
        // A single blocked write cannot masquerade as several independent
        // observation windows. Normal samples span roughly one second.
        double evidenceSeconds = Math.Min(seconds, 1.5);
        double frameBudget = 1000d / _targetFps;
        bool pressure = reliableVideo && actualFps < _targetFps * 0.80 &&
            averageSocketWriteMilliseconds > frameBudget * 1.10;
        if (!IsReduced)
        {
            _pressureSeconds = pressure ? _pressureSeconds + evidenceSeconds : 0;
            if (_pressureSeconds < PressureSecondsBeforeReduction) return false;
            if (_hasRecovered)
                _recoveryFailed = true;
            OutputSize = ReducedSize;
            ResetEvidence();
            return true;
        }

        // Extrapolate the cost of the native bitrate, not the deliberately
        // smaller current payload. This guides a bounded recovery trial, not
        // a measurement of unused link capacity. A failed trial locks this profile.
        bool comfortable = reliableVideo && actualFps >= _targetFps * 0.95 &&
            averageSocketWriteMilliseconds * _nativeRateGrowth < frameBudget * 0.60;
        _comfortableSeconds = comfortable ? _comfortableSeconds + evidenceSeconds : 0;
        // Smooth low-rate writes do not measure spare WAN capacity. Permit one
        // recovery trial per session/profile; if it congests again, keep the
        // working size until reconnect or an explicit capture-setting change.
        // Otherwise a saturated link stalls periodically forever (45/90/180s).
        if (_recoveryFailed || _profileSeconds < MinimumReducedHoldSeconds ||
            _comfortableSeconds < ComfortableSecondsBeforeRecovery) return false;
        OutputSize = RequestedSize;
        _hasRecovered = true;
        ResetEvidence();
        return true;
    }

    private void ResetEvidence()
    {
        _pressureSeconds = _comfortableSeconds = _profileSeconds = 0;
    }
}
