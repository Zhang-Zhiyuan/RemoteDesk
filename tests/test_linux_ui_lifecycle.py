"""Tk modal dialogs and event dispatch on an explicitly owned display."""
import gc
import os
from pathlib import Path
import queue
import sys
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires owned Xvfb")
class LinuxUiLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.root = app.tk.Tk()
        self.root.withdraw()
        self.errors = []
        self.root.report_callback_exception = lambda *error: self.errors.append(error)
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.root = self.root
        self.ui.closing = False
        self.ui.events = queue.Queue()
        self.ui.event_poll_after_id = None
        self.ui.viewer = mock.Mock(generation=1, remote_capabilities=app.CAPABILITY_FILE_RECEIVE)
        self.ui.viewer_generation = 1
        self.ui.viewer_window = None
        self.ui.viewer_file_preview_token = 1
        self.ui.viewer_file_preview_active = True
        self.ui.viewer_file_preview_cancel_event = threading.Event()
        self.ui._set_viewer_status = mock.Mock()
        self.ui._set_viewer_file_action_state = mock.Mock()
        self.ui._show_frame = mock.Mock()
        self.ui._cancel_relay_setup = mock.Mock()
        self.ui.disconnect_viewer = mock.Mock()
        self.ui.stop_host = mock.Mock()
        self.addCleanup(self.close_fixture)

    def close_fixture(self):
        for after_id in self.root.tk.call("after", "info"):
            self.root.after_cancel(after_id)
        try:
            self.root.destroy()
        except app.tk.TclError:
            pass
        self.ui = self.root = None
        gc.collect()

    def queue_preview(self):
        item = app.FileTransferPreviewItem("file", "/owned", "owned", 1, "remote/owned")
        preview = app.ViewerFilePreviewResult(
            1, self.ui.viewer, ("/owned",), (item,), "", "Confirm", "Send", "Queued")
        self.ui.events.put(("viewer_file_preview_ready", (1, preview)))
        self.ui.event_poll_after_id = self.root.after(0, self.ui._poll_events)

    def test_confirmation_keeps_frames_and_status_processing(self):
        observed = []

        def confirm(*_args):
            dialog = app.tk.Toplevel(self.root)
            def enqueue():
                self.ui.events.put(("viewer_status", (1, "still connected")))
                self.ui.events.put(("viewer_frame", (1, (1, 1, b"fixture", 0, 1, 1, ""))))
            def inspect():
                observed.append((self.ui._show_frame.call_count,
                                 mock.call("still connected") in self.ui._set_viewer_status.call_args_list))
                dialog.destroy()
            self.root.after(20, enqueue)
            self.root.after(180, inspect)
            dialog.wait_window()
            return False

        self.ui._show_file_transfer_confirmation_dialog = confirm
        self.queue_preview()
        self.root.update()
        self.assertEqual([(1, True)], observed)
        self.assertEqual([], self.errors)

    def test_close_during_confirmation_does_not_restart_polling(self):
        def confirm(*_args):
            dialog = app.tk.Toplevel(self.root)
            self.root.after(20, self.ui.close)
            dialog.wait_window()
            return False

        self.ui._show_file_transfer_confirmation_dialog = confirm
        self.queue_preview()
        self.root.update()
        self.assertTrue(self.ui.closing)
        self.assertIsNone(self.ui.event_poll_after_id)
        self.assertEqual([], self.errors)

    def test_disconnect_is_processed_during_confirmation_and_cannot_send_to_old_viewer(self):
        original_viewer = self.ui.viewer
        self.ui.viewer_reconnect_policy = mock.Mock()
        self.ui.viewer_reconnect_policy.next_delay.return_value = 1
        self.ui._cancel_viewer_stability_timer = mock.Mock()
        self.ui._apply_viewer_capture_target_transition = mock.Mock()
        self.ui._schedule_viewer_reconnect = mock.Mock()
        self.ui.viewer_pressed_keys = mock.Mock()
        self.ui.connect_button = mock.Mock()
        self.ui.disconnect_button = mock.Mock()
        self.ui.frame_label = None
        observed = []

        def confirm(*_args):
            dialog = app.tk.Toplevel(self.root)
            self.root.after(20, lambda: self.ui.events.put(("viewer_closed", 1)))
            def inspect():
                observed.append(self.ui.viewer is None)
                dialog.destroy()
            self.root.after(180, inspect)
            dialog.wait_window()
            return True

        self.ui._show_file_transfer_confirmation_dialog = confirm
        self.queue_preview()
        self.root.update()
        self.assertEqual([True], observed)
        self.ui._schedule_viewer_reconnect.assert_called_once_with(1)
        original_viewer.send_files.assert_not_called()
        self.assertNotIn(mock.call("已取消文件传输。"), self.ui._set_viewer_status.call_args_list)
        self.assertEqual([], self.errors)

    def test_warning_dialogs_are_serialized_while_event_polling_continues(self):
        active, opened, observations = [], [], []

        def warning(_title, message, **_kwargs):
            active.append(message)
            opened.append(message)
            dialog = app.tk.Toplevel(self.root)
            if message == "first":
                def enqueue():
                    self.ui.events.put(("viewer_file_failure", (1, "second")))
                    self.ui.events.put(("viewer_status", (1, "live status")))
                self.root.after(20, enqueue)
                def inspect():
                    observations.append((tuple(active),
                                         mock.call("live status") in self.ui._set_viewer_status.call_args_list))
                    dialog.destroy()
                self.root.after(180, inspect)
            else:
                self.root.after(10, dialog.destroy)
            dialog.wait_window()
            active.pop()

        self.ui.events.put(("viewer_file_failure", (1, "first")))
        self.ui.event_poll_after_id = self.root.after(0, self.ui._poll_events)
        with mock.patch.object(app.messagebox, "showwarning", side_effect=warning):
            self.root.update()
        self.assertEqual(["first", "second"], opened)
        self.assertEqual([(("first",), True)], observations)
        self.assertEqual([], self.errors)

    def test_pending_dialogs_are_cancelled_on_close(self):
        callback = mock.Mock()
        self.ui._defer_ui_dialog(callback)
        pending_id = self.ui.ui_dialog_after_id
        self.ui.close()
        self.root.update()
        callback.assert_not_called()
        self.assertIsNone(self.ui.ui_dialog_after_id)
        self.assertFalse(self.ui.ui_dialog_queue)
        self.assertNotIn(pending_id, self.root.tk.call("after", "info"))
        self.assertEqual([], self.errors)

    def test_pending_dialog_kind_keeps_only_the_newest_result(self):
        callback = mock.Mock()
        for index in range(1000):
            self.ui._defer_ui_dialog(callback, index)
        self.assertEqual(1, len(self.ui.ui_dialog_queue))
        self.root.update()
        callback.assert_called_once_with(999)

    def test_deferred_failure_from_old_viewer_is_not_shown(self):
        self.ui.events.put(("viewer_file_failure", (1, "old session")))
        self.ui._poll_events()
        self.ui.viewer_generation = 2
        with mock.patch.object(app.messagebox, "showwarning") as warning:
            self.root.update()
        warning.assert_not_called()

    def test_deferred_relay_identity_rechecks_setup_generation(self):
        self.ui.relay_setup_generation = 1
        self.ui.relay_setup_busy = True
        self.ui._confirm_relay_identity = mock.Mock()
        self.ui.events.put(("relay_setup", (1, "identity", "server", "fingerprint", "")))
        self.ui._poll_events()
        self.ui.relay_setup_generation = 2
        self.root.update()
        self.ui._confirm_relay_identity.assert_not_called()

    def test_late_old_relay_result_cannot_replace_current_pending_prompt(self):
        self.ui.relay_setup_generation = 2
        self.ui.relay_setup_busy = True
        self.ui._confirm_relay_identity = mock.Mock()
        self.ui.events.put(("relay_setup", (2, "identity", "current", "current-pin", "")))
        self.ui.events.put(("relay_setup", (1, "identity", "old", "old-pin", "")))
        self.ui._poll_events()
        self.root.update()
        self.ui._confirm_relay_identity.assert_called_once_with("current", "current-pin")


if __name__ == "__main__":
    unittest.main()
