#!/usr/bin/env python3
"""Compare exact-pixel, local-only PNG/PPM handoff on the Linux machine.

Optional Tk timing includes PhotoImage creation and GUI idle work, not physical
scanout or network latency. Run --tk only on an owned test display.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import statistics
import time
from unittest import mock
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
from PIL import Image, ImageDraw

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--tk", action="store_true")
    args = parser.parse_args()
    image = Image.new("RGB", (1920, 1080), "white")
    draw = ImageDraw.Draw(image)
    for y in range(30, 1080, 27):
        for x in range(20, 1920, 300):
            draw.text((x, y), f"RemoteDesk text {x}:{y} 1234567890", fill=(21, 32, 48))
    draw.rectangle((1000, 700, 1900, 1000), fill=(21, 32, 48))
    encoded = io.BytesIO(); image.save(encoded, format="JPEG", quality=90)
    original_save = Image.Image.save
    root = app.tk.Tk() if args.tk else None
    label = app.tk.Label(root) if root is not None else None
    if root is not None:
        root.title("RemoteDesk owned lossless handoff benchmark")
        label.pack()
        root.update_idletasks()
    result = {"scope": "synthetic 1920x1080 JPEG90; local decode/resize/display only",
              "tkMeasured": args.tk, "samplesPerMode": 30, "viewports": {}}
    try:
        for viewport in ((1280, 820), (1920, 1080)):
            modes = {}
            for mode, level in (("png6", 6), ("png1", 1), ("ppm", None)):
                timings, uploads, totals, cpu_times = [], [], [], []
                last = None

                def save(frame, fp, format=None, **params):
                    if format == "PNG": params["compress_level"] = level
                    return original_save(frame, fp, format=format, **params)

                with mock.patch.object(Image.Image, "save", save):
                    for index in range(32):
                        started = time.perf_counter()
                        cpu_started = time.process_time()
                        last = app.convert_frame_with_pillow(encoded.getvalue(), *viewport,
                            output_format="PPM" if mode == "ppm" else "PNG")
                        if last is None:
                            raise RuntimeError("Product converter returned no image")
                        converted = time.perf_counter()
                        if root is not None:
                            photo = app.create_tk_frame_photo(last, master=root)
                            label.configure(image=photo)
                            root.update_idletasks()
                        finished = time.perf_counter()
                        if index >= 2:
                            timings.append((converted - started) * 1000)
                            uploads.append((finished - converted) * 1000)
                            totals.append((finished - started) * 1000)
                            cpu_times.append((time.process_time() - cpu_started) * 1000)
                with Image.open(io.BytesIO(last)) as pixels:
                    digest = hashlib.sha256(pixels.convert("RGB").tobytes()).hexdigest()
                modes[mode] = {"conversionMedianMs": statistics.median(timings),
                    "tkUploadMedianMs": statistics.median(uploads) if root is not None else None,
                    "handoffMedianMs": statistics.median(totals), "handoffMaxMs": max(totals),
                    "cpuMedianMs": statistics.median(cpu_times),
                    "bytes": len(last), "pixelSha256": digest}
            modes["pixelsIdentical"] = len({modes[mode]["pixelSha256"]
                for mode in ("png6", "png1", "ppm")}) == 1
            result["viewports"]["x".join(map(str, viewport))] = modes
    finally:
        if root is not None:
            root.destroy()
    result["pixelsIdentical"] = all(modes["pixelsIdentical"]
        for modes in result["viewports"].values())
    Path(args.output).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result), flush=True)
    return 0 if result["pixelsIdentical"] else 1

if __name__ == "__main__": raise SystemExit(main())
