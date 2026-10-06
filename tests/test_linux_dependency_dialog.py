"""Actual dependency diagnostics and consent UI on an explicitly owned Xvfb."""
import os
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_dependencies as deps


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1"
    and os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1", "requires owned Xvfb")
class LinuxDependencyDialogTests(unittest.TestCase):
    def setUp(self):
        import tkinter as tk
        self.root = tk.Tk()
        self.root.withdraw()
        self.addCleanup(self.root.destroy)
        self.message = "当前 Ubuntu 22.04 / Python 3.10，软件源依赖不匹配。\n" + (
            "APT 诊断详情：" + "/long-path/" * 20 + "中文😀\n") * 40

    def exercise(self, question, action, resize=None):
        observed = []
        failures = []
        def inspect():
            window = self.root.nametowidget(".dependency_details")
            try:
                window.update_idletasks()
                if resize:
                    window.geometry(resize)
                    window.update_idletasks()
                self.assertGreaterEqual(window.winfo_rootx(), 0)
                self.assertGreaterEqual(window.winfo_rooty(), 0)
                self.assertLessEqual(window.winfo_rootx() + window.winfo_width(), self.root.winfo_screenwidth())
                self.assertLessEqual(window.winfo_rooty() + window.winfo_height(), self.root.winfo_screenheight())
                body = self.root.nametowidget(".dependency_details.content.details.message")
                self.assertEqual(self.message, body.get("1.0", "end-1c"))
                self.assertEqual("disabled", body.cget("state"))
                before = body.yview()
                body.yview_moveto(1)
                self.assertGreater(body.yview()[0], before[0])
                self.assertEqual(1, body.yview()[1])
                body.tag_add("sel", "1.0", "end-1c")
                self.assertEqual(self.message, body.get("sel.first", "sel.last"))
                body.event_generate("<<Copy>>")
                self.root.update_idletasks()
                self.assertEqual(self.message, self.root.clipboard_get())
                actions = self.root.nametowidget(".dependency_details.content.actions")
                for button in actions.winfo_children():
                    self.assertTrue(button.winfo_ismapped())
                    self.assertGreaterEqual(button.winfo_rootx(), window.winfo_rootx())
                    self.assertGreaterEqual(button.winfo_width(), button.winfo_reqwidth())
                    self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), window.winfo_rooty() + window.winfo_height())
                    self.assertLessEqual(button.winfo_rootx() + button.winfo_width(), window.winfo_rootx() + window.winfo_width())
                observed.append(True)
            except BaseException as error:
                failures.append(error)
            finally:
                self.root.nametowidget(".dependency_details.content.actions." + action).invoke()
        self.root.after(60, inspect)
        accepted = deps.scrollable_dialog(self.root, self.message, question=question)
        if failures:
            raise failures[0]
        self.assertTrue(observed)
        self.assertIsNone(self.root.grab_current())
        return accepted

    def test_long_error_is_bounded_scrollable_and_selectable_at_large_fonts(self):
        from tkinter import font
        for points in (10, 18, 24):
            with self.subTest(points=points):
                font.nametofont("TkDefaultFont").configure(size=points)
                self.assertFalse(self.exercise(False, "dismiss"))

    def test_long_consent_defaults_to_cancel_and_dismiss_does_not_accept(self):
        self.assertFalse(self.exercise(True, "dismiss"))

    def test_only_explicit_install_button_accepts_long_consent(self):
        self.assertTrue(self.exercise(True, "install"))

    def test_narrow_large_font_consent_keeps_both_actions_inside(self):
        from tkinter import font
        original = self.root.tk.call("tk", "scaling")
        try:
            for dpi in (96, 144, 192):
                with self.subTest(dpi=dpi):
                    self.root.tk.call("tk", "scaling", dpi / 72)
                    font.nametofont("TkDefaultFont").configure(size=24)
                    self.assertFalse(self.exercise(True, "dismiss", resize="380x260+24+24"))
        finally:
            self.root.tk.call("tk", "scaling", original)


if __name__ == "__main__":
    unittest.main()
