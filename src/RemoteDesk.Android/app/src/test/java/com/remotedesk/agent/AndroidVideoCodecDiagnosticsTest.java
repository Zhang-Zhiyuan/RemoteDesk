package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.Arrays;
import java.util.List;

import org.junit.After;
import org.junit.Test;

public final class AndroidVideoCodecDiagnosticsTest {
    @After
    public void tearDown() {
        AndroidVideoCodecDiagnostics.clearCacheForTests();
    }

    @Test
    public void formatH264StatusReportsHardwareEncoderAndBitrateModes() {
        AndroidVideoCodecDiagnostics.CodecReport report =
            new AndroidVideoCodecDiagnostics.CodecReport(
                true,
                "codec.avc.encoder",
                true,
                true,
                false,
                true,
                true,
                null);

        String status = AndroidVideoCodecDiagnostics.formatH264Status(report);

        assertTrue(status.contains("可用"));
        assertTrue(status.contains("硬件"));
        assertTrue(status.contains("CBR/VBR"));
    }

    @Test
    public void formatH264StatusReportsJpegFallbackWhenUnavailable() {
        String status = AndroidVideoCodecDiagnostics.formatH264Status(
            AndroidVideoCodecDiagnostics.CodecReport.unavailable());

        assertTrue(status.contains("未发现"));
        assertTrue(status.contains("JPEG"));
    }

    @Test
    public void formatH264StatusReportsSurfaceInputMismatch() {
        AndroidVideoCodecDiagnostics.CodecReport report =
            new AndroidVideoCodecDiagnostics.CodecReport(
                false,
                "codec.avc.encoder",
                false,
                false,
                false,
                true,
                false,
                null);

        String status = AndroidVideoCodecDiagnostics.formatH264Status(report);

        assertTrue(status.contains("不支持屏幕输入"));
        assertTrue(status.contains("JPEG"));
    }

    @Test
    public void formatH264LogLineIncludesCodecDetails() {
        AndroidVideoCodecDiagnostics.CodecReport report =
            new AndroidVideoCodecDiagnostics.CodecReport(
                true,
                "codec.avc.encoder",
                true,
                false,
                true,
                false,
                true,
                null);

        String logLine = AndroidVideoCodecDiagnostics.formatH264LogLine(report);

        assertTrue(logLine.contains("codec.avc.encoder"));
        assertTrue(logLine.contains("software=true"));
        assertTrue(logLine.contains("vbr=true"));
        assertTrue(logLine.contains("highFrameRate=false"));
    }

    @Test
    public void formatH264StatusReportsDetectionFailure() {
        AndroidVideoCodecDiagnostics.CodecReport report =
            AndroidVideoCodecDiagnostics.CodecReport.detectionFailed(new IllegalStateException("broken"));

        assertTrue(AndroidVideoCodecDiagnostics.formatH264Status(report).contains("检测失败"));
        assertTrue(AndroidVideoCodecDiagnostics.formatH264LogLine(report).contains("broken"));
    }

    @Test
    public void hardwareCandidatesSortBeforeUnknownAndSoftwareFallbacks() {
        AndroidVideoCodecDiagnostics.EncoderCandidate software =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "c2.android.avc.encoder",
                false,
                true,
                false,
                true);
        AndroidVideoCodecDiagnostics.EncoderCandidate unknown =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "vendor.avc.encoder",
                false,
                false,
                true,
                false);
        AndroidVideoCodecDiagnostics.EncoderCandidate hardware =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "c2.qti.avc.encoder",
                true,
                false,
                true,
                true);

        List<AndroidVideoCodecDiagnostics.EncoderCandidate> ordered =
            AndroidVideoCodecDiagnostics.orderEncoderCandidatesForTests(
                Arrays.asList(software, unknown, hardware));

        assertEquals("c2.qti.avc.encoder", ordered.get(0).codecName);
        assertEquals("vendor.avc.encoder", ordered.get(1).codecName);
        assertEquals("c2.android.avc.encoder", ordered.get(2).codecName);
        assertEquals("hardware", ordered.get(0).selectionLabel());
        assertEquals(
            "unknown-compatibility",
            ordered.get(1).selectionLabel());
        assertEquals(
            "software-fallback",
            ordered.get(2).selectionLabel());
    }

    @Test
    public void legacyNameHeuristicsDoNotCallAospSoftwareHardware() {
        assertTrue(AndroidVideoCodecDiagnostics.looksLikeSoftwareCodec(
            "OMX.google.h264.encoder"));
        assertTrue(AndroidVideoCodecDiagnostics.looksLikeSoftwareCodec(
            "c2.android.avc.encoder"));
        assertFalse(AndroidVideoCodecDiagnostics.looksLikeHardwareCodec(
            "c2.android.avc.encoder"));
        assertTrue(AndroidVideoCodecDiagnostics.looksLikeHardwareCodec(
            "OMX.qcom.video.encoder.avc"));
        assertTrue(AndroidVideoCodecDiagnostics.looksLikeHardwareCodec(
            "c2.qti.avc.encoder"));
    }

    @Test
    public void softwareStatusAndLogExplicitlyDescribeCompatibilityFallback() {
        AndroidVideoCodecDiagnostics.CodecReport report =
            new AndroidVideoCodecDiagnostics.CodecReport(
                true,
                "c2.android.avc.encoder",
                true,
                false,
                true,
                false,
                true,
                null);

        String status =
            AndroidVideoCodecDiagnostics.formatH264Status(report);
        String logLine =
            AndroidVideoCodecDiagnostics.formatH264LogLine(report);

        assertTrue(status.contains("兼容回退"));
        assertTrue(status.contains("软件"));
        assertTrue(logLine.contains("selection=software-fallback"));
    }

    @Test
    public void selectedRuntimeCodecReplacesCapabilityOnlyUiReport() {
        AndroidVideoCodecDiagnostics.EncoderCandidate selected =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "c2.android.avc.encoder",
                false,
                true,
                false,
                true);

        AndroidVideoCodecDiagnostics.recordSelectedH264Encoder(
            selected);
        AndroidVideoCodecDiagnostics.CodecReport report =
            AndroidVideoCodecDiagnostics.cachedH264Report();

        assertEquals("c2.android.avc.encoder", report.codecName);
        assertTrue(report.softwareOnly);
        assertTrue(
            AndroidVideoCodecDiagnostics
                .formatH264Status(report)
                .contains("兼容回退"));
    }

    @Test
    public void candidatePreflightRejectsInvalidOrUnalignedGeometry() {
        AndroidVideoCodecDiagnostics.EncoderCandidate candidate =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "test.encoder",
                true,
                false,
                true,
                true);

        assertTrue(candidate.supportsSizeAndRate(738, 1600, 60));
        assertFalse(candidate.supportsSizeAndRate(739, 1600, 60));
        assertFalse(candidate.supportsSizeAndRate(738, 1599, 60));
        assertFalse(candidate.supportsSizeAndRate(738, 1600, 0));
    }

    @Test
    public void malformedVendorEntryDoesNotHideLaterCodecCandidates() {
        List<String> candidates = AndroidVideoCodecDiagnostics.inspectCodecEntries(
            new String[] { "broken.vendor.codec", "c2.qti.avc.encoder", "not-hevc" },
            codecName -> {
                if (codecName.startsWith("broken")) {
                    throw new IllegalStateException("vendor metadata failure");
                }
                return codecName.contains("avc") ? codecName : null;
            });

        assertEquals(1, candidates.size());
        assertEquals("c2.qti.avc.encoder", candidates.get(0));
    }
}
