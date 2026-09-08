#!/usr/bin/env python3
"""Real Tk consent/cancel/progress windows on owned Xvfb, never installs packages."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import tkinter as tk
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_dependencies as deps


def product_startup():
    # Launch the actual __main__ entry, including its real dependency preflight.
    # This GUI starts no host until a user supplies a nonempty password.
    environment = os.environ.copy()
    environment.pop("REMOTEDESK_AUTO_INSTALL", None)
    if deps.detect_missing("app"):
        raise RuntimeError("Product startup smoke requires preinstalled dependencies; this probe never installs")
    product = Path(__file__).resolve().parents[1] / "scripts/linux/remotedesk_linux_app.py"
    child = subprocess.Popen([sys.executable, str(product)], env=environment,
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    visible = False
    try:
        deadline = time.monotonic() + 8
        while child.poll() is None and time.monotonic() < deadline:
            windows = subprocess.run(["xwininfo", "-root", "-tree"],
                capture_output=True, text=True, timeout=2, check=True)
            if '"RemoteDesk Linux"' in windows.stdout:
                # Tk can map the title before the constructor finishes and main
                # installs its signal handlers. Allow startup to finish first.
                time.sleep(1)
                visible = child.poll() is None
                break
            time.sleep(0.1)
    finally:
        if child.poll() is None:
            child.terminate()
        try:
            output, _ = child.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            child.kill()
            output, _ = child.communicate(timeout=2)
    return {"windowVisible": visible, "exitCode": child.returncode,
            "output": output[-2000:], "passed": visible and child.returncode == 0}


def answer_dialog(button):
    original = tk.Tk
    def create(*args, **kwargs):
        root = original(*args, **kwargs)
        attempts = 0
        def answer():
            nonlocal attempts
            attempts += 1
            widget = ".__tk__messagebox." + button
            if root.tk.call("winfo", "exists", widget):
                root.tk.call(widget, "invoke")
            elif attempts < 40:
                root.after(50, answer)
            else:
                root.destroy()
        root.after(50, answer)
        return root
    with mock.patch.object(tk, "Tk", side_effect=create):
        return deps.dialog("RemoteDesk isolated dependency UI test. No packages will be installed.",
                           question=True, graphical=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise SystemExit("Requires an owned Xvfb display")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"accepted": answer_dialog("yes"), "declined": not answer_dialog("no"),
              "scope": "actual Tk windows; simulated installer, no polkit/apt invocation"}
    for label, code in (("immediate", 0), ("failure", 100)):
        returned, tail = deps.run_installer([sys.executable, "-c",
            f"print('synthetic dependency progress', flush=True); raise SystemExit({code})"], True)
        report[label] = {"exitCode": returned, "passed": returned == code and "synthetic" in tail}
    report["productStartup"] = product_startup()
    report["passed"] = report["accepted"] and report["declined"] and all(report[name]["passed"] for name in ("immediate", "failure", "productStartup"))
    (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
