#!/usr/bin/env python3
"""Exercise real XTest/xdotool Unicode delivery in an owned Tk/Xvfb under capture load."""
import argparse
import hashlib
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import tkinter as tk

import interop_linux_node as node
import remotedesk_linux_host as host


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--unicode-delay", type=int, default=0, choices=(0, 12, 30, 50))
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    if args.unicode_delay:
        original = host.text_codepoint_to_xdotool_commands
        def commands(scalar):
            result = original(scalar)
            if scalar > 127 and result:
                result[0][result[0].index("--delay") + 1] = str(args.unicode_delay)
            return result
        host.text_codepoint_to_xdotool_commands = commands
    display = node.start_xvfb(output)
    root = tk.Tk()
    root.overrideredirect(True)
    root.geometry("1920x1080+0+0")
    entry = tk.Entry(root, font=("monospace", 30))
    entry.pack(fill="x")
    root.update(); root.focus_force(); entry.focus_set(); root.update()
    encoder = None
    results, messages = [], queue.Queue()
    stop = threading.Event()
    try:
        launch = host.find_jetson_gstreamer_encoder()
        candidate = host.build_jetson_h264_encoder_command(launch, os.environ["DISPLAY"], 1920, 1080, 1920, 1080, 30)
        encoder = subprocess.Popen(candidate.command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        def inject():
            controller = host.LinuxInputController()
            try:
                for batch in range(20):
                    if stop.is_set(): return
                    ready, done = threading.Event(), threading.Event()
                    text = f"中文测试😀界面清晰输入验证-{batch:02d}-RemoteDesk"
                    messages.put(("clear", ready))
                    if not ready.wait(3): raise RuntimeError("Owned Tk target not processing events")
                    started = time.monotonic()
                    for scalar in map(ord, text):
                        if stop.is_set(): return
                        if not controller.apply(host.InputCommand(host.INPUT_TEXT, 0, 0, 0, scalar), (1920, 1080), (1920, 1080)):
                            raise RuntimeError("Product rejected a valid scalar")
                    messages.put(("check", (batch, text, time.monotonic()-started, done)))
                    if not done.wait(3): raise RuntimeError("Owned Tk result not observed")
            except Exception as error:
                messages.put(("error", str(error)))
            finally:
                controller.close()
                messages.put(("finish", None))
        worker = threading.Thread(target=inject, daemon=True)
        worker.start()
        def poll():
            try:
                while True:
                    action, value = messages.get_nowait()
                    if action == "clear": entry.delete(0, "end"); root.update_idletasks(); value.set()
                    elif action == "check":
                        batch, expected, seconds, done = value
                        def check(batch=batch, expected=expected, seconds=seconds, done=done):
                            actual = entry.get()
                            results.append(dict(batch=batch, complete=actual == expected, expected=expected, actual=actual, seconds=seconds))
                            done.set()
                        root.after(100, check)
                    elif action == "error": results.append(dict(complete=False, failure=value))
                    elif action == "finish": root.quit(); return
            except queue.Empty: pass
            root.after(5, poll)
        poll()
        root.after(90000, root.quit)
        root.mainloop()
        stop.set(); worker.join(timeout=2)
        complete = len(results) == 20 and all(row["complete"] for row in results)
        node.save(output / "result.json", dict(complete=complete, unicodeDelay=args.unicode_delay, results=results,
            modulePath=str(Path(host.__file__).resolve()), hostSha256=hashlib.sha256(Path(host.__file__).read_bytes()).hexdigest()))
        print({"complete": complete, "batches": len(results), "failed": sum(not row["complete"] for row in results)}, flush=True)
        return 0 if complete else 1
    finally:
        stop.set()
        if encoder: node.stop(encoder)
        root.destroy()
        node.stop(display)


if __name__ == "__main__": raise SystemExit(main())
