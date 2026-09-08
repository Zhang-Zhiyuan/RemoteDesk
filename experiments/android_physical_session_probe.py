#!/usr/bin/env python3
"""Real Android host via an owned adb forward; touches only InteractionProbeActivity."""
import argparse
import getpass
import io
import json
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as wire
from PIL import Image, ImageStat


def read_ui(args):
    result = subprocess.run([args.adb, "-s", args.serial, "exec-out", "run-as",
        "com.remotedesk.codecprobe", "cat", "files/interaction-probe.json"],
        capture_output=True, text=True, encoding="utf-8", timeout=8, check=True)
    return json.loads(result.stdout)


def connect(args, password, h264=False):
    client = socket.create_connection(("127.0.0.1", args.port), timeout=5)
    client.settimeout(5)
    try:
        session = wire.authenticate(client, password)
        codecs = wire.VIDEO_CODEC_JPEG | (wire.VIDEO_CODEC_H264_ANNEX_B if h264 else 0)
        wire.write_message(client, session, wire.MESSAGE_CONTROL, wire.encode_viewer_info(codecs))
        wire.write_message(client, session, wire.MESSAGE_CONTROL, wire.encode_viewer_capabilities(
            wire.BASE_VIEWER_CAPABILITIES | wire.CAPABILITY_HIGH_FRAME_RATE_H264 | wire.CAPABILITY_HIGH_QUALITY_JPEG))
        wire.write_message(client, session, wire.MESSAGE_PING, b"")
        return client, session
    except BaseException:
        client.close()
        raise


def click(client, session, bounds, ui, width, height):
    x = round(((bounds[0] + bounds[2]) / 2) * (width - 1) / (ui["screenWidth"] - 1))
    y = round(((bounds[1] + bounds[3]) / 2) * (height - 1) / (ui["screenHeight"] - 1))
    for kind in (app.INPUT_MOUSE_MOVE, app.INPUT_MOUSE_DOWN, app.INPUT_MOUSE_UP):
        wire.write_message(client, session, wire.MESSAGE_INPUT, app.encode_input(kind, app.MOUSE_LEFT, x, y))


def stream(args, password, output, label, h264, count, test_input=False):
    started = time.monotonic()
    client, session = connect(args, password, h264)
    frames, arrivals, controls = [], [], []
    pong = False
    input_phase = 0
    first_ui = read_ui(args)
    ui = first_ui
    sample_text = "RemoteDesk实测42"
    burst_text = sample_text + "😀"
    expected = "H264AnnexB" if h264 else "Jpeg"
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            kind, payload = wire.read_message(client, session)
            if kind == wire.MESSAGE_PING:
                wire.write_message(client, session, wire.MESSAGE_PONG, payload)
            elif kind == wire.MESSAGE_PONG:
                pong |= payload == b""
            elif kind == wire.MESSAGE_CONTROL:
                controls.append(wire.decode_control(payload))
            elif kind in (wire.MESSAGE_FRAME, wire.MESSAGE_VIDEO_FRAME):
                info = wire.decode_frame(payload, kind)
                if info["encoding"] != expected:
                    continue
                encoded = payload[24 if kind == wire.MESSAGE_FRAME else 32:]
                frames.append((info, encoded))
                arrivals.append(time.monotonic())
                if test_input and len(frames) % 5 == 0:
                    ui = read_ui(args)
                    if input_phase == 0:
                        click(client, session, ui["buttonBounds"], ui, info["width"], info["height"])
                        input_phase = 1
                    elif input_phase == 1 and ui["clicks"] > first_ui["clicks"]:
                        click(client, session, ui["editorBounds"], ui, info["width"], info["height"])
                        input_phase = 2
                    elif input_phase == 2 and ui["editorFocused"]:
                        for char in burst_text:
                            wire.write_message(client, session, wire.MESSAGE_INPUT,
                                               app.encode_input(app.INPUT_TEXT, data=ord(char)))
                        input_phase = 3
                    elif input_phase == 3 and ui["text"] == burst_text:
                        for key_kind in (app.INPUT_KEY_DOWN, app.INPUT_KEY_UP):
                            wire.write_message(client, session, wire.MESSAGE_INPUT,
                                               app.encode_input(key_kind, data=0x08))
                        input_phase = 4
                    elif input_phase == 4 and ui["text"] == sample_text:
                        input_phase = 5
                if len(frames) >= count and pong and (not test_input or input_phase == 5):
                    break
    finally:
        client.close()
    if len(frames) < count or not pong:
        raise RuntimeError(f"{label}: incomplete stream ({len(frames)}/{count}, pong={pong}); controls={controls}")
    if test_input and input_phase != 5:
        raise RuntimeError(f"{label}: input not observed, phase={input_phase}, observed={ui}")
    last = frames[-1][0]
    if h264:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            raise RuntimeError("Local FFmpeg is needed to validate encoded synthetic pixels")
        decoded = subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "h264",
            "-i", "pipe:0", "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1"],
            input=b"".join(item[1] for item in frames[:30]), capture_output=True, timeout=10, check=True)
        pixels = decoded.stdout
    else:
        pixels = frames[-1][1]
    with Image.open(io.BytesIO(pixels)) as image:
        image.load()
        size = list(image.size)
        variance = ImageStat.Stat(image.convert("RGB")).stddev
        if size != [last["width"], last["height"]] or max(variance) < 15:
            raise RuntimeError(label + ": flat/invalid synthetic image")
        image.save(output / (label + "-synthetic.png"))
    elapsed = arrivals[-1] - arrivals[0]
    report = {"label": label, "encoding": expected, "frames": len(frames), "pong": pong,
        "size": size, "pixelStdDev": variance, "firstFrameMs": (arrivals[0] - started) * 1000,
        "arrivalFps": (len(frames) - 1) / elapsed, "encodedMbps": sum(len(item[1]) for item in frames) * 8 / elapsed / 1e6,
        "inputVerified": input_phase == 5 if test_input else None,
        "emojiBackspaceVerified": input_phase == 5 if test_input else None,
        "observedSyntheticUi": ui if test_input else None,
        "allIndependent": all(info.get("flags") == 3 for info, _ in frames) if h264 else None,
        "passed": True}
    (output / (label + ".json")).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False), flush=True)
    return report


def takeover(args, password, output):
    old, old_session = connect(args, password, True)
    closed = threading.Event()
    def drain():
        try:
            while True:
                kind, payload = wire.read_message(old, old_session)
                if kind == wire.MESSAGE_PING:
                    wire.write_message(old, old_session, wire.MESSAGE_PONG, payload)
        except Exception:
            closed.set()
    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    try:
        replacement = stream(args, password, output, "h264-takeover", True, 30)
        if not closed.wait(5):
            raise RuntimeError("Previous authenticated viewer was not disconnected on takeover")
        return {"previousViewerDisconnected": True, "replacement": replacement}
    finally:
        old.close()
        reader.join(timeout=2)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    password = getpass.getpass("Temporary Android test session password: ")
    report = {"complete": False, "scope": "actual Android host over USB/adb TCP forward; synthetic UI only"}
    try:
        initial = read_ui(args)
        if not initial["passwordStylesMasked"] or initial["clicks"] != 0 or initial["text"]:
            raise RuntimeError("Start a fresh InteractionProbeActivity with fixed password styles")
        try:
            with socket.create_connection(("127.0.0.1", args.port), timeout=5) as client:
                client.settimeout(5)
                wire.authenticate(client, password + "-incorrect")
            raise RuntimeError("Invalid password accepted")
        except PermissionError:
            report["wrongPasswordRejected"] = True
        report["jpeg"] = stream(args, password, output, "jpeg-input", False, 60, test_input=True)
        report["h264"] = stream(args, password, output, "h264", True, 120)
        report["reconnect"] = stream(args, password, output, "h264-reconnect", True, 30)
        report["takeover"] = takeover(args, password, output)
        report["complete"] = True
    except Exception as error:
        report["failure"] = str(error)
    (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
