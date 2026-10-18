using System.Diagnostics;

namespace RemoteDesk;

/// <summary>
/// Leaves headroom on a persistently bandwidth-bound TCP video stream. Admission
/// drops independently decodable frames before serialization, never reduces the
/// encoder's per-frame quality and never queues or delays a selected frame.
/// Socket-write occupancy is evidence of pressure, not an RTT measurement.
/// </summary>
internal sealed class ReliableH264FramePacer(int configuredFps, bool enabled)
{
    internal const double PressureSeconds = 3;
    internal const double RecoverySeconds = 10;
    private readonly int _maximumFps = Math.Clamp(configuredFps, 1, 60);
    private double _pressureSeconds;
    private double _comfortableSeconds;
    private long _nextFrameAt;

    internal int FramesPerSecond { get; private set; } = Math.Clamp(configuredFps, 1, 60);
    internal bool IsLimited => enabled && FramesPerSecond < _maximumFps;

    internal bool Observe(TimeSpan elapsed, double actualFps, double averageSocketWriteMilliseconds,
        bool reliableVideo)
    {
        double seconds = elapsed.TotalSeconds;
        if (!enabled || !reliableVideo || !double.IsFinite(seconds) || seconds <= 0 ||
            !double.IsFinite(actualFps) || actualFps <= 0 ||
            !double.IsFinite(averageSocketWriteMilliseconds) || averageSocketWriteMilliseconds < 0)
        {
            _pressureSeconds = _comfortableSeconds = 0;
            return false;
        }

        // Static/encoder-limited sources and CPU/lock contention are not network
        // pressure. Use only actual socket writes, not total frame-send time.
        double writeOccupancy = actualFps * averageSocketWriteMilliseconds / 1000;
        double evidence = Math.Min(seconds, 1.5);
        // After a successful reduction, a one-FPS recovery probe can refill
        // the queues while still delivering almost every requested frame.
        // Do not demand another 10% FPS loss before restoring headroom.
        bool congested = writeOccupancy >= 0.65 &&
            (IsLimited || actualFps < FramesPerSecond * 0.90);
        _pressureSeconds = congested ? _pressureSeconds + evidence : 0;
        if (_pressureSeconds >= PressureSeconds)
        {
            int next = Math.Clamp((int)Math.Floor(actualFps * 0.85), 1, FramesPerSecond);
            if (next < FramesPerSecond) return ChangeRate(next);
        }

        bool comfortable = IsLimited && actualFps >= FramesPerSecond * 0.90 && writeOccupancy <= 0.10;
        _comfortableSeconds = comfortable ? _comfortableSeconds + evidence : 0;
        // Slow, one-FPS probes avoid repeatedly refilling the TCP/TLS/relay
        // queues. No assumption that a fast small write proves spare bandwidth.
        return _comfortableSeconds >= RecoverySeconds && ChangeRate(FramesPerSecond + 1);
    }

    internal bool ShouldSendFrame(long timestamp, bool recoveryRequested = false)
    {
        if (!IsLimited) return true; // Healthy capture clocks are not paced twice.
        if (timestamp <= 0) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (!recoveryRequested && timestamp < _nextFrameAt) return false;
        long interval = Math.Max(1, Stopwatch.Frequency / FramesPerSecond);
        if (recoveryRequested || _nextFrameAt == 0)
            _nextFrameAt = timestamp + interval;
        else
            // No catch-up burst after a slow write or a static desktop.
            _nextFrameAt += ((timestamp - _nextFrameAt) / interval + 1) * interval;
        return true;
    }

    private bool ChangeRate(int fps)
    {
        FramesPerSecond = Math.Clamp(fps, 1, _maximumFps);
        _pressureSeconds = _comfortableSeconds = 0;
        _nextFrameAt = 0;
        return true;
    }
}
