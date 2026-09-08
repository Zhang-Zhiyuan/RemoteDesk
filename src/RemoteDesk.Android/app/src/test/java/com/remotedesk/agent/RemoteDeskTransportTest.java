package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.util.Arrays;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

import org.junit.Test;

public final class RemoteDeskTransportTest {
    @Test
    public void writeMessageEmitsEncryptedPacketWithOneBulkWrite() throws Exception {
        byte[] key = new byte[32];
        Arrays.fill(key, (byte) 7);
        CountingOutputStream output = new CountingOutputStream();

        RemoteDeskTransport.writeMessage(
            output,
            RemoteDeskProtocol.MESSAGE_PING,
            new byte[0],
            new RemoteDeskTransport.SecureSession(key, key),
            new Object());

        assertEquals(0, output.singleByteWrites);
        assertEquals(1, output.bulkWrites);
        assertEquals(1, output.flushes);

        RemoteDeskTransport.ProtocolMessage message = RemoteDeskTransport.readMessage(
            new ByteArrayInputStream(output.toByteArray()),
            new RemoteDeskTransport.SecureSession(key, key));
        assertEquals(RemoteDeskProtocol.MESSAGE_PING, message.messageType);
        assertEquals(0, message.payload.length);
    }

    @Test
    public void authenticateClientSendsProofAndCreatesSession()
        throws IOException, GeneralSecurityException {
        byte[] nonce = fixedNonce();
        ByteArrayOutputStream transcript = new ByteArrayOutputStream();
        transcript.write("RDK1".getBytes(StandardCharsets.US_ASCII));
        transcript.write(nonce);
        transcript.write(1);

        ByteArrayOutputStream output = new ByteArrayOutputStream();

        RemoteDeskTransport.SecureSession session = RemoteDeskTransport.authenticateClient(
            new ByteArrayInputStream(transcript.toByteArray()),
            output,
            "viewer password");

        byte[] proof = output.toByteArray();
        assertEquals('A', proof[0]);
        assertEquals('U', proof[1]);
        assertEquals('T', proof[2]);
        assertEquals('H', proof[3]);
        assertEquals(36, proof.length);
        assertTrue(MessageDigest.isEqual(
            expectedPasswordProof("viewer password", nonce),
            Arrays.copyOfRange(proof, 4, proof.length)));
        assertTrue(session.encrypt(new byte[] { 1, 2, 3 }).length > 3);
    }

    @Test
    public void authenticateClientRejectsBadHostResponse()
        throws IOException, GeneralSecurityException {
        byte[] nonce = fixedNonce();
        ByteArrayOutputStream transcript = new ByteArrayOutputStream();
        transcript.write("RDK1".getBytes(StandardCharsets.US_ASCII));
        transcript.write(nonce);
        transcript.write(0);

        assertThrows(SecurityException.class, () -> RemoteDeskTransport.authenticateClient(
            new ByteArrayInputStream(transcript.toByteArray()),
            new ByteArrayOutputStream(),
            "viewer password"));
    }

    @Test
    public void authenticateServerDetailedMarksProbeDisconnectAsIncomplete()
        throws IOException, GeneralSecurityException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();

        RemoteDeskTransport.AuthenticationResult result = RemoteDeskTransport.authenticateServerDetailed(
            new ByteArrayInputStream(new byte[0]),
            output,
            "server password");

        assertNull(result.session);
        assertTrue(result.incomplete);
        assertEquals('R', output.toByteArray()[0]);
        assertEquals('D', output.toByteArray()[1]);
        assertEquals('K', output.toByteArray()[2]);
        assertEquals('1', output.toByteArray()[3]);
    }

    @Test
    public void authenticateServerDetailedKeepsCredentialFailureDistinctFromIncompleteProbe()
        throws IOException, GeneralSecurityException {
        ByteArrayOutputStream input = new ByteArrayOutputStream();
        input.write("AUTH".getBytes(StandardCharsets.US_ASCII));
        input.write(new byte[32]);
        ByteArrayOutputStream output = new ByteArrayOutputStream();

        RemoteDeskTransport.AuthenticationResult result = RemoteDeskTransport.authenticateServerDetailed(
            new ByteArrayInputStream(input.toByteArray()),
            output,
            "server password");

        byte[] response = output.toByteArray();
        assertNull(result.session);
        assertFalse(result.incomplete);
        assertEquals(0, response[response.length - 1]);
    }

    @Test
    public void authenticationDeadlineIsRecheckedBetweenEveryPartialRead() {
        byte[] slowProof = new byte[36];
        System.arraycopy("AUTH".getBytes(StandardCharsets.US_ASCII), 0, slowProof, 0, 4);
        int[] deadlineChecks = { 0 };

        assertThrows(SocketTimeoutException.class, () ->
            RemoteDeskTransport.authenticateServerDetailed(
                new OneByteAtATimeInputStream(slowProof),
                new ByteArrayOutputStream(),
                "server password",
                () -> {
                    if (++deadlineChecks[0] > 5) {
                        throw new SocketTimeoutException("absolute deadline");
                    }
                }));

        assertEquals(6, deadlineChecks[0]);
    }

    @Test
    public void decodeViewerInfoNormalizesUnknownVideoCodecsToJpeg() throws IOException {
        byte[] payload = new byte[5];
        payload[0] = (byte) RemoteDeskProtocol.CONTROL_VIEWER_INFO;
        writeInt32LittleEndian(payload, 1, 1 << 12);

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload);

        assertEquals(RemoteDeskProtocol.VIDEO_CODEC_JPEG, message.videoCodecs);
    }

    @Test
    public void decodeViewerInfoKeepsKnownVideoCodecsOnly() throws IOException {
        byte[] payload = new byte[5];
        payload[0] = (byte) RemoteDeskProtocol.CONTROL_VIEWER_INFO;
        writeInt32LittleEndian(
            payload,
            1,
            RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B | (1 << 12));

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload);

        assertEquals(RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B, message.videoCodecs);
    }

    @Test
    public void decodeControlRejectsTrailingBytes() {
        byte[] payload = new byte[] {
            (byte) RemoteDeskProtocol.CONTROL_CLIPBOARD_GET_TEXT,
            0
        };

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(payload));
    }

    @Test
    public void decodeVideoKeyFrameRequestAcceptsEmptyPayload() throws IOException {
        byte[] payload = new byte[] {
            (byte) RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST
        };

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload);

        assertEquals(RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST, message.kind);
    }

    @Test
    public void decodeClipboardFileRequestAcceptsEmptyPayloadAndRejectsTrailingBytes() throws IOException {
        byte[] payload = new byte[] {
            (byte) RemoteDeskProtocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES
        };

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload);

        assertEquals(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES, message.kind);
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(new byte[] {
            (byte) RemoteDeskProtocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES,
            0
        }));
    }

    @Test
    public void decodeFileTransferStartRejectsOversizedControlString() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START);
        writeString(payload, "transfer-1");
        writeString(payload, "x".repeat(4_097));
        writeInt64LittleEndian(payload, 1);

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(payload.toByteArray()));
    }

    @Test
    public void decodeClipboardSetTextAllowsLargeClipboardText() throws IOException {
        String text = "x".repeat(4_097);
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT);
        writeString(payload, text);

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT, message.kind);
        assertEquals(text, message.text);
    }

    @Test
    public void decodeClipboardSetTextRejectsOversizedClipboardText() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT);
        writeString(payload, "x".repeat(256_001));

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(payload.toByteArray()));
    }

    @Test
    public void encodeClipboardTextRejectsOversizedClipboardText() {
        assertThrows(IOException.class, () ->
            RemoteDeskTransport.encodeClipboardText("x".repeat(256_001)));
    }

    @Test
    public void encodeViewerInfoNormalizesEmptyCodecMaskToJpeg() throws IOException {
        byte[] payload = RemoteDeskTransport.encodeViewerInfo(0);

        assertEquals(RemoteDeskProtocol.CONTROL_VIEWER_INFO, payload[0] & 0xFF);
        assertEquals(RemoteDeskProtocol.VIDEO_CODEC_JPEG, readInt32LittleEndian(payload, 1));
    }

    @Test
    public void encodeViewerCapabilitiesWritesCapabilityMask() throws IOException {
        int capabilities = RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM |
            RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_CANCEL;

        byte[] payload = RemoteDeskTransport.encodeViewerCapabilities(capabilities);

        assertEquals(RemoteDeskProtocol.CONTROL_VIEWER_CAPABILITIES, payload[0] & 0xFF);
        assertEquals(capabilities, readInt32LittleEndian(payload, 1));
    }

    @Test
    public void encodeVideoKeyFrameRequestWritesEmptyRecoveryControl() {
        assertArrayEquals(
            new byte[] {
                (byte) RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST
            },
            RemoteDeskTransport.encodeVideoKeyFrameRequest());
    }

    @Test
    public void encodeInputMatchesRemoteDeskPayloadLayout() throws IOException {
        byte[] payload = RemoteDeskTransport.encodeInput(
            RemoteDeskProtocol.INPUT_MOUSE_WHEEL,
            RemoteDeskProtocol.MOUSE_NONE,
            100,
            200,
            -120);

        assertEquals(14, payload.length);
        assertEquals(RemoteDeskProtocol.INPUT_MOUSE_WHEEL, payload[0] & 0xFF);
        assertEquals(RemoteDeskProtocol.MOUSE_NONE, payload[1] & 0xFF);
        assertEquals(100, readInt32LittleEndian(payload, 2));
        assertEquals(200, readInt32LittleEndian(payload, 6));
        assertEquals(-120, readInt32LittleEndian(payload, 10));
    }

    @Test
    public void decodeDeviceInfoReadsRemoteHostDescriptor() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_DEVICE_INFO);
        writeString(payload, "Remote-PC");
        writeString(payload, "Windows");
        writeInt32LittleEndian(payload, RemoteDeskProtocol.CAPABILITY_REMOTE_DESKTOP |
            RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL);

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_DEVICE_INFO, message.kind);
        assertEquals("Remote-PC", message.machineName);
        assertEquals("Windows", message.platform);
        assertTrue((message.capabilities & RemoteDeskProtocol.CAPABILITY_REMOTE_DESKTOP) != 0);
        assertTrue((message.capabilities & RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL) != 0);
    }

    @Test
    public void decodeCaptureTargetListReadsTargets() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_LIST);
        writeInt32LittleEndian(payload, 2);
        writeString(payload, "all");
        writeString(payload, "All screens");
        writeString(payload, "display-1");
        writeString(payload, "Main display");

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_LIST, message.kind);
        assertEquals(2, message.captureTargets.length);
        assertEquals("all", message.captureTargets[0].id);
        assertEquals("All screens", message.captureTargets[0].displayName);
        assertEquals("display-1", message.captureTargets[1].id);
        assertEquals("Main display", message.captureTargets[1].displayName);
    }

    @Test
    public void decodeCaptureTargetChangedReadsCurrentTarget() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_CHANGED);
        writeString(payload, "display-1");
        writeString(payload, "Main display");

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_CHANGED, message.kind);
        assertEquals(1, message.captureTargets.length);
        assertEquals("display-1", message.captureTargets[0].id);
        assertEquals("Main display", message.captureTargets[0].displayName);
    }

    @Test
    public void decodeStatusControlsReadSuccessAndMessage() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_STATUS);
        payload.write(1);
        writeString(payload, "saved");

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_STATUS, message.kind);
        assertTrue(message.success);
        assertEquals("saved", message.statusMessage);
    }

    @Test
    public void sessionRejectedControlRoundTrips() throws IOException {
        byte[] payload = RemoteDeskTransport.encodeSessionRejected(
            "another viewer is active");

        RemoteDeskTransport.ControlMessage message =
            RemoteDeskTransport.decodeControl(payload);

        assertEquals(
            RemoteDeskProtocol.CONTROL_SESSION_REJECTED,
            message.kind);
        assertEquals(
            "another viewer is active",
            message.statusMessage);
    }

    @Test
    public void decodeJpegFrameReadsHeaderAndBytes() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        writeInt32LittleEndian(payload, 640);
        writeInt32LittleEndian(payload, 360);
        writeDoubleLittleEndian(payload, 1.5);
        writeDoubleLittleEndian(payload, 2.5);
        payload.write(new byte[] { 1, 2, 3 });

        RemoteDeskTransport.FrameMessage frame = RemoteDeskTransport.decodeFrame(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.FRAME_ENCODING_JPEG, frame.encoding);
        assertEquals(640, frame.width);
        assertEquals(360, frame.height);
        assertEquals(RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME, frame.flags);
        assertEquals(1.5, frame.captureMillis, 0.001);
        assertEquals(2.5, frame.encodeMillis, 0.001);
        assertEquals(3, frame.encodedBytes.length);
    }

    @Test
    public void decodeVideoFrameReadsHeaderAndBytes() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        writeInt32LittleEndian(payload, RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B);
        writeInt32LittleEndian(payload, 1280);
        writeInt32LittleEndian(payload, 720);
        writeInt32LittleEndian(payload, RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME);
        writeDoubleLittleEndian(payload, 3.5);
        writeDoubleLittleEndian(payload, 4.5);
        payload.write(new byte[] { 4, 5, 6, 7 });

        RemoteDeskTransport.FrameMessage frame = RemoteDeskTransport.decodeVideoFrame(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B, frame.encoding);
        assertEquals(1280, frame.width);
        assertEquals(720, frame.height);
        assertEquals(RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME, frame.flags);
        assertEquals(3.5, frame.captureMillis, 0.001);
        assertEquals(4.5, frame.encodeMillis, 0.001);
        assertEquals(4, frame.encodedBytes.length);
    }

    @Test
    public void frameDecodersRejectDimensionsAbovePixelBudget() throws IOException {
        ByteArrayOutputStream legacy = new ByteArrayOutputStream();
        writeInt32LittleEndian(legacy, 8192);
        writeInt32LittleEndian(legacy, 8192);
        writeDoubleLittleEndian(legacy, 0);
        writeDoubleLittleEndian(legacy, 0);
        legacy.write(1);

        ByteArrayOutputStream video = new ByteArrayOutputStream();
        writeInt32LittleEndian(video, RemoteDeskProtocol.FRAME_ENCODING_JPEG);
        writeInt32LittleEndian(video, 8192);
        writeInt32LittleEndian(video, 8192);
        writeInt32LittleEndian(video, RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME);
        writeDoubleLittleEndian(video, 0);
        writeDoubleLittleEndian(video, 0);
        video.write(1);

        assertThrows(IOException.class, () ->
            RemoteDeskTransport.decodeFrame(legacy.toByteArray()));
        assertThrows(IOException.class, () ->
            RemoteDeskTransport.decodeVideoFrame(video.toByteArray()));
    }

    @Test
    public void decodeFileTransferChunkRejectsNegativeChunkLength() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK);
        writeString(payload, "transfer-1");
        writeInt64LittleEndian(payload, 0);
        writeInt32LittleEndian(payload, -1);

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(payload.toByteArray()));
    }

    @Test
    public void decodeFileTransferStartRejectsOutOfRangeFileLength() throws IOException {
        ByteArrayOutputStream negativePayload = createFileTransferStartPayload(-1);
        ByteArrayOutputStream tooLargePayload = createFileTransferStartPayload(1024L * 1024L * 1024L + 1);

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(negativePayload.toByteArray()));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(tooLargePayload.toByteArray()));
    }

    @Test
    public void decodeFileTransferChunkRejectsInvalidOffsetAndLength() throws IOException {
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(
            createFileTransferChunkPayload(-1, 1, 1).toByteArray()));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(
            createFileTransferChunkPayload(1024L * 1024L * 1024L + 1, 1, 1).toByteArray()));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(
            createFileTransferChunkPayload(0, 0, 0).toByteArray()));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(
            createFileTransferChunkPayload(0, 128 * 1024 + 1, 0).toByteArray()));
        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(
            createFileTransferChunkPayload(1024L * 1024L * 1024L, 1, 1).toByteArray()));
    }

    @Test
    public void decodeFileTransferCancelReadsTransferIdAndReason() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL);
        writeString(payload, "transfer-cancel");
        writeString(payload, "sender stopped");

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL, message.kind);
        assertEquals("transfer-cancel", message.transferId);
        assertEquals("sender stopped", message.text);
    }

    @Test
    public void decodeFileTransferChecksumReadsAlgorithmAndHex() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM);
        writeString(payload, "transfer-checksum");
        writeString(payload, "SHA-256");
        writeString(payload, "A".repeat(64));

        RemoteDeskTransport.ControlMessage message = RemoteDeskTransport.decodeControl(payload.toByteArray());

        assertEquals(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM, message.kind);
        assertEquals("transfer-checksum", message.transferId);
        assertEquals("SHA256", message.checksumAlgorithm);
        assertEquals("a".repeat(64), message.checksumHex);
    }

    @Test
    public void decodeFileTransferChecksumRejectsInvalidHex() throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM);
        writeString(payload, "transfer-checksum");
        writeString(payload, "SHA256");
        writeString(payload, "z".repeat(64));

        assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(payload.toByteArray()));
    }

    private static ByteArrayOutputStream createFileTransferStartPayload(long fileLength) throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START);
        writeString(payload, "transfer-1");
        writeString(payload, "file.bin");
        writeInt64LittleEndian(payload, fileLength);
        return payload;
    }

    private static ByteArrayOutputStream createFileTransferChunkPayload(
        long fileOffset,
        int declaredLength,
        int actualBytes) throws IOException {
        ByteArrayOutputStream payload = new ByteArrayOutputStream();
        payload.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK);
        writeString(payload, "transfer-1");
        writeInt64LittleEndian(payload, fileOffset);
        writeInt32LittleEndian(payload, declaredLength);
        for (int index = 0; index < actualBytes; index++) {
            payload.write(index & 0xFF);
        }

        return payload;
    }

    private static void writeString(ByteArrayOutputStream output, String value) throws IOException {
        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        write7BitEncodedInt(output, bytes.length);
        output.write(bytes);
    }

    private static void write7BitEncodedInt(ByteArrayOutputStream output, int value) {
        int remaining = value;
        while (remaining >= 0x80) {
            output.write((remaining & 0x7F) | 0x80);
            remaining >>>= 7;
        }

        output.write(remaining);
    }

    private static void writeInt32LittleEndian(byte[] buffer, int offset, int value) {
        buffer[offset] = (byte) value;
        buffer[offset + 1] = (byte) (value >>> 8);
        buffer[offset + 2] = (byte) (value >>> 16);
        buffer[offset + 3] = (byte) (value >>> 24);
    }

    private static void writeInt32LittleEndian(ByteArrayOutputStream output, int value) {
        output.write(value & 0xFF);
        output.write((value >>> 8) & 0xFF);
        output.write((value >>> 16) & 0xFF);
        output.write((value >>> 24) & 0xFF);
    }

    private static int readInt32LittleEndian(byte[] buffer, int offset) {
        return (buffer[offset] & 0xFF) |
            ((buffer[offset + 1] & 0xFF) << 8) |
            ((buffer[offset + 2] & 0xFF) << 16) |
            ((buffer[offset + 3] & 0xFF) << 24);
    }

    private static void writeInt64LittleEndian(ByteArrayOutputStream output, long value) {
        for (int index = 0; index < 8; index++) {
            output.write((int) (value >>> (8 * index)) & 0xFF);
        }
    }

    private static void writeDoubleLittleEndian(ByteArrayOutputStream output, double value) {
        long bits = Double.doubleToLongBits(value);
        for (int index = 0; index < 8; index++) {
            output.write((int) (bits >>> (8 * index)) & 0xFF);
        }
    }

    private static byte[] fixedNonce() {
        byte[] nonce = new byte[32];
        for (int index = 0; index < nonce.length; index++) {
            nonce[index] = (byte) index;
        }

        return nonce;
    }

    private static byte[] expectedPasswordProof(String password, byte[] nonce) throws GeneralSecurityException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] key = digest.digest(password.getBytes(StandardCharsets.UTF_8));
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return mac.doFinal(nonce);
    }

    private static final class CountingOutputStream extends OutputStream {
        private final ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        int singleByteWrites;
        int bulkWrites;
        int flushes;

        @Override
        public void write(int value) {
            singleByteWrites++;
            bytes.write(value);
        }

        @Override
        public void write(byte[] buffer, int offset, int length) {
            bulkWrites++;
            bytes.write(buffer, offset, length);
        }

        @Override
        public void flush() {
            flushes++;
        }

        byte[] toByteArray() {
            return bytes.toByteArray();
        }
    }

    private static final class OneByteAtATimeInputStream extends InputStream {
        private final byte[] bytes;
        private int offset;

        OneByteAtATimeInputStream(byte[] bytes) {
            this.bytes = bytes;
        }

        @Override
        public int read() {
            return offset < bytes.length ? bytes[offset++] & 0xFF : -1;
        }

        @Override
        public int read(byte[] buffer, int bufferOffset, int length) {
            if (offset >= bytes.length) {
                return -1;
            }
            buffer[bufferOffset] = bytes[offset++];
            return 1;
        }
    }
}
