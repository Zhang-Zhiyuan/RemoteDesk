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
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import socket
import ssl
import struct
import tempfile
import threading
import time
import uuid
import unicodedata

MAX_JSON = 65536
TIMEOUT = 12
COPY_BUFFER_BYTES = 16 * 1024
WRITE_BUFFER_HIGH_BYTES = 16 * 1024
WRITE_BUFFER_LOW_BYTES = 4 * 1024
TCP_NOTSENT_LOWAT_BYTES = 16 * 1024
# Only the private in-process loopback hop uses fixed small TCP windows. Its
# sub-millisecond RTT does not need WAN-sized autotuned queues of old frames.
LOOPBACK_BUFFER_BYTES = 16 * 1024
LOOPBACK_READER_LIMIT_BYTES = 16 * 1024
BRIDGE_WRITE_TIMEOUT_SECONDS = 30
STREAM_CLOSE_TIMEOUT_SECONDS = 2
MAX_DIRECT_ADDRESSES = 8


def normalize_address_report(message):
    port, values = message.get("directPort"), message.get("directAddresses")
    if type(port) is not int or not 1 <= port <= 65535 or not isinstance(values, list):
        return dict(directAddresses=[], directPort=0)
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
    return dict(directAddresses=addresses, directPort=port if addresses else 0)


def local_direct_addresses():
    """Read interface IPv4 addresses without DNS, subprocesses or route changes."""
    addresses = []
    try:
        import fcntl
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
            for _, name in socket.if_nameindex()[:64]:
                encoded = name.encode("utf-8")
                if name == "lo" or len(encoded) >= 16:
                    continue
                try:
                    request = struct.pack("256s", encoded)
                    flags = struct.unpack("H", fcntl.ioctl(control, 0x8913, request)[16:18])[0]
                    if flags & 1 and not flags & 8:  # IFF_UP, not IFF_LOOPBACK
                        addresses.append(socket.inet_ntoa(fcntl.ioctl(control, 0x8915, request)[20:24]))
                except OSError:
                    continue
    except (OSError, ImportError, AttributeError):
        pass
    return normalize_address_report(dict(directAddresses=sorted(set(addresses)), directPort=56565))["directAddresses"]


def direct_address_display(device):
    report = normalize_address_report(device)
    return " / ".join(f"{ip}:{report['directPort']}" for ip in report["directAddresses"]) or "未上报（仍可中转连接）"


class RelayIdentityError(PermissionError):
    pass


def _checked_endpoint(server_address, port):
    host = server_address.strip()
    if not host or len(host) > 253 or any(c.isspace() for c in host) or any(c in host for c in '/@\\'):
        raise ValueError("请填写中转服务器主机名或 IP，不要包含协议或路径。")
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError("中转端口无效。")
    return host, port


def _checked_pin(pin):
    pin = re.sub(r"[:\s-]", "", pin).upper()
    if not re.fullmatch(r"[0-9A-F]{64}", pin):
        raise ValueError("服务器身份指纹格式无效。")
    return pin


@dataclass(frozen=True, repr=False)
class RelayEnrollmentDraft:
    server_address: str
    port: int
    access_token: str
    device_id: str
    publish: bool = True

    def __repr__(self):
        return "RelayEnrollmentDraft(<redacted>)"

    def validate(self):
        host, port = _checked_endpoint(self.server_address, self.port)
        token = self.access_token.strip()
        if not 32 <= len(token) <= 4096:
            raise ValueError("服务器登录配置无效，请用 root 密码重新登录服务器。")
        try:
            device = str(uuid.UUID(self.device_id.strip()))
        except (ValueError, AttributeError) as error:
            raise ValueError("中转设备 ID 无效。") from error
        return replace(self, server_address=host, port=port, access_token=token, device_id=device)

    def same_endpoint(self, saved):
        return saved is not None and self.server_address.casefold() == saved.server_address.casefold() and self.port == saved.port

    def known_pin(self, saved, manual_pin=""):
        if manual_pin.strip():
            return _checked_pin(manual_pin)
        return saved.tls_certificate_sha256 if self.same_endpoint(saved) else None

    def replaces_identity(self, saved, pin):
        return self.same_endpoint(saved) and _checked_pin(pin) != saved.tls_certificate_sha256

    def with_pin(self, pin):
        return RelayOptions(self.server_address, self.port, self.access_token, pin, self.device_id, self.publish).validate()


@dataclass(frozen=True, repr=False)
class RelayOptions:
    server_address: str
    port: int
    access_token: str
    tls_certificate_sha256: str
    device_id: str
    publish: bool = True
    ssh_port: int = 22
    admin_username: str = 'root'
    ssh_host_key_sha256: str = ''

    def __repr__(self):
        return f"RelayOptions(server_address={self.server_address!r}, port={self.port}, access_token=<redacted>)"

    def validate(self):
        draft = RelayEnrollmentDraft(self.server_address, self.port, self.access_token, self.device_id, self.publish).validate()
        if type(self.ssh_port) is not int or not 1 <= self.ssh_port <= 65535:
            raise ValueError('服务器 SSH 端口无效。')
        if not re.fullmatch(r'[a-zA-Z_][a-zA-Z0-9_.-]{0,63}', self.admin_username):
            raise ValueError('服务器管理员账号无效。')
        if self.ssh_host_key_sha256 and not re.fullmatch(r'SHA256:[A-Za-z0-9+/]{43}', self.ssh_host_key_sha256):
            raise ValueError('已保存的 SSH 服务器身份无效。')
        return replace(self, server_address=draft.server_address, access_token=draft.access_token,
                       tls_certificate_sha256=_checked_pin(self.tls_certificate_sha256), device_id=draft.device_id)

    def to_dict(self):
        return dict(serverAddress=self.server_address, port=self.port, accessToken=self.access_token,
                    tlsCertificateSha256=self.tls_certificate_sha256, deviceId=self.device_id, publish=self.publish,
                    sshPort=self.ssh_port, adminUsername=self.admin_username, sshHostKeySha256=self.ssh_host_key_sha256)

    @classmethod
    def from_dict(cls, value):
        if not isinstance(value, dict):
            raise ValueError("中转配置必须是对象。")
        port = value.get("port", 56567)
        if isinstance(port, bool) or not isinstance(port, (int, str)):
            raise ValueError("中转端口无效。")
        return cls(str(value.get("serverAddress", "")), int(port),
                   str(value.get("accessToken", "")), str(value.get("tlsCertificateSha256", "")),
                   str(value.get("deviceId", "")), value.get("publish", True) is True,
                   value.get('sshPort', 22), value.get('adminUsername', 'root'),
                   value.get('sshHostKeySha256', '')).validate()


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
        raise RelayIdentityError("服务器登录已失效，请用 root 密码重新登录服务器；设备密钥无需更改。")
    # Do not echo untrusted server error text that could contain an access token.
    raise ConnectionError("中转拒绝连接：目标可能已离线，请刷新设备列表。")


async def close_writer(writer, *, abort=False):
    if writer is not None:
        # A bridge ends only after EOF, failure or cancellation of a session.
        # Its peer no longer consumes video; flushing that abandoned backlog
        # can retain the socket, delay reconnects and hit CPython gh-115514 on
        # distribution Python 3.12 builds. Abort ONLY these ended tunnels.
        # Live writes/receipts and short directory exchanges stay graceful.
        if abort:
            writer.transport.abort()
        else:
            writer.close()
        closed = asyncio.ensure_future(writer.wait_closed())
        # wait_closed() uses a shared protocol future. Cancelling that future
        # on timeout also poisons a later close by the bridge's owner. Shield
        # it, abort the actual transport on failure, and observe its completion
        # even when the caller itself is being cancelled.
        def observe_close(task):
            if not task.cancelled():
                task.exception()
        closed.add_done_callback(observe_close)
        try:
            await asyncio.wait_for(asyncio.shield(closed), STREAM_CLOSE_TIMEOUT_SECONDS)
        except asyncio.CancelledError:
            writer.transport.abort()
            raise
        except Exception:
            writer.transport.abort()


def configure_transport(writer):
    # Bound plaintext waiting for TLS independently from TCP's in-flight
    # window. Do not fix SO_RCVBUF/SO_SNDBUF or change global congestion policy:
    # high-RTT links still need room for already-sent, unacknowledged bytes.
    # Backpressure applies before the next copy, never by dropping/reordering
    # bytes from an authenticated RemoteDesk frame.
    writer.transport.set_write_buffer_limits(
        high=WRITE_BUFFER_HIGH_BYTES, low=WRITE_BUFFER_LOW_BYTES)
    sock = writer.get_extra_info("socket")
    if sock is None:
        return
    options = [(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)]
    if hasattr(socket, "TCP_NOTSENT_LOWAT"):
        options.append((socket.IPPROTO_TCP, socket.TCP_NOTSENT_LOWAT, TCP_NOTSENT_LOWAT_BYTES))
    for level, name, value in options:
        with contextlib.suppress(OSError, AttributeError, NotImplementedError):
            sock.setsockopt(level, name, value)


def configure_loopback_socket(sock):
    """For sockets created exclusively for our 127.0.0.1 adapter, never TLS/WAN."""
    for level, name, value in (
        (socket.SOL_SOCKET, socket.SO_RCVBUF, LOOPBACK_BUFFER_BYTES),
        (socket.SOL_SOCKET, socket.SO_SNDBUF, LOOPBACK_BUFFER_BYTES),
        (socket.IPPROTO_TCP, socket.TCP_NODELAY, 1),
    ):
        with contextlib.suppress(OSError, AttributeError, NotImplementedError):
            sock.setsockopt(level, name, value)


async def connect_local_host(port):
    # Set the loopback receive window before SYN. Changing it after connect
    # can leave an unnecessarily large negotiated window/read-ahead budget.
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        configure_loopback_socket(sock)
        sock.setblocking(False)
        await asyncio.get_running_loop().sock_connect(sock, ("127.0.0.1", port))
        return await asyncio.open_connection(sock=sock, limit=LOOPBACK_READER_LIMIT_BYTES)
    except BaseException:
        sock.close()
        raise


@dataclass(frozen=True)
class RelayNetworkPath:
    interface: str
    index: int
    local_address: str
    remote_address: str
    gateway: str


def _local_relay_interfaces():
    """Read only; never run ip/route, elevate, or alter a user's VPN policy."""
    if not hasattr(socket, "SO_BINDTODEVICE"):
        return []
    try:
        import fcntl
        with open("/proc/net/route", encoding="ascii") as source:
            rows = [line.split() for line in source.read(128 * 1024).splitlines()[1:]]
        gateways = {row[0]: row[2] for row in rows if len(row) >= 8 and
                    row[1] == "00000000" and row[7] == "00000000" and int(row[3], 16) & 1}
        found = []
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
            for index, name in socket.if_nameindex():
                encoded = name.encode("utf-8")
                if name == "lo" or len(encoded) >= 16:
                    continue
                request = struct.pack("256s", encoded)
                flags = struct.unpack("H", fcntl.ioctl(control, 0x8913, request)[16:18])[0]
                if not flags & 1:  # SIOCGIFFLAGS / IFF_UP
                    continue
                sysfs = Path("/sys/class/net") / name
                if (sysfs / "tun_flags").exists() or (sysfs / "type").read_text().strip() != "1":
                    return []  # Active tunnel/PPP: preserve normal system routing.
                if name not in gateways:
                    continue
                address = socket.inet_ntoa(fcntl.ioctl(control, 0x8915, request)[20:24])
                parsed = ipaddress.ip_address(address)
                if not parsed.is_loopback and not parsed.is_link_local and not parsed.is_unspecified:
                    found.append((name, index, address, gateways[name]))
        return sorted(found)[:4]
    except (OSError, ValueError, ImportError, AttributeError):
        return []


async def relay_network_paths(options):
    if os.environ.get("REMOTEDESK_RELAY_SYSTEM_ROUTE_ONLY") == "1":
        return []
    try:
        literal = ipaddress.ip_address(options.server_address)
    except ValueError:
        literal = None
    if options.server_address.lower() == "localhost" or literal is not None and (
            literal.version != 4 or not literal.is_global):
        return []
    # sysfs/ioctl enumeration is optional and must not stall the async owner.
    interfaces = await asyncio.to_thread(_local_relay_interfaces)
    if len(interfaces) < 2:
        return []
    try:
        addresses = [str(literal)] if literal is not None else [item[4][0] for item in
            await asyncio.get_running_loop().getaddrinfo(options.server_address, options.port,
                                                         type=socket.SOCK_STREAM)]
        ipv4 = [ipaddress.ip_address(address) for address in addresses
                if ipaddress.ip_address(address).version == 4]
        if not ipv4 or any(not address.is_global for address in ipv4):
            return []
        return [RelayNetworkPath(name, index, local, str(ipv4[0]), gateway)
                for name, index, local, gateway in interfaces]
    except (OSError, ValueError):
        return []


def _abort_relay_connection(connection):
    # Losing handshakes never sent an application token or data; do not wait for
    # a graceful TLS close on an unavailable network before using the winner.
    with contextlib.suppress(Exception):
        connection[1].transport.abort()


class RelayPathStability:
    """Millisecond policy shared with Windows/Android; future dials only."""
    PROBE_INTERVAL_MS, MINIMUM_HOLD_MS, HISTORY_MS = 30_000, 180_000, 600_000

    def __init__(self):
        self.lock = threading.Lock()
        self.samples = {}
        self.selected = self.challenger = None
        self.selected_at = self.last_use = self.wins = 0
        self.last_probe = self.last_round = None

    def preferred(self, now):
        with self.lock:
            if self.selected is not None and now - self.last_use >= self.HISTORY_MS:
                self.selected = None
                self._reset_challenge()
                self.samples.clear()
            self.last_use = now
            return self.selected

    def connected(self, path, now, failed_preferred=None):
        with self.lock:
            self.last_use = now
            if self.selected is None or self.selected == failed_preferred:
                self._select(path, now)

    def begin_probe(self, now):
        with self.lock:
            if self.last_probe is not None and now - self.last_probe < self.PROBE_INTERVAL_MS:
                return False
            self.last_probe = now
            return True

    def observe_round(self, now, observations):
        with self.lock:
            if self.last_round is not None and now - self.last_round < self.PROBE_INTERVAL_MS:
                return
            self.last_round = now
            for path, value in observations.items():
                history = [item for item in self.samples.get(path, []) if now - item[0] < self.HISTORY_MS][-7:]
                history.append((now, value if math.isfinite(value) and value >= 0 else math.inf))
                self.samples[path] = history
            if self.selected is None or self.selected not in observations:
                self._reset_challenge()
                return
            current = self._score(self.selected, now, False)
            best, best_score = None, math.inf
            for path in sorted(observations):
                if path == self.selected:
                    continue
                score = self._score(path, now, True)
                if score < best_score and self.worth_switching(current, score):
                    best, best_score = path, score
            if best is None:
                self._reset_challenge()
                return
            self.wins = self.wins + 1 if best == self.challenger else 1
            self.challenger = best
            if self.wins >= 3 and now - self.selected_at >= self.MINIMUM_HOLD_MS:
                self._select(best, now)

    @staticmethod
    def worth_switching(current, candidate):
        return (math.isfinite(current) and math.isfinite(candidate) and candidate >= 0 and
                current - candidate >= 8 and candidate <= current * .75)

    def _score(self, path, now, candidate):
        values = [value for at, value in self.samples.get(path, []) if now - at < self.HISTORY_MS]
        ok = sorted(value for value in values if math.isfinite(value))
        if len(values) < 3 or candidate and (len(ok) < 3 or any(not math.isfinite(value) for value in values[-3:])):
            return math.inf
        if not ok:
            return 2000
        median = (ok[(len(ok) - 1) // 2] + ok[len(ok) // 2]) / 2
        # One old spike/failure must not count as several independent wins.
        p80 = ok[math.ceil(len(ok) * .8) - 1]
        return median + .75 * (p80 - median) + 500 * max(0, len(values) - len(ok) - 1) / len(values)

    def _select(self, path, now):
        self.selected, self.selected_at = path, now
        self._reset_challenge()

    def _reset_challenge(self):
        self.challenger, self.wins = None, 0


class RelayNetworkPathSelector:
    HEAD_START_SECONDS = .15
    DISCOVERY_SECONDS = .25

    def __init__(self, paths=None, connect=None, clock=time.monotonic, discovery_seconds=None, background_probes=False):
        self.paths, self.attempt, self.clock = paths, connect, clock
        self.preferred, self.lock = {}, threading.Lock()
        self.discovery_seconds = self.DISCOVERY_SECONDS if discovery_seconds is None else discovery_seconds
        self.background_probes, self.probing = background_probes, False

    @staticmethod
    def identity(path):
        return repr(path) if path is not None else "<system>"

    async def discover(self, options):
        task = asyncio.create_task((self.paths or relay_network_paths)(options))

        def observe(completed):
            if not completed.cancelled():
                completed.exception()

        try:
            done, _ = await asyncio.wait([task], timeout=self.discovery_seconds)
            if not done or task.cancelled():
                return []
            return task.result()
        except asyncio.CancelledError:
            raise
        except Exception:
            return []  # Optional discovery cannot prevent a normal system-route connection.
        finally:
            # Do not wait for a resolver that ignores cancellation before using
            # the fallback; its eventual result/fault is still observed.
            task.add_done_callback(observe)
            if not task.done():
                task.cancel()

    async def connect(self, options):
        paths = await self.discover(options)
        attempt = self.attempt or _connect_tls_path
        if not paths:
            return await attempt(options, None)
        key = (options.server_address.lower(), options.port, options.tls_certificate_sha256, tuple(paths))
        with self.lock:
            if len(self.preferred) >= 32 and key not in self.preferred:
                self.preferred.clear()
            stability = self.preferred.setdefault(key, RelayPathStability())
        cached = stability.preferred(self.clock() * 1000)
        candidates = [None, *paths]
        matching = [path for path in candidates if self.identity(path) == cached]
        if matching:
            candidates.remove(matching[0])
            candidates.insert(0, matching[0])
        else:
            cached = None
        failed_preferred = False

        async def observed(options, path):
            nonlocal failed_preferred
            try:
                return await attempt(options, path)
            except Exception:
                if not asyncio.current_task().cancelling() and self.identity(path) == cached:
                    failed_preferred = True
                raise

        path, connection = await self.race(options, candidates, observed, self.HEAD_START_SECONDS if cached else 0)
        stability.connected(self.identity(path), self.clock() * 1000, cached if failed_preferred else None)
        if self.background_probes:
            self._start_probe(stability, options, candidates, attempt)
        return connection

    def _start_probe(self, stability, options, paths, attempt):
        with self.lock:
            scheduled_at = self.clock() * 1000
            if self.probing or not stability.begin_probe(scheduled_at):
                return
            self.probing = True

        def run():
            async def audit():
                current = await self.discover(options)
                if {self.identity(path) for path in current} != {self.identity(path) for path in paths if path is not None}:
                    return  # A new VPN, gateway, or address invalidates this snapshot.
                await self.probe_round(stability, options, paths, attempt, observed_at=scheduled_at)
            try:
                # The UI may close its per-call asyncio loop after a directory
                # request. A single bounded daemon owns these TLS-only probes.
                time.sleep(1)
                asyncio.run(audit())
            except Exception:
                pass
            finally:
                with self.lock:
                    self.probing = False

        try:
            threading.Thread(target=run, name="RelayPathQuality", daemon=True).start()
        except RuntimeError:
            with self.lock:
                self.probing = False

    async def probe_round(self, stability, options, paths, attempt, timeout=2, observed_at=None):
        round_at = self.clock() * 1000 if observed_at is None else observed_at
        async def measure(path):
            started, connection = time.monotonic(), None
            try:
                connection = await attempt(options, path)
                return math.inf if asyncio.current_task().cancelling() else (time.monotonic() - started) * 1000
            except (Exception, asyncio.CancelledError):
                return math.inf
            finally:
                if connection is not None:
                    _abort_relay_connection(connection)

        tasks = [asyncio.create_task(measure(path)) for path in paths]
        try:
            done, _ = await asyncio.wait(tasks, timeout=timeout)
            observations = {self.identity(path): task.result() if task in done else math.inf
                            for path, task in zip(paths, tasks)}
            stability.observe_round(round_at, observations)
        finally:
            for task in tasks:
                if not task.done():
                    task.cancel()
            # Keep the only audit slot until uncooperative native work ends.
            # This wait is never on the foreground connection's event loop.
            await asyncio.gather(*tasks, return_exceptions=True)

    @staticmethod
    async def race(options, paths, connect, head_start=0):
        if not paths:
            raise ValueError("At least one relay path is required")
        primary_failed = asyncio.Event()

        async def attempt(path, primary):
            try:
                if not primary and head_start > 0:
                    try:
                        await asyncio.wait_for(primary_failed.wait(), head_start)
                    except asyncio.TimeoutError:
                        pass
                return await connect(options, path)
            except BaseException:
                if primary:
                    primary_failed.set()
                raise

        tasks = {asyncio.create_task(attempt(path, index == 0)): path for index, path in enumerate(paths)}
        failures = []

        def release_loser(task):
            try:
                _abort_relay_connection(task.result())
            except BaseException:
                pass  # Observe cancellation and errors from every losing task.

        try:
            while tasks:
                done, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                for task in done:
                    path = tasks.pop(task)
                    try:
                        return path, task.result()
                    except (OSError, ValueError, ssl.SSLError) as error:
                        failures.append(error)
            identity = next((error for error in failures if isinstance(error, RelayIdentityError)), None)
            raise identity or (failures[0] if failures else ConnectionError("没有可用的中转网络路径。"))
        except asyncio.CancelledError:
            # Preserve an already observed pin rejection when another route stalls
            # until the shared deadline. Explicit cancellation remains cancellation.
            observed = failures + [task.exception() for task in tasks if task.done() and not task.cancelled()]
            identity = next((error for error in observed if isinstance(error, RelayIdentityError)), None)
            if identity is not None:
                raise asyncio.CancelledError() from identity
            raise
        finally:
            for task in tasks:
                task.add_done_callback(release_loser)
                task.cancel()


async def _connect_tls_path(options, path):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Explicit certificate pin is the trust anchor below.
    writer = sock = None
    try:
        if path is None:
            reader, writer = await asyncio.open_connection(options.server_address, options.port,
                ssl=context, ssl_handshake_timeout=10, limit=128 * 1024)
        else:
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            # A permission error rejects only this candidate. Never silently
            # remove the binding and mislabel a system-routed socket as Wi-Fi.
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BINDTODEVICE, path.interface.encode("utf-8") + b"\0")
            sock.bind((path.local_address, 0))
            sock.setblocking(False)
            await asyncio.get_running_loop().sock_connect(sock, (path.remote_address, options.port))
            reader, writer = await asyncio.open_connection(sock=sock, ssl=context,
                server_hostname=options.server_address, ssl_handshake_timeout=10, limit=128 * 1024)
        configure_transport(writer)
        certificate = writer.get_extra_info("ssl_object").getpeercert(binary_form=True)
        if not certificate or not hmac.compare_digest(hashlib.sha256(certificate).hexdigest().upper(),
                                                     options.tls_certificate_sha256):
            raise RelayIdentityError("中转 TLS 指纹不匹配，连接已拒绝；请核实服务器身份。")
        return reader, writer
    except BaseException as error:
        if writer is not None:
            if isinstance(error, RelayIdentityError):
                await close_writer(writer)
            else:
                _abort_relay_connection((None, writer))
        if sock is not None:
            sock.close()
        raise


_relay_path_selector = RelayNetworkPathSelector(background_probes=True)


async def _relay_deadline(operation):
    try:
        return await asyncio.wait_for(operation, TIMEOUT)
    except TimeoutError as error:
        cause = error.__cause__
        while cause is not None:
            if isinstance(cause, RelayIdentityError):
                raise RelayIdentityError("服务器身份与已保存的信息不符；原配置未更改。") from error
            cause = cause.__cause__
        raise


async def connect_tls(options):
    # The original deadline includes discovery and all candidate handshakes.
    return await _relay_deadline(_relay_path_selector.connect(options.validate()))


async def discover_server_identity(server_address, port):
    """Observe only: no token argument, application writes, persistence or reused TLS context."""
    host, port = _checked_endpoint(server_address, port)

    async def observe():
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE  # First-use observation, not an authenticated connection.
        writer = None
        try:
            _, writer = await asyncio.open_connection(host, port, ssl=context, ssl_handshake_timeout=10)
            certificate = writer.get_extra_info("ssl_object").getpeercert(binary_form=True)
            if not certificate:
                raise RelayIdentityError("无法取得服务器身份；未发送访问密钥。")
            return hashlib.sha256(certificate).hexdigest().upper()
        finally:
            await close_writer(writer)

    return await asyncio.wait_for(observe(), TIMEOUT)


def run_setup_request(operation, stop_event):
    """Run one UI worker operation; cancellation also closes its network request."""
    async def run():
        if stop_event.is_set():
            raise asyncio.CancelledError()
        task = asyncio.create_task(operation())
        try:
            while not stop_event.is_set():
                done, _ = await asyncio.wait([task], timeout=.05)
                if done:
                    if stop_event.is_set():
                        raise asyncio.CancelledError()
                    return task.result()
            raise asyncio.CancelledError()
        finally:
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
    try:
        return asyncio.run(run())
    except asyncio.CancelledError as error:
        raise ConnectionAbortedError("配置已取消；原配置未更改。") from error


def setup_error_message(error):
    if isinstance(error, RelayIdentityError):
        return "服务器登录或身份验证失败，请核实服务器是否重装，再用 root 密码重新登录；原配置未更改。"
    return "连接失败，请检查服务器地址、端口和网络；原配置未更改。"


async def hello(reader, writer, options, role, **fields):
    await write_json(writer, dict(version=1, role=role, token=options.access_token, **fields))
    response = await asyncio.wait_for(read_json(reader), TIMEOUT)
    ensure_success(response)
    return response


async def list_devices_async(options):
    return await _relay_deadline(_list_devices_pages(options))


NAMING_UNAVAILABLE = "此中继服务器尚不支持共享命名，请先更新服务器，再刷新在线列表。"


def naming_error_message(detail):
    # Return known messages, never echo arbitrary relay text (which may contain credentials).
    detail = str(detail)
    if "未知的中继连接类型" in detail:
        return NAMING_UNAVAILABLE
    if "无法读取" in detail:
        return "服务器的设备命名记录无法读取，请检查服务器存储；原文件未修改。"
    if "保存失败" in detail:
        return "设备名称保存失败，请检查服务器磁盘和目录权限；原名称未更改。"
    if "已离线" in detail or "不存在" in detail:
        return "设备已离线或不存在，请刷新在线列表后重试。"
    if "上限" in detail:
        return "已达到服务器的设备命名数量上限。"
    if "名称" in detail and ("80" in detail or "文字" in detail):
        return "请使用不超过 80 个字符的单行名称，不要包含控制字符。"
    return "共享名称保存失败，请刷新在线列表核对后重试。"


def normalize_device_name(name):
    if not isinstance(name, str):
        raise ValueError("设备名称必须是文字。")
    name = name.strip()
    if (any(unicodedata.category(char) in {"Cc", "Cf", "Cs", "Zl", "Zp"} for char in name)
            or len(name.encode("utf-16-le")) > 160):
        raise ValueError("请使用不超过 80 个字符的单行名称，不要包含控制字符。")
    return name


async def rename_device_async(options, name):
    options, name = options.validate(), normalize_device_name(name)
    async def send():
        reader, writer = await connect_tls(options)
        try:
            await write_json(writer, dict(version=1, role="rename-device", token=options.access_token,
                                          deviceId=options.device_id, name=name))
            result = await read_json(reader)
            if result.get('ok') is not True:
                if "访问密钥" in str(result.get('error', '')):
                    ensure_success(result)
                raise ConnectionError(naming_error_message(result.get('error', '')))
            if result.get("deviceId") != options.device_id or result.get("sharedName") != name:
                raise ValueError("中继未确认此设备的名称，请刷新在线列表核对后重试。")
        finally:
            await close_writer(writer)
    return await _relay_deadline(send())


def rename_device(options, name):
    return asyncio.run(rename_device_async(options, name))


async def _list_devices_pages(options):
    options = options.validate()
    result, seen, offset = [], set(), 0
    for _ in range(16):
        response = await _directory_page(options, offset)
        devices = response.get("devices")
        if not isinstance(devices, list) or len(devices) > 512:
            raise ValueError("中转在线列表无效。")
        for item in devices:
            if not isinstance(item, dict):
                continue
            try:
                device_id = str(uuid.UUID(item.get("deviceId", "")))
            except (ValueError, AttributeError, TypeError):
                continue
            if device_id in seen:
                continue
            seen.add(device_id)
            result.append(dict(deviceId=device_id, machineName=str(item.get("machineName", "未命名设备"))[:120],
                               platform=str(item.get("platform", "未知"))[:40], busy=item.get("busy") is True,
                               sharedName=str(item.get("sharedName", ""))[:80],
                               originalMachineName=str(item.get("originalMachineName", item.get("machineName", "")))[:120],
                               canRename=response.get("deviceNaming") is True,
                               namingUnavailableReason=(naming_error_message(response['deviceNamingError'])
                                                        if response.get('deviceNamingError') else NAMING_UNAVAILABLE),
                               **normalize_address_report(item)))
        if len(result) > 512:
            raise ValueError("中转在线设备过多。")
        next_offset = response.get("nextOffset")
        if next_offset is None:
            return result
        if type(next_offset) is not int or next_offset != offset + 32 or next_offset >= 512:
            raise ValueError("中转目录分页无效。")
        offset = next_offset
    raise ValueError("中转目录分页过多。")


async def _directory_page(options, offset):
    reader, writer = await connect_tls(options)
    try:
        return await hello(reader, writer, options, "directory", pageSize=32, offset=offset)
    finally:
        await close_writer(writer)


def list_devices(options):
    return asyncio.run(list_devices_async(options))


async def bridge(first, second):
    async def copy(reader, writer):
        while data := await reader.read(COPY_BUFFER_BYTES):
            writer.write(data)
            await asyncio.wait_for(writer.drain(), BRIDGE_WRITE_TIMEOUT_SECONDS)
    configure_transport(first[1])
    configure_transport(second[1])
    tasks = [asyncio.create_task(copy(first[0], second[1])),
             asyncio.create_task(copy(second[0], first[1]))]
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await asyncio.gather(close_writer(first[1], abort=True), close_writer(second[1], abort=True))


def connect_viewer(options, stop_event=None):
    """Return an ordinary TCP socket backed only by an authenticated relay tunnel."""
    options = options.validate()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    client = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        configure_loopback_socket(listener)
        configure_loopback_socket(client)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        client.connect(listener.getsockname())
        local, _ = listener.accept()
        configure_loopback_socket(local)
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
            local_reader, local_writer = await asyncio.open_connection(sock=local, limit=LOOPBACK_READER_LIMIT_BYTES)
            ready.set()
            await bridge((local_reader, local_writer), (reader, remote_writer))
        except BaseException as error:
            state["error"] = error
        finally:
            ready.set()
            await asyncio.gather(close_writer(remote_writer, abort=True), close_writer(local_writer, abort=True))
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
        self.address_refresh = asyncio.Event()

    def request_address_refresh(self):
        if self.stop_event.is_set() or self.loop is None or not self.online.is_set():
            return False
        try:
            self.loop.call_soon_threadsafe(self.address_refresh.set)
            return True
        except RuntimeError:
            return False

    def _address_report(self):
        return normalize_address_report(dict(directAddresses=local_direct_addresses(), directPort=self.local_port))

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
            registered = await hello(reader, writer, self.options, "host-control", deviceId=self.options.device_id,
                        machineName=self.machine_name, platform="Linux", **self._address_report())
            self.online.set()
            source = writer.get_extra_info("sockname")
            self._status(f"已上线到中转 {self.options.server_address}:{self.options.port}" +
                         (f" · 本地地址 {source[0]}" if source else "") +
                         (" · IP / 端口自动更新已启用" if registered.get("addressReporting") is True
                          else " · 服务器需更新才能显示 IP"))

            async def beat():
                while True:
                    with contextlib.suppress(asyncio.TimeoutError):
                        await asyncio.wait_for(self.address_refresh.wait(), 10)
                    self.address_refresh.clear()
                    await write_json(writer, dict(version=1, type="heartbeat", **self._address_report()))

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
                connect_local_host(self.local_port), 8)
            reader, remote_writer = await connect_tls(self.options)
            await hello(reader, remote_writer, self.options, "host-data",
                        deviceId=self.options.device_id, sessionId=session_id)
            await bridge((local_reader, local_writer), (reader, remote_writer))
        except (OSError, ValueError, asyncio.IncompleteReadError, TimeoutError):
            self._status("中转会话未建立或已结束，请确认本机被控端正在运行。")
        finally:
            await asyncio.gather(close_writer(local_writer, abort=True), close_writer(remote_writer, abort=True))


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
    value = json.loads(data)
    return None if value is None else RelayOptions.from_dict(value)


def save_settings(options, path=None):
    options = options.validate() if options is not None else None
    path = Path(path) if path is not None else settings_path()
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".relay-", dir=path.parent)
    try:
        os.chmod(temporary, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(options.to_dict() if options is not None else None, output, ensure_ascii=False)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)


def device_key_scope(options):
    server, port = _checked_endpoint(options.server_address, options.port)
    return hashlib.sha256((server.lower().rstrip('.') + '\n' + str(port) + '\n' +
        options.tls_certificate_sha256.upper()).encode('utf-8')).hexdigest()


def _device_key_store(options, directory=None):
    from remotedesk_linux_devices import Store
    import remotedesk_linux_startup as private
    root = Path(directory) if directory is not None else private.config_home() / 'remotedesk' / 'relay-device-keys'
    return Store(root / device_key_scope(options))


def load_device_key(options, directory=None):
    for node in _device_key_store(options, directory).load().nodes:
        if node.device_id == options.device_id:
            return node.password
    return ''


def save_device_key(options, password, directory=None):
    store = _device_key_store(options, directory)
    book = store.load()
    book.remember(options.server_address, options.port, password, device_id=options.device_id)
    store.save(book)
