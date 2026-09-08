package com.remotedesk.agent;

import java.util.Locale;

final class AndroidViewerHealthFormatter {
    private AndroidViewerHealthFormatter() {
    }

    static String format(AndroidViewerHealthTracker.Snapshot snapshot) {
        if (snapshot == null) {
            return "画面 — · FPS — · — Mbps · 路由 — · 捕获 — · 编码 — · ACK — · 仅观看";
        }
        String codec = codecName(snapshot.codec);
        String resolution = snapshot.width > 0 && snapshot.height > 0
            ? snapshot.width + "×" + snapshot.height
            : "—";
        String route = snapshot.udpVideoActive
            ? "UDP视频" + (snapshot.udpMouseActive ? "/输入" : "")
            : snapshot.udpMouseActive ? "TCP视频/UDP输入" : "TCP";
        String inputMode = snapshot.inputControlAvailable ? "可控制" : "仅观看";
        return String.format(
            Locale.ROOT,
            "%s %s · FPS %s · %s Mbps · 路由 %s · 捕获 %s ms · 编码 %s ms · ACK %s · %s",
            codec,
            resolution,
            number(snapshot.presentedFramesPerSecond, 1),
            number(snapshot.megabitsPerSecond, 2),
            route,
            number(snapshot.averageCaptureMillis, 1),
            number(snapshot.averageEncodeMillis, 1),
            snapshot.mouseAckSampleCount > 0L
                ? formatAckMillis(snapshot.mouseAckEwmaMicros)
                : "—",
            inputMode);
    }

    private static String codecName(AndroidViewerHealthTracker.Codec codec) {
        if (codec == AndroidViewerHealthTracker.Codec.JPEG) {
            return "JPEG";
        }
        if (codec == AndroidViewerHealthTracker.Codec.H264_HARDWARE) {
            return "H.264硬解";
        }
        if (codec == AndroidViewerHealthTracker.Codec.H264_COMPATIBILITY) {
            return "H.264兼容";
        }
        return "画面";
    }

    private static String formatAckMillis(long micros) {
        return String.format(Locale.ROOT, "%.2f ms", micros / 1_000.0d);
    }

    private static String number(double value, int decimals) {
        if (!Double.isFinite(value)) {
            return "—";
        }
        return String.format(Locale.ROOT, "%." + decimals + "f", value);
    }
}
