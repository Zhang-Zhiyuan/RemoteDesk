#!/usr/bin/env python3
"""Bounded, idle-only update of the explicitly selected owned relay.

SSH uses existing known_hosts, TLS is pinned before directory authentication,
credentials stay in memory, and previous server/config files remain in a private
server-side backup. No routes, firewall, global TCP settings or OS packages change.
"""
import argparse
import getpass
import hashlib
import json
from pathlib import Path
import ssl
import sys
import time
import uuid

import paramiko

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_relay as relay

SERVER = "/usr/local/lib/remotedesk-relay/remotedesk_relay_server.py"
CONFIG = "/etc/remotedesk-relay/config.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--tls-pin", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--congestion-control", choices=("system", "cubic", "bbr"),
                        help="Select a relay-only socket policy; bbr loads its already-installed kernel module")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"scope": "owned relay only; idle connections; no routes/firewall/global TCP settings changed",
              "applied": False, "rolledBack": False}
    password = getpass.getpass("Relay SSH password (hidden): ")
    client = paramiko.SSHClient()
    client.load_host_keys(str(Path.home() / ".ssh/known_hosts"))
    client.set_missing_host_key_policy(paramiko.RejectPolicy())

    def command(value, timeout=20):
        _, stdout, stderr = client.exec_command(value, timeout=timeout)
        data, error = stdout.read(128 * 1024), stderr.read(8192)
        if stdout.channel.recv_exit_status() != 0:
            # These are fixed diagnostic/service commands, never a secret-bearing command.
            raise RuntimeError("Remote command failed: " + error.decode("utf-8", errors="replace"))
        return data.decode("utf-8", errors="replace").strip()

    def read_file(sftp, path):
        with sftp.open(path, "rb") as stream:
            return stream.read(1024 * 1024)

    def write_file(sftp, path, contents, mode, owner=None):
        with sftp.open(path, "wb") as stream:
            stream.write(contents)
        sftp.chmod(path, mode)
        if owner is not None:
            sftp.chown(path, owner.st_uid, owner.st_gid)

    def replace_file(sftp, path, contents, metadata):
        temporary = path + ".transport-" + uuid.uuid4().hex
        write_file(sftp, temporary, contents, metadata.st_mode & 0o777, metadata)
        sftp.posix_rename(temporary, path)

    try:
        client.connect(args.host, username="root", password=password,
                       look_for_keys=False, allow_agent=False, timeout=10, auth_timeout=15)
        password = None
        with client.open_sftp() as sftp:
            old_server, old_config = read_file(sftp, SERVER), read_file(sftp, CONFIG)
            previous_hash = hashlib.sha256(old_server).hexdigest()
            if previous_hash.upper() != args.expected_sha256.upper():
                raise RuntimeError("Installed server differs from the inspected baseline; refusing to overwrite")
            config = json.loads(old_config)
            certificate = read_file(sftp, config["cert_file"]).decode("ascii")
            pin = hashlib.sha256(ssl.PEM_cert_to_DER_cert(certificate)).hexdigest()
            if pin.upper() != args.tls_pin.upper():
                raise RuntimeError("TLS identity changed; configuration was not modified")
            options = relay.RelayOptions(args.host, config["port"], config["access_token"], pin,
                                         str(uuid.uuid4())).validate()
            before = relay.list_devices(options)
            if any(device["busy"] for device in before):
                raise RuntimeError("Relay has active sessions; refusing to restart it")
            report.update(previousSha256=previous_hash, onlineBefore=len(before), busyBefore=False)
            if not args.apply:
                report["dryRun"] = True
                return
            candidate = (ROOT / "scripts/relay/remotedesk_relay_server.py").read_bytes()
            compile(candidate, SERVER, "exec")
            server_stat, config_stat = sftp.stat(SERVER), sftp.stat(CONFIG)
            backup = "/var/lib/remotedesk-relay/transport-backup-" + uuid.uuid4().hex
            sftp.mkdir(backup, 0o700)
            write_file(sftp, backup + "/server.py", old_server, 0o600)
            write_file(sftp, backup + "/config.json", old_config, 0o600)
            report["backup"] = backup
            if args.congestion_control == "bbr":
                # Make an existing kernel module available, not the system default.
                command("modprobe tcp_bbr")
                available = command("sysctl -n net.ipv4.tcp_available_congestion_control").split()
                if "bbr" not in available:
                    raise RuntimeError("Installed kernel does not offer BBR")
            if args.congestion_control is not None:
                config["tcp_congestion_control"] = "" if args.congestion_control == "system" else args.congestion_control
            else:
                # Preserve an existing explicit socket policy when changing code only.
                config.setdefault("tcp_congestion_control", "")
            new_config = (json.dumps(config, indent=2) + "\n").encode()
            # Recheck immediately before the short restart, without touching data sessions.
            if any(device["busy"] for device in relay.list_devices(options)):
                raise RuntimeError("A remote session started; update was not activated")
            try:
                replace_file(sftp, SERVER, candidate, server_stat)
                replace_file(sftp, CONFIG, new_config, config_stat)
                # Legacy services may wait for systemd's 90 s stop timeout.
                # Keep the SSH command alive; a timed-out client must not issue
                # a second overlapping restart while the first is still running.
                restarted_at = time.monotonic()
                command("systemctl restart remotedesk-relay", timeout=120)
                command("systemctl is-active --quiet remotedesk-relay")
                report["restartSeconds"] = round(time.monotonic() - restarted_at, 3)
                previous_ids = {item["deviceId"] for item in before}
                deadline = time.monotonic() + 35
                while True:
                    after = relay.list_devices(options)
                    if previous_ids.issubset({item["deviceId"] for item in after}):
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError("Registered hosts did not return after relay restart")
                    time.sleep(1)
                report.update(applied=True, onlineAfter=len(after), newSha256=hashlib.sha256(candidate).hexdigest(),
                              congestionControl=config["tcp_congestion_control"] or "system default",
                              globalCongestionControl=command("sysctl -n net.ipv4.tcp_congestion_control"))
            except Exception:
                replace_file(sftp, SERVER, old_server, server_stat)
                replace_file(sftp, CONFIG, old_config, config_stat)
                report["rolledBack"] = True
                command("systemctl restart remotedesk-relay", timeout=120)
                raise
    finally:
        client.close()
        (output / "update.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
