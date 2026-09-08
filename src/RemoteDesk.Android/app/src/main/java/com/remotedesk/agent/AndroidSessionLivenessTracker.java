package com.remotedesk.agent;

final class AndroidSessionLivenessTracker {
    static final long DEFAULT_INBOUND_READ_TIMEOUT_NANOS = 30_000_000_000L;

    private final long inboundReadTimeoutNanos;
    private long readGeneration;
    private long readStartedAtNanos;
    private boolean waitingForInboundMessage;

    AndroidSessionLivenessTracker() {
        this(DEFAULT_INBOUND_READ_TIMEOUT_NANOS);
    }

    AndroidSessionLivenessTracker(long inboundReadTimeoutNanos) {
        if (inboundReadTimeoutNanos <= 0) {
            throw new IllegalArgumentException("inbound read timeout must be positive");
        }

        this.inboundReadTimeoutNanos = inboundReadTimeoutNanos;
    }

    synchronized long beginInboundRead(long nowNanos) {
        readGeneration = nextGeneration(readGeneration);
        readStartedAtNanos = nowNanos;
        waitingForInboundMessage = true;
        return readGeneration;
    }

    synchronized void endInboundRead(long generation) {
        if (!waitingForInboundMessage || generation != readGeneration) {
            return;
        }

        waitingForInboundMessage = false;
        readStartedAtNanos = 0;
    }

    synchronized boolean hasTimedOut(long nowNanos) {
        if (!waitingForInboundMessage || nowNanos < readStartedAtNanos) {
            return false;
        }

        return nowNanos - readStartedAtNanos >= inboundReadTimeoutNanos;
    }

    synchronized long getReadGeneration() {
        return readGeneration;
    }

    synchronized boolean isWaitingForInboundMessage() {
        return waitingForInboundMessage;
    }

    long getInboundReadTimeoutNanos() {
        return inboundReadTimeoutNanos;
    }

    private static long nextGeneration(long generation) {
        return generation == Long.MAX_VALUE ? 1 : generation + 1;
    }
}
