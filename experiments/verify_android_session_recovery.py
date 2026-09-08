"""Opt-in LAN protocol check: password rejection, takeover and reconnect.

Uses the authorized host endpoint and a stdin-only password. Sends no OS input,
changes no settings, and writes metadata only (no screenshots or credentials).
Running it intentionally replaces an existing viewer; use only an idle test host.
"""
import argparse
import json
from pathlib import Path
import socket
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire


def connect(host, port, password, h264=False):
    channel = socket.create_connection((host, port), timeout=8)
    channel.settimeout(8)
    try:
        session = wire.authenticate(channel, password)
        wire.write_message(channel, session, wire.MESSAGE_CONTROL, wire.encode_viewer_info(
            wire.VIDEO_CODEC_JPEG | (wire.VIDEO_CODEC_H264_ANNEX_B if h264 else 0)))
        wire.write_message(channel, session, wire.MESSAGE_CONTROL,
                           wire.encode_viewer_capabilities(wire.BASE_VIEWER_CAPABILITIES))
        return channel, session
    except BaseException:
        channel.close()
        raise


def frame(channel, session):
    deadline = time.monotonic() + 12
    while time.monotonic() < deadline:
        kind, payload = wire.read_message(channel, session)
        if kind == wire.MESSAGE_PING:
            wire.write_message(channel, session, wire.MESSAGE_PONG, payload)
        elif kind in (wire.MESSAGE_FRAME, wire.MESSAGE_VIDEO_FRAME):
            info = wire.decode_frame(payload, kind)
            return {key: info[key] for key in ("encoding", "width", "height")}
    raise RuntimeError("No frame before the observation deadline")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, default=56565)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    password = sys.stdin.read().rstrip("\r\n")
    if not password:
        raise RuntimeError("A stdin-only authorized host password is required")
    output = Path(args.output).resolve()
    if output.exists():
        raise RuntimeError("Refusing to overwrite previous evidence")
    report = {"complete": False, "route": "direct LAN TCP", "host": args.host,
              "port": args.port, "scope": "protocol lifecycle; no remote OS input or decoded-pixel assertions"}
    channels = []
    try:
        first, first_session = connect(args.host, args.port, password)
        channels.append(first)
        report["initialFrame"] = frame(first, first_session)
        # A failed attempt must not displace an already-authenticated viewer.
        wrong_rejected = False
        try:
            unexpected, _ = connect(args.host, args.port, password + "-invalid-probe")
            unexpected.close()
        except PermissionError as error:
            wrong_rejected = True
            report["wrongPasswordErrorType"] = type(error).__name__
        report["wrongPasswordRejected"] = wrong_rejected
        if not wrong_rejected:
            raise RuntimeError("Wrong password unexpectedly authenticated")
        report["existingSessionAfterWrongPassword"] = frame(first, first_session)
        second, second_session = connect(args.host, args.port, password, h264=True)
        channels.append(second)
        report["replacementFrame"] = frame(second, second_session)
        first.settimeout(3)
        replaced = False
        deadline = time.monotonic() + 6
        while time.monotonic() < deadline:
            try:
                wire.read_message(first, first_session)
            except (EOFError, ConnectionError):
                replaced = True
                break
        report["oldViewerDisconnected"] = replaced
        if not replaced:
            raise RuntimeError("The old viewer was not disconnected after takeover")
        first.close(); second.close()
        third, third_session = connect(args.host, args.port, password)
        channels.append(third)
        report["reconnectFrame"] = frame(third, third_session)
        report["complete"] = True
    except Exception as error:
        report["failureType"] = type(error).__name__
        raise
    finally:
        for channel in channels:
            channel.close()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
