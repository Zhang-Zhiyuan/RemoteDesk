"""Locate this installation's SSH listener using its own public host keys."""
import glob
import os
import re
import shlex
import shutil
import socket
import subprocess
import sys
import threading
from pathlib import Path


def configured_ports_and_keys():
    ports, keys, visited = [], set(), set()

    def read_config(path):
        if path in visited or len(visited) >= 32:
            return
        visited.add(path)
        try:
            with Path(path).open(errors="replace") as config:
                lines = config.read(131072).splitlines()
            for raw in lines:
                fields = shlex.split(raw, comments=True)
                if len(fields) < 2:
                    continue
                key, value = fields[0].lower(), fields[1]
                if key == "match":
                    break
                if key == "port" and value.isdecimal():
                    ports.append(int(value))
                if key == "listenaddress" and (value.count(":") == 1 or "]:" in value):
                    port = value.rsplit(":", 1)[1]
                    if port.isdecimal():
                        ports.append(int(port))
                if key == "hostkey" and os.path.isabs(value):
                    keys.add(value + ".pub")
                if key == "include":
                    for pattern in fields[1:]:
                        for item in sorted(glob.glob(os.path.join("/etc/ssh", pattern)))[:32]:
                            read_config(item)
        except (OSError, ValueError):
            return

    read_config("/etc/ssh/sshd_config")
    if not keys:
        keys.update(glob.glob("/etc/ssh/ssh_host_*_key.pub"))
    public = set()
    for path in keys:
        try:
            with Path(path).open() as key_file:
                fields = key_file.read(16384).split()
            if len(fields) >= 2:
                public.add(tuple(fields[:2]))
        except (OSError, UnicodeError):
            pass
    # systemd socket activation may override all Port lines in sshd_config.
    try:
        unit = subprocess.run(["systemctl", "show", "ssh.socket", "sshd.socket", "-p", "Listen", "--value"],
                              capture_output=True, text=True, timeout=2)
        ports[:0] = [int(p) for p in re.findall(r"(?:^|[\s:])(\d{1,5})\s+\(Stream\)", unit.stdout)]
    except (OSError, subprocess.TimeoutExpired):
        pass
    ports.append(22)
    return list(dict.fromkeys(p for p in ports if 0 < p < 65536))[:8], public


def find_local_ssh(preferred=None):
    ports, public = configured_ports_and_keys()
    scanner = shutil.which("ssh-keyscan")
    if not public or not scanner:
        raise OSError("此系统需要 OpenSSH Server 和 OpenSSH 客户端工具。")
    if preferred:
        ports = list(dict.fromkeys([preferred, *ports]))
    for port in ports:
        for address in ("127.0.0.1", "::1"):
            try:
                with socket.create_connection((address, port), timeout=.5):
                    pass
                check = subprocess.run([scanner, "-T", "2", "-p", str(port), "-t", "ed25519,ecdsa,rsa", address],
                                       capture_output=True, text=True, timeout=3)
                if any(tuple(line.split()[1:3]) in public for line in check.stdout.splitlines()):
                    return address, port
            except (OSError, subprocess.TimeoutExpired):
                continue
    raise OSError("此系统的 SSH 监听尚未就绪。请检查 ssh.socket 或 sshd 服务。")


def serve_stdio():
    try:
        upstream = socket.create_connection(find_local_ssh(), timeout=2)
        upstream.settimeout(None)
    except OSError:
        os.write(1, b"\x02")
        return 1
    with upstream:
        os.write(1, b"\x01")
        def send():
            try:
                while chunk := os.read(0, 65536):
                    upstream.sendall(chunk)
                upstream.shutdown(socket.SHUT_WR)
            except OSError:
                pass
        threading.Thread(target=send, daemon=True).start()
        while chunk := upstream.recv(65536):
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    sys.exit(serve_stdio())
