package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import android.view.View;

import org.junit.Test;

public final class AndroidTouchInputPolicyTest {
    @Test
    public void moveThrottleUsesEightMillisecondMonotonicCadence() {
        assertFalse(AndroidTouchInputPolicy.shouldSendMove(100L, 107L));
        assertTrue(AndroidTouchInputPolicy.shouldSendMove(100L, 108L));
        assertTrue(AndroidTouchInputPolicy.shouldSendMove(100L, 99L));
    }

    @Test
    public void hiddenVideoSurfaceRemainsInLayoutForSurfaceCreation() {
        assertEquals(View.VISIBLE, AndroidTouchInputPolicy.hiddenVideoSurfaceVisibility());
        assertEquals(0.0f, AndroidTouchInputPolicy.hiddenVideoSurfaceAlpha(), 0.0f);
    }

    @Test
    public void jpegPresentationRemovesTheVendorSurfaceOverlay() {
        assertEquals(View.INVISIBLE, AndroidTouchInputPolicy.jpegVideoSurfaceVisibility());
    }
}
