"""SSH UI integration on an explicitly owned X server; no real host is started."""
import gc
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_relay as relay

ID = "01234567-89ab-cdef-0123-456789abcdef"
TOKEN = "isolated-relay-token-" * 3


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1" and
    os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1", "requires explicitly owned Xvfb")
class SshUiTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.environment = mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": self.temporary.name})
        self.environment.start()
        self.scan = mock.patch.object(app.DevicePanel, "scan")
        self.scan.start()
        self.root = app.tk.Tk()
        self.errors = []
        self.root.report_callback_exception = lambda *args: self.errors.append(str(args))
        self.ui = app.RemoteDeskLinuxApp(self.root)
        self.ui.relay_options = relay.RelayOptions("127.0.0.1", 12345, TOKEN, "ab" * 32, ID).validate()
        self.ui._save_host_preferences = mock.Mock()
        self.root.update()
        self.addCleanup(self.cleanup)

    def cleanup(self):
        self.ui.host_process = None
        self.ui.close()
        try:
            for after in self.root.tk.call("after", "info"):
                self.root.after_cancel(after)
            self.root.destroy()
        except app.tk.TclError:
            pass
        self.environment.stop()
        self.scan.stop()
        self.temporary.cleanup()
        self.ui = self.root = None
        gc.collect()

    def target(self, services, ssh=True):
        target = dict(deviceId=ID, machineName="Owned target", platform="Linux", busy=False,
                      services=services, sshTunneling=ssh, directAddresses=[], directPort=0)
        self.ui._show_relay_directory([target])
        self.ui.relay_list.selection_set(ID)
        self.ui._select_relay_key()
        self.ui.relay_password.set("owned-device-key")
        return target

    def test_pure_ssh_controls_disable_desktop_and_old_server_disables_ssh(self):
        self.target(["ssh"])
        self.assertEqual("disabled", str(self.ui.relay_connect_button.cget("state")))
        self.assertEqual("normal", str(self.ui.relay_ssh_button.cget("state")))
        self.assertEqual("normal", str(self.ui.relay_ssh_alias_button.cget("state")))
        self.assertEqual("仅 SSH", self.ui.relay_list.item(ID, "values")[1])
        with mock.patch.object(self.ui, "_start_viewer_attempt") as desktop:
            self.ui._connect_relay()
            desktop.assert_not_called()
        self.target(["desktop"], ssh=False)
        self.assertEqual("normal", str(self.ui.relay_connect_button.cget("state")))
        self.assertEqual("disabled", str(self.ui.relay_ssh_button.cget("state")))
        self.assertEqual("disabled", str(self.ui.relay_ssh_alias_button.cget("state")))

    def test_ssh_only_registration_and_desktop_toggle_reuse_connector(self):
        with mock.patch.object(relay, "RelayHostConnector") as factory:
            self.ui.host_password.set("owned-device-key")
            self.ui.host_ssh_enabled.set(True)
            self.ui._toggle_ssh_host()
            connector = factory.return_value
            connector.options = self.ui.relay_options
            self.assertFalse(factory.call_args.kwargs["desktop_enabled"])
            self.assertTrue(factory.call_args.kwargs["ssh_enabled"])
            self.assertIsNone(self.ui.host_process)
            self.ui.host_process = mock.Mock(poll=mock.Mock(return_value=None))
            self.ui.host_running_port = 56789
            self.ui._sync_relay_registration()
            self.assertTrue(connector.configure_services.call_args.kwargs["desktop_enabled"])
            self.ui.host_stopping_generation = 1
            self.ui._sync_relay_registration()
            self.assertFalse(connector.configure_services.call_args.kwargs["desktop_enabled"])
            self.assertTrue(connector.configure_services.call_args.kwargs["ssh_enabled"])
            factory.assert_called_once()
            connector.close.assert_not_called()

    def test_invalid_port_or_empty_device_key_revokes_ssh_immediately(self):
        connector = mock.Mock(options=self.ui.relay_options)
        self.ui.relay_host = connector
        self.ui.host_ssh_enabled.set(True)
        self.ui.host_password.set("owned-device-key")
        self.ui.host_ssh_port.set("not-a-port")
        connector.close.assert_called_once_with(wait=False)
        self.assertIsNone(self.ui.relay_host)

    def test_oversized_utf8_key_revokes_ssh_without_stopping_desktop_and_reports_error(self):
        connector = mock.Mock(options=self.ui.relay_options)
        self.ui.relay_host = connector
        self.ui.host_process = process = mock.Mock(poll=mock.Mock(return_value=None))
        self.ui.host_running_port = 56789
        self.ui.host_ssh_enabled.set(True)
        self.ui.host_password.set("owned-device-key")
        connector.configure_services.reset_mock()
        self.ui.host_password.set("密" * 5500)  # 5500 chars, but 16500 UTF-8 bytes.
        connector.configure_services.assert_called_once_with(desktop_enabled=True, ssh_enabled=False, ssh_port=22, device_key="")
        self.assertIn("过长", self.ui.host_status.cget("text"))
        process.terminate.assert_not_called()
        with mock.patch.object(app.messagebox, "showerror") as error:
            self.ui._toggle_ssh_host()
        self.assertFalse(self.ui.host_ssh_enabled.get())
        self.assertIn("16384", error.call_args.args[1])
        self.assertEqual([], self.errors)

    def test_modal_username_does_not_open_stale_relay_connection(self):
        self.target(["ssh"])
        def cancel_configuration(*args, **kwargs):
            self.ui.relay_options = None
            return "owned", ""
        with mock.patch.object(self.ui, "_ask_ssh_login", side_effect=cancel_configuration), \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as session:
            self.ui._open_relay_ssh()
            session.assert_not_called()

    def test_native_terminal_is_independent_of_view_only_and_window_close(self):
        self.target(["ssh"])
        self.ui.viewer_read_only = True
        desktop = self.ui.viewer = mock.Mock()
        with mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as factory:
            self.ui._open_relay_ssh()
            factory.return_value.start.assert_called_once()
            self.assertEqual(1, len(self.ui.ssh_sessions))
            _session, window, label = next(iter(self.ui.ssh_sessions.values()))
            self.root.update()
            window.tk.call(window.protocol("WM_DELETE_WINDOW"))
            factory.return_value.close.assert_not_called()
            self.assertEqual("withdrawn", window.state())
            self.assertIs(self.ui.viewer, desktop)
            desktop.close.assert_not_called()
            self.assertTrue(self.ui.viewer_read_only)
            self.assertEqual(1, len(self.ui.ssh_sessions))
            for widget in window.winfo_children()[0].winfo_children():
                if isinstance(widget, app.ttk.Button) and "断开 SSH" in widget.cget("text"):
                    widget.invoke()
                    break
            factory.return_value.close.assert_called_once()
            self.assertEqual({}, self.ui.ssh_sessions)
            self.assertEqual([], self.errors)
        self.ui.viewer = None

    def test_real_username_dialog_and_host_port_remain_reachable_at_server_dpi(self):
        self.target(["ssh"])
        self.root.geometry("640x480+0+0")
        self.root.minsize(1, 1)
        self.root.update()
        output = os.environ.get("REMOTEDESK_SSH_UI_OUTPUT")
        def descendants(widget):
            for child in widget.winfo_children():
                yield child
                yield from descendants(child)
        port = next(widget for widget in descendants(self.root)
                    if isinstance(widget, app.ttk.Entry) and str(widget.cget("textvariable")) == str(self.ui.host_ssh_port))
        port.event_generate("<FocusIn>")
        self.root.update()
        self.assertGreaterEqual(port.winfo_rootx(), 0)
        self.assertLessEqual(port.winfo_rootx() + port.winfo_width(), self.root.winfo_screenwidth())
        self.assertGreaterEqual(port.winfo_rooty(), 0)
        self.assertLessEqual(port.winfo_rooty() + port.winfo_height(), self.root.winfo_height())
        if output:
            from PIL import ImageGrab
            Path(output).mkdir(parents=True, exist_ok=True)
            ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(Path(output) / "host-ssh.png")
        measured = []
        def inspect_and_cancel():
            dialog = next(widget for widget in self.root.winfo_children() if isinstance(widget, app.tk.Toplevel))
            entry = next(widget for widget in descendants(dialog) if isinstance(widget, app.ttk.Entry))
            measured.append((dialog.winfo_width(), dialog.winfo_height(), dialog.winfo_rootx(), dialog.winfo_rooty(),
                             entry.winfo_width(), entry.winfo_reqwidth()))
            if output:
                from PIL import ImageGrab
                ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(Path(output) / "ssh-username.png")
            next(widget for widget in descendants(dialog) if isinstance(widget, app.ttk.Button)
                 and widget.cget("text") == "取消").invoke()
        self.root.after(100, inspect_and_cancel)
        self.ui._open_relay_ssh()
        self.assertEqual(1, len(measured))
        width, height, left, top, entry_width, entry_requested = measured[0]
        self.assertGreaterEqual(left, 0)
        self.assertGreaterEqual(top, 0)
        self.assertLessEqual(left + width, self.root.winfo_screenwidth())
        self.assertLessEqual(top + height, self.root.winfo_screenheight())
        self.assertGreaterEqual(entry_width, entry_requested)
        self.assertEqual([], self.errors)

    def test_explicit_settings_can_change_username_and_remember_only_identity_path(self):
        self.target(["ssh"])
        app.ssh_terminal.save_username(self.ui.relay_options, "remembered")
        with mock.patch.object(self.ui, "_ask_ssh_login", return_value=("changed", "/tmp/owned key")) as dialog, \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as factory:
            self.ui._open_relay_ssh(configure=True)
            dialog.assert_called_once_with("remembered", identity_file="")
            self.assertEqual("/tmp/owned key", factory.call_args.kwargs["identity_file"])
            self.assertEqual("changed", app.ssh_terminal.load_username(self.ui.relay_options))
            records = Path(self.temporary.name) / "remotedesk/ssh/logins.json"
            self.assertIn("/tmp/owned key", records.read_text())
            self.assertNotIn("owned-device-key", records.read_text())

    def test_one_click_uses_saved_login_and_recalls_existing_session_without_new_connection(self):
        self.target(["ssh"])
        app.ssh_terminal.save_username(self.ui.relay_options, "remembered")
        with mock.patch.object(self.ui, "_ask_ssh_login") as dialog, \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as factory:
            factory.return_value.done.is_set.return_value = False
            factory.return_value.options = self.ui.relay_options
            self.ui._open_relay_ssh()
            dialog.assert_not_called()
            factory.assert_called_once()
            self.assertEqual("remembered", factory.call_args.args[2])
            self.ui._open_relay_ssh()
            factory.assert_called_once()
            factory.return_value.activate.assert_called_once()
            factory.return_value.close.assert_not_called()

    def test_repeated_reconnect_does_not_accumulate_finished_hidden_status_windows(self):
        self.target(["ssh"])
        app.ssh_terminal.save_username(self.ui.relay_options, "remembered")
        with mock.patch.object(app.ssh_terminal, "TerminalSession") as factory:
            for _ in range(20):
                session = mock.Mock()
                session.done.is_set.return_value = False
                session.options = self.ui.relay_options
                factory.return_value = session
                old_windows = [row[1] for row in self.ui.ssh_sessions.values()]
                self.ui._open_relay_ssh()
                self.assertEqual(1, len(self.ui.ssh_sessions))
                self.assertTrue(all(not window.winfo_exists() for window in old_windows))
                current = next(iter(self.ui.ssh_sessions.values()))[1]
                current.withdraw()
                session.done.is_set.return_value = True
            self.assertEqual([], self.errors)

    def test_long_ssh_status_keeps_actions_visible_in_small_large_font_window(self):
        self.target(["ssh"])
        style = app.ttk.Style(self.root)
        original_font = style.lookup(".", "font")
        self.addCleanup(lambda: style.configure(".", font=original_font))
        style.configure(".", font=("DejaVu Sans", 20))
        with mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.ssh_terminal, "TerminalSession"):
            self.ui._open_relay_ssh()
            _, window, label = next(iter(self.ui.ssh_sessions.values()))
            window.minsize(1, 1)
            window.geometry("360x220+0+0")
            label.configure(text="SSH 无法连接，请检查目标服务端和网络。" * 100)
            self.root.update()
            def descendants(widget):
                for child in widget.winfo_children():
                    yield child
                    yield from descendants(child)
            buttons = [w for w in descendants(window) if isinstance(w, app.ttk.Button)]
            self.assertEqual(2, len(buttons))
            for button in buttons:
                self.assertTrue(button.winfo_ismapped(), button.cget("text"))
                self.assertGreaterEqual(button.winfo_width(), button.winfo_reqwidth())
                self.assertGreaterEqual(button.winfo_rooty(), window.winfo_rooty())
                self.assertLessEqual(button.winfo_rooty() + button.winfo_height(),
                                     window.winfo_rooty() + window.winfo_height())
            canvas = next(w for w in descendants(window) if isinstance(w, app.tk.Canvas))
            canvas.yview_moveto(1)
            self.root.update()
            self.assertGreater(canvas.yview()[0], 0)
            self.assertAlmostEqual(1, canvas.yview()[1])
            if os.environ.get("REMOTEDESK_SSH_UI_OUTPUT"):
                from PIL import ImageGrab
                output = Path(os.environ["REMOTEDESK_SSH_UI_OUTPUT"])
                output.mkdir(parents=True, exist_ok=True)
                ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "ssh-long-status.png")
            self.assertEqual([], self.errors)

    def test_missing_saved_identity_requires_settings_instead_of_silent_different_login(self):
        self.target(["ssh"])
        app.ssh_terminal.save_username(self.ui.relay_options, "remembered", identity_file="/missing/owned/key")
        with mock.patch.object(self.ui, "_ask_ssh_login", return_value=None) as dialog, \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as factory:
            self.ui._open_relay_ssh()
            dialog.assert_called_once_with("remembered", identity_file="/missing/owned/key")
            factory.assert_not_called()

    def test_username_save_failure_is_explicit_but_does_not_close_launched_terminal(self):
        self.target(["ssh"])
        with mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.ssh_terminal, "TerminalSession") as factory, \
                mock.patch.object(app.ssh_terminal, "save_username", side_effect=OSError("synthetic disk error")), \
                mock.patch.object(app.messagebox, "showwarning") as warning:
            self.ui._open_relay_ssh()
            factory.return_value.start.assert_called_once()
            factory.return_value.close.assert_not_called()
            self.assertEqual("SSH 用户名未保存", warning.call_args.args[0])

    def test_alias_creation_requires_confirmation_and_canonicalizes_displayed_command(self):
        self.target(["ssh"])
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="My5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "/owned/key")), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=True) as confirm, \
                mock.patch.object(app.ssh_terminal, "GateVerification") as verification, \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias", return_value=dict(backup="/owned/backup")) as configure:
            self.ui._configure_terminal_ssh_alias()
            configure.assert_not_called()
            verification.return_value.start.assert_called_once()
            self.ui._finish_ssh_alias_verification((self.ui.ssh_alias_pending["token"], True))
        configure.assert_called_once_with("my5090", "owned", self.ui.relay_options, "owned-device-key", identity_file="/owned/key")
        self.assertIn("ssh my5090", confirm.call_args.args[1])
        self.assertIn("ssh my5090", self.ui.relay_status.cget("text"))

    def test_alias_modal_logout_or_rejected_confirmation_performs_no_writes(self):
        self.target(["ssh"])
        original = self.ui.relay_options
        def logout(*args, **kwargs):
            self.ui.relay_options = None
            return True
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.messagebox, "askokcancel", side_effect=logout), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._configure_terminal_ssh_alias()
            configure.assert_not_called()
        self.ui.relay_options = original
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=False), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._configure_terminal_ssh_alias()
            configure.assert_not_called()

    def test_alias_conflict_has_clear_error_and_no_success_status(self):
        self.target(["ssh"])
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=True), \
                mock.patch.object(app.messagebox, "showerror") as error, \
                mock.patch.object(app.ssh_terminal, "GateVerification"), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias", side_effect=ValueError("现有 Host 规则冲突")):
            self.ui._configure_terminal_ssh_alias()
            self.ui._finish_ssh_alias_verification((self.ui.ssh_alias_pending["token"], True))
        self.assertIn("Host 规则冲突", error.call_args.args[1])
        self.assertNotIn("已配置", self.ui.relay_status.cget("text"))

    def test_alias_removal_is_explicit_and_does_not_require_relay_login(self):
        self.ui.relay_options = None
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=True), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._remove_terminal_ssh_alias()
        configure.assert_called_once_with("5090", "", remove=True)
        self.assertIn("已移除", self.ui.relay_status.cget("text"))

    def test_alias_remove_dialog_close_race_does_not_write(self):
        def closing(*args, **kwargs):
            self.ui.closing = True
            return True
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(app.messagebox, "askokcancel", side_effect=closing), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._remove_terminal_ssh_alias()
            configure.assert_not_called()
        self.ui.closing = False

    def test_alias_failed_gate_never_overwrites_existing_device_key_or_config(self):
        self.target(["ssh"])
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=True), \
                mock.patch.object(app.messagebox, "showerror") as error, \
                mock.patch.object(app.ssh_terminal, "GateVerification"), \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._configure_terminal_ssh_alias()
            pending = self.ui.ssh_alias_pending
            self.ui._finish_ssh_alias_verification((pending["token"], False))
            configure.assert_not_called()
        self.assertIn("原设备密钥", error.call_args.args[1])
        self.assertEqual("", pending["key"])

    def test_alias_cancel_and_late_gate_success_or_relogin_generation_never_saves(self):
        self.target(["ssh"])
        with mock.patch.object(self.ui, "_ask_terminal_alias", return_value="5090"), \
                mock.patch.object(self.ui, "_ask_ssh_login", return_value=("owned", "")), \
                mock.patch.object(app.messagebox, "askokcancel", return_value=True), \
                mock.patch.object(app.ssh_terminal, "GateVerification") as verification, \
                mock.patch.object(app.ssh_terminal, "configure_terminal_alias") as configure:
            self.ui._configure_terminal_ssh_alias()
            previous = self.ui.ssh_alias_pending
            self.ui._configure_terminal_ssh_alias()  # Same button is now cancel.
            verification.return_value.close.assert_called_once()
            self.assertEqual("", previous["key"])
            self.ui._finish_ssh_alias_verification((previous["token"], True))
            self.ui._configure_terminal_ssh_alias()
            self.ui.relay_setup_generation += 1  # Same options after a new login.
            self.ui._finish_ssh_alias_verification((self.ui.ssh_alias_pending["token"], True))
            configure.assert_not_called()

    def test_real_alias_dialog_fits_small_window_at_server_dpi_and_controls_are_reachable(self):
        self.root.geometry("640x480+0+0"); self.root.minsize(1, 1); self.root.update()
        measured = []
        def inspect():
            dialog = next(widget for widget in self.root.winfo_children() if isinstance(widget, app.tk.Toplevel))
            measured.append((dialog.winfo_rootx(), dialog.winfo_rooty(), dialog.winfo_width(), dialog.winfo_height()))
            buttons = [widget for frame in dialog.winfo_children() for widget in frame.winfo_children()
                       if isinstance(widget, app.ttk.Button)]
            for button in buttons:
                self.assertGreaterEqual(button.winfo_width(), button.winfo_reqwidth())
                self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), dialog.winfo_rooty() + dialog.winfo_height())
            output = os.environ.get("REMOTEDESK_SSH_UI_OUTPUT")
            if output:
                from PIL import ImageGrab
                Path(output).mkdir(parents=True, exist_ok=True)
                ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(Path(output) / "ssh-alias.png")
            next(button for button in buttons if button.cget("text") == "取消").invoke()
        self.root.after(150, inspect)
        self.assertIsNone(self.ui._ask_terminal_alias(initial="5090"))
        x, y, width, height = measured[0]
        self.assertGreaterEqual(x, 0); self.assertGreaterEqual(y, 0)
        self.assertLessEqual(x + width, self.root.winfo_screenwidth())
        self.assertLessEqual(y + height, self.root.winfo_screenheight())
        self.assertEqual([], self.errors)


if __name__ == "__main__":
    unittest.main()
