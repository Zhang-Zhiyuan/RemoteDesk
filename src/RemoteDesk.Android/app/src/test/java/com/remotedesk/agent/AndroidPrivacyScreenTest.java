package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.IOException;
import java.util.HexFormat;
import java.util.concurrent.atomic.AtomicBoolean;

public class AndroidPrivacyScreenTest {
    @Test public void queryNeverBlocksRestoreAndOldRepliesCannotUndoIt() {
        var controls = new AndroidPrivacyScreen.Controls();
        long query = controls.begin(0);
        long enable = controls.begin(1);
        assertTrue(controls.label().contains("恢复"));
        long disable = controls.begin(2);
        assertEquals(-1, controls.begin(2));
        assertEquals(-1, controls.begin(1));
        assertTrue(controls.complete(2, disable, new AndroidPrivacyScreen.Status("off", true, false, true, "restored")));
        assertFalse(controls.complete(0, query, new AndroidPrivacyScreen.Status("old", true, true, true, "old")));
        assertFalse(controls.queryBusy);
        assertFalse(controls.complete(1, enable, new AndroidPrivacyScreen.Status("old", true, true, true, "old")));
        assertEquals("开启隐私屏", controls.label());
        assertEquals(0, controls.command);
        assertEquals("restored", controls.status.note);
    }

    @Test public void restoreCompletesWhileQueryIsStillWaiting() throws Exception {
        var exchange = new AndroidPrivacyScreen();
        var sent = new java.util.concurrent.CountDownLatch(1);
        var queryId = new java.util.concurrent.atomic.AtomicReference<String>();
        var executor = java.util.concurrent.Executors.newSingleThreadExecutor();
        int cap = RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN;
        try {
            var query = executor.submit(() -> exchange.request(cap, 0, payload -> {
                queryId.set(id(payload)); sent.countDown();
            }, () -> true, 3000));
            assertTrue(sent.await(1, java.util.concurrent.TimeUnit.SECONDS));
            assertFalse(query.isDone());
            assertFalse(exchange.request(cap, 2, payload -> exchange.receive(
                new AndroidPrivacyScreen.Status(id(payload), true, false, true, "restored")), () -> true, 1000).enabled);
            exchange.receive(new AndroidPrivacyScreen.Status(queryId.get(), true, true, true, "old query"));
            assertTrue(query.get(1, java.util.concurrent.TimeUnit.SECONDS).enabled);
        } finally { executor.shutdownNow(); }
    }

    @Test public void sharedVectorsAndStrictValidation() throws Exception {
        for (int action = 0; action < 3; action++)
            assertEquals("2e0269640" + action, HexFormat.of().formatHex(RemoteDeskTransport.encodePrivacyScreenRequest("id", action)));
        byte[] data = HexFormat.of().parseHex("2f02696401010103e4b8ad");
        var status = RemoteDeskTransport.decodePrivacyScreenStatus(data);
        assertTrue(status.enabled); assertEquals("中", status.note);
        assertThrows(IOException.class, () -> RemoteDeskTransport.encodePrivacyScreenRequest("id", 3));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodePrivacyScreenStatus(java.util.Arrays.copyOf(data, data.length + 1)));
        for (int i = 4; i <= 6; i++) {
            byte[] invalid = data.clone(); invalid[i] = 2;
            assertThrows(IOException.class, () -> RemoteDeskTransport.decodePrivacyScreenStatus(invalid));
        }
        data[6] = 0;
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodePrivacyScreenStatus(data));
    }
    // UUID request ids use a single length byte; no Android runtime needed.
    private static String id(byte[] payload) { return new String(payload, 2, payload[1], java.nio.charset.StandardCharsets.UTF_8); }
    @Test public void correlationDenialTimeoutDisconnectAndRetry() throws Exception {
        AndroidPrivacyScreen exchange = new AndroidPrivacyScreen();
        int cap = RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN;
        var status = exchange.request(cap, 1, payload -> {
            exchange.receive(new AndroidPrivacyScreen.Status("stale", true, true, true, "stale"));
            exchange.receive(new AndroidPrivacyScreen.Status(id(payload), false, false, false, "denied"));
        }, () -> true, 1000);
        assertFalse(status.success); assertFalse(status.enabled); assertEquals("denied", status.note);
        assertThrows(IOException.class, () -> exchange.request(cap, 1, payload -> {}, () -> true, 1));
        AtomicBoolean connected = new AtomicBoolean(true);
        assertThrows(IOException.class, () -> exchange.request(cap, 1, payload -> {
            exchange.receive(new AndroidPrivacyScreen.Status(id(payload), true, true, true, "old connection"));
            connected.set(false);
        }, connected::get, 1000));
        assertTrue(exchange.request(cap, 1, payload -> exchange.receive(
            new AndroidPrivacyScreen.Status(id(payload), true, true, true, "confirmed")), () -> true, 1000).enabled);
        assertThrows(IOException.class, () -> exchange.request(0, 1, payload -> fail("legacy must not receive new controls"), () -> true, 1000));
    }
}
