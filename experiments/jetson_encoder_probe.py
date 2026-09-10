#!/usr/bin/env python3
"""Bounded native Jetson capture/quality probe on an owned Xvfb, never the user desktop."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import select
import subprocess
import time

import interop_linux_node as node
import remotedesk_linux_host as host


def run(command):
    return subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, timeout=15, check=True).stdout


def main():
    import tkinter as tk
    from PIL import Image, ImageChops, ImageStat

    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--frames", type=int, choices=(60, 180), default=60)
    parser.add_argument("--native-only", action="store_true")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    display = node.start_xvfb(output)
    root = None
    try:
        root = tk.Tk()
        root.overrideredirect(True)
        root.geometry("1920x1080+0+0")
        canvas = tk.Canvas(root, background="white", highlightthickness=0)
        canvas.pack(fill="both", expand=True)
        canvas.create_rectangle(960, 0, 1920, 1080, fill="#152030", outline="")
        for n, size in enumerate((10, 12, 16, 24, 36)):
            label = "RemoteDesk 1080P text: AaBb 0123456789 中文测试"
            canvas.create_text(50, 60 + n * 130, anchor="nw", text=label, font=("monospace", size), fill="black")
            canvas.create_text(990, 60 + n * 130, anchor="nw", text=label, font=("monospace", size), fill="white")
        for x, color in enumerate(("red", "green", "blue", "cyan", "magenta", "yellow")):
            canvas.create_rectangle(40 + x * 150, 820, 170 + x * 150, 970, fill=color, outline="")
        root.update()
        # Explicitly scoped to our just-created Xvfb; no real desktop input.
        run(["xdotool", "mousemove", "1919", "1079"])
        run(["ffmpeg", "-v", "error", "-f", "x11grab", "-video_size", "1920x1080", "-i", os.environ["DISPLAY"],
             "-frames:v", "1", str(output / "source.png")])
        launch = host.find_jetson_gstreamer_encoder()
        if not launch:
            raise RuntimeError("Native Jetson plugins unavailable")
        results = []
        cases = (("native", 1920, 1080), ("scaled", 1280, 720), ("padded", 1024, 768))
        for name, width, height in cases[:1] if args.native_only else cases:
            candidate = host.build_jetson_h264_encoder_command(launch, os.environ["DISPLAY"], 1920, 1080, width, height, 30)
            log = (output / (name + ".log")).open("wb")
            encoder = subprocess.Popen(candidate.command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=log, bufsize=0)
            frames = []
            first_ms = None
            buffer = bytearray()
            sps = pps = None
            started = time.monotonic()
            try:
                while len(frames) < args.frames and time.monotonic() - started < max(8, args.frames / 30 + 4):
                    if not select.select([encoder.stdout], [], [], .3)[0]:
                        continue
                    chunk = os.read(encoder.stdout.fileno(), 65536)
                    if not chunk:
                        break
                    buffer.extend(chunk)
                    for au in host.extract_h264_access_units(buffer):
                        encoded, sps, pps, flags = host.normalize_h264_access_unit(au, sps, pps)
                        if flags != host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG:
                            raise RuntimeError("Non-independent output frame")
                        frames.append(encoded)
                        if first_ms is None:
                            first_ms = (time.monotonic() - started) * 1000
                        if len(frames) == args.frames:
                            break
                elapsed = time.monotonic() - started
            finally:
                node.stop(encoder)
                encoder.stdout.close()
                log.close()
            if len(frames) != args.frames:
                raise RuntimeError("Native encoder failed continuous output: " + name)
            stream = output / (name + ".h264")
            stream.write_bytes(b"".join(frames))
            metadata = json.loads(run(["ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", str(stream)]))["streams"][0]
            if (metadata["width"], metadata["height"]) != (width, height) or int(metadata["nb_read_frames"]) != args.frames:
                raise RuntimeError("Bitstream geometry/frame count mismatch")
            if metadata.get("color_space") != "bt709":
                raise RuntimeError("Coded stream lost its explicit BT.709 matrix")
            decoded_path = output / (name + ".png")
            run(["ffmpeg", "-v", "error", "-i", str(stream), "-frames:v", "1", str(decoded_path)])
            result = dict(case=name, width=width, height=height, frames=len(frames), firstMs=first_ms,
                          fpsAfterFirst=(len(frames)-1)/(elapsed-first_ms/1000),
                          meanFrameBytes=sum(map(len, frames))/len(frames), profile=metadata["profile"],
                          pixelFormat=metadata["pix_fmt"], colorSpace=metadata["color_space"], independentFrames=True)
            with Image.open(decoded_path) as decoded:
                if name == "native":
                    with Image.open(output / "source.png") as source:
                        # Luma metric includes text; does not claim 4:2:0 H.264 is lossless or equivalent to JPEG.
                        diff = ImageChops.difference(source.convert("L"), decoded.convert("L"))
                        mse = ImageStat.Stat(diff).sum2[0] / (width * height)
                        result["lumaPsnrDb"] = 10 * math.log10(255**2 / mse) if mse else None
                        points = [(100 + 150 * n, 900) for n in range(6)] + [(800, 750), (1500, 750)]
                        reference, actual = source.convert("RGB"), decoded.convert("RGB")
                        result["solidColorMaxError"] = max(abs(a-b) for xy in points
                            for a, b in zip(reference.getpixel(xy), actual.getpixel(xy)))
                        if result["solidColorMaxError"] > 8:
                            raise RuntimeError("Solid colors differ beyond bounded quantization error")
                if name == "padded":
                    pixels = decoded.convert("RGB")
                    for point in ((width//2, 10), (width//2, height-10)):
                        if max(pixels.getpixel(point)) > 8:
                            raise RuntimeError("Expected aspect-preserving black borders")
                    result["letterboxVerified"] = True
            results.append(result)
        node.save(output / "result.json", dict(complete=True, results=results,
            modulePath=str(Path(host.__file__).resolve()), hostSha256=hashlib.sha256(Path(host.__file__).read_bytes()).hexdigest(),
            scope="Jetson native encoder, owned Xvfb/static chart, FFmpeg independent bitstream decode; not native user desktop"))
        print(json.dumps(results), flush=True)
    finally:
        if root is not None:
            root.destroy()
        node.stop(display)


if __name__ == "__main__":
    main()
