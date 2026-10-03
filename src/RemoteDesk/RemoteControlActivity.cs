namespace RemoteDesk;

// Authentication, not a TCP accept or discovery probe, owns each lease. Keeping
// overlapping leases prevents a replaced client's cleanup from hiding the notice.
internal sealed class RemoteControlActivity
{
    private readonly object _sync = new();
    private readonly Dictionary<object, Func<Task>> _sessions = new();

    public event Action? Changed;

    public bool IsActive
    {
        get { lock (_sync) return _sessions.Count != 0; }
    }

    public IDisposable Begin(Func<Task> disconnect)
    {
        ArgumentNullException.ThrowIfNull(disconnect);
        var key = new object();
        lock (_sync) _sessions.Add(key, disconnect);
        Changed?.Invoke();
        return new Lease(this, key);
    }

    public Task DisconnectAsync()
    {
        Func<Task>[] callbacks;
        lock (_sync) callbacks = _sessions.Values.ToArray();
        // Do not hold the lock while stopping sockets or waiting for cleanup.
        return Task.WhenAll(callbacks.Select(callback => Task.Run(callback)));
    }

    private void End(object key)
    {
        lock (_sync)
        {
            if (!_sessions.Remove(key)) return;
        }
        Changed?.Invoke();
    }

    private sealed class Lease(RemoteControlActivity owner, object key) : IDisposable
    {
        private RemoteControlActivity? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End(key);
    }
}
