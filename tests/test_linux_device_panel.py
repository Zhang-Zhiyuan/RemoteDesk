"""Real Tk widgets and encrypted persistence, using an isolated test profile."""
import os
from pathlib import Path
import sys
import tempfile
import time
import tkinter as tk
from tkinter import ttk
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_device_panel as panel
import remotedesk_linux_devices as model


@unittest.skipUnless(sys.platform.startswith("linux") and os.environ.get("DISPLAY"), "Tk display required; run with xvfb-run")
class DevicePanelUiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = tk.Tk(); self.root.geometry("900x400"); self.addCleanup(self.root.destroy)
        self.app = SimpleNamespace(root=self.root, viewer=None, viewer_host=tk.StringVar(), viewer_port=tk.StringVar(),
                                   viewer_password=tk.StringVar(), connect_viewer=mock.Mock())
        store = model.Store(Path(self.temp.name) / "devices")
        with mock.patch.object(model, "Store", return_value=store):
            self.panel = panel.DevicePanel(self.app, self.root)
        self.addCleanup(self.panel.close); self.panel.pack(fill=tk.BOTH)
        self.panel.refreshed = time.monotonic()  # Network is covered by separate bounded loopback tests.
        self.wait(lambda:self.panel.ready)

    def wait(self, predicate):
        end = time.monotonic() + 3
        while not predicate() and time.monotonic() < end:
            self.root.update(); time.sleep(.01)
        self.assertTrue(predicate())

    def widgets(self, parent):
        for child in parent.winfo_children():
            yield child; yield from self.widgets(child)

    def button(self, title, parent=None):
        return next(w for w in self.widgets(parent or self.root) if isinstance(w, ttk.Button) and w.cget("text") == title)

    def add_device(self, address, port, password, remark):
        def fill_dialog():
            dialog = next(w for w in self.root.winfo_children() if isinstance(w, tk.Toplevel))
            fields = [w for w in dialog.winfo_children() if isinstance(w, ttk.Entry)]
            self.assertEqual(4, len(fields)); self.assertEqual("*", fields[2].cget("show"))
            for entry, value in zip(fields, (address, port, password, remark)): entry.insert(0, value)
            self.button("保存设备", dialog).invoke()
        self.root.after(60, fill_dialog); self.button("新增设备").invoke()
        self.wait(lambda:self.app.viewer_password.get() == password)

    def test_add_merge_rename_restart_and_delete(self):
        self.add_device("PC.", "45678", "test-one", "测试电脑")
        original = self.panel.book.nodes[0].id
        self.add_device("pc", "45678", "test-two", "")
        self.assertEqual(1, len(self.panel.book.nodes)); self.assertEqual(original, self.panel.book.nodes[0].id)
        self.assertEqual("测试电脑", self.panel.book.nodes[0].remark)
        self.assertEqual("45678", self.app.viewer_port.get())
        self.panel.tree.selection_set("saved:" + original)
        with mock.patch.object(panel.simpledialog, "askstring", return_value="新的备注"):
            self.button("修改备注").invoke()
        self.wait(lambda:self.panel.book.nodes[0].remark == "新的备注")
        self.assertEqual("新的备注", self.panel.store.load().nodes[0].remark)
        with mock.patch.object(panel.messagebox, "askyesno", return_value=True), mock.patch.object(panel.messagebox, "askokcancel", return_value=True):
            self.button("删除记录").invoke()
        self.wait(lambda:not self.panel.book.nodes)
        self.assertFalse(self.panel.store.load().nodes)

    def test_refresh_never_overwrites_edited_fields(self):
        self.add_device("pc", "", "test-password", "备注")
        self.assertEqual("", self.app.viewer_port.get())
        self.panel.tree.selection_set("saved:" + self.panel.book.nodes[0].id)
        self.app.viewer_host.set("typed-new-address"); self.app.viewer_port.set("45679")
        self.panel.render(); self.root.update()
        self.assertEqual("typed-new-address", self.app.viewer_host.get()); self.assertEqual("45679", self.app.viewer_port.get())


@unittest.skipUnless(sys.platform.startswith("linux") and os.environ.get("DISPLAY"), "Tk display required")
class FullApplicationDevicePanelTests(unittest.TestCase):
    def test_real_application_initializes_new_directory_and_closes_cleanly(self):
        import remotedesk_linux_app as application
        with tempfile.TemporaryDirectory() as folder, mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}):
            root = tk.Tk()
            app = application.RemoteDeskLinuxApp(root)
            try:
                root.update()
                self.assertIsInstance(app.device_panel, panel.DevicePanel)
                self.assertEqual("", app.viewer_port.get())
                self.assertTrue(model.identity(app.relay_device_id))
                self.assertEqual(app.relay_device_id, model.local_device_id())
            finally:
                app.close()
            self.assertTrue(app.device_panel.closed)


if __name__ == "__main__": unittest.main()
