package com.remotedesk.agent;

import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidDiscoverySocketsTest {
    @Test
    public void discoveryBindRetriesBrieflyWhenPortIsStillBeingReleased() {
        assertTrue(AndroidDiscoverySockets.getBindAttemptCount() >= 3);
        assertTrue(AndroidDiscoverySockets.getBindRetryDelayMillis() >= 50);
        assertTrue(AndroidDiscoverySockets.getBindRetryDelayMillis() <= 500);
    }
}
