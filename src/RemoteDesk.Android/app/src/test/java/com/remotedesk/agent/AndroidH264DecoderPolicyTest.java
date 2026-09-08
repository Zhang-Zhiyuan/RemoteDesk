package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import android.media.MediaCodec;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import org.junit.Test;

public final class AndroidH264DecoderPolicyTest {
    @Test
    public void decoderCandidatesPreferEveryHardwareCodecBeforeCompatibilityFallbacks() {
        AndroidH264DecoderDiagnostics.DecoderCandidate software =
            candidate("c2.android.avc.decoder", false, true);
        AndroidH264DecoderDiagnostics.DecoderCandidate unknown =
            candidate("vendor.compat.decoder", false, false);
        AndroidH264DecoderDiagnostics.DecoderCandidate hardwareOne =
            candidate("c2.qti.avc.decoder", true, false);
        AndroidH264DecoderDiagnostics.DecoderCandidate hardwareTwo =
            candidate("omx.exynos.avc.decoder", true, false);

        List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates =
            new ArrayList<>();
        candidates.add(software);
        candidates.add(hardwareOne);
        candidates.add(unknown);
        candidates.add(hardwareTwo);

        List<AndroidH264DecoderDiagnostics.DecoderCandidate> ordered =
            AndroidH264DecoderDiagnostics.orderDecoderCandidatesForTests(candidates);

        assertEquals(hardwareOne, ordered.get(0));
        assertEquals(hardwareTwo, ordered.get(1));
        assertEquals(unknown, ordered.get(2));
        assertEquals(software, ordered.get(3));
    }

    @Test
    public void viewerAdvertisesH264OnlyWhenAConcreteDecoderCandidateExists() {
        assertEquals(
            RemoteDeskProtocol.VIDEO_CODEC_JPEG,
            AndroidH264DecoderPolicy.advertisedVideoCodecs(Collections.emptyList()));

        assertEquals(
            RemoteDeskProtocol.VIDEO_CODEC_JPEG |
                RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B,
            AndroidH264DecoderPolicy.advertisedVideoCodecs(
                List.of(candidate("hardware.decoder", true, false))));
    }

    @Test
    public void clarityModeCanExplicitlySelectJpegWithoutLosingAutomaticMode() {
        int automatic = RemoteDeskProtocol.VIDEO_CODEC_JPEG |
            RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B;

        assertEquals(
            RemoteDeskProtocol.VIDEO_CODEC_JPEG,
            AndroidH264DecoderPolicy.videoCodecsForClarityMode(
                automatic,
                true));
        assertEquals(
            automatic,
            AndroidH264DecoderPolicy.videoCodecsForClarityMode(
                automatic,
                false));
    }

    @Test
    public void viewerHighFrameRateCapabilityRequiresHardwareDecoder() {
        assertEquals(
            0,
            AndroidH264DecoderPolicy.advertisedViewerCapabilities(
                Collections.emptyList()));

        int softwareCapabilities =
            AndroidH264DecoderPolicy.advertisedViewerCapabilities(
                List.of(candidate("c2.android.avc.decoder", false, true)));
        assertTrue((softwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertFalse((softwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);

        int hardwareCapabilities =
            AndroidH264DecoderPolicy.advertisedViewerCapabilities(
                List.of(candidate("c2.qti.avc.decoder", true, false)));
        assertTrue((hardwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertTrue((hardwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);

        int selectedSoftwareCapabilities =
            AndroidH264DecoderPolicy.viewerCapabilitiesForSelectedCandidate(
                candidate("software.decoder", false, true));
        assertTrue((selectedSoftwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertFalse((selectedSoftwareCapabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);
    }

    @Test
    public void dependentFrameCannotColdStartDecoder() {
        assertFalse(AndroidH264DecoderPolicy.isRecoveryAccessUnit(0));
        assertFalse(AndroidH264DecoderPolicy.isRecoveryAccessUnit(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME));
        assertFalse(AndroidH264DecoderPolicy.isRecoveryAccessUnit(
            RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG));
        assertTrue(AndroidH264DecoderPolicy.isRecoveryAccessUnit(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
                RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG));
    }

    @Test
    public void onlyIndependentRecoveryCanSupersedeQueuedDecodeWork() {
        assertFalse(AndroidH264DecoderPolicy.canSupersedeQueuedAccessUnits(0));
        assertFalse(AndroidH264DecoderPolicy.canSupersedeQueuedAccessUnits(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME));
        assertTrue(AndroidH264DecoderPolicy.canSupersedeQueuedAccessUnits(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
                RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG));
    }

    @Test
    public void decoderQueueStaysBoundedAndOutputWaitFitsA60HzFrame() {
        assertEquals(3, AndroidH264SurfaceDecoder.ACCESS_UNIT_QUEUE_CAPACITY);
        assertEquals(8_000L, AndroidH264SurfaceDecoder.OUTPUT_DEQUEUE_BUDGET_US);
        assertTrue(AndroidH264SurfaceDecoder.OUTPUT_DEQUEUE_BUDGET_US < 16_667L);
    }

    @Test
    public void surfaceOutputUsesCodecFlagsRatherThanAnUnreliableByteCount() {
        assertTrue(AndroidH264DecoderPolicy.isDisplayableSurfaceOutput(0));
        assertFalse(AndroidH264DecoderPolicy.isDisplayableSurfaceOutput(
            MediaCodec.BUFFER_FLAG_CODEC_CONFIG));
        assertFalse(AndroidH264DecoderPolicy.isDisplayableSurfaceOutput(
            MediaCodec.BUFFER_FLAG_END_OF_STREAM));
    }

    @Test
    public void dimensionChangeRequiresARecoveryReconfigure() {
        assertFalse(AndroidH264DecoderPolicy.requiresReconfigure(
            0,
            0,
            1920,
            1080));
        assertFalse(AndroidH264DecoderPolicy.requiresReconfigure(
            1920,
            1080,
            1920,
            1080));
        assertTrue(AndroidH264DecoderPolicy.requiresReconfigure(
            1920,
            1080,
            1280,
            720));
    }

    @Test
    public void sameSurfaceInvalidToValidTransitionIsNotDropped() {
        assertFalse(AndroidH264DecoderPolicy.shouldProcessSurfaceUpdate(
            true,
            false,
            false));
        assertTrue(AndroidH264DecoderPolicy.shouldProcessSurfaceUpdate(
            true,
            false,
            true));
        assertTrue(AndroidH264DecoderPolicy.shouldProcessSurfaceUpdate(
            true,
            true,
            false));
        assertTrue(AndroidH264DecoderPolicy.shouldProcessSurfaceUpdate(
            false,
            true,
            true));
    }

    @Test
    public void saturationDropsDependentChainButCanKeepFreshRecoveryFrame() {
        assertEquals(
            AndroidH264DecoderPolicy.SaturationAction.RESET_DROP_AND_REQUEST_RECOVERY,
            AndroidH264DecoderPolicy.saturationAction(0));
        assertEquals(
            AndroidH264DecoderPolicy.SaturationAction.RESET_DROP_AND_REQUEST_RECOVERY,
            AndroidH264DecoderPolicy.saturationAction(
                RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME));
        assertEquals(
            AndroidH264DecoderPolicy.SaturationAction.RESET_AND_ACCEPT_RECOVERY,
            AndroidH264DecoderPolicy.saturationAction(
                RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
                    RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG));
    }

    @Test
    public void outputProbeConfirmsActualWinnerAndBoundsSilentCandidate() {
        assertEquals(
            30,
            AndroidH264DecoderPolicy.MAX_CONSECUTIVE_ACCESS_UNITS_WITHOUT_OUTPUT);
        int withoutOutput = 0;
        for (int index = 1;
             index < AndroidH264DecoderPolicy.MAX_CONSECUTIVE_ACCESS_UNITS_WITHOUT_OUTPUT;
             index++) {
            AndroidH264DecoderPolicy.OutputProbeResult result =
                AndroidH264DecoderPolicy.afterAccessUnit(withoutOutput, false);
            withoutOutput = result.consecutiveAccessUnitsWithoutOutput;
            assertFalse(result.outputConfirmed);
            assertFalse(result.candidateTimedOut);
        }

        AndroidH264DecoderPolicy.OutputProbeResult timedOut =
            AndroidH264DecoderPolicy.afterAccessUnit(withoutOutput, false);
        assertTrue(timedOut.candidateTimedOut);

        AndroidH264DecoderPolicy.OutputProbeResult confirmed =
            AndroidH264DecoderPolicy.afterAccessUnit(withoutOutput, true);
        assertTrue(confirmed.outputConfirmed);
        assertFalse(confirmed.candidateTimedOut);
        assertEquals(0, confirmed.consecutiveAccessUnitsWithoutOutput);
    }

    @Test
    public void transientInputSaturationDoesNotPermanentlyRejectDecoder() {
        assertFalse(AndroidH264DecoderPolicy.inputDequeueMissTimedOut(1));
        assertFalse(AndroidH264DecoderPolicy.inputDequeueMissTimedOut(29));
        assertTrue(AndroidH264DecoderPolicy.inputDequeueMissTimedOut(30));
    }

    private static AndroidH264DecoderDiagnostics.DecoderCandidate candidate(
        String name,
        boolean hardware,
        boolean software) {
        return new AndroidH264DecoderDiagnostics.DecoderCandidate(
            name,
            hardware,
            software);
    }
}
