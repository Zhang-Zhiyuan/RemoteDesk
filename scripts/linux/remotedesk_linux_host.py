#!/usr/bin/env python3
"""RemoteDesk Linux host prototype.

This host speaks the same encrypted RemoteDesk protocol as the Windows host.
It is intentionally focused on file transfer compatibility first: Windows can
connect to it, send files to Linux, and request Linux files back.
"""

from __future__ import annotations

import argparse
import base64
import ctypes
import ctypes.util
import hashlib
import ipaddress
import json
import math
import os
import re
import selectors
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
from collections import deque
from contextlib import contextmanager, nullcontext
from dataclasses import dataclass, field
from fractions import Fraction
from pathlib import Path
from typing import Any, Callable
from urllib.parse import unquote, urlparse
from uuid import uuid4

# CLI help must not probe the desktop, prompt for installation or require sudo.
if (__name__ == "__main__" and sys.platform.startswith("linux")
        and not any(argument in ("-h", "--help") for argument in sys.argv[1:])):
    from remotedesk_linux_dependencies import prepare_runtime

    dependency_status = prepare_runtime("host", graphical=False)
    if dependency_status:
        raise SystemExit(dependency_status)

from remotedesk_protocol_probe import (
    CAPABILITY_CAPTURE_TARGET_SELECTION,
    CAPABILITY_CLIPBOARD_TEXT,
    CAPABILITY_CLIPBOARD_SNAPSHOT_V1,
    CAPABILITY_FILE_CHECKSUM,
    CAPABILITY_FILE_RECEIVE,
    CAPABILITY_FILE_TRANSFER_RECEIPT,
    CAPABILITY_FILE_RECEIVE_LOCATION,
    CONTROL_FILE_RECEIVE_LOCATION_REQUEST,
    encode_file_receive_location,
    CAPABILITY_FILE_SEND,
    CAPABILITY_FILE_TRANSFER_CANCEL,
    CAPABILITY_FILE_TRANSFER_PREVIEW,
    CAPABILITY_HIGH_FRAME_RATE_H264,
    CAPABILITY_HIGH_QUALITY_JPEG,
    CAPABILITY_INPUT_CONTROL,
    CAPABILITY_REMOTE_DESKTOP,
    CAPABILITY_SHORT_GOP_H264,
    CONTROL_CAPTURE_TARGET_CHANGED,
    CONTROL_CLIPBOARD_GET_TEXT,
    CONTROL_CLIPBOARD_SET_TEXT,
    CONTROL_CLIPBOARD_SNAPSHOT_REQUEST,
    CONTROL_FILE_TRANSFER_CANCEL,
    CONTROL_FILE_TRANSFER_CHECKSUM,
    CONTROL_FILE_TRANSFER_CHUNK,
    CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES,
    CONTROL_FILE_TRANSFER_COMPLETE,
    CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES,
    CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES,
    CONTROL_FILE_TRANSFER_START,
    CONTROL_FILE_TRANSFER_STATUS,
    CONTROL_FILE_TRANSFER_RECEIPT,
    CONTROL_SELECT_CAPTURE_TARGET,
    CONTROL_VIDEO_KEY_FRAME_REQUEST,
    CONTROL_VIEWER_CAPABILITIES,
    CONTROL_VIEWER_INFO,
    CONTROL_DEVICE_IDENTITY_REQUEST,
    CAPABILITY_DEVICE_IDENTITY,
    encode_device_identity,
    FRAME_FLAG_CODEC_CONFIG,
    FRAME_FLAG_KEY_FRAME,
    RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES,
    INPUT_PAYLOAD_LENGTH,
    MAX_FILE_TRANSFER_BYTES,
    MAX_CLIPBOARD_TEXT_CHARS,
    MAX_CLIPBOARD_SNAPSHOT_UTF8_BYTES,
    MAX_FRAME_PAYLOAD_BYTES,
    MESSAGE_CONTROL,
    MESSAGE_FRAME,
    MESSAGE_INPUT,
    MESSAGE_PING,
    MESSAGE_PONG,
    MESSAGE_VIDEO_FRAME,
    IncomingFileTransferBudget,
    ProtocolError,
    SecureSession,
    TransferCancelledError,
    VIDEO_CODEC_H264_ANNEX_B,
    VIDEO_CODEC_JPEG,
    authenticate_server,
    cancel_incoming_file_transfer,
    cleanup_stale_temporary_files,
    complete_incoming_file_transfer,
    create_safe_directory_archive,
    decode_control,
    encode_capture_target_changed,
    encode_capture_target_list,
    encode_clipboard_status,
    encode_clipboard_text,
    encode_clipboard_snapshot,
    encode_device_info,
    encode_file_transfer_cancel,
    encode_file_transfer_checksum,
    encode_file_transfer_chunk,
    encode_file_transfer_clipboard_files_preview,
    encode_file_transfer_complete,
    encode_file_transfer_start,
    encode_file_transfer_status,
    encode_file_transfer_receipt,
    encode_session_rejected,
    encode_video_frame,
    read_message,
    rearm_tcp_quickack,
    resolve_password_argument,
    safe_transfer_path_size,
    sanitize_file_name,
    set_expected_file_checksum,
    start_incoming_file_transfer,
    write_incoming_file_chunk,
    write_message,
)


DISCOVERY_REQUEST = b"RemoteDesk.Discover.v1"
DISCOVERY_RESPONSE_TYPE = "RemoteDesk.Discover.Response.v1"
PLATFORM_LINUX = "Linux"
CAPTURE_TARGET_ID = "linux-desktop"
CAPTURE_TARGET_NAME = "Linux Desktop"
MAX_RETURN_FILES = 32
MAX_PENDING_AUTHENTICATIONS = 4
AUTHENTICATION_FAILURE_DELAY_SECONDS = 0.25
HOST_SESSION_INBOUND_TIMEOUT_SECONDS = 30.0
HOST_SESSION_WATCHDOG_POLL_SECONDS = 1.0
SESSION_REPLACEMENT_WRITE_TIMEOUT_SECONDS = 1.0
SESSION_REPLACEMENT_DRAIN_TIMEOUT_SECONDS = 2.0
SESSION_REPLACED_MESSAGE = "此连接已被另一台查看端接管；已停止自动重连。"
CLIPBOARD_OPERATION_QUEUE_LIMIT = 8
CLIPBOARD_WORKER_STOP_TIMEOUT_SECONDS = 0.25
RETURN_WORKER_STOP_TIMEOUT_SECONDS = 0.25

BASE_HOST_CAPABILITIES = (
    CAPABILITY_REMOTE_DESKTOP
    | CAPABILITY_CLIPBOARD_TEXT
    | CAPABILITY_CLIPBOARD_SNAPSHOT_V1
    | CAPABILITY_FILE_RECEIVE
    | CAPABILITY_FILE_TRANSFER_RECEIPT
    | CAPABILITY_FILE_RECEIVE_LOCATION
    | CAPABILITY_CAPTURE_TARGET_SELECTION
    | CAPABILITY_FILE_SEND
    | CAPABILITY_FILE_CHECKSUM
    | CAPABILITY_FILE_TRANSFER_CANCEL
    | CAPABILITY_FILE_TRANSFER_PREVIEW
    | CAPABILITY_SHORT_GOP_H264
    | CAPABILITY_HIGH_FRAME_RATE_H264
    | CAPABILITY_HIGH_QUALITY_JPEG
)


@dataclass(frozen=True)
class ActiveClientOwner:
    client: Any
    replace: Callable[[], None]


class ClientAdmissionGate:
    def __init__(self, max_pending: int = MAX_PENDING_AUTHENTICATIONS) -> None:
        if max_pending <= 0:
            raise ValueError("max_pending must be positive")
        self.max_pending = max_pending
        self.lock = threading.Lock()
        self.accepting = True
        self.pending: set[socket.socket] = set()
        self.active: ActiveClientOwner | None = None

    def try_register_pending(self, client: socket.socket) -> bool:
        with self.lock:
            if (
                not self.accepting
                or client in self.pending
                or (
                    self.active is not None
                    and client is self.active.client
                )
                or len(self.pending) >= self.max_pending
            ):
                return False
            self.pending.add(client)
            return True

    def activate_latest(
        self,
        client: socket.socket,
        replace: Callable[[], None],
    ) -> tuple[str, Callable[[], None] | None]:
        with self.lock:
            if client not in self.pending:
                return (
                    "stopped" if not self.accepting else "not-pending",
                    None,
                )
            self.pending.remove(client)
            if not self.accepting:
                return "stopped", None
            previous = self.active
            self.active = ActiveClientOwner(client, replace)
            return (
                "activated",
                previous.replace if previous is not None else None,
            )

    def authentication_ended(self, client: socket.socket) -> None:
        with self.lock:
            self.pending.discard(client)

    def release_active(self, client: socket.socket) -> bool:
        with self.lock:
            if self.active is None or self.active.client is not client:
                return False
            self.active = None
            return True

    def stop_and_drain(self) -> list[socket.socket]:
        with self.lock:
            self.accepting = False
            clients = list(self.pending)
            self.pending.clear()
            if self.active is not None:
                clients.append(self.active.client)
                self.active = None
            return clients

    def pending_count(self) -> int:
        with self.lock:
            return len(self.pending)

@dataclass(frozen=True)
class InputCapabilityVerification:
    checked_at: float
    display: str
    available: bool
    backend: str | None
    detail: str


@dataclass(frozen=True)
class X11DisplayProbe:
    display: str
    usable: bool
    content_state: str
    detail: str

    @property
    def rank(self) -> int:
        if not self.usable:
            return 0
        return {
            "content": 3,
            "unknown": 2,
            "near-black": 1,
        }.get(self.content_state, 2)


_input_capability_cache: InputCapabilityVerification | None = None
# Every capability probe below can move the real pointer.  Keep it process-wide
# and serialized; discovery/status readers never acquire this lock in order to
# start a probe and therefore can never turn unauthenticated traffic into input.
_input_capability_probe_lock = threading.RLock()

CAPABILITY_NAMES = (
    (CAPABILITY_REMOTE_DESKTOP, "RemoteDesktop"),
    (CAPABILITY_INPUT_CONTROL, "InputControl"),
    (CAPABILITY_CLIPBOARD_TEXT, "ClipboardText"),
    (CAPABILITY_CLIPBOARD_SNAPSHOT_V1, "ClipboardSnapshotV1"),
    (CAPABILITY_FILE_RECEIVE, "FileReceive"),
    (CAPABILITY_FILE_RECEIVE_LOCATION, "FileReceiveLocation"),
    (CAPABILITY_FILE_TRANSFER_RECEIPT, "FileTransferReceipt"),
    (CAPABILITY_CAPTURE_TARGET_SELECTION, "CaptureTargetSelection"),
    (CAPABILITY_FILE_SEND, "FileSend"),
    (CAPABILITY_FILE_CHECKSUM, "FileChecksum"),
    (CAPABILITY_FILE_TRANSFER_CANCEL, "FileTransferCancel"),
    (CAPABILITY_FILE_TRANSFER_PREVIEW, "FileTransferPreview"),
    (CAPABILITY_SHORT_GOP_H264, "ShortGopH264"),
    (CAPABILITY_HIGH_FRAME_RATE_H264, "HighFrameRateH264"),
    (CAPABILITY_HIGH_QUALITY_JPEG, "HighQualityJpeg"),
)

DEPENDENCY_COMMANDS = (
    ("xdpyinfo", "X11 display size detection"),
    ("ffmpeg", "continuous low-latency X11 capture"),
    ("import", "X11 root screenshot capture"),
    ("convert", "placeholder frame generation"),
    ("ldconfig", "native XTest input library discovery"),
    ("xdotool", "X11 input fallback"),
    ("xclip", "clipboard text and file URI read/write"),
    ("xsel", "clipboard text fallback"),
    ("wl-paste", "Wayland clipboard text and file URI read"),
)

FALLBACK_PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="
)
JPEG_SOI = b"\xff\xd8"
JPEG_EOI = b"\xff\xd9"
H264_START_CODE_3 = b"\x00\x00\x01"
H264_START_CODE_4 = b"\x00\x00\x00\x01"
H264_AUD_3 = H264_START_CODE_3 + b"\x09"
H264_AUD_4 = H264_START_CODE_4 + b"\x09"
FFMPEG_READ_CHUNK_BYTES = 16 * 1024
# Legacy JPEG frame payloads carry width, height, capture time and encode time
# ahead of the encoded image.  Keep the image2pipe parser inside the same
# authenticated 32 MiB message boundary enforced by the wire protocol.
LEGACY_FRAME_HEADER_BYTES = struct.calcsize("<iidd")
MAX_JPEG_FRAME_BYTES = MAX_FRAME_PAYLOAD_BYTES - LEGACY_FRAME_HEADER_BYTES
FFMPEG_STALE_FRAME_SECONDS = 0.5
FFMPEG_WARMUP_SECONDS = 0.8
VIEWER_CODEC_NEGOTIATION_GRACE_SECONDS = 2.0
H264_FIRST_ACCESS_UNIT_TIMEOUT_SECONDS = 1.5
H264_MAX_BUFFER_BYTES = 8 * 1024 * 1024
H264_STALE_FRAME_SECONDS = 0.5
# A frame freshness limit is not an encoder failure deadline. Brief X11/GPU
# scheduling pauses must not permanently blacklist a working NVENC encoder.
# Keep the latest-only mailbox/freshness limit unchanged; this adds no buffering.
H264_ENCODER_STALL_SECONDS = 2.0
H264_HARDWARE_ENCODERS = (
    ("h264_nvenc", "NVIDIA NVENC"),
    ("h264_qsv", "Intel Quick Sync"),
    ("h264_vaapi", "VA-API"),
    ("h264_v4l2m2m", "V4L2 M2M"),
)
DEFAULT_HOST_FPS = 60.0
MAX_HOST_FPS = 60.0
MAX_JPEG_FALLBACK_FPS = 30.0
QHD_PIXEL_COUNT = 2560 * 1440
UHD_PIXEL_COUNT = 3840 * 2160
STANDARD_H264_BITS_PER_PIXEL_FRAME = 0.18
UHD_H264_BITS_PER_PIXEL_FRAME = 0.24
UHD_HIGH_FRAME_RATE_H264_BITS_PER_PIXEL_FRAME = 0.16
DEFAULT_H264_MAX_BITRATE_BPS = 160_000_000
MIN_H264_MAX_BITRATE_BPS = 1_500_000
MAX_H264_MAX_BITRATE_BPS = 200_000_000
HOST_IDLE_POLL_SECONDS = 0.02
SOCKET_RECEIVE_BUFFER_BYTES = 32 * 1024
SOCKET_SEND_BUFFER_BYTES = 128 * 1024
INPUT_QUEUE_LIMIT = 256
INPUT_RELEASE_RESERVE = 64
INPUT_CAPABILITY_CACHE_SECONDS = 30.0
DISPLAY_PROBE_TIMEOUT_SECONDS = 1.0
DISPLAY_CONTENT_PROBE_TIMEOUT_SECONDS = 2.0
DISPLAY_CONTENT_PROBE_SIZE = (96, 54)
DISPLAY_NEAR_BLACK_LUMA = 6
DISPLAY_NEAR_BLACK_FRACTION = 0.995
DISPLAY_SIZE_REFRESH_SECONDS = 2.0
DISPLAY_AUTO_CANDIDATES = (":0", ":1", ":99")
MAX_DETECTED_DISPLAY_WIDTH = 16384
MAX_DETECTED_DISPLAY_HEIGHT = 16384
MAX_DETECTED_DISPLAY_ASPECT_RATIO = 8.0
INPUT_MOUSE_MOVE = 1
INPUT_MOUSE_DOWN = 2
INPUT_MOUSE_UP = 3
INPUT_MOUSE_WHEEL = 4
INPUT_KEY_DOWN = 5
INPUT_KEY_UP = 6
INPUT_TEXT = 7
INPUT_PINCH_ZOOM = 8
REMOTE_KEYBOARD_HAS_SCAN_CODE = 1 << 0
REMOTE_KEYBOARD_EXTENDED = 1 << 1
REMOTE_KEYBOARD_VALID_FLAGS = (
    REMOTE_KEYBOARD_HAS_SCAN_CODE
    | REMOTE_KEYBOARD_EXTENDED
)
MOUSE_LEFT = 1
MOUSE_RIGHT = 2
MOUSE_MIDDLE = 3
XDOTOOL_MOUSE_BUTTONS = {
    MOUSE_LEFT: 1,
    MOUSE_RIGHT: 3,
    MOUSE_MIDDLE: 2,
}
WINDOWS_VK_TO_XDOTOOL = {
    0x08: "BackSpace",
    0x09: "Tab",
    0x0C: "Clear",
    0x0D: "Return",
    0x10: "Shift_L",
    0x11: "Control_L",
    0x12: "Alt_L",
    0x13: "Pause",
    0x14: "Caps_Lock",
    0xA0: "Shift_L",
    0xA1: "Shift_R",
    0xA2: "Control_L",
    0xA3: "Control_R",
    0xA4: "Alt_L",
    0xA5: "Alt_R",
    0x1B: "Escape",
    0x20: "space",
    0x21: "Page_Up",
    0x22: "Page_Down",
    0x23: "End",
    0x24: "Home",
    0x25: "Left",
    0x26: "Up",
    0x27: "Right",
    0x28: "Down",
    0x2C: "Print",
    0x2D: "Insert",
    0x2E: "Delete",
    0x5B: "Super_L",
    0x5C: "Super_R",
    0x5D: "Menu",
    0x60: "KP_0",
    0x61: "KP_1",
    0x62: "KP_2",
    0x63: "KP_3",
    0x64: "KP_4",
    0x65: "KP_5",
    0x66: "KP_6",
    0x67: "KP_7",
    0x68: "KP_8",
    0x69: "KP_9",
    0x6A: "KP_Multiply",
    0x6B: "KP_Add",
    0x6C: "KP_Separator",
    0x6D: "KP_Subtract",
    0x6E: "KP_Decimal",
    0x6F: "KP_Divide",
    0x90: "Num_Lock",
    0x91: "Scroll_Lock",
    0xBA: "semicolon",
    0xBB: "equal",
    0xBC: "comma",
    0xBD: "minus",
    0xBE: "period",
    0xBF: "slash",
    0xC0: "grave",
    0xDB: "bracketleft",
    0xDC: "backslash",
    0xDD: "bracketright",
    0xDE: "apostrophe",
    0xE2: "less",
}
WINDOWS_VK_TO_X11_KEYSYM = {
    **WINDOWS_VK_TO_XDOTOOL,
    0x20: "space",
}
WINDOWS_MODIFIER_VKS = frozenset((0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5))
X11_MODIFIER_TO_WINDOWS_VK = {
    "Shift_L": 0xA0, "Shift_R": 0xA1,
    "Control_L": 0xA2, "Control_R": 0xA3,
    "Alt_L": 0xA4, "Alt_R": 0xA5,
}

WINDOWS_SCAN_TO_X11_KEY = {
    (0x1C, True): "KP_Enter",
    (0x1D, False): "Control_L",
    (0x1D, True): "Control_R",
    (0x2A, False): "Shift_L",
    (0x35, True): "KP_Divide",
    (0x36, False): "Shift_R",
    (0x37, False): "KP_Multiply",
    (0x38, False): "Alt_L",
    (0x38, True): "Alt_R",
    (0x47, False): "KP_Home",
    (0x48, False): "KP_Up",
    (0x49, False): "KP_Prior",
    (0x4A, False): "KP_Subtract",
    (0x4B, False): "KP_Left",
    (0x4C, False): "KP_Begin",
    (0x4D, False): "KP_Right",
    (0x4E, False): "KP_Add",
    (0x4F, False): "KP_End",
    (0x50, False): "KP_Down",
    (0x51, False): "KP_Next",
    (0x52, False): "KP_Insert",
    (0x53, False): "KP_Delete",
}


@dataclass
class TransferItem:
    path: Path
    transfer_name: str
    temporary: bool = False
    source_path: Path | None = None
    source_kind: str = "文件"
    source_size: int = 0


@dataclass
class ReturnOperation:
    explicit_paths: tuple[str, ...]
    viewer_capabilities: int
    cancel_event: threading.Event = field(default_factory=threading.Event)
    confirm_event: threading.Event = field(default_factory=threading.Event)
    cancel_lock: threading.Lock = field(default_factory=threading.Lock)
    cancel_reason: str = "远端文件回传已取消。"
    plan: list[TransferItem] | None = None
    ignored_count: int = 0
    preview_ready: bool = False
    terminal_reported: bool = False

    def cancel(self, reason: str) -> None:
        with self.cancel_lock:
            if self.cancel_event.is_set():
                return
            self.cancel_reason = reason
            self.cancel_event.set()


@dataclass(frozen=True)
class InputCommand:
    kind: int
    button: int
    x: int
    y: int
    data: int


@dataclass(frozen=True)
class ClipboardOperation:
    kind: int
    text: str = ""
    request_id: str = ""
    known_revision: str = ""


class HostPressedInputState:
    def __init__(self) -> None:
        self._pressed_inputs: list[InputCommand] = []
        self._pressed_key_identities: set[tuple[int, int, int]] = set()
        self._pressed_mouse_buttons: set[int] = set()
        self._last_pointer_x = 0
        self._last_pointer_y = 0

    @property
    def pressed_key_count(self) -> int:
        return len(self._pressed_key_identities)

    @property
    def pressed_mouse_button_count(self) -> int:
        return len(self._pressed_mouse_buttons)

    def observe(self, command: InputCommand) -> None:
        if command.kind in {
            INPUT_MOUSE_MOVE,
            INPUT_MOUSE_DOWN,
            INPUT_MOUSE_UP,
            INPUT_MOUSE_WHEEL,
            INPUT_PINCH_ZOOM,
        }:
            self._last_pointer_x = command.x
            self._last_pointer_y = command.y

        if command.kind == INPUT_KEY_DOWN:
            identity = input_key_identity(command)
            if identity not in self._pressed_key_identities:
                self._pressed_key_identities.add(identity)
                self._pressed_inputs.append(command)
            return

        if command.kind == INPUT_KEY_UP:
            pressed_index = next(
                (
                    index
                    for index in range(len(self._pressed_inputs) - 1, -1, -1)
                    if self._pressed_inputs[index].kind == INPUT_KEY_DOWN
                    and input_key_identity(self._pressed_inputs[index])
                    == input_key_identity(command)
                ),
                -1,
            )
            if pressed_index < 0 and command.data not in WINDOWS_MODIFIER_VKS:
                pressed_index = next(
                    (
                        index
                        for index in range(len(self._pressed_inputs) - 1, -1, -1)
                        if self._pressed_inputs[index].kind == INPUT_KEY_DOWN
                        and self._pressed_inputs[index].data == command.data
                    ),
                    -1,
                )
            if pressed_index >= 0:
                pressed = self._pressed_inputs.pop(pressed_index)
                self._pressed_key_identities.discard(input_key_identity(pressed))
            return

        if command.kind == INPUT_MOUSE_DOWN:
            if command.button not in self._pressed_mouse_buttons:
                self._pressed_mouse_buttons.add(command.button)
                self._pressed_inputs.append(command)
            return

        if command.kind == INPUT_MOUSE_UP:
            pressed_index = next(
                (
                    index
                    for index in range(len(self._pressed_inputs) - 1, -1, -1)
                    if self._pressed_inputs[index].kind == INPUT_MOUSE_DOWN
                    and self._pressed_inputs[index].button == command.button
                ),
                -1,
            )
            if pressed_index >= 0:
                self._pressed_inputs.pop(pressed_index)
                self._pressed_mouse_buttons.discard(command.button)

    def take_release_commands(self) -> tuple[InputCommand, ...]:
        releases: list[InputCommand] = []
        for pressed in reversed(self._pressed_inputs):
            if pressed.kind == INPUT_KEY_DOWN:
                releases.append(
                    InputCommand(
                        INPUT_KEY_UP,
                        0,
                        pressed.x,
                        pressed.y,
                        pressed.data,
                    )
                )
            elif pressed.kind == INPUT_MOUSE_DOWN:
                releases.append(
                    InputCommand(
                        INPUT_MOUSE_UP,
                        pressed.button,
                        self._last_pointer_x,
                        self._last_pointer_y,
                        0,
                    )
                )

        self._pressed_inputs.clear()
        self._pressed_key_identities.clear()
        self._pressed_mouse_buttons.clear()
        return tuple(releases)


class HostWritePriority:
    """Control responses go before the next frame, never inside an AES packet."""

    def __init__(self) -> None:
        self.condition = threading.Condition()
        self.active = False
        self.waiting_controls = 0

    @contextmanager
    def enter(self, video: bool):
        with self.condition:
            if not video:
                self.waiting_controls += 1
                self.condition.notify_all()
            try:
                while self.active or (video and self.waiting_controls):
                    self.condition.wait()
                self.active = True
            finally:
                if not video:
                    self.waiting_controls -= 1
        try:
            yield
        finally:
            with self.condition:
                self.active = False
                self.condition.notify_all()


class HostHeartbeatResponder:
    """One active + one pending Pong; a slow writer cannot block input reads."""

    def __init__(self, send, on_failure):
        self.send = send
        self.on_failure = on_failure
        self.condition = threading.Condition()
        self.pending = False
        self.stopped = False
        self.thread = None

    def request(self):
        with self.condition:
            if self.stopped:
                return False
            self.pending = True
            if self.thread is None:
                self.thread = threading.Thread(target=self._run, name="RemoteDeskHostPong", daemon=True)
                self.thread.start()
            self.condition.notify_all()
            return True

    def _run(self):
        try:
            while True:
                with self.condition:
                    while not self.pending and not self.stopped:
                        self.condition.wait()
                    if self.stopped:
                        return
                    self.pending = False
                self.send()
        except Exception:
            with self.condition:
                if self.stopped:
                    return
                self.stopped = True
                self.pending = False
            self.on_failure()

    def close(self):
        with self.condition:
            self.stopped = True
            self.pending = False
            self.condition.notify_all()
            thread = self.thread
        # The session closes its socket before joining, waking a blocked write.
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=1)


class HostInboundLivenessTracker:
    """Tracks only time spent waiting for one complete authenticated message.

    Control handling is intentionally outside the tracked interval. A slow
    disk write, checksum, archive walk, or user-confirmed file-return plan may
    legitimately take longer than the network deadline and must not look like
    a dead peer. The viewer heartbeat supplies a complete Ping every five
    seconds while the session is otherwise idle.
    """

    def __init__(
        self,
        timeout_seconds: float = HOST_SESSION_INBOUND_TIMEOUT_SECONDS,
        clock: Any = time.monotonic,
    ) -> None:
        if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
            raise ValueError("inbound liveness timeout must be positive")
        self.timeout_seconds = float(timeout_seconds)
        self._clock = clock
        self._lock = threading.Lock()
        self._read_generation = 0
        self._read_started_at = 0.0
        self._waiting_for_message = False

    def begin_read(self, now: float | None = None) -> int:
        started_at = self._clock() if now is None else float(now)
        with self._lock:
            self._read_generation = (
                1
                if self._read_generation >= (1 << 63) - 1
                else self._read_generation + 1
            )
            self._read_started_at = started_at
            self._waiting_for_message = True
            return self._read_generation

    def end_read(self, generation: int) -> None:
        with self._lock:
            if (
                not self._waiting_for_message
                or generation != self._read_generation
            ):
                return
            self._waiting_for_message = False
            self._read_started_at = 0.0

    def expire_if_timed_out(self, now: float | None = None) -> bool:
        checked_at = self._clock() if now is None else float(now)
        with self._lock:
            if (
                not self._waiting_for_message
                or checked_at < self._read_started_at
                or checked_at - self._read_started_at < self.timeout_seconds
            ):
                return False
            # Claim this generation atomically. If a complete message won the
            # lock first, end_read cleared the wait and no timeout is emitted.
            # If the deadline won, a late completion cannot cancel teardown.
            self._waiting_for_message = False
            self._read_started_at = 0.0
            return True

    def next_check_delay(
        self,
        maximum_seconds: float,
        now: float | None = None,
    ) -> float:
        maximum = max(0.001, float(maximum_seconds))
        checked_at = self._clock() if now is None else float(now)
        with self._lock:
            if not self._waiting_for_message or checked_at < self._read_started_at:
                return maximum
            remaining = self.timeout_seconds - (
                checked_at - self._read_started_at
            )
            return max(0.0, min(maximum, remaining))

    def is_waiting_for_message(self) -> bool:
        with self._lock:
            return self._waiting_for_message


@dataclass(frozen=True)
class H264EncoderCommand:
    encoder_name: str
    display_name: str
    command: tuple[str, ...]


@dataclass(frozen=True)
class H264EncodedFrame:
    width: int
    height: int
    flags: int
    encoded: bytes
    frame_id: int
    encoder_name: str


def _cached_input_capability_verification(
    display: str,
    *,
    require_fresh: bool,
    now: float | None = None,
) -> InputCapabilityVerification | None:
    checked_at = time.monotonic() if now is None else float(now)
    with _input_capability_probe_lock:
        cached = _input_capability_cache
        if cached is None or cached.display != display:
            return None
        if (
            require_fresh
            and checked_at - cached.checked_at > INPUT_CAPABILITY_CACHE_SECONDS
        ):
            return None
        return cached


def _store_input_capability_verification(
    display: str,
    available: bool,
    backend: str | None,
    detail: str,
    now: float | None = None,
) -> InputCapabilityVerification:
    global _input_capability_cache

    with _input_capability_probe_lock:
        cached = InputCapabilityVerification(
            time.monotonic() if now is None else float(now),
            display,
            bool(available),
            backend if available else None,
            detail,
        )
        _input_capability_cache = cached
        return cached


def input_control_status() -> tuple[bool, str]:
    """Return only an already verified status; never move the pointer.

    Discovery calls this path for unauthenticated packets.  A stale result is
    safe to advertise as a hint because an authenticated session binds its
    DeviceInfo capability to a retained controller and refreshes an expired
    verification before enabling input.
    """

    display = os.environ.get("DISPLAY") or ""
    if not display:
        return False, "DISPLAY is not set"
    cached = _cached_input_capability_verification(
        display,
        require_fresh=False,
    )
    if cached is None:
        return False, "input capability has not been actively verified"
    return cached.available, cached.detail


def probe_input_control_status(
    *,
    force: bool = False,
) -> tuple[bool, str]:
    """Actively verify pointer motion under a process-wide cooldown lock."""

    display = os.environ.get("DISPLAY") or ""
    with _input_capability_probe_lock:
        if not display:
            cached = _store_input_capability_verification(
                display,
                False,
                None,
                "DISPLAY is not set",
            )
            return cached.available, cached.detail

        if not force:
            cached = _cached_input_capability_verification(
                display,
                require_fresh=True,
            )
            if cached is not None:
                return cached.available, cached.detail

        native_available, native_detail = native_x11_input_status()
        if native_available:
            cached = _store_input_capability_verification(
                display,
                True,
                "XTest",
                native_detail,
            )
            return cached.available, cached.detail

        xdotool_available, xdotool_detail = xdotool_input_status()
        if xdotool_available:
            cached = _store_input_capability_verification(
                display,
                True,
                "xdotool",
                xdotool_detail,
            )
            return cached.available, cached.detail

        cached = _store_input_capability_verification(
            display,
            False,
            None,
            f"XTest unavailable: {native_detail}; "
            f"xdotool unavailable: {xdotool_detail}",
        )
        return cached.available, cached.detail


def input_control_available() -> bool:
    available, _ = input_control_status()
    return available


def get_host_capabilities(input_available: bool | None = None) -> int:
    capabilities = BASE_HOST_CAPABILITIES
    verified_input = (
        input_control_available()
        if input_available is None
        else bool(input_available)
    )
    if verified_input:
        capabilities |= CAPABILITY_INPUT_CONTROL
    return capabilities


def configure_low_latency_socket(sock: socket.socket, receive_buffer_size: int, send_buffer_size: int) -> None:
    for option, value in low_latency_socket_options(receive_buffer_size, send_buffer_size):
        if value <= 0:
            continue
        try:
            sock.setsockopt(option[0], option[1], value)
        except OSError:
            pass


def host_send_buffer_bytes(peer_address: str) -> int:
    try:
        address = ipaddress.ip_address(peer_address)
        address = getattr(address, "ipv4_mapped", None) or address
        if address.is_loopback:
            return 16 * 1024
    except ValueError:
        pass
    return SOCKET_SEND_BUFFER_BYTES


def low_latency_socket_options(receive_buffer_size: int, send_buffer_size: int) -> list[tuple[tuple[int, int], int]]:
    options = [
        ((socket.IPPROTO_TCP, socket.TCP_NODELAY), 1),
        ((socket.SOL_SOCKET, socket.SO_KEEPALIVE), 1),
        ((socket.SOL_SOCKET, socket.SO_RCVBUF), receive_buffer_size),
        ((socket.SOL_SOCKET, socket.SO_SNDBUF), send_buffer_size),
    ]
    quick_ack = getattr(socket, "TCP_QUICKACK", None)
    if quick_ack is not None:
        options.append(((socket.IPPROTO_TCP, quick_ack), 1))
    return options


def decode_input_payload(payload: bytes) -> InputCommand:
    if len(payload) != INPUT_PAYLOAD_LENGTH:
        raise ProtocolError("input command payload length is invalid")
    kind, button, x, y, data = struct.unpack("<BBiii", payload)
    if kind not in {
        INPUT_MOUSE_MOVE,
        INPUT_MOUSE_DOWN,
        INPUT_MOUSE_UP,
        INPUT_MOUSE_WHEEL,
        INPUT_KEY_DOWN,
        INPUT_KEY_UP,
        INPUT_TEXT,
        INPUT_PINCH_ZOOM,
    }:
        raise ProtocolError(f"unsupported input command kind: {kind}")
    if button not in (0, MOUSE_LEFT, MOUSE_RIGHT, MOUSE_MIDDLE):
        raise ProtocolError(f"unsupported mouse button: {button}")
    if kind in (INPUT_KEY_DOWN, INPUT_KEY_UP):
        has_scan_code = bool(y & REMOTE_KEYBOARD_HAS_SCAN_CODE)
        if (
            data <= 0
            or data > 0xFE
            or x < 0
            or x > 0xFF
            or y & ~REMOTE_KEYBOARD_VALID_FLAGS
            or has_scan_code != (x > 0)
            or (
                y & REMOTE_KEYBOARD_EXTENDED
                and not has_scan_code
            )
        ):
            raise ProtocolError("keyboard scan code metadata is invalid")
    return InputCommand(kind, button, x, y, data)


def input_keyboard_flags(command: InputCommand) -> int:
    # Older Windows viewers incorrectly marked right Shift's scan 0x36 as E0.
    # Normalize only that known Shift identity, never real extended Ctrl/Alt or
    # unrelated keys. Share the rule with held-key tracking so corrected key-up
    # metadata cannot accidentally release the other concurrently held Shift.
    if (
        command.kind in (INPUT_KEY_DOWN, INPUT_KEY_UP)
        and command.data in (0x10, 0xA1)
        and command.x == 0x36
        and command.y & REMOTE_KEYBOARD_HAS_SCAN_CODE
    ):
        return command.y & ~REMOTE_KEYBOARD_EXTENDED
    return command.y


def input_key_identity(command: InputCommand) -> tuple[int, int, int]:
    if command.data in WINDOWS_MODIFIER_VKS:
        # Raw input and low-level keyboard hooks may alternate generic and
        # side-specific VKs, or omit scans for an explicit side. Track the side
        # actually injected, not the representation; retain the original down
        # command separately for disconnect cleanup. A generic no-scan key has
        # only the existing left-side meaning, never permission to clear right.
        side = X11_MODIFIER_TO_WINDOWS_VK.get(input_command_key_name(command))
        if side is not None:
            return side, 0, 0
    return command.data, command.x, input_keyboard_flags(command)


def enqueue_input_command(queue_items: deque[InputCommand], command: InputCommand, max_items: int) -> bool:
    if max_items <= 0:
        return False

    if command.kind == INPUT_MOUSE_MOVE:
        if len(queue_items) == 1 and queue_items[0].kind == INPUT_MOUSE_MOVE and queue_items[0] == command:
            return False
        retained = [item for item in queue_items if item.kind != INPUT_MOUSE_MOVE]
        if len(retained) != len(queue_items):
            queue_items.clear()
            queue_items.extend(retained)

    if len(queue_items) >= max_items:
        drop_index = next((index for index, item in enumerate(queue_items) if item.kind == INPUT_MOUSE_MOVE), -1)
        if drop_index >= 0:
            remove_deque_item_at(queue_items, drop_index)
        elif (
            command.kind not in (INPUT_MOUSE_UP, INPUT_KEY_UP)
            or len(queue_items) >= max_items + INPUT_RELEASE_RESERVE
        ):
            return False

    queue_items.append(command)
    return True


def calculate_frame_loop_wait_seconds(
    continuous_capture_waited: bool,
    sent_frame: bool,
    frame_interval: float,
    elapsed: float,
) -> float:
    if continuous_capture_waited:
        # ContinuousFrameCapture.read_frame already waits for a newer frame.
        # Sleeping here as well would add another polling interval to a miss.
        return 0.0
    target_interval = frame_interval if sent_frame else HOST_IDLE_POLL_SECONDS
    return max(0.0, target_interval - max(0.0, elapsed))


def normalize_host_fps(fps: float) -> float:
    try:
        normalized = float(fps)
    except (TypeError, ValueError):
        return DEFAULT_HOST_FPS
    if not math.isfinite(normalized):
        return DEFAULT_HOST_FPS
    return max(1.0, min(MAX_HOST_FPS, normalized))


def calculate_jpeg_fallback_fps(width: int, height: int, requested_fps: float) -> float:
    """Keep the compatibility path usable without saturating the LAN/UI.

    The requested rate up to 60 fps is reserved for persistent hardware H.264.
    MJPEG has much larger frames and usually ends in a Tk copy/present path, so
    high-resolution fallbacks intentionally trade cadence for responsiveness.
    """

    pixel_count = max(1, int(width)) * max(1, int(height))
    if pixel_count >= UHD_PIXEL_COUNT:
        fallback_limit = 12.0
    elif pixel_count >= QHD_PIXEL_COUNT:
        fallback_limit = 20.0
    else:
        fallback_limit = MAX_JPEG_FALLBACK_FPS
    return min(normalize_host_fps(requested_fps), fallback_limit)


def negotiated_h264_fps(requested_fps: float, viewer_capabilities: int) -> float:
    normalized = normalize_host_fps(requested_fps)
    if viewer_capabilities & CAPABILITY_HIGH_FRAME_RATE_H264:
        return normalized
    return min(MAX_JPEG_FALLBACK_FPS, normalized)


def normalize_h264_max_bitrate_bps(max_bitrate_mbps: float) -> int:
    try:
        bitrate_bps = float(max_bitrate_mbps) * 1_000_000.0
    except (TypeError, ValueError):
        return DEFAULT_H264_MAX_BITRATE_BPS
    if not math.isfinite(bitrate_bps):
        return DEFAULT_H264_MAX_BITRATE_BPS
    return int(
        max(
            MIN_H264_MAX_BITRATE_BPS,
            min(MAX_H264_MAX_BITRATE_BPS, bitrate_bps),
        )
    )


def pop_next_input_command(queue_items: deque[InputCommand]) -> InputCommand | None:
    if not queue_items:
        return None
    command = queue_items.popleft()
    if command.kind != INPUT_MOUSE_MOVE:
        return command
    while queue_items and queue_items[0].kind == INPUT_MOUSE_MOVE:
        command = queue_items.popleft()
    return command


def remove_deque_item_at(queue_items: deque[Any], index: int) -> None:
    queue_items.rotate(-index)
    queue_items.popleft()
    queue_items.rotate(index)


def map_frame_point_to_display(
    x: int,
    y: int,
    frame_width: int,
    frame_height: int,
    display_width: int,
    display_height: int,
) -> tuple[int, int]:
    safe_frame_width = max(1, frame_width)
    safe_frame_height = max(1, frame_height)
    safe_display_width = max(1, display_width)
    safe_display_height = max(1, display_height)
    mapped_x = round(max(0, min(safe_frame_width - 1, x)) * (safe_display_width - 1) / max(1, safe_frame_width - 1))
    mapped_y = round(max(0, min(safe_frame_height - 1, y)) * (safe_display_height - 1) / max(1, safe_frame_height - 1))
    return mapped_x, mapped_y


def adjacent_pointer_probe_target(
    x: int,
    y: int,
    display_width: int,
    display_height: int,
) -> tuple[int, int] | None:
    """Choose an in-bounds point exactly one pixel from the current pointer."""

    if (
        display_width <= 0
        or display_height <= 0
        or x < 0
        or y < 0
        or x >= display_width
        or y >= display_height
    ):
        return None
    if x + 1 < display_width:
        return x + 1, y
    if x > 0:
        return x - 1, y
    if y + 1 < display_height:
        return x, y + 1
    if y > 0:
        return x, y - 1
    return None


def verify_pointer_motion_capability(
    backend_name: str,
    query_pointer: Callable[[], tuple[int, int]],
    move_pointer: Callable[[int, int], bool],
    synchronize: Callable[[], None],
    display_size: tuple[int, int],
) -> tuple[bool, str]:
    """Verify synthetic motion and restore the user's pointer immediately.

    XTestQueryExtension and a successful injection return value are not enough:
    WSLg can acknowledge XTestFakeMotionEvent without changing the pointer.  A
    one-pixel move is the smallest observable probe.  It never clicks, and the
    original point is restored before capability advertisement.  If another
    actor moves the pointer during the probe, cleanup does not overwrite that
    newer position.
    """

    try:
        original = query_pointer()
    except Exception as ex:
        return False, f"{backend_name} could not query the pointer: {ex}"

    target = adjacent_pointer_probe_target(
        original[0],
        original[1],
        display_size[0],
        display_size[1],
    )
    if target is None:
        return (
            False,
            f"{backend_name} cannot choose a safe one-pixel pointer probe "
            f"inside {display_size[0]}x{display_size[1]} at {original}",
        )

    move_attempted = False
    restored = False
    try:
        move_attempted = True
        if not move_pointer(target[0], target[1]):
            return False, f"{backend_name} rejected the one-pixel pointer probe"
        synchronize()
        observed = query_pointer()
        if observed != target:
            return (
                False,
                f"{backend_name} reported successful motion but the pointer "
                f"was {observed}, expected {target}; synthetic input is not "
                "effective on this display",
            )

        current_before_restore = query_pointer()
        synchronize()
        confirmed_before_restore = query_pointer()
        if (
            current_before_restore != target
            or confirmed_before_restore != target
        ):
            concurrent_position = (
                current_before_restore
                if current_before_restore != target
                else confirmed_before_restore
            )
            return (
                False,
                f"{backend_name} pointer changed concurrently to "
                f"{concurrent_position}; the newer position was left "
                "unchanged",
            )
        # X11 has no compare-and-swap cursor operation. A real user can still
        # move in the tiny interval between this second confirmation and the
        # restore request. Therefore active probes are limited to serialized
        # startup/authenticated-session checks with a cooldown; discovery can
        # never enter this path. The post-restore query below catches a move
        # that wins after the restore request, and cleanup never writes again
        # unless the cursor is still at our exact one-pixel target.
        if not move_pointer(original[0], original[1]):
            return False, f"{backend_name} could not restore the pointer"
        synchronize()
        restored_position = query_pointer()
        restored = restored_position == original
        if not restored:
            return (
                False,
                f"{backend_name} pointer restore verification failed: "
                f"observed {restored_position}, expected {original}",
            )
        return True, f"{backend_name} (one-pixel motion verified and restored)"
    except Exception as ex:
        return False, f"{backend_name} pointer motion probe failed: {ex}"
    finally:
        if move_attempted and not restored:
            try:
                current = query_pointer()
                # Do not clobber a concurrent real user movement.  Restore only
                # when the cursor is still at the exact point owned by us.
                if current == target:
                    move_pointer(original[0], original[1])
                    synchronize()
            except Exception:
                pass


def xdotool_key_name(virtual_key: int) -> str | None:
    if 0x30 <= virtual_key <= 0x39:
        return chr(virtual_key)
    if 0x41 <= virtual_key <= 0x5A:
        return chr(virtual_key).lower()
    if 0x70 <= virtual_key <= 0x87:
        return f"F{virtual_key - 0x6F}"
    return WINDOWS_VK_TO_XDOTOOL.get(virtual_key)


def x11_key_name(virtual_key: int) -> str | None:
    if 0x30 <= virtual_key <= 0x39:
        return chr(virtual_key)
    if 0x41 <= virtual_key <= 0x5A:
        return chr(virtual_key).lower()
    if 0x70 <= virtual_key <= 0x87:
        return f"F{virtual_key - 0x6F}"
    return WINDOWS_VK_TO_X11_KEYSYM.get(virtual_key)


def input_command_key_name(command: InputCommand) -> str | None:
    if command.kind not in (INPUT_KEY_DOWN, INPUT_KEY_UP):
        return None
    flags = input_keyboard_flags(command)
    if flags & REMOTE_KEYBOARD_HAS_SCAN_CODE:
        physical_name = WINDOWS_SCAN_TO_X11_KEY.get(
            (
                command.x,
                bool(flags & REMOTE_KEYBOARD_EXTENDED),
            )
        )
        if physical_name is not None:
            return physical_name
    return x11_key_name(command.data)


def text_codepoint_to_xdotool_commands(codepoint: int) -> list[list[str]]:
    if codepoint in (0x0A, 0x0D):
        return [["key", "Return"]]
    if codepoint == 0x09:
        return [["key", "Tab"]]
    if not (
        0x20 <= codepoint <= 0xD7FF
        or 0xE000 <= codepoint <= 0x10FFFF
    ):
        return []
    # libxdo temporarily maps Unicode keysyms. 12 ms still lost CJK characters
    # on a real Jetson/Tk target under 1080p hardware capture load: the target
    # must process MappingNotify before the helper restores/reuses its keycode.
    # Give only non-ASCII text extra settling time; pointer/physical-key events
    # remain native and ASCII keeps its existing cadence. No persistent keymap
    # remapping or clipboard replacement is used.
    delay_ms = "50" if codepoint > 0x7F else "12"
    return [["type", "--clearmodifiers", "--delay", delay_ms, "--", chr(codepoint)]]


def find_shared_library(*names: str) -> str | None:
    for name in names:
        found = ctypes.util.find_library(name)
        if found:
            return found
    for candidate in names:
        for path in (
            f"lib{candidate}.so.6",
            f"lib{candidate}.so",
            f"/lib/x86_64-linux-gnu/lib{candidate}.so.6",
            f"/usr/lib/x86_64-linux-gnu/lib{candidate}.so.6",
        ):
            try:
                ctypes.CDLL(path)
                return path
            except Exception:
                continue
    return None


def native_x11_libraries_available() -> bool:
    return find_shared_library("X11") is not None and find_shared_library("Xtst") is not None


class NativeX11InputController:
    def __init__(
        self,
        *,
        verify_pointer_motion: bool = True,
        initialize: bool = True,
    ) -> None:
        self.available = False
        self.error: str | None = None
        self.verification_detail: str | None = None
        self.display: Any | None = None
        self.x11: Any | None = None
        self.xtst: Any | None = None
        if not initialize:
            self.error = "XTest backend was not selected"
            return
        if not os.environ.get("DISPLAY"):
            self.error = "DISPLAY is not set"
            return

        try:
            x11_path = find_shared_library("X11")
            xtst_path = find_shared_library("Xtst")
            if x11_path is None or xtst_path is None:
                self.error = "libX11 or libXtst is not available"
                return

            self.x11 = ctypes.CDLL(x11_path)
            self.xtst = ctypes.CDLL(xtst_path)
            self._configure_ctypes()
            self.display = self.x11.XOpenDisplay(os.environ.get("DISPLAY", "").encode("utf-8"))
            if not self.display:
                self.error = "XOpenDisplay failed"
                self.display = None
                return

            event_base = ctypes.c_int()
            error_base = ctypes.c_int()
            major = ctypes.c_int()
            minor = ctypes.c_int()
            if not self.xtst.XTestQueryExtension(
                self.display,
                ctypes.byref(event_base),
                ctypes.byref(error_base),
                ctypes.byref(major),
                ctypes.byref(minor),
            ):
                self.error = "XTest extension is not available"
                self.close()
                return

            if verify_pointer_motion:
                verified, detail = self._verify_pointer_motion()
                self.verification_detail = detail
                if not verified:
                    self.error = detail
                    self.close()
                    return
            else:
                self.verification_detail = (
                    "XTest (recent process-level pointer verification reused)"
                )

            self.available = True
        except Exception as ex:
            self.error = repr(ex)
            self.close()

    def close(self) -> None:
        display = self.display
        self.display = None
        if display and self.x11 is not None:
            try:
                self.x11.XCloseDisplay(display)
            except Exception:
                pass
        self.available = False

    def apply(
        self,
        command: InputCommand,
        frame_size: tuple[int, int],
        display_size: tuple[int, int],
    ) -> bool:
        if not self.available or self.display is None or self.x11 is None or self.xtst is None:
            return False

        frame_width, frame_height = frame_size
        display_width, display_height = display_size
        if command.kind in (INPUT_MOUSE_MOVE, INPUT_MOUSE_DOWN, INPUT_MOUSE_UP, INPUT_MOUSE_WHEEL, INPUT_PINCH_ZOOM):
            x, y = map_frame_point_to_display(command.x, command.y, frame_width, frame_height, display_width, display_height)
            self._move(x, y)
            if command.kind == INPUT_MOUSE_MOVE:
                self._flush()
                return True
            if command.kind in (INPUT_MOUSE_WHEEL, INPUT_PINCH_ZOOM):
                self._wheel(command.data)
                self._flush()
                return True
            button = XDOTOOL_MOUSE_BUTTONS.get(command.button)
            if button is None:
                raise ProtocolError("mouse down/up requires a supported button")
            self._button(button, command.kind == INPUT_MOUSE_DOWN)
            self._flush()
            return True

        if command.kind in (INPUT_KEY_DOWN, INPUT_KEY_UP):
            key_name = input_command_key_name(command)
            if key_name is None:
                return False
            keycode = self._keycode(key_name)
            if keycode == 0:
                return False
            self.xtst.XTestFakeKeyEvent(self.display, keycode, 1 if command.kind == INPUT_KEY_DOWN else 0, 0)
            self._flush()
            return True

        if command.kind == INPUT_TEXT and command.data in (0x09, 0x0A, 0x0D):
            key_name = "Tab" if command.data == 0x09 else "Return"
            keycode = self._keycode(key_name)
            if keycode == 0:
                return False
            self.xtst.XTestFakeKeyEvent(self.display, keycode, 1, 0)
            self.xtst.XTestFakeKeyEvent(self.display, keycode, 0, 0)
            self._flush()
            return True

        return False

    def _configure_ctypes(self) -> None:
        self.x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
        self.x11.XOpenDisplay.restype = ctypes.c_void_p
        self.x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
        self.x11.XCloseDisplay.restype = ctypes.c_int
        self.x11.XFlush.argtypes = [ctypes.c_void_p]
        self.x11.XFlush.restype = ctypes.c_int
        self.x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XSync.restype = ctypes.c_int
        self.x11.XDefaultScreen.argtypes = [ctypes.c_void_p]
        self.x11.XDefaultScreen.restype = ctypes.c_int
        self.x11.XDisplayWidth.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XDisplayWidth.restype = ctypes.c_int
        self.x11.XDisplayHeight.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.x11.XDisplayHeight.restype = ctypes.c_int
        self.x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        self.x11.XDefaultRootWindow.restype = ctypes.c_ulong
        self.x11.XQueryPointer.argtypes = [
            ctypes.c_void_p,
            ctypes.c_ulong,
            ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_uint),
        ]
        self.x11.XQueryPointer.restype = ctypes.c_int
        self.x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
        self.x11.XStringToKeysym.restype = ctypes.c_ulong
        self.x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        self.x11.XKeysymToKeycode.restype = ctypes.c_uint

        self.xtst.XTestQueryExtension.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
        ]
        self.xtst.XTestQueryExtension.restype = ctypes.c_int
        self.xtst.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeMotionEvent.restype = ctypes.c_int
        self.xtst.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeButtonEvent.restype = ctypes.c_int
        self.xtst.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
        self.xtst.XTestFakeKeyEvent.restype = ctypes.c_int

    def _verify_pointer_motion(self) -> tuple[bool, str]:
        if self.display is None or self.x11 is None or self.xtst is None:
            return False, "XTest display is not initialized"
        screen = int(self.x11.XDefaultScreen(self.display))
        display_size = (
            int(self.x11.XDisplayWidth(self.display, screen)),
            int(self.x11.XDisplayHeight(self.display, screen)),
        )
        return verify_pointer_motion_capability(
            "XTest",
            self._query_pointer,
            lambda x, y: bool(
                self.xtst.XTestFakeMotionEvent(self.display, -1, x, y, 0)
            ),
            lambda: self.x11.XSync(self.display, 0),
            display_size,
        )

    def _query_pointer(self) -> tuple[int, int]:
        if self.display is None or self.x11 is None:
            raise RuntimeError("X11 display is not initialized")
        root = self.x11.XDefaultRootWindow(self.display)
        root_return = ctypes.c_ulong()
        child_return = ctypes.c_ulong()
        root_x = ctypes.c_int()
        root_y = ctypes.c_int()
        window_x = ctypes.c_int()
        window_y = ctypes.c_int()
        mask = ctypes.c_uint()
        if not self.x11.XQueryPointer(
            self.display,
            root,
            ctypes.byref(root_return),
            ctypes.byref(child_return),
            ctypes.byref(root_x),
            ctypes.byref(root_y),
            ctypes.byref(window_x),
            ctypes.byref(window_y),
            ctypes.byref(mask),
        ):
            raise RuntimeError("XQueryPointer failed")
        return int(root_x.value), int(root_y.value)

    def _move(self, x: int, y: int) -> None:
        if not self.xtst.XTestFakeMotionEvent(self.display, -1, int(x), int(y), 0):
            raise ProtocolError("XTest mouse move failed")

    def _button(self, button: int, pressed: bool) -> None:
        if not self.xtst.XTestFakeButtonEvent(self.display, button, 1 if pressed else 0, 0):
            raise ProtocolError("XTest mouse button failed")

    def _wheel(self, delta: int) -> None:
        if delta == 0:
            return
        button = 4 if delta > 0 else 5
        repeat = max(1, min(6, abs(delta) // 120 or 1))
        for _ in range(repeat):
            self._button(button, True)
            self._button(button, False)

    def _keycode(self, key_name: str) -> int:
        keysym = self.x11.XStringToKeysym(key_name.encode("ascii", errors="ignore"))
        if keysym == 0:
            return 0
        return int(self.x11.XKeysymToKeycode(self.display, keysym))

    def _flush(self) -> None:
        self.x11.XFlush(self.display)


def native_x11_input_status() -> tuple[bool, str]:
    controller = NativeX11InputController()
    try:
        if controller.available:
            return True, controller.verification_detail or "XTest"
        return False, controller.error or "XTest initialization failed"
    finally:
        controller.close()


def native_x11_input_available() -> bool:
    available, _ = native_x11_input_status()
    return available


def _parse_xdotool_pointer(output: str) -> tuple[int, int]:
    values: dict[str, int] = {}
    for line in output.splitlines():
        name, separator, raw_value = line.partition("=")
        if separator and name in ("X", "Y"):
            values[name] = int(raw_value.strip())
    if "X" not in values or "Y" not in values:
        raise RuntimeError("xdotool returned no X/Y pointer coordinates")
    return values["X"], values["Y"]


def xdotool_input_status() -> tuple[bool, str]:
    xdotool = shutil.which("xdotool")
    if xdotool is None:
        return False, "xdotool is not installed"
    if not os.environ.get("DISPLAY"):
        return False, "DISPLAY is not set"

    def run(args: list[str]) -> subprocess.CompletedProcess[str]:
        result = subprocess.run(
            [xdotool, *args],
            check=False,
            capture_output=True,
            text=True,
            timeout=0.6,
        )
        if result.returncode != 0:
            detail = result.stderr.strip() or f"exit code {result.returncode}"
            raise RuntimeError(f"xdotool {' '.join(args)} failed: {detail}")
        return result

    try:
        geometry_parts = run(["getdisplaygeometry"]).stdout.split()
        if len(geometry_parts) != 2:
            raise RuntimeError("xdotool returned invalid display geometry")
        display_size = int(geometry_parts[0]), int(geometry_parts[1])

        return verify_pointer_motion_capability(
            "xdotool",
            lambda: _parse_xdotool_pointer(
                run(["getmouselocation", "--shell"]).stdout
            ),
            lambda x, y: (
                run(["mousemove", str(x), str(y)]).returncode == 0
            ),
            lambda: None,
            display_size,
        )
    except Exception as ex:
        return False, str(ex)


def xdotool_input_available() -> bool:
    available, _ = xdotool_input_status()
    return available


class LinuxInputController:
    def __init__(self) -> None:
        display = os.environ.get("DISPLAY") or ""
        with _input_capability_probe_lock:
            probe_input_control_status()
            verification = _cached_input_capability_verification(
                display,
                require_fresh=True,
            )
            selected_backend = (
                verification.backend
                if verification is not None and verification.available
                else None
            )
            if selected_backend == "XTest":
                # Re-open a controller retained by this session, but reuse the
                # recent process-level motion proof so authentication does not
                # cause a second visible one-pixel movement.
                self.native = NativeX11InputController(
                    verify_pointer_motion=False
                )
            else:
                self.native = NativeX11InputController(
                    initialize=False
                )

            self.xdotool = shutil.which("xdotool")
            self.xdotool_available = (
                selected_backend == "xdotool"
                and self.xdotool is not None
                and bool(display)
            )
            self.xdotool_detail = (
                verification.detail
                if verification is not None
                else "input capability verification is unavailable"
            )
            self.available = (
                selected_backend == "XTest"
                and self.native.available
            ) or self.xdotool_available
            if selected_backend == "XTest" and not self.native.available:
                self.xdotool_detail = (
                    "recent XTest verification could not be bound to the "
                    f"session controller: {self.native.error or 'unknown error'}"
                )
                _store_input_capability_verification(
                    display,
                    False,
                    None,
                    self.xdotool_detail,
                )
            elif selected_backend == "xdotool" and not self.xdotool_available:
                self.xdotool_detail = (
                    "recent xdotool verification could not be bound to the "
                    "session controller"
                )
                _store_input_capability_verification(
                    display,
                    False,
                    None,
                    self.xdotool_detail,
                )
        self.backend_name = (
            "XTest"
            if self.native.available
            else "xdotool"
            if self.xdotool_available
            else "unavailable"
        )
        self.unavailable_reason = (
            f"XTest: {self.native.error or 'unavailable'}; "
            f"xdotool: {self.xdotool_detail}"
        )

    def close(self) -> None:
        self.native.close()

    def apply(
        self,
        command: InputCommand,
        frame_size: tuple[int, int],
        display_size: tuple[int, int],
    ) -> bool:
        if not self.available:
            raise ProtocolError(
                "Linux input injection requires verified XTest or xdotool "
                f"motion with DISPLAY ({self.unavailable_reason})."
            )

        if self.native.apply(command, frame_size, display_size):
            return True

        # XTest handles pointer/physical-key events and control characters, but
        # cannot synthesize arbitrary Unicode without a text/keymap helper.
        # A verified native session used to disable xdotool altogether, making
        # viewer "send text" fail even with xdotool installed. Reuse the native
        # display proof for this text-only helper; never broaden unverified
        # pointer/key fallback or advertise input on an unavailable session.
        if command.kind == INPUT_TEXT and self.native.available:
            if self.xdotool is None:
                raise ProtocolError(
                    "Linux 文字输入缺少 xdotool，请重新启动 RemoteDesk 并同意安装运行依赖；"
                    "鼠标和实体按键仍可使用。"
                )
            sent = False
            for item in text_codepoint_to_xdotool_commands(command.data):
                self._run(item, timeout=0.8)
                sent = True
            return sent

        if self.xdotool is None or not self.xdotool_available:
            raise ProtocolError("Linux input command requires a working xdotool fallback.")

        frame_width, frame_height = frame_size
        display_width, display_height = display_size
        if command.kind in (INPUT_MOUSE_MOVE, INPUT_MOUSE_DOWN, INPUT_MOUSE_UP, INPUT_MOUSE_WHEEL, INPUT_PINCH_ZOOM):
            x, y = map_frame_point_to_display(command.x, command.y, frame_width, frame_height, display_width, display_height)
            if command.kind == INPUT_MOUSE_MOVE:
                self._run(["mousemove", str(x), str(y)], timeout=0.4)
                return True
            if command.kind == INPUT_MOUSE_WHEEL:
                self._run(["mousemove", str(x), str(y)], timeout=0.4)
                self._wheel(command.data)
                return True
            if command.kind == INPUT_PINCH_ZOOM:
                self._run(["mousemove", str(x), str(y)], timeout=0.4)
                self._wheel(command.data)
                return True
            button = XDOTOOL_MOUSE_BUTTONS.get(command.button)
            if button is None:
                raise ProtocolError("mouse down/up requires a supported button")
            verb = "mousedown" if command.kind == INPUT_MOUSE_DOWN else "mouseup"
            self._run(["mousemove", str(x), str(y)], timeout=0.4)
            self._run([verb, str(button)], timeout=0.4)
            return True

        if command.kind in (INPUT_KEY_DOWN, INPUT_KEY_UP):
            key_name = input_command_key_name(command)
            if key_name is None:
                return False
            verb = "keydown" if command.kind == INPUT_KEY_DOWN else "keyup"
            self._run([verb, key_name], timeout=0.4)
            return True

        if command.kind == INPUT_TEXT:
            sent = False
            for item in text_codepoint_to_xdotool_commands(command.data):
                self._run(item, timeout=0.8)
                sent = True
            return sent

        return False

    def _wheel(self, delta: int) -> None:
        if delta == 0:
            return
        button = "4" if delta > 0 else "5"
        repeat = max(1, min(6, abs(delta) // 120 or 1))
        self._run(["click", "--repeat", str(repeat), button], timeout=0.6)

    def _run(self, args: list[str], timeout: float) -> None:
        operation = args[0] if args else "input"
        try:
            result = subprocess.run(
                [self.xdotool or "xdotool", *args],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=timeout,
            )
        except subprocess.TimeoutExpired:
            # TimeoutExpired.__str__ contains argv, including typed secrets.
            raise ProtocolError(f"xdotool {operation} timed out") from None
        if result.returncode != 0:
            # The type argument can be a password or private clipboard text.
            # Do not echo it into the host log or remote diagnostic UI.
            raise ProtocolError(f"xdotool {operation} failed (exit {result.returncode})")


class JpegFrameLimitError(ProtocolError):
    """Raised when FFmpeg emits an MJPEG frame that cannot fit on the wire."""


def extract_jpeg_frames(
    buffer: bytearray,
    max_frame_bytes: int = MAX_JPEG_FRAME_BYTES,
) -> list[bytes]:
    if max_frame_bytes < len(JPEG_SOI) + len(JPEG_EOI):
        raise ValueError("max_frame_bytes is too small for a JPEG frame")

    frames: list[bytes] = []
    while True:
        start = buffer.find(JPEG_SOI)
        if start < 0:
            if len(buffer) > 1:
                del buffer[:-1]
            return frames
        if start > 0:
            del buffer[:start]

        end = buffer.find(JPEG_EOI, len(JPEG_SOI))
        if end < 0:
            if len(buffer) > max_frame_bytes:
                raise JpegFrameLimitError(
                    "ffmpeg MJPEG frame exceeded the protocol-safe limit of "
                    f"{max_frame_bytes} encoded bytes"
                )
            return frames

        frame_end = end + len(JPEG_EOI)
        if frame_end > max_frame_bytes:
            raise JpegFrameLimitError(
                "ffmpeg MJPEG frame exceeded the protocol-safe limit of "
                f"{max_frame_bytes} encoded bytes"
            )
        frames.append(bytes(buffer[:frame_end]))
        del buffer[:frame_end]


def find_annex_b_start(
    data: bytes | bytearray,
    start: int = 0,
) -> tuple[int, int] | None:
    start3 = data.find(H264_START_CODE_3, max(0, start))
    start4 = data.find(H264_START_CODE_4, max(0, start))
    if start3 < 0:
        return (start4, 4) if start4 >= 0 else None
    if start4 >= 0 and start4 <= start3:
        return start4, 4
    return start3, 3


def annex_b_nal_units(data: bytes | bytearray) -> list[tuple[int, bytes]]:
    starts: list[tuple[int, int]] = []
    search_offset = 0
    data_length = len(data)
    while search_offset + 3 <= data_length:
        found = find_annex_b_start(data, search_offset)
        if found is None:
            break
        starts.append(found)
        search_offset = found[0] + found[1]

    units: list[tuple[int, bytes]] = []
    for unit_index, (start, prefix_length) in enumerate(starts):
        payload_start = start + prefix_length
        if payload_start >= data_length:
            continue
        end = starts[unit_index + 1][0] if unit_index + 1 < len(starts) else data_length
        units.append((data[payload_start] & 0x1F, bytes(data[start:end])))
    return units


def find_h264_aud(data: bytes | bytearray, start: int = 0) -> int:
    start3 = data.find(H264_AUD_3, max(0, start))
    start4 = data.find(H264_AUD_4, max(0, start))
    if start3 < 0:
        return start4
    if start4 < 0:
        return start3
    return min(start3, start4)


def extract_h264_access_units(buffer: bytearray, flush: bool = False) -> list[bytes]:
    first_aud = find_h264_aud(buffer)
    if first_aud < 0:
        if len(buffer) > H264_MAX_BUFFER_BYTES:
            del buffer[:-4]
        return []
    if first_aud > 0:
        del buffer[:first_aud]

    aud_offsets = [0]
    search_offset = len(H264_AUD_3)
    while True:
        next_aud = find_h264_aud(buffer, search_offset)
        if next_aud < 0:
            break
        aud_offsets.append(next_aud)
        search_offset = next_aud + len(H264_AUD_3)

    access_units = [
        bytes(buffer[start:end])
        for start, end in zip(aud_offsets, aud_offsets[1:])
        if end > start
    ]
    if len(aud_offsets) >= 2:
        del buffer[: aud_offsets[-1]]

    if flush and buffer:
        candidate = bytes(buffer)
        if any(
            nal_type in (1, 5)
            for nal_type, _ in annex_b_nal_units(candidate)
        ):
            access_units.append(candidate)
        buffer.clear()
    elif len(buffer) > H264_MAX_BUFFER_BYTES:
        # An encoder that ignores the AUD insertion filter is unusable for
        # this packet-per-access-unit protocol. Keep only a small suffix while
        # the first-AU watchdog moves on to the next candidate.
        del buffer[:-4]
    return access_units


def normalize_h264_access_unit(
    access_unit: bytes,
    cached_sps: bytes | None,
    cached_pps: bytes | None,
) -> tuple[bytes, bytes | None, bytes | None, int]:
    units = annex_b_nal_units(access_unit)
    current_sps = next((nal for nal_type, nal in units if nal_type == 7), None)
    current_pps = next((nal for nal_type, nal in units if nal_type == 8), None)
    if current_sps is not None:
        cached_sps = current_sps
    if current_pps is not None:
        cached_pps = current_pps

    is_key_frame = any(nal_type == 5 for nal_type, _ in units)
    has_sps = current_sps is not None
    has_pps = current_pps is not None
    normalized = access_unit
    if is_key_frame and not (has_sps and has_pps) and cached_sps is not None and cached_pps is not None:
        prefix_length = 0
        if units and units[0][0] == 9:
            prefix_length = len(units[0][1])
        normalized = (
            access_unit[:prefix_length]
            + cached_sps
            + cached_pps
            + access_unit[prefix_length:]
        )
        has_sps = True
        has_pps = True

    flags = FRAME_FLAG_KEY_FRAME if is_key_frame else 0
    if has_sps and has_pps:
        flags |= FRAME_FLAG_CODEC_CONFIG
    return normalized, cached_sps, cached_pps, flags


def detect_ffmpeg_h264_encoders(ffmpeg_path: str) -> set[str] | None:
    try:
        result = subprocess.run(
            [ffmpeg_path, "-hide_banner", "-encoders"],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            timeout=2.0,
        )
    except Exception:
        return None
    if result.returncode != 0:
        return None
    known = {encoder_name for encoder_name, _display_name in H264_HARDWARE_ENCODERS}
    available: set[str] = set()
    for line in result.stdout.splitlines():
        fields = line.split()
        if len(fields) >= 2 and fields[0].startswith("V") and fields[1] in known:
            available.add(fields[1])
    return available


def find_vaapi_render_device() -> str:
    render_directory = Path("/dev/dri")
    try:
        devices = sorted(render_directory.glob("renderD*"))
    except OSError:
        devices = []
    return str(devices[0]) if devices else "/dev/dri/renderD128"


def calculate_h264_bitrate(
    width: int,
    height: int,
    fps: float,
    max_bitrate_bps: int = DEFAULT_H264_MAX_BITRATE_BPS,
) -> int:
    pixel_count = max(1, width) * max(1, height)
    normalized_fps = max(1.0, fps)
    if normalized_fps <= MAX_JPEG_FALLBACK_FPS:
        uhd_bits_per_pixel_frame = UHD_H264_BITS_PER_PIXEL_FRAME
    elif normalized_fps >= MAX_HOST_FPS:
        uhd_bits_per_pixel_frame = UHD_HIGH_FRAME_RATE_H264_BITS_PER_PIXEL_FRAME
    else:
        high_frame_rate_blend = (
            (normalized_fps - MAX_JPEG_FALLBACK_FPS)
            / (MAX_HOST_FPS - MAX_JPEG_FALLBACK_FPS)
        )
        uhd_bits_per_pixel_frame = (
            UHD_H264_BITS_PER_PIXEL_FRAME
            + (
                UHD_HIGH_FRAME_RATE_H264_BITS_PER_PIXEL_FRAME
                - UHD_H264_BITS_PER_PIXEL_FRAME
            )
            * high_frame_rate_blend
        )
    uhd_resolution_blend = max(
        0.0,
        min(
            1.0,
            (pixel_count - QHD_PIXEL_COUNT)
            / (UHD_PIXEL_COUNT - QHD_PIXEL_COUNT),
        ),
    )
    bits_per_pixel_frame = (
        STANDARD_H264_BITS_PER_PIXEL_FRAME
        + (
            uhd_bits_per_pixel_frame
            - STANDARD_H264_BITS_PER_PIXEL_FRAME
        )
        * uhd_resolution_blend
    )
    estimated = int(
        pixel_count
        * normalized_fps
        * bits_per_pixel_frame
    )
    normalized_limit = max(
        MIN_H264_MAX_BITRATE_BPS,
        min(MAX_H264_MAX_BITRATE_BPS, int(max_bitrate_bps)),
    )
    return max(MIN_H264_MAX_BITRATE_BPS, min(normalized_limit, estimated))


class AdaptiveH264ResolutionController:
    """Session-local TCP spatial fallback; preserve the user's size ceiling."""

    def __init__(self, width: int, height: int, fps: float, enabled: bool = True) -> None:
        self.requested_size = (max(2, int(width)) & ~1, max(2, int(height)) & ~1)
        self.reduced_size = self.fit_full_hd(*self.requested_size)
        self.output_size = self.requested_size
        self.target_fps = max(1.0, min(60.0, fps))
        self.enabled = enabled and self.reduced_size != self.requested_size
        self.recovery_attempted = False
        self.recovery_failed = False
        self.pressure_seconds = self.comfortable_seconds = self.profile_seconds = 0.0
        self._reset_window(time.monotonic())

    @staticmethod
    def fit_full_hd(width: int, height: int) -> tuple[int, int]:
        width, height = max(2, width), max(2, height)
        maximum = (1080, 1920) if height > width else (1920, 1080)
        scale = min(1.0, maximum[0] / width, maximum[1] / height)
        return max(2, int(width * scale)) & ~1, max(2, int(height * scale)) & ~1

    def _reset_window(self, now: float) -> None:
        self.window_started = now
        self.frames = self.encoded_bytes = 0
        self.write_seconds = 0.0

    def record_frame(self, encoded_bytes: int, write_seconds: float, fps: float) -> bool:
        now = time.monotonic()
        if fps != self.target_fps:
            self.target_fps = max(1.0, min(60.0, fps))
            self.pressure_seconds = self.comfortable_seconds = 0.0
            self._reset_window(now)
        self.frames += 1
        self.encoded_bytes += max(0, encoded_bytes)
        self.write_seconds += write_seconds
        elapsed = now - self.window_started
        if elapsed < 1.0:
            return False
        changed = self.observe(elapsed, self.frames / elapsed,
                               self.write_seconds * 1000 / self.frames,
                               self.encoded_bytes * 8 / elapsed / 1_000_000)
        self._reset_window(now)
        return changed

    def observe(self, seconds: float, fps: float, write_ms: float, mbps: float) -> bool:
        if not self.enabled:
            return False
        if (not all(math.isfinite(value) for value in (seconds, fps, write_ms, mbps))
                or seconds <= 0 or fps <= 0 or write_ms < 0 or mbps <= 0):
            self.pressure_seconds = self.comfortable_seconds = 0.0
            return False
        self.profile_seconds += seconds
        evidence = min(seconds, 1.5)
        budget = 1000 / self.target_fps
        if self.output_size == self.requested_size:
            pressure = fps < self.target_fps * 0.80 and write_ms > budget * 1.10
            self.pressure_seconds = self.pressure_seconds + evidence if pressure else 0.0
            if self.pressure_seconds < 3:
                return False
            self.recovery_failed = self.recovery_attempted
            self.output_size = self.reduced_size
        else:
            growth = max(1.0, calculate_h264_bitrate(*self.requested_size, self.target_fps) /
                         calculate_h264_bitrate(*self.reduced_size, self.target_fps))
            comfortable = fps >= self.target_fps * 0.95 and write_ms * growth < budget * 0.60
            self.comfortable_seconds = self.comfortable_seconds + evidence if comfortable else 0.0
            # One bounded trial; fast writes at low bitrate cannot prove spare
            # WAN capacity and must not cause recurring resolution oscillation.
            if self.recovery_failed or self.profile_seconds < 45 or self.comfortable_seconds < 10:
                return False
            self.output_size = self.requested_size
            self.recovery_attempted = True
        self.pressure_seconds = self.comfortable_seconds = self.profile_seconds = 0.0
        return True


def build_h264_hardware_encoder_commands(
    ffmpeg_path: str,
    display: str,
    source_width: int,
    source_height: int,
    output_width: int,
    output_height: int,
    fps: float,
    available_encoders: set[str] | None,
    vaapi_device: str | None = None,
    max_bitrate_bps: int = DEFAULT_H264_MAX_BITRATE_BPS,
) -> list[H264EncoderCommand]:
    output_width = max(2, int(output_width)) & ~1
    output_height = max(2, int(output_height)) & ~1
    fps = normalize_host_fps(fps)
    bit_rate = calculate_h264_bitrate(
        output_width,
        output_height,
        fps,
        max_bitrate_bps,
    )
    # Desktop IDR frames need substantially more room than motion-video frame
    # averages. Four frame budgets keep small text readable after a static
    # pause without changing the capped average bitrate or adding B-frames.
    vbv_size = max(256_000, int((bit_rate / max(1.0, fps)) * 4))
    common_input = [
        "-hide_banner",
        "-loglevel",
        "error",
        "-fflags",
        "nobuffer",
        "-flags",
        "low_delay",
        "-probesize",
        "32",
        "-analyzeduration",
        "0",
        "-f",
        "x11grab",
        "-draw_mouse",
        "1",
        "-video_size",
        f"{source_width}x{source_height}",
        "-framerate",
        f"{fps:g}",
        "-thread_queue_size",
        "1",
        "-i",
        display,
    ]
    common_output = [
        "-an",
        "-vsync",
        "0",
        "-g",
        "1",
        "-bf",
        "0",
        "-b:v",
        str(bit_rate),
        "-maxrate",
        str(bit_rate),
        "-bufsize",
        str(vbv_size),
        "-bsf:v",
        "h264_metadata=aud=insert",
        "-f",
        "h264",
        "-flush_packets",
        "1",
        "pipe:1",
    ]
    if (source_width, source_height) == (output_width, output_height):
        scale_filter = ""
    else:
        scale_filter = (
            f"scale={output_width}:{output_height}:"
            "force_original_aspect_ratio=decrease:flags=bicubic+accurate_rnd,"
            f"pad={output_width}:{output_height}:(ow-iw)/2:(oh-ih)/2:black,"
        )
    encoder_options: dict[str, tuple[list[str], str, list[str]]] = {
        "h264_nvenc": (
            [],
            # X11 already delivers CPU pixels. Let NVENC reuse its own NV12
            # upload surfaces instead of adding a separate CUDA filter upload.
            # This is still hardware encoding; quality/latency flags stay below.
            f"{scale_filter}format=nv12",
            [
                "-preset",
                "p4",
                "-tune",
                "ull",
                "-rc",
                "vbr",
                "-spatial-aq",
                "1",
                "-aq-strength",
                "1",
                "-rc-lookahead",
                "0",
                "-zerolatency",
                "1",
                "-delay",
                "0",
                "-forced-idr",
                "1",
            ],
        ),
        "h264_qsv": (
            [],
            f"{scale_filter}format=nv12",
            [
                "-preset",
                "veryfast",
                "-async_depth",
                "1",
                "-look_ahead",
                "0",
                "-forced_idr",
                "1",
                "-repeat_pps",
                "1",
            ],
        ),
        "h264_vaapi": (
            ["-vaapi_device", vaapi_device or find_vaapi_render_device()],
            f"{scale_filter}format=nv12,hwupload",
            ["-rc_mode", "CBR"],
        ),
        "h264_v4l2m2m": (
            [],
            f"{scale_filter}format=yuv420p",
            [],
        ),
    }

    commands: list[H264EncoderCommand] = []
    for encoder_name, display_name in H264_HARDWARE_ENCODERS:
        if available_encoders is not None and encoder_name not in available_encoders:
            continue
        device_options, video_filter, private_options = encoder_options[encoder_name]
        command = (
            [ffmpeg_path]
            + device_options
            + common_input
            + ["-vf", video_filter, "-c:v", encoder_name]
            + private_options
            + common_output
        )
        commands.append(H264EncoderCommand(encoder_name, display_name, tuple(command)))
    return commands


def find_jetson_gstreamer_encoder() -> str | None:
    """Jetson NVENC uses the vendor V4L2 plugin, not desktop FFmpeg NVENC.

    Discover only; a real all-IDR first-frame probe still qualifies every session.
    Missing/incompatible optional plugins keep the existing FFmpeg/JPEG fallback.
    """
    if not sys.platform.startswith("linux") or not Path("/etc/nv_tegra_release").is_file():
        return None
    if not any(Path(device).exists() for device in ("/dev/v4l2-nvenc", "/dev/nvhost-msenc")):
        return None
    launch, inspect = shutil.which("gst-launch-1.0"), shutil.which("gst-inspect-1.0")
    if not launch or not inspect:
        return None
    for plugin in ("ximagesrc", "videoconvert", "videoscale", "nvvidconv", "nvv4l2h264enc", "h264parse"):
        try:
            result = subprocess.run([inspect, plugin], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                    stderr=subprocess.DEVNULL, timeout=2, check=False)
        except (OSError, subprocess.TimeoutExpired):
            return None
        if result.returncode != 0:
            return None
    return launch


def build_jetson_h264_encoder_command(
    launch: str, display: str, source_width: int, source_height: int,
    output_width: int, output_height: int, fps: float,
    max_bitrate_bps: int = DEFAULT_H264_MAX_BITRATE_BPS,
) -> H264EncoderCommand | None:
    # gst-launch parses its own pipeline syntax even without a shell. Restrict
    # this optional local-X11 path instead of interpolating arbitrary DISPLAY.
    if not re.fullmatch(r":[0-9]+(?:\.[0-9]+)?", display):
        return None
    if source_width < 2 or source_height < 2:
        return None
    output_width, output_height = max(2, int(output_width)) & ~1, max(2, int(output_height)) & ~1
    rate = Fraction(str(normalize_host_fps(fps))).limit_denominator(1000)
    bitrate = calculate_h264_bitrate(output_width, output_height, fps, max_bitrate_bps)
    vbv = max(256_000, int(bitrate / float(rate) * 4))
    command = [launch, "-q", "ximagesrc", f"display-name={display}", "use-damage=false", "show-pointer=true",
               "do-timestamp=true", f"endx={int(source_width) - 1}", f"endy={int(source_height) - 1}",
               "!", f"video/x-raw,framerate={rate.numerator}/{rate.denominator}", "!", "videoconvert"]
    if (source_width, source_height) != (output_width, output_height):
        # Match the existing aspect-preserving/padded desktop stream geometry.
        # GPU encoding does not imply zero-copy X11 capture or CPU-free scaling.
        command += ["!", "videoscale", "method=3", "add-borders=true"]
    # Keep conversion and the coded VUI on the same matrix. Otherwise Jetson's
    # NVMM conversion and untagged H.264 can disagree on 601/709 (visible hue
    # and luminance shifts even on solid desktop colors). Convert directly to
    # NV12 before upload: the tested VIC I420 -> NV12 path also shifted colors
    # despite matching caps. Identical NV12 layouts avoid that extra conversion.
    command += ["!", f"video/x-raw,format=NV12,width={output_width},height={output_height},pixel-aspect-ratio=1/1,colorimetry=bt709",
                "!", "nvvidconv", "!", "video/x-raw(memory:NVMM),format=NV12,colorimetry=bt709",
                "!", "nvv4l2h264enc", "profile=4", "control-rate=1", f"bitrate={bitrate}", f"vbv-size={vbv}",
                "iframeinterval=1", "idrinterval=1", "num-B-Frames=0", "insert-sps-pps=true", "insert-aud=true", "insert-vui=true",
                "!", "h264parse", "!", "video/x-h264,stream-format=byte-stream,alignment=au",
                "!", "fdsink", "fd=1", "sync=false"]
    return H264EncoderCommand("jetson-gstreamer", "Jetson GStreamer NVENC", tuple(command))


class ContinuousHardwareH264Capture:
    def __init__(
        self,
        width: int,
        height: int,
        fps: float,
        mode: str,
        max_bitrate_bps: int = DEFAULT_H264_MAX_BITRATE_BPS,
    ) -> None:
        self.width = max(2, int(width)) & ~1
        self.height = max(2, int(height)) & ~1
        self.fps = normalize_host_fps(fps)
        self.max_bitrate_bps = max(
            MIN_H264_MAX_BITRATE_BPS,
            min(MAX_H264_MAX_BITRATE_BPS, int(max_bitrate_bps)),
        )
        self.stall_timeout_seconds = max(H264_ENCODER_STALL_SECONDS, 3.0 / self.fps)
        self.mode = mode
        self.ffmpeg_path = shutil.which("ffmpeg")
        self.jetson_gstreamer_path: str | None = None
        self.jetson_gstreamer_probed = False
        self.condition = threading.Condition()
        self.process: subprocess.Popen[bytes] | None = None
        self.reader_thread: threading.Thread | None = None
        self.selection_thread: threading.Thread | None = None
        self.latest_frame: H264EncodedFrame | None = None
        self.latest_at = 0.0
        self.latest_frame_id = 0
        self.selected_encoder_name: str | None = None
        self.source_width = self.width
        self.source_height = self.height
        self.source_size_ready = False
        self.desired = False
        self.exhausted = False
        self.closed = False
        self.generation = 0
        self.cached_sps: bytes | None = None
        self.cached_pps: bytes | None = None
        self.unavailable_logged = False

    def update_fps(self, fps: float) -> bool:
        normalized_fps = normalize_host_fps(fps)
        process: subprocess.Popen[bytes] | None = None
        with self.condition:
            if self.closed or self.fps == normalized_fps:
                return False
            self.fps = normalized_fps
            self.stall_timeout_seconds = max(
                H264_ENCODER_STALL_SECONDS,
                3.0 / self.fps,
            )
            self.generation += 1
            self.exhausted = False
            self.unavailable_logged = False
            self.selected_encoder_name = None
            self.latest_frame = None
            process = self.process
            self.process = None
            self._ensure_selection_thread_locked()
            self.condition.notify_all()
        self._terminate_process(process)
        return True

    def update_output_size(self, width: int, height: int) -> bool:
        """Restart only the encoder; never change the X11 display mode."""
        size = (max(2, int(width)) & ~1, max(2, int(height)) & ~1)
        with self.condition:
            if self.closed or size == (self.width, self.height):
                return False
            self.width, self.height = size
            self.generation += 1
            self.exhausted = self.unavailable_logged = False
            self.selected_encoder_name = None
            self.latest_frame = None
            self.cached_sps = self.cached_pps = None
            process = self.process
            self.process = None
            self._ensure_selection_thread_locked()
            self.condition.notify_all()
        self._terminate_process(process)
        return True

    def update_source_size(self, width: int, height: int) -> None:
        if not is_plausible_display_size(width, height):
            return
        process: subprocess.Popen[bytes] | None = None
        with self.condition:
            if self.closed:
                return
            changed = self.source_size_ready and (self.source_width, self.source_height) != (width, height)
            self.source_width = width
            self.source_height = height
            self.source_size_ready = True
            if changed:
                self.generation += 1
                self.exhausted = False
                self.selected_encoder_name = None
                self.latest_frame = None
                process = self.process
                self.process = None
            self._ensure_selection_thread_locked()
            self.condition.notify_all()
        self._terminate_process(process)

    def request_start(self) -> None:
        with self.condition:
            if self.closed:
                return
            self.desired = True
            self._ensure_selection_thread_locked()
            self.condition.notify_all()

    def request_recovery_frame(self) -> None:
        with self.condition:
            # Every hardware candidate is configured as all-IDR. Dropping the
            # mailbox guarantees the request is satisfied by a newly encoded
            # IDR, with cached SPS/PPS inserted if the driver omits them.
            self.latest_frame = None
            self.condition.notify_all()

    def read_frame(
        self,
        timeout: float,
        after_frame_id: int | None,
    ) -> H264EncodedFrame | None:
        self.expire_stalled_encoder_if_needed()
        self.request_start()
        deadline = time.monotonic() + max(0.0, timeout)
        with self.condition:
            while not self.closed:
                now = time.monotonic()
                frame = self.latest_frame
                process = self.process
                if (
                    self.selected_encoder_name is not None
                    and process is not None
                    and process.poll() is None
                    and frame is not None
                    and frame.frame_id != after_frame_id
                    and now - self.latest_at <= H264_STALE_FRAME_SECONDS
                ):
                    return frame
                remaining = deadline - now
                if remaining <= 0:
                    return None
                self.condition.wait(timeout=remaining)
        return None

    def expire_stalled_encoder_if_needed(self, now: float | None = None) -> bool:
        process: subprocess.Popen[bytes] | None = None
        encoder_name: str | None = None
        with self.condition:
            checked_at = time.monotonic() if now is None else now
            active_process = self.process
            if (
                self.selected_encoder_name is None
                or active_process is None
                or active_process.poll() is not None
                or self.latest_at <= 0.0
                or checked_at - self.latest_at <= self.stall_timeout_seconds
            ):
                return False
            process = active_process
            encoder_name = self.selected_encoder_name
            self.process = None
            self.reader_thread = None
            self.selected_encoder_name = None
            self.latest_frame = None
            self.condition.notify_all()

        # Do not hold `condition` while waiting for process/pipe teardown: the
        # selector and stdout reader both need that lock to advance cleanly.
        self._terminate_process(process)
        log(
            f"{encoder_name} H.264 output stalled for more than "
            f"{self.stall_timeout_seconds:g}s; trying next hardware encoder."
        )
        return True

    def is_running(self) -> bool:
        with self.condition:
            process = self.process
            return (
                self.selected_encoder_name is not None
                and process is not None
                and process.poll() is None
                and not self.closed
            )

    def is_starting(self) -> bool:
        with self.condition:
            thread = self.selection_thread
            process = self.process
            return (
                self.desired
                and not self.closed
                and not self.exhausted
                and thread is not None
                and thread.is_alive()
                and (
                    self.selected_encoder_name is None
                    or process is None
                    or process.poll() is not None
                )
            )

    def has_exhausted_candidates(self) -> bool:
        with self.condition:
            return self.exhausted

    def stop(self) -> None:
        with self.condition:
            self.desired = False
            self.generation += 1
            self.exhausted = False
            self.unavailable_logged = False
            process = self.process
            self.process = None
            self.selected_encoder_name = None
            self.latest_frame = None
            self.condition.notify_all()
        self._terminate_process(process)

    def close(self) -> None:
        with self.condition:
            self.closed = True
            self.desired = False
            self.generation += 1
            process = self.process
            self.process = None
            selection_thread = self.selection_thread
            self.selected_encoder_name = None
            self.latest_frame = None
            self.condition.notify_all()
        self._terminate_process(process)
        if (
            selection_thread is not None
            and selection_thread.is_alive()
            and selection_thread is not threading.current_thread()
        ):
            selection_thread.join(timeout=2.0)

    def _ensure_selection_thread_locked(self) -> None:
        thread = self.selection_thread
        if (
            not self.desired
            or self.closed
            or self.exhausted
            or not self.source_size_ready
            or (thread is not None and thread.is_alive())
        ):
            return
        self.selection_thread = threading.Thread(
            target=self._selection_loop,
            name="RemoteDeskLinuxH264Select",
            daemon=True,
        )
        self.selection_thread.start()

    def _selection_loop(self) -> None:
        while True:
            with self.condition:
                if self.closed or not self.desired:
                    return
                if not self.source_size_ready:
                    self.condition.wait(timeout=0.25)
                    continue
                generation = self.generation
                source_width = self.source_width
                source_height = self.source_height
                ffmpeg_path = self.ffmpeg_path
                display = os.environ.get("DISPLAY") or ""

            if self.mode == "placeholder" or not display:
                self._mark_exhausted("X11 capture is unavailable")
                return

            available_encoders = detect_ffmpeg_h264_encoders(ffmpeg_path) if ffmpeg_path else set()
            commands = build_h264_hardware_encoder_commands(
                ffmpeg_path,
                display,
                source_width,
                source_height,
                self.width,
                self.height,
                self.fps,
                available_encoders,
                max_bitrate_bps=self.max_bitrate_bps,
            ) if ffmpeg_path else []
            if not self.jetson_gstreamer_probed:
                self.jetson_gstreamer_path = find_jetson_gstreamer_encoder()
                self.jetson_gstreamer_probed = True
            if self.jetson_gstreamer_path:
                jetson = build_jetson_h264_encoder_command(self.jetson_gstreamer_path, display, source_width, source_height,
                                                         self.width, self.height, self.fps, self.max_bitrate_bps)
                if jetson is not None:
                    commands.insert(0, jetson)
            if not commands:
                self._mark_exhausted("no supported FFmpeg/Jetson hardware H.264 encoder")
                return

            restart_requested = False
            for candidate in commands:
                with self.condition:
                    if self.closed or not self.desired:
                        return
                    if generation != self.generation:
                        restart_requested = True
                        break
                    first_frame_id = self.latest_frame_id
                    self.latest_frame = None
                    self.cached_sps = None
                    self.cached_pps = None

                try:
                    process = subprocess.Popen(
                        list(candidate.command),
                        stdin=subprocess.DEVNULL,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL,
                        bufsize=0,
                    )
                except Exception as ex:
                    log(f"{candidate.display_name} H.264 probe could not start: {ex}")
                    continue

                reader_thread = threading.Thread(
                    target=self._reader_loop,
                    args=(process, generation, candidate.encoder_name),
                    name=f"RemoteDeskLinux{candidate.encoder_name}",
                    daemon=True,
                )
                with self.condition:
                    if self.closed or not self.desired or generation != self.generation:
                        self._terminate_process(process)
                        restart_requested = generation != self.generation
                        break
                    self.process = process
                    self.reader_thread = reader_thread
                    reader_thread.start()
                    deadline = time.monotonic() + H264_FIRST_ACCESS_UNIT_TIMEOUT_SECONDS
                    while (
                        not self.closed
                        and self.desired
                        and generation == self.generation
                        and process.poll() is None
                        and self.latest_frame_id == first_frame_id
                    ):
                        remaining = deadline - time.monotonic()
                        if remaining <= 0:
                            break
                        self.condition.wait(timeout=remaining)
                    probe_succeeded = (
                        generation == self.generation
                        and self.latest_frame_id != first_frame_id
                        and process.poll() is None
                    )
                    if probe_succeeded:
                        self.selected_encoder_name = candidate.encoder_name
                        self.exhausted = False
                        self.condition.notify_all()

                if not probe_succeeded:
                    with self.condition:
                        if self.process is process:
                            self.process = None
                            self.reader_thread = None
                            self.latest_frame = None
                        restart_requested = generation != self.generation
                        self.condition.notify_all()
                    self._terminate_process(process)
                    if reader_thread.is_alive() and reader_thread is not threading.current_thread():
                        reader_thread.join(timeout=0.5)
                    if restart_requested:
                        break
                    log(f"{candidate.display_name} H.264 first-AU probe failed; trying next hardware encoder.")
                    continue

                log(
                    "hardware H.264 capture enabled: "
                    f"encoder={candidate.display_name} ({candidate.encoder_name}); "
                    f"display={display}; source={source_width}x{source_height}; "
                    f"output={self.width}x{self.height}; fps={self.fps:g}; "
                    f"bitrate-cap={self.max_bitrate_bps / 1_000_000:g}Mbps; all-IDR"
                )
                with self.condition:
                    while (
                        not self.closed
                        and self.desired
                        and generation == self.generation
                        and self.process is process
                        and process.poll() is None
                    ):
                        self.condition.wait(timeout=0.25)
                    restart_requested = generation != self.generation
                    if self.process is process:
                        self.process = None
                        self.reader_thread = None
                        self.selected_encoder_name = None
                        self.latest_frame = None
                    self.condition.notify_all()
                self._terminate_process(process)
                if reader_thread.is_alive() and reader_thread is not threading.current_thread():
                    reader_thread.join(timeout=0.5)
                if self.closed or not self.desired:
                    return
                if restart_requested:
                    break
                log(f"{candidate.display_name} H.264 encoder stopped; trying next hardware encoder.")

            if restart_requested:
                continue
            self._mark_exhausted("all advertised hardware encoders failed their first-AU probe")
            return

    def _reader_loop(
        self,
        process: subprocess.Popen[bytes],
        generation: int,
        encoder_name: str,
    ) -> None:
        buffer = bytearray()
        stdout = process.stdout
        try:
            output_fd = stdout.fileno() if stdout is not None else None
            while output_fd is not None:
                with self.condition:
                    if self.closed or self.process is not process or generation != self.generation:
                        return
                chunk = os.read(output_fd, FFMPEG_READ_CHUNK_BYTES)
                if not chunk:
                    break
                buffer.extend(chunk)
                for access_unit in extract_h264_access_units(buffer):
                    self._publish_access_unit(process, generation, encoder_name, access_unit)
            for access_unit in extract_h264_access_units(buffer, flush=True):
                self._publish_access_unit(process, generation, encoder_name, access_unit)
        except Exception:
            pass
        finally:
            with self.condition:
                self.condition.notify_all()

    def _publish_access_unit(
        self,
        process: subprocess.Popen[bytes],
        generation: int,
        encoder_name: str,
        access_unit: bytes,
    ) -> None:
        with self.condition:
            if self.closed or self.process is not process or generation != self.generation:
                return
            encoded, self.cached_sps, self.cached_pps, flags = normalize_h264_access_unit(
                access_unit,
                self.cached_sps,
                self.cached_pps,
            )
            if not flags & FRAME_FLAG_KEY_FRAME:
                return
            if not flags & FRAME_FLAG_CODEC_CONFIG:
                # The recovery contract requires SPS/PPS with every IDR. Do
                # not expose a candidate until it proves that contract.
                return
            self.latest_frame_id += 1
            self.latest_frame = H264EncodedFrame(
                self.width,
                self.height,
                flags,
                encoded,
                self.latest_frame_id,
                encoder_name,
            )
            self.latest_at = time.monotonic()
            self.condition.notify_all()

    def _mark_exhausted(self, reason: str) -> None:
        with self.condition:
            self.exhausted = True
            self.selected_encoder_name = None
            self.latest_frame = None
            self.condition.notify_all()
            should_log = not self.unavailable_logged
            self.unavailable_logged = True
        if should_log:
            log(f"hardware H.264 capture unavailable: {reason}; using JPEG fallback when supported.")

    @staticmethod
    def _terminate_process(process: subprocess.Popen[bytes] | None) -> None:
        if process is None or process.poll() is not None:
            return
        try:
            process.terminate()
            process.wait(timeout=1.0)
        except Exception:
            try:
                process.kill()
                process.wait(timeout=1.0)
            except Exception:
                pass


class ContinuousFrameCapture:
    def __init__(self, width: int, height: int, fps: float, mode: str) -> None:
        self.width = max(1, int(width))
        self.height = max(1, int(height))
        self.fps = calculate_jpeg_fallback_fps(self.width, self.height, fps)
        self.mode = mode
        self.ffmpeg_path = shutil.which("ffmpeg")
        self.process: subprocess.Popen[bytes] | None = None
        self.reader_thread: threading.Thread | None = None
        self.condition = threading.Condition()
        self.latest_frame: tuple[int, int, bytes] | None = None
        self.latest_frame_id = 0
        self.latest_at = 0.0
        self.disabled = False
        self.closed = False
        self.restarting = False
        self.unavailable_logged = False
        self.unavailable_reason: str | None = None
        self.source_width = self.width
        self.source_height = self.height
        self.source_size_ready = False

    def update_source_size(self, width: int, height: int) -> None:
        if not is_plausible_display_size(width, height):
            return

        process: subprocess.Popen[bytes] | None = None
        reader_thread: threading.Thread | None = None
        with self.condition:
            if self.closed:
                return
            was_ready = self.source_size_ready
            size_changed = (self.source_width, self.source_height) != (width, height)
            should_start = not was_ready or size_changed
            self.source_width = width
            self.source_height = height
            self.source_size_ready = True
            if size_changed:
                # x11grab may exit before the periodic probe observes XRandR.
                # A confirmed new geometry is a safe point to retry capture.
                self.disabled = False
            if should_start:
                # Keep send-thread start attempts out until this worker has
                # completed the initial launch or XRandR-driven replacement.
                self.restarting = True
            if size_changed and self.process is not None and self.process.poll() is None:
                process = self.process
                reader_thread = self.reader_thread
                self.process = None
                self.reader_thread = None
                self.latest_frame = None
                self.latest_at = 0.0
            self.condition.notify_all()

        if process is not None:
            self._stop_process(process, reader_thread)

        if should_start:
            with self.condition:
                self.restarting = False
                self.condition.notify_all()
                # The condition is re-entrant. Holding it here prevents a send
                # thread from winning the launch race after `restarting` clears.
                # This method itself runs only on the display-size worker.
                self.start()

    def read_frame(self, timeout: float, after_frame_id: int | None = None) -> tuple[int, int, bytes, int] | None:
        if not self.start():
            return None

        deadline = time.monotonic() + max(0.0, timeout)
        with self.condition:
            while not self.closed:
                process = self.process
                if process is None:
                    return None
                if process.poll() is not None:
                    self._mark_unavailable(f"ffmpeg exited with code {process.returncode}")
                    return None

                now = time.monotonic()
                if (
                    self.latest_frame is not None
                    and self.latest_frame_id != after_frame_id
                    and now - self.latest_at <= FFMPEG_STALE_FRAME_SECONDS
                ):
                    return (*self.latest_frame, self.latest_frame_id)

                remaining = deadline - now
                if remaining <= 0:
                    return None
                self.condition.wait(timeout=remaining)
        return None

    def has_fresh_frame(self) -> bool:
        with self.condition:
            return self.latest_frame is not None and time.monotonic() - self.latest_at <= FFMPEG_STALE_FRAME_SECONDS

    def is_running(self) -> bool:
        with self.condition:
            process = self.process
            return (
                not self.closed
                and not self.disabled
                and not self.restarting
                and process is not None
                and process.poll() is None
            )

    def start(self) -> bool:
        with self.condition:
            if self.closed or self.disabled or self.restarting:
                return False
            if self.mode == "placeholder":
                return False
            if self.ffmpeg_path is None:
                self._mark_unavailable("ffmpeg is not installed")
                return False
            display = os.environ.get("DISPLAY")
            if not display:
                self._mark_unavailable("DISPLAY is not set")
                return False
            if not self.source_size_ready:
                return False
            if self.process is not None and self.process.poll() is None:
                return True
            source_width, source_height = self.source_width, self.source_height
            command = [
                self.ffmpeg_path,
                "-hide_banner",
                "-loglevel",
                "error",
                "-fflags",
                "nobuffer",
                "-flags",
                "low_delay",
                "-probesize",
                "32",
                "-analyzeduration",
                "0",
                "-f",
                "x11grab",
                "-draw_mouse",
                "1",
                "-video_size",
                f"{source_width}x{source_height}",
                "-framerate",
                f"{self.fps:g}",
                "-thread_queue_size",
                "1",
                "-i",
                display,
            ]
            if source_width > self.width or source_height > self.height:
                command += [
                    "-vf",
                    (
                        f"scale={self.width}:{self.height}:"
                        "force_original_aspect_ratio=decrease:flags=lanczos"
                    ),
                ]
            command += [
                "-an",
                "-vsync",
                "0",
                "-vcodec",
                "mjpeg",
                "-q:v",
                "2",
                "-f",
                "image2pipe",
                "-flush_packets",
                "1",
                "pipe:1",
            ]

            try:
                process = subprocess.Popen(
                    command,
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    bufsize=0,
                )
            except Exception as ex:
                self._mark_unavailable(f"failed to start ffmpeg: {ex}")
                return False

            self.process = process
            self.reader_thread = threading.Thread(
                target=self._reader_loop,
                args=(process,),
                daemon=True,
            )
            self.reader_thread.start()
        target_text = f"{self.width}x{self.height}" if source_width > self.width or source_height > self.height else "native"
        log(f"ffmpeg continuous capture enabled: display={display}; source={source_width}x{source_height}; output={target_text}; fps={self.fps:g}")
        return True

    def close(self) -> None:
        with self.condition:
            self.closed = True
            process = self.process
            reader_thread = self.reader_thread
            self.process = None
            self.reader_thread = None
            self.latest_frame = None
            self.condition.notify_all()
        self._stop_process(process, reader_thread)

    def pause(self) -> None:
        with self.condition:
            process = self.process
            reader_thread = self.reader_thread
            self.process = None
            self.reader_thread = None
            self.latest_frame = None
            self.latest_at = 0.0
            self.condition.notify_all()
        self._stop_process(process, reader_thread)

    @staticmethod
    def _stop_process(
        process: subprocess.Popen[bytes] | None,
        reader_thread: threading.Thread | None,
    ) -> None:
        if process is not None and process.poll() is None:
            try:
                process.terminate()
                process.wait(timeout=1)
            except Exception:
                try:
                    process.kill()
                    process.wait(timeout=1)
                except Exception:
                    pass

        if (
            reader_thread is not None
            and reader_thread.is_alive()
            and reader_thread is not threading.current_thread()
        ):
            reader_thread.join(timeout=1)

    def _reader_loop(self, process: subprocess.Popen[bytes]) -> None:
        buffer = bytearray()
        stdout = process.stdout
        try:
            output_fd = stdout.fileno() if stdout is not None else None
            while not self.closed and output_fd is not None:
                chunk = os.read(output_fd, FFMPEG_READ_CHUNK_BYTES)
                if not chunk:
                    break
                buffer.extend(chunk)
                for frame in extract_jpeg_frames(buffer):
                    frame_size = image_size_from_bytes(frame)
                    if frame_size is None:
                        continue
                    with self.condition:
                        if self.closed or self.process is not process:
                            return
                        self.latest_frame = (frame_size[0], frame_size[1], frame)
                        self.latest_frame_id += 1
                        self.latest_at = time.monotonic()
                        self.condition.notify_all()
        except JpegFrameLimitError as ex:
            owned_process = False
            with self.condition:
                if not self.closed and self.process is process:
                    # The reader is about to exit. Detach first so no sender
                    # can mistake this ffmpeg instance for a live capture,
                    # then terminate/reap it outside the condition. Otherwise
                    # ffmpeg remains blocked forever writing the abandoned
                    # image2pipe until the whole viewer session ends.
                    self.process = None
                    if self.reader_thread is threading.current_thread():
                        self.reader_thread = None
                    self.latest_frame = None
                    self.latest_at = 0.0
                    self._mark_unavailable(f"ffmpeg capture reader failed: {ex}")
                    self.condition.notify_all()
                    owned_process = True
            if owned_process:
                self._stop_process(process, None)
        except Exception as ex:
            with self.condition:
                if not self.closed and self.process is process:
                    self._mark_unavailable(f"ffmpeg capture reader failed: {ex}")
        finally:
            with self.condition:
                self.condition.notify_all()
                if not self.closed and self.process is process:
                    return_code = process.poll()
                    if return_code is not None:
                        self._mark_unavailable(f"ffmpeg exited with code {return_code}")

    def _mark_unavailable(self, reason: str) -> None:
        self.disabled = True
        self.unavailable_reason = reason
        if self.unavailable_logged:
            return
        self.unavailable_logged = True
        log(f"ffmpeg continuous capture unavailable: {reason}; falling back to ImageMagick capture.")


class LinuxHostSession:
    def __init__(
        self,
        sock: socket.socket,
        session: SecureSession,
        args: argparse.Namespace,
        stop_event: threading.Event,
        write_lock: Any | None = None,
        session_stop: threading.Event | None = None,
    ) -> None:
        self.sock = sock
        self.session = session
        self.args = args
        self.stop_event = stop_event
        self.session_stop = (
            session_stop
            if session_stop is not None
            else threading.Event()
        )
        self.write_lock = (
            write_lock
            if write_lock is not None
            else threading.Lock()
        )
        self.inbound_liveness = HostInboundLivenessTracker()
        self.write_priority = HostWritePriority()
        self.inbound_liveness_thread: threading.Thread | None = None
        self.heartbeat = HostHeartbeatResponder(
            lambda: self._write_message(MESSAGE_PONG, b""), self._heartbeat_failed)
        self.input_controller = LinuxInputController()
        # Bind the advertised capability to the controller retained by this
        # session.  A transient second probe cannot otherwise advertise input
        # after the session's actual backend failed (or hide one that passed).
        self.host_capabilities = get_host_capabilities(
            self.input_controller.available
        )
        if getattr(args, "device_id", ""): self.host_capabilities |= CAPABILITY_DEVICE_IDENTITY
        self.viewer_capabilities = 0
        self.video_lock = threading.Lock()
        self.viewer_video_codecs = VIDEO_CODEC_JPEG
        self.viewer_info_received = False
        self.startup_jpeg_sent = False
        self.codec_negotiation_deadline = time.monotonic() + VIEWER_CODEC_NEGOTIATION_GRACE_SECONDS
        self.h264_stream_active = False
        self.last_h264_frame_id: int | None = None
        self.incoming: Any | None = None
        self.pending_return_plan: list[TransferItem] | None = None
        self.pending_return_ignored_count = 0
        self.pending_return_operation: ReturnOperation | None = None
        self.return_condition = threading.Condition()
        self.return_active_operation: ReturnOperation | None = None
        self.return_queued_operation: ReturnOperation | None = None
        self.return_worker_stop = False
        self.return_worker_thread: threading.Thread | None = None
        self.clipboard_condition = threading.Condition()
        self.clipboard_queue: deque[ClipboardOperation] = deque()
        self.clipboard_worker_stop = False
        self.clipboard_worker_thread: threading.Thread | None = None
        self.input_condition = threading.Condition()
        self.input_queue: deque[InputCommand] = deque()
        self.input_state = HostPressedInputState()
        self.input_stop = False
        self.input_thread: threading.Thread | None = None
        self.reader_thread: threading.Thread | None = None
        self.input_unavailable_reported = False
        self.geometry_lock = threading.Lock()
        self.display_size: tuple[int, int] = (args.width, args.height)
        self.display_refresh_stop = threading.Event()
        self.display_refresh_thread: threading.Thread | None = None
        self.continuous_frame_ready = False
        self.last_continuous_frame_id: int | None = None
        self.capture_warmup_until = 0.0
        self.receive_dir = Path(args.receive_dir).expanduser()
        self.receive_dir.mkdir(parents=True, exist_ok=True)
        cleanup_stale_temporary_files(self.receive_dir)
        self.incoming_receive_budget = IncomingFileTransferBudget()
        self.frame_width, self.frame_height = args.width, args.height
        self.last_frame_width, self.last_frame_height = self.frame_width, self.frame_height
        self.requested_fps = normalize_host_fps(float(args.fps))
        self.continuous_capture = ContinuousFrameCapture(
            self.frame_width,
            self.frame_height,
            self.requested_fps,
            str(args.capture),
        )
        self.hardware_h264_capture = ContinuousHardwareH264Capture(
            self.frame_width,
            self.frame_height,
            negotiated_h264_fps(self.requested_fps, self.viewer_capabilities),
            str(args.capture),
            normalize_h264_max_bitrate_bps(args.max_video_bitrate_mbps),
        )
        self.h264_resolution = AdaptiveH264ResolutionController(
            self.frame_width, self.frame_height, self.hardware_h264_capture.fps,
            enabled=not getattr(args, "no_adaptive_video", False))
        self._start_display_size_refresh_thread()
        if self.host_capabilities & CAPABILITY_INPUT_CONTROL:
            self.input_thread = threading.Thread(target=self._input_loop, name="RemoteDeskLinuxInput", daemon=True)
            self.input_thread.start()

    def run(self) -> None:
        try:
            self.sock.settimeout(None)
            self._start_return_worker()
            self._start_clipboard_worker()
            self._write_control(encode_device_info(self.args.machine_name, PLATFORM_LINUX, self.host_capabilities))
            log(f"viewer session authenticated; advertising capabilities: {format_capabilities(self.host_capabilities)}")
            if self.host_capabilities & CAPABILITY_INPUT_CONTROL:
                log(f"Linux input backend: {self.input_controller.backend_name}")
            self._send_capture_targets()
            self._send_capture_target_changed()
            self.reader_thread = threading.Thread(target=self._read_loop, name="RemoteDeskLinuxReader", daemon=True)
            self.inbound_liveness_thread = threading.Thread(
                target=self._inbound_liveness_loop,
                name="RemoteDeskLinuxInboundWatchdog",
                daemon=True,
            )
            self.reader_thread.start()
            self.inbound_liveness_thread.start()

            prestarted_capture = self.continuous_capture.start()
            if prestarted_capture:
                self.capture_warmup_until = time.monotonic() + FFMPEG_WARMUP_SECONDS
            while not self.stop_event.is_set() and not self.session_stop.is_set():
                frame_started_at = time.monotonic()
                sent_frame = self._send_frame()
                continuous_capture_waited = self.continuous_capture.is_running() and (
                    self.continuous_capture.has_fresh_frame()
                    or (
                        not self.continuous_frame_ready
                        and time.monotonic() < self.capture_warmup_until
                    )
                )
                wait_seconds = calculate_frame_loop_wait_seconds(
                    continuous_capture_waited,
                    sent_frame,
                    self._frame_interval_seconds(),
                    time.monotonic() - frame_started_at,
                )
                if wait_seconds > 0:
                    self.session_stop.wait(timeout=wait_seconds)
        finally:
            self.session_stop.set()
            self._cancel_return_operations("远端文件回传已取消：连接已结束。")
            try:
                self.sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                self.sock.close()
            except OSError:
                pass
            reader_thread = self.reader_thread
            if reader_thread is not None and reader_thread.is_alive() and reader_thread is not threading.current_thread():
                reader_thread.join(timeout=1.0)
            inbound_liveness_thread = self.inbound_liveness_thread
            if (
                inbound_liveness_thread is not None
                and inbound_liveness_thread.is_alive()
                and inbound_liveness_thread is not threading.current_thread()
            ):
                inbound_liveness_thread.join(timeout=1.0)
            self._stop_return_worker()
            self.heartbeat.close()
            self._stop_clipboard_worker()
            self._stop_input_thread()
            self._stop_display_size_refresh_thread()
            self.hardware_h264_capture.close()
            self.continuous_capture.close()
            self._abort_incoming_transfer()

    def _frame_interval_seconds(self) -> float:
        with self.video_lock:
            h264_stream_active = self.h264_stream_active
        active_fps = (
            self.hardware_h264_capture.fps
            if h264_stream_active
            else self.continuous_capture.fps
        )
        return 1.0 / max(1.0, active_fps)

    def _read_loop(self) -> None:
        try:
            while not self.stop_event.is_set() and not self.session_stop.is_set():
                read_generation = self.inbound_liveness.begin_read()
                try:
                    try:
                        message_type, payload = read_message(self.sock, self.session)
                    finally:
                        self.inbound_liveness.end_read(read_generation)
                    rearm_tcp_quickack(self.sock)
                except EOFError:
                    break
                except OSError:
                    break
                except Exception as ex:
                    self._abort_incoming_transfer()
                    self._safe_status(False, f"Linux host protocol error: {ex}")
                    break

                try:
                    self._handle_message(message_type, payload)
                except Exception as ex:
                    self._abort_incoming_transfer()
                    self._safe_status(False, f"Linux host control error: {ex}")
                    break
        finally:
            self.session_stop.set()

    def _inbound_liveness_loop(self) -> None:
        while not self.stop_event.is_set() and not self.session_stop.is_set():
            wait_seconds = self.inbound_liveness.next_check_delay(
                HOST_SESSION_WATCHDOG_POLL_SECONDS
            )
            if self.session_stop.wait(timeout=wait_seconds):
                return
            if not self.inbound_liveness.expire_if_timed_out():
                continue

            log(
                "viewer sent no complete authenticated TCP message for "
                f"{self.inbound_liveness.timeout_seconds:g} seconds; "
                "closing stale Linux host session"
            )
            self.session_stop.set()
            self._interrupt_socket_io()
            return

    def _interrupt_socket_io(self) -> None:
        # Do not take write_lock here. A blocked frame/file write may own it;
        # shutdown must remain able to wake both that writer and the inbound
        # read which triggered the application-level deadline.
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            self.sock.close()
        except OSError:
            pass

    def _heartbeat_failed(self):
        self.session_stop.set()
        self._interrupt_socket_io()

    def _handle_message(self, message_type: int, payload: bytes) -> None:
        if message_type == MESSAGE_PING:
            self.heartbeat.request()
            return

        if message_type == MESSAGE_INPUT:
            self._handle_input(payload)
            return

        if message_type != MESSAGE_CONTROL:
            return

        control = decode_control(payload)
        kind = int(control["kind"])
        if kind == CONTROL_DEVICE_IDENTITY_REQUEST and getattr(self.args, "device_id", ""):
            self._write_control(encode_device_identity(self.args.device_id))
        elif kind == CONTROL_FILE_RECEIVE_LOCATION_REQUEST:
            try:
                directory = str(self.receive_dir.resolve())
                note = "重名文件自动改名，不覆盖已有文件；完成回执显示实际保存位置。"
                success = True
            except (OSError, RuntimeError):
                directory, note, success = "", "无法读取接收目录，请检查被控端存储设置。", False
            self._write_control(encode_file_receive_location(control["transferId"], success, directory, note))
        elif kind == CONTROL_VIEWER_CAPABILITIES:
            self.viewer_capabilities = int(control.get("capabilities") or 0)
            negotiated_fps = negotiated_h264_fps(
                self.requested_fps,
                self.viewer_capabilities,
            )
            if self.hardware_h264_capture.update_fps(negotiated_fps):
                log(
                    "viewer H.264 cadence negotiation updated: "
                    f"requested={self.requested_fps:g}fps; "
                    f"negotiated={negotiated_fps:g}fps"
                )
        elif kind == CONTROL_VIEWER_INFO:
            self._handle_viewer_info(int(control.get("videoCodecs") or 0))
        elif kind == CONTROL_VIDEO_KEY_FRAME_REQUEST:
            self.hardware_h264_capture.request_recovery_frame()
        elif kind == CONTROL_SELECT_CAPTURE_TARGET:
            self._send_capture_targets()
            self._send_capture_target_changed()
        elif kind == CONTROL_CAPTURE_TARGET_CHANGED:
            return
        elif kind == CONTROL_CLIPBOARD_GET_TEXT:
            self._handle_clipboard_get()
        elif kind == CONTROL_CLIPBOARD_SET_TEXT:
            self._handle_clipboard_set(str(control.get("text") or ""))
        elif kind == CONTROL_CLIPBOARD_SNAPSHOT_REQUEST:
            self._handle_clipboard_snapshot(control["requestId"], control["knownRevision"])
        elif kind == CONTROL_FILE_TRANSFER_START:
            self._handle_file_transfer_start(payload)
        elif kind == CONTROL_FILE_TRANSFER_CHUNK:
            self._handle_file_transfer_chunk(payload)
        elif kind == CONTROL_FILE_TRANSFER_CHECKSUM:
            self._handle_file_transfer_checksum(payload)
        elif kind == CONTROL_FILE_TRANSFER_CANCEL:
            self._handle_file_transfer_cancel(payload)
        elif kind == CONTROL_FILE_TRANSFER_COMPLETE:
            self._handle_file_transfer_complete(payload)
        elif kind == CONTROL_FILE_TRANSFER_REQUEST_CLIPBOARD_FILES:
            self._send_requested_files()
        elif kind == CONTROL_FILE_TRANSFER_CONFIRM_CLIPBOARD_FILES:
            self._send_pending_requested_files()
        elif kind == CONTROL_FILE_TRANSFER_REJECT_CLIPBOARD_FILES:
            if not self._cancel_return_operations("远端文件回传已取消。"):
                log("Linux has no pending return files to cancel.")
        elif kind in (CONTROL_FILE_TRANSFER_STATUS, CONTROL_FILE_TRANSFER_RECEIPT):
            status_message = str(control.get("statusMessage") or "")
            log(f"viewer status: {status_message}")
            if not bool(control.get("success")):
                self._cancel_return_operations(
                    f"远端文件回传已取消：查看端报告接收失败：{status_message}"
                )

    def _send_capture_targets(self) -> None:
        self._write_control(encode_capture_target_list([(CAPTURE_TARGET_ID, CAPTURE_TARGET_NAME)]))

    def _send_capture_target_changed(self) -> None:
        self._write_control(encode_capture_target_changed(CAPTURE_TARGET_ID, CAPTURE_TARGET_NAME))

    def _handle_viewer_info(self, codecs: int) -> None:
        normalized = codecs & (VIDEO_CODEC_JPEG | VIDEO_CODEC_H264_ANNEX_B)
        with self.video_lock:
            previous = self.viewer_video_codecs
            self.viewer_video_codecs = normalized
            self.viewer_info_received = True
            if not normalized & VIDEO_CODEC_H264_ANNEX_B:
                self.h264_stream_active = False
                self.last_h264_frame_id = None
        if normalized & VIDEO_CODEC_H264_ANNEX_B:
            self.hardware_h264_capture.request_start()
        else:
            self.hardware_h264_capture.stop()
        if previous != normalized:
            names = []
            if normalized & VIDEO_CODEC_H264_ANNEX_B:
                names.append("H.264 Annex-B")
            if normalized & VIDEO_CODEC_JPEG:
                names.append("JPEG")
            log(f"viewer video codecs: {', '.join(names) if names else 'none'}")

    def _send_frame(self) -> bool:
        started = time.monotonic()
        with self.video_lock:
            viewer_codecs = self.viewer_video_codecs
            viewer_info_received = self.viewer_info_received
            h264_stream_active = self.h264_stream_active

        # A relay's loopback socket can accept many frames before the distant
        # viewer's codec selection arrives. One full-quality preview is enough:
        # a startup JPEG burst otherwise queues ahead of the first H.264 frame
        # for tens of seconds on a congested uplink. Legacy clients which never
        # send ViewerInfo resume normal JPEG after this bounded grace period.
        if (not viewer_info_received and self.startup_jpeg_sent
                and started < self.codec_negotiation_deadline):
            return False

        if viewer_codecs & VIDEO_CODEC_H264_ANNEX_B:
            h264_frame = self.hardware_h264_capture.read_frame(
                timeout=min(0.02, 1.0 / self.hardware_h264_capture.fps),
                after_frame_id=self.last_h264_frame_id,
            )
            if h264_frame is not None:
                self.last_h264_frame_id = h264_frame.frame_id
                with self.video_lock:
                    self.h264_stream_active = True
                if self.continuous_capture.is_running():
                    self.continuous_capture.pause()
                    self.continuous_frame_ready = False
                    self.last_continuous_frame_id = None
                with self.geometry_lock:
                    self.last_frame_width = h264_frame.width
                    self.last_frame_height = h264_frame.height
                payload = encode_video_frame(
                    h264_frame.width,
                    h264_frame.height,
                    h264_frame.flags,
                    h264_frame.encoded,
                    capture_ms=0.0,
                    encode_ms=0.0,
                )
                write_seconds = self._write_message(MESSAGE_VIDEO_FRAME, payload)
                adaptive = getattr(self, "h264_resolution", None)
                if adaptive is not None and isinstance(write_seconds, (int, float)):
                    if adaptive.record_frame(len(h264_frame.encoded), write_seconds,
                                             self.hardware_h264_capture.fps):
                        width, height = adaptive.output_size
                        self.hardware_h264_capture.update_output_size(width, height)
                        log(f"H.264 bandwidth profile: {width}x{height}; hardware encoding, "
                            "display mode unchanged; one recovery trial per session")
                return True

            if self.hardware_h264_capture.is_running() or self.hardware_h264_capture.is_starting():
                # Do not interleave JPEG after the first H.264 AU. While the
                # initial hardware probe runs, JPEG may continue only when the
                # viewer explicitly advertised it.
                if h264_stream_active or not viewer_codecs & VIDEO_CODEC_JPEG or self.startup_jpeg_sent:
                    return False
            else:
                with self.video_lock:
                    self.h264_stream_active = False
                h264_stream_active = False

        if viewer_info_received and not viewer_codecs & VIDEO_CODEC_JPEG:
            if not viewer_codecs & VIDEO_CODEC_H264_ANNEX_B:
                message = (
                    "Linux host and viewer have no mutually supported video codec."
                )
                self._safe_status(False, message)
                self.session_stop.set()
                raise ProtocolError(message)
            if self.hardware_h264_capture.has_exhausted_candidates():
                message = (
                    "Linux host could not start any hardware H.264 encoder, "
                    "and the viewer did not advertise JPEG fallback."
                )
                self._safe_status(False, message)
                self.session_stop.set()
                raise ProtocolError(message)
            return False

        if self.continuous_frame_ready:
            wait_timeout = min(0.02, 1.0 / self.continuous_capture.fps)
        else:
            wait_timeout = min(0.18, 1.0 / self.continuous_capture.fps)
        frame = self.continuous_capture.read_frame(timeout=wait_timeout, after_frame_id=self.last_continuous_frame_id)
        if frame is not None:
            self.continuous_frame_ready = True
            self.last_continuous_frame_id = frame[3]
        if frame is None:
            if self.continuous_capture.is_running() and (
                (not self.continuous_frame_ready and time.monotonic() < self.capture_warmup_until)
                or self.continuous_capture.has_fresh_frame()
            ):
                return False
            frame = capture_frame(self.frame_width, self.frame_height, self.args.capture)
        frame_width, frame_height, frame_bytes = frame[:3]
        if len(frame_bytes) > MAX_JPEG_FRAME_BYTES:
            message = (
                "Linux JPEG frame is too large for the RemoteDesk protocol: "
                f"{len(frame_bytes)} encoded bytes exceeds "
                f"{MAX_JPEG_FRAME_BYTES}."
            )
            self._safe_status(False, message)
            self.session_stop.set()
            raise ProtocolError(message)
        with self.geometry_lock:
            self.last_frame_width, self.last_frame_height = frame_width, frame_height
        capture_ms = (time.monotonic() - started) * 1000
        payload = (
            struct.pack("<ii", frame_width, frame_height)
            + struct.pack("<dd", capture_ms, 0.0)
            + frame_bytes
        )
        self._write_message(MESSAGE_FRAME, payload)
        self.startup_jpeg_sent = True
        return True

    def _handle_input(self, payload: bytes) -> None:
        if not (self.host_capabilities & CAPABILITY_INPUT_CONTROL):
            if not self.input_unavailable_reported:
                self.input_unavailable_reported = True
                self._safe_status(False, "Linux input injection requires XTest or xdotool with DISPLAY.")
            return

        command = decode_input_payload(payload)
        with self.input_condition:
            queued = enqueue_input_command(self.input_queue, command, INPUT_QUEUE_LIMIT)
            if queued:
                self.input_condition.notify()
                return

        if not self.input_unavailable_reported:
            self.input_unavailable_reported = True
            self._safe_status(False, "Linux input queue is full; dropping input until it catches up.")

    def _input_loop(self) -> None:
        while True:
            with self.input_condition:
                while not self.input_queue and not self.input_stop:
                    self.input_condition.wait(timeout=0.25)
                if self.input_stop and not self.input_queue:
                    break
                command = pop_next_input_command(self.input_queue)
            if command is None:
                continue

            try:
                frame_size, display_size = self._current_input_geometry()
                if self.input_controller.apply(
                    command,
                    frame_size,
                    display_size,
                ):
                    self.input_state.observe(command)
            except Exception as ex:
                if not self.input_unavailable_reported:
                    self.input_unavailable_reported = True
                    self._safe_status(False, f"Linux input injection failed: {ex}")

    def _current_input_geometry(self) -> tuple[tuple[int, int], tuple[int, int]]:
        with self.geometry_lock:
            frame_size = (self.last_frame_width, self.last_frame_height)
            display_size = self.display_size

        return frame_size, display_size

    def _display_size_refresh_loop(self) -> None:
        while not self.display_refresh_stop.is_set():
            try:
                detected_size = probe_display_size()
            except Exception:
                detected_size = None
            with self.geometry_lock:
                previous_size = self.display_size
                refreshed_size = select_refreshed_display_size(previous_size, detected_size)
                self.display_size = refreshed_size

            self.continuous_capture.update_source_size(*refreshed_size)
            self.hardware_h264_capture.update_source_size(*refreshed_size)
            if refreshed_size != previous_size:
                log(
                    "X11 display size refreshed: "
                    f"{previous_size[0]}x{previous_size[1]} -> "
                    f"{refreshed_size[0]}x{refreshed_size[1]}"
                )

            if self.display_refresh_stop.wait(DISPLAY_SIZE_REFRESH_SECONDS):
                break

    def _start_display_size_refresh_thread(self) -> None:
        thread = self.display_refresh_thread
        if thread is not None and thread.is_alive():
            return
        self.display_refresh_stop.clear()
        self.display_refresh_thread = threading.Thread(
            target=self._display_size_refresh_loop,
            name="RemoteDeskLinuxDisplaySize",
            daemon=True,
        )
        self.display_refresh_thread.start()

    def _stop_display_size_refresh_thread(self) -> None:
        self.display_refresh_stop.set()
        thread = self.display_refresh_thread
        if thread is not None and thread.is_alive() and thread is not threading.current_thread():
            thread.join(timeout=DISPLAY_PROBE_TIMEOUT_SECONDS + 0.5)
        self.display_refresh_thread = None

    def _stop_input_thread(self) -> None:
        with self.input_condition:
            self.input_stop = True
            self.input_queue.clear()
            self.input_condition.notify_all()
        thread = self.input_thread
        if thread is not None and thread.is_alive() and thread is not threading.current_thread():
            thread.join(timeout=2.0)
        if thread is not None and thread.is_alive():
            log(
                "Linux input worker did not stop before held-input cleanup; "
                "skipping concurrent release."
            )
        else:
            frame_size, display_size = self._current_input_geometry()
            released = 0
            for command in self.input_state.take_release_commands():
                try:
                    if self.input_controller.apply(
                        command,
                        frame_size,
                        display_size,
                    ):
                        released += 1
                except Exception as ex:
                    log(f"Linux held-input release failed: {ex}")
            if released:
                log(
                    "Linux viewer session ended; released "
                    f"{released} held key/mouse state(s)."
                )
        self.input_controller.close()

    def _handle_clipboard_get(self) -> None:
        if not self._queue_clipboard_operation(
            ClipboardOperation(CONTROL_CLIPBOARD_GET_TEXT)
        ):
            log("Linux clipboard worker queue is full; ignored GetText request.")

    def _handle_clipboard_set(self, text: str) -> None:
        if not self._queue_clipboard_operation(
            ClipboardOperation(CONTROL_CLIPBOARD_SET_TEXT, text)
        ):
            log("Linux clipboard worker queue is full; ignored SetText request.")

    def _handle_clipboard_snapshot(self, request_id: str, known_revision: str) -> None:
        # Snapshot responses have their own correlated wire kind. Never send
        # them to an older viewer or consume a legacy Get/Set reply slot.
        if not self._clipboard_snapshot_negotiated():
            return
        if not self._queue_clipboard_operation(ClipboardOperation(
                CONTROL_CLIPBOARD_SNAPSHOT_REQUEST, request_id=request_id, known_revision=known_revision)):
            self._write_clipboard_snapshot_failure(request_id, "Linux 剪贴板忙，请稍后重试。")

    def _clipboard_snapshot_negotiated(self) -> bool:
        return bool(getattr(self, "viewer_capabilities", 0) & CAPABILITY_CLIPBOARD_SNAPSHOT_V1)

    def _write_clipboard_snapshot_failure(self, request_id: str, message: str) -> None:
        if self._clipboard_session_is_active() and self._clipboard_snapshot_negotiated():
            self._write_control(encode_clipboard_snapshot(request_id, False, "", False, False, "", message))

    def _queue_clipboard_operation(
        self,
        operation: ClipboardOperation,
    ) -> bool:
        self._start_clipboard_worker()
        with self.clipboard_condition:
            if (
                self.clipboard_worker_stop
                or self.session_stop.is_set()
                or self.stop_event.is_set()
            ):
                return False

            # Legacy Get/Set messages have no request identifier. Every Get
            # and correlated Snapshot must retain its reply and relative
            # order. Only replace a pending Set at the tail, never across a
            # read, so Set→Get remains observable during rapid editor updates.
            if self.clipboard_queue:
                tail = self.clipboard_queue[-1]
                if (
                    tail.kind == CONTROL_CLIPBOARD_SET_TEXT
                    and operation.kind == CONTROL_CLIPBOARD_SET_TEXT
                ):
                    self.clipboard_queue[-1] = operation
                    return True
            if len(self.clipboard_queue) >= CLIPBOARD_OPERATION_QUEUE_LIMIT:
                return False
            self.clipboard_queue.append(operation)
            self.clipboard_condition.notify()
            return True

    def _start_clipboard_worker(self) -> None:
        with self.clipboard_condition:
            thread = self.clipboard_worker_thread
            if thread is not None and thread.is_alive():
                return
            if self.clipboard_worker_stop:
                return
            thread = threading.Thread(
                target=self._clipboard_worker_loop,
                name="RemoteDeskLinuxClipboard",
                daemon=True,
            )
            self.clipboard_worker_thread = thread
            thread.start()

    def _clipboard_worker_loop(self) -> None:
        while True:
            with self.clipboard_condition:
                while (
                    not self.clipboard_queue
                    and not self.clipboard_worker_stop
                ):
                    self.clipboard_condition.wait(timeout=0.25)
                if self.clipboard_worker_stop:
                    return
                operation = self.clipboard_queue.popleft()

            try:
                if not self._clipboard_session_is_active():
                    continue
                if operation.kind == CONTROL_CLIPBOARD_GET_TEXT:
                    text = read_clipboard_text()
                    if not self._clipboard_session_is_active():
                        continue
                    if text is None:
                        self._write_control(
                            encode_clipboard_status(
                                False,
                                "Linux clipboard text is unavailable.",
                            )
                        )
                    else:
                        self._write_control(encode_clipboard_text(text))
                elif operation.kind == CONTROL_CLIPBOARD_SET_TEXT:
                    ok = write_clipboard_text(operation.text)
                    if not self._clipboard_session_is_active():
                        continue
                    self._write_control(
                        encode_clipboard_status(
                            ok,
                            "Linux clipboard updated."
                            if ok
                            else "Linux clipboard text write failed.",
                        )
                    )
                elif operation.kind == CONTROL_CLIPBOARD_SNAPSHOT_REQUEST:
                    if not self._clipboard_snapshot_negotiated():
                        continue
                    if not getattr(self, "host_capabilities", 0) & CAPABILITY_CLIPBOARD_TEXT:
                        self._write_clipboard_snapshot_failure(operation.request_id, "此会话不允许读取剪贴板。")
                        continue
                    text = read_clipboard_snapshot_text(self._clipboard_session_is_active)
                    if not self._clipboard_session_is_active() or not self._clipboard_snapshot_negotiated():
                        continue
                    if not getattr(self, "host_capabilities", 0) & CAPABILITY_CLIPBOARD_TEXT:
                        self._write_clipboard_snapshot_failure(operation.request_id, "此会话不允许读取剪贴板。")
                    elif text is None:
                        self._write_clipboard_snapshot_failure(operation.request_id, "Linux 剪贴板暂不可读取。")
                    else:
                        raw = text.encode("utf-8")
                        if len(raw) > MAX_CLIPBOARD_SNAPSHOT_UTF8_BYTES or len(text.encode("utf-16-le")) // 2 > MAX_CLIPBOARD_TEXT_CHARS:
                            raise ProtocolError("Clipboard snapshot exceeds the text limit.")
                        revision = hashlib.sha256(raw).hexdigest()
                        changed = revision != operation.known_revision
                        self._write_control(encode_clipboard_snapshot(
                            operation.request_id, True, revision, bool(text), changed, text if changed else "", ""))
            except Exception as ex:
                if self._clipboard_session_is_active():
                    if operation.kind == CONTROL_CLIPBOARD_SNAPSHOT_REQUEST:
                        # Exception representations can include clipboard bytes
                        # (for example invalid UTF-8); never log those contents.
                        log(f"Linux clipboard snapshot failed: {type(ex).__name__}")
                        try:
                            self._write_clipboard_snapshot_failure(operation.request_id, "Linux 剪贴板快照读取失败或内容超过上限。")
                        except Exception:
                            pass
                        continue
                    message = f"Linux clipboard operation failed: {ex}"
                    log(message)
                    try:
                        self._write_control(
                            encode_clipboard_status(False, message)
                        )
                    except Exception:
                        pass

    def _clipboard_session_is_active(self) -> bool:
        return (
            not self.clipboard_worker_stop
            and not self.session_stop.is_set()
            and not self.stop_event.is_set()
        )

    def _stop_clipboard_worker(self) -> None:
        with self.clipboard_condition:
            self.clipboard_worker_stop = True
            self.clipboard_queue.clear()
            self.clipboard_condition.notify_all()
            thread = self.clipboard_worker_thread

        if (
            thread is not None
            and thread.is_alive()
            and thread is not threading.current_thread()
        ):
            thread.join(timeout=CLIPBOARD_WORKER_STOP_TIMEOUT_SECONDS)
        if thread is not None and thread.is_alive():
            log(
                "Linux clipboard worker is still leaving a blocked system "
                "clipboard call; session teardown will continue."
            )
        else:
            self.clipboard_worker_thread = None

    def _handle_file_transfer_start(self, payload: bytes) -> None:
        require_checksum = bool(self.viewer_capabilities & CAPABILITY_FILE_CHECKSUM)
        receive_budget = getattr(self, "incoming_receive_budget", None)
        if receive_budget is None:
            receive_budget = IncomingFileTransferBudget()
            self.incoming_receive_budget = receive_budget
        try:
            replacement = start_incoming_file_transfer(
                payload,
                self.receive_dir,
                require_checksum,
                receive_budget,
            )
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            self._safe_file_receipt(payload, False, f"Linux file transfer start failed: {ex}")
            return
        previous = self.incoming
        self.incoming = replacement
        if previous is not None:
            previous.abort()
        self._safe_status(
            True,
            f"Linux receiving file: {self.incoming.file_name} ({self.incoming.file_length} bytes)",
        )

    def _handle_file_transfer_chunk(self, payload: bytes) -> None:
        if self.incoming is None:
            self._safe_file_receipt(payload, False, "Linux file transfer failed: file chunk without active transfer")
            return
        transfer = self.incoming
        try:
            write_incoming_file_chunk(payload, transfer)
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            if not transfer.active:
                self.incoming = None
            self._safe_file_receipt(payload, False, f"Linux file transfer failed: {ex}")

    def _handle_file_transfer_checksum(self, payload: bytes) -> None:
        if self.incoming is None:
            self._safe_file_receipt(payload, False, "Linux file transfer failed: file checksum without active transfer")
            return
        transfer = self.incoming
        try:
            set_expected_file_checksum(payload, transfer)
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            if not transfer.active:
                self.incoming = None
            self._safe_file_receipt(payload, False, f"Linux file transfer failed: {ex}")

    def _handle_file_transfer_cancel(self, payload: bytes) -> None:
        if self.incoming is None:
            self._safe_file_receipt(payload, False, "Linux file transfer failed: file cancellation without active transfer")
            return
        transfer = self.incoming
        try:
            reason = cancel_incoming_file_transfer(payload, transfer)
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            if not transfer.active:
                self.incoming = None
            self._safe_file_receipt(payload, False, f"Linux file transfer failed: {ex}")
            return
        self.incoming = None
        self._safe_file_receipt(payload, False, f"Linux file transfer cancelled: {reason}")

    def _handle_file_transfer_complete(self, payload: bytes) -> None:
        if self.incoming is None:
            self._safe_file_receipt(payload, False, "Linux file transfer failed: file completion without active transfer")
            return
        transfer = self.incoming
        try:
            completed = complete_incoming_file_transfer(payload, transfer)
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            if not transfer.active:
                self.incoming = None
            self._safe_file_receipt(payload, False, f"Linux file transfer failed: {ex}")
            return
        else:
            self.incoming = None
            self._safe_file_receipt(payload, True, f"文件已保存到 Linux： {completed['path']}")

    def _safe_file_receipt(self, payload: bytes, success: bool, message: str) -> None:
        if not getattr(self, "viewer_capabilities", 0) & CAPABILITY_FILE_TRANSFER_RECEIPT:
            self._safe_status(success, message)
            return
        transfer_id = decode_control(payload).get("transferId", "")
        try:
            self._write_control(encode_file_transfer_receipt(transfer_id, success, message))
        except (OSError, ProtocolError):
            pass

    def _send_requested_files(self) -> None:
        self._start_return_worker()
        operation = ReturnOperation(
            tuple(str(item) for item in self.args.return_file),
            self.viewer_capabilities,
        )
        with self.return_condition:
            if self.return_worker_stop or self.session_stop.is_set():
                return

            if self.return_active_operation is not None:
                self.return_active_operation.cancel(
                    "远端文件回传已取消：收到新的回传请求。"
                )
            if self.return_queued_operation is not None:
                self.return_queued_operation.cancel(
                    "远端文件回传已取消：收到新的回传请求。"
                )
            self._clear_pending_return_locked()
            self.return_queued_operation = operation
            self.return_condition.notify_all()

    def _send_pending_requested_files(self) -> None:
        confirmed = False
        with self.return_condition:
            operation = self.return_active_operation
            if (
                operation is not None
                and operation.preview_ready
                and not operation.cancel_event.is_set()
                and self.pending_return_operation is operation
            ):
                operation.confirm_event.set()
                self._clear_pending_return_locked()
                self.return_condition.notify_all()
                confirmed = True

        if not confirmed:
            log("Linux has no pending return files to send.")

    def _start_return_worker(self) -> None:
        with self.return_condition:
            thread = self.return_worker_thread
            if thread is not None and thread.is_alive():
                return
            if self.return_worker_stop:
                return
            thread = threading.Thread(
                target=self._return_worker_loop,
                name="RemoteDeskLinuxReturn",
                daemon=True,
            )
            self.return_worker_thread = thread
            thread.start()

    def _return_worker_loop(self) -> None:
        while True:
            with self.return_condition:
                while self.return_queued_operation is None and not self.return_worker_stop:
                    self.return_condition.wait(timeout=0.25)
                if self.return_worker_stop:
                    break
                operation = self.return_queued_operation
                self.return_queued_operation = None
                self.return_active_operation = operation

            if operation is None:
                continue

            terminal_success, terminal_message = self._run_return_operation(operation)

            with self.return_condition:
                if self.pending_return_operation is operation:
                    self._clear_pending_return_locked()
                report_terminal = (
                    not operation.terminal_reported
                    and not self.return_worker_stop
                    and not self.session_stop.is_set()
                )
                if report_terminal:
                    operation.terminal_reported = True

            if report_terminal:
                self._safe_status(terminal_success, terminal_message)

            # Keep the operation active until its one terminal status has been written. The
            # worker cannot dequeue a newer request before this point, so the old terminal is
            # always ordered before the next batch's preview or transfer start.
            with self.return_condition:
                if self.return_active_operation is operation:
                    self.return_active_operation = None
                self.return_condition.notify_all()

    def _run_return_operation(self, operation: ReturnOperation) -> tuple[bool, str]:
        plan: list[TransferItem] | None = None
        try:
            self._check_return_cancelled(operation)
            candidates = resolve_return_files(
                list(operation.explicit_paths),
                cancel_event=operation.cancel_event,
            )
            self._check_return_cancelled(operation)
            if not candidates:
                return False, format_empty_return_file_status()

            operation.ignored_count = max(0, len(candidates) - MAX_RETURN_FILES)
            plan = create_transfer_items(
                candidates[:MAX_RETURN_FILES],
                cancel_event=operation.cancel_event,
            )
            operation.plan = plan
            self._check_return_cancelled(operation)
            if not plan:
                return False, format_empty_return_file_status(
                    "复制内容中没有可读取的文件或文件夹。"
                )

            if operation.viewer_capabilities & CAPABILITY_FILE_TRANSFER_PREVIEW:
                preview_payload = encode_file_transfer_clipboard_files_preview(
                    build_transfer_preview_items(plan),
                    build_transfer_preview_note(plan, operation.ignored_count),
                )
                self._check_return_cancelled(operation)
                with self.return_condition:
                    self._check_return_cancelled(operation)
                    operation.preview_ready = True
                    self.pending_return_operation = operation
                    self.pending_return_plan = plan
                    self.pending_return_ignored_count = operation.ignored_count

                self._write_control(preview_payload)
                log(f"Linux sent return file preview for {len(plan)} file(s); waiting for viewer confirmation.")
                while not operation.confirm_event.is_set():
                    self._check_return_cancelled(operation)
                    operation.cancel_event.wait(timeout=0.05)
                self._check_return_cancelled(operation)
                with self.return_condition:
                    if self.pending_return_operation is operation:
                        self._clear_pending_return_locked()

            return self._send_transfer_plan_to_viewer(
                plan,
                operation.ignored_count,
                operation,
            )
        except TransferCancelledError:
            return False, operation.cancel_reason
        except (ProtocolError, OSError, EOFError, ValueError) as ex:
            operation.cancel(f"远端文件回传失败：{ex}")
            return False, f"远端文件回传失败：{ex}"
        except Exception as ex:
            operation.cancel(f"远端文件回传失败：{ex}")
            return False, f"远端文件回传失败：{ex}"
        finally:
            operation.plan = None
            operation.preview_ready = False
            if plan is not None:
                cleanup_temporary_transfer_items(plan)
            with self.return_condition:
                if self.pending_return_operation is operation:
                    self._clear_pending_return_locked()

    def _send_transfer_plan_to_viewer(
        self,
        plan: list[TransferItem],
        ignored_count: int = 0,
        operation: ReturnOperation | None = None,
    ) -> tuple[bool, str]:
        self._safe_status(True, f"Linux returning {len(plan)} file(s).")
        sent = 0
        failed = 0
        for index, item in enumerate(plan):
            try:
                if operation is not None:
                    self._check_return_cancelled(operation)
                self._send_transfer_item_to_viewer(item, operation)
                sent += 1
            except TransferCancelledError:
                raise
            except Exception as ex:
                failed = len(plan) - index
                if operation is not None:
                    operation.cancel(
                        f"远端文件回传失败：无法发送 {item.transfer_name}：{ex}"
                    )
                break

        return (
            failed == 0 and sent > 0,
            format_return_file_terminal_status(sent, failed, ignored_count),
        )

    def _send_transfer_item_to_viewer(
        self,
        item: TransferItem,
        operation: ReturnOperation | None = None,
    ) -> None:
        temporary_archive: Path | None = None
        try:
            if operation is not None:
                self._check_return_cancelled(operation)
            source_path = item.path
            if item.source_kind == "文件夹":
                self._safe_status(True, f"Linux packing folder for return: {item.transfer_name}")
                temporary_archive = create_directory_archive(
                    item.path,
                    cancel_event=operation.cancel_event if operation is not None else None,
                )
                source_path = temporary_archive

            if operation is not None:
                self._check_return_cancelled(operation)
            self._send_file_to_viewer(source_path, item.transfer_name, operation)
        finally:
            if temporary_archive is not None:
                try:
                    temporary_archive.unlink(missing_ok=True)
                except OSError:
                    pass

    def _send_file_to_viewer(
        self,
        path: Path,
        transfer_name: str,
        operation: ReturnOperation | None = None,
    ) -> None:
        if operation is not None:
            self._check_return_cancelled(operation)
        if not path.is_file():
            raise FileNotFoundError(str(path))

        source_stat = path.stat()
        file_size = source_stat.st_size
        source_mtime_ns = source_stat.st_mtime_ns
        if file_size > MAX_FILE_TRANSFER_BYTES:
            raise ProtocolError(f"file exceeds RemoteDesk transfer limit: {file_size} bytes")

        transfer_id = uuid4().hex
        transfer_active = False
        viewer_capabilities = (
            operation.viewer_capabilities if operation is not None else self.viewer_capabilities
        )
        send_checksum = bool(viewer_capabilities & CAPABILITY_FILE_CHECKSUM)
        send_cancel = bool(viewer_capabilities & CAPABILITY_FILE_TRANSFER_CANCEL)
        try:
            if operation is not None:
                self._check_return_cancelled(operation)
            self._write_control(encode_file_transfer_start(transfer_id, transfer_name, file_size))
            transfer_active = True
            offset = 0
            digest = hashlib.sha256()
            with path.open("rb") as input_file:
                while offset < file_size:
                    if operation is not None:
                        self._check_return_cancelled(operation)
                    chunk = input_file.read(
                        min(RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES, file_size - offset)
                    )
                    if not chunk:
                        raise EOFError("source file was truncated while being read")
                    if operation is not None:
                        self._check_return_cancelled(operation)
                    digest.update(chunk)
                    self._write_control(encode_file_transfer_chunk(transfer_id, offset, chunk))
                    offset += len(chunk)

            if operation is not None:
                self._check_return_cancelled(operation)
            current_stat = path.stat()
            if current_stat.st_size != file_size or current_stat.st_mtime_ns != source_mtime_ns:
                raise EOFError("source file changed while being read")

            if send_checksum:
                if operation is not None:
                    self._check_return_cancelled(operation)
                self._write_control(encode_file_transfer_checksum(transfer_id, digest.hexdigest()))

            if operation is not None:
                self._check_return_cancelled(operation)
            self._write_control(encode_file_transfer_complete(transfer_id))
            transfer_active = False
        except Exception as ex:
            if transfer_active and send_cancel:
                try:
                    self._write_control(encode_file_transfer_cancel(transfer_id, f"Linux host cancelled: {ex}"))
                    transfer_active = False
                except Exception:
                    pass
            raise

    def _check_return_cancelled(self, operation: ReturnOperation) -> None:
        if self.session_stop.is_set() or self.stop_event.is_set():
            operation.cancel("远端文件回传已取消：连接已结束。")
        if operation.cancel_event.is_set():
            raise TransferCancelledError(operation.cancel_reason)

    def _cancel_return_operations(self, reason: str) -> bool:
        cancelled = False
        with self.return_condition:
            active = self.return_active_operation
            queued = self.return_queued_operation
            if active is not None:
                if not active.cancel_event.is_set():
                    active.cancel(reason)
                    cancelled = True
                if queued is not None:
                    queued.cancel(reason)
                    self.return_queued_operation = None
                    cancelled = True
            elif queued is not None:
                if not queued.cancel_event.is_set():
                    queued.cancel(reason)
                    cancelled = True
            self._clear_pending_return_locked()
            self.return_condition.notify_all()
        return cancelled

    def _clear_pending_return_locked(self) -> None:
        self.pending_return_operation = None
        self.pending_return_plan = None
        self.pending_return_ignored_count = 0

    def _stop_return_worker(self) -> None:
        with self.return_condition:
            self.return_worker_stop = True
            for operation in (self.return_active_operation, self.return_queued_operation):
                if operation is not None:
                    operation.cancel(
                        "远端文件回传已取消：连接已结束。"
                    )
            self._clear_pending_return_locked()
            self.return_condition.notify_all()
            thread = self.return_worker_thread

        if (
            thread is not None
            and thread.is_alive()
            and thread is not threading.current_thread()
        ):
            thread.join(timeout=RETURN_WORKER_STOP_TIMEOUT_SECONDS)

        worker_still_running = thread is not None and thread.is_alive()
        if worker_still_running:
            log(
                "Linux return worker is still leaving a blocked filesystem "
                "or socket call; session teardown will continue."
            )

        with self.return_condition:
            if not worker_still_running:
                self.return_worker_thread = None
                self.return_active_operation = None
                self.return_queued_operation = None

    def _write_control(self, payload: bytes) -> None:
        self._write_message(MESSAGE_CONTROL, payload)

    def _write_message(self, message_type: int, payload: bytes) -> float:
        if self.session_stop.is_set():
            raise ConnectionError("RemoteDesk Linux session is closed")
        priority = getattr(self, "write_priority", None)
        admission = priority.enter(message_type in (MESSAGE_FRAME, MESSAGE_VIDEO_FRAME)) if priority else nullcontext()
        with admission:
            with self.write_lock:
                if self.session_stop.is_set():
                    raise ConnectionError("RemoteDesk Linux session is closed")
                elapsed = write_message(self.sock, self.session, message_type, payload)
                if isinstance(elapsed, (int, float)) and elapsed > 1:
                    log(f"TCP write pressure: type={message_type}; bytes={len(payload)}; socket-write={elapsed:.2f}s (not RTT)")
                return elapsed

    def _safe_status(self, success: bool, message: str) -> None:
        log(message)
        try:
            self._write_control(encode_file_transfer_status(success, message))
        except Exception:
            pass

    def _abort_incoming_transfer(self) -> None:
        if self.incoming is None:
            return

        self.incoming.abort()
        self.incoming = None

    def _cleanup_pending_return_plan(self) -> None:
        self._cancel_return_operations("远端文件回传已取消。")


def log(message: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {message}", flush=True)


def format_capabilities(capabilities: int) -> str:
    names = [name for bit, name in CAPABILITY_NAMES if capabilities & bit]
    return ", ".join(names) if names else "none"


def get_dependency_status() -> list[tuple[str, str, bool]]:
    return [(command, purpose, shutil.which(command) is not None) for command, purpose in DEPENDENCY_COMMANDS]


def configure_display_environment(requested_display: str | None = None) -> tuple[str, str | None]:
    requested_display = (requested_display or "").strip()
    if requested_display:
        os.environ["DISPLAY"] = requested_display
        clear_input_capability_cache()
        return "forced", requested_display

    current = os.environ.get("DISPLAY")
    if current and not shutil.which("xdpyinfo"):
        return "current-unverified", current

    detected = detect_usable_x11_display(current)
    if detected:
        os.environ["DISPLAY"] = detected
        clear_input_capability_cache()
        return ("current" if detected == current else "auto"), detected

    return ("unusable", current) if current else ("missing", None)


def clear_input_capability_cache() -> None:
    global _input_capability_cache
    with _input_capability_probe_lock:
        _input_capability_cache = None


def x11_display_candidates(current_display: str | None = None) -> tuple[str, ...]:
    seen: set[str] = set()
    candidates: list[str] = []
    for candidate in (current_display, *DISPLAY_AUTO_CANDIDATES):
        normalized = (candidate or "").strip()
        if normalized and normalized not in seen:
            seen.add(normalized)
            candidates.append(normalized)
    return tuple(candidates)


def detect_usable_x11_display(current_display: str | None = None) -> str | None:
    if not shutil.which("xdpyinfo"):
        return None

    best: X11DisplayProbe | None = None
    for candidate in x11_display_candidates(current_display):
        probe = probe_x11_display(candidate)
        if not probe.usable:
            continue
        if best is None or probe.rank > best.rank:
            best = probe
        if probe.content_state == "content":
            break
    return None if best is None else best.display


def probe_x11_display(display: str) -> X11DisplayProbe:
    if not display or not shutil.which("xdpyinfo"):
        return X11DisplayProbe(
            display,
            False,
            "unusable",
            "xdpyinfo is missing or the DISPLAY value is empty.",
        )
    try:
        result = subprocess.run(
            ["xdpyinfo", "-display", display],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=DISPLAY_PROBE_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired:
        return X11DisplayProbe(
            display,
            False,
            "unusable",
            f"xdpyinfo timed out after {DISPLAY_PROBE_TIMEOUT_SECONDS:g}s.",
        )
    except Exception as ex:
        return X11DisplayProbe(
            display,
            False,
            "unusable",
            f"xdpyinfo failed: {ex}",
        )

    if result.returncode != 0:
        return X11DisplayProbe(
            display,
            False,
            "unusable",
            f"xdpyinfo exited with status {result.returncode}.",
        )

    content_state, detail = probe_x11_root_content(display)
    return X11DisplayProbe(display, True, content_state, detail)


def is_x11_display_usable(display: str) -> bool:
    return probe_x11_display(display).usable


def probe_x11_root_content(display: str) -> tuple[str, str]:
    if not shutil.which("import") or not shutil.which("convert"):
        return (
            "unknown",
            "root-content probe skipped because ImageMagick import/convert is missing.",
        )

    width, height = DISPLAY_CONTENT_PROBE_SIZE
    try:
        capture = subprocess.run(
            ["import", "-display", display, "-window", "root", "png:-"],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=DISPLAY_CONTENT_PROBE_TIMEOUT_SECONDS,
        )
        if capture.returncode != 0 or not capture.stdout:
            return (
                "unknown",
                f"root screenshot exited with status {capture.returncode}.",
            )

        reduced = subprocess.run(
            [
                "convert",
                "png:-",
                "-resize",
                f"{width}x{height}!",
                "-colorspace",
                "Gray",
                "-depth",
                "8",
                "gray:-",
            ],
            input=capture.stdout,
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=DISPLAY_CONTENT_PROBE_TIMEOUT_SECONDS,
        )
        if reduced.returncode != 0 or not reduced.stdout:
            return (
                "unknown",
                f"root screenshot analysis exited with status {reduced.returncode}.",
            )
    except subprocess.TimeoutExpired:
        return (
            "unknown",
            f"root-content probe timed out after {DISPLAY_CONTENT_PROBE_TIMEOUT_SECONDS:g}s.",
        )
    except Exception as ex:
        return "unknown", f"root-content probe failed: {ex}"

    expected = width * height
    pixels = reduced.stdout[:expected]
    if len(pixels) < expected:
        return "unknown", "root-content probe returned an incomplete grayscale image."

    mean_luma = sum(pixels) / len(pixels)
    near_black = sum(
        value <= DISPLAY_NEAR_BLACK_LUMA
        for value in pixels
    ) / len(pixels)
    state = (
        "near-black"
        if near_black >= DISPLAY_NEAR_BLACK_FRACTION
        else "content"
    )
    return (
        state,
        f"root sample mean-luma={mean_luma:.1f}; "
        f"near-black={near_black:.1%}.",
    )


def describe_selected_display(display: str | None) -> str:
    if not display:
        return "DISPLAY is unset."
    probe = probe_x11_display(display)
    if not probe.usable:
        return f"X11 display probe failed: {probe.detail}"
    if probe.content_state == "near-black":
        return (
            "X11 root screenshot is near-black; this can be a valid dark "
            f"desktop, but verify the active Xorg session. {probe.detail}"
        )
    if probe.content_state == "content":
        return f"X11 root screenshot contains visible content; {probe.detail}"
    return f"X11 display is reachable; {probe.detail}"


def log_startup_summary(args: argparse.Namespace) -> None:
    receive_dir = Path(args.receive_dir).expanduser()
    # This is the one explicit startup health check permitted to move the
    # pointer. Discovery only reads the resulting cache, and authenticated
    # sessions reuse it while it remains inside the cooldown window.
    input_available, input_backend_or_reason = (
        probe_input_control_status()
    )
    capabilities = get_host_capabilities(input_available)
    input_state = "enabled" if capabilities & CAPABILITY_INPUT_CONTROL else "disabled"
    display_state = getattr(args, "display_state", "current")
    display_value = os.environ.get("DISPLAY") or "<unset>"
    log(f"RemoteDesk Linux host package startup: machine={args.machine_name} platform={PLATFORM_LINUX}")
    log(f"TCP listen={args.host}:{args.port}; UDP discovery={'disabled' if args.no_discovery else f'{args.discovery_host}:{args.discovery_port}'}")
    jpeg_fps = calculate_jpeg_fallback_fps(args.width, args.height, args.fps)
    log(
        f"receive_dir={receive_dir}; return_files={len(args.return_file)}; "
        f"capture={args.capture}; requested-fps={args.fps:g}; "
        f"h264-bitrate-cap={args.max_video_bitrate_mbps:g}Mbps; "
        f"jpeg-fallback-fps={jpeg_fps:g}; size={args.width}x{args.height}"
    )
    if args.fps > jpeg_fps:
        log(
            f"{args.fps:g}fps is enabled for hardware H.264; "
            f"JPEG/software fallback is capped at {jpeg_fps:g}fps."
        )
    log(f"DISPLAY={display_value} ({display_state})")
    if args.capture != "placeholder":
        log(f"DISPLAY diagnostic: {describe_selected_display(os.environ.get('DISPLAY'))}")
    log(f"capabilities={format_capabilities(capabilities)}; RemoteStart=disabled; InputControl={input_state} ({input_backend_or_reason})")
    if os.environ.get("XDG_SESSION_TYPE", "").casefold() == "wayland":
        log("Wayland session detected; X11 capture/input may be limited to XWayland windows. Ubuntu on Xorg is recommended for full desktop control.")
    if args.capture != "placeholder" and not os.environ.get("DISPLAY"):
        log("DISPLAY is not set; X11 capture and clipboard tools may be unavailable. Placeholder frames will keep the viewer connection flow working.")

    for command, purpose, available in get_dependency_status():
        state = "ok" if available else "missing"
        log(f"dependency {command}: {state} ({purpose})")


def probe_display_size() -> tuple[int, int] | None:
    if shutil.which("xdpyinfo") and os.environ.get("DISPLAY"):
        try:
            result = subprocess.run(
                ["xdpyinfo"],
                check=False,
                capture_output=True,
                text=True,
                timeout=DISPLAY_PROBE_TIMEOUT_SECONDS,
            )
            for line in result.stdout.splitlines():
                marker = "dimensions:"
                if marker not in line:
                    continue
                text = line.split(marker, 1)[1].strip().split()[0]
                width_text, height_text = text.lower().split("x", 1)
                width = int(width_text)
                height = int(height_text)
                if is_plausible_display_size(width, height):
                    return width, height
        except Exception:
            pass
    return None


def select_refreshed_display_size(
    current_size: tuple[int, int],
    detected_size: tuple[int, int] | None,
) -> tuple[int, int]:
    if detected_size is None or not is_plausible_display_size(*detected_size):
        return current_size
    return detected_size


def detect_display_size(default_width: int, default_height: int) -> tuple[int, int]:
    return select_refreshed_display_size(
        (default_width, default_height),
        probe_display_size(),
    )


def is_plausible_display_size(width: int, height: int) -> bool:
    if width <= 0 or height <= 0:
        return False
    if width > MAX_DETECTED_DISPLAY_WIDTH or height > MAX_DETECTED_DISPLAY_HEIGHT:
        return False
    aspect_ratio = max(width / height, height / width)
    return aspect_ratio <= MAX_DETECTED_DISPLAY_ASPECT_RATIO


def image_size_from_bytes(data: bytes) -> tuple[int, int] | None:
    if data.startswith(b"\x89PNG\r\n\x1a\n") and len(data) >= 24:
        width, height = struct.unpack(">II", data[16:24])
        if width > 0 and height > 0:
            return width, height
    if data.startswith(b"\xff\xd8"):
        offset = 2
        while offset + 9 < len(data):
            if data[offset] != 0xFF:
                offset += 1
                continue
            marker = data[offset + 1]
            offset += 2
            if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
                continue
            if offset + 2 > len(data):
                return None
            segment_length = int.from_bytes(data[offset : offset + 2], "big")
            if segment_length < 2 or offset + segment_length > len(data):
                return None
            if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
                if segment_length >= 7:
                    height = int.from_bytes(data[offset + 3 : offset + 5], "big")
                    width = int.from_bytes(data[offset + 5 : offset + 7], "big")
                    if width > 0 and height > 0:
                        return width, height
                return None
            offset += segment_length
    return None


def capture_frame(width: int, height: int, mode: str) -> tuple[int, int, bytes]:
    if mode != "placeholder" and shutil.which("import") and os.environ.get("DISPLAY"):
        try:
            result = subprocess.run(
                ["import", "-window", "root", "-resize", f"{width}x{height}>", "-quality", "88", "jpg:-"],
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                timeout=3,
            )
            if result.returncode == 0 and result.stdout:
                frame_size = image_size_from_bytes(result.stdout) or (width, height)
                return frame_size[0], frame_size[1], result.stdout
        except Exception:
            pass

    if shutil.which("convert"):
        try:
            result = subprocess.run(
                ["convert", "-size", f"{width}x{height}", "xc:#101820", "png:-"],
                check=False,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                timeout=3,
            )
            if result.returncode == 0 and result.stdout:
                frame_size = image_size_from_bytes(result.stdout) or (width, height)
                return frame_size[0], frame_size[1], result.stdout
        except Exception:
            pass

    frame_size = image_size_from_bytes(FALLBACK_PNG) or (width, height)
    return frame_size[0], frame_size[1], FALLBACK_PNG


def _read_clipboard_command_bounded(command: list[str], deadline: float,
                                    is_active: Callable[[], bool], limit: int) -> bytes | None:
    """Read a test/clipboard helper without unbounded communicate() buffering."""
    process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.DEVNULL, bufsize=0)
    selector = selectors.DefaultSelector()
    data = bytearray()
    output_complete = False
    try:
        assert process.stdout is not None
        fd = process.stdout.fileno()
        os.set_blocking(fd, False)
        selector.register(fd, selectors.EVENT_READ)
        while True:
            if not is_active():
                raise TransferCancelledError("Clipboard snapshot request was cancelled.")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Clipboard snapshot helper timed out.")
            if output_complete:
                try:
                    code = process.wait(timeout=min(.05, remaining))
                    return bytes(data) if code == 0 else None
                except subprocess.TimeoutExpired:
                    continue
            for _key, _event in selector.select(min(.05, remaining)):
                chunk = os.read(fd, min(64 * 1024, limit + 1 - len(data)))
                if not chunk:
                    selector.unregister(fd)
                    output_complete = True
                    break
                data.extend(chunk)
                if len(data) > limit:
                    raise ProtocolError("Clipboard snapshot helper output exceeds the limit.")
    finally:
        selector.close()
        if process.stdout is not None:
            process.stdout.close()
        if process.poll() is None:
            try:
                process.kill()
            except OSError:
                pass
        try:
            process.wait(timeout=.5)
        except subprocess.TimeoutExpired:
            pass


def read_clipboard_snapshot_text(is_active: Callable[[], bool] | None = None) -> str | None:
    """Bounded, read-only snapshot; None means unavailable, never empty success.

    Only a successfully enumerated non-text selection or an actual empty text
    result returns ''. Missing helpers, access failures and timeouts cannot be
    mistaken for a user clearing their clipboard.
    """
    active = is_active or (lambda: True)
    deadline = time.monotonic() + 2.0
    backends: list[tuple[list[str] | None, list[str]]] = []
    if os.environ.get("WAYLAND_DISPLAY") and shutil.which("wl-paste"):
        backends.extend((
            (["wl-paste", "--list-types"], ["wl-paste", "--no-newline", "--type", "text/plain;charset=utf-8"]),
            (None, ["wl-paste", "--no-newline", "--type", "text/plain"]),
        ))
    if shutil.which("xclip"):
        backends.append((["xclip", "-selection", "clipboard", "-o", "-t", "TARGETS"],
                         ["xclip", "-selection", "clipboard", "-o"]))
    if shutil.which("xsel"):
        backends.append((None, ["xsel", "-ob"]))
    for targets_command, read_command in backends:
        if targets_command is not None:
            targets = _read_clipboard_command_bounded(targets_command, deadline, active, 16 * 1024)
            if targets is not None:
                formats = targets.decode("utf-8", errors="strict").casefold().splitlines()
                text_available = any(value in ("utf8_string", "string", "text", "compound_text")
                                     or value.startswith("text/plain") for value in formats)
                if not text_available:
                    return ""
        raw = _read_clipboard_command_bounded(read_command, deadline, active, MAX_CLIPBOARD_SNAPSHOT_UTF8_BYTES)
        if raw is None:
            continue
        text = raw.decode("utf-8", errors="strict")
        if len(text.encode("utf-16-le")) // 2 > MAX_CLIPBOARD_TEXT_CHARS:
            raise ProtocolError("Clipboard snapshot text exceeds the character limit.")
        return text
    return None


def read_clipboard_text() -> str | None:
    commands: list[list[str]] = []
    if os.environ.get("WAYLAND_DISPLAY"):
        commands.append(["wl-paste", "--no-newline", "--type", "text/plain;charset=utf-8"])
        commands.append(["wl-paste", "--no-newline", "--type", "text/plain"])
    commands.extend((["xclip", "-selection", "clipboard", "-o"], ["xsel", "-ob"]))
    for command in commands:
        if not shutil.which(command[0]):
            continue
        try:
            result = subprocess.run(command, check=False, capture_output=True, timeout=2)
            if result.returncode == 0:
                return result.stdout.decode("utf-8")
        except Exception:
            continue
    return None


def write_clipboard_text(text: str) -> bool:
    commands = []
    if os.environ.get("WAYLAND_DISPLAY"):
        commands.append(["wl-copy", "--type", "text/plain;charset=utf-8"])
    commands.extend((["xclip", "-selection", "clipboard", "-in", "-target", "UTF8_STRING"], ["xsel", "-ib"]))
    for command in commands:
        if not shutil.which(command[0]):
            continue
        try:
            result = subprocess.run(command, input=text.encode("utf-8"), check=False, timeout=2)
            if result.returncode == 0:
                return True
        except Exception:
            continue
    return False


def read_clipboard_file_paths(
    cancel_event: threading.Event | None = None,
) -> list[Path]:
    paths: list[Path] = []
    for target in ("x-special/gnome-copied-files", "text/uri-list"):
        if cancel_event is not None and cancel_event.is_set():
            raise TransferCancelledError("file transfer operation was cancelled")
        target_text = read_clipboard_target(target)
        if target_text:
            paths.extend(parse_clipboard_file_paths(target_text))
        if paths:
            break

    if not paths:
        if cancel_event is not None and cancel_event.is_set():
            raise TransferCancelledError("file transfer operation was cancelled")
        text = read_clipboard_text()
        if text:
            paths.extend(parse_clipboard_file_paths(text))
            for line in text.splitlines():
                candidate_text = line.strip()
                if not candidate_text:
                    continue
                try:
                    candidate = Path(candidate_text).expanduser()
                    if candidate.exists():
                        paths.append(candidate)
                except (OSError, ValueError):
                    # Plain-text clipboard contents are often arbitrary prose.
                    # Treat an invalid or overlong line as non-file text rather
                    # than allowing a filesystem probe to abort the whole return.
                    continue

    seen: set[str] = set()
    unique: list[Path] = []
    for path in paths:
        if cancel_event is not None and cancel_event.is_set():
            raise TransferCancelledError("file transfer operation was cancelled")
        try:
            exists = path.exists()
            key = str(path.resolve()) if exists else str(path)
        except (OSError, ValueError):
            # URI lists can contain stale, malformed, or platform-incompatible
            # paths. Ignore those entries and retain other valid clipboard files.
            continue
        if key in seen:
            continue
        seen.add(key)
        unique.append(path)
    return unique


def parse_clipboard_file_paths(text: str) -> list[Path]:
    paths: list[Path] = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or line.casefold() in {"copy", "cut"}:
            continue
        parsed = urlparse(line)
        if parsed.scheme.casefold() != "file":
            continue
        if parsed.netloc and parsed.netloc.casefold() not in {"", "localhost"}:
            continue
        paths.append(Path(unquote(parsed.path)))
    return paths


def read_clipboard_target(target: str) -> str | None:
    commands: list[list[str]] = []
    if os.environ.get("WAYLAND_DISPLAY"):
        commands.append(["wl-paste", "--no-newline", "--type", target])
    commands.append(["xclip", "-selection", "clipboard", "-t", target, "-o"])
    for command in commands:
        if not shutil.which(command[0]):
            continue
        try:
            result = subprocess.run(
                command,
                check=False,
                capture_output=True,
                text=True,
                timeout=2,
            )
            if result.returncode == 0:
                return result.stdout
        except Exception:
            continue
    return None


def resolve_return_files(
    explicit_paths: list[str],
    cancel_event: threading.Event | None = None,
) -> list[Path]:
    if cancel_event is not None and cancel_event.is_set():
        raise TransferCancelledError("file transfer operation was cancelled")
    paths: list[Path] = []
    for item in explicit_paths:
        try:
            paths.append(Path(item).expanduser())
        except (OSError, ValueError):
            # One malformed explicit argument must not hide other requested
            # files or turn a clipboard fallback into a session failure.
            continue
    paths.extend(read_clipboard_file_paths(cancel_event=cancel_event))
    seen: set[str] = set()
    result: list[Path] = []
    for path in paths:
        if cancel_event is not None and cancel_event.is_set():
            raise TransferCancelledError("file transfer operation was cancelled")
        try:
            if not path.exists():
                continue
            key = str(path.resolve())
        except (OSError, ValueError):
            # A stale or malformed clipboard entry is ignorable; continue with
            # valid explicit and URI-list entries in the same request.
            continue
        if key in seen:
            continue
        seen.add(key)
        result.append(path)
    return result


def create_transfer_items(
    paths: list[Path],
    cancel_event: threading.Event | None = None,
) -> list[TransferItem]:
    items: list[TransferItem] = []
    try:
        for path in paths:
            if cancel_event is not None and cancel_event.is_set():
                raise TransferCancelledError("file transfer operation was cancelled")
            if path.is_file():
                items.append(
                    TransferItem(
                        path,
                        sanitize_file_name(path.name),
                        source_path=path,
                        source_kind="文件",
                        source_size=safe_path_size(path, cancel_event=cancel_event),
                    )
                )
            elif path.is_dir():
                source_size = safe_path_size(path, cancel_event=cancel_event)
                items.append(
                    TransferItem(
                        path,
                        create_directory_archive_name(path),
                        source_path=path,
                        source_kind="文件夹",
                        source_size=source_size,
                    )
                )
        return items
    except Exception:
        cleanup_temporary_transfer_items(items)
        raise


def build_transfer_preview_items(items: list[TransferItem]) -> list[dict[str, Any]]:
    preview_items: list[dict[str, Any]] = []
    for item in items:
        preview_items.append(
            {
                "kind": item.source_kind,
                "sourcePath": str(item.source_path or item.path),
                "transferName": item.transfer_name,
                "sizeBytes": item.source_size,
                "destinationPath": f"控制端接收目录/{item.transfer_name}（如重名会自动改名）",
            }
        )
    return preview_items


def build_transfer_preview_note(items: list[TransferItem], ignored_count: int = 0) -> str:
    notes: list[str] = []
    if any(item.source_kind == "文件夹" for item in items):
        notes.append("文件夹会先打包为 zip 后传输，大小按原始文件夹内容统计。")
    if ignored_count > 0:
        notes.append(f"一次最多回传 {MAX_RETURN_FILES} 项，另有 {ignored_count} 项本次不会传输。")
    return "\n".join(notes)


def format_empty_return_file_status(detail: str | None = None) -> str:
    message = "远端剪贴板没有可回传文件。"
    if detail:
        return f"{message}{detail}"
    return f"{message}请先在 Linux 文件管理器中复制文件或文件夹。"


def format_return_file_terminal_status(sent: int, failed: int, ignored_count: int = 0) -> str:
    parts = [f"远端文件回传完成：{sent} 个" if sent > 0 else "远端文件回传失败"]
    if failed > 0:
        parts.append(f"{failed} 个失败")
    if ignored_count > 0:
        parts.append(f"{ignored_count} 个超出单次 {MAX_RETURN_FILES} 项限制")
    return "，".join(parts)


def safe_path_size(
    path: Path,
    cancel_event: threading.Event | None = None,
) -> int:
    return safe_transfer_path_size(path, cancel_event=cancel_event)


def cleanup_temporary_transfer_items(items: list[TransferItem]) -> None:
    for item in items:
        if not item.temporary:
            continue
        try:
            item.path.unlink(missing_ok=True)
        except OSError:
            pass


def create_directory_archive(
    directory: Path,
    cancel_event: threading.Event | None = None,
) -> Path:
    return create_safe_directory_archive(directory, cancel_event=cancel_event)


def create_directory_archive_name(directory: Path) -> str:
    name = directory.name or "folder"
    if not name.lower().endswith(".zip"):
        name += ".zip"
    return sanitize_file_name(name)


def discovery_loop(args: argparse.Namespace, stop_event: threading.Event) -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
        udp.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        udp.bind((args.discovery_host, args.discovery_port))
        udp.settimeout(0.5)
        log(f"UDP discovery listening on {args.discovery_host}:{args.discovery_port}")
        while not stop_event.is_set():
            try:
                data, address = udp.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                break

            if data != DISCOVERY_REQUEST:
                continue

            response = {
                "Type": DISCOVERY_RESPONSE_TYPE,
                "MachineName": args.machine_name,
                "DeviceId": getattr(args, "device_id", ""),
                "Port": args.port,
                "CaptureTarget": CAPTURE_TARGET_NAME,
                "IsHostRunning": True,
                "CanRemoteStart": False,
                "Platform": PLATFORM_LINUX,
                "Capabilities": get_host_capabilities(),
            }
            udp.sendto(json.dumps(response, ensure_ascii=False).encode("utf-8"), address)


def close_client_socket(client: socket.socket) -> None:
    try:
        client.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    try:
        client.close()
    except OSError:
        pass


def replace_authenticated_client(
    client: socket.socket,
    session: SecureSession,
    write_lock: Any,
    session_stop: threading.Event | None = None,
    session_closed: threading.Event | None = None,
) -> None:
    if session_stop is not None:
        session_stop.set()
    acquired = False
    try:
        acquired = write_lock.acquire(
            timeout=SESSION_REPLACEMENT_WRITE_TIMEOUT_SECONDS
        )
        if acquired:
            client.settimeout(
                SESSION_REPLACEMENT_WRITE_TIMEOUT_SECONDS
            )
            write_message(
                client,
                session,
                MESSAGE_CONTROL,
                encode_session_rejected(SESSION_REPLACED_MESSAGE),
            )
    except Exception:
        pass
    finally:
        if acquired:
            write_lock.release()
        close_client_socket(client)
        if session_closed is not None:
            session_closed.wait(
                timeout=SESSION_REPLACEMENT_DRAIN_TIMEOUT_SECONDS
            )


def run_admitted_client(
    client: socket.socket,
    address: tuple[Any, ...],
    args: argparse.Namespace,
    stop_event: threading.Event,
    client_gate: ClientAdmissionGate,
) -> None:
    active_owner = False
    session_closed = threading.Event()
    try:
        configure_low_latency_socket(client, SOCKET_RECEIVE_BUFFER_BYTES, host_send_buffer_bytes(address[0]))
        client.settimeout(args.auth_timeout)
        authentication_deadline = time.monotonic() + max(0.1, float(args.auth_timeout))
        session = authenticate_server(
            client,
            args.password,
            deadline=authentication_deadline,
        )
        write_lock = threading.Lock()
        session_stop = threading.Event()
        activation, replace_previous = client_gate.activate_latest(
            client,
            lambda: replace_authenticated_client(
                client,
                session,
                write_lock,
                session_stop,
                session_closed,
            ),
        )
        if activation != "activated":
            return
        active_owner = True
        if replace_previous is not None:
            log(
                "new authenticated viewer is replacing the active session: "
                f"{address[0]}:{address[1]}"
            )
            replace_previous()
        LinuxHostSession(
            client,
            session,
            args,
            stop_event,
            write_lock=write_lock,
            session_stop=session_stop,
        ).run()
    except PermissionError as ex:
        if not stop_event.is_set():
            stop_event.wait(AUTHENTICATION_FAILURE_DELAY_SECONDS)
            log(f"client authentication failed: {address[0]}:{address[1]}: {ex}")
    except (TimeoutError, socket.timeout) as ex:
        if not stop_event.is_set():
            log(f"client authentication timed out: {address[0]}:{address[1]}: {ex}")
    except Exception as ex:
        if not stop_event.is_set():
            log(f"client session ended: {ex}")
    finally:
        client_gate.authentication_ended(client)
        if active_owner:
            client_gate.release_active(client)
        close_client_socket(client)
        session_closed.set()


def serve(args: argparse.Namespace) -> int:
    log_startup_summary(args)
    stop_event = threading.Event()
    client_gate = ClientAdmissionGate()
    client_threads: set[threading.Thread] = set()
    client_threads_lock = threading.Lock()
    discovery_thread: threading.Thread | None = None
    if not args.no_discovery:
        discovery_thread = threading.Thread(target=discovery_loop, args=(args, stop_event), daemon=True)
        discovery_thread.start()

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((args.host, args.port))
        server.listen(4)
        server.settimeout(0.5)
        log(f"RemoteDesk Linux host listening on {args.host}:{args.port}")
        deadline = time.monotonic() + args.serve_seconds if args.serve_seconds > 0 else None
        try:
            while not stop_event.is_set():
                if deadline is not None and time.monotonic() >= deadline:
                    break
                try:
                    client, address = server.accept()
                except socket.timeout:
                    continue
                log(f"client connected: {address[0]}:{address[1]}")
                if not client_gate.try_register_pending(client):
                    log(
                        "authentication queue full; rejected client: "
                        f"{address[0]}:{address[1]}"
                    )
                    close_client_socket(client)
                    continue

                def client_worker(
                    admitted_client: socket.socket = client,
                    admitted_address: tuple[Any, ...] = address,
                ) -> None:
                    try:
                        run_admitted_client(
                            admitted_client,
                            admitted_address,
                            args,
                            stop_event,
                            client_gate,
                        )
                    finally:
                        with client_threads_lock:
                            client_threads.discard(threading.current_thread())

                client_thread = threading.Thread(
                    target=client_worker,
                    name=f"RemoteDeskLinuxClient-{address[0]}-{address[1]}",
                    daemon=True,
                )
                with client_threads_lock:
                    client_threads.add(client_thread)
                try:
                    client_thread.start()
                except RuntimeError:
                    with client_threads_lock:
                        client_threads.discard(client_thread)
                    client_gate.authentication_ended(client)
                    close_client_socket(client)
                    raise
                if args.once:
                    client_thread.join()
                    break
        except KeyboardInterrupt:
            pass
        finally:
            stop_event.set()
            for client in client_gate.stop_and_drain():
                close_client_socket(client)
            with client_threads_lock:
                remaining_threads = tuple(client_threads)
            for client_thread in remaining_threads:
                if client_thread is not threading.current_thread():
                    client_thread.join(timeout=1.0)
            if discovery_thread is not None:
                discovery_thread.join(timeout=1)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Run a RemoteDesk-compatible Linux host.")
    parser.add_argument("--host", default="0.0.0.0")
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
    parser.add_argument("--receive-dir", default="~/Downloads/RemoteDeskReceived")
    parser.add_argument("--return-file", action="append", default=[], help="File or directory to return when Windows requests remote clipboard files.")
    parser.add_argument("--machine-name", default=socket.gethostname() or "Linux")
    parser.add_argument(
        "--fps",
        type=float,
        default=DEFAULT_HOST_FPS,
        help=f"Requested capture rate; hardware H.264 supports up to {MAX_HOST_FPS:g} fps.",
    )
    parser.add_argument(
        "--max-video-bitrate-mbps",
        type=float,
        default=DEFAULT_H264_MAX_BITRATE_BPS / 1_000_000,
        help="Maximum hardware H.264 bitrate in Mbps (default: 160).",
    )
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--no-adaptive-video", action="store_true",
                        help="Keep the requested H.264 size even if TCP video is bandwidth-bound.")
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--capture", choices=("x11", "placeholder"), default="x11")
    parser.add_argument("--display", help="X11 DISPLAY to use for capture, input, and clipboard. Auto-detected when omitted.")
    parser.add_argument("--auth-timeout", type=float, default=10.0)
    parser.add_argument("--serve-seconds", type=float, default=0.0)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--no-discovery", action="store_true")
    parser.add_argument("--discovery-host", default="0.0.0.0")
    parser.add_argument("--discovery-port", type=int, default=56566)
    args = parser.parse_args()
    try:
        from remotedesk_linux_devices import local_device_id
        args.device_id = local_device_id()
    except Exception:
        # Identity/history problems must not prevent the remote host from starting.
        args.device_id = ""

    try:
        args.password = resolve_password_argument(args.password, args.password_fd)
    except (OSError, ValueError) as ex:
        parser.error(str(ex))
    if not math.isfinite(args.auth_timeout) or args.auth_timeout <= 0:
        parser.error("--auth-timeout must be positive")
    args.fps = normalize_host_fps(args.fps)
    args.max_video_bitrate_mbps = (
        normalize_h264_max_bitrate_bps(args.max_video_bitrate_mbps) / 1_000_000
    )
    args.display_state, _ = configure_display_environment(args.display)
    return serve(args)


if __name__ == "__main__":
    raise SystemExit(main())
