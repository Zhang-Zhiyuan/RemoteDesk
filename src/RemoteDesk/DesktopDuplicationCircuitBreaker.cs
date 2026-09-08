using System.Diagnostics;

namespace RemoteDesk;

internal sealed class DesktopDuplicationCircuitBreaker
{
    internal static readonly TimeSpan InitialBreakDuration =
        TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumBreakDuration =
        TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private readonly Func<long> _getTimestamp;
    private readonly long _timestampFrequency;

    private long _retryNotBefore;
    private int _consecutiveFailures;
    private bool _probeInProgress;

    public DesktopDuplicationCircuitBreaker()
        : this(
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency)
    {
    }

    internal DesktopDuplicationCircuitBreaker(
        Func<long> getTimestamp,
        long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(getTimestamp);
        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampFrequency));
        }

        _getTimestamp = getTimestamp;
        _timestampFrequency = timestampFrequency;
    }

    public bool TryBeginProbe(
        out TimeSpan retryAfter)
    {
        long now = _getTimestamp();
        lock (_sync)
        {
            if (_probeInProgress)
            {
                retryAfter = TimeSpan.Zero;
                return false;
            }

            if (_retryNotBefore > now)
            {
                retryAfter = TimestampDeltaToTimeSpan(
                    _retryNotBefore - now);
                return false;
            }

            _probeInProgress = true;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public TimeSpan RecordFailure()
    {
        long now = _getTimestamp();
        lock (_sync)
        {
            if (!_probeInProgress)
            {
                throw new InvalidOperationException(
                    "No Desktop Duplication probe is active.");
            }

            return RecordFailureCore(
                now,
                completeProbe: true);
        }
    }

    public TimeSpan RecordRuntimeFailure()
    {
        long now = _getTimestamp();
        lock (_sync)
        {
            return RecordFailureCore(
                now,
                completeProbe: false);
        }
    }

    public void RecordSuccess()
    {
        lock (_sync)
        {
            if (!_probeInProgress)
            {
                throw new InvalidOperationException(
                    "No Desktop Duplication probe is active.");
            }

            ResetCore();
        }
    }

    public void RecordStartupSuccess()
    {
        lock (_sync)
        {
            if (!_probeInProgress)
            {
                throw new InvalidOperationException(
                    "No Desktop Duplication probe is active.");
            }

            _probeInProgress = false;
            _retryNotBefore = 0;
        }
    }

    public bool RecordStableRuntimeSuccess()
    {
        lock (_sync)
        {
            if (_probeInProgress)
            {
                return false;
            }

            ResetCore();
            return true;
        }
    }

    public void CancelProbe()
    {
        lock (_sync)
        {
            _probeInProgress = false;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetCore();
        }
    }

    internal int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    private long AddDuration(
        long timestamp,
        TimeSpan duration)
    {
        double rawDelta =
            duration.TotalSeconds *
            _timestampFrequency;
        long delta = rawDelta >= long.MaxValue
            ? long.MaxValue
            : Math.Max(
                1,
                checked((long)Math.Ceiling(rawDelta)));
        return timestamp > long.MaxValue - delta
            ? long.MaxValue
            : timestamp + delta;
    }

    private TimeSpan TimestampDeltaToTimeSpan(
        long timestampDelta)
    {
        return timestampDelta <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                timestampDelta /
                (double)_timestampFrequency);
    }

    private void ResetCore()
    {
        _retryNotBefore = 0;
        _consecutiveFailures = 0;
        _probeInProgress = false;
    }

    private TimeSpan RecordFailureCore(
        long now,
        bool completeProbe)
    {
        if (completeProbe)
        {
            _probeInProgress = false;
        }

        _consecutiveFailures = Math.Min(
            _consecutiveFailures + 1,
            3);
        double multiplier =
            1 << (_consecutiveFailures - 1);
        TimeSpan breakDuration = TimeSpan.FromMilliseconds(
            Math.Min(
                MaximumBreakDuration.TotalMilliseconds,
                InitialBreakDuration.TotalMilliseconds *
                    multiplier));
        _retryNotBefore = AddDuration(
            now,
            breakDuration);
        return breakDuration;
    }
}
