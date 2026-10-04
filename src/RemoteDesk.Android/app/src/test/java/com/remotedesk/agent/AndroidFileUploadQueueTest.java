package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;

public final class AndroidFileUploadQueueTest {
    @Test public void multiSelectionDeduplicatesInOrderAndAcceptsSingleFallback() throws Exception {
        assertEquals(Arrays.asList("甲", "乙", "丙"), AndroidFileUploadQueue.selection(Arrays.asList("甲", "乙", "甲", null), "丙"));
        assertEquals(Arrays.asList("甲", "乙"), AndroidFileUploadQueue.selection(Arrays.asList("甲", "乙"), "甲"));
        assertEquals(Collections.singletonList("single"), AndroidFileUploadQueue.selection(null, "single"));
    }

    @Test public void invalidOrOversizedSelectionFailsBeforeConfirmation() throws Exception {
        List<String> tooMany = new ArrayList<>();
        for (int i = 0; i <= AndroidFileUploadQueue.MAX_FILES; i++) tooMany.add("file" + i);
        for (List<String> selection : Arrays.asList(Collections.<String>emptyList(), tooMany)) {
            try { AndroidFileUploadQueue.selection(selection, null); fail(); }
            catch (IOException expected) { assertFalse(expected.getMessage().isEmpty()); }
        }
        assertEquals("子_文件_.txt", AndroidFileUploadQueue.fileName("子/文件\n.txt"));
    }

    @Test public void cancelStopsActiveSenderAndNeverStartsWaitingFiles() throws Exception {
        AndroidFileUploadQueue<String> queue = batch();
        AndroidFileSender sender = queue.start(queue.entries.get(0));
        assertNotNull(sender);
        assertNull(queue.start(queue.entries.get(1)));
        queue.cancel();
        List<byte[]> writes = new ArrayList<>();
        try {
            sender.send(new ByteArrayInputStream(new byte[0]), "甲", 0, 0, writes::add, () -> true, ignored -> {});
            fail("cancelled sender must not start a transfer");
        } catch (IOException expected) { assertTrue(writes.isEmpty()); }
        queue.finish(queue.entries.get(0), false, true, "已取消");
        assertNull(queue.start(queue.entries.get(1)));
        assertEquals(AndroidFileUploadQueue.State.CANCELLED, queue.entries.get(1).state);
        assertTrue(queue.summary().contains("取消 2"));
    }

    @Test public void metadataSizeErrorsIdentifyTheSelectedFileAndAcceptBoundarySizes() throws Exception {
        AndroidFileUploadQueue.validateSize("空文件.bin", 0);
        AndroidFileUploadQueue.validateSize("边界.bin", AndroidFileSender.MAX_BYTES);
        for (long size : new long[] {-1, AndroidFileSender.MAX_BYTES + 1}) {
            try { AndroidFileUploadQueue.validateSize("批次中的这个文件.bin", size); fail(); }
            catch (IOException expected) {
                assertTrue(expected.getMessage().startsWith("批次中的这个文件.bin："));
                assertTrue(expected.getMessage().contains(size < 0 ? "未提供大小" : "不能超过 1 GB"));
            }
        }
    }

    @Test public void perFileReceiptIsNotConfusedWithLegacySendOrAnotherFile() {
        AndroidFileUploadQueue<String> queue = batch();
        AndroidFileSender first = queue.start(queue.entries.get(0));
        queue.finish(queue.entries.get(0), true, true, "远端已确认保存");
        AndroidFileSender second = queue.start(queue.entries.get(1));
        assertNotEquals(first.transferId, second.transferId);
        queue.finish(queue.entries.get(1), true, false, "数据已发送；请核实远端保存");
        assertTrue(queue.summary().contains("已确认保存 1 · 已发送待核实 1"));
        assertFalse(queue.summary().contains("失败")); // Status color must not mistake "失败 0" for an error.
        assertTrue(queue.summary().contains("不会自动粘贴"));
        queue.cancel();
        assertEquals(AndroidFileUploadQueue.State.SAVED, queue.entries.get(0).state);
    }

    @Test public void failedFileDoesNotPreventNextExplicitlyConfirmedFile() {
        AndroidFileUploadQueue<String> queue = batch();
        queue.start(queue.entries.get(0));
        queue.finish(queue.entries.get(0), false, true, "无法读取");
        assertNotNull(queue.start(queue.entries.get(1)));
        assertTrue(queue.summary().contains("失败 1"));
    }

    @Test public void peerWithoutCancellationFinishesCurrentFileButNeverStartsRemainingFiles() throws Exception {
        AndroidFileUploadQueue<String> queue = batch();
        AndroidFileSender sender = queue.start(queue.entries.get(0), false);
        List<Integer> messages = new ArrayList<>();
        String result = sender.send(new ByteArrayInputStream(new byte[0]), "甲", 0, 0,
            sender.withCancellationFence(new Object(), payload -> {
                messages.add(payload[0] & 255);
                if ((payload[0] & 255) == RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START) {
                    queue.cancel();
                    assertTrue(queue.summary().contains("当前文件将继续完成"));
                }
            }), () -> true, ignored -> {});
        assertTrue(messages.contains((int)RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE));
        assertFalse(messages.contains((int)RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL));
        queue.finish(queue.entries.get(0), true, false, result);
        assertEquals(AndroidFileUploadQueue.State.SENT_UNCONFIRMED, queue.entries.get(0).state);
        assertEquals(AndroidFileUploadQueue.State.CANCELLED, queue.entries.get(1).state);
        assertNull(queue.start(queue.entries.get(1), false));
    }

    private static AndroidFileUploadQueue<String> batch() {
        return new AndroidFileUploadQueue<>(Arrays.asList(new AndroidFileUploadQueue.Entry<>("a", "甲", 0),
            new AndroidFileUploadQueue.Entry<>("b", "乙", 10)));
    }
}
