package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import org.junit.Test;

public final class AndroidHostStatusTrackerTest {
    @Test public void unchangedStateDoesNotRequestUiOrNetworkRefresh() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        assertTrue(tracker.update(true, true, false, false, ""));
        for (int poll = 0; poll < 1000; poll++) {
            assertFalse(tracker.update(true, true, false, false, ""));
        }
    }

    @Test public void revocationAfterResumeAndReauthorizationAreObserved() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        tracker.update(true, true, false, false, "");
        assertTrue(tracker.update(true, false, true, false, ""));
        assertFalse(tracker.update(true, false, true, false, ""));
        assertTrue(tracker.update(true, true, false, false, ""));
    }

    @Test public void EveryIndividualFlagChangeIsObserved() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        tracker.update(false, false, false, false, "");
        assertTrue(tracker.update(true, false, false, false, ""));
        assertTrue(tracker.update(true, true, false, false, ""));
        assertTrue(tracker.update(true, true, true, false, ""));
        assertTrue(tracker.update(false, true, true, false, ""));
    }

    @Test public void failureUsesValueEqualityAndClearIsObserved() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        tracker.update(true, false, false, false, null);
        assertFalse(tracker.update(true, false, false, false, ""));
        assertTrue(tracker.update(true, false, false, false, "capture unavailable"));
        assertFalse(tracker.update(true, false, false, false, new String("capture unavailable")));
        assertTrue(tracker.update(true, false, false, false, ""));
    }

    @Test public void accessibilityBindingAfterResumeRefreshesAnOtherwiseIdleHost() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        tracker.update(true, false, true, false, "");
        assertTrue(tracker.update(true, false, true, true, ""));
        assertFalse(tracker.update(true, false, true, true, ""));
    }

    @Test public void accessibilityDisconnectionRefreshesWithoutServiceFlagChanges() {
        AndroidHostStatusTracker tracker = new AndroidHostStatusTracker();
        tracker.update(true, false, false, true, "");
        assertTrue(tracker.update(true, false, false, false, ""));
        assertFalse(tracker.update(true, false, false, false, ""));
    }
}
