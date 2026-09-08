package com.remotedesk.agent;

/** Keeps the active vendor encoder alignment stable across display polling. */
final class AndroidCaptureGeometryPolicy {
    private int widthAlignment = 2;
    private int heightAlignment = 2;

    void reset() {
        widthAlignment = 2;
        heightAlignment = 2;
    }

    void configureEncoderAlignment(
        int requestedWidthAlignment,
        int requestedHeightAlignment) {
        widthAlignment = normalizeAlignment(requestedWidthAlignment);
        heightAlignment = normalizeAlignment(requestedHeightAlignment);
    }

    int[] fitWithinMaxEdge(
        int sourceWidth,
        int sourceHeight,
        int maximumEdge) {
        return AndroidScreenCaptureSession.fitWithinMaxEdgeAligned(
            sourceWidth,
            sourceHeight,
            maximumEdge,
            widthAlignment,
            heightAlignment);
    }

    int getWidthAlignment() {
        return widthAlignment;
    }

    int getHeightAlignment() {
        return heightAlignment;
    }

    private static int normalizeAlignment(int value) {
        return Math.min(256, Math.max(1, value));
    }
}
