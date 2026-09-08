package com.remotedesk.agent;

final class AndroidH264CapabilityPolicy {
    static final long UDP_BITRATE_REDUCTION_INTERVAL_NANOS = 500_000_000L;

    private AndroidH264CapabilityPolicy() {
    }

    static int hostCapabilities(
        AndroidVideoCodecDiagnostics.CodecReport encoderReport) {
        if (encoderReport == null ||
            !encoderReport.available ||
            !encoderReport.surfaceInputSupported) {
            return 0;
        }

        // The Android encoder is configured as GOP1, which is a stricter form
        // of the negotiated short-GOP contract.
        int capabilities = RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264;
        if (AndroidVideoCodecDiagnostics.supportsHighFrameRateH264(
                encoderReport)) {
            capabilities |=
                RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;
        }
        return capabilities;
    }

    static int targetFramesPerSecond(
        int viewerCapabilities,
        AndroidVideoCodecDiagnostics.CodecReport encoderReport) {
        return AndroidVideoStreamSettings.resolveH264TargetFps(
            viewerCapabilities,
            AndroidVideoCodecDiagnostics.supportsHighFrameRateH264(
                encoderReport));
    }

    static int targetFramesPerSecondForActiveEncoder(
        int viewerCapabilities,
        boolean activeEncoderSupportsHighFrameRate) {
        return AndroidVideoStreamSettings.resolveH264TargetFps(
            viewerCapabilities,
            activeEncoderSupportsHighFrameRate);
    }

    static int clampNetworkTargetBitrate(long targetBitsPerSecond) {
        return (int) Math.max(
            AndroidVideoStreamSettings.H264_MIN_BITRATE,
            Math.min(
                AndroidVideoStreamSettings.H264_MAX_BITRATE,
                targetBitsPerSecond));
    }

    static int mergeRequestedBitrate(
        int currentBitrate,
        int adaptiveRequestedBitrate,
        int udpTargetBitrate) {
        int current = clampNetworkTargetBitrate(currentBitrate);
        int adaptive = adaptiveRequestedBitrate > 0
            ? clampNetworkTargetBitrate(adaptiveRequestedBitrate)
            : current;
        if (udpTargetBitrate <= 0) {
            return adaptive == current ? 0 : adaptive;
        }

        int networkCap = clampNetworkTargetBitrate(udpTargetBitrate);
        // Feedback is an immediate ceiling, never an independent ramp-up
        // controller. Recovery above the current rate remains gated by the
        // slower local comfort window, preventing alternating up/down IDRs.
        int merged = Math.min(adaptive, networkCap);
        return merged == current ? 0 : merged;
    }

    static int rateLimitedNetworkCeiling(
        int currentBitrate,
        int udpTargetBitrate,
        long nowNanos,
        long nextReductionAtNanos) {
        if (udpTargetBitrate <= 0) {
            return 0;
        }

        int current = clampNetworkTargetBitrate(currentBitrate);
        int target = clampNetworkTargetBitrate(udpTargetBitrate);
        return target < current && nowNanos < nextReductionAtNanos
            ? current
            : target;
    }

    static long recordNetworkReductionAttempt(
        int currentBitrate,
        int requestedBitrate,
        int rateLimitedNetworkCeiling,
        long nowNanos,
        long existingDeadlineNanos) {
        return requestedBitrate > 0 &&
            rateLimitedNetworkCeiling > 0 &&
            rateLimitedNetworkCeiling < currentBitrate
                ? nowNanos + UDP_BITRATE_REDUCTION_INTERVAL_NANOS
                : existingDeadlineNanos;
    }
}
