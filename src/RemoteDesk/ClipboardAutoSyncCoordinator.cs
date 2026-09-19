using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk;

// One coordinator per connection. No file enumeration, no unsolicited legacy
// replies, and no clipboard writes after a new local copy or session transition.
internal sealed class ClipboardAutoSyncCoordinator(
    Func<uint> readSequence,
    Func<Task<string>> readText,
    Func<string, Task<bool>> sendText,
    Func<string, Task<RemoteControlMessage?>> getSnapshot,
    Func<string, Func<bool>, Task<uint>> applyText,
    Func<bool> isCurrent)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private uint _observedSequence = readSequence();
    private string _remoteRevision = "";
    private bool _baselined;
    private bool _synchronized;
    private long _epoch;

    internal static string Revision(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Manual Ctrl+C/read-button writes must not be echoed back as a fresh local copy.
    internal void ObserveAppliedText(uint sequence, string text)
    {
        lock (_gate)
        {
            _observedSequence = sequence;
            _remoteRevision = Revision(text);
            _baselined = _synchronized = true;
            _epoch++;
        }
    }

    internal async Task<bool> SyncAsync(bool ensureLocalBeforeInput, bool allowLocalPush)
    {
        await _sync.WaitAsync();
        try
        {
            if (!isCurrent()) return false;
            uint sequence = readSequence();
            // Zero means no reliable clipboard sequence on this desktop (for
            // example during locking); it cannot fence an automatic write.
            if (sequence == 0) return false;
            uint observed;
            string revision;
            bool baseline, synchronized;
            long epoch;
            lock (_gate)
            {
                observed = _observedSequence;
                revision = _remoteRevision;
                baseline = _baselined;
                synchronized = _synchronized;
                epoch = _epoch;
            }
            bool Current()
            {
                lock (_gate) return _epoch == epoch && isCurrent();
            }
            bool LocalUnchanged() => Current() && readSequence() == sequence;

            bool localChanged = sequence != observed;
            if (localChanged || (ensureLocalBeforeInput && !synchronized))
            {
                if (!allowLocalPush) return false;
                string text = await readText();
                if (!LocalUnchanged()) return false;
                if (!string.IsNullOrEmpty(text))
                {
                    // Respect the existing wire limit; never silently truncate a copy.
                    if (text.Length > 256000) return false;
                    string localRevision = Revision(text);
                    // A new sequence is a fresh local copy even if its text
                    // matches our previous revision: the peer may have copied
                    // something else since the last snapshot.
                    if ((localChanged || localRevision != revision) && !await sendText(text)) return false;
                    if (!LocalUnchanged()) return false;
                    lock (_gate)
                    {
                        if (_epoch != epoch) return false;
                        _observedSequence = sequence;
                        _remoteRevision = localRevision;
                        _baselined = _synchronized = true;
                    }
                    return true;
                }
                // A copied file/image/empty value is not permission to restore stale text.
                // Rebaseline without applying the peer's existing clipboard.
                baseline = false;
                lock (_gate)
                {
                    if (_epoch != epoch) return false;
                    _observedSequence = sequence;
                    _baselined = false;
                    _synchronized = true;
                }
            }
            else if (ensureLocalBeforeInput) return true;

            RemoteControlMessage? reply = await getSnapshot(revision);
            if (!Current() || reply is not { Success: true, ClipboardRevision: { } remoteRevision }) return false;
            if (!LocalUnchanged()) return false;
            if (baseline && reply.ClipboardChanged && reply.ClipboardHasText && !string.IsNullOrEmpty(reply.Text))
            {
                uint appliedSequence = await applyText(reply.Text, LocalUnchanged);
                if (!Current()) return false;
                lock (_gate)
                {
                    if (_epoch != epoch) return false;
                    _observedSequence = appliedSequence;
                    _synchronized = true;
                }
            }
            lock (_gate)
            {
                if (_epoch != epoch) return false;
                _remoteRevision = remoteRevision;
                _baselined = true;
            }
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or
            System.Runtime.InteropServices.ExternalException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
        finally { _sync.Release(); }
    }
}
