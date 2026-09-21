"""Peer capabilities should not look unsupported merely because a label is missing."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_protocol_probe as protocol


class CapabilityNamesTests(unittest.TestCase):
    def test_android_device_identity_has_a_name(self):
        self.assertEqual(["DeviceIdentity"], protocol.capability_names(protocol.CAPABILITY_DEVICE_IDENTITY))
        # Negotiated on the physical RMX5200 during the public-relay audit.
        names = protocol.capability_names(435282703)
        self.assertIn("DeviceIdentity", names)
        self.assertFalse(any(name.startswith("Unknown(") for name in names))

    def test_windows_video_diagnostics_label_does_not_advertise_support(self):
        diagnostic = 1 << 25
        self.assertEqual(["HostVideoDiagnostics"], protocol.capability_names(diagnostic))
        self.assertEqual(0, protocol.BASE_VIEWER_CAPABILITIES & diagnostic)

    def test_unknown_peer_bits_remain_visible(self):
        self.assertEqual(["DeviceIdentity", "HostVideoDiagnostics", "Unknown(1073741824)"],
                         protocol.capability_names(protocol.CAPABILITY_DEVICE_IDENTITY | (1 << 25) | (1 << 30)))
        self.assertEqual([], protocol.capability_names(0))

    def test_known_flags_are_unique_powers_of_two(self):
        flags = [bit for bit, _ in protocol.CAPABILITIES]
        self.assertEqual(len(flags), len(set(flags)))
        self.assertTrue(all(bit > 0 and bit & (bit - 1) == 0 for bit in flags))


if __name__ == "__main__":
    unittest.main()
