package com.remotedesk.agent;

import org.junit.Test;
import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.util.Arrays;
import java.util.Base64;
import java.util.UUID;
import static org.junit.Assert.*;

public class AndroidDeviceIdentityTest {
    private static final String ID = "00112233-4455-6677-8899-aabbccddeeff";

    @Test public void sameAuthenticatedMachineMergesAddressesAndPreservesNote() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String original = history.remember("", "old-ip", 56565, "", "old", "", "PC", 1, ID).id;
        history.rename(original, "我的电脑");
        history.remember("", "new-ip", 45678, "", "new", "", "PC", 2, ID.toUpperCase(java.util.Locale.ROOT));
        history = AndroidConnectionHistory.decode(history.encode());
        assertEquals(1, history.entries().size()); assertEquals(original, history.entries().get(0).id);
        assertEquals("我的电脑", history.entries().get(0).remark);
        assertEquals("new-ip:45678", history.entries().get(0).address());
        assertEquals("new", history.entries().get(0).password);
        assertEquals(ID, history.entries().get(0).deviceId);
    }
    @Test public void sameNameDoesNotMergeDistinctMachines() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        history.remember("", "one", 56565, "", "one", "", "PC", 1, ID);
        history.remember("", "two", 56565, "", "two", "", "PC", 2, UUID.randomUUID().toString());
        assertEquals(2, history.entries().size());
    }
    @Test public void reusedEndpointKeepsDistinctMachinesAndCredentialsAcrossRestart() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String first = history.remember("", "pc", 56565, "", "first-secret", "", "PC", 1, ID).id;
        history.rename(first, "第一台");
        String other = UUID.randomUUID().toString();
        String second = history.remember("", "pc", 56565, "", "second-secret", "", "PC", 2, other).id;
        history = AndroidConnectionHistory.decode(history.encode());
        assertEquals(2, history.entries().size());
        assertEquals("第一台", history.find(first).remark);
        assertEquals("first-secret", history.find(first).password);
        assertEquals("", history.find(second).remark);
        assertEquals(other, history.find(second).deviceId);
    }
    @Test public void previousRecordIsNotRewrittenWhenIdentityChanges() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String first = history.remember("", "old", 56565, "", "first", "", "PC", 1, ID).id;
        history.rename(first, "第一台");
        AndroidConnectionHistory.Node second = history.remember(first, "new", 45678, "", "second", "", "PC", 2, UUID.randomUUID().toString());
        assertEquals(2, history.entries().size()); assertNotEquals(first, second.id);
        assertEquals("old", history.find(first).host); assertEquals("第一台", history.find(first).remark);
        assertEquals("", second.remark);
    }
    @Test public void manualUpdateDoesNotBridgeTwoKnownIdentities() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String first = history.remember("", "pc", 56565, "", "first", "", "PC", 1, ID).id;
        String other = UUID.randomUUID().toString();
        String second = history.remember("", "pc", 56565, "", "second", "", "PC", 2, other).id;
        AndroidConnectionHistory.Node updated = history.remember("", "pc", 56565, "", "manual", "", "", 3);
        assertEquals(2, history.entries().size()); assertEquals(second, updated.id);
        assertEquals(other, updated.deviceId); assertEquals("first", history.find(first).password);
    }
    @Test public void explicitlyAddedPortSurvivesRemarkIdentityUpdateAndRestart() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String node = history.remember("", "pc", 45678, "", "test", "", "", 1).id;
        history.autoPort(node, false); history.rename(node, "固定端口");
        history.remember(node, "pc", 45678, "", "test", "", "PC", 2, ID);
        history = AndroidConnectionHistory.decode(history.encode());
        assertFalse(history.find(node).autoPort); assertEquals("固定端口", history.find(node).remark);
        assertEquals(ID, history.find(node).deviceId);
    }
    @Test public void legacyV1HistoryMigratesWithoutLosingCredentialsOrRemark() throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (DataOutputStream out = new DataOutputStream(bytes)) {
            out.writeInt(1); out.writeInt(1);
            out.writeUTF(ID); out.writeUTF("old-pc"); out.writeInt(56565);
            out.writeUTF(""); out.writeUTF("old-secret"); out.writeUTF("");
            out.writeUTF("PC"); out.writeUTF("旧备注"); out.writeLong(123);
        }
        AndroidConnectionHistory history = AndroidConnectionHistory.decode(Base64.getEncoder().encodeToString(bytes.toByteArray()));
        history = AndroidConnectionHistory.decode(history.encode());
        assertEquals("old-secret", history.find(ID).password);
        assertEquals("旧备注", history.find(ID).remark); assertEquals("", history.find(ID).deviceId);
    }
    @Test public void identityWireMatchesDesktopAndRejectsTrailingData() throws Exception {
        byte[] response = RemoteDeskTransport.encodeDeviceIdentity(ID);
        assertEquals(34, response[0]); assertEquals(36, response[1]);
        assertEquals(ID, RemoteDeskTransport.decodeControl(response).text);
        assertEquals(33, RemoteDeskTransport.decodeControl(new byte[] {33}).kind);
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {33, 0}));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(Arrays.copyOf(response, response.length + 1)));
        assertThrows(IOException.class, () -> RemoteDeskTransport.encodeDeviceIdentity("00000000-0000-0000-0000-000000000000"));
    }
    @Test public void directoryFoldsIdentityAliasesButNotMatchingNames() {
        AndroidLanDevice first = new AndroidLanDevice("one", 56565, "PC", "Windows", false, true, ID);
        AndroidLanDevice ready = new AndroidLanDevice("two", 45678, "PC", "Windows", true, true, ID);
        AndroidLanDevice other = new AndroidLanDevice("three", 56565, "PC", "Windows", true, true, UUID.randomUUID().toString());
        java.util.List<AndroidLanDevice> nodes = AndroidLanDevice.collapseAliases(Arrays.asList(first, ready, other));
        assertEquals(2, nodes.size()); assertSame(ready, nodes.get(0));
    }
}
