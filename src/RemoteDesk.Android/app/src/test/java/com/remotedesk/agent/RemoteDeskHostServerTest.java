package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.io.IOException;
import java.net.SocketException;
import java.net.Socket;
import java.net.ServerSocket;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.RejectedExecutionException;

import org.junit.Test;

public final class RemoteDeskHostServerTest {
    @Test
    public void lateViewerInfoCanUpgradeAnAlreadyStartedJpegStream() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        assertFalse(RemoteDeskHostServer.shouldStartH264(true, state.viewerVideoCodecs.get()));

        state.viewerVideoCodecs.set(RemoteDeskProtocol.VIDEO_CODEC_JPEG |
            RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B);
        state.viewerInfoReceived.set(true);

        assertTrue(RemoteDeskHostServer.shouldStartH264(true, state.viewerVideoCodecs.get()));
        assertTrue(state.running.get());
    }

    @Test
    public void capabilitiesAloneDoNotSelectAnUnadvertisedCodec() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        state.viewerCapabilities.set(RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264);
        state.viewerCapabilitiesReceived.set(true);
        assertFalse(RemoteDeskHostServer.shouldStartH264(true, state.viewerVideoCodecs.get()));
    }

    @Test
    public void legacyAndExplicitJpegViewersKeepJpegCapture() {
        assertFalse(RemoteDeskHostServer.shouldStartH264(true, 0));
        assertFalse(RemoteDeskHostServer.shouldStartH264(true, RemoteDeskProtocol.VIDEO_CODEC_JPEG));
    }

    @Test
    public void compatibleCaptureAndRealFallbackCannotRestartH264() {
        int codecs = RemoteDeskProtocol.VIDEO_CODEC_JPEG | RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B;
        assertFalse(RemoteDeskHostServer.shouldStartH264(false, codecs));
        assertFalse(RemoteDeskHostServer.shouldStartH264(false, RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B));
    }

    @Test
    public void h264OnlyViewerCanUpgradeAfterTheStartupWait() {
        assertTrue(RemoteDeskHostServer.shouldStartH264(true, RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B));
    }

    @Test
    public void viewerWithdrawalImmediatelyPreventsAJpegUpgrade() {
        AndroidHostSessionState state = new AndroidHostSessionState();
        state.viewerVideoCodecs.set(RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B);
        assertTrue(RemoteDeskHostServer.shouldStartH264(true, state.viewerVideoCodecs.get()));
        state.viewerVideoCodecs.set(RemoteDeskProtocol.VIDEO_CODEC_JPEG);
        assertFalse(RemoteDeskHostServer.shouldStartH264(true, state.viewerVideoCodecs.get()));
    }

    @Test
    public void configureClientSocketForAuthenticationUsesLowLatencyAndTimeout() throws IOException {
        try (Socket socket = new Socket()) {
            RemoteDeskHostServer.configureClientSocketForAuthentication(socket);

            assertTrue(socket.getTcpNoDelay());
            assertTrue(socket.getKeepAlive());
            assertEquals(10_000, socket.getSoTimeout());

            RemoteDeskHostServer.configureAuthenticatedClientSocket(socket);

            assertEquals(0, socket.getSoTimeout());
        }
    }

    @Test
    public void configureClientSocketForAuthenticationIgnoresOptionalSocketOptionFailures()
        throws IOException {
        ThrowingOptionalSocket socket = new ThrowingOptionalSocket();

        RemoteDeskHostServer.configureClientSocketForAuthentication(socket);

        assertEquals(10_000, socket.getSoTimeout());
    }

    @Test
    public void configureClientSocketUsesBoundedFrameSendBuffer() throws IOException {
        RecordingSendBufferSocket socket = new RecordingSendBufferSocket();

        RemoteDeskHostServer.configureClientSocketForAuthentication(socket);

        assertEquals(
            AndroidVideoStreamSettings.FRAME_SEND_BUFFER_BYTES,
            socket.requestedSendBufferBytes);
    }

    @Test
    public void clientGateAllowsMultiplePendingAuthenticationsWithoutAnActiveOwner() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(4);
        Object first = new Object();
        Object second = new Object();
        gate.start();

        assertTrue(gate.tryRegisterPending(first));
        assertTrue(gate.tryRegisterPending(second));

        assertEquals(2, gate.pendingCount());
        assertNull(gate.activeClient());
    }

    @Test
    public void failedAuthenticationOnlyRemovesItsOwnPendingConnection() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(4);
        Object failed = new Object();
        Object successful = new Object();
        gate.start();
        assertTrue(gate.tryRegisterPending(failed));
        assertTrue(gate.tryRegisterPending(successful));

        gate.authenticationEnded(failed);

        assertEquals(1, gate.pendingCount());
        assertEquals(
            RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED,
            gate.activateLatest(successful, () -> { }).result);
        assertSame(successful, gate.activeClient());
    }

    @Test
    public void simultaneousSuccessfulAuthenticationsAtomicallyChooseNewestOwner()
        throws Exception {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(4);
        Object first = new Object();
        Object second = new Object();
        gate.start();
        assertTrue(gate.tryRegisterPending(first));
        assertTrue(gate.tryRegisterPending(second));

        CountDownLatch ready = new CountDownLatch(2);
        CountDownLatch start = new CountDownLatch(1);
        ExecutorService racers = Executors.newFixedThreadPool(2);
        try {
            Future<RemoteDeskHostServer.ClientAdmissionGate.Activation<Object>> firstResult =
                racers.submit(() -> activateAfterBarrier(gate, first, ready, start));
            Future<RemoteDeskHostServer.ClientAdmissionGate.Activation<Object>> secondResult =
                racers.submit(() -> activateAfterBarrier(gate, second, ready, start));
            assertTrue(ready.await(1, TimeUnit.SECONDS));
            start.countDown();

            RemoteDeskHostServer.ClientAdmissionGate.Activation<Object> one =
                firstResult.get(1, TimeUnit.SECONDS);
            RemoteDeskHostServer.ClientAdmissionGate.Activation<Object> two =
                secondResult.get(1, TimeUnit.SECONDS);
            int activated = (one.result ==
                RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED ? 1 : 0) +
                (two.result ==
                RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED ? 1 : 0);
            int replacements = (one.replacedClient != null ? 1 : 0) +
                (two.replacedClient != null ? 1 : 0);

            assertEquals(2, activated);
            assertEquals(1, replacements);
            assertEquals(0, gate.pendingCount());
            assertTrue(gate.activeClient() == first || gate.activeClient() == second);
        } finally {
            racers.shutdownNow();
        }
    }

    @Test
    public void replacedConnectionCannotClearTheNewOwner() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(4);
        Object owner = new Object();
        Object busy = new Object();
        boolean[] ownerReplacementCalled = { false };
        gate.start();
        assertTrue(gate.tryRegisterPending(owner));
        assertEquals(
            RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED,
            gate.activateLatest(
                owner,
                () -> ownerReplacementCalled[0] = true).result);
        assertTrue(gate.tryRegisterPending(busy));

        RemoteDeskHostServer.ClientAdmissionGate.Activation<Object> replacement =
            gate.activateLatest(busy, () -> { });
        assertEquals(
            RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED,
            replacement.result);
        assertSame(owner, replacement.replacedClient);
        replacement.replacePrevious();
        assertTrue(ownerReplacementCalled[0]);
        assertFalse(gate.releaseActive(owner));

        assertSame(busy, gate.activeClient());
        assertTrue(gate.releaseActive(busy));
        assertNull(gate.activeClient());
    }

    @Test
    public void stopDrainsActiveAndPendingConnectionsAndRejectsLatePromotion() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(4);
        Object owner = new Object();
        Object pending = new Object();
        gate.start();
        assertTrue(gate.tryRegisterPending(owner));
        assertEquals(
            RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.ACTIVATED,
            gate.activateLatest(owner, () -> { }).result);
        assertTrue(gate.tryRegisterPending(pending));

        List<Object> drained = gate.stopAndDrain();

        assertEquals(2, drained.size());
        assertTrue(drained.contains(owner));
        assertTrue(drained.contains(pending));
        assertEquals(0, gate.pendingCount());
        assertNull(gate.activeClient());
        assertFalse(gate.tryRegisterPending(new Object()));
        assertEquals(
            RemoteDeskHostServer.ClientAdmissionGate.ActivationResult.STOPPED,
            gate.activateLatest(pending, () -> { }).result);
    }

    @Test
    public void clientGateBoundsConcurrentAuthenticationWork() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(2);
        gate.start();

        assertTrue(gate.tryRegisterPending(new Object()));
        assertTrue(gate.tryRegisterPending(new Object()));
        assertFalse(gate.tryRegisterPending(new Object()));
        assertEquals(2, gate.pendingCount());
    }

    @Test
    public void clientGateRejectsAcceptedSocketFromPreviousListenerGeneration() {
        RemoteDeskHostServer.ClientAdmissionGate<Object> gate =
            new RemoteDeskHostServer.ClientAdmissionGate<>(2);
        long staleGeneration = gate.start();
        gate.stopAndDrain();
        long currentGeneration = gate.start();

        assertFalse(gate.tryRegisterPending(new Object(), staleGeneration));
        assertTrue(gate.tryRegisterPending(new Object(), currentGeneration));
    }

    @Test
    public void failedAcceptLoopSchedulingClosesBoundListenerAndDrainsGate() throws Exception {
        ExecutorService rejectedExecutor = Executors.newSingleThreadExecutor();
        rejectedExecutor.shutdownNow();
        RecordingListener firstListener = new RecordingListener();
        RecordingListener secondListener = new RecordingListener();
        int[] created = { 0 };
        RemoteDeskHostServer server =
            new RemoteDeskHostServer(
                null,
                null,
                rejectedExecutor,
                () -> created[0]++ == 0 ? firstListener : secondListener);

        IOException firstFailure =
            assertThrows(IOException.class, () -> server.start("test password"));
        assertFalse(server.isRunning());
        assertTrue(firstListener.closed);
        assertTrue(firstFailure.getCause() instanceof RejectedExecutionException);

        IOException secondFailure =
            assertThrows(IOException.class, () -> server.start("test password"));
        assertTrue(secondListener.closed);
        assertTrue(secondFailure.getCause() instanceof RejectedExecutionException);
    }

    @Test
    public void formatBitsPerSecondUsesReadableUnits() {
        assertEquals("900 Kbps", RemoteDeskHostServer.formatBitsPerSecond(900_000));
        assertEquals("1.5 Mbps", RemoteDeskHostServer.formatBitsPerSecond(1_500_000));
    }

    @Test
    public void h264WatchdogLogsAutomaticJpegFallbackClearly() {
        String probe = RemoteDeskHostServer.formatH264WatchdogProbe(false, 4_000L);
        String fallback =
            RemoteDeskHostServer.formatH264WatchdogFallback(false, 8_000L, true);

        assertTrue(probe.contains("first encoded frame"));
        assertTrue(probe.contains("requesting a sync frame"));
        assertTrue(fallback.contains("timed out"));
        assertTrue(fallback.contains("Falling back to JPEG"));
    }

    @Test
    public void h264WatchdogLogsForcedH264DisconnectClearly() {
        String fallback =
            RemoteDeskHostServer.formatH264WatchdogFallback(true, 12_000L, false);

        assertTrue(fallback.contains("last encoded frame"));
        assertTrue(fallback.contains("H.264 only"));
        assertTrue(fallback.contains("disconnect"));
    }

    @Test
    public void h264WatchdogPreservesAPreviouslyHealthyStaticDisplay() {
        String message =
            RemoteDeskHostServer.formatH264WatchdogStaticSilence(12_000L);

        assertTrue(message.contains("12000 ms"));
        assertTrue(message.contains("preserving"));
        assertTrue(message.contains("static"));
    }

    @Test
    public void getCapabilitiesAdvertisesOnlyActuallyAvailableVideoTier() {
        AndroidVideoCodecDiagnostics.CodecReport hardwareEncoder =
            new AndroidVideoCodecDiagnostics.CodecReport(
                true,
                "hardware.encoder",
                true,
                true,
                false,
                true,
                true,
                null);
        int capabilities = RemoteDeskHostServer.getCapabilities(
            hardwareEncoder,
            false);

        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE) != 0);
        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM) != 0);
        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_CANCEL) != 0);
        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);
        assertTrue((capabilities & RemoteDeskProtocol.CAPABILITY_HIGH_QUALITY_JPEG) != 0);
        assertFalse((capabilities & RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL) != 0);
        assertFalse((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_SEND) != 0);

        int noEncoderCapabilities = RemoteDeskHostServer.getCapabilities(
            AndroidVideoCodecDiagnostics.CodecReport.unavailable(),
            true);
        assertFalse((noEncoderCapabilities &
            RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0);
        assertFalse((noEncoderCapabilities &
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264) != 0);
        assertTrue((noEncoderCapabilities &
            RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL) != 0);
        assertTrue((noEncoderCapabilities &
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT) != 0);
        assertFalse((noEncoderCapabilities &
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK) != 0);
    }

    @Test
    public void unsupportedAndroidFileReturnStatusClosesViewerBatchWithActionableReason() {
        String status = RemoteDeskHostServer.formatUnsupportedFileReturnStatus();

        assertTrue(status.startsWith("远端剪贴板没有可回传文件"));
        assertTrue(status.contains("Android"));
        assertTrue(status.contains("当前仅支持 Windows 向 Android 发送文件"));
    }

    private static final class ThrowingOptionalSocket extends Socket {
        @Override
        public void setTcpNoDelay(boolean on) throws SocketException {
            throw new SocketException("tcp no delay unavailable");
        }

        @Override
        public void setKeepAlive(boolean on) throws SocketException {
            throw new SocketException("keep alive unavailable");
        }

        @Override
        public void setSendBufferSize(int size) throws SocketException {
            throw new SocketException("send buffer unavailable");
        }

        @Override
        public void setReceiveBufferSize(int size) throws SocketException {
            throw new SocketException("receive buffer unavailable");
        }
    }

    private static final class RecordingSendBufferSocket extends Socket {
        int requestedSendBufferBytes;

        @Override
        public void setSendBufferSize(int size) {
            requestedSendBufferBytes = size;
        }
    }

    private static final class RecordingListener extends ServerSocket {
        boolean closed;

        RecordingListener() throws IOException {
        }

        @Override
        public void bind(java.net.SocketAddress endpoint, int backlog) {
        }

        @Override
        public synchronized void close() {
            closed = true;
        }
    }

    private static RemoteDeskHostServer.ClientAdmissionGate.Activation<Object>
        activateAfterBarrier(
            RemoteDeskHostServer.ClientAdmissionGate<Object> gate,
            Object client,
            CountDownLatch ready,
            CountDownLatch start) throws InterruptedException {
        ready.countDown();
        if (!start.await(1, TimeUnit.SECONDS)) {
            throw new AssertionError("Activation race did not start.");
        }
        return gate.activateLatest(client, () -> { });
    }
}
