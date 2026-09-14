package com.remotedesk.agent;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Same millisecond-based policy as Windows/Python. Never migrates live sockets. */
final class RelayPathStability {
    static final long PROBE_INTERVAL_MS = 30_000, MINIMUM_HOLD_MS = 180_000, HISTORY_MS = 600_000;
    private static final class Sample {
        final long at; final double value;
        Sample(long at, double value) { this.at = at; this.value = value; }
    }
    private final Map<String, ArrayDeque<Sample>> samples = new HashMap<>();
    private String preferred, challenger;
    private long selectedAt, lastUse, lastProbe = Long.MIN_VALUE, lastRound = Long.MIN_VALUE;
    private int wins;

    synchronized String preferred(long now) {
        if (preferred != null && now - lastUse >= HISTORY_MS) {
            preferred = null; resetChallenge(); samples.clear();
        }
        lastUse = now;
        return preferred;
    }

    synchronized void connected(String path, long now, String failedPreferred) {
        lastUse = now;
        if (preferred == null || preferred.equals(failedPreferred)) select(path, now);
    }

    synchronized boolean beginProbe(long now) {
        if (lastProbe != Long.MIN_VALUE && now - lastProbe < PROBE_INTERVAL_MS) return false;
        lastProbe = now;
        return true;
    }

    synchronized void observeRound(long now, Map<String, Double> observations) {
        if (lastRound != Long.MIN_VALUE && now - lastRound < PROBE_INTERVAL_MS) return;
        lastRound = now;
        for (Map.Entry<String, Double> entry : observations.entrySet()) {
            ArrayDeque<Sample> history = samples.computeIfAbsent(entry.getKey(), key -> new ArrayDeque<>());
            while (!history.isEmpty() && (history.size() >= 8 || now - history.peek().at >= HISTORY_MS)) history.remove();
            double value = entry.getValue();
            history.add(new Sample(now, Double.isFinite(value) && value >= 0 ? value : Double.POSITIVE_INFINITY));
        }
        if (preferred == null || !observations.containsKey(preferred)) { resetChallenge(); return; }
        double current = score(preferred, now, false), bestScore = Double.POSITIVE_INFINITY;
        String best = null;
        List<String> paths = new ArrayList<>(observations.keySet());
        Collections.sort(paths);
        for (String path : paths) {
            if (path.equals(preferred)) continue;
            double value = score(path, now, true);
            if (value < bestScore && worthSwitching(current, value)) { best = path; bestScore = value; }
        }
        if (best == null) { resetChallenge(); return; }
        wins = best.equals(challenger) ? wins + 1 : 1;
        challenger = best;
        if (wins >= 3 && now - selectedAt >= MINIMUM_HOLD_MS) select(best, now);
    }

    static boolean worthSwitching(double current, double candidate) {
        return Double.isFinite(current) && Double.isFinite(candidate) && candidate >= 0 &&
            current - candidate >= 8 && candidate <= current * .75;
    }

    private double score(String path, long now, boolean candidate) {
        ArrayDeque<Sample> history = samples.get(path);
        if (history == null) return Double.POSITIVE_INFINITY;
        List<Double> values = new ArrayList<>(), ok = new ArrayList<>();
        for (Sample sample : history) if (now - sample.at < HISTORY_MS) {
            values.add(sample.value);
            if (Double.isFinite(sample.value)) ok.add(sample.value);
        }
        if (values.size() < 3 || candidate && ok.size() < 3) return Double.POSITIVE_INFINITY;
        if (candidate) for (int i = values.size() - 3; i < values.size(); i++)
            if (!Double.isFinite(values.get(i))) return Double.POSITIVE_INFINITY;
        if (ok.isEmpty()) return 2000;
        Collections.sort(ok);
        double median = (ok.get((ok.size() - 1) / 2) + ok.get(ok.size() / 2)) / 2;
        // P90 of eight samples is just the maximum. One old spike/failure
        // must not count as several independent wins in overlapping windows.
        double p80 = ok.get((int)Math.ceil(ok.size() * .8) - 1);
        return median + .75 * (p80 - median) + 500.0 * Math.max(0, values.size() - ok.size() - 1) / values.size();
    }

    private void select(String path, long now) { preferred = path; selectedAt = now; resetChallenge(); }
    private void resetChallenge() { challenger = null; wins = 0; }
}
