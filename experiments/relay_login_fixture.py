#!/usr/bin/env python3
"""Owned loopback SSH + real relay fixture. Never executes shell commands or serves user data."""
import argparse
import asyncio
import base64
import datetime
import hashlib
import json
from pathlib import Path
import shlex
import socket
import ssl
import sys
import threading
import time

import paramiko
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/relay'))
import remotedesk_relay_server as relay


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    if not output.is_relative_to(ROOT / 'artifacts'):
        raise ValueError('Fixture evidence must be inside artifacts')
    output.mkdir(parents=True, exist_ok=False)
    token = 'owned-relay-login-fixture-token-not-for-production'
    password = 'owned-test-root-password'
    ssh_key = paramiko.RSAKey.generate(2048)
    ssh_pin = 'SHA256:' + base64.b64encode(hashlib.sha256(ssh_key.asbytes()).digest()).decode().rstrip('=')
    tls_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'RemoteDesk owned login fixture')])
    now = datetime.datetime.now(datetime.timezone.utc)
    certificate = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(tls_key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(minutes=1))
        .not_valid_after(now + datetime.timedelta(days=1)).sign(tls_key, hashes.SHA256()))
    cert_path, key_path = output / 'owned.crt', output / 'owned.key'
    cert_path.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(tls_key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    key_path.chmod(0o600)
    pin = certificate.fingerprint(hashes.SHA256()).hex().upper()
    expected_reader = (ROOT / 'scripts/relay/read_remotedesk_relay_config.py').read_bytes()
    stopping, ready = threading.Event(), threading.Event()
    state = dict(authAttempts=0, successfulAuth=0, readCommands=0, invalidCommands=0)
    lock = threading.Lock()
    tcp = socket.socket()
    tcp.bind(('127.0.0.1', 0))
    tcp.listen(8)
    tcp.settimeout(.5)

    def save():
        with lock:
            (output / 'counts.json').write_text(json.dumps(state))

    async def serve_tls():
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert_path, key_path)
        service = relay.RelayServer(dict(access_token=token, cert_file=str(cert_path), key_file=str(key_path), port=0))
        async with await asyncio.start_server(service.handle_connection, '127.0.0.1', 0, ssl=context) as server:
            port = server.sockets[0].getsockname()[1]
            state['relayPort'] = port
            (output / 'ready.json').write_text(json.dumps(dict(server='127.0.0.1', sshPort=tcp.getsockname()[1], relayPort=port, sshIdentity=ssh_pin)))
            ready.set()
            while not stopping.is_set():
                await asyncio.sleep(.1)
    tls_thread = threading.Thread(target=lambda: asyncio.run(serve_tls()), daemon=True)
    tls_thread.start()

    class Server(paramiko.ServerInterface):
        def __init__(self):
            self.command_ready = threading.Event()
        def get_allowed_auths(self, username):
            return 'password'
        def check_auth_password(self, username, supplied):
            with lock:
                state['authAttempts'] += 1
                if username == 'root' and supplied == password:
                    state['successfulAuth'] += 1
                    return paramiko.AUTH_SUCCESSFUL
            return paramiko.AUTH_FAILED
        def check_channel_request(self, kind, channel_id):
            return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
        def check_channel_exec_request(self, channel, command):
            try:
                words = shlex.split(command.decode('ascii'))
                if words[:2] != ['python3', '-c'] or len(words) != 3:
                    raise ValueError()
                payload = words[2].split("b64decode('", 1)[1].split("'", 1)[0]
                if base64.b64decode(payload) != expected_reader:
                    raise ValueError()
                with lock:
                    state['readCommands'] += 1
                self.command_ready.set()
                return True
            except Exception:
                with lock:
                    state['invalidCommands'] += 1
                return False

    def handle(connection):
        try:
            with paramiko.Transport(connection) as transport:
                transport.add_server_key(ssh_key)
                server = Server()
                transport.start_server(server=server)
                channel = transport.accept(12)
                if channel is None:
                    return
                with channel:
                    if not server.command_ready.wait(12):
                        return
                    response = dict(version=1, port=state['relayPort'], accessToken=token, tlsCertificateSha256=pin)
                    channel.sendall(json.dumps(response).encode())
                    channel.send_exit_status(0)
                    channel.shutdown_write()
                    time.sleep(.15)
        except Exception:
            pass
        finally:
            connection.close()
            save()

    def serve_ssh():
        while not stopping.is_set():
            try:
                connection, _ = tcp.accept()
                threading.Thread(target=handle, args=(connection,), daemon=True).start()
            except socket.timeout:
                pass
            except OSError:
                break
    ssh_thread = threading.Thread(target=serve_ssh, daemon=True)
    ssh_thread.start()
    if not ready.wait(10):
        raise RuntimeError('Owned TLS relay did not start')
    print('Owned SSH/TLS fixture ready; type stop to finish.', flush=True)
    try:
        for line in sys.stdin:
            if line.strip() == 'stop':
                break
    finally:
        stopping.set()
        tcp.close()
        tls_thread.join(timeout=3)
        save()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
