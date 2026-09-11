package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

public class AndroidFileSenderTest {
    private static final int CAPS = RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM |
        RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_CANCEL | RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_RECEIPT;

    @Test public void waitsForSaveAndIgnoresWrongFileReceipt() throws Exception {
        AndroidFileSender sender = new AndroidFileSender();
        var executor = Executors.newSingleThreadExecutor();
        CountDownLatch completed = new CountDownLatch(1);
        List<RemoteDeskTransport.ControlMessage> messages = new ArrayList<>();
        byte[] data = "中文😀\r\n".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        try {
            var task = executor.submit(() -> sender.send(new ByteArrayInputStream(data), "中文.txt", data.length, CAPS, payload -> {
                var message = RemoteDeskTransport.decodeControl(payload); messages.add(message);
                if (message.kind == RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE) completed.countDown();
            }, () -> true, text -> {}));
            assertTrue(completed.await(2, TimeUnit.SECONDS));
            sender.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileTransferReceipt("wrong", true, "wrong")));
            assertFalse(task.isDone());
            sender.receive(RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeFileTransferReceipt(sender.transferId, true, "已保存😀")));
            assertEquals("已保存😀", task.get(2, TimeUnit.SECONDS));
            assertTrue(messages.stream().anyMatch(m -> m.kind == RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM));
        } finally { executor.shutdownNow(); }
    }

    @Test public void rejectedSaveTimeoutCancelAndTruncatedInputsFail() throws Exception {
        for (String scenario : new String[] { "rejected", "timeout", "cancel", "short", "grown", "disconnect" }) {
            AndroidFileSender sender = new AndroidFileSender(); sender.receiptTimeoutMillis = 1;
            List<Integer> sent = new ArrayList<>();
            try {
                sender.send(new ByteArrayInputStream(new byte[] {1}), "test.bin", scenario.equals("short") ? 2 : scenario.equals("grown") ? 0 : 1, CAPS,
                    payload -> {
                        var message = RemoteDeskTransport.decodeControl(payload); sent.add(message.kind);
                        if (message.kind == RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START) {
                            if (scenario.equals("cancel")) sender.cancel();
                            if (scenario.equals("rejected")) sender.receive(RemoteDeskTransport.decodeControl(
                                RemoteDeskTransport.encodeFileTransferReceipt(sender.transferId, false, "disk full")));
                        }
                    }, () -> !scenario.equals("disconnect"), ignored -> {});
                fail(scenario);
            } catch (IOException expected) {
                if (!scenario.equals("disconnect")) assertTrue(scenario, sent.contains(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL));
                if (scenario.equals("short") || scenario.equals("grown")) assertFalse(sent.contains(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE));
            }
        }
    }

    @Test public void legacyPeerGetsExplicitUnconfirmedResultAndEmptyFileWorks() throws Exception {
        AndroidFileSender sender = new AndroidFileSender();
        String result = sender.send(new ByteArrayInputStream(new byte[0]), "empty.txt", 0, 0, ignored -> {}, () -> true, ignored -> {});
        assertTrue(result.contains("旧版")); assertTrue(result.contains("确认是否保存"));
    }

    @Test public void publicationKeepsReceiptIdAcrossAsyncQueue() throws Exception {
        CountDownLatch done = new CountDownLatch(1);
        List<String> ids = new ArrayList<>();
        try (AndroidFileCompletionCoordinator worker = new AndroidFileCompletionCoordinator(1, new AndroidFileCompletionCoordinator.Owner() {
            public boolean isCurrent(long generation) { return true; }
            public void publish(long generation, String message, Throwable failure) { fail("lost transfer id"); }
            public void publishTransfer(long generation, String id, String message, Throwable failure) {
                ids.add(id); done.countDown();
            }
        })) {
            assertTrue(worker.offer("file-id", cancel -> "saved", () -> {}));
            assertTrue(done.await(2, TimeUnit.SECONDS)); assertEquals("file-id", ids.get(0));
        }
    }
}
