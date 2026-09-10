"""A local relay adapter must not queue WAN-sized histories of stale frames."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host


class RelayHostBuffersTests(unittest.TestCase):
    def test_only_loopback_uses_small_frame_queue(self):
        for peer in ("127.0.0.1", "127.0.0.2", "::1", "::ffff:127.0.0.1"):
            with self.subTest(peer=peer):
                self.assertEqual(16384, host.host_send_buffer_bytes(peer))

    def test_lan_wan_and_unknown_preserve_existing_window(self):
        for peer in ("10.7.163.74", "8.138.5.232", "::ffff:10.7.163.74", "2001:db8::1", "unknown"):
            with self.subTest(peer=peer):
                self.assertEqual(host.SOCKET_SEND_BUFFER_BYTES, host.host_send_buffer_bytes(peer))


if __name__ == "__main__":
    unittest.main()
