"""Real product viewer/host processes on an explicitly owned Xvfb only."""
import gc
import hashlib
import json
import os
from pathlib import Path
import queue
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as wire


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1"
    and os.environ.get("REMOTEDESK_ISOLATED_XVFB") == "1", "requires an explicitly owned Xvfb")
class OwnedProductSessionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="remotedesk-owned-session-")
        self.directory = Path(self.temporary.name)
        self.addCleanup(self.temporary.cleanup)
        self.viewers = []
        self.events = []
        self.frames = 0
        self.display_dimensions = None
        self.root = app.tk.Tk()
        self.root.geometry("640x360+0+0")
        self.entry = app.tk.Entry(self.root)
        self.entry.pack(fill="both", expand=True)
        self.entry.bind("<KeyPress>", lambda event: self.events.append(("down", event.keysym)))
        self.entry.bind("<KeyRelease>", lambda event: self.events.append(("up", event.keysym)))
        self.root.update()
        self.entry.focus_force()
        self.root.update()
        self.addCleanup(self.close_owned_session)
        self.return_file = self.directory / "return.txt"
        self.return_file.write_bytes("owned return 中文😀\n".encode())
        self.return_folder = self.directory / "return-folder"
        self.return_folder.mkdir()
        (self.return_folder / "nested.txt").write_bytes(b"owned folder content")
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            self.port = reservation.getsockname()[1]
        self.log_path = self.directory / "host.log"
        self.log = self.log_path.open("w")
        environment = dict(os.environ, XDG_CONFIG_HOME=str(self.directory / "config"), REMOTEDESK_AUTO_INSTALL="0")
        environment.pop("WAYLAND_DISPLAY", None)
        environment.pop("WAYLAND_SOCKET", None)
        self.host = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/linux/remotedesk_linux_host.py"),
            "--host", "127.0.0.1", "--port", str(self.port), "--password-fd", "0", "--no-discovery",
            "--capture", "placeholder", "--width", "640", "--height", "360", "--fps", "5",
            "--display", os.environ["DISPLAY"], "--serve-seconds", "90",
            "--receive-dir", str(self.directory / "received"),
            "--return-file", str(self.return_file), "--return-file", str(self.return_folder)],
            stdin=subprocess.PIPE, stdout=self.log, stderr=self.log, env=environment)
        self.host.stdin.write(b"owned-test-password")
        self.host.stdin.close()
        self.wait(lambda: "host listening on" in self.log_path.read_text(), timeout=15)

    def close_owned_session(self):
        for viewer in self.viewers:
            viewer.close()
        if getattr(self, "host", None) is not None:
            if self.host.poll() is None:
                self.host.terminate()
                try:
                    self.host.wait(5)
                except subprocess.TimeoutExpired:
                    self.host.kill()
                    self.host.wait(5)
            self.log.close()
            evidence = os.environ.get("REMOTEDESK_AUDIT_OUTPUT")
            if evidence:
                destination = Path(evidence)
                destination.mkdir(parents=True, exist_ok=True)
                (destination / f"{self._testMethodName}-host.log").write_bytes(self.log_path.read_bytes())
        self.root.destroy()
        self.root = self.entry = None
        gc.collect()

    def pump(self):
        self.root.update()
        for viewer in self.viewers:
            while True:
                try:
                    event, payload = viewer.events.get_nowait()
                except queue.Empty:
                    break
                if event == "viewer_frame":
                    self.frames += 1
                    self.display_dimensions = payload[1][4:6]

    def wait(self, predicate, timeout=8):
        deadline = time.monotonic() + timeout
        while not predicate() and time.monotonic() < deadline:
            self.pump()
            time.sleep(.005)
        self.pump()
        self.assertTrue(predicate(), self.log_path.read_text()[-5000:] if hasattr(self, "log_path") else "timed out")

    def connect(self, password="owned-test-password", read_only=False):
        viewer = app.ViewerConnection("127.0.0.1", self.port, password, queue.Queue(), len(self.viewers) + 1,
                                      allow_self_connection_for_testing=True)
        viewer.set_read_only(read_only)
        viewer.stop_auto_clipboard()  # Manual host clipboard is tested; auto sync has its own two-owner fixtures.
        self.viewers.append(viewer)
        viewer.start()
        return viewer

    def test_real_encrypted_input_clipboard_upload_folder_return_and_ui_progress(self):
        viewer = self.connect()
        self.wait(lambda: viewer.remote_capabilities & wire.CAPABILITY_INPUT_CONTROL and self.frames >= 2)
        self.entry.focus_force()
        self.root.update()
        self.assertTrue(viewer.send_input(app.INPUT_KEY_DOWN, data=0xA2))
        self.wait(lambda: ("down", "Control_L") in self.events)
        viewer.set_read_only(True)
        self.wait(lambda: ("up", "Control_L") in self.events)
        self.assertFalse(viewer.send_input(app.INPUT_KEY_DOWN, data=65))
        self.assertFalse(viewer.request_clipboard(read=False, text="blocked"))
        viewer.set_read_only(False)
        self.assertEqual(5, viewer.send_text("owned"))
        self.wait(lambda: self.entry.get() == "owned")
        clipboard = "owned clipboard 中文😀\n"
        self.assertTrue(viewer.request_clipboard(read=False, text=clipboard))
        self.wait(lambda: viewer.clipboard_pending is None)
        self.assertTrue(viewer.clipboard_latest.success)
        self.assertTrue(viewer.request_clipboard(read=True))
        self.wait(lambda: viewer.clipboard_pending is None)
        self.assertEqual(clipboard, viewer.clipboard_latest.text)
        ticks = []
        def tick():
            ticks.append(time.monotonic())
            self.root.after(10, tick)
        tick()
        upload = self.directory / "upload.bin"
        upload.write_bytes(bytes(range(256)) * 4096)
        self.assertTrue(viewer.send_files([str(upload)]))
        self.wait(lambda: viewer.transfer_tasks.upload.terminal)
        self.assertEqual("已完成", viewer.transfer_tasks.upload.phase)
        received = self.directory / "received" / upload.name
        self.assertEqual(hashlib.sha256(upload.read_bytes()).digest(), hashlib.sha256(received.read_bytes()).digest())
        viewer.set_read_only(True)
        returned = self.directory / "returned"
        returned.mkdir()
        manager = viewer.transfer_tasks
        self.assertTrue(manager.request_return(returned)[0])
        self.wait(lambda: manager.download.phase == "等待确认")
        task = manager.download
        self.assertEqual(2, len(manager.receive_expected))
        self.assertTrue(manager.confirm_return(task.id, True))
        self.wait(lambda: task.terminal)
        self.assertEqual("已完成", task.phase)
        self.assertEqual(self.return_file.read_bytes(), (returned / self.return_file.name).read_bytes())
        archives = list(returned.glob("*.zip"))
        self.assertEqual(1, len(archives))
        with zipfile.ZipFile(archives[0]) as archive:
            self.assertEqual(b"owned folder content", archive.read("return-folder/nested.txt"))
        frames_before = self.frames
        self.wait(lambda: self.frames > frames_before)
        self.assertGreater(len(ticks), 5)
        self.assertLess(max(b - a for a, b in zip(ticks, ticks[1:])), .5)
        self.assertTrue(viewer.read_only)
        viewer.set_display_size(320, 180)
        self.wait(lambda: self.display_dimensions == (320, 180))
        viewer.set_display_size(640, 360)
        self.wait(lambda: self.display_dimensions == (640, 360))
        print(json.dumps(dict(probe=self._testMethodName, frames=self.frames, uiTicks=len(ticks),
            maxUiGapSeconds=max(b - a for a, b in zip(ticks, ticks[1:])), uploadSha256=hashlib.sha256(upload.read_bytes()).hexdigest(),
            returnedFiles=len(manager.receive_paths), readOnlyPreserved=viewer.read_only)))

    def test_bad_auth_then_reconnect_and_process_loss_release_all_viewer_workers(self):
        denied = self.connect(password="incorrect-owned-password")
        self.wait(denied.stop_event.is_set)
        denied.close()
        viewer = self.connect(read_only=True)
        self.wait(lambda: viewer.remote_capabilities != 0 and self.frames >= 1)
        self.assertTrue(viewer.read_only)
        self.host.terminate()
        self.host.wait(5)
        self.wait(viewer.stop_event.is_set)
        started = time.monotonic()
        viewer.close()
        elapsed = time.monotonic() - started
        workers = (viewer.thread, viewer.decoder_thread, viewer.input_thread, viewer.capture_target_thread,
                   viewer.heartbeat_thread, viewer.liveness_thread, viewer.clipboard_auto_thread)
        self.assertFalse(any(worker.is_alive() for worker in workers))
        self.assertLess(elapsed, 1.5)
        print(json.dumps(dict(probe=self._testMethodName, closeSeconds=elapsed,
                             remainingViewerWorkers=sum(worker.is_alive() for worker in workers))))

    def test_real_large_upload_cancel_then_next_upload_and_return_keep_video_live(self):
        viewer = self.connect()
        self.wait(lambda: viewer.remote_capabilities & wire.CAPABILITY_FILE_RECEIVE and self.frames >= 1)
        upload = self.directory / "cancel-large.bin"
        upload.write_bytes(b"owned cancellation\n" * (4 * 1024 * 1024))
        self.assertTrue(viewer.send_files([str(upload)]))
        task = viewer.transfer_tasks.upload
        self.wait(lambda: task.completed_bytes > 0)
        self.assertTrue(viewer.transfer_tasks.cancel_task(task.id)[0], task.detail)
        self.wait(lambda: task.terminal)
        self.assertEqual("已取消", task.phase)
        received = self.directory / "received"
        self.wait(lambda: not list(received.iterdir()))
        self.assertFalse(viewer.stop_event.is_set())
        followup = self.directory / "after-cancel.txt"
        followup.write_bytes(b"same session after cancel")
        self.assertTrue(viewer.send_files([str(followup)]))
        self.wait(lambda: viewer.transfer_tasks.upload.terminal)
        self.assertEqual("已完成", viewer.transfer_tasks.upload.phase)
        self.assertEqual(followup.read_bytes(), (received / followup.name).read_bytes())
        returned = self.directory / "after-cancel-return"
        returned.mkdir()
        self.assertTrue(viewer.transfer_tasks.request_return(returned)[0])
        self.wait(lambda: viewer.transfer_tasks.download.phase == "等待确认")
        download = viewer.transfer_tasks.download
        self.assertTrue(viewer.transfer_tasks.cancel_task(download.id)[0])
        self.wait(lambda: download.terminal and viewer.transfer_tasks.download is None)
        self.assertEqual([], list(returned.iterdir()))
        self.assertTrue(viewer.transfer_tasks.request_return(returned)[0])
        self.wait(lambda: viewer.transfer_tasks.download.phase == "等待确认")
        accepted = viewer.transfer_tasks.download
        viewer.transfer_tasks.confirm_return(accepted.id, True)
        self.wait(lambda: accepted.terminal)
        self.assertEqual("已完成", accepted.phase)
        before = self.frames
        self.wait(lambda: self.frames > before)
        print(json.dumps(dict(probe=self._testMethodName, cancelledBytes=task.completed_bytes,
                             requestedBytes=task.total_bytes, frames=self.frames, sameSessionRecovered=True)))

    def test_real_inflight_return_cancel_cleans_partial_then_reuses_connection(self):
        self.return_file.write_bytes(b"owned returned bytes\n" * (4 * 1024 * 1024))
        viewer = self.connect()
        self.wait(lambda: viewer.remote_capabilities & wire.CAPABILITY_FILE_SEND and self.frames >= 1)
        directory = self.directory / "return-cancel"
        directory.mkdir()
        untouched = directory / "keep.txt"
        untouched.write_bytes(b"unrelated original")
        manager = viewer.transfer_tasks
        self.assertTrue(manager.request_return(directory)[0])
        self.wait(lambda: manager.download.phase == "等待确认")
        task = manager.download
        manager.confirm_return(task.id, True)
        self.wait(lambda: task.completed_bytes > 0)
        self.assertTrue(manager.cancel_task(task.id)[0], task.detail)
        self.wait(lambda: task.terminal and manager.download is None)
        self.assertEqual("已取消", task.phase)
        self.assertEqual([untouched], list(directory.iterdir()))
        self.assertEqual(b"unrelated original", untouched.read_bytes())
        self.return_file.write_bytes(b"same connection recovered after incoming cancellation")
        self.assertTrue(manager.request_return(directory)[0])
        self.wait(lambda: manager.download.phase == "等待确认")
        next_task = manager.download
        manager.confirm_return(next_task.id, True)
        self.wait(lambda: next_task.terminal)
        self.assertEqual("已完成", next_task.phase)
        self.assertEqual(self.return_file.read_bytes(), (directory / self.return_file.name).read_bytes())
        before = self.frames
        self.wait(lambda: self.frames > before)
        print(json.dumps(dict(probe=self._testMethodName, cancelledBytes=task.completed_bytes,
                             requestedBytes=task.total_bytes, frames=self.frames, sameSessionRecovered=True)))

    def test_real_bulk_return_finishes_with_bounded_queue_and_matching_checksum(self):
        self.return_file.write_bytes(bytes(range(256)) * (128 * 1024))
        expected_hash = hashlib.sha256(self.return_file.read_bytes()).hexdigest()
        viewer = self.connect(read_only=True)
        self.wait(lambda: viewer.remote_capabilities & wire.CAPABILITY_FILE_SEND and self.frames >= 1)
        directory = self.directory / "bulk-return"
        directory.mkdir()
        manager = viewer.transfer_tasks
        self.assertTrue(manager.request_return(directory)[0])
        self.wait(lambda: manager.download.phase == "等待确认")
        task = manager.download
        started = time.monotonic()
        write_chunk = wire.write_incoming_file_chunk
        stalled = False
        def short_disk_stall(payload, transfer):
            nonlocal stalled
            if not stalled:
                stalled = True
                time.sleep(.10)
            return write_chunk(payload, transfer)
        with mock.patch.object(wire, "write_incoming_file_chunk", side_effect=short_disk_stall):
            manager.confirm_return(task.id, True)
            self.wait(lambda: task.terminal, timeout=15)
        self.assertEqual("已完成", task.phase, f"{task.detail}; overflow={manager.receive_overflow.is_set()}")
        self.assertFalse(manager.receive_overflow.is_set())
        self.assertEqual(expected_hash, hashlib.sha256((directory / self.return_file.name).read_bytes()).hexdigest())
        self.assertTrue(viewer.read_only)
        print(json.dumps(dict(probe=self._testMethodName, returnedBytes=self.return_file.stat().st_size,
                             elapsedSeconds=time.monotonic() - started, sha256=expected_hash, frames=self.frames)))


if __name__ == "__main__":
    unittest.main()
