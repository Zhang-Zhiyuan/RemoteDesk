import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "experiments"))
from verify_android_dpi import rotation_command


class AndroidDpiRotationCommandTests(unittest.TestCase):
    def test_current_android_command(self):
        self.assertEqual("user-rotation", rotation_command(
            "Window manager commands:\n  user-rotation [-d DISPLAY_ID] [free|lock] [rotation]\n"))

    def test_legacy_android_command(self):
        self.assertEqual("set-user-rotation", rotation_command(
            "  set-user-rotation [free|lock] [-d DISPLAY_ID] [rotation]\n"))

    def test_current_command_preferred_when_both_advertised(self):
        self.assertEqual("user-rotation", rotation_command(
            "  set-user-rotation [free|lock]\n  user-rotation [free|lock]\n"))

    def test_description_and_other_commands_are_not_capability_evidence(self):
        for help_text in ("", "  fixed-to-user-rotation [enabled|disabled]\n",
                          "Rotate using user-rotation when available.\n",
                          "  user-rotation-unsupported [lock]\n"):
            with self.subTest(help_text=help_text), self.assertRaises(RuntimeError):
                rotation_command(help_text)


if __name__ == "__main__":
    unittest.main()
