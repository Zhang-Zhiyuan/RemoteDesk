import hashlib
from pathlib import Path
import queue
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_host as host
import remotedesk_protocol_probe as wire


class FileReceiptTests(unittest.TestCase):
    def test_receipt_wire(self):
        message = wire.decode_control(wire.encode_file_transfer_receipt("fixture", False, "空间不足😀"))
        self.assertEqual(35, message["kind"])
        self.assertEqual("fixture", message["transferId"])
        self.assertFalse(message["success"])
        self.assertEqual("空间不足😀", message["statusMessage"])

    def test_linux_viewer_waits_for_matching_receipt(self):
        for outcome in ("saved", "failed", "timeout", "disconnect", "legacy"):
            with self.subTest(outcome=outcome), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "中文😀.bin"
                path.write_bytes(bytes(range(256)) * 260)
                viewer = app.ViewerConnection("127.0.0.1", 1, "fixture", queue.Queue(), 1)
                viewer.remote_capabilities = wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_CANCEL
                if outcome != "legacy": viewer.remote_capabilities |= wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                viewer.file_receipt_timeout = .2
                completed = threading.Event()
                sent = []
                errors = []
                transfer = []
                def write(payload):
                    message = wire.decode_control(payload)
                    sent.append(message)
                    if message["kind"] == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                        transfer.append(message["transferId"])
                        viewer._handle_control(wire.encode_file_transfer_receipt("wrong-id", True, "wrong file"))
                        completed.set()
                viewer._write_file_control = write
                def run():
                    try: viewer._send_file_to_remote(path, path.name, path.name)
                    except Exception as error: errors.append(error)
                worker = threading.Thread(target=run); worker.start()
                self.assertTrue(completed.wait(2))
                if outcome != "legacy": self.assertTrue(worker.is_alive())
                if outcome in ("saved", "failed"):
                    viewer._handle_control(wire.encode_file_transfer_receipt(transfer[0], outcome == "saved", "fixture disk result"))
                elif outcome == "disconnect": viewer.stop_event.set()
                worker.join(3)
                self.assertFalse(worker.is_alive())
                self.assertEqual(outcome not in ("saved", "legacy"), bool(errors))
                checksum = next(m for m in sent if m["kind"] == wire.CONTROL_FILE_TRANSFER_CHECKSUM)
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), checksum["checksumHex"])

    def test_linux_host_only_receipts_after_valid_file_publication(self):
        for valid in (True, False):
            with self.subTest(valid=valid), tempfile.TemporaryDirectory() as directory:
                session = host.LinuxHostSession.__new__(host.LinuxHostSession)
                session.viewer_capabilities = wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                session.receive_dir = Path(directory)
                session.incoming = None
                messages = []
                session._write_control = lambda payload: messages.append(wire.decode_control(payload))
                session._safe_status = lambda *_: None
                data = "中文😀\r\n".encode()
                session._handle_file_transfer_start(wire.encode_file_transfer_start("id", "test.txt", len(data)))
                session._handle_file_transfer_chunk(wire.encode_file_transfer_chunk("id", 0, data))
                session._handle_file_transfer_checksum(wire.encode_file_transfer_checksum("id", hashlib.sha256(data if valid else b"wrong").hexdigest()))
                self.assertEqual([], messages)
                session._handle_file_transfer_complete(wire.encode_file_transfer_complete("id"))
                self.assertEqual(valid, messages[-1]["success"])
                self.assertEqual("id", messages[-1]["transferId"])
                self.assertEqual(valid, (Path(directory) / "test.txt").exists())

    def test_confirmation_cannot_send_to_reconnected_viewer(self):
        source = (Path(__file__).resolve().parents[1] / "scripts/linux/remotedesk_linux_app.py").read_text(encoding="utf-8")
        method = source.split("    def _finish_viewer_file_transfer_preview(", 1)[1].split("    def _show_file_transfer_confirmation_dialog", 1)[0]
        self.assertGreaterEqual(method.count("result.viewer is not self.viewer"), 2)
