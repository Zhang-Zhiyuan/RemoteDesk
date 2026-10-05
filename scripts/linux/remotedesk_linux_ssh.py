"""SSH gate v1 and an owned native-OpenSSH terminal adapter (no desktop input)."""
from __future__ import annotations

import asyncio
import contextlib
from dataclasses import replace
import fnmatch
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import shutil
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import uuid

MAGIC = b"RDS1"
HOST_DEADLINE = 10
CLIENT_DEADLINE = 20
MAX_HOST_SESSIONS = 4
ALIAS_MARKER = "# RemoteDesk SSH aliases v1"


class AliasConfigurationError(ValueError):
    """Local, credential-free configuration error suitable for stderr/UI."""


def gate_proof(device_key, role, device_id, nonce):
    if role not in ("server", "client") or len(nonce) != 32:
        raise ValueError("SSH 门禁参数无效。")
    if not device_key or len(device_key.encode("utf-8")) > 16384:
        raise ValueError("请设置非空设备密钥；不是 SSH 登录密码。")
    canonical = str(uuid.UUID(device_id))
    if canonical != device_id:
        raise ValueError("SSH 设备身份无效。")
    key = hashlib.sha256(device_key.encode("utf-8")).digest()
    message = ("RemoteDesk SSH gate v1\0" + role + "\0" + canonical).encode("utf-8") + nonce
    return hmac.new(key, message, hashlib.sha256).digest()


async def accept_gate(reader, writer, device_id, device_key, local_port, connect=None):
    """Return a loopback SSH connection ONLY after mutual device authentication."""
    if type(local_port) is not int or not 1 <= local_port <= 65535:
        raise ValueError("本机 SSH 端口无效。")
    async def handshake():
        nonce = secrets.token_bytes(32)
        writer.write(MAGIC + nonce + device_id.encode("ascii") + gate_proof(device_key, "server", device_id, nonce))
        await writer.drain()
        received = await reader.readexactly(32)
        if not hmac.compare_digest(received, gate_proof(device_key, "client", device_id, nonce)):
            writer.write(b"\0")
            await writer.drain()
            raise PermissionError("SSH 设备密钥验证失败。")
        local = None
        try:
            # Neither the viewer nor relay supplies a target address or port.
            dial = connect or asyncio.open_connection
            try:
                local = await dial("127.0.0.1", local_port)
            except OSError:
                local = await dial("::1", local_port)
        except OSError:
            writer.write(b"\2")
            await writer.drain()
            raise ConnectionError("设备已验证，但本机 SSH 服务不可达。") from None
        try:
            writer.write(b"\1")
            await writer.drain()
            return local
        except BaseException:
            local[1].close()
            raise
    return await asyncio.wait_for(handshake(), HOST_DEADLINE)


async def authenticate_gate(reader, writer, device_id, device_key):
    greeting = await reader.readexactly(104)
    if greeting[:4] != MAGIC or greeting[36:72] != device_id.encode("ascii"):
        raise PermissionError("SSH 目标身份不匹配；未发送 SSH 凭据。")
    nonce = greeting[4:36]
    expected = gate_proof(device_key, "server", device_id, nonce)
    if not hmac.compare_digest(greeting[72:], expected):
        raise PermissionError("SSH 目标设备密钥不匹配；未发送 SSH 凭据。")
    writer.write(gate_proof(device_key, "client", device_id, nonce))
    await writer.drain()
    result = await reader.readexactly(1)
    if result == b"\2":
        raise ConnectionError("设备已验证，但本机 SSH 服务不可达。")
    if result != b"\1":
        raise PermissionError("SSH 设备门禁拒绝连接。")


async def connect_ssh(options, device_key):
    import remotedesk_linux_relay as relay
    async def connect():
        reader, writer = await relay.connect_tls(options)
        try:
            result = await relay.hello(reader, writer, options, "ssh-viewer", deviceId=options.device_id)
            if result.get("type") != "paired" or result.get("service") != "ssh":
                raise PermissionError("中继不支持独立 SSH 通道；未回退到桌面。")
            await authenticate_gate(reader, writer, options.device_id, device_key)
            return reader, writer
        except BaseException:
            await relay.close_writer(writer, abort=True)
            raise
    return await asyncio.wait_for(connect(), CLIENT_DEADLINE)


class GateVerification:
    """Cancellable, bounded preflight; never writes configuration or starts Tk."""
    def __init__(self, options, device_key, completed):
        self.options, self.device_key, self.completed = options, device_key, completed
        self.cancelled, self.done = threading.Event(), threading.Event()
        self.loop = self.task = self.thread = None

    def start(self):
        self.thread = threading.Thread(target=self._worker, name="RemoteDesk SSH alias verification", daemon=True)
        self.thread.start()

    def close(self):
        self.cancelled.set()
        if self.loop is not None and self.task is not None:
            with contextlib.suppress(RuntimeError):
                self.loop.call_soon_threadsafe(self.task.cancel)

    async def _run(self):
        import remotedesk_linux_relay as relay
        self.loop, self.task = asyncio.get_running_loop(), asyncio.current_task()
        if self.cancelled.is_set():
            raise asyncio.CancelledError
        _reader, writer = await connect_ssh(self.options, self.device_key)
        try:
            if self.cancelled.is_set():
                raise asyncio.CancelledError
        finally:
            await relay.close_writer(writer, abort=True)

    def _worker(self):
        verified = False
        try:
            asyncio.run(self._run())
            verified = not self.cancelled.is_set()
        except (Exception, asyncio.CancelledError):
            pass
        finally:
            self.device_key = ""
            self.done.set()
            # Only a boolean crosses into the UI. Peer errors are not reflected
            # into logs/dialogs and the old remembered key is never changed here.
            self.completed(verified)


def host_key_alias(options):
    options = options.validate()
    return "remotedesk-" + options.tls_certificate_sha256.lower() + "-" + options.device_id + ":ssh"


def validate_username(username):
    if not isinstance(username, str) or not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.@\\-]{0,127}", username):
        raise ValueError("请输入有效的目标 SSH 用户名。")
    return username


def ssh_config_directory():
    from remotedesk_linux_startup import config_home
    return config_home() / "remotedesk" / "ssh"


def _load_logins(directory):
    from remotedesk_linux_startup import read_private
    try:
        value = json.loads(read_private(directory / "logins.json"))
    except FileNotFoundError:
        return {}
    if not isinstance(value, dict) or value.get("version") != 1 or not isinstance(value.get("logins"), dict):
        raise ValueError("SSH 用户名记录格式无效；原文件未改动。")
    logins = value["logins"]
    if len(logins) > 50 or any(not isinstance(scope, str) or not re.fullmatch(
            r"remotedesk-[0-9a-f]{64}-[0-9a-f-]{36}:ssh", scope) for scope in logins):
        raise ValueError("SSH 用户名记录格式无效；原文件未改动。")
    for username in logins.values():
        validate_username(username)
    return logins


def load_username(options, directory=None):
    return _load_logins(Path(directory) if directory is not None else ssh_config_directory()).get(host_key_alias(options), "")


def save_username(options, username, directory=None):
    from remotedesk_linux_startup import private_directory, atomic_private_write
    validate_username(username)
    directory = Path(directory) if directory is not None else ssh_config_directory()
    private_directory(directory)
    logins = _load_logins(directory)
    scope = host_key_alias(options)
    logins.pop(scope, None)
    logins[scope] = username
    while len(logins) > 50:
        del logins[next(iter(logins))]
    atomic_private_write(directory / "logins.json", json.dumps(dict(version=1, logins=logins), ensure_ascii=False).encode("utf-8"))


def ssh_arguments(options, username, proxy_port, token_path, known_hosts, identity_file=None):
    validate_username(username)
    executable = shutil.which("ssh")
    if not executable:
        raise FileNotFoundError("未找到系统 OpenSSH 客户端，请安装 openssh-client。")
    proxy = shlex.join([sys.executable, str(Path(__file__).resolve()), "--proxy", str(proxy_port), str(token_path)]).replace("%", "%%")
    # OpenSSH parses -o values as ssh_config, independently of argv quoting.
    records = _config_quote(known_hosts, expand_tokens=True)
    identity_args = []
    if identity_file:
        identity = str(Path(identity_file).expanduser().absolute())
        if any(ord(char) < 32 or ord(char) == 127 for char in identity):
            raise ValueError("SSH 私钥路径不能包含控制字符。")
        identity_args = ["-i", identity, "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none"]
    return [executable, "-F", "/dev/null", "-tt", *identity_args, "-o", "ProxyCommand=" + proxy,
            "-o", "HostKeyAlias=" + host_key_alias(options), "-o", "UserKnownHostsFile=" + records,
            "-o", "GlobalKnownHostsFile=/dev/null", "-o", "StrictHostKeyChecking=ask", "-o", "CheckHostIP=no",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "EscapeChar=none",
            "-o", "PermitLocalCommand=no", "-o", "ClearAllForwardings=yes", "-o", "ServerAliveInterval=20",
            "-o", "ServerAliveCountMax=3", "-l", username, "--", "remotedesk.invalid"]


def terminal_arguments(command):
    # Real terminal emulators supply a PTY and VT handling. No shell interpolation.
    for name, prefix in (("xterm", ["-T", "RemoteDesk SSH", "-e"]),
                         ("gnome-terminal", ["--wait", "--"]),
                         ("konsole", ["--separate", "--nofork", "-e"])):
        executable = shutil.which(name)
        if executable:
            return [executable, *prefix, *command]
    raise FileNotFoundError("需要系统真实终端：xterm、gnome-terminal 或 konsole。未自动安装。")


class TerminalSession:
    """One native terminal, one authenticated local adapter, one relay SSH bridge."""
    def __init__(self, options, device_key, username, status=lambda value: None, config_directory=None, identity_file=None):
        self.options, self.device_key, self.username = options.validate(), device_key, username
        gate_proof(device_key, "client", self.options.device_id, b"\0" * 32)
        self.status = status
        self.config_directory = Path(config_directory) if config_directory else ssh_config_directory()
        self.identity_file = identity_file
        self.cancelled, self.done = threading.Event(), threading.Event()
        self.loop = self.task = self.thread = self.process = None

    def start(self):
        if self.thread is not None:
            raise RuntimeError("SSH 会话不能重复启动。")
        # Fail synchronously before making any relay connection.
        terminal_arguments(ssh_arguments(self.options, self.username, 1, Path("unused"),
                                         self.config_directory / "known_hosts", self.identity_file))
        self.thread = threading.Thread(target=self._worker, name="RemoteDeskSshTerminal", daemon=True)
        self.thread.start()

    def close(self):
        self.cancelled.set()
        if self.loop is not None and self.task is not None:
            with contextlib.suppress(RuntimeError):
                self.loop.call_soon_threadsafe(self.task.cancel)

    def _status(self, text):
        with contextlib.suppress(Exception):
            self.status(text)

    def _worker(self):
        try:
            asyncio.run(self._run())
        except asyncio.CancelledError:
            self._status("SSH 已断开；桌面会话不受影响。")
        except (PermissionError, ConnectionError, FileNotFoundError, ValueError) as error:
            # These messages originate locally. Do not reflect arbitrary peer output.
            self._status(str(error) if type(error) in (PermissionError, ConnectionError, FileNotFoundError, ValueError)
                         else "SSH 连接失败，请检查设备密钥、服务与中继状态。")
        except Exception:
            self._status("SSH 连接失败或超时；未改变桌面会话。")
        finally:
            self.device_key = ""
            self.done.set()

    async def _run(self):
        import remotedesk_linux_relay as relay
        from remotedesk_linux_startup import private_directory
        self.loop, self.task = asyncio.get_running_loop(), asyncio.current_task()
        if self.cancelled.is_set():
            raise asyncio.CancelledError
        private_directory(self.config_directory)
        known_hosts = self.config_directory / "known_hosts"
        fd = os.open(known_hosts, os.O_CREAT | os.O_APPEND | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "ab") as records:
            info = os.fstat(records.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
                raise PermissionError("SSH 主机身份记录必须是当前用户的私有文件。")
        with tempfile.TemporaryDirectory(prefix="remotedesk-ssh-") as directory:
            identity_file = None
            if self.identity_file:
                # OpenSSH expands %h and other tokens in IdentityFile, even
                # when -i arrives as an argv element. A private, owned alias
                # preserves the selected literal path without copying its key.
                identity_file = Path(directory) / "selected-identity"
                identity_file.symlink_to(Path(self.identity_file).expanduser().absolute())
            token = secrets.token_bytes(32)
            token_path = Path(directory) / "proxy-token"
            fd = os.open(token_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "wb") as output:
                output.write(token)
            finished, accepted = asyncio.Event(), asyncio.Event()
            handlers = set()
            connection_error = []
            async def proxy(reader, writer):
                current = asyncio.current_task()
                handlers.add(current)
                remote = None
                owns_session = False
                try:
                    if len(handlers) > 4 or accepted.is_set():
                        return
                    proof = await asyncio.wait_for(reader.readexactly(32), 3)
                    if not hmac.compare_digest(proof, token) or accepted.is_set():
                        return
                    accepted.set()
                    owns_session = True
                    server.close()
                    self._status("正在验证 SSH 设备门禁…")
                    remote = await connect_ssh(self.options, self.device_key)
                    self.device_key = ""
                    self._status("SSH 通道已建立；请在系统终端核验主机指纹并登录。桌面仅观看不限制 SSH 权限。")
                    await relay.bridge((reader, writer), remote)
                except asyncio.CancelledError:
                    raise
                except Exception as error:
                    if owns_session:
                        connection_error.append(error)
                finally:
                    await relay.close_writer(writer, abort=True)
                    if remote:
                        await relay.close_writer(remote[1], abort=True)
                    handlers.discard(current)
                    if owns_session:
                        finished.set()
            server = await asyncio.start_server(proxy, "127.0.0.1", 0, limit=16384)
            try:
                port = server.sockets[0].getsockname()[1]
                argv = terminal_arguments(ssh_arguments(self.options, self.username, port, token_path, known_hosts, identity_file))
                self.process = subprocess.Popen(argv, start_new_session=True)
                self._status("已打开系统 SSH 终端；首次连接需要在终端确认目标指纹。")
                async def wait_or_terminal_exit(event):
                    while not event.is_set():
                        if self.process.poll() is not None:
                            return False
                        with contextlib.suppress(asyncio.TimeoutError):
                            await asyncio.wait_for(event.wait(), .2)
                    return True
                if not await asyncio.wait_for(wait_or_terminal_exit(accepted), 30):
                    return
                await wait_or_terminal_exit(finished)
                if connection_error:
                    raise connection_error[0]
                self._status("SSH 终端已结束；桌面会话不受影响。")
            finally:
                server.close()
                for handler in tuple(handlers):
                    handler.cancel()
                await asyncio.gather(*tuple(handlers), return_exceptions=True)
                # Python 3.12 waits for accepted connections as well as the
                # listener. Close owned handlers first, otherwise cancel hangs.
                await server.wait_closed()
                process = self.process
                if process is not None and process.poll() is None:
                    with contextlib.suppress(ProcessLookupError):
                        os.killpg(process.pid, signal.SIGTERM)
                    # Reap off the Tk thread, with a bounded fallback.
                    try:
                        process.wait(timeout=2)
                    except subprocess.TimeoutExpired:
                        with contextlib.suppress(ProcessLookupError):
                            os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=2)


def proxy_main(port, token_file):
    """OpenSSH ProxyCommand; its stdin/stdout are SSH bytes, never UI text."""
    fd = os.open(token_file, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise PermissionError("Private SSH adapter token required")
        token = source.read(33)
    if len(token) != 32:
        raise ValueError("Invalid SSH adapter token")
    with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
        connection.sendall(token)
        connection.settimeout(None)
        def upload():
            try:
                while data := os.read(0, 16384):
                    connection.sendall(data)
            except OSError:
                pass
            finally:
                with contextlib.suppress(OSError):
                    connection.shutdown(socket.SHUT_WR)
        threading.Thread(target=upload, daemon=True).start()
        while data := connection.recv(16384):
            offset = 0
            while offset < len(data):
                offset += os.write(1, data[offset:])


def validate_alias(alias):
    if not isinstance(alias, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", alias):
        raise AliasConfigurationError("SSH 别名只允许 1–64 个 ASCII 字母、数字、下划线和短横线，首位须为字母或数字。")
    # OpenSSH Host patterns are case-sensitive. Canonicalize the displayed and
    # stored command while checking ownership case-insensitively.
    return alias.lower()


def _alias_paths(directory=None, ssh_directory=None):
    root = Path(directory) if directory is not None else ssh_config_directory()
    ssh_root = Path(ssh_directory) if ssh_directory is not None else Path.home() / ".ssh"
    return root.absolute(), ssh_root.absolute()


def _alias_profiles(directory):
    from remotedesk_linux_startup import read_private
    try:
        value = json.loads(read_private(directory / "aliases.json"))
    except FileNotFoundError:
        return []
    if not isinstance(value, dict) or value.get("version") != 1 or not isinstance(value.get("profiles"), list) or len(value["profiles"]) > 50:
        raise AliasConfigurationError("SSH 别名记录无效；未修改现有配置。")
    profiles, aliases, ids = value["profiles"], set(), set()
    for item in profiles:
        fields = {"id", "alias", "user", "deviceId", "relayScope", "hostKeyAlias"}
        if not isinstance(item, dict) or set(item) not in (fields, fields | {"identityFile"}):
            raise AliasConfigurationError("SSH 别名记录无效；未修改现有配置。")
        validate_alias(item["alias"])
        validate_username(item["user"])
        for field in ("id", "deviceId"):
            if not isinstance(item[field], str) or str(uuid.UUID(item[field])) != item[field]:
                raise AliasConfigurationError("SSH 别名的设备身份无效。")
        if not isinstance(item["relayScope"], str) or not isinstance(item["hostKeyAlias"], str) or not re.fullmatch(r"[0-9a-f]{64}", item["relayScope"]) or not re.fullmatch(
                r"remotedesk-[0-9a-f]{64}-" + re.escape(item["deviceId"]) + r":ssh", item["hostKeyAlias"]):
            raise AliasConfigurationError("SSH 别名的中继身份无效。")
        if item["alias"].lower() in aliases or item["id"] in ids:
            raise AliasConfigurationError("SSH 别名记录重复；未修改现有配置。")
        identity = item.get("identityFile")
        if identity is not None:
            if not isinstance(identity, str) or not Path(identity).is_absolute():
                raise AliasConfigurationError("SSH 别名的私钥路径无效。")
            _config_quote(identity)
        aliases.add(item["alias"].lower()); ids.add(item["id"])
    return profiles


def _config_quote(value, expand_tokens=False):
    value = str(value)
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        raise AliasConfigurationError("SSH 配置路径不能包含控制字符。")
    if expand_tokens:
        if "${" in value:
            raise AliasConfigurationError("SSH 身份文件路径含 ${，OpenSSH 会自行展开环境变量；请使用不含该序列的路径。")
        value = value.replace("%", "%%")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def _alias_fragment(profiles, directory):
    # Pin the private configuration location, not a credential, so an ordinary
    # shell need not inherit the GUI's XDG_CONFIG_HOME environment.
    xdg = directory.parent.parent
    lines = [ALIAS_MARKER]
    for item in profiles:
        source = Path(__file__).resolve()
        launch = [sys.executable, str(source), "--stdio", item["id"]]
        if source.parent.name == "app" and (source.parent / "remotedesk_linux_runtime.sh").is_file():
            # The bundled interpreter requires its private runtime environment.
            # Reuse the packaged, silent-on-stdout launcher without starting Tk.
            launch = ["sh", "-c", '. "$1/app/remotedesk_linux_runtime.sh"; remotedesk_python "$1" "$1/app/remotedesk_linux_ssh.py" --stdio "$2"',
                      "remotedesk-ssh", str(source.parent.parent), item["id"]]
        command = shlex.join(["env", "XDG_CONFIG_HOME=" + str(xdg), *launch]).replace("%", "%%")
        lines.extend(["", "Host " + item["alias"], "  HostName " + item["id"] + ".remotedesk.invalid", "  User " + item["user"],
            "  HostKeyAlias " + item["hostKeyAlias"], "  UserKnownHostsFile " + _config_quote(directory / "known_hosts", True),
            "  StrictHostKeyChecking ask", "  CheckHostIP no", "  ForwardAgent no", "  ForwardX11 no",
            "  ControlMaster no", "  ControlPath none", "  CanonicalizeHostname no", "  PermitLocalCommand no", "  ProxyUseFdpass no",
            "  ServerAliveInterval 30", "  ServerAliveCountMax 3", "  ProxyCommand " + command])
        if item.get("identityFile"):
            lines.extend(["  IdentityFile " + _config_quote(item["identityFile"], True), "  IdentitiesOnly yes"])
    lines.extend(["", "Host *", ""])
    return "\n".join(lines).encode("utf-8")


def _include_prefix(fragment):
    return (ALIAS_MARKER + " begin\nInclude " + _config_quote(fragment) +
            "\nHost *\n" + ALIAS_MARKER + " end\n").encode("utf-8")


def _read_user_config(path):
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    except FileNotFoundError:
        return None, 0o600
    with os.fdopen(fd, "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o022:
            raise AliasConfigurationError("现有 SSH 配置必须由当前用户拥有且不可被其他用户写入；未更改权限。")
        data = stream.read(262145)
    if len(data) > 262144:
        raise AliasConfigurationError("现有 SSH 配置过大，无法安全自动合并。")
    return data, stat.S_IMODE(info.st_mode)


def _check_alias_conflicts(body, alias):
    try:
        text = body.decode("utf-8-sig")
        for line in text.splitlines():
            tokens = shlex.split(line, comments=True)
            if not tokens:
                continue
            # ssh_config accepts Keyword=value as well as Keyword value.
            if "=" in tokens[0]:
                keyword, value = tokens.pop(0).split("=", 1)
                tokens = [keyword, value, *tokens]
            keyword = tokens[0].lower()
            values = [value for value in tokens[1:] if value != "="]
            if keyword in ("include", "match"):
                raise AliasConfigurationError("现有 SSH 配置包含 Include/Match，无法安全自动合并。请先手动审阅配置；未执行 Match exec，也未修改配置。")
            if keyword != "host":
                continue
            if not values:
                raise AliasConfigurationError("现有 Host 规则无效；未修改配置。")
            negatives = [pattern[1:] for pattern in values if pattern.startswith("!")]
            excluded = any(fnmatch.fnmatchcase(alias.lower(), pattern.lower()) for pattern in negatives)
            matching = any(pattern != "*" and not pattern.startswith("!") and
                           fnmatch.fnmatchcase(alias.lower(), pattern.lower()) for pattern in values)
            if matching and not excluded:
                raise AliasConfigurationError("此别名已匹配现有 Host 规则。请选择其他别名，或自行审阅冲突规则；不会接管现有连接。")
    except (UnicodeError, ValueError) as error:
        if isinstance(error, AliasConfigurationError):
            raise
        raise AliasConfigurationError("现有 SSH 配置无法安全解析；请手动审阅，原文件未改动。") from None


def _replace_user_config(path, data, expected, mode):
    # Optimistic compare is in addition to our cooperative private lock. Never
    # overwrite an editor's changes detected since the original snapshot.
    if _read_user_config(path)[0] != expected:
        raise AliasConfigurationError("SSH 配置已被其他程序修改，请重新尝试；未覆盖外部修改。")
    fd, temporary = tempfile.mkstemp(prefix=".remotedesk-config-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(data); stream.flush(); os.fsync(stream.fileno())
        if _read_user_config(path)[0] != expected:
            raise AliasConfigurationError("SSH 配置已被其他程序修改，请重新尝试；未覆盖外部修改。")
        os.replace(temporary, path)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)


def configure_terminal_alias(alias, username, options=None, device_key=None, *, remove=False, identity_file=None, directory=None, ssh_directory=None):
    """Own only a marked Include and fragment; retain the user's body byte-for-byte."""
    import fcntl
    import remotedesk_linux_relay as relay
    from remotedesk_linux_startup import private_directory, read_private, atomic_private_write
    alias = validate_alias(alias)
    if not remove:
        validate_username(username)
        options = options.validate()
        if identity_file:
            identity_file = str(Path(identity_file).expanduser().absolute())
            _config_quote(identity_file)
            if not Path(identity_file).is_file():
                raise AliasConfigurationError("请选择存在的 SSH 私钥文件；只记住文件路径，不保存私钥内容或口令。")
    directory, ssh_root = _alias_paths(directory, ssh_directory)
    # OpenSSH versions disagree on percent expansion in Include paths. Never
    # generate a config that could start resolving a different file on upgrade.
    if any(character in str(ssh_root) for character in "%*?[]") or "${" in str(ssh_root):
        raise AliasConfigurationError("SSH 配置目录路径含 %、通配符或 ${，OpenSSH 的 Include 会自行展开路径；请手动配置，未修改文件。")
    private_directory(directory)
    private_directory(ssh_root)
    lock_fd = os.open(directory / "aliases.lock", os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_fd, "rb") as lock:
        info = os.fstat(lock.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise AliasConfigurationError("SSH 别名锁文件不安全。")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        profiles = _alias_profiles(directory)
        existing = next((item for item in profiles if item["alias"].lower() == alias.lower()), None)
        if remove and existing is None:
            raise AliasConfigurationError("不存在这个 RemoteDesk 受管别名；不会修改用户自建 Host。")
        if not remove and existing is not None and (existing["deviceId"] != options.device_id or existing["relayScope"] != relay.device_key_scope(options)):
            raise AliasConfigurationError("此受管别名已绑定另一设备或中继；请先明确移除旧别名，再重新配置。")
        if not remove and existing is None and len(profiles) >= 50:
            raise AliasConfigurationError("最多配置 50 个终端 SSH 别名，请先移除一个。")
        path, fragment = ssh_root / "config", ssh_root / "remotedesk_aliases.conf"
        original, mode = _read_user_config(path)
        prefix = _include_prefix(fragment)
        raw = original or b""
        body = raw[len(prefix):] if raw.startswith(prefix) else raw
        if ALIAS_MARKER.encode() in body or (profiles and not raw.startswith(prefix)):
            raise AliasConfigurationError("RemoteDesk 受管 Include 已被修改或移动，请手动审阅；未覆盖现有配置。")
        try:
            old_fragment = read_private(fragment, 262144)
        except FileNotFoundError:
            old_fragment = None
        if old_fragment is not None and old_fragment != _alias_fragment(profiles, directory):
            raise AliasConfigurationError("受管 SSH 片段已被修改或文件名被占用；不会覆盖。")
        if profiles and old_fragment is None:
            raise AliasConfigurationError("受管 SSH 片段缺失，请先审阅现有配置。")
        if not remove:
            _check_alias_conflicts(body, alias)
        updated = [item for item in profiles if item is not existing]
        if not remove:
            updated.append(dict(id=existing["id"] if existing else str(uuid.uuid4()), alias=alias, user=username,
                deviceId=options.device_id, relayScope=relay.device_key_scope(options), hostKeyAlias=host_key_alias(options)))
            if identity_file:
                updated[-1]["identityFile"] = identity_file
        new_fragment = _alias_fragment(updated, directory)
        if len(new_fragment) > 262144:
            raise AliasConfigurationError("受管 SSH 片段过大，请缩短配置目录路径或移除部分别名。")
        profile_data = json.dumps(dict(version=1, profiles=updated), ensure_ascii=False).encode("utf-8")
        if len(profile_data) > 65536:
            raise AliasConfigurationError("SSH 别名记录超过 64 KiB 安全上限，请缩短私钥路径或移除部分别名；现有配置和设备密钥未修改。")
        config_data = (prefix if updated else b"") + body
        if len(config_data) > 262144:
            raise AliasConfigurationError("加入受管 Include 后 SSH 配置超过 256 KiB 安全上限，请先精简原配置；现有配置和设备密钥未修改。")
        profile_path = directory / "aliases.json"
        try:
            old_profiles = read_private(profile_path)
        except FileNotFoundError:
            old_profiles = None
        backup = None
        if original is not None:
            backup = ssh_root / ("config.remotedesk-backup-" + uuid.uuid4().hex)
            fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "wb") as stream:
                stream.write(original); stream.flush(); os.fsync(stream.fileno())
        if not remove:
            # References the existing encrypted per-device store; the alias JSON
            # and OpenSSH configuration never contain tokens or device keys.
            if device_key is not None:
                gate_proof(device_key, "client", options.device_id, b"\0" * 32)
                relay.save_device_key(options, device_key)
            elif not relay.load_device_key(options):
                raise AliasConfigurationError("请先在 RemoteDesk 记住所选设备的设备密钥。")
            known_hosts = directory / "known_hosts"
            try:
                fd = os.open(known_hosts, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                os.close(fd)
            except FileExistsError:
                read_private(known_hosts)
        try:
            atomic_private_write(profile_path, profile_data)
            atomic_private_write(fragment, new_fragment)
            _replace_user_config(path, config_data, original, mode)
        except BaseException:
            for target, snapshot in ((profile_path, old_profiles), (fragment, old_fragment)):
                if snapshot is None:
                    with contextlib.suppress(FileNotFoundError): target.unlink()
                else:
                    atomic_private_write(target, snapshot)
            raise
        return dict(alias=alias, backup=str(backup) if backup else None, config=str(path), removed=remove,
                    profileId=None if remove else updated[-1]["id"])


def load_stdio_profile(profile_id):
    import remotedesk_linux_relay as relay
    from remotedesk_linux_startup import read_private
    if not isinstance(profile_id, str) or str(uuid.UUID(profile_id)) != profile_id:
        raise AliasConfigurationError("SSH 别名 ID 无效。")
    profile = next((item for item in _alias_profiles(ssh_config_directory()) if item["id"] == profile_id), None)
    if profile is None:
        raise AliasConfigurationError("终端 SSH 别名不存在或已撤销，请在 RemoteDesk 重新配置。")
    value = json.loads(read_private(relay.settings_path()))
    if not isinstance(value, dict):
        raise AliasConfigurationError("RemoteDesk 已退出中继，请先重新登录。")
    options = replace(relay.RelayOptions.from_dict(value), device_id=profile["deviceId"])
    if relay.device_key_scope(options) != profile["relayScope"] or host_key_alias(options) != profile["hostKeyAlias"]:
        raise AliasConfigurationError("当前登录的中继身份与此别名不符；未发送设备或 SSH 凭据。")
    key = relay.load_device_key(options)
    if not key:
        raise AliasConfigurationError("缺少设备密钥，请在 RemoteDesk 重新配置终端 SSH 别名。")
    return options, key


class _StdioOutputProtocol(asyncio.streams.FlowControlMixin):
    """Await actual pipe drain on close, not just the high-water threshold."""
    def __init__(self):
        super().__init__()
        self.closed = asyncio.get_running_loop().create_future()

    def connection_lost(self, error):
        super().connection_lost(error)
        if not self.closed.done():
            self.closed.set_result(None)


async def stdio_proxy(profile_id):
    import remotedesk_linux_relay as relay
    options, key = load_stdio_profile(profile_id)
    reader, writer = await connect_ssh(options, key)
    key = ""
    loop = asyncio.get_running_loop()
    input_transport = output_transport = None
    tasks = []
    flush_output = False
    try:
        incoming = asyncio.StreamReader(limit=65536)
        input_transport, _ = await loop.connect_read_pipe(lambda: asyncio.StreamReaderProtocol(incoming), sys.stdin.buffer)
        output_transport, protocol = await loop.connect_write_pipe(_StdioOutputProtocol, sys.stdout.buffer)
        outgoing = asyncio.StreamWriter(output_transport, protocol, None, loop)
        output_transport.set_write_buffer_limits(high=65536, low=16384)
        async def copy(source, target):
            while data := await source.read(16384):
                target.write(data)
                await target.drain()
        tasks = [asyncio.create_task(copy(incoming, writer)), asyncio.create_task(copy(reader, outgoing))]
        done, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in done:
            await task
        flush_output = True
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        if input_transport is not None: input_transport.close()
        try:
            if output_transport is not None:
                if flush_output:
                    output_transport.close()
                    try:
                        # drain() only waits above high-water; the final bytes
                        # can still be buffered below it when relay EOF arrives.
                        await protocol.closed
                    except BaseException:
                        output_transport.abort()
                        raise
                else:
                    # Cancellation/error must not wait for a paused consumer.
                    output_transport.abort()
        finally:
            await relay.close_writer(writer, abort=True)


if __name__ == "__main__":
    try:
        if len(sys.argv) == 3 and sys.argv[1] == "--stdio":
            asyncio.run(stdio_proxy(sys.argv[2]))
        elif len(sys.argv) == 4 and sys.argv[1] == "--proxy":
            proxy_main(int(sys.argv[2]), sys.argv[3])
        else:
            raise AliasConfigurationError("Usage: remotedesk_linux_ssh.py --stdio PROFILE_UUID (or GUI-owned --proxy PORT TOKEN_FILE)")
    except AliasConfigurationError as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
    except Exception:
        print("RemoteDesk SSH 代理连接失败：请检查当前中继登录、设备密钥和目标 SSH 服务；未回退直连。", file=sys.stderr)
        raise SystemExit(1)
