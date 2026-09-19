"""Actual viewer ZIP preparation on owned files, without a network connection."""
import hashlib
from pathlib import Path
import queue
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as protocol


class LinuxViewerArchiveCancellationTests(unittest.TestCase):
    def fixture(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        parent = Path(temporary.name)
        source, archives = parent / "source", parent / "archives"
        source.mkdir(); archives.mkdir()
        payload = b"owned archive cancellation fixture\n" * (protocol.FILE_TRANSFER_CHUNK_BYTES // 8)
        (source / "payload.bin").write_bytes(payload)
        viewer = app.ViewerConnection("127.0.0.1", 1, "owned fixture", queue.Queue(), 1)
        events = []
        viewer._put_event = lambda kind, value: events.append((kind, value))
        return viewer, source, archives, payload, events

    def test_already_disconnected_viewer_does_not_enumerate_or_pack_directory(self):
        viewer, source, archives, _, _ = self.fixture()
        viewer.stop_event.set()
        with mock.patch.object(protocol.tempfile, "tempdir", str(archives)), \
                mock.patch.object(protocol, "iter_safe_directory_entries") as enumerate_entries:
            with self.assertRaises(protocol.TransferCancelledError):
                viewer._prepare_transfer_path(source)
        enumerate_entries.assert_not_called()
        self.assertEqual([], list(archives.iterdir()))

    def test_disconnect_during_actual_zip_write_stops_at_first_chunk_and_cleans_partial_archive(self):
        viewer, source, archives, payload, _ = self.fixture()
        copied_chunks = []
        original_check = protocol.ensure_archive_stream_within_transfer_limit
        def disconnect_after_chunk(stream):
            original_check(stream)
            copied_chunks.append(stream.tell())
            viewer.stop_event.set()
        with mock.patch.object(protocol.tempfile, "tempdir", str(archives)), \
                mock.patch.object(protocol, "ensure_archive_stream_within_transfer_limit", side_effect=disconnect_after_chunk):
            with self.assertRaises(protocol.TransferCancelledError):
                viewer._prepare_transfer_path(source)
        self.assertEqual(1, len(copied_chunks))
        self.assertEqual([], list(archives.iterdir()))
        self.assertEqual(hashlib.sha256(payload).digest(), hashlib.sha256((source / "payload.bin").read_bytes()).digest())

    def test_cancelled_archive_batch_releases_lock_and_does_not_start_send_or_next_item(self):
        viewer, source, archives, _, events = self.fixture()
        original_check = protocol.ensure_archive_stream_within_transfer_limit
        def disconnect_after_chunk(stream):
            original_check(stream)
            viewer.stop_event.set()
        viewer.file_transfer_lock.acquire()
        with mock.patch.object(protocol.tempfile, "tempdir", str(archives)), \
                mock.patch.object(protocol, "ensure_archive_stream_within_transfer_limit", side_effect=disconnect_after_chunk), \
                mock.patch.object(viewer, "_send_file_to_remote") as send:
            viewer._send_files_locked([str(source), str(source / "payload.bin")])
        send.assert_not_called()
        self.assertFalse(viewer.file_transfer_lock.locked())
        self.assertEqual([], list(archives.iterdir()))
        details = next(value for kind, value in events if kind == "viewer_file_results")
        self.assertIn("已发送 0 项，未完成 1 项，未开始 1 项", details)

    def test_ordinary_viewer_archive_keeps_contents_and_caller_owned_cleanup(self):
        viewer, source, archives, payload, _ = self.fixture()
        with mock.patch.object(protocol.tempfile, "tempdir", str(archives)):
            path, transfer_name, display_name, temporary = viewer._prepare_transfer_path(source)
        self.assertEqual(path, temporary)
        self.assertEqual("source.zip", transfer_name)
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(payload, archive.read("source/payload.bin"))
        path.unlink()
        self.assertEqual([], list(archives.iterdir()))


if __name__ == "__main__":
    unittest.main()
