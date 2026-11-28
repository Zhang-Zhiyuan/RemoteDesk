"""Existing Linux endpoints must preserve the signed high capability bit."""
import sys
import struct
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire
import remotedesk_linux_host as host


class ReliableFeedbackCompatibilityTests(unittest.TestCase):
    def test_unknown_signed_bit_does_not_hide_existing_features(self):
        capabilities = -(1 << 31) | wire.CAPABILITY_REMOTE_DESKTOP | wire.CAPABILITY_PRIVACY_SCREEN
        for payload in (wire.encode_device_info("fixture", "Windows", capabilities),
                        wire.encode_viewer_capabilities(capabilities)):
            decoded = wire.decode_control(payload)["capabilities"]
            self.assertEqual(capabilities, decoded)
            self.assertTrue(decoded & wire.CAPABILITY_REMOTE_DESKTOP)
            self.assertTrue(decoded & wire.CAPABILITY_PRIVACY_SCREEN)
            self.assertFalse(decoded & wire.CAPABILITY_FILE_RECEIVE)

    @staticmethod
    def receipt(seq, total, delay=0):
        return struct.pack("<4sIqqiI", b"RVF1", 0, seq, total, delay, 0)

    def test_linux_host_bit_round_trips_without_changing_lower_capabilities(self):
        flags = wire.CAPABILITY_RELIABLE_VIDEO_FEEDBACK | wire.CAPABILITY_FILE_RECEIVE
        for payload in (wire.encode_device_info("Linux", "Linux", flags), wire.encode_viewer_capabilities(flags)):
            peer = wire.decode_control(payload)["capabilities"]
            self.assertLess(peer, 0)
            self.assertEqual(["FileReceive", "ReliableVideoFeedback"], wire.capability_names(peer))

    def test_window_requires_exact_receiver_credit_and_preserves_sequence_across_drops(self):
        window = host.ReliableVideoSendWindow()
        self.assertEqual(1, window.reserve(1000, 0))
        self.assertEqual(2, window.reserve(1000, 33))
        self.assertIsNone(window.reserve(1000, 66))
        for receipt in (self.receipt(2, 1999), self.receipt(4, 2000), self.receipt(2, 2000, 200)):
            window.acknowledge(receipt, 100)
            self.assertIsNone(window.reserve(1000, 101))
        window.acknowledge(self.receipt(2, 2000, 20), 103)
        self.assertEqual(3, window.reserve(1000, 104))
        window.acknowledge(self.receipt(1, 1000), 160)
        self.assertEqual(1000, window.sent - window.acked)
        with self.assertRaises(wire.ProtocolError):
            window.acknowledge(b"x" * 32, 160)

    def test_tracked_payload_only_prefixes_the_negotiated_frame(self):
        encoded = b"\x00\x00\x01\x65\x11"
        plain = wire.encode_video_frame(64, 48, wire.FRAME_FLAG_KEY_FRAME, encoded)
        self.assertEqual(struct.pack("<q", 18) + plain,
            wire.encode_video_frame(64, 48, wire.FRAME_FLAG_KEY_FRAME, encoded, sequence=18))
        self.assertEqual(32, wire.max_message_payload_bytes(wire.MESSAGE_RELIABLE_VIDEO_FEEDBACK))

    def test_capacity_sample_requires_fresh_confirmed_frames_and_includes_ack_delay(self):
        window = host.ReliableVideoSendWindow()
        for i in range(1, 7):
            self.assertEqual(i, window.reserve(50_000, i * 100))
            window.acknowledge(self.receipt(i, i * 50_000, 19), i * 100 + 20)
            if i == 3:
                self.assertEqual((20_000_000, False), window.take_delivery_sample(325))
                self.assertEqual((0, False), window.take_delivery_sample(325))
        self.assertEqual((0, False), window.take_delivery_sample(1000))
        self.assertIsNotNone(window.reserve(50_000, 1100))
        self.assertIsNotNone(window.reserve(50_000, 1150))
        self.assertIsNone(window.reserve(50_000, 1180))
        self.assertEqual((0, True), window.take_delivery_sample(1181))
        self.assertEqual((0, False), window.take_delivery_sample(1181))


if __name__ == "__main__":
    unittest.main()
