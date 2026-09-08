package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

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
}
