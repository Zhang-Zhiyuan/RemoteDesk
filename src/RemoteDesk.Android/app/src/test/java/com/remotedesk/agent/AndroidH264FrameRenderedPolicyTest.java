package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidH264FrameRenderedPolicyTest {
    @Test
    public void acceptsOnlyTheCurrentCodecDecoderAndSurfaceGeneration() {
        Object codec = new Object();
        Object surface = new Object();

        assertTrue(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false,
            true,
            codec,
            codec,
            codec,
            7,
            7,
            surface,
            surface,
            11,
            11));
    }

    @Test
    public void rejectsLateCallbacksFromEveryRetiredIdentity() {
        Object codec = new Object();
        Object retiredCodec = new Object();
        Object surface = new Object();
        Object retiredSurface = new Object();

        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false, true, codec, retiredCodec, retiredCodec,
            7, 7, surface, surface, 11, 11));
        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false, true, codec, codec, codec,
            8, 7, surface, surface, 11, 11));
        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false, true, codec, codec, codec,
            7, 7, surface, retiredSurface, 11, 11));
        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false, true, codec, codec, codec,
            7, 7, surface, surface, 12, 11));
        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            true, true, codec, codec, codec,
            7, 7, surface, surface, 11, 11));
        assertFalse(AndroidH264FrameRenderedPolicy.acceptsCallback(
            false, false, codec, codec, codec,
            7, 7, surface, surface, 11, 11));
    }

    @Test
    public void reportsMissingVendorCallbackOnceAfterAUsefulGraceWindow() {
        int threshold =
            AndroidH264FrameRenderedPolicy.MISSING_CALLBACK_SUBMISSION_THRESHOLD;

        assertFalse(AndroidH264FrameRenderedPolicy.shouldReportMissingCallback(
            threshold - 1L,
            0L,
            false));
        assertTrue(AndroidH264FrameRenderedPolicy.shouldReportMissingCallback(
            threshold,
            0L,
            false));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldReportMissingCallback(
            threshold,
            1L,
            false));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldReportMissingCallback(
            threshold + 1L,
            0L,
            true));
    }

    @Test
    public void rejectsAStartedDecoderThatNeverPresentsItsFirstFrame() {
        long startedAt = 1_000L;
        long deadline = startedAt +
            AndroidH264FrameRenderedPolicy.FIRST_PRESENTATION_TIMEOUT_NANOS;

        assertFalse(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, false, startedAt, deadline - 1L, 0L, false));
        assertTrue(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, false, startedAt, deadline, 0L, false));
    }

    @Test
    public void presentationTimeoutIgnoresConfirmedInactiveAndRecoveringDecoders() {
        long startedAt = 1_000L;
        long afterDeadline = startedAt +
            AndroidH264FrameRenderedPolicy.FIRST_PRESENTATION_TIMEOUT_NANOS + 1L;

        assertFalse(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, false, startedAt, afterDeadline, 1L, false));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            false, false, startedAt, afterDeadline, 0L, false));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, true, startedAt, afterDeadline, 0L, false));
    }

    @Test
    public void aVerifiedSurfaceSurvivesMissingCallbacksBeyondTheTimeout() {
        long startedAt = 1_000L;
        long afterDeadline = startedAt +
            AndroidH264FrameRenderedPolicy.FIRST_PRESENTATION_TIMEOUT_NANOS * 10L;
        assertFalse(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, false, startedAt, afterDeadline, 0L, true));
        // Submission alone still does not waive the black-Surface watchdog.
        assertTrue(AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
            true, false, startedAt, afterDeadline, 0L, false));
    }

    @Test
    public void surfaceProbeRequiresSubmittedOutputAndIsSingleFlight() {
        assertFalse(AndroidH264FrameRenderedPolicy.shouldProbeSurface(0, 0, false, false, 0));
        assertTrue(AndroidH264FrameRenderedPolicy.shouldProbeSurface(1, 0, false, false, 0));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldProbeSurface(30, 0, false, true, 1));
    }

    @Test
    public void surfaceProbeStopsAfterEvidenceOrTheRetryBudget() {
        assertFalse(AndroidH264FrameRenderedPolicy.shouldProbeSurface(30, 1, false, false, 0));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldProbeSurface(30, 0, true, false, 0));
        int limit = AndroidH264FrameRenderedPolicy.MAX_SURFACE_PROBE_ATTEMPTS;
        assertTrue(AndroidH264FrameRenderedPolicy.shouldProbeSurface(30, 0, false, false, limit - 1));
        assertFalse(AndroidH264FrameRenderedPolicy.shouldProbeSurface(30, 0, false, false, limit));
    }

    @Test
    public void telemetryRegistrationFailureIsContainedAndDoesNotBlockCodecWork() {
        boolean[] codecStarted = { false };

        AndroidH264FrameRenderedPolicy.RegistrationResult registration =
            AndroidH264FrameRenderedPolicy.attemptRegistration(() -> {
                throw new IllegalStateException("vendor callback unavailable");
            });
        codecStarted[0] = true;

        assertFalse(registration.registered);
        assertTrue(registration.failure instanceof IllegalStateException);
        assertTrue(codecStarted[0]);
    }
}
