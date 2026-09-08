package com.remotedesk.agent;

import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

final class AndroidVideoCodecDiagnostics {
    private static final String H264_MIME_TYPE = "video/avc";
    private static volatile CodecReport cachedH264Report;

    private AndroidVideoCodecDiagnostics() {
    }

    static CodecReport cachedH264Report() {
        CodecReport report = cachedH264Report;
        if (report != null) {
            return report;
        }

        synchronized (AndroidVideoCodecDiagnostics.class) {
            report = cachedH264Report;
            if (report == null) {
                try {
                    report = inspectH264Encoder();
                } catch (RuntimeException ex) {
                    report = CodecReport.detectionFailed(ex);
                }

                cachedH264Report = report;
            }
        }

        return report;
    }

    static String formatH264Status(CodecReport report) {
        if (report.detectionError != null) {
            return "检测失败，将使用 JPEG";
        }

        if (!report.available) {
            if (report.codecName != null && !report.surfaceInputSupported) {
                return "编码器不支持屏幕输入，将使用 JPEG";
            }

            return "未发现编码器，将使用 JPEG";
        }

        if (report.softwareOnly) {
            return "兼容回退：软件编码（未发现可用硬件编码器），" +
                bitrateModeLabel(report);
        }

        if (!report.hardwareAccelerated) {
            return "兼容回退：编码器加速类型未知，" +
                bitrateModeLabel(report);
        }

        return "可用：硬件编码，" + bitrateModeLabel(report);
    }

    static String formatH264LogLine(CodecReport report) {
        if (report.detectionError != null) {
            return "H.264 encoder detection failed: " + report.detectionError;
        }

        if (!report.available) {
            return report.codecName == null
                ? "H.264 encoder unavailable."
                : "H.264 encoder lacks Surface input: " + report.codecName;
        }

        return "H.264 encoder available: " + report.codecName +
            ", hardware=" + report.hardwareAccelerated +
            ", software=" + report.softwareOnly +
            ", selection=" + accelerationSelectionLabel(
                report.hardwareAccelerated,
                report.softwareOnly) +
            ", cbr=" + report.cbrSupported +
            ", vbr=" + report.vbrSupported +
            ", highFrameRate=" + supportsHighFrameRateH264(report);
    }

    static void clearCacheForTests() {
        cachedH264Report = null;
    }

    static void recordSelectedH264Encoder(
        EncoderCandidate candidate) {
        cachedH264Report = candidate.toCodecReport();
    }

    static List<EncoderCandidate> h264EncoderCandidates() {
        return inspectH264Encoders().surfaceCandidates;
    }

    static boolean supportsHighFrameRateH264(CodecReport report) {
        return report != null &&
            report.available &&
            report.surfaceInputSupported &&
            report.hardwareAccelerated &&
            !report.softwareOnly;
    }

    private static CodecReport inspectH264Encoder() {
        EncoderInspection inspection = inspectH264Encoders();
        if (!inspection.surfaceCandidates.isEmpty()) {
            return inspection.surfaceCandidates.get(0).toCodecReport();
        }

        return inspection.firstH264Encoder == null
            ? CodecReport.unavailable()
            : inspection.firstH264Encoder;
    }

    private static EncoderInspection inspectH264Encoders() {
        MediaCodecList codecList = new MediaCodecList(MediaCodecList.ALL_CODECS);
        CodecReport firstH264Encoder = null;
        List<EncoderCandidate> surfaceCandidates = new ArrayList<>();
        List<EncoderEntryInspection> inspectedEntries = inspectCodecEntries(
            codecList.getCodecInfos(),
            AndroidVideoCodecDiagnostics::inspectH264EncoderEntry);
        for (EncoderEntryInspection entry : inspectedEntries) {
            CodecReport report = entry.report;
            if (firstH264Encoder == null) {
                firstH264Encoder = report;
            }

            if (entry.surfaceCandidate != null) {
                surfaceCandidates.add(entry.surfaceCandidate);
            }
        }

        return new EncoderInspection(
            orderEncoderCandidates(surfaceCandidates),
            firstH264Encoder);
    }

    private static EncoderEntryInspection inspectH264EncoderEntry(
        MediaCodecInfo codecInfo) {
        // Treat every vendor MediaCodecInfo as untrusted. Some devices expose
        // entries whose basic metadata methods throw; one malformed entry must
        // not hide the usable hardware encoder that follows it.
        if (!codecInfo.isEncoder() || !supportsType(codecInfo, H264_MIME_TYPE)) {
            return null;
        }

        MediaCodecInfo.CodecCapabilities capabilities =
            codecInfo.getCapabilitiesForType(H264_MIME_TYPE);
        String codecName = codecInfo.getName();
        boolean surfaceInputSupported = supportsSurfaceInput(capabilities);
        boolean hardwareAccelerated = isHardwareAccelerated(codecInfo);
        boolean softwareOnly = isSoftwareOnly(codecInfo);
        boolean cbrSupported = supportsBitrateMode(
            capabilities,
            MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
        boolean vbrSupported = supportsBitrateMode(
            capabilities,
            MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR);
        MediaCodecInfo.VideoCapabilities videoCapabilities = null;
        try {
            videoCapabilities = capabilities.getVideoCapabilities();
        } catch (RuntimeException ignored) {
        }
        CodecReport report = new CodecReport(
            surfaceInputSupported,
            codecName,
            surfaceInputSupported,
            hardwareAccelerated,
            softwareOnly,
            cbrSupported,
            vbrSupported,
            null);
        EncoderCandidate candidate = surfaceInputSupported
            ? new EncoderCandidate(
                codecName,
                hardwareAccelerated,
                softwareOnly,
                cbrSupported,
                vbrSupported,
                videoCapabilities)
            : null;
        return new EncoderEntryInspection(report, candidate);
    }

    /** Applies a probe independently to each vendor codec entry. */
    static <T, R> List<R> inspectCodecEntries(
        T[] entries,
        CodecEntryInspector<T, R> inspector) {
        List<R> results = new ArrayList<>();
        if (entries == null) {
            return results;
        }

        for (T entry : entries) {
            try {
                R result = inspector.inspect(entry);
                if (result != null) {
                    results.add(result);
                }
            } catch (RuntimeException ex) {
                AndroidSessionLog.error(
                    "Skipping one malformed Android codec entry; continuing enumeration.",
                    ex);
            }
        }
        return results;
    }

    private static boolean supportsType(MediaCodecInfo codecInfo, String mimeType) {
        for (String type : codecInfo.getSupportedTypes()) {
            if (mimeType.equals(type.toLowerCase(Locale.ROOT))) {
                return true;
            }
        }

        return false;
    }

    private static boolean supportsSurfaceInput(MediaCodecInfo.CodecCapabilities capabilities) {
        for (int colorFormat : capabilities.colorFormats) {
            if (colorFormat == MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface) {
                return true;
            }
        }

        return false;
    }

    private static boolean supportsBitrateMode(
        MediaCodecInfo.CodecCapabilities capabilities,
        int bitrateMode) {
        try {
            return capabilities.getEncoderCapabilities().isBitrateModeSupported(bitrateMode);
        } catch (RuntimeException ex) {
            return false;
        }
    }

    private static boolean isHardwareAccelerated(MediaCodecInfo codecInfo) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codecInfo.isHardwareAccelerated();
        }

        return looksLikeHardwareCodec(codecInfo.getName());
    }

    private static boolean isSoftwareOnly(MediaCodecInfo codecInfo) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codecInfo.isSoftwareOnly();
        }

        return looksLikeSoftwareCodec(codecInfo.getName());
    }

    static List<EncoderCandidate> orderEncoderCandidatesForTests(
        List<EncoderCandidate> candidates) {
        return orderEncoderCandidates(candidates);
    }

    static boolean looksLikeSoftwareCodec(String codecName) {
        String normalized = codecName == null
            ? ""
            : codecName.toLowerCase(Locale.ROOT);
        return normalized.startsWith("omx.google.") ||
            normalized.startsWith("omx.ffmpeg.") ||
            normalized.startsWith("c2.android.") ||
            normalized.startsWith("c2.google.") ||
            normalized.contains(".software.") ||
            normalized.contains(".sw.") ||
            normalized.endsWith(".sw");
    }

    static boolean looksLikeHardwareCodec(String codecName) {
        String normalized = codecName == null
            ? ""
            : codecName.toLowerCase(Locale.ROOT);
        return normalized.startsWith("omx.qcom.") ||
            normalized.startsWith("omx.qti.") ||
            normalized.startsWith("omx.exynos.") ||
            normalized.startsWith("omx.sec.") ||
            normalized.startsWith("omx.mtk.") ||
            normalized.startsWith("omx.hisi.") ||
            normalized.startsWith("omx.intel.") ||
            normalized.startsWith("omx.nvidia.") ||
            normalized.startsWith("omx.amlogic.") ||
            normalized.startsWith("omx.rk.") ||
            normalized.startsWith("c2.qti.") ||
            normalized.startsWith("c2.exynos.") ||
            normalized.startsWith("c2.mtk.") ||
            normalized.startsWith("c2.hisi.") ||
            normalized.startsWith("c2.intel.") ||
            normalized.startsWith("c2.nvidia.") ||
            normalized.startsWith("c2.amlogic.") ||
            normalized.startsWith("c2.rk.");
    }

    static String accelerationSelectionLabel(
        boolean hardwareAccelerated,
        boolean softwareOnly) {
        if (softwareOnly) {
            return "software-fallback";
        }

        if (hardwareAccelerated) {
            return "hardware";
        }

        return "unknown-compatibility";
    }

    private static List<EncoderCandidate> orderEncoderCandidates(
        List<EncoderCandidate> candidates) {
        List<EncoderCandidate> ordered =
            new ArrayList<>(candidates);
        Collections.sort(
            ordered,
            Comparator.comparingInt(
                AndroidVideoCodecDiagnostics::accelerationRank));
        return Collections.unmodifiableList(ordered);
    }

    private static int accelerationRank(EncoderCandidate candidate) {
        if (candidate.softwareOnly) {
            return 2;
        }

        return candidate.hardwareAccelerated ? 0 : 1;
    }

    private static String bitrateModeLabel(CodecReport report) {
        if (report.cbrSupported && report.vbrSupported) {
            return "CBR/VBR";
        }

        if (report.cbrSupported) {
            return "CBR";
        }

        if (report.vbrSupported) {
            return "VBR";
        }

        return "默认码率";
    }

    static final class EncoderCandidate {
        final String codecName;
        final boolean hardwareAccelerated;
        final boolean softwareOnly;
        final boolean cbrSupported;
        final boolean vbrSupported;
        final int widthAlignment;
        final int heightAlignment;
        private final MediaCodecInfo.VideoCapabilities videoCapabilities;

        EncoderCandidate(
            String codecName,
            boolean hardwareAccelerated,
            boolean softwareOnly,
            boolean cbrSupported,
            boolean vbrSupported) {
            this(
                codecName,
                hardwareAccelerated,
                softwareOnly,
                cbrSupported,
                vbrSupported,
                null);
        }

        EncoderCandidate(
            String codecName,
            boolean hardwareAccelerated,
            boolean softwareOnly,
            boolean cbrSupported,
            boolean vbrSupported,
            MediaCodecInfo.VideoCapabilities videoCapabilities) {
            this.codecName = codecName;
            this.hardwareAccelerated = hardwareAccelerated;
            this.softwareOnly = softwareOnly;
            this.cbrSupported = cbrSupported;
            this.vbrSupported = vbrSupported;
            this.videoCapabilities = videoCapabilities;
            this.widthAlignment = readAlignment(videoCapabilities, true);
            this.heightAlignment = readAlignment(videoCapabilities, false);
        }

        boolean supportsSizeAndRate(int width, int height, double fps) {
            if (width <= 0 || height <= 0 || fps <= 0d ||
                width % widthAlignment != 0 ||
                height % heightAlignment != 0) {
                return false;
            }

            if (videoCapabilities == null) {
                return true;
            }

            try {
                return videoCapabilities.areSizeAndRateSupported(
                    width,
                    height,
                    fps);
            } catch (RuntimeException ignored) {
                // Vendor capability metadata is frequently incomplete. The
                // real configure/start/first-AU probe remains authoritative.
                return true;
            }
        }

        String selectionLabel() {
            return accelerationSelectionLabel(
                hardwareAccelerated,
                softwareOnly);
        }

        CodecReport toCodecReport() {
            return new CodecReport(
                true,
                codecName,
                true,
                hardwareAccelerated,
                softwareOnly,
                cbrSupported,
                vbrSupported,
                null);
        }

        private static int readAlignment(
            MediaCodecInfo.VideoCapabilities capabilities,
            boolean width) {
            if (capabilities == null) {
                return 2;
            }

            try {
                int alignment = width
                    ? capabilities.getWidthAlignment()
                    : capabilities.getHeightAlignment();
                return alignment > 0 && alignment <= 256
                    ? Math.max(2, alignment)
                    : 2;
            } catch (RuntimeException ignored) {
                return 2;
            }
        }
    }

    private static final class EncoderInspection {
        final List<EncoderCandidate> surfaceCandidates;
        final CodecReport firstH264Encoder;

        EncoderInspection(
            List<EncoderCandidate> surfaceCandidates,
            CodecReport firstH264Encoder) {
            this.surfaceCandidates = surfaceCandidates;
            this.firstH264Encoder = firstH264Encoder;
        }
    }

    private static final class EncoderEntryInspection {
        final CodecReport report;
        final EncoderCandidate surfaceCandidate;

        EncoderEntryInspection(
            CodecReport report,
            EncoderCandidate surfaceCandidate) {
            this.report = report;
            this.surfaceCandidate = surfaceCandidate;
        }
    }

    interface CodecEntryInspector<T, R> {
        R inspect(T entry);
    }

    static final class CodecReport {
        final boolean available;
        final String codecName;
        final boolean surfaceInputSupported;
        final boolean hardwareAccelerated;
        final boolean softwareOnly;
        final boolean cbrSupported;
        final boolean vbrSupported;
        final String detectionError;

        CodecReport(
            boolean available,
            String codecName,
            boolean surfaceInputSupported,
            boolean hardwareAccelerated,
            boolean softwareOnly,
            boolean cbrSupported,
            boolean vbrSupported,
            String detectionError) {
            this.available = available;
            this.codecName = codecName;
            this.surfaceInputSupported = surfaceInputSupported;
            this.hardwareAccelerated = hardwareAccelerated;
            this.softwareOnly = softwareOnly;
            this.cbrSupported = cbrSupported;
            this.vbrSupported = vbrSupported;
            this.detectionError = detectionError;
        }

        static CodecReport unavailable() {
            return new CodecReport(false, null, false, false, false, false, false, null);
        }

        static CodecReport detectionFailed(Throwable throwable) {
            String message = throwable.getMessage();
            if (message == null || message.trim().isEmpty()) {
                message = throwable.getClass().getSimpleName();
            }

            return new CodecReport(false, null, false, false, false, false, false, message.trim());
        }
    }
}
