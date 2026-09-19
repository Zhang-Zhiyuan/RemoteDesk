package com.remotedesk.agent;

/** Per-connection, bounded silence allowance for an actual upload and its save receipt. */
final class AndroidFileTransferLiveness {
    static final long TRANSFER_INBOUND_TIMEOUT_NANOS = 150_000_000_000L;
    private Lease active;
    private boolean legacyDraining;
    private long legacyDrainStartedNanos;

    synchronized Lease begin() {
        if (active != null) throw new IllegalStateException("Another upload is active on this connection");
        legacyDraining = false;
        active = new Lease();
        return active;
    }

    synchronized boolean hasInboundTimedOut(long nowNanos, long lastInboundNanos) {
        // Sending bytes is not evidence of inbound liveness. Even a stalled upload
        // disconnects after 150 seconds without an authenticated incoming message.
        boolean draining = legacyDraining && nowNanos >= legacyDrainStartedNanos &&
            nowNanos - legacyDrainStartedNanos < TRANSFER_INBOUND_TIMEOUT_NANOS;
        return AndroidViewerHeartbeat.hasInboundTimedOut(nowNanos, lastInboundNanos,
            active != null || draining ? TRANSFER_INBOUND_TIMEOUT_NANOS : AndroidViewerHeartbeat.INBOUND_TIMEOUT_NANOS);
    }

    final class Lease implements AutoCloseable {
        private boolean legacyCompleted;
        private Lease() {}
        void completedWithoutReceipt(long nowNanos) {
            synchronized (AndroidFileTransferLiveness.this) {
                if (active != this) return;
                legacyCompleted = true;
                legacyDraining = true;
                legacyDrainStartedNanos = nowNanos;
            }
        }
        @Override public void close() {
            synchronized (AndroidFileTransferLiveness.this) {
                if (active == this) {
                    active = null;
                    if (!legacyCompleted) legacyDraining = false;
                }
            }
        }
    }
}
