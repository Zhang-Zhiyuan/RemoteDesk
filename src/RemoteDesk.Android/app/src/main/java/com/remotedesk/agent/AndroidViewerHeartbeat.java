package com.remotedesk.agent;

final class AndroidViewerHeartbeat {
    static final long PING_INTERVAL_NANOS = 5_000_000_000L;
    static final long INBOUND_TIMEOUT_NANOS = 18_000_000_000L;
    static final long DEVICE_INFO_TIMEOUT_NANOS = 5_000_000_000L;

    private AndroidViewerHeartbeat() {
    }

    static boolean shouldSendPing(long nowNanos, long lastPingNanos) {
        return elapsedAtLeast(nowNanos, lastPingNanos, PING_INTERVAL_NANOS);
    }

    static boolean hasInboundTimedOut(long nowNanos, long lastInboundNanos) {
        return elapsedAtLeast(nowNanos, lastInboundNanos, INBOUND_TIMEOUT_NANOS);
    }

    static boolean hasDeviceInfoTimedOut(
        long nowNanos,
        long connectedAtNanos,
        boolean deviceInfoReceived) {
        return !deviceInfoReceived &&
            elapsedAtLeast(nowNanos, connectedAtNanos, DEVICE_INFO_TIMEOUT_NANOS);
    }

    private static boolean elapsedAtLeast(long nowNanos, long startedAtNanos, long durationNanos) {
        return nowNanos >= startedAtNanos && nowNanos - startedAtNanos >= durationNanos;
    }
}
