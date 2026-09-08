namespace RemoteDesk;

/// <summary>
/// Serializes every capture-target publication bundle for one host session.
/// Capture state locks are never held across network I/O. This coordinator's
/// own mutex deliberately remains held across the publication await so a
/// target bundle is ordered atomically against other bundles and frame-send
/// admission. A newer generation announces itself before waiting, closing
/// admission immediately and suppressing older queued publications.
/// </summary>
internal sealed class CaptureTargetPublicationCoordinator : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _stateLock = new();
    private int _desiredGeneration = int.MinValue;
    private int _publishedGeneration = int.MinValue;
    private TaskCompletionSource _publicationChanged =
        CreatePublicationSignal();

    public void AdvanceGeneration(int generation) =>
        AnnounceDesiredGeneration(generation);

    public async Task<bool> PublishIfCurrentAsync(
        CaptureTargetStateSnapshot expected,
        Func<CaptureTargetStateSnapshot, bool> isCurrent,
        Func<CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected.Target);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(publish);

        AnnounceDesiredGeneration(expected.Generation);
        await _mutex.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!IsDesiredGeneration(expected.Generation) ||
                !isCurrent(expected))
            {
                return false;
            }

            await publish(cancellationToken)
                .ConfigureAwait(false);

            // A capture transition can announce a newer desired generation
            // while the network write is in flight. Never open frame
            // admission for the old generation in that case. The old bundle
            // is ordered before the newer publication by this same mutex.
            if (!isCurrent(expected) ||
                !TryOpenPublishedGeneration(
                    expected.Generation))
            {
                return false;
            }
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<T> AdmitFrameIfCurrentAsync<T>(
        int expectedGeneration,
        Func<int, bool> isCurrent,
        Func<CancellationToken, T> admit,
        T rejectedValue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(admit);
        if (!await WaitForPublishedGenerationAsync(
                expectedGeneration,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return rejectedValue;
        }

        await _mutex.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!IsFrameGenerationOpen(expectedGeneration) ||
                !isCurrent(expectedGeneration))
            {
                return rejectedValue;
            }

            return admit(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> AdmitFrameIfCurrentAsync(
        int expectedGeneration,
        Func<int, bool> isCurrent,
        Func<CancellationToken, Task> admit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(admit);
        if (!await WaitForPublishedGenerationAsync(
                expectedGeneration,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return false;
        }

        await _mutex.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!IsFrameGenerationOpen(expectedGeneration) ||
                !isCurrent(expectedGeneration))
            {
                return false;
            }

            await admit(cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void AnnounceDesiredGeneration(int generation)
    {
        TaskCompletionSource? changed = null;
        lock (_stateLock)
        {
            if (generation > _desiredGeneration)
            {
                changed = _publicationChanged;
                _desiredGeneration = generation;
                _publicationChanged =
                    CreatePublicationSignal();
            }
        }

        // Never run continuations while holding the state lock.
        changed?.TrySetResult();
    }

    private bool IsDesiredGeneration(int generation)
    {
        lock (_stateLock)
        {
            return generation == _desiredGeneration;
        }
    }

    private bool IsFrameGenerationOpen(int generation)
    {
        lock (_stateLock)
        {
            return generation == _desiredGeneration &&
                generation == _publishedGeneration;
        }
    }

    private bool TryOpenPublishedGeneration(int generation)
    {
        TaskCompletionSource? changed = null;
        lock (_stateLock)
        {
            if (generation != _desiredGeneration)
            {
                return false;
            }

            _publishedGeneration = generation;
            changed = _publicationChanged;
        }

        changed.TrySetResult();
        return true;
    }

    private async Task<bool> WaitForPublishedGenerationAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task publicationChanged;
            lock (_stateLock)
            {
                if (generation != _desiredGeneration)
                {
                    return false;
                }

                if (generation == _publishedGeneration)
                {
                    return true;
                }

                publicationChanged =
                    _publicationChanged.Task;
            }

            await publicationChanged.WaitAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource
        CreatePublicationSignal() =>
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

    public void Dispose()
    {
        TaskCompletionSource changed;
        lock (_stateLock)
        {
            changed = _publicationChanged;
        }

        changed.TrySetCanceled();
        _mutex.Dispose();
    }
}
