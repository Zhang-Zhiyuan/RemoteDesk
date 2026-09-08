package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertTrue;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;

import org.junit.Test;

public final class AndroidScreenCaptureSessionConcurrencyTest {
    @Test
    public void sourceDimensionsAreVolatileAndGettersDoNotTakeCaptureLock() throws Exception {
        Field sourceWidth = AndroidScreenCaptureSession.class.getDeclaredField("sourceWidth");
        Field sourceHeight = AndroidScreenCaptureSession.class.getDeclaredField("sourceHeight");
        Method getSourceWidth = AndroidScreenCaptureSession.class.getDeclaredMethod("getSourceWidth");
        Method getSourceHeight = AndroidScreenCaptureSession.class.getDeclaredMethod("getSourceHeight");

        assertTrue(Modifier.isVolatile(sourceWidth.getModifiers()));
        assertTrue(Modifier.isVolatile(sourceHeight.getModifiers()));
        assertFalse(Modifier.isSynchronized(getSourceWidth.getModifiers()));
        assertFalse(Modifier.isSynchronized(getSourceHeight.getModifiers()));
    }

    @Test
    public void streamGeometryPreservesMaximumEdgeAndIsAlwaysEven() {
        assertArrayEquals(
            new int[] { 720, 1280 },
            AndroidScreenCaptureSession.fitWithinMaxEdge(721, 1281, 1600));
        assertArrayEquals(
            new int[] { 720, 1600 },
            AndroidScreenCaptureSession.fitWithinMaxEdge(1080, 2400, 1600));
        assertArrayEquals(
            new int[] { 1600, 720 },
            AndroidScreenCaptureSession.fitWithinMaxEdge(2400, 1080, 1600));
        assertArrayEquals(
            new int[] { 2, 2 },
            AndroidScreenCaptureSession.fitWithinMaxEdge(1, 1, 1600));
        assertArrayEquals(
            new int[] { 736, 1600 },
            AndroidScreenCaptureSession.fitWithinMaxEdgeAligned(
                738,
                1600,
                1600,
                16,
                16));
    }
}
