package com.remotedesk.agent;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public final class AndroidMouseAckLatencyTrackerTest {
    @Test
    public void firstSampleInitializesLatencyAndRequestsALog() {
        AndroidMouseAckLatencyTracker tracker = new AndroidMouseAckLatencyTracker();

        AndroidMouseAckLatencyTracker.Snapshot snapshot =
            tracker.record(17L, 2_500_000L);

        assertEquals(17L, snapshot.sequence);
        assertEquals(1L, snapshot.sampleCount);
        assertEquals(2_500L, snapshot.latestLatencyMicros);
        assertEquals(2_500L, snapshot.smoothedLatencyMicros);
        assertTrue(snapshot.shouldLog);
    }

    @Test
    public void smoothingDampensLatencySpikesWithoutBlockingTheInputPath() {
        AndroidMouseAckLatencyTracker tracker = new AndroidMouseAckLatencyTracker();
        tracker.record(1L, 1_000_000L);

        AndroidMouseAckLatencyTracker.Snapshot snapshot =
            tracker.record(2L, 9_000_000L);

        assertEquals(9_000L, snapshot.latestLatencyMicros);
        assertEquals(2_000L, snapshot.smoothedLatencyMicros);
        assertFalse(snapshot.shouldLog);
    }

    @Test
    public void periodicLoggingIsBounded() {
        AndroidMouseAckLatencyTracker tracker = new AndroidMouseAckLatencyTracker();
        AndroidMouseAckLatencyTracker.Snapshot snapshot = null;
        for (long sequence = 1L; sequence <= 64L; sequence++) {
            snapshot = tracker.record(sequence, 500_000L);
        }

        assertEquals(64L, snapshot.sampleCount);
        assertTrue(snapshot.shouldLog);
    }
}
