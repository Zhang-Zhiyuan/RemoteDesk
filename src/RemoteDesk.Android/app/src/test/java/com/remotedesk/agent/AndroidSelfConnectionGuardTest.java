package com.remotedesk.agent;

import java.net.InetAddress;
import java.net.Inet6Address;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidSelfConnectionGuardTest {
    private static final String SELF = "00112233-4455-6677-8899-aabbccddeeff";
    private static final String OTHER = "10112233-4455-6677-8899-aabbccddeeff";

    private InetAddress ip(int a, int b, int c, int d) throws Exception {
        return InetAddress.getByAddress(new byte[]{(byte)a, (byte)b, (byte)c, (byte)d});
    }
    private Inet6Address mapped(InetAddress address) throws Exception {
        byte[] bytes = new byte[16]; bytes[10] = (byte)255; bytes[11] = (byte)255;
        System.arraycopy(address.getAddress(), 0, bytes, 12, 4);
        return Inet6Address.getByAddress(null, bytes, 0);
    }
    private Socket connected(InetAddress remote, InetAddress local) {
        return new Socket() {
            @Override public InetAddress getInetAddress() { return remote; }
            @Override public InetAddress getLocalAddress() { return local; }
            @Override public boolean isConnected() { return true; }
        };
    }

    @Test public void productIncludingDebugCannotOptIntoLoopbackFixtures() {
        assertFalse(BuildConfig.ALLOW_LOOPBACK_FIXTURES);
    }

    @Test public void allIpv4LoopbackAndUnspecifiedFormsAreRejected() throws Exception {
        for (InetAddress address : Arrays.asList(ip(127,0,0,1), ip(127,42,5,9), ip(0,0,0,0),
                mapped(ip(127,0,0,1)), mapped(ip(0,0,0,0))))
            assertTrue(AndroidSelfConnectionGuard.isSelf(address, Collections.emptyList()));
    }

    @Test public void ipv6LoopbackAndUnspecifiedAreRejected() throws Exception {
        byte[] bytes = new byte[16];
        assertTrue(AndroidSelfConnectionGuard.isSelf(Inet6Address.getByAddress(null,bytes,0), Collections.emptyList()));
        bytes[15] = 1;
        assertTrue(AndroidSelfConnectionGuard.isSelf(Inet6Address.getByAddress(null,bytes,0), Collections.emptyList()));
    }

    @Test public void allInterfacesAreComparedNotJustSelectedRoute() throws Exception {
        InetAddress wifi = ip(192,168,1,2), vpn = ip(10,7,2,3), other = ip(10,7,2,4);
        assertTrue(AndroidSelfConnectionGuard.isSelf(vpn, Arrays.asList(wifi,vpn)));
        assertTrue(AndroidSelfConnectionGuard.isSelf(mapped(vpn), Arrays.asList(wifi,vpn)));
        assertTrue(AndroidSelfConnectionGuard.isSelf(vpn, Arrays.asList(wifi,mapped(vpn))));
        assertFalse(AndroidSelfConnectionGuard.isSelf(other, Arrays.asList(wifi,vpn)));
    }

    @Test public void sameAddressDoesNotRelyOnHostnamesOrIpv6TextFormatting() throws Exception {
        byte[] bytes = new byte[16]; bytes[0] = 0x20; bytes[1] = 1; bytes[15] = 42;
        InetAddress first = Inet6Address.getByAddress("one.example",bytes,0);
        InetAddress second = Inet6Address.getByAddress("different.example",bytes,0);
        assertTrue(AndroidSelfConnectionGuard.isSelf(first, Collections.singletonList(second)));
    }

    @Test public void linkLocalAddressesRespectExplicitInterfaceScope() throws Exception {
        byte[] bytes = new byte[16]; bytes[0]=(byte)0xfe; bytes[1]=(byte)0x80; bytes[15]=42;
        InetAddress first = Inet6Address.getByAddress(null,bytes,3);
        assertTrue(AndroidSelfConnectionGuard.isSelf(first, Collections.singletonList(Inet6Address.getByAddress(null,bytes,3))));
        assertTrue(AndroidSelfConnectionGuard.isSelf(first, Collections.singletonList(Inet6Address.getByAddress(null,bytes,0))));
        assertFalse(AndroidSelfConnectionGuard.isSelf(first, Collections.singletonList(Inet6Address.getByAddress(null,bytes,4))));
    }

    @Test public void unconnectedSocketFailsClosedWithoutAuthentication() throws Exception {
        try (Socket socket = new Socket()) {
            assertThrows(AndroidSelfConnectionGuard.VerificationFailed.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, false));
        }
    }

    @Test public void offlineLoopbackTargetIsRejectedBeforeAttemptingAConnection() throws Exception {
        assertThrows(AndroidSelfConnectionGuard.Rejected.class,
            () -> AndroidSelfConnectionGuard.requireResolvedRemote(InetAddress.getByName("localhost"), false));
        AndroidSelfConnectionGuard.requireResolvedRemote(null, false);
        AndroidSelfConnectionGuard.requireResolvedRemote(ip(127,0,0,1), true);
    }

    @Test public void actualHostnameSelectedLoopbackIsRejectedBeforeAnyOutboundBytes() throws Exception {
        InetAddress loopback = InetAddress.getByName("localhost");
        assertTrue("The fixture only binds a loopback address", loopback.isLoopbackAddress());
        try (ServerSocket listener = new ServerSocket(0, 1, loopback)) {
            listener.setSoTimeout(2000);
            Socket client = new Socket("localhost", listener.getLocalPort());
            try (Socket accepted = listener.accept()) {
                accepted.setSoTimeout(2000);
                try (Socket closeClient = client) {
                    assertThrows(AndroidSelfConnectionGuard.Rejected.class,
                        () -> AndroidSelfConnectionGuard.requireRemote(closeClient, false));
                }
                assertEquals("No AUTH or device key may be sent", -1, accepted.getInputStream().read());
            }
        }
    }

    @Test public void isolatedFixtureOptInOnlyAllowsActualLoopbackNotLocalInterfaces() throws Exception {
        InetAddress local = ip(10,0,0,9);
        try (Socket socket = connected(ip(127,0,0,1), local)) {
            AndroidSelfConnectionGuard.requireRemote(socket, true);
        }
        try (Socket socket = connected(local, local)) {
            assertThrows(AndroidSelfConnectionGuard.Rejected.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, true));
        }
    }

    @Test public void externalPeerIsAllowedAndNoCredentialOrSocketWriteIsNeeded() throws Exception {
        try (Socket socket = connected(ip(192,0,2,42), ip(10,0,0,9))) {
            AndroidSelfConnectionGuard.requireRemote(socket, false,
                () -> Collections.singletonList(socket.getLocalAddress()));
        }
    }

    @Test public void actualPeerCheckIncludesOtherInterfacesAndFailsClosedIfEnumerationFails() throws Exception {
        InetAddress local = ip(10,0,0,9), peer = ip(192,168,0,3);
        try (Socket socket = connected(peer, local)) {
            assertThrows(AndroidSelfConnectionGuard.Rejected.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, false, () -> Arrays.asList(local,peer)));
            assertThrows(AndroidSelfConnectionGuard.VerificationFailed.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, false,
                    () -> { throw new java.net.SocketException("synthetic failure"); }));
            assertThrows(AndroidSelfConnectionGuard.VerificationFailed.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, false,
                    () -> { throw new SecurityException("synthetic denial"); }));
            assertThrows(AndroidSelfConnectionGuard.VerificationFailed.class,
                () -> AndroidSelfConnectionGuard.requireRemote(socket, false, Collections::emptyList));
        }
    }

    @Test public void matchingDeviceIdentityIsRejectedWithoutNameOrEndpointGuessing() {
        assertTrue(AndroidSelfConnectionGuard.sameDevice(SELF.toUpperCase(java.util.Locale.ROOT), SELF));
        assertFalse(AndroidSelfConnectionGuard.sameDevice("", ""));
        assertFalse(AndroidSelfConnectionGuard.sameDevice("not-a-guid", SELF));
        assertFalse(AndroidSelfConnectionGuard.sameDevice(OTHER, SELF));
        assertThrows(AndroidSelfConnectionGuard.Rejected.class,
            () -> AndroidSelfConnectionGuard.requireOtherDevice(SELF, SELF));
        AndroidSelfConnectionGuard.requireOtherDevice(OTHER, SELF);
    }

    @Test public void knownHistoryBlocksSelfButExplicitEndpointReplacementIgnoresStaleDirectIdentity() {
        AndroidConnectionHistory.Node node = new AndroidConnectionHistory().remember("", "former-endpoint", 56565,
            "", "synthetic", "", "PC", 1, SELF);
        assertTrue(AndroidSelfConnectionGuard.knownSelf(node, false, SELF));
        assertFalse(AndroidSelfConnectionGuard.knownSelf(node, true, SELF));
    }

    @Test public void relayHistoryUsesTargetIdentityAndCannotUseDirectRedirectToEvadeIt() {
        AndroidConnectionHistory.Node node = new AndroidConnectionHistory().remember("", "relay.example", 56567,
            SELF, "synthetic", "synthetic-relay-config", "PC", 1, OTHER);
        assertTrue(AndroidSelfConnectionGuard.knownSelf(node, false, SELF));
        assertTrue(AndroidSelfConnectionGuard.knownSelf(node, true, SELF));
        assertFalse(AndroidSelfConnectionGuard.knownSelf(node, false, OTHER));
    }

    @Test public void selfRejectionIsNonretryableAndExplainsTheRealReason() {
        Throwable failure = new AndroidSelfConnectionGuard.Rejected();
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(failure));
        assertEquals(AndroidSelfConnectionGuard.SELF_MESSAGE, AndroidViewerStatusText.connectionFailure(failure));
        Throwable unavailable = new AndroidSelfConnectionGuard.VerificationFailed();
        assertFalse(AndroidViewerReconnectPolicy.isRetryable(unavailable));
        assertEquals(AndroidSelfConnectionGuard.CHECK_FAILED_MESSAGE, AndroidViewerStatusText.connectionFailure(unavailable));
    }
}
