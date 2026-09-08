package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class AndroidH264LowLatencyOptionsTest {
    @Test
    public void lowLatencyOptionsRequestRealtimeNoReorderingEncoding() {
        AndroidH264LowLatencyOptions options =
            AndroidH264LowLatencyOptions.forFrameRate(30);

        assertEquals(30, options.frameRate);
        assertEquals(0, options.latencyFrames);
        assertEquals(0, options.priority);
        assertEquals(120.0f, options.operatingRate, 0.001f);
        assertEquals(30.0f, options.maxFpsToEncoder, 0.001f);
        assertEquals(0, options.maxBFrames);
        assertEquals(
            500_000L,
            options.repeatPreviousFrameAfterMicroseconds);
    }

    @Test
    public void lowLatencyOptionsClampInvalidFrameRate() {
        AndroidH264LowLatencyOptions options =
            AndroidH264LowLatencyOptions.forFrameRate(0);

        assertEquals(1, options.frameRate);
        assertEquals(120.0f, options.operatingRate, 0.001f);
        assertEquals(1.0f, options.maxFpsToEncoder, 0.001f);
    }

    @Test
    public void operatingRateKeepsHigherRequestedCadence() {
        AndroidH264LowLatencyOptions options =
            AndroidH264LowLatencyOptions.forFrameRate(240);

        assertEquals(240.0f, options.operatingRate, 0.001f);
        assertEquals(240.0f, options.maxFpsToEncoder, 0.001f);
    }

    @Test
    public void lowLatencyOptionKeysMatchAndroidMediaFormatContract() {
        assertEquals("latency", AndroidH264LowLatencyOptions.KEY_LATENCY);
        assertEquals("priority", AndroidH264LowLatencyOptions.KEY_PRIORITY);
        assertEquals("operating-rate", AndroidH264LowLatencyOptions.KEY_OPERATING_RATE);
        assertEquals("max-fps-to-encoder", AndroidH264LowLatencyOptions.KEY_MAX_FPS_TO_ENCODER);
        assertEquals("max-bframes", AndroidH264LowLatencyOptions.KEY_MAX_B_FRAMES);
        assertEquals(
            "repeat-previous-frame-after",
            AndroidH264LowLatencyOptions.KEY_REPEAT_PREVIOUS_FRAME_AFTER);
    }
}
