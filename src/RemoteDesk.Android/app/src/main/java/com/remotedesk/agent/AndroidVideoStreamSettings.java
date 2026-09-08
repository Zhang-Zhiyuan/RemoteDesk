package com.remotedesk.agent;

final class AndroidVideoStreamSettings {
    static final int JPEG_TARGET_FPS = 18;
    static final int H264_COMPATIBILITY_FPS = 30;
    static final int H264_TARGET_FPS = 60;
    static final int H264_OUTPUT_POLL_MILLIS = 10;
    static final int H264_MIN_BITRATE = 1_000_000;
    static final int H264_MAX_BITRATE = 16_000_000;
    static final int FRAME_SEND_BUFFER_BYTES = 128 * 1024;
    static final int VIEWER_RECEIVE_BUFFER_BYTES = 128 * 1024;
    static final int VIEWER_INPUT_QUEUE_LIMIT = 64;

    static int resolveH264TargetFps(
        int viewerCapabilities,
        boolean localHighFrameRateEncoderAvailable) {
        return localHighFrameRateEncoderAvailable &&
            (viewerCapabilities &
                RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0
            ? H264_TARGET_FPS
            : H264_COMPATIBILITY_FPS;
    }

    private AndroidVideoStreamSettings() {
    }
}
