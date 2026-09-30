package com.remotedesk.agent;

import java.net.InetAddress;
import java.net.Inet6Address;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Arrays;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidIpv6Test {
    @Test public void localGlobalAddressDoesNotLeakInterfaceScope() throws Exception {
        Inet6Address local = Inet6Address.getByAddress(null, InetAddress.getByName("2001:db8::1").getAddress(), 19);
        assertEquals("2001:db8::1", AndroidRelayAddresses.localAddress(local));
        assertNull(AndroidRelayAddresses.normalizeAddress(local.getHostAddress()));
        assertNull(AndroidRelayAddresses.localAddress(Inet6Address.getByAddress(null,
            InetAddress.getByName("fe80::1").getAddress(), 19)));
    }

    @Test public void manyVirtualAdaptersDoNotHideIpv6() {
        java.util.List<String> values = new java.util.ArrayList<>();
        for (int i = 1; i <= 24; i++) values.add("192.0.2." + i);
        values.add("2001:db8::1");
        java.util.List<String> result = AndroidRelayAddresses.normalize(values);
        assertEquals(8, result.size()); assertEquals("2001:db8::1", result.get(7));
    }

    @Test public void hintsNormalizeAndDisplayUnambiguously() {
        assertEquals(Arrays.asList("2001:db8::1", "fd12::abcd"), AndroidRelayAddresses.normalize(Arrays.asList(
            "2001:0DB8:0:0:0:0:0:1", "2001:db8::1", "fd12::ABCD")));
        assertEquals("[2001:db8::1]:40565", AndroidRelayAddresses.endpoint("2001:db8::1", 40565));
        for (String value : new String[]{"::", "::1", "::2", "::ffff:192.0.2.1", "fe80::1", "fe80::1%19",
                "2001:db8::1%19", "ff02::1", "fec0::1", "[2001:db8::1]", "2001:db8::1\n"})
            assertNull(value, AndroidRelayAddresses.normalizeAddress(value));
    }

    @Test public void historyDiscoveryAcceptsLiteralIpv6WithoutResolvingNames() throws Exception {
        assertEquals(InetAddress.getByName("2001:db8::1"), AndroidLanDiscovery.literalAddress("2001:db8::1"));
        assertNull(AndroidLanDiscovery.literalAddress("host.invalid"));
        assertNull(AndroidLanDiscovery.literalAddress("ff02::1"));
    }

    @Test public void realTcpWorksInBothFamiliesAndReturnsUsableBlockingSocket() throws Exception {
        for (String address : new String[]{"127.0.0.1", "::1"}) {
            InetAddress ip = InetAddress.getByName(address);
            try (ServerSocket listener = new ServerSocket(0, 8, ip);
                    AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
                    Socket connected = AndroidTcpConnector.connect(new InetAddress[]{ip}, listener.getLocalPort(),
                        System.nanoTime() + 5_000_000_000L, dial, socket -> {})) {
                listener.setSoTimeout(3000);
                try (Socket peer = listener.accept()) {
                    peer.getOutputStream().write(42); peer.getOutputStream().flush();
                    connected.setSoTimeout(3000);
                    assertEquals(42, connected.getInputStream().read());
                }
            }
        }
    }

    @Test public void deadIpv6FallsBackAndCancelledDialDoesNotConnect() throws Exception {
        try (ServerSocket listener = new ServerSocket(0, 8, InetAddress.getByName("127.0.0.1"));
                AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
                Socket connected = AndroidTcpConnector.connect(new InetAddress[]{InetAddress.getByName("::1"), InetAddress.getByName("127.0.0.1")},
                    listener.getLocalPort(), System.nanoTime() + 5_000_000_000L, dial, socket -> {})) {
            listener.setSoTimeout(3000);
            try (Socket peer = listener.accept()) { assertTrue(connected.isConnected()); }
        }
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            dial.close();
            assertThrows(java.io.IOException.class, () -> AndroidTcpConnector.connect(new InetAddress[]{InetAddress.getByName("::1")},
                1, System.nanoTime() + 5_000_000_000L, dial, socket -> fail("Cancelled")));
        }
    }
}
