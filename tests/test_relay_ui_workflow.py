from dataclasses import replace
from pathlib import Path
import queue
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/linux'))
import remotedesk_linux_app as app
import remotedesk_linux_relay as relay

A = '11111111-1111-4111-8111-111111111111'
B = '22222222-2222-4222-8222-222222222222'
SAVED = relay.RelayOptions('relay.test', 56567, 'owned-test-internal-relay-token-only', 'AB' * 32, A, True).validate()


class RelayDeviceKeyTests(unittest.TestCase):
    def test_keys_are_encrypted_and_scoped_to_device_and_server_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            relay.save_device_key(SAVED, 'owned-device-a-secret', directory)
            relay.save_device_key(replace(SAVED, device_id=B), 'owned-device-b-secret', directory)
            self.assertEqual('owned-device-a-secret', relay.load_device_key(SAVED, directory))
            self.assertEqual('owned-device-b-secret', relay.load_device_key(replace(SAVED, device_id=B), directory))
            self.assertEqual('', relay.load_device_key(replace(SAVED, server_address='another.test'), directory))
            self.assertEqual('', relay.load_device_key(replace(SAVED, tls_certificate_sha256='CD' * 32), directory))
            for path in Path(directory).rglob('book'):
                self.assertNotIn(b'owned-device-', path.read_bytes())
                self.assertEqual(0o600, path.stat().st_mode & 0o777)

    def test_logout_has_an_explicit_empty_state_and_does_not_remove_device_keys(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'relay.json'
            relay.save_device_key(SAVED, 'owned-device-key', directory)
            relay.save_settings(SAVED, path)
            relay.save_settings(None, path)
            self.assertIsNone(relay.load_settings(path))
            self.assertNotIn(SAVED.access_token, path.read_text())
            self.assertEqual('owned-device-key', relay.load_device_key(SAVED, directory))


class RelayWorkflowTkTests(unittest.TestCase):
    def setUp(self):
        try: self.root = app.tk.Tk()
        except app.tk.TclError: self.skipTest('Owned Tk display required')
        self.addCleanup(self.root.destroy)
        self.root.geometry('800x800')
        self.ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui = self.ui
        ui.root = self.root; ui.closing = False; ui.relay_options = None
        ui.relay_setup_busy = False; ui.relay_setup_generation = 0; ui.relay_setup_cancel = None
        ui.relay_admin_operation = None; ui.relay_refresh_generation = 0; ui.relay_refreshing = False
        ui.viewer_relay_options = None; ui.viewer = None; ui.viewer_reconnect_after_id = None
        ui._sync_relay_registration = mock.Mock(); ui._stop_relay_registration = mock.Mock()
        ui._configure_style()
        notebook = app.ttk.Notebook(self.root); notebook.pack(fill='both', expand=True)
        with mock.patch.object(relay, 'load_settings', return_value=SAVED): ui._build_relay_tab(notebook)
        self.root.update()
        self.directory = tempfile.TemporaryDirectory(); self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'relay.json'; relay.save_settings(SAVED, self.path)
        patcher = mock.patch.object(relay, 'settings_path', return_value=self.path)
        patcher.start(); self.addCleanup(patcher.stop)

    def test_saved_login_is_not_misrepresented_as_unconfigured(self):
        self.assertIn('已保存登录', self.ui.relay_server_summary.cget('text'))
        self.assertNotIn('先保存', self.ui.relay_status.cget('text'))
        self.assertFalse(self.ui.relay_form.winfo_manager())
        self.ui._toggle_relay_form(); self.root.update()
        self.assertTrue(self.ui.relay_form.winfo_manager())

    def test_publication_switch_persists_and_applies_without_logging_in_again(self):
        with mock.patch.object(app.relay_login.LoginOperation, 'login') as login:
            self.ui.relay_publish_control.invoke()
        self.assertFalse(self.ui.relay_options.publish)
        self.assertFalse(relay.load_settings(self.path).publish)
        self.ui._sync_relay_registration.assert_called_once()
        login.assert_not_called()

    def test_save_failure_restores_switch_and_preserves_registration(self):
        with mock.patch.object(relay, 'save_settings', side_effect=OSError('owned failure')):
            self.ui.relay_publish_control.invoke()
        self.assertTrue(self.ui.relay_publish.get())
        self.assertTrue(self.ui.relay_options.publish)
        self.ui._sync_relay_registration.assert_not_called()
        self.assertIn('开关已恢复', self.ui.relay_status.cget('text'))

    def test_logout_clears_credentials_and_stops_only_this_registration(self):
        with mock.patch.object(app.messagebox, 'askyesno', return_value=True): self.ui._logout_relay()
        self.assertIsNone(self.ui.relay_options)
        self.assertIsNone(relay.load_settings(self.path))
        self.ui._stop_relay_registration.assert_called_once()
        self.assertEqual('', self.ui.relay_admin_password.get())

    def test_failed_logout_keeps_old_configuration_and_registration(self):
        with mock.patch.object(app.messagebox, 'askyesno', return_value=True), \
                mock.patch.object(relay, 'save_settings', side_effect=OSError('owned failure')):
            self.ui._logout_relay()
        self.assertIs(SAVED, self.ui.relay_options)
        self.ui._stop_relay_registration.assert_not_called()

    def test_refresh_preserves_selected_device_and_unsaved_key_edit(self):
        device = dict(deviceId=B, machineName='Owned PC', platform='Linux', busy=False)
        with mock.patch.object(relay, 'load_device_key', return_value='remembered-key'):
            self.ui._show_relay_directory([device])
            self.ui.relay_list.selection_set(B); self.root.update()
            self.ui.relay_password.set('editing-this-device-key')
            self.ui._show_relay_directory([device]); self.root.update()
            self.assertEqual((B,), self.ui.relay_list.selection())
            self.assertEqual('editing-this-device-key', self.ui.relay_password.get())

    def select_named_fixture(self):
        self.ui.events = queue.Queue()
        self.ui._show_relay_directory([dict(deviceId=B, machineName='共享工作机', originalMachineName='Original PC',
            sharedName='共享工作机', canRename=True, platform='Windows', busy=False)])
        with mock.patch.object(relay, 'load_device_key', return_value='owned-key'):
            self.ui.relay_list.selection_set(B)
            self.root.update()

    def test_shared_name_button_targets_uuid_and_explains_other_clients_visibility(self):
        self.select_named_fixture()
        with mock.patch.object(app.simpledialog, 'askstring', return_value='广州工作站') as dialog, \
                mock.patch.object(app.threading, 'Thread') as worker:
            self.ui.relay_rename_button.invoke()
        self.assertIn('同一服务器', dialog.call_args.args[1])
        self.assertEqual('共享工作机', dialog.call_args.kwargs['initialvalue'])
        self.assertTrue(self.ui.relay_renaming)
        with mock.patch.object(relay, 'rename_device') as rename:
            worker.call_args.kwargs['target']()
        rename.assert_called_once_with(replace(SAVED, device_id=B), '广州工作站')
        kind, payload = self.ui.events.get_nowait()
        self.assertEqual('relay_rename', kind)
        with mock.patch.object(self.ui, '_refresh_relay') as refresh:
            self.ui._complete_relay_rename(payload)
        refresh.assert_called_once()
        self.assertFalse(self.ui.relay_renaming)

    def test_cancel_and_changed_server_never_send_a_name(self):
        self.select_named_fixture()
        def changed(*_args, **_kwargs):
            self.ui.relay_options = replace(SAVED, server_address='other.test')
            return 'wrong-target'
        for reply in (None, changed):
            with mock.patch.object(app.simpledialog, 'askstring', **({'side_effect': reply} if callable(reply) else {'return_value': reply})), \
                    mock.patch.object(app.threading, 'Thread') as worker:
                self.ui._rename_relay_device()
            worker.assert_not_called()

    def test_failed_save_populates_warning_and_never_claims_success(self):
        self.select_named_fixture()
        self.ui.relay_renaming = True
        with mock.patch.object(app.messagebox, 'showwarning') as warning, mock.patch.object(self.ui, '_refresh_relay') as refresh:
            self.ui._complete_relay_rename((self.ui.relay_refresh_generation, SAVED, 'fixture disk full'))
        warning.assert_called_once()
        refresh.assert_not_called()
        self.assertIn('未确认保存', self.ui.relay_status.cget('text'))

    def test_legacy_server_is_visible_with_actionable_update_notice(self):
        self.select_named_fixture()
        self.ui.relay_devices[B]['canRename'] = False
        with mock.patch.object(app.messagebox, 'showinfo') as notice, mock.patch.object(app.simpledialog, 'askstring') as dialog:
            self.ui._rename_relay_device()
        self.assertIn('更新服务器', notice.call_args.args[1])
        dialog.assert_not_called()


if __name__ == '__main__': unittest.main()
