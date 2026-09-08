package com.remotedesk.agent;

/**
 * Bounds retries after an ImageReader or RGBA buffer failure.
 *
 * <p>The platform capture session keeps the existing MediaProjection and
 * VirtualDisplay. Only the JPEG ImageReader Surface is replaced, and repeated
 * vendor failures are rate limited so the capture loop cannot spin while the
 * authenticated control session remains usable.</p>
 */
final class AndroidJpegRecoveryPolicy {
    private static final long[] RETRY_DELAYS_NANOS = new long[] {
        250_000_000L,
        1_000_000_000L,
        5_000_000_000L
    };

    private int consecutiveAttempts;
    private long retryNotBeforeNanos;

    boolean shouldAttempt(long nowNanos) {
        return retryNotBeforeNanos == 0L || nowNanos >= retryNotBeforeNanos;
    }

    void recordAttempt(long nowNanos) {
        int delayIndex = Math.min(
            consecutiveAttempts,
            RETRY_DELAYS_NANOS.length - 1);
        consecutiveAttempts++;
        retryNotBeforeNanos = saturatingAdd(
            nowNanos,
            RETRY_DELAYS_NANOS[delayIndex]);
    }

    void recordFrameSuccess() {
        consecutiveAttempts = 0;
        retryNotBeforeNanos = 0L;
    }

    void reset() {
        recordFrameSuccess();
    }

    int getConsecutiveAttempts() {
        return consecutiveAttempts;
    }

    long getRetryNotBeforeNanos() {
        return retryNotBeforeNanos;
    }

    private static long saturatingAdd(long value, long increment) {
        if (increment <= 0L) {
            return value;
        }
        if (value > Long.MAX_VALUE - increment) {
            return Long.MAX_VALUE;
        }
        return value + increment;
    }
}
