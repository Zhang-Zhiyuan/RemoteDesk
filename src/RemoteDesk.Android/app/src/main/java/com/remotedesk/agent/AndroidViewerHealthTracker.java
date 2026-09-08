package com.remotedesk.agent;

/** Connection-local, constant-memory viewer health counters. */
final class AndroidViewerHealthTracker {
    enum Codec {
        UNKNOWN,
        JPEG,
        H264_HARDWARE,
        H264_COMPATIBILITY
    }

    private long receivedFrameCount;
    private long receivedEncodedBytes;
    private double captureTotalMillis;
    private long captureSampleCount;
    private double encodeTotalMillis;
    private long encodeSampleCount;
    private long presentedFrameCount;
    private boolean presentationTelemetryAvailable = true;
    private Codec codec = Codec.UNKNOWN;
    private int width;
    private int height;
    private long previousSnapshotAtNanos;
    private boolean snapshotInitialized;
    private long previousReceivedEncodedBytes;
    private long previousPresentedFrameCount;
    private long mouseAckSampleCount;
    private long mouseAckEwmaMicros;

    synchronized void recordReceivedFrame(
        int encodedBytes,
        double captureMillis,
        double encodeMillis) {
        receivedFrameCount++;
        receivedEncodedBytes += Math.max(0, encodedBytes);
        if (Double.isFinite(captureMillis) && captureMillis >= 0.0d) {
            captureTotalMillis += captureMillis;
            captureSampleCount++;
        }
        if (Double.isFinite(encodeMillis) && encodeMillis >= 0.0d) {
            encodeTotalMillis += encodeMillis;
            encodeSampleCount++;
        }
    }

    synchronized void recordPresentedFrame() {
        presentationTelemetryAvailable = true;
        presentedFrameCount++;
    }

    synchronized void markPresentationTelemetryUnavailable() {
        presentationTelemetryAvailable = false;
    }

    synchronized void setVideoFormat(Codec codec, int width, int height) {
        this.codec = codec == null ? Codec.UNKNOWN : codec;
        this.width = Math.max(0, width);
        this.height = Math.max(0, height);
    }

    synchronized void setResolution(int width, int height) {
        this.width = Math.max(0, width);
        this.height = Math.max(0, height);
    }

    synchronized void recordMouseAck(long ewmaMicros, long sampleCount) {
        if (sampleCount <= 0L) {
            return;
        }
        mouseAckSampleCount = sampleCount;
        mouseAckEwmaMicros = Math.max(0L, ewmaMicros);
    }

    synchronized void start(long nowNanos) {
        if (snapshotInitialized) {
            return;
        }
        snapshotInitialized = true;
        previousSnapshotAtNanos = nowNanos;
        previousReceivedEncodedBytes = receivedEncodedBytes;
        previousPresentedFrameCount = presentedFrameCount;
    }

    synchronized Snapshot snapshot(
        long nowNanos,
        boolean udpVideoActive,
        boolean udpMouseActive,
        boolean inputControlAvailable) {
        double presentedFramesPerSecond = Double.NaN;
        double megabitsPerSecond = Double.NaN;
        if (snapshotInitialized && nowNanos > previousSnapshotAtNanos) {
            double elapsedSeconds =
                (nowNanos - previousSnapshotAtNanos) / 1_000_000_000.0d;
            if (presentationTelemetryAvailable && presentedFrameCount > 0L) {
                presentedFramesPerSecond = Math.max(
                    0L,
                    presentedFrameCount - previousPresentedFrameCount) /
                    elapsedSeconds;
            }
            if (receivedFrameCount > 0L) {
                megabitsPerSecond = Math.max(
                    0L,
                    receivedEncodedBytes - previousReceivedEncodedBytes) * 8.0d /
                    elapsedSeconds /
                    1_000_000.0d;
            }
        }
        snapshotInitialized = true;
        previousSnapshotAtNanos = nowNanos;
        previousReceivedEncodedBytes = receivedEncodedBytes;
        previousPresentedFrameCount = presentedFrameCount;

        return new Snapshot(
            codec,
            width,
            height,
            presentedFramesPerSecond,
            megabitsPerSecond,
            captureSampleCount == 0L
                ? Double.NaN
                : captureTotalMillis / captureSampleCount,
            encodeSampleCount == 0L
                ? Double.NaN
                : encodeTotalMillis / encodeSampleCount,
            udpVideoActive,
            udpMouseActive,
            inputControlAvailable,
            mouseAckSampleCount,
            mouseAckEwmaMicros,
            receivedFrameCount,
            presentedFrameCount);
    }

    static final class Snapshot {
        final Codec codec;
        final int width;
        final int height;
        final double presentedFramesPerSecond;
        final double megabitsPerSecond;
        final double averageCaptureMillis;
        final double averageEncodeMillis;
        final boolean udpVideoActive;
        final boolean udpMouseActive;
        final boolean inputControlAvailable;
        final long mouseAckSampleCount;
        final long mouseAckEwmaMicros;
        final long receivedFrameCount;
        final long presentedFrameCount;

        Snapshot(
            Codec codec,
            int width,
            int height,
            double presentedFramesPerSecond,
            double megabitsPerSecond,
            double averageCaptureMillis,
            double averageEncodeMillis,
            boolean udpVideoActive,
            boolean udpMouseActive,
            boolean inputControlAvailable,
            long mouseAckSampleCount,
            long mouseAckEwmaMicros,
            long receivedFrameCount,
            long presentedFrameCount) {
            this.codec = codec;
            this.width = width;
            this.height = height;
            this.presentedFramesPerSecond = presentedFramesPerSecond;
            this.megabitsPerSecond = megabitsPerSecond;
            this.averageCaptureMillis = averageCaptureMillis;
            this.averageEncodeMillis = averageEncodeMillis;
            this.udpVideoActive = udpVideoActive;
            this.udpMouseActive = udpMouseActive;
            this.inputControlAvailable = inputControlAvailable;
            this.mouseAckSampleCount = mouseAckSampleCount;
            this.mouseAckEwmaMicros = mouseAckEwmaMicros;
            this.receivedFrameCount = receivedFrameCount;
            this.presentedFrameCount = presentedFrameCount;
        }
    }
}
