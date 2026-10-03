namespace RemoteDesk;

internal readonly record struct RemoteControlNoticeDisplay(string Id, Rectangle WorkingArea);

// UI-thread owned. Notice count follows the local display topology, not the
// number of viewers/streams; any notice can disconnect the entire host session.
internal sealed class RemoteControlNoticeManager : IDisposable
{
    private readonly Func<IReadOnlyList<RemoteControlNoticeDisplay>> _getDisplays;
    private readonly Dictionary<string, RemoteControlNotice> _notices = new(StringComparer.OrdinalIgnoreCase);
    private bool _active;
    private bool _disconnecting;
    private bool _disposed;

    internal RemoteControlNoticeManager(Func<IReadOnlyList<RemoteControlNoticeDisplay>>? getDisplays = null) =>
        _getDisplays = getDisplays ?? (() => Screen.AllScreens.Select(screen =>
            new RemoteControlNoticeDisplay(screen.DeviceName, screen.WorkingArea)).ToArray());

    internal IReadOnlyDictionary<string, RemoteControlNotice> Notices => _notices;
    internal event Action? DisconnectRequested;

    internal void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        if (active) RefreshDisplays();
        else ClearNotices();
    }

    internal void RefreshDisplays()
    {
        if (_disposed || !_active) return;
        IReadOnlyList<RemoteControlNoticeDisplay> displays = NormalizeDisplays(_getDisplays());
        var currentIds = new HashSet<string>(displays.Select(display => display.Id), StringComparer.OrdinalIgnoreCase);
        foreach (string id in _notices.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            RemoteControlNotice removed = _notices[id];
            _notices.Remove(id);
            removed.Dispose();
        }
        foreach (RemoteControlNoticeDisplay display in displays)
        {
            if (!_notices.TryGetValue(display.Id, out RemoteControlNotice? notice) || notice.IsDisposed)
            {
                notice = new RemoteControlNotice();
                RemoteControlNotice owner = notice;
                notice.DisconnectRequested += () => RequestDisconnect(display.Id, owner);
                _notices[display.Id] = notice;
            }
            notice.SetDisplayWorkingArea(display.WorkingArea);
            notice.ShowActive(_disconnecting);
        }
    }

    internal void SetDisconnecting(bool disconnecting = true)
    {
        if (_disposed) return;
        _disconnecting = disconnecting;
        foreach (RemoteControlNotice notice in _notices.Values) notice.SetDisconnecting(disconnecting);
    }

    private void RequestDisconnect(string displayId, RemoteControlNotice owner)
    {
        if (_disposed || !_active || _disconnecting || !_notices.TryGetValue(displayId, out var current) ||
            !ReferenceEquals(current, owner)) return;
        SetDisconnecting();
        DisconnectRequested?.Invoke();
    }

    internal static IReadOnlyList<RemoteControlNoticeDisplay> NormalizeDisplays(
        IReadOnlyList<RemoteControlNoticeDisplay> displays) => displays
        .Where(display => !string.IsNullOrWhiteSpace(display.Id) && display.WorkingArea.Width > 0 && display.WorkingArea.Height > 0)
        .DistinctBy(display => display.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private void ClearNotices()
    {
        RemoteControlNotice[] old = _notices.Values.ToArray();
        _notices.Clear();
        foreach (RemoteControlNotice notice in old) notice.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        ClearNotices();
    }
}
