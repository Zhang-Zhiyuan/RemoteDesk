package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.concurrent.TimeUnit;

import org.junit.Test;

public final class AndroidHostSessionStateTest {
    @Test
    public void everyOwnerStartsWithIndependentNegotiationAndGeometry() {
        AndroidHostSessionState stale = new AndroidHostSessionState();
        stale.viewerVideoCodecs.set(RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B);
        stale.viewerCapabilities.set(1234);
        stale.lastFrameWidth.set(1920);
        stale.keyFrameRequested.set(true);
        stale.udpTargetBitrate.set(4_000_000);

        AndroidHostSessionState replacement = new AndroidHostSessionState();
        assertEquals(RemoteDeskProtocol.VIDEO_CODEC_JPEG, replacement.viewerVideoCodecs.get());
        assertEquals(0, replacement.viewerCapabilities.get());
        assertEquals(0, replacement.lastFrameWidth.get());
        assertFalse(replacement.keyFrameRequested.get());
        assertEquals(0, replacement.udpTargetBitrate.get());
        assertTrue(replacement.running.get());
    }

    @Test
    public void stopIsOwnerLocalAndIdempotent() {
        AndroidHostSessionState stale = new AndroidHostSessionState();
        AndroidHostSessionState replacement = new AndroidHostSessionState();
        assertTrue(stale.tryStop());
        assertFalse(stale.tryStop());
        assertTrue(replacement.running.get());
    }

    @Test
    public void stopSignalNeverWaitsForGestureCallback() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        long startedAt = System.nanoTime();

        assertTrue(state.tryStop());

        assertTrue(TimeUnit.NANOSECONDS.toMillis(
            System.nanoTime() - startedAt) < 100L);
    }

}
