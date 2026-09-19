from __future__ import annotations

import queue
import gc
import tempfile
import os
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


LINUX_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "linux"
sys.path.insert(0, str(LINUX_SCRIPTS))

import remotedesk_linux_app as app  # noqa: E402


class LinuxAdaptiveLayoutTests(unittest.TestCase):
    def test_preferred_window_is_centered_when_it_fits(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (0, 0, 1920, 1080),
            (1180, 760),
            (640, 480),
        )

        self.assertEqual((1180, 760), (geometry.width, geometry.height))
        self.assertEqual((370, 160), (geometry.x, geometry.y))
        self.assertEqual((640, 480), (geometry.minimum_width, geometry.minimum_height))

    def test_small_screen_clamps_size_minimum_and_margin(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (0, 0, 800, 600),
            (1180, 760),
            (920, 640),
        )

        self.assertEqual((752, 552), (geometry.width, geometry.height))
        self.assertEqual((752, 552), (geometry.minimum_width, geometry.minimum_height))
        self.assertEqual((24, 24), (geometry.x, geometry.y))

    def test_owner_centering_is_clamped_on_negative_origin_display(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (-1280, 0, 1280, 1024),
            (960, 560),
            (480, 300),
            anchor_bounds=(-1400, 800, 400, 300),
        )

        self.assertEqual(-1256, geometry.x)
        self.assertEqual(440, geometry.y)
        self.assertEqual("960x560+-1256+440", geometry.to_tk_geometry())

    def test_tiny_virtual_screen_still_produces_positive_bounds(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (10, 20, 1, 1),
            (1180, 760),
            (640, 480),
        )

        self.assertEqual((1, 1, 10, 20), (geometry.width, geometry.height, geometry.x, geometry.y))
        self.assertEqual((1, 1), (geometry.minimum_width, geometry.minimum_height))

    def test_viewer_conversion_target_tracks_window_and_caps_extremes(self) -> None:
        self.assertIsNone(app.normalize_viewer_display_size(1, 1))
        self.assertEqual((640, 360), app.normalize_viewer_display_size(640, 360))
        self.assertEqual((3840, 2160), app.normalize_viewer_display_size(8000, 5000))

        connection = app.ViewerConnection("127.0.0.1", 56565, "test", queue.Queue(), 1)
        connection.set_display_size(640, 360)
        connection.set_display_size(1, 1)
        self.assertEqual((640, 360), connection.display_size)
        self.assertFalse(connection.display_active)

    def test_tk_scale_is_normalized_against_96_dpi(self) -> None:
        self.assertAlmostEqual(1.0, app.normalize_tk_ui_scale(96.0 / 72.0))
        self.assertAlmostEqual(2.0, app.normalize_tk_ui_scale(192.0 / 72.0))
        self.assertEqual(1.0, app.normalize_tk_ui_scale(float("nan")))
        self.assertEqual(3.0, app.normalize_tk_ui_scale(10.0))

    def test_viewer_image_fits_and_can_expand_with_window(self) -> None:
        self.assertEqual((800, 450), app.calculate_fitted_image_size(1920, 1080, 800, 600))
        self.assertEqual((1920, 1080), app.calculate_fitted_image_size(960, 540, 1920, 1080))
        self.assertEqual((338, 600), app.calculate_fitted_image_size(1080, 1920, 800, 600))

    def test_xrandr_monitors_are_parsed_and_selected_by_anchor(self) -> None:
        monitors = app.parse_xrandr_monitor_bounds(
            "Monitors: 2\n"
            " 0: +*eDP-1 1920/309x1080/174+0+0 eDP-1\n"
            " 1: +HDMI-1 1280/300x1024/220+-1280+0 HDMI-1\n"
        )

        self.assertEqual([(0, 0, 1920, 1080), (-1280, 0, 1280, 1024)], monitors)
        self.assertEqual((-1280, 0, 1280, 1024), app.choose_monitor_bounds(monitors, -400, 500))
        self.assertEqual((0, 0, 1920, 1080), app.choose_monitor_bounds(monitors, 2500, 500))

    def test_remote_edges_remain_clickable_when_image_is_downscaled(self) -> None:
        ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui.last_photo = object()
        ui.native_presenter_active = False
        ui.frame_label = mock.Mock()
        for remote in ((1920, 1080), (3840, 2160), (1080, 2400)):
            for displayed in ((640, 360), (320, 240), (1, 1)):
                ui.remote_width, ui.remote_height = remote
                ui.display_width, ui.display_height = displayed
                ui.frame_label.winfo_width.return_value = displayed[0] + 40
                ui.frame_label.winfo_height.return_value = displayed[1] + 60
                self.assertEqual((0, 0), ui._pointer_event_to_remote(SimpleNamespace(x=20, y=30)))
                expected = (remote[0] - 1, remote[1] - 1) if displayed != (1, 1) else (0, 0)
                self.assertEqual(expected, ui._pointer_event_to_remote(
                    SimpleNamespace(x=20 + displayed[0] - 1, y=30 + displayed[1] - 1)))
                self.assertIsNone(ui._pointer_event_to_remote(SimpleNamespace(x=19, y=30)))


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1",
                     "Set REMOTEDESK_RUN_TK_TESTS=1 on an owned display")
class RealTkAdaptiveLayoutTests(unittest.TestCase):
    def setUp(self):
        self.root = app.tk.Tk()
        self.root.geometry("640x400")
        self.addCleanup(self.cleanup_tk)
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.root = self.root
        self.ui._configure_style()

    def cleanup_tk(self):
        try:
            self.root.destroy()
        except app.tk.TclError:
            pass  # The full application closes its own root.
        self.ui = self.root = None
        # The next test starts worker threads. Collect this test's destroyed
        # interpreter/callback cycles on its owning thread before they start.
        gc.collect()

    def test_tab_scrolls_to_overflowing_inputs_and_tracks_new_rows(self):
        notebook = app.ttk.Notebook(self.root)
        notebook.pack(fill=app.tk.BOTH, expand=True)
        content = self.ui._create_scrollable_tab(notebook, "Layout test")
        app.ttk.Label(content, text="Test").pack()
        self.root.update()
        canvas = content.master
        initial_height = content.winfo_height()
        # Add controls after first layout; the viewport itself is unchanged.
        for index in range(15):
            app.ttk.Entry(content, width=140).pack(pady=4)
        last = content.winfo_children()[-1]
        self.root.update()
        self.assertGreater(content.winfo_height(), initial_height)
        self.assertGreater(content.winfo_width(), canvas.winfo_width())
        last.event_generate("<FocusIn>")
        self.root.update()
        self.assertGreater(canvas.yview()[0], 0)
        self.assertGreaterEqual(last.winfo_rooty(), canvas.winfo_rooty())
        self.assertLessEqual(last.winfo_rooty() + last.winfo_height(),
                             canvas.winfo_rooty() + canvas.winfo_height())
        content.destroy()
        self.root.update()  # Also verifies pending callbacks are detached.

    def test_main_pages_fit_narrow_viewports_at_large_font_scale(self):
        with tempfile.TemporaryDirectory() as directory, \
                mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": directory}), \
                mock.patch.object(app.DevicePanel, "scan"):
            self.root.tk.call("tk", "scaling", 192 / 72)
            ui = app.RemoteDeskLinuxApp(self.root)
            try:
                self.root.minsize(1, 1)
                for width in (1180, 800, 640, 800, 1180):
                    self.root.geometry(f"{width}x600")
                    for page in range(3):
                        ui.main_notebook.select(page)
                        self.root.update()
                        tab = ui.main_notebook.nametowidget(ui.main_notebook.select())
                        canvas = next(c for c in tab.winfo_children() if isinstance(c, app.tk.Canvas))
                        with self.subTest(width=width, page=page):
                            self.assertEqual((0.0, 1.0), canvas.xview())
                self.assertGreater(app.ttk.Style(self.root).lookup("Treeview", "rowheight"), 20)
            finally:
                ui.close()

    def test_wrapped_actions_keep_callbacks_and_fit_after_repeated_resize(self):
        panel = app.ttk.Frame(self.root)
        panel.pack(fill=app.tk.X)
        hits = []
        buttons = [app.ttk.Button(panel, text="按钮" * n, command=lambda n=n: hits.append(n)) for n in (2, 5, 3, 4)]
        for button in buttons:
            button.pack(side=app.tk.LEFT)
        app.RemoteDeskLinuxApp._wrap_action_buttons(panel, buttons)
        for width in (700, 320, 460, 700, 320):
            self.root.geometry(f"{width}x400")
            self.root.update()
            for button in buttons:
                self.assertGreaterEqual(button.winfo_x(), 0)
                self.assertLessEqual(button.winfo_x() + button.winfo_width(), panel.winfo_width())
                button.invoke()
        self.assertEqual([2, 5, 3, 4] * 5, hits)

    def test_viewer_reserves_clickable_controls_before_large_image(self):
        self.ui.viewer_window = None
        self.ui.window_icon = None
        self.ui.viewer = None
        self.ui.native_presenter_active = False
        self.ui.last_photo = None
        self.ui.text_input = app.tk.StringVar(self.root)
        self.ui._viewer_frame_focus_out = mock.Mock()
        with mock.patch.object(app, "apply_adaptive_window_geometry"):
            self.ui._open_viewer_window("layout-test.invalid", 56565)
        viewer = self.ui.viewer_window
        for scaling in (96 / 72, 144 / 72, 192 / 72):
            self.root.tk.call("tk", "scaling", scaling)
            for width, height in ((1280, 800), (640, 480), (480, 360), (800, 600)):
                viewer.geometry(f"{width}x{height}")
                image = app.tk.PhotoImage(master=viewer, width=1920, height=1080)
                # Keep the image's large requested size for the layout check.
                self.ui.frame_label.configure(image=image, text="")
                self.root.update()
                footer = self.ui.viewer_window_status.master
                self.assertGreaterEqual(self.ui.frame_label.winfo_height(), height * .35)
                more = next(control for parent in footer.winfo_children() for control in parent.winfo_children()
                            if isinstance(control, app.ttk.Menubutton))
                menu = more.nametowidget(more.cget("menu"))
                self.root.tk.call(menu.cget("postcommand"))
                menu_labels = {menu.entrycget(index, "label") for index in range(menu.index(app.tk.END) + 1)
                               if menu.type(index) == "command"}
                self.assertLessEqual(self.ui.viewer_window_status.winfo_rooty() + self.ui.viewer_window_status.winfo_height(),
                                     viewer.winfo_rooty() + viewer.winfo_height())
                for parent in (footer, *footer.winfo_children()):
                    if parent is self.ui.viewer_target_bar:
                        continue  # Deliberately hidden until multiple targets are advertised.
                    for control in parent.winfo_children():
                        if isinstance(control, (app.ttk.Button, app.ttk.Entry)):
                            if not control.winfo_ismapped():
                                self.assertTrue(more.winfo_ismapped())
                                self.assertIsInstance(control, app.ttk.Button)
                                self.assertIn(control.cget("text"), menu_labels)
                                continue
                            self.assertGreaterEqual(control.winfo_rootx(), viewer.winfo_rootx())
                            self.assertLessEqual(control.winfo_rootx() + control.winfo_width(), viewer.winfo_rootx() + viewer.winfo_width())
                            self.assertLessEqual(control.winfo_rooty() + control.winfo_height(), viewer.winfo_rooty() + viewer.winfo_height())
                            self.assertIs(control, viewer.winfo_containing(control.winfo_rootx() + control.winfo_width() // 2,
                                                                         control.winfo_rooty() + control.winfo_height() // 2))

    def test_viewer_overflow_menu_preserves_actions_and_disabled_state(self):
        self.root.tk.call("tk", "scaling", 192 / 72)
        self.ui._configure_style()
        self.ui.viewer_window = None
        self.ui.window_icon = None
        self.ui.viewer = None
        self.ui.native_presenter_active = False
        self.ui.last_photo = None
        self.ui.text_input = app.tk.StringVar(self.root)
        self.ui._viewer_frame_focus_out = mock.Mock()
        with mock.patch.object(app, "apply_adaptive_window_geometry"):
            self.ui._open_viewer_window("owned-layout.invalid", 56565)
        window = self.ui.viewer_window
        window.geometry("640x480")
        self.root.update()
        footer = self.ui.viewer_window_status.master
        more = next(control for parent in footer.winfo_children() for control in parent.winfo_children()
                    if isinstance(control, app.ttk.Menubutton))
        self.assertTrue(more.winfo_ismapped())
        menu = more.nametowidget(more.cget("menu"))
        sent = []
        self.ui.viewer_window_send_file_button.configure(command=lambda: sent.append("file"))
        for enabled in (False, True, False):
            self.ui.viewer_window_send_file_button.configure(state=app.tk.NORMAL if enabled else app.tk.DISABLED)
            menu.post(more.winfo_rootx(), more.winfo_rooty() + more.winfo_height())
            self.root.update()
            index = next(index for index in range(menu.index(app.tk.END) + 1)
                         if menu.type(index) == "command" and menu.entrycget(index, "label") == "发送文件")
            self.assertEqual("normal" if enabled else "disabled", menu.entrycget(index, "state"))
            menu.invoke(index)
            menu.unpost()
        self.assertEqual(["file"], sent)
        self.assertGreaterEqual(self.ui.frame_label.winfo_height(), 240)
        self.ui.viewer_status = mock.Mock()
        full_status = "Decoder fallback\n" + "Owned diagnostic line\n" * 6
        self.ui._set_viewer_status(full_status)
        self.root.update()
        self.assertNotIn("\n", self.ui.viewer_window_status.cget("text"))
        self.assertGreaterEqual(self.ui.frame_label.winfo_height(), 240)
        self.ui._show_viewer_status_details()
        details = next(child for child in window.winfo_children()
                       if isinstance(child, app.tk.Toplevel) and child.title() == "完整连接状态")
        details_text = next(child for child in details.winfo_children() if isinstance(child, app.tk.Text))
        self.assertEqual(full_status, details_text.get("1.0", "end-1c"))
        self.assertEqual("disabled", details_text.cget("state"))
        details.destroy()
        self.ui.viewer_target_selector.configure(values=("Screen 1", "Screen 2"), state="readonly")
        self.ui.viewer_target_bar.pack(fill=app.tk.X, before=self.ui.viewer_target_controls_anchor)
        self.root.update()
        self.assertTrue(self.ui.viewer_target_selector.winfo_ismapped())
        self.assertGreaterEqual(self.ui.frame_label.winfo_height(), 180)
        status = self.ui.viewer_window_status
        self.assertLessEqual(status.winfo_rooty() + status.winfo_height(), window.winfo_rooty() + window.winfo_height())
        self.ui.viewer_target_bar.pack_forget()
        for _ in range(3):
            window.geometry("1900x1000")
            self.root.update()
            self.assertFalse(more.winfo_ismapped())
            self.assertTrue(self.ui.viewer_window_send_file_button.winfo_ismapped())
            window.geometry("640x480")
            self.root.update()
            self.assertTrue(more.winfo_ismapped())
        background_errors = []
        self.root.createcommand("owned_bgerror", background_errors.append)
        self.root.tk.eval("proc bgerror {msg} {owned_bgerror $msg}")
        self.ui.viewer_target_controls_anchor.event_generate("<Configure>", width=620, height=100)
        window.destroy()
        self.root.update()
        self.assertEqual([], background_errors)


if __name__ == "__main__":
    unittest.main()
