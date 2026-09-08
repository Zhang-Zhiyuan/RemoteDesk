package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import java.net.InetAddress;
import java.net.DatagramSocket;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

import org.junit.Test;

public final class AndroidLowLatencyVideoTransportTest {
    private static final int FEATURES =
        LowLatencyVideoProtocol.FEATURE_CONGESTION_FEEDBACK |
            LowLatencyVideoProtocol.FEATURE_XOR_FEC |
            LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT |
            LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK |
            LowLatencyVideoProtocol.FEATURE_AUTHENTICATED_HEARTBEAT;

    @Test
    public void outOfOrderFeedbackDoesNotMoveAimdBaselineBackward() throws Exception {
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                InetAddress.getLoopbackAddress(),
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        try {
            LowLatencyVideoProtocol.FeedbackV2 at100 = feedback(100, 100, 0);
            LowLatencyVideoProtocol.FeedbackV2 stale90 = feedback(90, 90, 0);
            LowLatencyVideoProtocol.FeedbackV2 at110 = feedback(110, 110, 1);
            host.observeFeedback(at100);
            host.observeFeedback(stale90);
            assertEquals(100, host.lastFeedbackForTests().receiverElapsedMicroseconds);
            host.observeFeedback(at110);
            assertEquals(110, host.lastFeedbackForTests().receiverElapsedMicroseconds);
        } finally {
            host.close();
        }
    }

    @Test
    public void hostFeedbackRouteLossFallsBackWithoutAbortingTcpSession() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AtomicReference<AndroidLowLatencyVideoTransport.Host> hostRef = new AtomicReference<>();
        CountDownLatch stoppedSent = new CountDownLatch(1);
        AtomicInteger tcpAbortRequests = new AtomicInteger();
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    if (control.kind ==
                        RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOPPED) {
                        stoppedSent.countDown();
                    }
                    return true;
                },
                reason -> tcpAbortRequests.incrementAndGet(),
                input -> true);
        hostRef.set(host);
        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    return control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY &&
                        hostRef.get().markReady(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch);
                },
                reason -> { },
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { });
        try {
            awaitState(host, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);
            viewer.close();
            assertTrue(stoppedSent.await(5, TimeUnit.SECONDS));
            awaitState(host, AndroidLowLatencyVideoTransport.State.CLOSED, 1_000);
            assertEquals(0, tcpAbortRequests.get());
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void viewerSocketFailureUsesStopBarrierWithoutAbortingTcpSession() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AtomicReference<AndroidLowLatencyVideoTransport.Host> hostRef = new AtomicReference<>();
        AtomicReference<AndroidLowLatencyVideoTransport.Viewer> viewerRef = new AtomicReference<>();
        AtomicInteger tcpAbortRequests = new AtomicInteger();
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        hostRef.set(host);
        DatagramSocket viewerSocket = new DatagramSocket(0);
        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                viewerSocket,
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY) {
                        return hostRef.get().markReady(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch);
                    }
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP) {
                        int reason = hostRef.get().acceptStop(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch,
                            control.lowLatencyVideoStopReason);
                        viewerRef.get().acknowledgeStopped(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch,
                            reason);
                        return true;
                    }
                    return false;
                },
                reason -> tcpAbortRequests.incrementAndGet(),
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { },
                System::nanoTime);
        viewerRef.set(viewer);
        try {
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);
            viewerSocket.close();
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.CLOSED, 3_000);
            assertEquals(0, tcpAbortRequests.get());
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void frameSilencePolicyKeepsStaticDesktopButRejectsIncompleteOrDeadRoutes() {
        assertTrue(AndroidLowLatencyVideoTransport.Viewer.shouldFallbackForFrameSilence(
            false, 2_500, -1, -1, true));
        assertTrue(AndroidLowLatencyVideoTransport.Viewer.shouldFallbackForFrameSilence(
            true, 2_500, 11, 10, true));
        assertTrue(AndroidLowLatencyVideoTransport.Viewer.shouldFallbackForFrameSilence(
            true, 2_500, 10, 10, false));
        assertTrue(!AndroidLowLatencyVideoTransport.Viewer.shouldFallbackForFrameSilence(
            true, 2_500, 10, 10, true));
        assertTrue(!AndroidLowLatencyVideoTransport.Viewer.shouldFallbackForFrameSilence(
            false, 2_499, -1, -1, false));
    }

    @Test
    public void loopbackBindsStreamsRecoversFecAndPreservesUdpMouse() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AtomicReference<AndroidLowLatencyVideoTransport.Host> hostRef = new AtomicReference<>();
        AtomicReference<AndroidLowLatencyVideoTransport.Viewer> viewerRef = new AtomicReference<>();
        CountDownLatch frameReceived = new CountDownLatch(1);
        CountDownLatch mouseApplied = new CountDownLatch(2);
        CountDownLatch mouseAcknowledged = new CountDownLatch(2);
        AtomicReference<byte[]> receivedFrame = new AtomicReference<>();
        AtomicInteger fatalFailures = new AtomicInteger();

        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOPPED) {
                        AndroidLowLatencyVideoTransport.Viewer viewer = viewerRef.get();
                        if (viewer != null) {
                            viewer.acknowledgeStopped(
                                control.lowLatencyVideoChannelId,
                                control.lowLatencyVideoEpoch,
                                control.lowLatencyVideoStopReason);
                        }
                    }
                    return true;
                },
                reason -> fatalFailures.incrementAndGet(),
                input -> {
                    mouseApplied.countDown();
                    return true;
                });
        hostRef.set(host);

        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    AndroidLowLatencyVideoTransport.Host currentHost = hostRef.get();
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY) {
                        return currentHost.markReady(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch);
                    }
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP) {
                        int committedReason = currentHost.acceptStop(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch,
                            control.lowLatencyVideoStopReason);
                        viewerRef.get().acknowledgeStopped(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch,
                            committedReason);
                        return true;
                    }
                    return false;
                },
                reason -> fatalFailures.incrementAndGet(),
                (frameKind, payload) -> {
                    receivedFrame.set(payload);
                    frameReceived.countDown();
                },
                (sequence, elapsedNanos) -> mouseAcknowledged.countDown());
        viewerRef.set(viewer);

        try {
            awaitState(host, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);

            byte[] frame = sequenceBytes(
                LowLatencyVideoProtocol.maxFragmentPayloadBytes(
                    LowLatencyVideoProtocol.DEFAULT_MAX_DATAGRAM_BYTES) * 8 + 17,
                0x31);
            assertTrue(host.offerFrame(RemoteDeskProtocol.MESSAGE_VIDEO_FRAME, frame));
            assertTrue(frameReceived.await(3, TimeUnit.SECONDS));
            assertArrayEquals(frame, receivedFrame.get());

            assertTrue(viewer.offerMouseMove(mouseMove(100, 200)));
            awaitCount(mouseApplied, 1, 2_000);
            awaitCount(mouseAcknowledged, 1, 2_000);

            long heartbeatDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
            while (!viewer.hasFreshAuthenticatedHeartbeat() &&
                System.nanoTime() < heartbeatDeadline) {
                Thread.sleep(10);
            }
            assertTrue(viewer.hasFreshAuthenticatedHeartbeat());

            viewer.requestVideoFallback(
                RemoteDeskProtocol.LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT);
            awaitState(
                host,
                AndroidLowLatencyVideoTransport.State.VIDEO_DISABLED_INPUT_ACTIVE,
                2_000);
            awaitState(
                viewer,
                AndroidLowLatencyVideoTransport.State.VIDEO_DISABLED_INPUT_ACTIVE,
                2_000);

            assertTrue(viewer.offerMouseMove(mouseMove(300, 400)));
            assertTrue(mouseApplied.await(2, TimeUnit.SECONDS));
            assertTrue(mouseAcknowledged.await(2, TimeUnit.SECONDS));

            // Simulate complete loss of the host route after video-only
            // fallback. The viewer must stop accepting UDP mouse moves so the
            // caller can fall back to the reliable TCP input path instead of
            // retaining a fake-live pointer route while TCP video still works.
            host.close();
            long staleDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(4);
            while (viewer.hasFreshAuthenticatedHeartbeat() &&
                System.nanoTime() < staleDeadline) {
                Thread.sleep(10);
            }
            assertTrue(!viewer.offerMouseMove(mouseMove(500, 600)));
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.CLOSED, 5_000);
            assertEquals(AndroidLowLatencyVideoTransport.State.CLOSED, host.state());
            assertTrue(fatalFailures.get() == 0 || fatalFailures.get() == 1);
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void missingStoppedAcknowledgementHitsFiveSecondFatalDeadline() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AtomicReference<AndroidLowLatencyVideoTransport.Host> hostRef = new AtomicReference<>();
        CountDownLatch fatal = new CountDownLatch(1);

        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        hostRef.set(host);
        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    if (control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY) {
                        return hostRef.get().markReady(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch);
                    }
                    // Stop is reported as written, but no Stopped is delivered.
                    return control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP;
                },
                reason -> fatal.countDown(),
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { });
        try {
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);
            viewer.requestVideoFallback(RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
            assertTrue(fatal.await(7, TimeUnit.SECONDS));
            assertEquals(AndroidLowLatencyVideoTransport.State.CLOSED, viewer.state());
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void unsolicitedStoppedConvergesActiveViewerImmediately() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AtomicReference<AndroidLowLatencyVideoTransport.Host> hostRef = new AtomicReference<>();
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        hostRef.set(host);
        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    RemoteDeskTransport.ControlMessage control =
                        RemoteDeskTransport.decodeControl(payload);
                    return control.kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY &&
                        hostRef.get().markReady(
                            control.lowLatencyVideoChannelId,
                            control.lowLatencyVideoEpoch);
                },
                reason -> { },
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { });
        try {
            awaitState(viewer, AndroidLowLatencyVideoTransport.State.ACTIVE, 4_000);
            LowLatencyVideoProtocol.Offer identity = host.offerForTcp();
            viewer.acknowledgeStopped(
                identity.channelId,
                identity.epoch,
                RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
            assertEquals(AndroidLowLatencyVideoTransport.State.CLOSED, viewer.state());
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void unsolicitedStoppedConvergesSetupAndWrongHostIdentityIsIgnored() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        LowLatencyVideoProtocol.Offer identity = host.offerForTcp();
        assertEquals(
            -1,
            host.acceptStop(
                identity.channelId + 1,
                identity.epoch,
                RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC));
        assertEquals(AndroidLowLatencyVideoTransport.State.WAITING_FOR_PROBE, host.state());

        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                identity,
                FEATURES,
                payload -> true,
                reason -> { },
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { });
        try {
            viewer.acknowledgeStopped(
                identity.channelId,
                identity.epoch,
                RemoteDeskProtocol.LOW_LATENCY_FALLBACK_GENERIC);
            assertEquals(AndroidLowLatencyVideoTransport.State.CLOSED, viewer.state());
        } finally {
            viewer.close();
            host.close();
        }
    }

    @Test
    public void neverReturningReadySenderDoesNotMakeCloseUnbounded() throws Exception {
        InetAddress loopback = InetAddress.getLoopbackAddress();
        CountDownLatch readyEntered = new CountDownLatch(1);
        CountDownLatch releaseReady = new CountDownLatch(1);
        AndroidLowLatencyVideoTransport.Host host =
            new AndroidLowLatencyVideoTransport.Host(
                loopback,
                FEATURES,
                payload -> true,
                reason -> { },
                input -> true);
        AndroidLowLatencyVideoTransport.Viewer viewer =
            new AndroidLowLatencyVideoTransport.Viewer(
                loopback,
                host.offerForTcp(),
                FEATURES,
                payload -> {
                    if ((payload[0] & 0xFF) == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY) {
                        readyEntered.countDown();
                        releaseReady.await();
                    }
                    return true;
                },
                reason -> { },
                (kind, payload) -> { },
                (sequence, elapsedNanos) -> { });
        try {
            assertTrue(readyEntered.await(3, TimeUnit.SECONDS));
            long startedAt = System.nanoTime();
            viewer.close();
            long elapsedMillis = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - startedAt);
            assertTrue("close took " + elapsedMillis + " ms", elapsedMillis < 1_000);
            assertEquals(AndroidLowLatencyVideoTransport.State.CLOSED, viewer.state());
        } finally {
            releaseReady.countDown();
            viewer.close();
            host.close();
        }
    }

    private static void awaitState(
        AndroidLowLatencyVideoTransport.Host transport,
        AndroidLowLatencyVideoTransport.State expected,
        long timeoutMillis) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        while (transport.state() != expected && System.nanoTime() < deadline) {
            Thread.sleep(10);
        }
        assertEquals(expected, transport.state());
    }

    private static void awaitState(
        AndroidLowLatencyVideoTransport.Viewer transport,
        AndroidLowLatencyVideoTransport.State expected,
        long timeoutMillis) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        while (transport.state() != expected && System.nanoTime() < deadline) {
            Thread.sleep(10);
        }
        assertEquals(expected, transport.state());
    }

    private static void awaitCount(
        CountDownLatch latch,
        long remaining,
        long timeoutMillis) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
        while (latch.getCount() > remaining && System.nanoTime() < deadline) {
            Thread.sleep(5);
        }
        assertEquals(remaining, latch.getCount());
    }

    private static byte[] mouseMove(int x, int y) throws Exception {
        return RemoteDeskTransport.encodeInput(
            RemoteDeskProtocol.INPUT_MOUSE_MOVE,
            RemoteDeskProtocol.MOUSE_NONE,
            x,
            y,
            0);
    }

    private static LowLatencyVideoProtocol.FeedbackV2 feedback(
        long receiverElapsed,
        long authenticatedPackets,
        long settledLostPackets) {
        return new LowLatencyVideoProtocol.FeedbackV2(
            receiverElapsed,
            authenticatedPackets,
            0,
            LowLatencyVideoProtocol.FEEDBACK_METRIC_PACKET_DELIVERY |
                LowLatencyVideoProtocol.FEEDBACK_METRIC_FRAME_ASSEMBLY,
            authenticatedPackets,
            authenticatedPackets * 100,
            settledLostPackets,
            0,
            1,
            1,
            0,
            0,
            0,
            0,
            0);
    }

    private static byte[] sequenceBytes(int length, int start) {
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++) {
            bytes[index] = (byte) (start + index);
        }
        return bytes;
    }
}
