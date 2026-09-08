#!/usr/bin/env python3
"""Isolated Linux runtime / synthetic H.264 fixture diagnostics; no real desktop."""
import argparse
import io
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts" / "linux"))
import remotedesk_linux_app as app
import remotedesk_protocol_probe as wire
from PIL import Image, ImageStat


def receive_session(port, password, codecs, wanted, max_frames, seconds, output):
    frames, arrivals, controls = [], [], []
    ping = b""  # RemoteDesk heartbeat payloads are empty, not timestamp messages.
    pong = False
    started = time.monotonic()
    with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
        client.settimeout(3)
        session = wire.authenticate(client, password)
        wire.write_message(client, session, wire.MESSAGE_CONTROL, wire.encode_viewer_info(codecs))
        wire.write_message(client, session, wire.MESSAGE_CONTROL, wire.encode_viewer_capabilities(
            wire.BASE_VIEWER_CAPABILITIES | wire.CAPABILITY_HIGH_FRAME_RATE_H264))
        wire.write_message(client, session, wire.MESSAGE_PING, ping)
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            kind, payload = wire.read_message(client, session)
            if kind == wire.MESSAGE_PING:
                wire.write_message(client, session, wire.MESSAGE_PONG, payload)
            elif kind == wire.MESSAGE_PONG:
                pong |= payload == ping
            elif kind == wire.MESSAGE_CONTROL:
                controls.append(wire.decode_control(payload))
            elif kind in (wire.MESSAGE_FRAME, wire.MESSAGE_VIDEO_FRAME):
                info = wire.decode_frame(payload, kind)
                if info["encoding"] == wanted:
                    encoded = payload[24 if kind == wire.MESSAGE_FRAME else 32:]
                    frames.append((info, encoded))
                    arrivals.append(time.monotonic())
                    if len(frames) >= max_frames and pong:
                        break
    if not frames:
        raise RuntimeError("No " + wanted + " frames; metadata=" + repr(controls))
    if len(frames) != max_frames or not pong:
        raise RuntimeError(f"Incomplete session: {len(frames)}/{max_frames} frames, pong={pong}")
    info = frames[-1][0]
    if info["width"] != 1920 or info["height"] != 1080:
        raise RuntimeError("Capture geometry changed: " + repr(info))
    elapsed = arrivals[-1] - arrivals[0] if len(arrivals) > 1 else 0
    row = {"encoding": wanted, "authenticated": True, "frames": len(frames), "pong": pong,
        "width": info["width"], "height": info["height"],
        "firstFrameMs": (arrivals[0] - started) * 1000,
        "arrivalFps": (len(frames) - 1) / elapsed if elapsed else 0,
        "elapsedSeconds": time.monotonic() - started, "controlMessages": len(controls)}
    if wanted == "Jpeg":
        image = Image.open(io.BytesIO(frames[-1][1]))
        image.load()
        row["decodedSize"] = list(image.size)
        row["pixelStdDev"] = ImageStat.Stat(image.convert("RGB")).stddev
        if max(row["pixelStdDev"]) < 5:
            raise RuntimeError("Synthetic JPEG desktop unexpectedly flat/black")
    else:
        row["allIndependent"] = all(item[0].get("flags") == 3 for item in frames)
        if not row["allIndependent"]:
            raise RuntimeError("GOP1 session emitted a dependent frame")
        (output / "linux-captured.h264").write_bytes(b"".join(item[1] for item in frames))
    return row, frames


def decode_product_frames(frames, legacy_nobuffer=False, backends=None):
    candidates = app.probe_ffmpeg_h264_decoder_backends("/usr/bin/ffmpeg")
    selected = [item for item in candidates if item.key == "jetson-nvv4l2"][:1]
    if not selected:
        selected = [item for item in candidates if item.hwaccel == "cuda"][:1]
    selected += [item for item in candidates if not item.hardware][:1]
    if backends is not None:
        selected = list(backends)
    rows = []
    for backend in selected:
        if legacy_nobuffer:
            # Negative control only: reintroduce the former startup-dropping flag
            # in an isolated process, never in the product command builder.
            command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)
            command[command.index("-fflags") + 1] = "nobuffer+discardcorrupt"
            decoder = app.H264AnnexBDecoder(subprocess.Popen(command, stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0), backend)
        else:
            decoder = app.H264AnnexBDecoder.try_create(backend, "/usr/bin/ffmpeg")
        if decoder is None:
            rows.append({"backend": backend.key, "created": False})
            continue
        ready, valid, last_size, correlations = 0, 0, None, []
        attempts = 0
        started = time.monotonic()
        try:
            for index, (_, encoded) in enumerate(frames[:60]):
                attempts += 1
                result = decoder.decode_correlated(encoded, index, timeout_seconds=0.12)
                if result is not None:
                    ready += 1
                    correlations.append(result.correlation)
                    with Image.open(io.BytesIO(result.jpeg)) as image:
                        image.load()
                        last_size = list(image.size)
                        valid += int(max(ImageStat.Stat(image.convert("RGB")).stddev) > 5)
                if not decoder.is_running or decoder.correlation_overflowed:
                    break
            rows.append({"backend": backend.key, "hardware": backend.hardware,
                "legacyNobuffer": legacy_nobuffer,
                "inputAttemptCount": attempts,
                "outstandingCorrelations": decoder.outstanding_correlation_count,
                "blockedByCorrelationLimit": decoder.correlation_overflowed,
                "processRunningAtEnd": decoder.is_running,
                "ready": ready, "nonBlack": valid, "lastSize": last_size,
                "outputCorrelations": correlations,
                "seconds": time.monotonic() - started, "failureDetail": decoder.failure_detail,
                "passed": ready >= 55 and valid == ready and last_size == [1920, 1080]
                    and not decoder.correlation_overflowed and decoder.is_running,
                "nativeSurface": False, "pipeline": "H.264 -> FFmpeg -> MJPEG -> Pillow validation"})
        finally:
            decoder.close()
    return rows


def compare_ffmpeg_startup(units):
    """Bounded EOF-fed diagnostic, NOT the product queue or a live latency test."""
    candidates = app.probe_ffmpeg_h264_decoder_backends("/usr/bin/ffmpeg")
    selected = [item for item in candidates if item.hwaccel == "cuda"][:1]
    selected += [item for item in candidates if not item.hardware][:1]
    encoded = b"".join(app.terminate_h264_access_unit(unit) for unit in units[:60])
    rows = []
    for backend in selected:
        for legacy_nobuffer in (False, True):
            command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)
            if legacy_nobuffer:
                flags_index = command.index("-fflags") + 1
                command[flags_index] = "nobuffer+discardcorrupt"
            started = time.monotonic()
            completed = subprocess.run(command, input=encoded, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, timeout=12, check=False)
            decoded = app.extract_decoder_jpeg_frames(bytearray(completed.stdout))
            rows.append({"backend": backend.key, "legacyNobuffer": legacy_nobuffer,
                "inputFrames": 60, "outputJpegs": len(decoded), "exitCode": completed.returncode,
                "seconds": time.monotonic() - started,
                "stderrTail": completed.stderr.decode("utf-8", errors="replace")[-4096:],
                "scope": "Product FFmpeg command, bounded full input + EOF; bypasses product correlation queue"})
    return rows


def stop_owned(process):
    if process.poll() is not None:
        return
    process.send_signal(signal.SIGINT)
    try:
        process.wait(timeout=6)
    except subprocess.TimeoutExpired:
        # This exact Popen owns a new process session, including its codec children.
        if os.getpgid(process.pid) == process.pid:
            os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--decode-fixtures", help="Only exercise the product decoder with synthetic VideoQualityProbe files")
    args = parser.parse_args()
    if args.decode_fixtures:
        import remotedesk_linux_host as host_module
        output = Path(args.output).resolve()
        output.mkdir(parents=True, exist_ok=False)
        rows = []
        for name in ("static-current-gop1.h264", "static-research-gop30.h264"):
            units = host_module.extract_h264_access_units(
                bytearray((Path(args.decode_fixtures) / name).read_bytes()), flush=True)
            if len(units) != 180:
                raise RuntimeError("Expected 180 fixture AUs: " + name)
            decoders = decode_product_frames([({}, unit) for unit in units])
            rows.append({"fixture": name, "availableInputFrames": 60, "decoders": decoders,
                "legacyBoundedQueue": decode_product_frames([({}, unit) for unit in units], True),
                "ffmpegStartupComparison": compare_ffmpeg_startup(units)})
        (output / "result.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
        for row in rows:
            for decoder in row["decoders"]:
                print(json.dumps({"fixture": row["fixture"], **{
                    k: v for k, v in decoder.items() if k != "outputCorrelations"}}), flush=True)
            print(json.dumps({"fixture": row["fixture"],
                "ffmpegStartupComparison": row["ffmpegStartupComparison"]}), flush=True)
            for decoder in row["legacyBoundedQueue"]:
                print(json.dumps({"fixture": row["fixture"], **{
                    k: v for k, v in decoder.items() if k != "outputCorrelations"}}), flush=True)
        # Do not hide a product failure because an experimental command succeeds.
        return 0 if all(row["decoders"] and all(d.get("passed", False)
            for d in row["decoders"]) for row in rows) else 1
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise SystemExit("Use xvfb-run and REMOTEDESK_ISOLATED_XVFB=1; never run against the user's desktop.")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"environment": "Ubuntu WSL / isolated Xvfb, not physical Xorg/Wayland",
        "display": os.environ["DISPLAY"], "sessions": [], "complete": False}
    children = []
    files = []
    password = secrets.token_urlsafe(24)  # pipe only; never arguments, environment or logs
    try:
        motion_log = (output / "motion.log").open("wb")
        files.append(motion_log)
        motion = subprocess.Popen(["ffplay", "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-an", "-noborder",
            "-left", "0", "-top", "0", "-x", "1920", "-y", "1080",
            "-window_title", "RemoteDesk isolated runtime probe", "-t", "80", "-autoexit"],
            stdout=motion_log, stderr=subprocess.STDOUT, start_new_session=True)
        children.append(motion)
        time.sleep(1)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        host_log = (output / "host.log").open("w", encoding="utf-8")
        files.append(host_log)
        host = subprocess.Popen([sys.executable, "-u", str(ROOT / "scripts/linux/remotedesk_linux_host.py"),
            "--host", "127.0.0.1", "--port", str(port), "--password-fd", "0", "--no-discovery",
            "--width", "1920", "--height", "1080", "--fps", "60", "--serve-seconds", "65",
            "--machine-name", "RemoteDesk-isolated-probe", "--receive-dir", str(output / "receive")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, start_new_session=True, env={**os.environ, "REMOTEDESK_AUTO_INSTALL": "0"})
        children.append(host)
        host.stdin.write(password)
        host.stdin.close()
        listening = threading.Event()
        def collect_logs():
            for line in host.stdout:
                host_log.write(line)
                host_log.flush()
                if "host listening on" in line:
                    listening.set()
        logger = threading.Thread(target=collect_logs, daemon=True)
        logger.start()
        if not listening.wait(12):
            raise RuntimeError("Linux host did not start; inspect host.log")
        # Invalid authentication must not displace or corrupt the next valid session.
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
                client.settimeout(3)
                wire.authenticate(client, password + "-invalid")
            raise RuntimeError("Invalid password was accepted")
        except PermissionError:
            report["badPasswordRejected"] = True
        for label, codecs, wanted, count, seconds in [
            ("jpeg", wire.VIDEO_CODEC_JPEG, "Jpeg", 45, 6),
            ("h264", wire.VIDEO_CODEC_JPEG | wire.VIDEO_CODEC_H264_ANNEX_B, "H264AnnexB", 180, 8),
            ("h264-reconnect", wire.VIDEO_CODEC_JPEG | wire.VIDEO_CODEC_H264_ANNEX_B, "H264AnnexB", 60, 6),
        ]:
            row, frames = receive_session(port, password, codecs, wanted, count, seconds, output)
            row["label"] = label
            report["sessions"].append(row)
            print(json.dumps(row), flush=True)
            if label == "h264":
                report["decoders"] = decode_product_frames(frames)
                if not report["decoders"] or not all(d.get("passed", False) for d in report["decoders"]):
                    raise RuntimeError("Product decoder failed the bounded synthetic pixel check")
            time.sleep(0.3)
        report["complete"] = True
    except Exception as failure:
        report["failure"] = str(failure)
    finally:
        for child in reversed(children):
            stop_owned(child)
        if "logger" in locals():
            logger.join(timeout=2)
        for file in files:
            file.close()
        report["ownedProcessesStopped"] = all(child.poll() is not None for child in children)
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
