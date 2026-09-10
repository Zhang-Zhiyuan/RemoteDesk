"""Offline SPS-only compatibility experiment; never a production bitstream filter."""
import argparse
from pathlib import Path
import re
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
from remotedesk_linux_host import annex_b_nal_units


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise RuntimeError("Refusing to overwrite existing evidence")
    trace = subprocess.run(["ffmpeg", "-v", "verbose", "-i", str(args.source),
        "-frames:v", "1", "-c:v", "copy", "-bsf:v", "trace_headers", "-f", "null", "-"],
        capture_output=True, text=True, check=True, timeout=20).stderr
    offsets = set(re.findall(r"\]\s+(\d+)\s+bitstream_restriction_flag\s+1 = 1", trace))
    if len(offsets) != 1:
        raise RuntimeError("Expected a single traced SPS restriction flag offset")
    offset = int(offsets.pop())
    source = args.source.read_bytes()
    result = bytearray()
    previous_sps = None
    count = 0
    for nal_type, nal in annex_b_nal_units(source):
        if nal_type != 7:
            result.extend(nal)
            continue
        prefix = 3 if nal[2] == 1 else 4
        rbsp = nal[prefix:].replace(b"\x00\x00\x03", b"\x00\x00")
        if previous_sps is not None and rbsp != previous_sps:
            raise RuntimeError("Probe only accepts an unchanged SPS throughout the stream")
        previous_sps = rbsp
        bits = "".join(f"{value:08b}" for value in rbsp)
        if bits[offset] != "1":
            raise RuntimeError("Traced restriction flag does not match actual RBSP")
        # This flag is the last optional SPS/VUI section. Retain every earlier
        # bit (including colorimetry and timing), then write proper trailing bits.
        bits = bits[:offset] + "01"
        bits += "0" * (-len(bits) % 8)
        raw = bytes(int(bits[i:i+8], 2) for i in range(0, len(bits), 8))
        escaped = bytearray()
        zeroes = 0
        for value in raw:
            if zeroes >= 2 and value <= 3:
                escaped.append(3)
                zeroes = 0
            escaped.append(value)
            zeroes = zeroes + 1 if value == 0 else 0
        result.extend(nal[:prefix] + escaped)
        count += 1
    args.output.write_bytes(result)
    print(f"Modified {count} repeated SPS restriction flags at RBSP bit {offset}; slices untouched")


if __name__ == "__main__":
    main()
