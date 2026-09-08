package com.remotedesk.agent;

final class AndroidViewerScalePolicy {
    private AndroidViewerScalePolicy() {
    }

    static float fitScale(
        int viewWidth,
        int viewHeight,
        int frameWidth,
        int frameHeight,
        boolean allowUpscaling) {
        if (viewWidth <= 0 ||
            viewHeight <= 0 ||
            frameWidth <= 0 ||
            frameHeight <= 0) {
            return 0.0f;
        }

        float scale = Math.min(
            (float) viewWidth / frameWidth,
            (float) viewHeight / frameHeight);
        return allowUpscaling ? scale : Math.min(1.0f, scale);
    }

    static int scaledWidth(int frameWidth, float scale) {
        return Math.max(1, Math.round(frameWidth * scale));
    }

    static int scaledHeight(int frameHeight, float scale) {
        return Math.max(1, Math.round(frameHeight * scale));
    }
}
