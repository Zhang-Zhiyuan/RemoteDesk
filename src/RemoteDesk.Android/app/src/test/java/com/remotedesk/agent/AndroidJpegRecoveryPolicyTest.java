package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidJpegRecoveryPolicyTest {
    @Test
    public void repeatedFailuresBackOffAndCapAtFiveSeconds() {
        AndroidJpegRecoveryPolicy policy = new AndroidJpegRecoveryPolicy();
        long now = 10_000_000_000L;

        assertTrue(policy.shouldAttempt(now));
        policy.recordAttempt(now);
        assertFalse(policy.shouldAttempt(now));
        assertEquals(now + 250_000_000L, policy.getRetryNotBeforeNanos());

        now += 250_000_000L;
        assertTrue(policy.shouldAttempt(now));
        policy.recordAttempt(now);
        assertEquals(now + 1_000_000_000L, policy.getRetryNotBeforeNanos());

        now += 1_000_000_000L;
        policy.recordAttempt(now);
        assertEquals(now + 5_000_000_000L, policy.getRetryNotBeforeNanos());

        now += 5_000_000_000L;
        policy.recordAttempt(now);
        assertEquals(now + 5_000_000_000L, policy.getRetryNotBeforeNanos());
    }

    @Test
    public void successfulFrameResetsBackoff() {
        AndroidJpegRecoveryPolicy policy = new AndroidJpegRecoveryPolicy();
        long now = 20_000_000_000L;
        policy.recordAttempt(now);
        policy.recordAttempt(now);

        policy.recordFrameSuccess();

        assertEquals(0, policy.getConsecutiveAttempts());
        assertEquals(0L, policy.getRetryNotBeforeNanos());
        assertTrue(policy.shouldAttempt(now));
    }

    @Test
    public void deadlineAdditionSaturates() {
        AndroidJpegRecoveryPolicy policy = new AndroidJpegRecoveryPolicy();
        long now = Long.MAX_VALUE - 10L;
        policy.recordAttempt(now);
        policy.recordAttempt(now);

        assertEquals(Long.MAX_VALUE, policy.getRetryNotBeforeNanos());
    }
}
