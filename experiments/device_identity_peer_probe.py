#!/usr/bin/env python3
"""Read-only owned-host probe: authentication, negotiated identity and a few frames; no input."""
import argparse
import getpass
import json
from pathlib import Path
import socket
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", required=True); parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--output", required=True); parser.add_argument("--password-stdin", action="store_true")
    args = parser.parse_args()
    password = sys.stdin.readline().rstrip("\r\n") if args.password_stdin else getpass.getpass("Owned device connection password: ")
    report = dict(authenticated=False, identity=False, frames=0, platform="", inputSent=False)
    with socket.create_connection((args.host, args.port), timeout=5) as connection:
        connection.settimeout(8); session = wire.authenticate(connection, password); password = None
        report["authenticated"] = True
        wire.write_message(connection, session, wire.MESSAGE_CONTROL, wire.encode_viewer_info(wire.VIDEO_CODEC_JPEG))
        wire.write_message(connection, session, wire.MESSAGE_CONTROL, wire.encode_viewer_capabilities(wire.BASE_VIEWER_CAPABILITIES))
        deadline = time.monotonic() + 12; requested = False
        while time.monotonic() < deadline:
            kind, payload = wire.read_message(connection, session)
            if kind == wire.MESSAGE_PING: wire.write_message(connection, session, wire.MESSAGE_PONG, payload)
            elif kind == wire.MESSAGE_CONTROL:
                message = wire.decode_control(payload)
                if message["kind"] == wire.CONTROL_DEVICE_INFO:
                    report["platform"] = message["platform"]
                    if message["capabilities"] & wire.CAPABILITY_DEVICE_IDENTITY and not requested:
                        wire.write_message(connection, session, wire.MESSAGE_CONTROL, bytes([wire.CONTROL_DEVICE_IDENTITY_REQUEST])); requested = True
                elif message["kind"] == wire.CONTROL_DEVICE_IDENTITY: report["identity"] = bool(message["deviceId"])
            elif kind in (wire.MESSAGE_FRAME, wire.MESSAGE_VIDEO_FRAME): report["frames"] += 1
            if report["identity"] and report["frames"] >= 3: break
    report["passed"] = report["authenticated"] and report["identity"] and report["frames"] >= 3
    Path(args.output).write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))
    if not report["passed"]: raise AssertionError("Negotiated device identity/frame check failed")


if __name__ == "__main__": main()
