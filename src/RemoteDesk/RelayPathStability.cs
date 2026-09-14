namespace RemoteDesk;

// Shared policy semantics with Android/Python: milliseconds, bounded recent
// TLS-handshake observations, and hysteresis for FUTURE connections only.
internal sealed class RelayPathStability
{
    internal const int ProbeIntervalMilliseconds = 30_000;
    internal const int MinimumHoldMilliseconds = 180_000;
    internal const int HistoryMilliseconds = 600_000;
    internal const string SystemPath = "<system>";
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<(long At, double Value)>> _samples = new();
    private string? _preferred, _challenger;
    private long _selectedAt, _lastUse, _lastProbe = long.MinValue, _lastRound = long.MinValue;
    private int _wins;

    internal string? Preferred(long now)
    {
        lock (_sync)
        {
            if (_preferred is not null && now - _lastUse >= HistoryMilliseconds)
            {
                _preferred = _challenger = null;
                _wins = 0;
                _samples.Clear();
            }
            _lastUse = now;
            return _preferred;
        }
    }

    internal void Connected(string path, long now, string? failedPreferred = null)
    {
        lock (_sync)
        {
            _lastUse = now;
            // A real dial failure bypasses the hold; cancelled race losers and
            // a single slow handshake do not oscillate the cached preference.
            if (_preferred is null || _preferred == failedPreferred)
                Select(path, now);
        }
    }

    internal bool BeginProbe(long now)
    {
        lock (_sync)
        {
            if (_lastProbe != long.MinValue && now - _lastProbe < ProbeIntervalMilliseconds) return false;
            _lastProbe = now;
            return true;
        }
    }

    internal void ObserveRound(long now, IReadOnlyDictionary<string, double> observations)
    {
        lock (_sync)
        {
            if (_lastRound != long.MinValue && now - _lastRound < ProbeIntervalMilliseconds) return;
            _lastRound = now;
            foreach (var (path, value) in observations)
            {
                if (!_samples.TryGetValue(path, out var history)) _samples[path] = history = new();
                while (history.Count > 0 && (history.Count >= 8 || now - history.Peek().At >= HistoryMilliseconds))
                    history.Dequeue();
                history.Enqueue((now, double.IsFinite(value) && value >= 0 ? value : double.PositiveInfinity));
            }
            if (_preferred is null || !observations.ContainsKey(_preferred)) { ResetChallenge(); return; }
            double current = Score(_preferred, now, candidate: false);
            string? best = null;
            double bestScore = double.PositiveInfinity;
            foreach (string path in observations.Keys.Order(StringComparer.Ordinal))
            {
                if (path == _preferred) continue;
                double score = Score(path, now, candidate: true);
                if (score < bestScore && WorthSwitching(current, score)) { best = path; bestScore = score; }
            }
            if (best is null) { ResetChallenge(); return; }
            _wins = best == _challenger ? _wins + 1 : 1;
            _challenger = best;
            if (_wins >= 3 && now - _selectedAt >= MinimumHoldMilliseconds) Select(best, now);
        }
    }

    internal static bool WorthSwitching(double current, double candidate) =>
        double.IsFinite(current) && double.IsFinite(candidate) && candidate >= 0 &&
        current - candidate >= 8 && candidate <= current * .75;

    private double Score(string path, long now, bool candidate)
    {
        if (!_samples.TryGetValue(path, out var history)) return double.PositiveInfinity;
        double[] values = history.Where(item => now - item.At < HistoryMilliseconds).Select(item => item.Value).ToArray();
        if (values.Length < 3 || candidate && values.TakeLast(3).Any(value => !double.IsFinite(value)))
            return double.PositiveInfinity;
        double[] ok = values.Where(double.IsFinite).Order().ToArray();
        if (candidate && ok.Length < 3) return double.PositiveInfinity;
        if (ok.Length == 0) return 2000;
        double median = (ok[(ok.Length - 1) / 2] + ok[ok.Length / 2]) / 2;
        // P90 of eight samples is just the maximum: one old spike must not
        // count as several independent wins in overlapping sample windows.
        double p80 = ok[(int)Math.Ceiling(ok.Length * .8) - 1];
        return median + .75 * (p80 - median) + 500d * Math.Max(0, values.Length - ok.Length - 1) / values.Length;
    }

    private void Select(string path, long now) { _preferred = path; _selectedAt = now; ResetChallenge(); }
    private void ResetChallenge() { _challenger = null; _wins = 0; }
}
