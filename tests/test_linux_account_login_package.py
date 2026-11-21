import json
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
