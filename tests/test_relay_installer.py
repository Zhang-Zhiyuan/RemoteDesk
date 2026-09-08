"""Exercise the shipped installer in a private directory with OS services stubbed.

No real users, systemd units, firewall rules or /etc configuration are changed.
OpenSSL, Python, file permissions and the installer itself execute normally.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


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
        self.installer.write_text(installer, encoding="utf-8")
        self.log = self.root / "commands"
        self._command("id", '#!/bin/sh\n[ "$1" != "-u" ] || printf "0\\n"\n')
        self._command("chown", "#!/bin/sh\nexit 0\n")
        self._command("ufw", "#!/bin/sh\nexit 1\n")
        self._command("firewall-cmd", "#!/bin/sh\nexit 1\n")
        self._command("systemctl", '''#!/bin/sh
printf '%s\n' "$*" >> "$RELAY_TEST_LOG"
exit 0
''')
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                RELAY_TEST_LOG=str(self.log))

    def _command(self, name, content):
        target = self.bin / name
        target.write_text(content, encoding="utf-8")
        target.chmod(0o700)

    def install(self, token="ab" * 32, port=56567):
        return subprocess.run(
            ["sh", str(self.installer), str(SCRIPTS / "remotedesk_relay_server.py")],
            input=f"{token}\n{port}\n", text=True, capture_output=True,
            env=self.environment, timeout=30,
        )

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
        self.assertEqual(0o640, (self.config / "config.json").stat().st_mode & 0o777)
        self.assertEqual(0o640, (self.config / "relay.key").stat().st_mode & 0o777)

    def test_custom_low_port_and_changed_port_restart_only_when_needed(self):
        self.assertEqual(443, self.result(self.install(port=443))["port"])
        self.log.write_text("", encoding="utf-8")
        self.assertEqual(8443, self.result(self.install(port=8443))["port"])
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


if __name__ == "__main__":
    unittest.main()
