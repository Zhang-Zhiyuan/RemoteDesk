from __future__ import annotations

import os
import ctypes
from pathlib import Path
import sys
import tkinter as tk
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app
import remotedesk_linux_host as host


class LinuxKeyboardMappingTests(unittest.TestCase):
    MODIFIERS = (
        (0x10, 0xA0, 0x2A, 1), (0x10, 0xA1, 0x36, 1),
        (0x11, 0xA2, 0x1D, 1), (0x11, 0xA3, 0x1D, 3),
        (0x12, 0xA4, 0x38, 1), (0x12, 0xA5, 0x38, 3),
    )

    def test_mixed_generic_and_specific_modifier_release_matches_both_directions(self) -> None:
        for generic, specific, scan, flags in self.MODIFIERS:
            for down_vk, up_vk in ((generic, specific), (specific, generic)):
                with self.subTest(down=down_vk, up=up_vk):
                    state = host.HostPressedInputState()
                    state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, flags, down_vk))
                    state.observe(host.InputCommand(host.INPUT_KEY_UP, 0, scan, flags, up_vk))
                    self.assertEqual(0, state.pressed_key_count)
                    self.assertEqual((), state.take_release_commands())

    def test_mixed_repeat_is_one_key_and_disconnect_keeps_original_down_metadata(self) -> None:
        for generic, specific, scan, flags in self.MODIFIERS:
            with self.subTest(specific=specific):
                state = host.HostPressedInputState()
                state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, flags, generic))
                state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, flags, specific))
                self.assertEqual(1, state.pressed_key_count)
                self.assertEqual((host.InputCommand(host.INPUT_KEY_UP, 0, scan, flags, generic),),
                                 state.take_release_commands())

    def test_unmatched_other_side_release_never_removes_held_modifier(self) -> None:
        for generic, left_scan, right_scan in ((0x10, 0x2A, 0x36), (0x11, 0x1D, 0x1D),
                                              (0x12, 0x38, 0x38)):
            with self.subTest(generic=generic):
                state = host.HostPressedInputState()
                state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, left_scan, 1, generic))
                right_flags = 1 if generic == 0x10 else 3
                state.observe(host.InputCommand(host.INPUT_KEY_UP, 0, right_scan, right_flags, generic))
                self.assertEqual((host.InputCommand(host.INPUT_KEY_UP, 0, left_scan, 1, generic),),
                                 state.take_release_commands())

    def test_explicit_side_without_scan_matches_scanned_modifier(self) -> None:
        for generic, specific, scan, flags in self.MODIFIERS:
            for scanned_down in (True, False):
                with self.subTest(specific=specific, scanned_down=scanned_down):
                    scanned = (scan, flags, generic)
                    unscanned = (0, 0, specific)
                    down, up = (scanned, unscanned) if scanned_down else (unscanned, scanned)
                    state = host.HostPressedInputState()
                    state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, *down))
                    state.observe(host.InputCommand(host.INPUT_KEY_UP, 0, *up))
                    self.assertEqual(0, state.pressed_key_count)

    def test_ambiguous_generic_release_without_scan_cannot_release_right_modifier(self) -> None:
        for generic, specific, scan, flags in self.MODIFIERS[1::2]:
            with self.subTest(specific=specific):
                state = host.HostPressedInputState()
                state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, flags, specific))
                state.observe(host.InputCommand(host.INPUT_KEY_UP, 0, 0, 0, generic))
                self.assertEqual((host.InputCommand(host.INPUT_KEY_UP, 0, scan, flags, specific),),
                                 state.take_release_commands())

    def test_shift_tab_alias_maps_to_the_same_portable_key(self) -> None:
        for symbol in ("Tab", "ISO_Left_Tab"):
            with self.subTest(symbol=symbol):
                self.assertEqual(
                    0x09,
                    app.tk_event_to_windows_virtual_key(SimpleNamespace(keysym=symbol)),
                )

    def test_release_state_keeps_both_sides_independent(self) -> None:
        for left, right in ((0xA0, 0xA1), (0xA2, 0xA3), (0xA4, 0xA5)):
            with self.subTest(left=left, right=right):
                state = app.ViewerPressedKeyState()
                state.observe_down(left)
                state.observe_down(right)
                state.observe_up(right)
                self.assertEqual((left,), state.keys_in_release_order())
                state.observe_up(left)
                self.assertEqual(0, state.count)

    def test_legacy_right_shift_extended_bit_does_not_change_side(self) -> None:
        for virtual_key in (0x10, 0xA1):
            with self.subTest(virtual_key=virtual_key):
                wrong = host.InputCommand(host.INPUT_KEY_DOWN, 0, 0x36, 3, virtual_key)
                correct = host.InputCommand(host.INPUT_KEY_UP, 0, 0x36, 1, virtual_key)
                self.assertEqual("Shift_R", host.input_command_key_name(wrong))
                self.assertEqual(host.input_key_identity(correct), host.input_key_identity(wrong))

    def test_legacy_shift_fix_preserves_ctrl_alt_and_unrelated_scan_metadata(self) -> None:
        for vk, scan, expected in ((0x11, 0x1D, "Control_R"), (0x12, 0x38, "Alt_R")):
            command = host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, 3, vk)
            self.assertEqual(expected, host.input_command_key_name(command))
            self.assertEqual(3, host.input_keyboard_flags(command))
        unrelated = host.InputCommand(host.INPUT_KEY_DOWN, 0, 0x36, 3, 0x41)
        self.assertEqual("a", host.input_command_key_name(unrelated))
        self.assertEqual((0x41, 0x36, 3), host.input_key_identity(unrelated))

    def test_legacy_shift_release_does_not_remove_other_held_shift(self) -> None:
        state = host.HostPressedInputState()
        state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, 0x36, 3, 0x10))
        state.observe(host.InputCommand(host.INPUT_KEY_DOWN, 0, 0x2A, 1, 0x10))
        state.observe(host.InputCommand(host.INPUT_KEY_UP, 0, 0x36, 1, 0x10))
        releases = state.take_release_commands()
        self.assertEqual(1, len(releases))
        self.assertEqual(host.InputCommand(host.INPUT_KEY_UP, 0, 0x2A, 1, 0x10), releases[0])
        self.assertEqual("Shift_L", host.input_command_key_name(releases[0]))


@unittest.skipUnless(
    os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1"
    and os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1",
    "requires an explicitly owned Xvfb; never inject into a user desktop",
)
class LinuxKeyboardX11Tests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = tk.Tk()
        self.addCleanup(self.root.destroy)
        self.root.geometry("640x320")
        self.label = tk.Label(self.root, text="Owned RemoteDesk keyboard test", takefocus=True)
        self.label.pack(fill="both", expand=True)
        self.root.update()
        self.label.focus_force()
        self.root.update()
        self.native = host.NativeX11InputController(verify_pointer_motion=False)
        self.addCleanup(self.native.close)
        self.assertTrue(self.native.available, self.native.error)
        self.controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.controller.viewer_pressed_keys = app.ViewerPressedKeyState()
        self.controller.viewer_clipboard = mock.Mock()
        self.controller.viewer_clipboard_paste_key = False
        self.commands: list[host.InputCommand] = []
        self.controller.viewer = SimpleNamespace(
            remote_capabilities=app.CAPABILITY_INPUT_CONTROL,
            send_input=self._send,
            flush_pending_inputs=mock.Mock(),
        )
        self.events: list[tuple[str, str, int]] = []

        def key_event(event: tk.Event, pressed: bool) -> str:
            self.events.append(("down" if pressed else "up", event.keysym, event.state))
            return (
                self.controller._viewer_key_press(event)
                if pressed else self.controller._viewer_key_release(event)
            )

        self.label.bind("<KeyPress>", lambda event: key_event(event, True))
        self.label.bind("<KeyRelease>", lambda event: key_event(event, False))

    def _send(self, kind: int, **fields: int) -> bool:
        self.commands.append(host.decode_input_payload(app.encode_input(kind, **fields)))
        return True

    def inject(self, kind: int, virtual_key: int, scan: int = 0, flags: int = 0) -> None:
        command = host.InputCommand(kind, 0, scan, flags, virtual_key)
        self.assertTrue(self.native.apply(command, (640, 320), (640, 320)))
        self.native.x11.XSync(self.native.display, 0)
        self.root.update()

    def test_physical_left_right_modifiers_roundtrip_x11_and_viewer_wire(self) -> None:
        cases = (
            (0xA0, "Shift_L", 0x10, 0x2A, 1),
            (0xA1, "Shift_R", 0x10, 0x36, 1),
            (0xA2, "Control_L", 0x11, 0x1D, 1),
            (0xA3, "Control_R", 0x11, 0x1D, 3),
            (0xA4, "Alt_L", 0x12, 0x38, 1),
            (0xA5, "Alt_R", 0x12, 0x38, 3),
        )
        for side_vk, symbol, generic_vk, scan, flags in cases:
            for wire_vk, wire_scan, wire_flags in ((side_vk, 0, 0), (generic_vk, scan, flags)):
                with self.subTest(symbol=symbol, scan=wire_scan):
                    self.events.clear()
                    self.commands.clear()
                    self.inject(host.INPUT_KEY_DOWN, wire_vk, wire_scan, wire_flags)
                    self.inject(host.INPUT_KEY_UP, wire_vk, wire_scan, wire_flags)
                    self.assertEqual([("down", symbol), ("up", symbol)],
                                     [(kind, name) for kind, name, _ in self.events])
                    self.assertEqual([(host.INPUT_KEY_DOWN, side_vk), (host.INPUT_KEY_UP, side_vk)],
                                     [(command.kind, command.data) for command in self.commands])
                    self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_each_shift_produces_uppercase_and_releases_cleanly(self) -> None:
        for shift in (0xA0, 0xA1):
            with self.subTest(shift=shift):
                self.events.clear()
                self.inject(host.INPUT_KEY_DOWN, shift)
                self.inject(host.INPUT_KEY_DOWN, 0x41)
                self.inject(host.INPUT_KEY_UP, 0x41)
                self.inject(host.INPUT_KEY_UP, shift)
                self.assertIn(("down", "A", 1), self.events)
                self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_legacy_extended_right_shift_still_injects_real_right_side(self) -> None:
        for virtual_key in (0x10, 0xA1):
            with self.subTest(virtual_key=virtual_key):
                self.events.clear()
                self.commands.clear()
                self.inject(host.INPUT_KEY_DOWN, virtual_key, 0x36, 3)
                self.inject(host.INPUT_KEY_UP, virtual_key, 0x36, 3)
                self.assertEqual([("down", "Shift_R"), ("up", "Shift_R")],
                                 [(kind, name) for kind, name, _ in self.events])
                self.assertEqual([(host.INPUT_KEY_DOWN, 0xA1), (host.INPUT_KEY_UP, 0xA1)],
                                 [(command.kind, command.data) for command in self.commands])

    def test_mixed_modifier_key_up_releases_real_x11_key_and_host_tracking(self) -> None:
        keymap = (ctypes.c_ubyte * 32)()
        self.native.x11.XQueryKeymap.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        self.native.x11.XQueryKeymap.restype = ctypes.c_int
        for generic, specific, scan, flags in LinuxKeyboardMappingTests.MODIFIERS:
            for down_vk, up_vk in ((generic, specific), (specific, generic)):
                with self.subTest(down=down_vk, up=up_vk):
                    state = host.HostPressedInputState()
                    down = host.InputCommand(host.INPUT_KEY_DOWN, 0, scan, flags, down_vk)
                    up = host.InputCommand(host.INPUT_KEY_UP, 0, scan, flags, up_vk)
                    keycode = self.native._keycode(host.input_command_key_name(down))
                    try:
                        self.inject(down.kind, down.data, down.x, down.y)
                        state.observe(down)
                        self.native.x11.XQueryKeymap(self.native.display, keymap)
                        self.assertTrue(keymap[keycode // 8] & (1 << (keycode % 8)))
                    finally:
                        self.inject(up.kind, up.data, up.x, up.y)
                        state.observe(up)
                    self.native.x11.XQueryKeymap(self.native.display, keymap)
                    self.assertFalse(keymap[keycode // 8] & (1 << (keycode % 8)))
                    self.assertEqual(0, state.pressed_key_count)
                    self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_shift_tab_reaches_viewer_on_both_sides(self) -> None:
        for shift in (0xA0, 0xA1):
            with self.subTest(shift=shift):
                self.events.clear()
                self.commands.clear()
                self.inject(host.INPUT_KEY_DOWN, shift)
                self.inject(host.INPUT_KEY_DOWN, 0x09)
                self.inject(host.INPUT_KEY_UP, 0x09)
                self.inject(host.INPUT_KEY_UP, shift)
                self.assertIn("ISO_Left_Tab", [symbol for _, symbol, _ in self.events])
                self.assertEqual(
                    [(host.INPUT_KEY_DOWN, shift), (host.INPUT_KEY_DOWN, 0x09),
                     (host.INPUT_KEY_UP, 0x09), (host.INPUT_KEY_UP, shift)],
                    [(command.kind, command.data) for command in self.commands],
                )
                self.assertEqual(0, self.controller.viewer_pressed_keys.count)

    def test_focus_loss_releases_both_sides_without_merging(self) -> None:
        for left, right in ((0xA0, 0xA1), (0xA2, 0xA3), (0xA4, 0xA5)):
            with self.subTest(left=left, right=right):
                try:
                    self.inject(host.INPUT_KEY_DOWN, left)
                    self.inject(host.INPUT_KEY_DOWN, right)
                    self.assertEqual(2, self.controller.viewer_pressed_keys.count)
                    self.commands.clear()
                    self.controller._viewer_frame_focus_out(SimpleNamespace())
                    self.assertEqual([(host.INPUT_KEY_UP, right), (host.INPUT_KEY_UP, left)],
                                     [(command.kind, command.data) for command in self.commands])
                    self.assertEqual(0, self.controller.viewer_pressed_keys.count)
                finally:
                    self.inject(host.INPUT_KEY_UP, right)
                    self.inject(host.INPUT_KEY_UP, left)


if __name__ == "__main__":
    unittest.main()
