#!/usr/bin/env python3
"""Authorized, bounded WAN diagnostics; no desktop/input or persistent settings.

TCP uses the product's existing pinned TLS relay port and a synthetic server-side
host. UDP is a temporary authenticated echo on the same numeric port. No firewall
changes, production restart, credentials in argv, or secret-bearing reports.
"""
import argparse
import base64
import contextlib
import getpass
import hashlib
import hmac
import json
from pathlib import Path
import secrets
import select
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid

MAGIC = b"RD-WAN-AUDIT-v1!!"
UDP_HEADER_BYTES = len(MAGIC) + 16 + 4
UDP_TAG_BYTES = 32
MAX_BYTES = 512 * 1024


def payload(length):
    return hashlib.shake_256(b"RemoteDesk byte-only WAN audit").digest(length)


def receive(sock, length):
    result = bytearray()
    while len(result) < length:
        block = sock.recv(min(65536, length - len(result)))
        if not block:
            raise EOFError("Incomplete synthetic payload")
        result.extend(block)
    return bytes(result)


def udp_packet(key, nonce, sequence, size):
    if len(nonce) != 16 or not 80 <= size <= 1200:
        raise ValueError("Invalid diagnostic UDP packet dimensions")
    body = MAGIC + nonce + struct.pack("!I", sequence) + payload(size - UDP_HEADER_BYTES - UDP_TAG_BYTES)
    return body + hmac.digest(key, body, "sha256")


def udp_identity(key, packet):
    if (not 80 <= len(packet) <= 1200 or not packet.startswith(MAGIC)
            or not hmac.compare_digest(packet[-UDP_TAG_BYTES:], hmac.digest(key, packet[:-UDP_TAG_BYTES], "sha256"))):
        return None
    return packet[len(MAGIC):UDP_HEADER_BYTES]


def server(config):
    import remotedesk_linux_relay as relay
    options = relay.RelayOptions.from_dict(dict(config["relay"], serverAddress="127.0.0.1"))
    secret = bytes.fromhex(config["udpKey"])
    stop = threading.Event()
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(2)
    listener.settimeout(1)
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.settimeout(1)
    udp_error = None
    try:
        udp.bind(("0.0.0.0", options.port))
    except OSError as error:
        udp_error = type(error).__name__
        udp.close()
    stats = dict(udpValid=0, udpInvalid=0, udpBytes=0)

    def udp_echo():
        seen = set()
        deadline = time.monotonic() + 280
        while not stop.is_set() and time.monotonic() < deadline and stats["udpBytes"] < 24 * 1024 * 1024:
            try:
                packet, peer = udp.recvfrom(1400)
            except socket.timeout:
                continue
            except OSError:
                return
            identity = udp_identity(secret, packet)
            if identity is None:
                stats["udpInvalid"] += 1
                continue
            if identity in seen or len(seen) >= 20000:
                continue
            seen.add(identity)
            udp.sendto(packet, peer)  # No amplification; identical authenticated bytes.
            stats["udpValid"] += 1
            stats["udpBytes"] += len(packet)

    tcp_slots = threading.BoundedSemaphore(4)
    tcp_clients, tcp_lock = set(), threading.Lock()

    def serve_tcp(client):
        try:
            with client:
                client.settimeout(35)
                try:
                    while not stop.is_set():
                        mode, length = struct.unpack("!cI", receive(client, 5))
                        if mode not in (b"U", b"D", b"F") or not 0 < length <= MAX_BYTES:
                            break
                        expected = payload(length)
                        if mode == b"F":
                            if length != 24576:
                                break
                            data = receive(client, length)
                            if data[16:] != expected[16:]:
                                break
                            client.sendall(data[:16])
                        elif mode == b"U":
                            data = receive(client, length)
                            if data != expected:
                                break
                            client.sendall(hashlib.sha256(data).digest())
                        else:
                            client.sendall(expected)
                except (OSError, EOFError):
                    pass
        finally:
            with tcp_lock:
                tcp_clients.discard(client)
            tcp_slots.release()

    def tcp_endpoint():
        while not stop.is_set():
            try:
                client, _ = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            if not tcp_slots.acquire(blocking=False):
                client.close()
                continue
            with tcp_lock:
                tcp_clients.add(client)
            threading.Thread(target=serve_tcp, args=(client,), daemon=True).start()

    tcp_thread = threading.Thread(target=tcp_endpoint, daemon=True)
    tcp_thread.start()
    udp_thread = None
    if udp_error is None:
        udp_thread = threading.Thread(target=udp_echo, daemon=True)
        udp_thread.start()
    connector = relay.RelayHostConnector(options, listener.getsockname()[1], "RemoteDesk WAN byte-only audit")
    connector.start()
    try:
        if not connector.online.wait(20):
            raise TimeoutError("Synthetic relay host registration failed")
        print(json.dumps(dict(ready=True, udpReady=udp_error is None, udpError=udp_error)), flush=True)
        # Controller holds SSH stdin open and sends only this non-secret stop marker.
        sys.stdin.readline()
    finally:
        stop.set()
        connector.close()
        listener.close()
        with tcp_lock:
            for client in tcp_clients:
                with contextlib.suppress(OSError):
                    client.shutdown(socket.SHUT_RDWR)
                client.close()
        if udp_error is None:
            udp.close()
        tcp_thread.join(2)
        if udp_thread:
            udp_thread.join(2)
        print(json.dumps(stats), flush=True)


def client(config):
    import remotedesk_linux_relay as relay
    options = relay.RelayOptions.from_dict(config["relay"])
    if config.get("streamTest"):
        return stream_test(relay, options)
    rows = []
    # One WAN leg; the synthetic counterpart lives on the relay server itself.
    for round_index in range(0 if config.get("udpOnly") else 2):
        sock = None
        try:
            began = time.monotonic()
            sock = relay.connect_viewer(options)
            sock.settimeout(35)
            rows.append(dict(kind="connect", round=round_index, ms=(time.monotonic() - began) * 1000))
            for mode, length in ((b"U", 256), (b"U", 131072), (b"D", 131072), (b"U", MAX_BYTES), (b"D", MAX_BYTES)):
                data = payload(length)
                began = time.monotonic()
                sock.sendall(struct.pack("!cI", mode, length))
                if mode == b"U":
                    sock.sendall(data)
                    expected = hashlib.sha256(data).digest()
                    actual = receive(sock, 32)
                else:
                    actual = receive(sock, length)
                    expected = data
                elapsed = time.monotonic() - began
                if actual != expected:
                    raise ValueError("Synthetic payload integrity mismatch")
                rows.append(dict(kind="upload" if mode == b"U" else "download", round=round_index,
                                 bytes=length, ms=elapsed * 1000, Mbps=length * 8 / elapsed / 1e6,
                                 integrityVerified=True))
        except Exception as error:
            rows.append(dict(kind="tcpFailure", round=round_index, errorType=type(error).__name__))
        finally:
            if sock:
                sock.close()

    secret = bytes.fromhex(config["udpKey"])
    nonce = secrets.token_bytes(16)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        udp.connect((options.server_address, options.port))
        udp.settimeout(1)
        for index in range(12):
            size = 96 if index < 6 else 1200
            packet = udp_packet(secret, nonce, index, size)
            began = time.monotonic()
            try:
                udp.send(packet)
                actual = udp.recv(1400)
                rows.append(dict(kind="udp", bytes=size, ms=(time.monotonic() - began) * 1000,
                                 integrityVerified=actual == packet))
            except OSError as error:
                rows.append(dict(kind="udpFailure", bytes=size, errorType=type(error).__name__))
        udp.setblocking(False)
        for packets_per_second in (100, 500, 1000):
            count = packets_per_second  # One second of paced traffic; <= 1.2 MB each way.
            burst_nonce = secrets.token_bytes(16)
            sent, received = {}, {}
            began = time.monotonic()
            deadline = began + 3
            next_index = 0
            while time.monotonic() < deadline and len(received) < count:
                now = time.monotonic()
                # Do not create a catch-up flood after a scheduler stall.
                if next_index < count and now >= began + next_index / packets_per_second:
                    packet = udp_packet(secret, burst_nonce, next_index, 1200)
                    try:
                        udp.send(packet)
                        sent[udp_identity(secret, packet)] = (now, packet)
                        next_index += 1
                    except BlockingIOError:
                        pass
                readable, _, _ = select.select([udp], [], [], 0.0005)
                if readable:
                    while True:
                        try:
                            packet = udp.recv(1400)
                        except BlockingIOError:
                            break
                        identity = udp_identity(secret, packet)
                        if identity in sent and packet == sent[identity][1]:
                            received[identity] = (time.monotonic() - sent[identity][0]) * 1000
            timings = sorted(received.values())
            rows.append(dict(kind="udpBurst", targetMbps=packets_per_second * 1200 * 8 / 1e6,
                             sent=len(sent), echoed=len(received), integrityVerified=True,
                             medianRttMs=timings[len(timings) // 2] if timings else None,
                             p95RttMs=timings[min(len(timings) - 1, int(len(timings) * .95))] if timings else None))
    return dict(platform=sys.platform, measurements=rows,
                scope="One WAN leg to synthetic host on actual TLS relay; UDP is diagnostic-only, not product video")


def stream_test(relay, options):
    """Timestamped latest-only frame-size traffic. No actual video/desktop."""
    rows = []
    original = {name: getattr(relay, name) for name in (
        "COPY_BUFFER_BYTES", "WRITE_BUFFER_HIGH_BYTES", "WRITE_BUFFER_LOW_BYTES", "TCP_NOTSENT_LOWAT_BYTES",
        "LOOPBACK_READER_LIMIT_BYTES", "configure_loopback_socket")}
    for round_index, profile in enumerate(("baseline", "bounded", "baseline", "bounded")):
        for name, value in original.items():
            setattr(relay, name, value)
        if profile == "baseline":
            relay.COPY_BUFFER_BYTES = relay.WRITE_BUFFER_HIGH_BYTES = relay.TCP_NOTSENT_LOWAT_BYTES = 65536
            relay.WRITE_BUFFER_LOW_BYTES = 16384
            relay.LOOPBACK_READER_LIMIT_BYTES = 65536
            relay.configure_loopback_socket = lambda sock: None
        sock = relay.connect_viewer(options)
        # Match the Linux product host's accepted socket, not an unlimited producer.
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, (128 if profile == "baseline" else 16) * 1024)
        sock.settimeout(5)
        stop = threading.Event()
        sent, receipts = [], []
        started = time.monotonic()

        def read_receipts():
            buffer = bytearray()
            try:
                while not stop.is_set():
                    if not select.select([sock], [], [], .2)[0]:
                        continue
                    part = sock.recv(16 - len(buffer))
                    if not part:
                        return
                    buffer.extend(part)
                    if len(buffer) == 16:
                        sequence, created_at = struct.unpack("!Qd", buffer)
                        receipts.append(dict(sequence=sequence, roundTripMs=(time.monotonic() - created_at) * 1000))
                        buffer.clear()
            except (OSError, EOFError):
                pass

        reader = threading.Thread(target=read_receipts, daemon=True)
        reader.start()
        peak_pending = 0
        error = None
        try:
            while time.monotonic() - started < 12:
                created_at = time.monotonic()
                # Once a write unblocks, create only the current sample, never a
                # backlog of missed capture ticks (matching the product mailbox).
                sequence = int((created_at - started) * 30)
                data = struct.pack("!Qd", sequence, created_at) + payload(24576)[16:]
                sock.sendall(struct.pack("!cI", b"F", len(data)) + data)
                sent.append(sequence)
                peak_pending = max(peak_pending, len(sent) - len(receipts))
                stop.wait(max(0, 1 / 30 - (time.monotonic() - created_at)))
            reader.join(5)
        except OSError as failure:
            error = type(failure).__name__
        finally:
            stop.set()
            with contextlib.suppress(OSError):
                sock.shutdown(socket.SHUT_RDWR)
            sock.close()
            reader.join(2)
        timings = sorted(item["roundTripMs"] for item in receipts)
        rows.append(dict(round=round_index, profile=profile, sent=len(sent), received=len(receipts), peakPending=peak_pending,
                         elapsedSeconds=time.monotonic() - started, errorType=error,
                         medianRoundTripMs=timings[len(timings) // 2] if timings else None,
                         p95RoundTripMs=timings[min(len(timings) - 1, int(len(timings) * .95))] if timings else None,
                         receipts=receipts))
    for name, value in original.items():
        setattr(relay, name, value)
    return dict(platform=sys.platform, measurements=rows,
                scope="Timestamped 24 KiB latest-only synthetic frames at up to 30 FPS; actual pinned TLS relay, not video rendering")


def controller(args):
    import paramiko
    root = Path(__file__).resolve().parents[1]
    output = Path(args.output).resolve()
    if output.exists():
        raise RuntimeError("Output exists; preserve prior evidence")
    password = getpass.getpass("Relay SSH password (hidden): ")
    stage = "/tmp/remotedesk-wan-audit-" + uuid.uuid4().hex
    ssh = ["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5", args.linux]
    with socket.create_connection((args.host, 22), timeout=10) as connection:
        with paramiko.Transport(connection) as transport:
            transport.start_client(timeout=10)
            actual = "SHA256:" + base64.b64encode(hashlib.sha256(transport.get_remote_server_key().asbytes()).digest()).decode().rstrip("=")
            if not hmac.compare_digest(actual, args.ssh_pin):
                raise RuntimeError("SSH identity changed; no password transmitted")
            transport.auth_password("root", password, fallback=False)
            password = None
            output.mkdir(parents=True)
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                with sftp.open("/etc/remotedesk-relay/config.json", "rb") as source:
                    server_config = json.loads(source.read(65536))
                options = dict(serverAddress=args.host, port=server_config["port"],
                               accessToken=server_config["access_token"], tlsCertificateSha256=args.tls_pin,
                               deviceId=str(uuid.uuid4()))
                (output / "server-policy.json").write_text(json.dumps(dict(
                    configuredSocketCongestion=server_config.get("tcp_congestion_control", "system default"),
                    relayPort=options["port"])), encoding="utf-8")
                if args.inspect_network:
                    # Fixed read-only commands; no credentials, process argv, or firewall changes.
                    for name, command in (
                        ("qdisc", "tc -s qdisc show"),
                        ("filters", "ip -o link show | awk -F': ' '{print $2}' | cut -d@ -f1 | while read iface; do tc filter show dev \"$iface\"; done"),
                        ("route", "ip -j -4 route"),
                        ("listeners", "ss -lntup"),
                        ("tcp-policy", "sysctl net.ipv4.tcp_available_congestion_control net.ipv4.tcp_congestion_control net.core.default_qdisc"),
                    ):
                        with transport.open_session(timeout=10) as diagnostic:
                            diagnostic.settimeout(10)
                            diagnostic.exec_command(command)
                            value = diagnostic.makefile("r").read(65536).decode()
                            (output / ("server-" + name + ".txt")).write_text(value, encoding="utf-8")
                            print(name + ": " + value, flush=True)
                    return
                sftp.mkdir(stage, mode=0o700)
                remote = transport.open_session(timeout=10)
                remote.settimeout(40)
                input_stream = output_stream = None
                linux_stage = None
                try:
                    for path in (Path(__file__), root / "scripts/linux/remotedesk_linux_relay.py"):
                        sftp.put(str(path), stage + "/" + path.name)
                    remote.exec_command("timeout 300s env PYTHONDONTWRITEBYTECODE=1 python3 -u " + stage + "/relay_wan_path_probe.py --mode server")
                    input_stream, output_stream = remote.makefile_stdin("w"), remote.makefile("r")
                    config = dict(relay=options, udpKey=secrets.token_hex(32), udpOnly=args.udp_only,
                                  streamTest=args.stream_test)
                    input_stream.write(json.dumps(config) + "\n")
                    input_stream.flush()
                    ready = json.loads(output_stream.readline())
                    print(json.dumps(ready), flush=True)
                    (output / "server-start.json").write_text(json.dumps(ready), encoding="utf-8")
                    # Import production Python client without saving credentials.
                    sys.path.insert(0, str(root / "scripts/linux"))
                    if not args.linux_only:
                        print("Windows single-WAN-leg baseline", flush=True)
                        windows = client(config)
                        (output / "windows.json").write_text(json.dumps(windows, indent=2), encoding="utf-8")
                        print(json.dumps(windows), flush=True)
                    linux_stage = subprocess.check_output(ssh + ["mktemp -d /tmp/remotedesk-wan-client-XXXXXXXX"], text=True, timeout=15).strip()
                    import re
                    if not re.fullmatch(r"/tmp/remotedesk-wan-client-[A-Za-z0-9]{8}", linux_stage):
                        raise RuntimeError("Unexpected Linux staging path")
                    subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                                    str(Path(__file__)), str(root / "scripts/linux/remotedesk_linux_relay.py"),
                                    args.linux + ":" + linux_stage + "/"], check=True, timeout=30)
                    print("Linux single-WAN-leg baseline", flush=True)
                    result = subprocess.run(ssh + ["timeout 140s env PYTHONDONTWRITEBYTECODE=1 python3 -u " + linux_stage +
                                                  "/relay_wan_path_probe.py --mode client"],
                                            input=json.dumps(config) + "\n", text=True, capture_output=True, timeout=150)
                    linux = json.loads(result.stdout)
                    (output / "linux.json").write_text(json.dumps(linux, indent=2), encoding="utf-8")
                    print(json.dumps(linux), flush=True)
                finally:
                    if input_stream:
                        with contextlib.suppress(Exception):
                            input_stream.write("stop\n"); input_stream.flush()
                    if output_stream:
                        with contextlib.suppress(Exception):
                            stats = json.loads(output_stream.readline())
                            (output / "server-stop.json").write_text(json.dumps(stats), encoding="utf-8")
                            print(json.dumps(stats), flush=True)
                    remote.close()
                    for name in ("relay_wan_path_probe.py", "remotedesk_linux_relay.py"):
                        sftp.remove(stage + "/" + name)
                    sftp.rmdir(stage)
                    if linux_stage:
                        subprocess.run(ssh + ["rm -- " + linux_stage + "/relay_wan_path_probe.py " + linux_stage +
                                              "/remotedesk_linux_relay.py; rmdir -- " + linux_stage], timeout=15, check=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("controller", "server", "client"), default="controller")
    parser.add_argument("--host")
    parser.add_argument("--ssh-pin")
    parser.add_argument("--tls-pin")
    parser.add_argument("--linux")
    parser.add_argument("--output")
    parser.add_argument("--udp-only", action="store_true")
    parser.add_argument("--inspect-network", action="store_true")
    parser.add_argument("--stream-test", action="store_true")
    parser.add_argument("--linux-only", action="store_true")
    args = parser.parse_args()
    if args.mode == "controller":
        controller(args)
    elif args.mode == "server":
        server(json.loads(sys.stdin.readline()))
    else:
        print(json.dumps(client(json.loads(sys.stdin.readline()))))
