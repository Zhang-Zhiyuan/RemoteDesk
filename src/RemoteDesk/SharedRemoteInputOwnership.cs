namespace RemoteDesk;

// Independent screen transports still drive one physical keyboard and mouse.
// The most recent successful down owns its up; a delayed up/teardown from a
// previous window must not release a key/button now held by another window.
// Injection and bookkeeping are atomic across the dedicated input workers.
internal sealed class SharedRemoteInputOwnership
{
    private readonly object _sync = new();
    private readonly Dictionary<RemotePhysicalKey, KeyOwner> _keys = [];
    private readonly Dictionary<RemoteMouseButton, object> _buttons = [];

    internal bool Apply(object owner, RemoteInputCommand command, Action<RemoteInputCommand> inject)
    {
        lock (_sync)
        {
            switch (command.Kind)
            {
                case RemoteInputKind.KeyDown:
                    inject(command);
                    _keys[RemoteKeyboardInput.PhysicalKey(command)] = new(owner, command);
                    break;
                case RemoteInputKind.KeyUp:
                    return ReleaseKey(owner, command, pressed => inject(pressed with { Kind = RemoteInputKind.KeyUp }));
                case RemoteInputKind.MouseDown:
                    inject(command);
                    _buttons[command.Button] = owner;
                    break;
                case RemoteInputKind.MouseUp:
                    return ReleaseMouseButton(owner, command.Button, () => inject(command));
                default:
                    inject(command);
                    break;
            }
            return true;
        }
    }

    internal bool ReleaseKey(object owner, RemoteInputCommand command, Action<RemoteInputCommand> release)
    {
        lock (_sync)
        {
            RemotePhysicalKey exact = RemoteKeyboardInput.PhysicalKey(command);
            bool genericModifier = !((RemoteKeyboardFlags)command.Y).HasFlag(RemoteKeyboardFlags.HasScanCode) &&
                command.Data is 0x10 or 0x11 or 0x12;
            RemotePhysicalKey[] matches;
            if (!genericModifier && _keys.TryGetValue(exact, out KeyOwner entry))
                matches = ReferenceEquals(entry.Owner, owner) ? [exact] : [];
            else
                matches = _keys.Where(pair => ReferenceEquals(pair.Value.Owner, owner) &&
                    (pair.Key == exact || RemoteKeyboardInput.MatchesLegacyRelease(pair.Value.Command, command)))
                    .Select(pair => pair.Key).ToArray();
            foreach (RemotePhysicalKey key in matches)
            {
                // Resolve a legacy generic up to this connection's actual
                // pressed side/scan. Never send an ambiguous generic release
                // while another window may own the other modifier side.
                release(_keys[key].Command);
                _keys.Remove(key); // On failure retain ownership for retry.
            }
            return matches.Length != 0;
        }
    }

    internal bool ReleaseMouseButton(object owner, RemoteMouseButton button, Action release)
    {
        lock (_sync)
        {
            if (!_buttons.TryGetValue(button, out object? heldBy) || !ReferenceEquals(heldBy, owner)) return false;
            release();
            _buttons.Remove(button);
            return true;
        }
    }

    internal void ForgetOwner(object owner)
    {
        lock (_sync)
        {
            foreach (var key in _keys.Where(pair => ReferenceEquals(pair.Value.Owner, owner)).Select(pair => pair.Key).ToArray())
                _keys.Remove(key);
            foreach (var button in _buttons.Where(pair => ReferenceEquals(pair.Value, owner)).Select(pair => pair.Key).ToArray())
                _buttons.Remove(button);
        }
    }

    private readonly record struct KeyOwner(object Owner, RemoteInputCommand Command);
}
