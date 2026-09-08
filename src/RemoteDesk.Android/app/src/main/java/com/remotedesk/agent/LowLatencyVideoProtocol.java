package com.remotedesk.agent;

import java.io.IOException;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.LongSupplier;

import javax.crypto.AEADBadTagException;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * Wire-compatible implementation of the authenticated RemoteDesk UDP video
 * protocol.  This class deliberately has no Android dependencies so the
 * parser, replay protection and FEC recovery can be exercised by local JVM
 * tests as well as by the host/viewer transports.
 */
final class LowLatencyVideoProtocol {
    static final int VERSION = 1;
    static final int HEADER_LENGTH = 52;
    static final int TAG_LENGTH = 16;
    static final int KEY_LENGTH = 32;
    static final int NONCE_PREFIX_LENGTH = 4;
    static final int CHALLENGE_LENGTH = 16;
    static final int DEFAULT_MAX_DATAGRAM_BYTES = 1200;
    static final int MIN_DATAGRAM_BYTES = 576;
    static final int DEFAULT_MAX_FRAME_BYTES = 8 * 1024 * 1024;
    static final int MAX_FRAGMENT_COUNT = 8192;
    static final int REPLAY_WINDOW_PACKETS = 4096;
    static final int XOR_FEC_DATA_FRAGMENTS_PER_GROUP = 16;
    static final int RECOMMENDED_MINIMUM_XOR_FEC_DATA_FRAGMENTS = 8;
    static final int FEEDBACK_V2_PAYLOAD_LENGTH = 96;

    static final int KIND_BIND_PROBE = 1;
    static final int KIND_BIND_ACK = 2;
    static final int KIND_FRAME_FRAGMENT = 3;
    static final int KIND_FEEDBACK = 4;
    static final int KIND_FEEDBACK_V2 = 5;
    static final int KIND_FRAME_XOR_PARITY = 6;
    static final int KIND_MOUSE_MOVE = 7;
    static final int KIND_MOUSE_MOVE_APPLIED_ACK = 8;
    static final int KIND_HEARTBEAT = 9;

    static final int FEATURE_CONGESTION_FEEDBACK = 1;
    static final int FEATURE_XOR_FEC = 1 << 1;
    static final int FEATURE_UDP_MOUSE_INPUT = 1 << 2;
    static final int FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK = 1 << 3;
    static final int FEATURE_SHORT_GOP_H264 = 1 << 4;
    static final int FEATURE_AUTHENTICATED_HEARTBEAT = 1 << 5;
    static final int KNOWN_FEATURES =
        FEATURE_CONGESTION_FEEDBACK |
            FEATURE_XOR_FEC |
            FEATURE_UDP_MOUSE_INPUT |
            FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK |
            FEATURE_SHORT_GOP_H264 |
            FEATURE_AUTHENTICATED_HEARTBEAT;

    static final int FEEDBACK_METRIC_PACKET_DELIVERY = 1;
    static final int FEEDBACK_METRIC_FRAME_ASSEMBLY = 1 << 1;
    static final int FEEDBACK_METRIC_CONSUMER_QUEUE = 1 << 2;
    static final int FEEDBACK_METRIC_DECODE = 1 << 3;
    static final int KNOWN_FEEDBACK_METRICS =
        FEEDBACK_METRIC_PACKET_DELIVERY |
            FEEDBACK_METRIC_FRAME_ASSEMBLY |
            FEEDBACK_METRIC_CONSUMER_QUEUE |
            FEEDBACK_METRIC_DECODE;

    // UInt32 little-endian representation of ASCII "RDU1".
    private static final int MAGIC = 0x31554452;

    private LowLatencyVideoProtocol() {
    }

    static int featuresFromCapabilities(int capabilities) {
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK) == 0) {
            return 0;
        }

        int features = FEATURE_CONGESTION_FEEDBACK;
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC) != 0) {
            features |= FEATURE_XOR_FEC;
        }
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT) != 0) {
            features |= FEATURE_UDP_MOUSE_INPUT;
        }
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK) != 0) {
            features |= FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK;
        }
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264) != 0) {
            features |= FEATURE_SHORT_GOP_H264;
        }
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT) != 0) {
            features |= FEATURE_AUTHENTICATED_HEARTBEAT;
        }
        return normalizeFeatures(features);
    }

    static int negotiatedFeatures(int localCapabilities, int remoteCapabilities) {
        int common = localCapabilities & remoteCapabilities;
        if ((common & RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO) == 0) {
            return 0;
        }
        return featuresFromCapabilities(common);
    }

    static int normalizeFeatures(int features) {
        int normalized = features & KNOWN_FEATURES;
        if ((normalized & FEATURE_CONGESTION_FEEDBACK) == 0) {
            return 0;
        }
        if ((normalized & FEATURE_UDP_MOUSE_INPUT) == 0) {
            normalized &= ~FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK;
        }
        return normalized;
    }

    static int maxFragmentPayloadBytes(int maxDatagramBytes) {
        if (maxDatagramBytes < MIN_DATAGRAM_BYTES ||
            maxDatagramBytes > DEFAULT_MAX_DATAGRAM_BYTES) {
            throw new IllegalArgumentException("Invalid UDP datagram size.");
        }
        return maxDatagramBytes - HEADER_LENGTH - TAG_LENGTH;
    }

    static int frameFragmentCount(int frameLength, int maxDatagramBytes) {
        if (frameLength <= 0 || frameLength > DEFAULT_MAX_FRAME_BYTES) {
            throw new IllegalArgumentException("Invalid UDP video frame size.");
        }
        int maxPayload = maxFragmentPayloadBytes(maxDatagramBytes);
        int fragments = (frameLength + maxPayload - 1) / maxPayload;
        if (fragments > MAX_FRAGMENT_COUNT) {
            throw new IllegalArgumentException("UDP video frame has too many fragments.");
        }
        return fragments;
    }

    static boolean shouldSendXorFec(int dataFragmentCount) {
        return dataFragmentCount >= RECOMMENDED_MINIMUM_XOR_FEC_DATA_FRAGMENTS &&
            dataFragmentCount <= MAX_FRAGMENT_COUNT;
    }

    static int xorFecGroupCount(int dataFragmentCount) {
        if (dataFragmentCount <= 0 || dataFragmentCount > MAX_FRAGMENT_COUNT) {
            throw new IllegalArgumentException("Invalid XOR FEC fragment count.");
        }
        return (dataFragmentCount + XOR_FEC_DATA_FRAGMENTS_PER_GROUP - 1) /
            XOR_FEC_DATA_FRAGMENTS_PER_GROUP;
    }

    static Offer createOffer(int port) {
        SecureRandom random = new SecureRandom();
        long channelId;
        do {
            channelId = random.nextLong();
        } while (channelId == 0);
        int epoch;
        do {
            epoch = random.nextInt();
        } while (epoch == 0);
        return new Offer(
            port,
            DEFAULT_MAX_DATAGRAM_BYTES,
            DEFAULT_MAX_FRAME_BYTES,
            channelId,
            epoch,
            randomBytes(random, KEY_LENGTH),
            randomBytes(random, KEY_LENGTH),
            randomBytes(random, NONCE_PREFIX_LENGTH),
            randomBytes(random, NONCE_PREFIX_LENGTH),
            randomBytes(random, CHALLENGE_LENGTH));
    }

    private static byte[] randomBytes(SecureRandom random, int length) {
        byte[] bytes = new byte[length];
        random.nextBytes(bytes);
        return bytes;
    }

    static List<byte[]> fragmentFrame(
        SendCipher cipher,
        long frameSequence,
        int frameKind,
        byte[] framePayload,
        int maxDatagramBytes,
        boolean enableXorFec) throws GeneralSecurityException {
        if (frameSequence < 0 ||
            (frameKind != RemoteDeskProtocol.MESSAGE_FRAME &&
                frameKind != RemoteDeskProtocol.MESSAGE_VIDEO_FRAME)) {
            throw new IllegalArgumentException("Invalid UDP frame metadata.");
        }

        int maxPayload = maxFragmentPayloadBytes(maxDatagramBytes);
        int fragmentCount = frameFragmentCount(framePayload.length, maxDatagramBytes);
        int parityCount = enableXorFec && shouldSendXorFec(fragmentCount)
            ? xorFecGroupCount(fragmentCount)
            : 0;
        List<byte[]> datagrams = new ArrayList<>(fragmentCount + parityCount);
        for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++) {
            int offset = fragmentIndex * maxPayload;
            int length = Math.min(maxPayload, framePayload.length - offset);
            datagrams.add(cipher.encrypt(
                KIND_FRAME_FRAGMENT,
                frameSequence,
                framePayload.length,
                offset,
                fragmentIndex,
                fragmentCount,
                frameKind,
                0,
                Arrays.copyOfRange(framePayload, offset, offset + length)));
        }

        for (int groupIndex = 0; groupIndex < parityCount; groupIndex++) {
            int firstFragment = groupIndex * XOR_FEC_DATA_FRAGMENTS_PER_GROUP;
            int startOffset = firstFragment * maxPayload;
            int parityLength = Math.min(maxPayload, framePayload.length - startOffset);
            byte[] parity = new byte[parityLength];
            int endFragment = Math.min(
                fragmentCount,
                firstFragment + XOR_FEC_DATA_FRAGMENTS_PER_GROUP);
            for (int fragmentIndex = firstFragment; fragmentIndex < endFragment; fragmentIndex++) {
                int offset = fragmentIndex * maxPayload;
                int length = Math.min(maxPayload, framePayload.length - offset);
                for (int index = 0; index < length; index++) {
                    parity[index] ^= framePayload[offset + index];
                }
            }
            datagrams.add(cipher.encrypt(
                KIND_FRAME_XOR_PARITY,
                frameSequence,
                framePayload.length,
                startOffset,
                groupIndex,
                fragmentCount,
                frameKind,
                0,
                parity));
        }
        return datagrams;
    }

    static byte[] createJpegFramePayload(
        int width,
        int height,
        double captureMillis,
        double encodeMillis,
        byte[] jpegBytes) {
        byte[] payload = new byte[24 + jpegBytes.length];
        writeInt32LittleEndian(payload, 0, width);
        writeInt32LittleEndian(payload, 4, height);
        writeInt64LittleEndian(payload, 8, Double.doubleToLongBits(captureMillis));
        writeInt64LittleEndian(payload, 16, Double.doubleToLongBits(encodeMillis));
        System.arraycopy(jpegBytes, 0, payload, 24, jpegBytes.length);
        return payload;
    }

    static byte[] createVideoFramePayload(
        int width,
        int height,
        int encoding,
        int flags,
        double captureMillis,
        double encodeMillis,
        byte[] encodedBytes,
        int encodedLength) {
        if (encodedLength <= 0 || encodedLength > encodedBytes.length) {
            throw new IllegalArgumentException("Invalid encoded video length.");
        }
        byte[] payload = new byte[32 + encodedLength];
        writeInt32LittleEndian(payload, 0, encoding);
        writeInt32LittleEndian(payload, 4, width);
        writeInt32LittleEndian(payload, 8, height);
        writeInt32LittleEndian(payload, 12, flags);
        writeInt64LittleEndian(payload, 16, Double.doubleToLongBits(captureMillis));
        writeInt64LittleEndian(payload, 24, Double.doubleToLongBits(encodeMillis));
        System.arraycopy(encodedBytes, 0, payload, 32, encodedLength);
        return payload;
    }

    static boolean isChallengePacket(Datagram packet, int expectedKind, byte[] challenge) {
        return packet != null &&
            packet.kind == expectedKind &&
            packet.frameSequence == 0 &&
            packet.frameLength == challenge.length &&
            packet.fragmentOffset == 0 &&
            packet.fragmentIndex == 0 &&
            packet.fragmentCount == 1 &&
            packet.frameKind == 0 &&
            packet.flags == 0 &&
            constantTimeEquals(packet.plaintext, challenge);
    }

    static boolean isFeedbackPacket(Datagram packet) {
        if (packet == null || packet.frameSequence != 0 ||
            packet.fragmentOffset != 0 || packet.fragmentIndex != 0 ||
            packet.fragmentCount != 1 || packet.frameKind != 0 || packet.flags != 0) {
            return false;
        }
        if (packet.kind == KIND_FEEDBACK) {
            return packet.frameLength == 8 && packet.plaintext.length == 8;
        }
        return packet.kind == KIND_FEEDBACK_V2 &&
            packet.frameLength == FEEDBACK_V2_PAYLOAD_LENGTH &&
            packet.plaintext.length == FEEDBACK_V2_PAYLOAD_LENGTH &&
            tryDecodeFeedbackV2(packet.plaintext) != null;
    }

    static boolean isMouseMovePacket(Datagram packet) {
        return packet != null &&
            packet.kind == KIND_MOUSE_MOVE &&
            packet.frameSequence > 0 &&
            packet.frameLength == 14 &&
            packet.fragmentOffset == 0 &&
            packet.fragmentIndex == 0 &&
            packet.fragmentCount == 1 &&
            packet.frameKind == RemoteDeskProtocol.MESSAGE_INPUT &&
            packet.flags == 0 &&
            packet.plaintext.length == 14 &&
            (packet.plaintext[0] & 0xFF) == RemoteDeskProtocol.INPUT_MOUSE_MOVE &&
            (packet.plaintext[1] & 0xFF) == RemoteDeskProtocol.MOUSE_NONE;
    }

    static boolean isMouseMoveAppliedAckPacket(Datagram packet) {
        return packet != null &&
            packet.kind == KIND_MOUSE_MOVE_APPLIED_ACK &&
            packet.frameSequence > 0 &&
            packet.frameLength == 0 &&
            packet.fragmentOffset == 0 &&
            packet.fragmentIndex == 0 &&
            packet.fragmentCount == 1 &&
            packet.frameKind == RemoteDeskProtocol.MESSAGE_INPUT &&
            packet.flags == 0 &&
            packet.plaintext.length == 0;
    }

    static boolean isHeartbeatPacket(Datagram packet) {
        return packet != null &&
            packet.kind == KIND_HEARTBEAT &&
            packet.frameSequence == 0 &&
            packet.frameLength == 0 &&
            packet.fragmentOffset == 0 &&
            packet.fragmentIndex == 0 &&
            packet.fragmentCount == 1 &&
            packet.frameKind == 0 &&
            packet.flags == 0 &&
            packet.plaintext.length == 0;
    }

    private static boolean constantTimeEquals(byte[] first, byte[] second) {
        if (first.length != second.length) {
            return false;
        }
        int difference = 0;
        for (int index = 0; index < first.length; index++) {
            difference |= first[index] ^ second[index];
        }
        return difference == 0;
    }

    static final class Offer {
        final int port;
        final int maxDatagramBytes;
        final int maxFrameBytes;
        final long channelId;
        final int epoch;
        final byte[] hostToViewerKey;
        final byte[] viewerToHostKey;
        final byte[] hostNoncePrefix;
        final byte[] viewerNoncePrefix;
        final byte[] challenge;

        Offer(
            int port,
            int maxDatagramBytes,
            int maxFrameBytes,
            long channelId,
            int epoch,
            byte[] hostToViewerKey,
            byte[] viewerToHostKey,
            byte[] hostNoncePrefix,
            byte[] viewerNoncePrefix,
            byte[] challenge) {
            this.port = port;
            this.maxDatagramBytes = maxDatagramBytes;
            this.maxFrameBytes = maxFrameBytes;
            this.channelId = channelId;
            this.epoch = epoch;
            this.hostToViewerKey = hostToViewerKey.clone();
            this.viewerToHostKey = viewerToHostKey.clone();
            this.hostNoncePrefix = hostNoncePrefix.clone();
            this.viewerNoncePrefix = viewerNoncePrefix.clone();
            this.challenge = challenge.clone();
            validate();
        }

        void validate() {
            if (port <= 0 || port > 65_535 ||
                maxDatagramBytes < MIN_DATAGRAM_BYTES ||
                maxDatagramBytes > DEFAULT_MAX_DATAGRAM_BYTES ||
                maxFrameBytes < 24 || maxFrameBytes > DEFAULT_MAX_FRAME_BYTES ||
                channelId == 0 || epoch == 0 ||
                hostToViewerKey.length != KEY_LENGTH ||
                viewerToHostKey.length != KEY_LENGTH ||
                hostNoncePrefix.length != NONCE_PREFIX_LENGTH ||
                viewerNoncePrefix.length != NONCE_PREFIX_LENGTH ||
                challenge.length != CHALLENGE_LENGTH) {
                throw new IllegalArgumentException("Invalid low-latency UDP offer.");
            }
            int fragments = (maxFrameBytes + maxFragmentPayloadBytes(maxDatagramBytes) - 1) /
                maxFragmentPayloadBytes(maxDatagramBytes);
            if (fragments > MAX_FRAGMENT_COUNT) {
                throw new IllegalArgumentException("Low-latency UDP offer exceeds fragment limit.");
            }
        }

        boolean matches(long candidateChannelId, int candidateEpoch) {
            return channelId == candidateChannelId && epoch == candidateEpoch;
        }

        Offer copy() {
            return new Offer(
                port,
                maxDatagramBytes,
                maxFrameBytes,
                channelId,
                epoch,
                hostToViewerKey,
                viewerToHostKey,
                hostNoncePrefix,
                viewerNoncePrefix,
                challenge);
        }

        void clearSecrets() {
            Arrays.fill(hostToViewerKey, (byte) 0);
            Arrays.fill(viewerToHostKey, (byte) 0);
            Arrays.fill(hostNoncePrefix, (byte) 0);
            Arrays.fill(viewerNoncePrefix, (byte) 0);
            Arrays.fill(challenge, (byte) 0);
        }
    }

    static final class Datagram {
        final int kind;
        final long packetSequence;
        final long frameSequence;
        final int frameLength;
        final int fragmentOffset;
        final int fragmentIndex;
        final int fragmentCount;
        final int frameKind;
        final int flags;
        final byte[] plaintext;

        Datagram(
            int kind,
            long packetSequence,
            long frameSequence,
            int frameLength,
            int fragmentOffset,
            int fragmentIndex,
            int fragmentCount,
            int frameKind,
            int flags,
            byte[] plaintext) {
            this.kind = kind;
            this.packetSequence = packetSequence;
            this.frameSequence = frameSequence;
            this.frameLength = frameLength;
            this.fragmentOffset = fragmentOffset;
            this.fragmentIndex = fragmentIndex;
            this.fragmentCount = fragmentCount;
            this.frameKind = frameKind;
            this.flags = flags;
            this.plaintext = plaintext;
        }
    }

    static final class SendCipher implements AutoCloseable {
        private SecretKeySpec key;
        private byte[] noncePrefix;
        private final long channelId;
        private final int epoch;
        private long nextSequence;
        private boolean exhausted;

        SendCipher(byte[] key, byte[] noncePrefix, long channelId, int epoch) {
            if (key.length != KEY_LENGTH || noncePrefix.length != NONCE_PREFIX_LENGTH ||
                channelId == 0 || epoch == 0) {
                throw new IllegalArgumentException("Invalid UDP send cipher parameters.");
            }
            this.key = new SecretKeySpec(key.clone(), "AES");
            this.noncePrefix = noncePrefix.clone();
            this.channelId = channelId;
            this.epoch = epoch;
        }

        synchronized byte[] encrypt(
            int kind,
            long frameSequence,
            int frameLength,
            int fragmentOffset,
            int fragmentIndex,
            int fragmentCount,
            int frameKind,
            int flags,
            byte[] plaintext) throws GeneralSecurityException {
            ensureOpen();
            if (exhausted) {
                throw new GeneralSecurityException("UDP packet sequence is exhausted.");
            }
            long sequence = nextSequence;
            nextSequence++;
            if (nextSequence == -1L) {
                // Sequence UInt64.MaxValue is intentionally never used, just
                // like the Windows implementation.
                exhausted = true;
            }

            byte[] datagram = new byte[HEADER_LENGTH + plaintext.length + TAG_LENGTH];
            writeHeader(
                datagram,
                kind,
                channelId,
                epoch,
                sequence,
                frameSequence,
                frameLength,
                fragmentOffset,
                fragmentIndex,
                fragmentCount,
                plaintext.length,
                frameKind,
                flags);
            Cipher cipher = createCipher(Cipher.ENCRYPT_MODE, key, noncePrefix, sequence);
            cipher.updateAAD(datagram, 0, HEADER_LENGTH);
            byte[] encrypted = cipher.doFinal(plaintext);
            System.arraycopy(encrypted, 0, datagram, HEADER_LENGTH, encrypted.length);
            Arrays.fill(encrypted, (byte) 0);
            return datagram;
        }

        private void ensureOpen() {
            if (key == null || noncePrefix == null) {
                throw new IllegalStateException("UDP send cipher is closed.");
            }
        }

        @Override
        public synchronized void close() {
            if (noncePrefix != null) {
                Arrays.fill(noncePrefix, (byte) 0);
            }
            key = null;
            noncePrefix = null;
        }
    }

    static final class ReceiveCipher implements AutoCloseable {
        private SecretKeySpec key;
        private byte[] noncePrefix;
        private final long channelId;
        private final int epoch;
        private final ReplayWindow replayWindow = new ReplayWindow();

        ReceiveCipher(byte[] key, byte[] noncePrefix, long channelId, int epoch) {
            if (key.length != KEY_LENGTH || noncePrefix.length != NONCE_PREFIX_LENGTH ||
                channelId == 0 || epoch == 0) {
                throw new IllegalArgumentException("Invalid UDP receive cipher parameters.");
            }
            this.key = new SecretKeySpec(key.clone(), "AES");
            this.noncePrefix = noncePrefix.clone();
            this.channelId = channelId;
            this.epoch = epoch;
        }

        synchronized Datagram tryDecrypt(byte[] datagram, int length) throws GeneralSecurityException {
            ensureOpen();
            Header header = tryReadHeader(datagram, length, channelId, epoch);
            if (header == null || !replayWindow.wouldAccept(header.packetSequence)) {
                return null;
            }

            Cipher cipher = createCipher(
                Cipher.DECRYPT_MODE,
                key,
                noncePrefix,
                header.packetSequence);
            cipher.updateAAD(datagram, 0, HEADER_LENGTH);
            byte[] plaintext;
            try {
                plaintext = cipher.doFinal(
                    datagram,
                    HEADER_LENGTH,
                    header.payloadLength + TAG_LENGTH);
            } catch (AEADBadTagException ex) {
                return null;
            }
            if (plaintext.length != header.payloadLength ||
                !replayWindow.tryCommit(header.packetSequence)) {
                Arrays.fill(plaintext, (byte) 0);
                return null;
            }
            return new Datagram(
                header.kind,
                header.packetSequence,
                header.frameSequence,
                header.frameLength,
                header.fragmentOffset,
                header.fragmentIndex,
                header.fragmentCount,
                header.frameKind,
                header.flags,
                plaintext);
        }

        private void ensureOpen() {
            if (key == null || noncePrefix == null) {
                throw new IllegalStateException("UDP receive cipher is closed.");
            }
        }

        @Override
        public synchronized void close() {
            if (noncePrefix != null) {
                Arrays.fill(noncePrefix, (byte) 0);
            }
            key = null;
            noncePrefix = null;
        }
    }

    static final class ReplayWindow {
        private final long[] seen = new long[(REPLAY_WINDOW_PACKETS + 63) / 64];
        private long highest;
        private boolean initialized;

        synchronized boolean wouldAccept(long sequence) {
            if (!initialized || Long.compareUnsigned(sequence, highest) > 0) {
                return true;
            }
            return !isTooOld(sequence) && !isMarked(sequence);
        }

        synchronized boolean tryCommit(long sequence) {
            if (!wouldAccept(sequence)) {
                return false;
            }
            if (!initialized) {
                highest = sequence;
                initialized = true;
                mark(sequence);
                return true;
            }
            if (Long.compareUnsigned(sequence, highest) > 0) {
                long advance = sequence - highest;
                if (Long.compareUnsigned(advance, REPLAY_WINDOW_PACKETS) >= 0) {
                    Arrays.fill(seen, 0L);
                } else {
                    for (long offset = 1; Long.compareUnsigned(offset, advance) <= 0; offset++) {
                        clear(highest + offset);
                    }
                }
                highest = sequence;
            }
            mark(sequence);
            return true;
        }

        private boolean isTooOld(long sequence) {
            return initialized &&
                Long.compareUnsigned(sequence, highest) <= 0 &&
                Long.compareUnsigned(highest - sequence, REPLAY_WINDOW_PACKETS) >= 0;
        }

        private boolean isMarked(long sequence) {
            int slot = (int) Long.remainderUnsigned(sequence, REPLAY_WINDOW_PACKETS);
            return (seen[slot / 64] & (1L << (slot % 64))) != 0;
        }

        private void mark(long sequence) {
            int slot = (int) Long.remainderUnsigned(sequence, REPLAY_WINDOW_PACKETS);
            seen[slot / 64] |= 1L << (slot % 64);
        }

        private void clear(long sequence) {
            int slot = (int) Long.remainderUnsigned(sequence, REPLAY_WINDOW_PACKETS);
            seen[slot / 64] &= ~(1L << (slot % 64));
        }
    }

    static final class CompleteFrame {
        final long sequence;
        final int frameKind;
        final byte[] payload;

        CompleteFrame(long sequence, int frameKind, byte[] payload) {
            this.sequence = sequence;
            this.frameKind = frameKind;
            this.payload = payload;
        }
    }

    /** Keeps two adjacent frames so one late fragment cannot block the latest frame. */
    static final class FrameReassembler {
        private static final long FRAME_TIMEOUT_NANOS = 250_000_000L;

        private final int maxFrameBytes;
        private final int maxFragmentPayloadBytes;
        private final boolean xorFecEnabled;
        private final LongSupplier nanoTime;
        private final Map<Long, Assembly> assemblies = new HashMap<>();
        private long highestCompletedSequence = -1;
        private long completedFrameCount;
        private long abandonedIncompleteFrameCount;
        private long recoveredFragmentCount;

        FrameReassembler(int maxFrameBytes, int maxDatagramBytes, boolean xorFecEnabled) {
            this(maxFrameBytes, maxDatagramBytes, xorFecEnabled, System::nanoTime);
        }

        FrameReassembler(
            int maxFrameBytes,
            int maxDatagramBytes,
            boolean xorFecEnabled,
            LongSupplier nanoTime) {
            if (maxFrameBytes < 24 || maxFrameBytes > DEFAULT_MAX_FRAME_BYTES) {
                throw new IllegalArgumentException("Invalid reassembly frame limit.");
            }
            this.maxFrameBytes = maxFrameBytes;
            this.maxFragmentPayloadBytes = maxFragmentPayloadBytes(maxDatagramBytes);
            this.xorFecEnabled = xorFecEnabled;
            this.nanoTime = nanoTime;
        }

        synchronized CompleteFrame add(Datagram packet) {
            expireTimedOutFrames();
            if (!isValidFramePacket(packet) ||
                (highestCompletedSequence >= 0 && packet.frameSequence <= highestCompletedSequence)) {
                return null;
            }

            Assembly assembly = assemblies.get(packet.frameSequence);
            if (assembly == null) {
                if (assemblies.size() >= 2) {
                    Long oldest = assemblies.keySet().stream().min(Comparator.naturalOrder()).orElse(null);
                    if (oldest != null && packet.frameSequence <= oldest) {
                        return null;
                    }
                    abandon(oldest);
                }
                assembly = new Assembly(packet, nanoTime.getAsLong(), xorFecEnabled);
                assemblies.put(packet.frameSequence, assembly);
            } else if (!assembly.matches(packet)) {
                return null;
            }

            if (packet.kind == KIND_FRAME_FRAGMENT) {
                if (assembly.received[packet.fragmentIndex]) {
                    return null;
                }
                System.arraycopy(
                    packet.plaintext,
                    0,
                    assembly.buffer,
                    packet.fragmentOffset,
                    packet.plaintext.length);
                assembly.received[packet.fragmentIndex] = true;
                assembly.receivedCount++;
                if (xorFecEnabled) {
                    recoveredFragmentCount += assembly.tryRecover(
                        packet.fragmentIndex / XOR_FEC_DATA_FRAGMENTS_PER_GROUP,
                        maxFragmentPayloadBytes);
                }
            } else {
                int groupIndex = packet.fragmentIndex;
                if (assembly.parity[groupIndex] != null) {
                    return null;
                }
                assembly.parity[groupIndex] = packet.plaintext.clone();
                recoveredFragmentCount += assembly.tryRecover(groupIndex, maxFragmentPayloadBytes);
            }

            if (assembly.receivedCount != assembly.fragmentCount) {
                return null;
            }

            assemblies.remove(packet.frameSequence);
            highestCompletedSequence = packet.frameSequence;
            completedFrameCount++;
            abandonOlderThan(highestCompletedSequence);
            return new CompleteFrame(packet.frameSequence, assembly.frameKind, assembly.buffer);
        }

        synchronized void expireTimedOutFrames() {
            long now = nanoTime.getAsLong();
            List<Long> expired = new ArrayList<>();
            for (Map.Entry<Long, Assembly> item : assemblies.entrySet()) {
                if (now - item.getValue().startedAtNanos >= FRAME_TIMEOUT_NANOS) {
                    expired.add(item.getKey());
                }
            }
            for (Long sequence : expired) {
                abandon(sequence);
            }
        }

        synchronized long highestCompletedSequence() {
            return highestCompletedSequence;
        }

        synchronized long completedFrameCount() {
            return completedFrameCount;
        }

        synchronized long abandonedIncompleteFrameCount() {
            return abandonedIncompleteFrameCount;
        }

        synchronized long recoveredFragmentCount() {
            return recoveredFragmentCount;
        }

        private boolean isValidFramePacket(Datagram packet) {
            if (packet.kind != KIND_FRAME_FRAGMENT &&
                (packet.kind != KIND_FRAME_XOR_PARITY || !xorFecEnabled)) {
                return false;
            }
            if (packet.flags != 0 || packet.frameSequence < 0 ||
                (packet.frameKind != RemoteDeskProtocol.MESSAGE_FRAME &&
                    packet.frameKind != RemoteDeskProtocol.MESSAGE_VIDEO_FRAME) ||
                packet.frameLength <= 0 || packet.frameLength > maxFrameBytes ||
                packet.fragmentCount <= 0 || packet.fragmentCount > MAX_FRAGMENT_COUNT) {
                return false;
            }
            int expectedCount =
                (packet.frameLength + maxFragmentPayloadBytes - 1) / maxFragmentPayloadBytes;
            if (packet.fragmentCount != expectedCount) {
                return false;
            }
            if (packet.kind == KIND_FRAME_FRAGMENT) {
                if (packet.fragmentIndex < 0 || packet.fragmentIndex >= expectedCount) {
                    return false;
                }
                int expectedOffset = packet.fragmentIndex * maxFragmentPayloadBytes;
                int expectedLength = Math.min(
                    maxFragmentPayloadBytes,
                    packet.frameLength - expectedOffset);
                return packet.fragmentOffset == expectedOffset &&
                    packet.plaintext.length == expectedLength;
            }
            int groupCount = xorFecGroupCount(expectedCount);
            if (packet.fragmentIndex < 0 || packet.fragmentIndex >= groupCount) {
                return false;
            }
            int expectedOffset =
                packet.fragmentIndex * XOR_FEC_DATA_FRAGMENTS_PER_GROUP * maxFragmentPayloadBytes;
            int expectedLength = Math.min(
                maxFragmentPayloadBytes,
                packet.frameLength - expectedOffset);
            return packet.fragmentOffset == expectedOffset &&
                packet.plaintext.length == expectedLength;
        }

        private void abandonOlderThan(long completedSequence) {
            List<Long> stale = new ArrayList<>();
            for (Long sequence : assemblies.keySet()) {
                if (sequence <= completedSequence) {
                    stale.add(sequence);
                }
            }
            for (Long sequence : stale) {
                abandon(sequence);
            }
        }

        private void abandon(Long sequence) {
            if (sequence != null && assemblies.remove(sequence) != null) {
                abandonedIncompleteFrameCount++;
            }
        }
    }

    private static final class Assembly {
        final long frameSequence;
        final int frameLength;
        final int fragmentCount;
        final int frameKind;
        final int flags;
        final long startedAtNanos;
        final byte[] buffer;
        final boolean[] received;
        final byte[][] parity;
        int receivedCount;

        Assembly(Datagram packet, long startedAtNanos, boolean xorFecEnabled) {
            this.frameSequence = packet.frameSequence;
            this.frameLength = packet.frameLength;
            this.fragmentCount = packet.fragmentCount;
            this.frameKind = packet.frameKind;
            this.flags = packet.flags;
            this.startedAtNanos = startedAtNanos;
            this.buffer = new byte[frameLength];
            this.received = new boolean[fragmentCount];
            this.parity = xorFecEnabled ? new byte[xorFecGroupCount(fragmentCount)][] : new byte[0][];
        }

        boolean matches(Datagram packet) {
            return packet.frameLength == frameLength &&
                packet.fragmentCount == fragmentCount &&
                packet.frameKind == frameKind &&
                packet.flags == flags;
        }

        int tryRecover(int groupIndex, int maxPayload) {
            byte[] groupParity = parity[groupIndex];
            if (groupParity == null) {
                return 0;
            }
            int first = groupIndex * XOR_FEC_DATA_FRAGMENTS_PER_GROUP;
            int end = Math.min(fragmentCount, first + XOR_FEC_DATA_FRAGMENTS_PER_GROUP);
            int missing = -1;
            for (int index = first; index < end; index++) {
                if (!received[index]) {
                    if (missing >= 0) {
                        return 0;
                    }
                    missing = index;
                }
            }
            if (missing < 0) {
                parity[groupIndex] = null;
                return 0;
            }

            byte[] recovered = groupParity.clone();
            for (int fragmentIndex = first; fragmentIndex < end; fragmentIndex++) {
                if (fragmentIndex == missing) {
                    continue;
                }
                int offset = fragmentIndex * maxPayload;
                int length = Math.min(maxPayload, frameLength - offset);
                for (int index = 0; index < length; index++) {
                    recovered[index] ^= buffer[offset + index];
                }
            }
            int missingOffset = missing * maxPayload;
            int missingLength = Math.min(maxPayload, frameLength - missingOffset);
            if (missingLength > recovered.length) {
                parity[groupIndex] = null;
                return 0;
            }
            for (int index = missingLength; index < recovered.length; index++) {
                if (recovered[index] != 0) {
                    parity[groupIndex] = null;
                    return 0;
                }
            }
            System.arraycopy(recovered, 0, buffer, missingOffset, missingLength);
            received[missing] = true;
            receivedCount++;
            parity[groupIndex] = null;
            return 1;
        }
    }

    static final class FeedbackV2 {
        final long receiverElapsedMicroseconds;
        final long largestReceivedPacketSequence;
        final int ackDelayMicroseconds;
        final int validMetrics;
        final long totalAuthenticatedPackets;
        final long totalAuthenticatedWireBytes;
        final long totalSettledLostPackets;
        final long totalLatePackets;
        final long highestCompletedFrameSequence;
        final long totalCompletedFrames;
        final long totalAbandonedIncompleteFrames;
        final long totalConsumerDroppedFrames;
        final int consumerQueueDepth;
        final int consumerQueueCapacity;
        final int averageDecodeMicroseconds;

        FeedbackV2(
            long receiverElapsedMicroseconds,
            long largestReceivedPacketSequence,
            int ackDelayMicroseconds,
            int validMetrics,
            long totalAuthenticatedPackets,
            long totalAuthenticatedWireBytes,
            long totalSettledLostPackets,
            long totalLatePackets,
            long highestCompletedFrameSequence,
            long totalCompletedFrames,
            long totalAbandonedIncompleteFrames,
            long totalConsumerDroppedFrames,
            int consumerQueueDepth,
            int consumerQueueCapacity,
            int averageDecodeMicroseconds) {
            this.receiverElapsedMicroseconds = receiverElapsedMicroseconds;
            this.largestReceivedPacketSequence = largestReceivedPacketSequence;
            this.ackDelayMicroseconds = ackDelayMicroseconds;
            this.validMetrics = validMetrics;
            this.totalAuthenticatedPackets = totalAuthenticatedPackets;
            this.totalAuthenticatedWireBytes = totalAuthenticatedWireBytes;
            this.totalSettledLostPackets = totalSettledLostPackets;
            this.totalLatePackets = totalLatePackets;
            this.highestCompletedFrameSequence = highestCompletedFrameSequence;
            this.totalCompletedFrames = totalCompletedFrames;
            this.totalAbandonedIncompleteFrames = totalAbandonedIncompleteFrames;
            this.totalConsumerDroppedFrames = totalConsumerDroppedFrames;
            this.consumerQueueDepth = consumerQueueDepth;
            this.consumerQueueCapacity = consumerQueueCapacity;
            this.averageDecodeMicroseconds = averageDecodeMicroseconds;
            validateFeedback(this);
        }
    }

    static byte[] encodeFeedbackV2(FeedbackV2 feedback) {
        validateFeedback(feedback);
        byte[] payload = new byte[FEEDBACK_V2_PAYLOAD_LENGTH];
        writeInt64LittleEndian(payload, 0, feedback.receiverElapsedMicroseconds);
        writeInt64LittleEndian(payload, 8, feedback.largestReceivedPacketSequence);
        writeInt32LittleEndian(payload, 16, feedback.ackDelayMicroseconds);
        writeInt32LittleEndian(payload, 20, feedback.validMetrics);
        writeInt64LittleEndian(payload, 24, feedback.totalAuthenticatedPackets);
        writeInt64LittleEndian(payload, 32, feedback.totalAuthenticatedWireBytes);
        writeInt64LittleEndian(payload, 40, feedback.totalSettledLostPackets);
        writeInt64LittleEndian(payload, 48, feedback.totalLatePackets);
        writeInt64LittleEndian(payload, 56, feedback.highestCompletedFrameSequence);
        writeInt64LittleEndian(payload, 64, feedback.totalCompletedFrames);
        writeInt64LittleEndian(payload, 72, feedback.totalAbandonedIncompleteFrames);
        writeInt64LittleEndian(payload, 80, feedback.totalConsumerDroppedFrames);
        writeUInt16LittleEndian(payload, 88, feedback.consumerQueueDepth);
        writeUInt16LittleEndian(payload, 90, feedback.consumerQueueCapacity);
        writeInt32LittleEndian(payload, 92, feedback.averageDecodeMicroseconds);
        return payload;
    }

    static FeedbackV2 tryDecodeFeedbackV2(byte[] payload) {
        if (payload.length != FEEDBACK_V2_PAYLOAD_LENGTH) {
            return null;
        }
        try {
            return new FeedbackV2(
                readInt64LittleEndian(payload, 0),
                readInt64LittleEndian(payload, 8),
                readInt32LittleEndian(payload, 16),
                readInt32LittleEndian(payload, 20),
                readInt64LittleEndian(payload, 24),
                readInt64LittleEndian(payload, 32),
                readInt64LittleEndian(payload, 40),
                readInt64LittleEndian(payload, 48),
                readInt64LittleEndian(payload, 56),
                readInt64LittleEndian(payload, 64),
                readInt64LittleEndian(payload, 72),
                readInt64LittleEndian(payload, 80),
                readUInt16LittleEndian(payload, 88),
                readUInt16LittleEndian(payload, 90),
                readInt32LittleEndian(payload, 92));
        } catch (IllegalArgumentException ex) {
            return null;
        }
    }

    private static void validateFeedback(FeedbackV2 feedback) {
        if ((feedback.validMetrics & ~KNOWN_FEEDBACK_METRICS) != 0 ||
            Long.compareUnsigned(
                Integer.toUnsignedLong(feedback.ackDelayMicroseconds),
                feedback.receiverElapsedMicroseconds) > 0 ||
            Long.compareUnsigned(feedback.totalLatePackets, feedback.totalAuthenticatedPackets) > 0 ||
            feedback.consumerQueueDepth < 0 || feedback.consumerQueueDepth > 65_535 ||
            feedback.consumerQueueCapacity < 0 || feedback.consumerQueueCapacity > 65_535 ||
            feedback.consumerQueueDepth > feedback.consumerQueueCapacity ||
            ((feedback.validMetrics & FEEDBACK_METRIC_CONSUMER_QUEUE) == 0 &&
                (feedback.totalConsumerDroppedFrames != 0 ||
                    feedback.consumerQueueDepth != 0 ||
                    feedback.consumerQueueCapacity != 0)) ||
            ((feedback.validMetrics & FEEDBACK_METRIC_DECODE) == 0 &&
                feedback.averageDecodeMicroseconds != 0)) {
            throw new IllegalArgumentException("Invalid FeedbackV2 payload.");
        }
    }

    static final class ArrivalTracker {
        private static final long REORDERING_WINDOW_PACKETS = 64;
        private final long startedAtNanos;
        private final java.util.HashSet<Long> unsettledPackets = new java.util.HashSet<>();
        private long largestPacketSequence;
        private long largestPacketReceivedAtNanos;
        private boolean initialized;
        private boolean hasSettledAny;
        private long nextSequenceToSettle;
        private long totalPackets;
        private long totalWireBytes;
        private long totalSettledLostPackets;
        private long totalLatePackets;

        ArrivalTracker() {
            this(System.nanoTime());
        }

        ArrivalTracker(long startedAtNanos) {
            this.startedAtNanos = startedAtNanos;
        }

        synchronized void record(long packetSequence, int wireBytes, long receivedAtNanos) {
            totalPackets++;
            totalWireBytes += Math.max(0, wireBytes);
            if (!initialized || Long.compareUnsigned(packetSequence, largestPacketSequence) > 0) {
                if (!initialized) {
                    nextSequenceToSettle = packetSequence;
                    unsettledPackets.add(packetSequence);
                } else {
                    unsettledPackets.add(packetSequence);
                }
                initialized = true;
                largestPacketSequence = packetSequence;
                largestPacketReceivedAtNanos = receivedAtNanos;
                settleOldPackets();
                return;
            }

            if (Long.compareUnsigned(packetSequence, nextSequenceToSettle) < 0) {
                if (!hasSettledAny) {
                    nextSequenceToSettle = packetSequence;
                    unsettledPackets.add(packetSequence);
                } else {
                    totalLatePackets = saturatingIncrement(totalLatePackets);
                }
            } else {
                unsettledPackets.add(packetSequence);
            }
            settleOldPackets();
        }

        synchronized FeedbackV2 createFeedback(
            long nowNanos,
            long highestCompletedFrameSequence,
            long totalCompletedFrames,
            long totalAbandonedIncompleteFrames) {
            long elapsedMicros = Math.max(0, (nowNanos - startedAtNanos) / 1_000L);
            long ackDelay = initialized
                ? Math.max(0, (nowNanos - largestPacketReceivedAtNanos) / 1_000L)
                : 0;
            int boundedAckDelay = (int) Math.min(0xFFFF_FFFFL, ackDelay);
            return new FeedbackV2(
                elapsedMicros,
                initialized ? largestPacketSequence : 0,
                boundedAckDelay,
                FEEDBACK_METRIC_PACKET_DELIVERY | FEEDBACK_METRIC_FRAME_ASSEMBLY,
                totalPackets,
                totalWireBytes,
                totalSettledLostPackets,
                totalLatePackets,
                Math.max(0, highestCompletedFrameSequence),
                Math.max(0, totalCompletedFrames),
                Math.max(0, totalAbandonedIncompleteFrames),
                0,
                0,
                0,
                0);
        }

        private void settleOldPackets() {
            if (!initialized ||
                Long.compareUnsigned(largestPacketSequence, REORDERING_WINDOW_PACKETS) < 0) {
                return;
            }
            long settleThrough = largestPacketSequence - REORDERING_WINDOW_PACKETS;
            if (Long.compareUnsigned(settleThrough, nextSequenceToSettle) < 0) {
                return;
            }
            long expected = settleThrough - nextSequenceToSettle + 1;
            long received = 0;
            java.util.Iterator<Long> iterator = unsettledPackets.iterator();
            while (iterator.hasNext()) {
                long sequence = iterator.next();
                if (Long.compareUnsigned(sequence, settleThrough) <= 0) {
                    iterator.remove();
                    received++;
                }
            }
            if (Long.compareUnsigned(received, expected) < 0) {
                totalSettledLostPackets = saturatingAdd(
                    totalSettledLostPackets,
                    expected - received);
            }
            hasSettledAny = true;
            nextSequenceToSettle = settleThrough + 1;
        }

        private static long saturatingIncrement(long value) {
            return value == -1L ? value : value + 1;
        }

        private static long saturatingAdd(long value, long increment) {
            long remaining = -1L - value;
            return Long.compareUnsigned(remaining, increment) < 0
                ? -1L
                : value + increment;
        }
    }

    private static Header tryReadHeader(byte[] datagram, int length, long channelId, int epoch) {
        if (length < HEADER_LENGTH + TAG_LENGTH || length > datagram.length ||
            readInt32LittleEndian(datagram, 0) != MAGIC ||
            (datagram[4] & 0xFF) != VERSION ||
            readUInt16LittleEndian(datagram, 6) != HEADER_LENGTH ||
            readInt64LittleEndian(datagram, 8) != channelId ||
            readInt32LittleEndian(datagram, 16) != epoch) {
            return null;
        }
        int kind = datagram[5] & 0xFF;
        int payloadLength = readUInt16LittleEndian(datagram, 48);
        if (kind < KIND_BIND_PROBE || kind > KIND_HEARTBEAT ||
            length != HEADER_LENGTH + payloadLength + TAG_LENGTH) {
            return null;
        }
        return new Header(
            kind,
            readInt64LittleEndian(datagram, 20),
            readInt64LittleEndian(datagram, 28),
            readInt32LittleEndian(datagram, 36),
            readInt32LittleEndian(datagram, 40),
            readUInt16LittleEndian(datagram, 44),
            readUInt16LittleEndian(datagram, 46),
            payloadLength,
            datagram[50] & 0xFF,
            datagram[51] & 0xFF);
    }

    private static void writeHeader(
        byte[] output,
        int kind,
        long channelId,
        int epoch,
        long packetSequence,
        long frameSequence,
        int frameLength,
        int fragmentOffset,
        int fragmentIndex,
        int fragmentCount,
        int payloadLength,
        int frameKind,
        int flags) {
        if (output.length < HEADER_LENGTH || kind < KIND_BIND_PROBE || kind > KIND_HEARTBEAT ||
            fragmentIndex < 0 || fragmentIndex > 65_535 ||
            fragmentCount < 0 || fragmentCount > 65_535 ||
            payloadLength < 0 || payloadLength > 65_535 ||
            frameLength < 0 || fragmentOffset < 0 ||
            frameKind < 0 || frameKind > 255 || flags < 0 || flags > 255) {
            throw new IllegalArgumentException("Invalid UDP datagram header.");
        }
        Arrays.fill(output, 0, HEADER_LENGTH, (byte) 0);
        writeInt32LittleEndian(output, 0, MAGIC);
        output[4] = VERSION;
        output[5] = (byte) kind;
        writeUInt16LittleEndian(output, 6, HEADER_LENGTH);
        writeInt64LittleEndian(output, 8, channelId);
        writeInt32LittleEndian(output, 16, epoch);
        writeInt64LittleEndian(output, 20, packetSequence);
        writeInt64LittleEndian(output, 28, frameSequence);
        writeInt32LittleEndian(output, 36, frameLength);
        writeInt32LittleEndian(output, 40, fragmentOffset);
        writeUInt16LittleEndian(output, 44, fragmentIndex);
        writeUInt16LittleEndian(output, 46, fragmentCount);
        writeUInt16LittleEndian(output, 48, payloadLength);
        output[50] = (byte) frameKind;
        output[51] = (byte) flags;
    }

    private static Cipher createCipher(
        int mode,
        SecretKeySpec key,
        byte[] noncePrefix,
        long sequence) throws GeneralSecurityException {
        byte[] nonce = new byte[12];
        System.arraycopy(noncePrefix, 0, nonce, 0, NONCE_PREFIX_LENGTH);
        writeInt64LittleEndian(nonce, NONCE_PREFIX_LENGTH, sequence);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(mode, key, new GCMParameterSpec(TAG_LENGTH * 8, nonce));
        Arrays.fill(nonce, (byte) 0);
        return cipher;
    }

    private static int readUInt16LittleEndian(byte[] bytes, int offset) {
        return (bytes[offset] & 0xFF) | ((bytes[offset + 1] & 0xFF) << 8);
    }

    private static void writeUInt16LittleEndian(byte[] bytes, int offset, int value) {
        bytes[offset] = (byte) value;
        bytes[offset + 1] = (byte) (value >>> 8);
    }

    private static int readInt32LittleEndian(byte[] bytes, int offset) {
        return (bytes[offset] & 0xFF) |
            ((bytes[offset + 1] & 0xFF) << 8) |
            ((bytes[offset + 2] & 0xFF) << 16) |
            ((bytes[offset + 3] & 0xFF) << 24);
    }

    private static void writeInt32LittleEndian(byte[] bytes, int offset, int value) {
        bytes[offset] = (byte) value;
        bytes[offset + 1] = (byte) (value >>> 8);
        bytes[offset + 2] = (byte) (value >>> 16);
        bytes[offset + 3] = (byte) (value >>> 24);
    }

    private static long readInt64LittleEndian(byte[] bytes, int offset) {
        long value = 0;
        for (int index = 0; index < 8; index++) {
            value |= ((long) bytes[offset + index] & 0xFFL) << (index * 8);
        }
        return value;
    }

    private static void writeInt64LittleEndian(byte[] bytes, int offset, long value) {
        for (int index = 0; index < 8; index++) {
            bytes[offset + index] = (byte) (value >>> (index * 8));
        }
    }

    private static final class Header {
        final int kind;
        final long packetSequence;
        final long frameSequence;
        final int frameLength;
        final int fragmentOffset;
        final int fragmentIndex;
        final int fragmentCount;
        final int payloadLength;
        final int frameKind;
        final int flags;

        Header(
            int kind,
            long packetSequence,
            long frameSequence,
            int frameLength,
            int fragmentOffset,
            int fragmentIndex,
            int fragmentCount,
            int payloadLength,
            int frameKind,
            int flags) {
            this.kind = kind;
            this.packetSequence = packetSequence;
            this.frameSequence = frameSequence;
            this.frameLength = frameLength;
            this.fragmentOffset = fragmentOffset;
            this.fragmentIndex = fragmentIndex;
            this.fragmentCount = fragmentCount;
            this.payloadLength = payloadLength;
            this.frameKind = frameKind;
            this.flags = flags;
        }
    }
}
