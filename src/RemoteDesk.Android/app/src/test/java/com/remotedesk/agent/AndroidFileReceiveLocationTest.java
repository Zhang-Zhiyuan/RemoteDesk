package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.File;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

public class AndroidFileReceiveLocationTest {
    private static final int CAP = RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE_LOCATION;

    @Test public void roundTripAndBounds() throws Exception {
        assertArrayEquals(java.util.HexFormat.of().parseHex("2702696401082fe4b8adf09f988000"),
            RemoteDeskTransport.encodeFileReceiveLocation("id", true, "/中😀", ""));
        for (boolean success : new boolean[] { true, false }) {
            String path = success ? "/home/中文😀/Downloads" : "";
            byte[] bytes = RemoteDeskTransport.encodeFileReceiveLocation("id", success, path, "");
            var message = RemoteDeskTransport.decodeControl(bytes);
            assertEquals(39, message.kind); assertEquals("id", message.transferId);
            assertEquals(success, message.success); assertEquals(path, message.text);
            assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(java.util.Arrays.copyOf(bytes, bytes.length + 1)));
        }
        assertEquals("id", RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileReceiveLocationRequest("id")).transferId);
        assertThrows(IOException.class, () -> RemoteDeskTransport.encodeFileReceiveLocation("id", true, "x".repeat(8193), ""));
    }

    @Test public void correlatedResponseIgnoresWrongIdsAndReportsExactRemotePath() throws Exception {
        AndroidFileReceiveLocation query = new AndroidFileReceiveLocation();
        var location = query.request(CAP, payload -> {
            var request = RemoteDeskTransport.decodeControl(payload);
            assertEquals(38, request.kind);
            query.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileReceiveLocation("wrong", true, "/wrong", "")));
            query.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileReceiveLocation(request.transferId, true, "C:\\Downloads\\", "重名自动改名")));
        }, () -> true);
        assertEquals("C:\\Downloads\\中文😀.txt", location.file("中文😀.txt"));
        assertEquals("/中文.txt", new AndroidFileReceiveLocation.Location("/", "").file("中文.txt"));
    }

    @Test public void legacySendsNothingAndExplainsUnconfirmedDirectory() throws Exception {
        var location = new AndroidFileReceiveLocation().request(0, payload -> fail("legacy peer must not receive new message"), () -> true);
        assertTrue(location.file("file").contains("位置未确认"));
        assertTrue(location.note.contains("更新被控端"));
    }

    @Test public void failuresAndStaleRepliesNeverBecomeSuccessfulPreflight() throws Exception {
        for (String scenario : new String[] { "timeout", "rejected", "disconnect" }) {
            AndroidFileReceiveLocation query = new AndroidFileReceiveLocation();
            AtomicBoolean connected = new AtomicBoolean(true);
            assertThrows(scenario, IOException.class, () -> query.request(CAP, payload -> {
                var request = RemoteDeskTransport.decodeControl(payload);
                if (scenario.equals("disconnect")) connected.set(false);
                if (scenario.equals("rejected")) query.receive(RemoteDeskTransport.decodeControl(
                    RemoteDeskTransport.encodeFileReceiveLocation(request.transferId, false, "", "storage unavailable")));
            }, connected::get, 1));
            // A failure must also release the pending slot, allowing an explicit retry.
            var retried = query.request(CAP, payload -> {
                var request = RemoteDeskTransport.decodeControl(payload);
                query.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileReceiveLocation(request.transferId, true, "/retry", "")));
            }, () -> true);
            assertEquals("/retry/a", retried.file("a"));
        }
    }

    @Test public void publicSaveUsesActualMediaStoreNameAndHandlesUnavailableMetadata() {
        String actual = AndroidFileTransferReceiver.formatPublishedLocation("/storage/emulated/0", "Download/RemoteDeskReceived/", "中文 (1).txt", "content://fixture/1");
        assertTrue(actual.replace('\\', '/').endsWith("/Download/RemoteDeskReceived/中文 (1).txt"));
        assertTrue(AndroidFileTransferReceiver.formatPublishedLocation("/storage", null, "test", "content://fixture/2").contains("content://fixture/2"));
        assertTrue(AndroidFileTransferReceiver.formatPublishedLocation("/storage", "../", "test", "content://fixture/2").contains("未返回可靠路径"));
        File directory = new File(System.getProperty("java.io.tmpdir"), "RemoteDesk-location-test-no-create");
        assertEquals(directory.getAbsolutePath(), new AndroidFileTransferReceiver(directory).getAdvertisedReceiveDirectory());
    }
}
