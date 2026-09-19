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
import time
from unittest import mock

from PIL import ImageGrab
from interop_linux_node import app, start_xvfb, stop


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--owned-display", action="store_true",
                        help="Use the caller's isolated xvfb-run display (requires REMOTEDESK_ISOLATED_XVFB=1)")
    parser.add_argument("--dialogs-only", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    if args.owned_display and os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1":
        parser.error("--owned-display requires REMOTEDESK_ISOLATED_XVFB=1; never use a real desktop")
    display = None if args.owned_display else start_xvfb(output)
    report = {"scope": "actual Tk UI; owned Xvfb, disposable settings, no remote input", "cases": []}
    root = None
    try:
        # WSL's mounted Windows worktree may not enforce POSIX private modes.
        # Keep the disposable encrypted profile on the native Linux filesystem.
        with tempfile.TemporaryDirectory(prefix="remotedesk-ui-settings-") as settings, \
                mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": settings}), \
                mock.patch.object(app.DevicePanel, "scan"):
            for scale in (1.0, 1.5, 2.0):
                root = app.tk.Tk()
                root.tk.call("tk", "scaling", scale * 96 / 72)
                ui = app.RemoteDeskLinuxApp(root)
                deadline = time.monotonic() + 3
                while not ui.device_panel.ready and time.monotonic() < deadline:
                    root.update()
                    time.sleep(.01)
                if not ui.device_panel.ready:
                    raise RuntimeError("Disposable Linux device profile did not load")
                root.minsize(1, 1)
                for width, height in (() if args.dialogs_only else ((1180, 760), (800, 600), (640, 480))):
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
                if not args.dialogs_only:
                    ui._open_viewer_window("synthetic-layout.invalid", 56565)
                for width, height in (() if args.dialogs_only else ((1180, 760), (640, 480))):
                    ui.viewer_window.minsize(1, 1)
                    ui.viewer_window.geometry(f"{width}x{height}+0+0")
                    root.update()
                    ImageGrab.grab((0, 0, width, height), xdisplay=os.environ["DISPLAY"]).save(
                        output / f"viewer-{width}-{scale:g}.png")
                if ui.viewer_window:
                    ui._close_viewer_window()

                def descendants(parent):
                    for child in parent.winfo_children():
                        yield child
                        yield from descendants(child)

                def inspect_dialog(kind, size=None):
                    dialog = next(c for c in root.winfo_children() if isinstance(c, app.tk.Toplevel))
                    if size:
                        dialog.minsize(1, 1)
                        dialog.geometry(f"{size[0]}x{size[1]}+0+0")
                    else:
                        dialog.geometry("+0+0")
                    root.update()
                    width, height = dialog.winfo_width(), dialog.winfo_height()
                    name = f"{kind}-{size[0] if size else 'natural'}-{scale:g}"
                    ImageGrab.grab((0, 0, min(width, root.winfo_screenwidth()), min(height, root.winfo_screenheight())),
                                   xdisplay=os.environ["DISPLAY"]).save(output / (name + ".png"))
                    controls = [dict(caption=str(c.cget("text")), mapped=bool(c.winfo_ismapped()),
                                     x=c.winfo_rootx() - dialog.winfo_rootx(), y=c.winfo_rooty() - dialog.winfo_rooty(),
                                     width=c.winfo_width(), height=c.winfo_height())
                                for c in descendants(dialog) if isinstance(c, app.ttk.Button)]
                    report["cases"].append(dict(name=name, width=width, height=height, controls=controls))
                    dialog.destroy()

                for size in ((800, 600), (480, 360)):
                    item = app.FileTransferPreviewItem("文件", "/home/source/很长的文件夹/" * 4 + "中文报告😀.txt",
                                                       "中文报告😀.txt", 1024, "/home/receiver/Downloads/RemoteDeskReceived/中文报告😀.txt")
                    root.after(100, lambda size=size: inspect_dialog("file-confirm", size))
                    ui._show_file_transfer_confirmation_dialog(root, "确认发送文件", "确认后才会开始传输。", [item],
                                                              "接收设备：fixture；重名自动改名，不覆盖已有文件。文件夹会打包为 ZIP，不会自动解压。")
                root.after(100, lambda: inspect_dialog("device-add"))
                ui.device_panel.add()
                from remotedesk_linux_devices import Device
                devices = [Device("192.0.2.1", 40000 + index, "很长的合成设备名称" * 6) for index in range(30)]
                root.after(100, lambda: inspect_dialog("device-choose"))
                ui.device_panel.choose(devices)
                ui.close(); root = None
    finally:
        if root is not None:
            root.destroy()
        if display is not None:
            stop(display)
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
