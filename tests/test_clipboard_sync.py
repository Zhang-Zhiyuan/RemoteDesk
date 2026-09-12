import os
from pathlib import Path
import queue
import sys
import threading
import time
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_host as host
import remotedesk_protocol_probe as wire

TEXT = "简体中文、繁體 😀\r\n第二行\t缩进\n"


class ClipboardWireTests(unittest.TestCase):
    def test_text_roundtrip_and_probe_redaction(self):
        for text in (TEXT, " ", "", "😀" * 128000):
            encoded = wire.encode_clipboard_text(text)
            self.assertNotIn("text", wire.decode_control(encoded))
            self.assertEqual(text, wire.decode_control(encoded, include_clipboard_text=True)["text"])
            self.assertEqual(text, wire.decode_control(wire.encode_clipboard_set_text(text))["text"])

    def test_utf16_limits_match_windows_and_android(self):
        for text in ("😀" * 128000 + "x", "x" * 256001):
            with self.assertRaises(wire.ProtocolError):
                wire.encode_clipboard_set_text(text)
        raw = ("😀" * 128001).encode("utf-8")
        with self.assertRaises(wire.ProtocolError):
            wire.decode_control(bytes([wire.CONTROL_CLIPBOARD_TEXT]) + wire.encode_7bit_int(len(raw)) + raw)

    def test_control_message_truncation_does_not_split_emoji(self):
        payload = wire.encode_clipboard_status(False, "😀" * 3000)
        self.assertEqual("😀" * 2048, wire.decode_control(payload)["statusMessage"])

    def test_host_read_keeps_utf8_and_crlf_without_locale_conversion(self):
        with mock.patch.object(host.shutil, "which", return_value="/usr/bin/xclip"), \
                mock.patch.object(host.subprocess, "run", return_value=mock.Mock(returncode=0, stdout=TEXT.encode("utf-8"))) as run:
            self.assertEqual(TEXT, host.read_clipboard_text())
            self.assertNotIn("text", run.call_args.kwargs)

    def test_host_write_uses_utf8_bytes(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(host.shutil, "which", return_value="/usr/bin/xclip"), \
                mock.patch.object(host.subprocess, "run", return_value=mock.Mock(returncode=0)) as run:
            self.assertTrue(host.write_clipboard_text(TEXT))
            self.assertEqual(TEXT.encode("utf-8"), run.call_args.kwargs["input"])
            self.assertIn("UTF8_STRING", run.call_args.args[0])

    def test_wayland_write_uses_wayland_clipboard(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "test-only"}), \
                mock.patch.object(host.shutil, "which", return_value="/usr/bin/wl-copy"), \
                mock.patch.object(host.subprocess, "run", return_value=mock.Mock(returncode=0)) as run:
            self.assertTrue(host.write_clipboard_text(TEXT))
            self.assertEqual("wl-copy", run.call_args.args[0][0])


class ViewerClipboardTests(unittest.TestCase):
    def test_only_manual_relay_operations_get_the_longer_deadline(self):
        for via_relay, paste, expected in [(True, False, 130), (True, True, 108), (False, False, 108)]:
            with self.subTest(via_relay=via_relay, paste=paste):
                self.viewer.relay_options = object() if via_relay else None
                with mock.patch.object(app.time, "monotonic", return_value=100), \
                        mock.patch.object(app.threading, "Thread"):
                    self.assertTrue(self.viewer.request_clipboard(read=False, text=TEXT, paste=paste))
                    self.assertEqual(expected, self.viewer.clipboard_pending.deadline)
                self.viewer.clipboard_pending = None

    def setUp(self):
        self.viewer = app.ViewerConnection("127.0.0.1", 1, "fixture", queue.Queue(), 42)
        self.viewer.sock = mock.Mock()
        self.viewer.session = object()
        self.viewer.remote_capabilities = wire.CAPABILITY_CLIPBOARD_TEXT | wire.CAPABILITY_INPUT_CONTROL
        self.addCleanup(self.viewer.stop_event.set)

    def stage(self, read=True, baseline="before"):
        request = app.ViewerClipboardRequest(read, baseline)
        self.viewer.clipboard_pending = self.viewer.clipboard_latest = request
        return request

    def wait_finished(self):
        deadline = time.monotonic() + 2
        while self.viewer.clipboard_pending is not None and time.monotonic() < deadline:
            time.sleep(.01)
        self.assertIsNone(self.viewer.clipboard_pending)

    def test_read_reply_preserves_text_and_generation(self):
        request = self.stage()
        self.viewer._handle_control(wire.encode_clipboard_text(TEXT))
        self.assertTrue(request.completed.is_set())
        self.assertEqual(TEXT, request.text)
        self.assertTrue(self.viewer.can_apply_clipboard(request, "before"))
        self.assertFalse(self.viewer.can_apply_clipboard(request, "new copy"))
        self.viewer.stop_event.set()
        self.assertFalse(self.viewer.can_apply_clipboard(request, "before"))

    def test_timeout_blocks_retry_until_late_reply_is_drained(self):
        request = self.stage()
        request.deadline = time.monotonic() - 1
        self.assertFalse(self.viewer.request_clipboard(read=True))
        self.assertFalse(self.viewer.can_apply_clipboard(request, "before"))
        self.viewer._handle_control(wire.encode_clipboard_text("late"))
        self.assertIsNone(self.viewer.clipboard_pending)

    def test_unsolicited_reply_is_not_applied(self):
        self.viewer._handle_control(wire.encode_clipboard_text(TEXT))
        self.assertTrue(self.viewer.events.empty())

    def test_capture_status_is_not_a_clipboard_ack(self):
        request = self.stage(read=False)
        self.viewer._handle_control(wire.encode_clipboard_status(True,
            "screen ready\nRemoteDesk.CaptureTargetStatus/v1|available|eA==|eQ==|1"))
        self.assertFalse(request.completed.is_set())
        self.viewer._handle_control(wire.encode_clipboard_status(True, "updated"))
        self.assertTrue(request.completed.is_set())

    def test_paste_waits_for_remote_clipboard_ack_and_queues_complete_shortcut(self):
        sent = threading.Event()
        self.viewer._send_control = lambda payload: sent.set()
        self.assertTrue(self.viewer.request_clipboard(read=False, text=TEXT, paste=True))
        self.assertTrue(sent.wait(1))
        self.assertFalse(self.viewer.pending_inputs)
        self.viewer._handle_control(wire.encode_clipboard_status(True, "updated"))
        self.wait_finished()
        self.assertEqual([app.INPUT_KEY_DOWN, app.INPUT_KEY_DOWN, app.INPUT_KEY_UP, app.INPUT_KEY_UP],
                         [kind for kind, _ in self.viewer.pending_inputs])
        self.assertEqual([0x11, 0x56, 0x56, 0x11],
                         [int.from_bytes(payload[10:14], "little") for _, payload in self.viewer.pending_inputs])

    def test_failed_set_never_pastes_old_clipboard(self):
        sent = threading.Event()
        self.viewer._send_control = lambda payload: sent.set()
        self.assertTrue(self.viewer.request_clipboard(read=False, text=TEXT, paste=True))
        self.assertTrue(sent.wait(1))
        self.viewer._handle_control(wire.encode_clipboard_status(False, "clipboard unavailable"))
        self.wait_finished()
        self.assertFalse(self.viewer.pending_inputs)

    def test_empty_reply_leaves_local_clipboard_untouched(self):
        self.viewer._send_control = lambda payload: self.viewer._handle_control(wire.encode_clipboard_text(""))
        self.assertTrue(self.viewer.request_clipboard(read=True, baseline="keep"))
        self.wait_finished()
        events = list(self.viewer.events.queue)
        self.assertTrue(events)
        self.assertNotIn("viewer_clipboard_text", [event for event, _ in events])

    def test_receive_posts_only_to_own_generation(self):
        self.viewer._send_control = lambda payload: self.viewer._handle_control(wire.encode_clipboard_text(TEXT))
        self.assertTrue(self.viewer.request_clipboard(read=True, baseline="keep"))
        self.wait_finished()
        event, value = self.viewer.events.get_nowait()
        self.assertEqual("viewer_clipboard_text", event)
        self.assertEqual(42, value[0])
        self.assertEqual(TEXT, value[1][1])

    def test_empty_or_oversized_local_text_is_not_sent(self):
        self.assertFalse(self.viewer.request_clipboard(read=False, text=""))
        self.assertFalse(self.viewer.request_clipboard(read=False, text="😀" * 128001))
        self.assertIsNone(self.viewer.clipboard_pending)


class ClipboardUiShortcutTests(unittest.TestCase):
    def setUp(self):
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.viewer_clipboard = mock.Mock()
        self.ui._send_virtual_key_input = mock.Mock(return_value=True)
        self.ui.viewer_pressed_keys = app.ViewerPressedKeyState()

    def test_ctrl_v_is_intercepted_and_shift_is_kept_for_terminal_paste(self):
        event = SimpleNamespace(keysym="V", state=0x5)
        self.assertEqual("break", self.ui._viewer_key_press(event))
        self.ui.viewer_clipboard.assert_called_once_with(paste=True, paste_shift=True)
        self.ui._send_virtual_key_input.assert_not_called()
        self.ui._viewer_key_release(event)
        self.ui._send_virtual_key_input.assert_not_called()

    def test_copy_release_requests_clipboard_after_remote_input_flush(self):
        for key in ("c", "x"):
            self.ui.viewer_clipboard.reset_mock()
            event = SimpleNamespace(keysym=key, state=0x4)
            self.ui._viewer_key_press(event)
            self.ui._viewer_key_release(event)
            self.ui.viewer_clipboard.assert_called_once_with(read=True, after_copy=True)
            self.assertEqual(0, self.ui.viewer_pressed_keys.count)


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires isolated Xvfb")
class IsolatedSystemClipboardTests(unittest.TestCase):
    def test_real_x11_clipboard_keeps_chinese_emoji_and_newlines(self):
        # The test runner owns this X server; never replace a user's clipboard.
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        root.clipboard_clear()
        root.clipboard_append(TEXT)
        root.update()
        # Keep Tk servicing X selection requests while xclip reads it.
        result = []
        thread = threading.Thread(target=lambda: result.append(host.read_clipboard_text()))
        thread.start()
        deadline = time.monotonic() + 5
        while thread.is_alive() and time.monotonic() < deadline:
            root.update()
            time.sleep(.01)
        thread.join(.1)
        self.assertEqual([TEXT], result)
        self.assertTrue(host.write_clipboard_text(TEXT + "回传"))
        root.update()
        self.assertEqual(TEXT + "回传", root.clipboard_get())


if __name__ == "__main__":
    unittest.main()
