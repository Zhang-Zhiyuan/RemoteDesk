package com.remotedesk.agent;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicLong;

import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

final class RemoteDeskTransport {
    private static final byte[] MAGIC = ascii("RDK1");
    private static final byte[] AUTH_MARKER = ascii("AUTH");
    private static final byte[] SESSION_INFO = ascii("RemoteDesk session v1");
    private static final byte[] CLIENT_TO_SERVER_INFO = ascii("client->server");
    private static final byte[] SERVER_TO_CLIENT_INFO = ascii("server->client");
    private static final int HEADER_LENGTH = 5;
    private static final int ENCRYPTED_HEADER_LENGTH = 4;
    private static final int NONCE_LENGTH = 32;
    private static final int PROOF_LENGTH = 32;
    private static final int AES_TAG_LENGTH = 16;
    private static final int FRAME_HEADER_LENGTH = 24;
    private static final int VIDEO_FRAME_HEADER_LENGTH = 32;
    private static final int INPUT_PAYLOAD_LENGTH = 14;
    private static final int MAX_CONTROL_PAYLOAD_BYTES = 2 * 1024 * 1024;
    private static final int MAX_FRAME_PAYLOAD_BYTES = 32 * 1024 * 1024;
    private static final int MAX_ENCRYPTED_BYTES = HEADER_LENGTH + MAX_FRAME_PAYLOAD_BYTES + AES_TAG_LENGTH;
    private static final int MAX_CLIPBOARD_TEXT_CHARS = 256_000;
    private static final int MAX_CONTROL_STRING_CHARS = 4_096;
    private static final int FILE_TRANSFER_CHUNK_BYTES = 128 * 1024;
    private static final long MAX_FILE_TRANSFER_BYTES = 1024L * 1024L * 1024L;
    private static final String FILE_TRANSFER_CHECKSUM_ALGORITHM = "SHA256";
    private static final int SHA256_HEX_LENGTH = 64;

    private RemoteDeskTransport() {
    }

    static SecureSession authenticateServer(InputStream input, OutputStream output, String password)
        throws IOException, GeneralSecurityException {
        AuthenticationResult result = authenticateServerDetailed(input, output, password);
        if (result.session != null) {
            return result.session;
        }

        if (result.incomplete) {
            throw new EOFException("RemoteDesk authentication ended before the client proof was sent.");
        }

        throw new SecurityException("RemoteDesk password authentication failed.");
    }

    static AuthenticationResult authenticateServerDetailed(InputStream input, OutputStream output, String password)
        throws IOException, GeneralSecurityException {
        return authenticateServerDetailed(input, output, password, () -> { });
    }

    static AuthenticationResult authenticateServerDetailed(
        InputStream input,
        OutputStream output,
        String password,
        BeforeAuthenticationRead beforeRead) throws IOException, GeneralSecurityException {
        byte[] nonce = new byte[NONCE_LENGTH];
        new SecureRandom().nextBytes(nonce);

        output.write(MAGIC);
        output.write(nonce);
        output.flush();

        byte[] marker;
        try {
            marker = readExact(input, AUTH_MARKER.length, beforeRead);
        } catch (EOFException ignored) {
            return new AuthenticationResult(null, true);
        }

        byte[] proof;
        try {
            proof = readExact(input, PROOF_LENGTH, beforeRead);
        } catch (EOFException ignored) {
            return new AuthenticationResult(null, true);
        }

        byte[] expectedProof = computePasswordProof(password, nonce);
        boolean authenticated = Arrays.equals(marker, AUTH_MARKER) && MessageDigest.isEqual(proof, expectedProof);

        try {
            output.write(authenticated ? 1 : 0);
            output.flush();
        } catch (IOException ex) {
            if (!authenticated) {
                return new AuthenticationResult(null, false);
            }

            throw ex;
        }

        if (!authenticated) {
            return new AuthenticationResult(null, false);
        }

        return new AuthenticationResult(createSession(password, nonce), false);
    }

    @FunctionalInterface
    interface BeforeAuthenticationRead {
        void run() throws IOException;
    }

    static SecureSession authenticateClient(InputStream input, OutputStream output, String password)
        throws IOException, GeneralSecurityException {
        byte[] magic = readExact(input, MAGIC.length);
        if (!Arrays.equals(MAGIC, magic)) {
            throw new IOException("RemoteDesk host magic is invalid.");
        }

        byte[] nonce = readExact(input, NONCE_LENGTH);
        byte[] proof = computePasswordProof(password, nonce);
        output.write(AUTH_MARKER);
        output.write(proof);
        output.flush();

        int result = input.read();
        if (result < 0) {
            throw new EOFException("RemoteDesk authentication ended before the host response.");
        }

        if (result != 1) {
            throw new SecurityException("RemoteDesk password authentication failed.");
        }

        return createClientSession(password, nonce);
    }

    static void writeMessage(
        OutputStream output,
        int messageType,
        byte[] payload,
        SecureSession session,
        Object writeLock) throws IOException, GeneralSecurityException {
        if (!isKnownMessageType(messageType)) {
            throw new IOException("RemoteDesk message type is invalid.");
        }

        if (payload.length > maxPayloadBytes(messageType)) {
            throw new IOException("RemoteDesk payload is too large.");
        }

        byte[] plain = new byte[HEADER_LENGTH + payload.length];
        plain[0] = (byte) messageType;
        writeInt32LittleEndian(plain, 1, payload.length);
        System.arraycopy(payload, 0, plain, HEADER_LENGTH, payload.length);
        writeEncrypted(output, plain, session, writeLock);
    }

    static void writeFrame(
        OutputStream output,
        AndroidScreenCaptureSession.ScreenFrame frame,
        SecureSession session,
        Object writeLock) throws IOException, GeneralSecurityException {
        if (!areFrameDimensionsAllowed(frame.width, frame.height)) {
            throw new IOException("RemoteDesk frame dimensions exceed the safe pixel budget.");
        }
        int payloadLength = FRAME_HEADER_LENGTH + frame.jpegLength;
        if (payloadLength > maxPayloadBytes(RemoteDeskProtocol.MESSAGE_FRAME)) {
            throw new IOException("RemoteDesk frame is too large.");
        }

        byte[] plain = new byte[HEADER_LENGTH + payloadLength];
        plain[0] = (byte) RemoteDeskProtocol.MESSAGE_FRAME;
        writeInt32LittleEndian(plain, 1, payloadLength);
        writeInt32LittleEndian(plain, HEADER_LENGTH, frame.width);
        writeInt32LittleEndian(plain, HEADER_LENGTH + 4, frame.height);
        writeDoubleLittleEndian(plain, HEADER_LENGTH + 8, frame.captureMillis);
        writeDoubleLittleEndian(plain, HEADER_LENGTH + 16, frame.encodeMillis);
        System.arraycopy(frame.jpegBytes, 0, plain, HEADER_LENGTH + FRAME_HEADER_LENGTH, frame.jpegLength);
        writeEncrypted(output, plain, session, writeLock);
    }

    static void writeVideoFrame(
        OutputStream output,
        AndroidH264ScreenEncoder.VideoFrame frame,
        SecureSession session,
        Object writeLock) throws IOException, GeneralSecurityException {
        if (!areFrameDimensionsAllowed(frame.width, frame.height)) {
            throw new IOException("RemoteDesk video dimensions exceed the safe pixel budget.");
        }
        int payloadLength = VIDEO_FRAME_HEADER_LENGTH + frame.length;
        if (payloadLength > maxPayloadBytes(RemoteDeskProtocol.MESSAGE_VIDEO_FRAME)) {
            throw new IOException("RemoteDesk video frame is too large.");
        }

        byte[] plain = new byte[HEADER_LENGTH + payloadLength];
        plain[0] = (byte) RemoteDeskProtocol.MESSAGE_VIDEO_FRAME;
        writeInt32LittleEndian(plain, 1, payloadLength);
        writeInt32LittleEndian(plain, HEADER_LENGTH, RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B);
        writeInt32LittleEndian(plain, HEADER_LENGTH + 4, frame.width);
        writeInt32LittleEndian(plain, HEADER_LENGTH + 8, frame.height);
        writeInt32LittleEndian(plain, HEADER_LENGTH + 12, frame.flags);
        writeDoubleLittleEndian(plain, HEADER_LENGTH + 16, frame.captureMillis);
        writeDoubleLittleEndian(plain, HEADER_LENGTH + 24, frame.encodeMillis);
        System.arraycopy(frame.bytes, 0, plain, HEADER_LENGTH + VIDEO_FRAME_HEADER_LENGTH, frame.length);
        writeEncrypted(output, plain, session, writeLock);
    }

    static ProtocolMessage readMessage(InputStream input, SecureSession session)
        throws IOException, GeneralSecurityException {
        byte[] header = readExact(input, ENCRYPTED_HEADER_LENGTH);
        int encryptedLength = readInt32LittleEndian(header, 0);
        if (encryptedLength < HEADER_LENGTH + AES_TAG_LENGTH || encryptedLength > MAX_ENCRYPTED_BYTES) {
            throw new IOException("RemoteDesk encrypted message length is invalid.");
        }

        byte[] encrypted = readExact(input, encryptedLength);
        byte[] plain = session.decrypt(encrypted);
        if (plain.length < HEADER_LENGTH) {
            throw new IOException("RemoteDesk message header is incomplete.");
        }

        int messageType = plain[0] & 0xFF;
        int payloadLength = readInt32LittleEndian(plain, 1);
        if (!isKnownMessageType(messageType) ||
            payloadLength < 0 ||
            payloadLength > maxPayloadBytes(messageType) ||
            plain.length != HEADER_LENGTH + payloadLength) {
            throw new IOException("RemoteDesk message payload length is invalid.");
        }

        byte[] payload = Arrays.copyOfRange(plain, HEADER_LENGTH, plain.length);
        return new ProtocolMessage(messageType, payload);
    }

    static byte[] encodeDeviceInfo(String machineName, int capabilities) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_DEVICE_INFO);
        writeBoundedString(output, machineName, "RemoteDesk");
        writeBoundedString(output, RemoteDeskProtocol.PLATFORM_ANDROID, RemoteDeskProtocol.PLATFORM_ANDROID);
        writeInt32LittleEndian(output, capabilities);
        return output.toByteArray();
    }

    static byte[] encodeDeviceIdentity(String deviceId) throws IOException {
        String id = AndroidConnectionHistory.deviceIdentity(deviceId);
        if (id.isEmpty()) throw new IOException("Invalid device identity");
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_DEVICE_IDENTITY);
        writeBoundedString(output, id, "");
        return output.toByteArray();
    }

    static byte[] encodeCaptureTargetList() throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_LIST);
        writeInt32LittleEndian(output, 1);
        writeBoundedString(output, RemoteDeskProtocol.CAPTURE_TARGET_ID, "android-screen");
        writeBoundedString(output, RemoteDeskProtocol.CAPTURE_TARGET_NAME, "Android Screen");
        return output.toByteArray();
    }

    static byte[] encodeSelectCaptureTarget(String id) throws IOException {
        if (id == null || id.trim().isEmpty() || id.length() > MAX_CONTROL_STRING_CHARS) throw new IOException("Invalid capture target ID.");
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_SELECT_CAPTURE_TARGET);
        writeBoundedString(output, id, "capture-target");
        return output.toByteArray();
    }

    static byte[] encodeCaptureTargetChanged() throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_CHANGED);
        writeBoundedString(output, RemoteDeskProtocol.CAPTURE_TARGET_ID, "android-screen");
        writeBoundedString(output, RemoteDeskProtocol.CAPTURE_TARGET_NAME, "Android Screen");
        return output.toByteArray();
    }

    static byte[] encodeViewerInfo(int videoCodecs) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_VIEWER_INFO);
        writeInt32LittleEndian(output, normalizeVideoCodecs(videoCodecs));
        return output.toByteArray();
    }

    static byte[] encodeViewerCapabilities(int capabilities) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_VIEWER_CAPABILITIES);
        writeInt32LittleEndian(output, capabilities);
        return output.toByteArray();
    }

    static byte[] encodeLowLatencyVideoOffer(LowLatencyVideoProtocol.Offer offer) throws IOException {
        try {
            offer.validate();
        } catch (IllegalArgumentException ex) {
            throw new IOException("RemoteDesk low-latency UDP offer is invalid.", ex);
        }
        ByteArrayOutputStream output = new ByteArrayOutputStream(110);
        output.write(RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_OFFER);
        output.write(LowLatencyVideoProtocol.VERSION);
        writeUInt16LittleEndian(output, offer.port);
        writeUInt16LittleEndian(output, offer.maxDatagramBytes);
        writeInt32LittleEndian(output, offer.maxFrameBytes);
        writeInt64LittleEndian(output, offer.channelId);
        writeInt32LittleEndian(output, offer.epoch);
        output.write(offer.hostToViewerKey);
        output.write(offer.viewerToHostKey);
        output.write(offer.hostNoncePrefix);
        output.write(offer.viewerNoncePrefix);
        output.write(offer.challenge);
        return output.toByteArray();
    }

    static byte[] encodeLowLatencyVideoReady(long channelId, int epoch) throws IOException {
        return encodeLowLatencyVideoBarrier(
            RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY,
            channelId,
            epoch,
            -1);
    }

    static byte[] encodeLowLatencyVideoStop(long channelId, int epoch, int reason) throws IOException {
        return encodeLowLatencyVideoBarrier(
            RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP,
            channelId,
            epoch,
            reason);
    }

    static byte[] encodeLowLatencyVideoStopped(long channelId, int epoch, int reason) throws IOException {
        return encodeLowLatencyVideoBarrier(
            RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOPPED,
            channelId,
            epoch,
            reason);
    }

    private static byte[] encodeLowLatencyVideoBarrier(
        int kind,
        long channelId,
        int epoch,
        int reason) throws IOException {
        boolean ready = kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY;
        if (channelId == 0 || epoch == 0 ||
            (!ready && (reason < 0 || reason > 255))) {
            throw new IOException("RemoteDesk low-latency UDP control message is invalid.");
        }
        ByteArrayOutputStream output = new ByteArrayOutputStream(ready ? 13 : 14);
        output.write(kind);
        writeInt64LittleEndian(output, channelId);
        writeInt32LittleEndian(output, epoch);
        if (!ready) {
            output.write(reason);
        }
        return output.toByteArray();
    }

    static byte[] encodeVideoKeyFrameRequest() {
        return new byte[] {
            (byte) RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST
        };
    }

    static byte[] encodeInput(int kind, int button, int x, int y, int data) throws IOException {
        if (kind <= 0 || kind > RemoteDeskProtocol.INPUT_PINCH_ZOOM ||
            button < RemoteDeskProtocol.MOUSE_NONE || button > RemoteDeskProtocol.MOUSE_MIDDLE) {
            throw new IOException("RemoteDesk input command is invalid.");
        }

        byte[] payload = new byte[INPUT_PAYLOAD_LENGTH];
        payload[0] = (byte) kind;
        payload[1] = (byte) button;
        writeInt32LittleEndian(payload, 2, x);
        writeInt32LittleEndian(payload, 6, y);
        writeInt32LittleEndian(payload, 10, data);
        return payload;
    }

    static byte[] encodeClipboardText(String text) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_CLIPBOARD_TEXT);
        writeString(output, text == null ? "" : text, MAX_CLIPBOARD_TEXT_CHARS);
        return output.toByteArray();
    }

    static byte[] encodeClipboardSetText(String text) throws IOException {
        byte[] payload = encodeClipboardText(text);
        payload[0] = (byte) RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT;
        return payload;
    }

    static byte[] encodeClipboardStatus(boolean success, String message) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_CLIPBOARD_STATUS);
        output.write(success ? 1 : 0);
        writeBoundedString(output, message, "剪贴板操作已完成。");
        return output.toByteArray();
    }

    static byte[] encodeFileTransferStatus(boolean success, String message) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_STATUS);
        output.write(success ? 1 : 0);
        writeBoundedString(output, message, "文件传输状态已更新。");
        return output.toByteArray();
    }

    static byte[] encodeFileTransferReceipt(String transferId, boolean success, String message) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_RECEIPT);
        writeBoundedString(output, transferId, "");
        output.write(success ? 1 : 0);
        writeBoundedString(output, message, "文件保存结果已更新。");
        return output.toByteArray();
    }

    static byte[] encodeFileTransferStart(String id, String name, long length) throws IOException {
        validateFileLength(length);
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START);
        writeString(output, id, MAX_CONTROL_STRING_CHARS);
        writeString(output, name, MAX_CONTROL_STRING_CHARS);
        writeInt64LittleEndian(output, length);
        return output.toByteArray();
    }

    static byte[] encodeFileTransferChunk(String id, long offset, byte[] bytes, int length) throws IOException {
        validateFileChunk(offset, length);
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK);
        writeString(output, id, MAX_CONTROL_STRING_CHARS);
        writeInt64LittleEndian(output, offset);
        writeInt32LittleEndian(output, length);
        output.write(bytes, 0, length);
        return output.toByteArray();
    }

    static byte[] encodeFileTransferComplete(String id) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE);
        writeString(output, id, MAX_CONTROL_STRING_CHARS);
        return output.toByteArray();
    }

    static byte[] encodeFileTransferChecksum(String id, String checksum) throws IOException {
        validateSha256Hex(checksum);
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM);
        writeString(output, id, MAX_CONTROL_STRING_CHARS);
        writeString(output, "SHA256", MAX_CONTROL_STRING_CHARS);
        writeString(output, checksum, MAX_CONTROL_STRING_CHARS);
        return output.toByteArray();
    }

    static byte[] encodeFileTransferCancel(String id, String reason) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL);
        writeString(output, id, MAX_CONTROL_STRING_CHARS);
        writeBoundedString(output, reason, "发送端取消");
        return output.toByteArray();
    }

    static byte[] encodeSessionRejected(String message) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        output.write(RemoteDeskProtocol.CONTROL_SESSION_REJECTED);
        writeBoundedString(
            output,
            message,
            "被控端当前无法接受新的查看连接。");
        return output.toByteArray();
    }

    static ControlMessage decodeControl(byte[] payload) throws IOException {
        if (payload.length == 0 || payload.length > MAX_CONTROL_PAYLOAD_BYTES) {
            throw new IOException("RemoteDesk control payload length is invalid.");
        }

        ByteCursor cursor = new ByteCursor(payload);
        int kind = cursor.readUnsignedByte();
        String text = null;
        String transferId = null;
        String fileName = null;
        long fileLength = 0;
        long fileOffset = 0;
        byte[] fileBytes = null;
        switch (kind) {
            case RemoteDeskProtocol.CONTROL_DEVICE_IDENTITY_REQUEST:
                break;
            case RemoteDeskProtocol.CONTROL_DEVICE_IDENTITY:
                text = AndroidConnectionHistory.deviceIdentity(cursor.readString(MAX_CONTROL_STRING_CHARS));
                if (text.isEmpty()) throw new IOException("Invalid device identity");
                break;
            case RemoteDeskProtocol.CONTROL_DEVICE_INFO:
                String machineName = cursor.readString(MAX_CONTROL_STRING_CHARS);
                String platform = cursor.readString(MAX_CONTROL_STRING_CHARS);
                int capabilities = cursor.readInt32LittleEndian();
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    machineName,
                    platform,
                    capabilities,
                    null,
                    false,
                    null);
            case RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_LIST:
                int targetCount = cursor.readInt32LittleEndian();
                if (targetCount < 0 || targetCount > RemoteDeskProtocol.MAX_CONTROL_ITEMS) {
                    throw new IOException("RemoteDesk capture target count is invalid.");
                }

                CaptureTarget[] captureTargets = new CaptureTarget[targetCount];
                for (int index = 0; index < targetCount; index++) {
                    captureTargets[index] = new CaptureTarget(
                        cursor.readString(MAX_CONTROL_STRING_CHARS),
                        cursor.readString(MAX_CONTROL_STRING_CHARS));
                }

                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    captureTargets,
                    false,
                    null);
            case RemoteDeskProtocol.CONTROL_CAPTURE_TARGET_CHANGED:
                String targetId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                String displayName = cursor.readString(MAX_CONTROL_STRING_CHARS);
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    new CaptureTarget[] {
                        new CaptureTarget(targetId, displayName)
                    },
                    false,
                    null);
            case RemoteDeskProtocol.CONTROL_VIEWER_INFO:
                int videoCodecs = normalizeVideoCodecs(cursor.readInt32LittleEndian());
                cursor.ensureFullyRead();
                return new ControlMessage(kind, null, null, null, 0, 0, null, videoCodecs);
            case RemoteDeskProtocol.CONTROL_VIEWER_CAPABILITIES:
                int viewerCapabilities = cursor.readInt32LittleEndian();
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    viewerCapabilities,
                    null,
                    false,
                    null);
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_OFFER:
                int version = cursor.readUnsignedByte();
                if (version != LowLatencyVideoProtocol.VERSION) {
                    throw new IOException("RemoteDesk low-latency UDP version is unsupported.");
                }
                LowLatencyVideoProtocol.Offer offer;
                try {
                    offer = new LowLatencyVideoProtocol.Offer(
                        cursor.readUInt16LittleEndian(),
                        cursor.readUInt16LittleEndian(),
                        cursor.readInt32LittleEndian(),
                        cursor.readInt64LittleEndian(),
                        cursor.readInt32LittleEndian(),
                        cursor.readBytes(LowLatencyVideoProtocol.KEY_LENGTH),
                        cursor.readBytes(LowLatencyVideoProtocol.KEY_LENGTH),
                        cursor.readBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH),
                        cursor.readBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH),
                        cursor.readBytes(LowLatencyVideoProtocol.CHALLENGE_LENGTH));
                } catch (IllegalArgumentException ex) {
                    throw new IOException("RemoteDesk low-latency UDP offer is invalid.", ex);
                }
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    null,
                    false,
                    null,
                    offer,
                    0,
                    0,
                    0);
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY:
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOP:
            case RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_STOPPED:
                long lowLatencyChannelId = cursor.readInt64LittleEndian();
                int lowLatencyEpoch = cursor.readInt32LittleEndian();
                int lowLatencyReason = kind == RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_READY
                    ? 0
                    : cursor.readUnsignedByte();
                if (lowLatencyChannelId == 0 || lowLatencyEpoch == 0) {
                    throw new IOException("RemoteDesk low-latency UDP control identity is invalid.");
                }
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    null,
                    false,
                    null,
                    null,
                    lowLatencyChannelId,
                    lowLatencyEpoch,
                    lowLatencyReason);
            case RemoteDeskProtocol.CONTROL_VIDEO_KEY_FRAME_REQUEST:
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES:
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_SET_TEXT:
                text = cursor.readString(MAX_CLIPBOARD_TEXT_CHARS);
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_TEXT:
                text = cursor.readString(MAX_CLIPBOARD_TEXT_CHARS);
                break;
            case RemoteDeskProtocol.CONTROL_SELECT_CAPTURE_TARGET:
                text = cursor.readString(MAX_CONTROL_STRING_CHARS);
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_GET_TEXT:
                break;
            case RemoteDeskProtocol.CONTROL_CLIPBOARD_STATUS:
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_STATUS:
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_RECEIPT:
                if (kind == RemoteDeskProtocol.CONTROL_FILE_TRANSFER_RECEIPT) {
                    transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                }
                boolean success = cursor.readUnsignedByte() != 0;
                String statusMessage = cursor.readString(MAX_CONTROL_STRING_CHARS);
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    transferId,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    null,
                    success,
                    statusMessage);
            case RemoteDeskProtocol.CONTROL_SESSION_REJECTED:
                String rejectionMessage =
                    cursor.readString(MAX_CONTROL_STRING_CHARS);
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    null,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    null,
                    null,
                    null,
                    null,
                    0,
                    null,
                    false,
                    rejectionMessage);
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_START:
                transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                fileName = cursor.readString(MAX_CONTROL_STRING_CHARS);
                fileLength = cursor.readInt64LittleEndian();
                validateFileLength(fileLength);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHUNK:
                transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                fileOffset = cursor.readInt64LittleEndian();
                int length = cursor.readInt32LittleEndian();
                validateFileChunk(fileOffset, length);
                fileBytes = cursor.readBytes(length);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_COMPLETE:
                transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CANCEL:
                transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                text = cursor.readString(MAX_CONTROL_STRING_CHARS);
                break;
            case RemoteDeskProtocol.CONTROL_FILE_TRANSFER_CHECKSUM:
                transferId = cursor.readString(MAX_CONTROL_STRING_CHARS);
                String checksumAlgorithm = normalizeChecksumAlgorithm(cursor.readString(MAX_CONTROL_STRING_CHARS));
                String checksumHex = cursor.readString(MAX_CONTROL_STRING_CHARS).toLowerCase(Locale.ROOT);
                validateChecksumAlgorithm(checksumAlgorithm);
                validateSha256Hex(checksumHex);
                cursor.ensureFullyRead();
                return new ControlMessage(
                    kind,
                    null,
                    transferId,
                    null,
                    0,
                    0,
                    null,
                    RemoteDeskProtocol.VIDEO_CODEC_JPEG,
                    checksumAlgorithm,
                    checksumHex);
            default:
                cursor.skipRemaining();
                break;
        }

        cursor.ensureFullyRead();
        return new ControlMessage(kind, text, transferId, fileName, fileLength, fileOffset, fileBytes, RemoteDeskProtocol.VIDEO_CODEC_JPEG);
    }

    static FrameMessage decodeFrame(byte[] payload) throws IOException {
        if (payload.length < FRAME_HEADER_LENGTH) {
            throw new IOException("RemoteDesk JPEG frame is incomplete.");
        }

        int width = readInt32LittleEndian(payload, 0);
        int height = readInt32LittleEndian(payload, 4);
        double captureMillis = readDoubleLittleEndian(payload, 8);
        double encodeMillis = readDoubleLittleEndian(payload, 16);
        int encodedLength = payload.length - FRAME_HEADER_LENGTH;
        validateFrame(width, height, RemoteDeskProtocol.FRAME_ENCODING_JPEG, RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME, encodedLength);
        return new FrameMessage(
            RemoteDeskProtocol.FRAME_ENCODING_JPEG,
            width,
            height,
            RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME,
            captureMillis,
            encodeMillis,
            Arrays.copyOfRange(payload, FRAME_HEADER_LENGTH, payload.length));
    }

    static FrameMessage decodeVideoFrame(byte[] payload) throws IOException {
        if (payload.length < VIDEO_FRAME_HEADER_LENGTH) {
            throw new IOException("RemoteDesk video frame is incomplete.");
        }

        int encoding = readInt32LittleEndian(payload, 0);
        int width = readInt32LittleEndian(payload, 4);
        int height = readInt32LittleEndian(payload, 8);
        int flags = readInt32LittleEndian(payload, 12);
        double captureMillis = readDoubleLittleEndian(payload, 16);
        double encodeMillis = readDoubleLittleEndian(payload, 24);
        int encodedLength = payload.length - VIDEO_FRAME_HEADER_LENGTH;
        validateFrame(width, height, encoding, flags, encodedLength);
        return new FrameMessage(
            encoding,
            width,
            height,
            flags,
            captureMillis,
            encodeMillis,
            Arrays.copyOfRange(payload, VIDEO_FRAME_HEADER_LENGTH, payload.length));
    }

    private static void writeEncrypted(
        OutputStream output,
        byte[] plain,
        SecureSession session,
        Object writeLock) throws IOException, GeneralSecurityException {
        synchronized (writeLock) {
            output.write(session.encryptPacket(plain));
            output.flush();
        }
    }

    private static SecureSession createSession(String password, byte[] nonce) throws GeneralSecurityException {
        byte[] passwordKey = sha256(password.getBytes(StandardCharsets.UTF_8));
        byte[] masterKey = hmacSha256(passwordKey, join(SESSION_INFO, nonce));
        byte[] clientToServerKey = hmacSha256(masterKey, CLIENT_TO_SERVER_INFO);
        byte[] serverToClientKey = hmacSha256(masterKey, SERVER_TO_CLIENT_INFO);
        return new SecureSession(serverToClientKey, clientToServerKey);
    }

    private static SecureSession createClientSession(String password, byte[] nonce) throws GeneralSecurityException {
        byte[] passwordKey = sha256(password.getBytes(StandardCharsets.UTF_8));
        byte[] masterKey = hmacSha256(passwordKey, join(SESSION_INFO, nonce));
        byte[] clientToServerKey = hmacSha256(masterKey, CLIENT_TO_SERVER_INFO);
        byte[] serverToClientKey = hmacSha256(masterKey, SERVER_TO_CLIENT_INFO);
        return new SecureSession(clientToServerKey, serverToClientKey);
    }

    private static boolean isKnownMessageType(int messageType) {
        switch (messageType) {
            case RemoteDeskProtocol.MESSAGE_FRAME:
            case RemoteDeskProtocol.MESSAGE_INPUT:
            case RemoteDeskProtocol.MESSAGE_CONTROL:
            case RemoteDeskProtocol.MESSAGE_PING:
            case RemoteDeskProtocol.MESSAGE_PONG:
            case RemoteDeskProtocol.MESSAGE_VIDEO_FRAME:
                return true;
            default:
                return false;
        }
    }

    private static int maxPayloadBytes(int messageType) {
        switch (messageType) {
            case RemoteDeskProtocol.MESSAGE_FRAME:
            case RemoteDeskProtocol.MESSAGE_VIDEO_FRAME:
                return MAX_FRAME_PAYLOAD_BYTES;
            case RemoteDeskProtocol.MESSAGE_INPUT:
                return INPUT_PAYLOAD_LENGTH;
            case RemoteDeskProtocol.MESSAGE_CONTROL:
                return MAX_CONTROL_PAYLOAD_BYTES;
            case RemoteDeskProtocol.MESSAGE_PING:
            case RemoteDeskProtocol.MESSAGE_PONG:
            default:
                return 0;
        }
    }

    private static byte[] computePasswordProof(String password, byte[] nonce) throws GeneralSecurityException {
        byte[] key = sha256(password.getBytes(StandardCharsets.UTF_8));
        return hmacSha256(key, nonce);
    }

    private static byte[] hmacSha256(byte[] key, byte[] data) throws GeneralSecurityException {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return mac.doFinal(data);
    }

    private static byte[] sha256(byte[] data) throws GeneralSecurityException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        return digest.digest(data);
    }

    private static byte[] join(byte[] first, byte[] second) {
        byte[] joined = new byte[first.length + second.length];
        System.arraycopy(first, 0, joined, 0, first.length);
        System.arraycopy(second, 0, joined, first.length, second.length);
        return joined;
    }

    private static byte[] readExact(InputStream input, int length) throws IOException {
        return readExact(input, length, () -> { });
    }

    private static byte[] readExact(
        InputStream input,
        int length,
        BeforeAuthenticationRead beforeRead) throws IOException {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length) {
            beforeRead.run();
            int read = input.read(buffer, offset, length - offset);
            if (read < 0) {
                throw new EOFException("RemoteDesk connection closed.");
            }

            offset += read;
        }

        return buffer;
    }

    private static void writeString(ByteArrayOutputStream output, String value, int maxChars) throws IOException {
        if (value.length() > maxChars) {
            throw new IOException("RemoteDesk control string is too large.");
        }

        byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
        write7BitEncodedInt(output, bytes.length);
        output.write(bytes);
    }

    private static void writeBoundedString(ByteArrayOutputStream output, String value, String fallback) throws IOException {
        String text = value == null || value.trim().isEmpty() ? fallback : value.trim();
        writeString(
            output,
            text.length() <= MAX_CONTROL_STRING_CHARS ? text : text.substring(0, MAX_CONTROL_STRING_CHARS),
            MAX_CONTROL_STRING_CHARS);
    }

    private static int read7BitEncodedInt(ByteCursor cursor) throws IOException {
        int value = 0;
        int shift = 0;
        while (shift < 35) {
            int next = cursor.readUnsignedByte();
            value |= (next & 0x7F) << shift;
            if ((next & 0x80) == 0) {
                return value;
            }

            shift += 7;
        }

        throw new IOException("RemoteDesk string length is invalid.");
    }

    private static void write7BitEncodedInt(ByteArrayOutputStream output, int value) {
        int remaining = value;
        while (remaining >= 0x80) {
            output.write((remaining & 0x7F) | 0x80);
            remaining >>>= 7;
        }

        output.write(remaining);
    }

    private static void writeInt32LittleEndian(ByteArrayOutputStream output, int value) {
        output.write(value & 0xFF);
        output.write((value >>> 8) & 0xFF);
        output.write((value >>> 16) & 0xFF);
        output.write((value >>> 24) & 0xFF);
    }

    private static void writeUInt16LittleEndian(ByteArrayOutputStream output, int value) {
        output.write(value & 0xFF);
        output.write((value >>> 8) & 0xFF);
    }

    private static void writeInt64LittleEndian(ByteArrayOutputStream output, long value) {
        for (int index = 0; index < 8; index++) {
            output.write((int) (value >>> (8 * index)) & 0xFF);
        }
    }

    private static void writeInt32LittleEndian(byte[] buffer, int offset, int value) {
        buffer[offset] = (byte) value;
        buffer[offset + 1] = (byte) (value >>> 8);
        buffer[offset + 2] = (byte) (value >>> 16);
        buffer[offset + 3] = (byte) (value >>> 24);
    }

    private static int readInt32LittleEndian(byte[] buffer, int offset) {
        return (buffer[offset] & 0xFF) |
            ((buffer[offset + 1] & 0xFF) << 8) |
            ((buffer[offset + 2] & 0xFF) << 16) |
            ((buffer[offset + 3] & 0xFF) << 24);
    }

    private static void writeDoubleLittleEndian(byte[] buffer, int offset, double value) {
        long bits = Double.doubleToLongBits(value);
        for (int index = 0; index < 8; index++) {
            buffer[offset + index] = (byte) (bits >>> (8 * index));
        }
    }

    private static double readDoubleLittleEndian(byte[] buffer, int offset) {
        long bits = 0;
        for (int index = 0; index < 8; index++) {
            bits |= ((long) buffer[offset + index] & 0xFFL) << (8 * index);
        }

        return Double.longBitsToDouble(bits);
    }

    private static byte[] ascii(String text) {
        return text.getBytes(StandardCharsets.US_ASCII);
    }

    private static int normalizeVideoCodecs(int codecs) {
        int knownCodecs = RemoteDeskProtocol.VIDEO_CODEC_JPEG | RemoteDeskProtocol.VIDEO_CODEC_H264_ANNEX_B;
        int normalized = codecs & knownCodecs;
        return normalized == 0 ? RemoteDeskProtocol.VIDEO_CODEC_JPEG : normalized;
    }

    private static void validateFileLength(long fileLength) throws IOException {
        if (fileLength < 0 || fileLength > MAX_FILE_TRANSFER_BYTES) {
            throw new IOException("RemoteDesk file length is invalid.");
        }
    }

    private static void validateFileChunk(long offset, int length) throws IOException {
        if (offset < 0 ||
            offset > MAX_FILE_TRANSFER_BYTES ||
            length <= 0 ||
            length > FILE_TRANSFER_CHUNK_BYTES ||
            offset > MAX_FILE_TRANSFER_BYTES - length) {
            throw new IOException("RemoteDesk file chunk is invalid.");
        }
    }

    private static String normalizeChecksumAlgorithm(String algorithm) {
        return algorithm.replace("-", "").toUpperCase(Locale.ROOT);
    }

    private static void validateChecksumAlgorithm(String algorithm) throws IOException {
        if (!FILE_TRANSFER_CHECKSUM_ALGORITHM.equals(algorithm)) {
            throw new IOException("RemoteDesk file checksum algorithm is unsupported.");
        }
    }

    private static void validateSha256Hex(String checksumHex) throws IOException {
        if (checksumHex.length() != SHA256_HEX_LENGTH) {
            throw new IOException("RemoteDesk file checksum length is invalid.");
        }

        for (int index = 0; index < checksumHex.length(); index++) {
            char ch = checksumHex.charAt(index);
            boolean hex = (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            if (!hex) {
                throw new IOException("RemoteDesk file checksum format is invalid.");
            }
        }
    }

    private static void validateFrame(int width, int height, int encoding, int flags, int encodedLength)
        throws IOException {
        boolean knownEncoding = encoding == RemoteDeskProtocol.FRAME_ENCODING_JPEG ||
            encoding == RemoteDeskProtocol.FRAME_ENCODING_H264_ANNEX_B;
        int knownFlags = RemoteDeskProtocol.FRAME_FLAG_KEY_FRAME | RemoteDeskProtocol.FRAME_FLAG_CODEC_CONFIG;
        if (!knownEncoding ||
            (flags & ~knownFlags) != 0 ||
            width <= 0 ||
            height <= 0 ||
            width > RemoteDeskProtocol.MAX_FRAME_DIMENSION ||
            height > RemoteDeskProtocol.MAX_FRAME_DIMENSION ||
            (long) width * height > RemoteDeskProtocol.MAX_FRAME_PIXELS ||
            encodedLength <= 0) {
            throw new IOException("RemoteDesk frame payload is invalid.");
        }
    }

    static boolean areFrameDimensionsAllowed(int width, int height) {
        return width > 0 &&
            height > 0 &&
            width <= RemoteDeskProtocol.MAX_FRAME_DIMENSION &&
            height <= RemoteDeskProtocol.MAX_FRAME_DIMENSION &&
            (long) width * height <= RemoteDeskProtocol.MAX_FRAME_PIXELS;
    }

    static final class ProtocolMessage {
        final int messageType;
        final byte[] payload;

        ProtocolMessage(int messageType, byte[] payload) {
            this.messageType = messageType;
            this.payload = payload;
        }
    }

    static final class AuthenticationResult {
        final SecureSession session;
        final boolean incomplete;

        AuthenticationResult(SecureSession session, boolean incomplete) {
            this.session = session;
            this.incomplete = incomplete;
        }
    }

    static final class ControlMessage {
        final int kind;
        final String text;
        final String transferId;
        final String fileName;
        final long fileLength;
        final long fileOffset;
        final byte[] fileBytes;
        final int videoCodecs;
        final String checksumAlgorithm;
        final String checksumHex;
        final String machineName;
        final String platform;
        final int capabilities;
        final CaptureTarget[] captureTargets;
        final boolean success;
        final String statusMessage;
        final LowLatencyVideoProtocol.Offer lowLatencyVideoOffer;
        final long lowLatencyVideoChannelId;
        final int lowLatencyVideoEpoch;
        final int lowLatencyVideoStopReason;

        ControlMessage(
            int kind,
            String text,
            String transferId,
            String fileName,
            long fileLength,
            long fileOffset,
            byte[] fileBytes,
            int videoCodecs) {
            this(kind, text, transferId, fileName, fileLength, fileOffset, fileBytes, videoCodecs, null, null);
        }

        ControlMessage(
            int kind,
            String text,
            String transferId,
            String fileName,
            long fileLength,
            long fileOffset,
            byte[] fileBytes,
            int videoCodecs,
            String checksumAlgorithm,
            String checksumHex) {
            this(
                kind,
                text,
                transferId,
                fileName,
                fileLength,
                fileOffset,
                fileBytes,
                videoCodecs,
                checksumAlgorithm,
                checksumHex,
                null,
                null,
                0,
                null,
                false,
                null);
        }

        ControlMessage(
            int kind,
            String text,
            String transferId,
            String fileName,
            long fileLength,
            long fileOffset,
            byte[] fileBytes,
            int videoCodecs,
            String checksumAlgorithm,
            String checksumHex,
            String machineName,
            String platform,
            int capabilities,
            CaptureTarget[] captureTargets,
            boolean success,
            String statusMessage) {
            this(
                kind,
                text,
                transferId,
                fileName,
                fileLength,
                fileOffset,
                fileBytes,
                videoCodecs,
                checksumAlgorithm,
                checksumHex,
                machineName,
                platform,
                capabilities,
                captureTargets,
                success,
                statusMessage,
                null,
                0,
                0,
                0);
        }

        ControlMessage(
            int kind,
            String text,
            String transferId,
            String fileName,
            long fileLength,
            long fileOffset,
            byte[] fileBytes,
            int videoCodecs,
            String checksumAlgorithm,
            String checksumHex,
            String machineName,
            String platform,
            int capabilities,
            CaptureTarget[] captureTargets,
            boolean success,
            String statusMessage,
            LowLatencyVideoProtocol.Offer lowLatencyVideoOffer,
            long lowLatencyVideoChannelId,
            int lowLatencyVideoEpoch,
            int lowLatencyVideoStopReason) {
            this.kind = kind;
            this.text = text;
            this.transferId = transferId;
            this.fileName = fileName;
            this.fileLength = fileLength;
            this.fileOffset = fileOffset;
            this.fileBytes = fileBytes;
            this.videoCodecs = videoCodecs;
            this.checksumAlgorithm = checksumAlgorithm;
            this.checksumHex = checksumHex;
            this.machineName = machineName;
            this.platform = platform;
            this.capabilities = capabilities;
            this.captureTargets = captureTargets == null ? new CaptureTarget[0] : captureTargets;
            this.success = success;
            this.statusMessage = statusMessage;
            this.lowLatencyVideoOffer = lowLatencyVideoOffer;
            this.lowLatencyVideoChannelId = lowLatencyVideoChannelId;
            this.lowLatencyVideoEpoch = lowLatencyVideoEpoch;
            this.lowLatencyVideoStopReason = lowLatencyVideoStopReason;
        }
    }

    static final class CaptureTarget {
        final String id;
        final String displayName;

        CaptureTarget(String id, String displayName) {
            this.id = id;
            this.displayName = displayName;
        }
    }

    static final class FrameMessage {
        final int encoding;
        final int width;
        final int height;
        final int flags;
        final double captureMillis;
        final double encodeMillis;
        final byte[] encodedBytes;

        FrameMessage(
            int encoding,
            int width,
            int height,
            int flags,
            double captureMillis,
            double encodeMillis,
            byte[] encodedBytes) {
            this.encoding = encoding;
            this.width = width;
            this.height = height;
            this.flags = flags;
            this.captureMillis = captureMillis;
            this.encodeMillis = encodeMillis;
            this.encodedBytes = encodedBytes;
        }
    }

    private static final class ByteCursor {
        private final byte[] bytes;
        private int offset;

        ByteCursor(byte[] bytes) {
            this.bytes = bytes;
        }

        int readUnsignedByte() throws IOException {
            if (offset >= bytes.length) {
                throw new EOFException("RemoteDesk control payload ended unexpectedly.");
            }

            return bytes[offset++] & 0xFF;
        }

        String readString(int maxChars) throws IOException {
            int length = read7BitEncodedInt(this);
            if (length < 0 || length > bytes.length - offset) {
                throw new IOException("RemoteDesk control string length is invalid.");
            }

            String value = new String(bytes, offset, length, StandardCharsets.UTF_8);
            offset += length;
            if (value.length() > maxChars) {
                throw new IOException("RemoteDesk control string is too large.");
            }

            return value;
        }

        int readInt32LittleEndian() throws IOException {
            if (bytes.length - offset < 4) {
                throw new EOFException("RemoteDesk int32 payload ended unexpectedly.");
            }

            int value = (bytes[offset] & 0xFF) |
                ((bytes[offset + 1] & 0xFF) << 8) |
                ((bytes[offset + 2] & 0xFF) << 16) |
                ((bytes[offset + 3] & 0xFF) << 24);
            offset += 4;
            return value;
        }

        int readUInt16LittleEndian() throws IOException {
            if (bytes.length - offset < 2) {
                throw new EOFException("RemoteDesk uint16 payload ended unexpectedly.");
            }
            int value = (bytes[offset] & 0xFF) |
                ((bytes[offset + 1] & 0xFF) << 8);
            offset += 2;
            return value;
        }

        long readInt64LittleEndian() throws IOException {
            if (bytes.length - offset < 8) {
                throw new EOFException("RemoteDesk int64 payload ended unexpectedly.");
            }

            long value = 0;
            for (int index = 0; index < 8; index++) {
                value |= ((long) bytes[offset + index] & 0xFFL) << (8 * index);
            }

            offset += 8;
            return value;
        }

        byte[] readBytes(int length) throws IOException {
            if (length < 0 || length > bytes.length - offset) {
                throw new IOException("RemoteDesk byte payload length is invalid.");
            }

            byte[] value = Arrays.copyOfRange(bytes, offset, offset + length);
            offset += length;
            return value;
        }

        void ensureFullyRead() throws IOException {
            if (offset != bytes.length) {
                throw new IOException("RemoteDesk control payload has trailing bytes.");
            }
        }

        void skipRemaining() {
            offset = bytes.length;
        }
    }

    static final class SecureSession {
        private final SecretKeySpec sendKey;
        private final SecretKeySpec receiveKey;
        private final AtomicLong sendSequence = new AtomicLong();
        private final AtomicLong receiveSequence = new AtomicLong();

        SecureSession(byte[] sendKey, byte[] receiveKey) {
            this.sendKey = new SecretKeySpec(sendKey, "AES");
            this.receiveKey = new SecretKeySpec(receiveKey, "AES");
        }

        byte[] encrypt(byte[] plain) throws GeneralSecurityException {
            return doAesGcm(Cipher.ENCRYPT_MODE, sendKey, sendSequence.getAndIncrement(), plain);
        }

        byte[] encryptPacket(byte[] plain) throws GeneralSecurityException {
            long sequence = sendSequence.getAndIncrement();
            Cipher cipher = createAesGcmCipher(Cipher.ENCRYPT_MODE, sendKey, sequence);
            int encryptedLength = cipher.getOutputSize(plain.length);
            byte[] packet = new byte[ENCRYPTED_HEADER_LENGTH + encryptedLength];
            int written = cipher.doFinal(plain, 0, plain.length, packet, ENCRYPTED_HEADER_LENGTH);
            if (written != encryptedLength) {
                packet = Arrays.copyOf(packet, ENCRYPTED_HEADER_LENGTH + written);
            }

            writeInt32LittleEndian(packet, 0, written);
            return packet;
        }

        byte[] decrypt(byte[] encrypted) throws GeneralSecurityException {
            return doAesGcm(Cipher.DECRYPT_MODE, receiveKey, receiveSequence.getAndIncrement(), encrypted);
        }

        private static byte[] doAesGcm(int mode, SecretKeySpec key, long sequence, byte[] data)
            throws GeneralSecurityException {
            Cipher cipher = createAesGcmCipher(mode, key, sequence);
            return cipher.doFinal(data);
        }

        private static Cipher createAesGcmCipher(int mode, SecretKeySpec key, long sequence)
            throws GeneralSecurityException {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(mode, key, new GCMParameterSpec(AES_TAG_LENGTH * 8, createNonce(sequence)));
            return cipher;
        }

        private static byte[] createNonce(long sequence) {
            byte[] nonce = new byte[12];
            for (int index = 0; index < 8; index++) {
                nonce[4 + index] = (byte) (sequence >>> (8 * index));
            }

            return nonce;
        }
    }
}
