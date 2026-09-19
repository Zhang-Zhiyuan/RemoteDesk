#!/usr/bin/env python3
"""Actual Linux GUI connection/clipboard/file dialogs on an isolated Xvfb."""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace

from wsl_package_fixture import ROOT, extract_package, allow_owned_loopback_viewer


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--use-source", action="store_true")
    parser.add_argument("--input-only", action="store_true", help="Exercise owned remote pointer/text targets supplied in fixture JSON")
    parser.add_argument("--button", type=float, nargs=2)
    parser.add_argument("--editor", type=float, nargs=2)
    parser.add_argument("--text")
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1":
        parser.error("Requires a caller-owned Xvfb")
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    for key in ("button", "editor", "text"):
        if getattr(args, key) is not None:
            config[key] = getattr(args, key)
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    report = {"complete": False, "sourceMode": args.use_source, "checks": [],
              "scope": "Actual Linux GUI/ViewerConnection on owned Xvfb; remote is an owned synthetic fixture"}
    window_manager = None; ui = None; root = None
    with tempfile.TemporaryDirectory(prefix="remotedesk-viewer-audit-") as temporary:
        work = Path(temporary)
        extract_package(args.package.resolve(), work)
        os.environ.update(XDG_CONFIG_HOME=str(work / "profile"), XDG_CACHE_HOME=str(work / "cache"),
                          WAYLAND_DISPLAY="", XDG_SESSION_TYPE="x11", REMOTEDESK_AUTO_INSTALL="0")
        (work / "profile").mkdir(mode=0o700)
        sys.dont_write_bytecode = True
        sys.path.insert(0, str(ROOT / "scripts/linux" if args.use_source else work / "app"))
        app = importlib.import_module("remotedesk_linux_app")
        report["explicitOwnedLoopbackFixture"] = allow_owned_loopback_viewer(app)
        from PIL import ImageGrab
        try:
            window_manager = subprocess.Popen(["openbox", "--sm-disable"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            root = app.tk.Tk()
            root.tk.call("tk", "scaling", 192 / 72)
            ui = app.RemoteDeskLinuxApp(root)
            statuses = []
            original_status = ui._set_viewer_status
            def status(value):
                statuses.append(str(value))
                original_status(value)
            ui._set_viewer_status = status
            ui.device_panel.refreshed = time.monotonic() + 3600  # No discovery outside the fixture.
            root.geometry("1000x700+0+0")
            errors = []
            root.report_callback_exception = lambda typ, value, traceback: errors.append(typ.__name__ + ": " + str(value))
            def pump(predicate, timeout=12):
                deadline = time.monotonic() + timeout
                while time.monotonic() < deadline:
                    root.update()
                    if errors:
                        raise RuntimeError("Tk callback failure: " + errors[0])
                    if predicate():
                        return
                    time.sleep(.01)
                raise TimeoutError("Owned GUI condition did not complete")
            def capture(widget, name):
                root.update_idletasks()
                x, y = widget.winfo_rootx(), widget.winfo_rooty()
                ImageGrab.grab((x, y, x + widget.winfo_width(), y + widget.winfo_height()),
                               xdisplay=os.environ["DISPLAY"]).save(output / name)
            def descendants(widget):
                for child in widget.winfo_children():
                    yield child
                    yield from descendants(child)
            def presented():
                return (ui.frame_label is not None and (ui.last_photo is not None or ui.native_presenter_active)
                        and 1 < ui.display_width <= ui.frame_label.winfo_width()
                        and 1 < ui.display_height <= ui.frame_label.winfo_height())
            def connect():
                ui.viewer_host.set(config["host"]); ui.viewer_port.set(str(config["port"])); ui.viewer_password.set(config["password"])
                ui.connect_button.invoke()
                pump(lambda: ui.viewer is not None and ui.remote_width > 1 and presented(), 30)
                return ui.viewer
            first = connect()
            report["checks"].append({"name": "real-gui-connect", "passed": True,
                "width": ui.remote_width, "height": ui.remote_height, "frameSequence": first.frame_sequence})
            for width, height in ((1280, 800), (640, 480), (480, 360)):
                ui.viewer_window.minsize(1, 1); ui.viewer_window.geometry(f"{width}x{height}+0+0")
                first_sequence = first.frame_sequence
                pump(lambda: first.frame_sequence >= first_sequence + 3 and presented(), 30)
                capture(ui.viewer_window, f"connected-{width}x{height}-200.png")
                status = ui.viewer_window_status
                report["checks"].append({"name": f"connected-layout-{width}", "frameHeight": ui.frame_label.winfo_height(),
                    "windowHeight": ui.viewer_window.winfo_height(), "statusFullyInside": status.winfo_rooty() + status.winfo_height() <= ui.viewer_window.winfo_rooty() + ui.viewer_window.winfo_height()})
            ui.viewer_window.geometry("1000x700+0+0"); root.update()
            if args.input_only:
                before = first.frame_sequence
                pump(lambda: first.frame_sequence >= before + 3 and presented(), 30)
                for key in ("button", "editor"):
                    pump(presented, 30)
                    fx, fy = config[key]
                    label = ui.frame_label
                    x = (label.winfo_width() - ui.display_width) // 2 + round(fx * (ui.display_width - 1))
                    y = (label.winfo_height() - ui.display_height) // 2 + round(fy * (ui.display_height - 1))
                    report["checks"].append({"name": "pointer-map-" + key,
                        "labelSize": [label.winfo_width(), label.winfo_height()],
                        "displaySize": [ui.display_width, ui.display_height], "event": [x, y],
                        "remotePoint": ui._pointer_event_to_remote(SimpleNamespace(x=x, y=y))})
                    # Drive the actual owned X11 window, including WM focus and
                    # a normal press duration, not zero-time synthetic Tk events.
                    subprocess.run(["xdotool", "mousemove", "--sync", str(label.winfo_rootx() + x),
                                    str(label.winfo_rooty() + y), "mousedown", "1"], check=True, timeout=3)
                    held_until = time.monotonic() + .05
                    pump(lambda: time.monotonic() >= held_until, 2)
                    subprocess.run(["xdotool", "mouseup", "1"], check=True, timeout=3)
                    until = time.monotonic() + .7
                    pump(lambda: time.monotonic() >= until, 2)
                ui.text_input.set(config["text"])
                next(widget for widget in descendants(ui.viewer_window) if isinstance(widget, app.ttk.Button)
                     and widget.cget("text") == "发送文本").invoke()
                until = time.monotonic() + 4
                pump(lambda: time.monotonic() >= until, 6)
                capture(ui.viewer_window, "remote-after-input.png")
                report["checks"].append({"name": "actual-viewer-pointer-text-dispatch", "queued": True,
                    "expectedText": config["text"], "note": "Applied input must also be verified in the owned target snapshot"})
                report["dispatchComplete"] = True
                report["appliedInputVerified"] = False
                report["scopeStatus"] = "Dispatch completed; overall acceptance requires the independent owned target snapshot"
                return 2
            ui.root.clipboard_clear(); ui.root.clipboard_append("owned local baseline")
            ui.viewer_clipboard(read=True)
            pump(lambda: first.clipboard_latest is not None and first.clipboard_latest.completed.is_set())
            pump(lambda: ui._local_clipboard_text() == first.clipboard_latest.text)
            initial = ui._local_clipboard_text()
            if not initial:
                raise RuntimeError("Remote synthetic clipboard was empty")
            report["checks"].append({"name": "manual-remote-to-local", "passed": True, "characters": len(initial)})
            text = "WSL actual GUI 双向剪贴板 中文😀\r\nsecond line\towned"
            root.clipboard_clear(); root.clipboard_append(text); root.update()
            ui.viewer_clipboard()
            request = first.clipboard_latest
            pump(lambda: request.completed.is_set())
            if not request.success:
                raise RuntimeError("Clipboard write was not acknowledged")
            root.clipboard_clear(); root.clipboard_append("owned readback baseline")
            ui.viewer_clipboard(read=True)
            pump(lambda: ui._local_clipboard_text() == text)
            report["checks"].append({"name": "manual-local-to-remote-roundtrip", "passed": True, "sha256": hashlib.sha256(text.encode()).hexdigest()})
            local_only = "owned context-copy candidate 中文😀"
            root.clipboard_clear(); root.clipboard_append(local_only); root.update()
            settle = time.monotonic() + 2
            pump(lambda: time.monotonic() >= settle, 4)
            first.request_clipboard(read=True, baseline="owned-no-apply-sentinel")
            readback = first.clipboard_latest
            pump(lambda: readback.completed.is_set())
            report["checks"].append({"name": "clipboard-change-auto-send", "supported": readback.text == local_only,
                "localPreserved": ui._local_clipboard_text() == local_only,
                "scope": "Clipboard ownership change, no Ctrl shortcut or explicit send; not native remote context-menu execution"})

            result_details = []
            original_results = ui._show_file_transfer_results
            def results(details):
                result_details.append(details)
                original_results(details)
            ui._show_file_transfer_results = results
            source = work / "owned-upload-中文😀.txt"
            source.write_text("Owned Linux GUI upload 中文😀\n" * 4096, encoding="utf-8")
            folder = work / "owned-folder-中文"
            folder.mkdir(); (folder / "nested.txt").write_text("owned nested 中文😀", encoding="utf-8")
            def preview(paths, accept, name):
                observed = []
                started = time.monotonic()
                def act():
                    dialogs = [widget for widget in descendants(root) if isinstance(widget, app.tk.Toplevel)
                               and widget is not ui.viewer_window and widget.title().startswith("确认")]
                    if not dialogs:
                        if time.monotonic() - started < 20:
                            root.after(50, act)
                        return
                    dialog = dialogs[0]
                    texts = [widget.get("1.0", "end-1c") for widget in descendants(dialog) if isinstance(widget, app.tk.Text)]
                    observed.append({"details": texts, "confirmed": accept})
                    capture(dialog, name + "-confirmation.png")
                    next(widget for widget in descendants(dialog) if isinstance(widget, app.ttk.Button)
                         and widget.cget("text") == ("开始传输" if accept else "取消")).invoke()
                root.after(50, act)
                previous_results = len(result_details)
                if not ui._begin_viewer_file_transfer_preview([str(path) for path in paths], "确认发送文件", "确认后才会开始传输。", "正在传输合成文件…"):
                    raise RuntimeError("Could not start the real transfer preview")
                pump(lambda: bool(observed) and not ui.viewer_file_preview_active, 25)
                if accept:
                    pump(lambda: len(result_details) > previous_results, 25)
                    if "已发送 1 项，未完成 0 项" not in result_details[-1]:
                        raise RuntimeError("File upload did not finish: " + result_details[-1])
                    dialogs = [widget for widget in descendants(root) if isinstance(widget, app.tk.Toplevel)
                               and widget.title() == "文件传输结果"]
                    capture(dialogs[-1], name + "-result.png")
                    for dialog in dialogs:
                        dialog.destroy()
                elif len(result_details) != previous_results or first.file_transfer_lock.locked():
                    raise RuntimeError("Cancelled preview started a transfer")
                report["checks"].append({"name": name, "passed": True, "preview": observed,
                    "receipt": result_details[-1] if accept else None})
            preview([folder], False, "cancel-folder")
            preview([source], True, "upload-file")
            preview([folder], True, "upload-directory")
            ui.disconnect_button.invoke()
            pump(lambda: first.stop_event.is_set() and first.sock is None)
            second = connect()
            if second is first:
                raise RuntimeError("Reconnect reused the old connection")
            report["checks"].append({"name": "gui-disconnect-reconnect", "passed": True, "generation": second.generation})
            capture(ui.viewer_window, "reconnected.png")
            report["complete"] = True
        except Exception as error:
            report["failure"] = type(error).__name__ + ": " + str(error)
            report["statuses"] = statuses[-40:] if ui is not None else []
            if ui is not None:
                report["viewerObserved"] = None if ui.viewer is None else {
                    "frameSequence": ui.viewer.frame_sequence, "encoding": ui.viewer.received_frame_encoding,
                    "lastRenderedEncoding": ui.viewer.last_rendered_encoding, "stopped": ui.viewer.stop_event.is_set(),
                    "remoteWidth": ui.remote_width, "remoteHeight": ui.remote_height,
                    "nativePresenterActive": ui.native_presenter_active}
                capture(ui.viewer_window or root, "failure.png")
        finally:
            if ui is not None:
                ui.close()
            if window_manager is not None:
                window_manager.terminate(); window_manager.wait(timeout=6)
            (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"complete": report["complete"], "failure": report.get("failure"), "checks": len(report["checks"])}))
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
