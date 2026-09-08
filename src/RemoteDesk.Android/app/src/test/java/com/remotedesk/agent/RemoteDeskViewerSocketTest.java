package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import java.net.Socket;
import java.net.SocketException;
import java.util.Collections;

import org.junit.Test;

public final class RemoteDeskViewerSocketTest {
    @Test
    public void viewerAdvertisesAuthenticatedUdpBaselineWithoutH264Decoder() {
        int capabilities = RemoteDeskViewerActivity.advertisedViewerCapabilities(
            Collections.emptyList());
        int expected = RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT |
            RemoteDeskProtocol.CAPABILITY_HIGH_QUALITY_JPEG;
        assertEquals(expected, capabilities & expected);
    }

    @Test
    public void viewerSocketUsesLowLatencyOptionsAndBoundedFrameBuffer() {
        RecordingSocket socket = new RecordingSocket();

        RemoteDeskViewerActivity.configureViewerSocket(socket);

        assertTrue(socket.tcpNoDelay);
        assertTrue(socket.keepAlive);
        assertEquals(
            AndroidVideoStreamSettings.VIEWER_RECEIVE_BUFFER_BYTES,
            socket.receiveBufferBytes);
    }

    @Test
    public void viewerSocketIgnoresOptionalTuningFailures() {
        RemoteDeskViewerActivity.configureViewerSocket(new ThrowingSocket());
    }

    private static final class RecordingSocket extends Socket {
        boolean tcpNoDelay;
        boolean keepAlive;
        int receiveBufferBytes;

        @Override
        public void setTcpNoDelay(boolean on) {
            tcpNoDelay = on;
        }

        @Override
        public void setKeepAlive(boolean on) {
            keepAlive = on;
        }

        @Override
        public void setReceiveBufferSize(int size) {
            receiveBufferBytes = size;
        }
    }

    private static final class ThrowingSocket extends Socket {
        @Override
        public void setTcpNoDelay(boolean on) throws SocketException {
            throw new SocketException("tcp no delay unavailable");
        }

        @Override
        public void setKeepAlive(boolean on) throws SocketException {
            throw new SocketException("keep alive unavailable");
        }

        @Override
        public void setReceiveBufferSize(int size) throws SocketException {
            throw new SocketException("receive buffer unavailable");
        }
    }
}
