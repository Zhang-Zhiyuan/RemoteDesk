from __future__ import annotations

import os
import queue
import threading
import time
import tkinter as tk
from types import MethodType
import unittest
from unittest import mock

import test_linux_keyboard as keyboard

app, host = keyboard.app, keyboard.host


@unittest.skipUnless(
    os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1"
    and os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1",
    "requires an explicitly owned Xvfb",
)
class LinuxShortcutX11Tests(unittest.TestCase):
    setUp = keyboard.LinuxKeyboardX11Tests.setUp
    inject = keyboard.LinuxKeyboardX11Tests.inject
    _send = keyboard.LinuxKeyboardX11Tests._send

    def _real_clipboard_worker(self) -> app.ViewerConnection:
        viewer = app.ViewerConnection("127.0.0.1", 1, "owned fixture", queue.Queue(), 1)
        viewer.sock, viewer.session = mock.Mock(), object()
        viewer.remote_capabilities = app.CAPABILITY_INPUT_CONTROL | app.CAPABILITY_CLIPBOARD_TEXT
        viewer.flush_pending_inputs = mock.Mock(return_value=True)
        self.addCleanup(viewer._interrupt_transport)
        self.controller.viewer = viewer
        self.controller.viewer_clipboard = MethodType(app.RemoteDeskLinuxApp.viewer_clipboard, self.controller)
        self.controller._local_clipboard_text = mock.Mock(return_value="Owned synthetic text")
        self.label.bind("<FocusOut>", self.controller._viewer_frame_focus_out)
        return viewer

    def _wait_clipboard_idle(self, viewer: app.ViewerConnection) -> None:
        deadline = time.monotonic() + 2
        while viewer.clipboard_pending is not None and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.005)
        self.assertIsNone(viewer.clipboard_pending)

    def test_delayed_paste_ack_after_real_focus_loss_never_injects_late_chord(self) -> None:
        viewer = self._real_clipboard_worker()
        control_sent = threading.Event()
        viewer._send_control = lambda _payload: control_sent.set()
        outside = tk.Entry(self.root)
        outside.pack()
        self.root.update()
        self.inject(host.INPUT_KEY_DOWN, 0xA2)
        viewer.pending_inputs.clear()  # model the already sent Ctrl down
        self.inject(host.INPUT_KEY_DOWN, 0x56)
        self.assertTrue(control_sent.wait(1))
        request = viewer.clipboard_pending
        self.assertIsNotNone(request)
        outside.focus_force()  # actual X11 focus transition, not direct cancel invocation
        self.root.update()
        self.inject(host.INPUT_KEY_UP, 0x56)
        self.inject(host.INPUT_KEY_UP, 0xA2)
        request.success = True
        request.completed.set()
        self._wait_clipboard_idle(viewer)
        self.assertEqual([(6, 0xA2)], [(command.kind, command.data)
                         for _, payload in viewer.pending_inputs
                         for command in (host.decode_input_payload(payload),)])

    def test_explicit_toolbar_paste_after_focus_loss_does_not_need_surface_focus(self) -> None:
        viewer = self._real_clipboard_worker()
        invoked = threading.Event()
        def ack(_payload: bytes) -> None:
            request = viewer.clipboard_pending
            request.success = True
            request.completed.set()
            invoked.set()
        viewer._send_control = ack
        toolbar = tk.Button(self.root, text="Owned paste button",
                            command=lambda: self.controller.viewer_clipboard(paste=True))
        toolbar.pack()
        self.root.update()
        toolbar.focus_force()
        self.root.update()
        self.assertIs(self.root.focus_get(), toolbar)
        toolbar.invoke()
        self.assertTrue(invoked.wait(1))
        self._wait_clipboard_idle(viewer)
        self.assertEqual([(5, 0x11), (5, 0x56), (6, 0x56), (6, 0x11)],
                         [(command.kind, command.data) for _, payload in viewer.pending_inputs
                          for command in (host.decode_input_payload(payload),)])

    def test_ctrl_alt_and_meta_shortcuts_are_not_clipboard_shortcuts(self) -> None:
        for extra in (0xA4, 0xA5, 0x5B, 0x5C):
            for key in (0x56, 0x43, 0x58):
                with self.subTest(extra=extra, key=key):
                    self.commands.clear()
                    self.controller.viewer_clipboard.reset_mock()
                    try:
                        self.inject(host.INPUT_KEY_DOWN, 0xA2)
                        self.inject(host.INPUT_KEY_DOWN, extra)
                        self.inject(host.INPUT_KEY_DOWN, key)
                        self.inject(host.INPUT_KEY_UP, key)
                    finally:
                        self.inject(host.INPUT_KEY_UP, extra)
                        self.inject(host.INPUT_KEY_UP, 0xA2)
                    self.controller.viewer_clipboard.assert_not_called()
                    self.assertIn((host.INPUT_KEY_DOWN, key), [(c.kind, c.data) for c in self.commands])
                    self.assertIn((host.INPUT_KEY_UP, key), [(c.kind, c.data) for c in self.commands])
                    self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_keyboard_paste_keeps_physically_held_control_for_next_shortcut(self) -> None:
        self.controller.viewer_clipboard = MethodType(app.RemoteDeskLinuxApp.viewer_clipboard, self.controller)
        self.controller._local_clipboard_text = mock.Mock(return_value="Owned synthetic text")
        self.controller.viewer.request_clipboard = mock.Mock(return_value=True)
        try:
            self.inject(host.INPUT_KEY_DOWN, 0xA3)
            self.inject(host.INPUT_KEY_DOWN, 0x56)
            self.inject(host.INPUT_KEY_UP, 0x56)
            self.inject(host.INPUT_KEY_DOWN, 0x43)
            self.inject(host.INPUT_KEY_UP, 0x43)
            self.assertEqual((0xA3,), self.controller.viewer_pressed_keys.keys_in_release_order())
            self.assertNotIn((host.INPUT_KEY_UP, 0xA3), [(c.kind, c.data) for c in self.commands])
        finally:
            self.inject(host.INPUT_KEY_UP, 0xA3)
        self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_focus_loss_during_intercepted_paste_does_not_swallow_next_v_release(self) -> None:
        outside = tk.Entry(self.root)
        outside.pack()
        self.root.update()
        self.inject(host.INPUT_KEY_DOWN, 0xA2)
        self.inject(host.INPUT_KEY_DOWN, 0x56)
        self.controller._viewer_frame_focus_out(None)
        outside.focus_force()
        self.root.update()
        self.inject(host.INPUT_KEY_UP, 0x56)
        self.inject(host.INPUT_KEY_UP, 0xA2)
        self.label.focus_force()
        self.root.update()
        self.commands.clear()
        self.inject(host.INPUT_KEY_DOWN, 0x56)
        self.inject(host.INPUT_KEY_UP, 0x56)
        self.assertEqual([(host.INPUT_KEY_DOWN, 0x56), (host.INPUT_KEY_UP, 0x56)],
                         [(c.kind, c.data) for c in self.commands])
        self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_standard_lock_menu_print_and_pause_keys_are_forwarded(self) -> None:
        for virtual_key in (0x90, 0x91, 0x5D, 0x2C, 0x13):
            with self.subTest(virtual_key=virtual_key):
                self.commands.clear()
                try:
                    self.inject(host.INPUT_KEY_DOWN, virtual_key)
                    self.inject(host.INPUT_KEY_UP, virtual_key)
                    self.assertEqual([(host.INPUT_KEY_DOWN, virtual_key), (host.INPUT_KEY_UP, virtual_key)],
                                     [(c.kind, c.data) for c in self.commands])
                finally:
                    if virtual_key in (0x90, 0x91):
                        self.inject(host.INPUT_KEY_DOWN, virtual_key)
                        self.inject(host.INPUT_KEY_UP, virtual_key)

    def test_keypad_release_survives_numlock_and_shift_keysym_change(self) -> None:
        for keypad in range(0x60, 0x6A):
            for shift in (0, 0xA1):
                with self.subTest(keypad=keypad, shift=shift):
                    self.commands.clear()
                    try:
                        if shift:
                            self.inject(host.INPUT_KEY_DOWN, shift)
                        self.inject(host.INPUT_KEY_DOWN, keypad)
                        self.inject(host.INPUT_KEY_DOWN, 0x90)
                        self.inject(host.INPUT_KEY_UP, 0x90)
                        self.inject(host.INPUT_KEY_UP, keypad)
                        self.assertIn((host.INPUT_KEY_UP, keypad), [(c.kind, c.data) for c in self.commands])
                    finally:
                        self.inject(host.INPUT_KEY_DOWN, 0x90)
                        self.inject(host.INPUT_KEY_UP, 0x90)
                        if shift:
                            self.inject(host.INPUT_KEY_UP, shift)
                    self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_repeated_downs_keep_one_release_and_one_paste_request(self) -> None:
        for virtual_key in (0x41, 0x56):
            with self.subTest(virtual_key=virtual_key):
                self.commands.clear()
                self.controller.viewer_clipboard.reset_mock()
                try:
                    if virtual_key == 0x56:
                        self.inject(host.INPUT_KEY_DOWN, 0xA2)
                        self.inject(host.INPUT_KEY_DOWN, 0xA1)
                    for _ in range(8):
                        self.inject(host.INPUT_KEY_DOWN, virtual_key)
                    if virtual_key == 0x56:
                        self.controller.viewer_clipboard.assert_called_once_with(paste=True, paste_shift=True)
                    else:
                        self.assertEqual(1, self.controller.viewer_pressed_keys.count)
                finally:
                    self.inject(host.INPUT_KEY_UP, virtual_key)
                    if virtual_key == 0x56:
                        self.inject(host.INPUT_KEY_UP, 0xA1)
                        self.inject(host.INPUT_KEY_UP, 0xA2)
                self.assertEqual(0, self.controller.viewer_pressed_keys.count)


class ClipboardPasteQueueTests(unittest.TestCase):
    def setUp(self) -> None:
        self.viewer = app.ViewerConnection("127.0.0.1", 1, "owned fixture", queue.Queue(), 1)
        self.viewer.session, self.viewer.sock = object(), object()
        self.viewer.remote_capabilities = app.CAPABILITY_INPUT_CONTROL | app.CAPABILITY_CLIPBOARD_TEXT
        self.viewer.flush_pending_inputs = mock.Mock(return_value=True)
        self.addCleanup(self.viewer.stop_event.set)

    def paste(self, shift: bool = False, accepted: bool = True) -> list[host.InputCommand]:
        request = app.ViewerClipboardRequest(False, "owned", deadline=time.monotonic() + 5)
        self.viewer.clipboard_pending = request
        def ack(_payload: bytes) -> None:
            request.success = accepted
            request.completed.set()
        self.viewer._send_control = ack
        self.viewer._exchange_clipboard(request, b"synthetic clipboard control", True, False, shift)
        return [host.decode_input_payload(payload) for _, payload in self.viewer.pending_inputs]

    def test_paste_never_releases_control_or_shift_already_held_by_user(self) -> None:
        for control in (0xA2, 0xA3):
            for shift in (0, 0xA0, 0xA1):
                with self.subTest(control=control, shift=shift):
                    self.viewer.send_input(app.INPUT_KEY_DOWN, data=control)
                    if shift:
                        self.viewer.send_input(app.INPUT_KEY_DOWN, data=shift)
                    self.viewer.pending_inputs.clear()  # represent a successfully drained input batch
                    self.assertEqual([(app.INPUT_KEY_DOWN, 0x56), (app.INPUT_KEY_UP, 0x56)],
                                     [(c.kind, c.data) for c in self.paste(bool(shift))])
                    self.viewer.send_input(app.INPUT_KEY_UP, data=control)
                    if shift:
                        self.viewer.send_input(app.INPUT_KEY_UP, data=shift)
                    self.viewer.pending_inputs.clear()

    def test_paste_after_modifier_release_synthesizes_balanced_shortcut(self) -> None:
        self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA3)
        self.viewer.send_input(app.INPUT_KEY_UP, data=0xA3)
        self.viewer.pending_inputs.clear()
        self.assertEqual([(5, 0x11), (5, 0x56), (6, 0x56), (6, 0x11)],
                         [(c.kind, c.data) for c in self.paste()])

    def test_new_alt_meta_or_incompatible_shift_during_ack_cancels_paste_only(self) -> None:
        for modifier in (0xA4, 0xA5, 0x5B, 0x5C, 0xA0, 0xA1):
            with self.subTest(modifier=modifier):
                self.viewer.send_input(app.INPUT_KEY_DOWN, data=modifier)
                self.viewer.pending_inputs.clear()
                self.assertEqual([], self.paste())
                self.viewer.send_input(app.INPUT_KEY_UP, data=modifier)
                self.viewer.pending_inputs.clear()

    def test_failed_ack_does_not_inject_or_release_any_keys(self) -> None:
        self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA2)
        self.viewer.pending_inputs.clear()
        self.assertEqual([], self.paste(accepted=False))

    def test_android_shift_paste_is_not_sent_as_unsupported_chord(self) -> None:
        self.viewer.remote_device_info = {"platform": "Android"}
        self.viewer.remote_capabilities |= app.CAPABILITY_CLIPBOARD_PASTE_SHORTCUT
        self.assertEqual([], self.paste(shift=True))
        self.assertTrue(any("Ctrl+Shift+V" in str(value) for value in self.viewer.events.queue))

    def test_android_plain_paste_remains_supported(self) -> None:
        self.viewer.remote_device_info = {"platform": "Android"}
        self.viewer.remote_capabilities |= app.CAPABILITY_CLIPBOARD_PASTE_SHORTCUT
        self.assertEqual([(5, 0x11), (5, 0x56), (6, 0x56), (6, 0x11)],
                         [(c.kind, c.data) for c in self.paste()])

    def test_cancelled_paste_waiting_behind_automatic_clipboard_keeps_original_revision(self) -> None:
        self.viewer.clipboard_pending = app.ViewerClipboardRequest(False, "old", automatic=True)
        control_sent = threading.Event()
        def ack(_payload: bytes) -> None:
            request = self.viewer.clipboard_pending
            request.success = True
            request.completed.set()
            control_sent.set()
        self.viewer._send_control = ack
        self.assertTrue(self.viewer.request_clipboard(read=False, text="owned", paste=True))
        self.viewer.cancel_pending_clipboard_paste()
        with self.viewer.clipboard_lock:
            self.viewer.clipboard_pending = None
        self.assertTrue(control_sent.wait(1))
        deadline = time.monotonic() + 1
        while (self.viewer.clipboard_manual_waiting or self.viewer.clipboard_pending is not None) and time.monotonic() < deadline:
            time.sleep(.005)
        self.assertFalse(self.viewer.clipboard_manual_waiting)
        self.assertIsNone(self.viewer.clipboard_pending)
        self.assertFalse(self.viewer.pending_inputs)

    def test_cancel_before_worker_start_cannot_capture_new_revision_and_paste(self) -> None:
        captured: dict[str, object] = {}
        def make_thread(*, target, args, **_kwargs):
            captured.update(target=target, args=args)
            return mock.Mock()
        with mock.patch.object(app.threading, "Thread", side_effect=make_thread):
            self.assertTrue(self.viewer.request_clipboard(read=False, text="owned", paste=True))
        self.viewer.cancel_pending_clipboard_paste()
        def ack(_payload: bytes) -> None:
            request = self.viewer.clipboard_pending
            request.success = True
            request.completed.set()
        self.viewer._send_control = ack
        captured["target"](*captured["args"])
        self.assertFalse(self.viewer.pending_inputs)
        self.assertIsNone(self.viewer.clipboard_pending)

    def test_modifier_release_while_waiting_for_ack_stays_before_atomic_paste(self) -> None:
        self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA3)
        self.viewer.pending_inputs.clear()
        request = app.ViewerClipboardRequest(False, "owned", deadline=time.monotonic() + 3)
        self.viewer.clipboard_pending = request
        sent = threading.Event()
        self.viewer._send_control = lambda _payload: sent.set()
        worker = threading.Thread(target=self.viewer._exchange_clipboard,
                                  args=(request, b"owned", True, False, False))
        worker.start()
        try:
            self.assertTrue(sent.wait(1))
            self.assertFalse(self.viewer.pending_inputs)
            self.viewer.send_input(app.INPUT_KEY_UP, data=0xA3)
            request.success = True
            request.completed.set()
            worker.join(1)
            self.assertFalse(worker.is_alive())
            self.assertEqual([(6, 0xA3), (5, 0x11), (5, 0x56), (6, 0x56), (6, 0x11)],
                             [(command.kind, command.data) for _, payload in self.viewer.pending_inputs
                              for command in (host.decode_input_payload(payload),)])
        finally:
            self.viewer.stop_event.set()
            request.completed.set()
            worker.join(1)

    def test_full_queue_does_not_create_phantom_held_key_or_partial_paste(self) -> None:
        with mock.patch.object(app, "INPUT_QUEUE_LIMIT", 1):
            self.assertTrue(self.viewer.send_input(app.INPUT_MOUSE_MOVE, x=1))
            # Reliable input replaces motion; a second reliable down must fail.
            self.assertTrue(self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA3))
            self.assertFalse(self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA4))
            self.assertEqual({0xA3}, self.viewer.input_pressed_keys)
            before = list(self.viewer.pending_inputs)
            self.paste()
            self.assertEqual(before, list(self.viewer.pending_inputs))
            self.assertTrue(self.viewer.send_input(app.INPUT_KEY_UP, data=0xA3))
            self.assertFalse(self.viewer.input_pressed_keys)

    def test_transport_stop_clears_desired_keys_and_rejects_late_input(self) -> None:
        self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA2)
        self.viewer.sock = mock.Mock()
        self.viewer._interrupt_transport()
        self.assertFalse(self.viewer.input_pressed_keys)
        self.assertFalse(self.viewer.pending_inputs)
        self.assertFalse(self.viewer.send_input(app.INPUT_KEY_DOWN, data=0xA2))


class ClipboardShortcutStateTests(unittest.TestCase):
    def test_only_ctrl_and_optional_shift_lock_modifiers_are_intercepted(self) -> None:
        for locks in (0, 2, 16, 18):
            for shift in (0, 1):
                self.assertTrue(app.is_clipboard_shortcut_state(4 | locks | shift))
                for extra in (8, 32, 64, 128):
                    self.assertFalse(app.is_clipboard_shortcut_state(4 | locks | shift | extra))
        self.assertFalse(app.is_clipboard_shortcut_state(0))
        self.assertFalse(app.is_clipboard_shortcut_state(1))


if __name__ == "__main__":
    unittest.main()
