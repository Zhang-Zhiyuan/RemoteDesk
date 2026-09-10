package com.remotedesk.agent;

import java.io.IOException;
import java.net.InetAddress;
import java.util.ArrayList;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayAddressFallbackTest {
    private static InetAddress ip(String value) throws Exception { return InetAddress.getByName(value); }

    @Test public void dnsFamiliesAreInterleavedAndAttemptsAreBounded() throws Exception {
        InetAddress v4 = ip("8.138.5.232");
        List<InetAddress> result = AndroidRelay.relayAddresses(new InetAddress[] {
            ip("2001:db8::1"), ip("2001:db8::2"), ip("2001:db8::3"), ip("2001:db8::4"), v4, v4
        }, false);
        assertEquals(4, result.size());
        assertEquals(v4, result.get(1));
    }

    @Test public void normalSystemRouteRetainsPrivateAddresses() throws Exception {
        InetAddress privateAddress = ip("10.7.163.74");
        assertEquals(List.of(privateAddress), AndroidRelay.relayAddresses(new InetAddress[] {privateAddress}, false));
    }

    @Test public void optionalPathsRejectMixedOrNonPublicIpv4Dns() throws Exception {
        for (String invalid : List.of("10.7.163.74", "127.0.0.1", "192.168.1.1", "169.254.1.2",
                "100.64.1.1", "100.127.1.1", "0.1.2.3", "224.0.0.1", "255.255.255.255")) {
            InetAddress[] mixed = {ip("8.138.5.232"), ip(invalid)};
            assertThrows(IOException.class, () -> AndroidRelay.relayAddresses(mixed, true));
        }
        assertTrue(AndroidRelay.relayAddresses(new InetAddress[] {ip("2001:db8::1"), ip("fd00::1")}, true).isEmpty());
    }

    @Test public void unreachableFirstDnsAddressDoesNotHideWorkingFallback() throws Exception {
        List<InetAddress> addresses = List.of(ip("2001:db8::1"), ip("8.138.5.232"));
        List<InetAddress> attempts = new ArrayList<>();
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            String result = AndroidRelay.connectAddresses(addresses, dial, (address, timeout) -> {
                attempts.add(address);
                assertTrue(timeout < AndroidRelay.TIMEOUT_MS);
                if (address.equals(addresses.get(0))) throw new java.net.SocketTimeoutException("unreachable IPv6");
                return "pinned TLS";
            });
            assertEquals("pinned TLS", result);
            assertEquals(addresses, attempts);
        }
    }

    @Test public void allAddressFailuresRetainCertificateRejection() throws Exception {
        List<InetAddress> addresses = List.of(ip("8.138.5.232"), ip("1.1.1.1"));
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(AndroidRelay.IdentityFailure.class, () -> AndroidRelay.connectAddresses(addresses, dial,
                (address, timeout) -> {
                    if (address.equals(addresses.get(0))) throw new AndroidRelay.IdentityFailure(true, null);
                    throw new IOException("unreachable");
                }));
        }
    }

    @Test public void cancellationStopsAddressFallbackAndSingleAddressKeepsFullBudget() throws Exception {
        List<InetAddress> addresses = List.of(ip("8.138.5.232"), ip("1.1.1.1"));
        List<InetAddress> attempts = new ArrayList<>();
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            assertThrows(IOException.class, () -> AndroidRelay.connectAddresses(addresses, dial, (address, timeout) -> {
                attempts.add(address); dial.close(); throw new IOException("cancelled");
            }));
            assertEquals(1, attempts.size());
        }
        try (AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial()) {
            AndroidRelay.connectAddresses(List.of(addresses.get(0)), dial, (address, timeout) -> {
                assertEquals(AndroidRelay.TIMEOUT_MS, timeout); return "ok";
            });
        }
    }
}
