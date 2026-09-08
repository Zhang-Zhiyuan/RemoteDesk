package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidNetworkGenerationTest {
    @Test
    public void initialAndDuplicateAvailabilityAreNoOps() {
        AndroidNetworkGeneration tracker = new AndroidNetworkGeneration();
        tracker.setInitialNetwork("wifi");

        assertFalse(tracker.onAvailable("wifi"));
        assertFalse(tracker.onLost("cellular"));
        assertEquals(0L, tracker.getGeneration());
    }

    @Test
    public void firstNetworkAfterOfflineAdvancesGenerationWithoutClosingAgain() {
        AndroidNetworkGeneration tracker = new AndroidNetworkGeneration();
        tracker.setInitialNetwork(null);

        assertFalse(tracker.onAvailable("wifi"));
        assertEquals(1L, tracker.getGeneration());
        assertFalse(tracker.onAvailable("wifi"));
        assertEquals(1L, tracker.getGeneration());
    }

    @Test
    public void liveHandoverInvalidatesExactlyOnce() {
        AndroidNetworkGeneration tracker = new AndroidNetworkGeneration();
        tracker.setInitialNetwork("wifi");

        assertTrue(tracker.onAvailable("cellular"));
        assertFalse(tracker.onLost("wifi"));
        assertFalse(tracker.onAvailable("cellular"));
        assertEquals(1L, tracker.getGeneration());
    }

    @Test
    public void lossInvalidatesAndLaterAvailabilityDoesNotDoubleClose() {
        AndroidNetworkGeneration tracker = new AndroidNetworkGeneration();
        tracker.setInitialNetwork("wifi");

        assertTrue(tracker.onLost("wifi"));
        assertFalse(tracker.onAvailable("cellular"));
        assertEquals(2L, tracker.getGeneration());
    }
}
