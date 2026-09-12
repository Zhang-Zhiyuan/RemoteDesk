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
}
