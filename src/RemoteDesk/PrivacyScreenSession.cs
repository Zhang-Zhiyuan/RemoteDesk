namespace RemoteDesk;

internal enum PrivacyScreenAction : byte { Query = 0, Enable = 1, Disable = 2 }
internal sealed record PrivacyScreenStatus(bool Success, bool Enabled, bool Allowed, string Message);

internal interface IPrivacyScreenBackend : IDisposable
{
    bool IsActive { get; }
    string InactiveReason { get; }
    Task StartAsync(CancellationToken cancellationToken);
    void Stop();
}

// Authentication lives in the host. The curtain belongs to the connection that
// enabled it, never to a remembered device, IP, or a reconnecting viewer.
internal sealed class PrivacyScreenSession(IPrivacyScreenBackend backend) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _allowed;
    private bool _disposed;
    private object? _owner;
    private CancellationTokenRegistration _ownerCancellation;
    internal bool Allowed => Volatile.Read(ref _allowed) != 0;

    internal async Task SetAllowedAsync(bool allowed)
    {
        // Revoke immediately, including while a slow backend is starting.
        Volatile.Write(ref _allowed, allowed ? 1 : 0);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) Volatile.Write(ref _allowed, 0);
            if (!Allowed) Stop();
        }
        finally { _gate.Release(); }
    }

    internal async Task<PrivacyScreenStatus> RequestAsync(PrivacyScreenAction action, object owner, CancellationToken lifetime)
    {
        await _gate.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            lifetime.ThrowIfCancellationRequested();
            if (_disposed) return new(false, false, false, "被控端正在退出。");
            if (!Enum.IsDefined(action)) return State(false, "隐私屏操作无效。");
            if (action == PrivacyScreenAction.Disable) { Stop(); return State(true, "隐私屏已关闭，本地画面已恢复。"); }
            if (action == PrivacyScreenAction.Query)
                return State(true, backend.IsActive ? "隐私屏已开启；本地 Ctrl+Alt+Shift+P 可恢复显示。" :
                    !Allowed ? "请先在被控端勾选“允许隐私屏”。" : backend.InactiveReason);
            if (!Allowed) return State(false, "被控端未允许隐私屏，请在被控端设置中开启许可。");
            // Do not transfer ownership merely because another screen queried or
            // redundantly enabled the same curtain.
            if (backend.IsActive) return State(true, "隐私屏已开启。");
            Stop();
            try
            {
                await backend.StartAsync(lifetime).ConfigureAwait(false);
                if (!Allowed || lifetime.IsCancellationRequested || !backend.IsActive)
                {
                    Stop();
                    return State(false, "连接或本机许可已撤销，已恢复显示。");
                }
                _owner = owner;
                _ownerCancellation = lifetime.Register(() => { _ = ReleaseOwnerAsync(owner); });
                return State(true, "隐私屏已开启；所有本地屏幕被遮挡，Ctrl+Alt+Shift+P 可恢复。开启它的连接断开后自动恢复。");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Stop();
                return State(false, "隐私屏未开启：" + ex.Message);
            }
        }
        finally { _gate.Release(); }
    }

    private PrivacyScreenStatus State(bool success, string message) => new(success, backend.IsActive, Allowed || backend.IsActive, message);
    private async Task ReleaseOwnerAsync(object owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (ReferenceEquals(_owner, owner)) Stop(); }
        finally { _gate.Release(); }
    }
    private void Stop()
    {
        _owner = null;
        _ownerCancellation.Unregister();
        backend.Stop();
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; Volatile.Write(ref _allowed, 0); Stop(); backend.Dispose(); }
        finally { _gate.Release(); }
    }
}
