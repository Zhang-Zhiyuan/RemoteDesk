package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

public final class AndroidRelayTest {
    @Test public void smallHostQueueIsLimitedToLoopback() throws Exception {
        for (String address : new String[] {"127.0.0.1", "127.0.0.2", "::1", "::ffff:127.0.0.1"}) {
            assertEquals(16384, AndroidRelay.hostSendBufferBytes(java.net.InetAddress.getByName(address), 131072));
        }
        for (String address : new String[] {"10.7.163.74", "8.138.5.232", "::ffff:10.7.163.74"}) {
            assertEquals(131072, AndroidRelay.hostSendBufferBytes(java.net.InetAddress.getByName(address), 131072));
        }
        assertEquals(131072, AndroidRelay.hostSendBufferBytes(null, 131072));
    }

    @Test public void loopbackAdapterBoundsReadAheadAndWrites() throws Exception {
        try (java.net.Socket socket = new java.net.Socket()) {
            AndroidRelay.configureLoopbackSocket(socket);
            assertEquals(16384, socket.getReceiveBufferSize());
            assertEquals(16384, socket.getSendBufferSize());
            assertTrue(socket.getTcpNoDelay());
            assertEquals(16384, AndroidRelay.COPY_BUFFER_BYTES);
        }
    }

    @Test public void unsupportedLoopbackOptionsDoNotAbortConnection() throws Exception {
        try (java.net.Socket socket = new java.net.Socket() {
            @Override public void setReceiveBufferSize(int size) throws java.net.SocketException { throw new java.net.SocketException(); }
            @Override public void setSendBufferSize(int size) throws java.net.SocketException { throw new java.net.SocketException(); }
            @Override public void setTcpNoDelay(boolean value) throws java.net.SocketException { throw new java.net.SocketException(); }
        }) {
            AndroidRelay.configureLoopbackSocket(socket);
        }
    }

    private static final String ID = "4d623300-3405-4513-99c0-ef37df5d1f21";
    private static final String TOKEN = "relay-test-access-key-".repeat(3);
    private static final String PIN = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

    private static final class CountingOutput extends ByteArrayOutputStream {
        int writes, flushes;
        @Override public void write(byte[] bytes) throws IOException {
            writes++;
            super.write(bytes);
        }
        @Override public void flush() { flushes++; }
    }

    @Test public void jsonLengthAndUtf8PayloadUseOneWrite() throws Exception {
        for (String json : new String[] {"{\"type\":\"heartbeat\"}", "{\"machineName\":\"广州电脑\"}"}) {
            byte[] payload = json.getBytes(StandardCharsets.UTF_8);
            CountingOutput output = new CountingOutput();
            AndroidRelay.writeJsonPayload(output, payload);
            assertEquals(1, output.writes);
            assertEquals(1, output.flushes);
            ByteBuffer frame = ByteBuffer.wrap(output.toByteArray());
            assertEquals(payload.length, frame.getInt());
            byte[] actual = new byte[frame.remaining()];
            frame.get(actual);
            assertArrayEquals(payload, actual);
        }
    }

    @Test public void invalidJsonSizeDoesNotWriteAnything() {
        for (int length : new int[] {0, AndroidRelay.MAX_JSON + 1}) {
            CountingOutput output = new CountingOutput();
            assertThrows(IOException.class, () -> AndroidRelay.writeJsonPayload(output, new byte[length]));
            assertEquals(0, output.writes);
            assertEquals(0, output.flushes);
            assertEquals(0, output.size());
        }
    }

    @Test public void maximumJsonSizeRetainsItsExactFrame() throws Exception {
        byte[] payload = new byte[AndroidRelay.MAX_JSON];
        java.util.Arrays.fill(payload, (byte) 'x');
        CountingOutput output = new CountingOutput();
        AndroidRelay.writeJsonPayload(output, payload);
        assertEquals(payload.length + 4, output.size());
        assertEquals(payload.length, ByteBuffer.wrap(output.toByteArray()).getInt());
        assertEquals(1, output.writes);
    }

    @Test public void optionsNormalizeWithoutExposingToken() {
        AndroidRelay.Options value = new AndroidRelay.Options(" example.test ", 56567,
            TOKEN, PIN.toLowerCase(), ID.toUpperCase(), true);
        assertEquals("example.test", value.serverAddress);
        assertEquals(PIN, value.tlsCertificateSha256);
        assertEquals(ID, value.deviceId);
        assertFalse(value.toString().contains(TOKEN));
        String other = UUID.randomUUID().toString();
        assertEquals(other, value.target(other).deviceId);
        assertEquals(ID, value.deviceId);
    }

    @Test public void invalidEndpointAndCredentialsAreRejected() {
        for (String server : new String[] {"", "https://example.test", "name@host", "host/path", "two hosts"}) {
            assertThrows(IllegalArgumentException.class, () ->
                new AndroidRelay.Options(server, 56567, TOKEN, PIN, ID, true));
        }
        for (int port : new int[] {-1, 0, 65536}) assertThrows(IllegalArgumentException.class, () ->
            new AndroidRelay.Options("host", port, TOKEN, PIN, ID, true));
        assertThrows(IllegalArgumentException.class, () -> new AndroidRelay.Options("host", 56567, "short", PIN, ID, true));
        assertThrows(IllegalArgumentException.class, () -> new AndroidRelay.Options("host", 56567, TOKEN, "wrong", ID, true));
        assertThrows(IllegalArgumentException.class, () -> new AndroidRelay.Options("host", 56567, TOKEN, PIN, "not-a-uuid", true));
    }

    @Test public void certificatePinMustMatchExactDerBytes() throws Exception {
        byte[] bytes = "abc".getBytes(StandardCharsets.UTF_8);
        assertTrue(AndroidRelay.certificateMatches(bytes, PIN));
        assertFalse(AndroidRelay.certificateMatches("abd".getBytes(StandardCharsets.UTF_8), PIN));
        assertFalse(AndroidRelay.certificateMatches(bytes, "0".repeat(64)));
        assertFalse(AndroidRelay.certificateMatches(null, PIN));
        assertFalse(AndroidRelay.certificateMatches(bytes, "invalid"));
    }

    @Test public void relayRemovesEveryUdpCapabilityWithoutRemovingVideo() {
        int udp = RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT;
        int relay = RemoteDeskViewerActivity.relayViewerCapabilities(0x7fffffff);
        assertEquals(0, relay & udp);
        assertEquals(0x7fffffff & ~udp, relay);
    }

    @Test public void networkHandshakeFailureIsNotMisclassifiedAsChangedIdentity() {
        javax.net.ssl.SSLHandshakeException network = new javax.net.ssl.SSLHandshakeException("peer EOF");
        network.initCause(new java.io.EOFException());
        assertFalse(AndroidRelay.isIdentityFailure(network));
        javax.net.ssl.SSLHandshakeException identity = new javax.net.ssl.SSLHandshakeException("chain rejected");
        identity.initCause(new java.security.cert.CertificateException("wrong pinned certificate"));
        assertTrue(AndroidRelay.isIdentityFailure(identity));
    }

    @Test public void certificateFailureNamesRelayIdentityInsteadOfRemotePassword() {
        AndroidRelay.IdentityFailure failure = new AndroidRelay.IdentityFailure(true, new java.security.cert.CertificateException("untrusted details"));
        String display = AndroidViewerStatusText.connectionFailure(failure);
        assertTrue(display.contains("证书指纹"));
        assertFalse(display.contains("口令"));
        assertFalse(display.contains("untrusted details"));
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(failure));
    }

    @Test public void accessKeyFailureIsDistinctFromRemotePasswordFailure() {
        String display = AndroidViewerStatusText.connectionFailure(new AndroidRelay.IdentityFailure(false, null));
        assertTrue(display.contains("中转访问密钥被拒绝"));
        assertFalse(display.contains("口令"));
    }
}
