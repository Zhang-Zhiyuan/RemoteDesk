package com.remotedesk.agent;

import java.util.concurrent.TimeUnit;

/**
 * Detects MediaCodec instances that configure and start successfully but never
 * deliver usable startup video output.
 *
 * <p>The timeouts intentionally cover hundreds of expected 30 fps frame
 * periods. Android compositors and Surface-input encoders may legitimately
 * become quiet on a motionless display. After at least one frame, the watchdog
 * requests a sync frame and reports static silence, but never tears down a
 * healthy session solely because no changed buffer arrived.</p>
 */
final class AndroidH264OutputWatchdog {
    static final long STARTUP_KEY_FRAME_PROBE_MILLIS = 4_000L;
    static final long STARTUP_TIMEOUT_MILLIS = 8_000L;
    static final long STALLED_KEY_FRAME_PROBE_MILLIS = 6_000L;
    static final long STALLED_TIMEOUT_MILLIS = 12_000L;

    private static final long NO_OUTPUT_RECORDED = Long.MIN_VALUE;

    private final long startupProbeNanos;
    private final long startupTimeoutNanos;
    private final long stalledProbeNanos;
    private final long stalledTimeoutNanos;
    private final long startedAtNanos;

    private long lastOutputAtNanos = NO_OUTPUT_RECORDED;
    private boolean keyFrameProbeRequested;
    private boolean staticSilenceReported;

    AndroidH264OutputWatchdog(long startedAtNanos) {
        this(
            startedAtNanos,
            TimeUnit.MILLISECONDS.toNanos(STARTUP_KEY_FRAME_PROBE_MILLIS),
            TimeUnit.MILLISECONDS.toNanos(STARTUP_TIMEOUT_MILLIS),
            TimeUnit.MILLISECONDS.toNanos(STALLED_KEY_FRAME_PROBE_MILLIS),
            TimeUnit.MILLISECONDS.toNanos(STALLED_TIMEOUT_MILLIS));
    }

    AndroidH264OutputWatchdog(
        long startedAtNanos,
        long startupProbeNanos,
        long startupTimeoutNanos,
        long stalledProbeNanos,
        long stalledTimeoutNanos) {
        validateWindow(startupProbeNanos, startupTimeoutNanos, "startup");
        validateWindow(stalledProbeNanos, stalledTimeoutNanos, "stalled");
        this.startedAtNanos = startedAtNanos;
        this.startupProbeNanos = startupProbeNanos;
        this.startupTimeoutNanos = startupTimeoutNanos;
        this.stalledProbeNanos = stalledProbeNanos;
        this.stalledTimeoutNanos = stalledTimeoutNanos;
    }

    Action evaluate(long nowNanos) {
        boolean waitingForFirstOutput = !hasProducedOutput();
        long silenceNanos = silenceNanos(nowNanos);
        long timeoutNanos = waitingForFirstOutput ? startupTimeoutNanos : stalledTimeoutNanos;
        if (waitingForFirstOutput && silenceNanos >= timeoutNanos) {
            return Action.Fallback;
        }

        if (!waitingForFirstOutput && silenceNanos >= timeoutNanos) {
            if (!staticSilenceReported) {
                staticSilenceReported = true;
                return Action.StaticSilence;
            }
            return Action.None;
        }

        long probeNanos = waitingForFirstOutput ? startupProbeNanos : stalledProbeNanos;
        if (!keyFrameProbeRequested && silenceNanos >= probeNanos) {
            keyFrameProbeRequested = true;
            return Action.RequestKeyFrame;
        }

        return Action.None;
    }

    boolean recordOutput(long nowNanos) {
        boolean firstOutput = !hasProducedOutput();
        lastOutputAtNanos = nowNanos;
        keyFrameProbeRequested = false;
        staticSilenceReported = false;
        return firstOutput;
    }

    boolean hasProducedOutput() {
        return lastOutputAtNanos != NO_OUTPUT_RECORDED;
    }

    long silenceMillis(long nowNanos) {
        return TimeUnit.NANOSECONDS.toMillis(silenceNanos(nowNanos));
    }

    private long silenceNanos(long nowNanos) {
        long anchor = hasProducedOutput() ? lastOutputAtNanos : startedAtNanos;
        return Math.max(0L, nowNanos - anchor);
    }

    private static void validateWindow(long probeNanos, long timeoutNanos, String name) {
        if (probeNanos <= 0L || timeoutNanos <= probeNanos) {
            throw new IllegalArgumentException(
                name + " watchdog window must have 0 < probe < timeout.");
        }
    }

    enum Action {
        None,
        RequestKeyFrame,
        StaticSilence,
        Fallback
    }
}
