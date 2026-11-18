namespace RemoteDesk;

// One instance per viewer connection. A live heartbeat is not proof that the
// capture source is advancing, but a motionless desktop alone is not a fault.
internal sealed class IdleH264Refresh(CancellationTokenSource owner)
{
    internal CancellationTokenSource Owner { get; } = owner;
    private readonly object _gate = new();
    private const int QuietMs = 3000, ResponseGraceMs = 1000, RetryMs = 3000;
    private long? _lastFrame, _lastInput, _pendingSince, _firstRequest;
    private bool _probedThisBurst;

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

    // Called by the existing heartbeat tick, never on the input/decoder path.
    // Repeated mouse moves do not postpone a probe or cause a restart loop.
    internal int TakeDueRequest(long now)
    {
        lock (_gate)
        {
            if (_pendingSince is not long since || _lastFrame is not long frame ||
                now - since < ResponseGraceMs || now - frame < QuietMs) return 0;
            _probedThisBurst = true;
            if (_firstRequest is not long first) { _firstRequest = now; return 1; }
            if (now - first < RetryMs) return 0;
            _pendingSince = _firstRequest = null;
            return 2;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
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
