namespace RemoteDesk;

internal readonly record struct HostSessionLivenessSnapshot(
    bool IsWaitingForInboundMessage,
    long ReadGeneration,
    TimeSpan WaitDuration);

internal sealed class HostSessionLivenessTracker
{
    internal static readonly TimeSpan DefaultInboundReadTimeout =
        TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumWatchdogPollInterval =
        TimeSpan.FromSeconds(1);

    private readonly object _syncRoot = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _inboundReadTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private long _readGeneration;
    private long _readStartedAt;
    private bool _isWaitingForInboundMessage;

    internal HostSessionLivenessTracker(
        TimeProvider? timeProvider = null,
        TimeSpan? inboundReadTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inboundReadTimeout =
            inboundReadTimeout ?? DefaultInboundReadTimeout;
        if (_inboundReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inboundReadTimeout),
                "The inbound read timeout must be positive.");
        }

        _delayAsync = delayAsync ??
            ((delay, cancellationToken) =>
                Task.Delay(delay, _timeProvider, cancellationToken));
    }

    internal TimeSpan InboundReadTimeout => _inboundReadTimeout;

    internal long BeginInboundRead()
    {
        lock (_syncRoot)
        {
            _readGeneration = NextGeneration(_readGeneration);
            _readStartedAt = _timeProvider.GetTimestamp();
            _isWaitingForInboundMessage = true;
            return _readGeneration;
        }
    }

    internal void EndInboundRead(long readGeneration)
    {
        lock (_syncRoot)
        {
            if (!_isWaitingForInboundMessage ||
                readGeneration != _readGeneration)
            {
                return;
            }

            _isWaitingForInboundMessage = false;
            _readStartedAt = 0;
        }
    }

    internal HostSessionLivenessSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return CreateSnapshot();
        }
    }

    internal async Task WaitForInboundReadTimeoutAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HostSessionLivenessSnapshot snapshot = GetSnapshot();
            if (HasInboundReadTimedOut(
                    snapshot.IsWaitingForInboundMessage,
                    snapshot.WaitDuration,
                    _inboundReadTimeout))
            {
                return;
            }

            TimeSpan pollDelay =
                _inboundReadTimeout < MaximumWatchdogPollInterval
                    ? _inboundReadTimeout
                    : MaximumWatchdogPollInterval;
            if (snapshot.IsWaitingForInboundMessage)
            {
                TimeSpan remaining =
                    _inboundReadTimeout - snapshot.WaitDuration;
                if (remaining < pollDelay)
                {
                    pollDelay = remaining;
                }
            }

            await _delayAsync(pollDelay, cancellationToken)
                .ConfigureAwait(false);
            // The read may have completed or a new read may have started at
            // any poll boundary. Re-check current state before declaring the
            // session idle. A single low-frequency monitor avoids allocating
            // and cancelling a timer for every high-rate mouse message.
        }
    }

    internal static bool HasInboundReadTimedOut(
        bool isWaitingForInboundMessage,
        TimeSpan waitDuration,
        TimeSpan inboundReadTimeout)
    {
        return isWaitingForInboundMessage &&
            waitDuration >= inboundReadTimeout;
    }

    private HostSessionLivenessSnapshot CreateSnapshot()
    {
        TimeSpan waitDuration = _isWaitingForInboundMessage
            ? _timeProvider.GetElapsedTime(
                _readStartedAt,
                _timeProvider.GetTimestamp())
            : TimeSpan.Zero;
        return new HostSessionLivenessSnapshot(
            _isWaitingForInboundMessage,
            _readGeneration,
            waitDuration);
    }

    private static long NextGeneration(long generation) =>
        generation == long.MaxValue ? 1 : generation + 1;
}
