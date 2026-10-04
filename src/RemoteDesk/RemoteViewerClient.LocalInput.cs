namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private int _localInputPaused;
    private long _localInputIntentRevision;
    // Only commands admitted to the encrypted writer are held. Queue-only downs are
    // discarded on pause; an already encoded down gets a matching reliable up next.
    private readonly Dictionary<RemotePhysicalKey, RemoteInputCommand> _localHeldKeys = [];
    private readonly Dictionary<RemoteMouseButton, RemoteInputCommand> _localHeldButtons = [];
    private RemoteInputCommand? _localLastPointerInput;

    public bool IsLocalInputPaused => Volatile.Read(ref _localInputPaused) != 0;
    public event Action<bool>? LocalInputPausedChanged;

    public void SetLocalInputPaused(bool paused)
    {
        bool wake = false;
        lock (_inputLock)
        {
            if ((_localInputPaused != 0) == paused) return;
            Volatile.Write(ref _localInputPaused, paused ? 1 : 0);
            Interlocked.Increment(ref _localInputIntentRevision);
            if (paused)
            {
                var releases = new List<RemoteInputCommand>();
                while (_inputQueue.TryDequeue(out RemoteInputCommand command))
                {
                    if (IsLocalInputRelease(command)) releases.Add(command);
                    if (IsReliablePointerInput(command))
                        _pendingReliablePointerInputs = Math.Max(0, _pendingReliablePointerInputs - 1);
                }
                releases.AddRange(_localHeldKeys.Values.Select(command => command with { Kind = RemoteInputKind.KeyUp }));
                releases.AddRange(_localHeldButtons.Values.Select(command => command with
                {
                    Kind = RemoteInputKind.MouseUp,
                    X = _localLastPointerInput?.X ?? command.X,
                    Y = _localLastPointerInput?.Y ?? command.Y
                }));
                // Bounded normal input admission has already limited this collection. Preserve
                // every queued up; the ordinary queue cap must not discard cleanup releases.
                foreach (RemoteInputCommand command in releases.Distinct())
                {
                    _inputQueue.Enqueue(command, Math.Max(MaxQueuedInputs, releases.Count));
                    if (IsReliablePointerInput(command)) _pendingReliablePointerInputs++;
                }
                _lowLatencyVideoTransport?.DiscardPendingMouseMove();
                Interlocked.Increment(ref _mouseRouteGeneration);
                wake = true; // Also complete flush waiters after discarding ordinary input.
            }
        }
        if (wake)
        {
            ReleaseInputLoop();
            lock (_inputLock)
            {
                if (!_inputWriteInProgress && _inputQueue.Count == 0)
                {
                    foreach (var waiter in _inputFlushWaiters) waiter.TrySetResult(true);
                    _inputFlushWaiters.Clear();
                }
            }
        }
        LocalInputPausedChanged?.Invoke(paused);
    }

    private static bool IsLocalInputRelease(RemoteInputCommand command) =>
        command.Kind is RemoteInputKind.KeyUp or RemoteInputKind.MouseUp;

    // Track admitted pointer intent on both TCP and UDP routes. A pause release,
    // like the viewer's ownership cleanup, must use the latest pointer position,
    // not the old down position (MouseUp itself also moves the remote pointer).
    private void ObserveLocalPointerInput(RemoteInputCommand command)
    {
        if (command.Kind is RemoteInputKind.MouseMove or RemoteInputKind.MouseDown or
            RemoteInputKind.MouseUp or RemoteInputKind.MouseWheel)
            _localLastPointerInput = command;
    }

    private bool IsLocalInputIntentCurrent(long revision) =>
        !IsLocalInputPaused && Interlocked.Read(ref _localInputIntentRevision) == revision;

    private bool AdmitInputToWire(RemoteInputCommand command, long generation,
        long intentRevision, long mouseRouteGeneration)
    {
        lock (_inputLock)
        {
            if (generation != _inputConnectionGeneration ||
                (!IsLocalInputRelease(command) && !IsLocalInputIntentCurrent(intentRevision)) ||
                (command.Kind == RemoteInputKind.MouseMove && mouseRouteGeneration != Interlocked.Read(ref _mouseRouteGeneration)))
                return false;
            switch (command.Kind)
            {
                case RemoteInputKind.KeyDown:
                    _localHeldKeys[RemoteKeyboardInput.PhysicalKey(command)] = command;
                    break;
                case RemoteInputKind.KeyUp:
                    foreach (RemotePhysicalKey key in _localHeldKeys.Where(pair =>
                        pair.Key == RemoteKeyboardInput.PhysicalKey(command) ||
                        RemoteKeyboardInput.MatchesLegacyRelease(pair.Value, command)).Select(pair => pair.Key).ToArray())
                        _localHeldKeys.Remove(key);
                    break;
                case RemoteInputKind.MouseDown:
                    _localHeldButtons[command.Button] = command;
                    break;
                case RemoteInputKind.MouseUp:
                    _localHeldButtons.Remove(command.Button);
                    break;
            }
            return true;
        }
    }
}
