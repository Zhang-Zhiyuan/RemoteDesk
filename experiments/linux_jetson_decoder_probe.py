#!/usr/bin/env python3
"""Bounded real-machine test of the product's discovered Jetson decoder."""
import argparse
import json
from pathlib import Path

from linux_runtime_probe import app, decode_product_frames
import remotedesk_linux_host as host


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    backend = next((item for item in app.probe_ffmpeg_h264_decoder_backends("/usr/bin/ffmpeg")
                    if item.key == "jetson-nvv4l2"), None)
    if backend is None:
        raise SystemExit("Product did not discover a Jetson NVIDIA V4L2 decoder")
    rows = []
    for name in ("static-current-gop1.h264", "static-research-gop30.h264"):
        units = host.extract_h264_access_units(bytearray((Path(args.fixtures) / name).read_bytes()), flush=True)
        result = decode_product_frames([({}, unit) for unit in units], backends=[backend])
        rows.append({"fixture": name, "scope": "actual product backend selection and bounded decoder", "result": result})
        print(json.dumps(rows[-1]), flush=True)
    (output / "result.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    return 0 if all(row["result"] and all(item.get("passed") for item in row["result"]) for row in rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
