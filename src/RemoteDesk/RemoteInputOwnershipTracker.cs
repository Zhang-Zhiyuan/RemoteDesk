using System.Drawing;

namespace RemoteDesk;

internal readonly record struct RemoteInputQueueAdmission(
    bool Accepted,
    long ConnectionGeneration);

internal sealed class RemoteInputOwnershipTracker
{
    private readonly object _syncRoot = new();
    private readonly List<PressedRemoteKey> _pressedKeys = [];
    private readonly Dictionary<RemoteMouseButton, PressedRemoteMouseButton>
        _pressedMouseButtons = [];
    private int _pressedMouseButtonCount;

    public int PressedKeyCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _pressedKeys.Count;
            }
        }
    }

    public int PressedMouseButtonCount =>
        Volatile.Read(
            ref _pressedMouseButtonCount);

    public bool AnyPressedKey(
        long connectionGeneration,
        Func<RemotePhysicalKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_syncRoot)
        {
            return _pressedKeys.Any(
                pressed =>
                    pressed.ConnectionGeneration ==
                        connectionGeneration &&
                    predicate(pressed.Key));
        }
    }

    public bool TryQueueKey(
        RemoteInputCommand command,
        long currentConnectionGeneration,
        Func<RemoteInputCommand, RemoteInputQueueAdmission>
            queueCurrent,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueCurrent);
        ArgumentNullException.ThrowIfNull(queueOwned);
        var key = new RemotePhysicalKey(
            command.Data,
            command.X,
            (RemoteKeyboardFlags)command.Y);

        lock (_syncRoot)
        {
            if (command.Kind == RemoteInputKind.KeyDown)
            {
                RemoteInputQueueAdmission admission =
                    queueCurrent(command);
                if (!admission.Accepted)
                {
                    return false;
                }

                RemoveStaleOwnershipLocked(
                    admission.ConnectionGeneration);
                int existingIndex =
                    _pressedKeys.FindLastIndex(
                        pressed => pressed.Key == key);
                if (existingIndex < 0)
                {
                    _pressedKeys.Add(
                        new PressedRemoteKey(
                            key,
                            admission.ConnectionGeneration));
                }
                else
                {
                    _pressedKeys[existingIndex] =
                        new PressedRemoteKey(
                            key,
                            admission.ConnectionGeneration);
                }

                return true;
            }

            if (command.Kind != RemoteInputKind.KeyUp)
            {
                throw new ArgumentException(
                    "命令不是键盘按下或释放事件。",
                    nameof(command));
            }

            RemoveStaleOwnershipLocked(
                currentConnectionGeneration);
            int pressedIndex =
                _pressedKeys.FindLastIndex(
                    pressed => pressed.Key == key);
            if (pressedIndex < 0)
            {
                pressedIndex =
                    _pressedKeys.FindLastIndex(
                        pressed =>
                            pressed.Key.VirtualKey ==
                                key.VirtualKey);
            }

            if (pressedIndex < 0)
            {
                return false;
            }

            PressedRemoteKey pressedKey =
                _pressedKeys[pressedIndex];
            RemoteInputCommand release =
                RemoteInputCommand.KeyUp(
                    pressedKey.Key.VirtualKey,
                    pressedKey.Key.ScanCode,
                    pressedKey.Key.Flags);
            if (!queueOwned(
                    release,
                    pressedKey.ConnectionGeneration))
            {
                return false;
            }

            _pressedKeys.RemoveAt(pressedIndex);
            return true;
        }
    }

    public bool TryQueueMouseDown(
        RemoteMouseButton button,
        Point remotePoint,
        Func<RemoteInputCommand, RemoteInputQueueAdmission>
            queueCurrent)
    {
        ArgumentNullException.ThrowIfNull(queueCurrent);
        lock (_syncRoot)
        {
            RemoteInputQueueAdmission admission =
                queueCurrent(
                    RemoteInputCommand.MouseDown(
                        button,
                        remotePoint.X,
                        remotePoint.Y));
            if (!admission.Accepted)
            {
                return false;
            }

            RemoveStaleOwnershipLocked(
                admission.ConnectionGeneration);
            _pressedMouseButtons[button] =
                new PressedRemoteMouseButton(
                    remotePoint,
                    admission.ConnectionGeneration);
            UpdatePressedMouseButtonCountLocked();
            return true;
        }
    }

    public bool TryQueueMouseUp(
        RemoteMouseButton button,
        Point? mappedRemotePoint,
        long currentConnectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueOwned);
        lock (_syncRoot)
        {
            RemoveStaleOwnershipLocked(
                currentConnectionGeneration);
            if (!_pressedMouseButtons.TryGetValue(
                    button,
                    out PressedRemoteMouseButton pressed))
            {
                return false;
            }

            Point releasePoint =
                mappedRemotePoint ?? pressed.LastRemotePoint;
            if (!queueOwned(
                    RemoteInputCommand.MouseUp(
                        button,
                        releasePoint.X,
                        releasePoint.Y),
                    pressed.ConnectionGeneration))
            {
                return false;
            }

            _pressedMouseButtons.Remove(button);
            UpdatePressedMouseButtonCountLocked();
            return true;
        }
    }

    public void UpdatePressedMousePosition(
        Point remotePoint,
        long connectionGeneration)
    {
        lock (_syncRoot)
        {
            foreach (RemoteMouseButton button in
                _pressedMouseButtons.Keys.ToArray())
            {
                PressedRemoteMouseButton pressed =
                    _pressedMouseButtons[button];
                if (pressed.ConnectionGeneration ==
                    connectionGeneration)
                {
                    _pressedMouseButtons[button] =
                        pressed with
                        {
                            LastRemotePoint = remotePoint
                        };
                }
            }
        }
    }

    public int ReleaseAll(
        long currentConnectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueOwned);
        int released = 0;
        lock (_syncRoot)
        {
            RemoveStaleOwnershipLocked(
                currentConnectionGeneration);
            foreach (KeyValuePair<RemoteMouseButton, PressedRemoteMouseButton>
                entry in _pressedMouseButtons.ToArray())
            {
                if (!queueOwned(
                        RemoteInputCommand.MouseUp(
                            entry.Key,
                            entry.Value.LastRemotePoint.X,
                            entry.Value.LastRemotePoint.Y),
                        entry.Value.ConnectionGeneration))
                {
                    continue;
                }

                _pressedMouseButtons.Remove(entry.Key);
                UpdatePressedMouseButtonCountLocked();
                released++;
            }

            for (int index = _pressedKeys.Count - 1;
                 index >= 0;
                 index--)
            {
                PressedRemoteKey pressed =
                    _pressedKeys[index];
                if (!queueOwned(
                        RemoteInputCommand.KeyUp(
                            pressed.Key.VirtualKey,
                            pressed.Key.ScanCode,
                            pressed.Key.Flags),
                        pressed.ConnectionGeneration))
                {
                    continue;
                }

                _pressedKeys.RemoveAt(index);
                released++;
            }
        }

        return released;
    }

    private void RemoveStaleOwnershipLocked(
        long currentConnectionGeneration)
    {
        _pressedKeys.RemoveAll(
            pressed =>
                pressed.ConnectionGeneration !=
                    currentConnectionGeneration);
        foreach (RemoteMouseButton button in
            _pressedMouseButtons
                .Where(entry =>
                    entry.Value.ConnectionGeneration !=
                        currentConnectionGeneration)
                .Select(entry => entry.Key)
                .ToArray())
        {
            _pressedMouseButtons.Remove(button);
        }

        UpdatePressedMouseButtonCountLocked();
    }

    private void UpdatePressedMouseButtonCountLocked()
    {
        Volatile.Write(
            ref _pressedMouseButtonCount,
            _pressedMouseButtons.Count);
    }

    internal bool TryGetPressedMouseButton(
        RemoteMouseButton button,
        out Point lastRemotePoint,
        out long connectionGeneration)
    {
        lock (_syncRoot)
        {
            if (_pressedMouseButtons.TryGetValue(
                    button,
                    out PressedRemoteMouseButton pressed))
            {
                lastRemotePoint = pressed.LastRemotePoint;
                connectionGeneration =
                    pressed.ConnectionGeneration;
                return true;
            }
        }

        lastRemotePoint = default;
        connectionGeneration = default;
        return false;
    }

    private readonly record struct PressedRemoteKey(
        RemotePhysicalKey Key,
        long ConnectionGeneration);

    private readonly record struct PressedRemoteMouseButton(
        Point LastRemotePoint,
        long ConnectionGeneration);
}
