package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidH264CapabilityPolicyTest {
    @Test
    public void unavailableEncoderDoesNotAdvertiseVideoCapabilities() {
        assertEquals(
            0,
            AndroidH264CapabilityPolicy.hostCapabilities(
                AndroidVideoCodecDiagnostics.CodecReport.unavailable()));
        assertEquals(0, AndroidH264CapabilityPolicy.hostCapabilities(null));
    }

    @Test
    public void softwareEncoderAdvertisesRecoveryButNotSixtyFps() {
        AndroidVideoCodecDiagnostics.CodecReport software = report(
            false,
            true);

        int capabilities =
            AndroidH264CapabilityPolicy.hostCapabilities(software);

        assertTrue((capabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertFalse((capabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);
        assertEquals(
            AndroidVideoStreamSettings.H264_COMPATIBILITY_FPS,
            AndroidH264CapabilityPolicy.targetFramesPerSecond(
                RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264,
                software));
    }

    @Test
    public void hardwareEncoderAndViewerMustBothOptIntoSixtyFps() {
        AndroidVideoCodecDiagnostics.CodecReport hardware = report(
            true,
            false);
        int hostCapabilities =
            AndroidH264CapabilityPolicy.hostCapabilities(hardware);

        assertTrue((hostCapabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertTrue((hostCapabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);
        assertEquals(
            AndroidVideoStreamSettings.H264_COMPATIBILITY_FPS,
            AndroidH264CapabilityPolicy.targetFramesPerSecond(0, hardware));
        assertEquals(
            AndroidVideoStreamSettings.H264_TARGET_FPS,
            AndroidH264CapabilityPolicy.targetFramesPerSecond(
                RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264,
                hardware));
    }

    @Test
    public void softwareWinnerStaysStableAfterHardwareCandidateFailed() {
        int viewerCapabilities =
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264;

        assertEquals(
            AndroidVideoStreamSettings.H264_COMPATIBILITY_FPS,
            AndroidH264CapabilityPolicy.targetFramesPerSecondForActiveEncoder(
                viewerCapabilities,
                false));
        assertEquals(
            AndroidVideoStreamSettings.H264_TARGET_FPS,
            AndroidH264CapabilityPolicy.targetFramesPerSecondForActiveEncoder(
                viewerCapabilities,
                true));
    }

    @Test
    public void udpNetworkTargetIsClampedToEncoderContract() {
        assertEquals(
            AndroidVideoStreamSettings.H264_MIN_BITRATE,
            AndroidH264CapabilityPolicy.clampNetworkTargetBitrate(1L));
        assertEquals(
            4_500_000,
            AndroidH264CapabilityPolicy.clampNetworkTargetBitrate(4_500_000L));
        assertEquals(
            AndroidVideoStreamSettings.H264_MAX_BITRATE,
            AndroidH264CapabilityPolicy.clampNetworkTargetBitrate(Long.MAX_VALUE));
    }

    @Test
    public void udpFeedbackIsACeilingNotASecondRampController() {
        assertEquals(
            4_000_000,
            AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                8_000_000,
                0,
                4_000_000));
        assertEquals(
            0,
            AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                4_000_000,
                0,
                8_000_000));
        assertEquals(
            4_500_000,
            AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                4_000_000,
                4_500_000,
                8_000_000));
        assertEquals(
            3_000_000,
            AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                4_000_000,
                4_500_000,
                3_000_000));
        assertEquals(
            3_000_000,
            AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                4_000_000,
                3_000_000,
                0));
    }

    @Test
    public void repeatedNetworkReductionsAreCoalescedBeforeAnotherIdr() {
        assertEquals(
            8_000_000,
            AndroidH264CapabilityPolicy.rateLimitedNetworkCeiling(
                8_000_000,
                4_000_000,
                200L,
                500L));
        assertEquals(
            4_000_000,
            AndroidH264CapabilityPolicy.rateLimitedNetworkCeiling(
                8_000_000,
                4_000_000,
                500L,
                500L));
        assertEquals(
            10_000_000,
            AndroidH264CapabilityPolicy.rateLimitedNetworkCeiling(
                8_000_000,
                10_000_000,
                200L,
                500L));
        assertEquals(
            0,
            AndroidH264CapabilityPolicy.rateLimitedNetworkCeiling(
                8_000_000,
                0,
                200L,
                500L));
    }

    @Test
    public void failedNetworkReductionAttemptIsStillRateLimited() {
        assertEquals(
            500_000_200L,
            AndroidH264CapabilityPolicy.recordNetworkReductionAttempt(
                8_000_000,
                4_000_000,
                4_000_000,
                200L,
                0L));
        assertEquals(
            700L,
            AndroidH264CapabilityPolicy.recordNetworkReductionAttempt(
                8_000_000,
                0,
                4_000_000,
                200L,
                700L));
        assertEquals(
            700L,
            AndroidH264CapabilityPolicy.recordNetworkReductionAttempt(
                8_000_000,
                9_000_000,
                9_000_000,
                200L,
                700L));
    }

    private static AndroidVideoCodecDiagnostics.CodecReport report(
        boolean hardware,
        boolean software) {
        return new AndroidVideoCodecDiagnostics.CodecReport(
            true,
            "codec.avc.encoder",
            true,
            hardware,
            software,
            true,
            true,
            null);
    }
}
