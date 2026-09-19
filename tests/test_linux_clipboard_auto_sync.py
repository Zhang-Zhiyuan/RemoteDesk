from __future__ import annotations

import hashlib
import os
from pathlib import Path
import queue
import sys
import threading
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_host as host
import remotedesk_protocol_probe as wire


def local(text, stamp="", owner=10, can_replace=True):
    return app.LocalClipboardSnapshot.from_text(text, stamp, owner, can_replace)


def remote(text):
    return dict(success=True, revision=hashlib.sha256(text.encode()).hexdigest(),
                hasText=bool(text), changed=True, text=text)


class ClipboardAutoStateTests(unittest.TestCase):
    def setUp(self):
        self.state = app.ClipboardSyncState()

    def test_initially_different_clipboards_only_establish_baselines(self):
        self.assertEqual("baseline", self.state.observe(local("local"), remote("remote")))
        self.assertEqual("unchanged", self.state.observe(local("local"), remote("remote")))

    def test_local_change_push_and_successful_echo_is_not_sent_again(self):
        self.state.observe(local("first"), remote("remote"))
        text = "中文😀\r\n第二行\t"
        changed = local(text, "new selection")
        self.assertEqual("push", self.state.observe(changed, remote("remote")))
        self.state.committed(changed, changed.revision)
        for _ in range(10):
            self.assertEqual("equal", self.state.observe(changed, remote(text)))

    def test_remote_change_pull_then_reowned_local_is_not_echoed(self):
        self.state.observe(local("first"), remote("remote"))
        self.assertEqual("pull", self.state.observe(local("first"), remote("new remote")))
        applied = local("new remote", "new timestamp", 22)
        self.state.committed(applied, remote("new remote")["revision"])
        self.assertEqual("equal", self.state.observe(applied, remote("new remote")))

    def test_same_value_recopied_with_new_owner_or_timestamp_is_user_intent(self):
        for updated in (local("local", "2", 10), local("local", "1", 11)):
            with self.subTest(updated=updated):
                state = app.ClipboardSyncState()
                state.observe(local("local", "1", 10), remote("remote"))
                self.assertEqual("push", state.observe(updated, remote("remote")))

    def test_simultaneous_change_keeps_both_and_allows_explicit_recopy(self):
        self.state.observe(local("old local", "1"), remote("old remote"))
        self.assertEqual("conflict", self.state.observe(local("new local", "2"), remote("new remote")))
        self.assertEqual("unchanged", self.state.observe(local("new local", "2"), remote("new remote")))
        self.assertEqual("push", self.state.observe(local("new local", "3"), remote("new remote")))

    def test_empty_and_file_selection_never_clear_or_replace_other_side(self):
        self.state.observe(local("local"), remote("remote"))
        self.assertEqual("unchanged", self.state.observe(local("local"), remote("")))
        self.assertEqual("equal", self.state.observe(local(""), remote("")))
        self.assertEqual("conflict", self.state.observe(local("", can_replace=False), remote("new")))

    def test_failed_send_retries_only_if_no_remote_conflict(self):
        self.state.observe(local("old"), remote("remote"))
        for _ in range(3):
            self.assertEqual("push", self.state.observe(local("new"), remote("remote")))
        self.assertEqual("conflict", self.state.observe(local("new"), remote("changed during retry")))


class ClipboardAutoTransportTests(unittest.TestCase):
    def setUp(self):
        self.viewer = app.ViewerConnection("127.0.0.1", 1, "owned fixture", queue.Queue(), 5)
        self.viewer.sock, self.viewer.session = object(), object()
        self.viewer.remote_capabilities = wire.CAPABILITY_CLIPBOARD_TEXT | wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
        self.addCleanup(self.viewer.stop_event.set)
        self.addCleanup(self.viewer.clipboard_auto_stop.set)

    def test_capability_is_advertised_with_and_without_native_presenter(self):
        for native in (True, False):
            self.assertTrue(app.linux_viewer_capabilities(native) & wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1)
            self.assertTrue(app.linux_viewer_capabilities(native) & wire.CAPABILITY_CLIPBOARD_TEXT)

    def test_correlated_snapshot_does_not_complete_legacy_manual_request(self):
        manual = app.ViewerClipboardRequest(False, "manual")
        self.viewer.clipboard_pending = manual
        def send(payload):
            request = wire.decode_control(payload)
            value = remote("中文😀")
            self.viewer._handle_control(wire.encode_clipboard_snapshot(
                "unrelated", True, value["revision"], True, True, value["text"], ""))
            self.assertFalse(self.viewer.clipboard_snapshot_pending[1].is_set())
            self.viewer._handle_control(wire.encode_clipboard_snapshot(
                request["requestId"], True, value["revision"], True, True, value["text"], ""))
        self.viewer._send_control = send
        result = self.viewer._request_clipboard_snapshot("")
        self.assertEqual("中文😀", result["text"])
        self.assertFalse(manual.completed.is_set())
        self.assertIs(self.viewer.clipboard_pending, manual)
        self.assertIsNone(self.viewer.clipboard_snapshot_pending)

    def test_timed_out_snapshot_is_removed_and_late_id_is_ignored(self):
        captured = []
        self.viewer._send_control = lambda payload: captured.append(wire.decode_control(payload))
        with mock.patch.object(app.time, "monotonic", side_effect=[0, 5]):
            self.assertIsNone(self.viewer._request_clipboard_snapshot(""))
        value = remote("late")
        self.viewer._handle_control(wire.encode_clipboard_snapshot(
            captured[0]["requestId"], True, value["revision"], True, True, "late", ""))
        self.assertIsNone(self.viewer.clipboard_snapshot_pending)
        self.assertTrue(self.viewer.events.empty())

    def test_old_host_never_receives_new_wire_message(self):
        self.viewer.remote_capabilities = wire.CAPABILITY_CLIPBOARD_TEXT
        self.viewer._send_control = mock.Mock()
        with mock.patch.object(self.viewer.clipboard_auto_stop, "wait", side_effect=[False, False, True]):
            self.viewer._auto_clipboard_loop()
        self.viewer._send_control.assert_not_called()
        self.assertEqual(1, self.viewer.events.qsize())
        self.assertIn("不支持自动文字同步", self.viewer.events.get()[1][1])

    def test_auto_push_uses_ack_but_never_pastes_or_spams_status(self):
        self.viewer._send_control = lambda payload: self.viewer._handle_control(wire.encode_clipboard_status(True, "saved"))
        self.assertTrue(self.viewer._push_auto_clipboard(local("中文😀"), 0))
        self.assertFalse(self.viewer.pending_inputs)
        self.assertTrue(self.viewer.events.empty())

    def test_auto_push_does_not_take_a_manual_slot_or_stale_epoch(self):
        self.viewer._send_control = mock.Mock()
        self.viewer.clipboard_manual_epoch = 1
        self.assertFalse(self.viewer._push_auto_clipboard(local("text"), 0))
        self.viewer.clipboard_pending = app.ViewerClipboardRequest(True, "manual")
        self.assertFalse(self.viewer._push_auto_clipboard(local("text"), 1))
        self.viewer._send_control.assert_not_called()

    def test_busy_auto_set_queues_one_bounded_manual_paste_without_blocking(self):
        pending = app.ViewerClipboardRequest(False, "auto", automatic=True)
        self.viewer.clipboard_pending = pending
        self.viewer.remote_capabilities |= wire.CAPABILITY_INPUT_CONTROL
        writes = []
        def send(payload):
            writes.append(wire.decode_control(payload))
            self.viewer._handle_control(wire.encode_clipboard_status(True, "saved"))
        self.viewer._send_control = send
        before = time.monotonic()
        self.assertTrue(self.viewer.request_clipboard(read=False, text="manual", paste=True))
        self.assertLess(time.monotonic() - before, .1)
        self.assertFalse(self.viewer.request_clipboard(read=False, text="duplicate", paste=True))
        self.assertFalse(writes)
        with self.viewer.clipboard_lock:
            self.viewer.clipboard_pending = None
        deadline = time.monotonic() + 2
        while self.viewer.clipboard_manual_waiting and time.monotonic() < deadline:
            time.sleep(.01)
        self.assertFalse(self.viewer.clipboard_manual_waiting)
        self.assertEqual(["manual"], [value["text"] for value in writes])
        self.assertEqual(4, len(self.viewer.pending_inputs))

    def test_copy_while_ui_apply_is_queued_is_rechecked_in_worker(self):
        self.viewer._read_auto_local_clipboard = mock.Mock(side_effect=[
            local("old"), local("old"), local("old"), local("old"), local("new local", "new")])
        self.viewer._request_clipboard_snapshot = mock.Mock(side_effect=[remote("remote"), remote("new remote")])
        self.viewer._push_auto_clipboard = mock.Mock()
        applications = []
        def event(kind, value):
            if kind == "viewer_auto_clipboard":
                applications.append(value)
                value.ready.set()  # UI reaches the queued event after a copy.
        self.viewer._put_event = event
        with mock.patch.object(self.viewer.clipboard_auto_stop, "wait", side_effect=[False, False, True]):
            self.viewer._auto_clipboard_loop()
        self.assertEqual(1, len(applications))
        self.assertFalse(applications[0].verified)
        self.assertFalse(applications[0].applied)
        self.viewer._push_auto_clipboard.assert_not_called()

    def test_repeated_busy_failures_have_capped_backoff(self):
        self.viewer._read_auto_local_clipboard = mock.Mock(return_value=None)
        with mock.patch.object(self.viewer.clipboard_auto_stop, "wait", side_effect=[False] * 9 + [True]) as wait:
            self.viewer._auto_clipboard_loop()
        delays = [call.args[0] for call in wait.call_args_list]
        self.assertEqual([.75, 1.5, 3, 5], delays[:4])
        self.assertLessEqual(max(delays), 5)


class ClipboardAutoUiFenceTests(unittest.TestCase):
    def setUp(self):
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.root = mock.Mock()
        self.viewer = self.ui.viewer = app.ViewerConnection("127.0.0.1", 1, "test", queue.Queue(), 1)
        self.apply = app.ViewerAutoClipboardApply("remote", local("before", "1", 10), 0, time.monotonic() + 2)
        self.viewer.clipboard_auto_apply = self.apply

    def test_first_phase_only_wakes_worker_and_never_reads_external_clipboard(self):
        self.ui._apply_viewer_auto_clipboard(self.apply)
        self.assertTrue(self.apply.ready.is_set())
        self.ui.root.clipboard_get.assert_not_called()
        self.ui.root.clipboard_clear.assert_not_called()

    def test_final_owner_change_between_worker_and_tk_prevents_overwrite(self):
        self.apply.verified = True
        with mock.patch.object(app, "clipboard_x11_owner", return_value=11):
            self.ui._apply_viewer_auto_clipboard(self.apply)
        self.assertTrue(self.apply.completed.is_set())
        self.assertFalse(self.apply.applied)
        self.ui.root.clipboard_clear.assert_not_called()

    def test_current_owner_uses_tk_write_only(self):
        self.apply.verified = True
        with mock.patch.object(app, "clipboard_x11_owner", return_value=10):
            self.ui._apply_viewer_auto_clipboard(self.apply)
        self.assertTrue(self.apply.applied)
        self.ui.root.clipboard_append.assert_called_once_with("remote")
        self.ui.root.clipboard_get.assert_not_called()

    def test_manual_epoch_disconnect_expiration_and_replaced_operation_are_fenced(self):
        for case in ("manual", "disconnect", "expired", "replaced"):
            with self.subTest(case=case):
                self.setUp()
                self.apply.verified = True
                if case == "manual": self.viewer.clipboard_manual_epoch += 1
                elif case == "disconnect": self.viewer.stop_auto_clipboard()
                elif case == "expired": self.apply.deadline = 0
                else: self.viewer.clipboard_auto_apply = None
                self.ui._apply_viewer_auto_clipboard(self.apply)
                self.assertFalse(self.apply.applied)
                self.ui.root.clipboard_clear.assert_not_called()


class ClipboardAutoLocalReaderTests(unittest.TestCase):
    def setUp(self):
        self.viewer = app.ViewerConnection("127.0.0.1", 1, "test", queue.Queue(), 1)

    def test_x11_reader_preserves_unicode_and_real_ownership_metadata(self):
        text = "中文😀\r\n"
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "", "DISPLAY": ":owned"}), \
                mock.patch.object(app.shutil, "which", return_value="xclip"), \
                mock.patch.object(app, "clipboard_x11_owner", return_value=15), \
                mock.patch.object(host, "_read_clipboard_command_bounded", side_effect=[
                    b"TARGETS\nTIMESTAMP\nUTF8_STRING\n", b"12345", text.encode()]) as read:
            result = self.viewer._read_auto_local_clipboard()
        self.assertEqual(text, result.text)
        self.assertEqual((15, b"12345".hex()), (result.owner, result.ownership))
        self.assertFalse(result.can_replace, "No XFixes monitor: keep auto receive fail-closed")
        self.assertEqual(3, read.call_count)
        self.assertTrue(all(call.args[3] <= 1024 * 1024 for call in read.call_args_list))

    def test_file_clipboard_is_not_uploaded_as_text_or_replaced(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(app.shutil, "which", return_value="xclip"), \
                mock.patch.object(app, "clipboard_x11_owner", return_value=15), \
                mock.patch.object(host, "_read_clipboard_command_bounded", return_value=b"text/uri-list\nUTF8_STRING") as read:
            result = self.viewer._read_auto_local_clipboard()
        self.assertEqual("", result.text)
        self.assertFalse(result.can_replace)
        self.assertEqual(1, read.call_count)

    def test_owner_change_during_read_is_discarded(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(app.shutil, "which", return_value="xclip"), \
                mock.patch.object(app, "clipboard_x11_owner", side_effect=[15, 16]), \
                mock.patch.object(host, "_read_clipboard_command_bounded", side_effect=[b"UTF8_STRING", b"old"]):
            self.assertIsNone(self.viewer._read_auto_local_clipboard())

    def test_file_transition_invalidates_an_empty_text_apply_baseline(self):
        before = local("", owner=15, can_replace=True)
        after = local("", owner=15, can_replace=False)
        self.assertFalse(after.same_as(before))

    def test_advertised_timestamp_failure_is_not_silently_ignored(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(app.shutil, "which", return_value="xclip"), \
                mock.patch.object(app, "clipboard_x11_owner", return_value=15), \
                mock.patch.object(host, "_read_clipboard_command_bounded", side_effect=[b"UTF8_STRING\nTIMESTAMP", None]):
            self.assertIsNone(self.viewer._read_auto_local_clipboard())

    def test_wayland_files_and_images_are_not_treated_as_empty_text(self):
        for formats in (b"text/uri-list\ntext/plain", b"image/png"):
            with self.subTest(formats=formats), \
                    mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "owned"}), \
                    mock.patch.object(app.shutil, "which", return_value="wl-paste"), \
                    mock.patch.object(host, "_read_clipboard_command_bounded", return_value=formats) as read:
                result = self.viewer._read_auto_local_clipboard()
                self.assertFalse(result.can_replace)
                self.assertEqual("", result.text)
                self.assertEqual(1, read.call_count)

    def test_wayland_text_is_bounded_and_keeps_unicode(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "owned"}), \
                mock.patch.object(app.shutil, "which", return_value="wl-paste"), \
                mock.patch.object(host, "_read_clipboard_command_bounded", side_effect=[b"text/plain;charset=utf-8", "中文😀".encode()]):
            result = self.viewer._read_auto_local_clipboard()
            self.assertEqual("中文😀", result.text)
            self.assertIsNone(result.owner)
            self.assertFalse(result.can_replace, "Wayland has no portable final ownership fence")

    def test_x11_no_owner_is_an_empty_baseline_not_a_failed_read(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(app.shutil, "which", return_value="xclip"), \
                mock.patch.object(app, "clipboard_x11_owner", return_value=0):
            self.assertEqual("", self.viewer._read_auto_local_clipboard().text)


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires owned Xvfb")
class ClipboardAutoRealX11Tests(unittest.TestCase):
    def test_stopping_session_synchronously_closes_owned_metadata_display(self):
        viewer = app.ViewerConnection("127.0.0.1", 1, "test", queue.Queue(), 1)
        viewer.clipboard_changes = app.X11ClipboardChanges()
        self.addCleanup(viewer.clipboard_changes.close)
        self.assertIsNotNone(viewer.clipboard_changes.display)
        viewer.stop_auto_clipboard()
        self.assertIsNone(viewer.clipboard_changes.display)
        self.assertTrue(viewer.clipboard_auto_stop.is_set())

    def test_xfixes_catches_same_owner_same_value_reassertion_and_closes_its_display(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        changes = app.X11ClipboardChanges()
        self.addCleanup(changes.close)
        self.assertIsNotNone(changes.display, "Owned Xvfb should provide XFixes")
        root.clipboard_clear()
        root.clipboard_append("Same owned text")
        root.update()
        before = changes.snapshot()
        self.assertIsNotNone(before)
        # Use Tk's own Display/client to relinquish/reassert. Setting ownership
        # for a Tk window from another Display would change the serving client.
        root.selection_clear(selection="CLIPBOARD")
        root.clipboard_clear()
        root.clipboard_append("Same owned text")
        root.update()
        after = changes.snapshot()
        self.assertEqual(before[0], after[0])
        self.assertNotEqual(before[1], after[1])
        self.assertEqual(after, changes.snapshot())
        changes.close()
        self.assertIsNone(changes.display)
        self.assertIsNone(changes.snapshot())

    def test_tk_same_value_without_selection_signal_is_explicitly_unobservable(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        changes = app.X11ClipboardChanges()
        self.addCleanup(changes.close)
        root.clipboard_clear()
        root.clipboard_append("unchanged owned text")
        root.update()
        before = changes.snapshot()
        root.clipboard_clear()
        root.clipboard_append("unchanged owned text")
        root.update()
        # This is an observed limit, not a synchronization pass: Tk can keep
        # both owner and TIMESTAMP unchanged, and produce no XFixes event.
        self.assertEqual(before, changes.snapshot())
        self.assertEqual("unchanged owned text", root.clipboard_get())

    def test_bounded_reader_reads_an_actual_tk_selection_on_worker(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        root.clipboard_clear()
        root.clipboard_append("Owned 中文😀\r\ntext")
        root.update()
        viewer = app.ViewerConnection("127.0.0.1", 1, "test", queue.Queue(), 1)
        results, errors = [], []
        def read():
            try:
                results.append(viewer._read_auto_local_clipboard())
            except Exception as error:
                errors.append(repr(error))
        thread = threading.Thread(target=read)
        thread.start()
        deadline = time.monotonic() + 5
        while thread.is_alive() and time.monotonic() < deadline:
            root.update()
            time.sleep(.005)
        thread.join(.1)
        self.assertFalse(thread.is_alive())
        self.assertEqual([], errors)
        self.assertEqual(1, len(results))
        self.assertIsNotNone(results[0])
        self.assertEqual("Owned 中文😀\r\ntext", results[0].text)
        self.assertEqual(app.clipboard_x11_owner(), results[0].owner)

    def test_real_xfixes_guard_rejects_same_owner_copy_after_worker_verified(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        changes = app.X11ClipboardChanges()
        self.addCleanup(changes.close)
        root.clipboard_clear()
        root.clipboard_append("before")
        root.update()
        owner, token = changes.snapshot()
        viewer = app.ViewerConnection("127.0.0.1", 1, "test", queue.Queue(), 1)
        viewer.clipboard_changes = changes
        ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui.root, ui.viewer = root, viewer
        apply = app.ViewerAutoClipboardApply("stale remote", local("before", token, owner), 0, time.monotonic() + 2)
        apply.verified = True
        viewer.clipboard_auto_apply = apply
        root.selection_clear(selection="CLIPBOARD")
        root.clipboard_clear()
        root.clipboard_append("new user copy")
        root.update()
        ui._apply_viewer_auto_clipboard(apply)
        self.assertFalse(apply.applied)
        self.assertEqual("new user copy", root.clipboard_get())


if __name__ == "__main__":
    unittest.main()
