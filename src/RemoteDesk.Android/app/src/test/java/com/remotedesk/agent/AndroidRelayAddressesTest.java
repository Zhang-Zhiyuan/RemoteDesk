package com.remotedesk.agent;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayAddressesTest {
    @Test public void acceptsCanonicalUnicastIpv4WithoutDns() {
        for (String value : new String[] {"10.1.2.3", "192.168.1.2", "100.64.0.1", "198.51.100.42"})
            assertTrue(value, AndroidRelayAddresses.usable(value));
    }

    @Test public void rejectsUnusableOrAmbiguousAddresses() {
        for (String value : new String[] {null, "", "localhost", "127.0.0.1", "0.1.2.3", "169.254.1.2", "224.0.0.1",
            "255.255.255.255", "::1", "::ffff:10.1.2.3", "010.1.2.3", "10.1.2.3:1234", "10.1.2.3\n", "127.1", "0x7f000001"})
            assertFalse(value, AndroidRelayAddresses.usable(value));
    }

    @Test public void boundedDeduplicatedImmutableReport() {
        List<String> values = new ArrayList<>(Arrays.asList("10.1.2.3", "10.1.2.3", null, "bad"));
        for (int i = 1; i < 40; i++) values.add("192.0.2." + i);
        List<String> result = AndroidRelayAddresses.normalize(values);
        assertEquals(8, result.size());
        assertEquals("10.1.2.3", result.get(0));
        assertEquals(8, new java.util.HashSet<>(result).size());
        assertThrows(UnsupportedOperationException.class, () -> result.add("192.0.2.50"));
    }

    @Test public void legacyDevicesAndCustomPortsRemainUsable() {
        AndroidRelay.Device old = new AndroidRelay.Device("id", "host", "Windows", false);
        assertTrue(old.addressDisplay().contains("仍可中转"));
        AndroidRelay.Device device = new AndroidRelay.Device("id", "host", "Linux", false,
            Arrays.asList("192.0.2.4", "192.0.2.4", "localhost"), 40565);
        assertEquals("192.0.2.4:40565", device.addressDisplay());
    }
}
