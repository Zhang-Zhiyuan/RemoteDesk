#!/usr/bin/env python3
"""RemoteDesk private relay.

The relay authenticates endpoints, publishes the online directory and copies
opaque RemoteDesk bytes. Remote-control passwords and session keys are never
sent in this protocol and remain end-to-end between viewer and host.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import hmac
import ipaddress
import json
import os
import signal
import socket
import ssl
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional
from pathlib import Path


RELAY_RELEASE_VERSION = "1.0.8"
# Capture once when this process loads, not when an installer replaces the file.
RELAY_SOURCE_SHA256 = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()

MAX_HANDSHAKE_BYTES = 64 * 1024
MAX_CONNECTIONS = 512
MAX_PENDING_SESSIONS = 256
HOST_IDLE_TIMEOUT_SECONDS = 45
PAIR_TIMEOUT_SECONDS = 12
COPY_BUFFER_BYTES = 128 * 1024
WRITE_BUFFER_HIGH_BYTES = 64 * 1024
WRITE_BUFFER_LOW_BYTES = 16 * 1024
TCP_NOTSENT_LOWAT_BYTES = 64 * 1024
BRIDGE_WRITE_TIMEOUT_SECONDS = 30
STREAM_CLOSE_TIMEOUT_SECONDS = 2
MAX_DIRECT_ADDRESSES = 8


def normalize_address_report(message):
    """Optional IPv4 hints only: never resolve DNS or dial a reported address."""
    port, values = message.get("directPort"), message.get("directAddresses")
    if type(port) is not int or not 1 <= port <= 65535 or not isinstance(values, list):
        return [], 0
    addresses = []
    for value in values[:32]:
        if not isinstance(value, str) or len(value) > 15:
            continue
        try:
            address = ipaddress.IPv4Address(value)
        except ValueError:
            continue
        if (address.is_loopback or address.is_link_local or address.is_multicast
                or int(address) >> 24 == 0 or int(address) >> 24 >= 224):
            continue
        if str(address) not in addresses:
            addresses.append(str(address))
        if len(addresses) == MAX_DIRECT_ADDRESSES:
            break
    return addresses, port if addresses else 0


@dataclass
class HostConnection:
    device_id: str
    machine_name: str
    platform: str
    build_stamp: Optional[str]
    reader: asyncio.StreamReader
    writer: asyncio.StreamWriter
    write_lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    last_seen: float = field(default_factory=time.monotonic)
    active_sessions: int = 0
    direct_addresses: list = field(default_factory=list)
    direct_port: int = 0
    address_reported_at: Optional[float] = None

    def update_addresses(self, message):
        # Missing fields mean a legacy heartbeat; an explicit empty/invalid
        # report clears old hints instead of advertising a no-longer-owned IP.
        if "directAddresses" in message or "directPort" in message:
            self.direct_addresses, self.direct_port = normalize_address_report(message)
            self.address_reported_at = time.monotonic()


@dataclass
class PendingSession:
    session_id: str
    device_id: str
    host_connection: HostConnection
    data_connected: asyncio.Future
    completed: asyncio.Future
    created_at: float = field(default_factory=time.monotonic)


class RelayServer:
    def __init__(self, config: dict):
        self.token = config["access_token"]
        self.bind = config.get("bind", "0.0.0.0")
        self.port = int(config.get("port", 56567))
        self.cert_file = config["cert_file"]
        self.key_file = config["key_file"]
        self.tcp_congestion_control = validate_congestion_control(config.get("tcp_congestion_control", ""))
        self.hosts: dict[str, HostConnection] = {}
        self.pending: dict[str, PendingSession] = {}
        self.state_lock = asyncio.Lock()
        self.connection_slots = asyncio.Semaphore(MAX_CONNECTIONS)
        self.connections: dict[asyncio.Task, asyncio.StreamWriter] = {}
        self.stopping = False

    async def handle_connection(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
    ) -> None:
        if self.stopping:
            await close_writer(writer)
            return
        task = asyncio.current_task()
        self.connections[task] = writer
        try:
            await self._handle_connection(reader, writer)
        finally:
            self.connections.pop(task, None)

    async def shutdown(self) -> None:
        # Python 3.12 Server.wait_closed() also waits for connected clients.
        # Stop persistent heartbeat/read tasks first, otherwise SIGTERM can
        # hang until systemd kills the service's perfectly idle connections.
        self.stopping = True
        connections = tuple(self.connections.items())
        for task, writer in connections:
            writer.close()
            task.cancel()
        if connections:
            _, pending = await asyncio.wait([task for task, _ in connections], timeout=5)
            for task, writer in connections:
                if task in pending:
                    writer.transport.abort()
                    task.cancel()
            await asyncio.gather(*(task for task, _ in connections), return_exceptions=True)

    async def _handle_connection(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
    ) -> None:
        # Reject excess sockets immediately; waiting on the semaphore here would
        # leave an unbounded queue of accepted TLS connections outside the cap.
        if self.connection_slots.locked():
            await close_writer(writer)
            return
        await self.connection_slots.acquire()
        try:
            configure_transport(writer, self.tcp_congestion_control)
            hello = await asyncio.wait_for(
                read_json(reader), timeout=10
            )
            if hello.get("version") != 1:
                await write_error(writer, "不支持的中继协议版本。")
                return
            if not self._token_matches(hello.get("token")):
                await write_error(writer, "中继访问密钥错误。")
                return

            role = hello.get("role")
            if role == "host-control":
                await self.handle_host_control(reader, writer, hello)
            elif role == "directory":
                await self.handle_directory(writer, hello)
            elif role == "viewer":
                await self.handle_viewer(reader, writer, hello)
            elif role == "host-data":
                await self.handle_host_data(reader, writer, hello)
            elif role == "health":
                async with self.state_lock:
                    busy = bool(self.pending) or any(host.active_sessions for host in self.hosts.values())
                await write_json(writer, {"ok": True, "serverVersion": RELAY_RELEASE_VERSION,
                                          "serverSourceSha256": RELAY_SOURCE_SHA256, "busy": busy})
            else:
                await write_error(writer, "未知的中继连接类型。")
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        except asyncio.TimeoutError:
            with contextlib.suppress(Exception):
                await write_error(writer, "中继握手超时。")
        except (ValueError, json.JSONDecodeError) as exc:
            with contextlib.suppress(Exception):
                await write_error(writer, f"中继握手无效：{exc}")
        except Exception as exc:
            print(
                f"connection error: {type(exc).__name__}: {exc}",
                file=sys.stderr,
                flush=True,
            )
            with contextlib.suppress(Exception):
                await write_error(writer, "中继内部错误。")
        finally:
            self.connection_slots.release()
            await close_writer(writer)

    async def handle_host_control(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        hello: dict,
    ) -> None:
        device_id = normalize_device_id(hello.get("deviceId"))
        machine_name = normalize_text(
            hello.get("machineName"), "未命名设备", 120
        )
        platform = normalize_text(hello.get("platform"), "未知", 40)
        build_stamp = normalize_build_stamp(hello.get("buildStamp"))
        host = HostConnection(
            device_id,
            machine_name,
            platform,
            build_stamp,
            reader,
            writer,
        )
        host.update_addresses(hello)

        old_host = None
        async with self.state_lock:
            old_host = self.hosts.get(device_id)
            self.hosts[device_id] = host
        if old_host is not None and old_host is not host:
            old_host.writer.close()
        try:
            await self._write_host(
                host, {"ok": True, "type": "registered", "heartbeatAck": True, "addressReporting": True}
            )
            while True:
                message = await asyncio.wait_for(
                    read_json(reader),
                    timeout=HOST_IDLE_TIMEOUT_SECONDS,
                )
                if message.get("type") != "heartbeat":
                    raise ValueError("主机控制消息无效")
                host.last_seen = time.monotonic()
                host.update_addresses(message)
                await self._write_host(host, {"type": "heartbeat", "addressReporting": True})
        finally:
            async with self.state_lock:
                if self.hosts.get(device_id) is host:
                    self.hosts.pop(device_id, None)
                abandoned = [
                    item
                    for item in self.pending.values()
                    if item.host_connection is host
                ]
                for item in abandoned:
                    self.pending.pop(item.session_id, None)
                    if not item.data_connected.done():
                        item.data_connected.set_exception(
                            ConnectionError("目标设备已离线。")
                        )
                    if not item.completed.done():
                        item.completed.set_result(None)

    async def handle_directory(self, writer: asyncio.StreamWriter, request=None) -> None:
        request = request or {}
        paged = "pageSize" in request
        offset, page_size = request.get("offset", 0), request.get("pageSize", 32)
        if (type(offset) is not int or not 0 <= offset < MAX_CONNECTIONS or
                type(page_size) is not int or not 1 <= page_size <= 32):
            raise ValueError("目录分页参数无效")
        now = time.monotonic()
        async with self.state_lock:
            devices = [
                {
                    "deviceId": host.device_id,
                    "machineName": host.machine_name,
                    "platform": host.platform,
                    "buildStamp": host.build_stamp,
                    "busy": host.active_sessions > 0
                    or any(
                        item.device_id == host.device_id
                        for item in self.pending.values()
                    ),
                    "lastSeenSeconds": max(
                        0, int(now - host.last_seen)
                    ),
                    "directAddresses": (list(host.direct_addresses) if host.address_reported_at is not None
                                        and now - host.address_reported_at < HOST_IDLE_TIMEOUT_SECONDS else []),
                    "directPort": (host.direct_port if host.address_reported_at is not None
                                   and now - host.address_reported_at < HOST_IDLE_TIMEOUT_SECONDS else 0),
                    "addressAgeSeconds": (max(0, int(now - host.address_reported_at))
                                          if host.address_reported_at is not None else None),
                }
                for host in self.hosts.values()
                if not host.writer.is_closing()
                and now - host.last_seen < HOST_IDLE_TIMEOUT_SECONDS
            ]
        devices.sort(key=lambda item: (item["machineName"].casefold(), item["deviceId"]))
        if paged:
            page = devices[offset:offset + page_size]
            next_offset = offset + page_size if offset + page_size < len(devices) else None
            await write_json(writer, {"ok": True, "devices": page, "nextOffset": next_offset})
        else:
            # Old clients requested an unpaged directory and cannot use IP
            # metadata. Preserve their previous response size and shape.
            for device in devices:
                for key in ("directAddresses", "directPort", "addressAgeSeconds"):
                    device.pop(key, None)
            await write_json(writer, {"ok": True, "devices": devices})

    async def handle_viewer(
        self,
        _reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        hello: dict,
    ) -> None:
        device_id = normalize_device_id(hello.get("deviceId"))
        loop = asyncio.get_running_loop()
        async with self.state_lock:
            host = self.hosts.get(device_id)
            if (host is None or host.writer.is_closing()
                    or time.monotonic() - host.last_seen >= HOST_IDLE_TIMEOUT_SECONDS):
                raise ValueError("目标设备不在线。")
            if len(self.pending) >= MAX_PENDING_SESSIONS:
                raise ValueError("中继当前连接过多，请稍后重试。")
            session_id = str(uuid.uuid4())
            pending = PendingSession(
                session_id,
                device_id,
                host,
                loop.create_future(),
                loop.create_future(),
            )
            self.pending[session_id] = pending

        paired = False
        try:
            await self._write_host(
                host,
                {"type": "open", "sessionId": session_id},
            )
            host_reader, host_writer = await asyncio.wait_for(
                asyncio.shield(pending.data_connected),
                timeout=PAIR_TIMEOUT_SECONDS,
            )
            await write_json(host_writer, {"ok": True, "type": "paired"})
            await write_json(writer, {"ok": True, "type": "paired"})
            paired = True
            host.active_sessions += 1
            try:
                await bridge_streams(
                    _reader,
                    writer,
                    host_reader,
                    host_writer,
                )
            finally:
                host.active_sessions = max(0, host.active_sessions - 1)
                if not pending.completed.done():
                    pending.completed.set_result(None)
        except asyncio.TimeoutError:
            if not paired:
                await write_error(writer, "目标设备建立中继通道超时。")
        except ConnectionError as exc:
            if not paired:
                await write_error(writer, str(exc))
        finally:
            async with self.state_lock:
                if self.pending.get(session_id) is pending:
                    self.pending.pop(session_id, None)
            if not pending.completed.done():
                pending.completed.set_result(None)
            if not pending.data_connected.done():
                pending.data_connected.cancel()
            elif not pending.data_connected.cancelled():
                # A host may leave while its open request is still being sent.
                pending.data_connected.exception()

    async def handle_host_data(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        hello: dict,
    ) -> None:
        device_id = normalize_device_id(hello.get("deviceId"))
        session_id = normalize_uuid(hello.get("sessionId"), "会话标识")
        async with self.state_lock:
            pending = self.pending.get(session_id)
            if pending is None or pending.device_id != device_id:
                raise ValueError("中继会话不存在或已经过期。")
            if pending.data_connected.done():
                raise ValueError("中继会话已经建立。")
            pending.data_connected.set_result((reader, writer))
        await pending.completed

    async def _write_host(self, host: HostConnection, value: dict) -> None:
        async with host.write_lock:
            await write_json(host.writer, value)

    def _token_matches(self, presented) -> bool:
        return isinstance(presented, str) and hmac.compare_digest(
            self.token.encode("utf-8"), presented.encode("utf-8")
        )


async def read_json(reader: asyncio.StreamReader) -> dict:
    length_bytes = await reader.readexactly(4)
    length = int.from_bytes(length_bytes, "big", signed=False)
    if length <= 0 or length > MAX_HANDSHAKE_BYTES:
        raise ValueError("握手消息大小无效")
    payload = await reader.readexactly(length)
    value = json.loads(payload.decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("握手消息必须是对象")
    return value


async def write_json(writer: asyncio.StreamWriter, value: dict) -> None:
    payload = json.dumps(
        value, ensure_ascii=False, separators=(",", ":")
    ).encode("utf-8")
    if not payload or len(payload) > MAX_HANDSHAKE_BYTES:
        raise ValueError("握手响应大小无效")
    writer.write(len(payload).to_bytes(4, "big") + payload)
    await asyncio.wait_for(writer.drain(), timeout=10)


async def close_writer(writer: asyncio.StreamWriter) -> None:
    writer.close()
    try:
        await asyncio.wait_for(writer.wait_closed(), timeout=STREAM_CLOSE_TIMEOUT_SECONDS)
    except asyncio.CancelledError:
        writer.transport.abort()
        raise
    except Exception:
        # SSL close_notify can stall on a dead peer. Closing only the writer
        # leaves its socket alive and makes Server.wait_closed() wait forever.
        writer.transport.abort()


async def write_error(writer: asyncio.StreamWriter, message: str) -> None:
    await write_json(writer, {"ok": False, "error": message[:300]})


async def bridge_streams(
    first_reader: asyncio.StreamReader,
    first_writer: asyncio.StreamWriter,
    second_reader: asyncio.StreamReader,
    second_writer: asyncio.StreamWriter,
) -> None:
    async def pump(reader, writer):
        while True:
            data = await reader.read(COPY_BUFFER_BYTES)
            if not data:
                return
            writer.write(data)
            # Bound unsent TLS data independently of TCP's in-flight window.
            # A stalled peer must not keep both bridge tasks alive forever.
            await asyncio.wait_for(writer.drain(), BRIDGE_WRITE_TIMEOUT_SECONDS)

    tasks = {
        asyncio.create_task(pump(first_reader, second_writer)),
        asyncio.create_task(pump(second_reader, first_writer)),
    }
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await asyncio.gather(close_writer(first_writer), close_writer(second_writer))


def validate_congestion_control(value: str) -> str:
    if value not in ("", "cubic", "bbr"):
        raise ValueError("tcp_congestion_control must be empty, cubic or bbr")
    return value


def configure_transport(writer: asyncio.StreamWriter, congestion_control: str = "") -> None:
    # SSL transports otherwise permit a large plaintext backlog before drain()
    # applies backpressure. This changes no encrypted bytes and never drops or
    # reorders a chunk inside an end-to-end authenticated RemoteDesk message.
    writer.transport.set_write_buffer_limits(
        high=WRITE_BUFFER_HIGH_BYTES, low=WRITE_BUFFER_LOW_BYTES)
    sock = writer.get_extra_info("socket")
    if sock is None:
        return
    options = [(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)]
    if hasattr(socket, "TCP_NOTSENT_LOWAT"):
        options.append((socket.IPPROTO_TCP, socket.TCP_NOTSENT_LOWAT, TCP_NOTSENT_LOWAT_BYTES))
    if congestion_control and hasattr(socket, "TCP_CONGESTION"):
        options.append((socket.IPPROTO_TCP, socket.TCP_CONGESTION, congestion_control.encode("ascii")))
    for level, name, value in options:
        # Per-socket only. Older kernels/containers retain their working TCP
        # implementation; no global sysctl, routing or firewall changes.
        with contextlib.suppress(OSError, AttributeError, NotImplementedError):
            sock.setsockopt(level, name, value)


def normalize_text(value, fallback: str, max_length: int) -> str:
    if not isinstance(value, str) or not value.strip():
        return fallback
    return value.strip()[:max_length]


def normalize_uuid(value, label: str) -> str:
    if not isinstance(value, str):
        raise ValueError(f"{label}无效")
    try:
        return str(uuid.UUID(value))
    except ValueError as exc:
        raise ValueError(f"{label}无效") from exc


def normalize_device_id(value) -> str:
    return normalize_uuid(value, "设备标识")


def normalize_build_stamp(value) -> Optional[str]:
    if (
        isinstance(value, str)
        and len(value) == 14
        and value.isascii()
        and value.isdigit()
    ):
        return value
    return None


def load_config(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as stream:
        config = json.load(stream)
    token = config.get("access_token")
    if not isinstance(token, str) or len(token) < 32:
        raise ValueError("access_token 缺失或过短")
    port = int(config.get("port", 56567))
    if port < 1 or port > 65535:
        raise ValueError("port 无效")
    for name in ("cert_file", "key_file"):
        if not isinstance(config.get(name), str) or not config[name]:
            raise ValueError(f"{name} 缺失")
    validate_congestion_control(config.get("tcp_congestion_control", ""))
    return config


async def run(config_path: str) -> None:
    config = load_config(config_path)
    relay = RelayServer(config)
    tls_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls_context.minimum_version = ssl.TLSVersion.TLSv1_2
    tls_context.load_cert_chain(relay.cert_file, relay.key_file)
    server = await asyncio.start_server(
        relay.handle_connection,
        relay.bind,
        relay.port,
        ssl=tls_context,
        ssl_handshake_timeout=10,
        limit=MAX_HANDSHAKE_BYTES + COPY_BUFFER_BYTES,
        backlog=256,
    )
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        with contextlib.suppress(NotImplementedError):
            loop.add_signal_handler(sig, stop.set)
    sockets = ", ".join(str(sock.getsockname()) for sock in server.sockets)
    print(f"RemoteDesk relay listening on {sockets}", flush=True)
    try:
        await stop.wait()
    finally:
        server.close()
        await relay.shutdown()
        await server.wait_closed()


def main() -> int:
    config_path = (
        sys.argv[1]
        if len(sys.argv) > 1
        else os.environ.get(
            "REMOTEDESK_RELAY_CONFIG",
            "/etc/remotedesk-relay/config.json",
        )
    )
    try:
        asyncio.run(run(config_path))
        return 0
    except Exception as exc:
        print(f"fatal: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
