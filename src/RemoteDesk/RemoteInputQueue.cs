namespace RemoteDesk;

internal sealed class RemoteInputQueue
{
    private const int CompactConsumedThreshold = 256;
    internal const int ReleaseReserveCapacity = 64;

    private readonly List<RemoteInputCommand> _items = [];
    private int _head;

    public int Count => _items.Count - _head;

    public void Clear()
    {
        _items.Clear();
        _head = 0;
    }

    public bool Enqueue(RemoteInputCommand command, int maxQueuedInputs)
    {
        if (maxQueuedInputs <= 0)
        {
            return false;
        }

        if (Count == 0 && _head > 0)
        {
            Clear();
        }

        if (command.Kind == RemoteInputKind.MouseMove)
        {
            if (Count == 1 &&
                _items[_head].Kind == RemoteInputKind.MouseMove &&
                _items[_head].X == command.X &&
                _items[_head].Y == command.Y)
            {
                return false;
            }

            RemovePendingMouseMoves();
        }

        if (Count >= maxQueuedInputs)
        {
            int dropIndex = FindDropIndex(_items, _head, _items.Count);
            if (dropIndex >= 0)
            {
                _items.RemoveAt(dropIndex);
            }
        }

        if (Count >= maxQueuedInputs &&
            (!IsRelease(command) ||
                (long)Count >= (long)maxQueuedInputs + ReleaseReserveCapacity))
        {
            return false;
        }

        _items.Add(command);
        return true;
    }

    public bool TryDequeue(out RemoteInputCommand command)
    {
        if (Count == 0)
        {
            command = default;
            if (_head > 0)
            {
                Clear();
            }

            return false;
        }

        command = _items[_head];
        _head++;
        CompactIfNeeded();
        return true;
    }

    public int RemovePendingMouseMoves()
    {
        int removed = 0;
        for (int index = _items.Count - 1; index >= _head; index--)
        {
            if (_items[index].Kind == RemoteInputKind.MouseMove)
            {
                _items.RemoveAt(index);
                removed++;
            }
        }

        CompactIfNeeded();
        return removed;
    }

    internal static int FindDropIndex(IReadOnlyList<RemoteInputCommand> inputQueue)
    {
        return FindDropIndex(inputQueue, 0, inputQueue.Count);
    }

    private static int FindDropIndex(
        IReadOnlyList<RemoteInputCommand> inputQueue,
        int startIndex,
        int endIndex)
    {
        for (int index = startIndex; index < endIndex; index++)
        {
            if (inputQueue[index].Kind == RemoteInputKind.MouseMove)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsRelease(RemoteInputCommand command)
    {
        return command.Kind is RemoteInputKind.KeyUp or RemoteInputKind.MouseUp;
    }

    private void CompactIfNeeded()
    {
        if (_head == 0)
        {
            return;
        }

        if (_head >= _items.Count)
        {
            Clear();
            return;
        }

        if (_head >= CompactConsumedThreshold && _head * 2 >= _items.Count)
        {
            _items.RemoveRange(0, _head);
            _head = 0;
        }
    }
}
