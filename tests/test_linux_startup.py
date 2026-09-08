from __future__ import annotations

import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_startup as startup
import remotedesk_linux_app as app


@unittest.skipUnless(sys.platform.startswith("linux"), "Unix permissions and graphical login")
class HostPreferencesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.store = startup.HostPreferences(self.directory / "private")
        self.settings = dict(remember=True, armed=True, port="56565", capture="x11", login_start=True)

    def test_ciphertext_round_trip_restart_and_no_secret_in_json(self):
        password = "synthetic-local-credential-中文"
        self.store.save(self.settings, password)
        saved = startup.HostPreferences(self.store.directory).load()
        self.assertEqual(password, saved["password"])
        self.assertTrue(saved["armed"])
        raw = (self.store.directory / "settings.json").read_text()
        self.assertNotIn(password, raw)
        self.assertNotIn("password", json.loads(raw))
        self.assertEqual(0o700, self.store.directory.stat().st_mode & 0o777)
        for name in ("settings.json", "key"):
            self.assertEqual(0o600, (self.store.directory / name).stat().st_mode & 0o777)

    def test_explicit_stop_survives_next_load(self):
        self.store.save(self.settings, "test-password")
        self.store.save(dict(self.settings, armed=False), "test-password")
        self.assertFalse(self.store.load()["armed"])
        self.assertEqual("test-password", self.store.load()["password"])

    def test_forget_removes_secret_and_key_and_cannot_resume(self):
        self.store.save(self.settings, "test-password")
        self.store.save(dict(self.settings, remember=False), "test-password")
        saved = self.store.load()
        self.assertEqual("", saved["password"])
        self.assertFalse(saved["armed"])
        self.assertFalse((self.store.directory / "key").exists())

    def test_corrupt_or_missing_key_never_falls_back_to_default_password(self):
        self.store.save(self.settings, "test-password")
        (self.store.directory / "key").unlink()
        with self.assertRaises(FileNotFoundError):
            self.store.load()

    def test_public_permissions_symlinks_and_tampered_ciphertext_are_rejected(self):
        self.store.save(self.settings, "test-password")
        key = self.store.directory / "key"
        key.chmod(0o644)
        with self.assertRaises(PermissionError):
            self.store.load()
        key.chmod(0o600)
        key.rename(self.directory / "real-key")
        key.symlink_to(self.directory / "real-key")
        with self.assertRaises(OSError):
            self.store.load()
        key.unlink()
        (self.directory / "real-key").rename(key)
        data = json.loads((self.store.directory / "settings.json").read_text())
        data["encrypted_password"] = data["encrypted_password"][:-4] + "AAAA"
        startup.atomic_private_write(self.store.directory / "settings.json", json.dumps(data).encode())
        with self.assertRaises(Exception):
            self.store.load()

    def test_symlink_directory_is_not_written(self):
        self.store.directory.symlink_to(self.directory, target_is_directory=True)
        with self.assertRaises(PermissionError):
            self.store.save(self.settings, "test-password")
        self.assertFalse((self.directory / "key").exists())

    def test_owned_autostart_can_be_enabled_replaced_and_removed(self):
        directory = self.directory / "autostart"
        startup.set_login_start(True, sys.executable, __file__, directory)
        path = directory / "remotedesk-host.desktop"
        self.assertIn("--autostart", path.read_text())
        self.assertIn(startup.MARKER, path.read_text())
        self.assertNotIn("test-password", path.read_text())
        startup.set_login_start(True, sys.executable, __file__, directory)
        startup.set_login_start(False, sys.executable, __file__, directory)
        self.assertFalse(path.exists())

    def test_foreign_or_symlink_autostart_is_not_overwritten_or_removed(self):
        directory = self.directory / "autostart"
        directory.mkdir()
        path = directory / "remotedesk-host.desktop"
        path.write_text("[Desktop Entry]\nName=Unrelated application\n")
        for enabled in (False, True):
            with self.assertRaises(ValueError):
                startup.set_login_start(enabled, sys.executable, __file__, directory)
        self.assertIn("Unrelated", path.read_text())

    def test_instance_lock_blocks_duplicates_and_is_released(self):
        first = startup.AppInstance(self.store.directory)
        try:
            with self.assertRaises(BlockingIOError):
                startup.AppInstance(self.store.directory)
        finally:
            first.close()
        startup.AppInstance(self.store.directory).close()

    def test_login_uses_stable_installed_wrapper_and_runtime_environment(self):
        package = self.directory / "releases/version-1"
        (package / "app").mkdir(parents=True)
        source = package / "app/remotedesk_linux_app.py"
        source.touch()
        wrapper = package / "remotedesk-linux-app"
        wrapper.write_text("#!/bin/sh\nexit 0\n")
        wrapper.chmod(0o700)
        alias = self.directory / ".local/bin/remotedesk-linux-app"
        alias.parent.mkdir(parents=True)
        alias.symlink_to(wrapper)
        with mock.patch.object(startup.Path, "home", return_value=self.directory):
            command = startup.login_command(sys.executable, str(source))
        self.assertEqual(startup.desktop_argument(str(alias)) + " --autostart", command)
        self.assertNotIn("version-1", command)

    def test_desktop_arguments_do_not_expand_shell_metacharacters_or_field_codes(self):
        value = '/tmp/path with spaces/$HOME/percent%/"quoted"/test.py'
        argument = startup.desktop_argument(value)
        self.assertIn("\\$HOME", argument)
        self.assertIn("percent%%", argument)
        self.assertIn('\\"quoted\\"', argument)
        with self.assertRaises(ValueError):
            startup.desktop_argument("/tmp/path\nExec=unrelated")


class HostResumeLifecycleTests(unittest.TestCase):
    def controller(self):
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.closing = False
        controller.host_armed = True
        controller.host_process = None
        controller.host_stopping_generation = None
        controller.host_resume_after_id = "pending-retry"
        controller.root = mock.Mock()
        controller._stop_relay_registration = mock.Mock()
        controller._save_host_preferences = mock.Mock()
        return controller

    def test_stop_cancels_pending_retry_even_if_child_already_exited(self):
        controller = self.controller()
        controller.stop_host()
        self.assertFalse(controller.host_armed)
        controller.root.after_cancel.assert_called_once_with("pending-retry")
        controller._save_host_preferences.assert_called_once()

    def test_application_close_keeps_resume_setting_but_cancels_timer(self):
        controller = self.controller()
        controller.closing = True
        controller.stop_host()
        self.assertTrue(controller.host_armed)
        controller._save_host_preferences.assert_not_called()
        controller.root.after_cancel.assert_called_once()

    def test_late_retry_does_not_restart_after_stop_or_close(self):
        controller = self.controller()
        controller.start_host = mock.Mock()
        controller.host_armed = False
        controller._resume_host()
        controller.start_host.assert_not_called()
        controller.host_armed = True
        controller.closing = True
        controller._resume_host()
        controller.start_host.assert_not_called()

    def test_recovery_is_bounded_after_repeated_failures(self):
        controller = self.controller()
        controller.host_started_at = app.time.monotonic()
        controller.host_restart_attempt = 0
        controller._append_host_log = mock.Mock()
        for _ in range(4):
            controller._retry_host_after_exit()
        self.assertEqual(3, controller.root.after.call_count)
        self.assertEqual([1000, 3000, 10000], [call.args[0] for call in controller.root.after.call_args_list])


if __name__ == "__main__":
    unittest.main()
