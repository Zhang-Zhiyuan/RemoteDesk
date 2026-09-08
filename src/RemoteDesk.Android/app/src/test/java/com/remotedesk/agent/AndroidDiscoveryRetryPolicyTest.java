package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidDiscoveryRetryPolicyTest {
    @Test
    public void retryDelayGrowsAndRemainsCapped() {
        assertEquals(250L, AndroidDiscoveryRetryPolicy.retryDelayMillis(0));
        assertEquals(500L, AndroidDiscoveryRetryPolicy.retryDelayMillis(1));
        assertEquals(1_000L, AndroidDiscoveryRetryPolicy.retryDelayMillis(2));
        assertEquals(8_000L, AndroidDiscoveryRetryPolicy.retryDelayMillis(5));
        assertEquals(8_000L, AndroidDiscoveryRetryPolicy.retryDelayMillis(30));
        assertEquals(250L, AndroidDiscoveryRetryPolicy.retryDelayMillis(-1));
    }

    @Test
    public void successfulBindResetsDelayButNotLogLimiter() {
        AndroidDiscoveryRetryPolicy policy = new AndroidDiscoveryRetryPolicy();
        long now = 1_000L;

        AndroidDiscoveryRetryPolicy.FailureAction first = policy.recordFailure(now);
        AndroidDiscoveryRetryPolicy.FailureAction second = policy.recordFailure(now + 1L);
        policy.recordBound();
        AndroidDiscoveryRetryPolicy.FailureAction afterBound =
            policy.recordFailure(now + 2L);

        assertEquals(250L, first.retryDelayMillis);
        assertEquals(500L, second.retryDelayMillis);
        assertEquals(250L, afterBound.retryDelayMillis);
        assertTrue(first.shouldLog);
        assertFalse(second.shouldLog);
        assertFalse(afterBound.shouldLog);
    }

    @Test
    public void failureLoggingResumesAfterRateLimitWindow() {
        AndroidDiscoveryRetryPolicy policy = new AndroidDiscoveryRetryPolicy();
        long now = 10_000L;

        assertTrue(policy.recordFailure(now).shouldLog);
        assertFalse(policy.recordFailure(
            now + AndroidDiscoveryRetryPolicy.failureLogIntervalNanos() - 1L).shouldLog);
        assertTrue(policy.recordFailure(
            now + AndroidDiscoveryRetryPolicy.failureLogIntervalNanos()).shouldLog);
    }
}
