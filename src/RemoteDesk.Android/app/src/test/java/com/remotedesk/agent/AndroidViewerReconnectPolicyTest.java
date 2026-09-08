package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.io.IOException;
import java.net.SocketTimeoutException;
import java.security.GeneralSecurityException;

import org.junit.Test;

public final class AndroidViewerReconnectPolicyTest {
    @Test
    public void retryDelayBacksOffAndCapsAtTenSeconds() {
        long[] expected = { 500L, 1_000L, 2_000L, 4_000L, 8_000L, 10_000L, 10_000L };
        for (int index = 0; index < expected.length; index++) {
            assertEquals(expected[index], AndroidViewerReconnectPolicy.retryDelayMillis(index));
        }
        assertEquals(500L, AndroidViewerReconnectPolicy.retryDelayMillis(-5));
    }

    @Test
    public void onlyAStableQualifiedGenerationResetsFailureCount() {
        assertEquals(4, AndroidViewerReconnectPolicy.recordAttemptResult(3, false));
        assertEquals(0, AndroidViewerReconnectPolicy.recordAttemptResult(99, true));
        assertEquals(
            Integer.MAX_VALUE,
            AndroidViewerReconnectPolicy.recordAttemptResult(Integer.MAX_VALUE, false));
    }

    @Test
    public void recordedFailuresMapToTheDeclaredReconnectCadence() {
        int failures = 0;
        long[] expected = { 500L, 1_000L, 2_000L, 4_000L, 8_000L, 10_000L };
        for (long expectedDelay : expected) {
            failures = AndroidViewerReconnectPolicy.recordAttemptResult(failures, false);
            assertEquals(
                expectedDelay,
                AndroidViewerReconnectPolicy.retryDelayMillis(Math.max(0, failures - 1)));
        }
    }

    @Test
    public void transientTransportFailuresRetryButBadPasswordDoesNot() {
        assertTrue(AndroidViewerReconnectPolicy.isRetryable(new IOException("eof")));
        assertTrue(AndroidViewerReconnectPolicy.isRetryable(new SocketTimeoutException("timeout")));
        assertTrue(AndroidViewerReconnectPolicy.isRetryable(new GeneralSecurityException("cipher")));
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(new SecurityException("bad password")));
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(
            new AndroidSessionRejectedException("session replaced")));
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(new IllegalArgumentException("bad endpoint")));
    }

    @Test
    public void automaticReconnectRequiresAPreviouslyQualifiedIntent() {
        assertFalse(AndroidViewerReconnectPolicy.shouldReconnect(false, false, false));
        assertTrue(AndroidViewerReconnectPolicy.shouldReconnect(false, true, false));
        assertTrue(AndroidViewerReconnectPolicy.shouldReconnect(true, false, false));
    }

    @Test
    public void networkHandoverRetriesBeforeTheFirstDeviceInfo() {
        assertTrue(AndroidViewerReconnectPolicy.shouldReconnect(false, false, true));
    }

    @Test
    public void deviceInfoShortCloseDoesNotResetBackoffStorm() {
        assertFalse(AndroidViewerReconnectPolicy.isStableQualifiedSession(
            true,
            AndroidViewerReconnectPolicy.STABLE_SESSION_NANOS - 1));
        assertTrue(AndroidViewerReconnectPolicy.isStableQualifiedSession(
            true,
            AndroidViewerReconnectPolicy.STABLE_SESSION_NANOS));
        assertFalse(AndroidViewerReconnectPolicy.isStableQualifiedSession(
            false,
            AndroidViewerReconnectPolicy.STABLE_SESSION_NANOS * 2));
    }
}
