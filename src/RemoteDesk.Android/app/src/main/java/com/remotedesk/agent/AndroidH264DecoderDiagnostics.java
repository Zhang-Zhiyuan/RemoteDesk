package com.remotedesk.agent;

import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.os.Build;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

final class AndroidH264DecoderDiagnostics {
    private static final String H264_MIME_TYPE = "video/avc";

    private AndroidH264DecoderDiagnostics() {
    }

    static List<DecoderCandidate> h264DecoderCandidates() {
        MediaCodecList codecList = new MediaCodecList(MediaCodecList.ALL_CODECS);
        List<DecoderCandidate> candidates =
            AndroidVideoCodecDiagnostics.inspectCodecEntries(
                codecList.getCodecInfos(),
                AndroidH264DecoderDiagnostics::inspectH264DecoderEntry);

        return orderDecoderCandidates(candidates);
    }

    private static DecoderCandidate inspectH264DecoderEntry(
        MediaCodecInfo codecInfo) {
        // Basic metadata access is a vendor API boundary too. Keep a broken
        // entry local so later hardware decoders remain eligible.
        if (codecInfo.isEncoder() || !supportsType(codecInfo, H264_MIME_TYPE)) {
            return null;
        }

        codecInfo.getCapabilitiesForType(H264_MIME_TYPE);
        return new DecoderCandidate(
            codecInfo.getName(),
            isHardwareAccelerated(codecInfo),
            isSoftwareOnly(codecInfo));
    }

    static List<DecoderCandidate> orderDecoderCandidatesForTests(
        List<DecoderCandidate> candidates) {
        return orderDecoderCandidates(candidates);
    }

    static void recordSelectedDecoder(DecoderCandidate candidate) {
        AndroidSessionLog.info(
            "H.264 Surface decoder started: " +
            candidate.codecName +
            ", selection=" +
            candidate.selectionLabel() +
            ".");
    }

    private static boolean supportsType(MediaCodecInfo codecInfo, String mimeType) {
        for (String supportedType : codecInfo.getSupportedTypes()) {
            if (mimeType.equals(supportedType.toLowerCase(Locale.ROOT))) {
                return true;
            }
        }

        return false;
    }

    private static boolean isHardwareAccelerated(MediaCodecInfo codecInfo) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codecInfo.isHardwareAccelerated();
        }

        return AndroidVideoCodecDiagnostics.looksLikeHardwareCodec(codecInfo.getName());
    }

    private static boolean isSoftwareOnly(MediaCodecInfo codecInfo) {
        if (Build.VERSION.SDK_INT >= 29) {
            return codecInfo.isSoftwareOnly();
        }

        return AndroidVideoCodecDiagnostics.looksLikeSoftwareCodec(codecInfo.getName());
    }

    private static List<DecoderCandidate> orderDecoderCandidates(
        List<DecoderCandidate> candidates) {
        List<DecoderCandidate> ordered = new ArrayList<>(candidates);
        Collections.sort(
            ordered,
            Comparator.comparingInt(AndroidH264DecoderDiagnostics::accelerationRank));
        return Collections.unmodifiableList(ordered);
    }

    private static int accelerationRank(DecoderCandidate candidate) {
        if (candidate.softwareOnly) {
            return 2;
        }

        return candidate.hardwareAccelerated ? 0 : 1;
    }

    static final class DecoderCandidate {
        final String codecName;
        final boolean hardwareAccelerated;
        final boolean softwareOnly;

        DecoderCandidate(
            String codecName,
            boolean hardwareAccelerated,
            boolean softwareOnly) {
            this.codecName = codecName;
            this.hardwareAccelerated = hardwareAccelerated;
            this.softwareOnly = softwareOnly;
        }

        String selectionLabel() {
            return AndroidVideoCodecDiagnostics.accelerationSelectionLabel(
                hardwareAccelerated,
                softwareOnly);
        }
    }
}
