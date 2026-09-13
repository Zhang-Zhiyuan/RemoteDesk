#!/usr/bin/env python3
"""Owned Tk/mpv spatial-scaler check; no real session or OS input.

Software decode is intentional: this tests real gpu-next property application,
pixel changes, and exact rollback, not hardware decoder qualification.
"""
import hashlib
import json
import os
import socket
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app


def main():
    from PIL import Image
    import tkinter as tk
    output = Path(sys.argv[1]).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"scope": "owned Tk window + real mpv gpu-next with software decode/rasterizer allowed; not physical Linux GPU qualification"}
    mpv = root = None
    try:
        with tempfile.TemporaryDirectory(prefix="remotedesk-upscale-") as directory:
            work = Path(directory)
            root = tk.Tk(); root.geometry("400x300+0+0")
            root.title("RemoteDesk experimental upscale test")
            video = tk.Frame(root, bg="black"); video.pack(fill="both", expand=True); root.update()
            media = work / "pattern.mkv"
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=1",
                "-frames:v", "1", "-c:v", "ffv1", str(media)], check=True, timeout=20)
            ipc = str(work / "mpv.sock")
            mpv = subprocess.Popen(["mpv", "--no-config", "--vo=gpu-next", "--gpu-api=opengl", "--gpu-context=x11egl", "--gpu-sw=yes",
                "--hwdec=no", "--pause", "--keep-open=yes", "--audio=no", "--osd-level=0", "--osc=no",
                "--msg-level=all=warn", "--scale=bilinear", "--input-ipc-server=" + ipc,
                "--wid=" + str(video.winfo_id()), str(media)], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            names = ("scale", "scale-antiring", "cscale")
            def settle():
                for _ in range(10): root.update(); time.sleep(.03)
            for _ in range(25):
                settle()
                baseline = app.query_mpv_ipc_properties(ipc, names)
                if len(baseline) == len(names): break
                if mpv.poll() is not None: raise RuntimeError(mpv.stderr.read().decode())
            else: raise RuntimeError("mpv IPC not ready")
            settle()
            def snapshot(name):
                # Capture ONLY mpv's owned render target, never another window
                # or the user's desktop, even when this window is occluded.
                path = output / name
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as sock:
                    sock.settimeout(3); sock.connect(ipc)
                    sock.sendall(json.dumps({"command": ["screenshot-to-file", str(path), "window"], "request_id": 77}).encode() + b"\n")
                    with sock.makefile("rb") as stream:
                        for _ in range(30):
                            reply = json.loads(stream.readline())
                            if reply.get("request_id") == 77:
                                assert reply.get("error") == "success", reply
                                break
                        else: raise RuntimeError("screenshot reply missing")
                with Image.open(path) as image:
                    return hashlib.sha256(image.tobytes()).hexdigest()
            original_hash = snapshot("original.png")
            # Bypass ONLY the decoder qualification gate: the owned mpv above
            # deliberately uses software decoding. Exercise the product's
            # actual transactional settings method and IPC on its GPU renderer.
            controller = SimpleNamespace(upscale_lock=threading.Lock(), state_lock=threading.Lock(),
                is_native_surface_active=True, experimental_upscaling=False, original_scale_properties=None,
                activation_failure="", ipc_socket_path=ipc)
            for cycle in range(8):
                assert app.MpvNativeH264Presenter.set_experimental_upscaling(controller, True)
                settle()
                enhanced_hash = snapshot("experimental.png")
                assert enhanced_hash != original_hash, "scaler did not change actual rendered pixels"
                assert app.MpvNativeH264Presenter.set_experimental_upscaling(controller, False)
                settle()
                assert app.query_mpv_ipc_properties(ipc, names) == baseline
                assert snapshot("restored.png") == original_hash, "rollback changed original pixels"
            report.update(passed=True, cycles=8, original=baseline, exactPixelRollback=True,
                originalHash=original_hash, experimentalHash=enhanced_hash)
    except Exception as error:
        report.update(passed=False, error=repr(error))
    finally:
        for process in (mpv,):
            if process is not None and process.poll() is None:
                process.terminate()
                try: process.wait(timeout=3)
                except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=3)
        if root is not None: root.destroy()
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report), flush=True)
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
