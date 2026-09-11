package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidRelayUiPolicyTest {
    private static final String A = "11111111-1111-4111-8111-111111111111";
    private static final String B = "22222222-2222-4222-8222-222222222222";
    private static AndroidRelay.Options options(String id) {
        return new AndroidRelay.Options("relay.test", 56567, "owned-test-internal-token-not-a-device-key",
            "AB".repeat(32), id, true, 22, "root", "");
    }

    @Test public void publicationSavesImmediatelyWithoutNetworkOrAdministratorPassword() throws Exception {
        AndroidRelay.Options previous = options(A);
        AndroidRelay.Options[] written = new AndroidRelay.Options[1];
        AndroidRelay.Options next = AndroidRelayUiPolicy.publish(previous, false, value -> written[0] = value);
        assertSame(next, written[0]); assertFalse(next.publish); assertTrue(previous.publish);
        assertEquals(previous.deviceId, next.deviceId); assertEquals(previous.accessToken, next.accessToken);
        assertEquals(previous.tlsCertificateSha256, next.tlsCertificateSha256);
    }

    @Test public void failedSaveDoesNotChangeEffectiveState() {
        AndroidRelay.Options previous = options(A);
        assertThrows(java.io.IOException.class, () -> AndroidRelayUiPolicy.publish(previous, false,
            value -> { throw new java.io.IOException("owned save failure"); }));
        assertTrue(previous.publish);
    }

    @Test public void eachOnlineDeviceUsesOnlyItsOwnSavedKey() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        for (String id : new String[] { A, B }) {
            AndroidRelay.Options target = options(id);
            history.remember("", target.serverAddress, target.port, id, "key-for-" + id,
                target.json().toString(), "PC", 1);
        }
        assertEquals("key-for-" + A, history.findRelay(options(A)).password);
        assertEquals("key-for-" + B, history.findRelay(options(B)).password);
        assertNull(history.findRelay(options("33333333-3333-4333-8333-333333333333")));
    }

    @Test public void anotherServerOrChangedServerIdentityCannotBorrowTheSavedKey() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        AndroidRelay.Options target = options(A);
        history.remember("", target.serverAddress, target.port, A, "owned-device-key", target.json().toString(), "PC", 1);
        assertNull(history.findRelay(new AndroidRelay.Options("another.test", target.port, target.accessToken,
            target.tlsCertificateSha256, A, true)));
        assertNull(history.findRelay(new AndroidRelay.Options(target.serverAddress, target.port, target.accessToken,
            "CD".repeat(32), A, true)));
    }

    @Test public void unreadableShortcutIsNotAReasonToUseTheLocalDeviceKey() {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        history.remember("", "relay.test", 56567, A, "owned-device-key", "invalid-old-config", "PC", 1);
        assertNull(history.findRelay(options(A)));
    }

    @Test public void logoutRetainsHistoryButItCannotReuseAnOldServerLogin() throws Exception {
        AndroidConnectionHistory history = new AndroidConnectionHistory();
        AndroidRelay.Options target = options(A);
        AndroidConnectionHistory.Node node = history.remember("", target.serverAddress, target.port, A,
            "owned-device-key", target.json().toString(), "PC", 1);
        assertThrows(AndroidRelayUiPolicy.LoginRequired.class, () -> AndroidRelayUiPolicy.historyTarget(node, null));
        assertEquals("owned-device-key", history.find(node.id).password);
        AndroidRelay.Options renewed = new AndroidRelay.Options(target.serverAddress, target.port,
            "renewed-owned-test-internal-server-token", target.tlsCertificateSha256, B, true);
        assertEquals(renewed.accessToken, AndroidRelayUiPolicy.historyTarget(node, renewed).accessToken);
        assertEquals(A, AndroidRelayUiPolicy.historyTarget(node, renewed).deviceId);
    }
}
