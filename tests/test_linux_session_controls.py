"""Owned transport/Tk fixtures for local input pause and cancellable file tasks."""
import ast
import gc
import hashlib
import io
import os
from pathlib import Path
import queue
import re
import socket
import struct
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
import zipfile
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as wire
from remotedesk_linux_transfers import TransferTaskPanel


class SessionControlTests(unittest.TestCase):
    def viewer(self):
        viewer = app.ViewerConnection("192.0.2.1", 56565, "fixture", queue.Queue(), 1)
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer.remote_capabilities = (wire.CAPABILITY_INPUT_CONTROL | wire.CAPABILITY_CLIPBOARD_TEXT
            | wire.CAPABILITY_FILE_RECEIVE | wire.CAPABILITY_FILE_SEND | wire.CAPABILITY_FILE_TRANSFER_PREVIEW
            | wire.CAPABILITY_FILE_TRANSFER_CANCEL | wire.CAPABILITY_FILE_CHECKSUM)
        self.addCleanup(self.close_viewer, viewer)
        return viewer

    @staticmethod
    def close_viewer(viewer):
        viewer.stop_event.set()
        viewer.transfer_tasks.close()
        with viewer.input_condition:
            viewer.input_condition.notify_all()
        for worker in (viewer.input_thread, viewer.transfer_tasks.worker):
            if worker is not None and worker.is_alive():
                worker.join(2)

    def wait(self, predicate, timeout=2):
        deadline = time.monotonic() + timeout
        while not predicate() and time.monotonic() < deadline:
            time.sleep(.005)
        self.assertTrue(predicate())

    def test_pause_releases_actual_keys_and_drag_and_blocks_all_input_routes(self):
        viewer = self.viewer()
        writes = []
        with mock.patch.object(app, "write_message", side_effect=lambda _s, _a, kind, payload: writes.append((kind, payload))):
            viewer.input_thread.start()
            viewer.send_input(app.INPUT_KEY_DOWN, data=0xA3)
            viewer.send_input(app.INPUT_MOUSE_DOWN, app.MOUSE_LEFT, 50, 60)
            self.assertTrue(viewer.flush_pending_inputs(1))
            viewer.set_read_only(True)
            self.assertTrue(viewer.flush_pending_inputs(1))
            decoded = [struct.unpack("<BBiii", payload) for kind, payload in writes if kind == wire.MESSAGE_INPUT]
            self.assertEqual([app.INPUT_KEY_DOWN, app.INPUT_MOUSE_DOWN, app.INPUT_MOUSE_UP, app.INPUT_KEY_UP], [row[0] for row in decoded])
            self.assertEqual((app.MOUSE_LEFT, 50, 60), decoded[2][1:4])
            self.assertEqual(0xA3, decoded[-1][-1])
            self.assertFalse(viewer.send_input(app.INPUT_MOUSE_MOVE, x=2, y=3))
            self.assertEqual(0, viewer.send_text("中文\ntext"))
            self.assertFalse(viewer.send_android_navigation(0x1B))
            self.assertFalse(viewer.request_clipboard(read=False, text="private", paste=True))
            self.assertFalse(viewer._push_auto_clipboard(app.LocalClipboardSnapshot.from_text("automatic"), viewer.clipboard_manual_epoch))
            self.assertEqual(4, len(writes))

    def test_pause_drops_input_waiting_behind_write_lock(self):
        viewer = self.viewer()
        with mock.patch.object(app, "write_message") as write:
            with viewer.write_lock:
                viewer.input_thread.start()
                viewer.send_input(app.INPUT_KEY_DOWN, data=0x41)
                self.wait(lambda: viewer.input_send_active)
                viewer.set_read_only(True)
            self.assertTrue(viewer.flush_pending_inputs(1))
            write.assert_not_called()

    def test_pause_and_quick_resume_release_a_down_already_in_flight(self):
        viewer = self.viewer()
        entered, release = threading.Event(), threading.Event()
        writes = []
        def write(_sock, _session, kind, payload):
            writes.append(struct.unpack("<BBiii", payload))
            if writes[-1][0] == app.INPUT_KEY_DOWN:
                entered.set()
                self.assertTrue(release.wait(2))
        try:
            with mock.patch.object(app, "write_message", side_effect=write):
                viewer.input_thread.start()
                viewer.send_input(app.INPUT_KEY_DOWN, data=0x41)
                self.assertTrue(entered.wait(1))
                viewer.set_read_only(True)
                viewer.set_read_only(False)
                release.set()
                self.assertTrue(viewer.flush_pending_inputs(1))
            self.assertEqual([app.INPUT_KEY_DOWN, app.INPUT_KEY_UP], [item[0] for item in writes])
        finally:
            release.set()

    def test_old_delayed_clipboard_and_paste_do_not_return_after_resume(self):
        viewer = self.viewer()
        old_revision = viewer.clipboard_paste_revision
        old_epoch = viewer.input_epoch
        viewer.set_read_only(True)
        viewer.set_read_only(False)
        with mock.patch.object(viewer, "_send_control") as send:
            self.assertFalse(viewer.request_clipboard(read=False, text="old", paste=True,
                _paste_revision=old_revision, _input_epoch=old_epoch, _queued_deadline=time.monotonic() + 2))
            send.assert_not_called()

    def test_pending_clipboard_write_is_fenced_at_transport_after_pause(self):
        viewer = self.viewer()
        request = app.ViewerClipboardRequest(False, "old", input_epoch=viewer.input_epoch)
        finished = threading.Event()
        def exchange():
            try:
                viewer._exchange_clipboard(request, wire.encode_clipboard_set_text("old"), True, False, False)
            finally:
                finished.set()
        with viewer.write_lock, mock.patch.object(viewer, "_send_control") as send:
            worker = threading.Thread(target=exchange)
            worker.start()
            viewer.set_read_only(True)
        worker.join(2)
        self.assertTrue(finished.is_set())
        send.assert_not_called()
        self.assertFalse(viewer.pending_inputs)

    def test_capability_refresh_does_not_resume_input(self):
        viewer = self.viewer()
        viewer.set_read_only(True)
        with mock.patch.object(app, "decode_control", return_value=dict(kind=wire.CONTROL_DEVICE_INFO,
                machineName="peer", platform="Linux", capabilities=wire.CAPABILITY_INPUT_CONTROL)):
            viewer._handle_control(b"fixture")
        self.assertTrue(viewer.read_only)
        self.assertFalse(viewer.send_input(app.INPUT_KEY_DOWN, data=65))

    def test_upload_cancel_uses_current_wire_id_and_removes_partial_without_disconnect(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "source.bin"
            source.write_bytes(b"x" * (wire.RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES * 3))
            target = Path(folder) / "received"
            incoming, controls = [], []
            def write(payload):
                control = wire.decode_control(payload)
                controls.append(control)
                if control["kind"] == wire.CONTROL_FILE_TRANSFER_START:
                    incoming.append(wire.start_incoming_file_transfer(payload, target))
                elif control["kind"] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                    wire.write_incoming_file_chunk(payload, incoming[0])
                    self.assertTrue(viewer.transfer_tasks.cancel_task(viewer.transfer_tasks.upload.id)[0])
                elif control["kind"] == wire.CONTROL_FILE_TRANSFER_CANCEL:
                    wire.cancel_incoming_file_transfer(payload, incoming[0])
            viewer._write_file_control = write
            viewer.file_transfer_lock.acquire()
            viewer._send_files_locked([str(source)])
            task = viewer.transfer_tasks.upload
            self.assertEqual("已取消", task.phase)
            self.assertEqual(wire.RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES, task.completed_bytes)
            self.assertEqual(source.stat().st_size, task.total_bytes)
            self.assertEqual(controls[0]["transferId"], controls[-1]["transferId"])
            self.assertEqual(wire.CONTROL_FILE_TRANSFER_CANCEL, controls[-1]["kind"])
            self.assertFalse(viewer.stop_event.is_set())
            self.assertFalse(viewer.file_transfer_lock.locked())
            self.assertEqual([], list(target.iterdir()))

    def test_legacy_upload_disables_unsafe_cancel_and_reports_missing_save_receipt(self):
        viewer = self.viewer()
        viewer.remote_capabilities = wire.CAPABILITY_FILE_RECEIVE
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "source.bin"
            source.write_bytes(b"owned bytes")
            attempts = []
            def write(payload):
                if payload[0] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                    attempts.append(viewer.transfer_tasks.cancel_task(viewer.transfer_tasks.upload.id))
            viewer._write_file_control = write
            viewer.file_transfer_lock.acquire()
            viewer._send_files_locked([str(source)])
            self.assertEqual(1, len(attempts))
            self.assertFalse(attempts[0][0])
            self.assertIn("旧版", attempts[0][1])
            self.assertIn("未返回保存确认", viewer.transfer_tasks.upload.detail)
            self.assertFalse(viewer.stop_event.is_set())

    def test_file_frames_waiting_for_write_lock_recheck_cancel_but_cancel_frame_passes(self):
        payloads = (wire.encode_file_transfer_start("owned", "owned.bin", 1),
                    wire.encode_file_transfer_chunk("owned", 0, b"x"),
                    wire.encode_file_transfer_checksum("owned", hashlib.sha256(b"x").hexdigest()),
                    wire.encode_file_transfer_complete("owned"),
                    wire.encode_file_transfer_request_clipboard_files(),
                    wire.encode_file_transfer_confirm_clipboard_files())
        for payload in payloads:
            with self.subTest(kind=payload[0]):
                viewer = self.viewer()
                manager = viewer.transfer_tasks
                task = manager.begin_upload(["unused"])
                manager.download = task  # Owned task fixture for either wire direction.
                waiting, lock, errors = threading.Event(), threading.Lock(), []
                class ObservedLock:
                    def __enter__(self):
                        waiting.set()
                        lock.acquire()
                    def __exit__(self, *_args):
                        lock.release()
                viewer.write_lock = ObservedLock()
                def send():
                    try:
                        viewer._write_file_control(payload)
                    except Exception as error:
                        errors.append(error)
                with mock.patch.object(app, "write_message") as write:
                    with lock:
                        worker = threading.Thread(target=send)
                        worker.start()
                        self.assertTrue(waiting.wait(1))
                        self.assertTrue(manager.cancel_task(task.id)[0])
                    worker.join(1)
                    self.assertFalse(worker.is_alive())
                    self.assertEqual(1, len(errors))
                    self.assertIsInstance(errors[0], wire.TransferCancelledError)
                    write.assert_not_called()
                    cancel = wire.encode_file_transfer_cancel("owned", "cancelled")
                    viewer._write_file_control(cancel)
                    write.assert_called_once_with(viewer.sock, viewer.session, wire.MESSAGE_CONTROL, cancel)
                self.assertFalse(viewer.stop_event.is_set())

    def prepare_return(self, viewer, directory, name="owned.bin", size=5, kind="文件", items=None):
        viewer.remote_capabilities |= wire.CAPABILITY_FILE_TRANSFER_RECEIPT
        writes = []
        viewer._write_file_control = lambda payload: writes.append(wire.decode_control(payload))
        self.assertTrue(viewer.transfer_tasks.request_return(directory)[0])
        self.wait(lambda: bool(writes))
        task = viewer.transfer_tasks.download
        preview = wire.encode_file_transfer_clipboard_files_preview(items or [dict(kind=kind, sourcePath="/owned",
            transferName=name, sizeBytes=size, destinationPath="ignored")])
        viewer._handle_control(preview)
        self.wait(lambda: task.phase == "等待确认")
        self.assertEqual([], list(Path(directory).iterdir()))
        self.assertTrue(viewer.transfer_tasks.confirm_return(task.id, True))
        self.wait(lambda: any(item["kind"] == wire.CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES for item in writes))
        return task, writes

    def test_confirmed_return_saves_exact_bytes_and_real_path_even_while_read_only(self):
        viewer = self.viewer()
        viewer.set_read_only(True)
        with tempfile.TemporaryDirectory() as folder:
            task, writes = self.prepare_return(viewer, folder)
            data = b"owned"
            for payload in (wire.encode_file_transfer_start("incoming", "owned.bin", len(data)),
                    wire.encode_file_transfer_chunk("incoming", 0, data),
                    wire.encode_file_transfer_checksum("incoming", hashlib.sha256(data).hexdigest()),
                    wire.encode_file_transfer_complete("incoming"),
                    wire.encode_file_transfer_status(True, "远端文件回传完成：1 个")):
                viewer._handle_control(payload)
            self.wait(lambda: task.terminal)
            self.assertEqual(data, (Path(folder) / "owned.bin").read_bytes())
            self.assertEqual((5, 5, "已完成"), (task.completed_bytes, task.total_bytes, task.phase))
            self.assertIn(str(Path(folder) / "owned.bin"), task.detail)
            self.assertTrue(any(row["kind"] == wire.CONTROL_FILE_TRANSFER_RECEIPT and row["success"] for row in writes))
            self.assertTrue(viewer.read_only)
            self.assertFalse(viewer.pending_inputs)

    def test_return_cancel_waits_for_batch_end_and_cannot_cancel_replacement_task(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            task, writes = self.prepare_return(viewer, folder, size=10)
            viewer._handle_control(wire.encode_file_transfer_start("incoming", "owned.bin", 10))
            viewer._handle_control(wire.encode_file_transfer_chunk("incoming", 0, b"owned"))
            self.wait(lambda: task.completed_bytes == 5)
            self.assertTrue(viewer.transfer_tasks.cancel_task(task.id)[0])
            self.wait(lambda: any(row["kind"] == wire.CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES for row in writes))
            self.assertFalse(viewer.transfer_tasks.request_return(folder)[0])
            self.wait(lambda: not list(Path(folder).iterdir()))
            viewer._handle_control(wire.encode_file_transfer_status(False, "远端文件回传已取消。"))
            self.wait(lambda: task.terminal)
            self.assertEqual("已取消", task.phase)
            self.assertTrue(viewer.transfer_tasks.request_return(folder)[0])
            replacement = viewer.transfer_tasks.download
            self.assertFalse(viewer.transfer_tasks.cancel_task(task.id)[0])
            self.assertFalse(replacement.cancel.is_set())
            self.assertFalse(viewer.stop_event.is_set())

    def test_return_cancel_before_request_dequeues_needs_no_remote_drain(self):
        viewer = self.viewer()
        manager = viewer.transfer_tasks
        viewer._write_file_control = mock.Mock()
        manager.worker = mock.Mock()  # Start the owned worker only after cancel.
        with tempfile.TemporaryDirectory() as folder:
            self.assertTrue(manager.request_return(folder)[0])
            task = manager.download
            self.assertTrue(manager.cancel_task(task.id)[0])
            manager.worker = threading.Thread(target=manager._receive_loop)
            manager.worker.start()
            self.wait(lambda: task.terminal and manager.download is None)
            viewer._write_file_control.assert_not_called()
            self.assertTrue(manager.request_return(folder)[0])
            self.assertFalse(viewer.stop_event.is_set())

    def test_return_cancel_while_request_waits_for_write_lock_needs_no_remote_drain(self):
        viewer = self.viewer()
        manager = viewer.transfer_tasks
        waiting, lock = threading.Event(), threading.Lock()
        class ObservedLock:
            def __enter__(self):
                waiting.set()
                lock.acquire()
            def __exit__(self, *_args):
                lock.release()
        viewer.write_lock = ObservedLock()
        with tempfile.TemporaryDirectory() as folder, mock.patch.object(app, "write_message") as write:
            with lock:
                self.assertTrue(manager.request_return(folder)[0])
                task = manager.download
                self.assertTrue(waiting.wait(1))
                self.assertTrue(manager.cancel_task(task.id)[0])
            self.wait(lambda: task.terminal and manager.download is None)
            write.assert_not_called()
            self.assertTrue(manager.request_return(folder)[0])
            self.wait(lambda: write.called)
            self.assertEqual(wire.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES, write.call_args.args[-1][0])
            self.assertFalse(viewer.stop_event.is_set())

    def test_unconfirmed_return_never_writes_files(self):
        viewer = self.viewer()
        viewer._write_file_control = mock.Mock()
        with tempfile.TemporaryDirectory() as folder:
            viewer.transfer_tasks.request_return(folder)
            task = viewer.transfer_tasks.download
            viewer._handle_control(wire.encode_file_transfer_start("unconfirmed", "unconfirmed.bin", 5))
            self.wait(lambda: task.cancel.is_set())
            self.assertEqual([], list(Path(folder).iterdir()))
            self.assertFalse(viewer.stop_event.is_set())

    def test_return_checks_exact_preview_name_size_and_duplicate_count(self):
        for actual_name, actual_size in (("owned.bin", 6), ("../owned.bin", 5)):
            with self.subTest(name=actual_name, size=actual_size), tempfile.TemporaryDirectory() as folder:
                viewer = self.viewer()
                task, _writes = self.prepare_return(viewer, folder)
                viewer._handle_control(wire.encode_file_transfer_start("wrong", actual_name, actual_size))
                self.wait(lambda: task.cancel.is_set())
                self.assertEqual([], list(Path(folder).iterdir()))
        with tempfile.TemporaryDirectory() as folder:
            viewer = self.viewer()
            task, _writes = self.prepare_return(viewer, folder)
            self.finish_incoming(viewer, "first")
            self.wait(lambda: len(viewer.transfer_tasks.receive_paths) == 1)
            viewer._handle_control(wire.encode_file_transfer_start("duplicate", "owned.bin", 5))
            self.wait(lambda: task.cancel.is_set())
            self.assertEqual(["owned.bin"], [path.name for path in Path(folder).iterdir()])

    def test_per_file_failure_keeps_batch_fenced_through_late_remaining_files(self):
        viewer = self.viewer()
        manager = viewer.transfer_tasks
        items = [dict(kind="文件", transferName="failed.bin", sizeBytes=5),
                 dict(kind="文件", transferName="remaining.bin", sizeBytes=5)]
        with tempfile.TemporaryDirectory() as folder:
            task, _writes = self.prepare_return(viewer, folder, items=items)
            viewer._handle_control(wire.encode_file_transfer_status(False,
                "回传远端文件失败：failed.bin - 源文件不可访问"))
            self.wait(lambda: manager.receive_failed)
            self.assertFalse(task.terminal)
            self.assertIs(task, manager.download)
            self.assertFalse(manager.request_return(folder)[0])
            # Windows continues its existing batch after an individual failure.
            self.finish_incoming(viewer, "old-batch-second", name="remaining.bin")
            self.wait(lambda: len(manager.receive_paths) == 1)
            self.assertEqual(b"owned", (Path(folder) / "remaining.bin").read_bytes())
            self.assertIs(task, manager.download)
            self.assertFalse(manager.request_return(folder)[0])
            viewer._handle_control(wire.encode_file_transfer_status(False, "远端文件回传完成：1 个，1 个失败"))
            self.wait(lambda: task.terminal and manager.download is None)
            self.assertEqual("未完成", task.phase)
            self.assertTrue(manager.request_return(folder)[0])
            self.assertFalse(viewer.stop_event.is_set())

    def test_known_empty_and_request_failure_statuses_release_batch_barrier(self):
        messages = ("远端剪贴板没有可回传的文件。", "读取远端文件剪贴板失败：不可访问",
                    "Linux has no pending return files", "远端文件回传已拒绝：远程更新传输尚未结束。")
        for message in messages:
            with self.subTest(message=message), tempfile.TemporaryDirectory() as folder:
                viewer = self.viewer()
                viewer._write_file_control = mock.Mock()
                manager = viewer.transfer_tasks
                self.assertTrue(manager.request_return(folder)[0])
                task = manager.download
                self.wait(lambda: manager.receive_request_sent)
                viewer._handle_control(wire.encode_file_transfer_status(False, message))
                self.wait(lambda: task.terminal and manager.download is None)
                self.assertEqual("未完成", task.phase)
                self.assertTrue(manager.request_return(folder)[0])

    def test_folder_return_accepts_windows_unknown_size_and_linux_source_size(self):
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
            output.writestr("owned.txt", b"owned")
        data = archive.getvalue()
        for source_size in (0, 5):
            with self.subTest(source_size=source_size), tempfile.TemporaryDirectory() as folder:
                viewer = self.viewer()
                task, _writes = self.prepare_return(viewer, folder, name="owned.zip", size=source_size, kind="文件夹")
                self.finish_incoming(viewer, "archive", name="owned.zip", data=data)
                viewer._handle_control(wire.encode_file_transfer_status(True, "远端文件回传完成：1 个"))
                self.wait(lambda: task.terminal)
                self.assertEqual("已完成", task.phase)
                self.assertEqual((len(data), len(data)), (task.completed_bytes, task.total_bytes))
                with zipfile.ZipFile(Path(folder) / "owned.zip") as saved:
                    self.assertEqual(b"owned", saved.read("owned.txt"))

    def test_folder_return_enforces_windows_archive_overhead_and_protocol_limit(self):
        for source_size, claimed_size in ((0, wire.MAX_FILE_TRANSFER_BYTES + 1),
                (5, 5 + 16 * 1024 * 1024 + 1),
                (500 * 1024 * 1024, 550 * 1024 * 1024 + 1),
                (800 * 1024 * 1024, 864 * 1024 * 1024 + 1)):
            with self.subTest(source_size=source_size), tempfile.TemporaryDirectory() as folder:
                viewer = self.viewer()
                task, _writes = self.prepare_return(viewer, folder, name="owned.zip", size=source_size, kind="文件夹")
                payload = wire.encode_file_transfer_start("oversized", "owned.zip", 0)
                viewer._handle_control(payload[:-8] + struct.pack("<q", claimed_size))
                self.wait(lambda: task.cancel.is_set())
                self.assertEqual([], list(Path(folder).iterdir()))
                self.assertFalse(viewer.stop_event.is_set())

    def test_folder_preview_rejects_wrong_archive_suffix_before_confirmation(self):
        viewer = self.viewer()
        viewer._write_file_control = mock.Mock()
        with tempfile.TemporaryDirectory() as folder:
            manager = viewer.transfer_tasks
            self.assertTrue(manager.request_return(folder)[0])
            task = manager.download
            viewer._handle_control(wire.encode_file_transfer_clipboard_files_preview([
                dict(kind="文件夹", transferName="owned.exe", sizeBytes=0)]))
            self.wait(lambda: task.cancel.is_set())
            self.assertIsNone(manager.receive_expected)
            self.assertEqual([], list(Path(folder).iterdir()))
            self.assertFalse(any(event == "viewer_return_preview" for event, _payload in list(viewer.events.queue)))

    @staticmethod
    def finish_incoming(viewer, transfer_id, name="owned.bin", data=b"owned"):
        for payload in (wire.encode_file_transfer_start(transfer_id, name, len(data)),
                wire.encode_file_transfer_chunk(transfer_id, 0, data),
                wire.encode_file_transfer_checksum(transfer_id, hashlib.sha256(data).hexdigest()),
                wire.encode_file_transfer_complete(transfer_id)):
            viewer._handle_control(payload)

    def test_success_status_cannot_hide_missing_or_unfinished_confirmed_file(self):
        for started in (False, True):
            with self.subTest(started=started), tempfile.TemporaryDirectory() as folder:
                viewer = self.viewer()
                task, _writes = self.prepare_return(viewer, folder)
                # Two identically named preview entries remain two required files.
                with viewer.transfer_tasks.lock:
                    viewer.transfer_tasks.receive_expected.append(("owned.bin", "文件", 5))
                self.finish_incoming(viewer, "first")
                self.wait(lambda: len(viewer.transfer_tasks.receive_paths) == 1)
                if started:
                    viewer._handle_control(wire.encode_file_transfer_start("second", "owned.bin", 5))
                    viewer._handle_control(wire.encode_file_transfer_chunk("second", 0, b"ow"))
                viewer._handle_control(wire.encode_file_transfer_status(True, "远端文件回传完成：2 个"))
                self.wait(lambda: task.terminal)
                self.assertEqual("未完成", task.phase)
                self.assertIn("未完整收到", task.detail)
                self.assertEqual(["owned.bin"], [path.name for path in Path(folder).iterdir()])
                self.assertFalse(viewer.stop_event.is_set())

    def test_preview_cannot_change_after_confirmation(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            task, _writes = self.prepare_return(viewer, folder)
            viewer._handle_control(wire.encode_file_transfer_clipboard_files_preview([
                dict(kind="文件", transferName="changed.bin", sizeBytes=100)]))
            self.wait(lambda: task.cancel.is_set())
            self.assertEqual([], list(Path(folder).iterdir()))

    def test_return_timeout_only_removes_owned_partial_and_fences_next_batch(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            task, _writes = self.prepare_return(viewer, folder)
            original = Path(folder) / "owned.bin"
            unrelated = Path(folder) / "unrelated.partial"
            original.write_bytes(b"keep original")
            unrelated.write_bytes(b"keep unrelated")
            viewer._handle_control(wire.encode_file_transfer_start("partial", "owned.bin", 5))
            viewer._handle_control(wire.encode_file_transfer_chunk("partial", 0, b"ow"))
            self.wait(lambda: task.completed_bytes == 2)
            viewer.transfer_tasks.return_deadline = time.monotonic() - 1
            self.wait(lambda: task.terminal)
            self.assertEqual("等待远端结束超时", task.phase)
            self.assertEqual(b"keep original", original.read_bytes())
            self.assertEqual(b"keep unrelated", unrelated.read_bytes())
            self.assertEqual({original, unrelated}, set(Path(folder).iterdir()))
            self.assertFalse(viewer.transfer_tasks.request_return(folder)[0])
            self.assertFalse(viewer.stop_event.is_set())

    def test_return_collision_keeps_original_and_reports_actual_saved_path(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            task, _writes = self.prepare_return(viewer, folder)
            original = Path(folder) / "owned.bin"
            original.write_bytes(b"keep original")
            self.finish_incoming(viewer, "incoming")
            viewer._handle_control(wire.encode_file_transfer_status(True, "远端文件回传完成：1 个"))
            self.wait(lambda: task.terminal)
            self.assertEqual("已完成", task.phase)
            actual = Path(viewer.transfer_tasks.receive_paths[0])
            self.assertNotEqual(original, actual)
            self.assertEqual(b"keep original", original.read_bytes())
            self.assertEqual(b"owned", actual.read_bytes())
            self.assertIn(str(actual), task.detail)

    def test_upload_and_return_are_mutually_exclusive_in_both_directions(self):
        viewer = self.viewer()
        with tempfile.TemporaryDirectory() as folder:
            task, _writes = self.prepare_return(viewer, folder)
            with mock.patch.object(threading, "Thread") as thread:
                self.assertFalse(viewer.send_files(["unused"]))
                thread.assert_not_called()
            self.assertFalse(viewer.file_transfer_lock.locked())
            viewer._handle_control(wire.encode_file_transfer_status(False, "远端文件回传已拒绝。"))
            self.wait(lambda: task.terminal)
            viewer.transfer_tasks.begin_upload(["unused"])
            self.assertFalse(viewer.transfer_tasks.request_return(folder)[0])

    def test_idle_receive_worker_sleeps_until_close_without_periodic_polling(self):
        viewer = self.viewer()
        manager = viewer.transfer_tasks
        entered = threading.Event()
        original_get = manager.receive_queue.get
        timeouts = []
        def tracked_get(*args, **kwargs):
            timeouts.append(kwargs.get("timeout"))
            entered.set()
            return original_get(*args, **kwargs)
        with mock.patch.object(manager.receive_queue, "get", side_effect=tracked_get):
            manager.worker = threading.Thread(target=manager._receive_loop)
            manager.worker.start()
            self.assertTrue(entered.wait(1))
            manager.close()
            manager.worker.join(1)
        self.assertFalse(manager.worker.is_alive())
        self.assertEqual([None], timeouts)

    def test_receive_queue_short_backpressure_releases_manager_lock_and_keeps_batch(self):
        viewer = self.viewer()
        manager = viewer.transfer_tasks
        manager.download = manager._new("取回", "fixture")
        for _index in range(manager.receive_queue.maxsize):
            manager.receive_queue.put_nowait(("fixture", None))
        entered, finished = threading.Event(), threading.Event()
        payload = wire.encode_file_transfer_chunk("fixture", 0, b"owned")
        def enqueue():
            entered.set()
            manager.accept_control(payload)
            finished.set()
        producer = threading.Thread(target=enqueue)
        producer.start()
        try:
            self.assertTrue(entered.wait(1))
            self.assertFalse(finished.wait(.05))
            # The real disk worker needs this same lock before taking more work.
            self.assertTrue(manager.lock.acquire(timeout=.05))
            manager.lock.release()
            manager.receive_queue.get_nowait()
            self.assertTrue(finished.wait(1))
            self.assertFalse(manager.receive_overflow.is_set())
            self.assertFalse(manager.download.cancel.is_set())
        finally:
            manager.close()
            producer.join(1)

    def test_receive_queue_sustained_stall_is_bounded_and_close_interrupts_wait(self):
        for close_early in (False, True):
            with self.subTest(close_early=close_early):
                viewer = self.viewer()
                manager = viewer.transfer_tasks
                manager.download = manager._new("取回", "fixture")
                for _index in range(manager.receive_queue.maxsize):
                    manager.receive_queue.put_nowait(("fixture", None))
                finished = threading.Event()
                started = time.monotonic()
                def enqueue():
                    manager.accept_control(wire.encode_file_transfer_chunk("fixture", 0, b"owned"))
                    finished.set()
                producer = threading.Thread(target=enqueue)
                producer.start()
                if close_early:
                    manager.close()
                self.assertTrue(finished.wait(1))
                producer.join(1)
                self.assertLess(time.monotonic() - started, .75)
                self.assertEqual(not close_early, manager.receive_overflow.is_set())

    def test_real_encrypted_upload_cancel_keeps_transport_usable(self):
        viewer = self.viewer()
        viewer_socket, host_socket = socket.socketpair()
        self.addCleanup(viewer_socket.close)
        self.addCleanup(host_socket.close)
        viewer_socket.settimeout(3)
        host_socket.settimeout(3)
        viewer.sock = viewer_socket
        viewer.session = wire.SecureSession(b"a" * 32, b"b" * 32, is_server=False)
        host_session = wire.SecureSession(b"a" * 32, b"b" * 32, is_server=True)
        first_chunk, continue_reading, cancelled = threading.Event(), threading.Event(), threading.Event()
        errors = []
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "source.bin"
            source.write_bytes(b"x" * (4 * 1024 * 1024))
            destination = Path(folder) / "remote"
            def host_loop():
                incoming = None
                try:
                    while True:
                        kind, payload = wire.read_message(host_socket, host_session)
                        if kind == wire.MESSAGE_PING:
                            wire.write_message(host_socket, host_session, wire.MESSAGE_PONG, b"")
                            return
                        if payload[0] == wire.CONTROL_FILE_TRANSFER_START:
                            incoming = wire.start_incoming_file_transfer(payload, destination)
                        elif payload[0] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                            wire.write_incoming_file_chunk(payload, incoming)
                            first_chunk.set()
                            if not continue_reading.wait(2):
                                raise TimeoutError("test cancel was not released")
                        elif payload[0] == wire.CONTROL_FILE_TRANSFER_CANCEL:
                            wire.cancel_incoming_file_transfer(payload, incoming)
                            cancelled.set()
                except Exception as error:
                    errors.append(error)
                finally:
                    if incoming is not None and incoming.active:
                        incoming.abort()
            worker = threading.Thread(target=host_loop)
            worker.start()
            try:
                self.assertTrue(viewer.send_files([str(source)]))
                self.assertTrue(first_chunk.wait(2))
                task = viewer.transfer_tasks.upload
                self.assertTrue(viewer.transfer_tasks.cancel_task(task.id)[0])
                continue_reading.set()
                self.assertTrue(cancelled.wait(2))
                self.wait(lambda: task.terminal)
                wire.write_message(viewer_socket, viewer.session, wire.MESSAGE_PING, b"")
                self.assertEqual((wire.MESSAGE_PONG, b""), wire.read_message(viewer_socket, viewer.session))
                self.assertEqual([], list(destination.iterdir()))
                self.assertFalse(viewer.stop_event.is_set())
            finally:
                continue_reading.set()
                worker.join(3)
            self.assertEqual([], errors)

    def test_reconnect_inherits_read_only_before_worker_start(self):
        from test_linux_low_latency import LinuxLowLatencyPolicyTests
        ui, _pending = LinuxLowLatencyPolicyTests._viewer_start_poll_fixture()
        ui.viewer_read_only = True
        created = []
        def create(*args, **kwargs):
            viewer = app.ViewerConnection(*args, **kwargs)
            viewer.start = mock.Mock(side_effect=lambda: self.assertTrue(viewer.read_only))
            created.append(viewer)
            return viewer
        real_type = app.ViewerConnection
        def factory(*args, **kwargs):
            with mock.patch.object(app, "ViewerConnection", real_type):
                return create(*args, **kwargs)
        with mock.patch.object(app, "ViewerConnection", side_effect=factory):
            ui._start_viewer_attempt("192.0.2.1", 1, "owned", reconnecting=False)
            ui.viewer = None
            ui._start_viewer_attempt("192.0.2.1", 1, "owned", reconnecting=True)
        self.assertEqual(2, len(created))
        self.assertTrue(all(viewer.read_only for viewer in created))


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1", "requires owned Xvfb")
class SessionPanelTests(unittest.TestCase):
    def setUp(self):
        self.root = app.tk.Tk()
        self.root.geometry("640x480")
        self.viewer = app.ViewerConnection("192.0.2.1", 1, "owned", queue.Queue(), 1)
        self.ui = SimpleNamespace(root=self.root, viewer=self.viewer, transfer_task_panel=None, _set_viewer_status=mock.Mock())
        self.addCleanup(self.cleanup)

    def cleanup(self):
        self.viewer.transfer_tasks.close()
        self.root.destroy()
        self.ui = self.viewer = self.root = None
        gc.collect()

    def test_task_panel_is_nonmodal_scrollable_and_old_owner_cannot_cancel_new_task(self):
        panel = self.ui.transfer_task_panel = TransferTaskPanel(self.ui)
        self.root.tk.call("tk", "scaling", 192 / 72)
        panel.window.geometry("480x320")
        tasks = [self.viewer.transfer_tasks._new("发送", f"文件-{index}" * 10) for index in range(20)]
        for task in tasks:
            panel.update(self.viewer, task)
        self.root.update()
        self.assertIsNone(self.root.grab_current())
        panel.tree.selection_set(tasks[-1].id)
        panel.tree.see(tasks[-1].id)
        self.root.update()
        self.assertGreater(panel.tree.yview()[0], 0)
        button = panel.cancel_button
        self.assertLessEqual(button.winfo_rooty() + button.winfo_height(), panel.window.winfo_rooty() + panel.window.winfo_height())
        self.ui.viewer = SimpleNamespace(transfer_tasks=mock.Mock(), stop_event=threading.Event())
        panel.cancel()
        self.ui.viewer.transfer_tasks.cancel_task.assert_not_called()
        self.assertFalse(tasks[-1].cancel.is_set())
        panel.close()
        self.assertFalse(self.viewer.stop_event.is_set())

    def test_byte_only_task_updates_preserve_path_selection_and_scroll_without_rewrite(self):
        panel = self.ui.transfer_task_panel = TransferTaskPanel(self.ui)
        task = self.viewer.transfer_tasks._new("取回", "selected")
        task.detail = "\n".join(f"保存路径 {index}: /owned/资料😀/{index}.txt" for index in range(80))
        panel.update(self.viewer, task)
        self.root.update()
        panel.details.tag_add("sel", "30.0", "30.end")
        panel.details.yview("25.0")
        selected = panel.details.get("sel.first", "sel.last")
        top = panel.details.index("@0,0")
        with mock.patch.object(panel.details, "delete", wraps=panel.details.delete) as delete:
            task.completed_bytes += 1024
            panel.update(self.viewer, task)
            self.root.update()
            delete.assert_not_called()
        self.assertEqual(selected, panel.details.get("sel.first", "sel.last"))
        self.assertEqual(top, panel.details.index("@0,0"))
        top_text = panel.details.get(top, f"{top} lineend")
        task.detail = "新的状态说明\n" + task.detail
        panel.update(self.viewer, task)
        self.root.update()
        self.assertEqual(selected, panel.details.get("sel.first", "sel.last"))
        current_top = panel.details.index("@0,0")
        self.assertEqual(top_text, panel.details.get(current_top, f"{current_top} lineend"))

    def test_same_task_details_preserve_unchanged_unicode_prefix_or_suffix_only(self):
        panel = self.ui.transfer_task_panel = TransferTaskPanel(self.ui)
        task = self.viewer.transfer_tasks._new("取回", "selected")
        path = "/owned/资料😀/结果.txt"
        for original, updated, path_line in ((path + "\n发送中", path + "\n已完成，已保存", "1"),
                ("发送中\n" + path, "校验并保存完成\n" + path, "2")):
            task.detail = original
            panel.update(self.viewer, task)
            panel.details.tag_remove("sel", "1.0", "end")
            panel.details.tag_add("sel", f"{path_line}.0", f"{path_line}.end")
            task.detail = updated
            panel.update(self.viewer, task)
            self.assertEqual(path, panel.details.get("sel.first", "sel.last"))
        task.detail = "已取消\n/changed/file.txt"
        panel.update(self.viewer, task)
        self.assertFalse(panel.details.tag_ranges("sel"))

    def test_switching_tasks_does_not_keep_another_tasks_selection_or_scroll(self):
        panel = self.ui.transfer_task_panel = TransferTaskPanel(self.ui)
        first = self.viewer.transfer_tasks._new("取回", "first")
        second = self.viewer.transfer_tasks._new("取回", "second")
        first.detail = second.detail = "\n".join(f"路径 {index}" for index in range(80))
        panel.update(self.viewer, first)
        panel.update(self.viewer, second)
        self.root.update()
        panel.details.tag_add("sel", "30.0", "30.end")
        panel.details.yview("25.0")
        panel.tree.selection_set(second.id)
        panel.select()
        self.root.update()
        self.assertFalse(panel.details.tag_ranges("sel"))
        self.assertEqual("1.0", panel.details.index("@0,0"))


class LinuxSourcePackageDependencyTests(unittest.TestCase):
    def test_both_linux_packages_include_transitive_local_app_dependencies(self):
        repo = Path(__file__).resolve().parents[1]
        source = repo / "scripts/linux"
        pending, dependencies = ["remotedesk_linux_app"], set()
        while pending:
            name = pending.pop()
            if name in dependencies:
                continue
            dependencies.add(name)
            for node in ast.walk(ast.parse((source / f"{name}.py").read_text(encoding="utf-8-sig"))):
                imported = ([node.module] if isinstance(node, ast.ImportFrom) and node.module
                            else [item.name for item in node.names] if isinstance(node, ast.Import) else [])
                pending.extend(item for item in imported if (source / f"{item}.py").is_file())
        self.assertIn("remotedesk_linux_transfers", dependencies)
        system = (repo / "scripts/Build-LinuxSystemPackage.ps1").read_text(encoding="utf-8-sig")
        publish = (repo / "scripts/Publish-RemoteDesk.ps1").read_text(encoding="utf-8-sig").replace("\\", "/")
        file_list = re.search(r'foreach\s*\(\$name\s+in\s+@\((.*?)\)\)', system, re.S).group(1)
        for name in sorted(dependencies):
            with self.subTest(module=name):
                self.assertIn(f'"{name}.py"', file_list)
                self.assertRegex(publish, rf'Copy-Item[^\n]+scripts/linux/{name}\.py[^\n]+app/{name}\.py')


if __name__ == "__main__":
    unittest.main()
