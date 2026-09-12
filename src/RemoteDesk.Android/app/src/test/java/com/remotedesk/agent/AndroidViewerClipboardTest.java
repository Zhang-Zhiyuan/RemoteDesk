package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerClipboardTest {
    @Test public void relayReadWaitsLongerButNeverOverwritesANewerCopy() {
        AndroidViewerClipboard clipboard = new AndroidViewerClipboard();
        AndroidViewerClipboard.Request request = clipboard.begin(true, 4, 0, true);
        assertTrue(clipboard.canApply(request, 4, 12000));
        assertFalse(clipboard.canApply(request, 5, 12000));
        assertFalse(clipboard.canApply(request, 4, 30000));
        assertNull(clipboard.begin(false, 4, 30001));
        assertTrue(clipboard.receive(true, true, "late", 30001));
        assertNotNull(clipboard.begin(false, 4, 30002));
    }
    @Test public void clipboardTextPreservesChineseEmojiAndNewlines() throws Exception {
        String text = "中文、繁體，emoji 😀\r\n第二行\t缩进\n";
        assertEquals(text, AndroidClipboardText.boundText(text));
        assertEquals(text, RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeClipboardSetText(text)).text);
        assertEquals(text, RemoteDeskTransport.decodeControl(RemoteDeskTransport.encodeClipboardText(text)).text);
        assertEquals(" ", AndroidClipboardText.boundText(" "));
    }

    @Test public void clipboardLengthNeverSilentlyTruncatesASurrogate() throws Exception {
        String fits = "😀".repeat(128_000);
        assertEquals(fits, AndroidClipboardText.boundText(fits));
        assertThrows(IllegalArgumentException.class, () -> AndroidClipboardText.boundText("x" + fits));
        assertThrows(java.io.IOException.class, () -> RemoteDeskTransport.encodeClipboardSetText("x" + fits));
    }

    @Test public void readsCannotOverwriteANewerLocalCopy() {
        AndroidViewerClipboard clipboard = new AndroidViewerClipboard();
        AndroidViewerClipboard.Request request = clipboard.begin(true, 20, 100);
        assertNull(clipboard.begin(false, 20, 101));
        clipboard.receive(false, true, "", 102);
        assertEquals(1, request.completed.getCount());
        clipboard.receive(true, true, "中文", 103);
        assertEquals("中文", request.text);
        assertTrue(clipboard.canApply(request, 20, 103));
        assertFalse(clipboard.canApply(request, 21, 103));
        clipboard.finish(request, true);
        assertNotNull(clipboard.begin(true, 20, 104));
        assertFalse(clipboard.canApply(request, 20, 105));
    }

    @Test public void lateReplyIsDrainedBeforeRetry() {
        AndroidViewerClipboard clipboard = new AndroidViewerClipboard();
        AndroidViewerClipboard.Request request = clipboard.begin(true, 1, 0);
        clipboard.finish(request, true);
        assertNull(clipboard.begin(true, 1, 8001));
        assertFalse(clipboard.canApply(request, 1, 8001));
        clipboard.receive(true, true, "old", 8002);
        assertNotNull(clipboard.begin(true, 1, 8003));
    }

    @Test public void failedWriteDoesNotAuthorizePasteAndCanRetry() {
        AndroidViewerClipboard clipboard = new AndroidViewerClipboard();
        AndroidViewerClipboard.Request request = clipboard.begin(false, 1, 0);
        clipboard.receive(true, true, "unrelated", 1);
        assertEquals(1, request.completed.getCount());
        clipboard.receive(false, false, "", 2);
        assertFalse(request.success);
        assertEquals(0, request.completed.getCount());
        clipboard.finish(request, true);
        assertNotNull(clipboard.begin(false, 1, 3));
    }

    @Test public void modifierReleaseAndNewSessionCannotTriggerStalePaste() {
        AndroidClipboardShortcutState keys = new AndroidClipboardShortcutState();
        assertEquals(0, keys.key(0x56, true));
        keys.key(0x11, true);
        assertEquals(0x56, keys.key(0x56, true));
        assertEquals(0, keys.key(0x56, false));
        keys.key(0x12, true);
        assertEquals(0, keys.key(0x56, true));
        keys.key(0x12, false);
        assertEquals(0x43, keys.key(0x43, true));
        assertEquals(0x58, keys.key(0x58, true));
        assertEquals(0x41, keys.key(0x41, true));
        keys.reset();
        assertEquals(0, keys.key(0x56, true));
    }

    @Test public void inputFenceWaitsForTheRemovedButUnsentCommand() throws Exception {
        AndroidViewerInputQueue queue = new AndroidViewerInputQueue(8);
        assertTrue(queue.offerKeyboardBatch(AndroidViewerKeyboard.shortcut(1, 1, 0x11, 0x43)));
        while (queue.size() != 0) {
            assertNotNull(queue.take());
            assertFalse(queue.awaitIdle(0));
            queue.completeSend();
        }
        assertTrue(queue.awaitIdle(0));
        queue.close();
        assertFalse(queue.awaitIdle(0));
    }
}
