namespace RemoteDesk;

// One instance per viewer connection. A live heartbeat is not proof that the
// capture source is advancing, but a motionless desktop alone is not a fault.
internal sealed class IdleH264Refresh(CancellationTokenSource owner)
{
    internal CancellationTokenSource Owner { get; } = owner;
    private readonly object _gate = new();
    private const int QuietMs = 3000, ResponseGraceMs = 1000, RetryMs = 3000;
    private const int FastRetryMs = 1000, FastRoundTripMs = 100, PongFreshnessMs = 15000;
    private long? _lastFrame, _lastInput, _pendingSince, _firstRequest;
    private long? _lastPong;
    private int _fastPongs;
    private bool _probedThisBurst;
    private long? _nextDecoderRetry;
    private int _decoderRetryIntervalMs;

    // The viewer explicitly lost its prediction chain. Dependent frames still
    // count as network activity, but cannot cancel a decoder recovery request.
    internal void BeginDecoderWait(long now, TimeSpan remainingBackoff)
    {
        lock (_gate)
        {
            if (_nextDecoderRetry.HasValue) return;
            _pendingSince = _firstRequest = null;
            _decoderRetryIntervalMs = 1000;
            _nextDecoderRetry = now + Math.Max(_decoderRetryIntervalMs, (long)Math.Ceiling(remainingBackoff.TotalMilliseconds));
        }
    }

    internal void EndDecoderWait()
    {
        lock (_gate) _nextDecoderRetry = null;
    }

    internal bool TakeDueDecoderRetry(long now)
    {
        lock (_gate)
        {
            if (_nextDecoderRetry is not long due || now < due) return false;
            _decoderRetryIntervalMs = Math.Min(8000, _decoderRetryIntervalMs * 2);
            _nextDecoderRetry = now + _decoderRetryIntervalMs;
            return true;
        }
    }

    internal void ObserveFrame(RemoteFrameEncoding encoding, long now)
    {
        lock (_gate)
        {
            _lastFrame = encoding == RemoteFrameEncoding.H264AnnexB ? now : null;
            _pendingSince = _firstRequest = null;
        }
    }

    internal void ObserveInput(long now)
    {
        lock (_gate)
        {
            bool newBurst = _lastInput is not long last || now - last >= QuietMs;
            _lastInput = now;
            if (newBurst) _probedThisBurst = false;
            if (_lastFrame is long frame && !_probedThisBurst && _pendingSince is null &&
                (newBurst || now - frame >= QuietMs)) _pendingSince = now;
        }
    }

    internal void ObserveRoundTrip(TimeSpan elapsed, long now)
    {
        lock (_gate)
        {
            if (_lastPong is null || now - _lastPong > PongFreshnessMs) _fastPongs = 0;
            _fastPongs = elapsed >= TimeSpan.Zero && elapsed.TotalMilliseconds <= FastRoundTripMs
                ? Math.Min(2, _fastPongs + 1) : 0;
            _lastPong = now;
        }
    }

    // Called by the existing heartbeat tick, never on the input/decoder path.
    // Repeated mouse moves do not postpone a probe or cause a restart loop.
    internal int TakeDueRequest(long now, bool receivingMessage = false)
    {
        lock (_gate)
        {
            if (_nextDecoderRetry.HasValue || _pendingSince is not long since || _lastFrame is not long frame ||
                now - since < ResponseGraceMs || now - frame < QuietMs) return 0;
            _probedThisBurst = true;
            if (_firstRequest is not long first) { _firstRequest = now; return 1; }
            // Only accelerate the second request on an observed responsive
            // connection. A slow transfer may still be delivering the first IDR.
            bool fastRetry = !receivingMessage && _fastPongs >= 2 && now - _lastPong <= PongFreshnessMs;
            if (now - first < (fastRetry ? FastRetryMs : RetryMs)) return 0;
            _pendingSince = _firstRequest = null;
            return 2;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _nextDecoderRetry = null;
            _lastFrame = _lastInput = _pendingSince = _firstRequest = null;
            _probedThisBurst = false;
        }
    }

    internal void CancelInput()
    {
        lock (_gate)
        {
            _lastInput = _pendingSince = _firstRequest = null;
            _probedThisBurst = false;
        }
    }
}
