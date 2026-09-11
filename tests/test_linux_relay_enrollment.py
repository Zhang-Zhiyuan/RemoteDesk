import asyncio
from dataclasses import replace
from pathlib import Path
import queue
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_relay as relay
import remotedesk_linux_app as app

TOKEN = "owned-enrollment-test-token-" * 2
ID = "8220b49b-0f2f-4f87-913a-c3f95091500f"
DRAFT = relay.RelayEnrollmentDraft("relay.test", 56567, TOKEN, ID).validate()
SAVED = DRAFT.with_pin("ab" * 32)


class EnrollmentPolicyTests(unittest.TestCase):
    def test_first_use_has_no_pin_and_runtime_options_still_require_one(self):
        self.assertIsNone(DRAFT.known_pin(None))
        with self.assertRaises(ValueError):
            DRAFT.with_pin("")
        self.assertNotIn(TOKEN, repr(DRAFT))

    def test_legacy_configuration_is_reused_only_for_same_endpoint(self):
        self.assertEqual(SAVED.tls_certificate_sha256, DRAFT.known_pin(SAVED))
        self.assertEqual(SAVED.tls_certificate_sha256, replace(DRAFT, server_address="RELAY.TEST").known_pin(SAVED))
        for changed in (replace(DRAFT, port=443), replace(DRAFT, server_address="elsewhere.test")):
            self.assertIsNone(changed.known_pin(SAVED))

    def test_manual_replacement_is_explicit_and_normalized(self):
        pin = DRAFT.known_pin(SAVED, ":".join(["CD"] * 32))
        self.assertTrue(DRAFT.replaces_identity(SAVED, pin))
        self.assertFalse(DRAFT.replaces_identity(SAVED, SAVED.tls_certificate_sha256))
        with self.assertRaises(ValueError):
            DRAFT.known_pin(SAVED, "invalid")

    def test_invalid_draft_does_not_wait_for_network(self):
        for change in (dict(server_address="https://host"), dict(port=True), dict(port=12.5),
                       dict(port=0), dict(access_token="0"), dict(device_id="bad")):
            with self.subTest(change=change), self.assertRaises(ValueError):
                replace(DRAFT, **change).validate()

    def test_cancel_before_start_never_creates_network_task(self):
        stop, operation = threading.Event(), mock.Mock()
        stop.set()
        with self.assertRaises(ConnectionAbortedError):
            relay.run_setup_request(operation, stop)
        operation.assert_not_called()

    def test_cancel_running_worker_cleans_up_operation(self):
        stop, cleaned = threading.Event(), []
        async def operation():
            try:
                stop.set()
                await asyncio.Event().wait()
            finally:
                cleaned.append(True)
        with self.assertRaises(ConnectionAbortedError):
            relay.run_setup_request(operation, stop)
        self.assertEqual([True], cleaned)

    def test_worker_returns_results_and_propagates_failure(self):
        async def success(): return [1, 2]
        self.assertEqual([1, 2], relay.run_setup_request(success, threading.Event()))
        async def failure(): raise relay.RelayIdentityError("synthetic secret")
        with self.assertRaises(relay.RelayIdentityError):
            relay.run_setup_request(failure, threading.Event())
        self.assertNotIn("synthetic secret", relay.setup_error_message(relay.RelayIdentityError("synthetic secret")))


class EnrollmentUiStateTests(unittest.TestCase):
    def setUp(self):
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        self.ui.closing = False
        self.ui.root = mock.Mock()
        self.ui.relay_options = SAVED
        self.ui.relay_device_id = ID
        self.ui.relay_setup_generation = 4
        self.ui.relay_setup_busy = True
        self.ui.relay_setup_cancel = threading.Event()
        self.ui.relay_setup_controls = [mock.Mock() for _ in range(4)]
        self.ui.relay_cancel_button = mock.Mock()
        self.ui.relay_status = mock.Mock()
        self.ui.relay_server, self.ui.relay_port, self.ui.relay_pin = mock.Mock(), mock.Mock(), mock.Mock()
        self.ui.relay_list = mock.Mock()
        self.ui.relay_list.get_children.return_value = ()
        self.ui.events = queue.Queue()
        self.ui._sync_relay_registration = mock.Mock()

    def test_cancel_ignores_late_verified_result(self):
        stop = self.ui.relay_setup_cancel
        self.ui._cancel_relay_setup()
        self.assertTrue(stop.is_set())
        with mock.patch.object(relay, "save_settings") as save:
            self.ui._handle_relay_setup((4, "verified", replace(SAVED, port=443), [], ""))
        save.assert_not_called()
        self.assertIs(SAVED, self.ui.relay_options)

    def test_failed_verification_preserves_old_config(self):
        with mock.patch.object(relay, "save_settings") as save:
            self.ui._handle_relay_setup((4, "verified", replace(SAVED, port=443), None, "failed"))
        save.assert_not_called()
        self.assertIs(SAVED, self.ui.relay_options)
        self.assertFalse(self.ui.relay_setup_busy)

    def test_disk_failure_preserves_old_config_and_registration(self):
        with mock.patch.object(relay, "save_settings", side_effect=OSError("read only")):
            self.ui._handle_relay_setup((4, "verified", replace(SAVED, port=443), [], ""))
        self.assertIs(SAVED, self.ui.relay_options)
        self.ui._sync_relay_registration.assert_not_called()
        self.assertFalse(self.ui.relay_setup_busy)

    def test_verified_configuration_is_saved_before_applied(self):
        new = replace(SAVED, port=443)
        def persist(options):
            self.assertIs(new, options)
            self.assertIs(SAVED, self.ui.relay_options)
        with mock.patch.object(relay, "save_settings", side_effect=persist):
            self.ui._handle_relay_setup((4, "verified", new, [], ""))
        self.assertIs(new, self.ui.relay_options)
        self.ui._sync_relay_registration.assert_called_once()
        self.assertFalse(self.ui.relay_setup_busy)

    def test_rejected_trust_never_authenticates_or_saves(self):
        with mock.patch.object(app.messagebox, "askyesno", return_value=False), \
                mock.patch.object(self.ui, "_verify_relay_setup") as verify:
            self.ui._handle_relay_setup((4, "identity", DRAFT, SAVED.tls_certificate_sha256, ""))
        verify.assert_not_called()
        self.assertIs(SAVED, self.ui.relay_options)

    def test_close_while_trust_dialog_open_does_not_authenticate(self):
        def close(*args, **kwargs):
            self.ui.closing = True
            self.ui._cancel_relay_setup()
            return True
        with mock.patch.object(app.messagebox, "askyesno", side_effect=close), \
                mock.patch.object(self.ui, "_verify_relay_setup") as verify:
            self.ui._confirm_relay_identity(DRAFT, SAVED.tls_certificate_sha256)
        verify.assert_not_called()


class EnrollmentTkTests(unittest.TestCase):
    def setUp(self):
        try:
            root = app.tk.Tk()
        except app.tk.TclError:
            self.skipTest("Tk display unavailable")
        self.addCleanup(root.destroy)
        EnrollmentUiStateTests.setUp(self)
        self.ui.root = root
        self.ui.relay_setup_busy = False
        self.ui.relay_refreshing = False
        self.ui.relay_refresh_generation = 0
        self.ui._configure_style()
        root.geometry("1180x760")
        notebook = app.ttk.Notebook(root)
        notebook.pack(fill=app.tk.BOTH, expand=True)
        with mock.patch.object(relay, "load_settings", return_value=SAVED):
            self.ui._build_relay_tab(notebook)
        root.update()
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "relay.json"
        relay.save_settings(SAVED, self.path)
        patcher = mock.patch.object(relay, "settings_path", return_value=self.path)
        patcher.start()
        self.addCleanup(patcher.stop)

    def finish_setup(self):
        deadline = time.monotonic() + 4
        while self.ui.relay_setup_busy and time.monotonic() < deadline:
            self.ui.root.update()
            try:
                event, value = self.ui.events.get(timeout=.02)
            except queue.Empty:
                continue
            self.assertEqual("relay_setup", event)
            self.ui._handle_relay_setup(value)
        self.assertFalse(self.ui.relay_setup_busy)

    def test_old_configuration_reconnects_without_another_trust_dialog(self):
        self.assertEqual("", self.ui.relay_admin_password.get())
        with mock.patch.object(relay, "discover_server_identity", new_callable=mock.AsyncMock) as observe, \
                mock.patch.object(relay, "list_devices_async", new_callable=mock.AsyncMock, return_value=[]) as directory, \
                mock.patch.object(app.messagebox, "askyesno") as trust:
            self.ui._save_relay()
            self.finish_setup()
        observe.assert_not_called()
        trust.assert_not_called()
        self.assertEqual(SAVED, directory.call_args.args[0])
        self.assertEqual(SAVED, relay.load_settings(self.path))
        self.assertEqual("normal", str(self.ui.relay_setup_controls[0].cget("state")))

    def test_new_endpoint_logs_in_using_root_then_verifies_relay_without_manual_token(self):
        self.ui.relay_server.set("new.test")
        self.ui.relay_ssh_port.set("")
        self.ui.relay_admin_password.set('owned-root-password')
        order = []
        def login(request, password, device_id, publish):
            order.append('root-login')
            self.assertEqual('new.test', request.server)
            self.assertEqual(22, request.ssh_port)
            self.assertEqual('', request.expected_identity)
            self.assertEqual('owned-root-password', password)
            self.assertEqual(ID, device_id)
            return replace(SAVED, server_address='new.test')
        async def directory(options):
            order.append("authenticate")
            self.assertEqual("new.test", options.server_address)
            self.assertEqual(56567, options.port)
            self.assertEqual(SAVED.tls_certificate_sha256, options.tls_certificate_sha256)
            return []
        with mock.patch.object(app.relay_login.LoginOperation, 'login', side_effect=login) as login_call, \
                mock.patch.object(relay, "list_devices_async", side_effect=directory), \
                mock.patch.object(app.messagebox, "askyesno") as trust:
            self.ui._save_relay()
            self.assertEqual("disabled", str(self.ui.relay_setup_controls[0].cget("state")))
            self.assertEqual('', self.ui.relay_admin_password.get())
            self.finish_setup()
        self.assertEqual(["root-login", "authenticate"], order)
        login_call.assert_called_once()
        trust.assert_not_called()
        self.assertEqual("new.test", relay.load_settings(self.path).server_address)

    def test_failed_credentials_leave_original_file_unchanged(self):
        original = self.path.read_bytes()
        self.ui.relay_admin_password.set('wrong-root-password')
        with mock.patch.object(app.relay_login.LoginOperation, 'login',
                               side_effect=app.relay_login.RelayLoginError('服务器 root 密码错误；原配置未更改。')):
            self.ui._save_relay()
            self.finish_setup()
        self.assertEqual(original, self.path.read_bytes())
        self.assertIs(SAVED, self.ui.relay_options)
        self.assertIn("原配置未更改", self.ui.relay_status.cget("text"))

    def test_new_server_without_root_password_never_connects_or_changes_old_config(self):
        original = self.path.read_bytes()
        self.ui.relay_server.set('new.test')
        with mock.patch.object(app.relay_login.LoginOperation, 'login') as login_call:
            self.ui._save_relay()
        login_call.assert_not_called()
        self.assertFalse(self.ui.relay_setup_busy)
        self.assertEqual(original, self.path.read_bytes())
        self.assertIn('root', self.ui.relay_status.cget('text'))


if __name__ == "__main__":
    unittest.main()
