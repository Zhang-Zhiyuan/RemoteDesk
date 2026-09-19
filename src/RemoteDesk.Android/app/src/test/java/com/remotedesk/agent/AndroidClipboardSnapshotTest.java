package com.remotedesk.agent;

import static org.junit.Assert.*;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import org.junit.Test;

public final class AndroidClipboardSnapshotTest {
    private static final String EMPTY = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static final String ABC = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    @Test public void requestMatchesDotNetBinaryWriterWireLayout() throws Exception {
        byte[] wire = RemoteDeskTransport.encodeClipboardSnapshotRequest("r1", "");
        assertArrayEquals(new byte[] {40, 2, 'r', '1', 0}, wire);
        RemoteDeskTransport.ControlMessage decoded = RemoteDeskTransport.decodeControl(wire);
        assertEquals(40, decoded.kind);
        assertEquals("r1", decoded.clipboardSnapshot.requestId);
        assertEquals("", decoded.clipboardSnapshot.revision);
    }

    @Test public void changedSnapshotMatchesDotNetBinaryWriterWireLayout() throws Exception {
        AndroidClipboardSnapshot snapshot = AndroidClipboardSnapshot.capture("r1", "", "abc");
        byte[] wire = RemoteDeskTransport.encodeClipboardSnapshot(snapshot);
        assertArrayEquals((")\u0002r1\u0001@" + ABC + "\u0001\u0001\u0003abc\u0000")
            .getBytes(StandardCharsets.US_ASCII), wire);
        AndroidClipboardSnapshot decoded = RemoteDeskTransport.decodeControl(wire).clipboardSnapshot;
        assertTrue(decoded.success);
        assertTrue(decoded.hasText);
        assertTrue(decoded.changed);
        assertEquals(ABC, decoded.revision);
        assertEquals("abc", decoded.text);
    }

    @Test public void unicodeRevisionMatchesDotNetUtf8Sha256() throws Exception {
        AndroidClipboardSnapshot snapshot = AndroidClipboardSnapshot.capture("中文ID", "", "中文🙂");
        assertEquals("3f7e2b3029a16c844f54b308c8035842509a1a8d8a4f35a7548d2384f3b51901", snapshot.revision);
        AndroidClipboardSnapshot decoded = RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeClipboardSnapshot(snapshot)).clipboardSnapshot;
        assertEquals("中文ID", decoded.requestId);
        assertEquals("中文🙂", decoded.text);
    }

    @Test public void unchangedSnapshotDoesNotResendText() throws Exception {
        AndroidClipboardSnapshot snapshot = AndroidClipboardSnapshot.capture("r", ABC, "abc");
        assertTrue(snapshot.success);
        assertTrue(snapshot.hasText);
        assertFalse(snapshot.changed);
        assertEquals("", snapshot.text);
        assertEquals(ABC, snapshot.revision);
        assertEquals("", RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeClipboardSnapshot(snapshot)).clipboardSnapshot.text);
    }

    @Test public void readableNoTextHasEmptyStringHashNotUnavailable() throws Exception {
        AndroidClipboardSnapshot snapshot = AndroidClipboardSnapshot.capture("r", ABC, "");
        assertTrue(snapshot.success);
        assertFalse(snapshot.hasText);
        assertTrue(snapshot.changed);
        assertEquals(EMPTY, snapshot.revision);
        assertEquals("", snapshot.text);
        assertFalse(AndroidClipboardSnapshot.capture("r", EMPTY, "").changed);
    }

    @Test public void unavailableCannotClearOtherClipboard() throws Exception {
        AndroidClipboardSnapshot snapshot = RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeClipboardSnapshot(AndroidClipboardSnapshot.unavailable("r"))).clipboardSnapshot;
        assertFalse(snapshot.success);
        assertFalse(snapshot.hasText);
        assertFalse(snapshot.changed);
        assertEquals("", snapshot.revision);
        assertEquals("", snapshot.text);
        assertTrue(snapshot.statusMessage.contains("后台读取限制"));
        assertTrue(snapshot.statusMessage.contains("不会清空"));
    }

    @Test public void textLimitIsExactAndNeverTruncates() throws Exception {
        String boundary = "x".repeat(256000);
        AndroidClipboardSnapshot snapshot = AndroidClipboardSnapshot.capture("r", "", boundary);
        assertEquals(boundary, RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeClipboardSnapshot(snapshot)).clipboardSnapshot.text);
        assertThrows(IOException.class, () -> AndroidClipboardSnapshot.capture("r", "", boundary + "x"));
        assertThrows(IOException.class, () -> AndroidClipboardSnapshot.capture("r", "", null));
    }

    @Test public void malformedUnicodeDoesNotProduceCrossPlatformRevisionMismatch() {
        assertThrows(IOException.class, () -> AndroidClipboardSnapshot.capture("r", "", "x\ud800"));
        assertThrows(IOException.class, () -> AndroidClipboardSnapshot.capture("r", "", "\udc00"));
    }

    @Test public void requestValidatesBoundariesAndCanonicalRevision() throws Exception {
        assertEquals("r".repeat(64), RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeClipboardSnapshotRequest("r".repeat(64), ABC)).clipboardSnapshot.requestId);
        for (String id : new String[] {"", "x".repeat(65)})
            assertThrows(IOException.class, () -> RemoteDeskTransport.encodeClipboardSnapshotRequest(id, ""));
        for (String revision : new String[] {"a", "a".repeat(63), "a".repeat(65), ABC.toUpperCase(), "g".repeat(64)})
            assertThrows(IOException.class, () -> RemoteDeskTransport.encodeClipboardSnapshotRequest("r", revision));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {40, 0, 0}));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {40, 1, 'r', 1, 'x'}));
    }

    @Test public void decoderRejectsTruncationTrailingBytesAndInvalidBooleans() throws Exception {
        byte[] wire = RemoteDeskTransport.encodeClipboardSnapshot(AndroidClipboardSnapshot.capture("r", "", "abc"));
        for (int length = 1; length < wire.length; length++) {
            byte[] truncated = Arrays.copyOf(wire, length);
            assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(truncated));
        }
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(Arrays.copyOf(wire, wire.length + 1)));
        byte[] invalid = wire.clone();
        invalid[3] = 2;
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(invalid));
    }

    @Test public void impossibleSnapshotsAreRejected() {
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, "", true, true, "abc", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", false, "", false, false, "abc", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, ABC, true, false, "abc", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, ABC, true, true, "", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", false, "", false, true, "", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, ABC, false, false, "", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, EMPTY, true, false, "", ""));
        assertThrows(IOException.class, () -> new AndroidClipboardSnapshot("r", true, ABC, true, true, "other text", ""));
    }

    @Test public void strictUtf8AndOverlongLengthMatchDotNetValidation() {
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {40, 1, (byte)255, 0}));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {40, 2, (byte)0xc0, (byte)0x80, 0}));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {40, (byte)128, (byte)128, (byte)128, (byte)128, 16, 0}));
        assertThrows(IOException.class, () -> RemoteDeskTransport.encodeClipboardSnapshotRequest("\ud800", ""));
    }

    @Test public void legacyOrClipboardDisabledPeerDoesNotNegotiateSnapshot() {
        int text = RemoteDeskProtocol.CAPABILITY_CLIPBOARD_TEXT;
        int snapshot = RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1;
        assertTrue(AndroidClipboardSnapshot.isNegotiated(text | snapshot, snapshot));
        assertFalse(AndroidClipboardSnapshot.isNegotiated(text | snapshot, text));
        assertFalse(AndroidClipboardSnapshot.isNegotiated(snapshot, snapshot));
        assertFalse(AndroidClipboardSnapshot.isNegotiated(text, snapshot));
        assertEquals(1 << 28, snapshot);
        assertEquals(40, RemoteDeskProtocol.CONTROL_CLIPBOARD_SNAPSHOT_REQUEST);
        assertEquals(41, RemoteDeskProtocol.CONTROL_CLIPBOARD_SNAPSHOT);
    }

    @Test public void hostAdvertisesSnapshotWithoutRequiringInputControl() {
        int host = RemoteDeskHostServer.getCapabilities(AndroidVideoCodecDiagnostics.CodecReport.unavailable(), false);
        assertTrue((host & RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1) != 0);
        assertTrue((host & RemoteDeskProtocol.CAPABILITY_CLIPBOARD_TEXT) != 0);
        assertFalse((host & RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL) != 0);
        assertFalse((RemoteDeskViewerActivity.advertisedViewerCapabilities(java.util.List.of()) &
            RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1) != 0);
    }
}
