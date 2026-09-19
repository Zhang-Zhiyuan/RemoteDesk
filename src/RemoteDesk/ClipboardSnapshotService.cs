namespace RemoteDesk;

internal sealed record ClipboardSnapshotResult(bool Success, string Revision, bool HasText,
    bool Changed, string Text, string StatusMessage);

// Session-owned read-only snapshots. A nonzero, unchanged Windows sequence
// avoids reopening the clipboard. Only stable before/after reads are cached.
internal sealed class ClipboardSnapshotService
{
    private readonly Func<uint> _readSequence;
    private readonly Func<Task<string>> _readText;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _readTimeout;
    private uint _cachedSequence;
    private string _cachedText = string.Empty;
    private string _cachedRevision = string.Empty;

    internal ClipboardSnapshotService(Func<uint>? readSequence = null, Func<Task<string>>? readText = null,
        TimeSpan? readTimeout = null)
    {
        _readSequence = readSequence ?? ClipboardTextService.ReadClipboardSequenceNumber;
        _readText = readText ?? ClipboardTextService.GetTextAsync;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(3);
    }

    internal async Task<ClipboardSnapshotResult> ReadAsync(string knownRevision, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_readTimeout);
        bool entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                uint before = _readSequence();
                if (before == 0) return Failed("剪贴板暂不可访问，请确认被控端处于可交互桌面后重试。");
                if (before == _cachedSequence) return FromCache(knownRevision);
                string text = await _readText().WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (text is null || text.Length > RemoteMessageCodec.MaxClipboardSnapshotTextChars)
                    return Failed("剪贴板文本超过 256000 字符，未自动同步；请缩小内容后重试。");
                uint after = _readSequence();
                if (after != before || after == 0)
                {
                    await Task.Delay(15, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                string revision = RemoteMessageCodec.ComputeClipboardRevision(text);
                _cachedText = text;
                _cachedRevision = revision;
                _cachedSequence = after;
                return FromCache(knownRevision);
            }
            return Failed("剪贴板正在变化，请稍后重试；未返回旧内容。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Failed("暂时无法读取剪贴板，请关闭占用剪贴板的窗口后重试。");
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private ClipboardSnapshotResult FromCache(string knownRevision)
    {
        bool changed = !string.Equals(_cachedRevision, knownRevision, StringComparison.Ordinal);
        return new(true, _cachedRevision, _cachedText.Length != 0, changed,
            changed ? _cachedText : string.Empty, string.Empty);
    }

    private static ClipboardSnapshotResult Failed(string message) => new(false, string.Empty, false, false, string.Empty, message);
}
