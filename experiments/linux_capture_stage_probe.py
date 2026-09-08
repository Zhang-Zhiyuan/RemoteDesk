#!/usr/bin/env python3
"""Bounded synthetic Xvfb capture-stage timing; never uses a real desktop."""
import argparse
import io
import json
import os
from pathlib import Path
import re
import resource
import subprocess
import sys
import time
from PIL import Image, ImageStat

from linux_runtime_probe import ROOT, stop_owned
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_host as host


def measure(name, command, output, encoded):
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    started = time.monotonic()
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        check=False, timeout=15)
    elapsed = time.monotonic() - started
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    log = completed.stderr.decode("utf-8", errors="replace")
    (output / (name + ".log")).write_text(log, encoding="utf-8")
    frame_counts = re.findall(r"(?m)^frame=(\d+)\s*$", log)
    times = re.findall(r"(?m)^out_time_us=(\d+)\s*$", log)
    frames = int(frame_counts[-1]) if frame_counts else 0
    content_seconds = int(times[-1]) / 1_000_000 if times else 0
    row = {"name": name, "exitCode": completed.returncode, "frames": frames,
        "wallSeconds": elapsed, "contentSeconds": content_seconds,
        "wallFps": frames / elapsed,
        "cadenceFps": frames / content_seconds if content_seconds else 0,
        "cpuSeconds": after.ru_utime + after.ru_stime - before.ru_utime - before.ru_stime,
        "outputBytes": len(completed.stdout), "command": command}
    if encoded:
        units = host.extract_h264_access_units(bytearray(completed.stdout), flush=True)
        row["accessUnits"] = len(units)
        if len(units) != frames:
            raise RuntimeError("Progress / access unit count mismatch: " + name)
        sps = pps = None
        recoveries = 0
        for unit in units:
            _normalized, sps, pps, flags = host.normalize_h264_access_unit(unit, sps, pps)
            recoveries += int(flags == (host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG))
        row["recoverableUnits"] = recoveries
        if recoveries != frames:
            raise RuntimeError("Non-recoverable GOP1 access unit: " + name)
        decoded = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error",
            "-hwaccel", "cuda", "-c:v", "h264_cuvid", "-f", "h264", "-i", "pipe:0",
            "-frames:v", "1", "-f", "image2pipe", "-c:v", "mjpeg", "pipe:1"],
            input=units[0], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=6, check=True)
        with Image.open(io.BytesIO(decoded.stdout)) as image:
            image.load()
            row["decodedSize"] = list(image.size)
            row["decodedPixelStddev"] = ImageStat.Stat(image.convert("RGB")).stddev
        if row["decodedSize"] != [3840, 2160] or max(row["decodedPixelStddev"]) < 5:
            raise RuntimeError("4K hardware decode pixel check failed: " + name)
    if completed.returncode or not frames:
        raise RuntimeError("Stage failed: " + name + "; inspect log")
    print(json.dumps(row), flush=True)
    return row


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--focus-upload", action="store_true",
        help="Compare CUDA upload with NVENC's internal NV12 upload; same quality settings")
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise SystemExit("Run under an owned xvfb-run display, never the user's desktop")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"environment": "WSL / isolated Xvfb 3840x2160", "stages": []}
    with (output / "motion.log").open("wb") as motion_log:
        motion = subprocess.Popen(["ffplay", "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=60", "-an", "-noborder",
            "-left", "0", "-top", "0", "-x", "3840", "-y", "2160",
            "-window_title", "RemoteDesk isolated stage probe", "-t", "90", "-autoexit"],
            stdout=motion_log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            time.sleep(1)
            common = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostats",
                "-f", "x11grab", "-video_size", "3840x2160", "-framerate", "60",
                "-thread_queue_size", "1", "-i", os.environ["DISPLAY"],
                "-t", "5", "-progress", "pipe:2", "-an", "-fps_mode", "passthrough"]
            stages = [("capture-only", common + ["-f", "null", "-"], False)]
            for threads in (() if args.focus_upload else (0, 1, 2, 4)):
                stages.append((f"nv12-filter-{threads}", common + ["-filter_threads", str(threads),
                    "-vf", "format=nv12", "-f", "null", "-"], False))
            product = list(host.build_h264_hardware_encoder_commands("ffmpeg",
                os.environ["DISPLAY"], 3840, 2160, 3840, 2160, 60, {"h264_nvenc"})[0].command)
            # Bound output duration only; retain actual P4/VBR/GOP1 product flags.
            for threads in (() if args.focus_upload else (0, 1, 2, 4)):
                command = product[:-1] + ["-filter_threads", str(threads),
                    "-t", "5", "-progress", "pipe:2", product[-1]]
                stages.append((f"product-nvenc-filter-{threads}", command, True))
            if args.focus_upload:
                stages.append(("nv12-only", common + ["-vf", "format=nv12", "-f", "null", "-"], False))
                stages.append(("cuda-upload-only", common + ["-vf", "format=nv12,hwupload_cuda",
                    "-c:v", "wrapped_avframe", "-f", "null", "-"], False))
                for direct_upload in (False, True, False, True):
                    command = product[:-1] + ["-t", "5", "-progress", "pipe:2", product[-1]]
                    command[command.index("-vf") + 1] = (
                        "format=nv12" if direct_upload else "format=nv12,hwupload_cuda")
                    name = ("nvenc-internal-upload" if direct_upload else "nvenc-filter-upload") + f"-{len(stages)}"
                    stages.append((name, command, True))
            for name, command, encoded in stages:
                if motion.poll() is not None:
                    raise RuntimeError("Synthetic source exited before stage " + name)
                report["stages"].append(measure(name, command, output, encoded))
            report["complete"] = True
        except Exception as failure:
            report["failure"] = str(failure)
        finally:
            stop_owned(motion)
            report["ownedSourceStopped"] = motion.poll() is not None
            (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if report.get("complete") else 1


if __name__ == "__main__":
    raise SystemExit(main())
