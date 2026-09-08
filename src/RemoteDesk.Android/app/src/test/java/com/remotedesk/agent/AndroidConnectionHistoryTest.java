package com.remotedesk.agent;

import org.junit.Test;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import static org.junit.Assert.*;

public class AndroidConnectionHistoryTest {
    private AndroidConnectionHistory.Node add(AndroidConnectionHistory history, String host, int port, String password) {
        return history.remember("", host, port, "", password, "", "测试电脑", 123);
    }
    @Test public void emptyStorageIsEmptyHistory() throws Exception {
        assertTrue(AndroidConnectionHistory.decode("").entries().isEmpty());
    }
    @Test public void savesUnicodeRemarkAndSeparateCredentialsAcrossRestart() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history, "pc-a", 56565, "password-a").id;
        history.rename(id, "  家里电脑😀  "); add(history, "pc-b", 56565, "password-b");
        AndroidConnectionHistory restored = AndroidConnectionHistory.decode(history.encode());
        assertEquals(2, restored.entries().size());
        assertEquals("pc-b", restored.entries().get(0).host);
        assertEquals("password-b", restored.entries().get(0).password);
        assertEquals("password-a", restored.find(id).password);
        assertEquals("家里电脑😀", restored.find(id).title());
    }
    @Test public void reconnectDeduplicatesHostCaseAndPreservesRemarkAndIdentity() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history, "PC-A.", 56565, "old-password").id;
        history.rename(id, "工作机"); add(history, "pc-b", 56565, "other");
        add(history, "pc-a", 56565, "new-password");
        assertEquals(2, history.entries().size());
        assertEquals(id, history.entries().get(0).id);
        assertEquals("工作机", history.entries().get(0).remark);
        assertEquals("new-password", history.entries().get(0).password);
    }
    @Test public void portsAndRelayDevicesAndServersAreDistinct() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        add(history, "relay", 56565, "a"); add(history, "relay", 56567, "b");
        String first = "00000000-0000-0000-0000-000000000001";
        String second = "00000000-0000-0000-0000-000000000002";
        history.remember("", "relay", 56567, first, "c", "private-config-one", "", 1);
        history.remember("", "relay", 56567, second, "d", "private-config-two", "", 2);
        history.remember("", "relay-other", 56567, second, "e", "private-config-three", "", 3);
        AndroidConnectionHistory restored = AndroidConnectionHistory.decode(history.encode());
        assertEquals(5, restored.entries().size());
        assertEquals("private-config-three", restored.entries().get(0).relayConfiguration);
        assertTrue(restored.entries().get(0).relay());
    }
    @Test public void deletingNodeRemovesItsCredentialAndCannotBeUndoneByPendingReconnect() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history, "pc-a", 56565, "delete-me").id;
        add(history, "pc-b", 56565, "keep-me");
        assertTrue(history.remove(id)); assertFalse(history.remove(id));
        assertNull(history.remember(id, "pc-a", 56565, "", "delete-me", "", "", 4));
        assertNull(AndroidConnectionHistory.decode(history.encode()).find(id));
        assertEquals(1, history.entries().size());
        String serialized = new String(Base64.getDecoder().decode(history.encode()), StandardCharsets.ISO_8859_1);
        assertFalse(serialized.contains("delete-me"));
        assertTrue(serialized.contains("keep-me"));
    }
    @Test public void intentionalFreshConnectionCanSaveADeletedNodeAgain() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history, "pc-a", 56565, "one").id; history.remove(id);
        assertNotEquals(id, add(history, "pc-a", 56565, "two").id);
    }
    @Test public void boundsHistoryAndEvictsLeastRecentlyConnected() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        for (int i = 0; i < 30; i++) add(history, "pc-" + i, 56565, "test");
        assertEquals(AndroidConnectionHistory.LIMIT, history.entries().size());
        assertEquals("pc-29", history.entries().get(0).host);
        assertEquals("pc-10", history.entries().get(19).host);
    }
    @Test public void blankRemarkRestoresDeviceNameWithoutReordering() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history, "pc", 56565, "test").id;
        history.rename(id, "备注"); history.rename(id, "   ");
        assertEquals("测试电脑", history.find(id).title());
        assertFalse(history.rename("missing", "备注"));
    }
    @Test public void ipv6AddressesAreBracketedAndZoneCaseIsPreserved() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        add(history, "[FE80::A%ethX]", 56565, "test");
        add(history, "fe80::a%ethX", 56565, "new");
        assertEquals(1, history.entries().size());
        assertEquals("[fe80::a%ethX]:56565", history.entries().get(0).address());
        add(history, "fe80::a%ethx", 56565, "other-interface");
        assertEquals(2, history.entries().size());
    }
    @Test public void corruptOrFutureDataDoesNotSilentlyEraseHistory() throws Exception {
        for (String encoded : new String[] {"not-base64!", "AAAAAwAAAAA=", "AAAAAf////8=", "AAAAAQAAABU=", "AAAAAQAAAAAA"}) {
            assertThrows(IOException.class, () -> AndroidConnectionHistory.decode(encoded));
        }
        AndroidConnectionHistory history = new AndroidConnectionHistory(); add(history, "pc", 56565, "test");
        String truncated = history.encode().substring(0, 16);
        assertThrows(IOException.class, () -> AndroidConnectionHistory.decode(truncated));
    }
    @Test public void validationFailureDoesNotPartiallyModifyList() {
        AndroidConnectionHistory history = new AndroidConnectionHistory(); add(history, "pc", 56565, "test");
        assertThrows(IllegalArgumentException.class, () -> add(history, "pc", 0, "test"));
        assertThrows(IllegalArgumentException.class, () -> add(history, "https://pc", 56565, "test"));
        assertThrows(IllegalArgumentException.class, () -> add(history, "pc", 56565, ""));
        assertEquals(1, history.entries().size());
    }
    @Test public void diagnosticStringNeverIncludesCredential() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        assertFalse(add(history, "pc", 56565, "sensitive-test-password").toString().contains("sensitive-test-password"));
    }
    @Test public void confirmedAddressChangePreservesTheOriginalNodeAndRemark() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history,"10.0.0.8",56565,"test").id;
        history.rename(id,"工作机");
        history.remember(id,"10.0.0.9",40565,"","test","","PC",2);
        AndroidConnectionHistory restored=AndroidConnectionHistory.decode(history.encode());
        assertEquals(1,restored.entries().size());
        assertEquals("10.0.0.9:40565",restored.find(id).address());
        assertEquals("工作机",restored.find(id).remark);
    }
    @Test public void confirmedMoveDeduplicatesAnAlreadySavedDestination() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        String id = add(history,"10.0.0.8",56565,"first").id; history.rename(id,"保留备注");
        add(history,"10.0.0.9",40565,"second");
        history.remember(id,"10.0.0.9",40565,"","confirmed-password","","PC",2);
        assertEquals(1,history.entries().size()); assertEquals("保留备注",history.find(id).remark);
        assertEquals("confirmed-password",history.find(id).password);
    }
}
