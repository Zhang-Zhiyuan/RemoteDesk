package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidInputInjectorTest {
    @Test public void typingAndNextClickWaitForThePreviousClickToFinish() {
        assertTrue(AndroidInputInjector.requiresCompletedPointerRelease(7));
        assertTrue(AndroidInputInjector.requiresCompletedPointerRelease(5));
        assertTrue(AndroidInputInjector.requiresCompletedPointerRelease(2));
        assertFalse(AndroidInputInjector.requiresCompletedPointerRelease(1));
        assertFalse(AndroidInputInjector.requiresCompletedPointerRelease(3));
        assertFalse(AndroidInputInjector.requiresCompletedPointerRelease(6));
    }
    @Test
    public void keyboardAndTextInputsDoNotRequireFrameGeometry() {
        assertFalse(AndroidInputInjector.requiresFrameGeometry(AndroidInputInjector.INPUT_KEY_DOWN));
        assertFalse(AndroidInputInjector.requiresFrameGeometry(AndroidInputInjector.INPUT_KEY_UP));
        assertFalse(AndroidInputInjector.requiresFrameGeometry(AndroidInputInjector.INPUT_TEXT));
    }

    @Test
    public void pointerInputsRequireFrameGeometry() {
        assertTrue(AndroidInputInjector.requiresFrameGeometry(1));
        assertTrue(AndroidInputInjector.requiresFrameGeometry(2));
        assertTrue(AndroidInputInjector.requiresFrameGeometry(3));
        assertTrue(AndroidInputInjector.requiresFrameGeometry(4));
        assertTrue(AndroidInputInjector.requiresFrameGeometry(8));
    }
}
