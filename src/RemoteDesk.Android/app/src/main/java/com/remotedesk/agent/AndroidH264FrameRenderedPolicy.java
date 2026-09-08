package com.remotedesk.agent;

import java.util.concurrent.TimeUnit;

final class AndroidH264FrameRenderedPolicy {
    interface Registration {
        void register();
    }

    static final class RegistrationResult {
        final boolean registered;
        final RuntimeException failure;

        private RegistrationResult(boolean registered, RuntimeException failure) {
            this.registered = registered;
            this.failure = failure;
        }
    }

    // Missing callbacks are telemetry, not proof of a black Surface. Probe the
    // actual buffer a bounded number of times; keep a first-presentation timeout
    // when neither a render callback nor a readable Surface buffer is available.
    static final int MISSING_CALLBACK_SUBMISSION_THRESHOLD = 30;
    static final long FIRST_PRESENTATION_TIMEOUT_NANOS = TimeUnit.SECONDS.toNanos(4L);
    static final int MAX_SURFACE_PROBE_ATTEMPTS = 3;
    static final long SURFACE_PROBE_DELAY_MILLIS = 150L;

    private AndroidH264FrameRenderedPolicy() {
    }

    static boolean acceptsCallback(
        boolean closed,
        boolean surfaceUsable,
        Object activeCodec,
        Object callbackCodec,
        Object expectedCodec,
        int activeDecoderGeneration,
        int expectedDecoderGeneration,
        Object activeSurface,
        Object expectedSurface,
        int activeSurfaceGeneration,
        int expectedSurfaceGeneration) {
        return !closed &&
            surfaceUsable &&
            activeCodec != null &&
            activeCodec == callbackCodec &&
            callbackCodec == expectedCodec &&
            activeDecoderGeneration == expectedDecoderGeneration &&
            activeSurface == expectedSurface &&
            activeSurfaceGeneration == expectedSurfaceGeneration;
    }

    static boolean shouldReportMissingCallback(
        long submittedOutputs,
        long renderedCallbacks,
        boolean alreadyReported) {
        return !alreadyReported &&
            renderedCallbacks == 0 &&
            submittedOutputs >= MISSING_CALLBACK_SUBMISSION_THRESHOLD;
    }

    static boolean shouldRejectUnpresentedCandidate(
        boolean candidateActive,
        boolean recoveryNeeded,
        long candidateStartedAtNanos,
        long nowNanos,
        long renderedCallbacks,
        boolean surfaceBufferConfirmed) {
        return candidateActive &&
            !recoveryNeeded &&
            renderedCallbacks == 0L &&
            !surfaceBufferConfirmed &&
            candidateStartedAtNanos > 0L &&
            nowNanos >= candidateStartedAtNanos &&
            nowNanos - candidateStartedAtNanos >= FIRST_PRESENTATION_TIMEOUT_NANOS;
    }

    static boolean shouldProbeSurface(
        long submittedOutputs,
        long renderedCallbacks,
        boolean surfaceBufferConfirmed,
        boolean probePending,
        int attemptedProbes) {
        return submittedOutputs > 0L && renderedCallbacks == 0L &&
            !surfaceBufferConfirmed && !probePending &&
            attemptedProbes < MAX_SURFACE_PROBE_ATTEMPTS;
    }

    static RegistrationResult attemptRegistration(Registration registration) {
        if (registration == null) {
            throw new IllegalArgumentException("registration is required");
        }
        try {
            registration.register();
            return new RegistrationResult(true, null);
        } catch (RuntimeException ex) {
            return new RegistrationResult(false, ex);
        }
    }
}
