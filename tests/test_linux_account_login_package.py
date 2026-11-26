import json
import os
from pathlib import Path
import queue
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as wire


# Shared with the Windows and Android tests. The encrypted body is a test marker.
FIXTURE = Path(__file__).parent / "data/account-login-v1.asxaccount"


class AccountLoginPackageTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires owned Xvfb")
    def test_login_action_targets_local_or_same_remote_generation(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        ui = object.__new__(app.RemoteDeskLinuxApp)
        ui.root, ui.viewer_window, ui.viewer, ui.viewer_generation, ui.closing = root, None, None, 1, False
        def codex_button():
            ui.show_account_login(parent=root)
            dialog = next(w for w in root.winfo_children() if isinstance(w, app.tk.Toplevel))
            return next(w for w in dialog.winfo_children()[0].winfo_children()
                        if isinstance(w, app.ttk.Button) and w.cget("text") == "登录 Codex")
        with mock.patch.object(app.ssh_terminal, "launch_local_login") as launch:
            codex_button().invoke()
            launch.assert_called_once_with("Codex")
            viewer = mock.Mock(remote_device_info={"platform": "Linux", "machineName": "target"},
                               read_only=False, remote_capabilities=app.CAPABILITY_CLIPBOARD_TEXT)
            ui.viewer = viewer
            stale = codex_button()
            ui.viewer_generation += 1
            stale.invoke()
            viewer.request_clipboard.assert_not_called()
            codex_button().invoke()
            viewer.request_clipboard.assert_called_once_with(read=False, text="codex login --device-auth")
            launch.assert_called_once()

    @unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires owned Xvfb")
    def test_account_action_remains_visible_in_wide_and_narrow_headers(self):
        root = app.tk.Tk()
        self.addCleanup(root.destroy)
        ui = object.__new__(app.RemoteDeskLinuxApp)
        ui.root = root
        ui._build_header(root)
        header, = root.winfo_children()
        button = header.children["account_package"]
        for width, row in ((1000, 0), (320, 2), (1000, 0)):
            with self.subTest(width=width):
                root.geometry(f"{width}x240")
                root.update()
                self.assertTrue(button.winfo_ismapped())
                self.assertEqual(row, int(button.grid_info()["row"]))
                self.assertGreaterEqual(button.winfo_x(), 0)
                self.assertLessEqual(button.winfo_x() + button.winfo_width(), header.winfo_width())
                self.assertLessEqual(button.winfo_y() + button.winfo_height(), header.winfo_height())

    def test_header_explains_export_before_connecting_and_limits_the_picker_to_a_ready_session(self):
        ui = object.__new__(app.RemoteDeskLinuxApp)
        ui.root = mock.Mock()
        ui.viewer_window = None
        ui.viewer_generation = 1
        ui.viewer_file_preview_active = False
        with mock.patch.object(app.messagebox, "showinfo") as info, mock.patch.object(app.filedialog, "askopenfilename", return_value="") as pick:
            ui.viewer = None
            ui.send_viewer_account_package(parent=ui.root)
            info.assert_called_once_with(app.ACCOUNT_PACKAGE_TITLE, app.ACCOUNT_PACKAGE_GUIDE, parent=ui.root)
            ui.viewer = mock.Mock(remote_capabilities=0)
            ui.send_viewer_account_package(parent=ui.root)
            pick.assert_not_called()
            ui.viewer.remote_capabilities = app.CAPABILITY_FILE_RECEIVE
            ui.send_viewer_account_package(parent=ui.root)
            pick.assert_called_once()

    def test_plain_profiles_and_damaged_exports_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "示例.asxaccount"
            plain = {"format": "api-switcher-account-login", "version": 2, "cipher": {"name": "none"}}
            path.write_text(json.dumps(plain), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "没有设置迁移密码"):
                app.read_account_package(path)
            for field, value in (("version", 2), ("format", "api-switcher-profile"),
                                 ("cipher", {"name": "none"}), ("payload", "!")):
                with self.subTest(field=field):
                    data = json.loads(FIXTURE.read_bytes())
                    data[field] = value
                    path.write_text(json.dumps(data), encoding="utf-8")
                    with self.assertRaises(ValueError):
                        app.read_account_package(path)
            path.write_bytes(b" " * (app.ACCOUNT_PACKAGE_LIMIT + 1))
            with self.assertRaises(ValueError):
                app.read_account_package(path)

    def test_confirmed_preview_sends_original_bytes_through_file_sender(self):
        events = queue.Queue()
        viewer = app.ViewerConnection("127.0.0.1", 56565, "fixture", events, 1)
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer.remote_capabilities = wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_RECEIPT
        expected = FIXTURE.read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "示例.asxaccount"
            path.write_bytes(expected)
            with mock.patch.object(app.ViewerConnection, "get_file_receive_location", return_value=("/test/接收", "")):
                app.RemoteDeskLinuxApp._prepare_viewer_file_transfer_preview(events, [str(path)], "确认", "",
                    "已加入队列", viewer, 1, 9, threading.Event(), account_package=True)
            event, (generation, preview) = events.get_nowait()
            self.assertEqual(("viewer_file_preview_ready", 1), (event, generation))
            self.assertEqual("", preview.error)
            self.assertEqual(len(expected), preview.items[0].size_bytes)
            self.assertIn(path.name, preview.items[0].destination_path)
            path.unlink()
            received = bytearray()

            def accept(payload):
                message = wire.decode_control(payload)
                if message["kind"] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                    self.assertEqual(len(received), message["fileOffset"])
                    received.extend(payload[-message["fileBytes"]:])
                if message["kind"] == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                    viewer._handle_control(wire.encode_file_transfer_receipt(message["transferId"], True, "/test/接收/" + path.name))

            viewer._write_file_control = accept
            receipt = viewer._send_file_to_remote(path, path.name, path.name, account_package=preview.account_package)
            self.assertIn("/test/接收/", receipt)
            self.assertIn(app.ACCOUNT_IMPORT_HINT, receipt)
            self.assertEqual(expected, received)
            viewer.remote_capabilities = 0
            viewer._write_file_control = lambda payload: None
            unconfirmed = viewer._send_file_to_remote(path, path.name, path.name, account_package=preview.account_package)
            self.assertIn("旧版远端", unconfirmed)
            self.assertNotIn(app.ACCOUNT_IMPORT_HINT, unconfirmed)

    def test_cancel_or_reconnect_during_confirmation_sends_nothing(self):
        for action in ("cancel", "reconnect", "confirm"):
            with self.subTest(action=action):
                viewer = mock.Mock(generation=1)
                ui = mock.Mock(viewer=viewer, viewer_generation=1, closing=False, viewer_window=None,
                    viewer_file_preview_active=True, viewer_file_preview_token=7, viewer_file_preview_cancel_event=threading.Event())
                snapshot = FIXTURE.read_bytes()
                item = app.FileTransferPreviewItem("加密账号登录包", str(FIXTURE), FIXTURE.name, len(snapshot), "/fixture/received")
                preview = app.ViewerFilePreviewResult(7, viewer, (str(FIXTURE),), (item,), "", "确认", "", "", "", snapshot)

                def decide(*_):
                    if action == "reconnect":
                        ui.viewer_generation += 1
                    return action != "cancel"

                ui._show_file_transfer_confirmation_dialog.side_effect = decide
                app.RemoteDeskLinuxApp._finish_viewer_file_transfer_preview(ui, preview)
                if action == "confirm":
                    viewer.send_files.assert_called_once_with([str(FIXTURE)], account_package=snapshot)
                else:
                    viewer.send_files.assert_not_called()
