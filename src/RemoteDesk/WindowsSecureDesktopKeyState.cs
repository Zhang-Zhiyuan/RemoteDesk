namespace RemoteDesk;

// One pipe's held physical keys. Left/right modifiers and main/numpad Enter
// may share a virtual key; releasing one must not erase ownership of the other.
internal sealed class WindowsSecureDesktopKeyState
{
    private readonly Dictionary<RemotePhysicalKey, RemoteInputCommand> _keys = [];
    internal int Count => _keys.Count;

    internal void Press(RemoteInputCommand command) => _keys.TryAdd(Identity(command), command);

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
        bool genericModifier = !physical && command.Data is 0x10 or 0x11 or 0x12;
        // A precise release only consumes its exact owner when present. If a
        // modifier was synthesized without a scan code, permit the bounded
        // legacy match; it never matches a different known physical side.
        if (!genericModifier && _keys.ContainsKey(identity)) return [identity];
        // Legacy generic release intentionally keeps the existing all-matching
        // contract, including when a generic owner itself is also present.
        return _keys.Keys.Where(key =>
            RemoteKeyboardInput.MatchesLegacyRelease(_keys[key], command)).ToArray();
    }

    private static RemotePhysicalKey Identity(RemoteInputCommand command) =>
        RemoteKeyboardInput.PhysicalKey(command);
}
