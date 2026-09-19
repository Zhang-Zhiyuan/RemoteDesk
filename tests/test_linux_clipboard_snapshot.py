"""Snapshot v1 wire and real host worker tests; never read a user's clipboard."""
from collections import deque
import gc
import hashlib
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host
import remotedesk_protocol_probe as wire


SAMPLE = "中文😀\r\n"
SAMPLE_REVISION = "aaa697b842c3524351ee913227e6ffe0911a5648b10e41070cd672243bf7b19d"
EMPTY_REVISION = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"


class ClipboardSnapshotWireTests(unittest.TestCase):
    def test_binarywriter_cross_language_request_and_unicode_snapshot_fixture(self):
        self.assertEqual(bytes.fromhex("2802696400"), wire.encode_clipboard_snapshot_request("id", ""))
        # Independent .NET BinaryWriter shape, including UTF-8 byte length and
        # SHA256 computed by System.Security.Cryptography on the same string.
        expected = (bytes.fromhex("290269640140") + SAMPLE_REVISION.encode("ascii")
                    + bytes.fromhex("01010ce4b8ade69687f09f98800d0a00"))
        actual = wire.encode_clipboard_snapshot("id", True, SAMPLE_REVISION, True, True, SAMPLE)
        self.assertEqual(expected, actual)
        decoded = wire.decode_control(expected, include_clipboard_text=True)
        self.assertEqual(("id", SAMPLE_REVISION, True, True, SAMPLE),
                         (decoded["requestId"], decoded["revision"], decoded["hasText"], decoded["changed"], decoded["text"]))
        self.assertNotIn("text", wire.decode_control(expected))
        self.assertEqual(hashlib.sha256(SAMPLE.encode("utf-8")).hexdigest(), SAMPLE_REVISION)

    def test_empty_unchanged_and_failure_are_distinct(self):
        cases = [(True, EMPTY_REVISION, False, True, "", ""),
                 (True, SAMPLE_REVISION, True, False, "", ""),
                 (False, "", False, False, "", "unavailable")]
        for case in cases:
            with self.subTest(case=case):
                value = wire.decode_control(wire.encode_clipboard_snapshot("id", *case), include_clipboard_text=True)
                self.assertEqual(case, (value["success"], value["revision"], value["hasText"], value["changed"], value["text"], value["statusMessage"]))

    def test_request_bounds_revision_validation_and_trailing_data(self):
        for request_id in ("", "a" * 65, "😀" * 33):
            with self.subTest(request_id=request_id), self.assertRaises(wire.ProtocolError):
                wire.encode_clipboard_snapshot_request(request_id, "")
        for revision in ("a" * 63, "A" * 64, "g" * 64):
            with self.subTest(revision=revision), self.assertRaises(wire.ProtocolError):
                wire.encode_clipboard_snapshot_request("id", revision)
        for payload in (wire.encode_clipboard_snapshot_request("id", SAMPLE_REVISION),
                        wire.encode_clipboard_snapshot("id", True, SAMPLE_REVISION, True, False, "")):
            with self.assertRaises(wire.ProtocolError):
                wire.decode_control(payload + b"\0")
        accepted = wire.decode_control(wire.encode_clipboard_snapshot_request("😀" * 32, SAMPLE_REVISION))
        self.assertEqual(SAMPLE_REVISION, accepted["knownRevision"])

    def test_snapshot_rejects_truncation_wrong_revision_and_inconsistent_text(self):
        for case in [(True, SAMPLE_REVISION, True, False, SAMPLE),
                     (False, "", False, False, SAMPLE),
                     (True, EMPTY_REVISION, True, True, SAMPLE),
                     (True, SAMPLE_REVISION, False, True, SAMPLE),
                     (True, "", False, True, "")]:
            with self.subTest(case=case), self.assertRaises(wire.ProtocolError):
                wire.encode_clipboard_snapshot("id", *case)
        for text in ("x" * 256001, "😀" * 128001):
            with self.assertRaises(wire.ProtocolError):
                wire.encode_clipboard_snapshot("id", True, hashlib.sha256(text.encode()).hexdigest(), True, True, text)
        data = wire.encode_clipboard_snapshot("id", True, SAMPLE_REVISION, True, True, SAMPLE)
        for length in (1, 4, len(data) - 1):
            with self.assertRaises(wire.ProtocolError):
                wire.decode_control(data[:length])


class ClipboardSnapshotWorkerTests(unittest.TestCase):
    def setUp(self):
        # Earlier Tk tests may leave destroyed interpreter cycles. Collect
        # those on their owning unittest thread before starting our workers.
        gc.collect()

    def make_session(self):
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.stop_event = threading.Event()
        session.session_stop = threading.Event()
        session.clipboard_condition = threading.Condition()
        session.clipboard_queue = deque()
        session.clipboard_worker_stop = False
        session.clipboard_worker_thread = None
        session.viewer_capabilities = wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
        session.host_capabilities = wire.CAPABILITY_CLIPBOARD_TEXT | wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
        output = queue.Queue()
        session._write_control = output.put
        def close():
            worker = session.clipboard_worker_thread
            session._stop_clipboard_worker()
            if worker is not None:
                worker.join(2)
                self.assertFalse(worker.is_alive())
        self.addCleanup(close)
        return session, output

    def request(self, session, request_id="id", revision=""):
        session._handle_message(wire.MESSAGE_CONTROL, wire.encode_clipboard_snapshot_request(request_id, revision))

    def receive(self, output):
        return wire.decode_control(output.get(timeout=2), include_clipboard_text=True)

    def test_actual_worker_reads_off_request_thread_and_returns_correlated_snapshots(self):
        session, output = self.make_session()
        caller = threading.get_ident()
        worker_threads = []
        def read(_active):
            worker_threads.append(threading.get_ident())
            return SAMPLE
        with mock.patch.object(host, "read_clipboard_snapshot_text", side_effect=read):
            self.request(session)
            changed = self.receive(output)
            self.request(session, "second", SAMPLE_REVISION)
            unchanged = self.receive(output)
        self.assertTrue(all(value != caller for value in worker_threads))
        self.assertEqual((41, "id", SAMPLE, True), (changed["kind"], changed["requestId"], changed["text"], changed["changed"]))
        self.assertEqual(("second", "", False, True), (unchanged["requestId"], unchanged["text"], unchanged["changed"], unchanged["hasText"]))

    @unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("xvfb-run") and shutil.which("xclip"),
                         "Native X11 helper test requires existing xvfb-run and xclip")
    def test_native_x11_worker_in_independent_owned_display(self):
        if os.environ.get("REMOTEDESK_SNAPSHOT_OWNED_X11") != "1":
            # Always create a fresh display: never replace the real user's
            # clipboard, even when unittest itself is run on their desktop.
            environment = dict(os.environ, WAYLAND_DISPLAY="", REMOTEDESK_SNAPSHOT_OWNED_X11="1")
            result = subprocess.run(
                ["xvfb-run", "-a", "-s", "-screen 0 640x480x24 -nolisten tcp", sys.executable,
                 str(Path(__file__).resolve()),
                 "ClipboardSnapshotWorkerTests.test_native_x11_worker_in_independent_owned_display"],
                env=environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            return

        session, output = self.make_session()
        owners = []
        def close_owners():
            for owner in owners:
                if owner.poll() is None:
                    owner.kill()
                owner.wait(timeout=2)
        self.addCleanup(close_owners)

        def publish(payload, target):
            owner = subprocess.Popen(
                ["xclip", "-quiet", "-selection", "clipboard", "-in", "-target", target],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            owners.append(owner)
            owner.stdin.write(payload)
            owner.stdin.close()
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                formats = host._read_clipboard_command_bounded(
                    ["xclip", "-selection", "clipboard", "-o", "-t", "TARGETS"],
                    deadline, lambda: True, 16 * 1024)
                if formats is not None and target.encode() in formats.splitlines():
                    return
                time.sleep(.01)
            self.fail("Owned X11 clipboard owner did not become ready.")

        publish(SAMPLE.encode(), "UTF8_STRING")
        self.request(session, "native-text")
        first = self.receive(output)
        self.assertEqual((True, SAMPLE, SAMPLE_REVISION, True),
                         (first["success"], first["text"], first["revision"], first["changed"]))
        self.request(session, "native-unchanged", SAMPLE_REVISION)
        second = self.receive(output)
        self.assertEqual((True, "", SAMPLE_REVISION, False, True),
                         (second["success"], second["text"], second["revision"], second["changed"], second["hasText"]))

        publish(SAMPLE.encode(), "text/plain")
        self.request(session, "native-plain-text")
        plain_selection = self.receive(output)
        self.assertEqual((True, SAMPLE, SAMPLE_REVISION),
                         (plain_selection["success"], plain_selection["text"], plain_selection["revision"]))

        publish(b"owned synthetic non-text fixture", "image/png")
        self.request(session, "native-image", SAMPLE_REVISION)
        image_selection = self.receive(output)
        self.assertEqual((True, "", EMPTY_REVISION, True, False),
                         (image_selection["success"], image_selection["text"], image_selection["revision"],
                          image_selection["changed"], image_selection["hasText"]))

        publish(b"", "UTF8_STRING")
        self.request(session, "native-empty", EMPTY_REVISION)
        empty_selection = self.receive(output)
        self.assertEqual((True, "", EMPTY_REVISION, False, False),
                         (empty_selection["success"], empty_selection["text"], empty_selection["revision"],
                          empty_selection["changed"], empty_selection["hasText"]))

    def test_actual_worker_returns_empty_or_failure_without_legacy_ack(self):
        for supplied in ("", None, OSError("fixture denied"), "x" * 256001):
            with self.subTest(supplied=type(supplied).__name__):
                session, output = self.make_session()
                with mock.patch.object(host, "read_clipboard_snapshot_text", side_effect=supplied if isinstance(supplied, Exception) else None,
                                       return_value=supplied):
                    self.request(session)
                    result = self.receive(output)
                self.assertEqual(41, result["kind"])
                self.assertEqual("id", result["requestId"])
                self.assertFalse(result["hasText"])
                self.assertEqual("", result["text"])
                self.assertEqual(supplied == "", result["success"])
                self.assertEqual(EMPTY_REVISION if supplied == "" else "", result["revision"])

    def test_no_capability_no_read_or_reply_and_missing_permission_returns_correlated_failure(self):
        session, output = self.make_session()
        with mock.patch.object(host, "read_clipboard_snapshot_text") as read:
            session.viewer_capabilities = 0
            self.request(session)
            read.assert_not_called()
            self.assertTrue(output.empty())
            session.viewer_capabilities = wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
            session.host_capabilities = wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
            self.request(session, "denied")
            result = self.receive(output)
            read.assert_not_called()
        self.assertEqual((41, "denied", False, ""), (result["kind"], result["requestId"], result["success"], result["text"]))

    def test_queue_is_bounded_and_busy_reply_has_its_own_id(self):
        session, output = self.make_session()
        with mock.patch.object(session, "_start_clipboard_worker"):
            for index in range(host.CLIPBOARD_OPERATION_QUEUE_LIMIT):
                self.request(session, str(index))
            self.request(session, "busy")
        result = self.receive(output)
        self.assertEqual(("busy", False, 41), (result["requestId"], result["success"], result["kind"]))
        self.assertEqual([str(index) for index in range(host.CLIPBOARD_OPERATION_QUEUE_LIMIT)],
                         [operation.request_id for operation in session.clipboard_queue])

    def test_set_snapshot_set_snapshot_preserves_order_and_old_ack_types(self):
        session, output = self.make_session()
        current = {"text": ""}
        def write(text):
            current["text"] = text
            return True
        with mock.patch.object(host, "write_clipboard_text", side_effect=write), \
                mock.patch.object(host, "read_clipboard_snapshot_text", side_effect=lambda _active: current["text"]):
            with mock.patch.object(session, "_start_clipboard_worker"):
                session._handle_clipboard_set("first")
                self.request(session, "one")
                session._handle_clipboard_set("second")
                self.request(session, "two")
            session._start_clipboard_worker()
            result = [self.receive(output) for _ in range(4)]
        self.assertEqual([7, 41, 7, 41], [value["kind"] for value in result])
        self.assertEqual([("one", "first"), ("two", "second")],
                         [(value["requestId"], value["text"]) for value in result if value["kind"] == 41])

    def test_stop_or_permission_revocation_fences_late_snapshot(self):
        for revoke in ("stop", "permission", "capability"):
            with self.subTest(revoke=revoke):
                session, output = self.make_session()
                entered, release = threading.Event(), threading.Event()
                def read(_active):
                    entered.set()
                    release.wait(2)
                    return SAMPLE
                with mock.patch.object(host, "read_clipboard_snapshot_text", side_effect=read):
                    self.request(session)
                    self.assertTrue(entered.wait(1))
                    if revoke == "stop":
                        session.session_stop.set()
                    elif revoke == "permission":
                        session.host_capabilities = wire.CAPABILITY_CLIPBOARD_SNAPSHOT_V1
                    else:
                        session.viewer_capabilities = 0
                    release.set()
                    if revoke == "permission":
                        self.assertFalse(self.receive(output)["success"])
                    else:
                        session._stop_clipboard_worker()
                        self.assertTrue(output.empty())


class BoundedClipboardSnapshotReaderTests(unittest.TestCase):
    def test_non_text_types_do_not_read_contents_and_missing_tools_are_unavailable(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                mock.patch.object(host.shutil, "which", side_effect=lambda name: name if name == "xclip" else None), \
                mock.patch.object(host, "_read_clipboard_command_bounded", return_value=b"TARGETS\nimage/png\ntext/uri-list\n") as command:
            self.assertEqual("", host.read_clipboard_snapshot_text())
            command.assert_called_once()
        with mock.patch.object(host.shutil, "which", return_value=None):
            self.assertIsNone(host.read_clipboard_snapshot_text())

    def test_plain_text_reader_preserves_unicode_newlines_and_rejects_over_limit(self):
        for text in (SAMPLE, "x" * 256001):
            with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": ""}), \
                    mock.patch.object(host.shutil, "which", side_effect=lambda name: name if name == "xclip" else None), \
                    mock.patch.object(host, "_read_clipboard_command_bounded", side_effect=[b"UTF8_STRING\n", text.encode()]):
                if text == SAMPLE:
                    self.assertEqual(SAMPLE, host.read_clipboard_snapshot_text())
                else:
                    with self.assertRaises(wire.ProtocolError):
                        host.read_clipboard_snapshot_text()

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux pipe selector test")
    def test_real_owned_subprocess_is_bounded_by_output_time_and_cancellation(self):
        cases = [("import sys;sys.stdout.buffer.write(b'x'*2000000)", 2, lambda: True, wire.ProtocolError),
                 ("import time;time.sleep(5)", .15, lambda: True, TimeoutError),
                 ("import time;time.sleep(5)", 2, lambda: False, wire.TransferCancelledError)]
        for code, seconds, active, expected in cases:
            started = time.monotonic()
            with self.subTest(expected=expected.__name__), self.assertRaises(expected):
                host._read_clipboard_command_bounded([sys.executable, "-c", code], started + seconds, active, 1024)
            self.assertLess(time.monotonic() - started, 1.5)
        result = host._read_clipboard_command_bounded(
            [sys.executable, "-c", "import sys;sys.stdout.buffer.write(bytes.fromhex('e4b8ad'))"],
            time.monotonic() + 2, lambda: True, 1024)
        self.assertEqual("中".encode(), result)


if __name__ == "__main__":
    unittest.main()
