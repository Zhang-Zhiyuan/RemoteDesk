"""Local-only remote-control notice, with no network or credential interface.

Tk runs in its own process on the host's capture DISPLAY. Pipe EOF removes the
notice even if the host crashes. The capture/input threads never wait for Tk.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
from pathlib import Path
from typing import Callable


class HostControlNotice:
    def __init__(self, enabled: bool = True, log: Callable[[str], None] = print):
        self.enabled = enabled
        self.log = log
        self.lock = threading.Lock()
        self.changed = threading.Event()
        self.sessions: dict[object, Callable[[], None]] = {}
        self.revision = 0
        self.closed = False
        self.process: subprocess.Popen[str] | None = None
        self.worker: threading.Thread | None = None

    def __enter__(self):
        if self.enabled:
            self.worker = threading.Thread(target=self._run, name="RemoteDeskControlNotice", daemon=True)
            self.worker.start()
        return self

    def __exit__(self, *_):
        self.close()

    def begin(self, disconnect: Callable[[], None]) -> object:
        token = object()
        with self.lock:
            if self.closed:
                return token
            self.sessions[token] = disconnect
            self.revision += 1
        self.changed.set()
        return token

    def end(self, token: object) -> None:
        with self.lock:
            if token not in self.sessions:
                return
            del self.sessions[token]
            self.revision += 1
        self.changed.set()

    def snapshot(self) -> tuple[bool, int]:
        with self.lock:
            return bool(self.sessions) and not self.closed, self.revision

    def disconnect(self, revision: int) -> bool:
        with self.lock:
            if self.closed or revision != self.revision or not self.sessions:
                return False
            callbacks = tuple(self.sessions.values())
        for callback in callbacks:
            try:
                callback()
            except Exception as error:
                self.log(f"断开远程控制失败：{type(error).__name__}")
        return True

    def close(self) -> None:
        with self.lock:
            self.closed = True
        self.changed.set()
        if self.worker is not None and self.worker is not threading.current_thread():
            self.worker.join(timeout=4)

    def _read_actions(self, process: subprocess.Popen[str]) -> None:
        assert process.stdout is not None
        try:
            for line in process.stdout:
                try:
                    action = json.loads(line)
                    revision = action.get("disconnect")
                    if type(revision) is int and process is self.process:
                        self.disconnect(revision)
                except (ValueError, AttributeError):
                    pass
        finally:
            process.stdout.close()
            self.changed.set()

    @staticmethod
    def _stop(process: subprocess.Popen[str]) -> None:
        try:
            if process.stdin is not None:
                process.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=1)

    def _run(self) -> None:
        last_sent = -1
        process_display = ""
        warned = False
        retry_at = 0.0
        import time
        try:
            while True:
                self.changed.wait(timeout=1 if self.process is not None else None)
                self.changed.clear()
                active, revision = self.snapshot()
                display = os.environ.get("DISPLAY", "")
                if self.process is not None and (not active or display != process_display or self.process.poll() is not None):
                    if active and self.process.poll() is not None and not warned:
                        self.log("被控提示窗口意外退出，将自动重试；请检查图形桌面和 Tk 运行环境。")
                        warned = True
                    self._stop(self.process)
                    self.process = None
                    last_sent = -1
                if self.closed:
                    break
                if not active:
                    warned = False
                    retry_at = 0.0
                    continue
                if not display:
                    if not warned:
                        self.log("当前没有可用的图形桌面，无法显示被控提示。")
                        warned = True
                    # Retry if the host acquires a desktop during this session.
                    self.changed.wait(1)
                    self.changed.set()
                    continue
                try:
                    if self.process is None:
                        if time.monotonic() < retry_at:
                            self.changed.wait(1)
                            self.changed.set()
                            continue
                        retry_at = time.monotonic() + 5
                        process_display = display
                        self.process = subprocess.Popen(
                            [sys.executable, str(Path(__file__).resolve()), "--notice-window"],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            text=True, encoding="utf-8", bufsize=1,
                            env={**os.environ, "DISPLAY": display},
                        )
                        threading.Thread(target=self._read_actions, args=(self.process,),
                                         name="RemoteDeskNoticeActions", daemon=True).start()
                    if revision != last_sent:
                        assert self.process.stdin is not None
                        self.process.stdin.write(json.dumps({"revision": revision}) + "\n")
                        self.process.stdin.flush()
                        last_sent = revision
                except (OSError, ValueError) as error:
                    if not warned:
                        self.log(f"被控提示暂时不可用，将自动重试：{type(error).__name__}")
                        warned = True
                    if self.process is not None:
                        self._stop(self.process)
                        self.process = None
                    last_sent = -1
                    self.changed.wait(1)
                    self.changed.set()
        finally:
            if self.process is not None:
                self._stop(self.process)
                self.process = None


def run_notice_window() -> None:
    import tkinter as tk

    root = tk.Tk()
    root.withdraw()
    root.title("RemoteDesk Remote Control Notice")
    root.overrideredirect(True)
    root.attributes("-topmost", True)
    root.configure(bg="#7f1d1d", padx=10, pady=8)
    root.protocol("WM_DELETE_WINDOW", lambda: None)
    revision = 0
    drag: list[int] = []
    label = tk.Label(root, text="●  RemoteDesk · 正在被远程控制", bg="#7f1d1d",
                     fg="white", padx=4, justify="left", cursor="fleur", takefocus=False)
    label.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

    def disconnect():
        button.configure(state=tk.DISABLED, text="断开中")
        try:
            print(json.dumps({"disconnect": revision}), flush=True)
        except (BrokenPipeError, OSError):
            root.destroy()

    button = tk.Button(root, text="断开", command=disconnect, bg="#b91c1c", fg="white",
                       activebackground="#991b1b", activeforeground="white", takefocus=False,
                       relief=tk.FLAT, padx=12)
    button.pack(side=tk.RIGHT, padx=(12, 0))
    pressed = False

    # Keep the notice pointer-only. takefocus=False alone controls keyboard
    # traversal, not the actions a theme's pointer bindings may perform.
    def press_button(_event):
        nonlocal pressed
        pressed = str(button["state"]) != tk.DISABLED
        if pressed:
            button.configure(relief=tk.SUNKEN)
        return "break"

    def release_button(event):
        nonlocal pressed
        invoke = pressed and 0 <= event.x < button.winfo_width() and 0 <= event.y < button.winfo_height()
        pressed = False
        button.configure(relief=tk.FLAT)
        if invoke:
            disconnect()
        return "break"

    button.bind("<ButtonPress-1>", press_button)
    button.bind("<ButtonRelease-1>", release_button)

    def move(event):
        if drag:
            x = max(0, min(root.winfo_screenwidth() - root.winfo_width(), event.x_root - drag[0]))
            y = max(0, min(root.winfo_screenheight() - root.winfo_height(), event.y_root - drag[1]))
            root.geometry(f"+{x}+{y}")

    label.bind("<ButtonPress-1>", lambda e: drag.__setitem__(slice(None),
        [e.x_root - root.winfo_x(), e.y_root - root.winfo_y()]))
    label.bind("<B1-Motion>", move)
    label.bind("<ButtonRelease-1>", lambda _: drag.clear())
    pending = bytearray()

    def read_state(_fd, _mask):
        nonlocal revision
        data = os.read(sys.stdin.fileno(), 4096)
        if not data:
            root.destroy()
            return
        pending.extend(data)
        while b"\n" in pending:
            line, _, rest = pending.partition(b"\n")
            pending[:] = rest
            value = json.loads(line)
            revision = int(value["revision"])
            button.configure(state=tk.NORMAL, text="断开")
            if not root.winfo_viewable():
                label.configure(wraplength=max(60, root.winfo_screenwidth() - 180))
                root.update_idletasks()
                x = max(0, (root.winfo_screenwidth() - root.winfo_reqwidth()) // 2)
                root.geometry(f"+{x}+8")
                root.deiconify()  # No focus_set/focus_force; other apps keep focus.
    root.createfilehandler(sys.stdin, tk.READABLE, read_state)
    root.mainloop()


if __name__ == "__main__" and sys.argv[1:] == ["--notice-window"]:
    run_notice_window()
