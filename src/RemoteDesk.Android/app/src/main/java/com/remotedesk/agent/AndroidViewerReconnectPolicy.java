package com.remotedesk.agent;

import java.io.IOException;
import java.net.SocketException;
import java.net.SocketTimeoutException;
import java.security.GeneralSecurityException;

final class AndroidViewerReconnectPolicy {
    static final long STABLE_SESSION_NANOS = 5_000_000_000L;
    private static final long[] RETRY_DELAYS_MILLIS = {
        500L,
        1_000L,
        2_000L,
        4_000L,
        8_000L,
        10_000L
    };

    private AndroidViewerReconnectPolicy() {
    }

    static long retryDelayMillis(int failedAttemptCount) {
        int index = Math.max(0, Math.min(failedAttemptCount, RETRY_DELAYS_MILLIS.length - 1));
        return RETRY_DELAYS_MILLIS[index];
    }

    static int recordAttemptResult(
        int failedAttemptCount,
        boolean stableQualifiedSession) {
        if (stableQualifiedSession) {
            return 0;
        }

        return failedAttemptCount == Integer.MAX_VALUE
            ? Integer.MAX_VALUE
            : Math.max(0, failedAttemptCount) + 1;
    }

    static boolean isRetryable(Throwable failure) {
        if (failure == null ||
            failure instanceof SecurityException ||
            failure instanceof AndroidSessionRejectedException) {
            return false;
        }

        return failure instanceof SocketTimeoutException ||
            failure instanceof SocketException ||
            failure instanceof IOException ||
            failure instanceof GeneralSecurityException;
    }

    static boolean shouldReconnect(
        boolean intentQualifiedOnce,
        boolean currentAttemptReceivedDeviceInfo,
        boolean networkChangedDuringAttempt) {
        return intentQualifiedOnce ||
            currentAttemptReceivedDeviceInfo ||
            networkChangedDuringAttempt;
    }

    static boolean isStableQualifiedSession(
        boolean currentAttemptReceivedDeviceInfo,
        long connectedDurationNanos) {
        return currentAttemptReceivedDeviceInfo &&
            connectedDurationNanos >= STABLE_SESSION_NANOS;
    }
}
