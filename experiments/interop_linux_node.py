#!/usr/bin/env python3
"""Real Linux product endpoints on an owned Xvfb. No user session is touched."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import select
import signal
import socket
import subprocess
import sys
import time
from collections import deque

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_app as app


def save(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def source(output):
    import tkinter as tk
    root = tk.Tk()
    root.overrideredirect(True)
    root.geometry("1920x1080+0+0")
    canvas = tk.Canvas(root, background="#152030", highlightthickness=0)
    canvas.pack(fill="both", expand=True)
    canvas.create_rectangle(0, 0, 960, 1080, fill="white", outline="")
    canvas.create_text(70, 100, anchor="nw", text="RemoteDesk owned Linux target",
                       font=("monospace", 24), fill="#152030")
    marker = canvas.create_rectangle(100, 800, 200, 900, fill="#1677ff", outline="")
    clicks, tick = 0, 0
    text = tk.StringVar()
    def snapshot(*_):
        save(output / "target.json", {"clicks": clicks, "text": text.get(),
            "screenWidth": 1920, "screenHeight": 1080,
            "button": [350 / 1919, 400 / 1079], "editor": [350 / 1919, 620 / 1079]})
    def click():
        nonlocal clicks
        clicks += 1
        button.configure(text=f"Remote click target: {clicks}")
        snapshot()
    button = tk.Button(root, text="Remote click target: 0", command=click, font=("monospace", 24))
    button.place(x=100, y=350, width=700, height=100)
    editor = tk.Entry(root, textvariable=text, font=("monospace", 28))
    editor.place(x=100, y=580, width=750, height=80)
    text.trace_add("write", snapshot)
    def animate():
        nonlocal tick
        tick += 1
        x = 100 + tick * 9 % 700
        canvas.coords(marker, x, 800, x + 100, 900)
        root.after(33, animate)
    root.update(); root.focus_force(); snapshot(); animate()
    root.after(1800000, root.destroy)
    root.mainloop()


def start_xvfb(output):
    read_fd, write_fd = os.pipe()
    log = (output / "xvfb.log").open("w")
    # Keep the owned X server alive when a short probe closes its last client.
    # A reset would rewrite -displayfd after its parent has closed that pipe.
    child = subprocess.Popen(["Xvfb", "-noreset", "-displayfd", str(write_fd), "-screen", "0", "1920x1080x24",
                              "-nolisten", "tcp"], pass_fds=(write_fd,), stdout=log, stderr=log)
    os.close(write_fd)
    try:
        if not select.select([read_fd], [], [], 10)[0]:
            raise RuntimeError("Owned Xvfb did not start")
        display = os.read(read_fd, 80).decode().strip()
        if not display.isdigit():
            raise RuntimeError("Invalid Xvfb display")
        os.environ["DISPLAY"] = ":" + display
    except Exception:
        child.terminate(); child.wait(timeout=5)
        raise
    finally:
        os.close(read_fd); log.close()
    return child


def stop(child):
    if child.poll() is None:
        child.terminate()
        try: child.wait(timeout=6)
        except subprocess.TimeoutExpired: child.kill(); child.wait(timeout=3)


def linux_viewer_continuity(transport_active, first_frame_at, last_frame_at, now):
    """A startup burst followed by EOF/frozen display is not a live viewer."""
    age = None if last_frame_at is None else now - last_frame_at
    return dict(continuous=bool(transport_active and first_frame_at is not None
                               and last_frame_at is not None
                               and last_frame_at - first_frame_at >= 5
                               and 0 <= age <= 5),
                transportActive=bool(transport_active), lastFrameAgeSeconds=age)


def host(config, output):
    children = []
    logs = []
    relay_host = None
    try:
        children.append(start_xvfb(output))
        source_log = (output / "source.log").open("w"); logs.append(source_log)
        children.append(subprocess.Popen([sys.executable, __file__, "source", "--output", str(output)],
                                          stdout=source_log, stderr=source_log))
        for _ in range(100):
            if (output / "target.json").exists(): break
            if children[-1].poll() is not None: raise RuntimeError("Synthetic target exited; see source.log")
            time.sleep(.1)
        else: raise RuntimeError("Synthetic target failed")
        with socket.socket() as reservation:
            reservation.bind(("0.0.0.0", 0))
            port = reservation.getsockname()[1]
        log = (output / "host.log").open("w"); logs.append(log)
        fixture_arguments = []
        if config.get("featureFixtures") is True:
            returned_file = output / "return-fixture.txt"
            returned_file.write_text("RemoteDesk owned return fixture 中文😀\n", encoding="utf-8")
            fixture_arguments = ["--return-file", str(returned_file)]
        child = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/linux/remotedesk_linux_host.py"),
            "--host", "0.0.0.0", "--port", str(port), "--password-fd", "0", "--no-discovery",
            "--display", os.environ["DISPLAY"], "--width", "1920", "--height", "1080", "--fps", "30",
            "--serve-seconds", "1800", "--machine-name", "RemoteDesk-owned-interop-target",
            "--receive-dir", str(output / "receive"), *fixture_arguments], stdin=subprocess.PIPE, stdout=log, stderr=log,
            env={**os.environ, "REMOTEDESK_AUTO_INSTALL": "0"})
        children.append(child)
        child.stdin.write(config["password"].encode()); child.stdin.close()
        for _ in range(150):
            if "host listening on" in (output / "host.log").read_text(): break
            if child.poll() is not None: raise RuntimeError("Product host failed to start")
            time.sleep(.1)
        else: raise RuntimeError("Product host startup timed out")
        if "relay" in config:
            relay_host = app.relay.RelayHostConnector(app.relay.RelayOptions.from_dict(config["relay"]),
                port, "RemoteDesk-owned-Linux-relay-target", status=lambda line: print(line, flush=True))
            relay_host.start()
            if not relay_host.online.wait(40): raise RuntimeError("Native Linux relay registration failed")
        save(output / "endpoint.json", {"port": port, "display": os.environ["DISPLAY"],
            "route": "native public relay TLS/TCP" if relay_host else "direct LAN"})
        print(json.dumps({"ready": True, "port": port}), flush=True)
        deadline = time.monotonic() + 1750
        while child.poll() is None and time.monotonic() < deadline and not (output / "stop").exists():
            time.sleep(.25)
    finally:
        if relay_host: relay_host.close()
        for child in reversed(children): stop(child)
        for log in logs: log.close()


def viewer(config, output):
    import tkinter as tk
    display = start_xvfb(output)
    root = tk.Tk()
    root.geometry("1280x900+0+0")
    image_label = tk.Label(root); image_label.pack()
    events = queue.Queue()
    relay_options = app.relay.RelayOptions.from_dict(config["relay"]) if "relay" in config else None
    client = app.ViewerConnection(config["host"], config["port"], config["password"], events, 1, relay_options=relay_options)
    client.set_display_size(1280, 820)
    report = {"complete": False, "frames": 0, "changes": 0, "status": [], "inputQueued": False,
              "scope": "physical Linux product ViewerConnection and lossless Tk rendering on owned Xvfb",
              "route": "native public relay TLS/TCP (no UDP/LAN fallback)" if relay_options else "direct LAN"}
    started = time.monotonic()
    # UI status events are deliberately coalesced by the product. Preserve the
    # pre-coalescing diagnostics and wire progress separately so a subsequent
    # EOF cannot hide the heartbeat timeout which actually closed the socket.
    raw_status = deque(maxlen=512)
    wire_progress = deque(maxlen=1024)
    original_event = client._put_event
    original_read = app.read_message
    def measured_event(event, value):
        if event in ("viewer_status", "viewer_error", "viewer_disconnected", "viewer_auth_failed"):
            raw_status.append(dict(ms=(time.monotonic() - started) * 1000,
                                   event=event, detail=str(value)))
        original_event(event, value)
    def measured_read(sock, session):
        read_started = time.monotonic()
        try:
            kind, payload = original_read(sock, session)
        except Exception as error:
            wire_progress.append(dict(ms=(time.monotonic() - started) * 1000,
                                      error=type(error).__name__))
            raise
        wire_progress.append(dict(ms=(time.monotonic() - started) * 1000,
                                  readMs=(time.monotonic() - read_started) * 1000,
                                  kind=kind, payloadBytes=len(payload)))
        return kind, payload
    client._put_event = measured_event
    app.read_message = measured_read
    lags = {}
    original_fresh = app.is_h264_submission_fresh
    reserve_failures = []
    original_reserve = app.H264AnnexBDecoder._try_reserve_correlation
    def measured_reserve(decoder, correlation):
        result = original_reserve(decoder, correlation)
        if not result:
            reserve_failures.append({"backend": decoder.backend.key, "submitted": len(decoder.submitted_correlations),
                                     "ready": len(decoder.pending_jpegs)})
        return result
    app.H264AnnexBDecoder._try_reserve_correlation = measured_reserve
    def measured_fresh(decoded, latest, maximum):
        lag = str(latest - decoded)
        lags[lag] = lags.get(lag, 0) + 1
        return original_fresh(decoded, latest, maximum)
    app.is_h264_submission_fresh = measured_fresh
    previous_hash = None
    photo = None
    first_frame_at = last_frame_at = None
    def inputs():
        w, h = client.remote_width, client.remote_height
        def click(key):
            p = config[key]; x, y = round(p[0] * (w-1)), round(p[1] * (h-1))
            for kind in (app.INPUT_MOUSE_MOVE, app.INPUT_MOUSE_DOWN, app.INPUT_MOUSE_UP):
                if not client.send_input(kind, app.MOUSE_LEFT, x, y):
                    raise RuntimeError("Input queue rejected click")
        click("button")
        root.after(500, lambda: click("editor"))
        def text():
            report["textCodepointsQueued"] = client.send_text(config["text"])
        root.after(1000, text)
        report["inputQueued"] = True
    def pump():
        nonlocal previous_hash, photo, first_frame_at, last_frame_at
        try:
            for _ in range(100):
                try: event, wrapped = events.get_nowait()
                except queue.Empty: break
                if event == "viewer_closed":
                    report["status"].append([event, str(wrapped)])
                    continue
                _, value = wrapped
                if event == "viewer_frame":
                    w, h, png, decode_ms, dw, dh, backend = value
                    photo = app.create_tk_frame_photo(png, master=root)
                    image_label.configure(image=photo)
                    last_frame_at = time.monotonic()
                    if first_frame_at is None:
                        first_frame_at = last_frame_at
                    digest = hashlib.sha256(png).digest()
                    if previous_hash is not None and previous_hash != digest: report["changes"] += 1
                    previous_hash = digest
                    report.update(width=w, height=h, backend=backend, decodeMs=decode_ms)
                    report["frames"] += 1
                    if report["frames"] == 1: report["firstFrameMs"] = (time.monotonic()-started)*1000
                    if report["frames"] == 15:
                        photo.write(str(output / "rendered.png"), format="png")
                        inputs()
                elif event in ("viewer_status", "viewer_error", "viewer_disconnected"):
                    report["status"].append([event, str(value)])
            if time.monotonic() - started > (90 if relay_options else 28):
                report["renderingEvidence"] = linux_viewer_continuity(
                    not client.stop_event.is_set() and client.sock is not None,
                    first_frame_at, last_frame_at, time.monotonic())
                report["complete"] = (report["frames"] >= 30 and report["changes"] >= 5
                                      and report["inputQueued"] and report["renderingEvidence"]["continuous"])
                report["encoding"] = client.last_rendered_encoding
                root.destroy(); return
            root.after(10, pump)
        except Exception as error:
            report["failure"] = repr(error); root.destroy()
    try:
        client.start(); root.after(10, pump); root.mainloop()
    finally:
        client.close(); stop(display)
        client._put_event = original_event
        app.read_message = original_read
        report["rawStatus"] = list(raw_status)
        report["wireProgress"] = list(wire_progress)
        app.is_h264_submission_fresh = original_fresh
        app.H264AnnexBDecoder._try_reserve_correlation = original_reserve
        report["correlationLags"] = lags
        report["correlationReserveFailures"] = reserve_failures
        save(output / "viewer.json", report)
    print(json.dumps(report, ensure_ascii=False), flush=True)
    return 0 if report["complete"] else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("source", "host", "viewer"))
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=True)
    if args.mode == "source": return source(output)
    config = json.loads(sys.stdin.readline())
    return host(config, output) if args.mode == "host" else viewer(config, output)


if __name__ == "__main__":
    raise SystemExit(main())
