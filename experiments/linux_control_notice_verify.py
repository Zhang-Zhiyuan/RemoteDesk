"""Owned Xvfb desktop: real authentication, notice windows and local clicks.

Only the capture/input body is replaced with an inert wait, so no user desktop,
clipboard or files can be read. Never run on a real logged-in display.
"""
import argparse
import json
import os
import socket
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host
import remotedesk_protocol_probe as protocol
from remotedesk_linux_control_notice import HostControlNotice


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("An isolated Xvfb display with WAYLAND_DISPLAY unset is required")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    wm = subprocess.Popen(["openbox", "--sm-disable"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(.3)
    root = tk.Tk()
    root.title("RemoteDesk owned typing fixture")
    root.geometry("400x200+20+180")
    entry = tk.Entry(root)
    entry.pack(fill=tk.X, pady=40)
    root.update()
    entry.focus_force()
    checks = []
    def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})
        (output / "notice.json").write_text(json.dumps({"checks": checks}, ensure_ascii=False, indent=2), encoding="utf-8")
        if not passed:
            raise AssertionError(name)
    def xdotool(*command, required=True):
        result = subprocess.run(["xdotool", *map(str, command)], capture_output=True, text=True, timeout=3)
        if required and result.returncode:
            raise RuntimeError(result.stderr)
        return result.stdout.strip()
    def pump():
        root.update()
        time.sleep(.02)
    def wait(ready):
        deadline = time.monotonic() + 12
        while not ready():
            if time.monotonic() >= deadline:
                raise TimeoutError("Notice condition was not reached")
            pump()
        pump()
    def visible(notice):
        process = notice.process
        # Tk does not publish _NET_WM_PID on every X11 build. This whole display
        # is owned by the fixture, and the exact notice title is unique here.
        return [] if process is None else xdotool("search", "--onlyvisible", "--name",
            "^RemoteDesk Remote Control Notice$", required=False).split()
    def geometry(window):
        return {k: int(v) for k, v in (row.split("=", 1) for row in xdotool("getwindowgeometry", "--shell", window).splitlines())}
    pump()
    focus = xdotool("getwindowfocus")
    check("owned text entry has focus", root.focus_get() == entry)
    stop = threading.Event()
    gate = host.ClientAdmissionGate()
    workers = []
    class InertCaptureBody:
        def __init__(self, *_, session_stop, **__):
            self.stop = session_stop
        def run(self):
            while not stop.is_set() and not self.stop.wait(.05):
                pass
    notice = HostControlNotice(log=lambda value: print(value, flush=True))
    disconnect_notice = notice.disconnect
    def trace_disconnect(revision):
        (output / "focus-at-click.json").write_text(json.dumps({"before": focus,
            "atClick": xdotool("getwindowfocus", required=False)}), encoding="utf-8")
        return disconnect_notice(revision)
    notice.disconnect = trace_disconnect
    capture_patch = mock.patch.object(host, "LinuxHostSession", InertCaptureBody)
    capture_patch.start()
    start_process = subprocess.Popen
    child_errors = (output / "helper-errors.log").open("w", encoding="utf-8")
    def start_with_diagnostics(command, *positional, **options):
        if "--notice-window" in command:
            options["stderr"] = child_errors
        return start_process(command, *positional, **options)
    listener = socket.create_server(("127.0.0.1", 0))
    def connect(password="owned-notice-fixture"):
        peer = socket.create_connection(listener.getsockname(), timeout=4)
        client, address = listener.accept()
        assert gate.try_register_pending(client)
        thread = threading.Thread(target=host.run_admitted_client, args=(client, address,
            SimpleNamespace(password="owned-notice-fixture", auth_timeout=2), stop, gate, notice), daemon=True)
        workers.append(thread)
        thread.start()
        try:
            return peer, protocol.authenticate(peer, password)
        except Exception:
            peer.close()
            raise
    try:
        with notice, mock.patch("subprocess.Popen", side_effect=start_with_diagnostics):
            try:
                connect("wrong-key")
            except (protocol.ProtocolError, PermissionError):
                pass
            check("failed authentication has no notice", not notice.snapshot()[0] and not visible(notice))
            first, session = connect()
            wait(lambda: bool(visible(notice)))
            check("authenticated host displays persistent notice", notice.snapshot()[0])
            check("showing notice preserves typing focus", xdotool("getwindowfocus") == focus)
            crashed = notice.process
            crashed.terminate()
            wait(lambda: notice.process is not None and notice.process.pid != crashed.pid and bool(visible(notice)))
            check("notice process failure recovers during the same session", notice.snapshot()[0])
            first_pid = notice.process.pid
            second, second_session = connect()
            kind, payload = protocol.read_message(first, session)
            first.close()
            check("new viewer receives ownership and old viewer is rejected", kind == protocol.MESSAGE_CONTROL and
                  protocol.decode_control(payload)["kind"] == protocol.CONTROL_SESSION_REJECTED)
            wait(lambda: len(notice.sessions) == 1)
            check("takeover retains the same visible notice process", notice.process.pid == first_pid and visible(notice))
            # Let the coalesced ownership revision reach Tk before clicking it.
            for _ in range(15):
                pump()
            windows = visible(notice)
            window = max(windows, key=lambda item: geometry(item)["WIDTH"] * geometry(item)["HEIGHT"])
            bounds = geometry(window)
            check("notice fits within the display", bounds["X"] >= 0 and bounds["Y"] >= 0 and
                  bounds["X"] + bounds["WIDTH"] <= root.winfo_screenwidth())
            subprocess.run(["import", "-window", window, str(output / "notice.png")], check=True, timeout=5)
            xdotool("mousemove", "--window", window, bounds["WIDTH"] - 35, bounds["HEIGHT"] // 2, "click", "1")
            kind, payload = protocol.read_message(second, second_session)
            second.close()
            control = protocol.decode_control(payload)
            check("local button sends terminal no-reconnect rejection", kind == protocol.MESSAGE_CONTROL and
                  control["kind"] == protocol.CONTROL_SESSION_REJECTED and "主动断开" in control["statusMessage"])
            wait(lambda: not notice.snapshot()[0] and notice.process is None)
            check("disconnect removes window but preserves host listener", listener.fileno() >= 0 and not visible(notice))
            check("local click preserves keyboard focus", xdotool("getwindowfocus") == focus)
            reconnected_at = time.monotonic()
            third, _ = connect()
            wait(lambda: bool(visible(notice)))
            check("normal reconnect has no failure-retry delay", time.monotonic() - reconnected_at < 3)
            helper = notice.process
            notice.close()
            check("host shutdown removes notice process", helper.poll() is not None)
            third.close()
    finally:
        stop.set()
        for client in gate.stop_and_drain():
            host.close_client_socket(client)
        for worker in workers:
            worker.join(2)
        capture_patch.stop()
        listener.close()
        notice.close()
        child_errors.close()
        root.destroy()
        wm.terminate()
        wm.wait(timeout=3)
    print(json.dumps({"passed": len(checks), "output": str(output)}))


if __name__ == "__main__":
    main()
