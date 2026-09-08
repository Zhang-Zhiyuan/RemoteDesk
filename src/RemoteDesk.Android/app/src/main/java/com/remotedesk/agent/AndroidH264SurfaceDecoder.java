package com.remotedesk.agent;

import android.graphics.Bitmap;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Process;
import android.view.Surface;
import android.view.PixelCopy;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.TimeUnit;

final class AndroidH264SurfaceDecoder implements AutoCloseable {
    interface Listener {
        void onRecoveryFrameNeeded(String reason);

        void onDecoderStarted(AndroidH264DecoderDiagnostics.DecoderCandidate candidate);

        void onFrameRendered();

        // A readable Surface buffer confirms startup, not a per-frame timing
        // sample. Never count this fallback as presented FPS.
        void onSurfaceBufferAvailable();

        void onDecoderUnavailable(String reason);
    }

    enum OfferResult {
        ACCEPTED,
        DROPPED_WAITING_FOR_RECOVERY,
        REJECTED_CLOSED
    }

    // Leave enough room for a slow vendor codec cold start. Once the decoder
    // is running, independently decodable recovery units still collapse this
    // queue to the newest frame below.
    static final int ACCESS_UNIT_QUEUE_CAPACITY = 3;

    private static final String MIME_TYPE = "video/avc";
    private static final long INPUT_DEQUEUE_TIMEOUT_US = 5_000;
    // Most hardware decoders return a Surface buffer within a few milliseconds.
    // Waiting for up to 8 ms here is still below a 60 Hz frame budget and avoids
    // sleeping until the next network access unit when output misses a 2 ms poll.
    static final long OUTPUT_DEQUEUE_BUDGET_US = 8_000;

    private final Object stateLock = new Object();
    private final List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates;
    private final Listener listener;
    private final ArrayDeque<AccessUnit> accessUnits = new ArrayDeque<>();
    private final Set<String> failedCodecNames = new HashSet<>();
    private final HandlerThread frameRenderedThread;
    private final Handler frameRenderedHandler;
    private final Thread worker;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    private Surface outputSurface;
    private boolean outputSurfaceUsable;
    private int surfaceGeneration;
    private AccessUnit pendingSurfaceRecovery;
    private MediaCodec codec;
    private MediaCodec frameRenderedCodec;
    private AndroidH264DecoderDiagnostics.DecoderCandidate activeCandidate;
    private boolean resetRequested;
    private boolean recoveryNeeded = true;
    private boolean recoveryRequestReported;
    private boolean streamSeen;
    private boolean unavailableReported;
    private boolean closed;
    private int generation;
    private int configuredWidth;
    private int configuredHeight;
    private int consecutiveAccessUnitsWithoutOutput;
    private int consecutiveInputDequeueMisses;
    private boolean activeCandidateReported;
    private long nextPresentationTimeUs;
    private long submittedSurfaceOutputs;
    private long renderedSurfaceCallbacks;
    private long activeCandidateStartedAtNanos;
    private int activeCodecGeneration = -1;
    private int activeCodecSurfaceGeneration = -1;
    private boolean surfaceBufferConfirmed;
    private boolean surfaceProbePending;
    private int surfaceProbeAttempts;
    private boolean missingFrameRenderedCallbackReported;
    private boolean frameRenderedListenerFailureReported;
    private boolean frameRenderedRegistrationFailureReported;

    AndroidH264SurfaceDecoder(
        List<AndroidH264DecoderDiagnostics.DecoderCandidate> candidates,
        Listener listener) {
        if (candidates == null || candidates.isEmpty()) {
            throw new IllegalArgumentException("decoder candidates are required");
        }
        if (listener == null) {
            throw new IllegalArgumentException("listener is required");
        }

        this.candidates = candidates;
        this.listener = listener;
        frameRenderedThread = new HandlerThread(
            "RemoteDesk-H264-FrameRendered",
            Process.THREAD_PRIORITY_DISPLAY);
        frameRenderedThread.start();
        frameRenderedHandler = new Handler(frameRenderedThread.getLooper());
        worker = new Thread(this::runWorker, "RemoteDesk-H264-SurfaceDecoder");
        worker.start();
    }

    OfferResult offer(RemoteDeskTransport.FrameMessage frame) {
        if (frame == null ||
            frame.encoding != RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B) {
            throw new IllegalArgumentException("an H.264 frame is required");
        }

        boolean reportRecovery = false;
        String recoveryReason = "H.264 decoder needs a recovery frame";
        OfferResult result;
        synchronized (stateLock) {
            if (closed) {
                return OfferResult.REJECTED_CLOSED;
            }

            streamSeen = true;
            boolean recovery = AndroidH264DecoderPolicy.isRecoveryAccessUnit(frame.flags);
            boolean dropForDimensionChange = false;
            if (AndroidH264DecoderPolicy.requiresReconfigure(
                    configuredWidth,
                    configuredHeight,
                    frame.width,
                    frame.height)) {
                beginRecoveryLocked();
                if (!recovery) {
                    reportRecovery = markRecoveryRequestLocked();
                    recoveryReason = "H.264 dimensions changed";
                    dropForDimensionChange = true;
                }
            }

            if (dropForDimensionChange) {
                result = OfferResult.DROPPED_WAITING_FOR_RECOVERY;
            } else {
                AccessUnit accessUnit = new AccessUnit(
                    frame.width,
                    frame.height,
                    frame.flags,
                    frame.encodedBytes,
                    generation);
                if (outputSurface == null || !outputSurface.isValid()) {
                    if (recovery) {
                        pendingSurfaceRecovery = accessUnit;
                    } else {
                        reportRecovery = markRecoveryRequestLocked();
                    }
                    result = recovery
                        ? OfferResult.ACCEPTED
                        : OfferResult.DROPPED_WAITING_FOR_RECOVERY;
                } else if (recoveryNeeded) {
                    if (!recovery) {
                        reportRecovery = markRecoveryRequestLocked();
                        result = OfferResult.DROPPED_WAITING_FOR_RECOVERY;
                    } else {
                        enqueueRecoveryLocked(accessUnit);
                        result = OfferResult.ACCEPTED;
                    }
                } else if (
                    AndroidH264DecoderPolicy.canSupersedeQueuedAccessUnits(frame.flags)) {
                    replaceQueuedChainWithRecoveryLocked(accessUnit);
                    result = OfferResult.ACCEPTED;
                } else if (accessUnits.size() >= ACCESS_UNIT_QUEUE_CAPACITY) {
                    AndroidH264DecoderPolicy.SaturationAction action =
                        AndroidH264DecoderPolicy.saturationAction(frame.flags);
                    beginRecoveryLocked();
                    if (action ==
                            AndroidH264DecoderPolicy.SaturationAction.RESET_AND_ACCEPT_RECOVERY) {
                        accessUnit = new AccessUnit(
                            frame.width,
                            frame.height,
                            frame.flags,
                            frame.encodedBytes,
                            generation);
                        enqueueRecoveryLocked(accessUnit);
                        result = OfferResult.ACCEPTED;
                    } else {
                        reportRecovery = markRecoveryRequestLocked();
                        recoveryReason = "H.264 decoder queue was saturated";
                        result = OfferResult.DROPPED_WAITING_FOR_RECOVERY;
                    }
                } else {
                    accessUnits.addLast(accessUnit);
                    result = OfferResult.ACCEPTED;
                }
            }

            stateLock.notifyAll();
        }

        if (reportRecovery) {
            listener.onRecoveryFrameNeeded(recoveryReason);
        }
        return result;
    }

    boolean rejectUnpresentedCandidateIfTimedOut(long nowNanos) {
        String rejectedCodecName;
        boolean reportRecovery;
        synchronized (stateLock) {
            if (!AndroidH264FrameRenderedPolicy.shouldRejectUnpresentedCandidate(
                    codec != null && activeCodecGeneration == generation &&
                        activeCodecSurfaceGeneration == surfaceGeneration,
                    recoveryNeeded,
                    activeCandidateStartedAtNanos,
                    nowNanos,
                    renderedSurfaceCallbacks,
                    surfaceBufferConfirmed) ||
                activeCandidate == null) {
                return false;
            }

            rejectedCodecName = activeCandidate.codecName;
            failedCodecNames.add(rejectedCodecName);
            beginRecoveryLocked();
            reportRecovery = markRecoveryRequestLocked();
            stateLock.notifyAll();
        }

        AndroidSessionLog.info(
            "H.264 Surface decoder produced no presented frame within " +
                TimeUnit.NANOSECONDS.toMillis(
                    AndroidH264FrameRenderedPolicy.FIRST_PRESENTATION_TIMEOUT_NANOS) +
                " ms; rejecting " + rejectedCodecName + ".");
        if (reportRecovery) {
            listener.onRecoveryFrameNeeded(
                "H.264 decoder first-frame presentation timed out");
        }
        return true;
    }

    // Diagnostic count, deliberately separate from actual render callbacks.
    long submittedSurfaceOutputCount() {
        synchronized (stateLock) {
            return submittedSurfaceOutputs;
        }
    }

    void setOutputSurface(Surface surface) {
        boolean surfaceUsable = surface != null && surface.isValid();
        boolean reportRecovery = false;
        synchronized (stateLock) {
            if (closed ||
                !AndroidH264DecoderPolicy.shouldProcessSurfaceUpdate(
                    outputSurface == surface,
                    outputSurfaceUsable,
                    surfaceUsable)) {
                return;
            }

            outputSurface = surface;
            outputSurfaceUsable = surfaceUsable;
            surfaceGeneration++;
            beginRecoveryLocked();
            if (surfaceUsable && pendingSurfaceRecovery != null) {
                AccessUnit pending = pendingSurfaceRecovery;
                pendingSurfaceRecovery = null;
                enqueueRecoveryLocked(new AccessUnit(
                    pending.width,
                    pending.height,
                    pending.flags,
                    pending.bytes,
                    generation));
            } else if (surfaceUsable && streamSeen) {
                reportRecovery = markRecoveryRequestLocked();
            } else if (surface == null) {
                pendingSurfaceRecovery = null;
            }

            stateLock.notifyAll();
        }

        if (reportRecovery) {
            listener.onRecoveryFrameNeeded("H.264 output Surface was recreated");
        }
    }

    @Override
    public void close() {
        synchronized (stateLock) {
            if (closed) {
                return;
            }

            closed = true;
            generation++;
            surfaceGeneration++;
            frameRenderedCodec = null;
            accessUnits.clear();
            pendingSurfaceRecovery = null;
            stateLock.notifyAll();
        }

        worker.interrupt();
        if (Thread.currentThread() != worker) {
            try {
                worker.join(1_000);
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            }
        }
        frameRenderedHandler.removeCallbacksAndMessages(null);
        if (Thread.currentThread() == frameRenderedThread) {
            frameRenderedThread.quitSafely();
        } else {
            boolean callbackThreadStopped = AndroidBoundedThreadShutdown.stop(
                frameRenderedThread,
                new AndroidBoundedThreadShutdown.StopActions() {
                    @Override
                    public void requestGracefulStop() {
                        frameRenderedThread.quitSafely();
                    }

                    @Override
                    public void requestForcedStop() {
                        frameRenderedThread.quit();
                    }
                },
                500L);
            if (!callbackThreadStopped && frameRenderedThread.isAlive()) {
                AndroidSessionLog.info(
                    "H.264 frame-rendered callback thread did not stop within 500 ms.");
            }
        }
    }

    private void runWorker() {
        try {
            try {
                Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY);
            } catch (RuntimeException ignored) {
                // Thread priority is an optimization; codec compatibility and
                // the JPEG fallback must not depend on vendor scheduler policy.
            }

            while (true) {
                WorkItem workItem;
                synchronized (stateLock) {
                    while (!closed && !resetRequested && accessUnits.isEmpty()) {
                        stateLock.wait();
                    }

                    if (closed) {
                        break;
                    }

                    boolean reset = resetRequested;
                    resetRequested = false;
                    AccessUnit accessUnit = accessUnits.pollFirst();
                    workItem = new WorkItem(reset, accessUnit);
                }

                if (workItem.reset) {
                    releaseCodec();
                }
                if (workItem.accessUnit != null) {
                    processAccessUnit(workItem.accessUnit);
                }
            }
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        } finally {
            releaseCodec();
        }
    }

    private void processAccessUnit(AccessUnit accessUnit) {
        Surface surface;
        int expectedSurfaceGeneration;
        synchronized (stateLock) {
            if (closed || accessUnit.generation != generation) {
                return;
            }

            surface = outputSurface;
            expectedSurfaceGeneration = surfaceGeneration;
        }

        if (surface == null || !surface.isValid()) {
            transitionToRecovery(
                accessUnit,
                false,
                "H.264 output Surface is unavailable");
            return;
        }

        try {
            if (codec == null &&
                !startCodec(
                    accessUnit.width,
                    accessUnit.height,
                    surface,
                    accessUnit.generation,
                    expectedSurfaceGeneration)) {
                if (isCurrentGeneration(accessUnit.generation, surface)) {
                    reportDecoderUnavailable(
                        "No Android H.264 decoder candidate could start.");
                }
                transitionToRecovery(accessUnit, false, null);
                return;
            }

            boolean renderedOutput =
                drainOutputs(0, accessUnit.generation);
            int inputIndex = codec.dequeueInputBuffer(INPUT_DEQUEUE_TIMEOUT_US);
            if (inputIndex < 0) {
                consecutiveInputDequeueMisses++;
                if (AndroidH264DecoderPolicy.inputDequeueMissTimedOut(
                        consecutiveInputDequeueMisses)) {
                    throw new IllegalStateException(
                        "H.264 decoder input stayed saturated for " +
                        consecutiveInputDequeueMisses +
                        " consecutive access units.");
                }
                retryAccessUnitIfLatest(accessUnit);
                return;
            }
            consecutiveInputDequeueMisses = 0;

            ByteBuffer inputBuffer = codec.getInputBuffer(inputIndex);
            if (inputBuffer == null || inputBuffer.capacity() < accessUnit.bytes.length) {
                throw new IllegalStateException(
                    "H.264 decoder input buffer is unavailable or too small.");
            }

            inputBuffer.clear();
            inputBuffer.put(accessUnit.bytes);
            if (!isCurrentGeneration(accessUnit.generation, surface)) {
                return;
            }
            long presentationTimeUs = nextPresentationTimeUs++;
            codec.queueInputBuffer(
                inputIndex,
                0,
                accessUnit.bytes.length,
                presentationTimeUs,
                0);
            renderedOutput |= drainOutputs(
                OUTPUT_DEQUEUE_BUDGET_US,
                accessUnit.generation);
            AndroidH264DecoderPolicy.OutputProbeResult probeResult =
                AndroidH264DecoderPolicy.afterAccessUnit(
                    consecutiveAccessUnitsWithoutOutput,
                    renderedOutput);
            consecutiveAccessUnitsWithoutOutput =
                probeResult.consecutiveAccessUnitsWithoutOutput;
            if (probeResult.outputConfirmed) {
                reportActualDecoderCandidate();
            }
            if (probeResult.candidateTimedOut) {
                throw new IllegalStateException(
                    "H.264 decoder produced no Surface output for " +
                    consecutiveAccessUnitsWithoutOutput +
                    " consecutive access units.");
            }
        } catch (RuntimeException ex) {
            AndroidSessionLog.error(
                "H.264 Surface decoder failed while processing an access unit.",
                ex);
            boolean currentGeneration =
                rejectActiveCandidateIfCurrent(accessUnit.generation, surface);
            transitionToRecovery(
                accessUnit,
                false,
                currentGeneration
                    ? "H.264 decoder failed"
                    : "H.264 output Surface changed");
        }
    }

    private boolean startCodec(
        int width,
        int height,
        Surface surface,
        int expectedGeneration,
        int expectedSurfaceGeneration) {
        for (AndroidH264DecoderDiagnostics.DecoderCandidate candidate : candidates) {
            if (failedCodecNames.contains(candidate.codecName)) {
                continue;
            }

            Throwable lastFailure = null;
            for (boolean lowLatencyHint : new boolean[] { true, false }) {
                if (!isCurrentGeneration(expectedGeneration, surface)) {
                    return false;
                }

                MediaCodec candidateCodec = null;
                try {
                    candidateCodec = MediaCodec.createByCodecName(candidate.codecName);
                    candidateCodec.configure(
                        createFormat(width, height, lowLatencyHint),
                        surface,
                        null,
                        0);
                    MediaCodec registeredCodec = candidateCodec;
                    AndroidH264FrameRenderedPolicy.RegistrationResult registration =
                        AndroidH264FrameRenderedPolicy.attemptRegistration(() ->
                            registerFrameRenderedListener(
                                registeredCodec,
                                expectedGeneration,
                                surface,
                                expectedSurfaceGeneration));
                    if (!registration.registered) {
                        reportFrameRenderedRegistrationFailure(registration.failure);
                    }
                    candidateCodec.start();
                    boolean current;
                    synchronized (stateLock) {
                        current =
                            isCurrentGenerationLocked(expectedGeneration, surface) &&
                            surfaceGeneration == expectedSurfaceGeneration;
                        if (current) {
                            codec = candidateCodec;
                            frameRenderedCodec = registration.registered
                                ? candidateCodec
                                : null;
                            activeCandidate = candidate;
                            nextPresentationTimeUs = 0;
                            consecutiveAccessUnitsWithoutOutput = 0;
                            activeCandidateReported = false;
                            submittedSurfaceOutputs = 0;
                            renderedSurfaceCallbacks = 0;
                            activeCandidateStartedAtNanos = System.nanoTime();
                            activeCodecGeneration = expectedGeneration;
                            activeCodecSurfaceGeneration = expectedSurfaceGeneration;
                            surfaceBufferConfirmed = false;
                            surfaceProbePending = false;
                            surfaceProbeAttempts = 0;
                            missingFrameRenderedCallbackReported = false;
                            frameRenderedListenerFailureReported = false;
                            unavailableReported = false;
                        }
                    }
                    if (!current) {
                        releaseCandidateCodec(candidateCodec);
                        return false;
                    }
                    AndroidSessionLog.info(
                        "H.264 Surface decoder candidate is probing actual output: " +
                        candidate.codecName +
                        ", selection=" +
                        candidate.selectionLabel() +
                        ".");
                    return true;
                } catch (IOException | RuntimeException ex) {
                    lastFailure = ex;
                    releaseCandidateCodec(candidateCodec);
                    if (!isCurrentGeneration(expectedGeneration, surface)) {
                        return false;
                    }
                }
            }

            if (!rejectCandidateIfCurrent(
                    expectedGeneration,
                    surface,
                    candidate)) {
                return false;
            }
            if (lastFailure != null) {
                AndroidSessionLog.error(
                    "H.264 decoder candidate failed: " +
                    candidate.codecName +
                    ", selection=" +
                    candidate.selectionLabel() +
                    ".",
                    lastFailure);
            }
        }

        return false;
    }

    private static MediaFormat createFormat(
        int width,
        int height,
        boolean lowLatencyHint) {
        MediaFormat format = MediaFormat.createVideoFormat(MIME_TYPE, width, height);
        if (lowLatencyHint) {
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            format.setFloat(MediaFormat.KEY_OPERATING_RATE, 120.0f);
            if (Build.VERSION.SDK_INT >= 30) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            }
        }
        return format;
    }

    private boolean drainOutputs(long firstTimeoutUs, int expectedGeneration) {
        int renderIndex = -1;
        long timeoutUs = firstTimeoutUs;
        while (true) {
            int outputIndex = codec.dequeueOutputBuffer(bufferInfo, timeoutUs);
            timeoutUs = 0;
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                break;
            }
            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                continue;
            }
            if (outputIndex < 0) {
                continue;
            }

            boolean displayable =
                AndroidH264DecoderPolicy.isDisplayableSurfaceOutput(bufferInfo.flags);
            if (!displayable) {
                codec.releaseOutputBuffer(outputIndex, false);
                continue;
            }

            if (renderIndex >= 0) {
                codec.releaseOutputBuffer(renderIndex, false);
            }
            renderIndex = outputIndex;
        }

        if (renderIndex < 0) {
            return false;
        }

        boolean render = isCurrentGeneration(expectedGeneration, null);
        MediaCodec activeCodec = codec;
        activeCodec.releaseOutputBuffer(renderIndex, render);
        if (render) {
            recordSurfaceOutputSubmitted(activeCodec, expectedGeneration);
        }
        return render;
    }

    private void registerFrameRenderedListener(
        MediaCodec expectedCodec,
        int expectedDecoderGeneration,
        Surface expectedSurface,
        int expectedSurfaceGeneration) {
        expectedCodec.setOnFrameRenderedListener(
            (callbackCodec, presentationTimeUs, nanoTime) ->
                handleFrameRendered(
                    callbackCodec,
                    expectedCodec,
                    expectedDecoderGeneration,
                    expectedSurface,
                    expectedSurfaceGeneration),
            frameRenderedHandler);
    }

    private void reportFrameRenderedRegistrationFailure(RuntimeException failure) {
        synchronized (stateLock) {
            if (frameRenderedRegistrationFailureReported) {
                return;
            }
            frameRenderedRegistrationFailureReported = true;
        }
        AndroidSessionLog.error(
            "MediaCodec OnFrameRendered telemetry is unavailable; " +
            "H.264 decoding will continue and presented FPS will remain unknown.",
            failure);
    }

    private void handleFrameRendered(
        MediaCodec callbackCodec,
        MediaCodec expectedCodec,
        int expectedDecoderGeneration,
        Surface expectedSurface,
        int expectedSurfaceGeneration) {
        synchronized (stateLock) {
            if (!AndroidH264FrameRenderedPolicy.acceptsCallback(
                    closed,
                    outputSurfaceUsable &&
                        outputSurface != null &&
                        outputSurface.isValid(),
                    frameRenderedCodec,
                    callbackCodec,
                    expectedCodec,
                    generation,
                    expectedDecoderGeneration,
                    outputSurface,
                    expectedSurface,
                    surfaceGeneration,
                    expectedSurfaceGeneration)) {
                return;
            }
            renderedSurfaceCallbacks++;
        }

        try {
            // Keep listener ordering deterministic even when a vendor invokes
            // the callback immediately after releaseOutputBuffer(..., true).
            reportActualDecoderCandidate();
            listener.onFrameRendered();
        } catch (RuntimeException ex) {
            boolean reportFailure;
            synchronized (stateLock) {
                reportFailure =
                    !frameRenderedListenerFailureReported &&
                    frameRenderedCodec == expectedCodec &&
                    generation == expectedDecoderGeneration &&
                    outputSurface == expectedSurface &&
                    surfaceGeneration == expectedSurfaceGeneration;
                if (reportFailure) {
                    frameRenderedListenerFailureReported = true;
                }
            }
            if (reportFailure) {
                AndroidSessionLog.error(
                    "H.264 frame-rendered listener failed.",
                    ex);
            }
        }
    }

    private void recordSurfaceOutputSubmitted(
        MediaCodec submittedCodec,
        int expectedDecoderGeneration) {
        boolean reportMissingCallback = false;
        long submittedOutputCount = 0;
        synchronized (stateLock) {
            if (closed ||
                generation != expectedDecoderGeneration ||
                codec != submittedCodec) {
                return;
            }

            submittedSurfaceOutputs++;
            if (AndroidH264FrameRenderedPolicy.shouldReportMissingCallback(
                    submittedSurfaceOutputs,
                    renderedSurfaceCallbacks,
                    missingFrameRenderedCallbackReported)) {
                missingFrameRenderedCallbackReported = true;
                reportMissingCallback = true;
                submittedOutputCount = submittedSurfaceOutputs;
            }
            scheduleSurfaceProbeLocked(submittedCodec, expectedDecoderGeneration,
                outputSurface, surfaceGeneration);
        }

        if (reportMissingCallback) {
            String message =
                "MediaCodec submitted " + submittedOutputCount +
                " H.264 Surface outputs without an OnFrameRendered callback; " +
                "using bounded Surface-buffer verification; presented FPS remains unknown.";
            AndroidSessionLog.info(message);
        }
    }

    // stateLock is held by the caller. Probes are delayed off the codec and UI
    // threads, so a healthy vendor callback cancels them before any GPU readback.
    private void scheduleSurfaceProbeLocked(
        MediaCodec expectedCodec, int expectedGeneration,
        Surface expectedSurface, int expectedSurfaceGeneration) {
        if (!AndroidH264FrameRenderedPolicy.shouldProbeSurface(
                submittedSurfaceOutputs, renderedSurfaceCallbacks,
                surfaceBufferConfirmed, surfaceProbePending, surfaceProbeAttempts)) {
            return;
        }
        surfaceProbePending = true;
        frameRenderedHandler.postDelayed(() -> probeSurfaceBuffer(
            expectedCodec, expectedGeneration, expectedSurface, expectedSurfaceGeneration),
            AndroidH264FrameRenderedPolicy.SURFACE_PROBE_DELAY_MILLIS);
    }

    private boolean acceptsSurfaceProbeLocked(
        MediaCodec expectedCodec, int expectedGeneration,
        Surface expectedSurface, int expectedSurfaceGeneration) {
        return AndroidH264FrameRenderedPolicy.acceptsCallback(
            closed, outputSurfaceUsable && outputSurface != null && outputSurface.isValid(),
            codec, expectedCodec, expectedCodec, generation, expectedGeneration,
            outputSurface, expectedSurface, surfaceGeneration, expectedSurfaceGeneration);
    }

    private void probeSurfaceBuffer(
        MediaCodec expectedCodec, int expectedGeneration,
        Surface expectedSurface, int expectedSurfaceGeneration) {
        synchronized (stateLock) {
            if (!acceptsSurfaceProbeLocked(expectedCodec, expectedGeneration,
                    expectedSurface, expectedSurfaceGeneration)) {
                return;
            }
            if (renderedSurfaceCallbacks > 0L || surfaceBufferConfirmed) {
                surfaceProbePending = false;
                return;
            }
            surfaceProbeAttempts++;
        }

        // Only verify availability, not brightness: a genuinely black desktop
        // is valid content. The tiny sample is never retained or written to disk.
        Bitmap sample = Bitmap.createBitmap(1, 1, Bitmap.Config.ARGB_8888);
        try {
            PixelCopy.request(expectedSurface, sample, result -> {
                try {
                    finishSurfaceProbe(expectedCodec, expectedGeneration, expectedSurface,
                        expectedSurfaceGeneration, result == PixelCopy.SUCCESS);
                } finally {
                    sample.recycle();
                }
            }, frameRenderedHandler);
        } catch (RuntimeException ex) {
            sample.recycle();
            finishSurfaceProbe(expectedCodec, expectedGeneration, expectedSurface,
                expectedSurfaceGeneration, false);
        }
    }

    private void finishSurfaceProbe(
        MediaCodec expectedCodec, int expectedGeneration,
        Surface expectedSurface, int expectedSurfaceGeneration, boolean bufferAvailable) {
        synchronized (stateLock) {
            if (!acceptsSurfaceProbeLocked(expectedCodec, expectedGeneration,
                    expectedSurface, expectedSurfaceGeneration)) {
                return;
            }
            surfaceProbePending = false;
            if (renderedSurfaceCallbacks > 0L || surfaceBufferConfirmed) {
                return;
            }
            if (!bufferAvailable) {
                scheduleSurfaceProbeLocked(expectedCodec, expectedGeneration,
                    expectedSurface, expectedSurfaceGeneration);
                return;
            }
            surfaceBufferConfirmed = true;
            // Serialize startup notification and this one-shot confirmation
            // with Surface replacement / close. Otherwise a late probe could
            // enable the next Surface, or race onDecoderStarted resetting the UI.
            try {
                reportActualDecoderCandidate();
                if (!acceptsSurfaceProbeLocked(expectedCodec, expectedGeneration,
                        expectedSurface, expectedSurfaceGeneration)) {
                    return;
                }
                listener.onSurfaceBufferAvailable();
            } catch (RuntimeException ex) {
                AndroidSessionLog.error("H.264 Surface-buffer listener failed.", ex);
            }
        }
        AndroidSessionLog.info(
            "H.264 Surface buffer verified by PixelCopy without render callbacks; " +
            "continuing decoding without inventing presented FPS.");
    }

    private boolean isCurrentGeneration(int expectedGeneration, Surface expectedSurface) {
        synchronized (stateLock) {
            return isCurrentGenerationLocked(expectedGeneration, expectedSurface);
        }
    }

    private boolean rejectActiveCandidateIfCurrent(
        int expectedGeneration,
        Surface expectedSurface) {
        synchronized (stateLock) {
            if (!isCurrentGenerationLocked(expectedGeneration, expectedSurface)) {
                return false;
            }

            if (activeCandidate != null) {
                failedCodecNames.add(activeCandidate.codecName);
            }
            return true;
        }
    }

    private boolean rejectCandidateIfCurrent(
        int expectedGeneration,
        Surface expectedSurface,
        AndroidH264DecoderDiagnostics.DecoderCandidate candidate) {
        synchronized (stateLock) {
            if (!isCurrentGenerationLocked(expectedGeneration, expectedSurface)) {
                return false;
            }

            failedCodecNames.add(candidate.codecName);
            return true;
        }
    }

    private boolean isCurrentGenerationLocked(
        int expectedGeneration,
        Surface expectedSurface) {
        return !closed &&
            generation == expectedGeneration &&
            (expectedSurface == null ||
                (outputSurface == expectedSurface && expectedSurface.isValid()));
    }

    private void transitionToRecovery(
        AccessUnit accessUnit,
        boolean preserveRecovery,
        String reason) {
        boolean reportRecovery;
        synchronized (stateLock) {
            if (closed || accessUnit.generation != generation) {
                return;
            }

            beginRecoveryLocked();
            if (preserveRecovery &&
                AndroidH264DecoderPolicy.isRecoveryAccessUnit(accessUnit.flags) &&
                outputSurface != null &&
                outputSurface.isValid()) {
                enqueueRecoveryLocked(new AccessUnit(
                    accessUnit.width,
                    accessUnit.height,
                    accessUnit.flags,
                    accessUnit.bytes,
                    generation));
            }
            reportRecovery = reason != null && markRecoveryRequestLocked();
            stateLock.notifyAll();
        }

        if (reportRecovery) {
            listener.onRecoveryFrameNeeded(reason);
        }
    }

    private void retryAccessUnitIfLatest(AccessUnit accessUnit) {
        synchronized (stateLock) {
            if (closed || accessUnit.generation != generation) {
                return;
            }

            // A newly arrived GOP1 frame is fresher and already replaced the
            // queue. Otherwise retain this independently decodable unit so a
            // static source cannot lose its only startup frame to one transient
            // vendor input-buffer miss.
            if (accessUnits.isEmpty()) {
                accessUnits.addLast(accessUnit);
                stateLock.notifyAll();
            }
        }
    }

    private void beginRecoveryLocked() {
        generation++;
        accessUnits.clear();
        recoveryNeeded = true;
        recoveryRequestReported = false;
        resetRequested = true;
        configuredWidth = 0;
        configuredHeight = 0;
    }

    private void enqueueRecoveryLocked(AccessUnit accessUnit) {
        accessUnits.clear();
        accessUnits.addLast(accessUnit);
        recoveryNeeded = false;
        recoveryRequestReported = false;
        configuredWidth = accessUnit.width;
        configuredHeight = accessUnit.height;
    }

    private void replaceQueuedChainWithRecoveryLocked(AccessUnit accessUnit) {
        accessUnits.clear();
        accessUnits.addLast(accessUnit);
        recoveryNeeded = false;
        recoveryRequestReported = false;
        configuredWidth = accessUnit.width;
        configuredHeight = accessUnit.height;
    }

    private boolean markRecoveryRequestLocked() {
        if (recoveryRequestReported) {
            return false;
        }

        recoveryRequestReported = true;
        return true;
    }

    private void reportDecoderUnavailable(String reason) {
        synchronized (stateLock) {
            if (unavailableReported || closed) {
                return;
            }
            unavailableReported = true;
        }

        listener.onDecoderUnavailable(reason);
    }

    private void reportActualDecoderCandidate() {
        synchronized (stateLock) {
            if (closed || activeCandidateReported || activeCandidate == null ||
                activeCodecGeneration != generation ||
                activeCodecSurfaceGeneration != surfaceGeneration) {
                return;
            }

            activeCandidateReported = true;
            // Completion, not just the flag, must precede the render/probe
            // notification on the other thread.
            AndroidH264DecoderDiagnostics.recordSelectedDecoder(activeCandidate);
            listener.onDecoderStarted(activeCandidate);
        }
    }

    private void releaseCodec() {
        MediaCodec activeCodec;
        synchronized (stateLock) {
            activeCodec = codec;
            codec = null;
            if (frameRenderedCodec == activeCodec) {
                frameRenderedCodec = null;
            }
            activeCandidate = null;
            activeCandidateReported = false;
            activeCandidateStartedAtNanos = 0L;
            activeCodecGeneration = -1;
            activeCodecSurfaceGeneration = -1;
            surfaceProbePending = false;
            surfaceBufferConfirmed = false;
        }
        consecutiveAccessUnitsWithoutOutput = 0;
        consecutiveInputDequeueMisses = 0;
        nextPresentationTimeUs = 0;
        if (activeCodec == null) {
            return;
        }

        try {
            activeCodec.stop();
        } catch (RuntimeException ignored) {
        }
        try {
            activeCodec.release();
        } catch (RuntimeException ignored) {
        }
    }

    private static void releaseCandidateCodec(MediaCodec candidateCodec) {
        if (candidateCodec == null) {
            return;
        }

        try {
            candidateCodec.stop();
        } catch (RuntimeException ignored) {
        }
        try {
            candidateCodec.release();
        } catch (RuntimeException ignored) {
        }
    }

    private static final class AccessUnit {
        final int width;
        final int height;
        final int flags;
        final byte[] bytes;
        final int generation;

        AccessUnit(int width, int height, int flags, byte[] bytes, int generation) {
            this.width = width;
            this.height = height;
            this.flags = flags;
            this.bytes = bytes;
            this.generation = generation;
        }
    }

    private static final class WorkItem {
        final boolean reset;
        final AccessUnit accessUnit;

        WorkItem(boolean reset, AccessUnit accessUnit) {
            this.reset = reset;
            this.accessUnit = accessUnit;
        }
    }
}
