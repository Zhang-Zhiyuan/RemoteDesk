"""Actual Tk SSH login on owned Xvfb; disposable settings and no network session."""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app
from PIL import ImageGrab


def descendants(widget):
    for child in widget.winfo_children():
        yield child
        yield from descendants(child)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("An explicitly owned Xvfb is required")
    args.output.mkdir(parents=True, exist_ok=False)
    cases, failures = [], []
    with tempfile.TemporaryDirectory() as config, mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": config}), \
            mock.patch.object(app.DevicePanel, "scan"):
        root = app.tk.Tk()
        ui = app.RemoteDeskLinuxApp(root)
        root.report_callback_exception = lambda *error: failures.append(str(error))
        try:
            for points in (10, 20):
                app.ttk.Style(root).configure(".", font=("DejaVu Sans", points))
                for missing in (False, True):
                    key = app.tk.StringVar(value="") if missing else None
                    def inspect():
                        dialog = next(w for w in root.winfo_children() if isinstance(w, app.tk.Toplevel))
                        try:
                            dialog.minsize(1, 1)
                            for expanded in (False, True):
                                more = next(w for w in descendants(dialog) if isinstance(w, app.ttk.Checkbutton))
                                if expanded: more.invoke()
                                for width in (360, 720):
                                    dialog.geometry(f"{width}x300+0+0")
                                    root.update()
                                    for widget in descendants(dialog):
                                        if isinstance(widget, app.ttk.Entry) and widget.winfo_ismapped():
                                            widget.event_generate("<FocusIn>")
                                            root.update()
                                            assert widget.winfo_rooty() >= dialog.winfo_rooty()
                                            assert widget.winfo_rooty() + widget.winfo_height() <= dialog.winfo_rooty() + dialog.winfo_height()
                                    buttons = [w for w in descendants(dialog) if isinstance(w, app.ttk.Button) and w.cget("text") in ("打开终端", "取消")]
                                    assert len(buttons) == 2
                                    for button in buttons:
                                        assert button.winfo_ismapped()
                                        assert button.winfo_width() >= button.winfo_reqwidth()
                                        assert button.winfo_rootx() + button.winfo_width() <= dialog.winfo_rootx() + dialog.winfo_width()
                                        assert button.winfo_rooty() + button.winfo_height() <= dialog.winfo_rooty() + dialog.winfo_height()
                                    name = f"login-font{points}-missing{missing}-advanced{expanded}-{width}"
                                    ImageGrab.grab((0, 0, width, 300), xdisplay=os.environ["DISPLAY"]).save(args.output / (name + ".png"))
                                    cases.append(name)
                        except Exception as error:
                            failures.append(__import__("traceback").format_exc())
                            ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(args.output / f"failure-{points}-{missing}.png")
                        finally:
                            dialog.cancel()
                    root.after(100, inspect)
                    ui._ask_ssh_login("", device_key=key)
        finally:
            ui.close()
            (args.output / "report.json").write_text(json.dumps(dict(passed=not failures, cases=cases, failures=failures), indent=2))
    if failures:
        raise AssertionError(failures)
    print(f"Passed {len(cases)} actual Linux login layouts")


if __name__ == "__main__":
    main()
