#!/usr/bin/env python3
"""Bounded byte-only relay A/B probe. No screen/input or system settings.

The controller reads the authorized relay token into memory and feeds it to a
temporary Linux process over the existing, host-key-checked SSH connection.
Each case uses a fresh process and device ID. TCP_MAXSEG is diagnostic-only and
is set before connecting; production TLS pinning and relay adapters are reused.
"""
import argparse
import asyncio
import contextlib
import hashlib
import json
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import uuid


def node(config):
    import remotedesk_linux_relay as relay

    options = relay.RelayOptions.from_dict(dict(config["relay"], deviceId=str(uuid.uuid4())))
    mss = int(config.get("mss", 0))
    if mss not in (0, 1200):
        raise ValueError("Unsupported diagnostic MSS")
    original_open = asyncio.open_connection
    negotiated = []

    async def open_connection(*args, **kwargs):
        if not kwargs.get("ssl") or not mss:
            result = await original_open(*args, **kwargs)
        else:
            host, port = args
            # This diagnostic intentionally supports the supplied IPv4 server
            # only, not arbitrary DNS or production connection policy.
            socket.inet_pton(socket.AF_INET, host)
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_MAXSEG, mss)
                sock.setblocking(False)
                await asyncio.get_running_loop().sock_connect(sock, (host, port))
                result = await original_open(sock=sock, server_hostname=host, **kwargs)
            except BaseException:
                sock.close()
                raise
        if kwargs.get("ssl"):
            sock = result[1].get_extra_info("socket")
            negotiated.append(dict(localPort=sock.getsockname()[1],
                                   mss=sock.getsockopt(socket.IPPROTO_TCP, socket.TCP_MAXSEG)))
        return result

    asyncio.open_connection = open_connection
    stop = threading.Event()
    traces, measurements = [], []
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(1)
    active = []

    def echo():
        try:
            while not stop.is_set():
                try:
                    client, _ = listener.accept()
                except socket.timeout:
                    continue
                active.append(client)
                with client:
                    client.settimeout(1)
                    while not stop.is_set():
                        try:
                            data = client.recv(64 * 1024)
                        except socket.timeout:
                            continue
                        if not data:
                            break
                        client.sendall(data)
        except OSError:
            pass

    def sample():
        while not stop.is_set():
            try:
                result = subprocess.run(["ss", "-tinp", "dst", options.server_address,
                                         "dport", "=", str(options.port)],
                                        capture_output=True, text=True, timeout=2)
                traces.append(dict(seconds=round(time.monotonic() - started, 3),
                                   sockets=result.stdout))
            except (OSError, subprocess.TimeoutExpired):
                pass
            stop.wait(1)

    started = time.monotonic()
    echo_thread = threading.Thread(target=echo, daemon=True)
    sample_thread = threading.Thread(target=sample, daemon=True)
    echo_thread.start()
    sample_thread.start()
    connector = relay.RelayHostConnector(options, listener.getsockname()[1], "RemoteDesk byte-only audit")
    viewer = None
    failure = None
    try:
        connector.start()
        if not connector.online.wait(25):
            raise TimeoutError("Synthetic relay registration timed out")
        viewer = relay.connect_viewer(options)
        for length in (256, 128 * 1024, 512 * 1024):
            # Incompressible deterministic bytes, no captured user content.
            payload = hashlib.shake_256(b"RemoteDesk relay byte-only audit").digest(length)
            began = time.monotonic()
            deadline = began + 35
            viewer.settimeout(10)
            viewer.sendall(payload)
            received = bytearray()
            first_byte = None
            while len(received) < length:
                viewer.settimeout(max(.01, min(10, deadline - time.monotonic())))
                block = viewer.recv(min(64 * 1024, length - len(received)))
                if not block:
                    raise EOFError("Relay closed before payload completed")
                first_byte = first_byte or time.monotonic()
                received.extend(block)
                if time.monotonic() > deadline:
                    raise TimeoutError("Payload deadline exceeded")
            elapsed = time.monotonic() - began
            if received != payload:
                raise ValueError("Payload integrity mismatch")
            measurements.append(dict(bytes=length, milliseconds=round(elapsed * 1000, 3),
                                     firstByteMs=round((first_byte - began) * 1000, 3),
                                     echoAggregateMbps=round(2 * length * 8 / elapsed / 1e6, 3),
                                     sha256=hashlib.sha256(received).hexdigest()))
    except Exception as error:
        # No untrusted exception text: it must not leak a token/configuration.
        failure = type(error).__name__
    finally:
        if viewer is not None:
            viewer.close()
        connector.close()
        stop.set()
        listener.close()
        for client in active:
            with contextlib.suppress(OSError):
                client.shutdown(socket.SHUT_RDWR)
        echo_thread.join(2)
        sample_thread.join(3)
    return dict(case=config["case"], requestedMss=mss, complete=failure is None,
                failure=failure, seconds=round(time.monotonic() - started, 3),
                negotiated=negotiated, measurements=measurements, tcpSamples=traces,
                scope="Actual native TLS relay, synthetic loopback echo; not video FPS or desktop latency")


def controller(args):
    from relay_public_config import read_config

    root = Path(__file__).resolve().parents[1]
    output = Path(args.output).resolve()
    if output.exists():
        raise SystemExit("Output exists; refusing to overwrite evidence")
    options = read_config(args.host, args.ssh_pin, args.tls_pin)
    ssh = ["ssh", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
           "-o", "ConnectTimeout=5", args.linux]
    stage = subprocess.check_output(ssh + ["mktemp -d /tmp/remotedesk-byte-audit-XXXXXXXX"],
                                    text=True, timeout=15).strip()
    import re
    if not re.fullmatch(r"/tmp/remotedesk-byte-audit-[A-Za-z0-9]{8}", stage):
        raise RuntimeError("Unexpected temporary path")
    print("Owned Linux staging directory: " + stage, flush=True)
    output.mkdir(parents=True)
    rows = []
    try:
        subprocess.run(["scp", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                        str(Path(__file__).resolve()), str(root / "scripts/linux/remotedesk_linux_relay.py"),
                        args.linux + ":" + stage + "/"], check=True, timeout=30)
        for label, mss in (("stock", 0), ("mss1200", 1200), ("stock-repeat", 0), ("mss1200-repeat", 1200)):
            print("Testing " + label, flush=True)
            result = subprocess.run(ssh + ["timeout 115s env PYTHONDONTWRITEBYTECODE=1 python3 " + stage +
                                          "/linux_relay_transport_probe.py --node"],
                                    input=json.dumps(dict(relay=options, case=label, mss=mss)),
                                    text=True, capture_output=True, timeout=125)
            try:
                row = json.loads(result.stdout)
            except ValueError:
                row = dict(case=label, complete=False, failure="NoValidNodeResult", exitCode=result.returncode)
            rows.append(row)
            (output / (label + ".json")).write_text(json.dumps(row, indent=2), encoding="utf-8")
            print(json.dumps({k: v for k, v in row.items() if k != "tcpSamples"}), flush=True)
        (output / "summary.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    finally:
        # Remove only the two named, probe-created files and then the empty
        # validated directory. Never recurse through remote user data.
        subprocess.run(ssh + ["rm -- " + stage + "/linux_relay_transport_probe.py " +
                              stage + "/remotedesk_linux_relay.py; " +
                              "rmdir -- " + stage], timeout=15, check=False)
    return 0 if all(row.get("complete") for row in rows) else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--node", action="store_true")
    parser.add_argument("--host")
    parser.add_argument("--ssh-pin")
    parser.add_argument("--tls-pin")
    parser.add_argument("--linux")
    parser.add_argument("--output")
    args = parser.parse_args()
    if args.node:
        print(json.dumps(node(json.load(sys.stdin))))
    else:
        for name in ("host", "ssh_pin", "tls_pin", "linux", "output"):
            if not getattr(args, name):
                parser.error("Missing --" + name.replace("_", "-"))
        raise SystemExit(controller(args))
