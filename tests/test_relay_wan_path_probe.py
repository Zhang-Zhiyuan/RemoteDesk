"""Verify diagnostic packet framing before drawing network conclusions."""
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location(
    "relay_wan_path_probe", Path(__file__).resolve().parents[1] / "experiments/relay_wan_path_probe.py")
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class WanUdpFramingTests(unittest.TestCase):
    def test_exact_lengths_and_distinct_replay_identity(self):
        for size in (80, 96, 1200):
            identities = set()
            for sequence in (0, 1, 2, 255, 256, 65536):
                packet = probe.udp_packet(b"key", bytes(16), sequence, size)
                self.assertEqual(size, len(packet))
                identity = probe.udp_identity(b"key", packet)
                self.assertIsNotNone(identity)
                self.assertEqual(20, len(identity))
                identities.add(identity)
            self.assertEqual(6, len(identities))

    def test_tampered_truncated_oversized_and_wrong_key_are_rejected(self):
        packet = probe.udp_packet(b"key", bytes(16), 1, 1200)
        for invalid in (packet[:-1], packet + b"x", b"x" + packet[1:], packet[:30] + b"x" + packet[31:], b""):
            self.assertIsNone(probe.udp_identity(b"key", invalid))
        self.assertIsNone(probe.udp_identity(b"other", packet))

    def test_invalid_dimensions_fail_locally(self):
        for size in (79, 1201):
            with self.assertRaises(ValueError):
                probe.udp_packet(b"key", bytes(16), 0, size)
        with self.assertRaises(ValueError):
            probe.udp_packet(b"key", bytes(15), 0, 96)


if __name__ == "__main__":
    unittest.main()
