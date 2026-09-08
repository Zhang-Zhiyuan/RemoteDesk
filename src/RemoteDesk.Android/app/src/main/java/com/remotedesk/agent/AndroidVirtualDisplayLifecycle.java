package com.remotedesk.agent;

/**
 * Pure state model for the one-VirtualDisplay-per-MediaProjection contract.
 *
 * <p>Android 14 and newer reject a second createVirtualDisplay call on the
 * same MediaProjection. The platform-facing capture session uses this model
 * to make creation, surface switching, resizing, and projection teardown
 * explicit and independently testable.</p>
 */
final class AndroidVirtualDisplayLifecycle {
    enum Output {
        None,
        Jpeg,
        Video
    }

    private boolean projectionActive;
    private boolean displayCreated;
    private long nextProjectionGeneration;
    private long projectionGeneration;
    private int createCount;
    private int width;
    private int height;
    private int densityDpi;
    private int capturedContentWidth;
    private int capturedContentHeight;
    private Output output = Output.None;

    long beginProjection() {
        nextProjectionGeneration = nextProjectionGeneration == Long.MAX_VALUE
            ? 1L
            : nextProjectionGeneration + 1L;
        projectionGeneration = nextProjectionGeneration;
        projectionActive = true;
        displayCreated = false;
        createCount = 0;
        width = 0;
        height = 0;
        densityDpi = 0;
        capturedContentWidth = 0;
        capturedContentHeight = 0;
        output = Output.None;
        return projectionGeneration;
    }

    boolean isCurrentProjection(long generation) {
        return projectionActive &&
            generation != 0L &&
            generation == projectionGeneration;
    }

    long getProjectionGeneration() {
        return projectionActive ? projectionGeneration : 0L;
    }

    boolean shouldCreateVirtualDisplay() {
        return projectionActive && !displayCreated;
    }

    void recordVirtualDisplayCreated(
        int width,
        int height,
        int densityDpi,
        Output output) {
        if (!shouldCreateVirtualDisplay()) {
            throw new IllegalStateException(
                "A MediaProjection may create only one VirtualDisplay.");
        }

        displayCreated = true;
        createCount++;
        recordSize(width, height, densityDpi);
        this.output = normalize(output);
    }

    boolean needsResize(int width, int height, int densityDpi) {
        return displayCreated &&
            (this.width != width ||
                this.height != height ||
                this.densityDpi != densityDpi);
    }

    void recordResize(int width, int height, int densityDpi) {
        if (!displayCreated) {
            throw new IllegalStateException(
                "A VirtualDisplay must exist before it can be resized.");
        }

        recordSize(width, height, densityDpi);
    }

    void recordSurfaceAttached(Output output) {
        if (!displayCreated) {
            throw new IllegalStateException(
                "A VirtualDisplay must exist before its Surface can change.");
        }

        this.output = normalize(output);
    }

    boolean recordCapturedContentResize(
        long generation,
        int width,
        int height) {
        if (!isCurrentProjection(generation) || width <= 0 || height <= 0) {
            return false;
        }

        if (capturedContentWidth == width && capturedContentHeight == height) {
            return false;
        }

        capturedContentWidth = width;
        capturedContentHeight = height;
        return true;
    }

    boolean hasCapturedContentSize() {
        return projectionActive &&
            capturedContentWidth > 0 &&
            capturedContentHeight > 0;
    }

    int getCapturedContentWidth() {
        return hasCapturedContentSize() ? capturedContentWidth : 0;
    }

    int getCapturedContentHeight() {
        return hasCapturedContentSize() ? capturedContentHeight : 0;
    }

    void clearProjection() {
        projectionActive = false;
        displayCreated = false;
        projectionGeneration = 0L;
        width = 0;
        height = 0;
        densityDpi = 0;
        capturedContentWidth = 0;
        capturedContentHeight = 0;
        output = Output.None;
    }

    boolean isDisplayCreated() {
        return displayCreated;
    }

    int getCreateCount() {
        return createCount;
    }

    int getWidth() {
        return width;
    }

    int getHeight() {
        return height;
    }

    int getDensityDpi() {
        return densityDpi;
    }

    Output getOutput() {
        return output;
    }

    private void recordSize(int width, int height, int densityDpi) {
        if (width <= 0 || height <= 0 || densityDpi <= 0) {
            throw new IllegalArgumentException(
                "VirtualDisplay dimensions and density must be positive.");
        }

        this.width = width;
        this.height = height;
        this.densityDpi = densityDpi;
    }

    private static Output normalize(Output output) {
        return output == null ? Output.None : output;
    }
}
