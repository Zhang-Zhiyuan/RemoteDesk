package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import android.media.MediaCodecInfo;
import android.media.MediaCodec;

import org.junit.Test;

public final class AndroidH264ScreenEncoderTest {
    @Test
    public void encoderUsesIndependentlyRecoverableGopOneBaseline() {
        assertEquals(0, AndroidH264ScreenEncoder.I_FRAME_INTERVAL_SECONDS);
        assertTrue(
            AndroidH264ScreenEncoder.FIRST_RECOVERY_FRAME_TIMEOUT_MILLIS >=
                1_500L);
    }

    @Test
    public void inlineRecoveryAccessUnitAcceptsMixedThreeAndFourByteStartCodes() {
        byte[] accessUnit = new byte[] {
            0, 0, 0, 1, 0x67, 0x01,
            0, 0, 1, 0x68, 0x02,
            0, 0, 0, 1, 0x65, 0x03
        };

        assertTrue(AndroidH264ScreenEncoder.isAnnexBRecoveryAccessUnit(accessUnit));
        assertTrue(AndroidH264ScreenEncoder.containsAnnexBNalType(accessUnit, 7));
        assertTrue(AndroidH264ScreenEncoder.containsAnnexBNalType(accessUnit, 8));
        assertTrue(AndroidH264ScreenEncoder.containsAnnexBNalType(accessUnit, 5));
    }

    @Test
    public void recoveryDetectionRejectsPartialOrStartCodeLikePayloads() {
        assertFalse(AndroidH264ScreenEncoder.isAnnexBRecoveryAccessUnit(
            new byte[] {
                0, 0, 1, 0x67,
                0, 0, 1, 0x65
            }));
        assertFalse(AndroidH264ScreenEncoder.isAnnexBRecoveryAccessUnit(
            new byte[] {
                0, 0, 2, 0x67,
                0, 0, 1, 0x68,
                0, 0, 1, 0x65
            }));
        assertFalse(AndroidH264ScreenEncoder.containsAnnexBNalType(null, 5));
        assertFalse(AndroidH264ScreenEncoder.containsAnnexBNalType(
            new byte[] { 0, 0, 1 },
            5));
    }

    @Test
    public void inlineOrPrependedCodecConfigurationProducesRecoveryFlags() {
        byte[] inlineRecovery = new byte[] {
            0, 0, 1, 0x67,
            0, 0, 1, 0x68,
            0, 0, 1, 0x65
        };
        int inlineFlags = AndroidH264ScreenEncoder.recoveryFlagsForAnnexB(
            inlineRecovery,
            0,
            false);
        assertEquals(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
                RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG,
            inlineFlags);

        int prependedFlags = AndroidH264ScreenEncoder.recoveryFlagsForAnnexB(
            new byte[] { 0, 0, 1, 0x65 },
            MediaCodec.BUFFER_FLAG_KEY_FRAME,
            true);
        assertEquals(
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME |
                RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG,
            prependedFlags);

        assertEquals(
            0,
            AndroidH264ScreenEncoder.recoveryFlagsForAnnexB(
                new byte[] { 0, 0, 1, 0x41 },
                0,
                false));
    }

    @Test
    public void presentationTimestampRegressionDetectsReorderedEncoderOutput() {
        assertFalse(AndroidH264ScreenEncoder.hasPresentationOrderRegression(
            Long.MIN_VALUE,
            1000));
        assertFalse(AndroidH264ScreenEncoder.hasPresentationOrderRegression(
            1000,
            1000));
        assertFalse(AndroidH264ScreenEncoder.hasPresentationOrderRegression(
            1000,
            2000));
        assertTrue(AndroidH264ScreenEncoder.hasPresentationOrderRegression(
            2000,
            1000));
    }

    @Test
    public void bitrateAttemptsUseOnlyAdvertisedModesBeforeCodecDefault() {
        AndroidVideoCodecDiagnostics.EncoderCandidate cbrOnly =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "hardware.encoder",
                true,
                false,
                true,
                false);
        AndroidVideoCodecDiagnostics.EncoderCandidate noModes =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "unknown.encoder",
                false,
                false,
                false,
                false);

        assertArrayEquals(
            new int[] {
                MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR,
                -1
            },
            AndroidH264ScreenEncoder.bitrateModesForCandidate(cbrOnly));
        assertArrayEquals(
            new int[] { -1 },
            AndroidH264ScreenEncoder.bitrateModesForCandidate(noModes));
    }

    @Test
    public void softwareFallbackCannotSilentlyRunAtSixtyFps() {
        AndroidVideoCodecDiagnostics.EncoderCandidate hardware =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "hardware.encoder",
                true,
                false,
                true,
                true);
        AndroidVideoCodecDiagnostics.EncoderCandidate software =
            new AndroidVideoCodecDiagnostics.EncoderCandidate(
                "software.encoder",
                false,
                true,
                false,
                true);

        assertEquals(60, AndroidH264ScreenEncoder.candidateFrameRate(60, hardware));
        assertEquals(30, AndroidH264ScreenEncoder.candidateFrameRate(60, software));
        assertEquals(24, AndroidH264ScreenEncoder.candidateFrameRate(24, software));
        assertFalse(AndroidH264ScreenEncoder.supportsHighFrameRate(true, 30));
        assertTrue(AndroidH264ScreenEncoder.supportsHighFrameRate(true, 60));
        assertFalse(AndroidH264ScreenEncoder.supportsHighFrameRate(false, 60));
    }

    @Test
    public void lowLatencyHintsDegradeOneCompatibilityLayerAtATime() {
        assertArrayEquals(
            new int[] {
                AndroidH264ScreenEncoder.HINT_LATENCY |
                    AndroidH264ScreenEncoder.HINT_PRIORITY |
                    AndroidH264ScreenEncoder.HINT_OPERATING_RATE |
                    AndroidH264ScreenEncoder.HINT_MAX_FPS_TO_ENCODER |
                    AndroidH264ScreenEncoder.HINT_REPEAT_PREVIOUS_FRAME,
                AndroidH264ScreenEncoder.HINT_LATENCY |
                    AndroidH264ScreenEncoder.HINT_PRIORITY |
                    AndroidH264ScreenEncoder.HINT_OPERATING_RATE |
                    AndroidH264ScreenEncoder.HINT_MAX_FPS_TO_ENCODER,
                AndroidH264ScreenEncoder.HINT_LATENCY |
                    AndroidH264ScreenEncoder.HINT_PRIORITY |
                    AndroidH264ScreenEncoder.HINT_OPERATING_RATE,
                AndroidH264ScreenEncoder.HINT_LATENCY |
                    AndroidH264ScreenEncoder.HINT_PRIORITY,
                AndroidH264ScreenEncoder.HINT_LATENCY,
                0
            },
            AndroidH264ScreenEncoder.lowLatencyHintProfiles());
    }

    @Test
    public void outputPollingNeverOvershootsItsRemainingDeadline() {
        assertEquals(
            0L,
            AndroidH264ScreenEncoder.boundedOutputDequeueTimeoutUs(0));
        assertEquals(
            1L,
            AndroidH264ScreenEncoder.boundedOutputDequeueTimeoutUs(1));
        assertEquals(
            1_500L,
            AndroidH264ScreenEncoder.boundedOutputDequeueTimeoutUs(1_500_000));
        assertEquals(
            AndroidH264ScreenEncoder.OUTPUT_DEQUEUE_SLICE_US,
            AndroidH264ScreenEncoder.boundedOutputDequeueTimeoutUs(20_000_000));
    }

    @Test
    public void sixtyFpsBitrateHasHeadroomBeyondLegacyEightMegabitCap() {
        assertEquals(
            7_344_000,
            AndroidH264ScreenEncoder.estimateBitrate(1600, 900, 60));
        assertTrue(
            AndroidH264ScreenEncoder.estimateBitrate(1600, 1600, 60) >
                8_000_000);
        assertEquals(
            AndroidVideoStreamSettings.H264_MAX_BITRATE,
            AndroidH264ScreenEncoder.estimateBitrate(3840, 2160, 60));
    }
}
