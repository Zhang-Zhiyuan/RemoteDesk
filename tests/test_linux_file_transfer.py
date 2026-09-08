from __future__ import annotations

import errno
import hashlib
import os
import queue
import socket
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


LINUX_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "linux"
sys.path.insert(0, str(LINUX_SCRIPTS))

import remotedesk_linux_host as host  # noqa: E402
import remotedesk_protocol_probe as protocol  # noqa: E402


class LinuxIncomingFileTransferTests(unittest.TestCase):
    def test_receive_budget_rejects_low_disk_space(self) -> None:
        budget = protocol.IncomingFileTransferBudget(
            lambda _directory: protocol.MINIMUM_FREE_SPACE_RESERVE_BYTES
        )

        with self.assertRaisesRegex(OSError, "insufficient receive-disk space"):
            budget.reserve(Path("/tmp"), 1)

    def test_receive_budget_rejects_declared_bytes_above_session_limit(self) -> None:
        budget = protocol.IncomingFileTransferBudget(lambda _directory: 1 << 62)
        budget.reserve(Path("/tmp"), protocol.MAX_FILE_TRANSFER_BYTES)
        budget.reserve(Path("/tmp"), protocol.MAX_FILE_TRANSFER_BYTES)

        with self.assertRaisesRegex(protocol.ProtocolError, "declared-byte limit"):
            budget.reserve(Path("/tmp"), 1)

    def test_password_can_be_read_from_inherited_fd_without_command_line_value(self) -> None:
        read_fd, write_fd = os.pipe()
        os.write(write_fd, "管道口令".encode("utf-8"))
        os.close(write_fd)

        password = protocol.resolve_password_argument(None, read_fd)

        self.assertEqual("管道口令", password)
        with self.assertRaises(OSError):
            os.fstat(read_fd)

    def test_recv_exact_uses_one_absolute_deadline_across_partial_reads(self) -> None:
        now = [100.0]

        class SlowSocket:
            def __init__(self) -> None:
                self.timeouts: list[float] = []

            def settimeout(self, timeout: float) -> None:
                self.timeouts.append(timeout)

            def recv_into(self, view: memoryview, _length: int) -> int:
                view[0] = ord("x")
                now[0] += 0.6
                return 1

        slow_socket = SlowSocket()
        with (
            mock.patch.object(protocol.time, "monotonic", side_effect=lambda: now[0]),
            self.assertRaisesRegex(TimeoutError, "deadline expired"),
        ):
            protocol.recv_exact(slow_socket, 3, deadline=101.0)

        self.assertEqual(2, len(slow_socket.timeouts))
        self.assertGreater(slow_socket.timeouts[0], slow_socket.timeouts[1])

    def test_linux_client_admission_gate_lets_latest_viewer_replace_owner(self) -> None:
        gate = host.ClientAdmissionGate(max_pending=2)
        first, second, rejected = object(), object(), object()
        replaced: list[object] = []

        self.assertTrue(gate.try_register_pending(first))
        self.assertTrue(gate.try_register_pending(second))
        self.assertFalse(gate.try_register_pending(rejected))
        self.assertEqual(2, gate.pending_count())
        first_result, first_replacement = gate.activate_latest(
            first,
            lambda: replaced.append(first),
        )
        second_result, first_owner_replacement = gate.activate_latest(
            second,
            lambda: replaced.append(second),
        )

        self.assertEqual("activated", first_result)
        self.assertIsNone(first_replacement)
        self.assertEqual("activated", second_result)
        self.assertIsNotNone(first_owner_replacement)
        first_owner_replacement()
        self.assertEqual([first], replaced)
        self.assertFalse(gate.release_active(first))
        self.assertTrue(gate.release_active(second))

    def test_replaced_linux_viewer_receives_terminal_reason_before_close(self) -> None:
        host_socket, viewer_socket = socket.socketpair()
        viewer_socket.settimeout(2.0)
        client_to_server_key = bytes([0x31]) * 32
        server_to_client_key = bytes([0x72]) * 32
        host_session = protocol.SecureSession(
            client_to_server_key,
            server_to_client_key,
            is_server=True,
        )
        viewer_session = protocol.SecureSession(
            client_to_server_key,
            server_to_client_key,
            is_server=False,
        )
        worker = threading.Thread(
            target=host.replace_authenticated_client,
            args=(host_socket, host_session, threading.Lock()),
            daemon=True,
        )
        try:
            worker.start()
            message_type, payload = protocol.read_message(
                viewer_socket,
                viewer_session,
            )
            worker.join(timeout=2.0)

            self.assertEqual(protocol.MESSAGE_CONTROL, message_type)
            control = protocol.decode_control(payload)
            self.assertEqual(
                protocol.CONTROL_SESSION_REJECTED,
                control["kind"],
            )
            self.assertIn("另一台查看端接管", control["statusMessage"])
            self.assertFalse(worker.is_alive())
        finally:
            host.close_client_socket(host_socket)
            viewer_socket.close()

    def test_session_rejected_control_round_trips(self) -> None:
        payload = protocol.encode_session_rejected(
            "another viewer is active"
        )

        decoded = protocol.decode_control(payload)

        self.assertEqual(
            protocol.CONTROL_SESSION_REJECTED,
            decoded["kind"],
        )
        self.assertEqual(
            "another viewer is active",
            decoded["statusMessage"],
        )
        self.assertEqual(
            "SessionRejected",
            protocol.control_name(decoded["kind"]),
        )

    def test_protocol_probe_surfaces_session_replacement_reason(self) -> None:
        result = protocol.ProbeResult("127.0.0.1", 56565)

        with self.assertRaisesRegex(
            ConnectionRefusedError,
            "另一台查看端接管",
        ):
            protocol.apply_message(
                result,
                protocol.MESSAGE_CONTROL,
                protocol.encode_session_rejected(
                    "此连接已被另一台查看端接管；已停止自动重连。"
                ),
            )

        self.assertEqual(
            protocol.CONTROL_SESSION_REJECTED,
            result.controls[-1]["kind"],
        )

    @staticmethod
    def _receive_remote_update_payloads(
        receive_directory: str,
        payloads: list[bytes],
    ) -> tuple[
        dict[str, object],
        protocol.ProbeResult,
        list[bytes],
    ]:
        incoming = iter(
            (protocol.MESSAGE_CONTROL, payload)
            for payload in payloads
        )
        sent_payloads: list[bytes] = []
        result = protocol.ProbeResult("localhost", 56565)
        with (
            mock.patch.object(
                protocol,
                "read_message",
                side_effect=lambda *_args: next(incoming),
            ),
            mock.patch.object(
                protocol,
                "write_message",
                side_effect=lambda _sock, _session, _kind, payload: sent_payloads.append(payload),
            ),
        ):
            completed = protocol.receive_remote_update_package(
                object(),
                object(),
                result,
                receive_directory,
                timeout_seconds=1.0,
            )
        return completed, result, sent_payloads

    def test_remote_update_backup_rejects_file_transfer_start(self) -> None:
        with tempfile.TemporaryDirectory() as receive_directory:
            with self.assertRaisesRegex(
                protocol.ProtocolError,
                "instead of RemoteUpdateStart",
            ):
                self._receive_remote_update_payloads(
                    receive_directory,
                    [
                        protocol.encode_file_transfer_start(
                            "wrong-kind",
                            "RemoteDesk.exe",
                            1,
                        )
                    ],
                )

            self.assertEqual([], list(Path(receive_directory).iterdir()))

    def test_remote_update_backup_rejects_wrong_file_name(self) -> None:
        with tempfile.TemporaryDirectory() as receive_directory:
            with self.assertRaisesRegex(
                protocol.ProtocolError,
                "must be named RemoteDesk.exe",
            ):
                self._receive_remote_update_payloads(
                    receive_directory,
                    [
                        protocol.encode_remote_update_start(
                            "wrong-name",
                            "Other.exe",
                            1,
                        )
                    ],
                )

            self.assertEqual([], list(Path(receive_directory).iterdir()))

    def test_remote_update_backup_rejects_empty_package(self) -> None:
        with tempfile.TemporaryDirectory() as receive_directory:
            with self.assertRaisesRegex(
                protocol.ProtocolError,
                "must not be empty",
            ):
                self._receive_remote_update_payloads(
                    receive_directory,
                    [
                        protocol.encode_remote_update_start(
                            "empty-package",
                            "RemoteDesk.exe",
                            0,
                        )
                    ],
                )

            self.assertEqual([], list(Path(receive_directory).iterdir()))

    def test_remote_update_backup_requires_checksum(self) -> None:
        content = b"missing checksum"
        with tempfile.TemporaryDirectory() as receive_directory:
            with self.assertRaisesRegex(
                protocol.ProtocolError,
                "checksum was required but not sent",
            ):
                self._receive_remote_update_payloads(
                    receive_directory,
                    [
                        protocol.encode_remote_update_start(
                            "missing-checksum",
                            "RemoteDesk.exe",
                            len(content),
                        ),
                        protocol.encode_file_transfer_chunk(
                            "missing-checksum",
                            0,
                            content,
                        ),
                        protocol.encode_file_transfer_complete("missing-checksum"),
                    ],
                )

            self.assertEqual([], list(Path(receive_directory).iterdir()))

    def test_remote_update_backup_rejects_checksum_mismatch(self) -> None:
        content = b"checksum mismatch"
        with tempfile.TemporaryDirectory() as receive_directory:
            with self.assertRaisesRegex(
                protocol.ProtocolError,
                "checksum mismatch",
            ):
                self._receive_remote_update_payloads(
                    receive_directory,
                    [
                        protocol.encode_remote_update_start(
                            "bad-checksum",
                            "RemoteDesk.exe",
                            len(content),
                        ),
                        protocol.encode_file_transfer_chunk(
                            "bad-checksum",
                            0,
                            content,
                        ),
                        protocol.encode_file_transfer_checksum(
                            "bad-checksum",
                            "0" * protocol.SHA256_HEX_LENGTH,
                        ),
                        protocol.encode_file_transfer_complete("bad-checksum"),
                    ],
                )

            self.assertEqual([], list(Path(receive_directory).iterdir()))

    def test_remote_update_backup_verifies_and_never_overwrites(self) -> None:
        content = b"verified RemoteDesk executable backup"
        checksum_hex = hashlib.sha256(content).hexdigest()
        with tempfile.TemporaryDirectory() as receive_directory:
            original_path = Path(receive_directory) / "RemoteDesk.exe"
            original_path.write_bytes(b"existing backup")

            completed, result, sent_payloads = self._receive_remote_update_payloads(
                receive_directory,
                [
                    protocol.encode_remote_update_start(
                        "verified-update",
                        "RemoteDesk.exe",
                        len(content),
                    ),
                    protocol.encode_file_transfer_chunk(
                        "verified-update",
                        0,
                        content,
                    ),
                    protocol.encode_file_transfer_checksum(
                        "verified-update",
                        checksum_hex,
                    ),
                    protocol.encode_file_transfer_complete("verified-update"),
                ],
            )

            saved_path = Path(str(completed["path"]))
            self.assertEqual(b"existing backup", original_path.read_bytes())
            self.assertEqual("RemoteDesk (1).exe", saved_path.name)
            self.assertEqual(content, saved_path.read_bytes())
            self.assertTrue(completed["checksumVerified"])
            self.assertEqual(checksum_hex, completed["sha256"])
            self.assertEqual([completed], result.received_files)
            self.assertEqual(
                protocol.CONTROL_REMOTE_UPDATE_PACKAGE_REQUEST,
                sent_payloads[0][0],
            )
            terminal = protocol.decode_control(sent_payloads[-1])
            self.assertEqual(protocol.CONTROL_FILE_TRANSFER_STATUS, terminal["kind"])
            self.assertTrue(terminal["success"])
            self.assertEqual([], list(Path(receive_directory).glob(".remotedesk-*.rdtransfer")))

    def test_receive_uses_private_temp_validates_sha256_and_reserves_final_atomically(self) -> None:
        content = b"RemoteDesk Linux file transfer"
        with tempfile.TemporaryDirectory() as temporary_directory:
            receive_directory = Path(temporary_directory)
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("receive-1", "../unsafe.txt", len(content)),
                receive_directory,
                require_checksum=True,
            )

            self.assertEqual("unsafe.txt", transfer.file_name)
            self.assertEqual(receive_directory, transfer.temporary_path.parent)
            self.assertTrue(protocol.is_owned_incoming_temporary_file(transfer.temporary_path.name))
            if os.name != "nt":
                self.assertEqual(0, transfer.temporary_path.stat().st_mode & 0o077)

            split = 9
            protocol.write_incoming_file_chunk(
                protocol.encode_file_transfer_chunk(transfer.transfer_id, 0, content[:split]),
                transfer,
            )
            protocol.write_incoming_file_chunk(
                protocol.encode_file_transfer_chunk(transfer.transfer_id, split, content[split:]),
                transfer,
            )
            protocol.set_expected_file_checksum(
                protocol.encode_file_transfer_checksum(
                    transfer.transfer_id,
                    hashlib.sha256(content).hexdigest(),
                ),
                transfer,
            )

            # Simulate a same-name file appearing after START. Completion must not overwrite it.
            transfer.final_path.write_bytes(b"existing")
            completed = protocol.complete_incoming_file_transfer(
                protocol.encode_file_transfer_complete(transfer.transfer_id),
                transfer,
            )

            saved_path = Path(completed["path"])
            self.assertEqual(b"existing", transfer.final_path.read_bytes())
            self.assertEqual(b"RemoteDesk Linux file transfer", saved_path.read_bytes())
            self.assertEqual("unsafe (1).txt", saved_path.name)
            self.assertTrue(completed["checksumVerified"])
            self.assertFalse(transfer.temporary_path.exists())
            self.assertEqual(receive_directory.resolve(), saved_path.parent.resolve())

    def test_checksum_failure_removes_temp_and_does_not_publish_file(self) -> None:
        content = b"bad-checksum"
        with tempfile.TemporaryDirectory() as temporary_directory:
            receive_directory = Path(temporary_directory)
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("receive-bad", "bad.bin", len(content)),
                receive_directory,
                require_checksum=True,
            )
            protocol.write_incoming_file_chunk(
                protocol.encode_file_transfer_chunk(transfer.transfer_id, 0, content),
                transfer,
            )
            protocol.set_expected_file_checksum(
                protocol.encode_file_transfer_checksum(transfer.transfer_id, "0" * 64),
                transfer,
            )

            with self.assertRaisesRegex(protocol.ProtocolError, "checksum mismatch"):
                protocol.complete_incoming_file_transfer(
                    protocol.encode_file_transfer_complete(transfer.transfer_id),
                    transfer,
                )

            self.assertFalse(transfer.temporary_path.exists())
            self.assertFalse((receive_directory / "bad.bin").exists())

    def test_concurrent_receivers_publish_without_overwrite(self) -> None:
        first_content = b"first-content"
        second_content = b"second-content"
        with tempfile.TemporaryDirectory() as temporary_directory:
            receive_directory = Path(temporary_directory)
            first = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("first", "shared.bin", len(first_content)),
                receive_directory,
                require_checksum=True,
            )
            second = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("second", "shared.bin", len(second_content)),
                receive_directory,
                require_checksum=True,
            )
            for transfer, content in ((first, first_content), (second, second_content)):
                protocol.write_incoming_file_chunk(
                    protocol.encode_file_transfer_chunk(transfer.transfer_id, 0, content),
                    transfer,
                )
                protocol.set_expected_file_checksum(
                    protocol.encode_file_transfer_checksum(
                        transfer.transfer_id,
                        hashlib.sha256(content).hexdigest(),
                    ),
                    transfer,
                )

            start_gate = threading.Barrier(2)

            def complete(transfer: protocol.IncomingFileTransfer) -> dict[str, object]:
                start_gate.wait(timeout=5)
                return protocol.complete_incoming_file_transfer(
                    protocol.encode_file_transfer_complete(transfer.transfer_id),
                    transfer,
                )

            with ThreadPoolExecutor(max_workers=2) as executor:
                results = list(executor.map(complete, (first, second)))

            saved_contents = {Path(str(result["path"])).read_bytes() for result in results}
            self.assertEqual({first_content, second_content}, saved_contents)
            self.assertEqual(
                {"shared.bin", "shared (1).bin"},
                {Path(str(result["path"])).name for result in results},
            )
            self.assertEqual([], list(receive_directory.glob(".remotedesk-*.rdtransfer")))

    def test_final_publish_falls_back_to_exclusive_copy_when_hard_links_are_unsupported(self) -> None:
        for error_code in (errno.EACCES, errno.EINVAL, errno.ENOSYS):
            with self.subTest(error_code=error_code), tempfile.TemporaryDirectory() as temporary_directory:
                directory = Path(temporary_directory)
                temporary_path = directory / ".remotedesk-source.rdtransfer"
                desired_path = directory / "saved.bin"
                temporary_path.write_bytes(b"fallback-content")

                with mock.patch.object(
                    protocol.os,
                    "link",
                    side_effect=OSError(error_code, os.strerror(error_code)),
                ) as link_mock:
                    saved_path = protocol.move_temporary_to_unique_final_path(
                        temporary_path,
                        desired_path,
                        desired_path.name,
                    )

                self.assertEqual(desired_path, saved_path)
                self.assertEqual(b"fallback-content", saved_path.read_bytes())
                self.assertFalse(temporary_path.exists())
                link_mock.assert_called_once_with(temporary_path, desired_path)

    def test_hard_link_commit_succeeds_when_temp_cleanup_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            temporary_path = directory / ".remotedesk-source.rdtransfer"
            desired_path = directory / "saved.bin"
            temporary_path.write_bytes(b"committed-content")

            with mock.patch.object(
                type(temporary_path),
                "unlink",
                side_effect=PermissionError(errno.EACCES, "temporary file is busy"),
            ) as unlink_mock:
                saved_path = protocol.move_temporary_to_unique_final_path(
                    temporary_path,
                    desired_path,
                    desired_path.name,
                )

            self.assertEqual(desired_path, saved_path)
            self.assertEqual(b"committed-content", saved_path.read_bytes())
            self.assertTrue(temporary_path.exists())
            self.assertFalse((directory / "saved (1).bin").exists())
            unlink_mock.assert_called_once_with(missing_ok=True)

    def test_exclusive_copy_fallback_never_overwrites_existing_final_name(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            temporary_path = directory / ".remotedesk-source.rdtransfer"
            desired_path = directory / "saved.bin"
            temporary_path.write_bytes(b"received-content")
            desired_path.write_bytes(b"existing-content")

            with mock.patch.object(
                protocol.os,
                "link",
                side_effect=PermissionError(errno.EACCES, "hard links unavailable"),
            ):
                saved_path = protocol.move_temporary_to_unique_final_path(
                    temporary_path,
                    desired_path,
                    desired_path.name,
                )

            self.assertEqual(directory / "saved (1).bin", saved_path)
            self.assertEqual(b"existing-content", desired_path.read_bytes())
            self.assertEqual(b"received-content", saved_path.read_bytes())

    def test_cancel_must_match_active_transfer_before_cleanup(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("active", "cancel.bin", 1),
                Path(temporary_directory),
            )
            temporary_path = transfer.temporary_path

            with self.assertRaisesRegex(protocol.ProtocolError, "cancellation id mismatch"):
                protocol.cancel_incoming_file_transfer(
                    protocol.encode_file_transfer_cancel("different", "stale cancel"),
                    transfer,
                )
            self.assertTrue(temporary_path.exists())

            reason = protocol.cancel_incoming_file_transfer(
                protocol.encode_file_transfer_cancel("active", "sender stopped"),
                transfer,
            )
            self.assertEqual("sender stopped", reason)
            self.assertFalse(temporary_path.exists())

    def test_host_replacement_is_committed_only_after_new_start_succeeds(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            receive_directory = Path(temporary_directory)
            first = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("first", "first.bin", 1),
                receive_directory,
            )
            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.incoming = first
            session.receive_dir = receive_directory
            session.viewer_capabilities = 0
            statuses: list[tuple[bool, str]] = []
            session._safe_status = lambda success, message: statuses.append((success, message))

            with mock.patch.object(host, "start_incoming_file_transfer", side_effect=OSError("disk full")):
                session._handle_file_transfer_start(
                    protocol.encode_file_transfer_start("replacement", "replacement.bin", 2)
                )
            self.assertIs(first, session.incoming)
            self.assertTrue(first.temporary_path.exists())
            self.assertFalse(statuses[-1][0])

            session._handle_file_transfer_start(
                protocol.encode_file_transfer_start("second", "second.bin", 2)
            )
            self.assertFalse(first.temporary_path.exists())
            self.assertEqual("second", session.incoming.transfer_id)
            self.assertTrue(session.incoming.temporary_path.exists())
            session._abort_incoming_transfer()

    def test_host_wrong_cancel_does_not_abort_another_transfer(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("current", "current.bin", 1),
                Path(temporary_directory),
            )
            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.incoming = transfer
            statuses: list[tuple[bool, str]] = []
            session._safe_status = lambda success, message: statuses.append((success, message))

            session._handle_file_transfer_cancel(
                protocol.encode_file_transfer_cancel("old", "late cancel")
            )
            self.assertIs(transfer, session.incoming)
            self.assertTrue(transfer.temporary_path.exists())
            self.assertFalse(statuses[-1][0])

            session._handle_file_transfer_cancel(
                protocol.encode_file_transfer_cancel("current", "cancel current")
            )
            self.assertIsNone(session.incoming)
            self.assertFalse(transfer.temporary_path.exists())

    def test_mismatched_complete_keeps_active_transfer_usable(self) -> None:
        content = b"continue-after-stale-complete"
        with tempfile.TemporaryDirectory() as temporary_directory:
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("current", "continue.bin", len(content)),
                Path(temporary_directory),
                require_checksum=True,
            )
            split = 8
            protocol.write_incoming_file_chunk(
                protocol.encode_file_transfer_chunk("current", 0, content[:split]),
                transfer,
            )

            with self.assertRaisesRegex(protocol.ProtocolError, "completion id mismatch"):
                protocol.complete_incoming_file_transfer(
                    protocol.encode_file_transfer_complete("stale"),
                    transfer,
                )
            self.assertTrue(transfer.active)
            self.assertTrue(transfer.temporary_path.exists())

            protocol.write_incoming_file_chunk(
                protocol.encode_file_transfer_chunk("current", split, content[split:]),
                transfer,
            )
            protocol.set_expected_file_checksum(
                protocol.encode_file_transfer_checksum("current", hashlib.sha256(content).hexdigest()),
                transfer,
            )
            completed = protocol.complete_incoming_file_transfer(
                protocol.encode_file_transfer_complete("current"),
                transfer,
            )
            self.assertEqual(content, Path(completed["path"]).read_bytes())

    def test_invalid_matching_chunk_aborts_and_removes_private_temp(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            transfer = protocol.start_incoming_file_transfer(
                protocol.encode_file_transfer_start("current", "bad-order.bin", 2),
                Path(temporary_directory),
            )
            with self.assertRaisesRegex(protocol.ProtocolError, "offset is out of order"):
                protocol.write_incoming_file_chunk(
                    protocol.encode_file_transfer_chunk("current", 1, b"x"),
                    transfer,
                )

            self.assertFalse(transfer.active)
            self.assertFalse(transfer.temporary_path.exists())

    def test_stale_cleanup_deletes_only_owned_private_temp_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            directory = Path(temporary_directory)
            stale_owned = directory / (
                f"{protocol.INCOMING_TEMPORARY_FILE_PREFIX}{'a' * 32}"
                f"{protocol.INCOMING_TEMPORARY_FILE_SUFFIX}"
            )
            fresh_owned = directory / (
                f"{protocol.INCOMING_TEMPORARY_FILE_PREFIX}{'b' * 32}"
                f"{protocol.INCOMING_TEMPORARY_FILE_SUFFIX}"
            )
            unrelated = directory / "important.rdtransfer"
            malformed = directory / ".remotedesk-not-a-transfer.rdtransfer"
            for path in (stale_owned, fresh_owned, unrelated, malformed):
                path.write_bytes(b"temporary")

            old_time = time.time() - protocol.STALE_TEMPORARY_FILE_SECONDS - 60
            for path in (stale_owned, unrelated, malformed):
                os.utime(path, (old_time, old_time))

            self.assertEqual(1, protocol.cleanup_stale_temporary_files(directory))
            self.assertFalse(stale_owned.exists())
            self.assertTrue(fresh_owned.exists())
            self.assertTrue(unrelated.exists())
            self.assertTrue(malformed.exists())

    def test_sanitized_file_name_fits_linux_byte_limit(self) -> None:
        sanitized = protocol.sanitize_file_name(f"{'\U0001f600' * 180}.txt")
        archive_name = host.create_directory_archive_name(Path("\U0001f600" * 180))

        self.assertLessEqual(len(sanitized.encode("utf-8")), protocol.MAX_SAFE_FILE_NAME_LENGTH)
        self.assertTrue(sanitized.endswith(".txt"))
        self.assertLessEqual(len(archive_name.encode("utf-8")), protocol.MAX_SAFE_FILE_NAME_LENGTH)
        self.assertTrue(archive_name.endswith(".zip"))


class LinuxDirectoryArchiveTests(unittest.TestCase):
    def test_partial_archive_is_deleted_when_packing_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            parent = Path(temporary_directory)
            source = parent / "source"
            archive_directory = parent / "archives"
            source.mkdir()
            archive_directory.mkdir()
            (source / "payload.bin").write_bytes(b"too large")

            with (
                mock.patch.object(protocol.tempfile, "tempdir", str(archive_directory)),
                mock.patch.object(protocol, "MAX_FILE_TRANSFER_BYTES", 1),
                self.assertRaisesRegex(protocol.ProtocolError, "transfer limit"),
            ):
                protocol.create_safe_directory_archive(source, source_size=0)

            self.assertEqual([], list(archive_directory.glob("remotedesk-folder-*.zip")))

    def test_archive_is_safe_excludes_its_own_output_and_preserves_empty_directories(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            parent = Path(temporary_directory)
            source = parent / "folder"
            source.mkdir()
            (source / "payload.txt").write_bytes(b"payload")
            (source / "empty").mkdir()
            outside = parent / "outside.txt"
            outside.write_bytes(b"outside secret")

            link_created = False
            try:
                (source / "outside-link.txt").symlink_to(outside)
                link_created = True
            except OSError:
                pass

            if link_created:
                self.assertEqual(len(b"payload"), protocol.safe_transfer_path_size(source))

            with mock.patch.object(protocol.tempfile, "tempdir", str(source)):
                archive_path = protocol.create_safe_directory_archive(source)
            try:
                self.assertEqual(source, archive_path.parent)
                with zipfile.ZipFile(archive_path, "r") as archive:
                    entry_names = archive.namelist()
                    self.assertEqual(b"payload", archive.read("folder/payload.txt"))

                self.assertIn("folder/empty/", entry_names)
                self.assertNotIn(f"folder/{archive_path.name}", entry_names)
                if link_created:
                    self.assertNotIn("folder/outside-link.txt", entry_names)
                for entry_name in entry_names:
                    self.assertFalse(entry_name.startswith("/"))
                    self.assertNotIn("\\", entry_name)
                    self.assertNotIn("..", entry_name.rstrip("/").split("/"))
            finally:
                archive_path.unlink(missing_ok=True)

    def test_archive_entry_names_sanitize_windows_paths_and_detect_collisions(self) -> None:
        entry_name = protocol.safe_archive_entry_name(
            "CON",
            ("..\\escape", "name:with?marks.txt"),
            is_directory=False,
        )
        self.assertEqual("_CON/.._escape/name_with_marks.txt", entry_name)

        seen_names: set[str] = set()
        first = protocol.safe_archive_entry_name("folder", ("a:b.txt",), False)
        second = protocol.safe_archive_entry_name("FOLDER", ("A?B.TXT",), False)
        protocol.reserve_archive_entry_name(first, seen_names)
        with self.assertRaisesRegex(protocol.ProtocolError, "colliding entry names"):
            protocol.reserve_archive_entry_name(second, seen_names)

    def test_archive_rejects_selected_root_symlink_or_junction(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "source"
            source.mkdir()
            (source / "payload.txt").write_bytes(b"payload")

            with mock.patch.object(protocol, "is_link_like", return_value=True):
                with self.assertRaisesRegex(protocol.ProtocolError, "symbolic link or junction"):
                    protocol.safe_transfer_path_size(source)
                with self.assertRaisesRegex(protocol.ProtocolError, "symbolic link or junction"):
                    protocol.create_safe_directory_archive(source)

    def test_directory_enumeration_and_zip_copy_honor_cancellation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            parent = Path(temporary_directory)
            source = parent / "source"
            archives = parent / "archives"
            source.mkdir()
            archives.mkdir()
            (source / "first.bin").write_bytes(b"first")
            (source / "second.bin").write_bytes(b"second")

            enumerate_cancel = threading.Event()
            entries = protocol.iter_safe_directory_entries(
                source,
                cancel_event=enumerate_cancel,
            )
            next(entries)
            enumerate_cancel.set()
            with self.assertRaises(protocol.TransferCancelledError):
                next(entries)

            archive_cancel = threading.Event()
            (source / "large.bin").write_bytes(
                b"x" * (protocol.FILE_TRANSFER_CHUNK_BYTES * 2 + 1)
            )
            original_limit_check = protocol.ensure_archive_stream_within_transfer_limit

            def cancel_after_first_archive_chunk(stream: object) -> None:
                original_limit_check(stream)
                archive_cancel.set()

            with (
                mock.patch.object(protocol.tempfile, "tempdir", str(archives)),
                mock.patch.object(
                    protocol,
                    "ensure_archive_stream_within_transfer_limit",
                    side_effect=cancel_after_first_archive_chunk,
                ),
                self.assertRaises(protocol.TransferCancelledError),
            ):
                protocol.create_safe_directory_archive(
                    source,
                    source_size=0,
                    cancel_event=archive_cancel,
                )

            self.assertEqual([], list(archives.glob("remotedesk-folder-*.zip")))


class LinuxReturnStatusTests(unittest.TestCase):
    @staticmethod
    def _clipboard_session() -> host.LinuxHostSession:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.stop_event = threading.Event()
        session.session_stop = threading.Event()
        session.clipboard_condition = threading.Condition()
        session.clipboard_queue = deque()
        session.clipboard_worker_stop = False
        session.clipboard_worker_thread = None
        session._write_control = mock.Mock()
        session._safe_status = mock.Mock()
        return session

    def test_clipboard_reader_queues_work_without_blocking(self) -> None:
        session = self._clipboard_session()
        clipboard_entered = threading.Event()
        allow_clipboard = threading.Event()

        def blocked_read() -> str:
            clipboard_entered.set()
            self.assertTrue(allow_clipboard.wait(timeout=1.0))
            return "clipboard"

        try:
            with mock.patch.object(host, "read_clipboard_text", side_effect=blocked_read):
                started_at = time.monotonic()
                session._handle_clipboard_get()
                self.assertLess(time.monotonic() - started_at, 0.1)
                self.assertTrue(clipboard_entered.wait(timeout=0.5))
                # A reader-thread Ping can still be handled while the system
                # clipboard utility is blocked on its owner worker.
                session._write_message = mock.Mock()
                session._handle_message(protocol.MESSAGE_PING, b"")
                session._write_message.assert_called_once_with(
                    protocol.MESSAGE_PONG,
                    b"",
                )
                allow_clipboard.set()
                self.assertTrue(
                    self._wait_until(
                        lambda: session._write_control.call_count == 1
                    )
                )
        finally:
            allow_clipboard.set()
            session._stop_clipboard_worker()

    def test_clipboard_queue_is_bounded_and_preserves_set_get_order(self) -> None:
        session = self._clipboard_session()
        session._start_clipboard_worker = mock.Mock()
        self.assertTrue(
            session._queue_clipboard_operation(
                host.ClipboardOperation(
                    protocol.CONTROL_CLIPBOARD_SET_TEXT,
                    "first",
                )
            )
        )
        self.assertTrue(
            session._queue_clipboard_operation(
                host.ClipboardOperation(
                    protocol.CONTROL_CLIPBOARD_SET_TEXT,
                    "latest",
                )
            )
        )
        self.assertTrue(
            session._queue_clipboard_operation(
                host.ClipboardOperation(
                    protocol.CONTROL_CLIPBOARD_GET_TEXT,
                )
            )
        )
        next_kind = protocol.CONTROL_CLIPBOARD_SET_TEXT
        for index in range(host.CLIPBOARD_OPERATION_QUEUE_LIMIT - 2):
            kind = next_kind
            self.assertTrue(
                session._queue_clipboard_operation(
                    host.ClipboardOperation(kind, str(index))
                )
            )
            next_kind = (
                protocol.CONTROL_CLIPBOARD_GET_TEXT
                if kind == protocol.CONTROL_CLIPBOARD_SET_TEXT
                else protocol.CONTROL_CLIPBOARD_SET_TEXT
            )
        overflow_kind = (
            protocol.CONTROL_CLIPBOARD_GET_TEXT
            if session.clipboard_queue[-1].kind
            == protocol.CONTROL_CLIPBOARD_SET_TEXT
            else protocol.CONTROL_CLIPBOARD_SET_TEXT
        )
        self.assertFalse(
            session._queue_clipboard_operation(
                host.ClipboardOperation(
                    overflow_kind,
                    "overflow",
                )
            )
        )
        self.assertEqual(host.CLIPBOARD_OPERATION_QUEUE_LIMIT, len(session.clipboard_queue))
        self.assertEqual("latest", session.clipboard_queue[0].text)
        self.assertEqual(protocol.CONTROL_CLIPBOARD_GET_TEXT, session.clipboard_queue[1].kind)

    def test_clipboard_stop_fences_late_result_and_returns_promptly(self) -> None:
        session = self._clipboard_session()
        clipboard_entered = threading.Event()
        allow_clipboard = threading.Event()

        def blocked_read() -> str:
            clipboard_entered.set()
            allow_clipboard.wait(timeout=2.0)
            return "late"

        with mock.patch.object(host, "read_clipboard_text", side_effect=blocked_read):
            session._handle_clipboard_get()
            self.assertTrue(clipboard_entered.wait(timeout=0.5))
            started_at = time.monotonic()
            session._stop_clipboard_worker()
            elapsed = time.monotonic() - started_at

        self.assertLess(elapsed, 0.6)
        allow_clipboard.set()
        worker = session.clipboard_worker_thread
        if worker is not None:
            worker.join(timeout=1.0)
        session._write_control.assert_not_called()

    def test_clipboard_worker_reports_clipboard_status_on_failure(self) -> None:
        session = self._clipboard_session()
        session.clipboard_queue.append(
            host.ClipboardOperation(protocol.CONTROL_CLIPBOARD_GET_TEXT)
        )

        with mock.patch.object(
            host,
            "read_clipboard_text",
            side_effect=OSError("clipboard helper failed"),
        ):
            session._start_clipboard_worker()
            self.assertTrue(
                self._wait_until(lambda: session._write_control.call_count == 1),
                "clipboard failure status was not written",
            )
            session._stop_clipboard_worker()

        payload = session._write_control.call_args.args[0]
        decoded = protocol.decode_control(payload)
        self.assertEqual(protocol.CONTROL_CLIPBOARD_STATUS, decoded["kind"])
        self.assertFalse(decoded["success"])
        self.assertIn("clipboard helper failed", decoded["statusMessage"])
        session._safe_status.assert_not_called()

    def test_return_worker_stop_is_bounded_when_filesystem_is_stuck(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.return_condition = threading.Condition()
        operation = host.ReturnOperation((), 0)
        session.return_active_operation = operation
        session.return_queued_operation = None
        session.pending_return_operation = None
        session.pending_return_plan = None
        session.pending_return_ignored_count = 0
        session.return_worker_stop = False
        release_worker = threading.Event()
        worker = threading.Thread(
            target=lambda: release_worker.wait(timeout=2.0),
            daemon=True,
        )
        session.return_worker_thread = worker
        worker.start()

        started_at = time.monotonic()
        session._stop_return_worker()
        elapsed = time.monotonic() - started_at

        self.assertLess(elapsed, 0.6)
        self.assertTrue(operation.cancel_event.is_set())
        self.assertIs(session.return_worker_thread, worker)
        release_worker.set()
        worker.join(timeout=1.0)

    @staticmethod
    def _wait_until(predicate: object, timeout: float = 1.0) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():  # type: ignore[operator]
                return True
            time.sleep(0.01)
        return bool(predicate())  # type: ignore[operator]

    def test_linux_clipboard_file_formats_parse_gnome_wayland_and_uri_lists(self) -> None:
        parsed = host.parse_clipboard_file_paths(
            "copy\n"
            "file:///home/test/My%20File.txt\n"
            "file://localhost/home/test/folder\n"
            "# comment\n"
            "https://example.test/not-local\n"
            "file://other-host/home/test/not-local\n"
        )

        self.assertEqual(
            [Path("/home/test/My File.txt"), Path("/home/test/folder")],
            parsed,
        )

    def test_plain_clipboard_text_ignores_invalid_and_overlong_path_lines(self) -> None:
        with (
            mock.patch.object(
                host,
                "read_clipboard_target",
                return_value=None,
            ),
            mock.patch.object(
                host,
                "read_clipboard_text",
                return_value="ordinary clipboard prose, not a file path.\n",
            ),
            mock.patch.object(
                type(host.Path("clipboard-prose")),
                "exists",
                side_effect=OSError("filename too long"),
            ),
        ):
            self.assertEqual([], host.read_clipboard_file_paths())

    def test_clipboard_file_path_deduplication_ignores_path_probe_errors(self) -> None:
        valid = Path("/tmp/valid-return-file")
        invalid = Path("/tmp/invalid-return-file")
        with mock.patch.object(
            host,
            "read_clipboard_target",
            return_value="file:///tmp/valid-return-file" + chr(10)
            + "file:///tmp/invalid-return-file" + chr(10),
        ):
            with mock.patch.object(
                type(valid),
                "exists",
                side_effect=[OSError("filename too long"), True],
            ):
                self.assertEqual([invalid], host.read_clipboard_file_paths())

    def test_resolve_return_files_ignores_invalid_explicit_and_clipboard_entries(self) -> None:
        valid = Path("/tmp/valid-return-file")
        with (
            mock.patch.object(
                host,
                "read_clipboard_file_paths",
                return_value=[Path("/tmp/clipboard-invalid")],
            ),
            mock.patch.object(
                type(valid),
                "exists",
                side_effect=OSError("filename too long"),
            ),
        ):
            self.assertEqual([], host.resolve_return_files(["\0invalid"]))

    def test_wayland_clipboard_target_falls_back_to_xclip(self) -> None:
        completed = [
            mock.Mock(returncode=1, stdout=""),
            mock.Mock(returncode=0, stdout="file:///tmp/returned.txt\n"),
        ]
        with (
            mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}),
            mock.patch.object(host.shutil, "which", side_effect=lambda command: f"/usr/bin/{command}"),
            mock.patch.object(host.subprocess, "run", side_effect=completed) as run_mock,
        ):
            result = host.read_clipboard_target("text/uri-list")

        self.assertEqual("file:///tmp/returned.txt\n", result)
        self.assertEqual(
            ["wl-paste", "--no-newline", "--type", "text/uri-list"],
            run_mock.call_args_list[0].args[0],
        )
        self.assertEqual(
            ["xclip", "-selection", "clipboard", "-t", "text/uri-list", "-o"],
            run_mock.call_args_list[1].args[0],
        )

    def test_empty_return_status_is_windows_recognizable_and_non_actionable_for_text_copy(self) -> None:
        status = host.format_empty_return_file_status()
        self.assertTrue(status.startswith("远端剪贴板没有可回传文件"))
        self.assertIn("Linux 文件管理器", status)

    def test_preview_and_terminal_status_explain_single_batch_limit(self) -> None:
        plan = [host.TransferItem(Path("first.bin"), "first.bin")]
        note = host.build_transfer_preview_note(plan, ignored_count=3)
        self.assertIn("一次最多回传 32 项", note)
        self.assertIn("另有 3 项本次不会传输", note)
        self.assertEqual(
            "远端文件回传完成：32 个，3 个超出单次 32 项限制",
            host.format_return_file_terminal_status(32, 0, ignored_count=3),
        )

    def test_return_plan_emits_windows_compatible_success_and_partial_failure_statuses(self) -> None:
        plan = [
            host.TransferItem(Path("first.bin"), "first.bin"),
            host.TransferItem(Path("second.bin"), "second.bin"),
        ]

        success_statuses: list[tuple[bool, str]] = []
        success_session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        success_session._safe_status = lambda success, message: success_statuses.append((success, message))
        success_session._send_transfer_item_to_viewer = mock.Mock()
        success_terminal = success_session._send_transfer_plan_to_viewer(plan)
        self.assertEqual((True, "远端文件回传完成：2 个"), success_terminal)
        self.assertEqual((True, "Linux returning 2 file(s)."), success_statuses[-1])

        partial_statuses: list[tuple[bool, str]] = []
        partial_session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        partial_session.stop_event = threading.Event()
        partial_session.session_stop = threading.Event()
        partial_session._safe_status = lambda success, message: partial_statuses.append((success, message))
        partial_session._send_transfer_item_to_viewer = mock.Mock(
            side_effect=[None, OSError("synthetic send failure")]
        )
        operation = host.ReturnOperation((), 0)
        partial_terminal = partial_session._send_transfer_plan_to_viewer(
            plan,
            operation=operation,
        )
        self.assertEqual(
            (False, "远端文件回传完成：1 个，1 个失败"),
            partial_terminal,
        )
        self.assertTrue(operation.cancel_reason.startswith("远端文件回传失败"))

        self.assertEqual("远端文件回传失败，2 个失败", host.format_return_file_terminal_status(0, 2))

    def test_probe_confirms_remote_file_preview(self) -> None:
        preview = protocol.encode_file_transfer_clipboard_files_preview(
            [
                {
                    "kind": "文件",
                    "sourcePath": "/tmp/source.bin",
                    "transferName": "source.bin",
                    "sizeBytes": 1,
                    "destinationPath": "receive/source.bin",
                }
            ],
            "",
        )
        remote_failure = protocol.encode_file_transfer_status(False, "synthetic stop")
        incoming = iter(
            (
                (protocol.MESSAGE_CONTROL, preview),
                (protocol.MESSAGE_CONTROL, remote_failure),
            )
        )
        sent_payloads: list[bytes] = []
        result = protocol.ProbeResult("localhost", 56565)

        with (
            tempfile.TemporaryDirectory() as receive_directory,
            mock.patch.object(protocol, "read_message", side_effect=lambda *_args: next(incoming)),
            mock.patch.object(
                protocol,
                "write_message",
                side_effect=lambda _sock, _session, _kind, payload: sent_payloads.append(payload),
            ),
        ):
            protocol.receive_remote_clipboard_files(
                object(),
                object(),
                result,
                receive_directory,
                timeout_seconds=1.0,
                expect_files=False,
                require_checksum=False,
            )

        self.assertEqual(protocol.CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES, sent_payloads[0][0])
        self.assertEqual(protocol.CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES, sent_payloads[1][0])

    def test_return_prepare_failure_uses_windows_terminal_prefix(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.stop_event = threading.Event()
        session.session_stop = threading.Event()
        session.return_condition = threading.Condition()
        session.pending_return_operation = None
        session.pending_return_plan = None
        session.pending_return_ignored_count = 0
        operation = host.ReturnOperation(("/missing/source",), 0)

        with mock.patch.object(host, "resolve_return_files", side_effect=OSError("access denied")):
            success, message = session._run_return_operation(operation)

        self.assertFalse(success)
        self.assertEqual("远端文件回传失败：access denied", message)
        self.assertTrue(operation.cancel_reason.startswith("远端文件回传失败"))

    def test_return_worker_reject_before_preview_emits_one_canonical_terminal(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "return-before-preview.bin"
            source.write_bytes(b"return-before-preview")

            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.args = SimpleNamespace(return_file=[str(source)])
            session.stop_event = threading.Event()
            session.session_stop = threading.Event()
            session.viewer_capabilities = protocol.CAPABILITY_FILE_TRANSFER_PREVIEW
            session.pending_return_plan = None
            session.pending_return_ignored_count = 0
            session.pending_return_operation = None
            session.return_condition = threading.Condition()
            session.return_active_operation = None
            session.return_queued_operation = None
            session.return_worker_stop = False
            session.return_worker_thread = None

            resolve_entered = threading.Event()
            allow_resolve = threading.Event()
            terminal_received = threading.Event()
            statuses: list[tuple[bool, str]] = []
            controls: list[bytes] = []

            def delayed_resolve(
                _paths: list[str],
                *,
                cancel_event: threading.Event | None = None,
            ) -> list[Path]:
                resolve_entered.set()
                self.assertTrue(allow_resolve.wait(2), "test did not release return preparation")
                if cancel_event is not None and cancel_event.is_set():
                    raise host.TransferCancelledError("cancelled before preview")
                return [source]

            def record_status(success: bool, message: str) -> None:
                statuses.append((success, message))
                terminal_received.set()

            session._safe_status = record_status
            session._write_control = controls.append

            with mock.patch.object(host, "resolve_return_files", side_effect=delayed_resolve):
                session._send_requested_files()
                self.assertTrue(resolve_entered.wait(2), "return preparation did not start")
                session._handle_message(
                    protocol.MESSAGE_CONTROL,
                    protocol.encode_file_transfer_reject_clipboard_files(),
                )
                allow_resolve.set()
                self.assertTrue(terminal_received.wait(2), "reject did not produce a terminal status")

            session._handle_message(
                protocol.MESSAGE_CONTROL,
                protocol.encode_file_transfer_reject_clipboard_files(),
            )
            time.sleep(0.05)
            session._stop_return_worker()

            self.assertEqual([(False, "远端文件回传已取消。")], statuses)
            self.assertEqual([], controls)

    @mock.patch.object(host, "read_clipboard_file_paths", return_value=[])
    def test_return_worker_writes_old_terminal_before_queued_retry_preview(
        self, clipboard_paths: mock.Mock,
    ) -> None:
        # Exercise real file preparation and worker ordering, but never probe
        # the user's X11/Wayland clipboard (which may block or contain files).
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "return-order.bin"
            source.write_bytes(b"x" * (protocol.FILE_TRANSFER_CHUNK_BYTES * 2 + 1))

            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.args = SimpleNamespace(return_file=[str(source)])
            session.stop_event = threading.Event()
            session.session_stop = threading.Event()
            session.viewer_capabilities = (
                protocol.CAPABILITY_FILE_TRANSFER_PREVIEW
                | protocol.CAPABILITY_FILE_TRANSFER_CANCEL
            )
            session.pending_return_plan = None
            session.pending_return_ignored_count = 0
            session.pending_return_operation = None
            session.return_condition = threading.Condition()
            session.return_active_operation = None
            session.return_queued_operation = None
            session.return_worker_stop = False
            session.return_worker_thread = None

            controls_lock = threading.Lock()
            controls: list[dict[str, object]] = []
            first_preview = threading.Event()
            second_preview = threading.Event()
            first_chunk = threading.Event()
            release_first_chunk = threading.Event()
            retry_completed = threading.Event()
            preview_count = 0

            def record_control(payload: bytes) -> None:
                nonlocal preview_count
                decoded = protocol.decode_control(payload)
                kind = int(decoded["kind"])
                with controls_lock:
                    controls.append(decoded)
                if kind == protocol.CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW:
                    preview_count += 1
                    (first_preview if preview_count == 1 else second_preview).set()
                elif kind == protocol.CONTROL_FILE_TRANSFER_CHUNK and not first_chunk.is_set():
                    first_chunk.set()
                    if not release_first_chunk.wait(2):
                        raise TimeoutError("test did not release the first transfer chunk")
                elif (
                    kind == protocol.CONTROL_FILE_TRANSFER_STATUS
                    and bool(decoded.get("success"))
                    and str(decoded.get("statusMessage") or "").startswith("远端文件回传完成")
                ):
                    retry_completed.set()

            session._write_control = record_control

            try:
                session._send_requested_files()
                self.assertTrue(first_preview.wait(2), "first preview was not sent")
                session._send_pending_requested_files()
                self.assertTrue(first_chunk.wait(2), "first transfer did not start")

                session._handle_message(
                    protocol.MESSAGE_CONTROL,
                    protocol.encode_file_transfer_reject_clipboard_files(),
                )
                session._send_requested_files()
                with session.return_condition:
                    self.assertIsNotNone(session.return_active_operation)
                    self.assertIsNotNone(session.return_queued_operation)

                release_first_chunk.set()
                self.assertTrue(second_preview.wait(2), "queued retry preview was not sent")

                with controls_lock:
                    cancel_terminal_index = next(
                        index
                        for index, item in enumerate(controls)
                        if int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_STATUS
                        and not bool(item.get("success"))
                        and str(item.get("statusMessage") or "").startswith("远端文件回传已取消")
                    )
                    preview_indexes = [
                        index
                        for index, item in enumerate(controls)
                        if int(item["kind"])
                        == protocol.CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW
                    ]
                self.assertEqual(2, len(preview_indexes))
                self.assertLess(cancel_terminal_index, preview_indexes[1])

                session._send_pending_requested_files()
                self.assertTrue(retry_completed.wait(3), "queued retry did not complete")
            finally:
                release_first_chunk.set()
                session._stop_return_worker()

            self.assertEqual(2, clipboard_paths.call_count)

    def test_return_worker_reject_remains_responsive_and_allows_retry(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory) / "return.bin"
            source.write_bytes(b"x" * (protocol.FILE_TRANSFER_CHUNK_BYTES * 12 + 17))

            session = host.LinuxHostSession.__new__(host.LinuxHostSession)
            session.sock = object()
            session.session = object()
            session.args = SimpleNamespace(return_file=[str(source)])
            session.stop_event = threading.Event()
            session.session_stop = threading.Event()
            session.write_lock = threading.Lock()
            session.inbound_liveness = host.HostInboundLivenessTracker()
            session.viewer_capabilities = (
                protocol.CAPABILITY_FILE_TRANSFER_PREVIEW
                | protocol.CAPABILITY_FILE_TRANSFER_CANCEL
            )
            session.incoming = None
            session.pending_return_plan = None
            session.pending_return_ignored_count = 0
            session.pending_return_operation = None
            session.return_condition = threading.Condition()
            session.return_active_operation = None
            session.return_queued_operation = None
            session.return_worker_stop = False
            session.return_worker_thread = None

            incoming_messages: queue.Queue[tuple[int, bytes] | None] = queue.Queue()
            output_lock = threading.Lock()
            controls: list[dict[str, object]] = []
            preview_events = [threading.Event(), threading.Event()]
            first_chunk = threading.Event()
            pong_received = threading.Event()
            input_received = threading.Event()
            cancelled_terminal = threading.Event()
            completed_terminal = threading.Event()
            preview_count = 0

            def fake_read_message(_sock: object, _session: object) -> tuple[int, bytes]:
                message = incoming_messages.get(timeout=3)
                if message is None:
                    raise EOFError
                return message

            def fake_write_message(
                _sock: object,
                _session: object,
                message_type: int,
                payload: bytes,
            ) -> None:
                nonlocal preview_count
                if message_type == protocol.MESSAGE_PONG:
                    pong_received.set()
                    return
                if message_type != protocol.MESSAGE_CONTROL:
                    return
                decoded = protocol.decode_control(payload)
                with output_lock:
                    controls.append(decoded)
                kind = int(decoded["kind"])
                if kind == protocol.CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW:
                    preview_count += 1
                    preview_events[min(preview_count, 2) - 1].set()
                elif kind == protocol.CONTROL_FILE_TRANSFER_CHUNK:
                    first_chunk.set()
                    time.sleep(0.02)
                elif kind == protocol.CONTROL_FILE_TRANSFER_STATUS:
                    message = str(decoded.get("statusMessage") or "")
                    if message.startswith("远端文件回传已取消"):
                        cancelled_terminal.set()
                    if bool(decoded.get("success")) and message.startswith("远端文件回传完成"):
                        completed_terminal.set()

            session._handle_input = lambda _payload: input_received.set()

            with (
                mock.patch.object(host, "read_message", side_effect=fake_read_message),
                mock.patch.object(host, "write_message", side_effect=fake_write_message),
                mock.patch.object(host, "rearm_tcp_quickack", return_value=True),
                mock.patch.object(host, "resolve_return_files", return_value=[source]),
            ):
                reader = threading.Thread(target=session._read_loop, name="return-test-reader")
                reader.start()

                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_request_clipboard_files(),
                    )
                )
                self.assertTrue(preview_events[0].wait(2), "first preview was not sent")
                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_confirm_clipboard_files(),
                    )
                )
                self.assertTrue(first_chunk.wait(2), "first transfer did not start")
                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_reject_clipboard_files(),
                    )
                )
                incoming_messages.put((protocol.MESSAGE_INPUT, b"synthetic-input"))
                incoming_messages.put((protocol.MESSAGE_PING, b""))

                self.assertTrue(input_received.wait(1), "input was not read during return transfer")
                self.assertTrue(pong_received.wait(1), "ping was not answered during return transfer")
                self.assertTrue(cancelled_terminal.wait(2), "reject did not finish cancellation")
                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_reject_clipboard_files(),
                    )
                )

                with output_lock:
                    first_chunks_after_cancel = sum(
                        int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_CHUNK
                        for item in controls
                    )
                    self.assertLessEqual(first_chunks_after_cancel, 2)
                time.sleep(0.1)
                with output_lock:
                    self.assertEqual(
                        first_chunks_after_cancel,
                        sum(
                            int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_CHUNK
                            for item in controls
                        ),
                    )
                    self.assertEqual(
                        1,
                        sum(
                            int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_CANCEL
                            for item in controls
                        ),
                    )

                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_request_clipboard_files(),
                    )
                )
                self.assertTrue(preview_events[1].wait(2), "retry preview was not sent")
                incoming_messages.put(
                    (
                        protocol.MESSAGE_CONTROL,
                        protocol.encode_file_transfer_confirm_clipboard_files(),
                    )
                )
                self.assertTrue(completed_terminal.wait(3), "retry did not complete")

                with output_lock:
                    self.assertEqual(
                        1,
                        sum(
                            int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_COMPLETE
                            for item in controls
                        ),
                    )
                    self.assertEqual(
                        1,
                        sum(
                            int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_CANCEL
                            for item in controls
                        ),
                    )
                    self.assertEqual(
                        1,
                        sum(
                            int(item["kind"]) == protocol.CONTROL_FILE_TRANSFER_STATUS
                            and not bool(item.get("success"))
                            and str(item.get("statusMessage") or "").startswith("远端文件回传已取消")
                            for item in controls
                        ),
                    )

                incoming_messages.put(None)
                reader.join(timeout=2)
                self.assertFalse(reader.is_alive())
                worker = session.return_worker_thread
                session._stop_return_worker()
                self.assertIsNotNone(worker)
                self.assertFalse(worker.is_alive())

    def test_viewer_failure_status_cancels_and_clears_pending_return(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        operation = host.ReturnOperation((), 0)
        plan = [host.TransferItem(Path("pending.bin"), "pending.bin")]
        operation.plan = plan
        operation.preview_ready = True
        session.return_condition = threading.Condition()
        session.return_active_operation = operation
        session.return_queued_operation = None
        session.pending_return_operation = operation
        session.pending_return_plan = plan
        session.pending_return_ignored_count = 0

        session._handle_message(
            protocol.MESSAGE_CONTROL,
            protocol.encode_file_transfer_status(False, "viewer disk full"),
        )

        self.assertTrue(operation.cancel_event.is_set())
        self.assertTrue(operation.cancel_reason.startswith("远端文件回传已取消"))
        self.assertIn("viewer disk full", operation.cancel_reason)
        self.assertIsNone(session.pending_return_operation)
        self.assertIsNone(session.pending_return_plan)


if __name__ == "__main__":
    unittest.main()
