package com.remotedesk.agent;

/**
 * Connection-local telemetry for authenticated UDP mouse applied ACKs.
 * Networking never waits for this tracker and missing ACKs are harmless.
 */
final class AndroidMouseAckLatencyTracker {
    private static final long NANOS_PER_MICROSECOND = 1_000L;
    private static final long LOG_EVERY_SAMPLES = 64L;

    private long sampleCount;
    private long smoothedLatencyNanos;

    synchronized Snapshot record(long sequence, long elapsedNanos) {
        long safeElapsedNanos = Math.max(0L, elapsedNanos);
        sampleCount++;
        if (sampleCount == 1L) {
            smoothedLatencyNanos = safeElapsedNanos;
        } else {
            smoothedLatencyNanos +=
                (safeElapsedNanos - smoothedLatencyNanos) / 8L;
        }
        return new Snapshot(
            sequence,
            sampleCount,
            safeElapsedNanos / NANOS_PER_MICROSECOND,
            smoothedLatencyNanos / NANOS_PER_MICROSECOND,
            sampleCount == 1L || sampleCount % LOG_EVERY_SAMPLES == 0L);
    }

    static final class Snapshot {
        final long sequence;
        final long sampleCount;
        final long latestLatencyMicros;
        final long smoothedLatencyMicros;
        final boolean shouldLog;

        Snapshot(
            long sequence,
            long sampleCount,
            long latestLatencyMicros,
            long smoothedLatencyMicros,
            boolean shouldLog) {
            this.sequence = sequence;
            this.sampleCount = sampleCount;
            this.latestLatencyMicros = latestLatencyMicros;
            this.smoothedLatencyMicros = smoothedLatencyMicros;
            this.shouldLog = shouldLog;
        }
    }
}
