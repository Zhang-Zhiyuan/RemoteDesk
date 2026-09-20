package com.remotedesk.agent;

import android.content.Context;
import android.os.Process;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.security.GeneralSecurityException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Set;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

final class RemoteDeskHostServer {
    private static final int JPEG_QUALITY = 72;
    private static final int HIGH_QUALITY_JPEG = 85;
    private static final int HIGH_QUALITY_JPEG_FLOOR = 70;
    private static final int MAX_STREAM_EDGE = 1600;
    private static final int INPUT_RECEIVE_BUFFER_BYTES = 32 * 1024;
    private static final int AUTHENTICATION_TIMEOUT_MILLIS = 10_000;
    private static final int AUTHENTICATION_FAILURE_DELAY_MILLIS = 250;
    private static final int MAX_PENDING_AUTHENTICATIONS = 4;
    private static final int BLOCKING_READ_TIMEOUT_MILLIS = 0;
    private static final int SESSION_LIVENESS_POLL_MILLIS = 1_000;
    private static final long SESSION_REPLACEMENT_WRITE_TIMEOUT_MILLIS = 1_000L;
    private static final long SESSION_REPLACEMENT_DRAIN_TIMEOUT_MILLIS = 2_000L;
    private static final String SESSION_REPLACED_MESSAGE =
        "此连接已被另一台查看端接管；已停止自动重连。";

    private final ExecutorService executor;
    private final ListenerFactory listenerFactory;
    private final Context appContext;
    private final AndroidScreenCaptureSession captureSession;
    private final AtomicBoolean running = new AtomicBoolean();
    private final AtomicLong nextSessionGeneration = new AtomicLong();
    private final ClientAdmissionGate<Socket> clientGate =
        new ClientAdmissionGate<>(MAX_PENDING_AUTHENTICATIONS);

    private volatile ServerSocket serverSocket;
    private AndroidHostSessionState activeSessionState;
    private long activeSessionGeneration;
    private AndroidFileTransferReceiver activeFileTransferReceiver;
    private volatile String password;
    private volatile Runnable sessionChanged = () -> {};

    void setSessionChangedListener(Runnable listener) {
        sessionChanged = listener == null ? () -> {} : listener;
    }

    synchronized boolean hasAuthenticatedSession() {
        return running.get() && activeSessionState != null;
    }

    private void notifySessionChanged() {
        try { sessionChanged.run(); }
        catch (RuntimeException ex) { AndroidSessionLog.error("Session power update failed.", ex); }
    }

    RemoteDeskHostServer(Context context, AndroidScreenCaptureSession captureSession) {
        this(
            context.getApplicationContext(),
            captureSession,
            Executors.newCachedThreadPool(),
            ServerSocket::new);
    }

    RemoteDeskHostServer(
        Context appContext,
        AndroidScreenCaptureSession captureSession,
        ExecutorService executor,
        ListenerFactory listenerFactory) {
        this.appContext = appContext;
        this.captureSession = captureSession;
        this.executor = executor;
        this.listenerFactory = listenerFactory;
    }

    synchronized void start(String hostPassword) throws IOException {
        if (running.get()) {
            password = hostPassword;
            return;
        }

        ServerSocket socket = null;
        boolean gateStarted = false;
        try {
            password = hostPassword;
            socket = listenerFactory.create();
            socket.setReuseAddress(true);
            socket.bind(new InetSocketAddress(
                InetAddress.getByName("0.0.0.0"), RemoteDeskProtocol.HOST_PORT), 8);
            long listenerGeneration = clientGate.start();
            gateStarted = true;
            serverSocket = socket;
            running.set(true);
            ServerSocket scheduledSocket = socket;
            executor.execute(() -> acceptLoop(scheduledSocket, listenerGeneration));
        } catch (IOException | RuntimeException ex) {
            running.set(false);
            if (serverSocket == socket) {
                serverSocket = null;
            }
            if (gateStarted) {
                for (Socket client : clientGate.stopAndDrain()) {
                    closeQuietly(client);
                }
            }
            closeQuietly(socket);
            if (ex instanceof IOException) {
                throw (IOException) ex;
            }
            throw new IOException("Could not schedule the Android host accept loop.", ex);
        }
        AndroidSessionLog.info("Host listening on TCP " + RemoteDeskProtocol.HOST_PORT + ".");
    }

    synchronized void stop() {
        running.set(false);
        ServerSocket listener = serverSocket;
        serverSocket = null;
        closeQuietly(listener);
        for (Socket client : clientGate.stopAndDrain()) {
            closeQuietly(client);
        }

        AndroidHostSessionState sessionState = activeSessionState;
        activeSessionState = null;
        activeSessionGeneration = 0L;
        if (sessionState != null) {
            stopSessionState(sessionState);
        }

        activeFileTransferReceiver = null;
        notifySessionChanged();
        // The active session worker owns file cleanup. Its socket was just
        // closed above, so its finally block will close/delete any partial
        // transfer off the service main thread.
    }

    synchronized void shutdown() {
        stop();
        executor.shutdownNow();
    }

    boolean isRunning() {
        return running.get() && serverSocket != null && !serverSocket.isClosed();
    }

    private void acceptLoop(ServerSocket listener, long listenerGeneration) {
        while (running.get() && serverSocket == listener) {
            try {
                Socket socket = listener.accept();
                boolean registered;
                synchronized (this) {
                    registered = running.get() &&
                        serverSocket == listener &&
                        clientGate.tryRegisterPending(socket, listenerGeneration);
                }
                if (!registered) {
                    AndroidSessionLog.info("Rejected client because the authentication queue is full or stopping: " +
                        socket.getRemoteSocketAddress());
                    closeQuietly(socket);
                    continue;
                }

                AndroidSessionLog.info("Accepted viewer authentication from " +
                    socket.getRemoteSocketAddress() + ".");
                try {
                    executor.execute(() -> handleClient(socket));
                } catch (RuntimeException ex) {
                    clientGate.authenticationEnded(socket);
                    closeQuietly(socket);
                    if (running.get()) {
                        AndroidSessionLog.error("Could not schedule viewer authentication.", ex);
                    }
                }
            } catch (IOException ignored) {
                if (running.get() && serverSocket == listener) {
                    AndroidSessionLog.error("Accept loop failed while host is running.", ignored);
                    sleep(500);
                }
            }
        }
    }

    private void handleClient(Socket socket) {
        Object writeLock = new Object();
        AndroidHostSessionState state = null;
        AndroidFileTransferReceiver fileTransferReceiver = null;
        AndroidFileCompletionCoordinator fileCompletionCoordinator = null;
        Future<?> inputFuture = null;
        Future<?> watchdogFuture = null;
        boolean activeOwner = false;
        long sessionGeneration = 0L;
        CountDownLatch sessionClosed = new CountDownLatch(1);

        try (Socket client = socket;
             InputStream input = client.getInputStream();
             OutputStream output = client.getOutputStream()) {
            configureClientSocketForAuthentication(client);

            RemoteDeskTransport.AuthenticationResult authentication =
                RemoteDeskTransport.authenticateServerDetailed(
                    input,
                    output,
                    password,
                    new AuthenticationDeadline(
                        System.nanoTime() +
                            TimeUnit.MILLISECONDS.toNanos(AUTHENTICATION_TIMEOUT_MILLIS))
                        .forSocket(client));
            if (authentication.session == null) {
                AndroidSessionLog.info(authentication.incomplete
                    ? "Viewer disconnected during authentication."
                    : "Viewer authentication failed.");
                if (!authentication.incomplete) {
                    sleep(AUTHENTICATION_FAILURE_DELAY_MILLIS);
                }
                return;
            }

            RemoteDeskTransport.SecureSession session = authentication.session;
            AndroidHostSessionState sessionState = new AndroidHostSessionState();
            ClientAdmissionGate.Activation<Socket> activation =
                clientGate.activateLatest(
                    client,
                    () -> replaceAuthenticatedViewer(
                        client,
                        output,
                        session,
                        writeLock,
                        sessionState,
                        sessionClosed));
            if (activation.result !=
                ClientAdmissionGate.ActivationResult.ACTIVATED) {
                return;
            }

            activeOwner = true;
            if (activation.replacedClient != null) {
                AndroidSessionLog.info(
                    "New authenticated viewer is replacing the active session: " +
                        client.getRemoteSocketAddress() + ".");
                activation.replacePrevious();
            }
            AndroidSessionLivenessTracker inboundLiveness =
                new AndroidSessionLivenessTracker();
            AndroidFileTransferReceiver sessionFileTransferReceiver =
                new AndroidFileTransferReceiver(appContext);
            long activatedSessionGeneration = nextSessionGeneration();
            state = sessionState;
            fileTransferReceiver = sessionFileTransferReceiver;
            sessionGeneration = activatedSessionGeneration;
            synchronized (this) {
                if (!running.get() || !clientGate.isActive(client)) {
                    return;
                }
                activeSessionState = sessionState;
                activeSessionGeneration = activatedSessionGeneration;
                activeFileTransferReceiver = sessionFileTransferReceiver;
            }
            notifySessionChanged();

            AndroidFileCompletionCoordinator sessionFileCompletionCoordinator =
                createFileCompletionCoordinator(
                    activatedSessionGeneration,
                    sessionState,
                    client,
                    output,
                    session,
                    writeLock);
            fileCompletionCoordinator = sessionFileCompletionCoordinator;

            configureAuthenticatedClientSocket(client);
            AndroidSessionLog.info("Viewer authenticated: " + client.getRemoteSocketAddress() + ".");
            RemoteDeskAccessibilityService.requestRemoteWake(() ->
                isCurrentSessionOwner(activatedSessionGeneration, sessionState, client));

            int capabilities = getCapabilities();
            RemoteDeskTransport.writeMessage(
                output,
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeDeviceInfo(AndroidDeviceNames.displayName(), capabilities),
                session,
                writeLock);
            RemoteDeskTransport.writeMessage(
                output,
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeCaptureTargetList(),
                session,
                writeLock);
            RemoteDeskTransport.writeMessage(
                output,
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeCaptureTargetChanged(),
                session,
                writeLock);

            inputFuture = executor.submit(() -> runInputLoop(
                input,
                output,
                session,
                writeLock,
                sessionState,
                client,
                sessionFileTransferReceiver,
                sessionFileCompletionCoordinator,
                inboundLiveness));
            watchdogFuture = executor.submit(() -> runInboundLivenessWatchdog(
                inboundLiveness,
                sessionState,
                client));
            runCaptureLoop(output, session, writeLock, sessionState);
        } catch (Exception ignored) {
            if (state == null || state.running.get()) {
                AndroidSessionLog.error("Viewer session ended with error.", ignored);
            } else {
                AndroidSessionLog.info("Viewer session stopped or replaced.");
            }
        } finally {
            clientGate.authenticationEnded(socket);
            if (state != null) {
                stopSessionState(state);
            }
            closeQuietly(socket);
            if (fileCompletionCoordinator != null) {
                fileCompletionCoordinator.close();
            }
            if (fileTransferReceiver != null) {
                fileTransferReceiver.abortActiveTransfer();
            }
            if (fileCompletionCoordinator != null &&
                !fileCompletionCoordinator.awaitStopped(1_500L)) {
                AndroidSessionLog.info(
                    "Android file finalizer did not stop within 1500 ms; " +
                        "its generation remains fenced and interrupted.");
            }
            waitForSessionWorker(inputFuture);
            waitForSessionWorker(watchdogFuture);
            synchronized (this) {
                if (activeOwner) {
                    clientGate.releaseActive(socket);
                }
                if (state != null &&
                    activeSessionState == state &&
                    activeSessionGeneration == sessionGeneration) {
                    activeSessionState = null;
                    activeSessionGeneration = 0L;
                }
                if (fileTransferReceiver != null &&
                    activeFileTransferReceiver == fileTransferReceiver) {
                    activeFileTransferReceiver = null;
                }
            }

            if (state != null) {
                state.finishGestureTeardown();
                state.closeLowLatencyVideo();
            }
            notifySessionChanged();
            AndroidSessionLog.info(activeOwner
                ? "Viewer session closed."
                : "Viewer authentication connection closed.");
            sessionClosed.countDown();
        }
    }

    private static void waitForSessionWorker(Future<?> worker) {
        if (worker == null) {
            return;
        }
        try {
            worker.get(1_500L, TimeUnit.MILLISECONDS);
        } catch (Exception ignored) {
            worker.cancel(true);
        }
    }

    private static void replaceAuthenticatedViewer(
        Socket socket,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state,
        CountDownLatch sessionClosed) {
        stopSessionState(state);
        Thread notification = new Thread(
            () -> {
                try {
                    RemoteDeskTransport.writeMessage(
                        output,
                        RemoteDeskProtocol.MESSAGE_CONTROL,
                        RemoteDeskTransport.encodeSessionRejected(
                            SESSION_REPLACED_MESSAGE),
                        session,
                        writeLock);
                } catch (IOException | GeneralSecurityException ignored) {
                }
            },
            "RemoteDeskViewerReplacement");
        notification.setDaemon(true);
        notification.start();
        try {
            notification.join(SESSION_REPLACEMENT_WRITE_TIMEOUT_MILLIS);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        } finally {
            closeQuietly(socket);
        }
        try {
            sessionClosed.await(
                SESSION_REPLACEMENT_DRAIN_TIMEOUT_MILLIS,
                TimeUnit.MILLISECONDS);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
    }

    private AndroidFileCompletionCoordinator createFileCompletionCoordinator(
        long generation,
        AndroidHostSessionState state,
        Socket socket,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock) {
        return new AndroidFileCompletionCoordinator(
            generation,
            new AndroidFileCompletionCoordinator.Owner() {
                @Override
                public boolean isCurrent(long expectedGeneration) {
                    return isCurrentSessionOwner(
                        expectedGeneration,
                        state,
                        socket);
                }

                @Override
                public void publish(
                    long expectedGeneration,
                    String message,
                    Throwable failure) {
                    publishTransfer(expectedGeneration, null, message, failure);
                }

                @Override
                public void publishTransfer(long expectedGeneration, String transferId, String message, Throwable failure) {
                    publishFileCompletion(
                        expectedGeneration,
                        state,
                        socket,
                        output,
                        session,
                        writeLock,
                        transferId,
                        message,
                        failure);
                }
            });
    }

    private synchronized boolean isCurrentSessionOwner(
        long generation,
        AndroidHostSessionState state,
        Socket socket) {
        return generation != 0L &&
            running.get() &&
            state != null &&
            state.running.get() &&
            activeSessionGeneration == generation &&
            activeSessionState == state &&
            clientGate.isActive(socket);
    }

    private void publishFileCompletion(
        long generation,
        AndroidHostSessionState state,
        Socket socket,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        String transferId,
        String message,
        Throwable failure) {
        if (!isCurrentSessionOwner(generation, state, socket)) {
            return;
        }

        boolean success = failure == null;
        String statusMessage = success
            ? message == null || message.trim().isEmpty()
                ? "Android 文件已完成"
                : message
            : "Android 文件接收失败：" +
                MainActivity.formatExceptionMessage(failure);
        if (failure != null) {
            AndroidSessionLog.error(
                "Android file receive failed while completing.",
                failure);
        } else {
            AndroidSessionLog.info(statusMessage);
        }

        try {
            if (!isCurrentSessionOwner(generation, state, socket)) {
                return;
            }
            sendFileReceipt(
                output,
                session,
                writeLock,
                transferId,
                success,
                statusMessage);
        } catch (IOException | GeneralSecurityException ex) {
            if (isCurrentSessionOwner(generation, state, socket)) {
                AndroidSessionLog.error(
                    "Android file completion status write failed.",
                    ex);
                state.tryStop();
                closeQuietly(socket);
            }
        }
    }

    private long nextSessionGeneration() {
        return nextSessionGeneration.updateAndGet(
            current -> current == Long.MAX_VALUE ? 1L : current + 1L);
    }

    private static void stopSessionState(AndroidHostSessionState state) {
        // May be called from service/projection callbacks on the Android main
        // thread. Only signal here; the session owner closes UDP after its
        // input/watchdog workers have left.
        AndroidHostStopPolicy.signalFromMainThread(
            new AndroidHostStopPolicy.Session() {
                @Override
                public void signalStopped() {
                    state.tryStop();
                }

                @Override
                public void signalUdpClose() {
                    state.signalLowLatencyVideoClose();
                }

                @Override
                public void closeWorkers() {
                    state.closeLowLatencyVideo();
                }
            });
    }

    private boolean applySessionInput(
        AndroidHostSessionState state,
        byte[] payload) {
        synchronized (state) {
            if (!running.get() || !state.running.get()) {
                return false;
            }
            boolean enteringUnlockPin = RemoteDeskAccessibilityService.isEnteringUnlockPin();
            boolean screenOff = !enteringUnlockPin && AndroidRemoteUnlock.isScreenOff(appContext);
            if (state.blocksInputForScreenState(screenOff, enteringUnlockPin)) {
                if (!enteringUnlockPin)
                    RemoteDeskAccessibilityService.requestRemoteWake(() -> running.get() && state.running.get());
                return false;
            }
            return AndroidInputInjector.apply(
                payload,
                state.lastFrameWidth.get(),
                state.lastFrameHeight.get(),
                captureSession.getSourceWidth(),
                captureSession.getSourceHeight(),
                state.gestureState,
                () -> running.get() && state.running.get());
        }
    }

    private void runCaptureLoop(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state) throws IOException, GeneralSecurityException {
        waitForViewerInfo(state);
        Set<String> rejectedH264Codecs = new HashSet<>();
        boolean allowH264Upgrade = !captureSession.isAccessibilityCapture();
        while (running.get() && state.running.get()) {
            if (shouldStartH264(allowH264Upgrade, state.viewerVideoCodecs.get())) {
                H264CaptureResult result = runH264CaptureLoopIfAvailable(
                    output,
                    session,
                    writeLock,
                    state,
                    rejectedH264Codecs);
                if (result == H264CaptureResult.Completed) {
                    return;
                }

                if (result == H264CaptureResult.Restart) {
                    state.keyFrameRequested.set(false);
                    continue;
                }

                // A real encoder failure or viewer-requested fallback stays on
                // JPEG for this session; never retry a broken codec every frame.
                allowH264Upgrade = false;
            }

            AndroidSessionLog.info("Starting JPEG screen stream.");
            runJpegCaptureLoop(output, session, writeLock, state, allowH264Upgrade);
        }
    }

    static boolean shouldStartH264(boolean allowH264Upgrade, int viewerVideoCodecs) {
        return allowH264Upgrade &&
            (viewerVideoCodecs & RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B) != 0;
    }

    private void runJpegCaptureLoop(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state,
        boolean allowH264Upgrade) throws IOException, GeneralSecurityException {
        AdaptiveCaptureController adaptive = new AdaptiveCaptureController(
            state.viewerCapabilities.get());
        while (running.get() && state.running.get()) {
            // The bounded startup wait is not a codec-selection deadline. Relay
            // negotiation can arrive after JPEG starts; switch the existing
            // projection Surface instead of remaining on JPEG until reconnect.
            int viewerVideoCodecs = state.viewerVideoCodecs.get();
            if (shouldStartH264(allowH264Upgrade, viewerVideoCodecs)) {
                AndroidSessionLog.info("Late viewer H.264 negotiation received; upgrading JPEG stream in this session.");
                return;
            }
            if ((viewerVideoCodecs & RemoteDeskProtocol.VIDEO_CODEC_JPEG) == 0) {
                throw new IOException("No mutually supported RemoteDesk video codec is available.");
            }
            long startedAt = System.nanoTime();
            long displayGeneration =
                captureSession.getDisplayConfigurationGeneration();
            AndroidScreenCaptureSession.ScreenFrame frame =
                captureSession.captureJpeg(appContext, adaptive.currentQuality, adaptive.currentMaxEdge);
            if (displayGeneration !=
                captureSession.getDisplayConfigurationGeneration()) {
                synchronized (state) {
                    state.gestureState.reset();
                }
            }
            if (frame != null) {
                state.lastFrameWidth.set(frame.width);
                state.lastFrameHeight.set(frame.height);
                long sendStartedAt = System.nanoTime();
                AndroidLowLatencyVideoTransport.Host udp = state.lowLatencyVideo;
                boolean sentUdp = udp != null && udp.offerFrame(
                    RemoteDeskProtocol.MESSAGE_FRAME,
                    LowLatencyVideoProtocol.createJpegFramePayload(
                        frame.width,
                        frame.height,
                        frame.captureMillis,
                        frame.encodeMillis,
                        Arrays.copyOf(frame.jpegBytes, frame.jpegLength)));
                if (!sentUdp) {
                    RemoteDeskTransport.writeFrame(output, frame, session, writeLock);
                }
                adaptive.recordFrame(frame, nanosToMillis(System.nanoTime() - sendStartedAt));
            }

            long elapsed = System.nanoTime() - startedAt;
            adaptive.updateIfNeeded();
            long frameIntervalNanos = TimeUnit.SECONDS.toNanos(1) / adaptive.currentFps;
            long remainingMillis = (frameIntervalNanos - elapsed) / 1_000_000L;
            if (remainingMillis > 0) {
                sleep(remainingMillis);
            } else {
                Thread.yield();
            }
        }
    }

    private H264CaptureResult runH264CaptureLoopIfAvailable(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state,
        Set<String> rejectedH264Codecs) throws IOException, GeneralSecurityException {
        preferDisplayThreadPriority();
        captureSession.refreshDisplayConfigurationIfChanged(appContext, true);
        AndroidH264ScreenEncoder encoder =
            new AndroidH264ScreenEncoder(captureSession, rejectedH264Codecs);
        int requestedFramesPerSecond =
            AndroidH264CapabilityPolicy.targetFramesPerSecond(
                state.viewerCapabilities.get(),
                AndroidVideoCodecDiagnostics.cachedH264Report());
        boolean started;
        try {
            started = encoder.start(requestedFramesPerSecond);
        } catch (IOException | RuntimeException ex) {
            AndroidSessionLog.error("H.264 encoder failed to start; falling back if viewer supports JPEG.", ex);
            encoder.close();
            return H264CaptureResult.Fallback;
        }

        if (!started) {
            AndroidSessionLog.info("H.264 encoder unavailable; falling back if viewer supports JPEG.");
            encoder.close();
            return H264CaptureResult.Fallback;
        }

        AndroidSessionLog.info("Starting H.264 screen stream.");
        try (AndroidH264ScreenEncoder activeEncoder = encoder) {
            AndroidSessionLog.info(
                "H.264 frame-rate negotiation: requested=" +
                requestedFramesPerSecond +
                ", actual=" + activeEncoder.getCurrentFps() +
                ", viewerCapabilities=" + state.viewerCapabilities.get() + ".");
            AdaptiveH264BitrateController adaptive = new AdaptiveH264BitrateController(
                activeEncoder.getCurrentBitrate(),
                activeEncoder.getCurrentFps());
            AndroidH264OutputWatchdog outputWatchdog =
                new AndroidH264OutputWatchdog(System.nanoTime());
            long nextUdpBitrateReductionAtNanos = 0L;
            try {
                while (running.get() && state.running.get()) {
                    if ((state.viewerVideoCodecs.get() & RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B) == 0) {
                        return H264CaptureResult.Fallback;
                    }

                    int desiredFramesPerSecond =
                        AndroidH264CapabilityPolicy
                            .targetFramesPerSecondForActiveEncoder(
                            state.viewerCapabilities.get(),
                            activeEncoder.supportsHighFrameRate());
                    if (desiredFramesPerSecond != activeEncoder.getCurrentFps()) {
                        AndroidSessionLog.info(
                            "Viewer H.264 frame-rate capability changed; restarting stream " +
                            activeEncoder.getCurrentFps() + " -> " +
                            desiredFramesPerSecond + " fps.");
                        return H264CaptureResult.Restart;
                    }

                    if (captureSession.refreshDisplayConfigurationIfChanged(appContext)) {
                        synchronized (state) {
                            state.gestureState.reset();
                        }
                        AndroidSessionLog.info("Display configuration changed; restarting H.264 stream.");
                        return H264CaptureResult.Restart;
                    }

                    if (state.keyFrameRequested.getAndSet(false)) {
                        activeEncoder.requestKeyFrame();
                    }

                    AndroidH264ScreenEncoder.VideoFrame frame = activeEncoder.dequeueFrame(
                        AndroidVideoStreamSettings.H264_OUTPUT_POLL_MILLIS);
                    if (frame == null) {
                        long now = System.nanoTime();
                        AndroidH264OutputWatchdog.Action watchdogAction =
                            outputWatchdog.evaluate(now);
                        if (watchdogAction == AndroidH264OutputWatchdog.Action.RequestKeyFrame) {
                            AndroidSessionLog.info(formatH264WatchdogProbe(
                                outputWatchdog.hasProducedOutput(),
                                outputWatchdog.silenceMillis(now)));
                            activeEncoder.requestKeyFrame();
                        } else if (watchdogAction ==
                                AndroidH264OutputWatchdog.Action.StaticSilence) {
                            AndroidSessionLog.info(formatH264WatchdogStaticSilence(
                                outputWatchdog.silenceMillis(now)));
                        } else if (watchdogAction == AndroidH264OutputWatchdog.Action.Fallback) {
                            AndroidSessionLog.info(formatH264WatchdogFallback(
                                outputWatchdog.hasProducedOutput(),
                                outputWatchdog.silenceMillis(now),
                                viewerSupportsJpeg(state)));
                            if (rejectSelectedH264Codec(activeEncoder, rejectedH264Codecs)) {
                                AndroidSessionLog.info(
                                    "Trying the next H.264 encoder candidate after a runtime stall.");
                                return H264CaptureResult.Restart;
                            }
                            return H264CaptureResult.Fallback;
                        }

                        continue;
                    }

                    boolean firstOutput = outputWatchdog.recordOutput(System.nanoTime());
                    if (firstOutput) {
                        AndroidSessionLog.info(
                            "H.264 encoder produced its first video frame; output watchdog is healthy.");
                    }

                    state.lastFrameWidth.set(frame.width);
                    state.lastFrameHeight.set(frame.height);
                    AndroidLowLatencyVideoTransport.Host udp = state.lowLatencyVideo;
                    boolean sentUdp = udp != null && udp.offerFrame(
                        RemoteDeskProtocol.MESSAGE_VIDEO_FRAME,
                        LowLatencyVideoProtocol.createVideoFramePayload(
                            frame.width,
                            frame.height,
                            RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B,
                            frame.flags,
                            frame.captureMillis,
                            frame.encodeMillis,
                            frame.bytes,
                            frame.length));
                    if (!sentUdp) {
                        double socketWriteMillis = RemoteDeskTransport.writeVideoFrame(output, frame, session, writeLock);
                        adaptive.recordFrame(frame.length, socketWriteMillis);
                    }
                    // UDP already has explicit receiver pressure feedback. Its
                    // enqueue time cannot be used as TCP bandwidth evidence.
                    if (sentUdp) adaptive.reset(System.nanoTime());
                    int adaptiveRequestedBitrate = sentUdp ? 0 : adaptive.updateIfNeeded();
                    int currentBitrate = activeEncoder.getCurrentBitrate();
                    int udpTargetBitrate = state.udpTargetBitrate.get();
                    long bitrateDecisionAtNanos = System.nanoTime();
                    int rateLimitedUdpCeiling =
                        AndroidH264CapabilityPolicy.rateLimitedNetworkCeiling(
                            currentBitrate,
                            udpTargetBitrate,
                            bitrateDecisionAtNanos,
                            nextUdpBitrateReductionAtNanos);
                    int requestedBitrate =
                        AndroidH264CapabilityPolicy.mergeRequestedBitrate(
                            currentBitrate,
                            adaptiveRequestedBitrate,
                            rateLimitedUdpCeiling);
                    // Some vendor codecs reject dynamic bitrate changes. Rate
                    // limit failed attempts as well as successful changes so
                    // a persistent MediaCodec refusal cannot turn into a
                    // per-frame Binder call loop.
                    nextUdpBitrateReductionAtNanos =
                        AndroidH264CapabilityPolicy.recordNetworkReductionAttempt(
                            currentBitrate,
                            requestedBitrate,
                            rateLimitedUdpCeiling,
                            bitrateDecisionAtNanos,
                            nextUdpBitrateReductionAtNanos);
                    if (requestedBitrate > 0 && activeEncoder.setBitrate(requestedBitrate)) {
                        int previousBitrate = adaptive.getCurrentBitrate();
                        adaptive.acceptBitrate(activeEncoder.getCurrentBitrate());
                        AndroidSessionLog.info("H.264 bitrate adjusted: " +
                            formatBitsPerSecond(previousBitrate) + " -> " +
                            formatBitsPerSecond(activeEncoder.getCurrentBitrate()) + ".");
                        activeEncoder.requestKeyFrame();
                    }
                }
            } catch (RuntimeException ex) {
                AndroidSessionLog.error("H.264 streaming failed; falling back if viewer supports JPEG.", ex);
                if (rejectSelectedH264Codec(activeEncoder, rejectedH264Codecs)) {
                    AndroidSessionLog.info(
                        "Trying the next H.264 encoder candidate after a runtime failure.");
                    return H264CaptureResult.Restart;
                }
                return H264CaptureResult.Fallback;
            }
        }

        return H264CaptureResult.Completed;
    }

    private static boolean rejectSelectedH264Codec(
        AndroidH264ScreenEncoder encoder,
        Set<String> rejectedH264Codecs) {
        String codecName = encoder.getSelectedCodecName();
        return codecName != null &&
            !codecName.isEmpty() &&
            rejectedH264Codecs.add(codecName);
    }

    private static boolean viewerSupportsJpeg(AndroidHostSessionState state) {
        return (state.viewerVideoCodecs.get() & RemoteDeskProtocol.VIDEO_CODEC_JPEG) != 0;
    }

    static String formatH264WatchdogProbe(boolean producedOutput, long silenceMillis) {
        String phase = producedOutput
            ? "since the last encoded frame"
            : "while waiting for the first encoded frame";
        return "H.264 output watchdog observed " + silenceMillis + " ms of silence " +
            phase + "; requesting a sync frame before fallback.";
    }

    static String formatH264WatchdogFallback(
        boolean producedOutput,
        long silenceMillis,
        boolean jpegSupported) {
        String phase = producedOutput
            ? "after the last encoded frame"
            : "without producing the first encoded frame";
        String action = jpegSupported
            ? "Falling back to JPEG."
            : "The viewer negotiated H.264 only, so the session will disconnect.";
        return "H.264 output watchdog timed out after " + silenceMillis + " ms " +
            phase + ". " + action;
    }

    static String formatH264WatchdogStaticSilence(long silenceMillis) {
        return "H.264 encoder has been quiet for " + silenceMillis +
            " ms after valid output; preserving the stream because a static " +
            "Android display may legitimately produce no changed buffers.";
    }

    private void runInputLoop(
        InputStream input,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state,
        Socket socket,
        AndroidFileTransferReceiver fileTransferReceiver,
        AndroidFileCompletionCoordinator fileCompletionCoordinator,
        AndroidSessionLivenessTracker inboundLiveness) {
        AndroidClipboardSnapshotQueue clipboardSnapshots = new AndroidClipboardSnapshotQueue(
            executor, () -> running.get() && state.running.get() && !socket.isClosed(),
            () -> { state.tryStop(); closeQuietly(socket); });
        AndroidHostHeartbeatResponder heartbeat = new AndroidHostHeartbeatResponder(
            () -> {
                if (running.get() && state.running.get()) {
                    RemoteDeskTransport.writeMessage(
                        output, RemoteDeskProtocol.MESSAGE_PONG, new byte[0], session, writeLock);
                }
            },
            () -> {
                state.tryStop();
                closeQuietly(socket);
            });
        try {
            while (running.get() && state.running.get()) {
                long readGeneration = inboundLiveness.beginInboundRead(System.nanoTime());
                RemoteDeskTransport.ProtocolMessage message;
                try {
                    message = RemoteDeskTransport.readMessage(input, session);
                } finally {
                    inboundLiveness.endInboundRead(readGeneration);
                }
                if (message.messageType == RemoteDeskProtocol.MESSAGE_PING) {
                    heartbeat.request();
                } else if (message.messageType == RemoteDeskProtocol.MESSAGE_INPUT) {
                    applySessionInput(state, message.payload);
                } else if (message.messageType == RemoteDeskProtocol.MESSAGE_CONTROL) {
                    handleControlMessage(
                        message.payload,
                        output,
                        session,
                        writeLock,
                        fileTransferReceiver,
                        fileCompletionCoordinator,
                        clipboardSnapshots,
                        state,
                        socket);
                }
            }
        } catch (Exception ignored) {
            AndroidSessionLog.error("Input/control loop stopped.", ignored);
        } finally {
            state.tryStop();
            closeQuietly(socket);
            clipboardSnapshots.close();
            heartbeat.close();
            fileCompletionCoordinator.close();
            fileTransferReceiver.abortActiveTransfer();
        }
    }

    private void runInboundLivenessWatchdog(
        AndroidSessionLivenessTracker inboundLiveness,
        AndroidHostSessionState state,
        Socket socket) {
        while (running.get() && state.running.get()) {
            sleep(SESSION_LIVENESS_POLL_MILLIS);
            if (!inboundLiveness.hasTimedOut(System.nanoTime())) {
                continue;
            }

            AndroidSessionLog.info(
                "Viewer sent no complete authenticated TCP message for 30 seconds; closing stale session.");
            state.tryStop();
            closeQuietly(socket);
            return;
        }
    }

    private void handleControlMessage(
        byte[] payload,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver,
        AndroidFileCompletionCoordinator fileCompletionCoordinator,
        AndroidClipboardSnapshotQueue clipboardSnapshots,
        AndroidHostSessionState state,
        Socket sessionSocket) throws IOException, GeneralSecurityException {
        RemoteDeskTransport.ControlMessage control = RemoteDeskTransport.decodeControl(payload);
        boolean receipts = (state.viewerCapabilities.get() & RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_RECEIPT) != 0;
        switch (control.kind) {
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_SNAPSHOT_REQUEST:
                // Never emit a new control kind without negotiated clipboard access.
                if (canReadClipboardSnapshot(state, sessionSocket) &&
                    !clipboardSnapshots.offer(() -> sendClipboardSnapshot(
                        control.clipboardSnapshot, output, session, writeLock, state, sessionSocket))) {
                    throw new IOException("Clipboard snapshot queue is unavailable or full.");
                }
                break;
            case RemoteDeskProtocol.CONTROL_FILE_RECEIVE_LOCATION_REQUEST:
                String directory = "", note;
                boolean locationAvailable;
                try {
                    directory = fileTransferReceiver.getAdvertisedReceiveDirectory();
                    note = fileTransferReceiver.getReceiveLocationNote();
                    locationAvailable = true;
                } catch (RuntimeException ex) {
                    note = "无法读取手机接收目录，请检查存储状态。";
                    locationAvailable = false;
                }
                RemoteDeskTransport.writeMessage(output, RemoteDeskProtocol.MESSAGE_CONTROL,
                    RemoteDeskTransport.encodeFileReceiveLocation(control.transferId, locationAvailable, directory, note), session, writeLock);
                break;
            case RemoteDeskProtocol.CONTROL_DEVICE_IDENTITY_REQUEST:
                RemoteDeskTransport.writeMessage(output, RemoteDeskProtocol.MESSAGE_CONTROL,
                    RemoteDeskTransport.encodeDeviceIdentity(AndroidRelaySettings.localDeviceId(appContext)), session, writeLock);
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_GET_TEXT:
                sendClipboardText(output, session, writeLock);
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT:
                setClipboardText(control.text, output, session, writeLock,
                    () -> running.get() && state.running.get() && !sessionSocket.isClosed());
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START:
                handleFileTransferStart(control, output, session, writeLock, fileTransferReceiver, receipts);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK:
                handleFileTransferChunk(control, output, session, writeLock, fileTransferReceiver, receipts);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM:
                handleFileTransferChecksum(control, output, session, writeLock, fileTransferReceiver, receipts);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE:
                handleFileTransferComplete(
                    control,
                    output,
                    session,
                    writeLock,
                    fileTransferReceiver,
                    fileCompletionCoordinator, receipts);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL:
                handleFileTransferCancel(control, output, session, writeLock, fileTransferReceiver, receipts);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES:
                sendFileTransferStatus(
                    output,
                    session,
                    writeLock,
                    false,
                    formatUnsupportedFileReturnStatus());
                break;
            case RemoteDeskProtocol.CONTROL_VIEWER_INFO:
                state.viewerVideoCodecs.set(control.videoCodecs);
                state.viewerInfoReceived.set(true);
                AndroidSessionLog.info("Viewer video codecs: " + control.videoCodecs + ".");
                break;
            case RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST:
                state.keyFrameRequested.set(true);
                AndroidSessionLog.info("Viewer requested a video key frame.");
                break;
            case RemoteDeskProtocol.CONTROL_VIEWER_CAPABILITIES:
                state.viewerCapabilities.set(control.capabilities);
                state.viewerCapabilitiesReceived.set(true);
                AndroidSessionLog.info(
                    "Viewer capabilities: " + control.capabilities + ".");
                startLowLatencyVideoIfNegotiated(
                    output,
                    session,
                    writeLock,
                    state,
                    control.capabilities,
                    sessionSocket);
                break;
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY:
                AndroidLowLatencyVideoTransport.Host readyTransport = state.lowLatencyVideo;
                if (readyTransport != null) {
                    readyTransport.markReady(
                        control.lowLatencyVideoChannelId,
                        control.lowLatencyVideoEpoch);
                }
                break;
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP:
                AndroidLowLatencyVideoTransport.Host stopTransport = state.lowLatencyVideo;
                if (stopTransport != null && stopTransport.matches(
                    control.lowLatencyVideoChannelId,
                    control.lowLatencyVideoEpoch)) {
                    int stoppedReason = stopTransport.acceptStop(
                        control.lowLatencyVideoChannelId,
                        control.lowLatencyVideoEpoch,
                        control.lowLatencyVideoStopReason);
                    if (stoppedReason >= 0) {
                        RemoteDeskTransport.writeMessage(
                            output,
                            RemoteDeskProtocol.MESSAGE_CONTROL,
                            RemoteDeskTransport.encodeLowLatencyVideoStopped(
                                control.lowLatencyVideoChannelId,
                                control.lowLatencyVideoEpoch,
                                stoppedReason),
                            session,
                            writeLock);
                    }
                }
                break;
            default:
                break;
        }
    }

    private void startLowLatencyVideoIfNegotiated(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidHostSessionState state,
        int viewerCapabilities,
        Socket sessionSocket) {
        if (state.lowLatencyVideo != null ||
            (viewerCapabilities & RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO) == 0 ||
            (viewerCapabilities &
                RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK) == 0 ||
            !(sessionSocket.getRemoteSocketAddress() instanceof InetSocketAddress)) {
            return;
        }
        try {
            InetAddress peerAddress =
                ((InetSocketAddress) sessionSocket.getRemoteSocketAddress()).getAddress();
            int features = LowLatencyVideoProtocol.negotiatedFeatures(
                getCapabilities(),
                viewerCapabilities);
            AndroidLowLatencyVideoTransport.Host transport =
                new AndroidLowLatencyVideoTransport.Host(
                    peerAddress,
                    features,
                    payload -> {
                        RemoteDeskTransport.writeMessage(
                            output,
                            RemoteDeskProtocol.MESSAGE_CONTROL,
                            payload,
                            session,
                            writeLock);
                        return true;
                    },
                    reason -> {
                        AndroidSessionLog.error(reason, new IOException(reason));
                        if (state.running.get()) {
                            state.tryStop();
                            closeQuietly(sessionSocket);
                        }
                    },
                    inputPayload -> applySessionInput(state, inputPayload),
                    pressure -> state.udpTargetBitrate.set((int) Math.max(
                        1_000_000L,
                        Math.min(16_000_000L, pressure.targetBitsPerSecond))),
                    () -> {
                        state.udpTargetBitrate.set(0);
                        state.keyFrameRequested.set(true);
                    });
            state.lowLatencyVideo = transport;
            if (!state.running.get() ||
                transport.state() == AndroidLowLatencyVideoTransport.State.CLOSED) {
                if (state.lowLatencyVideo == transport) {
                    state.lowLatencyVideo = null;
                }
                transport.close();
                return;
            }
            RemoteDeskTransport.writeMessage(
                output,
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeLowLatencyVideoOffer(
                    transport.offerForTcp()),
                session,
                writeLock);
            AndroidSessionLog.info("Authenticated low-latency UDP offer sent.");
        } catch (Exception ex) {
            state.closeLowLatencyVideo();
            AndroidSessionLog.error(
                "Low-latency UDP setup failed; continuing on TCP.",
                ex);
        }
    }

    private void sendClipboardText(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock) throws IOException, GeneralSecurityException {
        try {
            String text = AndroidClipboardText.getText(appContext);
            RemoteDeskTransport.writeMessage(
                output,
                RemoteDeskProtocol.MESSAGE_CONTROL,
                RemoteDeskTransport.encodeClipboardText(text),
                session,
                writeLock);
        } catch (Exception ex) {
            AndroidSessionLog.error("Reading Android clipboard failed.", ex);
            sendClipboardStatus(output, session, writeLock, false, "读取 Android 剪贴板失败：" + ex.getMessage());
        }
    }

    private boolean canReadClipboardSnapshot(AndroidHostSessionState state, Socket socket) {
        return running.get() && state.running.get() && !socket.isClosed() &&
            state.viewerCapabilitiesReceived.get() && AndroidClipboardSnapshot.isNegotiated(
                getCapabilities(), state.viewerCapabilities.get());
    }

    private void sendClipboardSnapshot(AndroidClipboardSnapshot request, OutputStream output,
        RemoteDeskTransport.SecureSession session, Object writeLock,
        AndroidHostSessionState state, Socket socket) throws IOException, GeneralSecurityException {
        if (!canReadClipboardSnapshot(state, socket)) return;
        AndroidClipboardSnapshot snapshot;
        try {
            // Only the OS clipboard access runs on the main thread. Hashing and
            // the response write remain off both UI and authenticated read pump.
            String text = AndroidClipboardText.getText(appContext,
                () -> canReadClipboardSnapshot(state, socket));
            snapshot = AndroidClipboardSnapshot.capture(request.requestId, request.revision, text);
        } catch (Exception error) {
            snapshot = AndroidClipboardSnapshot.unavailable(request.requestId);
        }
        if (!canReadClipboardSnapshot(state, socket)) return;
        RemoteDeskTransport.writeMessage(output, RemoteDeskProtocol.MESSAGE_CONTROL,
            RemoteDeskTransport.encodeClipboardSnapshot(snapshot), session, writeLock);
    }

    private void setClipboardText(
        String text,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock, java.util.function.BooleanSupplier authorized) throws IOException, GeneralSecurityException {
        try {
            AndroidClipboardText.setText(appContext, text, authorized);
            sendClipboardStatus(output, session, writeLock, true, "已写入 Android 剪贴板");
        } catch (Exception ex) {
            AndroidSessionLog.error("Writing Android clipboard failed.", ex);
            sendClipboardStatus(output, session, writeLock, false, "写入 Android 剪贴板失败：" + ex.getMessage());
        }
    }

    private void sendClipboardStatus(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        boolean success,
        String message) throws IOException, GeneralSecurityException {
        RemoteDeskTransport.writeMessage(
            output,
            RemoteDeskProtocol.MESSAGE_CONTROL,
            RemoteDeskTransport.encodeClipboardStatus(success, message),
            session,
            writeLock);
    }

    private void handleFileTransferStart(
        RemoteDeskTransport.ControlMessage control,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver, boolean receipts) throws IOException, GeneralSecurityException {
        String message;
        try {
            message = fileTransferReceiver.start(control);
        } catch (Exception ex) {
            AndroidSessionLog.error("Android file receive failed at start.", ex);
            sendFileReceipt(output, session, writeLock, receipts ? control.transferId : null, false, "Android 文件接收失败：" + ex.getMessage());
            return;
        }

        AndroidSessionLog.info(message);
        sendFileTransferStatus(output, session, writeLock, true, message);
    }

    private void handleFileTransferChunk(
        RemoteDeskTransport.ControlMessage control,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver, boolean receipts) throws IOException, GeneralSecurityException {
        try {
            fileTransferReceiver.writeChunk(control);
        } catch (Exception ex) {
            AndroidSessionLog.error("Android file receive failed while writing chunk.", ex);
            sendFileReceipt(output, session, writeLock, receipts ? control.transferId : null, false, "Android 文件接收失败：" + ex.getMessage());
        }
    }

    private void handleFileTransferComplete(
        RemoteDeskTransport.ControlMessage control,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver,
        AndroidFileCompletionCoordinator fileCompletionCoordinator, boolean receipts)
        throws IOException, GeneralSecurityException {
        AndroidFileTransferReceiver.PreparedCompletion preparedCompletion;
        try {
            // Detach the completed transfer before returning to the read pump.
            // The next file START may legally arrive immediately after this
            // COMPLETE; only the potentially large publication remains async.
            preparedCompletion = fileTransferReceiver.prepareCompletion(control);
        } catch (Exception ex) {
            AndroidSessionLog.error(
                "Android file receive failed while preparing completion.",
                ex);
            sendFileReceipt(
                output,
                session,
                writeLock,
                receipts ? control.transferId : null,
                false,
                "Android 文件接收失败：" +
                    MainActivity.formatExceptionMessage(ex));
            return;
        }

        boolean scheduled = fileCompletionCoordinator.offer(
            receipts ? control.transferId : null,
            cancellationSignal -> fileTransferReceiver.publishCompletion(
                preparedCompletion,
                cancellationSignal::isCancelled),
            () -> fileTransferReceiver.discardCompletion(preparedCompletion));
        if (scheduled) {
            return;
        }

        sendFileReceipt(
            output,
            session,
            writeLock,
            receipts ? control.transferId : null,
            false,
            "Android 文件完成队列繁忙，传输已取消");
    }

    private void handleFileTransferChecksum(
        RemoteDeskTransport.ControlMessage control,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver, boolean receipts) throws IOException, GeneralSecurityException {
        try {
            fileTransferReceiver.setExpectedChecksum(control);
        } catch (Exception ex) {
            AndroidSessionLog.error("Android file receive failed while setting checksum.", ex);
            sendFileReceipt(output, session, writeLock, receipts ? control.transferId : null, false, "Android 文件接收失败：" + ex.getMessage());
        }
    }

    private void handleFileTransferCancel(
        RemoteDeskTransport.ControlMessage control,
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        AndroidFileTransferReceiver fileTransferReceiver, boolean receipts) throws IOException, GeneralSecurityException {
        String message;
        try {
            message = fileTransferReceiver.cancel(control);
        } catch (Exception ex) {
            AndroidSessionLog.error("Android file receive failed while cancelling.", ex);
            sendFileReceipt(output, session, writeLock, receipts ? control.transferId : null, false, "Android 文件取消失败：" + ex.getMessage());
            return;
        }

        AndroidSessionLog.info(message);
        sendFileReceipt(output, session, writeLock, receipts ? control.transferId : null, false, message);
    }

    private void sendFileTransferStatus(
        OutputStream output,
        RemoteDeskTransport.SecureSession session,
        Object writeLock,
        boolean success,
        String message) throws IOException, GeneralSecurityException {
        RemoteDeskTransport.writeMessage(
            output,
            RemoteDeskProtocol.MESSAGE_CONTROL,
            RemoteDeskTransport.encodeFileTransferStatus(success, message),
            session,
            writeLock);
    }

    private void sendFileReceipt(OutputStream output, RemoteDeskTransport.SecureSession session,
        Object writeLock, String transferId, boolean success, String message) throws IOException, GeneralSecurityException {
        RemoteDeskTransport.writeMessage(output, RemoteDeskProtocol.MESSAGE_CONTROL,
            transferId == null ? RemoteDeskTransport.encodeFileTransferStatus(success, message)
                : RemoteDeskTransport.encodeFileTransferReceipt(transferId, success, message), session, writeLock);
    }

    private static void closeQuietly(ServerSocket socket) {
        if (socket == null) {
            return;
        }

        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }

    static int getCapabilities() {
        int capabilities = getCapabilities(
            AndroidVideoCodecDiagnostics.cachedH264Report(),
            AndroidInputInjector.isEnabled());
        if (AndroidScreenCaptureSession.getInstance().isAccessibilityCapture()) {
            capabilities &= ~AndroidH264CapabilityPolicy.hostCapabilities(AndroidVideoCodecDiagnostics.cachedH264Report());
        }
        return capabilities;
    }

    static int getCapabilities(
        AndroidVideoCodecDiagnostics.CodecReport encoderReport,
        boolean inputEnabled) {
        int capabilities = RemoteDeskProtocol.CAPABILITY_REMOTE_DESKTOP |
            RemoteDeskProtocol.CAPABILITY_CLIPBOARD_TEXT |
            RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1 |
            RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE |
            RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_RECEIPT |
            RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE_LOCATION |
            RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM |
            RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_CANCEL |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT |
            RemoteDeskProtocol.CAPABILITY_HIGH_QUALITY_JPEG |
            RemoteDeskProtocol.CAPABILITY_DEVICE_IDENTITY |
            AndroidH264CapabilityPolicy.hostCapabilities(encoderReport);
        if (inputEnabled) {
            capabilities |= RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL;
            capabilities |= RemoteDeskProtocol.CAPABILITY_CLIPBOARD_PASTE_SHORTCUT;
            capabilities |= RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT;
        }

        return capabilities;
    }

    static String formatUnsupportedFileReturnStatus() {
        return "远端剪贴板没有可回传文件。Android 系统不允许被控服务可靠读取其它应用复制的文件；" +
            "可在手机的远控会话中打开“更多 → 发送文件”，主动选择要发送的文件。";
    }

    static void configureClientSocketForAuthentication(Socket client) throws IOException {
        trySetTcpNoDelay(client);
        trySetKeepAlive(client);
        trySetSendBufferSize(client);
        trySetReceiveBufferSize(client);
        client.setSoTimeout(AUTHENTICATION_TIMEOUT_MILLIS);
    }

    static void configureAuthenticatedClientSocket(Socket client) throws IOException {
        client.setSoTimeout(BLOCKING_READ_TIMEOUT_MILLIS);
    }

    private static void trySetTcpNoDelay(Socket client) {
        try {
            client.setTcpNoDelay(true);
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private static void trySetKeepAlive(Socket client) {
        try {
            client.setKeepAlive(true);
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private static void trySetSendBufferSize(Socket client) {
        try {
            client.setSendBufferSize(AndroidRelay.hostSendBufferBytes(client.getInetAddress(),
                AndroidVideoStreamSettings.FRAME_SEND_BUFFER_BYTES));
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private static void trySetReceiveBufferSize(Socket client) {
        try {
            client.setReceiveBufferSize(INPUT_RECEIVE_BUFFER_BYTES);
        } catch (IOException | RuntimeException ignored) {
        }
    }

    private static void closeQuietly(Socket socket) {
        if (socket == null) {
            return;
        }

        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }

    private static void sleep(long milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private static void preferDisplayThreadPriority() {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_DISPLAY);
        } catch (RuntimeException ignored) {
        }
    }

    private static double nanosToMillis(long nanos) {
        return nanos / 1_000_000d;
    }

    /**
     * Keeps unauthenticated sockets separate from the active viewer. The
     * newest authenticated client atomically becomes owner and receives the
     * prior owner's bounded replacement action. Identity-based release keeps
     * delayed cleanup from clearing the replacement's slot.
     */
    static final class ClientAdmissionGate<T> {
        enum ActivationResult {
            ACTIVATED,
            STOPPED,
            NOT_PENDING
        }

        static final class Activation<T> {
            final ActivationResult result;
            final T replacedClient;
            private final Runnable replacePrevious;

            Activation(
                ActivationResult result,
                T replacedClient,
                Runnable replacePrevious) {
                this.result = result;
                this.replacedClient = replacedClient;
                this.replacePrevious = replacePrevious;
            }

            void replacePrevious() {
                if (replacePrevious != null) {
                    replacePrevious.run();
                }
            }
        }

        private static final class ActiveClient<T> {
            final T client;
            final Runnable replace;

            ActiveClient(T client, Runnable replace) {
                this.client = client;
                this.replace = replace;
            }
        }

        private final int maxPending;
        private final Set<T> pending =
            Collections.newSetFromMap(new IdentityHashMap<>());
        private boolean accepting;
        private ActiveClient<T> active;
        private long generation;

        ClientAdmissionGate(int maxPending) {
            if (maxPending <= 0) {
                throw new IllegalArgumentException("maxPending must be positive.");
            }
            this.maxPending = maxPending;
        }

        synchronized long start() {
            if (active != null || !pending.isEmpty()) {
                throw new IllegalStateException(
                    "The client gate must be drained before it is restarted.");
            }
            accepting = true;
            generation++;
            return generation;
        }

        synchronized boolean tryRegisterPending(T client) {
            return tryRegisterPending(client, generation);
        }

        synchronized boolean tryRegisterPending(T client, long listenerGeneration) {
            if (client == null ||
                !accepting ||
                listenerGeneration != generation ||
                (active != null && active.client == client) ||
                pending.contains(client) ||
                pending.size() >= maxPending) {
                return false;
            }
            pending.add(client);
            return true;
        }

        synchronized Activation<T> activateLatest(
            T client,
            Runnable replace) {
            if (!pending.remove(client)) {
                return new Activation<>(
                    accepting
                        ? ActivationResult.NOT_PENDING
                        : ActivationResult.STOPPED,
                    null,
                    null);
            }
            if (!accepting) {
                return new Activation<>(
                    ActivationResult.STOPPED,
                    null,
                    null);
            }
            ActiveClient<T> previous = active;
            active = new ActiveClient<>(client, replace);
            return new Activation<>(
                ActivationResult.ACTIVATED,
                previous == null ? null : previous.client,
                previous == null ? null : previous.replace);
        }

        synchronized void authenticationEnded(T client) {
            pending.remove(client);
        }

        synchronized boolean releaseActive(T client) {
            if (active == null || active.client != client) {
                return false;
            }
            active = null;
            return true;
        }

        synchronized boolean isActive(T client) {
            return active != null && active.client == client;
        }

        synchronized List<T> stopAndDrain() {
            accepting = false;
            generation++;
            List<T> clients = new ArrayList<>(pending);
            pending.clear();
            if (active != null) {
                clients.add(active.client);
                active = null;
            }
            return clients;
        }

        synchronized int pendingCount() {
            return pending.size();
        }

        synchronized T activeClient() {
            return active == null ? null : active.client;
        }
    }

    static final class AuthenticationDeadline {
        private final long deadlineNanos;

        AuthenticationDeadline(long deadlineNanos) {
            this.deadlineNanos = deadlineNanos;
        }

        RemoteDeskTransport.BeforeAuthenticationRead forSocket(Socket socket) {
            return () -> {
                long remainingNanos = deadlineNanos - System.nanoTime();
                if (remainingNanos <= 0L) {
                    throw new SocketTimeoutException(
                        "RemoteDesk authentication exceeded its absolute deadline.");
                }
                long remainingMillis = Math.max(
                    1L,
                    TimeUnit.NANOSECONDS.toMillis(remainingNanos) + 1L);
                socket.setSoTimeout((int) Math.min(Integer.MAX_VALUE, remainingMillis));
            };
        }
    }

    @FunctionalInterface
    interface ListenerFactory {
        ServerSocket create() throws IOException;
    }

    private enum H264CaptureResult {
        Completed,
        Restart,
        Fallback
    }

    private void waitForViewerInfo(AndroidHostSessionState state) {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(500);
        while (running.get() &&
            state.running.get() &&
            (!state.viewerInfoReceived.get() ||
                !state.viewerCapabilitiesReceived.get()) &&
            System.nanoTime() < deadline) {
            sleep(10);
        }
    }

    private static final class AdaptiveCaptureController {
        private static final long METRICS_WINDOW_NANOS = 3_000_000_000L;
        private static final int MIN_FPS = 8;
        private static final int QUALITY_STEP = 6;
        private static final int EDGE_STEP = 160;
        private static final int MIN_EDGE = 960;

        private final int maximumQuality;
        private final int minimumQuality;
        private int currentFps = AndroidVideoStreamSettings.JPEG_TARGET_FPS;
        private int currentQuality;
        private int currentMaxEdge = MAX_STREAM_EDGE;
        private int frames;
        private double captureMillis;
        private double encodeMillis;
        private double sendMillis;
        private long windowStartedAt = System.nanoTime();

        AdaptiveCaptureController(int viewerCapabilities) {
            boolean highQuality = (viewerCapabilities &
                RemoteDeskProtocol.CAPABILITY_HIGH_QUALITY_JPEG) != 0;
            maximumQuality = highQuality
                ? HIGH_QUALITY_JPEG
                : JPEG_QUALITY;
            minimumQuality = highQuality
                ? HIGH_QUALITY_JPEG_FLOOR
                : 48;
            currentQuality = maximumQuality;
        }

        void recordFrame(AndroidScreenCaptureSession.ScreenFrame frame, double frameSendMillis) {
            frames++;
            captureMillis += frame.captureMillis;
            encodeMillis += frame.encodeMillis;
            sendMillis += frameSendMillis;
        }

        void updateIfNeeded() {
            long now = System.nanoTime();
            if (now - windowStartedAt < METRICS_WINDOW_NANOS || frames <= 0) {
                return;
            }

            double elapsedSeconds = (now - windowStartedAt) / 1_000_000_000d;
            double actualFps = frames / elapsedSeconds;
            double averageCapture = captureMillis / frames;
            double averageEncode = encodeMillis / frames;
            double averageSend = sendMillis / frames;
            double budget = 1000d / currentFps;
            if (AndroidScreenCaptureSession.getInstance().isAccessibilityCapture())
                budget = AndroidScreenshotGate.frameBudgetMillis(currentFps);
            double expectedFps = 1000d / budget;
            double processingMillis = averageCapture + averageEncode + averageSend;
            boolean overloaded = processingMillis > budget * 0.82 || averageSend > budget * 0.45 || actualFps < expectedFps * 0.84;
            boolean comfortable = processingMillis < budget * 0.48 && averageSend < budget * 0.22 && actualFps > expectedFps * 0.94;

            if (overloaded) {
                reduceLoad();
            } else if (comfortable) {
                restoreQuality();
            }

            frames = 0;
            captureMillis = 0;
            encodeMillis = 0;
            sendMillis = 0;
            windowStartedAt = now;
        }

        private void reduceLoad() {
            if (currentQuality > minimumQuality) {
                int previousQuality = currentQuality;
                currentQuality = Math.max(
                    minimumQuality,
                    currentQuality - QUALITY_STEP);
                AndroidSessionLog.info("JPEG adaptive profile reduced quality: " +
                    previousQuality + " -> " + currentQuality + ", maxEdge=" + currentMaxEdge +
                    ", fps=" + currentFps + ".");
                return;
            }

            if (currentMaxEdge > MIN_EDGE) {
                int previousMaxEdge = currentMaxEdge;
                currentMaxEdge = Math.max(MIN_EDGE, currentMaxEdge - EDGE_STEP);
                AndroidSessionLog.info("JPEG adaptive profile reduced resolution edge: " +
                    previousMaxEdge + " -> " + currentMaxEdge + ", quality=" + currentQuality +
                    ", fps=" + currentFps + ".");
                return;
            }

            if (currentFps > MIN_FPS) {
                int previousFps = currentFps;
                currentFps = Math.max(MIN_FPS, currentFps - 2);
                AndroidSessionLog.info("JPEG adaptive profile reduced fps: " +
                    previousFps + " -> " + currentFps + ", quality=" + currentQuality +
                    ", maxEdge=" + currentMaxEdge + ".");
            }
        }

        private void restoreQuality() {
            if (currentFps < AndroidVideoStreamSettings.JPEG_TARGET_FPS) {
                int previousFps = currentFps;
                currentFps = Math.min(AndroidVideoStreamSettings.JPEG_TARGET_FPS, currentFps + 2);
                AndroidSessionLog.info("JPEG adaptive profile restored fps: " +
                    previousFps + " -> " + currentFps + ", quality=" + currentQuality +
                    ", maxEdge=" + currentMaxEdge + ".");
                return;
            }

            if (currentMaxEdge < MAX_STREAM_EDGE) {
                int previousMaxEdge = currentMaxEdge;
                currentMaxEdge = Math.min(MAX_STREAM_EDGE, currentMaxEdge + EDGE_STEP);
                AndroidSessionLog.info("JPEG adaptive profile restored resolution edge: " +
                    previousMaxEdge + " -> " + currentMaxEdge + ", quality=" + currentQuality +
                    ", fps=" + currentFps + ".");
                return;
            }

            if (currentQuality < maximumQuality) {
                int previousQuality = currentQuality;
                currentQuality = Math.min(
                    maximumQuality,
                    currentQuality + QUALITY_STEP);
                AndroidSessionLog.info("JPEG adaptive profile restored quality: " +
                    previousQuality + " -> " + currentQuality + ", maxEdge=" + currentMaxEdge +
                    ", fps=" + currentFps + ".");
            }
        }
    }

    private static final class AdaptiveH264BitrateController {
        private static final long METRICS_WINDOW_NANOS = 2_500_000_000L;
        private static final int MIN_BITRATE =
            AndroidVideoStreamSettings.H264_MIN_BITRATE;
        private static final int MAX_BITRATE =
            AndroidVideoStreamSettings.H264_MAX_BITRATE;

        private final int targetFps;
        private final int maxBitrate;
        private int currentBitrate;
        private int frames;
        private long encodedBytes;
        private double sendMillis;
        private long windowStartedAt = System.nanoTime();

        AdaptiveH264BitrateController(int initialBitrate, int targetFps) {
            this.currentBitrate = Math.max(MIN_BITRATE, Math.min(MAX_BITRATE, initialBitrate));
            this.maxBitrate = this.currentBitrate;
            this.targetFps = Math.max(1, targetFps);
        }

        void recordFrame(int bytes, double frameSendMillis) {
            frames++;
            encodedBytes += Math.max(0, bytes);
            sendMillis += Math.max(0, frameSendMillis);
        }

        int updateIfNeeded() {
            long now = System.nanoTime();
            if (now - windowStartedAt < METRICS_WINDOW_NANOS || frames <= 0) {
                return 0;
            }

            double elapsedSeconds = (now - windowStartedAt) / 1_000_000_000d;
            double actualFps = frames / elapsedSeconds;
            double averageSendMillis = sendMillis / frames;
            double budgetMillis = 1000d / targetFps;
            double bitsPerSecond = encodedBytes * 8d / Math.max(0.001d, elapsedSeconds);
            boolean overloaded = AndroidH264BitratePolicy.isNetworkBound(
                actualFps, targetFps, averageSendMillis, bitsPerSecond, currentBitrate);
            boolean comfortable = averageSendMillis < budgetMillis * 0.18 &&
                actualFps > targetFps * 0.9 &&
                bitsPerSecond < currentBitrate * 0.72;

            int nextBitrate = currentBitrate;
            if (overloaded) {
                nextBitrate = Math.max(MIN_BITRATE, (int) Math.round(currentBitrate * 0.78));
            } else if (comfortable && currentBitrate < maxBitrate) {
                nextBitrate = Math.min(maxBitrate, (int) Math.round(currentBitrate * 1.12));
            }

            reset(now);
            if (nextBitrate == currentBitrate) {
                return 0;
            }

            return nextBitrate;
        }

        void acceptBitrate(int bitrate) {
            currentBitrate = Math.max(MIN_BITRATE, Math.min(maxBitrate, bitrate));
        }

        int getCurrentBitrate() {
            return currentBitrate;
        }

        private void reset(long now) {
            frames = 0;
            encodedBytes = 0;
            sendMillis = 0;
            windowStartedAt = now;
        }
    }

    static String formatBitsPerSecond(int bitsPerSecond) {
        if (bitsPerSecond >= 1_000_000) {
            return String.format(java.util.Locale.ROOT, "%.1f Mbps", bitsPerSecond / 1_000_000d);
        }

        return String.format(java.util.Locale.ROOT, "%.0f Kbps", bitsPerSecond / 1_000d);
    }
}
