#!/usr/bin/env python3
"""Bounded real-machine checks using an owned Xvfb and synthetic pixels only."""
import argparse
import io
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import socket
import subprocess
import sys
import threading
import time

from linux_runtime_probe import ROOT, app, receive_session, stop_owned, wire
from PIL import Image


def synthetic_desktop(output):
    import tkinter as tk
    root = tk.Tk()
    root.overrideredirect(True)
    root.geometry("1920x1080+0+0")
    canvas = tk.Canvas(root, background="#152030", highlightthickness=0)
    canvas.pack(fill="both", expand=True)
    canvas.create_rectangle(0, 0, 960, 1080, fill="white", outline="")
    canvas.create_text(100, 100, anchor="nw", text="RemoteDesk isolated physical-machine test",
                       font=("monospace", 24), fill="#152030")
    marker = canvas.create_rectangle(100, 300, 200, 400, fill="#1677ff", outline="")
    with (output / "input-events.jsonl").open("x", encoding="utf-8") as events:
        def record(event):
            events.write(json.dumps({"type": str(event.type), "x": event.x_root,
                "y": event.y_root, "keysym": event.keysym}) + "\n")
            events.flush()
        for sequence in ("<ButtonPress-1>", "<ButtonRelease-1>", "<KeyPress>", "<KeyRelease>"):
            root.bind_all(sequence, record)
        tick = 0
        def animate():
            nonlocal tick
            tick += 1
            x = 100 + (tick * 8) % 600
            canvas.coords(marker, x, 300, x + 100, 400)
            root.after(33, animate)
        root.update()
        root.focus_force()
        (output / "source-ready").touch(exist_ok=False)
        animate()
        root.after(90000, root.destroy)
        root.mainloop()


def input_session(port, password, output):
    with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
        client.settimeout(3)
        session = wire.authenticate(client, password)
        wire.write_message(client, session, wire.MESSAGE_CONTROL,
                           wire.encode_viewer_info(wire.VIDEO_CODEC_JPEG))
        caps, width, height = 0, 0, 0
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and not (caps and (width, height) == (1920, 1080)):
            kind, payload = wire.read_message(client, session)
            if kind == wire.MESSAGE_PING:
                wire.write_message(client, session, wire.MESSAGE_PONG, payload)
            elif kind == wire.MESSAGE_CONTROL:
                control = wire.decode_control(payload)
                if control["kind"] == wire.CONTROL_DEVICE_INFO:
                    caps = control["capabilities"]
            elif kind in (wire.MESSAGE_FRAME, wire.MESSAGE_VIDEO_FRAME):
                info = wire.decode_frame(payload, kind)
                width, height = info["width"], info["height"]
        if not caps & wire.CAPABILITY_INPUT_CONTROL or (width, height) != (1920, 1080):
            raise RuntimeError(f"Input unavailable or invalid frame geometry: {caps}, {width}x{height}")
        for kind in (app.INPUT_MOUSE_MOVE, app.INPUT_MOUSE_DOWN, app.INPUT_MOUSE_UP):
            wire.write_message(client, session, wire.MESSAGE_INPUT,
                               app.encode_input(kind, app.MOUSE_LEFT, 350, 550))
        for kind in (app.INPUT_KEY_DOWN, app.INPUT_KEY_UP):
            wire.write_message(client, session, wire.MESSAGE_INPUT, app.encode_input(kind, data=0x41))
        # Read frames while waiting, so host output cannot block the input test.
        deadline = time.monotonic() + 4
        events = []
        while time.monotonic() < deadline:
            kind, payload = wire.read_message(client, session)
            if kind == wire.MESSAGE_PING:
                wire.write_message(client, session, wire.MESSAGE_PONG, payload)
            lines = (output / "input-events.jsonl").read_text(encoding="utf-8").splitlines()
            events = [json.loads(line) for line in lines]
            if {"2", "3", "4", "5"}.issubset({event["type"] for event in events}):
                break
        clicks = [event for event in events if event["type"] in ("4", "5")]
        passed = ({"2", "3", "4", "5"}.issubset({event["type"] for event in events})
                  and len(clicks) == 2 and all(abs(event["x"] - 350) <= 1
                  and abs(event["y"] - 550) <= 1 for event in clicks)
                  and any(event["keysym"].lower() == "a" for event in events))
        if not passed:
            raise RuntimeError("Injected input not observed in owned Tk window: " + repr(events))
        return {"passed": True, "events": events, "scope": "encrypted protocol -> XTest -> owned Tk window"}


def host_probe(output):
    report = {"machine": platform.machine(), "kernel": platform.release(),
              "scope": "Linux runtime, isolated Xvfb; inspect kernel to distinguish WSL/physical; not the user's graphical desktop",
              "dependencies": {name: shutil.which(name) for name in ("ffmpeg", "import", "convert")},
              "complete": False, "sessions": []}
    children, files = [], []
    logger = None
    password = secrets.token_urlsafe(24)
    try:
        source_log = (output / "source.log").open("w", encoding="utf-8")
        files.append(source_log)
        source = subprocess.Popen([sys.executable, __file__, "--mode", "source", "--output", str(output)],
            stdout=source_log, stderr=subprocess.STDOUT, start_new_session=True)
        children.append(source)
        deadline = time.monotonic() + 8
        while not (output / "source-ready").exists() and time.monotonic() < deadline:
            if source.poll() is not None:
                raise RuntimeError("Synthetic Tk source failed; see source.log")
            time.sleep(0.05)
        if not (output / "source-ready").exists():
            raise RuntimeError("Synthetic Tk source did not become ready")
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        host_log = (output / "host.log").open("w", encoding="utf-8")
        files.append(host_log)
        host = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/linux/remotedesk_linux_host.py"),
            "--host", "127.0.0.1", "--port", str(port), "--password-fd", "0", "--no-discovery",
            "--display", os.environ["DISPLAY"], "--width", "1920", "--height", "1080",
            "--fps", "30", "--serve-seconds", "70", "--machine-name", "RemoteDesk-physical-probe",
            "--receive-dir", str(output / "receive")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, start_new_session=True,
            env={**os.environ, "REMOTEDESK_AUTO_INSTALL": "0"})
        children.append(host)
        host.stdin.write(password)
        host.stdin.close()
        listening = threading.Event()
        def collect():
            for line in host.stdout:
                host_log.write(line)
                host_log.flush()
                if "host listening on" in line:
                    listening.set()
        logger = threading.Thread(target=collect, daemon=True)
        logger.start()
        if not listening.wait(12):
            raise RuntimeError("Host failed to start; see host.log")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
                client.settimeout(3)
                wire.authenticate(client, password + "-invalid")
            raise RuntimeError("Invalid password accepted")
        except PermissionError:
            report["badPasswordRejected"] = True
        for label in ("jpeg", "jpeg-reconnect"):
            row, frames = receive_session(port, password, wire.VIDEO_CODEC_JPEG,
                                         "Jpeg", 45, 10, output)
            with Image.open(io.BytesIO(frames[-1][1])) as frame:
                rgb = frame.convert("RGB")
                white = rgb.getpixel((700, 800))
                dark = rgb.getpixel((1500, 800))
                if min(white) < 220 or max(dark) > 70:
                    raise RuntimeError("Captured pixels do not match owned synthetic source")
                row["sourcePixelsVerified"] = True
            row["label"] = label
            report["sessions"].append(row)
        report["input"] = input_session(port, password, output)
        report["complete"] = True
    except Exception as failure:
        report["failure"] = str(failure)
    finally:
        for child in reversed(children):
            stop_owned(child)
        if logger:
            logger.join(timeout=2)
        for file in files:
            file.close()
        report["ownedProcessesStopped"] = all(child.poll() is not None for child in children)
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report["complete"] else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--mode", choices=("host", "source"), default="host")
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise SystemExit("Requires an owned xvfb-run with REMOTEDESK_ISOLATED_XVFB=1")
    output = Path(args.output).resolve()
    if args.mode == "source":
        synthetic_desktop(output)
        return 0
    output.mkdir(parents=True, exist_ok=False)
    return host_probe(output)


if __name__ == "__main__":
    raise SystemExit(main())
