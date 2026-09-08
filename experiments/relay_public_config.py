#!/usr/bin/env python3
"""Read an authorized relay's existing config, without installing or restarting it.

Authenticate only after checking a previously verified SSH host-key fingerprint.
Password arrives via a hidden terminal prompt or the caller's in-memory value.
Never print the returned access token or write it into an evidence file.
"""
import base64
import getpass
import hashlib
import hmac
import json
import socket

import paramiko


def read_config(host, ssh_pin, tls_pin, password=None):
    if password is None:
        password = getpass.getpass("Relay SSH password (hidden): ")
    with socket.create_connection((host, 22), timeout=10) as connection:
        with paramiko.Transport(connection) as transport:
            transport.start_client(timeout=10)
            actual = "SHA256:" + base64.b64encode(hashlib.sha256(
                transport.get_remote_server_key().asbytes()).digest()).decode().rstrip("=")
            if not hmac.compare_digest(actual, ssh_pin):
                raise RuntimeError("SSH identity mismatch; no password was transmitted")
            transport.auth_password("root", password, fallback=False)
            channel = transport.open_session(timeout=10)
            channel.settimeout(10)
            channel.exec_command("cat /etc/remotedesk-relay/config.json")
            data = bytearray()
            while block := channel.recv(8192):
                data.extend(block)
                if len(data) > 65536:
                    raise RuntimeError("Relay configuration exceeds limit")
            if channel.recv_exit_status() != 0:
                raise RuntimeError("Unable to read existing relay configuration")
            config = json.loads(data)
            # The independently verified TLS pin, not a value trusted from config,
            # remains the product client's trust anchor.
            return dict(serverAddress=host, port=int(config["port"]),
                        accessToken=config["access_token"], tlsCertificateSha256=tls_pin)


if __name__ == "__main__":
    import argparse
    import sys
    import uuid
    from pathlib import Path
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True)
    parser.add_argument("--ssh-pin", required=True)
    parser.add_argument("--tls-pin", required=True)
    args = parser.parse_args()
    value = read_config(args.host, args.ssh_pin, args.tls_pin)
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
    import remotedesk_linux_relay as relay
    options = relay.RelayOptions.from_dict(dict(value, deviceId=str(uuid.uuid4())))
    devices = relay.list_devices(options)
    print(json.dumps(dict(server=options.server_address, port=options.port,
                          identityVerified=True, onlineDevices=len(devices),
                          platforms=[item["platform"] for item in devices])))
