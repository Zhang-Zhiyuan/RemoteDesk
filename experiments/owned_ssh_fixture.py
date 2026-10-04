"""Owned loopback TLS relay + Linux SSH gate + temporary-key sshd. No system edits."""
from __future__ import annotations

import argparse
import asyncio
import base64
import contextlib
import datetime
import getpass
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "scripts/linux"), str(ROOT / "scripts/relay")]
import remotedesk_linux_relay as relay
import remotedesk_linux_ssh as ssh
import remotedesk_relay_server as server
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID


async def run(output, key_type="rsa", allow_forwarding=False, desktop=False):
    if desktop and (os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY")
                    or os.environ.get("WAYLAND_DISPLAY") or os.environ.get("WAYLAND_SOCKET")):
        raise RuntimeError("Desktop fixture requires explicitly owned xvfb-run with Wayland unset")
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="remotedesk-owned-sshd-") as temporary:
        private = Path(temporary)
        host_key, client_key = private / "host-key", private / "client-key"
        for key in (host_key, client_key):
            algorithm = ["-t", "rsa", "-b", "2048", "-m", "PEM"] if key_type == "rsa" else ["-t", "ed25519"]
            subprocess.run(["ssh-keygen", "-q", *algorithm, "-N", "", "-f", str(key)], check=True)
        public_parts = Path(str(host_key) + ".pub").read_text().split()
        host_fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(base64.b64decode(public_parts[1])).digest()).decode().rstrip("=")
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            local_port = reserve.getsockname()[1]
        sshd_config = private / "sshd.conf"
        sshd_config.write_text("\n".join([
            f"Port {local_port}", "ListenAddress 127.0.0.1", f"HostKey {host_key}", f"PidFile {private / 'sshd.pid'}",
            f"AuthorizedKeysFile {client_key}.pub", "PasswordAuthentication no", "KbdInteractiveAuthentication no",
            # OpenSSH StrictModes rejects the shared /tmp ancestor even though
            # this fixture's unpredictable directory is 0700. Only this private
            # daemon bypasses that ancestor check; no system policy is edited.
            "PubkeyAuthentication yes", "UsePAM no", "StrictModes no", "PermitRootLogin no",
            f"AllowUsers {getpass.getuser()}", "AllowTcpForwarding " + ("yes" if allow_forwarding else "no"), "AllowAgentForwarding no", "X11Forwarding no",
            "Subsystem sftp internal-sftp",
            "PermitTunnel no", "LogLevel VERBOSE", "MaxSessions 4", "PrintMotd no", "PrintLastLog no", ""]))
        with (output / "sshd.log").open("w") as sshd_log:
            process = subprocess.Popen(["/usr/sbin/sshd", "-D", "-e", "-f", str(sshd_config)], stdout=sshd_log, stderr=subprocess.STDOUT)
            host = listener = None
            desktop_children, desktop_logs = [], []
            desktop_port = 56565
            tasks, writers = set(), set()
            try:
                for _ in range(100):
                    if process.poll() is not None:
                        raise RuntimeError("Owned sshd exited; see fixture sshd.log")
                    try:
                        reader, writer = await asyncio.open_connection("127.0.0.1", local_port)
                        await reader.readline()
                        writer.close()
                        await writer.wait_closed()
                        break
                    except OSError:
                        await asyncio.sleep(.02)
                else:
                    raise TimeoutError("Owned sshd startup")
                tls_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
                name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Owned SSH relay fixture")])
                now = datetime.datetime.now(datetime.timezone.utc)
                certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(tls_key.public_key())
                    .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(minutes=1))
                    .not_valid_after(now + datetime.timedelta(days=1)).sign(tls_key, hashes.SHA256()))
                cert_file, tls_file = private / "tls-cert", private / "tls-key"
                cert_file.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
                tls_file.write_bytes(tls_key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
                context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.load_cert_chain(cert_file, tls_file)
                token, device_key, identity = secrets.token_urlsafe(32), secrets.token_urlsafe(32), str(uuid.uuid4())
                if desktop:
                    environment = dict(os.environ, XDG_CONFIG_HOME=str(private / "config"), REMOTEDESK_AUTO_INSTALL="0")
                    target_log = (output / "target.log").open("w"); desktop_logs.append(target_log)
                    target = subprocess.Popen([sys.executable, str(ROOT / "experiments/interop_linux_node.py"),
                        "source", "--output", str(output)], stdout=target_log, stderr=target_log, env=environment)
                    desktop_children.append(target)
                    for _ in range(100):
                        if target.poll() is not None:
                            raise RuntimeError("Owned desktop target exited")
                        if (output / "target.json").exists():
                            break
                        await asyncio.sleep(.05)
                    else:
                        raise TimeoutError("Owned desktop target startup")
                    with socket.socket() as reserve:
                        reserve.bind(("127.0.0.1", 0)); desktop_port = reserve.getsockname()[1]
                    return_file, return_folder = output / "return-fixture.txt", output / "return-folder"
                    return_file.write_text("Owned desktop and SSH concurrency fixture 中文😀\n", encoding="utf-8")
                    return_folder.mkdir()
                    (return_folder / "nested.txt").write_text("Owned directory fixture 中文😀\n", encoding="utf-8")
                    desktop_log = (output / "desktop.log").open("w"); desktop_logs.append(desktop_log)
                    desktop_process = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/linux/remotedesk_linux_host.py"),
                        "--host", "127.0.0.1", "--port", str(desktop_port), "--password-fd", "0", "--no-discovery",
                        "--display", os.environ["DISPLAY"], "--width", "1920", "--height", "1080", "--fps", "15",
                        "--serve-seconds", "1800", "--machine-name", "Owned desktop plus SSH target",
                        "--receive-dir", str(output / "receive"), "--return-file", str(return_file), "--return-file", str(return_folder)],
                        stdin=subprocess.PIPE, stdout=desktop_log, stderr=desktop_log, env=environment)
                    desktop_children.append(desktop_process)
                    desktop_process.stdin.write(device_key.encode()); desktop_process.stdin.close()
                    for _ in range(200):
                        if desktop_process.poll() is not None:
                            raise RuntimeError("Owned desktop host exited")
                        if "host listening on" in (output / "desktop.log").read_text():
                            break
                        await asyncio.sleep(.05)
                    else:
                        raise TimeoutError("Owned desktop host startup")
                service = server.RelayServer(dict(access_token=token, cert_file=str(cert_file), key_file=str(tls_file)))
                async def handle(reader, writer):
                    task = asyncio.current_task()
                    tasks.add(task)
                    writers.add(writer)
                    try:
                        await service.handle_connection(reader, writer)
                    finally:
                        tasks.discard(task)
                        writers.discard(writer)
                listener = await asyncio.start_server(handle, "127.0.0.1", 0, ssl=context)
                port = listener.sockets[0].getsockname()[1]
                pin = hashlib.sha256(certificate.public_bytes(serialization.Encoding.DER)).hexdigest().upper()
                options = relay.RelayOptions("127.0.0.1", port, token, pin, identity).validate()
                host = relay.RelayHostConnector(options, desktop_port, "Owned temporary desktop and SSH target" if desktop else "Owned temporary SSH target",
                    desktop_enabled=desktop, ssh_enabled=True, ssh_port=local_port, device_key=device_key)
                host.start()
                for _ in range(250):
                    if host.online.is_set():
                        break
                    await asyncio.sleep(.02)
                else:
                    raise TimeoutError("Owned host registration")
                key_copy = output / "fixture-client-key"
                key_copy.write_bytes(client_key.read_bytes())
                key_copy.chmod(0o600)
                windows_key_path = subprocess.check_output(["wslpath", "-w", str(key_copy)], text=True).strip()
                metadata = dict(serverAddress="127.0.0.1", port=port, token=token, tlsCertificateSha256=pin,
                    deviceId=identity, deviceKey=device_key, sshUsername=getpass.getuser(), keyPath=windows_key_path,
                    linuxKeyPath=str(client_key), sshHostKeySha256=host_fingerprint,
                    sshHostKeyPublic=Path(str(host_key) + ".pub").read_text().strip(), hostKeyAlias=ssh.host_key_alias(options),
                    sshPort=local_port, processId=os.getpid(), sshdProcessId=process.pid, owned=True, expiresSeconds=1800,
                    keyType=key_type, allowForwarding=allow_forwarding, sshClientKeyPublic=Path(str(client_key) + ".pub").read_text().strip())
                if desktop:
                    metadata.update(desktopFixture=True, desktopPort=desktop_port, display=os.environ["DISPLAY"],
                        desktopProcessId=desktop_process.pid, sourceProcessId=target.pid,
                        targetPath=str(output / "target.json"), receiveDirectory=str(output / "receive"))
                (output / "connection.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
                print(f"Owned SSH fixture ready, relay port {port}; private metadata saved", flush=True)
                stop = asyncio.Event()
                for sig in (signal.SIGINT, signal.SIGTERM):
                    asyncio.get_running_loop().add_signal_handler(sig, stop.set)
                deadline = time.monotonic() + 1800
                last_control = None
                while not stop.is_set() and not (output / "stop").exists() and time.monotonic() < deadline:
                    control_file = output / "control.json"
                    if desktop and control_file.exists():
                        command = json.loads(control_file.read_text())
                        if command != last_control:
                            if set(command) != {"id", "desktopEnabled", "sshEnabled"} or not isinstance(command["id"], str) or any(
                                    type(command[field]) is not bool for field in ("desktopEnabled", "sshEnabled")):
                                raise ValueError("Invalid owned fixture service control")
                            host.configure_services(desktop_enabled=command["desktopEnabled"], ssh_enabled=command["sshEnabled"],
                                                    ssh_port=local_port, device_key=device_key)
                            last_control = command
                            (output / "control-result.json").write_text(json.dumps(dict(id=command["id"], services=host._services(),
                                owned=True, registrationStillOnline=host.online.is_set())))
                    await asyncio.sleep(.2)
            finally:
                if host:
                    await asyncio.to_thread(host.close)
                if listener:
                    listener.close()
                for writer in tuple(writers):
                    await relay.close_writer(writer, abort=True)
                for task in tuple(tasks):
                    task.cancel()
                await asyncio.gather(*tuple(tasks), return_exceptions=True)
                if listener:
                    await listener.wait_closed()
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=3)
                for child in reversed(desktop_children):
                    if child.poll() is None:
                        child.terminate()
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            child.kill(); child.wait(timeout=3)
                for log in desktop_logs:
                    log.close()
                (output / "stopped.json").write_text(json.dumps(dict(owned=True, sshdStopped=process.poll() is not None,
                    desktopChildrenStopped=all(child.poll() is not None for child in desktop_children))))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--key-type", choices=("rsa", "ed25519"), default="rsa")
    parser.add_argument("--allow-forwarding", action="store_true", help="Owned native SSH alias forwarding tests only; default disabled")
    parser.add_argument("--desktop", action="store_true", help="Add a real desktop host and synthetic target on explicitly owned Xvfb")
    arguments = parser.parse_args()
    asyncio.run(run(arguments.output.resolve(), arguments.key_type, arguments.allow_forwarding, arguments.desktop))
