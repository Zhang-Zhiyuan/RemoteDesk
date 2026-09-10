#!/usr/bin/env python3
"""Compare old/new Linux RGB handoff; synthetic pixels, no capture or OS input.

Timings cover decode/resize/PPM serialization only, not network or scanout.
The baseline repeats the previous unconditional RGB conversion with the same
dimension validation, LANCZOS filter and local PPM format as the product.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import statistics
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
from PIL import Image, ImageDraw


def previous_converter(encoded, source_size, viewport):
    if app.inspect_encoded_image_dimensions(encoded) != source_size:
        raise ValueError("Unexpected source dimensions")
    with Image.open(io.BytesIO(encoded)) as source:
        if source.size != source_size:
            raise ValueError("Decoded dimensions changed")
        image = source.convert("RGB")
        fitted = app.calculate_fitted_image_size(*source_size, *viewport)
        if image.size != fitted:
            image = image.resize(fitted, Image.Resampling.LANCZOS)
        output = io.BytesIO()
        image.save(output, format="PPM")
        return output.getvalue()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--samples", type=int, default=24)
    args = parser.parse_args()
    if not 4 <= args.samples <= 100:
        parser.error("samples must be between 4 and 100")
    results = {"scope": "synthetic Linux decode, LANCZOS resize and PPM serialization; not end-to-end latency",
               "samplesPerMode": args.samples, "cases": []}
    for source_size, viewport in (((1920, 1080), (1920, 1080)),
                                  ((2560, 1440), (1920, 1080)),
                                  ((3840, 2160), (1920, 1080)),
                                  ((3840, 2160), (3840, 2160)),
                                  ((734, 1600), (900, 600))):
        source = Image.new("RGB", source_size, "white")
        draw = ImageDraw.Draw(source)
        for y in range(8, source.height, 24):
            for x in range(8, source.width, 250):
                draw.text((x, y), "RemoteDesk text RGB 0123456789", fill=(25, 35, 65))
        encoded = io.BytesIO()
        source.save(encoded, format="JPEG", quality=90)
        source.close()
        payload = encoded.getvalue()
        times = {"before": [], "after": []}
        outputs = {}
        for index in range(args.samples + 3):
            # Alternate order to reduce warm-cache / CPU frequency bias.
            for mode in (("before", "after") if index % 2 else ("after", "before")):
                started = time.perf_counter()
                value = (previous_converter(payload, source_size, viewport) if mode == "before"
                         else app.convert_frame_for_tk(payload, *viewport, *source_size))
                elapsed = (time.perf_counter() - started) * 1000
                if value is None:
                    raise RuntimeError("Product converter returned no image")
                outputs[mode] = value
                if index >= 3:
                    times[mode].append(elapsed)
        if outputs["before"] != outputs["after"]:
            raise AssertionError("The local PPM pixels or dimensions changed")
        before, after = (statistics.median(times[mode]) for mode in ("before", "after"))
        case = {"source": source_size, "viewport": viewport, "beforeMedianMs": before,
                "afterMedianMs": after, "reductionPercent": (before - after) / before * 100,
                "ppmBytesIdentical": True, "ppmSha256": hashlib.sha256(outputs["after"]).hexdigest()}
        results["cases"].append(case)
        print(json.dumps(case), flush=True)
    destination = Path(args.output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(results, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
