package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidViewerHeartbeatTest {
    @Test
    public void pingAndInboundDeadlinesUseMonotonicDurations() {
        long start = 1_000L;
        assertFalse(AndroidViewerHeartbeat.shouldSendPing(
            start + AndroidViewerHeartbeat.PING_INTERVAL_NANOS - 1,
            start));
        assertTrue(AndroidViewerHeartbeat.shouldSendPing(
            start + AndroidViewerHeartbeat.PING_INTERVAL_NANOS,
            start));
        assertTrue(AndroidViewerHeartbeat.hasInboundTimedOut(
            start + AndroidViewerHeartbeat.INBOUND_TIMEOUT_NANOS,
            start));
    }

    @Test
    public void deviceInfoDeadlineEndsWhenCurrentOwnerQualifies() {
        long start = 2_000L;
        long deadline = start + AndroidViewerHeartbeat.DEVICE_INFO_TIMEOUT_NANOS;
        assertFalse(AndroidViewerHeartbeat.hasDeviceInfoTimedOut(deadline - 1, start, false));
        assertTrue(AndroidViewerHeartbeat.hasDeviceInfoTimedOut(deadline, start, false));
        assertFalse(AndroidViewerHeartbeat.hasDeviceInfoTimedOut(deadline + 1, start, true));
    }
}
