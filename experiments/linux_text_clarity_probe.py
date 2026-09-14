#!/usr/bin/env python3
"""Paced synthetic decoder check against an installed Linux app; no desktop/network input."""
import argparse
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import time


def benchmark(app, backends, fixtures, output):
    """File-fed control: isolate decoding from the installed JPEG bridge."""
    fixture = (fixtures / "10mbps-static-native-gop30.h264").resolve(strict=True)
    report = []
    for backend in backends:
        for bridge in (False, True):
            command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)
            command[command.index("-i") + 1] = str(fixture)
            if bridge:
                # Keep the exact installed decode/filter/JPEG options. Send the
                # encoded images to the null device, not a user path/stdout log.
                command[-1] = "/dev/null"
            else:
                command = command[:command.index("-i") + 2] + ["-vsync", "0", "-f", "null", "-"]
            command[1:1] = ["-y", "-nostats", "-progress", "pipe:1"]
            command[-1:-1] = ["-frames:v", "180"]
            started = time.monotonic()
            completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=40)
            seconds = time.monotonic() - started
            progress = completed.stdout.decode("utf-8", errors="replace")
            counts = re.findall(r"(?m)^frame=(\d+)\s*$", progress)
            frames = int(counts[-1]) if counts else 0
            row = dict(backend=backend.key, jpegBridge=bridge, frames=frames,
                       exitCode=completed.returncode, seconds=seconds, fps=frames / seconds,
                       passed=completed.returncode == 0 and frames == 180, command=command,
                       stderr=completed.stderr.decode("utf-8", errors="replace"), progress=progress,
                       scope="File-fed throughput including startup. Exact installed backend flags; null output excludes display, image-to-Tk, paced pipe input and network. Not per-frame latency.")
            report.append(row)
            output.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps({k: v for k, v in row.items() if k not in ("command", "stderr", "progress")}), flush=True)
    return 0 if all(row["passed"] for row in report) else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", required=True)
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--wait-ms", type=int, default=120)
    parser.add_argument("--throughput", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.wait_ms <= 1500:
        raise SystemExit("--wait-ms must be 1..1500; long waits are diagnostics only")
    output = Path(args.output)
    if output.exists():
        raise SystemExit("Use a new output path; previous evidence is preserved")
    app_root = Path(args.app).resolve(strict=True)
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(app_root))
    import remotedesk_linux_app as app
    import remotedesk_linux_host as host
    from PIL import Image, ImageStat
    assert Path(app.__file__).resolve().parent == app_root
    candidates = app.probe_ffmpeg_h264_decoder_backends("/usr/bin/ffmpeg")
    backends = [item for item in candidates if item.key == "jetson-nvv4l2"][:1]
    backends += [item for item in candidates if not item.hardware][:1]
    if not backends:
        raise SystemExit("No tested product decoder backend available")
    if args.throughput:
        return benchmark(app, backends, Path(args.fixtures), output)
    report = []
    for fixture in ("10mbps-static-native-gop1.h264", "10mbps-static-native-gop30.h264"):
        encoded = (Path(args.fixtures) / fixture).read_bytes()
        units = host.extract_h264_access_units(bytearray(encoded), flush=True)
        assert len(units) == 180, "Incomplete fixture"
        for backend in backends:
            decoder = app.H264AnnexBDecoder.try_create(backend, "/usr/bin/ffmpeg")
            if decoder is None:
                report.append(dict(fixture=fixture, backend=backend.key, created=False))
                continue
            timestamps = {}
            rows = []
            started = time.monotonic()
            try:
                for index, unit in enumerate(units[:48]):
                    if time.monotonic() - started > 25:
                        break
                    if index:
                        time.sleep(0.5 if 24 <= index < 30 else 1 / 30)
                    submitted = time.monotonic()
                    timestamps[index] = submitted
                    result = decoder.decode_correlated(unit, index, timeout_seconds=args.wait_ms / 1000)
                    ready = time.monotonic()
                    row = dict(input=index, quiet=24 <= index < 30, callMs=(ready - submitted) * 1000,
                               output=result.correlation if result else None)
                    if result is not None:
                        row["fifoAgeMs"] = (ready - timestamps[result.correlation]) * 1000
                        with Image.open(io.BytesIO(result.jpeg)) as picture:
                            picture.load()
                            row["size"] = list(picture.size)
                            row["nonFlat"] = max(ImageStat.Stat(picture.convert("RGB")).stddev) > 5
                    rows.append(row)
                    if not decoder.is_running or decoder.correlation_overflowed:
                        break
                output_rows = [item for item in rows if item["output"] is not None]
                bounded_progress = (
                    len(rows) == 48
                    and not decoder.correlation_overflowed
                    and decoder.is_running
                    and 48 - len(output_rows) <= app.h264_correlated_submission_lag(backend)
                    and [item["output"] for item in output_rows] == list(range(len(output_rows)))
                    and all(item["size"] == [3840, 2160] and item["nonFlat"] for item in output_rows)
                )
                row = dict(fixture=fixture, backend=backend.key, hardware=backend.hardware, created=True,
                           waitMs=args.wait_ms,
                           attempts=len(rows), outputs=sum(item["output"] is not None for item in rows),
                           expectedInputs=48, boundedProgress=bounded_progress,
                           outstanding=decoder.outstanding_correlation_count,
                           overflow=decoder.correlation_overflowed, running=decoder.is_running,
                           error=decoder.failure_detail, rows=rows,
                           scope="Real installed decoder; synthetic 4K input, motion/2Hz/motion pacing. FIFO correlation is not an embedded picture timestamp. JPEG bridge, not native GPU presentation or a remote session.")
                report.append(row)
                print(json.dumps({k: v for k, v in row.items() if k != "rows"}), flush=True)
            finally:
                decoder.close()
            output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if all(item.get("created") and item.get("boundedProgress") for item in report) else 1


if __name__ == "__main__":
    raise SystemExit(main())
