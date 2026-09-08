package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidInputInjectorTest {
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
