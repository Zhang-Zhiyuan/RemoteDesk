namespace RemoteDesk;

// A failed decoder must not silently renegotiate a working H.264 stream to
// bandwidth-heavy JPEG. Retry at recovery frames with a bounded backoff instead.
// Host-supplied JPEG (lock screen, startup preview, legacy JPEG-only host) is
// still accepted; its arrival must not reset a failing H.264 retry loop.
internal sealed class H264DecoderRecovery(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _failures;
    private long _failedAt;
    private TimeSpan _delay;
    private bool _waiting;

    internal bool IsWaiting { get { lock (_gate) return _waiting; } }

    internal TimeSpan RecordFailure()
    {
        lock (_gate)
        {
            if (_waiting) return _delay;
            _failures = Math.Min(_failures + 1, 4);
            _delay = TimeSpan.FromSeconds(1 << (_failures - 1));
            _failedAt = _clock.GetTimestamp();
            _waiting = true;
            return _delay;
        }
    }

    internal bool TryBeginRetry(bool independentFrame)
    {
        lock (_gate)
        {
            if (!_waiting || !independentFrame || _clock.GetElapsedTime(_failedAt) < _delay) return false;
            _waiting = false;
            return true;
        }
    }

    internal void RecordSuccess()
    {
        lock (_gate)
        {
            _waiting = false;
            _failures = 0;
        }
    }
}
