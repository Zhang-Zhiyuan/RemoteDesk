package com.remotedesk.agent;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertThrows;
import static org.junit.Assert.assertTrue;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import org.junit.Test;

public final class LowLatencyVideoProtocolTest {
    @Test
    public void negotiatedFeaturesAreStrictCapabilityIntersection() {
        int local = RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
            RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT;
        int remote = RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT;
        int features = LowLatencyVideoProtocol.negotiatedFeatures(local, remote);
        assertEquals(
            LowLatencyVideoProtocol.FEATURE_CONGESTION_FEEDBACK |
                LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT,
            features);
        assertEquals(0, LowLatencyVideoProtocol.negotiatedFeatures(
            local,
            RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK));
    }
    private static final long CHANNEL_ID = 0x1020_3040_5060_7080L;
    private static final int EPOCH = 0x8122_3344;

    @Test
    public void featureNegotiationMatchesWindowsDependencies() {
        int allCapabilities =
            RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
                RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK |
                RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC |
                RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT |
                RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK |
                RemoteDeskProtocol.CAPABILITY_SHORT_GOP_H264 |
                RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT;

        assertEquals(
            LowLatencyVideoProtocol.KNOWN_FEATURES,
            LowLatencyVideoProtocol.featuresFromCapabilities(allCapabilities));
        assertEquals(
            0,
            LowLatencyVideoProtocol.featuresFromCapabilities(
                allCapabilities &
                    ~RemoteDeskProtocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK));
        assertEquals(
            LowLatencyVideoProtocol.FEATURE_CONGESTION_FEEDBACK,
            LowLatencyVideoProtocol.normalizeFeatures(
                LowLatencyVideoProtocol.FEATURE_CONGESTION_FEEDBACK |
                    LowLatencyVideoProtocol.FEATURE_UDP_MOUSE_INPUT_APPLIED_ACK));
    }

    @Test
    public void offerAndStateControlsUseExactWindowsWireLayout() throws Exception {
        LowLatencyVideoProtocol.Offer offer = fixedOffer();

        byte[] encodedOffer = RemoteDeskTransport.encodeLowLatencyVideoOffer(offer);
        assertEquals(
            "1C01F5DCB00400100000807060504030201044332281" +
                "1112131415161718191A1B1C1D1E1F202122232425262728292A2B2C2D2E2F30" +
                "3132333435363738393A3B3C3D3E3F404142434445464748494A4B4C4D4E4F50" +
                "51525354616263647172737475767778797A7B7C7D7E7F80",
            hex(encodedOffer));
        assertEquals(110, encodedOffer.length);
        assertEquals(RemoteDeskProtocol.CONTROL_LOW_LATENCY_VIDEO_OFFER, encodedOffer[0] & 0xFF);
        assertEquals(LowLatencyVideoProtocol.VERSION, encodedOffer[1] & 0xFF);
        assertEquals(56_565, readUInt16LittleEndian(encodedOffer, 2));
        assertEquals(1_200, readUInt16LittleEndian(encodedOffer, 4));
        assertEquals(4_096, readInt32LittleEndian(encodedOffer, 6));
        assertEquals(CHANNEL_ID, readInt64LittleEndian(encodedOffer, 10));
        assertEquals(EPOCH, readInt32LittleEndian(encodedOffer, 18));

        RemoteDeskTransport.ControlMessage decodedOffer =
            RemoteDeskTransport.decodeControl(encodedOffer);
        assertNotNull(decodedOffer.lowLatencyVideoOffer);
        assertEquals(CHANNEL_ID, decodedOffer.lowLatencyVideoOffer.channelId);
        assertArrayEquals(offer.challenge, decodedOffer.lowLatencyVideoOffer.challenge);
        offer.hostToViewerKey[0] ^= 0x7F;
        assertFalse(offer.hostToViewerKey[0] ==
            decodedOffer.lowLatencyVideoOffer.hostToViewerKey[0]);

        byte[] ready = RemoteDeskTransport.encodeLowLatencyVideoReady(CHANNEL_ID, EPOCH);
        byte[] stop = RemoteDeskTransport.encodeLowLatencyVideoStop(CHANNEL_ID, EPOCH, 7);
        byte[] stopped = RemoteDeskTransport.encodeLowLatencyVideoStopped(CHANNEL_ID, EPOCH, 9);
        assertEquals(13, ready.length);
        assertEquals(14, stop.length);
        assertEquals(14, stopped.length);

        RemoteDeskTransport.ControlMessage decodedReady = RemoteDeskTransport.decodeControl(ready);
        RemoteDeskTransport.ControlMessage decodedStop = RemoteDeskTransport.decodeControl(stop);
        RemoteDeskTransport.ControlMessage decodedStopped = RemoteDeskTransport.decodeControl(stopped);
        assertEquals(CHANNEL_ID, decodedReady.lowLatencyVideoChannelId);
        assertEquals(EPOCH, decodedReady.lowLatencyVideoEpoch);
        assertEquals(0, decodedReady.lowLatencyVideoStopReason);
        assertEquals(7, decodedStop.lowLatencyVideoStopReason);
        assertEquals(9, decodedStopped.lowLatencyVideoStopReason);

        for (byte[] valid : new byte[][] { encodedOffer, ready, stop, stopped }) {
            assertThrows(
                IOException.class,
                () -> RemoteDeskTransport.decodeControl(Arrays.copyOf(valid, valid.length - 1)));
            byte[] trailing = Arrays.copyOf(valid, valid.length + 1);
            assertThrows(IOException.class, () -> RemoteDeskTransport.decodeControl(trailing));
        }
        byte[] invalidVersion = encodedOffer.clone();
        invalidVersion[1] = 2;
        assertThrows(IOException.class,
            () -> RemoteDeskTransport.decodeControl(invalidVersion));
        byte[] invalidDatagramSize = encodedOffer.clone();
        invalidDatagramSize[4] = 0;
        invalidDatagramSize[5] = 1;
        assertThrows(IOException.class,
            () -> RemoteDeskTransport.decodeControl(invalidDatagramSize));
    }

    @Test
    public void viewerCapabilitiesDecodeWithoutBeingSilentlySkipped() throws Exception {
        int expected =
            RemoteDeskProtocol.CAPABILITY_HIGH_FRAME_RATE_H264 |
                RemoteDeskProtocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO |
                RemoteDeskProtocol.CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT;

        RemoteDeskTransport.ControlMessage decoded = RemoteDeskTransport.decodeControl(
            RemoteDeskTransport.encodeViewerCapabilities(expected));

        assertEquals(RemoteDeskProtocol.CONTROL_VIEWER_CAPABILITIES, decoded.kind);
        assertEquals(expected, decoded.capabilities);
    }

    @Test
    public void authenticatedDatagramsRejectAadTamperingAndReplay() throws Exception {
        byte[] key = sequenceBytes(LowLatencyVideoProtocol.KEY_LENGTH, 0x21);
        byte[] nonce = sequenceBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH, 0x71);
        try (LowLatencyVideoProtocol.SendCipher sender =
                 new LowLatencyVideoProtocol.SendCipher(key, nonce, CHANNEL_ID, EPOCH);
             LowLatencyVideoProtocol.ReceiveCipher receiver =
                 new LowLatencyVideoProtocol.ReceiveCipher(key, nonce, CHANNEL_ID, EPOCH)) {
            byte[] first = sender.encrypt(
                LowLatencyVideoProtocol.KIND_FRAME_FRAGMENT,
                10,
                3,
                0,
                0,
                1,
                RemoteDeskProtocol.MESSAGE_FRAME,
                0,
                new byte[] { 1, 2, 3 });
            // Generated independently by System.Security.Cryptography.AesGcm
            // using the Windows wire encoder. This pins header endian order,
            // nonce construction, AAD coverage, ciphertext and tag.
            assertEquals(
                "52445531010334008070605040302010443322810000000000000000" +
                    "0A0000000000000003000000000000000000010003000100" +
                    "CFB3E6136C15CBF0C01A4E804800216C1754CE",
                hex(first));

            LowLatencyVideoProtocol.Datagram packet = receiver.tryDecrypt(first, first.length);
            assertNotNull(packet);
            assertArrayEquals(new byte[] { 1, 2, 3 }, packet.plaintext);
            assertNull(receiver.tryDecrypt(first, first.length));

            byte[] second = sender.encrypt(
                LowLatencyVideoProtocol.KIND_FRAME_FRAGMENT,
                11,
                3,
                0,
                0,
                1,
                RemoteDeskProtocol.MESSAGE_FRAME,
                0,
                new byte[] { 4, 5, 6 });
            byte[] tampered = second.clone();
            tampered[51] ^= 1;
            assertNull(receiver.tryDecrypt(tampered, tampered.length));
            assertArrayEquals(new byte[] { 4, 5, 6 }, receiver.tryDecrypt(second, second.length).plaintext);
        }
    }

    @Test
    public void replayWindowAcceptsOutOfOrderAndEnforcesExactBoundary() {
        LowLatencyVideoProtocol.ReplayWindow replay = new LowLatencyVideoProtocol.ReplayWindow();
        assertTrue(replay.tryCommit(100));
        assertTrue(replay.tryCommit(98));
        assertTrue(replay.tryCommit(99));
        assertFalse(replay.tryCommit(98));

        long highest = LowLatencyVideoProtocol.REPLAY_WINDOW_PACKETS + 10L;
        LowLatencyVideoProtocol.ReplayWindow boundary = new LowLatencyVideoProtocol.ReplayWindow();
        assertTrue(boundary.tryCommit(highest));
        assertTrue(boundary.tryCommit(11));
        assertFalse(boundary.tryCommit(10));
    }

    @Test
    public void xorFecRecoversOneMissingDataFragment() throws Exception {
        byte[] key = sequenceBytes(LowLatencyVideoProtocol.KEY_LENGTH, 0x31);
        byte[] nonce = sequenceBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH, 0x41);
        int maxDatagram = LowLatencyVideoProtocol.MIN_DATAGRAM_BYTES;
        int maxPayload = LowLatencyVideoProtocol.maxFragmentPayloadBytes(maxDatagram);
        byte[] frame = sequenceBytes(maxPayload * 8 - 37, 0x51);
        List<byte[]> encrypted;
        try (LowLatencyVideoProtocol.SendCipher sender =
                 new LowLatencyVideoProtocol.SendCipher(key, nonce, CHANNEL_ID, EPOCH)) {
            encrypted = LowLatencyVideoProtocol.fragmentFrame(
                sender,
                37,
                RemoteDeskProtocol.MESSAGE_VIDEO_FRAME,
                frame,
                maxDatagram,
                true);
        }

        List<LowLatencyVideoProtocol.Datagram> packets = new ArrayList<>();
        try (LowLatencyVideoProtocol.ReceiveCipher receiver =
                 new LowLatencyVideoProtocol.ReceiveCipher(key, nonce, CHANNEL_ID, EPOCH)) {
            for (byte[] datagram : encrypted) {
                packets.add(receiver.tryDecrypt(datagram, datagram.length));
            }
        }
        assertEquals(9, packets.size());

        LowLatencyVideoProtocol.FrameReassembler reassembler =
            new LowLatencyVideoProtocol.FrameReassembler(frame.length, maxDatagram, true);
        LowLatencyVideoProtocol.CompleteFrame complete = null;
        for (LowLatencyVideoProtocol.Datagram packet : packets) {
            if (packet.kind == LowLatencyVideoProtocol.KIND_FRAME_FRAGMENT && packet.fragmentIndex == 3) {
                continue;
            }
            LowLatencyVideoProtocol.CompleteFrame candidate = reassembler.add(packet);
            if (candidate != null) {
                complete = candidate;
            }
        }

        assertNotNull(complete);
        assertArrayEquals(frame, complete.payload);
        assertEquals(1, reassembler.recoveredFragmentCount());
    }

    @Test
    public void feedbackV2RoundTripsAndRejectsInvalidQueueMetadata() {
        LowLatencyVideoProtocol.FeedbackV2 expected = new LowLatencyVideoProtocol.FeedbackV2(
            10_000,
            42,
            50,
            LowLatencyVideoProtocol.FEEDBACK_METRIC_PACKET_DELIVERY |
                LowLatencyVideoProtocol.FEEDBACK_METRIC_FRAME_ASSEMBLY,
            100,
            120_000,
            2,
            1,
            20,
            18,
            2,
            0,
            0,
            0,
            0);
        byte[] payload = LowLatencyVideoProtocol.encodeFeedbackV2(expected);
        LowLatencyVideoProtocol.FeedbackV2 actual =
            LowLatencyVideoProtocol.tryDecodeFeedbackV2(payload);
        assertNotNull(actual);
        assertEquals(expected.totalAuthenticatedWireBytes, actual.totalAuthenticatedWireBytes);
        assertEquals(expected.highestCompletedFrameSequence, actual.highestCompletedFrameSequence);

        byte[] invalid = payload.clone();
        invalid[88] = 1;
        assertNull(LowLatencyVideoProtocol.tryDecodeFeedbackV2(invalid));
    }

    @Test
    public void arrivalTrackerSettlesGapsAndCountsLatePacketsAfterReorderWindow() {
        LowLatencyVideoProtocol.ArrivalTracker tracker =
            new LowLatencyVideoProtocol.ArrivalTracker(1_000_000L);
        tracker.record(100, 100, 1_100_000L);
        tracker.record(102, 100, 1_200_000L);
        tracker.record(101, 100, 1_300_000L);
        tracker.record(166, 100, 1_400_000L);
        LowLatencyVideoProtocol.FeedbackV2 reordered =
            tracker.createFeedback(1_500_000L, 0, 0, 0);
        assertEquals(0, reordered.totalSettledLostPackets);
        assertEquals(0, reordered.totalLatePackets);

        tracker.record(170, 100, 1_600_000L);
        LowLatencyVideoProtocol.FeedbackV2 gap =
            tracker.createFeedback(1_700_000L, 0, 0, 0);
        assertEquals(4, gap.totalSettledLostPackets);

        tracker.record(103, 100, 1_800_000L);
        LowLatencyVideoProtocol.FeedbackV2 late =
            tracker.createFeedback(1_900_000L, 0, 0, 0);
        assertEquals(1, late.totalLatePackets);
        assertEquals(170, late.largestReceivedPacketSequence);
        assertEquals(300, late.ackDelayMicroseconds);
    }

    @Test
    public void everyNonFrameDatagramKindRequiresExactMetadata() {
        byte[] challenge = sequenceBytes(LowLatencyVideoProtocol.CHALLENGE_LENGTH, 1);
        LowLatencyVideoProtocol.Datagram probe = packet(
            LowLatencyVideoProtocol.KIND_BIND_PROBE,
            0,
            challenge.length,
            0,
            0,
            1,
            0,
            0,
            challenge);
        assertTrue(LowLatencyVideoProtocol.isChallengePacket(
            probe,
            LowLatencyVideoProtocol.KIND_BIND_PROBE,
            challenge));
        assertFalse(LowLatencyVideoProtocol.isChallengePacket(
            packet(
                probe.kind,
                1,
                probe.frameLength,
                0,
                0,
                1,
                0,
                0,
                challenge),
            LowLatencyVideoProtocol.KIND_BIND_PROBE,
            challenge));

        LowLatencyVideoProtocol.Datagram feedback = packet(
            LowLatencyVideoProtocol.KIND_FEEDBACK,
            0,
            8,
            0,
            0,
            1,
            0,
            0,
            new byte[8]);
        assertTrue(LowLatencyVideoProtocol.isFeedbackPacket(feedback));
        assertFalse(LowLatencyVideoProtocol.isFeedbackPacket(packet(
            feedback.kind, 0, 8, 0, 0, 1, 0, 1, new byte[8])));

        byte[] mousePayload = new byte[14];
        mousePayload[0] = (byte) RemoteDeskProtocol.INPUT_MOUSE_MOVE;
        mousePayload[1] = (byte) RemoteDeskProtocol.MOUSE_NONE;
        LowLatencyVideoProtocol.Datagram mouse = packet(
            LowLatencyVideoProtocol.KIND_MOUSE_MOVE,
            1,
            14,
            0,
            0,
            1,
            RemoteDeskProtocol.MESSAGE_INPUT,
            0,
            mousePayload);
        assertTrue(LowLatencyVideoProtocol.isMouseMovePacket(mouse));
        assertFalse(LowLatencyVideoProtocol.isMouseMovePacket(packet(
            mouse.kind, 0, 14, 0, 0, 1, RemoteDeskProtocol.MESSAGE_INPUT, 0, new byte[14])));
        byte[] reliableButtonPayload = mousePayload.clone();
        reliableButtonPayload[0] = (byte) RemoteDeskProtocol.INPUT_MOUSE_DOWN;
        reliableButtonPayload[1] = (byte) RemoteDeskProtocol.MOUSE_LEFT;
        assertFalse(LowLatencyVideoProtocol.isMouseMovePacket(packet(
            mouse.kind,
            2,
            14,
            0,
            0,
            1,
            RemoteDeskProtocol.MESSAGE_INPUT,
            0,
            reliableButtonPayload)));

        LowLatencyVideoProtocol.Datagram ack = packet(
            LowLatencyVideoProtocol.KIND_MOUSE_MOVE_APPLIED_ACK,
            1,
            0,
            0,
            0,
            1,
            RemoteDeskProtocol.MESSAGE_INPUT,
            0,
            new byte[0]);
        assertTrue(LowLatencyVideoProtocol.isMouseMoveAppliedAckPacket(ack));
        assertFalse(LowLatencyVideoProtocol.isMouseMoveAppliedAckPacket(packet(
            ack.kind, 1, 1, 0, 0, 1, RemoteDeskProtocol.MESSAGE_INPUT, 0, new byte[0])));

        LowLatencyVideoProtocol.Datagram heartbeat = packet(
            LowLatencyVideoProtocol.KIND_HEARTBEAT,
            0,
            0,
            0,
            0,
            1,
            0,
            0,
            new byte[0]);
        assertTrue(LowLatencyVideoProtocol.isHeartbeatPacket(heartbeat));
        assertFalse(LowLatencyVideoProtocol.isHeartbeatPacket(packet(
            heartbeat.kind, 0, 0, 0, 0, 0, 0, 0, new byte[0])));
    }

    private static LowLatencyVideoProtocol.Offer fixedOffer() {
        return new LowLatencyVideoProtocol.Offer(
            56_565,
            LowLatencyVideoProtocol.DEFAULT_MAX_DATAGRAM_BYTES,
            4_096,
            CHANNEL_ID,
            EPOCH,
            sequenceBytes(LowLatencyVideoProtocol.KEY_LENGTH, 0x11),
            sequenceBytes(LowLatencyVideoProtocol.KEY_LENGTH, 0x31),
            sequenceBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH, 0x51),
            sequenceBytes(LowLatencyVideoProtocol.NONCE_PREFIX_LENGTH, 0x61),
            sequenceBytes(LowLatencyVideoProtocol.CHALLENGE_LENGTH, 0x71));
    }

    private static byte[] sequenceBytes(int length, int start) {
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++) {
            bytes[index] = (byte) (start + index);
        }
        return bytes;
    }

    private static LowLatencyVideoProtocol.Datagram packet(
        int kind,
        long frameSequence,
        int frameLength,
        int fragmentOffset,
        int fragmentIndex,
        int fragmentCount,
        int frameKind,
        int flags,
        byte[] plaintext) {
        return new LowLatencyVideoProtocol.Datagram(
            kind,
            0,
            frameSequence,
            frameLength,
            fragmentOffset,
            fragmentIndex,
            fragmentCount,
            frameKind,
            flags,
            plaintext);
    }

    private static String hex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            result.append(String.format("%02X", value & 0xFF));
        }
        return result.toString();
    }

    private static int readUInt16LittleEndian(byte[] bytes, int offset) {
        return (bytes[offset] & 0xFF) | ((bytes[offset + 1] & 0xFF) << 8);
    }

    private static int readInt32LittleEndian(byte[] bytes, int offset) {
        return (bytes[offset] & 0xFF) |
            ((bytes[offset + 1] & 0xFF) << 8) |
            ((bytes[offset + 2] & 0xFF) << 16) |
            ((bytes[offset + 3] & 0xFF) << 24);
    }

    private static long readInt64LittleEndian(byte[] bytes, int offset) {
        long value = 0;
        for (int index = 0; index < 8; index++) {
            value |= ((long) bytes[offset + index] & 0xFFL) << (8 * index);
        }
        return value;
    }
}
