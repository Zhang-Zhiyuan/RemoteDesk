"""Actual native xterm/OpenSSH/PTY on an explicitly owned Xvfb and SSH fixture."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_relay as relay
import remotedesk_linux_ssh as ssh


async def run(metadata, output):
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("This probe requires a separately owned Xvfb without Wayland")
    data = json.loads(metadata.read_text())
    if data.get("owned") is not True:
        raise RuntimeError("Only an owned SSH fixture is allowed")
    output.mkdir(parents=True, exist_ok=True)
    options = relay.RelayOptions(data["serverAddress"], data["port"], data["token"],
                                 data["tlsCertificateSha256"], data["deviceId"]).validate()
    statuses = []
    report = dict(owned=True, display=os.environ["DISPLAY"], checks=[])
    if "keyType" in data:
        expected = {"rsa": "ssh-rsa", "ed25519": "ssh-ed25519"}[data["keyType"]]
        report["hostKeyType"] = data["sshHostKeyPublic"].split()[0]
        report["userKeyType"] = data["sshClientKeyPublic"].split()[0]
        if report["hostKeyType"] != expected or report["userKeyType"] != expected:
            raise AssertionError("Fixture host and user keys do not match the requested algorithm")
    with tempfile.TemporaryDirectory(prefix="remotedesk-native-ssh-probe-") as temp:
        private = Path(temp)
        config = private / "config"
        config.mkdir(mode=0o700)
        known = config / "known_hosts"
        known.write_text(ssh.host_key_alias(options) + " " + data["sshHostKeyPublic"] + "\n")
        known.chmod(0o600)
        result = private / "pty-result"
        identity = private / "identity %h with spaces"
        identity.write_bytes(Path(data["linuxKeyPath"]).read_bytes())
        identity.chmod(0o600)
        session = ssh.TerminalSession(options, data["deviceKey"], data["sshUsername"], statuses.append, config,
                                      identity_file=identity)
        real_arguments = ssh.ssh_arguments
        def arguments(*args):
            command = real_arguments(*args)
            command[1:1] = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes"]
            command.extend(["env", "HISTFILE=/dev/null", "/bin/sh", "-i"])
            return command
        with mock.patch.object(ssh, "ssh_arguments", side_effect=arguments):
            task = asyncio.create_task(session._run())
            try:
                for _ in range(150):
                    if task.done():
                        await task
                        raise RuntimeError("Terminal exited before fixture handshake")
                    if any("通道已建立" in status for status in statuses):
                        break
                    await asyncio.sleep(.1)
                else:
                    raise TimeoutError("Native SSH gate")
                await asyncio.sleep(.6)
                windows = subprocess.check_output(["xdotool", "search", "--pid", str(session.process.pid)], text=True).splitlines()
                if not windows:
                    raise RuntimeError("Owned xterm window was not created")
                window = windows[-1]
                subprocess.run(["xdotool", "windowfocus", "--sync", window], check=True)
                command = "stty rows 31 cols 97; { printf 'native-pty:'; tty; stty size; printf '中文-✓\\n'; } > " + shlex.quote(str(result))
                subprocess.run(["xdotool", "type", "--clearmodifiers", "--", command], check=True)
                subprocess.run(["xdotool", "key", "Return"], check=True)
                for _ in range(100):
                    if result.exists():
                        # The shell creates this file before tty/stty/printf
                        # finish; existence alone races partial DrvFS output.
                        text = result.read_text()
                        if "native-pty:/dev/pts/" in text and "31 97" in text and "中文-✓" in text:
                            break
                    if task.done():
                        await task
                        raise RuntimeError("Terminal ended before PTY command")
                    await asyncio.sleep(.1)
                text = result.read_text()
                if not ("native-pty:/dev/pts/" in text and "31 97" in text and "中文-✓" in text):
                    (output / "failure.json").write_text(json.dumps(dict(owned=True, observedPtyOutput=text, statuses=statuses), ensure_ascii=False, indent=2))
                    raise AssertionError("Real terminal PTY/resize/Unicode output mismatch")
                report["checks"].extend(["TLS pin + device gate", "real xterm + OpenSSH", "selected private key argument", "PTY allocated", "PTY resize", "Unicode shell input"])
                from PIL import ImageGrab
                ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "native-terminal.png")
                report["ptyOutput"] = text
                session.close()
                try:
                    await asyncio.wait_for(task, 3)
                except asyncio.CancelledError:
                    pass
                if session.process.poll() is None:
                    raise AssertionError("Owned native process not reaped")
                report["checks"].append("cancel reaps owned terminal and tunnel")
                report["passed"] = len(report["checks"])
                (output / "native-terminal.json").write_text(json.dumps(report, ensure_ascii=False, indent=2))
                print(json.dumps(dict(passed=report["passed"], output=str(output)), ensure_ascii=False))
            finally:
                session.close()
                with __import__("contextlib").suppress(asyncio.CancelledError):
                    await task


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(run(args.metadata, args.output))
