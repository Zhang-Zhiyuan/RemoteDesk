package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidSessionLivenessTrackerTest {
    @Test
    public void onlyAnOutstandingReadCanExpire() {
        AndroidSessionLivenessTracker tracker = new AndroidSessionLivenessTracker(100L);
        assertFalse(tracker.hasTimedOut(500L));

        long generation = tracker.beginInboundRead(1_000L);
        assertFalse(tracker.hasTimedOut(1_099L));
        assertTrue(tracker.hasTimedOut(1_100L));

        tracker.endInboundRead(generation);
        assertFalse(tracker.hasTimedOut(2_000L));
    }

    @Test
    public void staleReadCompletionCannotClearReplacementRead() {
        AndroidSessionLivenessTracker tracker = new AndroidSessionLivenessTracker(100L);
        long stale = tracker.beginInboundRead(10L);
        long current = tracker.beginInboundRead(20L);

        tracker.endInboundRead(stale);
        assertTrue(tracker.isWaitingForInboundMessage());
        assertFalse(tracker.hasTimedOut(119L));
        assertTrue(tracker.hasTimedOut(120L));

        tracker.endInboundRead(current);
        assertFalse(tracker.isWaitingForInboundMessage());
    }
}
