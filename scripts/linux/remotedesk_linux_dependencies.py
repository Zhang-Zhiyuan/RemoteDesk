#!/usr/bin/env python3
"""Consent-based startup dependency repair; never elevates the RemoteDesk app."""
from __future__ import annotations

import argparse
from collections import deque
from dataclasses import asdict, dataclass
import json
import os
import platform
import re
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


def preflight_install(command: list[str]) -> None:
    """Read the resolver's plan without privilege, downloads or package writes.

    APT simulation is advisory (another package manager may change the state),
    so call it both before consent and immediately before requesting elevation.
    Never repair a machine's sources or turn an app install into a driver update.
    """
    if not command or command[0] != "/usr/bin/apt-get" or "install" not in command:
        raise ValueError("Invalid dependency installation command")
    packages = command[command.index("install") + 1:]
    if not packages or any(package not in ALLOWED_PACKAGES for package in packages):
        raise ValueError("Invalid dependency package list")
    try:
        distribution = platform.freedesktop_os_release()
    except OSError:
        distribution = {}
    system = distribution.get("PRETTY_NAME", distribution.get("ID", "Linux"))
    context = f"当前系统：{system}\n当前 Python：{sys.version_info.major}.{sys.version_info.minor}（{sys.executable}）"
    environment = system_environment()
    environment["LC_ALL"] = "C"  # APT's documented Inst/Remv operations are parsed below.
    try:
        result = subprocess.run([command[0], "--simulate", *command[1:]],
            stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=environment, timeout=20, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeError("安装前检查未完成，尚未申请管理员权限或开始安装。\n"
            + context + "\n请检查系统包管理器后重试。\n" + str(error)) from error
    output = (result.stdout + "\n" + result.stderr).strip()
    if result.returncode:
        raise RuntimeError("系统软件源无法满足 RemoteDesk 的运行依赖，尚未申请管理员权限或开始安装。\n"
            + context + "\n请检查软件源是否对应当前系统版本，以及依赖版本是否冲突；RemoteDesk 不会自动改源、降级系统或修复其他软件。"
            + "\n\nAPT 检查结果：\n" + output[-3000:])
    if re.search(r"(?m)^Remv\s", output):
        raise RuntimeError("安装计划需要删除已有软件，已停止自动安装；请先由系统管理员处理。\n" + context)
    planned = re.findall(r"(?m)^Inst\s+(\S+)\s+([^\n]+)", output)
    if not planned:
        raise RuntimeError("系统包管理器没有可安装的缺项，但当前运行环境仍无法使用这些组件。\n"
            + context + "\n请使用 RemoteDesk 自带启动器，或检查 Python/虚拟环境与系统包是否兼容；本次不会重复申请安装。")
    # Conf may finish a previously unpacked driver, even without a matching Inst.
    changed = re.findall(r"(?m)^(?:Inst|Conf)\s+(\S+)\s+([^\n]+)", output)
    protected = sorted({name for name, _ in changed if name.partition(":")[0].startswith((
        "nvidia-", "libnvidia-", "cuda-", "libcuda", "linux-image-", "linux-modules-", "linux-headers-"))})
    if protected:
        raise RuntimeError("安装计划会改动显卡驱动或内核组件，已停止自动安装。\n" + context
            + "\n涉及：" + ", ".join(protected[:12]) + "\n请由系统管理员处理这些组件后重试。")
    # Check positive Ubuntu-origin evidence in the *selected plan*, not every
    # configured source: unrelated PPAs must not block otherwise healthy installs.
    codename = distribution.get("UBUNTU_CODENAME") or (
        distribution.get("VERSION_CODENAME", "") if distribution.get("ID") == "ubuntu" else "")
    if codename:
        conflicts = []
        for name, detail in planned:
            for version, suite in re.findall(r"(?:^|[ (])Ubuntu:([^/\s]+)/([A-Za-z0-9.+_-]+)", detail):
                if suite != codename and not suite.startswith(codename + "-"):
                    conflicts.append(f"{name}：Ubuntu {version} / {suite}")
        if conflicts:
            raise RuntimeError("软件源中的安装候选包与当前 Ubuntu 版本不匹配，已停止自动安装。\n"
                + context + f"\n当前应使用：{codename}\n" + "\n".join(conflicts[:12])
                + "\n请先由系统管理员检查软件源；RemoteDesk 不会混装其他发行版的组件或自动改源。")


def elevated_command(command: list[str], graphical: bool) -> list[str]:
    if os.geteuid() == 0:
        return command
    if graphical and os.access("/usr/bin/pkexec", os.X_OK):
        # Without a session agent fail explicitly, not an invisible tty prompt.
        return ["/usr/bin/pkexec", "--disable-internal-agent", *command]
    if sys.stdin.isatty() and os.access("/usr/bin/sudo", os.X_OK):
        return ["/usr/bin/sudo", "--", *command]
    raise RuntimeError("无法申请管理员权限：桌面需要 polkit 授权代理，或在终端中启动后使用 sudo。")


def scrollable_dialog(parent, message: str, *, question: bool) -> bool:
    """Long dependency diagnostics must not push consent buttons off screen."""
    import tkinter as tk
    from tkinter import font as tkfont, ttk
    window = tk.Toplevel(parent, name="dependency_details")
    window.withdraw()
    window.title("RemoteDesk · 运行环境")
    window.rowconfigure(0, weight=1)
    window.columnconfigure(0, weight=1)
    content = ttk.Frame(window, name="content", padding=14)
    content.grid(row=0, column=0, sticky="nsew")
    content.rowconfigure(1, weight=1)
    content.columnconfigure(0, weight=1)
    width = min(760, max(1, parent.winfo_screenwidth() - 48))
    height = min(580, max(1, parent.winfo_screenheight() - 96))
    heading = ttk.Label(content, text="安装缺少的运行组件？" if question else "运行环境需要处理",
        wraplength=max(1, width - 40))
    heading.grid(row=0, column=0, sticky="w", pady=(0, 10))
    details = ttk.Frame(content, name="details")
    details.grid(row=1, column=0, sticky="nsew")
    details.rowconfigure(0, weight=1)
    details.columnconfigure(0, weight=1)
    text = tk.Text(details, name="message", wrap="word", height=1, width=1,
        font="TkDefaultFont", padx=8, pady=8, takefocus=True)
    scrollbar = ttk.Scrollbar(details, orient="vertical", command=text.yview)
    text.configure(yscrollcommand=scrollbar.set)
    text.grid(row=0, column=0, sticky="nsew")
    scrollbar.grid(row=0, column=1, sticky="ns")
    text.insert("1.0", message)
    text.configure(state="disabled")
    def select_all(_event):
        text.tag_add("sel", "1.0", "end-1c")
        return "break"
    text.bind("<Control-a>", select_all)
    actions = ttk.Frame(content, name="actions")
    actions.grid(row=2, column=0, sticky="ew", pady=(12, 0))
    accepted = False
    def finish(value=False):
        nonlocal accepted
        accepted = value
        window.destroy()
    cancel = ttk.Button(actions, name="dismiss", text="取消" if question else "知道了", width=0, command=finish)
    buttons = [cancel]
    if question:
        buttons.append(ttk.Button(actions, name="install", text="安装依赖", width=0, command=lambda: finish(True)))
    vertical = None
    def arrange(event=None):
        nonlocal vertical
        if event is not None and event.widget is not window:
            return
        available = max(1, window.winfo_width() - 28)
        heading.configure(wraplength=available)
        stack = sum(button.winfo_reqwidth() for button in buttons) + 8 * (len(buttons) - 1) > available
        if vertical != stack:
            vertical = stack
            for button in buttons:
                button.pack_forget()
            for index, button in enumerate(buttons):
                if stack:
                    button.pack(side="top", fill="x", pady=(0, 6 if index + 1 < len(buttons) else 0))
                else:
                    button.pack(side="right", padx=(0, 8 if index else 0))
        button_height = (sum(button.winfo_reqheight() for button in buttons) + 6 * (len(buttons) - 1)
                         if stack else max(button.winfo_reqheight() for button in buttons))
        needed = heading.winfo_reqheight() + button_height + 3 * tkfont.nametofont("TkDefaultFont").metrics("linespace") + 60
        # Stacking cannot help if even a single action is wider than the window.
        # Account for the actual font/DPI, rather than only a fixed pixel minimum.
        minimum_width = max(380, max(button.winfo_reqwidth() for button in buttons) + 28)
        window.minsize(min(width, minimum_width), min(height, max(260, needed)))
    window.minsize(min(380, width), min(260, height))
    x = max(0, (parent.winfo_screenwidth() - width) // 2)
    y = max(0, (parent.winfo_screenheight() - height) // 2)
    window.geometry(f"{width}x{height}+{x}+{y}")
    window.bind("<Configure>", arrange)
    window.protocol("WM_DELETE_WINDOW", finish)
    window.bind("<Escape>", lambda _event: finish())
    window.deiconify()
    window.update_idletasks()
    window.grab_set()
    cancel.focus_set()  # Long installation prompts never default to acceptance.
    parent.wait_window(window)
    return accepted


def dialog(message: str, *, question: bool, graphical: bool) -> bool:
    if graphical:
        root = None
        try:
            import tkinter as tk
            from tkinter import messagebox
            root = tk.Tk(className="RemoteDeskDependencies")
            root.withdraw()
            if len(message) > 600 or message.count("\n") > 12:
                return scrollable_dialog(root, message, question=question)
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
            preflight_install(command)
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
        preflight_install(command)
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
    checks = parser.add_mutually_exclusive_group()
    checks.add_argument("--check", action="store_true", help="Read-only JSON report; never prompts or installs")
    checks.add_argument("--check-install", action="store_true",
                        help="Read-only dependency and APT plan report; never prompts, elevates or installs")
    args = parser.parse_args()
    if args.check or args.check_install:
        try:
            missing = detect_missing(args.mode)
            report = {"interpreter": sys.executable, "missing": [asdict(item) for item in missing]}
            if args.check_install:
                try:
                    if missing:
                        preflight_install(install_command(missing))
                    report["installable"] = True
                except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
                    report.update(installable=False, error=str(error))
                    print(json.dumps(report, ensure_ascii=False, indent=2))
                    return UNAVAILABLE
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 1 if missing else 0
        except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired) as error:
            print(json.dumps({"error": str(error)}, ensure_ascii=False))
            return UNAVAILABLE
    return prepare_runtime(args.mode)


if __name__ == "__main__":
    raise SystemExit(main())
