"""Heartbeat pressure must not stall the host's input reader."""
import sys
import threading
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host


class HostHeartbeatTests(unittest.TestCase):
    def test_input_dispatch_does_not_wait_for_blocked_pong(self):
        entered, release, applied = threading.Event(), threading.Event(), threading.Event()
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        failures = mock.Mock()
        def blocked_send():
            entered.set()
            release.wait(3)
        session._write_message = lambda kind, payload: blocked_send()
        session.heartbeat = host.HostHeartbeatResponder(blocked_send, failures)
        session._handle_input = lambda payload: applied.set()
        def read_messages():
            session._handle_message(host.MESSAGE_PING, b"")
            session._handle_message(host.MESSAGE_INPUT, b"owned fixture input")
        reader = threading.Thread(target=read_messages)
        try:
            reader.start()
            self.assertTrue(entered.wait(1))
            self.assertTrue(applied.wait(.3), "Pong write blocked subsequent input")
            self.assertFalse(release.is_set())
        finally:
            release.set()
            reader.join(2)
            session.heartbeat.close()
        failures.assert_not_called()

    def test_ping_flood_keeps_one_pending_reply(self):
        entered, release, second, finish = (threading.Event() for _ in range(4))
        sends = []
        def send():
            sends.append(1)
            if len(sends) == 1:
                entered.set()
                release.wait(3)
            else:
                second.set()
                finish.wait(3)
        worker = host.HostHeartbeatResponder(send, mock.Mock())
        try:
            worker.request()
            self.assertTrue(entered.wait(1))
            for _ in range(10_000):
                self.assertTrue(worker.request())
            self.assertEqual(1, len(sends))
            release.set()
            self.assertTrue(second.wait(1))
            self.assertEqual(2, len(sends))
        finally:
            release.set()
            finish.set()
            worker.close()

    def test_failed_write_closes_session_and_rejects_requests(self):
        failed = threading.Event()
        worker = host.HostHeartbeatResponder(mock.Mock(side_effect=OSError("fixture")), failed.set)
        try:
            worker.request()
            self.assertTrue(failed.wait(1))
            self.assertFalse(worker.request())
        finally:
            worker.close()

    def test_close_drops_pending_reply_before_socket_shutdown_releases_writer(self):
        entered, release = threading.Event(), threading.Event()
        sends = []
        def send():
            sends.append(1)
            entered.set()
            release.wait(3)
        worker = host.HostHeartbeatResponder(send, mock.Mock())
        worker.request()
        self.assertTrue(entered.wait(1))
        worker.request()
        closing = threading.Thread(target=worker.close)
        closing.start()
        # Wait for the close signal, not an arbitrary sleep.
        with worker.condition:
            self.assertTrue(worker.condition.wait_for(lambda: worker.stopped, timeout=1))
        release.set()
        closing.join(2)
        self.assertFalse(worker.thread.is_alive())
        self.assertFalse(worker.request())
        self.assertEqual(1, len(sends))

    def test_idle_close_does_not_create_a_thread_or_send(self):
        send = mock.Mock()
        worker = host.HostHeartbeatResponder(send, mock.Mock())
        worker.close()
        self.assertFalse(worker.request())
        self.assertIsNone(worker.thread)
        send.assert_not_called()


if __name__ == "__main__":
    unittest.main()
