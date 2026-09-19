"""Real Tk widgets and encrypted persistence, using an isolated test profile."""
import os
import gc
from pathlib import Path
import sys
import tempfile
import threading
import time
import tkinter as tk
from tkinter import ttk
from types import SimpleNamespace
import unittest
import weakref
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_device_panel as panel
import remotedesk_linux_devices as model


@unittest.skipUnless(sys.platform.startswith("linux") and os.environ.get("DISPLAY"), "Tk display required; run with xvfb-run")
class DevicePanelUiTests(unittest.TestCase):
    def setUp(self):
        from remotedesk_linux_app import RemoteDeskLinuxApp
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = tk.Tk(); self.root.geometry("900x400"); self.addCleanup(self.close_tk_fixture)
        self.app = SimpleNamespace(root=self.root, viewer=None, viewer_host=tk.StringVar(), viewer_port=tk.StringVar(),
                                   viewer_password=tk.StringVar(), connect_viewer=mock.Mock(),
                                   _wrap_action_buttons=RemoteDeskLinuxApp._wrap_action_buttons,
                                   _wrapping_label=RemoteDeskLinuxApp._wrapping_label,
                                   _device_table=RemoteDeskLinuxApp._device_table)
        store = model.Store(Path(self.temp.name) / "devices")
        with mock.patch.object(model, "Store", return_value=store):
            self.panel = panel.DevicePanel(self.app, self.root)
        self.panel.pack(fill=tk.BOTH)
        self.panel.refreshed = time.monotonic()  # Network is covered by separate bounded loopback tests.
        self.wait(lambda:self.panel.ready)

    def close_tk_fixture(self):
        # Destroying the window does not release Python StringVar/widget
        # cycles. Finalize those on their owning thread, before the next
        # fixture starts a storage worker which could otherwise trigger GC.
        if getattr(self, "panel", None) is not None:
            self.panel.close()
            self.panel.storage.shutdown(wait=True, cancel_futures=True)
            self.panel = None
        self.app = None
        self.root.destroy()
        self.root = None
        gc.collect()

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
        self.assertIn("暂无已保存设备", self.panel.status.cget("text"))
        self.add_device("PC.", "45678", "test-one", "测试电脑")
        self.assertIn("已保存 1 个设备", self.panel.status.cget("text"))
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
        self.assertIn("暂无已保存设备", self.panel.status.cget("text"))

    def test_refresh_never_overwrites_edited_fields(self):
        self.add_device("pc", "", "test-password", "备注")
        self.assertEqual("", self.app.viewer_port.get())
        self.panel.tree.selection_set("saved:" + self.panel.book.nodes[0].id)
        self.app.viewer_host.set("typed-new-address"); self.app.viewer_port.set("45679")
        self.panel.render(); self.root.update()
        self.assertEqual("typed-new-address", self.app.viewer_host.get()); self.assertEqual("45679", self.app.viewer_port.get())

    def test_reused_endpoint_renders_separate_rows_and_preserves_each_credential(self):
        first_id = "00112233-4455-6677-8899-aabbccddeeff"
        second_id = "00112233-4455-6677-8899-aabbccddeeaa"
        first = self.panel.book.remember("pc", 56565, "first-secret", device_id=first_id, remark="第一台")
        second = self.panel.book.remember("pc", 56565, "second-secret", device_id=second_id, remark="第二台")
        self.panel.nearby = [model.Device("pc", 56565, "PC", device_id=second_id)]
        self.panel.render(); self.root.update()
        self.assertEqual(2, len(self.panel.tree.get_children()))
        self.assertEqual("第二台", self.panel.tree.item("saved:" + second.id, "text"))
        self.panel.tree.selection_set("saved:" + first.id); self.panel.fill_selection()
        self.assertEqual("first-secret", self.app.viewer_password.get())
        self.panel.tree.selection_set("saved:" + second.id); self.panel.fill_selection()
        self.assertEqual("second-secret", self.app.viewer_password.get())

    def test_discovery_does_not_borrow_credentials_from_conflicting_saved_identity(self):
        first = self.panel.book.remember("pc", 56565, "first-secret", device_id="00112233-4455-6677-8899-aabbccddeeff")
        self.panel.nearby = [model.Device("pc", 56565, "Other", device_id="00112233-4455-6677-8899-aabbccddeeaa")]
        self.panel.render(); self.root.update()
        self.assertEqual(2, len(self.panel.tree.get_children()))
        self.panel.tree.selection_set("found:pc:56565"); self.panel.fill_selection()
        self.assertEqual("", self.app.viewer_password.get())
        self.assertIn("saved:" + first.id, self.panel.rows)

    def test_choose_many_long_named_candidates_is_scrollable_and_returns_selected_endpoint(self):
        from remotedesk_linux_app import RemoteDeskLinuxApp
        self.root.tk.call("tk", "scaling", 192 / 72)
        RemoteDeskLinuxApp._configure_style(self.app)
        devices = [model.Device("192.0.2.1", 40000 + index, "很长的测试设备名称" * 12) for index in range(40)]
        for accept in (False, True):
            errors = []
            def inspect():
                dialog = next(w for w in self.root.winfo_children() if isinstance(w, tk.Toplevel))
                try:
                    dialog.minsize(1, 1)
                    dialog.geometry("480x360")
                    self.root.update()
                    tree = next(w for w in self.widgets(dialog) if isinstance(w, ttk.Treeview))
                    tree.selection_set("39"); tree.see("39")
                    self.root.update()
                    self.assertGreater(tree.yview()[0], 0)
                    detail = next(w for w in self.widgets(dialog) if isinstance(w, tk.Text))
                    self.assertIn(devices[-1].name, detail.get("1.0", tk.END))
                    self.assertIn("192.0.2.1:40039", detail.get("1.0", tk.END))
                    for title in ("取消", "连接此设备"):
                        button = self.button(title, dialog)
                        self.assertTrue(button.winfo_ismapped())
                        self.assertGreaterEqual(button.winfo_rootx(), dialog.winfo_rootx())
                        self.assertLessEqual(button.winfo_rootx() + button.winfo_width(), dialog.winfo_rootx() + dialog.winfo_width())
                        self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), dialog.winfo_rooty() + dialog.winfo_height())
                    self.button("连接此设备" if accept else "取消", dialog).invoke()
                except Exception as error:
                    errors.append(error); dialog.destroy()
            self.root.after(80, inspect)
            result = self.panel.choose(devices)
            self.assertEqual([], errors)
            self.assertIs(devices[-1] if accept else None, result)
        self.assertIsNone(self.panel.choose([]))
        self.assertIs(devices[0], self.panel.choose(devices[:1]))

    def test_add_dialog_large_font_inputs_fit_small_display(self):
        import remotedesk_linux_app as application
        self.root.tk.call("tk", "scaling", 192 / 72)
        application.RemoteDeskLinuxApp._configure_style(self.app)
        errors = []
        def inspect():
            dialog = next(w for w in self.root.winfo_children() if isinstance(w, tk.Toplevel))
            try:
                self.root.update()
                self.assertLessEqual(dialog.winfo_width(), 592)
                self.assertLessEqual(dialog.winfo_height(), 432)
                for widget in (w for w in self.widgets(dialog) if isinstance(w, (ttk.Entry, ttk.Button))):
                    self.assertTrue(widget.winfo_ismapped())
                    self.assertGreaterEqual(widget.winfo_rootx(), dialog.winfo_rootx())
                    self.assertLessEqual(widget.winfo_rootx() + widget.winfo_width(), dialog.winfo_rootx() + dialog.winfo_width())
                    self.assertLessEqual(widget.winfo_rooty() + widget.winfo_height(), dialog.winfo_rooty() + dialog.winfo_height())
                self.button("取消", dialog).invoke()
            except Exception as error:
                errors.append(error); dialog.destroy()
        self.root.after(80, inspect)
        with mock.patch.object(application, "query_xrandr_monitor_bounds", return_value=[(0, 0, 640, 480)]):
            self.panel.add()
        self.assertEqual([], errors)

    def test_add_invalid_endpoint_reports_error_without_losing_fields(self):
        errors = []
        def inspect():
            dialog = next(w for w in self.root.winfo_children() if isinstance(w, tk.Toplevel))
            try:
                fields = [w for w in dialog.winfo_children() if isinstance(w, ttk.Entry)]
                for field, value in zip(fields, ("192.0.2.1", "70000", "fixture", "备注")):
                    field.insert(0, value)
                with mock.patch.object(panel.messagebox, "showwarning") as warning:
                    self.button("保存设备", dialog).invoke()
                warning.assert_called_once()
                self.assertIs(dialog, warning.call_args.kwargs["parent"])
                self.assertEqual("70000", fields[1].get())
                self.assertEqual("备注", fields[3].get())
                self.assertEqual([], self.panel.book.nodes)
                self.button("取消", dialog).invoke()
            except Exception as error:
                errors.append(error); dialog.destroy()
        self.root.after(60, inspect)
        self.panel.add()
        self.assertEqual([], errors)


@unittest.skipUnless(sys.platform.startswith("linux") and os.environ.get("DISPLAY"), "Tk display required")
class FullApplicationDevicePanelTests(unittest.TestCase):
    def test_relay_address_dialog_fills_direct_endpoint_without_connecting(self):
        import remotedesk_linux_app as application
        with tempfile.TemporaryDirectory() as folder, mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}):
            root = tk.Tk(); root.withdraw()
            app = application.RemoteDeskLinuxApp(root)
            try:
                options = app.relay_options
                target = dict(deviceId="9220b49b-0f2f-4f87-913a-c3f95091500f", machineName="Owned target",
                              directAddresses=["192.0.2.3", "198.51.100.4"], directPort=40565)
                app.relay_password.set("test-only-password")
                app._show_relay_addresses(options, target)
                root.update()
                dialog = next(widget for widget in root.winfo_children() if isinstance(widget, tk.Toplevel))
                addresses = next(widget for widget in dialog.winfo_children() if isinstance(widget, tk.Listbox))
                addresses.selection_clear(0, tk.END); addresses.selection_set(1)
                next(widget for widget in dialog.winfo_children() if isinstance(widget, ttk.Button)).invoke()
                root.update()
                self.assertEqual("198.51.100.4", app.viewer_host.get())
                self.assertEqual("40565", app.viewer_port.get())
                self.assertEqual("test-only-password", app.viewer_password.get())
                self.assertIsNone(app.viewer)
                self.assertEqual(1, app.main_notebook.index(app.main_notebook.select()))
            finally:
                app.close()

    def test_close_releases_tk_before_slow_address_lookup_finishes(self):
        import remotedesk_linux_app as application
        entered, release, finished = threading.Event(), threading.Event(), threading.Event()
        def delayed_directory(_options):
            entered.set()
            try:
                if not release.wait(5): raise TimeoutError("test address request was not released")
                return []
            finally: finished.set()
        with (tempfile.TemporaryDirectory() as folder,
              mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}),
              mock.patch.object(application.relay, "list_devices", side_effect=delayed_directory)):
            root = tk.Tk(); root.withdraw()
            app = application.RemoteDeskLinuxApp(root)
            storage = app.device_panel.storage
            references = [weakref.ref(value) for value in (root, app, app.relay_server)]
            try:
                app.relay_options = object()
                app.relay_list.insert("", tk.END, iid="owned-target", text="Owned target")
                app.relay_list.selection_set("owned-target")
                app._request_relay_addresses()
                self.assertTrue(entered.wait(2))
                app.close()
                del app, root
                gc.collect()
                self.assertTrue(all(reference() is None for reference in references))
            finally:
                release.set(); storage.shutdown(wait=True)
                self.assertTrue(finished.wait(2))

    def test_close_releases_tk_before_slow_relay_directory_finishes(self):
        import remotedesk_linux_app as application
        entered, release, finished = threading.Event(), threading.Event(), threading.Event()
        def delayed_directory(_options):
            entered.set()
            try:
                if not release.wait(5): raise TimeoutError("test relay request was not released")
                return []
            finally: finished.set()
        with (tempfile.TemporaryDirectory() as folder,
              mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}),
              mock.patch.object(application.relay, "list_devices", side_effect=delayed_directory)):
            root = tk.Tk(); root.withdraw()
            app = application.RemoteDeskLinuxApp(root)
            storage = app.device_panel.storage
            references = [weakref.ref(value) for value in (root, app, app.relay_server)]
            try:
                app.relay_options = object()
                app._refresh_relay()
                self.assertTrue(entered.wait(2))
                app.close()
                del app, root
                gc.collect()
                self.assertTrue(all(reference() is None for reference in references))
            finally:
                release.set(); storage.shutdown(wait=True)
                self.assertTrue(finished.wait(2))

    def test_close_releases_tk_before_slow_storage_worker_finishes(self):
        import remotedesk_linux_app as application
        entered, release = threading.Event(), threading.Event()
        def delayed_load():
            entered.set()
            if not release.wait(5): raise TimeoutError("test worker was not released")
            return model.Book()
        with (tempfile.TemporaryDirectory() as folder,
              mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}),
              mock.patch.object(model.Store, "load", side_effect=delayed_load)):
            root = tk.Tk(); root.withdraw()
            app = application.RemoteDeskLinuxApp(root)
            storage = app.device_panel.storage
            references = [weakref.ref(value) for value in (root, app, app.device_panel, app.host_port)]
            try:
                self.assertTrue(entered.wait(2))
                app.close()
                del app, root
                gc.collect()  # Tk finalizers must run here, never on the delayed worker.
                self.assertTrue(all(reference() is None for reference in references))
            finally:
                release.set(); storage.shutdown(wait=True)

    def test_close_releases_tk_before_cancelled_discovery_finishes(self):
        import remotedesk_linux_app as application
        entered, release, finished = threading.Event(), threading.Event(), threading.Event()
        def delayed_scan(*_):
            entered.set()
            try:
                if not release.wait(5): raise TimeoutError("test scanner was not released")
                return []
            finally: finished.set()
        with (tempfile.TemporaryDirectory() as folder,
              mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": folder}),
              mock.patch.object(model.Scanner, "scan", side_effect=delayed_scan)):
            root = tk.Tk(); root.withdraw()
            app = application.RemoteDeskLinuxApp(root)
            storage = app.device_panel.storage
            references = [weakref.ref(value) for value in (root, app, app.device_panel)]
            try:
                app.device_panel.scan("127.0.0.1", callback=app.device_panel.render)
                self.assertTrue(entered.wait(2))
                app.close(); app.close()
                del app, root
                gc.collect()
                self.assertTrue(all(reference() is None for reference in references))
            finally:
                release.set(); storage.shutdown(wait=True)
                self.assertTrue(finished.wait(2))

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
