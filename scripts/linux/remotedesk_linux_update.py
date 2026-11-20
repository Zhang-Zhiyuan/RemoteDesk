"""Install only a newer RemoteDesk package described by a publisher-signed ticket."""
import asyncio
import base64
import contextlib
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile

APP = Path(__file__).resolve().parent
MAX_SIZE = 256 * 1024 * 1024
VERSION = re.compile(r"(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){2}")


def installed(app=APP):
    try:
        version = (app / "fleet-version").read_text("ascii").strip()
        if VERSION.fullmatch(version) and (app / "fleet-publisher.cer").is_file():
            platform = ("linux-deb-amd64" if app == Path("/opt/remotedesk/app") else
                        "linux-amd64" if (app.parent / "runtime/bin/python").is_file() else "linux-python")
            return platform, version
    except OSError:
        pass
    return "", ""


def verify(ticket, platform, current, cert):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import padding
    version, build, size, digest = (ticket[k] for k in ("version", "build", "size", "sha256"))
    if (ticket["platform"] != platform or not VERSION.fullmatch(version) or not VERSION.fullmatch(current)
            or tuple(map(int, version.split("."))) <= tuple(map(int, current.split(".")))
            or not re.fullmatch(r"[0-9]{14}", build) or not re.fullmatch(r"[0-9a-f]{64}", digest)
            or type(size) is not int or not 0 < size <= MAX_SIZE):
        raise ValueError("更新平台、版本或长度无效。")
    content = "\n".join(("RemoteDesk/fleet/v1", version, build, platform, str(size), digest)).encode("ascii")
    x509.load_der_x509_certificate(cert).public_key().verify(
        base64.b64decode(ticket["signature"], validate=True), content, padding.PKCS1v15(), hashes.SHA256())


async def download(options, ticket, path):
    import remotedesk_linux_relay as relay
    reader, writer = await relay.connect_tls(options)
    try:
        reply = await relay.hello(reader, writer, options, "update-package",
                                 updatePlatform=ticket["platform"], sha256=ticket["sha256"])
        if reply.get("size") != ticket["size"]:
            raise ValueError("更新包长度不符。")
        remaining, digest = ticket["size"], hashlib.sha256()
        with path.open("xb") as dest:
            while remaining:
                data = await asyncio.wait_for(reader.read(min(65536, remaining)), 30)
                if not data:
                    raise EOFError("更新包下载中断。")
                dest.write(data)
                digest.update(data)
                remaining -= len(data)
        if digest.hexdigest() != ticket["sha256"]:
            raise ValueError("更新包 SHA-256 校验失败。")
    finally:
        writer.close()
        with contextlib.suppress(OSError, asyncio.TimeoutError):
            await asyncio.wait_for(writer.wait_closed(), 2)


@contextlib.contextmanager
def activity_lock(exclusive=False):
    """Shared by direct desktop hosts in another process and the GUI updater."""
    if not installed()[0]:
        yield
        return
    import fcntl
    from remotedesk_linux_startup import config_home, private_directory
    directory = config_home() / "remotedesk" / "host-state"
    private_directory(directory)
    fd = os.open(directory / "update-session.lock", os.O_CREAT | os.O_NOFOLLOW | os.O_RDWR, 0o600)
    try:
        fcntl.flock(fd, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
        yield
    finally:
        os.close(fd)


def tree_inventory(root):
    # Only exact shipped contents may be retired. Keep portable installations with custom files intact.
    result = {}
    for folder, dirs, files in os.walk(root, followlinks=False):
        directory = Path(folder)
        for name in dirs + files:
            path = directory / name
            relative = path.relative_to(root).as_posix()
            if path.is_symlink():
                result[relative] = "link:" + os.readlink(path)
            elif path.is_dir():
                if name != "__pycache__" and "__pycache__" not in path.relative_to(root).parts:
                    result[relative] = "dir"
            elif relative == "app/fleet-files.json":
                continue
            elif path.parent.name == "__pycache__" and re.fullmatch(r"[\w.]+\.cpython-\d+(?:\.opt-\d+)?\.pyc", name):
                continue
            elif path.is_file():
                with path.open("rb") as source:
                    digest = hashlib.sha256()
                    for data in iter(lambda: source.read(131072), b""):
                        digest.update(data)
                    result[relative] = digest.hexdigest()
            else:
                raise ValueError("程序目录中有非安装包文件。")
    return result


def extract_portable(package, target):
    total = count = 0
    with tarfile.open(package, "r:gz") as archive:
        for member in archive:
            relative = PurePosixPath(member.name)
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError("包内路径无效。")
            path = target.joinpath(*relative.parts)
            if path == target and member.isdir():
                continue
            count += 1
            total += member.size
            if count > 60000 or total > 1024**3 or any(p.is_symlink() for p in (path, *path.parents)):
                raise ValueError("包内结构无效。")
            if member.isdir():
                path.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                path.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as src, path.open("xb") as dst:
                    shutil.copyfileobj(src, dst)
                path.chmod(0o755 if member.mode & 0o111 else 0o644)
            elif member.issym() and member.linkname not in ("", ".", "..") and "/" not in member.linkname:
                path.symlink_to(member.linkname)
            else:
                raise ValueError("包中存在不支持的文件类型。")


def exchange(old, new):
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.renameat2(-100, os.fsencode(old), -100, os.fsencode(new), 2) != 0:
        raise OSError(ctypes.get_errno(), "系统未能原子替换安装目录。")


def install_portable(package, ticket, idle, app=APP):
    current = app.parent
    if current == Path.home() or current.stat().st_uid != os.geteuid() or current == Path("/") or app.name != "app":
        raise ValueError("请将便携版解压到本用户单独的程序目录后再更新。")
    inventory = json.loads((app / "fleet-files.json").read_text())
    if tree_inventory(current) != inventory:
        raise ValueError("便携版目录含有用户文件或修改，已保留原目录。")
    with tempfile.TemporaryDirectory(prefix=".remotedesk-stage-", dir=current.parent) as staging:
        stage = Path(staging)
        extract_portable(package, stage)
        if installed(stage / "app") != (ticket["platform"], ticket["version"]):
            raise ValueError("便携包版本或架构与签名不符。")
        launcher = "remotedesk-linux-app"
        subprocess.run([str(stage / launcher), "--fleet-check"], check=True, timeout=30,
                       stdout=subprocess.DEVNULL, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
        if not idle():
            raise BlockingIOError("本机正在使用。")
        with activity_lock(exclusive=True):
            exchange(current, stage)
            try:
                if tree_inventory(stage) != inventory:
                    raise ValueError("更新期间原目录有文件变动，已取消替换。")
                subprocess.run([str(current / launcher), "--fleet-check"], check=True, timeout=30,
                               stdout=subprocess.DEVNULL, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
            except BaseException:
                exchange(current, stage)
                raise
        return str(current / launcher)


def install_deb_admin(source):
    # Re-verify from the system installation after polkit authorization. The privileged
    # package manager only sees a copy in its own private directory.
    platform, version = installed()
    if os.geteuid() != 0 or platform != "linux-deb-amd64":
        raise PermissionError("需要系统安装的 RemoteDesk 和管理员授权。")
    line = source.readline(4097)
    if len(line) > 4096 or not line.endswith(b"\n"):
        raise ValueError("更新清单无效。")
    ticket = json.loads(line)
    verify(ticket, platform, version, (APP / "fleet-publisher.cer").read_bytes())
    with tempfile.TemporaryDirectory(prefix="remotedesk-deb-") as directory:
        deb = Path(directory) / "remotedesk.deb"
        digest, left = hashlib.sha256(), ticket["size"]
        with deb.open("xb") as dest:
            while left:
                data = source.read(min(left, 65536))
                if not data:
                    raise EOFError("安装包不完整。")
                dest.write(data)
                digest.update(data)
                left -= len(data)
        if digest.hexdigest() != ticket["sha256"]:
            raise ValueError("安装包验证失败。")
        fields = [subprocess.check_output(["/usr/bin/dpkg-deb", "-f", str(deb), field], text=True).strip()
                  for field in ("Package", "Version", "Architecture")]
        if fields != ["remotedesk-linux-host", ticket["version"], "amd64"]:
            raise ValueError("系统包身份不匹配。")
        subprocess.run(["/usr/bin/dpkg", "--install", str(deb)], check=True)


def apply(options, ticket, idle):
    platform, current = installed()
    verify(ticket, platform, current, (APP / "fleet-publisher.cer").read_bytes())
    with tempfile.TemporaryDirectory(prefix="remotedesk-update-") as folder:
        path = Path(folder) / "package"
        asyncio.run(asyncio.wait_for(download(options, ticket, path), 600))
        if not idle():
            raise BlockingIOError("本机正在使用。")
        if platform != "linux-deb-amd64":
            return install_portable(path, ticket, idle)
        with activity_lock(exclusive=True):
            with subprocess.Popen(["/usr/bin/pkexec", "/usr/bin/python3", "-I", str(APP / "remotedesk_linux_update.py"),
                                   "--install-deb"], stdin=subprocess.PIPE) as installer:
                try:
                    installer.stdin.write(json.dumps(ticket, separators=(",", ":")).encode("ascii") + b"\n")
                    with path.open("rb") as package:
                        shutil.copyfileobj(package, installer.stdin)
                    installer.stdin.close()
                finally:
                    if not installer.stdin.closed:
                        installer.stdin.close()
                if installer.wait() != 0:
                    raise RuntimeError("系统安装未获授权或失败。")
        return "/usr/bin/remotedesk-linux-app"


if __name__ == "__main__" and sys.argv[1:] == ["--install-deb"]:
    install_deb_admin(sys.stdin.buffer)
elif __name__ == "__main__" and len(sys.argv) == 3 and sys.argv[1] == "--inventory":
    root = Path(sys.argv[2]).resolve()
    (root / "app/fleet-files.json").write_text(json.dumps(tree_inventory(root), separators=(",", ":")), encoding="utf-8")
