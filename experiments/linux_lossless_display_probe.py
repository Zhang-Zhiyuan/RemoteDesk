#!/usr/bin/env python3
"""Exercise the installed GUI's frame handoff on an owned Xvfb display.

Synthetic pixels only. No host is started, no input is injected, and settings
are isolated. This is presentation validation, not a network latency test.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-dir", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(Path(args.app_dir).resolve()))
    import remotedesk_linux_app as app
    from PIL import Image, ImageDraw

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"complete": False, "scope": __doc__.strip(), "frames": []}
    with tempfile.TemporaryDirectory(prefix="isolated-settings-", dir=output) as settings:
        os.environ["XDG_CONFIG_HOME"] = settings
        root = app.tk.Tk()
        window = app.RemoteDeskLinuxApp(root)
        try:
            source = Image.new("RGB", (1920, 1080), "white")
            drawing = ImageDraw.Draw(source)
            for y in range(0, 1080, 13):
                drawing.line((0, y, 1919, y), fill=(y % 256, (y * 11) % 256, (y * 37) % 256))
            drawing.text((200, 100), "RemoteDesk exact RGB handoff", fill="black")
            jpeg = io.BytesIO()
            source.save(jpeg, format="JPEG", quality=90)
            window._open_viewer_window("Owned display fixture", 56565)
            for geometry in ("1100x780", "760x580"):
                window.viewer_window.geometry(geometry)
                root.update()
                viewport = app.normalize_viewer_display_size(
                    window.frame_label.winfo_width(), window.frame_label.winfo_height())
                if viewport is None:
                    raise RuntimeError("Viewer did not obtain a usable viewport")
                reference = app.convert_frame_with_pillow(jpeg.getvalue(), *viewport)
                current = app.convert_frame_for_tk(jpeg.getvalue(), *viewport)
                # Same product entry point used by the GUI event pump.
                window._show_frame(1920, 1080, current, 0, *viewport)
                root.update_idletasks()
                rendered = output / (geometry + ".png")
                window.last_photo.write(str(rendered), format="png")
                with (Image.open(io.BytesIO(reference)) as previous,
                      Image.open(rendered) as actual):
                    expected_hash = hashlib.sha256(previous.convert("RGB").tobytes()).hexdigest()
                    actual_hash = hashlib.sha256(actual.convert("RGB").tobytes()).hexdigest()
                    passed = expected_hash == actual_hash and actual.size == previous.size
                    report["frames"].append(dict(geometry=geometry, viewport=viewport,
                        displayed=actual.size, pixelsIdentical=passed, pixelSha256=actual_hash))
                    if not passed:
                        raise RuntimeError("Actual GUI pixels differ from the previous display path")
            # Stale work from before a resize must not repaint the new viewport.
            last_photo = window.last_photo
            window._show_frame(1920, 1080, current, 0, viewport[0] + 10, viewport[1])
            report["staleViewportRejected"] = window.last_photo is last_photo
            report["hostNotStarted"] = window.host_process is None
            report["appSha256"] = hashlib.sha256(Path(app.__file__).read_bytes()).hexdigest()
            report["complete"] = report["staleViewportRejected"] and report["hostNotStarted"]
        except Exception as error:
            report["failure"] = f"{type(error).__name__}: {error}"
        finally:
            window.close()
    (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
