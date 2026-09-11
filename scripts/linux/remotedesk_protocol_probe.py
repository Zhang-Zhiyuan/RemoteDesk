#!/usr/bin/env python3
"""RemoteDesk protocol probe for Linux/WSL.

This is a small compatibility probe, not a viewer. It authenticates to a
RemoteDesk host, sends viewer codec capability, and decodes the first few
server messages to prove the encrypted protocol is usable from Linux.
"""

from __future__ import annotations

import argparse
import errno
import hashlib
import hmac
import json
import os
import shutil
import socket
import stat
import struct
import sys
import tempfile
import threading
import time
import unicodedata
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterator
from uuid import uuid4

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


MAGIC = b"RDK1"
AUTH_MARKER = b"AUTH"
SESSION_INFO = b"RemoteDesk session v1"
CLIENT_TO_SERVER_INFO = b"client->server"
SERVER_TO_CLIENT_INFO = b"server->client"

MESSAGE_FRAME = 1
MESSAGE_INPUT = 2
MESSAGE_CONTROL = 3
MESSAGE_PING = 4
MESSAGE_PONG = 5
MESSAGE_VIDEO_FRAME = 6

INPUT_KEY_DOWN = 5
INPUT_KEY_UP = 6

CONTROL_CAPTURE_TARGET_LIST = 1
CONTROL_SELECT_CAPTURE_TARGET = 2
CONTROL_CAPTURE_TARGET_CHANGED = 3
CONTROL_CLIPBOARD_GET_TEXT = 4
CONTROL_CLIPBOARD_SET_TEXT = 5
CONTROL_CLIPBOARD_TEXT = 6
CONTROL_CLIPBOARD_STATUS = 7
CONTROL_FILE_TRANSFER_START = 8
CONTROL_FILE_TRANSFER_CHUNK = 9
CONTROL_FILE_TRANSFER_COMPLETE = 10
CONTROL_FILE_TRANSFER_STATUS = 11
CONTROL_DEVICE_INFO = 12
CONTROL_DEVICE_IDENTITY_REQUEST = 33
CONTROL_DEVICE_IDENTITY = 34
CONTROL_FILE_TRANSFER_RECEIPT = 35
CAPABILITY_DEVICE_IDENTITY = 1 << 22
CAPABILITY_CLIPBOARD_PASTE_SHORTCUT = 1 << 23
CAPABILITY_FILE_TRANSFER_RECEIPT = 1 << 24
CONTROL_VIEWER_INFO = 13
CONTROL_VIDEO_KEY_FRAME_REQUEST = 14
CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES = 18
CONTROL_FILE_TRANSFER_CHECKSUM = 19
CONTROL_VIEWER_CAPABILITIES = 20
CONTROL_FILE_TRANSFER_CANCEL = 21
CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW = 22
CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES = 23
CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES = 24
CONTROL_REMOTE_UPDATE_START = 25
CONTROL_REMOTE_UPDATE_PACKAGE_REQUEST = 26
CONTROL_DEVICE_BUILD_INFO = 27
CONTROL_LOW_LATENCY_VIDEO_OFFER = 28
CONTROL_LOW_LATENCY_VIDEO_READY = 29
CONTROL_LOW_LATENCY_VIDEO_STOP = 30
CONTROL_LOW_LATENCY_VIDEO_STOPPED = 31
CONTROL_SESSION_REJECTED = 32
LOW_LATENCY_FALLBACK_GENERIC = 1
LOW_LATENCY_FALLBACK_BIND_TIMEOUT = 3
LOW_LATENCY_FALLBACK_PRESERVE_UDP_INPUT = 4

VIDEO_CODEC_JPEG = 1
VIDEO_CODEC_H264_ANNEX_B = 1 << 1
FRAME_ENCODING_JPEG = 1
FRAME_ENCODING_H264_ANNEX_B = 2
FRAME_FLAG_KEY_FRAME = 1
FRAME_FLAG_CODEC_CONFIG = 1 << 1
LOW_LATENCY_DATAGRAM_KIND_BIND_PROBE = 1
LOW_LATENCY_DATAGRAM_KIND_BIND_ACK = 2
LOW_LATENCY_DATAGRAM_KIND_FRAME_FRAGMENT = 3
LOW_LATENCY_DATAGRAM_KIND_FEEDBACK = 4
LOW_LATENCY_DATAGRAM_KIND_FEEDBACK_V2 = 5
LOW_LATENCY_DATAGRAM_KIND_FRAME_XOR_PARITY = 6
LOW_LATENCY_DATAGRAM_KIND_MOUSE_MOVE = 7
LOW_LATENCY_DATAGRAM_KIND_MOUSE_MOVE_APPLIED_ACK = 8
LOW_LATENCY_DATAGRAM_KIND_HEARTBEAT = 9
VIDEO_FRAME_HEADER_BYTES = 32
FILE_TRANSFER_CHUNK_BYTES = 128 * 1024
RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES = 32 * 1024
MAX_FILE_TRANSFER_BYTES = 1024 * 1024 * 1024
MAX_FILES_PER_RECEIVE_SESSION = 128
MAX_DECLARED_BYTES_PER_RECEIVE_SESSION = 2 * MAX_FILE_TRANSFER_BYTES
MINIMUM_FREE_SPACE_RESERVE_BYTES = 256 * 1024 * 1024
INPUT_PAYLOAD_LENGTH = 14
MAX_FRAME_PAYLOAD_BYTES = 32 * 1024 * 1024
MAX_FRAME_DIMENSION = 32_768
MAX_FRAME_PIXELS = 16_777_216
MAX_CONTROL_PAYLOAD_BYTES = 2 * 1024 * 1024
MAX_SAFE_FILE_NAME_LENGTH = 180
MAX_SAFE_EXTENSION_LENGTH = 32
MAX_UNIQUE_FILE_PATH_ATTEMPTS = 10_000
MAX_SAFE_ARCHIVE_ENTRY_BYTES = 4_096
MAX_CONTROL_ITEMS = 64
MAX_CLIPBOARD_TEXT_CHARS = 256_000
MAX_CONTROL_STRING_CHARS = 4_096
SHA256_HEX_LENGTH = 64
MAX_PASSWORD_UTF8_BYTES = 4_096
FILE_TRANSFER_CHECKSUM_ALGORITHM = "SHA256"
CAPABILITY_REMOTE_DESKTOP = 1 << 0
CAPABILITY_INPUT_CONTROL = 1 << 1
CAPABILITY_CLIPBOARD_TEXT = 1 << 2
CAPABILITY_FILE_RECEIVE = 1 << 3
CAPABILITY_CAPTURE_TARGET_SELECTION = 1 << 4
CAPABILITY_REMOTE_START = 1 << 5
CAPABILITY_FILE_DROP_PASTE = 1 << 6
CAPABILITY_FILE_SEND = 1 << 7
CAPABILITY_FILE_CHECKSUM = 1 << 8
CAPABILITY_FILE_TRANSFER_CANCEL = 1 << 9
CAPABILITY_FILE_TRANSFER_PREVIEW = 1 << 10
CAPABILITY_REMOTE_UPDATE = 1 << 11
CAPABILITY_CLIPBOARD_SEQUENCE_TRACKING = 1 << 12
CAPABILITY_LOW_LATENCY_UDP_VIDEO = 1 << 13
CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK = 1 << 14
CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC = 1 << 15
CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT = 1 << 16
CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK = 1 << 17
CAPABILITY_SHORT_GOP_H264 = 1 << 18
CAPABILITY_HIGH_FRAME_RATE_H264 = 1 << 19
CAPABILITY_AUTHENTICATED_UDP_HEARTBEAT = 1 << 20
CAPABILITY_HIGH_QUALITY_JPEG = 1 << 21
BASE_VIEWER_CAPABILITIES = (
    CAPABILITY_SHORT_GOP_H264
    | CAPABILITY_HIGH_QUALITY_JPEG
)
STALE_TEMPORARY_FILE_SECONDS = 24 * 60 * 60
INCOMING_TEMPORARY_FILE_PREFIX = ".remotedesk-"
INCOMING_TEMPORARY_FILE_SUFFIX = ".rdtransfer"

CAPABILITIES = [
    (1 << 0, "RemoteDesktop"),
    (1 << 1, "InputControl"),
    (1 << 2, "ClipboardText"),
    (1 << 3, "FileReceive"),
    (1 << 4, "CaptureTargetSelection"),
    (1 << 5, "RemoteStart"),
    (1 << 6, "FileDropPaste"),
    (1 << 7, "FileSend"),
    (1 << 8, "FileChecksum"),
    (1 << 9, "FileTransferCancel"),
    (1 << 10, "FileTransferPreview"),
    (1 << 11, "RemoteUpdate"),
    (1 << 12, "ClipboardSequenceTracking"),
    (1 << 13, "LowLatencyUdpVideo"),
    (1 << 14, "UdpVideoCongestionFeedback"),
    (1 << 15, "LowLatencyUdpVideoXorFec"),
    (1 << 16, "LowLatencyUdpMouseInput"),
    (1 << 17, "LowLatencyUdpMouseInputAppliedAck"),
    (1 << 18, "ShortGopH264"),
    (1 << 19, "HighFrameRateH264"),
    (1 << 20, "AuthenticatedUdpHeartbeat"),
    (1 << 21, "HighQualityJpeg"),
    (1 << 23, "ClipboardPasteShortcut"),
]

RESERVED_FILE_NAMES = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    "COM1",
    "COM2",
    "COM3",
    "COM4",
    "COM5",
    "COM6",
    "COM7",
    "COM8",
    "COM9",
    "LPT1",
    "LPT2",
    "LPT3",
    "LPT4",
    "LPT5",
    "LPT6",
    "LPT7",
    "LPT8",
    "LPT9",
}


class ProtocolError(Exception):
    pass


class TransferCancelledError(Exception):
    """Raised when a cancellable file enumeration or archive operation is stopped."""


def raise_if_transfer_cancelled(cancel_event: threading.Event | None) -> None:
    if cancel_event is not None and cancel_event.is_set():
        raise TransferCancelledError("file transfer operation was cancelled")


def resolve_password_argument(password: str | None, password_fd: int | None) -> str:
    if password and password_fd is not None:
        raise ValueError("password and password fd are mutually exclusive")

    if password_fd is None:
        resolved = password or ""
    else:
        if password_fd < 0:
            raise ValueError("password fd must be non-negative")
        data = bytearray()
        try:
            while len(data) <= MAX_PASSWORD_UTF8_BYTES:
                chunk = os.read(
                    password_fd,
                    min(1024, MAX_PASSWORD_UTF8_BYTES + 1 - len(data)),
                )
                if not chunk:
                    break
                data.extend(chunk)
        finally:
            os.close(password_fd)
        if len(data) > MAX_PASSWORD_UTF8_BYTES:
            raise ValueError("password exceeds the UTF-8 byte limit")
        try:
            resolved = data.decode("utf-8")
        except UnicodeDecodeError as ex:
            raise ValueError("password fd did not contain valid UTF-8") from ex
        if resolved.endswith("\r\n"):
            resolved = resolved[:-2]
        elif resolved.endswith("\n"):
            resolved = resolved[:-1]

    if not resolved:
        raise ValueError("password is required")
    if "\x00" in resolved:
        raise ValueError("password must not contain NUL")
    if len(resolved.encode("utf-8")) > MAX_PASSWORD_UTF8_BYTES:
        raise ValueError("password exceeds the UTF-8 byte limit")
    return resolved


@dataclass
class ProbeResult:
    host: str
    port: int
    authenticated: bool = False
    device: dict[str, Any] | None = None
    build_stamp: str = ""
    capture_targets: list[dict[str, str]] = field(default_factory=list)
    selected_target: dict[str, str] | None = None
    frames: list[dict[str, Any]] = field(default_factory=list)
    controls: list[dict[str, Any]] = field(default_factory=list)
    pings: list[dict[str, Any]] = field(default_factory=list)
    file_statuses: list[dict[str, Any]] = field(default_factory=list)
    sent_file: dict[str, Any] | None = None
    received_files: list[dict[str, Any]] = field(default_factory=list)
    remote_file_request: bool = False
    remote_file_request_skipped_reason: str | None = None
    elapsed_ms: float = 0

    def ok(self) -> bool:
        file_ok = self.sent_file is None or self.sent_file.get("remoteSaved") is True
        ping_ok = not self.pings or all(item.get("success") is True for item in self.pings)
        return self.authenticated and self.device is not None and file_ok and ping_ok


@dataclass
class IncomingFileTransfer:
    transfer_id: str
    file_name: str
    file_length: int
    final_path: Path
    temporary_path: Path
    stream: Any
    bytes_received: int = 0
    sha256: Any = field(default_factory=hashlib.sha256)
    expected_sha256: bytes | None = None
    require_checksum: bool = False
    active: bool = True

    def abort(self) -> None:
        self.active = False
        try:
            self.stream.close()
        except OSError:
            pass
        try:
            self.temporary_path.unlink(missing_ok=True)
        except OSError:
            pass


class IncomingFileTransferBudget:
    """Bounds disk commitments made by one authenticated receive session."""

    def __init__(
        self,
        available_space_provider: Callable[[Path], int] | None = None,
    ) -> None:
        self._available_space_provider = (
            available_space_provider
            if available_space_provider is not None
            else lambda directory: int(shutil.disk_usage(directory).free)
        )
        self.accepted_transfer_count = 0
        self.accepted_declared_bytes = 0

    def reserve(self, receive_directory: Path, file_length: int) -> None:
        validate_file_length(file_length)
        if self.accepted_transfer_count >= MAX_FILES_PER_RECEIVE_SESSION:
            raise ProtocolError(
                f"receive session reached its {MAX_FILES_PER_RECEIVE_SESSION}-file limit"
            )
        if (
            self.accepted_declared_bytes
            > MAX_DECLARED_BYTES_PER_RECEIVE_SESSION - file_length
        ):
            raise ProtocolError(
                "receive session exceeded its declared-byte limit"
            )

        available_bytes = int(self._available_space_provider(receive_directory))
        required_bytes = file_length + MINIMUM_FREE_SPACE_RESERVE_BYTES
        if available_bytes < required_bytes:
            raise OSError(
                "insufficient receive-disk space: "
                f"{required_bytes} bytes required, {max(0, available_bytes)} available"
            )

        self.accepted_transfer_count += 1
        self.accepted_declared_bytes += file_length


class SecureSession:
    def __init__(
        self,
        client_to_server_key: bytes,
        server_to_client_key: bytes,
        is_server: bool = False,
    ) -> None:
        send_key = server_to_client_key if is_server else client_to_server_key
        receive_key = client_to_server_key if is_server else server_to_client_key
        self._send_cipher = AESGCM(send_key)
        self._receive_cipher = AESGCM(receive_key)
        self._send_sequence = 0
        self._receive_sequence = 0

    def encrypt(self, plain: bytes) -> bytes:
        nonce = self._nonce(self._send_sequence)
        self._send_sequence += 1
        return self._send_cipher.encrypt(nonce, plain, None)

    def decrypt(self, encrypted: bytes) -> bytes:
        nonce = self._nonce(self._receive_sequence)
        self._receive_sequence += 1
        return self._receive_cipher.decrypt(nonce, encrypted, None)

    @staticmethod
    def _nonce(sequence: int) -> bytes:
        return b"\x00\x00\x00\x00" + struct.pack("<q", sequence)


class Cursor:
    def __init__(self, payload: bytes) -> None:
        self._payload = payload
        self._offset = 0

    def read_u8(self) -> int:
        self._ensure(1)
        value = self._payload[self._offset]
        self._offset += 1
        return value

    def read_i32(self) -> int:
        self._ensure(4)
        value = struct.unpack_from("<i", self._payload, self._offset)[0]
        self._offset += 4
        return value

    def read_i64(self) -> int:
        self._ensure(8)
        value = struct.unpack_from("<q", self._payload, self._offset)[0]
        self._offset += 8
        return value

    def read_bool(self) -> bool:
        return self.read_u8() != 0

    def read_dotnet_string(self, max_chars: int = MAX_CONTROL_STRING_CHARS) -> str:
        length = self._read_7bit_int()
        if length < 0:
            raise ProtocolError("negative string length")
        self._ensure(length)
        raw = self._payload[self._offset : self._offset + length]
        self._offset += length
        try:
            value = raw.decode("utf-8")
        except UnicodeDecodeError as ex:
            raise ProtocolError("control string is not valid UTF-8") from ex
        if len(value.encode("utf-16-le")) // 2 > max_chars:
            raise ProtocolError("control string is too large")
        return value

    def read_bytes(self, length: int) -> bytes:
        self._ensure(length)
        raw = self._payload[self._offset : self._offset + length]
        self._offset += length
        return raw

    def remaining(self) -> int:
        return len(self._payload) - self._offset

    def ensure_done(self) -> None:
        if self.remaining() != 0:
            raise ProtocolError(f"{self.remaining()} trailing bytes")

    def _read_7bit_int(self) -> int:
        value = 0
        shift = 0
        for _ in range(5):
            current = self.read_u8()
            value |= (current & 0x7F) << shift
            if (current & 0x80) == 0:
                return value
            shift += 7
        raise ProtocolError("invalid 7-bit string length")

    def _ensure(self, length: int) -> None:
        if length < 0 or self._offset + length > len(self._payload):
            raise ProtocolError("payload ended early")


def recv_exact(
    sock: socket.socket,
    length: int,
    deadline: float | None = None,
) -> bytes:
    if length < 0:
        raise ProtocolError("negative receive length")

    buffer = bytearray(length)
    view = memoryview(buffer)
    offset = 0
    while offset < length:
        if deadline is not None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("RemoteDesk authentication deadline expired")
            sock.settimeout(remaining)
        received = sock.recv_into(view[offset:], length - offset)
        if received == 0:
            raise EOFError("connection closed")
        offset += received
    return bytes(buffer)


def create_session(password: str, nonce: bytes, is_server: bool = False) -> SecureSession:
    password_key = hashlib.sha256(password.encode("utf-8")).digest()
    master_key = hmac.new(password_key, SESSION_INFO + nonce, hashlib.sha256).digest()
    client_to_server_key = hmac.new(master_key, CLIENT_TO_SERVER_INFO, hashlib.sha256).digest()
    server_to_client_key = hmac.new(master_key, SERVER_TO_CLIENT_INFO, hashlib.sha256).digest()
    return SecureSession(client_to_server_key, server_to_client_key, is_server=is_server)


def authenticate_server(
    sock: socket.socket,
    password: str,
    nonce: bytes | None = None,
    deadline: float | None = None,
) -> SecureSession:
    if nonce is None:
        nonce = os.urandom(32)
    if len(nonce) != 32:
        raise ProtocolError("server nonce must be 32 bytes")

    sock.sendall(MAGIC + nonce)
    marker = recv_exact(sock, len(AUTH_MARKER), deadline)
    if marker != AUTH_MARKER:
        raise PermissionError("RemoteDesk client did not send AUTH marker")

    proof = recv_exact(sock, 32, deadline)
    password_key = hashlib.sha256(password.encode("utf-8")).digest()
    expected = hmac.new(password_key, nonce, hashlib.sha256).digest()
    authenticated = hmac.compare_digest(proof, expected)
    sock.sendall(b"\x01" if authenticated else b"\x00")
    if not authenticated:
        raise PermissionError("RemoteDesk client authentication rejected")
    return create_session(password, nonce, is_server=True)


def authenticate(sock: socket.socket, password: str) -> SecureSession:
    magic = recv_exact(sock, len(MAGIC))
    if magic != MAGIC:
        raise ProtocolError(f"target magic is {magic!r}, not RemoteDesk")
    nonce = recv_exact(sock, 32)
    password_key = hashlib.sha256(password.encode("utf-8")).digest()
    proof = hmac.new(password_key, nonce, hashlib.sha256).digest()
    sock.sendall(AUTH_MARKER + proof)
    result = recv_exact(sock, 1)
    if result != b"\x01":
        raise PermissionError("RemoteDesk authentication rejected")
    return create_session(password, nonce)


def write_message(sock: socket.socket, session: SecureSession, message_type: int, payload: bytes) -> None:
    plain = bytes([message_type]) + struct.pack("<i", len(payload)) + payload
    encrypted = session.encrypt(plain)
    header = struct.pack("<i", len(encrypted))
    sock.sendall(header + encrypted)


def configure_low_latency_socket(sock: socket.socket) -> None:
    try:
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except OSError:
        pass

    rearm_tcp_quickack(sock)


def rearm_tcp_quickack(sock: socket.socket) -> bool:
    quick_ack = getattr(socket, "TCP_QUICKACK", None)
    if quick_ack is None:
        return False
    try:
        sock.setsockopt(socket.IPPROTO_TCP, quick_ack, 1)
        return True
    except OSError:
        return False


def read_message(sock: socket.socket, session: SecureSession) -> tuple[int, bytes]:
    encrypted_length = struct.unpack("<i", recv_exact(sock, 4))[0]
    if encrypted_length < 21 or encrypted_length > MAX_FRAME_PAYLOAD_BYTES + 21:
        raise ProtocolError(f"invalid encrypted message length {encrypted_length}")
    plain = session.decrypt(recv_exact(sock, encrypted_length))
    if len(plain) < 5:
        raise ProtocolError("plain message header missing")
    message_type = plain[0]
    payload_length = struct.unpack_from("<i", plain, 1)[0]
    if payload_length < 0 or len(plain) != 5 + payload_length:
        raise ProtocolError("plain message length mismatch")
    max_payload_bytes = max_message_payload_bytes(message_type)
    if payload_length > max_payload_bytes:
        raise ProtocolError(f"message payload length {payload_length} exceeds limit {max_payload_bytes}")
    return message_type, plain[5:]


def max_message_payload_bytes(message_type: int) -> int:
    if message_type in (MESSAGE_FRAME, MESSAGE_VIDEO_FRAME):
        return MAX_FRAME_PAYLOAD_BYTES
    if message_type == MESSAGE_INPUT:
        return INPUT_PAYLOAD_LENGTH
    if message_type == MESSAGE_CONTROL:
        return MAX_CONTROL_PAYLOAD_BYTES
    if message_type in (MESSAGE_PING, MESSAGE_PONG):
        return 0
    raise ProtocolError(f"unknown message type {message_type}")


def encode_viewer_info(codecs: int) -> bytes:
    return bytes([CONTROL_VIEWER_INFO]) + struct.pack("<i", codecs)


def encode_viewer_capabilities(capabilities: int) -> bytes:
    return bytes([CONTROL_VIEWER_CAPABILITIES]) + struct.pack("<i", capabilities)


def encode_video_key_frame_request() -> bytes:
    return bytes([CONTROL_VIDEO_KEY_FRAME_REQUEST])


def encode_video_frame(
    width: int,
    height: int,
    flags: int,
    encoded: bytes,
    capture_ms: float = 0.0,
    encode_ms: float = 0.0,
) -> bytes:
    validate_frame_dimensions(width, height)
    if not encoded:
        raise ValueError("video frame payload must not be empty")
    known_flags = FRAME_FLAG_KEY_FRAME | FRAME_FLAG_CODEC_CONFIG
    if flags & ~known_flags:
        raise ValueError("video frame contains unsupported flags")
    return (
        struct.pack(
            "<iiiidd",
            FRAME_ENCODING_H264_ANNEX_B,
            width,
            height,
            flags,
            capture_ms,
            encode_ms,
        )
        + encoded
    )


def encode_device_info(machine_name: str, platform: str, capabilities: int) -> bytes:
    return (
        bytes([CONTROL_DEVICE_INFO])
        + encode_bounded_dotnet_string(machine_name, "RemoteDesk")
        + encode_bounded_dotnet_string(platform, "Unknown")
        + struct.pack("<i", capabilities)
    )


def encode_device_build_info(build_stamp: str) -> bytes:
    return bytes([CONTROL_DEVICE_BUILD_INFO]) + encode_bounded_dotnet_string(build_stamp, "")


def encode_device_identity(device_id: str) -> bytes:
    import uuid
    parsed = uuid.UUID(device_id)
    if not parsed.int: raise ValueError("Invalid device identity")
    return bytes([CONTROL_DEVICE_IDENTITY]) + encode_bounded_dotnet_string(str(parsed), "")


def encode_capture_target_list(targets: list[tuple[str, str]]) -> bytes:
    target_items = targets[:MAX_CONTROL_ITEMS]
    payload = bytes([CONTROL_CAPTURE_TARGET_LIST]) + struct.pack("<i", len(target_items))
    for target_id, display_name in target_items:
        payload += encode_bounded_dotnet_string(target_id, "capture-target")
        payload += encode_bounded_dotnet_string(display_name, "Screen")
    return payload


def encode_capture_target_changed(target_id: str, display_name: str) -> bytes:
    return (
        bytes([CONTROL_CAPTURE_TARGET_CHANGED])
        + encode_bounded_dotnet_string(target_id, "capture-target")
        + encode_bounded_dotnet_string(display_name, "Screen")
    )


def encode_select_capture_target(target_id: str) -> bytes:
    return bytes([CONTROL_SELECT_CAPTURE_TARGET]) + encode_bounded_dotnet_string(target_id, "capture-target")


def encode_clipboard_text(text: str) -> bytes:
    return bytes([CONTROL_CLIPBOARD_TEXT]) + encode_dotnet_string(text, MAX_CLIPBOARD_TEXT_CHARS)


def encode_clipboard_set_text(text: str) -> bytes:
    return bytes([CONTROL_CLIPBOARD_SET_TEXT]) + encode_dotnet_string(text, MAX_CLIPBOARD_TEXT_CHARS)


def encode_clipboard_status(success: bool, message: str) -> bytes:
    return (
        bytes([CONTROL_CLIPBOARD_STATUS])
        + (b"\x01" if success else b"\x00")
        + encode_bounded_dotnet_string(message, "Clipboard operation completed.")
    )


def encode_session_rejected(message: str) -> bytes:
    return bytes([CONTROL_SESSION_REJECTED]) + encode_bounded_dotnet_string(
        message,
        "Remote host cannot accept another viewer.",
    )


def encode_key_input(kind: int, virtual_key: int) -> bytes:
    if kind not in (INPUT_KEY_DOWN, INPUT_KEY_UP):
        raise ProtocolError("unsupported input kind")
    if virtual_key <= 0 or virtual_key > 0xFE:
        raise ProtocolError("virtual key is out of range")
    return bytes([kind, 0]) + struct.pack("<iii", 0, 0, virtual_key)


def encode_file_transfer_start(transfer_id: str, file_name: str, file_length: int) -> bytes:
    validate_control_string(transfer_id)
    validate_control_string(file_name)
    validate_file_length(file_length)
    return (
        bytes([CONTROL_FILE_TRANSFER_START])
        + encode_dotnet_string(transfer_id)
        + encode_dotnet_string(file_name)
        + struct.pack("<q", file_length)
    )


def encode_remote_update_start(transfer_id: str, file_name: str, file_length: int) -> bytes:
    validate_control_string(transfer_id)
    validate_control_string(file_name)
    validate_file_length(file_length)
    return (
        bytes([CONTROL_REMOTE_UPDATE_START])
        + encode_dotnet_string(transfer_id)
        + encode_dotnet_string(file_name)
        + struct.pack("<q", file_length)
    )


def encode_remote_update_package_request() -> bytes:
    return bytes([CONTROL_REMOTE_UPDATE_PACKAGE_REQUEST])


def encode_file_transfer_chunk(transfer_id: str, offset: int, chunk: bytes) -> bytes:
    validate_control_string(transfer_id)
    if (
        offset < 0
        or offset > MAX_FILE_TRANSFER_BYTES
        or not chunk
        or len(chunk) > FILE_TRANSFER_CHUNK_BYTES
        or offset > MAX_FILE_TRANSFER_BYTES - len(chunk)
    ):
        raise ProtocolError("invalid file chunk length")
    return (
        bytes([CONTROL_FILE_TRANSFER_CHUNK])
        + encode_dotnet_string(transfer_id)
        + struct.pack("<q", offset)
        + struct.pack("<i", len(chunk))
        + chunk
    )


def encode_file_transfer_complete(transfer_id: str) -> bytes:
    validate_control_string(transfer_id)
    return bytes([CONTROL_FILE_TRANSFER_COMPLETE]) + encode_dotnet_string(transfer_id)


def encode_file_transfer_checksum(transfer_id: str, checksum_hex: str) -> bytes:
    validate_control_string(transfer_id)
    checksum_hex = checksum_hex.lower()
    validate_sha256_hex(checksum_hex)
    return (
        bytes([CONTROL_FILE_TRANSFER_CHECKSUM])
        + encode_dotnet_string(transfer_id)
        + encode_dotnet_string(FILE_TRANSFER_CHECKSUM_ALGORITHM)
        + encode_dotnet_string(checksum_hex)
    )


def encode_file_transfer_cancel(transfer_id: str, reason: str) -> bytes:
    validate_control_string(transfer_id)
    return (
        bytes([CONTROL_FILE_TRANSFER_CANCEL])
        + encode_dotnet_string(transfer_id)
        + encode_bounded_dotnet_string(reason, "File transfer cancelled.")
    )


def encode_file_transfer_status(success: bool, message: str) -> bytes:
    return (
        bytes([CONTROL_FILE_TRANSFER_STATUS])
        + (b"\x01" if success else b"\x00")
        + encode_bounded_dotnet_string(message, "File transfer status updated.")
    )


def encode_file_transfer_receipt(transfer_id: str, success: bool, message: str) -> bytes:
    validate_control_string(transfer_id)
    return (bytes([CONTROL_FILE_TRANSFER_RECEIPT]) + encode_dotnet_string(transfer_id)
            + (b"\x01" if success else b"\x00")
            + encode_bounded_dotnet_string(message, "File save result updated."))


def encode_file_transfer_request_clipboard_files() -> bytes:
    return bytes([CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES])


def encode_file_transfer_confirm_clipboard_files() -> bytes:
    return bytes([CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES])


def encode_file_transfer_reject_clipboard_files() -> bytes:
    return bytes([CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES])


def encode_file_transfer_clipboard_files_preview(
    items: list[dict[str, Any]],
    note: str | None = None,
) -> bytes:
    preview_items = items[:MAX_CONTROL_ITEMS]
    payload = bytes([CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW]) + struct.pack("<i", len(preview_items))
    for item in preview_items:
        size_bytes = max(0, int(item.get("sizeBytes") or 0))
        payload += encode_bounded_dotnet_string(str(item.get("kind") or "文件"), "文件")
        payload += encode_bounded_dotnet_string(str(item.get("sourcePath") or "unknown"), "unknown")
        payload += encode_bounded_dotnet_string(str(item.get("transferName") or "file"), "file")
        payload += struct.pack("<q", size_bytes)
        payload += encode_bounded_dotnet_string(str(item.get("destinationPath") or "控制端接收目录"), "控制端接收目录")
    payload += encode_bounded_dotnet_string(note, "")
    return payload


def encode_dotnet_string(text: str, max_chars: int = MAX_CONTROL_STRING_CHARS) -> bytes:
    if len(text.encode("utf-16-le")) // 2 > max_chars:
        raise ProtocolError("control string is too large")
    raw = text.encode("utf-8")
    return encode_7bit_int(len(raw)) + raw


def encode_bounded_dotnet_string(text: str | None, fallback: str) -> bytes:
    normalized = (text or "").strip() or fallback
    bounded = normalized.encode("utf-16-le")[:MAX_CONTROL_STRING_CHARS * 2].decode("utf-16-le", errors="ignore")
    return encode_dotnet_string(bounded)


def encode_7bit_int(value: int) -> bytes:
    if value < 0:
        raise ProtocolError("negative 7-bit integer")
    encoded = bytearray()
    while value >= 0x80:
        encoded.append((value & 0x7F) | 0x80)
        value >>= 7
    encoded.append(value)
    return bytes(encoded)


def capability_names(capabilities: int) -> list[str]:
    names = [name for bit, name in CAPABILITIES if capabilities & bit]
    unknown = capabilities & ~sum(bit for bit, _ in CAPABILITIES)
    if unknown:
        names.append(f"Unknown({unknown})")
    return names


def decode_control(payload: bytes, *, include_clipboard_text: bool = False) -> dict[str, Any]:
    if not payload or len(payload) > MAX_CONTROL_PAYLOAD_BYTES:
        raise ProtocolError("invalid control payload length")

    cursor = Cursor(payload)
    kind = cursor.read_u8()
    message: dict[str, Any] = {"kind": kind, "kindName": control_name(kind)}
    if kind == CONTROL_DEVICE_INFO:
        machine_name = cursor.read_dotnet_string()
        platform = cursor.read_dotnet_string()
        capabilities = cursor.read_i32()
        build_stamp = cursor.read_dotnet_string() if cursor.remaining() > 0 else ""
        message.update(
            {
                "machineName": machine_name,
                "platform": platform,
                "capabilities": capabilities,
                "capabilityNames": capability_names(capabilities),
                "buildStamp": build_stamp,
            }
        )
    elif kind == CONTROL_DEVICE_IDENTITY:
        import uuid
        parsed = uuid.UUID(cursor.read_dotnet_string())
        if not parsed.int: raise ProtocolError("invalid device identity")
        message["deviceId"] = str(parsed)
    elif kind == CONTROL_DEVICE_IDENTITY_REQUEST:
        pass
    elif kind == CONTROL_DEVICE_BUILD_INFO:
        message["buildStamp"] = cursor.read_dotnet_string()
    elif kind == CONTROL_CAPTURE_TARGET_LIST:
        count = cursor.read_i32()
        if count < 0 or count > MAX_CONTROL_ITEMS:
            raise ProtocolError("invalid capture target count")
        targets = []
        for _ in range(count):
            targets.append(
                {
                    "id": cursor.read_dotnet_string(),
                    "displayName": cursor.read_dotnet_string(),
                }
            )
        message["targets"] = targets
    elif kind == CONTROL_SELECT_CAPTURE_TARGET:
        message["targetId"] = cursor.read_dotnet_string()
    elif kind == CONTROL_CAPTURE_TARGET_CHANGED:
        message["target"] = {
            "id": cursor.read_dotnet_string(),
            "displayName": cursor.read_dotnet_string(),
        }
    elif kind == CONTROL_CLIPBOARD_GET_TEXT:
        pass
    elif kind == CONTROL_CLIPBOARD_SET_TEXT:
        message["text"] = cursor.read_dotnet_string(MAX_CLIPBOARD_TEXT_CHARS)
    elif kind == CONTROL_CLIPBOARD_TEXT:
        text = cursor.read_dotnet_string(MAX_CLIPBOARD_TEXT_CHARS)
        message["textLength"] = len(text)
        # Diagnostic probes must not print the user's clipboard. Only an
        # interactive viewer explicitly opts in to receiving the actual text.
        if include_clipboard_text:
            message["text"] = text
    elif kind in (CONTROL_FILE_TRANSFER_START, CONTROL_REMOTE_UPDATE_START):
        message["transferId"] = cursor.read_dotnet_string()
        message["fileName"] = cursor.read_dotnet_string()
        file_length = cursor.read_i64()
        validate_file_length(file_length)
        message["fileLength"] = file_length
    elif kind == CONTROL_FILE_TRANSFER_CHUNK:
        message["transferId"] = cursor.read_dotnet_string()
        offset = cursor.read_i64()
        length = cursor.read_i32()
        validate_file_chunk(offset, length)
        if length > cursor.remaining():
            raise ProtocolError("invalid file chunk length")
        cursor.read_bytes(length)
        message["fileOffset"] = offset
        message["fileBytes"] = length
    elif kind == CONTROL_FILE_TRANSFER_COMPLETE:
        message["transferId"] = cursor.read_dotnet_string()
    elif kind == CONTROL_FILE_TRANSFER_CHECKSUM:
        message["transferId"] = cursor.read_dotnet_string()
        checksum_algorithm = normalize_checksum_algorithm(cursor.read_dotnet_string())
        checksum_hex = cursor.read_dotnet_string().lower()
        validate_checksum_algorithm(checksum_algorithm)
        validate_sha256_hex(checksum_hex)
        message["checksumAlgorithm"] = checksum_algorithm
        message["checksumHex"] = checksum_hex
    elif kind == CONTROL_FILE_TRANSFER_CANCEL:
        message["transferId"] = cursor.read_dotnet_string()
        message["statusMessage"] = cursor.read_dotnet_string()
    elif kind == CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW:
        count = cursor.read_i32()
        if count < 0 or count > MAX_CONTROL_ITEMS:
            raise ProtocolError("invalid file transfer preview count")
        items = []
        for _ in range(count):
            item_kind = cursor.read_dotnet_string()
            source_path = cursor.read_dotnet_string()
            transfer_name = cursor.read_dotnet_string()
            size_bytes = cursor.read_i64()
            if size_bytes < 0:
                raise ProtocolError("invalid file transfer preview size")
            destination_path = cursor.read_dotnet_string()
            items.append(
                {
                    "kind": item_kind,
                    "sourcePath": source_path,
                    "transferName": transfer_name,
                    "sizeBytes": size_bytes,
                    "destinationPath": destination_path,
                }
            )
        message["previewItems"] = items
        message["statusMessage"] = cursor.read_dotnet_string()
    elif kind == CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES:
        pass
    elif kind == CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES:
        pass
    elif kind == CONTROL_FILE_TRANSFER_RECEIPT:
        message["transferId"] = cursor.read_dotnet_string()
        message["success"] = cursor.read_bool()
        message["statusMessage"] = cursor.read_dotnet_string()
    elif kind == CONTROL_CLIPBOARD_STATUS or kind == CONTROL_FILE_TRANSFER_STATUS:
        message["success"] = cursor.read_bool()
        message["statusMessage"] = cursor.read_dotnet_string()
    elif kind == CONTROL_SESSION_REJECTED:
        message["statusMessage"] = cursor.read_dotnet_string()
    elif kind == CONTROL_VIEWER_CAPABILITIES:
        capabilities = cursor.read_i32()
        message["capabilities"] = capabilities
        message["capabilityNames"] = capability_names(capabilities)
    elif kind == CONTROL_VIEWER_INFO:
        message["videoCodecs"] = cursor.read_i32()
    elif kind == CONTROL_VIDEO_KEY_FRAME_REQUEST:
        pass
    else:
        message["payloadBytes"] = cursor.remaining()
        cursor.read_bytes(cursor.remaining())
    cursor.ensure_done()
    return message


def control_name(kind: int) -> str:
    names = {
        CONTROL_CAPTURE_TARGET_LIST: "CaptureTargetList",
        CONTROL_SELECT_CAPTURE_TARGET: "SelectCaptureTarget",
        CONTROL_CAPTURE_TARGET_CHANGED: "CaptureTargetChanged",
        CONTROL_CLIPBOARD_GET_TEXT: "ClipboardGetText",
        CONTROL_CLIPBOARD_SET_TEXT: "ClipboardSetText",
        CONTROL_CLIPBOARD_TEXT: "ClipboardText",
        CONTROL_CLIPBOARD_STATUS: "ClipboardStatus",
        CONTROL_FILE_TRANSFER_START: "FileTransferStart",
        CONTROL_FILE_TRANSFER_CHUNK: "FileTransferChunk",
        CONTROL_FILE_TRANSFER_COMPLETE: "FileTransferComplete",
        CONTROL_FILE_TRANSFER_STATUS: "FileTransferStatus",
        CONTROL_DEVICE_INFO: "DeviceInfo",
        CONTROL_VIEWER_INFO: "ViewerInfo",
        CONTROL_VIDEO_KEY_FRAME_REQUEST: "VideoKeyFrameRequest",
        CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES: "FileTransferRequestClipboardFiles",
        CONTROL_FILE_TRANSFER_CHECKSUM: "FileTransferChecksum",
        CONTROL_VIEWER_CAPABILITIES: "ViewerCapabilities",
        CONTROL_FILE_TRANSFER_CANCEL: "FileTransferCancel",
        CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW: "FileTransferClipboardFilesPreview",
        CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES: "FileTransferConfirmClipboardFiles",
        CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES: "FileTransferRejectClipboardFiles",
        CONTROL_REMOTE_UPDATE_START: "RemoteUpdateStart",
        CONTROL_REMOTE_UPDATE_PACKAGE_REQUEST: "RemoteUpdatePackageRequest",
        CONTROL_DEVICE_BUILD_INFO: "DeviceBuildInfo",
        CONTROL_SESSION_REJECTED: "SessionRejected",
    }
    return names.get(kind, f"Control({kind})")


def validate_control_string(value: str) -> None:
    if not value or not value.strip() or len(value) > MAX_CONTROL_STRING_CHARS:
        raise ProtocolError("control string is invalid")


def validate_file_length(file_length: int) -> None:
    if file_length < 0 or file_length > MAX_FILE_TRANSFER_BYTES:
        raise ProtocolError("file length is out of range")


def validate_file_chunk(offset: int, length: int) -> None:
    if (
        offset < 0
        or offset > MAX_FILE_TRANSFER_BYTES
        or length <= 0
        or length > FILE_TRANSFER_CHUNK_BYTES
        or offset > MAX_FILE_TRANSFER_BYTES - length
    ):
        raise ProtocolError("invalid file chunk length")


def normalize_checksum_algorithm(algorithm: str) -> str:
    return algorithm.strip().replace("-", "").upper()


def validate_checksum_algorithm(algorithm: str) -> None:
    if normalize_checksum_algorithm(algorithm) != FILE_TRANSFER_CHECKSUM_ALGORITHM:
        raise ProtocolError(f"unsupported file checksum algorithm: {algorithm}")


def validate_sha256_hex(checksum_hex: str) -> None:
    if len(checksum_hex) != SHA256_HEX_LENGTH:
        raise ProtocolError("invalid SHA-256 checksum length")
    if any(char not in "0123456789abcdefABCDEF" for char in checksum_hex):
        raise ProtocolError("invalid SHA-256 checksum hex")


def validate_frame_dimensions(width: int, height: int) -> None:
    if (
        width <= 0
        or height <= 0
        or width > MAX_FRAME_DIMENSION
        or height > MAX_FRAME_DIMENSION
        or width * height > MAX_FRAME_PIXELS
    ):
        raise ProtocolError("frame dimensions exceed the safe pixel budget")


def inspect_encoded_image_dimensions(encoded: bytes) -> tuple[int, int]:
    if encoded.startswith(b"\x89PNG\r\n\x1a\n"):
        if len(encoded) < 24 or encoded[12:16] != b"IHDR":
            raise ProtocolError("PNG frame header is incomplete")
        width, height = struct.unpack_from(">II", encoded, 16)
        validate_frame_dimensions(width, height)
        return width, height

    if not encoded.startswith(b"\xff\xd8"):
        raise ProtocolError("frame is not a supported JPEG or PNG image")

    index = 2
    while index < len(encoded):
        while index < len(encoded) and encoded[index] != 0xFF:
            index += 1
        while index < len(encoded) and encoded[index] == 0xFF:
            index += 1
        if index >= len(encoded):
            break

        marker = encoded[index]
        index += 1
        if marker in (0x00, 0x01, 0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
            continue
        if index + 2 > len(encoded):
            break
        segment_length = struct.unpack_from(">H", encoded, index)[0]
        if segment_length < 2 or index + segment_length > len(encoded):
            raise ProtocolError("JPEG frame segment length is invalid")
        if marker in {
            0xC0,
            0xC1,
            0xC2,
            0xC3,
            0xC5,
            0xC6,
            0xC7,
            0xC9,
            0xCA,
            0xCB,
            0xCD,
            0xCE,
            0xCF,
        }:
            if segment_length < 7:
                raise ProtocolError("JPEG frame dimensions are incomplete")
            height, width = struct.unpack_from(">HH", encoded, index + 3)
            validate_frame_dimensions(width, height)
            return width, height
        if marker == 0xDA:
            break
        index += segment_length

    raise ProtocolError("JPEG frame dimensions are missing")


def decode_frame(payload: bytes, message_type: int) -> dict[str, Any]:
    if message_type == MESSAGE_FRAME:
        if len(payload) < 24:
            raise ProtocolError("jpeg frame header missing")
        width, height = struct.unpack_from("<ii", payload, 0)
        capture_ms, encode_ms = struct.unpack_from("<dd", payload, 8)
        validate_frame_dimensions(width, height)
        if len(payload) == 24:
            raise ProtocolError("jpeg frame payload missing")
        return {
            "type": "Frame",
            "encoding": "Jpeg",
            "width": width,
            "height": height,
            "payloadBytes": len(payload),
            "encodedBytes": len(payload) - 24,
            "captureMs": round(capture_ms, 3),
            "encodeMs": round(encode_ms, 3),
        }

    if len(payload) < VIDEO_FRAME_HEADER_BYTES:
        raise ProtocolError("video frame header missing")
    encoding, width, height, flags = struct.unpack_from("<iiii", payload, 0)
    capture_ms, encode_ms = struct.unpack_from("<dd", payload, 16)
    validate_frame_dimensions(width, height)
    if encoding not in (FRAME_ENCODING_JPEG, FRAME_ENCODING_H264_ANNEX_B):
        raise ProtocolError("video frame encoding is unsupported")
    if flags & ~(FRAME_FLAG_KEY_FRAME | FRAME_FLAG_CODEC_CONFIG):
        raise ProtocolError("video frame flags are unsupported")
    if len(payload) == VIDEO_FRAME_HEADER_BYTES:
        raise ProtocolError("video frame payload missing")
    return {
        "type": "VideoFrame",
        "encoding": "H264AnnexB" if encoding == 2 else f"Encoding({encoding})",
        "width": width,
        "height": height,
        "flags": flags,
        "payloadBytes": len(payload),
        "encodedBytes": len(payload) - VIDEO_FRAME_HEADER_BYTES,
        "captureMs": round(capture_ms, 3),
        "encodeMs": round(encode_ms, 3),
    }


def try_save_jpeg_frame(payload: bytes, message_type: int, save_frame: str | None) -> None:
    if not save_frame or message_type != MESSAGE_FRAME or len(payload) <= 24:
        return

    output_path = Path(save_frame)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(payload[24:])


def try_save_h264_frame(
    payload: bytes,
    message_type: int,
    save_h264_frame: str | None,
) -> None:
    if (
        not save_h264_frame
        or message_type != MESSAGE_VIDEO_FRAME
        or len(payload) <= VIDEO_FRAME_HEADER_BYTES
    ):
        return

    encoding = struct.unpack_from("<i", payload, 0)[0]
    if encoding != FRAME_ENCODING_H264_ANNEX_B:
        return

    output_path = Path(save_h264_frame)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(payload[VIDEO_FRAME_HEADER_BYTES:])


def apply_message(result: ProbeResult, message_type: int, payload: bytes) -> dict[str, Any]:
    if message_type == MESSAGE_CONTROL:
        control = decode_control(payload)
        result.controls.append(control)
        if control["kind"] == CONTROL_DEVICE_INFO:
            result.device = {
                "machineName": control["machineName"],
                "platform": control["platform"],
                "capabilities": control["capabilities"],
                "capabilityNames": control["capabilityNames"],
                "buildStamp": control.get("buildStamp") or result.build_stamp,
            }
        elif control["kind"] == CONTROL_DEVICE_BUILD_INFO:
            result.build_stamp = str(control.get("buildStamp") or "")
            if result.device is not None:
                result.device["buildStamp"] = result.build_stamp
        elif control["kind"] == CONTROL_CAPTURE_TARGET_LIST:
            result.capture_targets = control["targets"]
        elif control["kind"] == CONTROL_CAPTURE_TARGET_CHANGED:
            result.selected_target = control["target"]
        elif control["kind"] == CONTROL_FILE_TRANSFER_STATUS:
            result.file_statuses.append(control)
        elif control["kind"] == CONTROL_SESSION_REJECTED:
            reason = str(
                control.get("statusMessage")
                or "Remote host intentionally ended this viewer session."
            ).strip()
            raise ConnectionRefusedError(reason)
        return control

    if message_type in (MESSAGE_FRAME, MESSAGE_VIDEO_FRAME):
        frame = decode_frame(payload, message_type)
        result.frames.append(frame)
        return frame

    message = {"messageType": message_type, "payloadBytes": len(payload)}
    result.controls.append(message)
    return message


def measure_pings(
    sock: socket.socket,
    session: SecureSession,
    result: ProbeResult,
    count: int,
    timeout_seconds: float,
    interval_seconds: float,
) -> None:
    if count <= 0:
        return

    original_timeout = sock.gettimeout()
    try:
        for index in range(count):
            started = time.monotonic()
            write_message(sock, session, MESSAGE_PING, b"")
            deadline = started + max(0.05, timeout_seconds)
            pong_received = False

            while time.monotonic() < deadline:
                remaining = deadline - time.monotonic()
                sock.settimeout(max(0.05, min(remaining, timeout_seconds)))
                try:
                    message_type, payload = read_message(sock, session)
                except socket.timeout:
                    continue

                rearm_tcp_quickack(sock)
                if message_type == MESSAGE_PONG:
                    result.pings.append(
                        {
                            "index": index + 1,
                            "success": True,
                            "rttMs": round((time.monotonic() - started) * 1000, 3),
                        }
                    )
                    pong_received = True
                    break

                apply_message(result, message_type, payload)

            if not pong_received:
                result.pings.append(
                    {
                        "index": index + 1,
                        "success": False,
                        "timeoutMs": round(max(0.05, timeout_seconds) * 1000, 1),
                    }
                )

            if interval_seconds > 0 and index + 1 < count:
                time.sleep(interval_seconds)
    finally:
        sock.settimeout(original_timeout)


def set_remote_clipboard_text(
    sock: socket.socket,
    session: SecureSession,
    text: str,
    result: ProbeResult,
    timeout_seconds: float,
) -> None:
    write_message(sock, session, MESSAGE_CONTROL, encode_clipboard_set_text(text))
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            message_type, payload = read_message(sock, session)
        except socket.timeout:
            continue

        message = apply_message(result, message_type, payload)
        if message_type == MESSAGE_CONTROL and message.get("kind") == CONTROL_CLIPBOARD_STATUS:
            if not message.get("success"):
                raise ProtocolError(f"remote rejected clipboard text: {message.get('statusMessage')}")
            return

    raise TimeoutError("timed out waiting for remote clipboard status")


def send_key(sock: socket.socket, session: SecureSession, virtual_key: int, key_delay_seconds: float) -> None:
    write_message(sock, session, MESSAGE_INPUT, encode_key_input(INPUT_KEY_DOWN, virtual_key))
    if key_delay_seconds > 0:
        time.sleep(key_delay_seconds)
    write_message(sock, session, MESSAGE_INPUT, encode_key_input(INPUT_KEY_UP, virtual_key))
    if key_delay_seconds > 0:
        time.sleep(key_delay_seconds)


def send_chord(sock: socket.socket, session: SecureSession, virtual_keys: list[int], key_delay_seconds: float) -> None:
    if not virtual_keys:
        return

    for virtual_key in virtual_keys:
        write_message(sock, session, MESSAGE_INPUT, encode_key_input(INPUT_KEY_DOWN, virtual_key))
        if key_delay_seconds > 0:
            time.sleep(key_delay_seconds)
    for virtual_key in reversed(virtual_keys):
        write_message(sock, session, MESSAGE_INPUT, encode_key_input(INPUT_KEY_UP, virtual_key))
        if key_delay_seconds > 0:
            time.sleep(key_delay_seconds)


def run_remote_clipboard_command(
    sock: socket.socket,
    session: SecureSession,
    command: str,
    result: ProbeResult,
    clipboard_timeout_seconds: float,
    step_delay_seconds: float,
    key_delay_seconds: float,
    launcher: str,
) -> None:
    set_remote_clipboard_text(sock, session, command, result, clipboard_timeout_seconds)
    time.sleep(step_delay_seconds)
    if launcher == "run-after-escape":
        send_key(sock, session, 0x1B, key_delay_seconds)
        time.sleep(step_delay_seconds)
        send_chord(sock, session, [0x5B, 0x52], key_delay_seconds)
    elif launcher == "run-after-alt-f4":
        send_key(sock, session, 0x1B, key_delay_seconds)
        time.sleep(step_delay_seconds)
        send_chord(sock, session, [0x12, 0x73], key_delay_seconds)
        time.sleep(step_delay_seconds)
        send_chord(sock, session, [0x5B, 0x52], key_delay_seconds)
    elif launcher == "search":
        send_chord(sock, session, [0x11, 0x1B], key_delay_seconds)
    elif launcher == "run-rwin":
        send_chord(sock, session, [0x5C, 0x52], key_delay_seconds)
    else:
        send_chord(sock, session, [0x5B, 0x52], key_delay_seconds)
    time.sleep(step_delay_seconds)
    send_chord(sock, session, [0x11, 0x56], key_delay_seconds)
    time.sleep(max(0.1, key_delay_seconds))
    send_key(sock, session, 0x0D, key_delay_seconds)
    time.sleep(step_delay_seconds)


def send_file_to_remote(
    sock: socket.socket,
    session: SecureSession,
    file_path: str,
    result: ProbeResult,
    timeout_seconds: float,
    send_checksum: bool,
    send_cancel: bool,
    remote_update: bool = False,
) -> None:
    path = Path(file_path)
    if not path.is_file():
        raise FileNotFoundError(f"probe file does not exist: {file_path}")

    source_stat = path.stat()
    file_size = source_stat.st_size
    source_mtime_ns = source_stat.st_mtime_ns
    if file_size > MAX_FILE_TRANSFER_BYTES:
        raise ProtocolError(f"probe file exceeds RemoteDesk transfer limit: {file_size} bytes")

    transfer_id = uuid4().hex
    sent_file: dict[str, Any] = {
        "path": str(path),
        "name": path.name,
        "bytes": file_size,
        "transferId": transfer_id,
        "remoteSaved": False,
        "terminalStatus": None,
        "cancelSupported": send_cancel,
    }
    result.sent_file = sent_file

    transfer_active = False
    try:
        start_payload = (
            encode_remote_update_start(transfer_id, path.name, file_size)
            if remote_update
            else encode_file_transfer_start(transfer_id, path.name, file_size)
        )
        write_message(
            sock,
            session,
            MESSAGE_CONTROL,
            start_payload,
        )
        transfer_active = True

        offset = 0
        digest = hashlib.sha256()
        with path.open("rb") as input_file:
            while offset < file_size:
                read_size = min(RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES, file_size - offset)
                chunk = input_file.read(read_size)
                if not chunk:
                    raise EOFError("probe file was truncated while being read")
                digest.update(chunk)
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_chunk(transfer_id, offset, chunk),
                )
                offset += len(chunk)

        current_stat = path.stat()
        if offset != file_size or current_stat.st_size != file_size or current_stat.st_mtime_ns != source_mtime_ns:
            raise EOFError("probe file changed while being read")

        if send_checksum:
            checksum_hex = digest.hexdigest()
            sent_file["sha256"] = checksum_hex
            write_message(
                sock,
                session,
                MESSAGE_CONTROL,
                encode_file_transfer_checksum(transfer_id, checksum_hex),
            )

        write_message(sock, session, MESSAGE_CONTROL, encode_file_transfer_complete(transfer_id))
        transfer_active = False
    except Exception as ex:
        if transfer_active and send_cancel:
            try:
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_cancel(transfer_id, f"Linux probe cancelled: {ex}"),
                )
            except Exception:
                pass
        raise

    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            message_type, payload = read_message(sock, session)
        except socket.timeout:
            continue

        message = apply_message(result, message_type, payload)
        if not (message_type == MESSAGE_CONTROL and message.get("kind") == CONTROL_FILE_TRANSFER_STATUS):
            continue

        status_message = str(message.get("statusMessage") or "")
        if not message.get("success"):
            sent_file["terminalStatus"] = status_message
            raise ProtocolError(f"remote rejected probe file: {status_message}")

        if is_terminal_file_saved_status(status_message):
            sent_file["remoteSaved"] = True
            sent_file["terminalStatus"] = status_message
            return

    raise TimeoutError("timed out waiting for remote file save status")


def receive_remote_clipboard_files(
    sock: socket.socket,
    session: SecureSession,
    result: ProbeResult,
    receive_dir: str,
    timeout_seconds: float,
    expect_files: bool,
    require_checksum: bool,
) -> None:
    result.remote_file_request = True
    receive_directory = Path(receive_dir)
    receive_directory.mkdir(parents=True, exist_ok=True)
    cleanup_stale_temporary_files(receive_directory)
    transfer: IncomingFileTransfer | None = None
    receive_budget = IncomingFileTransferBudget()

    write_message(sock, session, MESSAGE_CONTROL, encode_file_transfer_request_clipboard_files())
    deadline = time.monotonic() + timeout_seconds
    try:
        while time.monotonic() < deadline:
            try:
                message_type, payload = read_message(sock, session)
            except socket.timeout:
                continue

            if message_type != MESSAGE_CONTROL or not payload:
                apply_message(result, message_type, payload)
                continue

            kind = payload[0]
            if kind == CONTROL_FILE_TRANSFER_START:
                try:
                    replacement = start_incoming_file_transfer(
                        payload,
                        receive_directory,
                        require_checksum,
                        receive_budget,
                    )
                except (ProtocolError, OSError, EOFError, ValueError) as ex:
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, f"Linux receive start failed: {ex}"),
                    )
                    continue
                previous = transfer
                transfer = replacement
                if previous is not None:
                    previous.abort()
                result.controls.append(decode_control(payload))
            elif kind == CONTROL_FILE_TRANSFER_CHUNK:
                if transfer is None:
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, "Linux received a file chunk without an active transfer"),
                    )
                    continue
                try:
                    write_incoming_file_chunk(payload, transfer)
                except (ProtocolError, OSError, EOFError, ValueError) as ex:
                    if not transfer.active:
                        transfer = None
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, f"Linux receive failed: {ex}"),
                    )
                    continue
                result.controls.append(decode_control(payload))
            elif kind == CONTROL_FILE_TRANSFER_CHECKSUM:
                if transfer is None:
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, "Linux received a checksum without an active transfer"),
                    )
                    continue
                try:
                    set_expected_file_checksum(payload, transfer)
                except (ProtocolError, OSError, EOFError, ValueError) as ex:
                    if not transfer.active:
                        transfer = None
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, f"Linux receive failed: {ex}"),
                    )
                    continue
                result.controls.append(decode_control(payload))
            elif kind == CONTROL_FILE_TRANSFER_CANCEL:
                if transfer is None:
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, "Linux received a cancellation without an active transfer"),
                    )
                    continue
                try:
                    reason = cancel_incoming_file_transfer(payload, transfer)
                except (ProtocolError, OSError, EOFError, ValueError) as ex:
                    if not transfer.active:
                        transfer = None
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, f"Linux receive failed: {ex}"),
                    )
                    continue
                transfer = None
                result.controls.append(decode_control(payload))
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_status(True, f"Linux file transfer cancelled: {reason}"),
                )
                return
            elif kind == CONTROL_FILE_TRANSFER_COMPLETE:
                if transfer is None:
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, "Linux received a completion without an active transfer"),
                    )
                    continue
                try:
                    completed = complete_incoming_file_transfer(payload, transfer)
                except (ProtocolError, OSError, EOFError, ValueError) as ex:
                    if not transfer.active:
                        transfer = None
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_file_transfer_status(False, f"Linux receive failed: {ex}"),
                    )
                    continue
                transfer = None
                result.controls.append(decode_control(payload))
                result.received_files.append(completed)
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_status(True, f"文件已保存到 Linux：{completed['path']}"),
                )
            elif kind == CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW:
                result.controls.append(decode_control(payload))
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_confirm_clipboard_files(),
                )
            else:
                message = apply_message(result, message_type, payload)
                if kind == CONTROL_FILE_TRANSFER_STATUS:
                    success = bool(message.get("success"))
                    status_message = str(message.get("statusMessage") or "")
                    if not success and not result.received_files:
                        if expect_files:
                            raise ProtocolError(f"remote did not send files: {status_message}")
                        return
                    if result.received_files and is_terminal_file_saved_status(status_message):
                        return

        if expect_files and not result.received_files:
            raise TimeoutError("timed out waiting for remote clipboard files")
    finally:
        if transfer is not None:
            transfer.abort()


def receive_remote_update_package(
    sock: socket.socket,
    session: SecureSession,
    result: ProbeResult,
    receive_dir: str,
    timeout_seconds: float,
) -> dict[str, Any]:
    receive_directory = Path(receive_dir)
    receive_directory.mkdir(parents=True, exist_ok=True)
    cleanup_stale_temporary_files(receive_directory)
    transfer: IncomingFileTransfer | None = None
    receive_budget = IncomingFileTransferBudget()

    write_message(
        sock,
        session,
        MESSAGE_CONTROL,
        encode_remote_update_package_request(),
    )
    deadline = time.monotonic() + timeout_seconds
    try:
        while time.monotonic() < deadline:
            try:
                message_type, payload = read_message(sock, session)
            except socket.timeout:
                continue

            if message_type != MESSAGE_CONTROL or not payload:
                apply_message(result, message_type, payload)
                continue

            kind = payload[0]
            if kind == CONTROL_FILE_TRANSFER_START:
                raise ProtocolError(
                    "remote sent FileTransferStart instead of RemoteUpdateStart"
                )

            if kind == CONTROL_REMOTE_UPDATE_START:
                if transfer is not None:
                    raise ProtocolError(
                        "remote started another update package before completing the active transfer"
                    )
                if result.received_files:
                    raise ProtocolError("remote sent more than one update package")

                control = decode_control(payload)
                file_name = str(control.get("fileName") or "")
                file_length = int(control.get("fileLength") or 0)
                if file_name.casefold() != "remotedesk.exe":
                    raise ProtocolError(
                        "remote update package must be named RemoteDesk.exe"
                    )
                if file_length <= 0:
                    raise ProtocolError("remote update package must not be empty")

                transfer = start_incoming_file_transfer(
                    payload,
                    receive_directory,
                    require_checksum=True,
                    budget=receive_budget,
                )
                result.controls.append(control)
                continue

            if kind == CONTROL_FILE_TRANSFER_CHUNK:
                if transfer is None:
                    raise ProtocolError(
                        "remote sent an update package chunk without an active transfer"
                    )
                write_incoming_file_chunk(payload, transfer)
                result.controls.append(decode_control(payload))
                continue

            if kind == CONTROL_FILE_TRANSFER_CHECKSUM:
                if transfer is None:
                    raise ProtocolError(
                        "remote sent an update package checksum without an active transfer"
                    )
                set_expected_file_checksum(payload, transfer)
                result.controls.append(decode_control(payload))
                continue

            if kind == CONTROL_FILE_TRANSFER_CANCEL:
                if transfer is None:
                    raise ProtocolError(
                        "remote cancelled an update package without an active transfer"
                    )
                reason = cancel_incoming_file_transfer(payload, transfer)
                transfer = None
                result.controls.append(decode_control(payload))
                raise ProtocolError(f"remote cancelled the update package: {reason}")

            if kind == CONTROL_FILE_TRANSFER_COMPLETE:
                if transfer is None:
                    raise ProtocolError(
                        "remote completed an update package without an active transfer"
                    )

                checksum_hex = transfer.sha256.hexdigest()
                completed = complete_incoming_file_transfer(payload, transfer)
                transfer = None
                completed["sha256"] = checksum_hex
                result.controls.append(decode_control(payload))
                result.received_files.append(completed)
                write_message(
                    sock,
                    session,
                    MESSAGE_CONTROL,
                    encode_file_transfer_status(
                        True,
                        f"RemoteDesk.exe backup saved: {completed['path']}",
                    ),
                )
                return completed

            message = apply_message(result, message_type, payload)
            if kind == CONTROL_FILE_TRANSFER_STATUS and not message.get("success"):
                status_message = str(message.get("statusMessage") or "")
                raise ProtocolError(
                    f"remote did not send its update package: {status_message}"
                )

        raise TimeoutError("timed out waiting for the remote update package")
    except Exception as ex:
        try:
            write_message(
                sock,
                session,
                MESSAGE_CONTROL,
                encode_file_transfer_status(
                    False,
                    f"RemoteDesk.exe backup receive failed: {ex}",
                ),
            )
        except Exception:
            pass
        raise
    finally:
        if transfer is not None:
            transfer.abort()


def remote_supports_file_send(result: ProbeResult) -> bool:
    if result.device is None:
        return False
    capabilities = int(result.device.get("capabilities") or 0)
    return (capabilities & CAPABILITY_FILE_SEND) != 0


def remote_supports_file_checksum(result: ProbeResult) -> bool:
    if result.device is None:
        return False
    capabilities = int(result.device.get("capabilities") or 0)
    return (capabilities & CAPABILITY_FILE_CHECKSUM) != 0


def remote_supports_file_transfer_cancel(result: ProbeResult) -> bool:
    if result.device is None:
        return False
    capabilities = int(result.device.get("capabilities") or 0)
    return (capabilities & CAPABILITY_FILE_TRANSFER_CANCEL) != 0


def remote_supports_file_transfer_preview(result: ProbeResult) -> bool:
    if result.device is None:
        return False
    capabilities = int(result.device.get("capabilities") or 0)
    return (capabilities & CAPABILITY_FILE_TRANSFER_PREVIEW) != 0


def remote_supports_remote_update(result: ProbeResult) -> bool:
    if result.device is None:
        return False
    capabilities = int(result.device.get("capabilities") or 0)
    return (capabilities & CAPABILITY_REMOTE_UPDATE) != 0


def start_incoming_file_transfer(
    payload: bytes,
    receive_directory: Path,
    require_checksum: bool = False,
    budget: IncomingFileTransferBudget | None = None,
) -> IncomingFileTransfer:
    control = decode_control(payload)
    transfer_id = str(control.get("transferId") or "")
    file_name = sanitize_file_name(str(control.get("fileName") or "remote-file"))
    file_length = int(control.get("fileLength") or 0)
    if not transfer_id:
        raise ProtocolError("remote file transfer id is missing")
    validate_file_length(file_length)

    receive_directory.mkdir(parents=True, exist_ok=True)
    (budget or IncomingFileTransferBudget()).reserve(receive_directory, file_length)
    final_path = unique_file_path(receive_directory, file_name)
    temporary_path, stream = create_incoming_temporary_file(receive_directory)
    return IncomingFileTransfer(
        transfer_id=transfer_id,
        file_name=file_name,
        file_length=file_length,
        final_path=final_path,
        temporary_path=temporary_path,
        stream=stream,
        require_checksum=require_checksum,
    )


def write_incoming_file_chunk(payload: bytes, transfer: IncomingFileTransfer) -> None:
    cursor = Cursor(payload)
    kind = cursor.read_u8()
    if kind != CONTROL_FILE_TRANSFER_CHUNK:
        raise ProtocolError("not a file chunk")

    transfer_id = cursor.read_dotnet_string()
    if transfer_id != transfer.transfer_id:
        raise ProtocolError("remote file transfer id mismatch")

    try:
        offset = cursor.read_i64()
        length = cursor.read_i32()
        if offset != transfer.bytes_received:
            raise ProtocolError("remote file chunk offset is out of order")
        validate_file_chunk(offset, length)
        if transfer.bytes_received + length > transfer.file_length:
            raise ProtocolError("remote file chunk exceeds declared length")

        data = cursor.read_bytes(length)
        cursor.ensure_done()
        written = transfer.stream.write(data)
        if written != len(data):
            raise OSError("remote file chunk could not be written completely")
        transfer.sha256.update(data)
        transfer.bytes_received += length
    except Exception:
        transfer.abort()
        raise


def set_expected_file_checksum(payload: bytes, transfer: IncomingFileTransfer) -> None:
    cursor = Cursor(payload)
    kind = cursor.read_u8()
    if kind != CONTROL_FILE_TRANSFER_CHECKSUM:
        raise ProtocolError("not a file checksum")
    transfer_id = cursor.read_dotnet_string()
    if transfer_id != transfer.transfer_id:
        raise ProtocolError("remote file checksum id mismatch")

    try:
        algorithm = normalize_checksum_algorithm(cursor.read_dotnet_string())
        checksum_hex = cursor.read_dotnet_string().lower()
        cursor.ensure_done()
        validate_checksum_algorithm(algorithm)
        validate_sha256_hex(checksum_hex)
        transfer.expected_sha256 = bytes.fromhex(checksum_hex)
    except Exception:
        transfer.abort()
        raise


def cancel_incoming_file_transfer(payload: bytes, transfer: IncomingFileTransfer) -> str:
    cursor = Cursor(payload)
    kind = cursor.read_u8()
    if kind != CONTROL_FILE_TRANSFER_CANCEL:
        raise ProtocolError("not a file cancellation")
    transfer_id = cursor.read_dotnet_string()
    if transfer_id != transfer.transfer_id:
        raise ProtocolError("remote file cancellation id mismatch")

    try:
        reason = cursor.read_dotnet_string().strip() or "cancelled by remote"
        cursor.ensure_done()
    except Exception:
        transfer.abort()
        raise
    transfer.abort()
    return reason


def complete_incoming_file_transfer(payload: bytes, transfer: IncomingFileTransfer) -> dict[str, Any]:
    cursor = Cursor(payload)
    kind = cursor.read_u8()
    if kind != CONTROL_FILE_TRANSFER_COMPLETE:
        raise ProtocolError("not a file completion")
    transfer_id = cursor.read_dotnet_string()
    if transfer_id != transfer.transfer_id:
        raise ProtocolError("remote file completion id mismatch")

    completed = False
    try:
        cursor.ensure_done()
        if transfer.bytes_received != transfer.file_length:
            raise ProtocolError("remote file transfer completed before all bytes arrived")

        checksum_verified = False
        if transfer.expected_sha256 is None:
            if transfer.require_checksum:
                raise ProtocolError("remote file checksum was required but not sent")
        else:
            actual_sha256 = transfer.sha256.digest()
            if not hmac.compare_digest(actual_sha256, transfer.expected_sha256):
                raise ProtocolError("remote file SHA-256 checksum mismatch")
            checksum_verified = True

        transfer.stream.flush()
        transfer.stream.close()
        transfer.active = False
        final_path = move_temporary_to_unique_final_path(
            transfer.temporary_path,
            transfer.final_path,
            transfer.file_name,
        )
        completed = True
        return {
            "name": transfer.file_name,
            "path": str(final_path),
            "bytes": transfer.bytes_received,
            "checksumVerified": checksum_verified,
        }
    finally:
        if not completed:
            transfer.abort()


def sanitize_file_name(file_name: str) -> str:
    normalized = unicodedata.normalize("NFC", file_name).replace("\\", "/").split("/")[-1].strip()
    if not normalized:
        normalized = "remote-file"

    return sanitize_path_component(normalized, "remote-file")


def sanitize_path_component(value: str, fallback: str = "_") -> str:
    normalized = unicodedata.normalize("NFC", value).strip()

    sanitized = []
    for char in normalized:
        code_point = ord(char)
        if char in '<>:"/\\|?*' or code_point < 32:
            sanitized.append("_")
        else:
            sanitized.append(char)
    name = "".join(sanitized).strip().rstrip(". ")
    if not name:
        name = fallback

    suffix = Path(name).suffix
    raw_stem = Path(name).stem.strip().rstrip(". ")
    if not raw_stem:
        suffix = ""
    if len(suffix) > MAX_SAFE_EXTENSION_LENGTH:
        suffix = suffix[:MAX_SAFE_EXTENSION_LENGTH]
    suffix = truncate_utf8(suffix, MAX_SAFE_EXTENSION_LENGTH)

    stem = (Path(name).stem if suffix else name).strip().rstrip(". ") or fallback
    if stem.upper() in RESERVED_FILE_NAMES:
        stem = f"_{stem}"

    max_stem_bytes = max(1, MAX_SAFE_FILE_NAME_LENGTH - len(suffix.encode("utf-8")))
    stem = truncate_utf8(stem, max_stem_bytes) or fallback

    return f"{stem}{suffix}"


def truncate_utf8(value: str, maximum_bytes: int) -> str:
    encoded = value.encode("utf-8")
    if len(encoded) <= maximum_bytes:
        return value
    return encoded[:maximum_bytes].decode("utf-8", errors="ignore")


def unique_file_path(directory: Path, file_name: str) -> Path:
    base = Path(file_name).stem or "remote-file"
    suffix = Path(file_name).suffix
    for index in range(MAX_UNIQUE_FILE_PATH_ATTEMPTS):
        candidate = directory / file_name if index == 0 else directory / f"{base} ({index}){suffix}"
        if not os.path.lexists(candidate):
            return candidate
    raise ProtocolError("could not reserve a unique receive file path")


def create_incoming_temporary_file(directory: Path) -> tuple[Path, Any]:
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    for optional_flag in ("O_BINARY", "O_CLOEXEC", "O_NOFOLLOW"):
        flags |= int(getattr(os, optional_flag, 0))

    for _ in range(MAX_UNIQUE_FILE_PATH_ATTEMPTS):
        temporary_path = directory / (
            f"{INCOMING_TEMPORARY_FILE_PREFIX}{uuid4().hex}{INCOMING_TEMPORARY_FILE_SUFFIX}"
        )
        try:
            file_descriptor = os.open(temporary_path, flags, 0o600)
        except FileExistsError:
            continue

        try:
            return temporary_path, os.fdopen(file_descriptor, "wb")
        except Exception:
            os.close(file_descriptor)
            temporary_path.unlink(missing_ok=True)
            raise

    raise ProtocolError("could not reserve a temporary receive file path")


def move_temporary_to_unique_final_path(temporary_path: Path, desired_final_path: Path, file_name: str) -> Path:
    directory = desired_final_path.parent
    base = Path(file_name).stem or "remote-file"
    suffix = Path(file_name).suffix

    for index in range(MAX_UNIQUE_FILE_PATH_ATTEMPTS):
        candidate = desired_final_path if index == 0 else directory / f"{base} ({index}){suffix}"
        try:
            os.link(temporary_path, candidate)
        except FileExistsError:
            continue
        except OSError as ex:
            if ex.errno == errno.EEXIST:
                continue
            if ex.errno not in (
                errno.EXDEV,
                errno.EPERM,
                errno.EACCES,
                errno.EINVAL,
                errno.ENOSYS,
                errno.ENOTSUP,
                errno.EOPNOTSUPP,
            ):
                raise
        else:
            unlink_temporary_file_best_effort(temporary_path)
            return candidate

        destination_identity: tuple[int, int] | None = None
        try:
            with temporary_path.open("rb") as source, candidate.open("xb") as destination:
                destination_stat = os.fstat(destination.fileno())
                destination_identity = (destination_stat.st_dev, destination_stat.st_ino)
                shutil.copyfileobj(source, destination, FILE_TRANSFER_CHUNK_BYTES)
        except FileExistsError:
            continue
        except Exception:
            unlink_if_same_file(candidate, destination_identity)
            raise

        unlink_temporary_file_best_effort(temporary_path)
        return candidate

    raise ProtocolError("could not reserve a unique receive file path")


def unlink_if_same_file(path: Path, expected_identity: tuple[int, int] | None) -> None:
    if expected_identity is None:
        return
    try:
        current_stat = path.lstat()
        if (current_stat.st_dev, current_stat.st_ino) == expected_identity:
            path.unlink()
    except OSError:
        pass


def unlink_temporary_file_best_effort(path: Path) -> None:
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def cleanup_stale_temporary_files(directory: Path) -> int:
    deleted = 0
    cutoff = time.time() - STALE_TEMPORARY_FILE_SECONDS
    for path in directory.glob(f"{INCOMING_TEMPORARY_FILE_PREFIX}*{INCOMING_TEMPORARY_FILE_SUFFIX}"):
        try:
            if not is_owned_incoming_temporary_file(path.name):
                continue
            path_stat = path.lstat()
            if not stat.S_ISREG(path_stat.st_mode) or path_stat.st_mtime > cutoff:
                continue
            path.unlink()
            deleted += 1
        except OSError:
            pass
    return deleted


def is_owned_incoming_temporary_file(file_name: str) -> bool:
    if not (
        file_name.startswith(INCOMING_TEMPORARY_FILE_PREFIX)
        and file_name.endswith(INCOMING_TEMPORARY_FILE_SUFFIX)
    ):
        return False
    token_start = len(INCOMING_TEMPORARY_FILE_PREFIX)
    token_end = len(file_name) - len(INCOMING_TEMPORARY_FILE_SUFFIX)
    token = file_name[token_start:token_end]
    return len(token) == 32 and all(char in "0123456789abcdef" for char in token)


def safe_transfer_path_size(
    path: Path,
    cancel_event: threading.Event | None = None,
) -> int:
    raise_if_transfer_cancelled(cancel_event)
    requested_path = path.expanduser()
    if is_link_like(requested_path) and requested_path.is_dir():
        raise ProtocolError(f"directory transfer source must not be a symbolic link or junction: {path}")
    source = requested_path.resolve(strict=True)
    raise_if_transfer_cancelled(cancel_event)
    source_stat = source.stat()
    if stat.S_ISREG(source_stat.st_mode):
        return max(0, source_stat.st_size)
    if not stat.S_ISDIR(source_stat.st_mode):
        return 0

    total = 0
    for _entry_path, _relative_parts, is_directory, entry_stat in iter_safe_directory_entries(
        source,
        cancel_event=cancel_event,
    ):
        raise_if_transfer_cancelled(cancel_event)
        if is_directory:
            continue
        total += max(0, entry_stat.st_size)
        if total > MAX_FILE_TRANSFER_BYTES:
            return total
    return total


def create_safe_directory_archive(
    directory: Path,
    source_size: int | None = None,
    cancel_event: threading.Event | None = None,
) -> Path:
    raise_if_transfer_cancelled(cancel_event)
    requested_directory = directory.expanduser()
    if is_link_like(requested_directory):
        raise ProtocolError(
            f"directory transfer source must not be a symbolic link or junction: {directory}"
        )
    source = requested_directory.resolve(strict=True)
    raise_if_transfer_cancelled(cancel_event)
    if not source.is_dir():
        raise NotADirectoryError(str(directory))

    if source_size is None:
        source_size = safe_transfer_path_size(source, cancel_event=cancel_event)
    if source_size < 0 or source_size > MAX_FILE_TRANSFER_BYTES:
        raise ProtocolError(f"directory exceeds RemoteDesk transfer limit: {source_size} bytes")

    file_descriptor, archive_name = tempfile.mkstemp(prefix="remotedesk-folder-", suffix=".zip")
    archive = Path(archive_name)
    archive_stream: Any | None = None
    try:
        archive_stream = os.fdopen(file_descriptor, "w+b")
        file_descriptor = -1
        raise_if_transfer_cancelled(cancel_event)
        root_name = sanitize_path_component(requested_directory.name or source.name or "folder", "folder")
        seen_names: set[str] = set()
        raw_bytes = 0
        with archive_stream:
            with zipfile.ZipFile(
                archive_stream,
                "w",
                compression=zipfile.ZIP_DEFLATED,
                compresslevel=1,
            ) as output:
                for entry_path, relative_parts, is_directory, entry_stat in iter_safe_directory_entries(
                    source,
                    excluded_path=archive,
                    cancel_event=cancel_event,
                ):
                    raise_if_transfer_cancelled(cancel_event)
                    entry_name = safe_archive_entry_name(root_name, relative_parts, is_directory)
                    reserve_archive_entry_name(entry_name, seen_names)
                    if is_directory:
                        output.writestr(create_zip_info(entry_name, entry_stat, is_directory=True), b"")
                    else:
                        raw_bytes += max(0, entry_stat.st_size)
                        if raw_bytes > MAX_FILE_TRANSFER_BYTES:
                            raise ProtocolError(
                                f"directory exceeds RemoteDesk transfer limit: {raw_bytes} bytes"
                            )
                        write_safe_file_to_archive(
                            output,
                            archive_stream,
                            entry_path,
                            entry_name,
                            entry_stat,
                            cancel_event=cancel_event,
                        )
                    raise_if_transfer_cancelled(cancel_event)
                    ensure_archive_stream_within_transfer_limit(archive_stream)

            archive_stream.flush()

        raise_if_transfer_cancelled(cancel_event)
        ensure_archive_path_within_transfer_limit(archive)
        return archive
    except Exception:
        if file_descriptor >= 0:
            try:
                os.close(file_descriptor)
            except OSError:
                pass
        if archive_stream is not None and not archive_stream.closed:
            try:
                archive_stream.close()
            except OSError:
                pass
        archive.unlink(missing_ok=True)
        raise


def iter_safe_directory_entries(
    source: Path,
    excluded_path: Path | None = None,
    cancel_event: threading.Event | None = None,
) -> Iterator[tuple[Path, tuple[str, ...], bool, os.stat_result]]:
    raise_if_transfer_cancelled(cancel_event)
    source = source.resolve(strict=True)
    pending_directories: list[tuple[Path, tuple[str, ...]]] = [(source, ())]
    while pending_directories:
        raise_if_transfer_cancelled(cancel_event)
        root_path, relative_root_parts = pending_directories.pop()
        resolved_root = root_path.resolve(strict=True)
        if not resolved_root.is_relative_to(source):
            raise ProtocolError(f"directory entry escaped selected folder: {root_path}")

        directory_names: list[str] = []
        file_names: list[str] = []
        with os.scandir(root_path) as entries:
            for entry in entries:
                raise_if_transfer_cancelled(cancel_event)
                if entry.is_dir(follow_symlinks=False):
                    directory_names.append(entry.name)
                elif entry.is_file(follow_symlinks=False):
                    file_names.append(entry.name)

        safe_directories: list[tuple[Path, tuple[str, ...]]] = []
        for directory_name in sorted(directory_names):
            raise_if_transfer_cancelled(cancel_event)
            child = root_path / directory_name
            if is_link_like(child) or same_path(child, excluded_path):
                continue
            child_stat = child.lstat()
            if not stat.S_ISDIR(child_stat.st_mode):
                continue
            if not child.resolve(strict=True).is_relative_to(source):
                raise ProtocolError(f"directory entry escaped selected folder: {child}")
            safe_directories.append((child, (*relative_root_parts, directory_name)))

        safe_files: list[tuple[Path, os.stat_result]] = []
        for file_name in sorted(file_names):
            raise_if_transfer_cancelled(cancel_event)
            child = root_path / file_name
            if is_link_like(child) or same_path(child, excluded_path):
                continue
            child_stat = child.lstat()
            if not stat.S_ISREG(child_stat.st_mode):
                continue
            if not child.resolve(strict=True).is_relative_to(source):
                raise ProtocolError(f"file entry escaped selected folder: {child}")
            safe_files.append((child, child_stat))

        if not safe_directories and not safe_files:
            raise_if_transfer_cancelled(cancel_event)
            yield root_path, relative_root_parts, True, root_path.stat()
        for child, child_stat in safe_files:
            raise_if_transfer_cancelled(cancel_event)
            yield child, (*relative_root_parts, child.name), False, child_stat
        pending_directories.extend(reversed(safe_directories))


def is_link_like(path: Path) -> bool:
    if path.is_symlink():
        return True
    is_junction = getattr(path, "is_junction", None)
    return bool(is_junction is not None and is_junction())


def same_path(left: Path, right: Path | None) -> bool:
    if right is None:
        return False
    normalized_left = os.path.normcase(os.path.abspath(left.resolve(strict=False)))
    normalized_right = os.path.normcase(os.path.abspath(right.resolve(strict=False)))
    return normalized_left == normalized_right


def safe_archive_entry_name(root_name: str, relative_parts: tuple[str, ...], is_directory: bool) -> str:
    safe_parts = [sanitize_path_component(root_name, "folder")]
    for part in relative_parts:
        if not part or part in (".", ".."):
            raise ProtocolError("directory archive contains an unsafe path component")
        safe_parts.append(sanitize_path_component(part, "_"))

    entry_name = "/".join(safe_parts)
    if len(entry_name.encode("utf-8")) > MAX_SAFE_ARCHIVE_ENTRY_BYTES:
        raise ProtocolError("directory archive entry path is too long")
    return f"{entry_name}/" if is_directory else entry_name


def reserve_archive_entry_name(entry_name: str, seen_names: set[str]) -> None:
    collision_key = unicodedata.normalize("NFC", entry_name.rstrip("/")).casefold()
    if collision_key in seen_names:
        raise ProtocolError(f"directory archive contains colliding entry names: {entry_name}")
    seen_names.add(collision_key)


def create_zip_info(entry_name: str, entry_stat: os.stat_result, is_directory: bool) -> zipfile.ZipInfo:
    timestamp = time.localtime(entry_stat.st_mtime)
    if timestamp.tm_year < 1980:
        date_time = (1980, 1, 1, 0, 0, 0)
    elif timestamp.tm_year > 2107:
        date_time = (2107, 12, 31, 23, 59, 58)
    else:
        date_time = (
            timestamp.tm_year,
            timestamp.tm_mon,
            timestamp.tm_mday,
            timestamp.tm_hour,
            timestamp.tm_min,
            timestamp.tm_sec,
        )
    info = zipfile.ZipInfo(entry_name, date_time=date_time)
    info.create_system = 3
    info.compress_type = zipfile.ZIP_DEFLATED
    mode = entry_stat.st_mode
    if is_directory:
        mode = stat.S_IFDIR | (stat.S_IMODE(mode) or 0o755)
        info.external_attr |= 0x10
    info.external_attr |= (mode & 0xFFFF) << 16
    return info


def write_safe_file_to_archive(
    output: zipfile.ZipFile,
    archive_stream: Any,
    source_path: Path,
    entry_name: str,
    expected_stat: os.stat_result,
    cancel_event: threading.Event | None = None,
) -> None:
    raise_if_transfer_cancelled(cancel_event)
    flags = os.O_RDONLY
    for optional_flag in ("O_BINARY", "O_CLOEXEC", "O_NOFOLLOW"):
        flags |= int(getattr(os, optional_flag, 0))

    file_descriptor = os.open(source_path, flags)
    try:
        raise_if_transfer_cancelled(cancel_event)
        current_stat = os.fstat(file_descriptor)
        if not stat.S_ISREG(current_stat.st_mode):
            raise ProtocolError(f"directory archive entry is not a regular file: {source_path}")
        if (current_stat.st_dev, current_stat.st_ino) != (expected_stat.st_dev, expected_stat.st_ino):
            raise ProtocolError(f"directory archive entry changed while packing: {source_path}")

        with os.fdopen(file_descriptor, "rb") as input_file:
            file_descriptor = -1
            info = create_zip_info(entry_name, current_stat, is_directory=False)
            with output.open(info, "w") as entry:
                remaining = current_stat.st_size
                while remaining > 0:
                    raise_if_transfer_cancelled(cancel_event)
                    chunk = input_file.read(min(FILE_TRANSFER_CHUNK_BYTES, remaining))
                    if not chunk:
                        raise ProtocolError(f"directory archive entry was truncated while packing: {source_path}")
                    raise_if_transfer_cancelled(cancel_event)
                    entry.write(chunk)
                    remaining -= len(chunk)
                    ensure_archive_stream_within_transfer_limit(archive_stream)
                raise_if_transfer_cancelled(cancel_event)
                if input_file.read(1):
                    raise ProtocolError(f"directory archive entry grew while packing: {source_path}")

            raise_if_transfer_cancelled(cancel_event)
            final_stat = os.fstat(input_file.fileno())
            if (
                final_stat.st_size != current_stat.st_size
                or final_stat.st_mtime_ns != current_stat.st_mtime_ns
                or (final_stat.st_dev, final_stat.st_ino) != (current_stat.st_dev, current_stat.st_ino)
            ):
                raise ProtocolError(f"directory archive entry changed while packing: {source_path}")
    finally:
        if file_descriptor >= 0:
            os.close(file_descriptor)


def ensure_archive_stream_within_transfer_limit(archive_stream: Any) -> None:
    if archive_stream.tell() > MAX_FILE_TRANSFER_BYTES:
        raise ProtocolError(
            f"directory archive exceeds RemoteDesk transfer limit: {MAX_FILE_TRANSFER_BYTES} bytes"
        )


def ensure_archive_path_within_transfer_limit(archive: Path) -> None:
    if archive.stat().st_size > MAX_FILE_TRANSFER_BYTES:
        raise ProtocolError(
            f"directory archive exceeds RemoteDesk transfer limit: {MAX_FILE_TRANSFER_BYTES} bytes"
        )


def is_terminal_file_saved_status(message: str) -> bool:
    normalized = message.casefold()
    terminal_tokens = (
        "已保存",
        "保存到",
        "回传完成",
        "saved to",
        "saved at",
        "saved:",
        "transfer complete",
        "complete:",
        "completed:",
    )
    return any(token in normalized for token in terminal_tokens)


def run_probe(args: argparse.Namespace) -> ProbeResult:
    started = time.monotonic()
    result = ProbeResult(args.host, args.port)
    with socket.create_connection((args.host, args.port), timeout=args.connect_timeout) as sock:
        configure_low_latency_socket(sock)
        sock.settimeout(args.read_timeout)
        session = authenticate(sock, args.password)
        result.authenticated = True
        write_message(sock, session, MESSAGE_CONTROL, encode_viewer_info(args.video_codecs))
        write_message(
            sock,
            session,
            MESSAGE_CONTROL,
            encode_viewer_capabilities(BASE_VIEWER_CAPABILITIES),
        )

        deadline = time.monotonic() + args.duration
        while time.monotonic() < deadline:
            try:
                message_type, payload = read_message(sock, session)
            except socket.timeout:
                break

            try_save_jpeg_frame(payload, message_type, args.save_frame)
            try_save_h264_frame(
                payload,
                message_type,
                args.save_h264_frame,
            )
            apply_message(result, message_type, payload)
            has_requested_frames = args.frames <= 0 or len(result.frames) >= args.frames
            has_initial_metadata = result.device is not None and result.capture_targets and result.selected_target
            if has_initial_metadata and (args.select_target_id or has_requested_frames):
                break

        if args.select_target_id:
            result.frames.clear()
            write_message(
                sock,
                session,
                MESSAGE_CONTROL,
                encode_select_capture_target(args.select_target_id),
            )
            deadline = time.monotonic() + args.duration
            while time.monotonic() < deadline:
                try:
                    message_type, payload = read_message(sock, session)
                except socket.timeout:
                    break

                try_save_jpeg_frame(payload, message_type, args.save_frame)
                try_save_h264_frame(
                    payload,
                    message_type,
                    args.save_h264_frame,
                )
                apply_message(result, message_type, payload)
                target_selected = result.selected_target is not None and result.selected_target.get("id") == args.select_target_id
                has_requested_frames = args.frames <= 0 or len(result.frames) >= args.frames
                if target_selected and has_requested_frames:
                    break

        viewer_capabilities = BASE_VIEWER_CAPABILITIES
        if remote_supports_file_checksum(result):
            viewer_capabilities |= CAPABILITY_FILE_CHECKSUM
        if remote_supports_file_transfer_cancel(result):
            viewer_capabilities |= CAPABILITY_FILE_TRANSFER_CANCEL
        if remote_supports_file_transfer_preview(result):
            viewer_capabilities |= CAPABILITY_FILE_TRANSFER_PREVIEW
        if viewer_capabilities:
            write_message(
                sock,
                session,
                MESSAGE_CONTROL,
                encode_viewer_capabilities(viewer_capabilities),
            )
        measure_pings(
            sock,
            session,
            result,
            args.pings,
            args.ping_timeout,
            args.ping_interval,
        )
        if args.run_remote_clipboard_command:
            run_remote_clipboard_command(
                sock,
                session,
                args.run_remote_clipboard_command,
                result,
                args.clipboard_status_timeout,
                args.remote_input_step_delay,
                args.remote_input_key_delay,
                args.remote_command_launcher,
            )
        if args.copy_remote_selection:
            send_chord(
                sock,
                session,
                [0x11, 0x43],
                args.remote_input_key_delay,
            )
            time.sleep(args.remote_input_step_delay)
        if args.send_file:
            send_file_to_remote(
                sock,
                session,
                args.send_file,
                result,
                args.file_status_timeout,
                remote_supports_file_checksum(result),
                remote_supports_file_transfer_cancel(result),
            )
        if args.send_remote_update:
            if not remote_supports_remote_update(result):
                raise ProtocolError("remote does not advertise RemoteUpdate")
            if Path(args.send_remote_update).name.casefold() != "remotedesk.exe":
                raise ProtocolError("remote update package must be named RemoteDesk.exe")
            send_file_to_remote(
                sock,
                session,
                args.send_remote_update,
                result,
                args.file_status_timeout,
                remote_supports_file_checksum(result),
                remote_supports_file_transfer_cancel(result),
                remote_update=True,
            )
        if args.request_remote_update_package:
            if not remote_supports_remote_update(result):
                raise ProtocolError("remote does not advertise RemoteUpdate")
            if not remote_supports_file_checksum(result):
                raise ProtocolError(
                    "remote does not advertise the checksum capability required for update backup"
                )
            if not remote_supports_file_transfer_cancel(result):
                raise ProtocolError(
                    "remote does not advertise the cancellation capability required for update backup"
                )
            receive_remote_update_package(
                sock,
                session,
                result,
                args.receive_dir,
                args.remote_file_timeout,
            )
        if args.request_remote_files:
            if remote_supports_file_send(result):
                receive_remote_clipboard_files(
                    sock,
                    session,
                    result,
                    args.receive_dir,
                    args.remote_file_timeout,
                    args.expect_remote_files,
                    remote_supports_file_checksum(result),
                )
            else:
                result.remote_file_request = True
                result.remote_file_request_skipped_reason = "remote does not advertise FileSend"
                result.file_statuses.append(
                    {
                        "success": False,
                        "statusMessage": result.remote_file_request_skipped_reason,
                    }
                )
                if args.expect_remote_files:
                    raise ProtocolError(result.remote_file_request_skipped_reason)

    result.elapsed_ms = round((time.monotonic() - started) * 1000, 1)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe a RemoteDesk host from Linux/WSL.")
    parser.add_argument("--host", required=True)
    parser.add_argument("--port", type=int, default=56565)
    password_group = parser.add_mutually_exclusive_group(required=True)
    password_group.add_argument(
        "--password",
        help="Connection password (visible in process listings; prefer --password-fd).",
    )
    password_group.add_argument(
        "--password-fd",
        type=int,
        help="Read the UTF-8 connection password from this inherited file descriptor.",
    )
    parser.add_argument("--duration", type=float, default=5.0)
    parser.add_argument("--frames", type=int, default=1)
    parser.add_argument("--select-target-id", help="Select a remote capture target before collecting requested frames.")
    parser.add_argument("--pings", type=int, default=0, help="Measure protocol ping RTT after initial messages.")
    parser.add_argument("--ping-timeout", type=float, default=2.0)
    parser.add_argument("--ping-interval", type=float, default=0.05)
    parser.add_argument("--save-frame", help="Save the latest received JPEG frame to this path.")
    parser.add_argument(
        "--save-h264-frame",
        help="Save the latest received Annex-B H.264 access unit to this path.",
    )
    parser.add_argument("--connect-timeout", type=float, default=3.0)
    parser.add_argument("--read-timeout", type=float, default=2.0)
    parser.add_argument(
        "--run-remote-clipboard-command",
        help="Set remote text clipboard, send Win+R, paste it, and press Enter.",
    )
    parser.add_argument(
        "--remote-command-launcher",
        choices=("run", "run-rwin", "run-after-escape", "run-after-alt-f4", "search"),
        default="run",
        help="How to launch --run-remote-clipboard-command on the remote Windows desktop.",
    )
    parser.add_argument("--clipboard-status-timeout", type=float, default=5.0)
    parser.add_argument("--remote-input-step-delay", type=float, default=0.8)
    parser.add_argument("--remote-input-key-delay", type=float, default=0.04)
    parser.add_argument(
        "--copy-remote-selection",
        action="store_true",
        help="Send Ctrl+C to the active remote selection before requesting clipboard files.",
    )
    parser.add_argument("--send-file", help="Send one local file to the RemoteDesk host after authentication.")
    parser.add_argument("--send-remote-update", help="Send a Windows RemoteDesk.exe package as a remote update.")
    parser.add_argument(
        "--request-remote-update-package",
        action="store_true",
        help="Download the host's current RemoteDesk.exe as a verified backup without applying it.",
    )
    parser.add_argument("--file-status-timeout", type=float, default=8.0)
    parser.add_argument(
        "--request-remote-files",
        action="store_true",
        help="Ask the RemoteDesk host to return files currently present in its file clipboard.",
    )
    parser.add_argument(
        "--expect-remote-files",
        action="store_true",
        help="Fail if --request-remote-files does not receive at least one file.",
    )
    parser.add_argument("--receive-dir", default="/tmp/remotedesk-received")
    parser.add_argument("--remote-file-timeout", type=float, default=8.0)
    parser.add_argument(
        "--video-codecs",
        type=int,
        default=VIDEO_CODEC_JPEG,
        help="RemoteDesk video codec bitmask to advertise; default is JPEG only.",
    )
    parser.add_argument("--json", action="store_true", help="Print JSON only.")
    args = parser.parse_args()
    try:
        args.password = resolve_password_argument(args.password, args.password_fd)
    except (OSError, ValueError) as ex:
        parser.error(str(ex))
    if args.copy_remote_selection and not args.request_remote_files:
        parser.error(
            "--copy-remote-selection requires --request-remote-files"
        )
    if args.request_remote_update_package and (
        args.send_remote_update or args.request_remote_files
    ):
        parser.error(
            "--request-remote-update-package cannot be combined with "
            "--send-remote-update or --request-remote-files"
        )

    try:
        result = run_probe(args)
    except Exception as ex:
        if args.json:
            print(json.dumps({"ok": False, "error": str(ex)}, ensure_ascii=False))
        else:
            print(f"RemoteDesk Linux protocol probe failed: {ex}", file=sys.stderr)
        return 1

    output = {
        "ok": result.ok(),
        "host": result.host,
        "port": result.port,
        "authenticated": result.authenticated,
        "device": result.device,
        "captureTargets": result.capture_targets,
        "selectedTarget": result.selected_target,
        "frames": result.frames,
        "pings": result.pings,
        "fileStatuses": result.file_statuses,
        "sentFile": result.sent_file,
        "remoteFileRequest": result.remote_file_request,
        "remoteFileRequestSkippedReason": result.remote_file_request_skipped_reason,
        "receivedFiles": result.received_files,
        "elapsedMs": result.elapsed_ms,
    }
    if args.json:
        print(json.dumps(output, ensure_ascii=False))
    else:
        print(json.dumps(output, ensure_ascii=False, indent=2))

    return 0 if result.ok() else 2


if __name__ == "__main__":
    raise SystemExit(main())
