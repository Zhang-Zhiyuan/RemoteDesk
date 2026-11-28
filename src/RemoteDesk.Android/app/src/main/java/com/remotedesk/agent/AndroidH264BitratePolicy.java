package com.remotedesk.agent;

/** A quiet screen or a slow encoder is not evidence of a congested network. */
final class AndroidH264BitratePolicy {
    static boolean isNetworkBound(double actualFps, int targetFps,
            double socketWriteMillis, double bitsPerSecond, int currentBitrate) {
        if (!Double.isFinite(actualFps) || actualFps <= 0 || targetFps <= 0 ||
                !Double.isFinite(socketWriteMillis) || socketWriteMillis < 0 ||
                !Double.isFinite(bitsPerSecond) || bitsPerSecond <= 0 || currentBitrate <= 0)
            return false;
        double budget = 1000d / targetFps;
        return (actualFps < targetFps * 0.80 && socketWriteMillis > budget * 0.55) ||
            (bitsPerSecond > currentBitrate * 1.18 && socketWriteMillis > budget * 0.55);
    }

    private AndroidH264BitratePolicy() { }

    /** Receiver-confirmed UDP delivery. The capture thread asks for changes;
     * feedback arrives on the transport thread. Never accumulate sending credit. */
    static final class UdpHeadroom {
        private static final long FRESH_NANOS = 1_000_000_000L;
        private static final long GROWTH_INTERVAL = 4 * FRESH_NANOS;
        private long lastReport, cleanSince;
        private long nextGrowth, nextRevert;
        private boolean healthy;
        private double deliveredBps;

        synchronized void observe(LowLatencyVideoProtocol.FeedbackV2 previous,
                LowLatencyVideoProtocol.FeedbackV2 feedback, long now) {
            int required = LowLatencyVideoProtocol.FEEDBACK_METRIC_PACKET_DELIVERY |
                    LowLatencyVideoProtocol.FEEDBACK_METRIC_FRAME_ASSEMBLY;
            long micros = feedback.receiverElapsedMicroseconds - previous.receiverElapsedMicroseconds;
            long bytes = feedback.totalAuthenticatedWireBytes - previous.totalAuthenticatedWireBytes;
            boolean usable = (feedback.validMetrics & previous.validMetrics & required) == required &&
                    micros > 0 && micros <= 1_000_000 && bytes > 0 &&
                    feedback.totalAuthenticatedPackets > previous.totalAuthenticatedPackets &&
                    feedback.totalCompletedFrames > previous.totalCompletedFrames &&
                    feedback.totalSettledLostPackets == previous.totalSettledLostPackets &&
                    feedback.totalAbandonedIncompleteFrames == previous.totalAbandonedIncompleteFrames &&
                    feedback.totalConsumerDroppedFrames == previous.totalConsumerDroppedFrames &&
                    feedback.consumerQueueDepth <= 1;
            if (usable) {
                double measured = bytes * 8_000_000d / micros;
                if (!healthy || now - lastReport > FRESH_NANOS || now < lastReport) {
                    cleanSince = now;
                    deliveredBps = measured;
                } else {
                    deliveredBps = Math.min(measured, deliveredBps * 1.15);
                }
            }
            healthy = usable;
            lastReport = now;
        }

        synchronized int request(long now, int current, int initial, int networkCeiling) {
            if (!healthy || now < lastReport || now - lastReport > FRESH_NANOS ||
                    (current > initial && (deliveredBps < current * .65 || networkCeiling < current))) {
                if (current <= initial || now < nextRevert) return 0;
                nextRevert = now + FRESH_NANOS;
                nextGrowth = now + 30 * FRESH_NANOS;
                return initial;
            }
            if (now - cleanSince < GROWTH_INTERVAL || now < nextGrowth || deliveredBps < current * .80) return 0;
            int ceiling = Math.min(AndroidVideoStreamSettings.H264_MAX_BITRATE,
                    Math.min(networkCeiling, initial + initial / 4));
            if (ceiling <= current) return 0;
            nextGrowth = now + GROWTH_INTERVAL; // Also rate-limit encoders that reject setParameters.
            return Math.min(ceiling, current + current / 10);
        }
    }
}
