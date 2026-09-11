#!/usr/bin/env python3
"""Consent-based startup dependency repair; never elevates the RemoteDesk app."""
from __future__ import annotations

import argparse
from collections import deque
from dataclasses import asdict, dataclass
import json
import os
import shlex
import shutil
import subprocess
import sys
import threading


CANCELLED = 125
UNAVAILABLE = 78
SYSTEM_PATH = "/usr/sbin:/usr/bin:/sbin:/bin"


@dataclass(frozen=True)
class MissingDependency:
    key: str
    label: str
    package: str
    detail: str = ""


# Packages are fixed by the application, never taken from a peer or shell text.
DEPENDENCIES = {
    "crypto": ("加密通信", "python3-cryptography"),
    "tk": ("图形界面 Tk", "python3-tk"),
    "pillow": ("JPEG 图像解码", "python3-pil"),
    "ssh": ("服务器 root 登录", "python3-paramiko"),
    "ffmpeg": ("画面采集与视频编解码 FFmpeg", "ffmpeg"),
    "x11": ("X11 图形库", "libx11-6"),
    "xtst": ("鼠标和键盘控制 XTest", "libxtst6"),
    "xdotool": ("Unicode 文字输入", "xdotool"),
    "xrandr": ("显示器枚举", "x11-xserver-utils"),
    "xdpyinfo": ("桌面连接检测", "x11-utils"),
    "clipboard": ("X11 文本与文件剪贴板", "xclip"),
    "wayland_clipboard": ("Wayland 剪贴板", "wl-clipboard"),
}
ALLOWED_PACKAGES = frozenset(value[1] for value in DEPENDENCIES.values())
MODULE_PROBE = r'''
import ctypes, ctypes.util, json, sys
checks = {
    "crypto": "from cryptography.hazmat.primitives.ciphers.aead import AESGCM; AESGCM.generate_key(bit_length=128)",
    "pillow": "from PIL import Image; Image.new('RGB', (1, 1)).close()",
    "x11": "ctypes.CDLL(ctypes.util.find_library('X11') or 'libX11.so.6')",
    "xtst": "ctypes.CDLL(ctypes.util.find_library('Xtst') or 'libXtst.so.6')",
}
if sys.argv[1] == "app":
    checks["tk"] = "import tkinter; tkinter.Tcl()"
    checks["ssh"] = "import paramiko"
errors = {}
for key, code in checks.items():
    try:
        exec(code)
    except Exception as error:
        errors[key] = str(error)[:400]
print(json.dumps(errors))
'''


def system_environment() -> dict[str, str]:
    # Bundled Python/OpenSSL/FFmpeg libraries must not be injected into apt,
    # polkit or a distribution's dialog executable.
    result = {key: value for key, value in os.environ.items()
              if not key.startswith(("PYTHON", "LD_")) and key not in ("APT_CONFIG", "BASH_ENV", "ENV")}
    result["PATH"] = SYSTEM_PATH
    return result


def detect_missing(mode: str = "app") -> list[MissingDependency]:
    if mode not in ("app", "host"):
        raise ValueError("Unsupported dependency profile")
    result = subprocess.run([sys.executable, "-c", MODULE_PROBE, mode],
        capture_output=True, text=True, timeout=12, check=False)
    if result.returncode:
        raise RuntimeError("包内 Python 无法运行；请检查架构、glibc 和安装完整性。\n" + result.stderr[-2000:])
    try:
        errors = json.loads(result.stdout)
        if not isinstance(errors, dict) or any(key not in DEPENDENCIES for key in errors):
            raise ValueError("Invalid dependency probe result")
    except (ValueError, TypeError) as error:
        raise RuntimeError("无法读取运行环境检查结果") from error
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        errors["ffmpeg"] = "未找到 ffmpeg"
    else:
        try:
            version = subprocess.run([ffmpeg, "-version"], capture_output=True,
                text=True, timeout=5, check=False, env=system_environment())
            if version.returncode:
                errors["ffmpeg"] = version.stderr[-400:] or "ffmpeg 无法运行"
        except (OSError, subprocess.TimeoutExpired) as error:
            errors["ffmpeg"] = str(error)[:400]
    for name in ("xrandr", "xdpyinfo", "xdotool"):
        if not shutil.which(name):
            errors[name] = "未找到 " + name
    if not any(shutil.which(name) for name in ("xclip", "xsel")):
        errors["clipboard"] = "未找到 xclip / xsel"
    if os.environ.get("WAYLAND_DISPLAY") and not all(shutil.which(name) for name in ("wl-copy", "wl-paste")):
        errors["wayland_clipboard"] = "未找到 wl-copy / wl-paste"
    return [MissingDependency(key, *DEPENDENCIES[key], str(detail)) for key, detail in errors.items()]


def install_command(missing: list[MissingDependency]) -> list[str]:
    packages = sorted({item.package for item in missing})
    if not packages or any(package not in ALLOWED_PACKAGES for package in packages):
        raise ValueError("Invalid dependency package list")
    # This release targets Ubuntu/Debian (including Jetson). Do not guess package
    # names on other distributions, enable third-party repos or replace drivers.
    apt = "/usr/bin/apt-get"
    if not os.path.isfile(apt) or not os.access(apt, os.X_OK):
        raise RuntimeError("当前自动安装支持 Debian / Ubuntu / Jetson；此系统请按缺项手动安装。")
    return [apt, "--assume-yes", "--no-remove", "--no-install-recommends",
            "-o", "DPkg::Lock::Timeout=30", "-o", "Acquire::Retries=1",
            "-o", "Acquire::http::Timeout=30", "-o", "Acquire::https::Timeout=30",
            "install", *packages]


def elevated_command(command: list[str], graphical: bool) -> list[str]:
    if os.geteuid() == 0:
        return command
    if graphical and os.access("/usr/bin/pkexec", os.X_OK):
        # Without a session agent fail explicitly, not an invisible tty prompt.
        return ["/usr/bin/pkexec", "--disable-internal-agent", *command]
    if sys.stdin.isatty() and os.access("/usr/bin/sudo", os.X_OK):
        return ["/usr/bin/sudo", "--", *command]
    raise RuntimeError("无法申请管理员权限：桌面需要 polkit 授权代理，或在终端中启动后使用 sudo。")


def dialog(message: str, *, question: bool, graphical: bool) -> bool:
    if graphical:
        root = None
        try:
            import tkinter as tk
            from tkinter import messagebox
            root = tk.Tk(className="RemoteDeskDependencies")
            root.withdraw()
            if question:
                return bool(messagebox.askyesno("RemoteDesk · 运行环境", message, parent=root))
            messagebox.showerror("RemoteDesk · 运行环境", message, parent=root)
            return False
        except Exception:
            pass
        finally:
            if root is not None:
                try:
                    root.destroy()
                except Exception:
                    pass
        for executable, arguments in (
            ("zenity", ["--question" if question else "--error", "--no-markup", "--title=RemoteDesk", "--text=" + message]),
            ("kdialog", ["--yesno" if question else "--error", message, "--title", "RemoteDesk"]),
            ("xmessage", ["-center", "-buttons", "Install:0,Cancel:1" if question else "OK:0", message]),
        ):
            path = shutil.which(executable, path=SYSTEM_PATH)
            if path:
                try:
                    completed = subprocess.run([path, *arguments], env=system_environment(), check=False)
                    return question and completed.returncode == 0
                except OSError:
                    continue
        if question and not sys.stdin.isatty():
            raise RuntimeError("无法显示安装确认窗口；请从终端启动 RemoteDesk，或安装 Tk / zenity 后重试。\n" + message)
    print(message, file=sys.stderr, flush=True)
    if question and sys.stdin.isatty():
        try:
            return input("安装上述依赖？[y/N] ").strip().lower() in ("y", "yes")
        except (EOFError, KeyboardInterrupt):
            return False
    return False


def run_installer(command: list[str], graphical: bool) -> tuple[int, str]:
    """Keep GUI responsive and apt output bounded; never kill a package transaction."""
    root = None
    label = None
    progress = None
    if graphical:
        try:
            import tkinter as tk
            from tkinter import ttk
            root = tk.Tk(className="RemoteDeskDependencies")
            root.title("RemoteDesk · 正在准备运行环境")
            root.geometry("560x180")
            label = tk.StringVar(value="请完成系统管理员授权；安装日志将显示在这里。")
            ttk.Label(root, textvariable=label, wraplength=520).pack(padx=20, pady=20)
            progress = ttk.Progressbar(root, mode="indeterminate")
            progress.pack(fill="x", padx=20, pady=10)
            progress.start(15)
            root.protocol("WM_DELETE_WINDOW", lambda: label.set("包管理器仍在运行，请等待完成；授权窗口可以取消。"))
            root.update()
        except Exception:
            if root is not None:
                try:
                    if progress is not None:
                        progress.stop()
                    root.destroy()
                except Exception:
                    pass
            root = None
    tail: deque[str] = deque(maxlen=80)
    lock = threading.Lock()
    process = None
    try:
        # Passwords are handled by polkit or sudo's controlling terminal, never
        # read or stored here. The command is an argv list, not a root shell.
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL if graphical else None, env=system_environment(),
            text=True, errors="replace", bufsize=1)
        def collect():
            assert process is not None and process.stdout is not None
            for line in process.stdout:
                with lock:
                    tail.append(line[-1000:])
                print(line, end="", flush=True)
        reader = threading.Thread(target=collect, name="RemoteDeskDependencyInstall", daemon=True)
        reader.start()
        if root is not None:
            def poll():
                with lock:
                    latest = tail[-1] if tail else "等待系统授权或包管理器响应……"
                label.set(latest[-400:])
                if process.poll() is None:
                    root.after(100, poll)
                else:
                    root.quit()
            root.after(0, poll)
            root.mainloop()
        code = process.wait()
        reader.join(timeout=3)
        with lock:
            return code, "".join(tail)[-5000:]
    finally:
        if process is not None and process.poll() is not None and process.stdout is not None:
            process.stdout.close()
        if root is not None:
            if progress is not None:
                progress.stop()
            root.destroy()


def prepare_runtime(mode: str = "app", *, graphical: bool | None = None) -> int:
    if os.environ.get("REMOTEDESK_AUTO_INSTALL") == "0":
        # Explicit unattended/diagnostic opt-out: never opens dialogs or elevates.
        return 0
    if graphical is None:
        graphical = mode == "app" and bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"))
    try:
        missing = detect_missing(mode)
        if not missing:
            return 0
        summary = "\n".join(f"• {item.label}（{item.package}）" for item in missing)
        try:
            command = install_command(missing)
        except RuntimeError as error:
            raise RuntimeError(str(error) + "\n\n" + summary) from error
        manual = "sudo " + shlex.join(command)
        message = ("RemoteDesk 检测到缺少以下运行组件：\n\n" + summary
            + "\n\n是否从系统软件源安装？随后由系统申请管理员授权。"
              "\n不会以管理员身份运行 RemoteDesk，不修改驱动或软件源。"
              "\n取消后本次不启动，可安装完再打开。\n\n终端安装命令：\n" + manual)
        if not dialog(message, question=True, graphical=graphical):
            print("RemoteDesk 依赖安装未获确认；本次启动已取消。", file=sys.stderr)
            return CANCELLED
        elevated = elevated_command(command, graphical)
        code, output = run_installer(elevated, graphical)
        if code == 126 and elevated[0] == "/usr/bin/pkexec":
            print("RemoteDesk 管理员授权已取消。", file=sys.stderr)
            return CANCELLED
        if code:
            raise RuntimeError(f"依赖安装未完成（退出码 {code}）。\n{output}\n\n可重试：\n{manual}")
        # Re-probe the same interpreter, not system Python. A successful apt
        # transaction does not prove a bundled/venv runtime can import its modules.
        remaining = detect_missing(mode)
        if remaining:
            detail = "\n".join(f"{item.label}: {item.detail}" for item in remaining)
            raise RuntimeError("安装命令已结束，但当前运行环境仍有缺项：\n" + detail
                + "\n请检查包内 Python/虚拟环境与系统包是否兼容；本次不会循环安装。")
        print("RemoteDesk 运行依赖已安装并复检通过。", flush=True)
        return 0
    except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
        message = str(error)
        print(message, file=sys.stderr, flush=True)
        dialog(message, question=False, graphical=graphical)
        return UNAVAILABLE


def main() -> int:
    parser = argparse.ArgumentParser(description="RemoteDesk Linux dependency check / consent-based installation")
    parser.add_argument("--mode", choices=("app", "host"), default="app")
    parser.add_argument("--check", action="store_true", help="Read-only JSON report; never prompts or installs")
    args = parser.parse_args()
    if args.check:
        try:
            missing = detect_missing(args.mode)
            print(json.dumps({"interpreter": sys.executable, "missing": [asdict(item) for item in missing]}, ensure_ascii=False, indent=2))
            return 1 if missing else 0
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
            print(json.dumps({"error": str(error)}, ensure_ascii=False))
            return UNAVAILABLE
    return prepare_runtime(args.mode)


if __name__ == "__main__":
    raise SystemExit(main())
