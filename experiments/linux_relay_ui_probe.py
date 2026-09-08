#!/usr/bin/env python3
"""Real Tk relay controls on an owned Xvfb; deterministic directory, no OS input.

This checks GUI wiring/races, not network interoperability (the six-way runner
does that separately). Settings use a disposable XDG directory and fake secrets.
"""
import argparse
from dataclasses import replace
import json
import os
from pathlib import Path
import tempfile
import time
import tkinter as tk
from tkinter import ttk
from unittest import mock

from interop_linux_node import app, save, start_xvfb, stop


def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)
    display = start_xvfb(output)
    root = None
    report = {"complete": False, "scope": "actual Tk controls on owned Xvfb; simulated directory and connection callback"}
    try:
        with tempfile.TemporaryDirectory(prefix="relay-ui-", dir=output) as settings:
            with mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": settings}):
                root = tk.Tk(); window = app.RemoteDeskLinuxApp(root)
                notebook = next(child for child in root.winfo_children() if isinstance(child, ttk.Notebook))
                report["threeTabs"] = len(notebook.tabs()) == 3
                notebook.select(2); root.update()
                def pump_until(predicate):
                    deadline = time.monotonic() + 3
                    while time.monotonic() < deadline:
                        root.update()
                        if predicate(): return
                        time.sleep(.01)
                    raise RuntimeError("Tk relay event did not complete")
                local_id, other_id = window.relay_device_id, "aac4c2b8-7d28-4dbe-8b1c-3f396cecc6d6"
                devices = [dict(deviceId=local_id, machineName="Local", platform="Linux", busy=False),
                           dict(deviceId=other_id, machineName="Owned other target", platform="Android", busy=True)]
                with mock.patch.object(app.relay, "list_devices", return_value=devices), \
                     mock.patch.object(window, "_start_viewer_attempt") as connect:
                    window.relay_server.set("relay.invalid")
                    window.relay_token.set("owned-ui-test-token-" * 3)
                    window.relay_pin.set("AA" * 32)
                    window._save_relay()
                    pump_until(lambda: not window.relay_refreshing and len(window.relay_list.get_children()) == 2)
                    saved = app.relay.load_settings()
                    report["savedPrivately"] = saved == window.relay_options and (app.relay.settings_path().stat().st_mode & 0o777) == 0o600
                    window.relay_list.selection_set(local_id); window._connect_relay()
                    report["selfConnectionBlocked"] = not connect.called and "不能连接自己" in window.relay_status.cget("text")
                    window.relay_list.selection_set(other_id)
                    window.relay_password.set("owned-ui-test-password")
                    window._connect_relay()
                    report["selectedTargetKeepsRelayRoute"] = (connect.call_count == 1 and
                        window.viewer_relay_options.device_id == other_id and
                        window.viewer_relay_options.server_address == "relay.invalid" and
                        window.relay_options.device_id == local_id)
                    # A completion from the old configuration must not populate
                    # rows that the new configuration could connect elsewhere.
                    old = window.relay_options
                    window.relay_options = replace(old, server_address="new-relay.invalid")
                    window.relay_refreshing = True
                    app.put_ui_event(window.events, "relay_directory", (window.relay_refresh_generation,
                        old, [dict(devices[1], machineName="STALE")], ""))
                    pump_until(lambda: not window.relay_refreshing)
                    report["staleDirectoryDiscarded"] = all(window.relay_list.item(item, "text") != "STALE"
                                                          for item in window.relay_list.get_children())
                    before = window.relay_status.cget("text")
                    app.put_ui_event(window.events, "relay_status", (object(), "STALE REGISTRATION"))
                    pump_until(window.events.empty)
                    report["staleRegistrationIgnored"] = window.relay_status.cget("text") == before
                window.close(); root = None
                report["complete"] = all(value for key, value in report.items() if key not in ("complete", "scope"))
    except Exception as error:
        report["failure"] = type(error).__name__ + ": " + str(error)
    finally:
        if root is not None: root.destroy()
        stop(display); save(output / "result.json", report)
    print(json.dumps(report), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__": raise SystemExit(main())
