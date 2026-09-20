namespace RemoteDesk;

// Per authenticated input connection. Windows does not reliably turn an
// injected Win+L into a session lock, so use its session-lock API instead.
// Track only remote modifiers, never read another app's/physical key state.
internal sealed class WindowsSessionShortcutHandler
{
    private readonly Dictionary<RemotePhysicalKey, RemoteInputCommand> _modifiers = [];
    private readonly HashSet<RemotePhysicalKey> _consumed = [];

    internal bool TryHandle(RemoteInputCommand command, Action<RemoteInputCommand> releaseKey, Action lockSession)
    {
        if (command.Kind is not (RemoteInputKind.KeyDown or RemoteInputKind.KeyUp)) return false;
        RemotePhysicalKey key = ToPhysicalKey(command);
        if (_consumed.Contains(key))
        {
            if (command.Kind == RemoteInputKind.KeyUp) _consumed.Remove(key);
            return true;
        }
        if (command.Kind != RemoteInputKind.KeyDown || command.Data != (int)Keys.L ||
            !_modifiers.Values.Any(pressed => IsWindowsKey(pressed.Data)) ||
            _modifiers.Values.Any(pressed => !IsWindowsKey(pressed.Data))) return false;

        // Release only our own Win keys BEFORE the desktop transition. The
        // later network key-ups must not re-release a physically held key.
        foreach (var pressed in _modifiers.ToArray())
        {
            releaseKey(pressed.Value);
            _modifiers.Remove(pressed.Key);
            _consumed.Add(pressed.Key);
        }
        _consumed.Add(key); // L itself was never injected, even if locking fails.
        lockSession();
        return true;
    }

    internal void Observe(RemoteInputCommand command)
    {
        if (!IsModifier(command.Data)) return;
        if (command.Kind == RemoteInputKind.KeyDown)
            _modifiers[ToPhysicalKey(command)] = command;
        else if (command.Kind == RemoteInputKind.KeyUp)
        {
            if (_modifiers.Remove(ToPhysicalKey(command))) return;
            foreach (var pressed in _modifiers.Where(pair =>
                RemoteKeyboardInput.MatchesLegacyRelease(pair.Value, command)).ToArray())
                _modifiers.Remove(pressed.Key);
        }
    }

    internal bool TryConsumeRelease(RemoteInputCommand pressed)
    {
        RemotePhysicalKey key = ToPhysicalKey(pressed);
        _modifiers.Remove(key);
        return _consumed.Remove(key);
    }

    private static bool IsWindowsKey(int key) => key is (int)Keys.LWin or (int)Keys.RWin;
    private static RemotePhysicalKey ToPhysicalKey(RemoteInputCommand command) =>
        RemoteKeyboardInput.PhysicalKey(command);
    private static bool IsModifier(int key) => IsWindowsKey(key) || key is
        (int)Keys.ShiftKey or (int)Keys.LShiftKey or (int)Keys.RShiftKey or
        (int)Keys.ControlKey or (int)Keys.LControlKey or (int)Keys.RControlKey or
        (int)Keys.Menu or (int)Keys.LMenu or (int)Keys.RMenu;
}
