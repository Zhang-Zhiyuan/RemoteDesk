import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
from remotedesk_linux_control_notice import HostControlNotice
import remotedesk_linux_host as host


class ControlNoticeTests(unittest.TestCase):
    def test_replacement_cleanup_does_not_remove_new_notice(self):
        notice = HostControlNotice(enabled=False)
        self.assertFalse(notice.snapshot()[0])
        old = notice.begin(mock.Mock())
        new = notice.begin(mock.Mock())
        notice.end(old)
        notice.end(old)
        self.assertTrue(notice.snapshot()[0])
        notice.end(new)
        self.assertFalse(notice.snapshot()[0])

    def test_stale_click_cannot_disconnect_a_new_session(self):
        notice = HostControlNotice(enabled=False)
        first, second = mock.Mock(), mock.Mock()
        old = notice.begin(first)
        revision = notice.snapshot()[1]
        notice.end(old)
        notice.begin(second)
        self.assertFalse(notice.disconnect(revision))
        first.assert_not_called()
        second.assert_not_called()
        self.assertTrue(notice.disconnect(notice.snapshot()[1]))
        second.assert_called_once()

    def test_disconnect_callback_can_release_its_lease(self):
        notice = HostControlNotice(enabled=False)
        token = notice.begin(lambda: notice.end(token))
        worker = threading.Thread(target=notice.disconnect, args=(notice.snapshot()[1],), daemon=True)
        worker.start()
        worker.join(1)
        self.assertFalse(worker.is_alive(), "Disconnect deadlocked on the notice lock")
        self.assertFalse(notice.snapshot()[0])

    def test_close_prevents_late_sessions_and_clicks(self):
        notice = HostControlNotice(enabled=False)
        callback = mock.Mock()
        notice.begin(callback)
        revision = notice.snapshot()[1]
        notice.close()
        notice.begin(callback)
        self.assertFalse(notice.snapshot()[0])
        self.assertFalse(notice.disconnect(revision))
        callback.assert_not_called()

    def test_failed_disconnect_does_not_skip_other_owned_sessions(self):
        log = mock.Mock()
        notice = HostControlNotice(enabled=False, log=log)
        notice.begin(mock.Mock(side_effect=OSError()))
        other = mock.Mock()
        notice.begin(other)
        notice.disconnect(notice.snapshot()[1])
        other.assert_called_once()
        log.assert_called_once()

    def test_failed_disconnect_publishes_fresh_revision_for_button_retry(self):
        notice = HostControlNotice(enabled=False, log=mock.Mock())
        callback = mock.Mock(side_effect=[OSError(), None])
        notice.begin(callback)
        revision = notice.snapshot()[1]
        notice.changed.clear()
        self.assertTrue(notice.disconnect(revision))
        self.assertTrue(notice.changed.is_set())
        self.assertGreater(notice.snapshot()[1], revision)
        self.assertFalse(notice.disconnect(revision))
        self.assertTrue(notice.disconnect(notice.snapshot()[1]))
        self.assertEqual(2, callback.call_count)

    def test_same_revision_disconnect_is_consumed_once_even_during_callback(self):
        notice = HostControlNotice(enabled=False)
        calls = []
        notice.begin(lambda: calls.append(notice.disconnect(revision)))
        revision = notice.snapshot()[1]
        self.assertTrue(notice.disconnect(revision))
        self.assertEqual([False], calls)
        self.assertFalse(notice.disconnect(revision))

    def test_failed_old_disconnect_does_not_reset_new_session_revision(self):
        notice = HostControlNotice(enabled=False, log=mock.Mock())
        observations = []
        def replace_then_fail():
            notice.end(token)
            notice.begin(mock.Mock())
            observations.append(notice.snapshot()[1])
            raise OSError()
        token = notice.begin(replace_then_fail)
        notice.disconnect(notice.snapshot()[1])
        self.assertEqual(observations[0], notice.snapshot()[1])

    def _run_admitted(self, authentication):
        client = mock.Mock()
        gate = host.ClientAdmissionGate()
        gate.try_register_pending(client)
        notice = HostControlNotice(enabled=False)
        observations = []
        with mock.patch.object(host, "authenticate_server", authentication), \
                mock.patch.object(host, "configure_low_latency_socket"), \
                mock.patch.object(host, "LinuxHostSession") as session, \
                mock.patch.object(host, "log"):
            session.return_value.run.side_effect = lambda: observations.append(notice.snapshot()[0])
            host.run_admitted_client(client, ("127.0.0.1", 12345),
                SimpleNamespace(password="owned-fixture", auth_timeout=1), threading.Event(), gate, notice)
        self.assertFalse(notice.snapshot()[0])
        return observations

    def test_actual_host_path_shows_notice_only_after_authentication(self):
        self.assertEqual([True], self._run_admitted(mock.Mock(return_value=object())))

    def test_authentication_failure_does_not_show_notice(self):
        self.assertEqual([], self._run_admitted(mock.Mock(side_effect=PermissionError("fixture"))))

    def test_incomplete_tcp_probe_does_not_show_notice(self):
        self.assertEqual([], self._run_admitted(mock.Mock(side_effect=EOFError())))

    def test_terminal_reason_is_sent_before_waking_socket_cleanup(self):
        stopped = threading.Event()
        observations = []
        with mock.patch.object(host, "write_message", side_effect=lambda *_: observations.append(stopped.is_set())), \
                mock.patch.object(host, "close_client_socket"):
            host.replace_authenticated_client(mock.Mock(), mock.Mock(), threading.Lock(), stopped,
                message="被控端已主动断开远程控制；已停止自动重连。")
        self.assertEqual([False], observations)
        self.assertTrue(stopped.is_set())


if __name__ == "__main__":
    unittest.main()
