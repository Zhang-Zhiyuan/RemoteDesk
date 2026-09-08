package com.remotedesk.agent;

import java.io.Closeable;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketAddress;
import java.net.SocketException;
import java.security.GeneralSecurityException;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Authenticated UDP route used by both Android host and Android viewer.
 * Reliable state transitions remain on the encrypted TCP control stream;
 * this class owns UDP binding, media, feedback, heartbeat and latest-only
 * pointer datagrams.
 */
final class AndroidLowLatencyVideoTransport {
    static final long PROBE_INTERVAL_MILLIS = 200;
    static final long VIEWER_BIND_TIMEOUT_MILLIS = 3_000;
    static final long HOST_PROBE_TIMEOUT_MILLIS = 3_500;
    static final long HOST_READY_TIMEOUT_MILLIS = 2_000;
    static final long FEEDBACK_INTERVAL_MILLIS = 100;
    static final long FEEDBACK_HARD_TIMEOUT_MILLIS = 2_500;
    static final long HEARTBEAT_INTERVAL_MILLIS = 250;
    static final long STOPPED_BARRIER_TIMEOUT_MILLIS = 5_000;

    interface TcpControlSender {
        boolean send(byte[] payload) throws Exception;
    }

    interface FatalRouteListener {
        void onFatalRouteFailure(String reason);
    }

    interface FrameListener {
        void onFrame(int frameKind, byte[] payload);
    }

    interface UdpMouseApplier {
        boolean apply(byte[] inputPayload);
    }

    interface MouseAckListener {
        void onMouseMoveApplied(long sequence, long elapsedNanos);
    }

    interface NetworkPressureListener {
        void onNetworkPressure(NetworkPressure pressure);
    }

    interface VideoFallbackListener {
        void onVideoFallbackToTcp();
    }

    static final class NetworkPressure {
        final double packetLossRatio;
        final double frameAbandonRatio;
        final long targetBitsPerSecond;

        NetworkPressure(
            double packetLossRatio,
            double frameAbandonRatio,
            long targetBitsPerSecond) {
            this.packetLossRatio = packetLossRatio;
            this.frameAbandonRatio = frameAbandonRatio;
            this.targetBitsPerSecond = targetBitsPerSecond;
        }
    }

    interface Clock {
        long nanoTime();
    }

    enum State {
        NEW,
        WAITING_FOR_PROBE,
        WAITING_FOR_READY,
        WAITING_FOR_BIND_ACK,
        ACTIVE,
        WAITING_FOR_STOPPED,
        VIDEO_DISABLED_INPUT_ACTIVE,
        CLOSED
    }

    private AndroidLowLatencyVideoTransport() {
    }

    static final class Host implements Closeable {
        private final Object stateLock = new Object();
        private final DatagramSocket socket;
        private final InetAddress expectedPeerAddress;
        private final LowLatencyVideoProtocol.Offer offer;
        private final int features;
        private final TcpControlSender tcpControlSender;
        private final FatalRouteListener fatalRouteListener;
        private final UdpMouseApplier mouseApplier;
        private final NetworkPressureListener networkPressureListener;
        private final VideoFallbackListener videoFallbackListener;
        private final Clock clock;
        private final AtomicReference<PendingFrame> pendingFrame = new AtomicReference<>();
        private final AtomicReference<PendingHostMouse> pendingHostMouse = new AtomicReference<>();
        private final AtomicBoolean running = new AtomicBoolean(true);
        private final AtomicLong latestMouseSequence = new AtomicLong();
        private final LowLatencyVideoProtocol.SendCipher sendCipher;
        private final LowLatencyVideoProtocol.ReceiveCipher receiveCipher;
        private final Thread receiveThread;
        private final Thread sendThread;
        private final Thread monitorThread;
        private final Thread mouseThread;
        private volatile State state = State.WAITING_FOR_PROBE;
        private volatile InetSocketAddress peerEndpoint;
        private volatile long offeredAtNanos;
        private volatile long firstProbeAtNanos;
        private volatile long lastFeedbackAtNanos;
        private volatile long nextHeartbeatAtNanos;
        private volatile long nextFrameSequence;
        private volatile int fallbackReason;
        private volatile boolean stopBarrierSent;
        private volatile boolean hasLatestMouseSequence;
        private final AtomicBoolean fatalNotificationSent = new AtomicBoolean();
        private final AtomicBoolean videoFallbackNotificationSent = new AtomicBoolean();
        private volatile long targetBitsPerSecond = 24_000_000L;
        private volatile long pacingWindowStartedAtNanos;
        private volatile long pacingWindowWireBytes;
        private volatile int pacingWindowPackets;
        private volatile LowLatencyVideoProtocol.FeedbackV2 lastFeedback;
        private volatile long latestReceivedMouseSequence;
        private volatile boolean hasLatestReceivedMouseSequence;

        Host(
            InetAddress expectedPeerAddress,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            UdpMouseApplier mouseApplier) throws SocketException {
            this(
                new DatagramSocket(new InetSocketAddress(0)),
                expectedPeerAddress,
                features,
                tcpControlSender,
                fatalRouteListener,
                mouseApplier,
                null,
                null,
                System::nanoTime);
        }

        Host(
            InetAddress expectedPeerAddress,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            UdpMouseApplier mouseApplier,
            NetworkPressureListener networkPressureListener) throws SocketException {
            this(
                new DatagramSocket(new InetSocketAddress(0)),
                expectedPeerAddress,
                features,
                tcpControlSender,
                fatalRouteListener,
                mouseApplier,
                networkPressureListener,
                null,
                System::nanoTime);
        }

        Host(
            InetAddress expectedPeerAddress,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            UdpMouseApplier mouseApplier,
            NetworkPressureListener networkPressureListener,
            VideoFallbackListener videoFallbackListener) throws SocketException {
            this(
                new DatagramSocket(new InetSocketAddress(0)),
                expectedPeerAddress,
                features,
                tcpControlSender,
                fatalRouteListener,
                mouseApplier,
                networkPressureListener,
                videoFallbackListener,
                System::nanoTime);
        }

        Host(
            DatagramSocket socket,
            InetAddress expectedPeerAddress,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            UdpMouseApplier mouseApplier,
            NetworkPressureListener networkPressureListener,
            Clock clock) throws SocketException {
            this(
                socket,
                expectedPeerAddress,
                features,
                tcpControlSender,
                fatalRouteListener,
                mouseApplier,
                networkPressureListener,
                null,
                clock);
        }

        Host(
            DatagramSocket socket,
            InetAddress expectedPeerAddress,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            UdpMouseApplier mouseApplier,
            NetworkPressureListener networkPressureListener,
            VideoFallbackListener videoFallbackListener,
            Clock clock) throws SocketException {
            this.socket = socket;
            this.expectedPeerAddress = normalize(expectedPeerAddress);
            this.features = LowLatencyVideoProtocol.normalizeFeatures(features);
            this.tcpControlSender = tcpControlSender;
            this.fatalRouteListener = fatalRouteListener;
            this.mouseApplier = mouseApplier;
            this.networkPressureListener = networkPressureListener;
            this.videoFallbackListener = videoFallbackListener;
            this.clock = clock;
            socket.setReceiveBufferSize(4 * 1024 * 1024);
            socket.setSendBufferSize(4 * 1024 * 1024);
            socket.setSoTimeout(200);
            offer = LowLatencyVideoProtocol.createOffer(socket.getLocalPort());
            sendCipher = new LowLatencyVideoProtocol.SendCipher(
                offer.hostToViewerKey,
                offer.hostNoncePrefix,
                offer.channelId,
                offer.epoch);
            receiveCipher = new LowLatencyVideoProtocol.ReceiveCipher(
                offer.viewerToHostKey,
                offer.viewerNoncePrefix,
                offer.channelId,
                offer.epoch);
            offeredAtNanos = clock.nanoTime();
            pacingWindowStartedAtNanos = offeredAtNanos;
            nextHeartbeatAtNanos = offeredAtNanos + millisToNanos(HEARTBEAT_INTERVAL_MILLIS);
            receiveThread = startThread("RemoteDesk Android UDP host receiver", this::receiveLoop);
            sendThread = startThread("RemoteDesk Android UDP host sender", this::sendLoop);
            mouseThread = startThread("RemoteDesk Android UDP host mouse", this::mouseLoop);
            monitorThread = startThread("RemoteDesk Android UDP host monitor", this::monitorLoop);
        }

        LowLatencyVideoProtocol.Offer offerForTcp() {
            return offer.copy();
        }

        State state() {
            return state;
        }

        boolean isVideoActive() {
            return state == State.ACTIVE;
        }

        boolean isUdpMouseActive() {
            return state == State.ACTIVE || state == State.VIDEO_DISABLED_INPUT_ACTIVE;
        }

        boolean markReady(long channelId, int epoch) {
            synchronized (stateLock) {
                if (!offer.matches(channelId, epoch) || state != State.WAITING_FOR_READY ||
                    elapsedMillis(firstProbeAtNanos, clock.nanoTime()) > HOST_READY_TIMEOUT_MILLIS) {
                    return false;
                }
                state = State.ACTIVE;
                lastFeedbackAtNanos = clock.nanoTime();
                stateLock.notifyAll();
                return true;
            }
        }

        boolean matches(long channelId, int epoch) {
            return offer.matches(channelId, epoch) && state != State.CLOSED;
        }

        int acceptStop(long channelId, int epoch, int requestedReason) {
            boolean preserved;
            synchronized (stateLock) {
                if (!offer.matches(channelId, epoch) || state == State.CLOSED) {
                    return -1;
                }
                preserved = requestedReason ==
                        RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT &&
                    state == State.ACTIVE &&
                    (features & LowLatencyVideoProtocol.FEATURE_AUTHENTICATED_HEARTBEAT) != 0 &&
                    (features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT) != 0 &&
                    elapsedMillis(lastFeedbackAtNanos, clock.nanoTime()) <= FEEDBACK_HARD_TIMEOUT_MILLIS;
                clearPendingFrame();
                if (preserved) {
                    state = State.VIDEO_DISABLED_INPUT_ACTIVE;
                    stateLock.notifyAll();
                }
            }
            notifyVideoFallbackOnce();
            if (preserved) {
                return requestedReason;
            }
            closeFatal("UDP route stopped by peer.", false);
            return requestedReason == RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT
                ? RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC
                : requestedReason;
        }

        boolean offerFrame(int frameKind, byte[] framePayload) {
            if (!isVideoActive() || framePayload == null || framePayload.length <= 0 ||
                framePayload.length > offer.maxFrameBytes) {
                return false;
            }
            PendingFrame replaced = pendingFrame.getAndSet(
                new PendingFrame(nextFrameSequence++, frameKind, framePayload.clone()));
            clearFrame(replaced);
            synchronized (stateLock) {
                stateLock.notifyAll();
            }
            return true;
        }

        private void receiveLoop() {
            byte[] buffer = new byte[LowLatencyVideoProtocol.DEFAULT_MAX_DATAGRAM_BYTES];
            while (running.get()) {
                try {
                    DatagramPacket datagram = new DatagramPacket(buffer, buffer.length);
                    socket.receive(datagram);
                    InetSocketAddress source = (InetSocketAddress) datagram.getSocketAddress();
                    if (!normalize(source.getAddress()).equals(expectedPeerAddress)) {
                        continue;
                    }
                    LowLatencyVideoProtocol.Datagram packet =
                        receiveCipher.tryDecrypt(datagram.getData(), datagram.getLength());
                    if (packet == null) {
                        continue;
                    }
                    handlePacket(source, packet);
                } catch (java.net.SocketTimeoutException ignored) {
                } catch (Exception ex) {
                    if (running.get()) {
                        requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
                    }
                }
            }
        }

        private void handlePacket(
            InetSocketAddress source,
            LowLatencyVideoProtocol.Datagram packet) throws IOException, GeneralSecurityException {
            State current = state;
            if (current == State.WAITING_FOR_PROBE &&
                LowLatencyVideoProtocol.isChallengePacket(
                    packet,
                    LowLatencyVideoProtocol.KIND_BIND_PROBE,
                    offer.challenge) &&
                elapsedMillis(offeredAtNanos, clock.nanoTime()) <= HOST_PROBE_TIMEOUT_MILLIS) {
                synchronized (stateLock) {
                    if (state == State.WAITING_FOR_PROBE) {
                        peerEndpoint = source;
                        firstProbeAtNanos = clock.nanoTime();
                        state = State.WAITING_FOR_READY;
                    }
                }
                sendDatagram(source, sendCipher.encrypt(
                    LowLatencyVideoProtocol.KIND_BIND_ACK,
                    0,
                    offer.challenge.length,
                    0,
                    0,
                    1,
                    0,
                    0,
                    offer.challenge));
                return;
            }
            if (current == State.WAITING_FOR_READY &&
                LowLatencyVideoProtocol.isChallengePacket(
                    packet,
                    LowLatencyVideoProtocol.KIND_BIND_PROBE,
                    offer.challenge) &&
                elapsedMillis(firstProbeAtNanos, clock.nanoTime()) <= HOST_READY_TIMEOUT_MILLIS) {
                synchronized (stateLock) {
                    if (state == State.WAITING_FOR_READY) {
                        // Before Ready, an authenticated re-probe may move the
                        // viewer source port to escape a one-way NAT black hole.
                        peerEndpoint = source;
                    }
                }
                sendDatagram(source, sendCipher.encrypt(
                    LowLatencyVideoProtocol.KIND_BIND_ACK,
                    0,
                    offer.challenge.length,
                    0,
                    0,
                    1,
                    0,
                    0,
                    offer.challenge));
                return;
            }
            if ((current != State.ACTIVE && current != State.VIDEO_DISABLED_INPUT_ACTIVE) ||
                !source.equals(peerEndpoint)) {
                return;
            }
            if (LowLatencyVideoProtocol.isFeedbackPacket(packet)) {
                lastFeedbackAtNanos = clock.nanoTime();
                if (packet.kind == LowLatencyVideoProtocol.KIND_FEEDBACK_V2) {
                    observeFeedback(
                        LowLatencyVideoProtocol.tryDecodeFeedbackV2(packet.plaintext));
                }
                return;
            }
            if ((features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT) != 0 &&
                LowLatencyVideoProtocol.isMouseMovePacket(packet)) {
                if (hasLatestReceivedMouseSequence &&
                    Long.compareUnsigned(
                        packet.frameSequence,
                        latestReceivedMouseSequence) <= 0) {
                    return;
                }
                latestReceivedMouseSequence = packet.frameSequence;
                hasLatestReceivedMouseSequence = true;
                pendingHostMouse.set(new PendingHostMouse(
                    source,
                    packet.frameSequence,
                    packet.plaintext.clone()));
                synchronized (stateLock) {
                    stateLock.notifyAll();
                }
            }
        }

        private void sendLoop() {
            while (running.get()) {
                PendingFrame frame = pendingFrame.getAndSet(null);
                if (frame == null) {
                    waitForSignal(100);
                    continue;
                }
                if (!isVideoActive()) {
                    clearFrame(frame);
                    continue;
                }
                try {
                    boolean fec = (features & LowLatencyVideoProtocol.FEATURE_XOR_FEC) != 0;
                    List<byte[]> datagrams = LowLatencyVideoProtocol.fragmentFrame(
                        sendCipher,
                        frame.sequence,
                        frame.frameKind,
                        frame.payload,
                        offer.maxDatagramBytes,
                        fec);
                    InetSocketAddress endpoint = peerEndpoint;
                    for (int index = 0; index < datagrams.size(); index++) {
                        byte[] datagram = datagrams.get(index);
                        if (!isVideoActive() || endpoint == null) {
                            break;
                        }
                        sendDatagram(endpoint, datagram);
                        pace(datagram.length, index == datagrams.size() - 1);
                    }
                } catch (Exception ex) {
                    requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
                } finally {
                    Arrays.fill(frame.payload, (byte) 0);
                }
            }
        }

        private void mouseLoop() {
            while (running.get()) {
                PendingHostMouse mouse = pendingHostMouse.getAndSet(null);
                if (mouse == null) {
                    waitForSignal(50);
                    continue;
                }
                if (!isUdpMouseActive()) {
                    continue;
                }
                long appliedSequence = latestMouseSequence.get();
                if (hasLatestMouseSequence &&
                    Long.compareUnsigned(mouse.sequence, appliedSequence) <= 0) {
                    continue;
                }
                boolean applied;
                try {
                    applied = mouseApplier != null && mouseApplier.apply(mouse.payload);
                } catch (RuntimeException ex) {
                    applied = false;
                }
                if (!applied) {
                    continue;
                }
                latestMouseSequence.set(mouse.sequence);
                hasLatestMouseSequence = true;
                if ((features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK) != 0) {
                    try {
                        sendDatagram(mouse.source, sendCipher.encrypt(
                            LowLatencyVideoProtocol.KIND_MOUSE_MOVE_APPLIED_ACK,
                            mouse.sequence,
                            0,
                            0,
                            0,
                            1,
                            RemoteDeskProtocol.MESSAGE_INPUT,
                            0,
                            new byte[0]));
                    } catch (Exception ignored) {
                        // Applied ACKs are latency telemetry only. Losing one
                        // must never disable a successfully applied pointer.
                    }
                }
            }
        }

        void observeFeedback(LowLatencyVideoProtocol.FeedbackV2 feedback) {
            if (feedback == null) {
                return;
            }
            LowLatencyVideoProtocol.FeedbackV2 previous = lastFeedback;
            if (previous == null) {
                lastFeedback = feedback;
                return;
            }
            if (
                Long.compareUnsigned(
                    feedback.receiverElapsedMicroseconds,
                    previous.receiverElapsedMicroseconds) <= 0 ||
                Long.compareUnsigned(
                    feedback.totalAuthenticatedPackets,
                    previous.totalAuthenticatedPackets) < 0 ||
                Long.compareUnsigned(
                    feedback.totalSettledLostPackets,
                    previous.totalSettledLostPackets) < 0 ||
                Long.compareUnsigned(
                    feedback.totalCompletedFrames,
                    previous.totalCompletedFrames) < 0 ||
                Long.compareUnsigned(
                    feedback.totalAbandonedIncompleteFrames,
                    previous.totalAbandonedIncompleteFrames) < 0) {
                return;
            }
            lastFeedback = feedback;

            long packetDelta = feedback.totalAuthenticatedPackets -
                previous.totalAuthenticatedPackets;
            long lostDelta = feedback.totalSettledLostPackets -
                previous.totalSettledLostPackets;
            long frameDelta = feedback.totalCompletedFrames -
                previous.totalCompletedFrames;
            long abandonDelta = feedback.totalAbandonedIncompleteFrames -
                previous.totalAbandonedIncompleteFrames;
            double packetLoss = packetDelta == 0
                ? 0
                : Math.min(1d, (double) lostDelta / (packetDelta + lostDelta));
            double frameAbandon = frameDelta + abandonDelta == 0
                ? 0
                : Math.min(1d, (double) abandonDelta / (frameDelta + abandonDelta));
            boolean congested = packetLoss >= 0.02 || frameAbandon >= 0.05;
            long current = targetBitsPerSecond;
            if (congested) {
                targetBitsPerSecond = Math.max(2_000_000L, (current * 85) / 100);
            } else if (packetDelta >= 64 || frameDelta >= 4) {
                targetBitsPerSecond = Math.min(80_000_000L, current + 500_000L);
            }
            if (networkPressureListener != null) {
                try {
                    networkPressureListener.onNetworkPressure(
                        new NetworkPressure(packetLoss, frameAbandon, targetBitsPerSecond));
                } catch (RuntimeException ignored) {
                }
            }
        }

        LowLatencyVideoProtocol.FeedbackV2 lastFeedbackForTests() {
            return lastFeedback;
        }

        private void pace(int wireBytes, boolean flush) {
            long now = clock.nanoTime();
            long startedAt = pacingWindowStartedAtNanos;
            if (now - startedAt >= 20_000_000L) {
                pacingWindowStartedAtNanos = now;
                pacingWindowWireBytes = 0;
                pacingWindowPackets = 0;
                startedAt = now;
            }
            pacingWindowWireBytes += wireBytes;
            pacingWindowPackets++;
            if (!flush && pacingWindowPackets < 16) {
                return;
            }
            long desiredNanos = (long) Math.ceil(
                pacingWindowWireBytes * 8_000_000_000d /
                    Math.max(1, targetBitsPerSecond));
            long remaining = desiredNanos - (clock.nanoTime() - startedAt);
            if (remaining <= 0) {
                pacingWindowPackets = 0;
                return;
            }
            try {
                long millis = remaining / 1_000_000L;
                int nanos = (int) (remaining % 1_000_000L);
                Thread.sleep(millis, nanos);
            } catch (InterruptedException ex) {
                Thread.currentThread().interrupt();
            } finally {
                pacingWindowPackets = 0;
            }
        }

        private void monitorLoop() {
            while (running.get()) {
                long now = clock.nanoTime();
                State current = state;
                if (current == State.WAITING_FOR_PROBE &&
                    elapsedMillis(offeredAtNanos, now) > HOST_PROBE_TIMEOUT_MILLIS) {
                    requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_BIND_TIMEOUT);
                } else if (current == State.WAITING_FOR_READY &&
                    elapsedMillis(firstProbeAtNanos, now) > HOST_READY_TIMEOUT_MILLIS) {
                    requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_BIND_TIMEOUT);
                } else if (current == State.ACTIVE ||
                    current == State.VIDEO_DISABLED_INPUT_ACTIVE) {
                    if (elapsedMillis(lastFeedbackAtNanos, now) > FEEDBACK_HARD_TIMEOUT_MILLIS) {
                        // Viewer->host feedback proves both directions are
                        // usable. A host heartbeat alone cannot keep a one-way
                        // dead pointer route falsely active.
                        requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
                    } else if ((features &
                            LowLatencyVideoProtocol.FEATURE_AUTHENTICATED_HEARTBEAT) != 0 &&
                        now >= nextHeartbeatAtNanos) {
                        nextHeartbeatAtNanos = now + millisToNanos(HEARTBEAT_INTERVAL_MILLIS);
                        try {
                            InetSocketAddress endpoint = peerEndpoint;
                            if (endpoint != null) {
                                sendDatagram(endpoint, sendCipher.encrypt(
                                    LowLatencyVideoProtocol.KIND_HEARTBEAT,
                                    0,
                                    0,
                                    0,
                                    0,
                                    1,
                                    0,
                                    0,
                                    new byte[0]));
                            }
                        } catch (Exception ex) {
                            requestFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
                        }
                    }
                }
                sleep(25);
            }
        }

        private void requestFallback(int reason) {
            synchronized (stateLock) {
                if (state == State.CLOSED || stopBarrierSent) {
                    return;
                }
                stopBarrierSent = true;
                fallbackReason = reason;
                running.set(false);
                state = State.CLOSED;
                clearPendingFrame();
                pendingHostMouse.set(null);
                stateLock.notifyAll();
            }
            notifyVideoFallbackOnce();
            socket.close();
            AtomicBoolean completed = new AtomicBoolean();
            startThread("RemoteDesk Android UDP host fallback control", () -> {
                boolean sent = false;
                try {
                    sent = tcpControlSender != null && tcpControlSender.send(
                        RemoteDeskTransport.encodeLowLatencyVideoStopped(
                            offer.channelId,
                            offer.epoch,
                            reason));
                } catch (Exception ignored) {
                } finally {
                    completed.set(true);
                }
                if (!sent) {
                    notifyFatalOnce("UDP fallback control send failed.");
                }
            });
            startThread("RemoteDesk Android UDP host fallback deadline", () -> {
                sleep(STOPPED_BARRIER_TIMEOUT_MILLIS);
                if (!completed.get()) {
                    notifyFatalOnce("UDP fallback control send timed out.");
                }
            });
        }

        private void closeFatal(String reason, boolean notify) {
            boolean wasRunning = running.getAndSet(false);
            synchronized (stateLock) {
                state = State.CLOSED;
                clearPendingFrame();
                pendingHostMouse.set(null);
                stateLock.notifyAll();
            }
            socket.close();
            if (wasRunning && notify) {
                notifyFatalOnce(reason);
            }
        }

        private void notifyFatalOnce(String reason) {
            if (fatalRouteListener != null &&
                fatalNotificationSent.compareAndSet(false, true)) {
                fatalRouteListener.onFatalRouteFailure(reason);
            }
        }

        private void notifyVideoFallbackOnce() {
            if (videoFallbackListener != null &&
                videoFallbackNotificationSent.compareAndSet(false, true)) {
                try {
                    videoFallbackListener.onVideoFallbackToTcp();
                } catch (RuntimeException ignored) {
                }
            }
        }

        private void clearPendingFrame() {
            clearFrame(pendingFrame.getAndSet(null));
        }

        private static void clearFrame(PendingFrame frame) {
            if (frame != null) {
                Arrays.fill(frame.payload, (byte) 0);
            }
        }

        private void sendDatagram(InetSocketAddress target, byte[] bytes) throws IOException {
            socket.send(new DatagramPacket(bytes, bytes.length, target));
        }

        private void waitForSignal(long milliseconds) {
            synchronized (stateLock) {
                if (running.get() && pendingFrame.get() == null) {
                    try {
                        stateLock.wait(milliseconds);
                    } catch (InterruptedException ex) {
                        Thread.currentThread().interrupt();
                    }
                }
            }
        }

        @Override
        public void close() {
            signalClose();
            join(receiveThread);
            join(sendThread);
            join(mouseThread);
            join(monitorThread);
            sendCipher.close();
            receiveCipher.close();
            offer.clearSecrets();
        }

        void signalClose() {
            running.set(false);
            synchronized (stateLock) {
                state = State.CLOSED;
                stateLock.notifyAll();
            }
            socket.close();
        }
    }

    static final class Viewer implements Closeable {
        private final Object stateLock = new Object();
        private final DatagramSocket socket;
        private final InetAddress expectedHostAddress;
        private final InetSocketAddress hostEndpoint;
        private final LowLatencyVideoProtocol.Offer offer;
        private final int features;
        private final TcpControlSender tcpControlSender;
        private final FatalRouteListener fatalRouteListener;
        private final FrameListener frameListener;
        private final MouseAckListener mouseAckListener;
        private final Clock clock;
        private final AtomicBoolean running = new AtomicBoolean(true);
        private final AtomicReference<PendingMouse> pendingMouse = new AtomicReference<>();
        private final LowLatencyVideoProtocol.SendCipher sendCipher;
        private final LowLatencyVideoProtocol.ReceiveCipher receiveCipher;
        private final LowLatencyVideoProtocol.FrameReassembler reassembler;
        private final LowLatencyVideoProtocol.ArrivalTracker arrivalTracker;
        private final Thread receiveThread;
        private final Thread monitorThread;
        private volatile State state = State.WAITING_FOR_BIND_ACK;
        private volatile long startedAtNanos;
        private volatile long lastHeartbeatAtNanos;
        private volatile long lastCompleteFrameAtNanos;
        private volatile long highestAuthenticatedFrameSequence = -1;
        private volatile long highestCompleteFrameSequence = -1;
        private volatile long nextFeedbackAtNanos;
        private volatile long nextMouseSequence;
        private volatile long stopStartedAtNanos;
        private volatile int pendingStopReason;
        private volatile long lastMouseSentSequence;
        private volatile long lastMouseSentAtNanos;
        private volatile boolean readyClaimed;
        private volatile boolean ioFailed;

        Viewer(
            InetAddress expectedHostAddress,
            LowLatencyVideoProtocol.Offer offer,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            FrameListener frameListener,
            MouseAckListener mouseAckListener) throws SocketException {
            this(
                new DatagramSocket(new InetSocketAddress(0)),
                expectedHostAddress,
                offer,
                features,
                tcpControlSender,
                fatalRouteListener,
                frameListener,
                mouseAckListener,
                System::nanoTime);
        }

        Viewer(
            DatagramSocket socket,
            InetAddress expectedHostAddress,
            LowLatencyVideoProtocol.Offer offer,
            int features,
            TcpControlSender tcpControlSender,
            FatalRouteListener fatalRouteListener,
            FrameListener frameListener,
            MouseAckListener mouseAckListener,
            Clock clock) throws SocketException {
            this.socket = socket;
            this.expectedHostAddress = normalize(expectedHostAddress);
            this.hostEndpoint = new InetSocketAddress(this.expectedHostAddress, offer.port);
            this.offer = offer.copy();
            this.features = LowLatencyVideoProtocol.normalizeFeatures(features);
            this.tcpControlSender = tcpControlSender;
            this.fatalRouteListener = fatalRouteListener;
            this.frameListener = frameListener;
            this.mouseAckListener = mouseAckListener;
            this.clock = clock;
            socket.setReceiveBufferSize(8 * 1024 * 1024);
            socket.setSendBufferSize(1024 * 1024);
            socket.setSoTimeout(100);
            sendCipher = new LowLatencyVideoProtocol.SendCipher(
                offer.viewerToHostKey,
                offer.viewerNoncePrefix,
                offer.channelId,
                offer.epoch);
            receiveCipher = new LowLatencyVideoProtocol.ReceiveCipher(
                offer.hostToViewerKey,
                offer.hostNoncePrefix,
                offer.channelId,
                offer.epoch);
            reassembler = new LowLatencyVideoProtocol.FrameReassembler(
                offer.maxFrameBytes,
                offer.maxDatagramBytes,
                (this.features & LowLatencyVideoProtocol.FEATURE_XOR_FEC) != 0);
            startedAtNanos = clock.nanoTime();
            nextFeedbackAtNanos = startedAtNanos;
            arrivalTracker = new LowLatencyVideoProtocol.ArrivalTracker(startedAtNanos);
            receiveThread = startThread("RemoteDesk Android UDP viewer receiver", this::receiveLoop);
            monitorThread = startThread("RemoteDesk Android UDP viewer monitor", this::monitorLoop);
        }

        State state() {
            return state;
        }

        boolean isVideoActive() {
            return state == State.ACTIVE;
        }

        boolean isUdpMouseActive() {
            return state == State.ACTIVE || state == State.VIDEO_DISABLED_INPUT_ACTIVE;
        }

        boolean hasFreshAuthenticatedHeartbeat() {
            long receivedAt = lastHeartbeatAtNanos;
            return receivedAt != 0 &&
                elapsedMillis(receivedAt, clock.nanoTime()) <= FEEDBACK_HARD_TIMEOUT_MILLIS;
        }

        boolean offerMouseMove(byte[] inputPayload) {
            if (inputPayload == null || inputPayload.length != 14 ||
                (inputPayload[0] & 0xFF) != RemoteDeskProtocol.INPUT_MOUSE_MOVE ||
                (inputPayload[1] & 0xFF) != RemoteDeskProtocol.MOUSE_NONE ||
                (features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT) == 0 ||
                !canUseUdpMouseNow()) {
                return false;
            }
            pendingMouse.set(new PendingMouse(inputPayload.clone()));
            synchronized (stateLock) {
                stateLock.notifyAll();
            }
            if (!canUseUdpMouseNow()) {
                pendingMouse.set(null);
                return false;
            }
            return true;
        }

        private boolean canUseUdpMouseNow() {
            State current = state;
            if (current == State.ACTIVE) {
                return true;
            }
            if (current != State.VIDEO_DISABLED_INPUT_ACTIVE) {
                return false;
            }
            if (hasFreshAuthenticatedHeartbeat()) {
                return true;
            }
            pendingMouse.set(null);
            requestVideoFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
            return false;
        }

        void discardPendingMouseMove() {
            pendingMouse.set(null);
        }

        void acknowledgeStopped(long channelId, int epoch, int reason) {
            synchronized (stateLock) {
                State current = state;
                if (!offer.matches(channelId, epoch) || current == State.CLOSED) {
                    return;
                }
                boolean preserve = reason ==
                        RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT &&
                    current == State.WAITING_FOR_STOPPED &&
                    pendingStopReason == reason &&
                    (features & LowLatencyVideoProtocol.FEATURE_AUTHENTICATED_HEARTBEAT) != 0 &&
                    (features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT) != 0 &&
                    lastHeartbeatAtNanos != 0 &&
                    elapsedMillis(lastHeartbeatAtNanos, clock.nanoTime()) <= FEEDBACK_HARD_TIMEOUT_MILLIS;
                if (preserve) {
                    state = State.VIDEO_DISABLED_INPUT_ACTIVE;
                    stopStartedAtNanos = 0;
                    // The TCP Stopped acknowledgement proves the peers agreed
                    // to keep authenticated UDP input. Use this as a short
                    // initial heartbeat lease until the next real heartbeat
                    // arrives, then require fresh heartbeats for more UDP
                    // mouse movement.
                    lastHeartbeatAtNanos = clock.nanoTime();
                    stateLock.notifyAll();
                    return;
                }
            }
            // A matching unsolicited Stopped is authoritative. Hosts send it
            // for setup, feedback or send-path failures without first waiting
            // for a viewer Stop; leaving ACTIVE here would strand video and
            // input on a route the host has already torn down.
            closeFatal("UDP Stopped acknowledged without a live preserved input route.", false);
        }

        void requestVideoFallback(int reason) {
            beginStop(reason, false);
        }

        private void beginStop(int reason, boolean routeIoFailed) {
            boolean sendStop = true;
            synchronized (stateLock) {
                State current = state;
                if (current != State.ACTIVE &&
                    current != State.VIDEO_DISABLED_INPUT_ACTIVE &&
                    !(routeIoFailed && current == State.WAITING_FOR_STOPPED)) {
                    return;
                }
                if (current == State.WAITING_FOR_STOPPED) {
                    if (ioFailed) {
                        return;
                    }
                    ioFailed = true;
                    pendingMouse.set(null);
                    sendStop = pendingStopReason !=
                        RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC;
                    pendingStopReason = RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC;
                    reason = pendingStopReason;
                } else if (current == State.VIDEO_DISABLED_INPUT_ACTIVE) {
                    reason = RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC;
                    pendingStopReason = reason;
                    state = State.WAITING_FOR_STOPPED;
                    stopStartedAtNanos = clock.nanoTime();
                    pendingMouse.set(null);
                    ioFailed = routeIoFailed;
                } else {
                    pendingStopReason = reason;
                    state = State.WAITING_FOR_STOPPED;
                    stopStartedAtNanos = clock.nanoTime();
                    pendingMouse.set(null);
                    ioFailed = routeIoFailed;
                }
            }
            if (routeIoFailed) {
                socket.close();
            }
            if (!sendStop) {
                return;
            }
            final int requestedReason = reason;
            startThread("RemoteDesk Android UDP viewer Stop control", () -> {
                boolean sent = false;
                try {
                    sent = tcpControlSender != null && tcpControlSender.send(
                        RemoteDeskTransport.encodeLowLatencyVideoStop(
                            offer.channelId,
                            offer.epoch,
                            requestedReason));
                } catch (Exception ignored) {
                }
                if (!sent && running.get()) {
                    closeFatal("UDP Stop control send failed.", true);
                }
            });
        }

        private void receiveLoop() {
            byte[] buffer = new byte[LowLatencyVideoProtocol.DEFAULT_MAX_DATAGRAM_BYTES];
            while (running.get()) {
                try {
                    DatagramPacket datagram = new DatagramPacket(buffer, buffer.length);
                    socket.receive(datagram);
                    InetSocketAddress source = (InetSocketAddress) datagram.getSocketAddress();
                    if (!normalize(source.getAddress()).equals(expectedHostAddress) ||
                        source.getPort() != hostEndpoint.getPort()) {
                        continue;
                    }
                    LowLatencyVideoProtocol.Datagram packet =
                        receiveCipher.tryDecrypt(datagram.getData(), datagram.getLength());
                    if (packet == null) {
                        continue;
                    }
                    arrivalTracker.record(packet.packetSequence, datagram.getLength(), clock.nanoTime());
                    handlePacket(packet);
                } catch (java.net.SocketTimeoutException ignored) {
                } catch (Exception ex) {
                    if (running.get()) {
                        requestRouteFailure();
                        return;
                    }
                }
            }
        }

        private void handlePacket(LowLatencyVideoProtocol.Datagram packet) throws IOException {
            State current = state;
            if (current == State.WAITING_FOR_BIND_ACK &&
                LowLatencyVideoProtocol.isChallengePacket(
                    packet,
                    LowLatencyVideoProtocol.KIND_BIND_ACK,
                    offer.challenge)) {
                boolean shouldSendReady;
                synchronized (stateLock) {
                    shouldSendReady = state == State.WAITING_FOR_BIND_ACK && !readyClaimed;
                    if (shouldSendReady) {
                        readyClaimed = true;
                    }
                }
                if (!shouldSendReady) {
                    return;
                }
                startThread("RemoteDesk Android UDP viewer Ready control", this::sendReadyControl);
                return;
            }
            if (current == State.ACTIVE &&
                (packet.kind == LowLatencyVideoProtocol.KIND_FRAME_FRAGMENT ||
                    packet.kind == LowLatencyVideoProtocol.KIND_FRAME_XOR_PARITY)) {
                if (packet.frameSequence >= 0 &&
                    packet.frameSequence > highestAuthenticatedFrameSequence) {
                    highestAuthenticatedFrameSequence = packet.frameSequence;
                }
                LowLatencyVideoProtocol.CompleteFrame frame = reassembler.add(packet);
                if (frame != null) {
                    lastCompleteFrameAtNanos = clock.nanoTime();
                    highestCompleteFrameSequence = frame.sequence;
                    if (frameListener != null) {
                        frameListener.onFrame(frame.frameKind, frame.payload);
                    }
                }
                return;
            }
            if ((current == State.ACTIVE || current == State.WAITING_FOR_STOPPED ||
                    current == State.VIDEO_DISABLED_INPUT_ACTIVE) &&
                LowLatencyVideoProtocol.isHeartbeatPacket(packet)) {
                lastHeartbeatAtNanos = clock.nanoTime();
                return;
            }
            if ((current == State.ACTIVE || current == State.VIDEO_DISABLED_INPUT_ACTIVE) &&
                (features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK) != 0 &&
                LowLatencyVideoProtocol.isMouseMoveAppliedAckPacket(packet) &&
                packet.frameSequence == lastMouseSentSequence &&
                mouseAckListener != null) {
                mouseAckListener.onMouseMoveApplied(
                    packet.frameSequence,
                    Math.max(0, clock.nanoTime() - lastMouseSentAtNanos));
            }
        }

        private void monitorLoop() {
            long nextProbeAt = startedAtNanos;
            while (running.get()) {
                long now = clock.nanoTime();
                State current = state;
                try {
                    if (current == State.WAITING_FOR_BIND_ACK) {
                        if (elapsedMillis(startedAtNanos, now) > VIEWER_BIND_TIMEOUT_MILLIS) {
                            closeFatal(
                                readyClaimed
                                    ? "UDP Ready control send timed out."
                                    : "UDP bind acknowledgement timed out.",
                                readyClaimed);
                        } else if (now >= nextProbeAt) {
                            nextProbeAt = now + millisToNanos(PROBE_INTERVAL_MILLIS);
                            sendDatagram(sendCipher.encrypt(
                                LowLatencyVideoProtocol.KIND_BIND_PROBE,
                                0,
                                offer.challenge.length,
                                0,
                                0,
                                1,
                                0,
                                0,
                                offer.challenge));
                        }
                    } else if (current == State.ACTIVE ||
                        current == State.VIDEO_DISABLED_INPUT_ACTIVE ||
                        current == State.WAITING_FOR_STOPPED) {
                        if (!ioFailed && now >= nextFeedbackAtNanos) {
                            nextFeedbackAtNanos = now + millisToNanos(FEEDBACK_INTERVAL_MILLIS);
                            LowLatencyVideoProtocol.FeedbackV2 feedback = arrivalTracker.createFeedback(
                                now,
                                reassembler.highestCompletedSequence(),
                                reassembler.completedFrameCount(),
                                reassembler.abandonedIncompleteFrameCount());
                            byte[] payload = LowLatencyVideoProtocol.encodeFeedbackV2(feedback);
                            sendDatagram(sendCipher.encrypt(
                                LowLatencyVideoProtocol.KIND_FEEDBACK_V2,
                                0,
                                payload.length,
                                0,
                                0,
                                1,
                                0,
                                0,
                                payload));
                        }
                        if (!ioFailed) {
                            sendLatestMouseIfAvailable();
                        }
                        if (current == State.ACTIVE && hasFrameDeliveryTimedOut(now)) {
                            requestVideoFallback(selectFrameFallbackReason(now));
                        } else if (current == State.VIDEO_DISABLED_INPUT_ACTIVE &&
                            (lastHeartbeatAtNanos == 0 ||
                                elapsedMillis(lastHeartbeatAtNanos, now) > FEEDBACK_HARD_TIMEOUT_MILLIS)) {
                            requestVideoFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
                        } else if (current == State.WAITING_FOR_STOPPED &&
                            elapsedMillis(stopStartedAtNanos, now) > STOPPED_BARRIER_TIMEOUT_MILLIS) {
                            closeFatal("UDP Stop/Stopped barrier timed out.", true);
                        }
                    }
                } catch (Exception ex) {
                    requestRouteFailure();
                }
                waitForSignal(20);
            }
        }

        private void sendReadyControl() {
            boolean readySent = false;
            try {
                readySent = tcpControlSender != null && tcpControlSender.send(
                    RemoteDeskTransport.encodeLowLatencyVideoReady(
                        offer.channelId,
                        offer.epoch));
            } catch (Exception ignored) {
            }
            synchronized (stateLock) {
                if (!running.get() || state != State.WAITING_FOR_BIND_ACK) {
                    return;
                }
                if (readySent) {
                    state = State.ACTIVE;
                    startedAtNanos = clock.nanoTime();
                    nextFeedbackAtNanos = startedAtNanos;
                    stateLock.notifyAll();
                }
            }
            if (!readySent) {
                closeFatal("UDP Ready control send failed.", true);
            }
        }

        private boolean hasFrameDeliveryTimedOut(long nowNanos) {
            long silenceStartedAt = lastCompleteFrameAtNanos == 0
                ? startedAtNanos
                : lastCompleteFrameAtNanos;
            return shouldFallbackForFrameSilence(
                lastCompleteFrameAtNanos != 0,
                elapsedMillis(silenceStartedAt, nowNanos),
                highestAuthenticatedFrameSequence,
                highestCompleteFrameSequence,
                hasFreshAuthenticatedHeartbeat());
        }

        static boolean shouldFallbackForFrameSilence(
            boolean hasCompleteFrame,
            long silenceMillis,
            long highestAuthenticatedFrameSequence,
            long highestCompleteFrameSequence,
            boolean authenticatedHeartbeatFresh) {
            if (silenceMillis < FEEDBACK_HARD_TIMEOUT_MILLIS) {
                return false;
            }
            if (!hasCompleteFrame) {
                return true;
            }
            // A changed-frame capture backend may legitimately emit nothing
            // on a static desktop. A fresh authenticated heartbeat plus no
            // newer frame sequence proves that this is static silence, not an
            // incomplete access unit or a dead host->viewer path.
            return !authenticatedHeartbeatFresh ||
                highestAuthenticatedFrameSequence > highestCompleteFrameSequence;
        }

        private int selectFrameFallbackReason(long nowNanos) {
            return (features & LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT) != 0 &&
                (features & LowLatencyVideoProtocol.FEATURE_AUTHENTICATED_HEARTBEAT) != 0 &&
                lastHeartbeatAtNanos != 0 &&
                elapsedMillis(lastHeartbeatAtNanos, nowNanos) < FEEDBACK_HARD_TIMEOUT_MILLIS
                    ? RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT
                    : 2;
        }

        private void sendLatestMouseIfAvailable() throws GeneralSecurityException, IOException {
            if (!canUseUdpMouseNow()) {
                pendingMouse.set(null);
                return;
            }
            PendingMouse mouse = pendingMouse.getAndSet(null);
            if (mouse == null) {
                return;
            }
            long sequence = ++nextMouseSequence;
            lastMouseSentSequence = sequence;
            lastMouseSentAtNanos = clock.nanoTime();
            sendDatagram(sendCipher.encrypt(
                LowLatencyVideoProtocol.KIND_MOUSE_MOVE,
                sequence,
                mouse.payload.length,
                0,
                0,
                1,
                RemoteDeskProtocol.MESSAGE_INPUT,
                0,
                mouse.payload));
        }

        private void sendDatagram(byte[] bytes) throws IOException {
            socket.send(new DatagramPacket(bytes, bytes.length, hostEndpoint));
        }

        private void requestRouteFailure() {
            State current = state;
            if (current == State.WAITING_FOR_BIND_ACK) {
                // No UDP-only routing decision was committed yet. Closing the
                // candidate route is enough; the host's reliable Stopped
                // barrier will converge independently while TCP keeps going.
                closeFatal("UDP setup route failed.", false);
                return;
            }
            if (current == State.ACTIVE ||
                current == State.VIDEO_DISABLED_INPUT_ACTIVE ||
                current == State.WAITING_FOR_STOPPED) {
                beginStop(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC, true);
            }
        }

        private void closeFatal(String reason, boolean notify) {
            boolean wasRunning = running.getAndSet(false);
            synchronized (stateLock) {
                state = State.CLOSED;
                pendingMouse.set(null);
                stateLock.notifyAll();
            }
            socket.close();
            if (wasRunning && notify && fatalRouteListener != null) {
                fatalRouteListener.onFatalRouteFailure(reason);
            }
        }

        private void waitForSignal(long milliseconds) {
            synchronized (stateLock) {
                if (running.get()) {
                    try {
                        stateLock.wait(milliseconds);
                    } catch (InterruptedException ex) {
                        Thread.currentThread().interrupt();
                    }
                }
            }
        }

        @Override
        public void close() {
            closeFatal("UDP viewer route closed.", false);
            join(receiveThread);
            join(monitorThread);
            sendCipher.close();
            receiveCipher.close();
            offer.clearSecrets();
        }
    }

    private static final class PendingFrame {
        final long sequence;
        final int frameKind;
        final byte[] payload;

        PendingFrame(long sequence, int frameKind, byte[] payload) {
            this.sequence = sequence;
            this.frameKind = frameKind;
            this.payload = payload;
        }
    }

    private static final class PendingMouse {
        final byte[] payload;

        PendingMouse(byte[] payload) {
            this.payload = payload;
        }
    }

    private static final class PendingHostMouse {
        final InetSocketAddress source;
        final long sequence;
        final byte[] payload;

        PendingHostMouse(InetSocketAddress source, long sequence, byte[] payload) {
            this.source = source;
            this.sequence = sequence;
            this.payload = payload;
        }
    }

    private static Thread startThread(String name, Runnable action) {
        Thread thread = new Thread(action, name);
        thread.setDaemon(true);
        thread.start();
        return thread;
    }

    private static void join(Thread thread) {
        if (thread == Thread.currentThread()) {
            return;
        }
        try {
            thread.join(2_000);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }

    private static InetAddress normalize(InetAddress address) {
        byte[] bytes = address.getAddress();
        if (bytes.length == 16) {
            boolean mapped = true;
            for (int index = 0; index < 10; index++) {
                mapped &= bytes[index] == 0;
            }
            mapped &= bytes[10] == (byte) 0xFF && bytes[11] == (byte) 0xFF;
            if (mapped) {
                try {
                    return InetAddress.getByAddress(Arrays.copyOfRange(bytes, 12, 16));
                } catch (Exception ignored) {
                }
            }
        }
        return address;
    }

    private static long millisToNanos(long milliseconds) {
        return milliseconds * 1_000_000L;
    }

    private static long elapsedMillis(long startedAt, long endedAt) {
        if (startedAt == 0 || endedAt <= startedAt) {
            return 0;
        }
        return (endedAt - startedAt) / 1_000_000L;
    }

    private static void sleep(long milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException ex) {
            Thread.currentThread().interrupt();
        }
    }
}
