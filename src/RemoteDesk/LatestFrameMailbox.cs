namespace RemoteDesk;

internal readonly record struct LatestFrameOffer<T>(
    bool Accepted,
    bool ShouldSchedule,
    T? Replaced)
    where T : class;

internal sealed class LatestFrameMailbox<T>
    where T : class
{
    private readonly object _syncRoot = new();
    private T? _latest;
    private bool _dispatchScheduled;
    private bool _closed;

    public bool HasPendingDispatch
    {
        get
        {
            lock (_syncRoot)
            {
                return !_closed && _dispatchScheduled && _latest is not null;
            }
        }
    }

    public LatestFrameOffer<T> Offer(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_syncRoot)
        {
            if (_closed)
            {
                return new LatestFrameOffer<T>(
                    Accepted: false,
                    ShouldSchedule: false,
                    Replaced: null);
            }

            T? replaced = _latest;
            _latest = value;
            bool shouldSchedule = !_dispatchScheduled;
            _dispatchScheduled = true;
            return new LatestFrameOffer<T>(
                Accepted: true,
                ShouldSchedule: shouldSchedule,
                Replaced: replaced);
        }
    }

    public T? TakeLatest()
    {
        lock (_syncRoot)
        {
            T? latest = _latest;
            _latest = null;
            return latest;
        }
    }

    public bool CompleteDispatch()
    {
        lock (_syncRoot)
        {
            if (_closed)
            {
                _dispatchScheduled = false;
                return false;
            }

            if (_latest is not null)
            {
                return true;
            }

            _dispatchScheduled = false;
            return false;
        }
    }

    public T? Close()
    {
        lock (_syncRoot)
        {
            _closed = true;
            _dispatchScheduled = false;
            T? latest = _latest;
            _latest = null;
            return latest;
        }
    }
}
