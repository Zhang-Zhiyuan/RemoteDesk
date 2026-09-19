namespace RemoteDesk;

// The legacy clipboard protocol has no request IDs. Allow one operation at a
// time and retain timed-out requests until their reply is drained. Otherwise a
// late reply could overwrite a newer local copy or acknowledge the wrong paste.
internal sealed class ClipboardRequestTracker
{
    internal const int TimeoutMilliseconds = 8000;
    internal const int RelayTimeoutMilliseconds = 30000;
    private readonly object _gate = new();
    private Request? _pending;
    private long _generation = -1;
    private long _revision;
    private readonly Func<long> _clock;

    internal ClipboardRequestTracker(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    internal bool IsPending { get { lock (_gate) return _pending is not null; } }

    internal Request? Begin(long generation, bool read, uint localSequence = 0,
        int timeoutMilliseconds = TimeoutMilliseconds)
    {
        lock (_gate)
        {
            if (generation < _generation) return null;
            if (generation != _generation)
            {
                _pending?.Completion.TrySetResult(false);
                _pending = null;
                _generation = generation;
            }
            if (_pending is not null) return null;
            return _pending = new Request(generation, ++_revision, read, localSequence, _clock,
                Math.Clamp(timeoutMilliseconds, TimeoutMilliseconds, RelayTimeoutMilliseconds));
        }
    }

    internal Request? Take(long generation, bool textReply, bool success)
    {
        lock (_gate)
        {
            Request? request = _pending;
            if (request is null || generation != request.Generation ||
                (textReply ? !request.Read : request.Read && success)) return null;
            _pending = null;
            return request;
        }
    }

    internal bool CanApply(Request request, uint currentSequence)
    {
        lock (_gate)
            return request.Generation == _generation && request.Revision == _revision &&
                !request.Expired && request.LocalSequence == currentSequence;
    }

    internal sealed class Request(long generation, long revision, bool read, uint localSequence, Func<long> clock,
        int timeoutMilliseconds)
    {
        internal long Generation { get; } = generation;
        internal long Revision { get; } = revision;
        internal bool Read { get; } = read;
        internal uint LocalSequence { get; } = localSequence;
        private readonly long _startedAt = clock();
        internal int ReplyTimeoutMilliseconds { get; } = timeoutMilliseconds;
        internal int RemainingTimeoutMilliseconds => (int)Math.Clamp(
            ReplyTimeoutMilliseconds - (clock() - _startedAt), 0, ReplyTimeoutMilliseconds);
        internal bool Expired => clock() - _startedAt >= ReplyTimeoutMilliseconds;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
