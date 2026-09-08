package com.remotedesk.agent;

import static org.junit.Assert.*;
import org.junit.Test;

public final class AndroidScreenshotGateTest {
    @Test public void screenshotRateLimitDoesNotLookLikeEncoderOverload() {
        assertEquals(350d, AndroidScreenshotGate.frameBudgetMillis(30), 0.001);
        assertEquals(350d, AndroidScreenshotGate.frameBudgetMillis(8), 0.001);
        assertEquals(1000d, AndroidScreenshotGate.frameBudgetMillis(1), 0.001);
    }
    @Test public void boundsRequestsAndDropsConsumedFrames() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        AndroidScreenshotGate.Request request = gate.begin(0);
        assertNotNull(request);
        assertNull(gate.begin(1_000_000_000L));
        assertTrue(gate.complete(request, "frame1"));
        assertEquals("frame1", gate.poll());
        assertNull(gate.poll());
        assertNull(gate.begin(100_000_000L));
        assertNotNull(gate.begin(AndroidScreenshotGate.INTERVAL_NANOS));
    }

    @Test public void callbacksAfterStopCannotPublishOrRestart() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        AndroidScreenshotGate.Request request = gate.begin(0);
        gate.close();
        assertFalse(gate.complete(request, "private old screen"));
        assertNull(gate.poll());
        assertNull(gate.begin(Long.MAX_VALUE));
    }

    @Test public void onlyLatestCompletedFrameIsRetained() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        gate.complete(gate.begin(0), "old");
        gate.complete(gate.begin(1_000_000_000L), "latest");
        assertEquals("latest", gate.poll());
        gate.complete(gate.begin(2_000_000_000L), null);
        assertNull(gate.poll());
        assertNotNull(gate.begin(3_000_000_000L));
    }

    @Test public void missingCallbackTimesOutWithoutRequiringServiceRestart() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        AndroidScreenshotGate.Request old = gate.begin(0);
        assertFalse(old.recoveredFromTimeout);
        assertNull(gate.begin(AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS - 1));
        AndroidScreenshotGate.Request replacement = gate.begin(AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS);
        assertNotNull(replacement);
        assertTrue(replacement.recoveredFromTimeout);
        assertFalse(gate.isCurrent(old));
        assertTrue(gate.complete(replacement, "recovered"));
        assertEquals("recovered", gate.poll());
    }

    @Test public void staleCallbackCannotReleaseOrOverwriteTheNewRequest() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        AndroidScreenshotGate.Request old = gate.begin(0);
        AndroidScreenshotGate.Request current = gate.begin(AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS);
        assertFalse(gate.complete(old, "old screen"));
        assertNull(gate.poll());
        assertNull(gate.begin(AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS + AndroidScreenshotGate.INTERVAL_NANOS));
        assertTrue(gate.complete(current, "current screen"));
        assertFalse(gate.complete(old, null));
        assertEquals("current screen", gate.poll());
    }

    @Test public void duplicateAndForeignCallbacksCannotPublish() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        AndroidScreenshotGate<String> otherSession = new AndroidScreenshotGate<>();
        AndroidScreenshotGate.Request current = gate.begin(0);
        assertFalse(gate.complete(otherSession.begin(0), "foreign screen"));
        assertTrue(gate.complete(current, "first completion"));
        assertFalse(gate.complete(current, "duplicate completion"));
        assertFalse(gate.complete(null, "unsolicited completion"));
        assertEquals("first completion", gate.poll());
    }

    @Test public void nanoTimeCanBeNegativeOrWrapAround() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        assertNotNull(gate.begin(-1_000_000_000L));
        assertTrue(gate.begin(4_000_000_000L).recoveredFromTimeout);
        AndroidScreenshotGate<String> wrapping = new AndroidScreenshotGate<>();
        long start = Long.MAX_VALUE - AndroidScreenshotGate.INTERVAL_NANOS;
        wrapping.begin(start);
        assertNull(wrapping.begin(start + AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS - 1));
        assertTrue(wrapping.begin(start + AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS).recoveredFromTimeout);
    }

    @Test public void repeatedTimeoutsRemainRateLimitedAndRecover() {
        AndroidScreenshotGate<String> gate = new AndroidScreenshotGate<>();
        for (int i = 0; i < 100; i++) {
            long now = i * AndroidScreenshotGate.REQUEST_TIMEOUT_NANOS;
            AndroidScreenshotGate.Request request = gate.begin(now);
            assertNotNull(request);
            assertEquals(i > 0, request.recoveredFromTimeout);
            assertNull(gate.begin(now + AndroidScreenshotGate.INTERVAL_NANOS));
            if (i == 99) assertTrue(gate.complete(request, "recovered"));
        }
        assertEquals("recovered", gate.poll());
    }
}
