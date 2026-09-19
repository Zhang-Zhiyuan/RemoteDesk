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


class LinuxFileHeartbeatTests(unittest.TestCase):
    def make_viewer(self, generation=1):
        viewer = app.ViewerConnection("127.0.0.1", 56565, "fixture", queue.Queue(), generation)
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)
        viewer.awaiting_pong_since = 0.0
        return viewer

    def test_upload_grace_is_bounded_without_fabricating_inbound_activity(self):
        viewer = self.make_viewer()
        lease = viewer._begin_outgoing_file_transfer()
        try:
            self.assertTrue(viewer._liveness_step(18.0))
            self.assertTrue(viewer._liveness_step(149.99))
            self.assertEqual(0.0, viewer.last_message_received_at)
            self.assertEqual(0.0, viewer.awaiting_pong_since)
            self.assertFalse(viewer._liveness_step(150.0))
            viewer.sock.shutdown.assert_called_once()
        finally:
            viewer._end_outgoing_file_transfer(lease)
        self.assertIsNone(viewer._outgoing_file_transfer)

    def test_idle_and_completed_upload_return_to_normal_eighteen_second_deadline(self):
        for had_upload in (False, True):
            with self.subTest(had_upload=had_upload):
                viewer = self.make_viewer()
                if had_upload:
                    lease = viewer._begin_outgoing_file_transfer()
                    viewer._end_outgoing_file_transfer(lease)
                self.assertTrue(viewer._liveness_step(17.99))
                self.assertFalse(viewer._liveness_step(18.0))

    def test_other_connection_and_replaced_transport_do_not_inherit_upload_grace(self):
        sending = self.make_viewer(1)
        receiving = self.make_viewer(2)
        lease = sending._begin_outgoing_file_transfer()
        self.assertFalse(receiving._liveness_step(18.0))
        self.assertTrue(sending._liveness_step(18.0))
        sending.sock = mock.Mock()
        sending.session = object()
        self.assertFalse(sending._liveness_step(18.0))
        sending._end_outgoing_file_transfer(lease)

    def test_old_cleanup_does_not_clear_replacement_upload(self):
        viewer = self.make_viewer()
        old = viewer._begin_outgoing_file_transfer()
        current = viewer._begin_outgoing_file_transfer()
        viewer._end_outgoing_file_transfer(old)
        self.assertIs(current, viewer._outgoing_file_transfer)
        self.assertTrue(viewer._liveness_step(40.0))
        viewer._end_outgoing_file_transfer(current)
        self.assertFalse(viewer._liveness_step(40.0))

    def test_legacy_drain_does_not_extend_original_inbound_silence_deadline(self):
        viewer = self.make_viewer()
        lease = viewer._begin_outgoing_file_transfer()
        with mock.patch.object(app.time, "monotonic", return_value=140.0):
            viewer._end_outgoing_file_transfer(lease, legacy_drain=True)
        self.assertIsNone(viewer._outgoing_file_transfer)
        self.assertEqual((lease, 140.0), viewer._outgoing_file_drain)
        self.assertEqual(0.0, viewer.last_message_received_at)
        self.assertEqual(0.0, viewer.awaiting_pong_since)
        self.assertTrue(viewer._liveness_step(149.999))
        self.assertFalse(viewer._liveness_step(150.0))

    def test_legacy_drain_has_its_own_completion_clock_and_early_pong_does_not_clear_it(self):
        viewer = self.make_viewer()
        lease = viewer._begin_outgoing_file_transfer()
        with mock.patch.object(app.time, "monotonic", return_value=100.0):
            viewer._end_outgoing_file_transfer(lease, legacy_drain=True)
        viewer._mark_message_received(149.0)
        viewer._mark_pong_received()
        self.assertEqual((lease, 100.0), viewer._outgoing_file_drain)
        # A subsequent probe can be queued behind the tail. Keep the original
        # drain deadline, rather than clearing or extending it for this Pong.
        viewer.awaiting_pong_since = 149.0
        with mock.patch.object(app.time, "monotonic", return_value=249.999):
            self.assertTrue(viewer._liveness_step())
        with mock.patch.object(app.time, "monotonic", return_value=250.0):
            self.assertFalse(viewer._liveness_step())
        self.assertIsNone(viewer._outgoing_file_drain)
        self.assertEqual(149.0, viewer.last_message_received_at)

    def test_legacy_drain_is_not_inherited_by_replaced_transport_or_new_connection(self):
        for replacement in ("socket", "session", "deactivate"):
            with self.subTest(replacement=replacement):
                viewer = self.make_viewer()
                lease = viewer._begin_outgoing_file_transfer()
                with mock.patch.object(app.time, "monotonic", return_value=10.0):
                    viewer._end_outgoing_file_transfer(lease, legacy_drain=True)
                if replacement == "socket":
                    viewer.sock = mock.Mock()
                elif replacement == "session":
                    viewer.session = object()
                else:
                    viewer._deactivate_heartbeat()
                    self.assertIsNone(viewer._outgoing_file_drain)
                    viewer._activate_heartbeat(0.0)
                    viewer.awaiting_pong_since = 0.0
                self.assertFalse(viewer._liveness_step(18.0))
                self.assertIsNone(viewer._outgoing_file_drain)

    def test_old_cleanup_cannot_start_or_clear_another_transfers_drain(self):
        viewer = self.make_viewer()
        old = viewer._begin_outgoing_file_transfer()
        current = viewer._begin_outgoing_file_transfer()
        viewer._end_outgoing_file_transfer(old, legacy_drain=True)
        self.assertIsNone(viewer._outgoing_file_drain)
        with mock.patch.object(app.time, "monotonic", return_value=10.0):
            viewer._end_outgoing_file_transfer(current, legacy_drain=True)
        viewer._end_outgoing_file_transfer(old)
        self.assertEqual((current, 10.0), viewer._outgoing_file_drain)
        self.assertTrue(viewer._liveness_step(40.0))
        next_lease = viewer._begin_outgoing_file_transfer()
        self.assertIsNone(viewer._outgoing_file_drain)
        viewer._end_outgoing_file_transfer(next_lease)  # failed or acknowledged
        self.assertFalse(viewer._liveness_step(40.0))

    def test_actual_legacy_sender_enters_drain_only_after_successful_complete(self):
        for length in (0, 100_000):
            for checksum in (False, True):
                with self.subTest(length=length, checksum=checksum), tempfile.TemporaryDirectory() as directory:
                    path = Path(directory) / "fixture.bin"
                    path.write_bytes(b"x" * length)
                    viewer = self.make_viewer()
                    viewer.remote_capabilities = wire.CAPABILITY_FILE_CHECKSUM if checksum else 0
                    messages = []

                    def write(payload):
                        message = wire.decode_control(payload)
                        messages.append(message)
                        self.assertIsNotNone(viewer._outgoing_file_transfer)
                        self.assertIsNone(viewer._outgoing_file_drain)
                        self.assertTrue(viewer._liveness_step(40.0))

                    viewer._write_file_control = write
                    with mock.patch.object(app.time, "monotonic", return_value=80.0):
                        result = viewer._send_file_to_remote(path, path.name, path.name)
                    self.assertIn("旧版远端未返回保存确认", result)
                    self.assertIsNone(viewer._outgoing_file_transfer)
                    self.assertEqual(80.0, viewer._outgoing_file_drain[1])
                    self.assertIs(viewer.sock, viewer._outgoing_file_drain[0][1])
                    self.assertIs(viewer.session, viewer._outgoing_file_drain[0][2])
                    self.assertEqual(wire.CONTROL_FILE_TRANSFER_START, messages[0]["kind"])
                    self.assertEqual(wire.CONTROL_FILE_TRANSFER_COMPLETE, messages[-1]["kind"])
                    self.assertEqual(checksum, any(row["kind"] == wire.CONTROL_FILE_TRANSFER_CHECKSUM for row in messages))
                    self.assertTrue(viewer._liveness_step(149.999))
                    self.assertFalse(viewer._liveness_step(150.0))

    def test_legacy_sender_failure_never_starts_drain(self):
        for failing_kind in (wire.CONTROL_FILE_TRANSFER_START, wire.CONTROL_FILE_TRANSFER_CHUNK,
                             wire.CONTROL_FILE_TRANSFER_CHECKSUM, wire.CONTROL_FILE_TRANSFER_COMPLETE):
            with self.subTest(kind=failing_kind), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "fixture.bin"
                path.write_bytes(b"fixture")
                viewer = self.make_viewer()
                viewer.remote_capabilities = wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_CANCEL
                def write(payload):
                    if wire.decode_control(payload)["kind"] == failing_kind:
                        raise OSError("fixture write failed")
                viewer._write_file_control = write
                with self.assertRaises(OSError):
                    viewer._send_file_to_remote(path, path.name, path.name)
                self.assertIsNone(viewer._outgoing_file_transfer)
                self.assertIsNone(viewer._outgoing_file_drain)
                self.assertFalse(viewer._liveness_step(18.0))

    def test_sender_failure_clears_grace_before_best_effort_cancel_write(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.bin"
            path.write_bytes(b"fixture")
            viewer = self.make_viewer()
            viewer.remote_capabilities = wire.CAPABILITY_FILE_TRANSFER_CANCEL
            cancelled = []
            def write(payload):
                message = wire.decode_control(payload)
                if message["kind"] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                    raise OSError("fixture write failed")
                if message["kind"] == wire.CONTROL_FILE_TRANSFER_CANCEL:
                    self.assertIsNone(viewer._outgoing_file_transfer)
                    self.assertIsNone(viewer._outgoing_file_drain)
                    self.assertFalse(viewer._liveness_step(18.0))
                    cancelled.append(message)
            viewer._write_file_control = write
            with self.assertRaises(OSError):
                viewer._send_file_to_remote(path, path.name, path.name)
            self.assertEqual(1, len(cancelled))

    def test_actual_sender_holds_grace_through_start_chunks_checksum_and_receipt(self):
        for length in (0, 100_000):
            with self.subTest(length=length), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "fixture.bin"
                path.write_bytes(b"x" * length)
                viewer = self.make_viewer()
                viewer.remote_capabilities = wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                complete = threading.Event()
                errors = []
                transfer_ids = []
                kinds = []

                def write(payload):
                    message = wire.decode_control(payload)
                    kinds.append(message["kind"])
                    self.assertIsNotNone(viewer._outgoing_file_transfer)
                    self.assertTrue(viewer._liveness_step(40.0))
                    self.assertEqual(0.0, viewer.last_message_received_at)
                    if message["kind"] == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                        transfer_ids.append(message["transferId"])
                        complete.set()

                viewer._write_file_control = write

                def run():
                    try:
                        viewer._send_file_to_remote(path, path.name, path.name)
                    except BaseException as error:
                        errors.append(error)

                worker = threading.Thread(target=run)
                worker.start()
                try:
                    self.assertTrue(complete.wait(3))
                    self.assertTrue(worker.is_alive())
                    self.assertIsNotNone(viewer._outgoing_file_transfer)
                    self.assertTrue(viewer._liveness_step(149.0))
                    viewer._handle_control(wire.encode_file_transfer_receipt(transfer_ids[0], True, "/fixture/fixture.bin"))
                finally:
                    if not complete.is_set():
                        viewer.stop_event.set()
                    worker.join(3)
                self.assertFalse(worker.is_alive())
                self.assertEqual([], errors)
                self.assertIn(wire.CONTROL_FILE_TRANSFER_START, kinds)
                self.assertEqual(length > 0, wire.CONTROL_FILE_TRANSFER_CHUNK in kinds)
                self.assertIn(wire.CONTROL_FILE_TRANSFER_CHECKSUM, kinds)
                self.assertIsNone(viewer._outgoing_file_transfer)
                self.assertIsNone(viewer._outgoing_file_drain)
                self.assertEqual(0.0, viewer.last_message_received_at)

    def test_sender_clears_grace_when_start_or_chunk_or_save_receipt_fails(self):
        for stage in ("start", "chunk", "receipt", "timeout", "disconnect"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "fixture.bin"
                path.write_bytes(b"fixture")
                viewer = self.make_viewer()
                viewer.remote_capabilities = wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                viewer.file_receipt_timeout = .01

                def write(payload):
                    message = wire.decode_control(payload)
                    self.assertIsNotNone(viewer._outgoing_file_transfer)
                    if stage == "start" and message["kind"] == wire.CONTROL_FILE_TRANSFER_START:
                        raise OSError("write failed")
                    if stage == "chunk" and message["kind"] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                        raise OSError("write failed")
                    if message["kind"] == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                        if stage == "receipt":
                            viewer._handle_control(wire.encode_file_transfer_receipt(message["transferId"], False, "disk full"))
                        elif stage == "disconnect":
                            viewer.stop_event.set()

                viewer._write_file_control = write
                with self.assertRaises(OSError):
                    viewer._send_file_to_remote(path, path.name, path.name)
                self.assertIsNone(viewer._outgoing_file_transfer)
                self.assertIsNone(viewer._outgoing_file_drain)
                self.assertEqual(0.0, viewer.last_message_received_at)


if __name__ == "__main__":
    unittest.main()
