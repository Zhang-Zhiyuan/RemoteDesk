package com.remotedesk.agent;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.List;
import org.json.JSONObject;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidSshProtocolTest {
    private static final String ID = "01234567-89ab-cdef-0123-456789abcdef";
    private static final String KEY = "test-device-key";
    private byte[] nonce() { byte[] nonce = new byte[32]; for (int i=0;i<32;i++) nonce[i]=(byte)i; return nonce; }
    private byte[] hash() throws Exception { return MessageDigest.getInstance("SHA-256").digest(KEY.getBytes(StandardCharsets.UTF_8)); }
    private String hex(byte[] bytes) { StringBuilder text=new StringBuilder(); for(byte b:bytes)text.append(String.format("%02x",b&255));return text.toString(); }
    private byte[] hello(int status) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        bytes.write("RDS1".getBytes(StandardCharsets.US_ASCII)); bytes.write(nonce());
        bytes.write(ID.getBytes(StandardCharsets.US_ASCII)); bytes.write(AndroidSshProtocol.proof(hash(),"server",ID,nonce()));
        bytes.write(status); return bytes.toByteArray();
    }
    @Test public void sharedCrossPlatformGoldenVectors() throws Exception {
        assertEquals("1f3698cd93105499cee01ff797ef4a2e5b67f88602361194c42e7e818003d145",hex(AndroidSshProtocol.proof(hash(),"server",ID,nonce())));
        assertEquals("2cd865b69b18ae31b0848a8cf9d76c5ce72a013a2844355f8514bfbeca4da792",hex(AndroidSshProtocol.proof(hash(),"client",ID,nonce())));
    }
    @Test public void gateAuthenticatesBeforePassingThroughSshBanner() throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream(); bytes.write(hello(1)); bytes.write("SSH-2.0-test\r\n".getBytes(StandardCharsets.US_ASCII));
        ByteArrayInputStream input = new ByteArrayInputStream(bytes.toByteArray()); ByteArrayOutputStream output = new ByteArrayOutputStream();
        AndroidSshProtocol.authenticate(input,output,ID,KEY);
        assertArrayEquals(AndroidSshProtocol.proof(hash(),"client",ID,nonce()),output.toByteArray());
        assertEquals("SSH-2.0-test\r\n",new String(input.readAllBytes(),StandardCharsets.US_ASCII));
    }
    @Test public void corruptHeaderIdentityNonceAndProofNeverReleaseClientProof() throws Exception {
        for (int offset : new int[]{0,3,4,35,36,71,72,103}) {
            byte[] value=hello(1);value[offset]^=1;ByteArrayOutputStream output=new ByteArrayOutputStream();
            assertThrows(IOException.class,()->AndroidSshProtocol.authenticate(new ByteArrayInputStream(value),output,ID,KEY));
            assertEquals(0,output.size());
        }
    }
    @Test public void wrongDeviceKeyReleasesNoProof() throws Exception {
        ByteArrayOutputStream output=new ByteArrayOutputStream();
        assertThrows(IOException.class,()->AndroidSshProtocol.authenticate(new ByteArrayInputStream(hello(1)),output,ID,"wrong"));
        assertEquals(0,output.size());
    }
    @Test public void reflectedClientProofIsNotServerProof() throws Exception {
        byte[] value=hello(1); System.arraycopy(AndroidSshProtocol.proof(hash(),"client",ID,nonce()),0,value,72,32);
        ByteArrayOutputStream output=new ByteArrayOutputStream();
        assertThrows(IOException.class,()->AndroidSshProtocol.authenticate(new ByteArrayInputStream(value),output,ID,KEY));
        assertEquals(0,output.size());
    }
    @Test public void truncatedHelloAndMissingStatusFailClosed() throws Exception {
        for(int length:new int[]{0,4,36,72,103,104}) {
            byte[] value=Arrays.copyOf(hello(1),length);
            assertThrows(IOException.class,()->AndroidSshProtocol.authenticate(new ByteArrayInputStream(value),new ByteArrayOutputStream(),ID,KEY));
        }
    }
    @Test public void gateErrorsDistinguishUnavailableSshAndBadAuthentication() throws Exception {
        for(int code:new int[]{0,2,3,255}) {
            IOException ex=assertThrows(IOException.class,()->AndroidSshProtocol.authenticate(new ByteArrayInputStream(hello(code)),new ByteArrayOutputStream(),ID,KEY));
            assertEquals(code==2,ex.getMessage().contains("不可达"));
        }
    }
    @Test public void malformedProofInputsRejected() throws Exception {
        assertThrows(IOException.class,()->AndroidSshProtocol.proof(hash(),"viewer",ID,nonce()));
        assertThrows(IOException.class,()->AndroidSshProtocol.proof(hash(),"server",ID,new byte[31]));
        assertThrows(IOException.class,()->AndroidSshProtocol.proof(new byte[31],"server",ID,nonce()));
        assertThrows(IOException.class,()->AndroidSshProtocol.proof(hash(),"server",ID.toUpperCase(),nonce()));
    }
    @Test public void onlyExactSshPairedIsAccepted() throws Exception {
        AndroidSshProtocol.paired(new JSONObject("{\"ok\":true,\"type\":\"paired\",\"service\":\"ssh\"}"));
        for(String json:new String[]{"{}","{\"ok\":true,\"type\":\"paired\"}","{\"ok\":\"true\",\"type\":\"paired\",\"service\":\"ssh\"}",
                "{\"ok\":true,\"type\":\"paired\",\"service\":\"desktop\"}","{\"ok\":true,\"type\":\"waiting\",\"service\":\"ssh\"}"})
            assertThrows(IOException.class,()->AndroidSshProtocol.paired(new JSONObject(json)));
    }
    @Test public void legacyAndMalformedCapabilitiesFailClosedForSsh() throws Exception {
        JSONObject capable=new JSONObject("{\"services\":[\"desktop\",\"ssh\"]}");
        for(String json:new String[]{"{}","{\"sshTunneling\":false}","{\"sshTunneling\":\"true\"}"})
            assertFalse(AndroidSshProtocol.available(new JSONObject(json),capable));
        JSONObject directory=new JSONObject("{\"sshTunneling\":true}");
        for(String json:new String[]{"{}","{\"services\":null}","{\"services\":\"ssh\"}","{\"services\":[\"desktop\"]}","{\"services\":[\"SSH\"]}"})
            assertFalse(AndroidSshProtocol.available(directory,new JSONObject(json)));
        assertTrue(AndroidSshProtocol.available(directory,capable));
    }
    @Test public void sshOnlyHostCannotOfferDesktopWhileLegacyCan() throws Exception {
        assertTrue(AndroidSshProtocol.desktopAvailable(new JSONObject()));
        assertTrue(AndroidSshProtocol.desktopAvailable(new JSONObject("{\"services\":[\"desktop\",\"ssh\"]}")));
        for(String json:new String[]{"{\"services\":[\"ssh\"]}","{\"services\":null}","{\"services\":[]}","{\"services\":\"desktop\"}"})
            assertFalse(AndroidSshProtocol.desktopAvailable(new JSONObject(json)));
    }
    @Test public void identityScopesToRelayCertificateDeviceAndServiceNotAdminOrEphemeralPort() {
        AndroidRelay.Options one=new AndroidRelay.Options("relay.test",56567,"x".repeat(40),"AB".repeat(32),ID,true);
        AndroidRelay.Options same=new AndroidRelay.Options("alias.test",1234,"z".repeat(40),"AB".repeat(32),ID,true,2222,"admin","");
        assertEquals(AndroidSshProtocol.alias(one),AndroidSshProtocol.alias(same));
        assertNotEquals(AndroidSshProtocol.alias(one),AndroidSshProtocol.alias(one.target("11234567-89ab-cdef-0123-456789abcdef")));
        assertNotEquals(AndroidSshProtocol.alias(one),AndroidSshProtocol.alias(new AndroidRelay.Options("relay.test",56567,"x".repeat(40),"CD".repeat(32),ID,true)));
        assertTrue(AndroidSshProtocol.alias(one).endsWith(":ssh"));
    }
    @Test public void hostKeyFingerprintUsesOpenSshSha256() throws Exception {
        assertEquals("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0",AndroidSshProtocol.fingerprint("abc".getBytes(StandardCharsets.UTF_8)));
    }
    @Test public void capabilityChangesRecreateDirectoryActions() {
        AndroidRelay.Device desktop=new AndroidRelay.Device(ID,"name","Linux",true,List.of(),0,"","",false,"",false,true);
        AndroidRelay.Device both=new AndroidRelay.Device(ID,"name","Linux",true,List.of(),0,"","",false,"",true,true);
        AndroidRelay.Device sshOnly=new AndroidRelay.Device(ID,"name","Linux",true,List.of(),0,"","",false,"",true,false);
        assertFalse(AndroidRelayDeviceList.same(List.of(desktop),List.of(both)));
        assertFalse(AndroidRelayDeviceList.same(List.of(both),List.of(sshOnly)));
    }
    @Test public void privateKeyReaderPreservesBytesAndAllowsBoundedFiles() throws Exception {
        byte[] original=new byte[131072]; for(int i=0;i<original.length;i++)original[i]=(byte)i;
        assertArrayEquals(original,AndroidSshProtocol.readPrivateKey(new ByteArrayInputStream(original)));
        assertArrayEquals(new byte[]{7},AndroidSshProtocol.readPrivateKey(new ByteArrayInputStream(new byte[]{7})));
    }
    @Test public void privateKeyReaderRejectsEmptyAndOversizedProviders() {
        assertThrows(IOException.class,()->AndroidSshProtocol.readPrivateKey(new ByteArrayInputStream(new byte[0])));
        assertThrows(IOException.class,()->AndroidSshProtocol.readPrivateKey(new ByteArrayInputStream(new byte[131073])));
    }
    @Test public void privateKeyReaderHandlesProvidersWithAnInitialZeroByteRead() throws Exception {
        ByteArrayInputStream stream=new ByteArrayInputStream(new byte[]{4,5,6}) {
            boolean first=true;
            @Override public synchronized int read(byte[] b,int off,int len){if(first){first=false;return 0;}return super.read(b,off,len);}
        };
        assertArrayEquals(new byte[]{4,5,6},AndroidSshProtocol.readPrivateKey(stream));
    }
}
