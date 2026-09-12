namespace RemoteDesk;

// Session-only capture diagnostics, requested by an authenticated viewer.
// Never reads disk logs, settings, clipboard data or another session's history.
internal sealed class HostVideoDiagnostics
{
    internal const int MaximumCharacters = 4096;
    private readonly object _lock = new();
    private readonly Queue<string> _events = new();
    private string _metrics = string.Empty;
    private long _lastRequestAt = long.MinValue;

    internal void Record(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        string bounded = message.Length > 600 ? message[..600] : message;
        lock (_lock)
        {
            if (message.StartsWith("画面统计：", StringComparison.Ordinal) || message.StartsWith("H.264 统计：", StringComparison.Ordinal))
                _metrics = bounded;
            else
            {
                _events.Enqueue(bounded);
                while (_events.Count > 5) _events.Dequeue();
            }
        }
    }

    internal string? TryRead(long nowMilliseconds)
    {
        lock (_lock)
        {
            if (_lastRequestAt != long.MinValue && nowMilliseconds - _lastRequestAt < 1000) return null;
            _lastRequestAt = nowMilliseconds;
            string text = string.Join("\n", _events.Append(_metrics).Where(line => line.Length != 0));
            return text.Length == 0 ? "等待被控端视频状态。" : text;
        }
    }
}
