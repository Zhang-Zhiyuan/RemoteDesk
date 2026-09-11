#!/usr/bin/env python3
"""Read only public SSH host keys, after checking an already trusted host identity."""
import argparse
import base64
import getpass
import hashlib
import json
from pathlib import Path
import shlex
import socket
import paramiko


def pin(key):
    return 'SHA256:' + base64.b64encode(hashlib.sha256(key).digest()).decode().rstrip('=')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--identity', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    if not output.is_relative_to(Path(__file__).resolve().parents[1] / 'artifacts') or output.exists():
        raise ValueError('Use a new evidence path inside artifacts')
    with socket.create_connection(('8.138.5.232', 22), timeout=12) as tcp, paramiko.Transport(tcp) as ssh:
        ssh.start_client(timeout=12)
        identity = pin(ssh.get_remote_server_key().asbytes())
        print('Observed SSH algorithm:', ssh.get_remote_server_key().get_name(), 'identity:', identity, flush=True)
        if identity != args.identity:
            raise RuntimeError('Trusted host identity mismatch; no password requested')
        password = getpass.getpass('Root password for read-only public host key inventory: ')
        try:
            ssh.auth_password('root', password, fallback=False)
        finally:
            password = None
        with ssh.open_session(timeout=12) as channel:
            channel.settimeout(12)
            # Never read the private keys or the relay access token.
            code = 'import glob,json,pathlib; print(json.dumps([pathlib.Path(p).read_text().split()[:2] for p in glob.glob("/etc/ssh/ssh_host_*_key.pub")]))'
            channel.exec_command('python3 -c ' + shlex.quote(code))
            channel.shutdown_write()
            raw = channel.makefile('rb').read(65537)
            if len(raw) > 65536 or channel.recv_exit_status() != 0:
                raise RuntimeError('Public host key inventory failed')
            result = dict(verifiedIdentity=identity, publicHostKeys=[dict(algorithm=key[0], identity=pin(base64.b64decode(key[1]))) for key in json.loads(raw)])
    output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    main()
