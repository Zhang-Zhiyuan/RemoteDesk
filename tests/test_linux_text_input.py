import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host


class NativeTextFallbackTests(unittest.TestCase):
    def controller(self, *, verified=True, helper="/usr/bin/xdotool"):
        controller = host.LinuxInputController.__new__(host.LinuxInputController)
        controller.available = verified
        controller.unavailable_reason = "test display not verified"
        controller.native = mock.Mock(available=verified)
        controller.native.apply.return_value = False
        controller.xdotool = helper
        controller.next_text_helper_check = 0
        controller.xdotool_available = False
        return controller

    def command(self, text):
        return host.InputCommand(host.INPUT_TEXT, 0, 0, 0, ord(text))

    def test_verified_native_session_uses_text_helper_without_selecting_mouse_fallback(self):
        for text in ("a", "中", "😀", "-", "'"):
            with self.subTest(text=text):
                controller = self.controller()
                with mock.patch.object(controller, "_run") as run:
                    self.assertTrue(controller.apply(self.command(text), (1920, 1080), (1920, 1080)))
                delay = "50" if ord(text) > 0x7F else "12"
                run.assert_called_once_with(["type", "--clearmodifiers", "--delay", delay, "--", text], timeout=.8)

    def test_unicode_mapping_settles_without_slowing_ascii_or_control_keys(self):
        for text in ("中", "试", "😀", "é"):
            self.assertEqual("50", host.text_codepoint_to_xdotool_commands(ord(text))[0][3])
        for text in ("A", "0", " ", "-"):
            self.assertEqual("12", host.text_codepoint_to_xdotool_commands(ord(text))[0][3])
        self.assertEqual([["key", "Tab"]], host.text_codepoint_to_xdotool_commands(9))
        self.assertEqual([["key", "Return"]], host.text_codepoint_to_xdotool_commands(10))

    def test_missing_helper_gives_actionable_error(self):
        controller = self.controller(helper=None)
        with mock.patch.object(host.shutil, "which", return_value=None), self.assertRaisesRegex(host.ProtocolError, "xdotool.*安装"):
            controller.apply(self.command("a"), (1, 1), (1, 1))

    def test_installing_helper_restores_text_without_reconnecting(self):
        controller = self.controller(helper=None)
        with mock.patch.object(host.shutil, "which", side_effect=(None, "/usr/bin/xdotool")) as lookup, \
             mock.patch.object(host.time, "monotonic") as clock, mock.patch.object(controller, "_run") as run:
            clock.return_value = 10.
            for _ in range(2):
                with self.assertRaises(host.ProtocolError):
                    controller.apply(self.command("中"), (1, 1), (1, 1))
            lookup.assert_called_once_with("xdotool")
            clock.return_value = 11.
            self.assertTrue(controller.apply(self.command("中"), (1, 1), (1, 1)))
            self.assertTrue(controller.apply(self.command("文"), (1, 1), (1, 1)))
            self.assertEqual(2, lookup.call_count)
            self.assertEqual(2, run.call_count)

    def test_unverified_session_cannot_inject_text(self):
        controller = self.controller(verified=False)
        with mock.patch.object(controller, "_run") as run, self.assertRaises(host.ProtocolError):
            controller.apply(self.command("a"), (1, 1), (1, 1))
        run.assert_not_called()

    def test_native_handled_control_char_does_not_spawn_helper(self):
        controller = self.controller()
        controller.native.apply.return_value = True
        with mock.patch.object(controller, "_run") as run:
            self.assertTrue(controller.apply(self.command("\n"), (1, 1), (1, 1)))
        run.assert_not_called()

    def test_invalid_unicode_does_not_spawn_helper(self):
        controller = self.controller()
        with mock.patch.object(controller, "_run") as run:
            self.assertFalse(controller.apply(self.command("\ud800"), (1, 1), (1, 1)))
        run.assert_not_called()

    def test_failed_text_diagnostic_never_echoes_private_text(self):
        controller = self.controller()
        with mock.patch.object(host.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)), \
             self.assertRaises(host.ProtocolError) as failure:
            controller._run(["type", "--", "private-test-text"], .8)
        self.assertNotIn("private-test-text", str(failure.exception))
        self.assertIn("type failed", str(failure.exception))

    def test_timed_out_text_diagnostic_never_echoes_private_text(self):
        controller = self.controller()
        arguments = ["type", "--", "private-test-text"]
        with mock.patch.object(host.subprocess, "run", side_effect=subprocess.TimeoutExpired(arguments, .8)), \
             self.assertRaises(host.ProtocolError) as failure:
            controller._run(arguments, .8)
        self.assertNotIn("private-test-text", str(failure.exception))
        self.assertIn("type timed out", str(failure.exception))


if __name__ == "__main__": unittest.main()
