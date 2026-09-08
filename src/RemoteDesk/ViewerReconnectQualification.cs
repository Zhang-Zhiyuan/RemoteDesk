namespace RemoteDesk;

internal sealed class ViewerReconnectQualification
{
    private readonly object _syncRoot = new();
    private long _manualDisconnectFence;
    private long _nextIntentId;
    private PendingConnection? _pendingConnection;
    private ActiveIntent? _activeIntent;

    public long BeginManualConnection(
        ViewerConnectionSnapshot connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_syncRoot)
        {
            _pendingConnection = new PendingConnection(
                connection,
                _manualDisconnectFence);
            return _manualDisconnectFence;
        }
    }

    public ViewerReconnectQualificationResult ObserveDeviceInfo(
        long connectionGeneration,
        long reconnectAttemptOrdinal)
    {
        lock (_syncRoot)
        {
            ActiveIntent? intent = _activeIntent;
            bool intentCreated = false;
            if (intent is null)
            {
                PendingConnection? pending =
                    _pendingConnection;
                if (pending is null ||
                    pending.ManualDisconnectFence !=
                        _manualDisconnectFence)
                {
                    return default;
                }

                intent = new ActiveIntent(
                    ++_nextIntentId,
                    pending.Connection,
                    pending.ManualDisconnectFence);
                _activeIntent = intent;
                _pendingConnection = null;
                intentCreated = true;
            }
            else if (reconnectAttemptOrdinal <= 0 &&
                _pendingConnection is null)
            {
                // Only the active reconnect loop may qualify a replacement
                // generation without a new manual-connect owner. This keeps a
                // delayed DeviceInfo from an obsolete session from reviving
                // the last successful intent after its transport detached.
                return default;
            }

            if (intent.ManualDisconnectFence !=
                _manualDisconnectFence)
            {
                return default;
            }

            if (intent.QualifiedConnectionGeneration !=
                    long.MinValue &&
                connectionGeneration <
                    intent.QualifiedConnectionGeneration)
            {
                return default;
            }

            bool generationQualified =
                intent.QualifiedConnectionGeneration !=
                    connectionGeneration;
            intent.QualifiedConnectionGeneration =
                connectionGeneration;
            return new ViewerReconnectQualificationResult(
                Accepted: true,
                intentCreated,
                generationQualified,
                intent.IntentId,
                intent.Connection,
                connectionGeneration,
                reconnectAttemptOrdinal);
        }
    }

    public bool IsCurrent(
        long intentId,
        long connectionGeneration)
    {
        lock (_syncRoot)
        {
            return _activeIntent is { } intent &&
                intent.IntentId == intentId &&
                intent.ManualDisconnectFence ==
                    _manualDisconnectFence &&
                intent.QualifiedConnectionGeneration ==
                    connectionGeneration;
        }
    }

    public void SetActiveIntent(
        long intentId,
        ViewerConnectionSnapshot connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_syncRoot)
        {
            _nextIntentId = Math.Max(
                _nextIntentId,
                intentId);
            _activeIntent = new ActiveIntent(
                intentId,
                connection,
                _manualDisconnectFence);
            _pendingConnection = null;
        }
    }

    public void ClearActiveIntent(
        long intentId)
    {
        lock (_syncRoot)
        {
            if (_activeIntent?.IntentId == intentId)
            {
                _activeIntent = null;
            }
        }
    }

    public void CancelForManualDisconnect()
    {
        lock (_syncRoot)
        {
            _manualDisconnectFence++;
            _pendingConnection = null;
            _activeIntent = null;
        }
    }

    private sealed record PendingConnection(
        ViewerConnectionSnapshot Connection,
        long ManualDisconnectFence);

    private sealed class ActiveIntent(
        long intentId,
        ViewerConnectionSnapshot connection,
        long manualDisconnectFence)
    {
        public long IntentId { get; } = intentId;

        public ViewerConnectionSnapshot Connection { get; } =
            connection;

        public long ManualDisconnectFence { get; } =
            manualDisconnectFence;

        public long QualifiedConnectionGeneration { get; set; } =
            long.MinValue;
    }
}

internal readonly record struct ViewerReconnectQualificationResult(
    bool Accepted,
    bool IntentCreated,
    bool GenerationQualified,
    long IntentId,
    ViewerConnectionSnapshot? Connection,
    long ConnectionGeneration,
    long ReconnectAttemptOrdinal);
