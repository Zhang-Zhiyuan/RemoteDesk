"""Exercise the shipped installer in a private directory with OS services stubbed.

No real users, systemd units, firewall rules or /etc configuration are changed.
OpenSSL, Python, file permissions and the installer itself execute normally.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import signal
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import time
import unittest
import uuid


SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "relay"


@unittest.skipUnless(os.name == "posix" and shutil.which("openssl"), "requires Linux shell and OpenSSL")
class RelayInstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="remotedesk-installer-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.config = self.root / "config"
        self.units = self.root / "units"
        self.units.mkdir()
        installer = (SCRIPTS / "install_remotedesk_relay.sh").read_text(encoding="utf-8")
        for original, replacement in {
            "/etc/remotedesk-relay": self.config,
            "/var/lib/remotedesk-relay": self.root / "state",
            "/usr/local/lib/remotedesk-relay": self.root / "lib",
            "/etc/systemd/system/remotedesk-relay.service": self.units / "remotedesk-relay.service",
        }.items():
            installer = installer.replace(original, str(replacement))
        self.installer = self.root / "install.sh"
        installer = installer.replace("HEALTH_TIMEOUT = 12", "HEALTH_TIMEOUT = 2")
        installer = installer.replace('bind=config.get("bind", "0.0.0.0")', 'bind=config.get("bind", "127.0.0.1")')
        self.installer.write_text(installer, encoding="utf-8")
        self.log = self.root / "commands"
        self.port = self._free_port()
        self._command("id", '#!/bin/sh\n[ "$1" != "-u" ] || printf "0\\n"\n')
        self._command("chown", "#!/bin/sh\nexit 0\n")
        self._command("ufw", "#!/bin/sh\nexit 1\n")
        self._command("firewall-cmd", "#!/bin/sh\nexit 1\n")
        # Real Python/TLS relay, but ONLY a private fixture process. No systemd
        # or host firewall operation escapes this stub.
        self._command("systemctl", "#!" + sys.executable + "\n" + r'''
import json, os, signal, subprocess, sys, time
from pathlib import Path
root = Path(os.environ["RELAY_TEST_ROOT"])
pid_file = root / "service.pid"
server = str(root / "lib/remotedesk_relay_server.py")
with (root / "commands").open("a") as log:
    print(" ".join(sys.argv[1:]), file=log)
def alive():
    try:
        pid = int(pid_file.read_text())
        data = Path("/proc") / str(pid)
        args = (data / "cmdline").read_bytes().split(b"\0")
        return pid if server.encode() in args and (data / "stat").read_text().split()[2] != "Z" else None
    except (OSError, ValueError):
        return None
def stop():
    pid = alive()
    if pid:
        os.kill(pid, signal.SIGTERM)
        deadline = time.monotonic() + 4
        while alive() and time.monotonic() < deadline:
            time.sleep(.02)
        if alive():
            os.kill(pid, signal.SIGKILL)  # Exact owned fixture, never a system service.
    pid_file.unlink(missing_ok=True)
action = sys.argv[1]
failure = root / "fail-once"
if failure.exists() and failure.read_text().strip() == action:
    failure.unlink()
    raise SystemExit(1)
if action == "restart" and (root / "fail-all-restarts").exists():
    raise SystemExit(1)
if action == "is-active":
    raise SystemExit(0 if alive() else 3)
if action == "is-enabled":
    raise SystemExit(0 if (root / "enabled").exists() else 1)
if action == "stop":
    stop()
elif action == "restart":
    stop()
    with (root / "server.log").open("ab") as log:
        process = subprocess.Popen([sys.executable, server, str(root / "config/config.json")],
            stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    pid_file.write_text(str(process.pid))
    (root / "restarted").touch()
elif action == "enable":
    (root / "enabled").touch()
elif action == "disable":
    (root / "enabled").unlink(missing_ok=True)
''')
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                RELAY_TEST_LOG=str(self.log), RELAY_TEST_ROOT=str(self.root))
        self.addCleanup(self._stop_service)

    def _command(self, name, content):
        target = self.bin / name
        target.write_text(content, encoding="utf-8")
        target.chmod(0o700)

    @staticmethod
    def _free_port():
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            return reservation.getsockname()[1]

    def _stop_service(self):
        subprocess.run([str(self.bin / "systemctl"), "stop", "remotedesk-relay.service"],
                       env=self.environment, capture_output=True, timeout=10)

    def install(self, token="ab" * 32, port=None, source=None):
        return subprocess.run(
            ["sh", str(self.installer), str(source or SCRIPTS / "remotedesk_relay_server.py")],
            input=f"{token}\n{self.port if port is None else port}\n", text=True, capture_output=True,
            env=self.environment, timeout=30,
        )

    def candidate(self, version="1.0.9", broken=False):
        value = (SCRIPTS / "remotedesk_relay_server.py").read_text()
        value = value.replace('RELAY_RELEASE_VERSION = "1.0.8"', 'RELAY_RELEASE_VERSION = "' + version + '"')
        if broken:
            value = value.replace("asyncio.run(run(config_path))", 'raise RuntimeError("deliberate test-only startup failure")')
        path = self.root / ("candidate-" + version + ".py")
        path.write_text(value)
        return path

    def installed_files(self):
        return {str(path.relative_to(self.root)): (path.read_bytes(), path.stat().st_mode & 0o777)
                for path in (self.config / "config.json", self.config / "relay.crt", self.config / "relay.key",
                             self.root / "lib/remotedesk_relay_server.py",
                             self.units / "remotedesk-relay.service", self.root / "state/deployment.json") if path.exists()}

    def tls(self):
        context = ssl.create_default_context(cafile=str(self.config / "relay.crt"))
        context.check_hostname = False
        sock = context.wrap_socket(socket.create_connection(("127.0.0.1", self.port), timeout=3), server_hostname=None)
        self.addCleanup(sock.close)
        return sock

    @staticmethod
    def send(sock, value):
        data = json.dumps(value).encode()
        sock.sendall(struct.pack(">I", len(data)) + data)

    @staticmethod
    def receive(sock):
        def exact(count):
            data = bytearray()
            while len(data) < count:
                chunk = sock.recv(count - len(data))
                if not chunk:
                    raise EOFError()
                data.extend(chunk)
            return data
        return json.loads(exact(struct.unpack(">I", exact(4))[0]))

    def result(self, process):
        self.assertEqual(0, process.returncode, process.stderr)
        marker = "REMOTEDESK_RELAY_RESULT="
        return json.loads(next(line[len(marker):] for line in process.stdout.splitlines() if line.startswith(marker)))

    def test_repeat_install_preserves_identity_and_does_not_restart(self):
        first = self.result(self.install())
        self.assertTrue(first["installed"])
        self.log.write_text("", encoding="utf-8")
        second = self.result(self.install(token="cd" * 32))
        self.assertFalse(second["installed"])
        self.assertEqual(first["accessToken"], second["accessToken"])
        self.assertEqual(first["tlsCertificateSha256"], second["tlsCertificateSha256"])
        self.assertNotIn("restart", self.log.read_text())
        self.assertTrue(second["healthVerified"])
        self.assertEqual("1.0.8", second["serverVersion"])
        self.assertEqual(1, len(list((self.root / "state").glob("update-*"))))
        self.assertEqual(0o640, (self.config / "config.json").stat().st_mode & 0o777)
        self.assertEqual(0o640, (self.config / "relay.key").stat().st_mode & 0o777)

    def test_custom_port_and_changed_port_restart_only_when_needed(self):
        self.assertEqual(self.port, self.result(self.install())["port"])
        self.log.write_text("", encoding="utf-8")
        changed_port = self._free_port()
        self.assertEqual(changed_port, self.result(self.install(port=changed_port))["port"])
        self.assertEqual(1, self.log.read_text().count("restart remotedesk-relay.service"))

    def test_corrupt_existing_config_is_not_silently_replaced(self):
        self.result(self.install())
        path = self.config / "config.json"
        broken = '{"access_token":null}'
        path.write_text(broken, encoding="utf-8")
        self.log.write_text("", encoding="utf-8")
        self.assertNotEqual(0, self.install().returncode)
        self.assertEqual(broken, path.read_text())
        self.assertNotIn("restart", self.log.read_text())

    def test_access_token_is_not_exposed_in_child_arguments(self):
        python = shutil.which("python3")
        self._command("python3", f'''#!/bin/sh
printf '%s\\n' "$*" >> "$RELAY_TEST_LOG"
exec '{python}' "$@"
''')
        token = "ef" * 32
        self.result(self.install(token=token))
        self.assertNotIn(token, self.log.read_text())

    def test_downgrade_rejected_before_changing_config_or_restarting(self):
        self.result(self.install())
        before = self.installed_files()
        self.log.write_text("")
        result = self.install(source=self.candidate("1.0.7"), port=self._free_port())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("downgrade", result.stderr)
        self.assertEqual(before, self.installed_files())
        self.assertNotIn("restart", self.log.read_text())

    def test_same_version_different_source_is_rejected(self):
        self.result(self.install())
        before = self.installed_files()
        result = self.install(source=self.candidate("1.0.8", broken=True))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("version bump", result.stderr)
        self.assertEqual(before, self.installed_files())

    def test_verified_metadata_blocks_downgrade_after_older_file_was_copied_over_server(self):
        self.result(self.install())
        old = self.candidate("1.0.7")
        (self.root / "lib/remotedesk_relay_server.py").write_bytes(old.read_bytes())
        before = self.installed_files()
        result = self.install(source=old)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("downgrade", result.stderr)
        self.assertEqual(before, self.installed_files())

    def test_legacy_unversioned_server_migrates_with_same_certificate_and_token(self):
        first = self.result(self.install())
        self._stop_service()
        server = self.root / "lib/remotedesk_relay_server.py"
        server.write_text(server.read_text().replace('RELAY_RELEASE_VERSION = "1.0.8"', '# Legacy unversioned server'))
        (self.root / "state/deployment.json").unlink()
        subprocess.run([str(self.bin / "systemctl"), "restart", "remotedesk-relay.service"],
                       env=self.environment, check=True, capture_output=True, timeout=10)
        deadline = time.monotonic() + 5
        while True:
            try:
                with self.tls() as sock:
                    self.send(sock, dict(version=1, token="ab" * 32, role="directory", pageSize=32, offset=0))
                    self.assertTrue(self.receive(sock)["ok"])
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(.02)
        result = self.result(self.install())
        self.assertEqual("1.0.8", result["serverVersion"])
        self.assertEqual(first["accessToken"], result["accessToken"])
        self.assertEqual(first["tlsCertificateSha256"], result["tlsCertificateSha256"])

    def test_symlinked_installation_file_is_not_overwritten(self):
        self.result(self.install())
        server = self.root / "lib/remotedesk_relay_server.py"
        unrelated = self.root / "unrelated.py"
        unrelated.write_bytes(server.read_bytes())
        server.unlink()
        server.symlink_to(unrelated)
        before = unrelated.read_bytes()
        result = self.install(source=self.candidate())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("non-regular", result.stderr)
        self.assertTrue(server.is_symlink())
        self.assertEqual(before, unrelated.read_bytes())

    def test_missing_candidate_version_is_rejected(self):
        candidate = self.root / "legacy.py"
        candidate.write_text("print('not an upgrade')\n")
        result = self.install(source=candidate)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.config / "config.json").exists())

    def test_healthy_upgrade_retains_credentials_and_private_rollback_backup(self):
        first = self.result(self.install())
        previous = self.installed_files()
        result = self.result(self.install(source=self.candidate()))
        self.assertEqual("1.0.9", result["serverVersion"])
        self.assertEqual(first["accessToken"], result["accessToken"])
        self.assertEqual(first["tlsCertificateSha256"], result["tlsCertificateSha256"])
        self.assertFalse((self.root / "state/deployment-pending.json").exists())
        metadata = json.loads((self.root / "state/deployment.json").read_text())
        backup = Path(metadata["previousBackup"])
        self.assertEqual(0o700, backup.stat().st_mode & 0o777)
        self.assertEqual(previous["config/config.json"][0], (backup / "config.json").read_bytes())
        self.assertEqual(0o600, (backup / "relay.key").stat().st_mode & 0o777)

    def test_new_process_failing_health_rolls_back_all_files_and_service(self):
        self.result(self.install())
        previous = self.installed_files()
        result = self.install(source=self.candidate(broken=True), port=self._free_port())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("previous files/service restored", result.stderr)
        self.assertEqual(previous, self.installed_files())
        self.assertFalse((self.root / "state/deployment-pending.json").exists())
        self.assertEqual("1.0.8", self.result(self.install())["serverVersion"])

    def test_restart_failure_rolls_back_and_reports_failure_not_success(self):
        self.result(self.install())
        previous = self.installed_files()
        (self.root / "fail-once").write_text("restart")
        result = self.install(source=self.candidate())
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("REMOTEDESK_RELAY_RESULT=", result.stdout)
        self.assertEqual(previous, self.installed_files())
        self.assertEqual("1.0.8", self.result(self.install())["serverVersion"])

    def test_first_install_failure_removes_candidate_and_disables_service(self):
        result = self.install(source=self.candidate(broken=True))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual({}, self.installed_files())
        self.assertFalse((self.root / "enabled").exists())
        self.assertFalse((self.root / "service.pid").exists())
        self.assertFalse((self.root / "state/deployment-pending.json").exists())

    def test_failed_rollback_keeps_recovery_marker_and_blocks_another_update(self):
        self.result(self.install())
        (self.root / "fail-all-restarts").touch()
        result = self.install(source=self.candidate())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("manual recovery", result.stderr)
        pending = self.root / "state/deployment-pending.json"
        self.assertTrue(pending.exists())
        before = self.installed_files()
        another = self.install(source=self.candidate("1.0.10"))
        self.assertIn("interrupted deployment", another.stderr)
        self.assertEqual(before, self.installed_files())

    def test_missing_existing_certificate_does_not_rotate_identity(self):
        self.result(self.install())
        (self.config / "relay.crt").unlink()
        before = self.installed_files()
        self.log.write_text("")
        result = self.install(source=self.candidate())
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(before, self.installed_files())
        self.assertNotIn("restart", self.log.read_text())

    def test_candidate_invalid_syntax_fails_before_mutation(self):
        self.result(self.install())
        before = self.installed_files()
        candidate = self.root / "invalid.py"
        candidate.write_text("def broken(:\n")
        result = self.install(source=candidate)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(before, self.installed_files())

    def test_active_pending_session_blocks_upgrade_but_not_noop_configuration(self):
        self.result(self.install())
        host, viewer = self.tls(), self.tls()
        device_id = str(uuid.uuid4())
        self.send(host, dict(version=1, token="ab" * 32, role="host-control",
                             deviceId=device_id, machineName="Owned installer fixture", platform="test"))
        self.assertTrue(self.receive(host)["ok"])
        self.send(viewer, dict(version=1, token="ab" * 32, role="viewer", deviceId=device_id))
        self.assertEqual("open", self.receive(host)["type"])
        before = self.installed_files()
        self.assertEqual("1.0.8", self.result(self.install())["serverVersion"])
        result = self.install(source=self.candidate())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("active/pending", result.stderr)
        self.assertEqual(before, self.installed_files())

    def test_signal_during_candidate_health_check_restores_previous_service(self):
        self.result(self.install())
        before = self.installed_files()
        (self.root / "restarted").unlink()
        process = subprocess.Popen(["sh", str(self.installer), str(self.candidate(broken=True))],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   env=self.environment, text=True)
        try:
            process.stdin.write("ab" * 32 + "\n" + str(self.port) + "\n")
            process.stdin.close()
            process.stdin = None
            deadline = time.monotonic() + 10
            while not (self.root / "restarted").exists() and process.poll() is None and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertTrue((self.root / "restarted").exists())
            process.send_signal(signal.SIGTERM)
            output, error = process.communicate(timeout=15)
            self.assertNotEqual(0, process.returncode)
            self.assertNotIn("REMOTEDESK_RELAY_RESULT=", output)
            self.assertIn("previous files/service restored", error)
            self.assertEqual(before, self.installed_files())
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()


if __name__ == "__main__":
    unittest.main()
