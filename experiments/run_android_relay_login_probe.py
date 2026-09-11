#!/usr/bin/env python3
"""Exercise the isolated Android login probe; real passwords use hidden stdin only."""
import argparse
import getpass
import json
from pathlib import Path
import socket
import struct
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--fixture', type=Path)
    parser.add_argument('--wrong-password', action='store_true')
    parser.add_argument('--wrong-identity', action='store_true')
    parser.add_argument('--identity', default='')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = Path(args.output).resolve()
    if not output.is_relative_to(root / 'artifacts') or output.exists():
        raise ValueError('Use a new evidence file inside artifacts')
    if not args.fixture and (args.wrong_password or args.wrong_identity):
        raise ValueError('Negative authentication probes are restricted to the owned fixture')
    request = dict(serverAddress='8.138.5.232', sshPort=22, sshIdentity=args.identity)
    if args.fixture:
        fixture = json.loads(args.fixture.read_text())
        request.update(serverAddress='10.0.2.2', sshPort=fixture['sshPort'], sshIdentity=fixture['sshIdentity'])
        password = 'owned-test-root-password' if not args.wrong_password else 'intentionally-wrong-fixture-password'
        if args.wrong_identity:
            request['sshIdentity'] = 'SHA256:' + 'A' * 43
    else:
        password = getpass.getpass('Authorized server root password (not stored): ')

    def adb(*arguments, check=True):
        return subprocess.run([args.adb, '-s', args.serial, *arguments], check=check,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30).stdout.decode().strip()

    package = 'com.remotedesk.viewerprobe'
    adb('shell', 'am', 'force-stop', package)
    adb('shell', 'run-as', package, 'rm', '-f', 'files/relay-login-ready.json')
    adb('shell', 'am', 'start', '-W', '-n', package + '/com.remotedesk.agent.RelayLoginProbeActivity')
    until = time.monotonic() + 15
    port = None
    while time.monotonic() < until:
        try:
            port = json.loads(adb('exec-out', 'run-as', package, 'cat', 'files/relay-login-ready.json'))['port']
            break
        except (subprocess.CalledProcessError, ValueError, KeyError):
            time.sleep(.2)
    if port is None:
        raise RuntimeError('Isolated Android probe did not become ready')
    forwarded = adb('forward', 'tcp:0', f'tcp:{port}')
    try:
        with socket.create_connection(('127.0.0.1', int(forwarded)), timeout=10) as control:
            control.settimeout(55)
            request['password'] = password
            payload = bytearray(json.dumps(request).encode())
            request.pop('password')
            password = None
            try:
                control.sendall(struct.pack('>I', len(payload)))
                control.sendall(payload)
            finally:
                payload[:] = b'\0' * len(payload)

            def read(length):
                chunks = bytearray()
                while len(chunks) < length:
                    chunk = control.recv(length - len(chunks))
                    if not chunk:
                        raise EOFError('Android probe closed early')
                    chunks.extend(chunk)
                return chunks

            length = struct.unpack('>I', read(4))[0]
            if not 0 < length <= 65536:
                raise ValueError('Invalid probe result length')
            result = json.loads(read(length))
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(result, ensure_ascii=True), flush=True)
        expected_success = not (args.wrong_password or args.wrong_identity)
        return 0 if result['complete'] == expected_success else 1
    finally:
        password = None
        adb('forward', '--remove', 'tcp:' + forwarded)
        adb('shell', 'am', 'force-stop', package)


if __name__ == '__main__':
    raise SystemExit(main())
