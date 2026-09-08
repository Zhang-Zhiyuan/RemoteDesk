package com.remotedesk.agent;

import java.io.ByteArrayInputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidLanDiscoveryTest {
    private AndroidConnectionHistory.Node node(String host, int port, String name) {
        return new AndroidConnectionHistory().remember("", host, port, "", "test", "", name, 1);
    }
    private AndroidLanDevice device(String host, int port, String name) {
        return new AndroidLanDevice(host, port, name, "Windows", true, true);
    }

    @Test public void portsMatchDesktopDefaultsAndAutomaticFallbacks() {
        assertArrayEquals(new int[]{56566,40566}, AndroidLanDiscovery.DISCOVERY_PORTS);
        assertArrayEquals(new int[]{56565,40565,40567}, AndroidLanDiscovery.COMPATIBLE_HOST_PORTS);
    }
    @Test public void customAdvertisedPortIsNotReplacedByTheDefault() {
        assertEquals("10.0.0.8:12345", device("10.0.0.8",12345,"PC").address());
    }
    @Test public void missingPortIsDistinctFromAnExplicitPortIncludingIpv6() {
        for (String value : new String[]{"10.0.0.8","pc.local","10.0.0.8:","[fe80::1]","fe80::1"})
            assertFalse(AndroidLanDevice.explicitPort(value));
        for (String value : new String[]{"10.0.0.8:40565","pc.local:12345","[fe80::1]:12345"})
            assertTrue(AndroidLanDevice.explicitPort(value));
    }
    @Test public void invalidExplicitPortCannotBeMistakenForMissingPort() {
        assertTrue(AndroidLanDevice.explicitPort("10.0.0.8:wrong"));
    }
    @Test public void hostListDoesNotResolveNamesOrProbeInvalidIpLiterals() {
        for (String host : new String[]{"server.invalid","1.2.3.999","1.2.3","1.2.3.4.5","1.2.3.-1","http://1.2.3.4","0.0.0.0","224.0.0.1","255.255.255.255"})
            assertNull(AndroidLanDiscovery.literalIpv4(host));
        assertEquals("10.7.9.79", AndroidLanDiscovery.literalIpv4("10.7.9.79").getHostAddress());
    }
    @Test public void exactEndpointWinsOverAnotherSameNamedMachine() {
        AndroidLanDevice exact = device("10.0.0.8",40565,"PC");
        assertEquals(Collections.singletonList(exact), AndroidLanDevice.candidates(node("10.0.0.8",40565,"PC"),
            Arrays.asList(device("10.0.0.9",12345,"PC"),exact)));
    }
    @Test public void sameAddressFindsChangedPort() {
        AndroidLanDevice moved = device("10.0.0.8",40565,"PC");
        assertEquals(Collections.singletonList(moved), AndroidLanDevice.candidates(node("10.0.0.8",56565,"PC"), Collections.singletonList(moved)));
    }
    @Test public void sameNameFindsChangedIpButDoesNotResolveAmbiguity() {
        AndroidLanDevice a=device("10.0.0.9",40565,"PC"), b=device("10.0.0.10",12345,"PC");
        assertEquals(2, AndroidLanDevice.candidates(node("10.0.0.8",56565,"PC"),Arrays.asList(a,b)).size());
    }
    @Test public void nameCannotMatchANonAdvertisedBannerOrAnOfflineHost() {
        assertTrue(AndroidLanDevice.candidates(node("10.0.0.8",56565,"PC"),Arrays.asList(
            new AndroidLanDevice("10.0.0.9",40565,"PC","",true,false),
            new AndroidLanDevice("10.0.0.10",40565,"PC","",false,true))).isEmpty());
    }
    @Test public void relayNodesAreNotChangedByLanDiscovery() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        AndroidConnectionHistory.Node relay = history.remember("","10.0.0.8",56567,
            "00000000-0000-0000-0000-000000000001","test","relay-config","PC",1);
        assertTrue(AndroidLanDevice.candidates(relay,Collections.singletonList(device("10.0.0.8",56567,"PC"))).isEmpty());
        assertNull(AndroidLanDevice.exact(history.entries(),device("10.0.0.8",56567,"PC")));
    }
    @Test public void exactSavedNodeLookupKeepsOtherPortsSeparate() {
        AndroidConnectionHistory.Node node = node("10.0.0.8",40565,"PC");
        assertNull(AndroidLanDevice.exact(Collections.singletonList(node),device("10.0.0.8",56565,"PC")));
        assertEquals(node,AndroidLanDevice.exact(Collections.singletonList(node),device("10.0.0.8",40565,"PC")));
    }
    @Test public void namesCannotInjectNewlinesOrBidiControlsIntoConnectionDialogs() {
        AndroidLanDevice device=device("10.0.0.8",12345,"PC\n\r\u202e\u0000Name");
        assertEquals("PCName",device.name);
        assertEquals(96,device("10.0.0.8",12345,"x".repeat(200)).name.length());
    }
    @Test public void bannerProbeReadsOnlyFourBytesAndCannotSendAuthentication() throws Exception {
        ByteArrayInputStream input=new ByteArrayInputStream("RDK1nonce-is-not-read".getBytes(StandardCharsets.US_ASCII));
        assertTrue(AndroidLanDiscovery.readBanner(input)); assertEquals('n',input.read());
    }
    @Test public void bannerProbeRejectsUnrelatedServicesAndTruncation() throws Exception {
        for(String text:new String[]{"","R","RDK","HTTP/1.1","SSH-2.0","RDK2"})
            assertFalse(AndroidLanDiscovery.readBanner(new ByteArrayInputStream(text.getBytes(StandardCharsets.US_ASCII))));
    }
    @Test public void bannerProbeAcceptsFragmentedReads() throws Exception {
        InputStream oneByteAtATime=new InputStream(){int offset; final byte[] bytes={'R','D','K','1'};
            @Override public int read(){return offset<bytes.length?bytes[offset++]:-1;}};
        assertTrue(AndroidLanDiscovery.readBanner(oneByteAtATime));
    }
    @Test public void cancelledScanDoesNotEvenStartDnsOrAcquireAndroidResources() throws Exception {
        AndroidLanDiscovery discovery=new AndroidLanDiscovery(); discovery.close();
        assertTrue(discovery.scan(null,"never-resolve-this.invalid",Collections.emptyList()).isEmpty());
        discovery.close();
    }
    @Test public void deeplyNestedJsonIsRejectedBeforeAndroidJsonCanOverflowTheStack() {
        assertFalse(AndroidLanDiscovery.safeJsonEnvelope("[1,2]"));
        assertFalse(AndroidLanDiscovery.safeJsonEnvelope("{\"a\":".repeat(1500) + "0" + "}".repeat(1500)));
        assertFalse(AndroidLanDiscovery.safeJsonEnvelope("{\"a\":\"unterminated}"));
        assertTrue(AndroidLanDiscovery.safeJsonEnvelope("{\"a\":\"{[[{ \\\" quoted\",\"b\":[{}]}"));
    }
}
