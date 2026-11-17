"""Existing Linux endpoints must preserve the signed high capability bit."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire


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


if __name__ == "__main__":
    unittest.main()
