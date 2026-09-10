namespace RemoteDesk;

// One pipe's held physical keys. Left/right modifiers and main/numpad Enter
// may share a virtual key; releasing one must not erase ownership of the other.
internal sealed class WindowsSecureDesktopKeyState
{
    private readonly Dictionary<RemotePhysicalKey, RemoteInputCommand> _keys = [];
    internal int Count => _keys.Count;

    internal void Press(RemoteInputCommand command) => _keys[Identity(command)] = command;

    internal void ForgetReleased(RemoteInputCommand command)
    {
        foreach (var key in Matching(command)) _keys.Remove(key);
    }

    internal void Release(RemoteInputCommand command, Action<RemoteInputCommand> release)
    {
        foreach (var key in Matching(command))
        {
            release(_keys[key]);
            _keys.Remove(key); // Retain ownership if native release fails.
        }
    }

    internal void ReleaseAll(Action<RemoteInputCommand> release)
    {
        foreach (var pressed in _keys.ToArray())
        {
            try { release(pressed.Value); _keys.Remove(pressed.Key); }
            catch (InvalidOperationException) { } // Other keys still need releasing.
        }
    }

    private RemotePhysicalKey[] Matching(RemoteInputCommand command)
    {
        var identity = Identity(command);
        bool physical = ((RemoteKeyboardFlags)command.Y).HasFlag(RemoteKeyboardFlags.HasScanCode);
        return _keys.Keys.Where(key => physical ? key == identity : key.VirtualKey == command.Data).ToArray();
    }

    private static RemotePhysicalKey Identity(RemoteInputCommand command) =>
        new(command.Data, command.X, (RemoteKeyboardFlags)command.Y);
}
