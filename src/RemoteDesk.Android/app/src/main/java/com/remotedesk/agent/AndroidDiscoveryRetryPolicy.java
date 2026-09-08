package com.remotedesk.agent;

import java.util.concurrent.TimeUnit;

/** Continuous, capped discovery rebind backoff with rate-limited diagnostics. */
final class AndroidDiscoveryRetryPolicy {
    private static final long INITIAL_RETRY_DELAY_MILLIS = 250L;
    private static final long MAX_RETRY_DELAY_MILLIS = 8_000L;
    private static final long FAILURE_LOG_INTERVAL_NANOS =
        TimeUnit.SECONDS.toNanos(30L);

    private int consecutiveFailures;
    private long nextFailureLogAtNanos;

    synchronized FailureAction recordFailure(long nowNanos) {
        int delayIndex = consecutiveFailures;
        if (consecutiveFailures < 30) {
            consecutiveFailures++;
        }

        boolean shouldLog = nextFailureLogAtNanos == 0L ||
            nowNanos >= nextFailureLogAtNanos;
        if (shouldLog) {
            nextFailureLogAtNanos = saturatingAdd(
                nowNanos,
                FAILURE_LOG_INTERVAL_NANOS);
        }
        return new FailureAction(retryDelayMillis(delayIndex), shouldLog);
    }

    synchronized void recordBound() {
        consecutiveFailures = 0;
    }

    static long retryDelayMillis(int consecutiveFailureIndex) {
        int shift = Math.max(0, Math.min(30, consecutiveFailureIndex));
        long multiplier = 1L << shift;
        long delay;
        try {
            delay = Math.multiplyExact(INITIAL_RETRY_DELAY_MILLIS, multiplier);
        } catch (ArithmeticException ignored) {
            delay = Long.MAX_VALUE;
        }
        return Math.min(MAX_RETRY_DELAY_MILLIS, delay);
    }

    static long failureLogIntervalNanos() {
        return FAILURE_LOG_INTERVAL_NANOS;
    }

    private static long saturatingAdd(long left, long right) {
        if (right > 0L && left > Long.MAX_VALUE - right) {
            return Long.MAX_VALUE;
        }
        return left + right;
    }

    static final class FailureAction {
        final long retryDelayMillis;
        final boolean shouldLog;

        FailureAction(long retryDelayMillis, boolean shouldLog) {
            this.retryDelayMillis = retryDelayMillis;
            this.shouldLog = shouldLog;
        }
    }
}
