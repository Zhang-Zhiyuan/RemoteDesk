"""Privacy wire compatibility and real Linux viewer request/reply dispatch."""
import queue
import sys
import unittest
from pathlib import Path
from unittest import mock
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire
import remotedesk_linux_app as app


class PrivacyScreenTests(unittest.TestCase):
    def test_shared_vectors_and_malformed_messages(self):
        for action in (0, 1, 2):
            data = wire.encode_privacy_screen_request("id", action)
            self.assertEqual(bytes.fromhex("2e026964") + bytes([action]), data)
            self.assertEqual(action, wire.decode_control(data)["action"])
        data = wire.encode_privacy_screen_status("id", True, True, True, "中")
        self.assertEqual("2f02696401010103e4b8ad", data.hex())
        self.assertTrue(wire.decode_control(data)["enabled"])
        for payload in (data + b"\x00", data[:6] + b"\x00" + data[7:], bytes.fromhex("2e02696403")):
            with self.assertRaises(wire.ProtocolError):
                wire.decode_control(payload)
        for index in (4, 5, 6):
            with self.assertRaises(wire.ProtocolError):
                wire.decode_control(data[:index] + b"\x02" + data[index + 1:])

    def viewer(self):
        viewer = app.ViewerConnection("192.0.2.1", 56565, "fixture", queue.Queue(), 1)
        viewer.remote_capabilities = wire.CAPABILITY_PRIVACY_SCREEN
        self.addCleanup(viewer.stop_event.set)
        self.addCleanup(viewer.transfer_tasks.close)
        return viewer

    def test_current_ack_only_not_optimistic_and_no_input_required(self):
        viewer = self.viewer()
        def receive(payload):
            request = wire.decode_control(payload)
            viewer._handle_control(wire.encode_privacy_screen_status("stale", True, False, True, "stale"))
            viewer._handle_control(wire.encode_privacy_screen_status(request["requestId"], True, True, True, "confirmed"))
        with mock.patch.object(viewer, "_write_file_control", side_effect=receive):
            result = viewer.request_privacy_screen(1)
        self.assertTrue(result["enabled"])
        self.assertEqual("confirmed", result["statusMessage"])
        self.assertEqual({}, viewer.privacy_requests)

    def test_timeout_disconnect_denial_unsupported_and_retry(self):
        viewer = self.viewer()
        with mock.patch.object(viewer, "_write_file_control"):
            with self.assertRaises(TimeoutError): viewer.request_privacy_screen(1, .001)
        self.assertEqual({}, viewer.privacy_requests)
        def decline(payload):
            request = wire.decode_control(payload)
            viewer._handle_control(wire.encode_privacy_screen_status(request["requestId"], False, False, False, "not allowed"))
        with mock.patch.object(viewer, "_write_file_control", side_effect=decline):
            self.assertFalse(viewer.request_privacy_screen(1)["success"])
        viewer.remote_capabilities = 0
        with mock.patch.object(viewer, "_write_file_control") as send:
            with self.assertRaises(OSError): viewer.request_privacy_screen(1)
            send.assert_not_called()
        viewer.stop_event.set()
        with self.assertRaises(OSError): viewer.request_privacy_screen()


if __name__ == "__main__":
    unittest.main()
