package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

public final class AndroidRelayTest {
    private static final String ID = "4d623300-3405-4513-99c0-ef37df5d1f21";
    private static final String TOKEN = "relay-test-access-key-".repeat(3);
    private static final String PIN = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

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
