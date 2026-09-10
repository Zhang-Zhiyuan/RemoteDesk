package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidViewerScalePolicyTest {
    @Test
    public void clearModeDoesNotInventPixelsForSmallerRemoteFrame() {
        assertEquals(
            1.0f,
            AndroidViewerScalePolicy.fitScale(
                2560,
                1280,
                1920,
                1080,
                false),
            0.0001f);
    }

    @Test
    public void fitModeStillFillsAvailableViewerArea() {
        assertEquals(
            1280.0f / 1080.0f,
            AndroidViewerScalePolicy.fitScale(
                2560,
                1280,
                1920,
                1080,
                true),
            0.0001f);
    }

    @Test
    public void bothModesDownscaleFramesThatDoNotFit() {
        float expected = 1280.0f / 2160.0f;
        assertEquals(
            expected,
            AndroidViewerScalePolicy.fitScale(
                2560,
                1280,
                3840,
                2160,
                false),
            0.0001f);
        assertEquals(
            expected,
            AndroidViewerScalePolicy.fitScale(
                2560,
                1280,
                3840,
                2160,
                true),
            0.0001f);
    }

    @Test
    public void surfaceBufferFollowsSourceRatherThanFittedWindow() {
        for (int[] source : new int[][] {{1280, 720}, {1920, 1080}, {2560, 1440},
                                        {3840, 2160}, {734, 1600}, {3440, 1440}}) {
            assertTrue(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(0, 0, source[0], source[1]));
            for (int[] window : new int[][] {{1080, 1800}, {1920, 720}, {800, 300}, {3840, 2160}}) {
                float fit = AndroidViewerScalePolicy.fitScale(window[0], window[1], source[0], source[1], false);
                assertTrue(fit > 0 && fit <= 1);
                // Neither viewport changes nor a 1:1 zoom replace this buffer.
                assertFalse(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(
                    source[0], source[1], source[0], source[1]));
            }
        }
    }

    @Test
    public void surfaceBufferChangesWhenRemoteRotatesOrChangesResolution() {
        assertTrue(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(1920, 1080, 1080, 1920));
        assertTrue(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(1920, 1080, 3840, 2160));
        assertFalse(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(3840, 2160, 3840, 2160));
    }

    @Test
    public void invalidAndOversizedSourcesCannotAllocateSurfaceBuffers() {
        for (int[] source : new int[][] {{0, 1080}, {1920, -1}, {8192, 8192},
                                        {32769, 1}, {1, 32769}, {Integer.MAX_VALUE, Integer.MAX_VALUE}}) {
            assertFalse(AndroidViewerScalePolicy.shouldResizeSurfaceBuffer(1920, 1080, source[0], source[1]));
        }
    }
}
