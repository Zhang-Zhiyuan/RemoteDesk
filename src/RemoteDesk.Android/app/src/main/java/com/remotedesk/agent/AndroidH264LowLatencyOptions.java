package com.remotedesk.agent;

final class AndroidH264LowLatencyOptions {
    static final float MIN_REALTIME_OPERATING_RATE = 120.0f;

    static final String KEY_LATENCY = "latency";
    static final String KEY_PRIORITY = "priority";
    static final String KEY_OPERATING_RATE = "operating-rate";
    static final String KEY_MAX_FPS_TO_ENCODER = "max-fps-to-encoder";
    static final String KEY_MAX_B_FRAMES = "max-bframes";
    static final String KEY_REPEAT_PREVIOUS_FRAME_AFTER =
        "repeat-previous-frame-after";
    static final long STATIC_FRAME_REPEAT_AFTER_MICROSECONDS = 500_000L;

    final int frameRate;
    final int latencyFrames;
    final int priority;
    final float operatingRate;
    final float maxFpsToEncoder;
    final int maxBFrames;
    final long repeatPreviousFrameAfterMicroseconds;

    private AndroidH264LowLatencyOptions(int frameRate) {
        this.frameRate = Math.max(1, frameRate);
        latencyFrames = 0;
        priority = 0;
        // Keep the hardware block out of low-throughput power states while
        // preserving the requested capture cadence separately. Unsupported
        // codecs are retried without this optional hint by the encoder.
        operatingRate = Math.max(MIN_REALTIME_OPERATING_RATE, this.frameRate);
        maxFpsToEncoder = this.frameRate;
        maxBFrames = 0;
        // Surface-input encoders are allowed to become completely quiet when
        // the display is motionless. A sparse repeat keeps the codec and the
        // encrypted stream observable without transmitting the static desktop
        // at the full capture cadence.
        repeatPreviousFrameAfterMicroseconds =
            STATIC_FRAME_REPEAT_AFTER_MICROSECONDS;
    }

    static AndroidH264LowLatencyOptions forFrameRate(int frameRate) {
        return new AndroidH264LowLatencyOptions(frameRate);
    }
}
