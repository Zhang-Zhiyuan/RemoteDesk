using System.Collections.Concurrent;

namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private sealed record PendingClipboardSnapshot(CancellationTokenSource Owner, long Generation,
        TaskCompletionSource<RemoteControlMessage> Completion);

    private readonly ConcurrentDictionary<string, PendingClipboardSnapshot> _clipboardSnapshots = new();
    private readonly ClipboardSnapshotSendGate _clipboardSnapshotSendGate = new();

    internal bool SupportsClipboardSnapshots =>
        (_remoteCapabilities & (RemoteDeviceCapabilities.ClipboardText | RemoteDeviceCapabilities.ClipboardSnapshotV1)) ==
        (RemoteDeviceCapabilities.ClipboardText | RemoteDeviceCapabilities.ClipboardSnapshotV1);
    internal bool IsClipboardRequestPending => _clipboardRequests.IsPending;
    internal event Action<long, uint, string>? LocalClipboardTextApplied;

    internal async Task<RemoteControlMessage?> GetClipboardSnapshotAsync(
        string knownRevision, long expectedGeneration, CancellationToken cancellationToken)
    {
        CancellationTokenSource? owner = _cancellationTokenSource;
        if (owner is null || !IsConnected || !SupportsClipboardSnapshots ||
            InputConnectionGeneration != expectedGeneration || _clipboardSnapshots.Count >= 2) return null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.Token, cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(8));
        string id = Guid.NewGuid().ToString("N");
        var pending = new PendingClipboardSnapshot(owner, expectedGeneration,
            new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_clipboardSnapshots.TryAdd(id, pending)) return null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!await _clipboardSnapshotSendGate.TrySendAsync(
                () => SendControlAsync(RemoteMessageCodec.EncodeClipboardSnapshotRequest(id, knownRevision), owner),
                lifetime.Token))
                return null;
            return await pending.Completion.Task.WaitAsync(lifetime.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            return null;
        }
        finally { _clipboardSnapshots.TryRemove(id, out _); }
    }

    private void CompleteClipboardSnapshot(RemoteControlMessage reply, CancellationTokenSource owner, long generation)
    {
        if (reply.TransferId is { } id && _clipboardSnapshots.TryGetValue(id, out var pending) &&
            ReferenceEquals(pending.Owner, owner) && pending.Generation == generation &&
            IsCurrentConnection(owner) && IsCurrentInputConnectionGeneration(generation))
            pending.Completion.TrySetResult(reply);
    }
}

// A snapshot is read-only and can expire while its TCP write is queued behind a
// file. Do not cancel a partially written frame or enqueue more snapshots while
// that old write is still pending. Its eventual response has an expired ID.
internal sealed class ClipboardSnapshotSendGate
{
    private int _pending;
    internal bool HasPendingSend => Volatile.Read(ref _pending) != 0;

    internal async Task<bool> TrySendAsync(Func<Task<bool>> send, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        Task<bool>? pending = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending = send();
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending is null)
                Volatile.Write(ref _pending, 0);
            else if (pending.IsCompleted)
            {
                // Cancellation can win WaitAsync just as the underlying task
                // faults; observe that exception even in this completion race.
                _ = pending.Exception;
                Volatile.Write(ref _pending, 0);
            }
            else
                _ = ReleaseAfterCompletionAsync(pending);
        }
    }

    private async Task ReleaseAfterCompletionAsync(Task<bool> pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* The caller expired; observe the late send failure. */ }
        finally { Volatile.Write(ref _pending, 0); }
    }
}
