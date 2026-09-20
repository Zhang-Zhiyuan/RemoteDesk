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

    public bool HasPendingKeyReleases(long connectionGeneration)
    {
        lock (_syncRoot)
        {
            return HasPendingKeyReleasesLocked(connectionGeneration);
        }
    }

    public int RetryPendingKeyReleases(
        long connectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueOwned);
        lock (_syncRoot)
        {
            // A delayed retry for an old connection must neither release nor
            // discard a newer press of the same key. queueOwned also fences
            // admission against the currently connected generation.
            return RetryPendingKeyReleasesLocked(connectionGeneration, queueOwned);
        }
    }

    public bool HasPendingReleases(long connectionGeneration)
    {
        lock (_syncRoot)
        {
            return HasPendingReleasesLocked(connectionGeneration);
        }
    }

    public int RetryPendingReleases(
        long connectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueOwned);
        lock (_syncRoot)
        {
            return RetryPendingReleasesLocked(connectionGeneration, queueOwned);
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
        RemotePhysicalKey key = RemoteKeyboardInput.PhysicalKey(command);

        lock (_syncRoot)
        {
            if (command.Kind == RemoteInputKind.KeyDown)
            {
                RemoveStaleOwnershipLocked(currentConnectionGeneration);
                RetryPendingReleasesLocked(currentConnectionGeneration, queueOwned);
                if (HasPendingReleasesLocked(currentConnectionGeneration))
                {
                    // Do not allow a later letter/re-press to overtake a failed
                    // release. Only marked KeyUp/MouseUp are automatically retried.
                    return false;
                }

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
                            command,
                            admission.ConnectionGeneration));
                }
                else
                {
                    _pressedKeys[existingIndex] = _pressedKeys[existingIndex] with
                    { ConnectionGeneration = admission.ConnectionGeneration };
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
                            RemoteKeyboardInput.MatchesLegacyRelease(pressed.Command, command));
            }

            if (pressedIndex < 0)
            {
                return false;
            }

            PressedRemoteKey pressedKey =
                _pressedKeys[pressedIndex];
            RemoteInputCommand release = pressedKey.Command with { Kind = RemoteInputKind.KeyUp };
            if (!queueOwned(
                    release,
                    pressedKey.ConnectionGeneration))
            {
                _pressedKeys[pressedIndex] = pressedKey with { PendingRelease = true };
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
            // Legacy callers cannot fence/retry a release. Do not let them
            // overwrite pending ownership or overtake an unreleased modifier.
            if (_pressedKeys.Any(pressed => pressed.PendingRelease) ||
                _pressedMouseButtons.Values.Any(pressed => pressed.PendingReleasePoint.HasValue))
            {
                return false;
            }

            return TryQueueMouseDownLocked(button, remotePoint, queueCurrent);
        }
    }

    public bool TryQueueMouseDown(
        RemoteMouseButton button,
        Point remotePoint,
        long currentConnectionGeneration,
        Func<RemoteInputCommand, RemoteInputQueueAdmission> queueCurrent,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        ArgumentNullException.ThrowIfNull(queueCurrent);
        ArgumentNullException.ThrowIfNull(queueOwned);
        lock (_syncRoot)
        {
            RemoveStaleOwnershipLocked(currentConnectionGeneration);
            RetryPendingReleasesLocked(currentConnectionGeneration, queueOwned);
            if (HasPendingReleasesLocked(currentConnectionGeneration))
            {
                return false;
            }

            return TryQueueMouseDownLocked(button, remotePoint, queueCurrent);
        }
    }

    private bool TryQueueMouseDownLocked(
        RemoteMouseButton button,
        Point remotePoint,
        Func<RemoteInputCommand, RemoteInputQueueAdmission> queueCurrent)
    {
        RemoteInputQueueAdmission admission = queueCurrent(
            RemoteInputCommand.MouseDown(button, remotePoint.X, remotePoint.Y));
        if (!admission.Accepted)
        {
            return false;
        }

        RemoveStaleOwnershipLocked(admission.ConnectionGeneration);
        _pressedMouseButtons[button] = new PressedRemoteMouseButton(
            remotePoint, admission.ConnectionGeneration);
        UpdatePressedMouseButtonCountLocked();
        return true;
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
                pressed.PendingReleasePoint ?? mappedRemotePoint ?? pressed.LastRemotePoint;
            if (!queueOwned(
                    RemoteInputCommand.MouseUp(
                        button,
                        releasePoint.X,
                        releasePoint.Y),
                    pressed.ConnectionGeneration))
            {
                _pressedMouseButtons[button] = pressed with { PendingReleasePoint = releasePoint };
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
                Point releasePoint = entry.Value.PendingReleasePoint ?? entry.Value.LastRemotePoint;
                if (!queueOwned(
                        RemoteInputCommand.MouseUp(
                            entry.Key,
                            releasePoint.X,
                            releasePoint.Y),
                        entry.Value.ConnectionGeneration))
                {
                    _pressedMouseButtons[entry.Key] = entry.Value with { PendingReleasePoint = releasePoint };
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
                        pressed.Command with { Kind = RemoteInputKind.KeyUp },
                        pressed.ConnectionGeneration))
                {
                    _pressedKeys[index] = pressed with { PendingRelease = true };
                    continue;
                }

                _pressedKeys.RemoveAt(index);
                released++;
            }
        }

        return released;
    }

    private bool HasPendingKeyReleasesLocked(long connectionGeneration) =>
        _pressedKeys.Any(pressed =>
            pressed.ConnectionGeneration == connectionGeneration && pressed.PendingRelease);

    private bool HasPendingReleasesLocked(long connectionGeneration) =>
        HasPendingKeyReleasesLocked(connectionGeneration) ||
        _pressedMouseButtons.Values.Any(pressed =>
            pressed.ConnectionGeneration == connectionGeneration && pressed.PendingReleasePoint.HasValue);

    private int RetryPendingReleasesLocked(
        long connectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        int released = 0;
        foreach (var entry in _pressedMouseButtons.ToArray())
        {
            if (entry.Value.ConnectionGeneration != connectionGeneration ||
                entry.Value.PendingReleasePoint is not Point releasePoint)
            {
                continue;
            }

            if (queueOwned(RemoteInputCommand.MouseUp(entry.Key, releasePoint.X, releasePoint.Y),
                    entry.Value.ConnectionGeneration))
            {
                _pressedMouseButtons.Remove(entry.Key);
                released++;
            }
        }

        UpdatePressedMouseButtonCountLocked();
        return released + RetryPendingKeyReleasesLocked(connectionGeneration, queueOwned);
    }

    private int RetryPendingKeyReleasesLocked(
        long connectionGeneration,
        Func<RemoteInputCommand, long, bool> queueOwned)
    {
        int released = 0;
        for (int index = _pressedKeys.Count - 1; index >= 0; index--)
        {
            PressedRemoteKey pressed = _pressedKeys[index];
            if (pressed.ConnectionGeneration != connectionGeneration || !pressed.PendingRelease)
            {
                continue;
            }

            if (queueOwned(pressed.Command with { Kind = RemoteInputKind.KeyUp },
                    pressed.ConnectionGeneration))
            {
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
        RemoteInputCommand Command,
        long ConnectionGeneration,
        bool PendingRelease = false);

    private readonly record struct PressedRemoteMouseButton(
        Point LastRemotePoint,
        long ConnectionGeneration,
        Point? PendingReleasePoint = null);
}
