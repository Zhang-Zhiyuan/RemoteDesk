"""Run unchanged under separately owned X servers at 96/120/144/192 DPI."""
import gc
import json
import os
from pathlib import Path
import queue
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
from remotedesk_linux_transfers import TransferTaskPanel


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1"
    and os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1", "requires owned Xvfb")
class RealServerDpiLayoutTests(unittest.TestCase):
    def setUp(self):
        self.root = app.tk.Tk()
        self.root.geometry("800x600+0+0")
        self.errors = []
        self.root.report_callback_exception = lambda *args: self.errors.append(str(args))
        self.dpi = self.root.winfo_fpixels("1i")
        self.scaling = float(self.root.tk.call("tk", "scaling"))
        expected = os.environ.get("REMOTEDESK_EXPECT_DPI")
        if expected:
            self.assertAlmostEqual(float(expected), self.dpi, delta=1.5)
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.root = self.root
        self.ui._configure_style()
        self.ui.window_icon = None
        self.addCleanup(self.cleanup)

    def cleanup(self):
        try:
            for after in self.root.tk.call("after", "info"):
                self.root.after_cancel(after)
            self.root.destroy()
        except app.tk.TclError:
            pass
        self.ui = self.root = None
        gc.collect()

    def descendants(self, widget):
        for child in widget.winfo_children():
            yield child
            yield from self.descendants(child)

    def settle(self):
        for _ in range(3):
            self.root.update()
        self.assertEqual([], self.errors)

    def assert_control_fits(self, widget, window):
        self.assertTrue(widget.winfo_ismapped(), str(widget))
        self.assertGreaterEqual(widget.winfo_width(), widget.winfo_reqwidth(), str(widget))
        self.assertGreaterEqual(widget.winfo_height(), widget.winfo_reqheight(), str(widget))
        self.assertGreaterEqual(widget.winfo_rootx(), window.winfo_rootx())
        self.assertGreaterEqual(widget.winfo_rooty(), window.winfo_rooty())
        self.assertLessEqual(widget.winfo_rootx() + widget.winfo_width(), window.winfo_rootx() + window.winfo_width())
        self.assertLessEqual(widget.winfo_rooty() + widget.winfo_height(), window.winfo_rooty() + window.winfo_height())

    def assert_window_fits_screen(self, window):
        self.assertGreaterEqual(window.winfo_rootx(), 0)
        self.assertGreaterEqual(window.winfo_rooty(), 0)
        self.assertLessEqual(window.winfo_rootx() + window.winfo_width(), window.winfo_screenwidth())
        self.assertLessEqual(window.winfo_rooty() + window.winfo_height(), window.winfo_screenheight())

    def evidence(self, name, window, **metrics):
        report = dict(test=name, serverDpi=self.dpi, tkScaling=self.scaling,
                      size=[window.winfo_width(), window.winfo_height()], **metrics)
        print(json.dumps(report, ensure_ascii=False))
        location = os.environ.get("REMOTEDESK_DPI_OUTPUT")
        if location:
            from PIL import ImageGrab
            folder = Path(location)
            folder.mkdir(parents=True, exist_ok=True)
            (folder / f"{name}.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            window.lift()
            self.settle()
            ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(folder / f"{name}.png")

    def test_main_pages_header_actions_and_relay_scrolling_at_server_dpi(self):
        with tempfile.TemporaryDirectory() as config, mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": config}), \
                mock.patch.object(app.DevicePanel, "scan"):
            ui = app.RemoteDeskLinuxApp(self.root)
            try:
                self.settle()
                self.assert_window_fits_screen(self.root)
                ui._show_relay_directory([dict(deviceId=f"owned-{index}", machineName="隔离测试设备-" + str(index) + "很长的名称" * 4,
                    platform="Linux", busy=bool(index % 2), directAddresses=["192.0.2.1"], directPort=56565) for index in range(30)])
                self.root.minsize(1, 1)  # Also stress WM/work-area constraints below the preferred minimum.
                ui.relay_ssh_more_button.invoke()  # Optional controls must fit when explicitly expanded.
                actions = 0
                for width, height in ((1180, 760), (800, 600), (640, 480)):
                    self.root.geometry(f"{width}x{height}+0+0")
                    for page in range(3):
                        ui.main_notebook.select(page)
                        self.settle()
                        tab = ui.main_notebook.nametowidget(ui.main_notebook.select())
                        for control in self.descendants(tab):
                            if isinstance(control, app.ttk.Button):
                                control.event_generate("<FocusIn>")
                                self.settle()
                                self.assert_control_fits(control, self.root)
                                actions += 1
                        if page == 2:
                            ui.relay_list.event_generate("<FocusIn>")
                            ui.relay_list.see(ui.relay_list.get_children()[-1])
                            self.settle()
                            self.assertGreater(ui.relay_list.yview()[0], 0)
                    header_labels = [item for item in self.descendants(self.root) if isinstance(item, app.tk.Label)
                                     and "Linux ·" in str(item.cget("text"))]
                    for label in header_labels:
                        self.assertLessEqual(label.winfo_rootx() + label.winfo_width(), self.root.winfo_width())
                ui.main_notebook.select(2)
                for tree, samples in ((ui.relay_list, {"platform": "Windows", "status": "使用中（可接管）"}),
                                      (ui.device_panel.tree, {"state": "已保存 · 可自动探测"})):
                    ui.main_notebook.select(2 if tree is ui.relay_list else 1)
                    self.settle()
                    heading_font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup("Treeview.Heading", "font"))
                    row_font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup("Treeview", "font"))
                    for column in ("#0", *tree.cget("columns")):
                        self.assertGreaterEqual(tree.column(column, "width"), heading_font.measure(tree.heading(column, "text")) + 12, column)
                    for column, value in samples.items():
                        self.assertGreaterEqual(tree.column(column, "minwidth"), row_font.measure(value) + 12, (column, value))
                    horizontal = next(child for child in tree.master.winfo_children()
                                      if isinstance(child, app.ttk.Scrollbar) and str(child.cget("orient")) == "horizontal")
                    tree.xview_moveto(1)
                    horizontal.event_generate("<FocusIn>")
                    self.settle()
                    self.assertEqual(1.0, tree.xview()[1])
                    self.assert_control_fits(horizontal, self.root)
                ui.main_notebook.select(2)
                self.root.geometry("800x600+0+0")
                ui.relay_list.event_generate("<FocusIn>")
                total_width = sum(ui.relay_list.column(column, "width") for column in ("#0", *ui.relay_list.cget("columns")))
                ui.relay_list.xview_moveto(ui.relay_list.column("#0", "width") / total_width)
                self.settle()
                self.evidence("main-relay", self.root, actionReachabilityChecks=actions)
            finally:
                ui.close()

    def test_viewer_actions_are_visible_or_reachable_in_overflow_at_server_dpi(self):
        ui = self.ui
        ui.viewer_window = None
        ui.viewer = None
        ui.native_presenter_active = False
        ui.last_photo = None
        ui.text_input = app.tk.StringVar(self.root)
        ui._viewer_frame_focus_out = mock.Mock()
        ui._open_viewer_window("owned-dpi.invalid", 1)
        window = ui.viewer_window
        self.settle()
        self.assert_window_fits_screen(window)
        window.minsize(1, 1)
        configurations = []
        window.bind("<Configure>", lambda event: configurations.append((event.width, event.height)), add="+")
        for width, height in ((1100, 700), (800, 600), (640, 480), (480, 360), (800, 600)):
            window.geometry(f"{width}x{height}+0+0")
            self.settle()
            footer = ui.viewer_window_status.master
            more = next(widget for widget in self.descendants(footer) if isinstance(widget, app.ttk.Menubutton))
            menu = more.nametowidget(more.cget("menu"))
            self.root.tk.call(menu.cget("postcommand"))
            labels = {menu.entrycget(index, "label") for index in range(menu.index("end") + 1) if menu.type(index) == "command"}
            for control in self.descendants(footer):
                if isinstance(control, app.ttk.Button) and control not in ui.viewer_android_buttons:
                    if control.winfo_ismapped():
                        self.assert_control_fits(control, window)
                    else:
                        self.assertIn(control.cget("text"), labels)
            count = len(configurations)
            self.settle()
            self.assertEqual(count, len(configurations), "idle layout must settle rather than oscillate")
            self.assertGreater(ui.frame_label.winfo_height(), height * .30)
        self.evidence("viewer", window, configureEvents=len(configurations))

    def test_task_phase_and_bytes_remain_readable_with_dpi_and_larger_font(self):
        ui = self.ui
        ui.viewer = app.ViewerConnection("192.0.2.1", 1, "owned", queue.Queue(), 1)
        ui.transfer_task_panel = None
        ui.viewer_window = None
        ui._set_viewer_status = mock.Mock()
        for font_size in (10, 14):
            app.ttk.Style(self.root).configure("Treeview", font=("DejaVu Sans", font_size))
            ui._show_transfer_tasks()
            panel = ui.transfer_task_panel
            self.settle()
            self.assert_window_fits_screen(panel.window)
            panel.window.minsize(1, 1)
            panel.window.geometry("640x420+0+0")
            task = ui.viewer.transfer_tasks._new("取回", "隔离测试-资料归档.zip")
            task.phase = "单项失败，等待本批结束"
            task.completed_bytes = task.total_bytes = 1073741824
            panel.update(ui.viewer, task)
            self.settle()
            font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup("Treeview", "font"))
            self.assertGreaterEqual(int(app.ttk.Style(self.root).lookup(panel.tree.cget("style"), "rowheight")), font.metrics("linespace") + 4)
            for column, value in zip(("direction", "phase", "bytes"), panel.tree.item(task.id, "values")):
                self.assertGreaterEqual(panel.tree.column(column, "width"), font.measure(value) + 12, (font_size, column, value))
            panel.tree.xview_moveto(1)
            self.settle()
            self.assertEqual(1.0, panel.tree.xview()[1])
            self.assert_control_fits(panel.cancel_button, panel.window)
            self.evidence(f"tasks-font-{font_size}", panel.window, fontLineHeight=font.metrics("linespace"))
            panel.close()

    def test_confirmation_small_window_keeps_choices_and_selected_paths_reachable(self):
        app.tkfont.nametofont("TkFixedFont").configure(size=14)
        ui = self.ui
        items = [app.FileTransferPreviewItem("文件夹" if index == 0 else "文件", "/owned/来源/" + "目录" * 30,
                    f"隔离文件-{index}.txt", 1073741824, "/owned/接收/" + "目录" * 30 + f"/{index}.txt") for index in range(20)]
        inspected = []
        def inspect():
            dialog = next(child for child in self.root.winfo_children() if isinstance(child, app.tk.Toplevel))
            try:
                self.settle()
                self.assert_window_fits_screen(dialog)
                dialog.minsize(1, 1)
                dialog.geometry("640x480+0+0")
                self.settle()
                controls = list(self.descendants(dialog))
                for button in (control for control in controls if isinstance(control, app.ttk.Button)):
                    self.assert_control_fits(button, dialog)
                tree = next(control for control in controls if isinstance(control, app.ttk.Treeview))
                row_font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup("Treeview", "font"))
                heading_font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup("Treeview.Heading", "font"))
                for column in tree.cget("columns"):
                    self.assertGreaterEqual(tree.column(column, "width"), heading_font.measure(tree.heading(column, "text")) + 12, column)
                for column, value in (("kind", "文件夹"), ("size", app.format_transfer_bytes(1073741824))):
                    self.assertGreaterEqual(tree.column(column, "width"), row_font.measure(value) + 12, column)
                tree.selection_set(tree.get_children()[-1])
                tree.see(tree.get_children()[-1])
                self.settle()
                detail = next(control for control in controls if isinstance(control, app.tk.Text))
                self.assertIn("隔离文件-19.txt", detail.get("1.0", "end"))
                font = app.tkfont.Font(root=self.root, font=detail.cget("font"))
                self.assertGreaterEqual(detail.winfo_height(), font.metrics("linespace") + 4)
                detail.yview_moveto(1)
                self.settle()
                self.assertEqual(1.0, detail.yview()[1])
                self.evidence("confirmation", dialog, detailFontHeight=font.metrics("linespace"))
                inspected.append(True)
            except Exception as error:
                inspected.append(error)
            finally:
                dialog.destroy()
        self.root.after(20, inspect)
        self.assertFalse(ui._show_file_transfer_confirmation_dialog(self.root, "隔离DPI确认", "确认发送文件和文件夹", items, "完整路径可在下方滚动查看。"))
        self.assertEqual([True], inspected)

    def test_live_font_changes_keep_table_rows_readable_and_layout_settled(self):
        ui = self.ui
        ui.viewer = app.ViewerConnection("192.0.2.1", 1, "owned", queue.Queue(), 1)
        ui.transfer_task_panel = None
        panel = ui.transfer_task_panel = TransferTaskPanel(ui)
        task = ui.viewer.transfer_tasks._new("取回", "隔离资料.zip")
        task.phase = "单项失败，等待本批结束"
        task.detail = "/owned/保存的文件/资料.zip"
        panel.update(ui.viewer, task)
        tree = ui._device_table(self.root, columns=("state",), height=4)
        tree.heading("#0", text="设备 / 备注")
        tree.heading("state", text="状态")
        app.fit_table_columns(tree, {"#0": (220, 100, ()), "state": (180, 140, ("已保存 · 可自动探测",))})
        tree.insert("", "end", text="隔离设备", values=("已保存 · 可自动探测",))
        self.settle()
        panel.details.tag_add("sel", "1.0", "1.end")
        configurations = []
        panel.window.bind("<Configure>", lambda event: configurations.append((event.width, event.height)), add="+")
        for font_size in (14, 10, 14):
            app.ttk.Style(self.root).configure("Treeview", font=("DejaVu Sans", font_size))
            self.settle()
            for target in (tree, panel.tree):
                style = target.cget("style")
                font = app.tkfont.Font(root=self.root, font=app.ttk.Style(self.root).lookup(style, "font"))
                self.assertGreaterEqual(int(app.ttk.Style(self.root).lookup(style, "rowheight")), font.metrics("linespace") + 4)
            self.assertGreaterEqual(panel.tree.column("phase", "minwidth"), font.measure(task.phase) + 12)
            self.assertEqual(task.detail, panel.details.get(*panel.details.tag_ranges("sel")))
            count = len(configurations)
            self.settle()
            self.assertEqual(count, len(configurations), "font reflow must not keep rescheduling itself")
        self.evidence("live-font-change", panel.window, configureEvents=len(configurations))


if __name__ == "__main__":
    unittest.main()
