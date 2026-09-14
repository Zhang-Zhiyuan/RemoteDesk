#!/usr/bin/env python3
"""Screenshot the real Tk UI on an owned Xvfb with disposable settings.

No host, remote viewer connection, capture permission or OS input is started.
The displayed device names/statuses are synthetic. Images stay in local evidence.
"""
import argparse
import json
import os
from pathlib import Path
import tempfile
from unittest import mock

from PIL import ImageGrab
from interop_linux_node import app, start_xvfb, stop


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    display = start_xvfb(output)
    report = {"scope": "actual Tk UI; owned Xvfb, disposable settings, no remote input", "cases": []}
    root = None
    try:
        with tempfile.TemporaryDirectory(prefix="ui-settings-", dir=output) as settings, \
                mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": settings}):
            for scale in (1.0, 1.5, 2.0):
                root = app.tk.Tk()
                root.tk.call("tk", "scaling", scale * 96 / 72)
                ui = app.RemoteDeskLinuxApp(root)
                root.minsize(1, 1)
                for width, height in ((1180, 760), (800, 600), (640, 480)):
                    root.geometry(f"{width}x{height}+0+0")
                    for page in range(3):
                        ui.main_notebook.select(page)
                        root.update()
                        tab = ui.main_notebook.nametowidget(ui.main_notebook.select())
                        canvas = next(c for c in tab.winfo_children() if isinstance(c, app.tk.Canvas))
                        canvas.xview_moveto(0); canvas.yview_moveto(0)
                        root.update()
                        name = f"main-{page}-{width}-{scale:g}"
                        ImageGrab.grab((0, 0, width, height), xdisplay=os.environ["DISPLAY"]).save(output / (name + ".png"))
                        def overflow(parent):
                            found = []
                            for child in parent.winfo_children():
                                if child.winfo_ismapped() and child.winfo_rootx() + child.winfo_width() > canvas.winfo_rootx() + canvas.winfo_width():
                                    found.append(dict(kind=child.winfo_class(), width=child.winfo_width(), requested=child.winfo_reqwidth(),
                                        caption=str(child.cget("text")) if isinstance(child, (app.ttk.Label, app.ttk.Button, app.ttk.Checkbutton)) else ""))
                                found.extend(overflow(child))
                            return found
                        report["cases"].append(dict(name=name, width=width, scale=scale,
                            xview=canvas.xview(), yview=canvas.yview(), overflow=overflow(canvas)))
                ui._open_viewer_window("synthetic-layout.invalid", 56565)
                for width, height in ((1180, 760), (640, 480)):
                    ui.viewer_window.minsize(1, 1)
                    ui.viewer_window.geometry(f"{width}x{height}+0+0")
                    root.update()
                    ImageGrab.grab((0, 0, width, height), xdisplay=os.environ["DISPLAY"]).save(
                        output / f"viewer-{width}-{scale:g}.png")
                ui.close(); root = None
    finally:
        if root is not None:
            root.destroy()
        stop(display)
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
