package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class AndroidVideoStreamSettingsTest {
    @Test
    public void h264UsesHigherFrameRateWithoutChangingJpegFallback() {
        assertEquals(18, AndroidVideoStreamSettings.JPEG_TARGET_FPS);
        assertEquals(30, AndroidVideoStreamSettings.H264_COMPATIBILITY_FPS);
        assertEquals(60, AndroidVideoStreamSettings.H264_TARGET_FPS);
        assertEquals(10, AndroidVideoStreamSettings.H264_OUTPUT_POLL_MILLIS);
    }

    @Test
    public void frameSocketBuffersUseBoundedLowLatencyDefaults() {
        assertEquals(128 * 1024, AndroidVideoStreamSettings.FRAME_SEND_BUFFER_BYTES);
        assertEquals(128 * 1024, AndroidVideoStreamSettings.VIEWER_RECEIVE_BUFFER_BYTES);
    }

    @Test
    public void viewerInputQueueIsBounded() {
        assertEquals(64, AndroidVideoStreamSettings.VIEWER_INPUT_QUEUE_LIMIT);
    }

    @Test
    public void sixtyFpsRequiresBothLocalHardwareAndViewerCapability() {
        int highFrameRate =
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;

        assertEquals(
            30,
            AndroidVideoStreamSettings.resolveH264TargetFps(0, true));
        assertEquals(
            30,
            AndroidVideoStreamSettings.resolveH264TargetFps(
                highFrameRate,
                false));
        assertEquals(
            60,
            AndroidVideoStreamSettings.resolveH264TargetFps(
                highFrameRate,
                true));
    }
}
