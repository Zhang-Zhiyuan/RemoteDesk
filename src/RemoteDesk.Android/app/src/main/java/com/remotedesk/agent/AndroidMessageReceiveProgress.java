package com.remotedesk.agent;

import java.util.function.LongSupplier;

/** Per-connection grace for a large encrypted message, not authenticated liveness. */
final class AndroidMessageReceiveProgress {
    static final long MAX_MESSAGE_NANOS = 90_000_000_000L;
    private final LongSupplier clock;
    private long startedAt, lastProgressAt;
    private boolean active, receivedBytes;

    AndroidMessageReceiveProgress() { this(System::nanoTime); }
    AndroidMessageReceiveProgress(LongSupplier clock) { this.clock = clock; }

    synchronized void begin() {
        startedAt = clock.getAsLong();
        receivedBytes = false;
        active = true;
    }

    synchronized void advance() {
        lastProgressAt = clock.getAsLong();
        receivedBytes = true;
    }

    synchronized void end() { active = false; }

    synchronized boolean isMakingProgress(long nowNanos, long idleTimeoutNanos) {
        long elapsed = nowNanos - startedAt;
        long idle = nowNanos - lastProgressAt;
        return active && receivedBytes && elapsed >= 0 && elapsed < MAX_MESSAGE_NANOS &&
            idle >= 0 && idle < idleTimeoutNanos;
    }
}
