package com.remotedesk.agent;

import android.media.MediaCodec;

import java.util.List;

final class AndroidH264DecoderPolicy {
    // At 60 FPS this is a 500 ms cold-start/output budget. Six frames proved
    // too aggressive for vendor codecs that allocate Surface buffers lazily.
    static final int MAX_CONSECUTIVE_ACCESS_UNITS_WITHOUT_OUTPUT = 30;
    static final int MAX_CONSECUTIVE_INPUT_DEQUEUE_MISSES = 30;

    private AndroidH264DecoderPolicy() {
    }

    static int advertisedVideoCodecs(
        List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates) {
        int codecs = RemoteDeskProtocol.VIDEO_CODEC_JPEG;
        if (candidates != null && !candidates.isEmpty()) {
            codecs |= RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B;
        }

        return codecs;
    }

    static int videoCodecsForClarityMode(
        int automaticVideoCodecs,
        boolean preferJpegClarity) {
        return preferJpegClarity
            ? RemoteDeskProtocol.VIDEO_CODEC_JPEG
            : automaticVideoCodecs;
    }

    static int advertisedViewerCapabilities(
        List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates) {
        if (candidates == null || candidates.isEmpty()) {
            return 0;
        }

        int capabilities = RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264;
        for (AndroidH264DecoderDiagnostics.DecoderCandidate candidate : candidates) {
            if ((viewerCapabilitiesForSelectedCandidate(candidate) &
                    RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0) {
                capabilities |=
                    RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;
                break;
            }
        }

        return capabilities;
    }

    static int viewerCapabilitiesForSelectedCandidate(
        AndroidH264DecoderDiagnostics.DecoderCandidate candidate) {
        if (candidate == null) {
            return 0;
        }

        int capabilities = RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264;
        if (candidate.hardwareAccelerated && !candidate.softwareOnly) {
            capabilities |=
                RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;
        }
        return capabilities;
    }

    static boolean isRecoveryAccessUnit(int flags) {
        int recoveryFlags =
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
            RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG;
        return (flags & recoveryFlags) == recoveryFlags;
    }

    static boolean canSupersedeQueuedAccessUnits(int flags) {
        // SPS/PPS + IDR is independently decodable, so it can replace every
        // access unit that is still waiting in the user-space queue. A plain
        // key-frame flag without codec configuration is not sufficient for a
        // cold decoder or a decoder that has just changed generations.
        return isRecoveryAccessUnit(flags);
    }

    static boolean isDisplayableSurfaceOutput(int flags) {
        // Surface decoders do not expose pixel bytes to the app, and vendor
        // codecs are allowed to report a zero BufferInfo size for a valid
        // rendered frame. The flags, rather than byte count, are authoritative.
        int nonDisplayFlags =
            MediaCodec.BUFFER_FLAG_CODEC_CONFIG |
            MediaCodec.BUFFER_FLAG_END_OF_STREAM;
        return (flags & nonDisplayFlags) == 0;
    }

    static boolean requiresReconfigure(
        int configuredWidth,
        int configuredHeight,
        int frameWidth,
        int frameHeight) {
        return configuredWidth > 0 &&
            configuredHeight > 0 &&
            (configuredWidth != frameWidth || configuredHeight != frameHeight);
    }

    static boolean shouldProcessSurfaceUpdate(
        boolean sameSurface,
        boolean previousSurfaceUsable,
        boolean nextSurfaceUsable) {
        // A Surface object can be delivered before its native buffer queue is
        // valid and later become usable without changing Java identity. Do not
        // suppress that invalid -> valid transition as a duplicate callback.
        return !sameSurface ||
            previousSurfaceUsable != nextSurfaceUsable;
    }

    static SaturationAction saturationAction(int flags) {
        return isRecoveryAccessUnit(flags)
            ? SaturationAction.RESET_AND_ACCEPT_RECOVERY
            : SaturationAction.RESET_DROP_AND_REQUEST_RECOVERY;
    }

    static OutputProbeResult afterAccessUnit(
        int consecutiveAccessUnitsWithoutOutput,
        boolean renderedOutput) {
        if (renderedOutput) {
            return new OutputProbeResult(0, true, false);
        }

        int nextCount = Math.max(0, consecutiveAccessUnitsWithoutOutput) + 1;
        return new OutputProbeResult(
            nextCount,
            false,
            nextCount >= MAX_CONSECUTIVE_ACCESS_UNITS_WITHOUT_OUTPUT);
    }

    static boolean inputDequeueMissTimedOut(int consecutiveMisses) {
        return Math.max(0, consecutiveMisses) >=
            MAX_CONSECUTIVE_INPUT_DEQUEUE_MISSES;
    }

    enum SaturationAction {
        RESET_AND_ACCEPT_RECOVERY,
        RESET_DROP_AND_REQUEST_RECOVERY
    }

    static final class OutputProbeResult {
        final int consecutiveAccessUnitsWithoutOutput;
        final boolean outputConfirmed;
        final boolean candidateTimedOut;

        OutputProbeResult(
            int consecutiveAccessUnitsWithoutOutput,
            boolean outputConfirmed,
            boolean candidateTimedOut) {
            this.consecutiveAccessUnitsWithoutOutput =
                consecutiveAccessUnitsWithoutOutput;
            this.outputConfirmed = outputConfirmed;
            this.candidateTimedOut = candidateTimedOut;
        }
    }
}
