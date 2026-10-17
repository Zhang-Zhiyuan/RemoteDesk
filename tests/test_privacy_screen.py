"""Privacy wire compatibility and real Linux viewer request/reply dispatch."""
import queue
import concurrent.futures
import threading
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

    def test_restore_does_not_wait_for_inflight_query(self):
        viewer = self.viewer()
        query_sent = threading.Event()
        query_id = []
        def receive(payload):
            request = wire.decode_control(payload)
            if request["action"] == 0:
                query_id.append(request["requestId"])
                query_sent.set()
            else:
                viewer._handle_control(wire.encode_privacy_screen_status(request["requestId"], True, False, True, "restored"))
        with mock.patch.object(viewer, "_write_file_control", side_effect=receive), concurrent.futures.ThreadPoolExecutor() as executor:
            query = executor.submit(viewer.request_privacy_screen, 0, 2)
            self.assertTrue(query_sent.wait(1))
            self.assertFalse(query.done())
            self.assertFalse(viewer.request_privacy_screen(2)["enabled"])
            viewer._handle_control(wire.encode_privacy_screen_status(query_id[0], True, True, True, "old query"))
            self.assertTrue(query.result(1)["enabled"])

    def test_superseded_enable_is_not_sent_after_restore(self):
        viewer = self.viewer()
        with mock.patch.object(viewer, "_write_file_control") as send:
            with self.assertRaises(OSError):
                viewer.request_privacy_screen(1, send_if=lambda: False)
            send.assert_not_called()
        self.assertEqual({}, viewer.privacy_requests)

    def test_ui_restore_supersedes_query_and_enable_without_confirmation_chain(self):
        ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui.viewer = mock.Mock(remote_capabilities=wire.CAPABILITY_PRIVACY_SCREEN)
        ui.viewer.stop_event.is_set.return_value = False
        ui.viewer.request_privacy_screen.side_effect = lambda action, **kwargs: dict(
            success=True, enabled=action != 2, allowed=True, statusMessage="confirmed")
        ui.root, ui.closing, ui.viewer_window = mock.Mock(), False, None
        ui._set_viewer_status = mock.Mock()
        ui._release_pressed_viewer_inputs = mock.Mock()
        workers, callbacks = [], []
        ui.root.after.side_effect = lambda delay, callback: callbacks.append(callback)
        def thread(*, target, **kwargs):
            return mock.Mock(start=lambda: workers.append(target))
        with mock.patch.object(app.threading, "Thread", side_effect=thread), \
                mock.patch.object(app.messagebox, "showinfo") as info, \
                mock.patch.object(app.messagebox, "askokcancel") as confirm:
            for action in (0, 1, 2): ui._show_privacy_screen(action)
            self.assertEqual(3, len(workers))
            # Delayed old replies arrive after the explicit restore acknowledgement.
            for index in (2, 0, 1):
                workers[index]()
                callbacks[index]()
            self.assertFalse(ui._privacy_status["enabled"])
            self.assertIsNone(ui._privacy_command)
            self.assertFalse(ui._privacy_query_busy)
            info.assert_not_called()
            confirm.assert_not_called()


if __name__ == "__main__":
    unittest.main()
