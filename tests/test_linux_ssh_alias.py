"""Owned OpenSSH configuration and standalone alias helper; never ~/.ssh tests."""
import asyncio
from dataclasses import replace
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_ssh as ssh
import remotedesk_linux_relay as relay


@unittest.skipUnless(sys.platform.startswith("linux"), "private Unix OpenSSH configuration")
class TerminalAliasTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="owned-ssh-alias-")
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.xdg = self.folder / "config with spaces"
        self.ssh_root = self.folder / "ssh with spaces"
        self.environment = mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": str(self.xdg)})
        self.environment.start(); self.addCleanup(self.environment.stop)
        self.options = relay.RelayOptions("127.0.0.1", 12345, "synthetic-relay-token-" * 3,
            "ab" * 32, "01234567-89ab-cdef-0123-456789abcdef").validate()
        relay.save_settings(self.options)
        self.key = "synthetic-private-device-key"

    def configure(self, alias="5090", username="owned", **kwargs):
        return ssh.configure_terminal_alias(alias, username, self.options, self.key, ssh_directory=self.ssh_root, **kwargs)

    def config(self, body, mode=0o600):
        self.ssh_root.mkdir(mode=0o700, exist_ok=True)
        path = self.ssh_root / "config"
        path.write_bytes(body); path.chmod(mode)
        return path

    def rendered(self, host):
        result = subprocess.run(["ssh", "-G", "-F", str(self.ssh_root / "config"), host],
                                capture_output=True, check=True, text=True)
        return dict(line.split(" ", 1) for line in result.stdout.splitlines() if " " in line)

    def test_native_parser_precedes_host_star_and_preserves_user_bytes_and_other_hosts(self):
        body = b"# preserved UTF-8 body\r\nHost *\r\n  User fallback\r\n  Port 2222\r\nHost other\r\n  HostName 192.0.2.42\r\n"
        path = self.config(body, 0o644)
        result = self.configure()
        self.assertTrue(path.read_bytes().endswith(body))
        self.assertEqual(body, Path(result["backup"]).read_bytes())
        self.assertEqual(0o644, path.stat().st_mode & 0o777)
        rendered = self.rendered("5090")
        self.assertEqual("owned", rendered["user"])
        self.assertEqual(result["profileId"] + ".remotedesk.invalid", rendered["hostname"])
        self.assertEqual(ssh.host_key_alias(self.options), rendered["hostkeyalias"])
        self.assertIn("--stdio " + result["profileId"], rendered["proxycommand"])
        self.assertEqual("no", rendered["clearallforwardings"])
        self.assertEqual("fallback", self.rendered("other")["user"])
        self.assertEqual("192.0.2.42", self.rendered("other")["hostname"])
        for file in (path, self.ssh_root / "remotedesk_aliases.conf", ssh.ssh_config_directory() / "aliases.json"):
            content = file.read_text()
            self.assertNotIn(self.options.access_token, content)
            self.assertNotIn(self.key, content)

    def test_same_alias_case_insensitive_update_requires_same_target(self):
        first = self.configure("My5090")
        second = self.configure("my5090", "changed")
        self.assertEqual(first["profileId"], second["profileId"])
        self.assertEqual("my5090", first["alias"])
        self.assertEqual("changed", self.rendered(second["alias"])["user"])
        other = replace(self.options, device_id=str(uuid.uuid4()))
        with self.assertRaises(ssh.AliasConfigurationError):
            ssh.configure_terminal_alias("my5090", "other", other, self.key, ssh_directory=self.ssh_root)
        self.assertEqual("changed", self.rendered("my5090")["user"])

    def test_exact_wildcard_include_and_match_conflicts_never_execute_or_modify(self):
        marker = self.folder / "must-not-exist"
        bodies = (b"Host 5090\n User existing\n", b"Host 50*\n User existing\n",
                  b"Include \"missing/path\"\n", f'Match exec "touch {marker}"\n User existing\n'.encode())
        for body in bodies:
            with self.subTest(body=body):
                path = self.config(body)
                with self.assertRaises(ssh.AliasConfigurationError): self.configure()
                self.assertEqual(body, path.read_bytes())
                self.assertFalse((self.ssh_root / "remotedesk_aliases.conf").exists())
                self.assertFalse((ssh.ssh_config_directory() / "aliases.json").exists())
                self.assertFalse(marker.exists())

    def test_negated_pattern_does_not_claim_alias(self):
        self.config(b"Host 50* !5090\n User other\nHost *\n User fallback\n")
        self.configure()
        self.assertEqual("owned", self.rendered("5090")["user"])

    def test_invalid_aliases_are_rejected_without_creating_ssh_config(self):
        for alias in ("", "-x", "a.b", "a b", "a\nHost other", "设备", "*", "x" * 65):
            with self.subTest(alias=alias), self.assertRaises(ssh.AliasConfigurationError): self.configure(alias)
        self.assertFalse(self.ssh_root.exists())

    def test_unmanaged_fragment_or_symlink_is_not_replaced(self):
        self.config(b"Host *\n User fallback\n")
        fragment = self.ssh_root / "remotedesk_aliases.conf"
        fragment.write_bytes(b"user-owned fragment"); fragment.chmod(0o600)
        with self.assertRaises(ssh.AliasConfigurationError): self.configure()
        self.assertEqual(b"user-owned fragment", fragment.read_bytes())
        fragment.unlink()
        target = self.folder / "untouched"
        target.write_bytes(b"not a config")
        fragment.symlink_to(target)
        with self.assertRaises(OSError): self.configure()
        self.assertEqual(b"not a config", target.read_bytes())

    def test_main_config_symlink_and_public_writability_fail_closed(self):
        path = self.config(b"Host *\n", 0o666)
        with self.assertRaises(ssh.AliasConfigurationError): self.configure()
        path.unlink()
        target = self.folder / "original"
        target.write_bytes(b"user-owned")
        path.symlink_to(target)
        with self.assertRaises(OSError): self.configure()
        self.assertEqual(b"user-owned", target.read_bytes())

    def test_write_failure_rolls_back_profile_fragment_and_preserves_original(self):
        body = b"Host *\n User fallback\n"
        path = self.config(body)
        with mock.patch.object(ssh, "_replace_user_config", side_effect=OSError("owned synthetic write failure")):
            with self.assertRaises(OSError): self.configure()
        self.assertEqual(body, path.read_bytes())
        self.assertFalse((self.ssh_root / "remotedesk_aliases.conf").exists())
        self.assertFalse((ssh.ssh_config_directory() / "aliases.json").exists())

    def test_detected_concurrent_edit_is_preserved_and_owned_changes_rolled_back(self):
        path = self.config(b"Host *\n User original\n")
        original = ssh._replace_user_config
        def edited(config, data, expected, mode):
            path.write_bytes(b"Host *\n User external-edit\n")
            return original(config, data, expected, mode)
        with mock.patch.object(ssh, "_replace_user_config", side_effect=edited), self.assertRaises(ssh.AliasConfigurationError):
            self.configure()
        self.assertEqual(b"Host *\n User external-edit\n", path.read_bytes())
        self.assertFalse((ssh.ssh_config_directory() / "aliases.json").exists())

    def test_removal_restores_exact_body_and_retains_device_key_and_known_hosts(self):
        body = b"# exact preserved bytes\r\nHost *\r\n User fallback\r\n"
        path = self.config(body)
        result = self.configure()
        known = ssh.ssh_config_directory() / "known_hosts"
        known.write_bytes(b"owned-known-host-record\n")
        ssh.configure_terminal_alias("5090", "", remove=True, ssh_directory=self.ssh_root)
        self.assertEqual(body, path.read_bytes())
        self.assertEqual(self.key, relay.load_device_key(self.options))
        self.assertEqual(b"owned-known-host-record\n", known.read_bytes())
        with self.assertRaises(ssh.AliasConfigurationError): ssh.load_stdio_profile(result["profileId"])

    def test_cli_profile_uses_existing_key_and_rejects_logout_or_different_relay(self):
        result = self.configure()
        options, key = ssh.load_stdio_profile(result["profileId"])
        self.assertEqual(self.options, options)
        self.assertEqual(self.key, key)
        relay.save_settings(replace(self.options, tls_certificate_sha256="cd" * 32))
        with self.assertRaises(ssh.AliasConfigurationError): ssh.load_stdio_profile(result["profileId"])
        relay.save_settings(None)
        with self.assertRaises(ssh.AliasConfigurationError): ssh.load_stdio_profile(result["profileId"])

    def test_cli_failure_has_no_stdout_or_secret_and_no_gui_dependency(self):
        result = self.configure()
        relay.save_settings(None)
        process = subprocess.run([sys.executable, ssh.__file__, "--stdio", result["profileId"]], capture_output=True)
        self.assertEqual(1, process.returncode)
        self.assertEqual(b"", process.stdout)
        self.assertIn("退出中继", process.stderr.decode())
        self.assertNotIn(self.key.encode(), process.stderr)
        self.assertNotIn(self.options.access_token.encode(), process.stderr)

    def test_malformed_or_public_profile_and_relay_files_fail_before_connect(self):
        result = self.configure()
        path = ssh.ssh_config_directory() / "aliases.json"
        original = path.read_bytes()
        for field, value in (("relayScope", None), ("hostKeyAlias", []), ("id", "not-a-uuid"), ("user", None)):
            with self.subTest(field=field):
                data = json.loads(original)
                data["profiles"][0][field] = value
                path.write_text(json.dumps(data))
                with self.assertRaises(ValueError): ssh.load_stdio_profile(result["profileId"])
        path.write_bytes(original); path.chmod(0o644)
        with self.assertRaises(PermissionError): ssh.load_stdio_profile(result["profileId"])
        path.chmod(0o600); relay.settings_path().chmod(0o644)
        with self.assertRaises(PermissionError): ssh.load_stdio_profile(result["profileId"])

    def test_existing_profile_update_failure_preserves_active_previous_configuration(self):
        first = self.configure()
        paths = [self.ssh_root / "config", self.ssh_root / "remotedesk_aliases.conf", ssh.ssh_config_directory() / "aliases.json"]
        snapshots = [path.read_bytes() for path in paths]
        with mock.patch.object(ssh, "_replace_user_config", side_effect=OSError("synthetic")), self.assertRaises(OSError):
            self.configure(username="new-user")
        self.assertEqual(snapshots, [path.read_bytes() for path in paths])
        self.assertEqual("owned", self.rendered("5090")["user"])
        self.assertEqual(self.key, ssh.load_stdio_profile(first["profileId"])[1])

    def test_second_managed_alias_removal_retains_first_and_exact_body(self):
        body = b"Host *\n User fallback\n"
        self.config(body)
        first = self.configure()
        self.configure("another", "second")
        ssh.configure_terminal_alias("ANOTHER", "", remove=True, ssh_directory=self.ssh_root)
        self.assertEqual(self.key, ssh.load_stdio_profile(first["profileId"])[1])
        self.assertEqual("owned", self.rendered("5090")["user"])
        self.assertEqual("fallback", self.rendered("another")["user"])
        self.assertTrue((self.ssh_root / "config").read_bytes().endswith(body))

    def test_edited_managed_prefix_and_fragment_are_never_overwritten(self):
        self.configure()
        fragment = self.ssh_root / "remotedesk_aliases.conf"
        original_fragment = fragment.read_bytes()
        fragment.write_bytes(original_fragment + b"# manual edit\n")
        with self.assertRaises(ssh.AliasConfigurationError): self.configure()
        self.assertTrue(fragment.read_bytes().endswith(b"# manual edit\n"))
        fragment.write_bytes(original_fragment)
        path = self.ssh_root / "config"
        original = path.read_bytes()
        path.write_bytes(b"# moved prefix\n" + original)
        with self.assertRaises(ssh.AliasConfigurationError): self.configure()
        self.assertEqual(b"# moved prefix\n" + original, path.read_bytes())

    def test_lock_contention_fails_without_changing_config(self):
        import fcntl
        self.configure()
        path = self.ssh_root / "config"
        original = path.read_bytes()
        with (ssh.ssh_config_directory() / "aliases.lock").open("rb") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaises(BlockingIOError): self.configure(username="changed")
        self.assertEqual(original, path.read_bytes())

    def test_packed_runtime_proxy_uses_launcher_without_gui_or_inherited_python_environment(self):
        import shutil
        import shlex
        installed = self.folder / "owned installed %h tree"
        app_dir = installed / "app"
        app_dir.mkdir(parents=True)
        source = Path(ssh.__file__)
        for item in source.parent.glob("*.py"):
            shutil.copyfile(item, app_dir / item.name)
        shutil.copyfile(source.parent / "remotedesk_linux_runtime.sh", app_dir / "remotedesk_linux_runtime.sh")
        with mock.patch.object(ssh, "__file__", str(app_dir / source.name)):
            result = self.configure()
        command = self.rendered("5090")["proxycommand"]
        self.assertIn("remotedesk_linux_runtime.sh", command)
        relay.save_settings(None)
        # ssh -G does not expand the runtime %% tokens. Native SSH does that
        # immediately before invoking its shell, simulated for this no-network check.
        run = subprocess.run(["sh", "-c", command.replace("%%", "%")], capture_output=True,
            env=dict(os.environ, PYTHONHOME="/owned-invalid-runtime", PYTHONPATH="/owned-invalid-imports", DISPLAY=""))
        self.assertEqual(1, run.returncode)
        self.assertEqual(b"", run.stdout)
        self.assertIn("退出中继", run.stderr.decode())
        self.assertNotIn("tkinter", run.stderr.decode())

    def test_include_percent_path_refuses_before_creating_config(self):
        target = self.folder / "ssh %h directory"
        with self.assertRaises(ssh.AliasConfigurationError):
            ssh.configure_terminal_alias("5090", "owned", self.options, self.key, ssh_directory=target)
        self.assertFalse(target.exists())

    def test_include_glob_and_environment_paths_refuse_before_creating_any_config(self):
        for name in ("owned[1]", "owned*", "owned?", "owned]", "owned${USER}"):
            with self.subTest(name=name):
                target = self.folder / name / ".ssh"
                with self.assertRaises(ssh.AliasConfigurationError):
                    ssh.configure_terminal_alias("5090", "owned", self.options, self.key, ssh_directory=target)
                self.assertFalse(target.parent.exists())

    def test_identity_file_is_only_a_path_with_native_percent_escaping_and_no_agent_forwarding(self):
        key = self.folder / "identity %h %d %% with spaces"
        key.write_text("synthetic-key-content-must-not-copy")
        self.configure(identity_file=key)
        data = json.loads((ssh.ssh_config_directory() / "aliases.json").read_text())
        self.assertEqual(str(key), data["profiles"][0]["identityFile"])
        fragment = (self.ssh_root / "remotedesk_aliases.conf").read_text()
        self.assertNotIn(key.read_text(), fragment)
        self.assertIn("%%h %%d %%%%", fragment)
        rendered = self.rendered("5090")
        self.assertEqual("yes", rendered["identitiesonly"])
        self.assertEqual("no", rendered["forwardagent"])
        self.assertEqual("no", rendered["forwardx11"])

    def test_host_star_multiplex_and_local_command_defaults_cannot_reuse_other_device_session(self):
        self.config(b"Host *\n ControlMaster auto\n ControlPath /tmp/owned-%r@%h:%p\n CanonicalizeHostname yes\n PermitLocalCommand yes\n ProxyUseFdpass yes\n")
        first = self.configure()
        other = replace(self.options, device_id=str(uuid.uuid4()))
        second = ssh.configure_terminal_alias("second", "owned", other, self.key, ssh_directory=self.ssh_root)
        for alias, result in (("5090", first), ("second", second)):
            rendered = self.rendered(alias)
            self.assertEqual("false", rendered["controlmaster"])
            self.assertEqual("none", rendered.get("controlpath", "none"))
            self.assertEqual("false", rendered["canonicalizehostname"])
            self.assertEqual("no", rendered["permitlocalcommand"])
            self.assertEqual("no", rendered["proxyusefdpass"])
            self.assertEqual("30", rendered["serveraliveinterval"])
            self.assertEqual("3", rendered["serveralivecountmax"])
            self.assertEqual("no", rendered["clearallforwardings"])
            self.assertEqual(result["profileId"] + ".remotedesk.invalid", rendered["hostname"])
        self.assertNotEqual(first["profileId"], second["profileId"])
        self.assertEqual("auto", self.rendered("unrelated")["controlmaster"])

    def test_openssh_environment_expansion_in_identity_paths_is_rejected(self):
        identity = self.folder / "key-${USER}"
        identity.write_text("synthetic-key")
        with self.assertRaises(ssh.AliasConfigurationError): self.configure(identity_file=identity)
        self.assertFalse((self.ssh_root / "config").exists())
        with self.assertRaises(ssh.AliasConfigurationError):
            ssh._config_quote(self.folder / "${HOME}" / "known_hosts", True)

    def test_many_long_private_key_paths_refuse_before_exceeding_readable_profile_limit(self):
        identity = self.folder
        for index in range(20):
            identity /= str(index) + "owned-long-path-" * 6
        identity.mkdir(parents=True)
        identity /= "private-key"
        identity.write_text("synthetic-private-key-content")
        profile = ssh.ssh_config_directory() / "aliases.json"
        paths = [profile, self.ssh_root / "config", self.ssh_root / "remotedesk_aliases.conf"]
        previous = None
        for index in range(50):
            snapshots = [path.read_bytes() if path.exists() else None for path in paths]
            try:
                result = self.configure("node" + str(index), identity_file=identity)
            except ssh.AliasConfigurationError as error:
                self.assertIn("64 KiB", str(error))
                self.assertEqual(snapshots, [path.read_bytes() for path in paths])
                self.assertEqual(self.key, ssh.load_stdio_profile(previous["profileId"])[1])
                self.assertLessEqual(len(profile.read_bytes()), 65536)
                break
            else:
                previous = result
        else:
            self.fail("Expected bounded profile preflight before 50 long-path records")

    def test_user_config_plus_include_limit_is_checked_before_any_real_write(self):
        body = b"#" + b"x" * 262142 + b"\n"
        path = self.config(body)
        with mock.patch.object(relay, "save_device_key") as save_key, self.assertRaisesRegex(ssh.AliasConfigurationError, "256 KiB"):
            self.configure()
        save_key.assert_not_called()
        self.assertEqual(body, path.read_bytes())
        self.assertFalse(list(self.ssh_root.glob("config.remotedesk-backup-*")))
        self.assertFalse((self.ssh_root / "remotedesk_aliases.conf").exists())
        self.assertFalse((ssh.ssh_config_directory() / "aliases.json").exists())

    def test_generated_fragment_limit_is_checked_before_any_real_write(self):
        original = ssh._alias_fragment
        def expanded(profiles, directory):
            return b"x" * 262145 if profiles else original(profiles, directory)
        with mock.patch.object(ssh, "_alias_fragment", side_effect=expanded), \
                mock.patch.object(relay, "save_device_key") as save_key, self.assertRaisesRegex(ssh.AliasConfigurationError, "片段过大"):
            self.configure()
        save_key.assert_not_called()
        self.assertFalse((self.ssh_root / "config").exists())
        self.assertFalse((self.ssh_root / "remotedesk_aliases.conf").exists())
        self.assertFalse((ssh.ssh_config_directory() / "aliases.json").exists())

    def test_stdio_remote_eof_flushes_final_bytes_to_a_temporarily_slow_native_client(self):
        import threading
        import time
        script = f'''
import asyncio, sys
from unittest import mock
sys.path.insert(0, {str(Path(ssh.__file__).parent)!r})
import remotedesk_linux_ssh as ssh
async def connection(*args):
    reader = asyncio.StreamReader()
    reader.feed_data(b"owned-tail" * 6000)
    reader.feed_eof()
    return reader, mock.Mock(wait_closed=mock.AsyncMock())
with mock.patch.object(ssh, "load_stdio_profile", return_value=(None, "owned")), mock.patch.object(ssh, "connect_ssh", side_effect=connection):
    asyncio.run(ssh.stdio_proxy("owned-profile"))
'''
        process = subprocess.Popen([sys.executable, "-c", script], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, pipesize=4096)
        watchdog = threading.Timer(3, process.kill)
        watchdog.daemon = True
        watchdog.start()
        try:
            # Deliberately let the ProxyCommand reach remote EOF before native
            # OpenSSH drains its small stdout pipe; stdin remains open.
            time.sleep(.2)
            received = process.stdout.read()
            process.stdin.close()
            self.assertEqual(0, process.wait(timeout=3), process.stderr.read().decode())
            self.assertEqual(b"owned-tail" * 6000, received)
        finally:
            watchdog.cancel()
            watchdog.join(timeout=1)
            if process.poll() is None:
                process.kill(); process.wait(timeout=3)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()

    def test_stdio_cancellation_is_bounded_when_native_client_never_reads_stdout(self):
        for payload_size in (60000, 200000):
            with self.subTest(payload_size=payload_size):
                script = f'''
import asyncio, contextlib, sys
from unittest import mock
sys.path.insert(0, {str(Path(ssh.__file__).parent)!r})
import remotedesk_linux_ssh as ssh
async def connection(*args):
    reader = asyncio.StreamReader()
    reader.feed_data(b"x" * {payload_size})
    reader.feed_eof()
    return reader, mock.Mock(wait_closed=mock.AsyncMock())
async def main():
    task = asyncio.create_task(ssh.stdio_proxy("owned-profile"))
    await asyncio.sleep(.1)
    task.cancel()
    with contextlib.suppress(asyncio.CancelledError):
        await asyncio.wait_for(task, .5)
with mock.patch.object(ssh, "load_stdio_profile", return_value=(None, "owned")), mock.patch.object(ssh, "connect_ssh", side_effect=connection):
    asyncio.run(main())
'''
                process = subprocess.Popen([sys.executable, "-c", script], stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, pipesize=4096)
                try:
                    # No stdout reads until the cancelled child has exited.
                    self.assertEqual(0, process.wait(timeout=2), process.stderr.read().decode())
                    self.assertLessEqual(len(process.stdout.read()), 4096)
                finally:
                    if process.poll() is None:
                        process.kill(); process.wait(timeout=3)
                    for stream in (process.stdin, process.stdout, process.stderr):
                        stream.close()


if __name__ == "__main__":
    unittest.main()
