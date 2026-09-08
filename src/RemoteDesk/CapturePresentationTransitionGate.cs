namespace RemoteDesk;

/// <summary>
/// Serializes a direct HWND presentation with capture-target availability
/// transitions. Callers must acquire this gate before decoder/pending-frame or
/// D3D presenter locks; no lower-level rendering path may acquire it.
/// </summary>
internal sealed class CapturePresentationTransitionGate
{
    private readonly object _syncRoot = new();

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_syncRoot)
        {
            action();
        }
    }

    public T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_syncRoot)
        {
            return action();
        }
    }
}
