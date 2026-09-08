#!/usr/bin/env python3
"""Native private-relay transport. TLS pinning precedes every secret write.

The asyncio owner serializes TLS I/O; a private loopback socket adapts it to the
existing synchronous RemoteDesk protocol, just like the Windows product does.
There is no LAN fallback, external proxy process, or SSH/ADB tunnel.
"""
from __future__ import annotations

import asyncio
import contextlib
from dataclasses import dataclass, replace
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import socket
import ssl
import struct
import tempfile
import threading
import uuid

MAX_JSON = 65536
TIMEOUT = 12


class RelayIdentityError(PermissionError):
    pass


@dataclass(frozen=True, repr=False)
class RelayOptions:
    server_address: str
    port: int
    access_token: str
    tls_certificate_sha256: str
    device_id: str
    publish: bool = True

    def __repr__(self):
        return f"RelayOptions(server_address={self.server_address!r}, port={self.port}, access_token=<redacted>)"

    def validate(self):
        host = self.server_address.strip()
        token = self.access_token.strip()
        pin = re.sub(r"[:\s-]", "", self.tls_certificate_sha256).upper()
        if not host or len(host) > 253 or any(c.isspace() for c in host) or any(c in host for c in '/@\\'):
            raise ValueError("请填写中转服务器主机名或 IP，不要包含协议或路径。")
        if isinstance(self.port, bool) or not 1 <= int(self.port) <= 65535:
            raise ValueError("中转端口无效。")
        if not 32 <= len(token) <= 4096:
            raise ValueError("中转访问密钥无效。")
        if not re.fullmatch(r"[0-9A-F]{64}", pin):
            raise ValueError("中转 TLS 指纹必须是 64 位 SHA-256。")
        try:
            device = str(uuid.UUID(self.device_id.strip()))
        except (ValueError, AttributeError) as error:
            raise ValueError("中转设备 ID 无效。") from error
        return replace(self, server_address=host, port=int(self.port), access_token=token,
                       tls_certificate_sha256=pin, device_id=device)

    def to_dict(self):
        return dict(serverAddress=self.server_address, port=self.port, accessToken=self.access_token,
                    tlsCertificateSha256=self.tls_certificate_sha256, deviceId=self.device_id, publish=self.publish)

    @classmethod
    def from_dict(cls, value):
        if not isinstance(value, dict):
            raise ValueError("中转配置必须是对象。")
        port = value.get("port", 56567)
        if isinstance(port, bool) or not isinstance(port, (int, str)):
            raise ValueError("中转端口无效。")
        return cls(str(value.get("serverAddress", "")), int(port),
                   str(value.get("accessToken", "")), str(value.get("tlsCertificateSha256", "")),
                   str(value.get("deviceId", "")), value.get("publish", True) is True).validate()


async def read_json(reader):
    length = struct.unpack("!I", await reader.readexactly(4))[0]
    if not 0 < length <= MAX_JSON:
        raise ValueError("中转握手消息长度无效。")
    value = json.loads((await reader.readexactly(length)).decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("中转握手消息必须是对象。")
    return value


async def write_json(writer, value):
    data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if not 0 < len(data) <= MAX_JSON:
        raise ValueError("中转握手消息过大。")
    writer.write(struct.pack("!I", len(data)) + data)
    await asyncio.wait_for(writer.drain(), TIMEOUT)


def ensure_success(value):
    if value.get("ok") is True:
        return
    detail = str(value.get("error", "中转拒绝连接。"))[:300]
    if "访问密钥" in detail:
        raise RelayIdentityError("中转访问密钥被拒绝，请检查配置。")
    # Do not echo untrusted server error text that could contain an access token.
    raise ConnectionError("中转拒绝连接：目标可能已离线，请刷新设备列表。")


async def close_writer(writer):
    if writer is not None:
        writer.close()
        with contextlib.suppress(Exception):
            await asyncio.wait_for(writer.wait_closed(), 2)


async def connect_tls(options):
    options = options.validate()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Explicit certificate pin is the trust anchor below.
    reader, writer = await asyncio.wait_for(asyncio.open_connection(
        options.server_address, options.port, ssl=context, ssl_handshake_timeout=10,
        limit=128 * 1024), TIMEOUT)
    try:
        certificate = writer.get_extra_info("ssl_object").getpeercert(binary_form=True)
        if not certificate or not hmac.compare_digest(hashlib.sha256(certificate).hexdigest().upper(),
                                                     options.tls_certificate_sha256):
            raise RelayIdentityError("中转 TLS 指纹不匹配，连接已拒绝；请核实服务器身份。")
        return reader, writer
    except BaseException:
        await close_writer(writer)
        raise


async def hello(reader, writer, options, role, **fields):
    await write_json(writer, dict(version=1, role=role, token=options.access_token, **fields))
    response = await asyncio.wait_for(read_json(reader), TIMEOUT)
    ensure_success(response)
    return response


async def list_devices_async(options):
    options = options.validate()
    reader, writer = await connect_tls(options)
    try:
        response = await hello(reader, writer, options, "directory")
        devices = response.get("devices")
        if not isinstance(devices, list) or len(devices) > 512:
            raise ValueError("中转在线列表无效。")
        result, seen = [], set()
        for item in devices:
            if not isinstance(item, dict):
                continue
            try:
                device_id = str(uuid.UUID(item.get("deviceId", "")))
            except (ValueError, AttributeError):
                continue
            if device_id in seen:
                continue
            seen.add(device_id)
            result.append(dict(deviceId=device_id, machineName=str(item.get("machineName", "未命名设备"))[:120],
                               platform=str(item.get("platform", "未知"))[:40], busy=item.get("busy") is True))
        return result
    finally:
        await close_writer(writer)


def list_devices(options):
    return asyncio.run(list_devices_async(options))


async def bridge(first, second):
    async def copy(reader, writer):
        while data := await reader.read(64 * 1024):
            writer.write(data)
            await asyncio.wait_for(writer.drain(), 30)
    tasks = [asyncio.create_task(copy(first[0], second[1])),
             asyncio.create_task(copy(second[0], first[1]))]
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await asyncio.gather(close_writer(first[1]), close_writer(second[1]))


def connect_viewer(options, stop_event=None):
    """Return an ordinary TCP socket backed only by an authenticated relay tunnel."""
    options = options.validate()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        client.connect(listener.getsockname())
        local, _ = listener.accept()
    except BaseException:
        client.close()
        raise
    finally:
        listener.close()
    ready = threading.Event()
    state = {}

    async def run():
        state["loop"], state["task"] = asyncio.get_running_loop(), asyncio.current_task()
        remote_writer = local_writer = None
        try:
            reader, remote_writer = await connect_tls(options)
            await hello(reader, remote_writer, options, "viewer", deviceId=options.device_id)
            local.setblocking(False)
            local_reader, local_writer = await asyncio.open_connection(sock=local)
            ready.set()
            await bridge((local_reader, local_writer), (reader, remote_writer))
        except BaseException as error:
            state["error"] = error
        finally:
            ready.set()
            await asyncio.gather(close_writer(remote_writer), close_writer(local_writer))
            local.close()

    threading.Thread(target=lambda: asyncio.run(run()), name="RemoteDeskRelayViewer", daemon=True).start()
    while not ready.wait(.05):
        if stop_event is not None and stop_event.is_set():
            client.close()
            if "loop" in state:
                with contextlib.suppress(RuntimeError):
                    state["loop"].call_soon_threadsafe(state["task"].cancel)
            raise ConnectionAbortedError("中转连接已取消。")
    if "error" in state:
        client.close()
        if isinstance(state["error"], asyncio.CancelledError):
            raise ConnectionAbortedError("中转连接已取消。")
        raise state["error"]
    client.settimeout(TIMEOUT)
    return client


class RelayHostConnector:
    def __init__(self, options, local_port, machine_name="Linux", status=lambda value: None):
        self.options = options.validate()
        self.local_port = int(local_port)
        if not 1 <= self.local_port <= 65535:
            raise ValueError("本机被控端口无效。")
        self.machine_name, self.status_callback = machine_name[:120], status
        self.stop_event, self.online = threading.Event(), threading.Event()
        self.thread = None
        self.loop = self.task = None

    def _status(self, value):
        with contextlib.suppress(Exception):
            self.status_callback(value)

    def start(self):
        if self.thread is not None:
            raise RuntimeError("中转注册对象不能重复启动。")
        self.thread = threading.Thread(target=lambda: asyncio.run(self._run()),
                                       name="RemoteDeskRelayHost", daemon=True)
        self.thread.start()

    def close(self, wait=True):
        self.stop_event.set()
        if self.loop is not None and self.task is not None:
            with contextlib.suppress(RuntimeError):
                self.loop.call_soon_threadsafe(self.task.cancel)
        if wait and self.thread is not None and self.thread is not threading.current_thread():
            self.thread.join(timeout=5)

    async def _run(self):
        self.loop, self.task = asyncio.get_running_loop(), asyncio.current_task()
        failures = 0
        try:
            while not self.stop_event.is_set():
                try:
                    await self._control()
                    failures = 0
                except RelayIdentityError as error:
                    self._status(str(error))
                    return
                except (OSError, ValueError, asyncio.IncompleteReadError, TimeoutError):
                    delay = (1, 2, 5, 10, 20)[min(failures, 4)]
                    failures += 1
                    self._status(f"中转离线，{delay} 秒后重试。")
                    await asyncio.sleep(delay)
        except asyncio.CancelledError:
            pass
        finally:
            self.online.clear()
            if self.stop_event.is_set():
                self._status("中转注册已停止。")

    async def _control(self):
        reader, writer = await connect_tls(self.options)
        heartbeat = None
        pending = set()
        try:
            await hello(reader, writer, self.options, "host-control", deviceId=self.options.device_id,
                        machineName=self.machine_name, platform="Linux")
            self.online.set()
            self._status(f"已上线到中转 {self.options.server_address}:{self.options.port}")

            async def beat():
                while True:
                    await asyncio.sleep(10)
                    await write_json(writer, dict(version=1, type="heartbeat"))

            heartbeat = asyncio.create_task(beat())
            while True:
                reading = asyncio.create_task(asyncio.wait_for(read_json(reader), 45))
                try:
                    completed, _ = await asyncio.wait((reading, heartbeat), return_when=asyncio.FIRST_COMPLETED)
                    if heartbeat in completed:
                        await heartbeat
                        raise ConnectionError("中转心跳已停止。")
                    message = await reading
                finally:
                    reading.cancel()
                    await asyncio.gather(reading, return_exceptions=True)
                if message.get("type") != "open" or len(pending) >= 16:
                    continue
                try:
                    session_id = str(uuid.UUID(message.get("sessionId", "")))
                except (ValueError, AttributeError):
                    continue
                task = asyncio.create_task(self._data(session_id))
                pending.add(task)
                task.add_done_callback(pending.discard)
        finally:
            self.online.clear()
            if heartbeat is not None:
                heartbeat.cancel()
                await asyncio.gather(heartbeat, return_exceptions=True)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
            await close_writer(writer)

    async def _data(self, session_id):
        local_writer = remote_writer = None
        try:
            local_reader, local_writer = await asyncio.wait_for(
                asyncio.open_connection("127.0.0.1", self.local_port), 8)
            reader, remote_writer = await connect_tls(self.options)
            await hello(reader, remote_writer, self.options, "host-data",
                        deviceId=self.options.device_id, sessionId=session_id)
            await bridge((local_reader, local_writer), (reader, remote_writer))
        except (OSError, ValueError, asyncio.IncompleteReadError, TimeoutError):
            self._status("中转会话未建立或已结束，请确认本机被控端正在运行。")
        finally:
            await asyncio.gather(close_writer(local_writer), close_writer(remote_writer))


def settings_path():
    return Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config"))) / "remotedesk" / "relay.json"


def load_settings(path=None):
    path = Path(path) if path is not None else settings_path()
    if not path.exists():
        return None
    with path.open("r", encoding="utf-8") as source:
        data = source.read(MAX_JSON + 1)
    if len(data) > MAX_JSON:
        raise ValueError("中转配置文件过大。")
    return RelayOptions.from_dict(json.loads(data))


def save_settings(options, path=None):
    options = options.validate()
    path = Path(path) if path is not None else settings_path()
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".relay-", dir=path.parent)
    try:
        os.chmod(temporary, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(options.to_dict(), output, ensure_ascii=False)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)
