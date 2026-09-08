"""Bounded LAN discovery and a current-user encrypted device book. No network authentication during discovery."""
from __future__ import annotations
import base64
from dataclasses import asdict, dataclass, replace
import ipaddress
import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import time
import unicodedata
import uuid
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
import remotedesk_linux_startup as private

DISCOVERY_PORTS = (56566, 40566)
HOST_PORTS = (56565, 40565, 40567)
REQUEST = b"RemoteDesk.Discover.v1"
IDENTITY_CAPABILITY = 1 << 22
IDENTITY_REQUEST = 33
IDENTITY_RESPONSE = 34
LIMIT = 20


def identity(value):
    try:
        parsed = uuid.UUID(str(value))
        return str(parsed) if parsed.int else ""
    except (ValueError, AttributeError): return ""


def label(value, limit=128):
    return "".join(c for c in str(value or "") if not unicodedata.category(c).startswith("C"))[:limit].strip()


def endpoint(address, port=""):
    host = str(address).strip(); explicit = bool(str(port).strip())
    if host.startswith("["):
        end = host.find("]")
        if end < 0: raise ValueError("IPv6 地址格式无效。")
        suffix = host[end+1:]; host = host[1:end]
        if suffix:
            if not suffix.startswith(":") or len(suffix) < 2: raise ValueError("端口格式无效。")
            port = suffix[1:]; explicit = True
    elif host.count(":") == 1:
        host, port = host.rsplit(":", 1); explicit = True
    if not host or len(host) > 253 or any(c.isspace() or c in "/\\@[]" for c in host):
        raise ValueError("请填写 IP 或主机名，不要包含协议或路径。")
    try: number = int(port) if explicit and str(port).isdigit() else 56565 if not explicit else 0
    except (ValueError, TypeError): number = 0
    if not 1 <= number <= 65535: raise ValueError("端口应为 1–65535；留空可自动探测。")
    if "%" not in host: host = host.lower().rstrip(".")
    if not host: raise ValueError("请填写有效 IP 或主机名。")
    return host, number, explicit


@dataclass(frozen=True, repr=False)
class Node:
    id: str
    host: str
    port: int
    password: str
    name: str = ""
    remark: str = ""
    device_id: str = ""
    auto_port: bool = True
    updated: float = 0

    @property
    def address(self): return f"[{self.host}]:{self.port}" if ":" in self.host else f"{self.host}:{self.port}"
    @property
    def title(self): return self.remark or self.name or self.address
    def __repr__(self): return "SavedDevice(<redacted>)"

    @classmethod
    def parse(cls, row):
        host, port, _ = endpoint(row["host"], row["port"])
        if not identity(row.get("id")) or not isinstance(row.get("password"), str) or not 0 < len(row["password"]) <= 4096:
            raise ValueError("设备记录无效。")
        return cls(identity(row["id"]), host, port, row["password"], label(row.get("name")), label(row.get("remark"), 80),
                   identity(row.get("device_id")), row.get("auto_port", True) is True, float(row.get("updated", 0)))


class Book:
    def __init__(self, nodes=()): self.nodes = list(nodes)[:LIMIT]
    def find(self, node_id): return next((n for n in self.nodes if n.id == node_id), None)
    def remember(self, host, port, password, name="", device_id="", previous_id="", remark=None, auto_port=True):
        if previous_id and self.find(previous_id) is None: return None  # A deleted connecting record stays deleted.
        fresh = Node.parse(dict(id=str(uuid.uuid4()), host=host, port=port, password=password, name=name,
                                device_id=device_id, auto_port=auto_port, updated=time.time()))
        matches = [n for n in self.nodes if n.id == previous_id or (fresh.device_id and n.device_id == fresh.device_id) or
                   (n.host == fresh.host and n.port == fresh.port)]
        existing = self.find(previous_id) or next((n for n in matches if n.device_id == fresh.device_id and fresh.device_id), None) or next(iter(matches), None)
        note = label(remark, 80) if remark is not None else next((n.remark for n in matches if n.remark), "")
        if existing:
            fresh = replace(fresh, id=existing.id, name=fresh.name or existing.name,
                            device_id=fresh.device_id or existing.device_id)
        fresh = replace(fresh, remark=note)
        self.nodes = [fresh] + [n for n in self.nodes if n not in matches]
        self.nodes = self.nodes[:LIMIT]
        return fresh
    def rename(self, node_id, note):
        self.nodes = [replace(n, remark=label(note, 80)) if n.id == node_id else n for n in self.nodes]
    def remove(self, node_id): self.nodes = [n for n in self.nodes if n.id != node_id]
    def encode(self): return json.dumps(dict(version=1, nodes=[asdict(n) for n in self.nodes]), ensure_ascii=False).encode("utf-8")
    @classmethod
    def decode(cls, raw):
        if len(raw) > 1024*1024: raise ValueError("设备记录过大。")
        value = json.loads(raw)
        if value.get("version") != 1 or not isinstance(value.get("nodes"), list) or len(value["nodes"]) > LIMIT:
            raise ValueError("设备记录格式无效。")
        book = cls(); ids = set()
        # Old duplicate entries are folded with newest endpoint first, preserving notes.
        for row in reversed(value["nodes"]):
            n = Node.parse(row)
            if n.id in ids: raise ValueError("重复的设备记录编号。")
            ids.add(n.id)
            saved = book.remember(n.host, n.port, n.password, n.name, n.device_id, remark=n.remark or None, auto_port=n.auto_port)
            book.nodes[0] = replace(saved, id=n.id, updated=n.updated)
        return book


class Store:
    AAD = b"RemoteDesk/Linux/Devices/v1"
    def __init__(self, directory=None):
        self.directory = Path(directory) if directory else private.config_home()/"remotedesk"/"devices"
        self.lock = threading.RLock()
    def load(self):
        with self.lock:
            if not self.directory.exists() and not self.directory.is_symlink(): return Book()
            private.private_directory(self.directory)
            try: encrypted = base64.b64decode(private.read_private(self.directory/"book", 1024*1024), validate=True)
            except FileNotFoundError: return Book()
            key = private.read_private(self.directory/"key", 32)
            return Book.decode(AESGCM(key).decrypt(encrypted[:12], encrypted[12:], self.AAD))
    def save(self, book):
        with self.lock:
            private.private_directory(self.directory)
            try: key = private.read_private(self.directory/"key", 32)
            except FileNotFoundError:
                key = AESGCM.generate_key(bit_length=256)
                try:
                    fd = os.open(self.directory/"key", os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW, 0o600)
                    with os.fdopen(fd, "wb") as f: f.write(key); f.flush(); os.fsync(f.fileno())
                except FileExistsError: key = private.read_private(self.directory/"key", 32)
            nonce = os.urandom(12)
            private.atomic_private_write(self.directory/"book", base64.b64encode(nonce+AESGCM(key).encrypt(nonce, book.encode(), self.AAD)))


def local_device_id(directory=None):
    directory = Path(directory) if directory else private.config_home()/"remotedesk"/"devices"
    private.private_directory(directory)
    try:
        value = identity(private.read_private(directory/"identity", 128).decode("ascii"))
        if not value: raise ValueError("本机设备标识损坏。")
        return value
    except FileNotFoundError:
        import remotedesk_linux_relay as relay
        saved = relay.load_settings() if directory == private.config_home()/"remotedesk"/"devices" else None
        value = identity(saved.device_id) if saved else str(uuid.uuid4())
        try:
            fd = os.open(directory/"identity", os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "wb") as f: f.write(value.encode("ascii")); f.flush(); os.fsync(f.fileno())
        except FileExistsError: return local_device_id(directory)
        return value


@dataclass(frozen=True)
class Device:
    host: str
    port: int
    name: str = ""
    platform: str = "RemoteDesk"
    listening: bool = True
    device_id: str = ""
    advertised: bool = True
    @property
    def address(self): return f"{self.host}:{self.port}"


def parse_response(data, source):
    if not 2 <= len(data) <= 8192: return None
    try:
        value = json.loads(data.decode("utf-8"))
        if not isinstance(value, dict) or value.get("Type") != "RemoteDesk.Discover.Response.v1": return None
        port = value.get("Port"); running = value.get("IsHostRunning", True)
        if type(port) is not int or not 1 <= port <= 65535 or type(running) is not bool: return None
        address = ipaddress.ip_address(source)
        if address.is_unspecified or address.is_multicast: return None
        return Device(str(address), port, label(value.get("MachineName")), label(value.get("Platform"), 40), running, identity(value.get("DeviceId")))
    except (ValueError, TypeError, UnicodeError, RecursionError): return None


def candidates(node, devices):
    ready = [d for d in devices if d.listening]
    same_id = [d for d in ready if node.device_id and node.device_id == d.device_id]
    exact = [d for d in ready if d.host == node.host and d.port == node.port]
    same_ip = [d for d in ready if d.host == node.host]
    same_name = [d for d in ready if d.advertised and node.name and d.name.casefold() == node.name.casefold()]
    return same_id or exact or same_ip or same_name


def interfaces():
    broadcasts, own = {"255.255.255.255"}, {"127.0.0.1"}
    try:
        result = subprocess.run(["ip", "-j", "-4", "addr", "show"], capture_output=True, timeout=1, check=True)
        for link in json.loads(result.stdout):
            for info in link.get("addr_info", []):
                if info.get("local"): own.add(info["local"])
                if info.get("broadcast"): broadcasts.add(info["broadcast"])
    except (OSError, ValueError, subprocess.SubprocessError): pass
    return broadcasts, own


class Scanner:
    def __init__(self): self.cancelled = threading.Event(); self.sockets = []; self.lock = threading.Lock()
    def close(self):
        self.cancelled.set()
        with self.lock:
            for sock in self.sockets:
                try: sock.close()
                except OSError: pass
            self.sockets.clear()
    def _socket(self, sock):
        with self.lock:
            if self.cancelled.is_set(): sock.close(); raise OSError("Discovery canceled")
            self.sockets.append(sock)
        return sock
    def scan(self, target=None, nodes=(), *, discovery_ports=DISCOVERY_PORTS, host_ports=HOST_PORTS, seconds=1.5):
        if self.cancelled.is_set(): return []
        destinations, own = interfaces(); explicit = set()
        if target:
            explicit = {row[4][0] for row in socket.getaddrinfo(target, 0, socket.AF_INET, socket.SOCK_DGRAM)}
            explicit = set(sorted(explicit)[:4]); destinations = explicit
        else:
            for node in nodes:
                try:
                    address = ipaddress.ip_address(node.host)
                    if isinstance(address, ipaddress.IPv4Address) and not address.is_unspecified and not address.is_multicast:
                        destinations.add(node.host)
                except ValueError: pass
        result = {}; started = time.monotonic()
        try:
            udp = self._socket(socket.socket(socket.AF_INET, socket.SOCK_DGRAM)); udp.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1); udp.settimeout(.12)
            udp.bind(("0.0.0.0", 0)); rounds = 0
            while not self.cancelled.is_set() and time.monotonic()-started < seconds:
                if rounds == 0 or (rounds == 1 and time.monotonic()-started >= .6):
                    for host in list(destinations)[:40]:
                        for port in discovery_ports:
                            try: udp.sendto(REQUEST, (host, port))
                            except OSError: pass
                    rounds += 1
                try: data, source = udp.recvfrom(8193)
                except (socket.timeout, ConnectionResetError, ConnectionRefusedError): continue
                if explicit and source[0] not in explicit: continue
                if not explicit and (source[0] in own or ipaddress.ip_address(source[0]).is_loopback): continue
                item = parse_response(data, source[0])
                if item and (item.address in result or len(result) < 64): result[item.address] = item
            if explicit and not any(d.listening for d in result.values()):
                ports = list(dict.fromkeys([n.port for n in nodes if n.host == target][:4]+list(host_ports)))
                for host in explicit:
                    for port in ports:
                        if self.cancelled.is_set() or time.monotonic()-started > 4.5: break
                        try:
                            with self._socket(socket.socket()) as tcp:
                                tcp.settimeout(.25); tcp.connect((host, port)); banner = b""
                                while len(banner) < 4:
                                    part = tcp.recv(4-len(banner))
                                    if not part: break
                                    banner += part
                                if banner == b"RDK1": result[f"{host}:{port}"] = Device(host, port, advertised=False)
                        except OSError: pass
            return sorted(result.values(), key=lambda d:(not d.listening, d.name.casefold(), d.host, d.port))
        except OSError:
            if self.cancelled.is_set(): return []
            raise
        finally: self.close()
