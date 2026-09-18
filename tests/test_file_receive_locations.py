from pathlib import Path
import queue
import os
import gc
import sys
import tempfile
import threading
import unittest
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_host as host
import remotedesk_protocol_probe as wire


class FileReceiveLocationTests(unittest.TestCase):
    def test_wire_bounds_and_trailing_bytes(self):
        self.assertEqual(bytes.fromhex("2702696401082fe4b8adf09f988000"), wire.encode_file_receive_location("id", True, "/中😀", ""))
        for success, directory, note in [(True, "/home/中文😀/Downloads", ""), (False, "", "不可用")]:
            data = wire.encode_file_receive_location("id", success, directory, note)
            result = wire.decode_control(data)
            self.assertEqual((39, "id", success, directory, note),
                             (result["kind"], result["transferId"], result["success"], result["text"], result["statusMessage"]))
            with self.assertRaises(wire.ProtocolError): wire.decode_control(data + b"\0")
        self.assertEqual("id", wire.decode_control(wire.encode_file_receive_location_request("id"))["transferId"])
        with self.assertRaises(wire.ProtocolError): wire.encode_file_receive_location("id", True, "a" * 8193, "")

    def test_windows_and_linux_destination_on_either_platform(self):
        for directory, path in [("C:\\Downloads\\", "C:\\Downloads\\中文.txt"), ("/tmp/", "/tmp/中文.txt"), ("/", "/中文.txt")]:
            self.assertIn(path, app.format_remote_receive_destination("中文.txt", directory))
        self.assertIn("位置未确认", app.format_remote_receive_destination("x"))

    def test_preflight_correlates_reply_and_releases_pending_on_all_outcomes(self):
        for outcome in ("saved", "legacy", "wrong", "failed", "cancel"):
            with self.subTest(outcome=outcome):
                viewer = app.ViewerConnection("127.0.0.1", 1, "fixture", queue.Queue(), 1)
                viewer.remote_capabilities = 0 if outcome == "legacy" else wire.CAPABILITY_FILE_RECEIVE_LOCATION
                cancel = threading.Event()
                sent = []
                def write(payload):
                    message = wire.decode_control(payload)
                    sent.append(message)
                    self.assertEqual(38, message["kind"])
                    if outcome == "cancel": cancel.set(); return
                    viewer._handle_control(wire.encode_file_receive_location("wrong", True, "/wrong", ""))
                    if outcome != "wrong":
                        viewer._handle_control(wire.encode_file_receive_location(message["transferId"], outcome != "failed",
                                               "" if outcome == "failed" else "/fixture/中文😀", "disk error" if outcome == "failed" else ""))
                viewer._write_file_control = write
                if outcome in ("saved", "legacy"):
                    directory, note = viewer.get_file_receive_location(cancel, timeout=.02)
                    self.assertEqual("/fixture/中文😀" if outcome == "saved" else "", directory)
                    if outcome == "legacy": self.assertIn("位置未确认", note)
                else:
                    with self.assertRaises((OSError, wire.TransferCancelledError)):
                        viewer.get_file_receive_location(cancel, timeout=.02)
                self.assertEqual({}, viewer.file_location_requests)
                self.assertEqual(0 if outcome == "legacy" else 1, len(sent))

    def test_host_reports_configured_location_without_creating_it(self):
        with tempfile.TemporaryDirectory() as directory:
            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.receive_dir = Path(directory) / "not-created"
            session.args = SimpleNamespace()
            received = []
            session._write_control = lambda payload: received.append(wire.decode_control(payload))
            session._handle_message(wire.MESSAGE_CONTROL, wire.encode_file_receive_location_request("fixture"))
            self.assertEqual(str(session.receive_dir.resolve()), received[0]["text"])
            self.assertTrue(received[0]["success"])
            self.assertFalse(session.receive_dir.exists())

    def test_batch_results_keep_all_receipts_and_failure(self):
        viewer = app.ViewerConnection("127.0.0.1", 1, "fixture", queue.Queue(), 1)
        viewer.remote_capabilities = wire.CAPABILITY_FILE_TRANSFER_RECEIPT
        viewer._prepare_transfer_path = lambda path: (path, path.name, path.name, None)
        def send(path, name, display):
            if name == "bad": raise OSError("disk full")
            return "实际路径：/receive/" + name + " (1)"
        viewer._send_file_to_remote = send
        events = []
        viewer._put_event = lambda kind, value: events.append((kind, value))
        viewer.file_transfer_lock.acquire()
        viewer._send_files_locked(["one", "bad", "two"])
        detail = next(value for kind, value in events if kind == "viewer_file_results")
        self.assertIn("/receive/one (1)", detail)
        self.assertIn("/receive/two (1)", detail)
        self.assertIn("未完成：bad", detail)
        self.assertIn("disk full", detail)
        self.assertFalse(viewer.file_transfer_lock.locked())


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "Owned Tk display required")
class FileTransferDialogTkTests(unittest.TestCase):
    def setUp(self):
        self.root = app.tk.Tk()
        self.root.geometry("800x600")
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.root = self.root
        self.ui.viewer_window = None
        self.ui.window_icon = None
        self.ui._configure_style()

    def tearDown(self):
        self.root.destroy()
        self.ui = self.root = None
        gc.collect()

    def descendants(self, widget):
        for child in widget.winfo_children():
            yield child
            yield from self.descendants(child)

    def capture(self, dialog, name):
        directory = os.environ.get("REMOTEDESK_FILE_UI_EVIDENCE")
        if directory:
            from PIL import ImageGrab
            Path(directory).mkdir(parents=True, exist_ok=True)
            dialog.update_idletasks()
            x, y = dialog.winfo_rootx(), dialog.winfo_rooty()
            ImageGrab.grab(bbox=(x, y, x + dialog.winfo_width(), y + dialog.winfo_height())).save(Path(directory) / name)

    def test_real_confirmation_shows_full_paths_and_both_choices(self):
        destination = "/home/fixture/Downloads/RemoteDeskReceived/中文报告😀.txt"
        item = app.FileTransferPreviewItem("文件", "/home/source/中文报告😀.txt", "中文报告😀.txt", 1024, destination)
        for accept in (False, True):
            errors = []
            def inspect():
                dialog = next(c for c in self.root.winfo_children() if isinstance(c, app.tk.Toplevel))
                try:
                    text = next(c for c in self.descendants(dialog) if isinstance(c, app.tk.Text))
                    self.assertIn(destination, text.get("1.0", app.tk.END))
                    button = next(c for c in self.descendants(dialog) if isinstance(c, app.ttk.Button) and c.cget("text") == ("开始传输" if accept else "取消"))
                    self.assertTrue(button.winfo_viewable())
                    self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), dialog.winfo_rooty() + dialog.winfo_height())
                    self.capture(dialog, "linux-confirm.png")
                    button.invoke()
                except Exception as error:
                    errors.append(error)
                    dialog.destroy()
            self.root.after(80, inspect)
            result = self.ui._show_file_transfer_confirmation_dialog(self.root, "确认发送文件", "确认后才会开始传输。", [item], "接收设备：fixture；重名自动改名，不覆盖已有文件。")
            self.assertEqual([], errors)
            self.assertEqual(accept, result)

    def test_real_results_preserve_actual_renamed_paths_and_failure(self):
        details = "已发送：中文报告.txt\n文件已保存：/home/fixture/Downloads/中文报告 (1).txt\n\n未完成：second.txt\ndisk full"
        self.ui._show_file_transfer_results(details)
        self.root.update()
        dialog = next(c for c in self.root.winfo_children() if isinstance(c, app.tk.Toplevel))
        text = next(c for c in self.descendants(dialog) if isinstance(c, app.tk.Text))
        self.assertEqual(details, text.get("1.0", "end-1c"))
        self.assertEqual("disabled", text.cget("state"))
        self.capture(dialog, "linux-result.png")
