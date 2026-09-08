"""Current-user host preferences and graphical-login startup (never root/autologin).

The AES key is local and protected by Unix permissions, not a hardware-backed vault.
An attacker already running as this user/root can read it. No system password is saved.
"""
from __future__ import annotations

import base64
import contextlib
import json
import os
from pathlib import Path
import stat
import tempfile

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


MARKER = "X-RemoteDesk-Managed=host-startup-v1"
AAD = b"RemoteDesk/Linux/HostPassword/v1"
FIELDS = ("port", "receive_dir", "capture", "fps", "size", "machine_name", "remember", "armed", "login_start")


def config_home() -> Path:
    value = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    return value if value.is_absolute() else Path.home() / ".config"


def private_directory(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.mkdir(mode=0o700, exist_ok=True)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
        raise PermissionError("RemoteDesk 配置目录必须是本用户专用的 0700 目录，不能是链接。")


def read_private(path: Path, limit: int = 65536) -> bytes:
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise PermissionError("RemoteDesk 私有文件权限不正确。")
        data = source.read(limit + 1)
    if len(data) > limit:
        raise ValueError("RemoteDesk 配置文件过大。")
    return data


def atomic_private_write(path: Path, data: bytes) -> None:
    descriptor, temporary = tempfile.mkstemp(prefix=".remotedesk-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as output:
            os.fchmod(output.fileno(), 0o600)
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)


class HostPreferences:
    def __init__(self, directory: Path | None = None):
        self.directory = Path(directory) if directory is not None else config_home() / "remotedesk" / "host-state"

    def load(self) -> dict:
        if not self.directory.exists() and not self.directory.is_symlink():
            return {}
        private_directory(self.directory)
        try:
            data = json.loads(read_private(self.directory / "settings.json"))
        except FileNotFoundError:
            return {}
        if not isinstance(data, dict) or data.get("version") != 1:
            raise ValueError("无法识别保存的被控设置。")
        values = {key: data[key] for key in FIELDS if key in data}
        for key in ("remember", "armed", "login_start"):
            if key in values and type(values[key]) is not bool:
                raise ValueError("保存的被控状态无效。")
        values["password"] = ""
        encrypted = data.get("encrypted_password", "")
        if values.get("remember") and encrypted:
            key = read_private(self.directory / "key", 32)
            payload = base64.b64decode(encrypted, validate=True)
            values["password"] = AESGCM(key).decrypt(payload[:12], payload[12:], AAD).decode("utf-8")
        values["armed"] = bool(values.get("armed") and values["password"])
        return values

    def save(self, values: dict, password: str) -> None:
        private_directory(self.directory)
        data = {key: values[key] for key in FIELDS if key in values}
        data["version"] = 1
        data["armed"] = bool(data.get("remember") and data.get("armed") and password)
        data["encrypted_password"] = ""
        if data.get("remember") and password:
            try:
                key = read_private(self.directory / "key", 32)
            except FileNotFoundError:
                key = AESGCM.generate_key(bit_length=256)
                # Exclusive creation avoids key replacement if two startup attempts race.
                descriptor = os.open(self.directory / "key", os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                with os.fdopen(descriptor, "wb") as output:
                    output.write(key)
                    output.flush()
                    os.fsync(output.fileno())
            nonce = os.urandom(12)
            data["encrypted_password"] = base64.b64encode(nonce + AESGCM(key).encrypt(nonce, password.encode("utf-8"), AAD)).decode("ascii")
        atomic_private_write(self.directory / "settings.json", json.dumps(data, ensure_ascii=False).encode("utf-8"))
        if not data.get("remember"):
            # Opt-out removes the saved secret and its key, but keeps harmless UI preferences.
            with contextlib.suppress(FileNotFoundError):
                (self.directory / "key").unlink()


def desktop_argument(value: str) -> str:
    if any(character in value for character in "\n\r\x00"):
        raise ValueError("启动路径包含无效字符。")
    # Exec is parsed by the desktop specification, not a shell. Percent is a field code.
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"').replace("`", "\\`").replace("$", "\\$").replace("%", "%%") + '"'


def login_command(python: str, script: str) -> str:
    source = Path(script).resolve()
    # Use the installed wrapper: bundled Python needs its environment, and the
    # per-user launcher/current symlink follows upgrades to a new release directory.
    candidates = [Path.home() / ".local/bin/remotedesk-linux-app"]
    if source == Path("/opt/remotedesk/app/remotedesk_linux_app.py"):
        candidates.append(Path("/usr/bin/remotedesk-linux-app"))
    candidates.append(source.parent.parent / "remotedesk-linux-app")
    if source.name == "remotedesk_linux_app.py":
        for launcher in candidates:
            if not launcher.is_file() or not os.access(launcher, os.X_OK):
                continue
            expected = launcher.resolve().parent / "app/remotedesk_linux_app.py"
            if expected.resolve() == source or (launcher == Path("/usr/bin/remotedesk-linux-app") and source == Path("/opt/remotedesk/app/remotedesk_linux_app.py")):
                return desktop_argument(str(launcher)) + " --autostart"
    return " ".join((desktop_argument(python), desktop_argument(script), "--autostart"))


def set_login_start(enabled: bool, python: str, script: str, directory: Path | None = None) -> None:
    directory = Path(directory) if directory is not None else config_home() / "autostart"
    path = directory / "remotedesk-host.desktop"
    if path.exists() or path.is_symlink():
        if path.is_symlink() or not path.is_file() or MARKER not in path.read_text(encoding="utf-8").splitlines():
            raise ValueError("同名登录启动项不属于 RemoteDesk，未覆盖。")
    if not enabled:
        with contextlib.suppress(FileNotFoundError):
            path.unlink()
        return
    if not Path(python).is_absolute() or not Path(script).is_absolute() or not Path(python).is_file() or not Path(script).is_file():
        raise ValueError("登录启动需要完整、有效的程序路径。")
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    command = login_command(python, script)
    # Backslashes are unescaped once as a desktop string before Exec argument parsing.
    content = ("[Desktop Entry]\nType=Application\nName=RemoteDesk\nTerminal=false\n"
               "Exec=" + command.replace("\\", "\\\\") + "\n" + MARKER + "\n")
    atomic_private_write(path, content.encode("utf-8"))


class AppInstance:
    """Prevents graphical autostart and a manual launch from binding the same port twice."""
    def __init__(self, directory: Path | None = None):
        import fcntl
        directory = Path(directory) if directory is not None else config_home() / "remotedesk" / "host-state"
        private_directory(directory)
        descriptor = os.open(directory / "app.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
        self.file = os.fdopen(descriptor, "r+b")
        try:
            info = os.fstat(descriptor)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
                raise PermissionError("RemoteDesk 进程锁权限不正确。")
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            self.file.close()
            raise

    def close(self) -> None:
        self.file.close()
