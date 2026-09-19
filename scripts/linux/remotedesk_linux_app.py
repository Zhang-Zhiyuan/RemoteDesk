#!/usr/bin/env python3
"""RemoteDesk Linux graphical app.

This is a lightweight Tk front-end for the Linux host and a basic Linux
viewer. It intentionally reuses the protocol implementation from
remotedesk_protocol_probe.py so the GUI stays aligned with the package's
existing encrypted protocol behavior.
"""

from __future__ import annotations

import atexit
import base64
import functools
import hashlib
import io
import json
import math
import os
import queue
import re
import signal
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
from collections import deque
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Any, Iterator
from uuid import uuid4
import remotedesk_linux_relay as relay
import remotedesk_linux_relay_login as relay_login

if __name__ == "__main__" and sys.platform.startswith("linux"):
    # Run before importing Tk/Pillow/cryptography so missing imports are repairable.
    from remotedesk_linux_dependencies import prepare_runtime

    dependency_status = prepare_runtime("app")
    if dependency_status:
        raise SystemExit(dependency_status)

try:
    import tkinter as tk
    from tkinter import filedialog, font as tkfont, messagebox, simpledialog, ttk
except Exception as ex:  # pragma: no cover - exercised on target desktops.
    print(f"RemoteDesk Linux GUI requires tkinter: {ex}", file=sys.stderr)
    raise SystemExit(2)

from remotedesk_protocol_probe import (
    CAPABILITY_CLIPBOARD_TEXT,
    CAPABILITY_CLIPBOARD_PASTE_SHORTCUT,
    CAPABILITY_FILE_CHECKSUM,
    CAPABILITY_FILE_RECEIVE,
    CAPABILITY_FILE_TRANSFER_RECEIPT,
    CAPABILITY_FILE_RECEIVE_LOCATION,
    CONTROL_FILE_RECEIVE_LOCATION,
    encode_file_receive_location_request,
    CAPABILITY_FILE_TRANSFER_CANCEL,
    CAPABILITY_HIGH_FRAME_RATE_H264,
    CAPABILITY_HIGH_QUALITY_JPEG,
    CAPABILITY_INPUT_CONTROL,
    CAPABILITY_SHORT_GOP_H264,
    CONTROL_CAPTURE_TARGET_CHANGED,
    CONTROL_CAPTURE_TARGET_LIST,
    CONTROL_CLIPBOARD_STATUS,
    CONTROL_CLIPBOARD_GET_TEXT,
    CONTROL_CLIPBOARD_TEXT,
    CONTROL_DEVICE_INFO,
    CONTROL_DEVICE_IDENTITY_REQUEST,
    CONTROL_DEVICE_IDENTITY,
    CAPABILITY_DEVICE_IDENTITY,
    CONTROL_FILE_TRANSFER_STATUS,
    CONTROL_FILE_TRANSFER_RECEIPT,
    CONTROL_SESSION_REJECTED,
    RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES,
    MAX_FILE_TRANSFER_BYTES,
    MESSAGE_CONTROL,
    MESSAGE_FRAME,
    MESSAGE_INPUT,
    MESSAGE_PING,
    MESSAGE_PONG,
    MESSAGE_VIDEO_FRAME,
    VIDEO_CODEC_H264_ANNEX_B,
    VIDEO_CODEC_JPEG,
    ProtocolError,
    TransferCancelledError,
    authenticate,
    capability_names,
    create_safe_directory_archive,
    decode_control,
    encode_clipboard_set_text,
    encode_file_transfer_cancel,
    encode_file_transfer_checksum,
    encode_file_transfer_chunk,
    encode_file_transfer_complete,
    encode_file_transfer_start,
    encode_select_capture_target,
    encode_viewer_capabilities,
    encode_viewer_info,
    encode_video_key_frame_request,
    inspect_encoded_image_dimensions,
    read_message,
    rearm_tcp_quickack,
    safe_transfer_path_size,
    sanitize_file_name,
    validate_frame_dimensions,
    write_message,
)
import remotedesk_linux_startup as host_startup
from remotedesk_linux_device_panel import DevicePanel


SCRIPT_DIR = Path(__file__).resolve().parent
HOST_SCRIPT = SCRIPT_DIR / "remotedesk_linux_host.py"
APP_ICON = SCRIPT_DIR / "RemoteDesk.png"
MAX_TEXT_INPUT_CODEPOINTS = 1024
FILE_TRANSFER_RENAME_SUFFIX = "（如重名会自动改名）"
DISPLAY_DEFAULT_WIDTH = 960
DISPLAY_DEFAULT_HEIGHT = 540
DISPLAY_LIMIT_WIDTH = 3840
DISPLAY_LIMIT_HEIGHT = 2160
WINDOW_MARGIN_PIXELS = 24
TK_BASE_SCALING = 96.0 / 72.0
EVENT_POLL_MS = 16
EVENT_BACKLOG_POLL_MS = 1
UI_EVENT_POLL_MAX_EVENTS = 128
UI_EVENT_POLL_BUDGET_SECONDS = 0.006
UI_EVENT_MAX_PENDING = 512
PENDING_HOST_LOG_MAX_CHARACTERS = 64 * 1024
PENDING_HOST_LOG_TRIM_CHARACTERS = 48 * 1024
HOST_LOG_MAX_CHARACTERS = 768 * 1024
HOST_LOG_TRIM_CHARACTERS = 640 * 1024
MOUSE_MOTION_INTERVAL_SECONDS = 0.006
FRAME_STATUS_INTERVAL_SECONDS = 0.75
INPUT_QUEUE_LIMIT = 256
INPUT_RELEASE_RESERVE = 64
VIEWER_FILE_PREVIEW_WORKER_NAME = "RemoteDeskFilePreview"
SOCKET_RECEIVE_BUFFER_BYTES = 128 * 1024
SOCKET_SEND_BUFFER_BYTES = 32 * 1024
VIEWER_HEARTBEAT_INTERVAL_SECONDS = 5.0
VIEWER_HEARTBEAT_TIMEOUT_SECONDS = 18.0
VIEWER_FILE_TRANSFER_HEARTBEAT_TIMEOUT_SECONDS = 150.0
VIEWER_HEARTBEAT_POLL_SECONDS = 0.25
VIEWER_RECONNECT_DELAYS_SECONDS = (0.5, 1.0, 2.0, 4.0, 8.0, 10.0)
VIEWER_RECONNECT_STABLE_SECONDS = 5.0
CAPTURE_TARGET_STATUS_TRAILER_PREFIX = "RemoteDesk.CaptureTargetStatus/v1|"
CAPTURE_TARGET_STATUS_TRAILER_PATTERN = re.compile(
    r"RemoteDesk\.CaptureTargetStatus/v1\|"
    r"(?:available|unavailable)\|"
    r"(?P<target_id>[A-Za-z0-9+/]+={0,2})\|"
    r"(?P<display_name>[A-Za-z0-9+/]+={0,2})\|[0-9]+"
)
FRAME_ENCODING_JPEG = 1
FRAME_ENCODING_H264_ANNEX_B = 2
FRAME_FLAG_KEY_FRAME = 1
FRAME_FLAG_CODEC_CONFIG = 1 << 1
H264_DECODE_TIMEOUT_SECONDS = 0.12
H264_DECODE_MISS_FALLBACK_THRESHOLD = 8
H264_HARDWARE_DECODE_MISS_ROTATION_THRESHOLD = 4
H264_KEY_FRAME_REQUEST_INTERVAL = 3
H264_KEY_FRAME_REQUEST_MIN_SECONDS = 0.55
MAX_QUEUED_H264_FRAMES = 4
MAX_NATIVE_PRESENTER_H264_FRAMES = 2
NATIVE_PRESENTER_ACTIVATION_TIMEOUT_SECONDS = 1.5
NATIVE_PRESENTER_IPC_QUERY_TIMEOUT_SECONDS = 0.08
NATIVE_PRESENTER_COMPATIBILITY_PREVIEW_TIMEOUT_SECONDS = 0.04
# FFmpeg may keep a small fixed number of access units in flight before it
# emits the first decoded frame.  Admit the common two-AU pipeline without
# tying freshness to the much larger consecutive-miss fallback threshold.
# The correlation queue is independently bounded so a decoder that emits an
# occasional, increasingly stale frame cannot grow memory or latency forever.
MAX_H264_CORRELATED_SUBMISSION_LAG = 2
MAX_H264_DECODER_OUTSTANDING_CORRELATIONS = MAX_QUEUED_H264_FRAMES
DECODER_ERROR_TAIL_LENGTH = 2048
MAX_DECODER_JPEG_BYTES = 8 * 1024 * 1024
JPEG_START_MARKER = b"\xff\xd8"
JPEG_END_MARKER = b"\xff\xd9"
H264_AUD_BOUNDARY = b"\x00\x00\x00\x01\x09\xf0"
HOST_SIZE_OPTIONS = (
    "960x540",
    "1280x720",
    "1600x900",
    "1920x1080",
    "2560x1440",
    "3840x2160",
)
HOST_FPS_OPTIONS = ("24.0", "30.0", "60.0", "15.0")
DEFAULT_HOST_SIZE = "1920x1080"
DEFAULT_HOST_FPS = "60.0"
APP_BG = "#f3f6fb"
PANEL_BG = "#ffffff"
PANEL_BORDER = "#dbe4ef"
PANEL_MUTED_BG = "#f8fafc"
TEXT_COLOR = "#0f172a"
MUTED_TEXT_COLOR = "#475569"
SUBTLE_TEXT_COLOR = "#64748b"
ACCENT_COLOR = "#2563eb"
ACCENT_ACTIVE_COLOR = "#1d4ed8"
ACCENT_DISABLED_COLOR = "#93c5fd"
DANGER_COLOR = "#dc2626"
DANGER_ACTIVE_COLOR = "#b91c1c"
HEADER_BG = "#0b1220"
HEADER_TEXT_COLOR = "#f8fafc"
HEADER_MUTED_COLOR = "#94a3b8"
VIEWER_BG = "#020617"
VIEWER_PANEL_BG = "#0b1220"
VIEWER_STATUS_BG = "#111a2e"
VIEWER_TEXT_COLOR = "#e5e7eb"

INPUT_MOUSE_MOVE = 1
INPUT_MOUSE_DOWN = 2
INPUT_MOUSE_UP = 3
INPUT_MOUSE_WHEEL = 4
INPUT_KEY_DOWN = 5
INPUT_KEY_UP = 6
INPUT_TEXT = 7
MOUSE_NONE = 0
MOUSE_LEFT = 1
MOUSE_RIGHT = 2
MOUSE_MIDDLE = 3
TK_KEYSYM_TO_WINDOWS_VK = {
    "BackSpace": 0x08,
    "Tab": 0x09,
    "Return": 0x0D,
    "Enter": 0x0D,
    "Shift_L": 0xA0,
    "Shift_R": 0xA1,
    "Control_L": 0xA2,
    "Control_R": 0xA3,
    "Alt_L": 0xA4,
    "Alt_R": 0xA5,
    "ISO_Level3_Shift": 0xA5,
    "Mode_switch": 0xA5,
    "Escape": 0x1B,
    "space": 0x20,
    "Caps_Lock": 0x14,
    "Prior": 0x21,
    "Next": 0x22,
    "End": 0x23,
    "Home": 0x24,
    "Left": 0x25,
    "Up": 0x26,
    "Right": 0x27,
    "Down": 0x28,
    "Insert": 0x2D,
    "Delete": 0x2E,
    "Super_L": 0x5B,
    "Super_R": 0x5C,
    "KP_0": 0x60,
    "KP_1": 0x61,
    "KP_2": 0x62,
    "KP_3": 0x63,
    "KP_4": 0x64,
    "KP_5": 0x65,
    "KP_6": 0x66,
    "KP_7": 0x67,
    "KP_8": 0x68,
    "KP_9": 0x69,
    "KP_Insert": 0x60,
    "KP_End": 0x61,
    "KP_Down": 0x62,
    "KP_Next": 0x63,
    "KP_Left": 0x64,
    "KP_Begin": 0x65,
    "KP_Right": 0x66,
    "KP_Home": 0x67,
    "KP_Up": 0x68,
    "KP_Prior": 0x69,
    "KP_Multiply": 0x6A,
    "KP_Add": 0x6B,
    "KP_Separator": 0x6C,
    "KP_Subtract": 0x6D,
    "KP_Decimal": 0x6E,
    "KP_Delete": 0x6E,
    "KP_Divide": 0x6F,
    "KP_Enter": 0x0D,
    "semicolon": 0xBA,
    "colon": 0xBA,
    "equal": 0xBB,
    "plus": 0xBB,
    "comma": 0xBC,
    "less": 0xBC,
    "minus": 0xBD,
    "underscore": 0xBD,
    "period": 0xBE,
    "greater": 0xBE,
    "slash": 0xBF,
    "question": 0xBF,
    "grave": 0xC0,
    "asciitilde": 0xC0,
    "bracketleft": 0xDB,
    "braceleft": 0xDB,
    "backslash": 0xDC,
    "bar": 0xDC,
    "bracketright": 0xDD,
    "braceright": 0xDD,
    "apostrophe": 0xDE,
    "quotedbl": 0xDE,
    "exclam": 0x31,
    "at": 0x32,
    "numbersign": 0x33,
    "dollar": 0x34,
    "percent": 0x35,
    "asciicircum": 0x36,
    "ampersand": 0x37,
    "asterisk": 0x38,
    "parenleft": 0x39,
    "parenright": 0x30,
}

for _function_key in range(1, 25):
    TK_KEYSYM_TO_WINDOWS_VK[f"F{_function_key}"] = 0x6F + _function_key


@dataclass(frozen=True)
class AdaptiveWindowGeometry:
    width: int
    height: int
    x: int
    y: int
    minimum_width: int
    minimum_height: int

    def to_tk_geometry(self) -> str:
        return f"{self.width}x{self.height}+{self.x}+{self.y}"


@dataclass
class ViewerReconnectPolicy:
    """Retains reconnect eligibility across short-lived transport attempts.

    A logical viewer session earns automatic reconnect only after receiving
    authenticated DeviceInfo.  Failed reconnect attempts retain that proof,
    but only five seconds of generation-owned stability resets accumulated
    backoff.  An authentication rejection or explicit disconnect revokes the
    proof immediately.
    """

    qualified: bool = False
    cancelled: bool = True
    authentication_failed: bool = False
    retry_index: int = 0

    def begin(self) -> None:
        self.qualified = False
        self.cancelled = False
        self.authentication_failed = False
        self.retry_index = 0

    def mark_device_info(self) -> None:
        if self.cancelled:
            return
        self.qualified = True
        self.authentication_failed = False

    def mark_connection_stable(self) -> None:
        if self.cancelled or self.authentication_failed or not self.qualified:
            return
        self.retry_index = 0

    def mark_authentication_failed(self) -> None:
        self.authentication_failed = True
        self.qualified = False

    def cancel(self) -> None:
        self.cancelled = True
        self.qualified = False

    def next_delay(self) -> float | None:
        if self.cancelled or self.authentication_failed or not self.qualified:
            return None
        delay = VIEWER_RECONNECT_DELAYS_SECONDS[
            min(self.retry_index, len(VIEWER_RECONNECT_DELAYS_SECONDS) - 1)
        ]
        self.retry_index += 1
        return delay


@dataclass(frozen=True)
class ViewerCaptureTarget:
    target_id: str
    display_name: str


@dataclass(frozen=True)
class ViewerCaptureTargetSnapshot:
    list_received: bool
    targets: tuple[ViewerCaptureTarget, ...]
    selected_target: ViewerCaptureTarget | None


@dataclass(frozen=True)
class ViewerCaptureTargetChoice:
    target_id: str | None
    label: str
    available: bool


@dataclass(frozen=True)
class ViewerCaptureTargetTransition:
    choices: tuple[ViewerCaptureTargetChoice, ...]
    selected_target_id: str | None
    visible: bool
    enabled: bool
    status: str | None = None
    select_target_id: str | None = None


class ViewerCaptureTargetState:
    """Generation-fenced capture-target intent and presentation state.

    ``desired_target_id`` survives transport generations inside one logical
    auto-reconnecting session.  Per-generation handshake, list and request
    state never does.  That separation prevents an old connection from
    changing the selector while still allowing one reselect when the desired
    target becomes available on the replacement connection.
    """

    def __init__(self) -> None:
        self.generation: int | None = None
        self.device_info_received = False
        self.list_received = False
        self.targets: tuple[ViewerCaptureTarget, ...] = ()
        self.selected_target: ViewerCaptureTarget | None = None
        self.desired_target_id: str | None = None
        self.desired_display_name = ""
        self.requested_target_id: str | None = None
        self._desired_was_available = False

    def begin_logical_session(self) -> None:
        self.desired_target_id = None
        self.desired_display_name = ""
        self.generation = None
        self._reset_generation_state()

    def end_logical_session(self) -> None:
        self.begin_logical_session()

    def begin_generation(self, generation: int) -> ViewerCaptureTargetTransition:
        self.generation = int(generation)
        self._reset_generation_state()
        return self._transition()

    def observe_device_info(self, generation: int) -> ViewerCaptureTargetTransition:
        if generation != self.generation:
            return self._transition()
        self.device_info_received = True
        return self._transition_with_reselect()

    def observe_snapshot(
        self,
        generation: int,
        snapshot: ViewerCaptureTargetSnapshot,
    ) -> ViewerCaptureTargetTransition:
        if generation != self.generation:
            return self._transition()
        if snapshot.list_received:
            self.list_received = True
            self.targets = normalize_viewer_capture_targets(snapshot.targets)
        if snapshot.selected_target is not None:
            selected = normalize_viewer_capture_target(snapshot.selected_target)
            if selected is not None:
                self.selected_target = selected
                if self.requested_target_id == selected.target_id:
                    self.requested_target_id = None

        desired_available = self._desired_is_available()
        if self.desired_target_id is not None and not desired_available:
            # A request made before a topology removal is no longer in
            # flight.  If the exact target returns, permit one fresh request.
            self.requested_target_id = None
        elif desired_available and not self._desired_was_available:
            self.requested_target_id = None
        self._desired_was_available = desired_available
        return self._transition_with_reselect()

    def choose_target(
        self,
        generation: int,
        target_id: str,
    ) -> ViewerCaptureTargetTransition:
        if generation != self.generation or not self._ready():
            return self._transition()
        target = self._target_by_id(target_id)
        if target is None:
            return self._transition(
                status="所选屏幕已不在远端可用列表中。",
            )
        supersedes_request = (
            self.requested_target_id is not None
            and self.requested_target_id != target.target_id
        )
        if self.desired_target_id != target.target_id:
            self.requested_target_id = None
        self.desired_target_id = target.target_id
        self.desired_display_name = target.display_name
        self._desired_was_available = True
        return self._transition_with_reselect(
            user_selected=True,
            force_selection=supersedes_request,
        )

    def selection_enqueue_failed(
        self,
        generation: int,
        target_id: str,
    ) -> ViewerCaptureTargetTransition:
        if (
            generation == self.generation
            and self.requested_target_id == target_id
        ):
            self.requested_target_id = None
            return self._transition(status="屏幕切换请求未能加入发送队列。")
        return self._transition()

    def _reset_generation_state(self) -> None:
        self.device_info_received = False
        self.list_received = False
        self.targets = ()
        self.selected_target = None
        self.requested_target_id = None
        self._desired_was_available = False

    def _ready(self) -> bool:
        return self.device_info_received and self.list_received

    def _target_by_id(self, target_id: str) -> ViewerCaptureTarget | None:
        return next(
            (target for target in self.targets if target.target_id == target_id),
            None,
        )

    def _desired_is_available(self) -> bool:
        return (
            self.desired_target_id is not None
            and self._target_by_id(self.desired_target_id) is not None
        )

    def _transition_with_reselect(
        self,
        *,
        user_selected: bool = False,
        force_selection: bool = False,
    ) -> ViewerCaptureTargetTransition:
        status: str | None = None
        select_target_id: str | None = None
        desired = (
            self._target_by_id(self.desired_target_id)
            if self.desired_target_id is not None
            else None
        )
        if self._ready() and self.desired_target_id is not None:
            if desired is None:
                current = self._selected_description()
                status = (
                    f"期望屏幕“{self._desired_description()}”已不可用；"
                    f"远端当前显示{current}，未自动改选其他屏幕。"
                )
            elif (
                self.selected_target is None
                or self.selected_target.target_id != desired.target_id
            ):
                if self.requested_target_id != desired.target_id:
                    self.requested_target_id = desired.target_id
                    select_target_id = desired.target_id
                status = f"正在切换到屏幕“{desired.display_name}”…"
            else:
                if force_selection:
                    self.requested_target_id = desired.target_id
                    select_target_id = desired.target_id
                status = f"正在查看屏幕“{desired.display_name}”。"
        elif self._ready() and self.selected_target is not None:
            status = f"正在查看屏幕“{self.selected_target.display_name}”。"
        elif user_selected:
            status = "远端屏幕列表尚未就绪。"
        return self._transition(status=status, select_target_id=select_target_id)

    def _selected_description(self) -> str:
        if self.selected_target is None:
            return "的屏幕尚未确认"
        return f"屏幕“{self.selected_target.display_name}”"

    def _desired_description(self) -> str:
        return self.desired_display_name or self.desired_target_id or "未知屏幕"

    def _transition(
        self,
        *,
        status: str | None = None,
        select_target_id: str | None = None,
    ) -> ViewerCaptureTargetTransition:
        choices = build_viewer_capture_target_choices(
            self.targets,
            self.desired_target_id,
            self.desired_display_name,
        )
        desired_missing = (
            self.desired_target_id is not None
            and not self._desired_is_available()
        )
        if desired_missing:
            selected_target_id = self.desired_target_id
        elif self.selected_target is not None:
            selected_target_id = self.selected_target.target_id
        else:
            selected_target_id = self.desired_target_id
        # A normal zero/one-screen host needs no selector.  If a remembered
        # target disappeared, keep it visible so the mismatch is explicit and
        # the user can deliberately choose an available replacement.
        visible = self.list_received and (
            len(self.targets) > 1 or desired_missing
        )
        enabled = self._ready() and (
            len(self.targets) > 1
            or (desired_missing and bool(self.targets))
        )
        return ViewerCaptureTargetTransition(
            choices,
            selected_target_id,
            visible,
            enabled,
            status,
            select_target_id,
        )


def normalize_viewer_capture_target(
    target: ViewerCaptureTarget,
) -> ViewerCaptureTarget | None:
    target_id = str(target.target_id or "")
    if not target_id:
        return None
    display_name = str(target.display_name or "").strip() or target_id
    return ViewerCaptureTarget(target_id, display_name)


def normalize_viewer_capture_targets(
    targets: tuple[ViewerCaptureTarget, ...],
) -> tuple[ViewerCaptureTarget, ...]:
    normalized: list[ViewerCaptureTarget] = []
    seen_ids: set[str] = set()
    for item in targets:
        target = normalize_viewer_capture_target(item)
        if target is None or target.target_id in seen_ids:
            continue
        seen_ids.add(target.target_id)
        normalized.append(target)
    return tuple(normalized)


def build_viewer_capture_target_choices(
    targets: tuple[ViewerCaptureTarget, ...],
    desired_target_id: str | None,
    desired_display_name: str,
) -> tuple[ViewerCaptureTargetChoice, ...]:
    normalized = normalize_viewer_capture_targets(targets)
    name_counts: dict[str, int] = {}
    for target in normalized:
        name_counts[target.display_name] = name_counts.get(target.display_name, 0) + 1
    choices: list[ViewerCaptureTargetChoice] = []
    used_labels: set[str] = set()
    for target in normalized:
        label = target.display_name
        if name_counts[label] > 1:
            label = f"{label} — {target.target_id}"
        while label in used_labels:
            label += " "
        used_labels.add(label)
        choices.append(ViewerCaptureTargetChoice(target.target_id, label, True))
    if (
        desired_target_id is not None
        and all(target.target_id != desired_target_id for target in normalized)
    ):
        label = f"{desired_display_name or desired_target_id}（不可用）"
        if label in used_labels:
            label = f"{label} — {desired_target_id}"
        choices.insert(
            0,
            ViewerCaptureTargetChoice(desired_target_id, label, False),
        )
    return tuple(choices)


def strip_capture_target_status_trailer(message: str) -> str:
    """Hide a strict Windows capture-status machine trailer from Linux UI."""

    text = str(message or "")
    separator_index = text.rfind("\n")
    if separator_index <= 0:
        return text
    trailer = text[separator_index + 1:]
    match = CAPTURE_TARGET_STATUS_TRAILER_PATTERN.fullmatch(trailer)
    if (
        not trailer.startswith(CAPTURE_TARGET_STATUS_TRAILER_PREFIX)
        or match is None
        or not is_canonical_base64_text(match.group("target_id"))
        or not is_canonical_base64_text(match.group("display_name"))
        or CAPTURE_TARGET_STATUS_TRAILER_PREFIX in text[:separator_index]
    ):
        return text
    display_message = text[:separator_index]
    return display_message if display_message.strip() else text


def is_canonical_base64_text(value: str) -> bool:
    if not value or len(value) % 4 != 0:
        return False
    try:
        decoded = base64.b64decode(value, validate=True)
    except (ValueError, TypeError):
        return False
    return base64.b64encode(decoded).decode("ascii") == value


class BoundedLineBuffer:
    """Keeps a low-water-mark tail without rescanning retained log text."""

    def __init__(self, maximum_characters: int, trim_characters: int) -> None:
        self.maximum_characters = max(1, int(maximum_characters))
        self.trim_characters = max(
            0,
            min(int(trim_characters), self.maximum_characters),
        )
        self._complete_lines: deque[str] = deque()
        self._partial_line = ""
        self.character_count = 0

    def append(self, text: str) -> None:
        if not text:
            return
        pieces = text.splitlines(keepends=True)
        if not pieces:
            pieces = [text]
        for piece in pieces:
            if self._partial_line:
                piece = self._partial_line + piece
                self.character_count -= len(self._partial_line)
                self._partial_line = ""
            self.character_count += len(piece)
            if piece.endswith(("\n", "\r")):
                self._complete_lines.append(piece)
            else:
                self._partial_line = piece
        self._trim_if_needed()

    def _trim_if_needed(self) -> None:
        if self.character_count <= self.maximum_characters:
            return
        while (
            self._complete_lines
            and self.character_count > self.trim_characters
        ):
            self.character_count -= len(self._complete_lines.popleft())
        if self.character_count > self.maximum_characters:
            keep = min(self.trim_characters, len(self._partial_line))
            self._partial_line = self._partial_line[-keep:] if keep else ""
            self.character_count = len(self._partial_line)

    def __str__(self) -> str:
        return "".join((*self._complete_lines, self._partial_line))


class BoundedLineLengthTracker:
    """Tracks Text-widget line lengths and returns batched deletion sizes."""

    def __init__(self, maximum_characters: int, trim_characters: int) -> None:
        self.maximum_characters = max(1, int(maximum_characters))
        self.trim_characters = max(
            0,
            min(int(trim_characters), self.maximum_characters),
        )
        self._complete_line_lengths: deque[int] = deque()
        self._partial_line_length = 0
        self.character_count = 0

    def append(self, text: str) -> int:
        if not text:
            return 0
        pieces = text.splitlines(keepends=True) or [text]
        for piece in pieces:
            piece_length = len(piece)
            self.character_count += piece_length
            self._partial_line_length += piece_length
            if piece.endswith(("\n", "\r")):
                self._complete_line_lengths.append(
                    self._partial_line_length
                )
                self._partial_line_length = 0

        if self.character_count <= self.maximum_characters:
            return 0
        removed = 0
        while (
            self._complete_line_lengths
            and self.character_count - removed > self.trim_characters
        ):
            removed += self._complete_line_lengths.popleft()
        if removed:
            self.character_count -= removed

        if self.character_count > self.maximum_characters:
            # A single unterminated line cannot be cropped at a line
            # boundary. Keep a bounded suffix rather than allowing an
            # adversarial line to grow the widget indefinitely.
            partial_removed = max(
                0,
                self.character_count - self.trim_characters,
            )
            self.character_count -= partial_removed
            self._partial_line_length = max(
                0,
                self._partial_line_length - partial_removed,
            )
            removed += partial_removed
        return removed


def calculate_adaptive_window_geometry(
    screen_bounds: tuple[int, int, int, int],
    preferred_size: tuple[int, int],
    minimum_size: tuple[int, int],
    anchor_bounds: tuple[int, int, int, int] | None = None,
    margin: int = WINDOW_MARGIN_PIXELS,
) -> AdaptiveWindowGeometry:
    screen_x, screen_y, screen_width, screen_height = screen_bounds
    screen_width = max(1, int(screen_width))
    screen_height = max(1, int(screen_height))
    requested_margin = max(0, int(margin))
    margin_x = min(requested_margin, max(0, (screen_width - 1) // 2))
    margin_y = min(requested_margin, max(0, (screen_height - 1) // 2))
    available_x = int(screen_x) + margin_x
    available_y = int(screen_y) + margin_y
    available_width = max(1, screen_width - margin_x * 2)
    available_height = max(1, screen_height - margin_y * 2)

    requested_minimum_width = max(1, int(minimum_size[0]))
    requested_minimum_height = max(1, int(minimum_size[1]))
    minimum_width = min(requested_minimum_width, available_width)
    minimum_height = min(requested_minimum_height, available_height)
    width = min(
        available_width,
        max(minimum_width, max(1, int(preferred_size[0]))),
    )
    height = min(
        available_height,
        max(minimum_height, max(1, int(preferred_size[1]))),
    )

    anchor_x, anchor_y, anchor_width, anchor_height = anchor_bounds or screen_bounds
    centered_x = int(anchor_x) + (max(1, int(anchor_width)) - width) // 2
    centered_y = int(anchor_y) + (max(1, int(anchor_height)) - height) // 2
    maximum_x = max(available_x, available_x + available_width - width)
    maximum_y = max(available_y, available_y + available_height - height)
    x = max(available_x, min(centered_x, maximum_x))
    y = max(available_y, min(centered_y, maximum_y))
    return AdaptiveWindowGeometry(
        width,
        height,
        x,
        y,
        minimum_width,
        minimum_height,
    )


def normalize_viewer_display_size(width: int, height: int) -> tuple[int, int] | None:
    if width < 64 or height < 64:
        return None
    return (
        min(DISPLAY_LIMIT_WIDTH, max(64, int(width))),
        min(DISPLAY_LIMIT_HEIGHT, max(64, int(height))),
    )


def parse_xrandr_monitor_bounds(output: str) -> list[tuple[int, int, int, int]]:
    monitor_pattern = re.compile(
        r"(?P<width>\d+)/\d+x(?P<height>\d+)/\d+"
        r"(?P<x>[+-]-?\d+)(?P<y>[+-]-?\d+)"
    )
    monitors: list[tuple[int, int, int, int]] = []
    for line in (output or "").splitlines():
        match = monitor_pattern.search(line)
        if match is None:
            continue
        x_text = match.group("x").replace("+-", "-")
        y_text = match.group("y").replace("+-", "-")
        monitors.append(
            (
                int(x_text),
                int(y_text),
                max(1, int(match.group("width"))),
                max(1, int(match.group("height"))),
            )
        )
    return monitors


def choose_monitor_bounds(
    monitors: list[tuple[int, int, int, int]],
    anchor_x: int,
    anchor_y: int,
) -> tuple[int, int, int, int] | None:
    if not monitors:
        return None
    for monitor in monitors:
        x, y, width, height = monitor
        if x <= anchor_x < x + width and y <= anchor_y < y + height:
            return monitor

    def squared_distance(monitor: tuple[int, int, int, int]) -> int:
        x, y, width, height = monitor
        nearest_x = max(x, min(anchor_x, x + width - 1))
        nearest_y = max(y, min(anchor_y, y + height - 1))
        return (nearest_x - anchor_x) ** 2 + (nearest_y - anchor_y) ** 2

    return min(monitors, key=squared_distance)


def query_xrandr_monitor_bounds() -> list[tuple[int, int, int, int]]:
    xrandr = shutil.which("xrandr")
    if not xrandr:
        return []
    try:
        completed = subprocess.run(
            [xrandr, "--listactivemonitors"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            check=False,
            text=True,
            timeout=1,
        )
    except Exception:
        return []
    return parse_xrandr_monitor_bounds(completed.stdout) if completed.returncode == 0 else []


def normalize_tk_ui_scale(tk_scaling: float) -> float:
    if not math.isfinite(tk_scaling) or tk_scaling <= 0:
        return 1.0
    return max(0.75, min(3.0, tk_scaling / TK_BASE_SCALING))


def calculate_fitted_image_size(
    source_width: int,
    source_height: int,
    target_width: int,
    target_height: int,
) -> tuple[int, int]:
    if source_width <= 0 or source_height <= 0 or target_width <= 0 or target_height <= 0:
        return 1, 1
    scale = min(target_width / source_width, target_height / source_height)
    return (
        max(1, min(target_width, round(source_width * scale))),
        max(1, min(target_height, round(source_height * scale))),
    )


def apply_adaptive_window_geometry(
    window: tk.Tk | tk.Toplevel,
    preferred_size: tuple[int, int],
    minimum_size: tuple[int, int],
    parent: tk.Misc | None = None,
) -> AdaptiveWindowGeometry:
    window.update_idletasks()
    try:
        ui_scale = normalize_tk_ui_scale(float(window.tk.call("tk", "scaling")))
    except (tk.TclError, TypeError, ValueError):
        ui_scale = 1.0
    scaled_preferred_size = (
        max(1, round(preferred_size[0] * ui_scale)),
        max(1, round(preferred_size[1] * ui_scale)),
    )
    scaled_minimum_size = (
        max(1, round(minimum_size[0] * ui_scale)),
        max(1, round(minimum_size[1] * ui_scale)),
    )
    screen_x = int(window.winfo_vrootx())
    screen_y = int(window.winfo_vrooty())
    screen_width = max(1, int(window.winfo_vrootwidth() or window.winfo_screenwidth()))
    screen_height = max(1, int(window.winfo_vrootheight() or window.winfo_screenheight()))
    anchor_bounds: tuple[int, int, int, int] | None = None
    if parent is not None:
        try:
            anchor_bounds = (
                int(parent.winfo_rootx()),
                int(parent.winfo_rooty()),
                max(1, int(parent.winfo_width())),
                max(1, int(parent.winfo_height())),
            )
        except tk.TclError:
            anchor_bounds = None

    if anchor_bounds is not None:
        anchor_x = anchor_bounds[0] + anchor_bounds[2] // 2
        anchor_y = anchor_bounds[1] + anchor_bounds[3] // 2
    else:
        try:
            anchor_x, anchor_y = (int(value) for value in window.winfo_pointerxy())
        except tk.TclError:
            anchor_x = screen_x + screen_width // 2
            anchor_y = screen_y + screen_height // 2
    active_monitor = choose_monitor_bounds(
        query_xrandr_monitor_bounds(),
        anchor_x,
        anchor_y,
    )
    if active_monitor is not None:
        screen_x, screen_y, screen_width, screen_height = active_monitor

    geometry = calculate_adaptive_window_geometry(
        (screen_x, screen_y, screen_width, screen_height),
        scaled_preferred_size,
        scaled_minimum_size,
        anchor_bounds,
        margin=max(0, round(WINDOW_MARGIN_PIXELS * ui_scale)),
    )
    window.minsize(geometry.minimum_width, geometry.minimum_height)
    window.geometry(geometry.to_tk_geometry())
    return geometry


def encode_input(kind: int, button: int = MOUSE_NONE, x: int = 0, y: int = 0, data: int = 0) -> bytes:
    return struct.pack("<BBiii", kind, button, int(x), int(y), int(data))


def enqueue_input_payload(queue_items: deque[tuple[int, bytes]], item: tuple[int, bytes], max_items: int) -> bool:
    if max_items <= 0:
        return False

    kind, _payload = item
    if kind == INPUT_MOUSE_MOVE:
        if len(queue_items) == 1 and queue_items[0][0] == INPUT_MOUSE_MOVE and queue_items[0][1] == item[1]:
            return False
        retained = [pending for pending in queue_items if pending[0] != INPUT_MOUSE_MOVE]
        if len(retained) != len(queue_items):
            queue_items.clear()
            queue_items.extend(retained)

    if len(queue_items) >= max_items:
        drop_index = next((index for index, pending in enumerate(queue_items) if pending[0] == INPUT_MOUSE_MOVE), -1)
        if drop_index >= 0:
            remove_deque_item_at(queue_items, drop_index)
        elif (
            kind not in (INPUT_MOUSE_UP, INPUT_KEY_UP)
            or len(queue_items) >= max_items + INPUT_RELEASE_RESERVE
        ):
            return False

    queue_items.append(item)
    return True


def pop_next_input_payload(queue_items: deque[tuple[int, bytes]]) -> tuple[int, bytes] | None:
    if not queue_items:
        return None
    item = queue_items.popleft()
    if item[0] != INPUT_MOUSE_MOVE:
        return item
    while queue_items and queue_items[0][0] == INPUT_MOUSE_MOVE:
        item = queue_items.popleft()
    return item


def remove_deque_item_at(queue_items: deque[Any], index: int) -> None:
    queue_items.rotate(-index)
    queue_items.popleft()
    queue_items.rotate(index)


def linux_viewer_capabilities(has_native_h264_presenter: bool) -> int:
    capabilities = (
        CAPABILITY_FILE_CHECKSUM
        | CAPABILITY_FILE_TRANSFER_RECEIPT
        | CAPABILITY_FILE_TRANSFER_CANCEL
        | CAPABILITY_SHORT_GOP_H264
        | CAPABILITY_HIGH_QUALITY_JPEG
    )
    if has_native_h264_presenter:
        capabilities |= CAPABILITY_HIGH_FRAME_RATE_H264
    return capabilities


CRITICAL_UI_EVENTS = frozenset(
    {
        "viewer_file_failure",
        "viewer_file_results",
        "host_exited",
        "host_stop_completed",
        "viewer_error",
        "viewer_auth_failed",
        "viewer_session_replaced",
        "viewer_reconnect_qualified",
        "viewer_closed",
        "viewer_file_preview_ready",
    }
)
DISCARDABLE_UI_EVENTS = frozenset(
    {
        "host_log",
        "viewer_status",
        "viewer_capture_metadata",
        "viewer_frame",
        "viewer_native_frame",
    }
)
COALESCED_VIEWER_EVENTS = frozenset(
    {
        "viewer_status",
        "viewer_error",
        "viewer_auth_failed",
        "viewer_session_replaced",
        "viewer_reconnect_qualified",
        "viewer_capture_metadata",
        "viewer_file_preview_ready",
    }
)


def _viewer_event_generation(item: object) -> int | None:
    if not isinstance(item, tuple) or len(item) != 2:
        return None
    value = item[1]
    if not isinstance(value, tuple) or len(value) != 2:
        return None
    generation = value[0]
    return generation if isinstance(generation, int) else None


def _same_coalesced_ui_event(left: object, right: object) -> bool:
    if (
        not isinstance(left, tuple)
        or len(left) != 2
        or not isinstance(right, tuple)
        or len(right) != 2
    ):
        return False
    left_event = left[0]
    right_event = right[0]
    if (
        left_event in ("viewer_frame", "viewer_native_frame")
        and right_event in ("viewer_frame", "viewer_native_frame")
    ):
        return (
            _viewer_event_generation(left)
            == _viewer_event_generation(right)
        )
    if left_event != right_event:
        return False
    if left_event in COALESCED_VIEWER_EVENTS:
        return (
            _viewer_event_generation(left)
            == _viewer_event_generation(right)
        )
    if left_event == "viewer_closed":
        return left[1] == right[1]
    if left_event in ("host_exited", "host_stop_completed"):
        left_value = left[1]
        right_value = right[1]
        left_generation = (
            left_value[0]
            if isinstance(left_value, tuple) and left_value
            else None
        )
        right_generation = (
            right_value[0]
            if isinstance(right_value, tuple) and right_value
            else None
        )
        return left_generation == right_generation
    return False


def put_ui_event(
    events: "queue.Queue[tuple[str, Any]]",
    event: str,
    value: Any,
) -> None:
    """Enqueue one bounded/coalesced UI event without blocking producers."""

    item: tuple[str, Any]
    with events.mutex:
        if event == "host_log":
            for index, pending in enumerate(events.queue):
                if not isinstance(pending, tuple) or pending[0] != "host_log":
                    continue
                pending_value = pending[1]
                if not isinstance(pending_value, BoundedLineBuffer):
                    buffer = BoundedLineBuffer(
                        PENDING_HOST_LOG_MAX_CHARACTERS,
                        PENDING_HOST_LOG_TRIM_CHARACTERS,
                    )
                    buffer.append(str(pending_value))
                    events.queue[index] = ("host_log", buffer)
                    pending_value = buffer
                pending_value.append(str(value))
                events.not_empty.notify()
                return
            buffer = BoundedLineBuffer(
                PENDING_HOST_LOG_MAX_CHARACTERS,
                PENDING_HOST_LOG_TRIM_CHARACTERS,
            )
            buffer.append(str(value))
            item = (event, buffer)
        else:
            item = (event, value)

        retained = [
            pending
            for pending in events.queue
            if not _same_coalesced_ui_event(pending, item)
        ]
        removed = len(events.queue) - len(retained)

        while len(retained) >= UI_EVENT_MAX_PENDING:
            discard_index = next(
                (
                    index
                    for index, pending in enumerate(retained)
                    if isinstance(pending, tuple)
                    and pending
                    and pending[0] in DISCARDABLE_UI_EVENTS
                ),
                None,
            )
            if discard_index is None:
                if event in DISCARDABLE_UI_EVENTS:
                    return
                # The newest terminal/control state is more useful than a
                # stale one from an older generation.  This also keeps a
                # malicious stream of distinct terminal events bounded.
                retained.pop(0)
                removed += 1
                continue
            retained.pop(discard_index)
            removed += 1

        if removed:
            events.queue.clear()
            events.queue.extend(retained)
            events.unfinished_tasks = max(
                0,
                events.unfinished_tasks - removed,
            )
        events.queue.append(item)
        events.unfinished_tasks += 1
        events.not_empty.notify()


def put_viewer_event(
    events: "queue.Queue[tuple[str, Any]]",
    event: str,
    generation: int,
    value: Any,
) -> None:
    put_ui_event(events, event, (generation, value))


def is_same_viewer_frame_event(item: object, generation: int) -> bool:
    if not isinstance(item, tuple) or len(item) != 2:
        return False
    event, value = item
    if event not in ("viewer_frame", "viewer_native_frame"):
        return False
    return isinstance(value, tuple) and len(value) == 2 and value[0] == generation


def has_pending_viewer_frame_event(
    events: "queue.Queue[tuple[str, Any]]",
    generation: int,
) -> bool:
    # Status/log events share this queue but are not frame backpressure.  Only
    # an already-unpainted frame from this viewer generation should suppress
    # another expensive conversion.
    with events.mutex:
        return any(
            is_same_viewer_frame_event(item, generation)
            for item in events.queue
        )


def pop_next_critical_ui_event(
    events: "queue.Queue[tuple[str, Any]]",
) -> tuple[str, Any] | None:
    with events.mutex:
        critical_index = next(
            (
                index
                for index, pending in enumerate(events.queue)
                if isinstance(pending, tuple)
                and pending
                and pending[0] in CRITICAL_UI_EVENTS
            ),
            None,
        )
        if critical_index is None:
            return None
        item = events.queue[critical_index]
        remove_deque_item_at(events.queue, critical_index)
        events.unfinished_tasks = max(0, events.unfinished_tasks - 1)
        events.not_full.notify()
        return item


def iter_ui_event_batch(
    events: "queue.Queue[tuple[str, Any]]",
    maximum_events: int = UI_EVENT_POLL_MAX_EVENTS,
    budget_seconds: float = UI_EVENT_POLL_BUDGET_SECONDS,
    clock: Any = time.monotonic,
) -> Iterator[tuple[str, Any]]:
    maximum = max(1, int(maximum_events))
    deadline = clock() + max(0.0, float(budget_seconds))
    emitted = 0
    while emitted < maximum:
        # This check runs when the generator resumes after the caller handled
        # the prior item, so handler/render time is part of the tick budget.
        if emitted and clock() >= deadline:
            break
        item = pop_next_critical_ui_event(events)
        if item is None:
            try:
                item = events.get_nowait()
            except queue.Empty:
                break
            # UI events do not use Queue.join/task_done; keep bookkeeping in
            # sync with custom replacement/removal paths.
            with events.mutex:
                events.unfinished_tasks = max(0, events.unfinished_tasks - 1)
        emitted += 1
        yield item


def dequeue_ui_event_batch(
    events: "queue.Queue[tuple[str, Any]]",
    maximum_events: int = UI_EVENT_POLL_MAX_EVENTS,
    budget_seconds: float = UI_EVENT_POLL_BUDGET_SECONDS,
    clock: Any = time.monotonic,
) -> list[tuple[str, Any]]:
    return list(
        iter_ui_event_batch(
            events,
            maximum_events,
            budget_seconds,
            clock,
        )
    )


def is_supported_text_codepoint(codepoint: int) -> bool:
    return (
        codepoint in (0x09, 0x0A, 0x0D)
        or 0x20 <= codepoint <= 0xD7FF
        or 0xE000 <= codepoint <= 0x10FFFF
    )


def tk_event_to_windows_virtual_key(event: tk.Event[Any]) -> int | None:
    keysym = str(getattr(event, "keysym", "") or "")
    if len(keysym) == 1:
        char = keysym.upper()
        if "A" <= char <= "Z" or "0" <= char <= "9":
            return ord(char)
    mapped = TK_KEYSYM_TO_WINDOWS_VK.get(keysym)
    if mapped is not None:
        return mapped
    # Tk's numeric keycode is an X11/Wayland platform code, not a portable
    # Windows virtual-key value. Never put it in the wire Data field.
    return None


class ViewerPressedKeyState:
    def __init__(self) -> None:
        self._pressed_keys: list[int] = []
        self._pressed_key_set: set[int] = set()
        self._pressed_mouse_buttons: list[int] = []
        self._pressed_mouse_button_set: set[int] = set()
        self._last_pointer_x = 0
        self._last_pointer_y = 0

    @property
    def count(self) -> int:
        return len(self._pressed_key_set)

    @property
    def mouse_button_count(self) -> int:
        return len(self._pressed_mouse_button_set)

    def observe_down(self, virtual_key: int) -> None:
        if virtual_key not in self._pressed_key_set:
            self._pressed_key_set.add(virtual_key)
            self._pressed_keys.append(virtual_key)

    def observe_up(self, virtual_key: int) -> None:
        if virtual_key not in self._pressed_key_set:
            return
        self._pressed_key_set.remove(virtual_key)
        self._pressed_keys.remove(virtual_key)

    def keys_in_release_order(self) -> tuple[int, ...]:
        return tuple(reversed(self._pressed_keys))

    def observe_pointer(
        self,
        kind: int,
        button: int,
        x: int,
        y: int,
    ) -> None:
        self._last_pointer_x = int(x)
        self._last_pointer_y = int(y)
        if kind == INPUT_MOUSE_DOWN:
            if button not in self._pressed_mouse_button_set:
                self._pressed_mouse_button_set.add(button)
                self._pressed_mouse_buttons.append(button)
        elif kind == INPUT_MOUSE_UP and button in self._pressed_mouse_button_set:
            self._pressed_mouse_button_set.remove(button)
            self._pressed_mouse_buttons.remove(button)

    def mouse_buttons_in_release_order(self) -> tuple[tuple[int, int, int], ...]:
        return tuple(
            (
                button,
                self._last_pointer_x,
                self._last_pointer_y,
            )
            for button in reversed(self._pressed_mouse_buttons)
        )

    def clear(self) -> None:
        self._pressed_keys.clear()
        self._pressed_key_set.clear()
        self._pressed_mouse_buttons.clear()
        self._pressed_mouse_button_set.clear()


def normalize_port(value: str, fallback: int = 56565) -> int:
    try:
        port = int(value.strip())
    except (TypeError, ValueError):
        return fallback
    return port if 1 <= port <= 65535 else fallback


def configure_low_latency_socket(sock: socket.socket, receive_buffer_size: int, send_buffer_size: int) -> None:
    for option, value in low_latency_socket_options(receive_buffer_size, send_buffer_size):
        if value <= 0:
            continue
        try:
            sock.setsockopt(option[0], option[1], value)
        except OSError:
            pass


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


def parse_size(value: str, fallback: tuple[int, int] = (1920, 1080)) -> tuple[int, int]:
    text = (value or "").strip().lower().replace(" ", "")
    if "x" not in text:
        return fallback
    width_text, height_text = text.split("x", 1)
    try:
        width = int(width_text)
        height = int(height_text)
    except ValueError:
        return fallback
    if width < 320 or height < 180 or width > 3840 or height > 2160:
        return fallback
    return width, height


def parse_legacy_frame(payload: bytes) -> tuple[int, int, int, int, bytes]:
    if len(payload) < 24:
        raise ProtocolError("RemoteDesk frame header is incomplete.")
    width, height = struct.unpack_from("<ii", payload, 0)
    encoded = payload[24:]
    validate_frame_dimensions(width, height)
    if not encoded:
        raise ProtocolError("RemoteDesk frame is invalid.")
    return FRAME_ENCODING_JPEG, width, height, FRAME_FLAG_KEY_FRAME, encoded


def parse_video_frame(payload: bytes) -> tuple[int, int, int, int, bytes]:
    if len(payload) < 32:
        raise ProtocolError("RemoteDesk video frame header is incomplete.")
    encoding, width, height, flags = struct.unpack_from("<iiii", payload, 0)
    encoded = payload[32:]
    known_flags = FRAME_FLAG_KEY_FRAME | FRAME_FLAG_CODEC_CONFIG
    if flags & ~known_flags:
        raise ProtocolError("RemoteDesk video frame flags are unsupported.")
    if encoding not in (FRAME_ENCODING_JPEG, FRAME_ENCODING_H264_ANNEX_B):
        raise ProtocolError(f"Unsupported RemoteDesk frame encoding: {encoding}.")
    validate_frame_dimensions(width, height)
    if not encoded:
        raise ProtocolError("RemoteDesk video frame is invalid.")
    return encoding, width, height, flags, encoded


def is_h264_recovery_frame(frame: tuple[int, int, int, int, int, bytes, float]) -> bool:
    _sequence, encoding, _width, _height, flags, _encoded, _received_at = frame
    return (
        encoding == FRAME_ENCODING_H264_ANNEX_B
        and bool(flags & FRAME_FLAG_KEY_FRAME)
        and bool(flags & FRAME_FLAG_CODEC_CONFIG)
    )


def find_h264_queue_start_index(
    frames: list[tuple[int, int, int, int, int, bytes, float]] | deque[tuple[int, int, int, int, int, bytes, float]],
    max_queued_frames: int,
) -> tuple[int, bool]:
    if not frames:
        return 0, False
    normalized_max = max(1, max_queued_frames)
    search_start = max(0, len(frames) - normalized_max)
    for index in range(len(frames) - 1, search_start - 1, -1):
        if is_h264_recovery_frame(frames[index]):
            return index, True
    for index in range(len(frames) - 1, search_start - 1, -1):
        _sequence, encoding, _width, _height, flags, _encoded, _received_at = frames[index]
        if encoding == FRAME_ENCODING_H264_ANNEX_B and flags & FRAME_FLAG_KEY_FRAME:
            return index, True
    return len(frames), False


def enqueue_h264_frame(
    frames: list[tuple[int, int, int, int, int, bytes, float]] | deque[tuple[int, int, int, int, int, bytes, float]],
    frame: tuple[int, int, int, int, int, bytes, float],
    max_queued_frames: int,
) -> bool:
    if is_h264_recovery_frame(frame):
        frames.clear()
    frames.append(frame)
    if len(frames) <= max_queued_frames:
        return False

    start_index, starts_at_recovery_frame = find_h264_queue_start_index(
        frames,
        max_queued_frames,
    )
    trim_h264_frame_queue(frames, start_index)
    return not starts_at_recovery_frame


def trim_h264_frame_queue(
    frames: list[tuple[int, int, int, int, int, bytes, float]] | deque[tuple[int, int, int, int, int, bytes, float]],
    start_index: int,
) -> None:
    if start_index <= 0:
        return
    if isinstance(frames, deque):
        for _ in range(min(start_index, len(frames))):
            frames.popleft()
        return
    del frames[:start_index]


def is_h264_submission_fresh(
    decoded_submission_id: int,
    latest_submission_id: int,
    max_lag: int,
) -> bool:
    normalized_lag = max(0, max_lag)
    return (
        latest_submission_id - normalized_lag
        <= decoded_submission_id
        <= latest_submission_id
    )


def h264_decode_timeout_for_native_disposition(native_disposition: str) -> float:
    if native_disposition == "pending-preview":
        return NATIVE_PRESENTER_COMPATIBILITY_PREVIEW_TIMEOUT_SECONDS
    return H264_DECODE_TIMEOUT_SECONDS


def extract_decoder_jpeg_frames(buffer: bytearray) -> list[bytes]:
    frames: list[bytes] = []
    while True:
        start = buffer.find(JPEG_START_MARKER)
        if start < 0:
            if len(buffer) > 1:
                del buffer[:-1]
            return frames
        if start > 0:
            del buffer[:start]

        end = buffer.find(JPEG_END_MARKER, len(JPEG_START_MARKER))
        if end < 0:
            if len(buffer) > MAX_DECODER_JPEG_BYTES:
                del buffer[:-1]
            return frames

        frame_end = end + len(JPEG_END_MARKER)
        if frame_end <= MAX_DECODER_JPEG_BYTES:
            frames.append(bytes(buffer[:frame_end]))
        del buffer[:frame_end]


def terminate_h264_access_unit(encoded: bytes) -> bytes:
    if encoded.endswith(H264_AUD_BOUNDARY):
        return encoded
    return encoded + H264_AUD_BOUNDARY


def redact_command(command: list[str], secret_options: set[str] | None = None) -> str:
    secrets = secret_options or {"--password"}
    redacted: list[str] = []
    hide_next = False
    for value in command:
        if hide_next:
            redacted.append("***")
            hide_next = False
            continue
        redacted.append(value)
        if value in secrets:
            hide_next = True
    return " ".join(redacted)


@functools.lru_cache(maxsize=1)
def find_image_converter() -> tuple[str, ...] | None:
    magick = shutil.which("magick")
    if magick and is_imagemagick((magick,)):
        return (magick,)
    convert = shutil.which("convert")
    if convert and is_imagemagick((convert,)):
        return (convert,)
    return None


def is_imagemagick(command: tuple[str, ...]) -> bool:
    try:
        completed = subprocess.run(
            [*command, "--version"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
            timeout=2,
        )
    except Exception:
        return False
    output = completed.stdout.decode("utf-8", errors="replace")
    return completed.returncode == 0 and "ImageMagick" in output


@functools.lru_cache(maxsize=1)
def find_pillow_image() -> Any | None:
    try:
        from PIL import Image  # type: ignore

        return Image
    except Exception:
        return None


def convert_frame_with_pillow(
    encoded: bytes,
    max_width: int,
    max_height: int,
    expected_width: int | None = None,
    expected_height: int | None = None,
    *,
    output_format: str = "PNG",
) -> bytes | None:
    if output_format not in ("PNG", "PPM"):
        return None
    image_module = find_pillow_image()
    if image_module is None:
        return None
    try:
        encoded_size = inspect_encoded_image_dimensions(encoded)
        if (expected_width is None) != (expected_height is None):
            return None
        if expected_width is not None and encoded_size != (expected_width, expected_height):
            return None
        with image_module.open(io.BytesIO(encoded)) as image:
            if image.size != encoded_size:
                return None
            # JPEG/FFmpeg frames are normally already RGB. convert("RGB")
            # would allocate and copy a full source frame even in that case
            # (especially costly for 4K), without changing any pixels.
            if image.mode != "RGB":
                image = image.convert("RGB")
            resampling_owner = getattr(image_module, "Resampling", image_module)
            resample = getattr(resampling_owner, "LANCZOS", 1)
            fitted_size = calculate_fitted_image_size(
                image.width,
                image.height,
                max_width,
                max_height,
            )
            if image.size != fitted_size:
                image = image.resize(fitted_size, resample)
            output = io.BytesIO()
            # This is a bounded, local-only Tk handoff, never network traffic.
            # PPM carries the exact same RGB pixels without PNG compression
            # and decompression. Keep PNG available for compatibility/probes.
            if output_format == "PPM":
                image.save(output, format="PPM")
                signature = b"P6\n"
            else:
                image.save(output, format="PNG", compress_level=1)
                signature = b"\x89PNG"
            display_data = output.getvalue()
            return display_data if display_data.startswith(signature) else None
    except Exception:
        return None


def convert_frame_for_tk(
    encoded: bytes,
    max_width: int,
    max_height: int,
    expected_width: int | None = None,
    expected_height: int | None = None,
) -> bytes | None:
    try:
        encoded_size = inspect_encoded_image_dimensions(encoded)
    except ProtocolError:
        return None
    if (expected_width is None) != (expected_height is None):
        return None
    if expected_width is not None and encoded_size != (expected_width, expected_height):
        return None

    display_data = convert_frame_with_pillow(
        encoded,
        max_width,
        max_height,
        expected_width,
        expected_height,
        output_format="PPM",
    )
    if display_data is not None:
        return display_data

    if encoded.startswith(b"\x89PNG"):
        return encoded

    converter = find_image_converter()
    if converter:
        try:
            completed = subprocess.run(
                [*converter, "-", "-resize", f"{max_width}x{max_height}", "png:-"],
                input=encoded,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
                timeout=3,
            )
            if completed.returncode == 0 and completed.stdout.startswith(b"\x89PNG"):
                return completed.stdout
        except Exception:
            pass

    return None


def create_tk_frame_photo(
    display_data: bytes,
    *,
    master: Any = None,
) -> tk.PhotoImage:
    # Only the local converter emits PPM. The wire decoder still accepts the
    # same validated network formats; no new image format is negotiated.
    image_format = "ppm" if display_data.startswith(b"P6\n") else "png"
    return tk.PhotoImage(master=master, data=display_data, format=image_format)


@functools.lru_cache(maxsize=1)
def find_ffmpeg() -> str | None:
    return shutil.which("ffmpeg")


@functools.lru_cache(maxsize=1)
def find_mpv() -> str | None:
    return shutil.which("mpv")


@dataclass(frozen=True)
class MpvNativeH264Backend:
    key: str
    label: str
    hwdec: str
    interop: str
    surface_tokens: tuple[str, ...]

    @property
    def diagnostic(self) -> str:
        return (
            f"mpv/{self.label} 原生硬件 surface → gpu-next/EGL/X11"
            "（已验证无 CPU 回读）"
        )


MPV_NATIVE_H264_BACKENDS = (
    MpvNativeH264Backend(
        "nvdec",
        "NVDEC",
        "nvdec",
        "cuda",
        (
            "[vo/gpu-next/cuda]",
            "using cuda interop",
            "using cuda hwdec interop",
            "cuda hwdec interop initialized",
        ),
    ),
    MpvNativeH264Backend(
        "vaapi",
        "VA-API",
        "vaapi",
        "vaapi",
        (
            "using egl dmabuf interop",
            "using libplacebo dmabuf interop",
        ),
    ),
    MpvNativeH264Backend(
        "drm",
        "DRM PRIME",
        "drm",
        "drmprime",
        (
            "using egl dmabuf interop",
            "using libplacebo dmabuf interop",
        ),
    ),
)


@dataclass(frozen=True)
class NativeH264InputFrame:
    encoded: bytes
    recovery: bool


@dataclass(frozen=True)
class NativeH264SubmitResult:
    accepted: bool
    request_recovery: bool


def is_mpv_native_presenter_environment(
    window_id: int,
    windowing_system: str,
    display: str,
) -> bool:
    return (
        int(window_id) > 0
        and str(windowing_system).strip().lower() == "x11"
        and bool(str(display).strip())
    )


def parse_mpv_hwdec_help(output: str) -> frozenset[str]:
    available: set[str] = set()
    known = {backend.hwdec for backend in MPV_NATIVE_H264_BACKENDS}
    for line in (output or "").splitlines():
        fields = line.strip().lower().split(maxsplit=1)
        if not fields:
            continue
        candidate = fields[0].rstrip(":")
        if candidate in known and not candidate.endswith("-copy"):
            available.add(candidate)
    return frozenset(available)


def select_mpv_native_h264_backends(
    available_hwdecs: frozenset[str] | set[str],
) -> tuple[MpvNativeH264Backend, ...]:
    normalized = {value.strip().lower() for value in available_hwdecs}
    return tuple(
        backend
        for backend in MPV_NATIVE_H264_BACKENDS
        if backend.hwdec in normalized and not backend.hwdec.endswith("-copy")
    )


def choose_next_mpv_native_h264_backend(
    candidates: tuple[MpvNativeH264Backend, ...],
    failed_backend_keys: set[str] | frozenset[str],
) -> MpvNativeH264Backend | None:
    return next(
        (candidate for candidate in candidates if candidate.key not in failed_backend_keys),
        None,
    )


def build_mpv_native_h264_presenter_command(
    mpv: str,
    window_id: int,
    backend: MpvNativeH264Backend,
    ipc_socket_path: str,
) -> list[str]:
    if backend.hwdec.endswith("-copy"):
        raise ValueError("copy-back mpv hardware decoders are not native surface presenters")
    return [
        mpv,
        "--no-config",
        "--terminal=yes",
        "--input-terminal=no",
        f"--input-ipc-server={ipc_socket_path}",
        "--msg-level=all=warn,cplayer=info,vd=v,vo=v",
        f"--wid={int(window_id)}",
        "--vo=gpu-next",
        "--gpu-api=opengl",
        "--gpu-context=x11egl",
        f"--hwdec={backend.hwdec}",
        "--hwdec-codecs=h264",
        f"--gpu-hwdec-interop={backend.interop}",
        "--profile=low-latency",
        "--cache=no",
        "--demuxer-readahead-secs=0",
        "--demuxer-lavf-analyzeduration=0",
        "--demuxer-lavf-probesize=32",
        "--demuxer-lavf-probe-info=no",
        "--demuxer-max-bytes=4MiB",
        "--demuxer-max-back-bytes=0",
        "--untimed",
        "--video-latency-hacks=yes",
        "--video-sync=display-desync",
        "--interpolation=no",
        "--framedrop=vo",
        "--swapchain-depth=1",
        "--opengl-swapinterval=0",
        "--vd-lavc-threads=1",
        "--audio=no",
        "--sub=no",
        "--osc=no",
        "--osd-level=0",
        "--input-default-bindings=no",
        "--input-cursor=no",
        "--demuxer-lavf-format=h264",
        "-",
    ]


def build_mpv_ipc_socket_path() -> str:
    runtime_directory = os.environ.get("XDG_RUNTIME_DIR", "").strip()
    runtime_path = Path(runtime_directory) if runtime_directory else None
    base_directory = (
        runtime_path
        if runtime_path is not None and runtime_path.is_dir()
        else Path("/tmp")
    )
    file_name = f"remotedesk-mpv-{os.getpid()}-{uuid4().hex[:16]}.sock"
    socket_path = str(base_directory / file_name)
    if len(os.fsencode(socket_path)) >= 100:
        socket_path = str(Path("/tmp") / file_name)
    return socket_path


def parse_mpv_ipc_property_response(
    response: bytes,
    request_id: int,
) -> Any | None:
    for raw_line in response.splitlines():
        try:
            message = json.loads(raw_line.decode("utf-8", errors="strict"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            continue
        if (
            isinstance(message, dict)
            and message.get("request_id") == request_id
            and message.get("error") == "success"
        ):
            return message.get("data")
    return None


def query_mpv_ipc_property(
    ipc_socket_path: str,
    property_name: str,
    timeout_seconds: float = 0.2,
) -> Any | None:
    return query_mpv_ipc_properties(
        ipc_socket_path,
        (property_name,),
        timeout_seconds,
    ).get(property_name)


def query_mpv_ipc_properties(
    ipc_socket_path: str,
    property_names: tuple[str, ...],
    timeout_seconds: float = NATIVE_PRESENTER_IPC_QUERY_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    if not hasattr(socket, "AF_UNIX"):
        return {}
    names = tuple(dict.fromkeys(name for name in property_names if name))
    if not names:
        return {}
    request_names = {
        request_id: property_name
        for request_id, property_name in enumerate(names, start=1)
    }
    request = b"".join(
        json.dumps(
            {
                "command": ["get_property", property_name],
                "request_id": request_id,
            },
            separators=(",", ":"),
        ).encode("utf-8")
        + b"\n"
        for request_id, property_name in request_names.items()
    )
    response = bytearray()
    properties: dict[str, Any] = {}
    deadline = time.monotonic() + max(0.01, timeout_seconds)
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as ipc_socket:
            ipc_socket.settimeout(max(0.01, timeout_seconds))
            ipc_socket.connect(ipc_socket_path)
            ipc_socket.sendall(request)
            while len(response) < 64 * 1024:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                ipc_socket.settimeout(remaining)
                chunk = ipc_socket.recv(4096)
                if not chunk:
                    break
                response.extend(chunk)
                for raw_line in response.splitlines():
                    try:
                        message = json.loads(raw_line.decode("utf-8", errors="strict"))
                    except (UnicodeDecodeError, json.JSONDecodeError):
                        continue
                    if (
                        not isinstance(message, dict)
                        or message.get("error") != "success"
                    ):
                        continue
                    property_name = request_names.get(message.get("request_id"))
                    if property_name is not None:
                        properties[property_name] = message.get("data")
                if len(properties) == len(request_names):
                    break
    except (OSError, ValueError):
        return properties
    return properties


def set_mpv_ipc_properties(ipc_socket_path: str, properties: dict[str, Any],
                           timeout_seconds: float = 0.3) -> bool:
    """Bounded, acknowledged property changes; never run on the Tk thread."""
    if not properties or not hasattr(socket, "AF_UNIX"):
        return False
    pending = set(range(1, len(properties) + 1))
    request = b"".join(json.dumps({"command": ["set_property", name, value],
        "request_id": index}, separators=(",", ":")).encode("utf-8") + b"\n"
        for index, (name, value) in enumerate(properties.items(), 1))
    deadline = time.monotonic() + max(0.01, timeout_seconds)
    buffer = bytearray()
    received = 0
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as ipc:
            ipc.settimeout(max(0.01, timeout_seconds))
            ipc.connect(ipc_socket_path)
            ipc.sendall(request)
            while pending and received < 64 * 1024:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                ipc.settimeout(remaining)
                chunk = ipc.recv(4096)
                if not chunk:
                    return False
                received += len(chunk)
                buffer.extend(chunk)
                while b"\n" in buffer:
                    raw, _, rest = buffer.partition(b"\n")
                    buffer = bytearray(rest)
                    reply = json.loads(raw)
                    if isinstance(reply, dict) and reply.get("request_id") in pending:
                        if reply.get("error") != "success":
                            return False
                        pending.remove(reply["request_id"])
        return not pending
    except (OSError, ValueError, TypeError):
        return False


def parse_mpv_active_hardware_decoder(line: str) -> str | None:
    match = re.search(r"using hardware decoding\s*\(([^)]+)\)", line, re.IGNORECASE)
    return match.group(1).strip().lower() if match else None


def mpv_log_confirms_native_surface(
    line: str,
    backend: MpvNativeH264Backend,
) -> bool:
    normalized = line.strip().lower()
    if any(
        marker in normalized
        for marker in (
            "failed",
            "error",
            "unsupported",
            "unavailable",
            "could not",
            "cannot",
        )
    ):
        return False
    return any(token in normalized for token in backend.surface_tokens)


def mpv_log_reports_native_surface_failure(
    line: str,
    backend: MpvNativeH264Backend,
) -> bool:
    normalized = line.strip().lower()
    names_native_surface = (
        any(token in normalized for token in backend.surface_tokens)
        or backend.interop in normalized
    )
    return names_native_surface and any(
        marker in normalized
        for marker in (
            "failed",
            "error",
            "unsupported",
            "unavailable",
            "could not",
            "cannot",
        )
    )


def mpv_log_reports_decode_fallback(line: str) -> bool:
    normalized = line.strip().lower()
    return any(
        marker in normalized
        for marker in (
            "using software decoding",
            "hardware decoding of this stream failed",
            "failed to initialize a hardware decoder",
            "could not initialize a decoder",
        )
    )


def enqueue_native_h264_input(
    frames: deque[NativeH264InputFrame],
    frame: NativeH264InputFrame,
    max_queued_frames: int,
) -> NativeH264SubmitResult:
    normalized_max = max(0, int(max_queued_frames))
    if normalized_max == 0:
        return NativeH264SubmitResult(False, True)
    if frame.recovery:
        # Every recovery AU is independently decodable, so replace all older
        # pipe writes and keep the newest screen state.
        frames.clear()
        frames.append(frame)
        return NativeH264SubmitResult(True, False)
    if len(frames) >= normalized_max:
        # Dropping a prediction AU from the middle would corrupt all later
        # dependencies. Discard the whole pending chain and ask for recovery.
        frames.clear()
        return NativeH264SubmitResult(False, True)
    frames.append(frame)
    return NativeH264SubmitResult(True, False)


def _read_mpv_hwdec_help(mpv: str) -> str:
    try:
        completed = subprocess.run(
            [mpv, "--no-config", "--hwdec=help"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
            timeout=3,
        )
    except Exception:
        return ""
    if completed.returncode != 0:
        return ""
    return completed.stdout.decode("utf-8", errors="replace")


@functools.lru_cache(maxsize=4)
def probe_mpv_native_h264_backends(
    mpv: str,
) -> tuple[MpvNativeH264Backend, ...]:
    return select_mpv_native_h264_backends(
        parse_mpv_hwdec_help(_read_mpv_hwdec_help(mpv))
    )


class MpvNativeH264Presenter:
    def __init__(
        self,
        process: subprocess.Popen[bytes],
        backend: MpvNativeH264Backend,
        ipc_socket_path: str,
    ) -> None:
        self.process = process
        self.backend = backend
        self.ipc_socket_path = ipc_socket_path
        self.stop_event = threading.Event()
        self.first_submission_event = threading.Event()
        self.close_lock = threading.Lock()
        self.closed = False
        self.frame_condition = threading.Condition()
        self.state_lock = threading.Lock()
        self.pending_frames: deque[NativeH264InputFrame] = deque()
        self.first_submission_at: float | None = None
        self.decoder_confirmed = False
        self.surface_confirmed = False
        self.compatibility_preview_claimed = False
        self.upscale_lock = threading.Lock()
        self.experimental_upscaling = False
        self.original_scale_properties: dict[str, Any] | None = None
        self.activation_failure = ""
        self.error_tail = ""
        self.writer_thread = threading.Thread(
            target=self._write_loop,
            name="RemoteDeskMpvNativeIn",
            daemon=True,
        )
        self.log_thread = threading.Thread(
            target=self._read_log_loop,
            name="RemoteDeskMpvNativeLog",
            daemon=True,
        )
        self.ipc_thread = threading.Thread(
            target=self._monitor_ipc_loop,
            name="RemoteDeskMpvNativeIpc",
            daemon=True,
        )
        self.writer_thread.start()
        self.log_thread.start()
        self.ipc_thread.start()

    @classmethod
    def try_create(
        cls,
        window_id: int,
        backend: MpvNativeH264Backend,
        mpv: str | None = None,
    ) -> "MpvNativeH264Presenter | None":
        mpv = mpv or find_mpv()
        if not mpv:
            return None
        ipc_socket_path = build_mpv_ipc_socket_path()
        try:
            process = subprocess.Popen(
                build_mpv_native_h264_presenter_command(
                    mpv,
                    window_id,
                    backend,
                    ipc_socket_path,
                ),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                bufsize=0,
            )
            return cls(process, backend, ipc_socket_path)
        except Exception:
            try:
                Path(ipc_socket_path).unlink(missing_ok=True)
            except OSError:
                pass
            return None

    @property
    def is_running(self) -> bool:
        return not self.stop_event.is_set() and self.process.poll() is None

    @property
    def is_native_surface_active(self) -> bool:
        with self.state_lock:
            return (
                self.decoder_confirmed
                and self.surface_confirmed
                and not self.activation_failure
                and self.is_running
            )

    @property
    def failure_detail(self) -> str:
        with self.state_lock:
            detail = self.activation_failure or self.error_tail.strip()
        code = self.process.poll()
        if code is not None:
            prefix = f"mpv exited with code {code}"
            return prefix + (f": {detail}" if detail else "")
        return detail

    def set_experimental_upscaling(self, enabled: bool) -> bool:
        with self.upscale_lock:
            if not self.is_native_surface_active:
                return False
            if enabled == self.experimental_upscaling:
                return True
            names = ("scale", "scale-antiring", "cscale")
            if enabled and self.original_scale_properties is None:
                original = query_mpv_ipc_properties(self.ipc_socket_path, names)
                if any(name not in original for name in names):
                    return False  # Never modify a setting we cannot restore.
                self.original_scale_properties = original
            original = self.original_scale_properties
            if original is None:
                return False
            # An unset cscale inherits scale on some mpv versions. Freeze its
            # original choice so a luma upscaling experiment cannot also alter
            # chroma reconstruction during native-size/downscaled viewing.
            chroma = original["cscale"]
            if chroma in ("", "auto"):
                chroma = original["scale"]
            desired = {"cscale": chroma, "scale-antiring": 1.0, "scale": "catmull_rom"} if enabled else original
            applied = set_mpv_ipc_properties(self.ipc_socket_path, desired)
            observed = query_mpv_ipc_properties(self.ipc_socket_path, names) if applied else {}
            if applied and all(observed.get(name) == value for name, value in desired.items()):
                self.experimental_upscaling = enabled
                return True
            # Restore every property if a renderer/version accepts only part of
            # the change. If IPC itself has failed, the existing native-surface
            # recovery will replace this renderer with the unmodified path.
            restored = set_mpv_ipc_properties(self.ipc_socket_path, original)
            self.experimental_upscaling = False
            if not restored:
                with self.state_lock:
                    self.activation_failure = "新版放大设置无法恢复，切回原版呈现后端"
            return False

    def activation_timed_out(self, now: float | None = None) -> bool:
        with self.state_lock:
            first_submission_at = self.first_submission_at
            failed = bool(self.activation_failure)
            active = self.decoder_confirmed and self.surface_confirmed
        return (
            failed
            or (
                first_submission_at is not None
                and not active
                and (now if now is not None else time.monotonic()) - first_submission_at
                >= NATIVE_PRESENTER_ACTIVATION_TIMEOUT_SECONDS
            )
        )

    def claim_compatibility_preview(self) -> bool:
        with self.state_lock:
            if self.compatibility_preview_claimed:
                return False
            self.compatibility_preview_claimed = True
            return True

    def submit(self, encoded: bytes, recovery: bool) -> NativeH264SubmitResult:
        if not encoded or not self.is_running:
            return NativeH264SubmitResult(False, False)
        frame = NativeH264InputFrame(terminate_h264_access_unit(encoded), recovery)
        with self.frame_condition:
            result = enqueue_native_h264_input(
                self.pending_frames,
                frame,
                MAX_NATIVE_PRESENTER_H264_FRAMES,
            )
            if result.accepted:
                with self.state_lock:
                    if self.first_submission_at is None:
                        self.first_submission_at = time.monotonic()
                        self.first_submission_event.set()
                self.frame_condition.notify()
            return result

    def close(self) -> None:
        with self.close_lock:
            if self.closed:
                return
            self.closed = True
            self.stop_event.set()
        with self.frame_condition:
            self.pending_frames.clear()
            self.frame_condition.notify_all()
        try:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=0.4)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    try:
                        self.process.wait(timeout=0.4)
                    except subprocess.TimeoutExpired:
                        pass
        except OSError:
            pass
        try:
            if self.process.stdin is not None:
                self.process.stdin.close()
        except (BrokenPipeError, OSError, ValueError):
            pass
        for thread in (self.writer_thread, self.log_thread, self.ipc_thread):
            if thread.is_alive() and threading.current_thread() is not thread:
                thread.join(timeout=0.4)
        try:
            Path(self.ipc_socket_path).unlink(missing_ok=True)
        except OSError:
            pass

    def _record_log_line(self, line: str) -> None:
        normalized = line.strip()
        if not normalized:
            return
        active_decoder = parse_mpv_active_hardware_decoder(normalized)
        reports_surface_failure = mpv_log_reports_native_surface_failure(
            normalized,
            self.backend,
        )
        reports_fallback = mpv_log_reports_decode_fallback(normalized)
        with self.state_lock:
            self.error_tail = (self.error_tail + normalized + "\n")[-DECODER_ERROR_TAIL_LENGTH:]
            if active_decoder is not None:
                if (
                    active_decoder != self.backend.hwdec
                    or active_decoder.endswith("-copy")
                ):
                    self.activation_failure = (
                        f"mpv activated {active_decoder}, expected non-copy {self.backend.hwdec}"
                    )
            if reports_surface_failure:
                self.activation_failure = (
                    f"mpv {self.backend.interop} surface interop failed"
                )
            if reports_fallback:
                self.activation_failure = "mpv fell back from hardware decoding"

    def _write_loop(self) -> None:
        while not self.stop_event.is_set():
            with self.frame_condition:
                while not self.pending_frames and not self.stop_event.is_set():
                    self.frame_condition.wait(timeout=0.25)
                if self.stop_event.is_set():
                    break
                frame = self.pending_frames.popleft()
            try:
                if self.process.stdin is None:
                    raise BrokenPipeError("mpv stdin is unavailable")
                self.process.stdin.write(frame.encoded)
                self.process.stdin.flush()
            except (BrokenPipeError, OSError, ValueError) as ex:
                with self.state_lock:
                    self.activation_failure = f"mpv input stopped: {ex}"
                self.stop_event.set()
                with self.frame_condition:
                    self.pending_frames.clear()
                    self.frame_condition.notify_all()
                break

    def _monitor_ipc_loop(self) -> None:
        while not self.stop_event.is_set():
            if self.first_submission_event.wait(timeout=0.1):
                break
        if self.stop_event.is_set():
            return

        with self.state_lock:
            first_submission_at = self.first_submission_at or time.monotonic()
        deadline = first_submission_at + NATIVE_PRESENTER_ACTIVATION_TIMEOUT_SECONDS
        last_hwdec = ""
        last_interop = ""
        while not self.stop_event.is_set() and time.monotonic() < deadline:
            properties = query_mpv_ipc_properties(
                self.ipc_socket_path,
                ("hwdec-current", "hwdec-interop"),
            )
            hwdec_value = properties.get("hwdec-current")
            interop_value = properties.get("hwdec-interop")
            last_hwdec = str(hwdec_value or "").strip().lower()
            last_interop = str(interop_value or "").strip().lower()
            with self.state_lock:
                if last_hwdec and last_hwdec not in ("no", "auto"):
                    if (
                        last_hwdec == self.backend.hwdec
                        and not last_hwdec.endswith("-copy")
                    ):
                        self.decoder_confirmed = True
                    else:
                        self.activation_failure = (
                            f"mpv IPC reports hwdec-current={last_hwdec}, "
                            f"expected non-copy {self.backend.hwdec}"
                        )
                if last_interop and last_interop not in ("no", "auto"):
                    if last_interop == self.backend.interop:
                        self.surface_confirmed = True
                    else:
                        self.activation_failure = (
                            f"mpv IPC reports hwdec-interop={last_interop}, "
                            f"expected {self.backend.interop}"
                        )
                active = (
                    self.decoder_confirmed
                    and self.surface_confirmed
                    and not self.activation_failure
                )
                failed = bool(self.activation_failure)
            if active or failed:
                return
            self.stop_event.wait(0.02)

        if not self.stop_event.is_set():
            with self.state_lock:
                if not self.decoder_confirmed or not self.surface_confirmed:
                    self.activation_failure = (
                        "mpv IPC did not confirm native surface activation "
                        f"(hwdec-current={last_hwdec or 'unavailable'}, "
                        f"hwdec-interop={last_interop or 'unavailable'})"
                    )

    def _read_log_loop(self) -> None:
        if self.process.stdout is None:
            return
        try:
            while not self.stop_event.is_set():
                line = self.process.stdout.readline()
                if not line:
                    break
                self._record_log_line(line.decode("utf-8", errors="replace"))
        except (OSError, ValueError):
            pass
        finally:
            if self.process.poll() is not None:
                self.stop_event.set()
            with self.frame_condition:
                self.frame_condition.notify_all()


@dataclass(frozen=True)
class FfmpegH264DecoderBackend:
    key: str
    label: str
    decoder_name: str
    hardware: bool
    hwaccel: str | None = None
    hwaccel_output_format: str | None = None
    download_filter: str | None = None
    decoder_options: tuple[str, ...] = ()

    @property
    def diagnostic(self) -> str:
        if self.hardware:
            return f"{self.label} 硬解（GPU→CPU 回读，MJPEG/Tk 显示）"
        return f"{self.label}（软件解码，MJPEG/Tk 显示）"


@dataclass(frozen=True)
class CorrelatedH264Jpeg:
    correlation: Any
    jpeg: bytes


@dataclass(frozen=True)
class H264DecodeFrameToken:
    submission_id: int
    sequence: int
    width: int
    height: int
    received_at: float


def parse_ffmpeg_hwaccels(output: str) -> frozenset[str]:
    names: set[str] = set()
    for line in output.splitlines():
        name = line.strip().lower()
        if re.fullmatch(r"[a-z0-9_]+", name):
            names.add(name)
    return frozenset(names)


def parse_ffmpeg_decoders(output: str) -> frozenset[str]:
    names: set[str] = set()
    for line in output.splitlines():
        match = re.match(r"^\s*[A-Z.]{6}\s+([a-zA-Z0-9_]+)(?:\s|$)", line)
        if match:
            names.add(match.group(1).lower())
    return frozenset(names)


def select_ffmpeg_h264_decoder_backends(
    hwaccels: frozenset[str] | set[str],
    decoders: frozenset[str] | set[str],
) -> tuple[FfmpegH264DecoderBackend, ...]:
    available_hwaccels = {name.lower() for name in hwaccels}
    available_decoders = {name.lower() for name in decoders}
    backends: list[FfmpegH264DecoderBackend] = []

    # Jetson FFmpeg can list desktop CUVID although that API cannot open its
    # integrated decoder. Prefer its explicitly compiled NVIDIA V4L2 backend.
    # This returns CPU-visible frames; it is not a zero-copy native Surface.
    if "h264_nvv4l2dec" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "jetson-nvv4l2",
                "Jetson NVIDIA V4L2",
                "h264_nvv4l2dec",
                True,
            )
        )

    if "cuda" in available_hwaccels:
        cuda_decoder = next(
            (
                name
                for name in ("h264_cuvid", "h264_nvdec", "h264")
                if name in available_decoders
            ),
            None,
        )
        if cuda_decoder is not None:
            backends.append(
                FfmpegH264DecoderBackend(
                    "cuda-nvdec",
                    "CUDA/NVDEC",
                    cuda_decoder,
                    True,
                    "cuda",
                    "cuda",
                    "hwdownload,format=nv12",
                )
            )

    if "vaapi" in available_hwaccels and "h264" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "vaapi",
                "VAAPI",
                "h264",
                True,
                "vaapi",
                "vaapi",
                "hwdownload,format=nv12",
            )
        )

    if "qsv" in available_hwaccels:
        qsv_decoder = next(
            (name for name in ("h264_qsv", "h264") if name in available_decoders),
            None,
        )
        if qsv_decoder is not None:
            backends.append(
                FfmpegH264DecoderBackend(
                    "qsv",
                    "Intel QSV",
                    qsv_decoder,
                    True,
                    "qsv",
                    "qsv",
                    "hwdownload,format=nv12",
                    ("-async_depth", "1"),
                )
            )

    if "h264_v4l2m2m" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "v4l2m2m",
                "V4L2 M2M",
                "h264_v4l2m2m",
                True,
            )
        )

    if "drm" in available_hwaccels and "h264" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "drm",
                "DRM PRIME",
                "h264",
                True,
                "drm",
                "drm_prime",
                "hwdownload,format=nv12",
            )
        )

    if "vdpau" in available_hwaccels and "h264" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "vdpau",
                "VDPAU",
                "h264",
                True,
                "vdpau",
                "vdpau",
                "hwdownload,format=nv12",
            )
        )

    if "h264" in available_decoders:
        backends.append(
            FfmpegH264DecoderBackend(
                "software",
                "FFmpeg H.264",
                "h264",
                False,
            )
        )

    return tuple(backends)


def choose_next_h264_decoder_backend(
    candidates: tuple[FfmpegH264DecoderBackend, ...],
    failed_backend_keys: set[str] | frozenset[str],
) -> FfmpegH264DecoderBackend | None:
    return next(
        (candidate for candidate in candidates if candidate.key not in failed_backend_keys),
        None,
    )


def h264_decoder_miss_threshold(backend: FfmpegH264DecoderBackend) -> int:
    return (
        H264_HARDWARE_DECODE_MISS_ROTATION_THRESHOLD
        if backend.hardware
        else H264_DECODE_MISS_FALLBACK_THRESHOLD
    )


def h264_correlated_submission_lag(backend: FfmpegH264DecoderBackend) -> int:
    # Jetson's V4L2 capture plane retains three submissions for some real
    # Windows H.264 streams. The generic two-AU freshness limit rejected the
    # first valid output and needlessly disabled hardware decoding. Admit its
    # fixed pipeline depth, but keep the four-correlation memory bound and
    # reject any further backlog; other decoders retain the stricter limit.
    return 3 if backend.key == "jetson-nvv4l2" else MAX_H264_CORRELATED_SUBMISSION_LAG


def build_ffmpeg_h264_decoder_command(
    ffmpeg: str,
    backend: FfmpegH264DecoderBackend,
) -> list[str]:
    command = [
        ffmpeg,
        "-hide_banner",
        "-loglevel",
        "error",
        "-max_alloc",
        str(128 * 1024 * 1024),
        "-fflags",
        # nobuffer discards packets consumed while probing the input. Losing
        # the first IDR starves the bounded correlation queue until another
        # IDR arrives (and also shifts GOP1 input/output attribution).
        # Keep the probe/thread/queue limits below, but retain that first AU.
        "discardcorrupt",
        "-flags",
        "low_delay",
        "-flags2",
        "fast",
        "-probesize",
        "32",
        "-analyzeduration",
        "0",
        "-fpsprobesize",
        "0",
        "-thread_type",
        "slice",
        "-threads",
        "1",
    ]
    if backend.hwaccel is not None:
        command.extend(["-hwaccel", backend.hwaccel])
    if backend.hwaccel_output_format is not None:
        command.extend(["-hwaccel_output_format", backend.hwaccel_output_format])
    command.extend(
        [
            "-c:v",
            backend.decoder_name,
            *backend.decoder_options,
            "-f",
            "h264",
            "-i",
            "pipe:0",
            "-an",
            "-sn",
            "-dn",
            "-vsync",
            "0",
        ]
    )
    if backend.key == "jetson-nvv4l2":
        # Raw Annex-B has no container timestamps. Some Jetson decoder outputs
        # repeat PTS for Android streams, causing the intermediate MJPEG encoder
        # to exit with "Invalid pts <= last". Assign one monotonic tick per
        # decoded picture for this local, untimed JPEG pipe. Passthrough vsync
        # preserves exactly one output per input correlation; this does not
        # change network FPS, presentation clocks or the H.264 stream.
        filters = [backend.download_filter] if backend.download_filter else []
        filters.extend(["settb=expr=1/60", "setpts=N"])
        command.extend(["-vf", ",".join(filters), "-enc_time_base", "1:60"])
    elif backend.download_filter is not None:
        command.extend(["-vf", backend.download_filter])
    command.extend(
        [
            "-f",
            "image2pipe",
            "-c:v",
            "mjpeg",
            "-threads",
            "1",
            "-pix_fmt",
            "yuvj420p",
            "-q:v",
            "4",
            "-flush_packets",
            "1",
            "pipe:1",
        ]
    )
    return command


def _read_ffmpeg_listing(ffmpeg: str, option: str) -> str:
    try:
        completed = subprocess.run(
            [ffmpeg, "-hide_banner", option],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
            timeout=3,
        )
    except Exception:
        return ""
    if completed.returncode != 0:
        return ""
    return completed.stdout.decode("utf-8", errors="replace")


@functools.lru_cache(maxsize=4)
def probe_ffmpeg_h264_decoder_backends(
    ffmpeg: str,
) -> tuple[FfmpegH264DecoderBackend, ...]:
    hwaccels = parse_ffmpeg_hwaccels(_read_ffmpeg_listing(ffmpeg, "-hwaccels"))
    decoders = parse_ffmpeg_decoders(_read_ffmpeg_listing(ffmpeg, "-decoders"))
    return select_ffmpeg_h264_decoder_backends(hwaccels, decoders)


class H264AnnexBDecoder:
    def __init__(
        self,
        process: subprocess.Popen[bytes],
        backend: FfmpegH264DecoderBackend,
    ) -> None:
        self.process = process
        self.backend = backend
        self.stop_event = threading.Event()
        self.write_lock = threading.Lock()
        self.frame_condition = threading.Condition()
        self.error_lock = threading.Lock()
        self.submitted_correlations: deque[Any] = deque()
        self.pending_jpegs: deque[CorrelatedH264Jpeg] = deque()
        self.correlation_overflowed = False
        self.error_tail = ""
        self.stdout_thread = threading.Thread(target=self._read_jpeg_loop, name="RemoteDeskH264DecoderOut", daemon=True)
        self.stderr_thread = threading.Thread(target=self._drain_error_loop, name="RemoteDeskH264DecoderErr", daemon=True)
        self.stdout_thread.start()
        self.stderr_thread.start()

    @classmethod
    def try_create(
        cls,
        backend: FfmpegH264DecoderBackend | None = None,
        ffmpeg: str | None = None,
    ) -> "H264AnnexBDecoder | None":
        ffmpeg = ffmpeg or find_ffmpeg()
        if not ffmpeg:
            return None

        if backend is None:
            candidates = probe_ffmpeg_h264_decoder_backends(ffmpeg)
            backend = choose_next_h264_decoder_backend(candidates, frozenset())
        if backend is None:
            return None

        command = build_ffmpeg_h264_decoder_command(ffmpeg, backend)
        try:
            process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
            )
            return cls(process, backend)
        except Exception:
            return None

    @property
    def is_running(self) -> bool:
        return not self.stop_event.is_set() and self.process.poll() is None

    @property
    def failure_detail(self) -> str:
        with self.error_lock:
            tail = self.error_tail.strip()
        code = self.process.poll()
        if code is None:
            return tail
        return f"ffmpeg exited with code {code}" + (f": {tail}" if tail else "")

    @property
    def outstanding_correlation_count(self) -> int:
        with self.frame_condition:
            return len(self.submitted_correlations) + len(self.pending_jpegs)

    def decode(self, encoded: bytes, timeout_seconds: float = H264_DECODE_TIMEOUT_SECONDS) -> bytes | None:
        decoded = self.decode_correlated(encoded, object(), timeout_seconds)
        return decoded.jpeg if decoded is not None else None

    def decode_correlated(
        self,
        encoded: bytes,
        correlation: Any,
        timeout_seconds: float = H264_DECODE_TIMEOUT_SECONDS,
    ) -> CorrelatedH264Jpeg | None:
        if not encoded or not self.is_running or self.process.stdin is None:
            return None
        # A completed JPEG still occupies a correlation slot until consumed.
        # Drain one ready output before reserving at capacity; otherwise a
        # healthy asynchronous decoder is rejected merely because its output
        # arrived between calls. Keep FIFO attribution and the same hard cap.
        ready = None
        with self.frame_condition:
            if (len(self.submitted_correlations) + len(self.pending_jpegs)
                    >= MAX_H264_DECODER_OUTSTANDING_CORRELATIONS and self.pending_jpegs):
                ready = self.pending_jpegs.popleft()
        if not self._try_reserve_correlation(correlation):
            return None
        try:
            with self.write_lock:
                self.process.stdin.write(terminate_h264_access_unit(encoded))
                self.process.stdin.flush()
        except (BrokenPipeError, OSError, ValueError):
            with self.frame_condition:
                try:
                    self.submitted_correlations.remove(correlation)
                except ValueError:
                    pass
            return None
        return ready if ready is not None else self._wait_for_next_jpeg(timeout_seconds)

    def _try_reserve_correlation(self, correlation: Any) -> bool:
        with self.frame_condition:
            outstanding = (
                len(self.submitted_correlations) + len(self.pending_jpegs)
            )
            if outstanding >= MAX_H264_DECODER_OUTSTANDING_CORRELATIONS:
                self.correlation_overflowed = True
                return False
            self.submitted_correlations.append(correlation)
            return True

    def close(self) -> None:
        if self.stop_event.is_set():
            return
        self.stop_event.set()
        try:
            if self.process.stdin is not None:
                self.process.stdin.close()
        except (BrokenPipeError, OSError, ValueError):
            pass

        try:
            if self.process.poll() is None:
                self.process.terminate()
                try:
                    self.process.wait(timeout=0.4)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    try:
                        self.process.wait(timeout=0.4)
                    except subprocess.TimeoutExpired:
                        pass
        except OSError:
            pass

        with self.frame_condition:
            self.frame_condition.notify_all()
        for thread in (self.stdout_thread, self.stderr_thread):
            if thread.is_alive() and threading.current_thread() is not thread:
                thread.join(timeout=0.4)

    def _wait_for_next_jpeg(self, timeout_seconds: float) -> CorrelatedH264Jpeg | None:
        deadline = time.monotonic() + timeout_seconds
        with self.frame_condition:
            while not self.stop_event.is_set():
                if self.pending_jpegs:
                    return self.pending_jpegs.popleft()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self.frame_condition.wait(timeout=remaining)
        return None

    def _publish_jpeg(self, jpeg: bytes) -> None:
        if not jpeg.startswith(b"\xff\xd8"):
            return
        with self.frame_condition:
            if not self.submitted_correlations:
                return
            correlation = self.submitted_correlations.popleft()
            self.pending_jpegs.append(CorrelatedH264Jpeg(correlation, jpeg))
            self.frame_condition.notify_all()

    def _append_error(self, data: bytes) -> None:
        if not data:
            return
        text = data.decode("utf-8", errors="replace")
        with self.error_lock:
            self.error_tail = (self.error_tail + text)[-DECODER_ERROR_TAIL_LENGTH:]

    def _read_jpeg_loop(self) -> None:
        if self.process.stdout is None:
            return
        frame_bytes = bytearray()
        try:
            output_fd = self.process.stdout.fileno()
            while not self.stop_event.is_set():
                chunk = os.read(output_fd, 16 * 1024)
                if not chunk:
                    break
                frame_bytes.extend(chunk)
                for jpeg in extract_decoder_jpeg_frames(frame_bytes):
                    self._publish_jpeg(jpeg)
        except (OSError, ValueError):
            pass
        finally:
            with self.frame_condition:
                self.frame_condition.notify_all()

    def _drain_error_loop(self) -> None:
        if self.process.stderr is None:
            return
        try:
            error_fd = self.process.stderr.fileno()
            while not self.stop_event.is_set():
                data = os.read(error_fd, 1024)
                if not data:
                    break
                self._append_error(data)
        except (OSError, ValueError):
            pass


def safe_path_size(
    path: Path,
    cancel_event: threading.Event | None = None,
) -> int:
    return safe_transfer_path_size(path, cancel_event=cancel_event)


def create_directory_archive(directory: Path, source_size: int | None = None) -> Path:
    return create_safe_directory_archive(directory, source_size)


def create_directory_archive_name(directory: Path) -> str:
    name = directory.name or "folder"
    if not name.lower().endswith(".zip"):
        name += ".zip"
    return sanitize_file_name(name)


@dataclass(frozen=True)
class FileTransferPreviewItem:
    kind: str
    source_path: str
    transfer_name: str
    size_bytes: int
    destination_path: str


@dataclass(frozen=True)
class ViewerFilePreviewResult:
    token: int
    viewer: Any
    normalized_paths: tuple[str, ...]
    items: tuple[FileTransferPreviewItem, ...]
    note: str
    title: str
    action_text: str
    queued_status: str
    error: str = ""


def create_file_transfer_preview(
    paths: list[str],
    cancel_event: threading.Event | None = None,
) -> tuple[list[str], list[FileTransferPreviewItem], str]:
    normalized_paths: list[str] = []
    items: list[FileTransferPreviewItem] = []
    seen: set[str] = set()
    skipped_missing = 0
    has_directory = False
    for raw_path in paths:
        if cancel_event is not None and cancel_event.is_set():
            raise TransferCancelledError("file preview was cancelled")
        if not raw_path:
            continue
        path = Path(raw_path).expanduser()
        try:
            key = str(path.resolve())
        except OSError:
            key = str(path)
        if key in seen:
            continue
        seen.add(key)

        if path.is_file():
            try:
                size_bytes = max(0, path.stat().st_size)
            except OSError:
                skipped_missing += 1
                continue
            if size_bytes > MAX_FILE_TRANSFER_BYTES:
                raise ProtocolError(f"文件超过 RemoteDesk 传输上限：{size_bytes} bytes")
            transfer_name = sanitize_file_name(path.name)
            kind = "文件"
        elif path.is_dir():
            size_bytes = safe_path_size(path, cancel_event=cancel_event)
            if size_bytes > MAX_FILE_TRANSFER_BYTES:
                raise ProtocolError(f"文件夹超过 RemoteDesk 传输上限：{size_bytes} bytes")
            transfer_name = create_directory_archive_name(path)
            kind = "文件夹"
            has_directory = True
        else:
            skipped_missing += 1
            continue

        normalized_paths.append(str(path))
        items.append(
            FileTransferPreviewItem(
                kind,
                str(path),
                transfer_name,
                size_bytes,
                format_remote_receive_destination(transfer_name),
            )
        )

    note_parts: list[str] = []
    if has_directory:
        note_parts.append("文件夹会先打包为 zip 后传输，大小按原始文件夹内容统计。")
    if skipped_missing:
        note_parts.append(f"已跳过 {skipped_missing} 个不存在或不可访问的项目。")
    return normalized_paths, items, "\n".join(note_parts)


def format_remote_receive_destination(transfer_name: str, directory: str = "") -> str:
    if not directory:
        return f"位置未确认：远端未提供完整接收目录/{transfer_name}{FILE_TRANSFER_RENAME_SUFFIX}"
    separator = "\\" if "\\" in directory and not directory.startswith("/") else "/"
    return directory.rstrip("/\\") + separator + transfer_name + FILE_TRANSFER_RENAME_SUFFIX


def format_transfer_bytes(bytes_count: int) -> str:
    units = ("B", "KB", "MB", "GB")
    value = float(max(0, bytes_count))
    unit_index = 0
    while value >= 1024 and unit_index < len(units) - 1:
        value /= 1024
        unit_index += 1
    return f"{int(value)} {units[unit_index]}" if unit_index == 0 else f"{value:.1f} {units[unit_index]}"


class SessionRejectedError(ConnectionRefusedError):
    """Authenticated host ended this logical session intentionally."""


@dataclass
class ViewerClipboardRequest:
    read: bool
    baseline: str | None
    deadline: float = field(default_factory=lambda: time.monotonic() + 8.0)
    completed: threading.Event = field(default_factory=threading.Event)
    success: bool = False
    text: str = ""


@dataclass
class ViewerFileReceipt:
    transfer_id: str
    completed: threading.Event = field(default_factory=threading.Event)
    success: bool = False
    message: str = ""


class ViewerConnection:
    def __init__(
        self,
        host: str,
        port: int,
        password: str,
        events: "queue.Queue[tuple[str, Any]]",
        generation: int,
        relay_options: relay.RelayOptions | None = None,
    ) -> None:
        self.host = host
        self.port = port
        self.password = password
        self.events = events
        self.generation = generation
        self.relay_options = relay_options
        self.stop_event = threading.Event()
        self.write_lock = threading.Lock()
        self.file_transfer_lock = threading.Lock()
        self.file_receipt_lock = threading.Lock()
        self.file_receipt: ViewerFileReceipt | None = None
        self.file_receipt_timeout = 120.0
        self.file_location_lock = threading.Lock()
        self.file_location_requests: dict[str, tuple[threading.Event, dict[str, Any]]] = {}
        self.clipboard_lock = threading.Lock()
        self.clipboard_pending: ViewerClipboardRequest | None = None
        self.clipboard_latest: ViewerClipboardRequest | None = None
        self.sock: socket.socket | None = None
        self.session: Any | None = None
        self.thread = threading.Thread(target=self._run, name="RemoteDeskViewer", daemon=True)
        self.decoder_thread = threading.Thread(target=self._decode_frames, name="RemoteDeskFrameDecoder", daemon=True)
        self.input_thread = threading.Thread(target=self._send_inputs, name="RemoteDeskInputSender", daemon=True)
        self.capture_target_thread = threading.Thread(
            target=self._send_capture_target_selections,
            name="RemoteDeskCaptureTargetSender",
            daemon=True,
        )
        self.heartbeat_thread = threading.Thread(
            target=self._heartbeat_loop,
            name="RemoteDeskViewerHeartbeat",
            daemon=True,
        )
        self.liveness_thread = threading.Thread(
            target=self._liveness_loop,
            name="RemoteDeskViewerLiveness",
            daemon=True,
        )
        self.heartbeat_lock = threading.Lock()
        self.heartbeat_active = False
        self.last_ping_sent_at = 0.0
        self.last_message_received_at = 0.0
        self.awaiting_pong_since: float | None = None
        self._outgoing_file_transfer: tuple[object, Any, Any] | None = None
        self._outgoing_file_drain: tuple[tuple[object, Any, Any], float] | None = None
        self.frame_condition = threading.Condition()
        self.pending_frame: tuple[int, int, int, int, int, bytes, float] | None = None
        self.pending_h264_frames: deque[tuple[int, int, int, int, int, bytes, float]] = deque()
        self.frame_sequence = 0
        self.latest_frame_sequence = -1
        self.received_frame_encoding = 0
        self.frame_stream_epoch = 0
        self.input_condition = threading.Condition()
        self.pending_inputs: deque[tuple[int, bytes]] = deque()
        self.input_send_active = False
        self.capture_target_condition = threading.Condition()
        self.pending_capture_target_id: str | None = None
        self.active_capture_target_id: str | None = None
        self.missing_decoder_reported = False
        self.supported_video_codecs = VIDEO_CODEC_JPEG
        self.ffmpeg_path: str | None = None
        self.mpv_path: str | None = None
        self.h264_decoder_candidates: tuple[FfmpegH264DecoderBackend, ...] = ()
        self.failed_h264_decoder_backends: set[str] = set()
        self.h264_decoder_lock = threading.Lock()
        self.h264_decoder: H264AnnexBDecoder | None = None
        self.reported_h264_decoder_backend: str | None = None
        self.native_presenter_window_id = 0
        self.native_presenter_windowing_system = ""
        self.native_presenter_display = ""
        self.native_h264_presenter_candidates: tuple[MpvNativeH264Backend, ...] = ()
        self.failed_native_h264_presenter_backends: set[str] = set()
        self.native_h264_presenter_lock = threading.Lock()
        self.native_h264_presenter: MpvNativeH264Presenter | None = None
        self.reported_native_h264_presenter_backend: str | None = None
        self.native_h264_presenter_exhausted_reported = False
        self.advertised_viewer_capabilities = 0
        self.h264_decode_misses = 0
        self.next_h264_submission_id = 0
        self.latest_h264_submission_id = -1
        self.h264_fallback_requested = False
        self.waiting_for_h264_recovery = True
        self.reset_h264_decoder_on_recovery = False
        self.last_key_frame_request_at = 0.0
        self.last_rendered_encoding = 0
        self.remote_width = 0
        self.remote_height = 0
        self.remote_capabilities = 0
        self.capture_target_list_received = False
        self.capture_targets: tuple[ViewerCaptureTarget, ...] = ()
        self.selected_capture_target: ViewerCaptureTarget | None = None
        self.display_size = (DISPLAY_DEFAULT_WIDTH, DISPLAY_DEFAULT_HEIGHT)
        self.display_active = True

    def _put_event(self, event: str, value: Any) -> None:
        put_viewer_event(self.events, event, self.generation, value)

    def start(self) -> None:
        self.decoder_thread.start()
        self.input_thread.start()
        self.capture_target_thread.start()
        self.heartbeat_thread.start()
        self.liveness_thread.start()
        self.thread.start()

    def set_display_size(self, width: int, height: int) -> None:
        normalized_size = normalize_viewer_display_size(width, height)
        self.display_active = normalized_size is not None
        if normalized_size is None:
            return
        self.display_size = normalized_size

    def set_native_presenter_target(
        self,
        window_id: int,
        windowing_system: str,
        display: str,
    ) -> None:
        self.native_presenter_window_id = max(0, int(window_id))
        self.native_presenter_windowing_system = str(windowing_system or "")
        self.native_presenter_display = str(display or "")

    def set_experimental_upscaling(self, enabled: bool) -> None:
        with self.native_h264_presenter_lock:
            presenter = self.native_h264_presenter
        success = presenter is not None and presenter.set_experimental_upscaling(enabled)
        with self.native_h264_presenter_lock:
            current = self.native_h264_presenter is presenter
        active = bool(success and enabled and current)
        self._put_event("viewer_upscaling", active)
        self._put_event("viewer_status", (
            "新版放大（实验）已开启，仅 GPU 放大时生效；传输分辨率与输入坐标不变。" if active else
            "已恢复原版放大。" if success else
            "新版放大未启用：需要已激活的 mpv 原生 GPU 画面；JPEG / 兼容显示保持原版。"))

    def close(self) -> None:
        self._signal_stop_and_release_decoder()
        current_thread = threading.current_thread()
        called_from_worker = current_thread in (
            self.thread,
            self.decoder_thread,
            self.input_thread,
            self.capture_target_thread,
            self.heartbeat_thread,
            self.liveness_thread,
        )
        if self.thread.is_alive() and not called_from_worker:
            self.thread.join(timeout=1.0)
        self._signal_stop_and_release_decoder()
        if self.decoder_thread.is_alive() and current_thread is not self.decoder_thread:
            self.decoder_thread.join(timeout=1.0)
        if self.input_thread.is_alive() and current_thread is not self.input_thread:
            self.input_thread.join(timeout=1.0)
        if (
            self.capture_target_thread.is_alive()
            and current_thread is not self.capture_target_thread
        ):
            self.capture_target_thread.join(timeout=1.0)
        if self.heartbeat_thread.is_alive() and current_thread is not self.heartbeat_thread:
            self.heartbeat_thread.join(timeout=1.0)
        if self.liveness_thread.is_alive() and current_thread is not self.liveness_thread:
            self.liveness_thread.join(timeout=1.0)

    def request_close(self) -> None:
        """Promptly interrupt socket I/O; heavier teardown may run elsewhere."""

        self._interrupt_transport()

    def send_input(self, kind: int, button: int = MOUSE_NONE, x: int = 0, y: int = 0, data: int = 0) -> bool:
        if self.session is None or self.sock is None:
            return False
        payload = encode_input(kind, button, x, y, data)
        with self.input_condition:
            queued = enqueue_input_payload(self.pending_inputs, (kind, payload), INPUT_QUEUE_LIMIT)
            if queued:
                self.input_condition.notify()
            return queued

    def flush_pending_inputs(self, timeout_seconds: float = 0.2) -> bool:
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        with self.input_condition:
            while (
                self.pending_inputs or self.input_send_active
            ) and not self.stop_event.is_set():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self.input_condition.wait(timeout=remaining)
            return not self.pending_inputs and not self.input_send_active

    def send_text(self, text: str) -> int:
        sent = 0
        for ch in text:
            if sent >= MAX_TEXT_INPUT_CODEPOINTS:
                break
            codepoint = ord(ch)
            if is_supported_text_codepoint(codepoint):
                if self.send_input(INPUT_TEXT, data=codepoint):
                    sent += 1
        return sent

    def request_clipboard(self, *, read: bool, text: str = "", baseline: str | None = None,
                          paste: bool = False, after_copy: bool = False, paste_shift: bool = False) -> bool:
        if self.stop_event.is_set() or self.sock is None or self.session is None:
            return False
        if not self.remote_capabilities & CAPABILITY_CLIPBOARD_TEXT:
            self._put_event("viewer_status", "远端未提供文本剪贴板功能。")
            return False
        if not read and not text:
            self._put_event("viewer_status", "本机剪贴板没有文字，未修改远端。")
            return False
        try:
            payload = bytes([CONTROL_CLIPBOARD_GET_TEXT]) if read else encode_clipboard_set_text(text)
        except (ProtocolError, ValueError, UnicodeError):
            self._put_event("viewer_status", "剪贴板文字过长或编码无效，未截断或发送。请分段复制。")
            return False
        with self.clipboard_lock:
            if self.clipboard_pending is not None:
                self._put_event("viewer_status", "上一项剪贴板操作仍在等待远端，请稍后重试；长时间无响应请重连。")
                return False
            # Manual relay transfers may wait across a slow in-flight frame.
            # Keep automatic paste short-lived so it cannot unexpectedly target
            # another input field much later. Read replies retain local fences.
            timeout = 30.0 if getattr(self, "relay_options", None) is not None and not paste else 8.0
            request = ViewerClipboardRequest(read, baseline, deadline=time.monotonic() + timeout)
            self.clipboard_pending = self.clipboard_latest = request
        threading.Thread(target=self._exchange_clipboard,
                         args=(request, payload, paste, after_copy, paste_shift),
                         name="RemoteDeskViewerClipboard", daemon=True).start()
        return True

    def _exchange_clipboard(self, request: ViewerClipboardRequest, payload: bytes,
                            paste: bool, after_copy: bool, paste_shift: bool) -> None:
        sent = False
        try:
            if not self.flush_pending_inputs(1.5):
                self._put_event("viewer_status", "输入队列仍忙，未执行剪贴板操作，请重试。")
                return
            if after_copy and self.stop_event.wait(0.55):
                return
            if self.stop_event.is_set():
                return
            # Mark before writing: an incomplete write may still reach the peer.
            sent = True
            self._send_control(payload)
            if not request.completed.is_set():
                self._put_event("viewer_status", "正在等待远端剪贴板确认…")
            while not request.completed.wait(0.1):
                if self.stop_event.is_set():
                    return
                if time.monotonic() >= request.deadline:
                    self._put_event("viewer_status", "等待远端剪贴板超时，未覆盖本机或触发粘贴；可重新连接后重试。")
                    return
            if self.stop_event.is_set() or time.monotonic() >= request.deadline or not request.success:
                return
            if request.read:
                if request.text:
                    self._put_event("viewer_clipboard_text", (request, request.text))
                else:
                    self._put_event("viewer_status", "远端没有可读取的文字，本机剪贴板保持不变。")
            elif paste and self.remote_capabilities & CAPABILITY_INPUT_CONTROL:
                if (getattr(self, "remote_device_info", {}).get("platform", "").lower() == "android" and
                        not self.remote_capabilities & CAPABILITY_CLIPBOARD_PASTE_SHORTCUT):
                    self._put_event("viewer_status", "文字已写入远端剪贴板；此旧版 Android 请长按输入框粘贴，或更新远端后使用快捷粘贴。")
                    return
                keys = [0x11, 0x10, 0x56] if paste_shift else [0x11, 0x56]
                commands = [(INPUT_KEY_DOWN, encode_input(INPUT_KEY_DOWN, data=key)) for key in keys]
                commands += [(INPUT_KEY_UP, encode_input(INPUT_KEY_UP, data=key)) for key in reversed(keys)]
                with self.input_condition:
                    if self.stop_event.is_set() or len(self.pending_inputs) + len(commands) > INPUT_QUEUE_LIMIT:
                        self._put_event("viewer_status", "剪贴板已写入，但输入队列忙，未粘贴；请重试。")
                        return
                    self.pending_inputs.extend(commands)
                    self.input_condition.notify()
                self._put_event("viewer_status", "已写入远端剪贴板，并请求在当前输入框粘贴。")
            else:
                self._put_event("viewer_status", "已写入远端文本剪贴板。")
        except Exception:
            if not self.stop_event.is_set():
                self._put_event("viewer_status", "剪贴板传输失败，未触发粘贴；请检查连接后重试。")
        finally:
            with self.clipboard_lock:
                # Retain a timed-out request until its late reply is drained.
                if self.clipboard_pending is request and (not sent or request.completed.is_set()):
                    self.clipboard_pending = None

    def _receive_clipboard_reply(self, *, text_reply: bool, success: bool, text: str = "") -> bool:
        with self.clipboard_lock:
            request = self.clipboard_pending
            if request is None or (text_reply and not request.read) or (not text_reply and request.read and success):
                return False
            request.success, request.text = success, text
            request.completed.set()
            if time.monotonic() >= request.deadline:
                self.clipboard_pending = None
            return True

    def can_apply_clipboard(self, request: ViewerClipboardRequest, current_text: str | None) -> bool:
        return (not self.stop_event.is_set() and self.clipboard_latest is request and
                time.monotonic() < request.deadline and request.baseline == current_text)

    def queue_capture_target_selection(self, target_id: str) -> bool:
        target_id = str(target_id or "")
        if (
            not target_id
            or self.stop_event.is_set()
            or self.session is None
            or self.sock is None
        ):
            return False
        with self.capture_target_condition:
            if self.stop_event.is_set():
                return False
            if self.pending_capture_target_id == target_id:
                return True
            if self.active_capture_target_id == target_id:
                # The newest intent matches the in-flight request; cancel a
                # different waiting request left by an intermediate click.
                self.pending_capture_target_id = None
                return True
            # The user intent is latest-only.  At most one control is being
            # written and one newer choice can wait behind it.
            self.pending_capture_target_id = target_id
            self.capture_target_condition.notify()
            return True

    def get_file_receive_location(self, cancel_event: threading.Event, timeout: float = 10.0) -> tuple[str, str]:
        if not (self.remote_capabilities & CAPABILITY_FILE_RECEIVE_LOCATION):
            return "", "远端版本不支持报告完整接收目录。位置未确认；建议更新被控端后再传输。"
        request_id = uuid4().hex
        completed, response = threading.Event(), {}
        with self.file_location_lock:
            self.file_location_requests[request_id] = (completed, response)
        try:
            self._write_file_control(encode_file_receive_location_request(request_id))
            deadline = time.monotonic() + timeout
            while not completed.wait(0.1):
                if cancel_event.is_set() or self.stop_event.is_set():
                    raise TransferCancelledError("连接或文件确认已取消，尚未发送文件。")
                if time.monotonic() >= deadline:
                    raise TimeoutError("读取远端接收位置超时，尚未发送文件，请重试。")
            if not response.get("success") or not str(response.get("text", "")).strip():
                raise OSError(response.get("statusMessage") or "远端未能确认接收目录，未开始传输。")
            return response["text"], str(response.get("statusMessage") or "")
        finally:
            with self.file_location_lock:
                self.file_location_requests.pop(request_id, None)

    def send_files(self, file_paths: list[str]) -> bool:
        if self.session is None or self.sock is None:
            return False
        if not (self.remote_capabilities & CAPABILITY_FILE_RECEIVE):
            self._put_event("viewer_status", "远端未声明文件接收能力。")
            return False
        normalized_paths = [str(Path(item).expanduser()) for item in file_paths if item]
        if not normalized_paths:
            return False
        if not self.file_transfer_lock.acquire(blocking=False):
            self._put_event("viewer_status", "已有文件传输正在进行。")
            return False
        thread = threading.Thread(
            target=self._send_files_locked,
            args=(normalized_paths,),
            name="RemoteDeskFileSender",
            daemon=True,
        )
        thread.start()
        return True

    def _send_files_locked(self, file_paths: list[str]) -> None:
        sent = 0
        failed = 0
        results: list[str] = []
        try:
            for file_path in file_paths:
                if self.stop_event.is_set():
                    break
                temporary_archive: Path | None = None
                source_path = Path(file_path)
                try:
                    transfer_path, transfer_name, display_name, temporary_archive = self._prepare_transfer_path(source_path)
                    receipt_message = self._send_file_to_remote(transfer_path, transfer_name, display_name)
                    results.append(f"已发送：{display_name}\n{receipt_message}\n")
                    sent += 1
                except Exception as ex:
                    failed += 1
                    results.append(f"未完成：{source_path.name}\n{ex}\n")
                    if not self.stop_event.is_set():
                        self._put_event("viewer_status", f"发送项目失败：{source_path.name} - {ex}")
                finally:
                    if temporary_archive is not None:
                        try:
                            temporary_archive.unlink(missing_ok=True)
                        except OSError:
                            pass
            if sent > 0 and not self.stop_event.is_set():
                suffix = f"，失败 {failed} 个" if failed else ""
                status = (f"远端已确认保存 {sent} 个项目{suffix}。"
                          if self.remote_capabilities & CAPABILITY_FILE_TRANSFER_RECEIPT else
                          f"已发送 {sent} 个项目{suffix}；对方是旧版，请检查接收目录确认是否保存。")
                self._put_event("viewer_status", status)
        finally:
            self.file_transfer_lock.release()
            if results:
                remaining = len(file_paths) - sent - failed
                summary = f"已发送 {sent} 项，未完成 {failed} 项，未开始 {remaining} 项。\n文件夹以 ZIP 保存，不自动解压。\n\n"
                self._put_event("viewer_file_results", summary + "\n".join(results))

    def _prepare_transfer_path(self, source: Path) -> tuple[Path, str, str, Path | None]:
        path = source.expanduser()
        if path.is_file():
            transfer_name = sanitize_file_name(path.name)
            display_name = path.name if transfer_name == path.name else f"{path.name} -> {transfer_name}"
            return path, transfer_name, display_name, None
        if path.is_dir():
            source_size = safe_path_size(path)
            if source_size > MAX_FILE_TRANSFER_BYTES:
                raise ProtocolError(f"文件夹超过 RemoteDesk 传输上限：{source_size} bytes")
            transfer_name = create_directory_archive_name(path)
            self._put_event("viewer_status", f"正在打包文件夹：{path.name} ({source_size} bytes)")
            archive = create_directory_archive(path, source_size)
            return archive, transfer_name, f"{path.name} -> {transfer_name}", archive
        raise FileNotFoundError("只支持发送文件或文件夹。")

    def _send_file_to_remote(self, path: Path, transfer_name: str, display_name: str) -> str:
        if not path.is_file():
            raise FileNotFoundError("只支持发送普通文件。")
        source_stat = path.stat()
        file_size = source_stat.st_size
        source_mtime_ns = source_stat.st_mtime_ns
        if file_size > MAX_FILE_TRANSFER_BYTES:
            raise ProtocolError(f"文件超过 RemoteDesk 传输上限：{file_size} bytes")

        transfer_id = uuid4().hex
        send_checksum = bool(self.remote_capabilities & CAPABILITY_FILE_CHECKSUM)
        send_cancel = bool(self.remote_capabilities & CAPABILITY_FILE_TRANSFER_CANCEL)
        receipt = ViewerFileReceipt(transfer_id) if self.remote_capabilities & CAPABILITY_FILE_TRANSFER_RECEIPT else None
        with self.file_receipt_lock:
            self.file_receipt = receipt
        transfer_active = False
        heartbeat_lease = self._begin_outgoing_file_transfer()
        legacy_drain = False
        try:
            self._write_file_control(encode_file_transfer_start(transfer_id, transfer_name, file_size))
            transfer_active = True
            self._put_event("viewer_status", f"正在发送：{display_name} ({file_size} bytes)")

            offset = 0
            digest = hashlib.sha256()
            last_progress_at = 0.0
            with path.open("rb") as input_file:
                while offset < file_size:
                    if receipt is not None and receipt.completed.is_set() and not receipt.success:
                        raise OSError(receipt.message or "远端拒绝接收文件。")
                    if self.stop_event.is_set():
                        raise EOFError("连接已关闭")
                    chunk = input_file.read(
                        min(RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES, file_size - offset)
                    )
                    if not chunk:
                        raise EOFError("源文件读取提前结束。")
                    digest.update(chunk)
                    self._write_file_control(encode_file_transfer_chunk(transfer_id, offset, chunk))
                    offset += len(chunk)

                    now = time.monotonic()
                    if now - last_progress_at >= 0.75:
                        last_progress_at = now
                        percent = 100 if file_size == 0 else min(100, int(offset * 100 / max(1, file_size)))
                        self._put_event("viewer_status", f"正在发送：{display_name} {percent}%")

            current_stat = path.stat()
            if (
                offset != file_size
                or current_stat.st_size != file_size
                or current_stat.st_mtime_ns != source_mtime_ns
            ):
                raise EOFError("源文件在传输过程中发生变化。")

            if send_checksum:
                self._write_file_control(encode_file_transfer_checksum(transfer_id, digest.hexdigest()))
            self._write_file_control(encode_file_transfer_complete(transfer_id))
            if receipt is not None:
                self._put_event("viewer_status", f"数据已发送，正在等待远端保存确认：{transfer_name}")
                deadline = time.monotonic() + self.file_receipt_timeout
                while not receipt.completed.wait(0.1):
                    if self.stop_event.is_set():
                        raise ConnectionError("连接已结束，未收到文件保存确认。")
                    if time.monotonic() >= deadline:
                        raise TimeoutError("等待远端保存确认超时，文件可能已保存，请检查接收目录后再重试。")
                if not receipt.success:
                    raise OSError(receipt.message or "远端保存文件失败。")
                self._put_event("viewer_status", receipt.message)
            transfer_active = False
            legacy_drain = receipt is None
            return (receipt.message or "远端确认保存，但未提供实际路径。") if receipt is not None else "旧版远端未返回保存确认或实际路径，请到被控端检查。"
        except Exception as ex:
            # A best-effort cancel can itself wait behind TCP data. Failure
            # must restore the normal watchdog before attempting that write.
            self._end_outgoing_file_transfer(heartbeat_lease)
            if transfer_active and send_cancel:
                try:
                    self._write_file_control(encode_file_transfer_cancel(transfer_id, f"Linux viewer cancelled: {ex}"))
                except Exception:
                    pass
            raise

        finally:
            self._end_outgoing_file_transfer(heartbeat_lease, legacy_drain=legacy_drain)
            with self.file_receipt_lock:
                if self.file_receipt is receipt:
                    self.file_receipt = None

    def _send_control(self, payload: bytes) -> None:
        if self.session is None or self.sock is None:
            return
        with self.write_lock:
            write_message(self.sock, self.session, MESSAGE_CONTROL, payload)

    def _set_high_frame_rate_capability(self, enabled: bool) -> None:
        capabilities = linux_viewer_capabilities(enabled)
        if self.advertised_viewer_capabilities == capabilities:
            return
        try:
            self._send_control(encode_viewer_capabilities(capabilities))
            self.advertised_viewer_capabilities = capabilities
        except Exception as ex:
            if not self.stop_event.is_set():
                action = "启用" if enabled else "降低"
                self._put_event(
                    "viewer_status",
                    f"无法通知主机{action}高帧率 H.264：{ex}",
                )

    def _write_file_control(self, payload: bytes) -> None:
        sock = self.sock
        session = self.session
        if sock is None or session is None:
            raise ConnectionError("连接已断开。")
        with self.write_lock:
            write_message(sock, session, MESSAGE_CONTROL, payload)

    def _request_video_key_frame(self) -> None:
        try:
            self._send_control(encode_video_key_frame_request())
        except Exception as ex:
            if not self.stop_event.is_set():
                self._put_event("viewer_status", f"Key-frame request failed: {ex}")

    def _request_video_key_frame_if_due(self) -> None:
        now = time.monotonic()
        if now - self.last_key_frame_request_at < H264_KEY_FRAME_REQUEST_MIN_SECONDS:
            return
        self.last_key_frame_request_at = now
        self._request_video_key_frame()

    def _request_jpeg_fallback(self, reason: str) -> None:
        if self.h264_fallback_requested:
            return
        self.h264_fallback_requested = True
        self.supported_video_codecs = VIDEO_CODEC_JPEG
        try:
            self._send_control(encode_viewer_info(VIDEO_CODEC_JPEG))
        except Exception as ex:
            if not self.stop_event.is_set():
                self._put_event("viewer_status", f"{reason}; JPEG fallback request failed: {ex}")
                return
        if not self.stop_event.is_set():
            self._put_event("viewer_status", f"{reason}; requested JPEG fallback.")

    def _reset_h264_decoder(self) -> None:
        with self.h264_decoder_lock:
            decoder = self.h264_decoder
            self.h264_decoder = None
        if decoder is not None:
            decoder.close()

    def _reset_native_h264_presenter(self) -> None:
        with self.native_h264_presenter_lock:
            presenter = self.native_h264_presenter
            self.native_h264_presenter = None
        if presenter is not None:
            presenter.close()

    def _signal_stop_and_release_decoder(self) -> None:
        self._interrupt_transport()
        with self.h264_decoder_lock:
            decoder = self.h264_decoder
            self.h264_decoder = None
        with self.native_h264_presenter_lock:
            presenter = self.native_h264_presenter
            self.native_h264_presenter = None
        if decoder is not None:
            decoder.close()
        if presenter is not None:
            presenter.close()

    def _interrupt_transport(self) -> None:
        """Wake all transport workers without waiting for the write lock."""

        self.stop_event.set()
        with self.frame_condition:
            self.pending_frame = None
            self.pending_h264_frames.clear()
            self.frame_condition.notify_all()
        with self.input_condition:
            self.pending_inputs.clear()
            self.input_condition.notify_all()
        with self.capture_target_condition:
            self.pending_capture_target_id = None
            self.capture_target_condition.notify_all()
        sock = self.sock
        if sock is None:
            return
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            sock.close()
        except OSError:
            pass

    def _activate_heartbeat(self, now: float | None = None) -> None:
        activated_at = time.monotonic() if now is None else now
        with self.heartbeat_lock:
            self.heartbeat_active = True
            self.last_ping_sent_at = activated_at
            self.last_message_received_at = activated_at
            self.awaiting_pong_since = None

    def _deactivate_heartbeat(self) -> None:
        with self.heartbeat_lock:
            self.heartbeat_active = False
            self.awaiting_pong_since = None
            self._outgoing_file_transfer = None
            self._outgoing_file_drain = None

    def _mark_pong_received(self) -> None:
        with self.heartbeat_lock:
            if self.heartbeat_active:
                self.awaiting_pong_since = None

    def _mark_message_received(self, now: float | None = None) -> None:
        received_at = time.monotonic() if now is None else now
        with self.heartbeat_lock:
            if self.heartbeat_active:
                self.last_message_received_at = received_at

    def _write_heartbeat_ping(self, now: float) -> bool:
        # Record the outstanding probe before the wire write, but never hold
        # the heartbeat-state lock across potentially blocking socket I/O.
        # That lets receive-side teardown acquire the state lock and close the
        # socket, which in turn interrupts a blocked writer.
        with self.heartbeat_lock:
            if not self.heartbeat_active or self.stop_event.is_set():
                return False
            if self.awaiting_pong_since is None:
                self.awaiting_pong_since = now
            self.last_ping_sent_at = now

        with self.write_lock:
            sock = self.sock
            session = self.session
            if (
                self.stop_event.is_set()
                or sock is None
                or session is None
            ):
                return False
            write_message(sock, session, MESSAGE_PING, b"")
            return True

    def _write_heartbeat_message(self, message_type: int) -> bool:
        with self.write_lock:
            sock = self.sock
            session = self.session
            if (
                self.stop_event.is_set()
                or sock is None
                or session is None
            ):
                return False
            write_message(sock, session, message_type, b"")
            return True

    def _handle_heartbeat_message(self, message_type: int) -> bool:
        if message_type == MESSAGE_PING:
            if (
                not self._write_heartbeat_message(MESSAGE_PONG)
                and not self.stop_event.is_set()
            ):
                raise ConnectionError("Linux viewer could not send heartbeat Pong.")
            return True
        if message_type == MESSAGE_PONG:
            self._mark_pong_received()
            return True
        return False

    def _heartbeat_step(self, now: float | None = None) -> bool:
        if self.stop_event.is_set():
            return False
        current = time.monotonic() if now is None else now
        if not self._liveness_step(current):
            return False
        send_ping = False
        with self.heartbeat_lock:
            if not self.heartbeat_active:
                return True
            if (
                current >= self.last_ping_sent_at
                and current - self.last_ping_sent_at
                >= VIEWER_HEARTBEAT_INTERVAL_SECONDS
            ):
                send_ping = True

        if not send_ping:
            return True

        try:
            return self._write_heartbeat_ping(current) or not self.stop_event.is_set()
        except Exception as ex:
            if not self.stop_event.is_set():
                self._put_event("viewer_status", f"Heartbeat send failed: {ex}")
                self._interrupt_transport()
            return False

    def _heartbeat_loop(self) -> None:
        while not self.stop_event.is_set():
            if not self._heartbeat_step():
                return
            if self.stop_event.wait(VIEWER_HEARTBEAT_POLL_SECONDS):
                return

    def _liveness_step(self, now: float | None = None) -> bool:
        """Expire an idle inbound channel without acquiring ``write_lock``."""

        if self.stop_event.is_set():
            return False
        current = time.monotonic() if now is None else now
        with self.heartbeat_lock:
            outgoing = self._outgoing_file_transfer
            drain = self._outgoing_file_drain
            draining = (
                drain is not None
                and drain[0][1] is self.sock
                and drain[0][2] is self.session
                and 0 <= current - drain[1] < VIEWER_FILE_TRANSFER_HEARTBEAT_TIMEOUT_SECONDS
            )
            if drain is not None and not draining:
                self._outgoing_file_drain = None
            # File chunks and Ping share one serialized TCP stream. A slow
            # relay can queue Ping behind healthy upload traffic, while a
            # static desktop produces no inbound frames. Keep a bounded grace
            # for this exact transport, including the remote save receipt.
            # Legacy peers cannot acknowledge Complete: retain an independent
            # bounded tail-drain phase after its local write. An older Pong
            # may precede those tail bytes, so receiving it cannot end drain.
            # Local writes never count as authenticated inbound activity.
            timeout = (
                VIEWER_FILE_TRANSFER_HEARTBEAT_TIMEOUT_SECONDS
                if draining or (outgoing is not None and outgoing[1] is self.sock and outgoing[2] is self.session)
                else VIEWER_HEARTBEAT_TIMEOUT_SECONDS
            )
            timed_out = (
                self.heartbeat_active
                and self.awaiting_pong_since is not None
                and current >= self.awaiting_pong_since
                and current - self.awaiting_pong_since
                >= timeout
                and current >= self.last_message_received_at
                and current - self.last_message_received_at
                >= timeout
            )
            if timed_out:
                # Atomically claim this timeout so heartbeat and watchdog
                # cannot both publish a terminal status.
                self.heartbeat_active = False

        if not timed_out:
            return True
        self._put_event(
            "viewer_status",
            "RemoteDesk heartbeat timed out; closing the stale connection.",
        )
        self._interrupt_transport()
        return False

    def _begin_outgoing_file_transfer(self) -> tuple[object, Any, Any]:
        with self.heartbeat_lock:
            lease = (object(), self.sock, self.session)
            self._outgoing_file_transfer = lease
            self._outgoing_file_drain = None
            return lease

    def _end_outgoing_file_transfer(self, lease: tuple[object, Any, Any], *, legacy_drain: bool = False) -> None:
        with self.heartbeat_lock:
            if self._outgoing_file_transfer is lease:
                self._outgoing_file_transfer = None
                self._outgoing_file_drain = (
                    (lease, time.monotonic())
                    if legacy_drain and not self.stop_event.is_set()
                    and self.heartbeat_active and lease[1] is self.sock and lease[2] is self.session
                    else None
                )

    def _liveness_loop(self) -> None:
        # This worker never takes write_lock.  If a file/frame/control send is
        # stuck in the kernel, socket shutdown remains able to interrupt it.
        while not self.stop_event.is_set():
            if not self._liveness_step():
                return
            if self.stop_event.wait(VIEWER_HEARTBEAT_POLL_SECONDS):
                return

    def _get_h264_decoder(self) -> H264AnnexBDecoder | None:
        with self.h264_decoder_lock:
            return self.h264_decoder

    def _get_native_h264_presenter(self) -> MpvNativeH264Presenter | None:
        with self.native_h264_presenter_lock:
            return self.native_h264_presenter

    def _next_h264_decode_token(
        self,
        sequence: int,
        width: int,
        height: int,
        received_at: float,
    ) -> H264DecodeFrameToken:
        submission_id = self.next_h264_submission_id
        self.next_h264_submission_id += 1
        self.latest_h264_submission_id = submission_id
        return H264DecodeFrameToken(
            submission_id,
            sequence,
            width,
            height,
            received_at,
        )

    def _get_or_create_h264_decoder(self) -> H264AnnexBDecoder | None:
        with self.h264_decoder_lock:
            if self.h264_decoder is not None:
                return self.h264_decoder
            if self.stop_event.is_set():
                return None

        while True:
            if self.stop_event.is_set():
                return None
            backend = choose_next_h264_decoder_backend(
                self.h264_decoder_candidates,
                self.failed_h264_decoder_backends,
            )
            if backend is None:
                return None
            decoder = H264AnnexBDecoder.try_create(backend, self.ffmpeg_path)
            if decoder is None:
                if self.stop_event.is_set():
                    return None
                self.failed_h264_decoder_backends.add(backend.key)
                self._put_event("viewer_status", f"无法启动 H.264 解码后端：{backend.label}，继续轮换。")
                continue

            with self.h264_decoder_lock:
                if self.stop_event.is_set():
                    selected_decoder = None
                elif self.h264_decoder is None:
                    self.h264_decoder = decoder
                    selected_decoder = decoder
                else:
                    selected_decoder = self.h264_decoder
            if selected_decoder is not decoder:
                decoder.close()
                return selected_decoder
            self._put_event("viewer_status", f"正在尝试 H.264 解码后端：{backend.diagnostic}")
            return decoder

    def _get_or_create_native_h264_presenter(
        self,
        recovery_frame: bool,
    ) -> MpvNativeH264Presenter | None:
        with self.native_h264_presenter_lock:
            if self.native_h264_presenter is not None:
                return self.native_h264_presenter
            if self.stop_event.is_set() or not recovery_frame:
                return None

        while True:
            if self.stop_event.is_set():
                return None
            backend = choose_next_mpv_native_h264_backend(
                self.native_h264_presenter_candidates,
                self.failed_native_h264_presenter_backends,
            )
            if backend is None:
                if (
                    self.native_h264_presenter_candidates
                    and not self.native_h264_presenter_exhausted_reported
                ):
                    self.native_h264_presenter_exhausted_reported = True
                    self._put_event(
                        "viewer_status",
                        "mpv 原生硬件 surface 后端已耗尽；继续使用 "
                        "FFmpeg GPU→CPU 回读/Tk 或 JPEG 兼容显示。",
                    )
                self._set_high_frame_rate_capability(False)
                return None
            presenter = MpvNativeH264Presenter.try_create(
                self.native_presenter_window_id,
                backend,
                self.mpv_path,
            )
            if presenter is None:
                if self.stop_event.is_set():
                    return None
                self.failed_native_h264_presenter_backends.add(backend.key)
                self._put_event(
                    "viewer_status",
                    f"无法启动 mpv 原生呈现后端 {backend.label}；继续轮换。",
                )
                continue

            with self.native_h264_presenter_lock:
                if self.stop_event.is_set():
                    selected_presenter = None
                elif self.native_h264_presenter is None:
                    self.native_h264_presenter = presenter
                    selected_presenter = presenter
                else:
                    selected_presenter = self.native_h264_presenter
            if selected_presenter is not presenter:
                presenter.close()
                return selected_presenter
            # Reconnect/backend replacement starts with the original renderer.
            # Do not leave the old window's button claiming the experiment is on.
            self._put_event("viewer_upscaling", False)
            self._put_event(
                "viewer_status",
                f"已预热 mpv/{backend.label}；收到恢复帧后验证原生硬解与 "
                "gpu-next surface 导入，验证期最多保留一帧 Tk 兼容预览。",
            )
            return presenter

    def _disable_native_h264_presenter(
        self,
        reason: str,
        presenter: MpvNativeH264Presenter,
    ) -> bool:
        was_active = self.reported_native_h264_presenter_backend == presenter.backend.key
        self._put_event("viewer_upscaling", False)
        self.failed_native_h264_presenter_backends.add(presenter.backend.key)
        with self.native_h264_presenter_lock:
            if self.native_h264_presenter is presenter:
                self.native_h264_presenter = None
        presenter.close()
        next_backend = choose_next_mpv_native_h264_backend(
            self.native_h264_presenter_candidates,
            self.failed_native_h264_presenter_backends,
        )
        next_text = (
            f"下一个恢复帧将验证 {next_backend.label}"
            if next_backend is not None
            else "原生后端已耗尽"
        )
        if next_backend is None:
            self._set_high_frame_rate_capability(False)
        self._put_event(
            "viewer_status",
            f"{reason}; 已禁用 mpv/{presenter.backend.label} 原生呈现，{next_text}；"
            "回退 FFmpeg GPU→CPU 回读/Tk 显示。",
        )
        return was_active

    def _try_present_native_h264(
        self,
        frame: tuple[int, int, int, int, int, bytes, float],
    ) -> str:
        _sequence, _encoding, width, height, _flags, encoded, received_at = frame
        recovery_frame = is_h264_recovery_frame(frame)
        presenter = self._get_or_create_native_h264_presenter(recovery_frame)
        if presenter is None:
            return "fallback"

        while presenter is not None:
            failure_reason = ""
            if not presenter.is_running:
                failure_reason = presenter.failure_detail or "mpv process stopped"
            elif presenter.activation_timed_out():
                failure_reason = (
                    presenter.failure_detail
                    or "mpv did not confirm both non-copy hardware decode and GPU surface import"
                )
            if failure_reason:
                was_active = self._disable_native_h264_presenter(
                    failure_reason,
                    presenter,
                )
                if was_active and not recovery_frame:
                    self.reset_h264_decoder_on_recovery = True
                    self._request_video_key_frame_if_due()
                    return "wait-recovery"
                presenter = self._get_or_create_native_h264_presenter(recovery_frame)
                continue

            submit_result = presenter.submit(encoded, recovery_frame)
            if not submit_result.accepted:
                detail = presenter.failure_detail
                reason = detail or (
                    "mpv input queue lost its prediction chain"
                    if submit_result.request_recovery
                    else "mpv input is unavailable"
                )
                was_active = self._disable_native_h264_presenter(reason, presenter)
                if submit_result.request_recovery or was_active:
                    self._request_video_key_frame_if_due()
                return "wait-recovery" if was_active and not recovery_frame else "fallback"

            if not presenter.is_native_surface_active:
                return (
                    "pending-preview"
                    if presenter.claim_compatibility_preview()
                    else "pending"
                )

            self._set_high_frame_rate_capability(True)
            if self.reported_native_h264_presenter_backend != presenter.backend.key:
                self.reported_native_h264_presenter_backend = presenter.backend.key
                self.reported_h264_decoder_backend = None
                self._reset_h264_decoder()
                self._put_event(
                    "viewer_status",
                    f"实际 Linux H.264 呈现后端：{presenter.backend.diagnostic}",
                )
            if self.last_rendered_encoding != FRAME_ENCODING_H264_ANNEX_B:
                self.last_rendered_encoding = FRAME_ENCODING_H264_ANNEX_B
                self._put_event(
                    "viewer_status",
                    "Rendering H.264 through verified native GPU surfaces.",
                )
            self.h264_decode_misses = 0
            self._put_event(
                "viewer_native_frame",
                (
                    width,
                    height,
                    (time.monotonic() - received_at) * 1000,
                    presenter.backend.diagnostic,
                ),
            )
            return "presented"

        return "fallback"

    def _rotate_h264_decoder(
        self,
        reason: str,
        failed_backend: FfmpegH264DecoderBackend | None = None,
    ) -> bool:
        decoder = self._get_h264_decoder()
        if failed_backend is None and decoder is not None:
            failed_backend = decoder.backend
        if failed_backend is not None:
            self.failed_h264_decoder_backends.add(failed_backend.key)
        self._reset_h264_decoder()
        self.h264_decode_misses = 0

        next_backend = choose_next_h264_decoder_backend(
            self.h264_decoder_candidates,
            self.failed_h264_decoder_backends,
        )
        if next_backend is None:
            self._request_jpeg_fallback(f"{reason}; all H.264 decoder backends failed")
            return False

        with self.frame_condition:
            self.pending_h264_frames.clear()
            self.waiting_for_h264_recovery = True
            self.reset_h264_decoder_on_recovery = False
        failed_label = failed_backend.label if failed_backend is not None else "current backend"
        self._put_event(
            "viewer_status",
            f"{reason}; disabled {failed_label}, next backend is {next_backend.label}; requesting recovery frame.",
        )
        self.last_key_frame_request_at = time.monotonic()
        self._request_video_key_frame()
        return True

    def _run(self) -> None:
        self.ffmpeg_path = find_ffmpeg()
        self.h264_decoder_candidates = (
            probe_ffmpeg_h264_decoder_backends(self.ffmpeg_path)
            if self.ffmpeg_path is not None
            else ()
        )
        self.failed_h264_decoder_backends.clear()
        native_environment = is_mpv_native_presenter_environment(
            self.native_presenter_window_id,
            self.native_presenter_windowing_system,
            self.native_presenter_display,
        )
        self.mpv_path = find_mpv() if native_environment else None
        self.native_h264_presenter_candidates = (
            probe_mpv_native_h264_backends(self.mpv_path)
            if self.mpv_path is not None
            else ()
        )
        self.failed_native_h264_presenter_backends.clear()
        authenticated = False
        try:
            connection = (relay.connect_viewer(self.relay_options, self.stop_event)
                          if self.relay_options is not None
                          else socket.create_connection((self.host, self.port), timeout=6.0))
            with connection as sock:
                configure_low_latency_socket(sock, SOCKET_RECEIVE_BUFFER_BYTES, SOCKET_SEND_BUFFER_BYTES)
                self.sock = sock
                if self.stop_event.is_set():
                    return
                self.session = authenticate(sock, self.password)
                authenticated = True
                self._activate_heartbeat()
                route = "公网中转 TLS/TCP" if self.relay_options is not None else "IP 直连"
                self._put_event("viewer_status", f"{route}：{self.host}:{self.port}")
                # Start conservatively. HighFrameRateH264 is enabled only
                # after mpv IPC validates both decoder and surface interop.
                self.advertised_viewer_capabilities = linux_viewer_capabilities(False)
                self.supported_video_codecs = VIDEO_CODEC_JPEG
                if self.h264_decoder_candidates or self.native_h264_presenter_candidates:
                    self.supported_video_codecs |= VIDEO_CODEC_H264_ANNEX_B
                if self.native_h264_presenter_candidates:
                    # Start mpv before advertising H.264 so process and IPC
                    # setup overlap the host's first recovery-frame capture.
                    self._get_or_create_native_h264_presenter(True)
                self._send_control(
                    encode_viewer_capabilities(
                        self.advertised_viewer_capabilities
                    )
                )
                self._send_control(encode_viewer_info(self.supported_video_codecs))
                codec_text = "JPEG/H.264" if self.supported_video_codecs & VIDEO_CODEC_H264_ANNEX_B else "JPEG"
                self._put_event("viewer_status", f"Viewer video codecs: {codec_text}")
                if self.h264_decoder_candidates:
                    candidate_text = " → ".join(
                        backend.label for backend in self.h264_decoder_candidates
                    )
                    self._put_event("viewer_status", f"H.264 decoder candidates: {candidate_text}")
                elif (
                    self.ffmpeg_path is not None
                    and not self.native_h264_presenter_candidates
                ):
                    self._put_event(
                        "viewer_status",
                        "ffmpeg 未枚举到可用 H.264 decoder；仅声明 JPEG。",
                    )
                if self.native_h264_presenter_candidates:
                    native_candidate_text = " → ".join(
                        backend.label for backend in self.native_h264_presenter_candidates
                    )
                    self._put_event(
                        "viewer_status",
                        "mpv 原生 surface 候选（仅实际激活后标记零回读）："
                        f"{native_candidate_text}",
                    )
                elif not native_environment:
                    self._put_event(
                        "viewer_status",
                        "mpv 原生 surface 呈现仅在带 DISPLAY 的 X11/XWayland Tk 窗口启用；"
                        "当前使用兼容显示。",
                    )
                elif self.mpv_path is None:
                    self._put_event(
                        "viewer_status",
                        "未安装 mpv；使用 FFmpeg GPU→CPU 回读/Tk 兼容显示。",
                    )
                sock.settimeout(None)
                while not self.stop_event.is_set():
                    try:
                        message_type, payload = read_message(sock, self.session)
                        # Match Windows/Android: complete authenticated frames,
                        # clipboard/file messages and Pong all prove inbound
                        # liveness. On a slow TCP relay Pong can be queued behind
                        # video; receiving that video must not cause a false
                        # disconnect. Partial or unauthenticated bytes do not
                        # refresh this deadline, and Pong state stays separate.
                        self._mark_message_received()
                        rearm_tcp_quickack(sock)
                    except socket.timeout:
                        continue
                    except EOFError:
                        self._put_event("viewer_status", "Remote closed the connection.")
                        break

                    if self._handle_heartbeat_message(message_type):
                        continue
                    if message_type == MESSAGE_CONTROL:
                        self._handle_control(payload)
                    elif message_type == MESSAGE_FRAME:
                        try:
                            self._handle_frame(payload, legacy=True)
                        except ProtocolError as ex:
                            self._put_event("viewer_status", str(ex))
                    elif message_type == MESSAGE_VIDEO_FRAME:
                        try:
                            self._handle_frame(payload, legacy=False)
                        except ProtocolError as ex:
                            self._put_event("viewer_status", str(ex))
        except SessionRejectedError as ex:
            if not self.stop_event.is_set():
                self._put_event("viewer_session_replaced", str(ex))
        except PermissionError as ex:
            if not self.stop_event.is_set():
                self._put_event(
                    "viewer_error" if authenticated else "viewer_auth_failed",
                    str(ex),
                )
        except Exception as ex:
            if not self.stop_event.is_set():
                self._put_event("viewer_error", str(ex))
        finally:
            self._signal_stop_and_release_decoder()
            self._deactivate_heartbeat()
            self.sock = None
            self.session = None
            for worker in (
                self.decoder_thread,
                self.input_thread,
                self.capture_target_thread,
                self.heartbeat_thread,
                self.liveness_thread,
            ):
                if worker.is_alive() and threading.current_thread() is not worker:
                    worker.join(timeout=1.0)
            put_ui_event(self.events, "viewer_closed", self.generation)

    def _handle_control(self, payload: bytes) -> None:
        control = decode_control(payload, include_clipboard_text=True)
        kind = int(control.get("kind") or 0)
        if kind == CONTROL_SESSION_REJECTED:
            raise SessionRejectedError(
                str(
                    control.get("statusMessage")
                    or "Remote host cannot accept another viewer."
                ).strip()
            )
        if kind == CONTROL_DEVICE_INFO:
            self.remote_device_info = dict(control, deviceId=getattr(self, "remote_device_info", {}).get("deviceId", ""))
            if control.get("capabilities", 0) & CAPABILITY_DEVICE_IDENTITY and not getattr(self, "identity_requested", False):
                self.identity_requested = True
                self._send_control(bytes([CONTROL_DEVICE_IDENTITY_REQUEST]))
            self.remote_capabilities = int(control.get("capabilities") or 0)
            name = control.get("machineName") or "RemoteDesk"
            platform = control.get("platform") or "Unknown"
            capabilities = ", ".join(capability_names(self.remote_capabilities)) or "none"
            self._put_event("viewer_status", f"{name} ({platform}) - {capabilities}")
            self._put_event("viewer_reconnect_qualified", True)
        elif kind == CONTROL_DEVICE_IDENTITY and getattr(self, "identity_requested", False):
            self.remote_device_info = dict(getattr(self, "remote_device_info", {}), deviceId=control["deviceId"])
            self._put_event("viewer_device_identity", self.remote_device_info)
        elif kind == CONTROL_CAPTURE_TARGET_LIST:
            self.capture_target_list_received = True
            self.capture_targets = normalize_viewer_capture_targets(
                tuple(
                    ViewerCaptureTarget(
                        str(target.get("id") or ""),
                        str(target.get("displayName") or ""),
                    )
                    for target in (control.get("targets") or [])
                    if isinstance(target, dict)
                )
            )
            self._publish_capture_target_snapshot()
        elif kind == CONTROL_CAPTURE_TARGET_CHANGED:
            target = control.get("target") or {}
            if isinstance(target, dict):
                self.selected_capture_target = normalize_viewer_capture_target(
                    ViewerCaptureTarget(
                        str(target.get("id") or ""),
                        str(target.get("displayName") or ""),
                    )
                )
            self._publish_capture_target_snapshot()
        elif kind == CONTROL_CLIPBOARD_TEXT:
            self._receive_clipboard_reply(text_reply=True, success=True, text=control.get("text", ""))
        elif kind == CONTROL_FILE_TRANSFER_RECEIPT:
            with self.file_receipt_lock:
                receipt = self.file_receipt
                if receipt is not None and receipt.transfer_id == control.get("transferId") and not receipt.completed.is_set():
                    receipt.success = bool(control.get("success"))
                    receipt.message = str(control.get("statusMessage") or "")
                    receipt.completed.set()
        elif kind == CONTROL_FILE_RECEIVE_LOCATION:
            with self.file_location_lock:
                pending = self.file_location_requests.get(str(control.get("transferId") or ""))
                if pending is not None and not pending[0].is_set():
                    pending[1].update(control)
                    pending[0].set()
        elif kind in (CONTROL_CLIPBOARD_STATUS, CONTROL_FILE_TRANSFER_STATUS):
            message = str(
                control.get("statusMessage")
                or control.get("message")
                or "Remote status updated."
            )
            if kind == CONTROL_CLIPBOARD_STATUS:
                if CAPTURE_TARGET_STATUS_TRAILER_PREFIX not in message:
                    if self._receive_clipboard_reply(text_reply=False, success=bool(control.get("success"))) and control.get("success"):
                        return  # The clipboard worker publishes the final result.
                message = strip_capture_target_status_trailer(message)
            self._put_event("viewer_status", message)

    def _publish_capture_target_snapshot(self) -> None:
        self._put_event(
            "viewer_capture_metadata",
            ViewerCaptureTargetSnapshot(
                self.capture_target_list_received,
                self.capture_targets,
                self.selected_capture_target,
            ),
        )

    def _handle_frame(self, payload: bytes, legacy: bool) -> None:
        encoding, width, height, flags, encoded = parse_legacy_frame(payload) if legacy else parse_video_frame(payload)
        self.remote_width = width
        self.remote_height = height
        request_key_frame = False
        with self.frame_condition:
            sequence = self.frame_sequence
            self.frame_sequence += 1
            self.latest_frame_sequence = sequence
            if encoding != self.received_frame_encoding:
                self.received_frame_encoding = encoding
                self.frame_stream_epoch += 1
            frame = (sequence, encoding, width, height, flags, encoded, time.monotonic())
            if encoding == FRAME_ENCODING_H264_ANNEX_B:
                self.pending_frame = None
                if self.waiting_for_h264_recovery and not is_h264_recovery_frame(frame):
                    request_key_frame = True
                else:
                    if self.waiting_for_h264_recovery:
                        self.waiting_for_h264_recovery = False
                        self.pending_h264_frames.clear()
                    request_key_frame = enqueue_h264_frame(
                        self.pending_h264_frames,
                        frame,
                        MAX_QUEUED_H264_FRAMES,
                    )
                    if request_key_frame and not self.pending_h264_frames:
                        self.waiting_for_h264_recovery = True
                        self.reset_h264_decoder_on_recovery = True
            else:
                self.pending_h264_frames.clear()
                self.waiting_for_h264_recovery = True
                self.reset_h264_decoder_on_recovery = True
                self.pending_frame = frame
            self.frame_condition.notify()
        if request_key_frame:
            self._request_video_key_frame_if_due()

    def _decode_frames(self) -> None:
        try:
            self._decode_frames_loop()
        finally:
            self._reset_native_h264_presenter()
            self._reset_h264_decoder()

    def _decode_frames_loop(self) -> None:
        while not self.stop_event.is_set():
            with self.frame_condition:
                while self.pending_frame is None and not self.pending_h264_frames and not self.stop_event.is_set():
                    self.frame_condition.wait(timeout=0.25)
                if self.stop_event.is_set() and self.pending_frame is None and not self.pending_h264_frames:
                    break
                if self.pending_h264_frames:
                    frame = self.pending_h264_frames.popleft()
                else:
                    frame = self.pending_frame
                    self.pending_frame = None
                frame_stream_epoch = self.frame_stream_epoch
            if frame is None:
                continue
            sequence, encoding, width, height, flags, encoded, received_at = frame
            display_width, display_height = self.display_size
            decoded_h264_submission_id: int | None = None
            display_data: bytes | None
            if encoding == FRAME_ENCODING_JPEG:
                self._reset_native_h264_presenter()
                if not self.display_active:
                    continue
                self.h264_decode_misses = 0
                if self.last_rendered_encoding != 0 and has_pending_viewer_frame_event(
                    self.events,
                    self.generation,
                ):
                    continue
                display_data = convert_frame_for_tk(
                    encoded,
                    display_width,
                    display_height,
                )
            elif encoding == FRAME_ENCODING_H264_ANNEX_B:
                if is_h264_recovery_frame(frame) and self.reset_h264_decoder_on_recovery:
                    self.reset_h264_decoder_on_recovery = False
                    self._reset_h264_decoder()
                native_disposition = self._try_present_native_h264(frame)
                if native_disposition in ("presented", "pending"):
                    continue
                if native_disposition == "wait-recovery":
                    continue
                decoder = self._get_or_create_h264_decoder()
                if decoder is None:
                    if self.stop_event.is_set():
                        break
                    if native_disposition == "pending-preview":
                        continue
                    self._request_jpeg_fallback("no usable ffmpeg H.264 decoder backend")
                    continue
                if self.stop_event.is_set():
                    break
                decoded = decoder.decode_correlated(
                    encoded,
                    self._next_h264_decode_token(
                        sequence,
                        width,
                        height,
                        received_at,
                    ),
                    h264_decode_timeout_for_native_disposition(
                        native_disposition
                    ),
                )
                if decoded is None:
                    self.h264_decode_misses += 1
                    if getattr(decoder, "correlation_overflowed", False):
                        self._rotate_h264_decoder(
                            f"H.264 decoder {decoder.backend.label} exceeded its bounded correlation window",
                            decoder.backend,
                        )
                    elif not decoder.is_running:
                        detail = decoder.failure_detail
                        message = f"H.264 decoder {decoder.backend.label} stopped"
                        if detail:
                            message += f" ({detail})"
                        self._rotate_h264_decoder(message, decoder.backend)
                    else:
                        if self.h264_decode_misses % H264_KEY_FRAME_REQUEST_INTERVAL == 0:
                            self._request_video_key_frame_if_due()
                        if self.h264_decode_misses >= h264_decoder_miss_threshold(
                            decoder.backend
                        ):
                            self._rotate_h264_decoder(
                                f"H.264 decoder {decoder.backend.label} produced no displayable frames",
                                decoder.backend,
                            )
                    continue
                if not isinstance(decoded.correlation, H264DecodeFrameToken):
                    self._rotate_h264_decoder(
                        f"H.264 decoder {decoder.backend.label} returned an uncorrelated frame",
                        decoder.backend,
                    )
                    continue
                sequence = decoded.correlation.sequence
                width = decoded.correlation.width
                height = decoded.correlation.height
                received_at = decoded.correlation.received_at
                decoded_h264_submission_id = decoded.correlation.submission_id
                jpeg = decoded.jpeg
                if not is_h264_submission_fresh(
                    decoded_h264_submission_id,
                    self.latest_h264_submission_id,
                    h264_correlated_submission_lag(decoder.backend),
                ):
                    self.h264_decode_misses += 1
                    if self.h264_decode_misses >= h264_decoder_miss_threshold(
                        decoder.backend
                    ):
                        self._rotate_h264_decoder(
                            f"H.264 decoder {decoder.backend.label} produced only stale correlated frames",
                            decoder.backend,
                        )
                    continue
                self.h264_decode_misses = 0
                if self.reported_h264_decoder_backend != decoder.backend.key:
                    self.reported_h264_decoder_backend = decoder.backend.key
                    self._put_event(
                        "viewer_status",
                        f"实际 H.264 解码后端：{decoder.backend.diagnostic}",
                    )
                if not self.display_active:
                    continue
                if self.last_rendered_encoding != 0 and has_pending_viewer_frame_event(
                    self.events,
                    self.generation,
                ):
                    continue
                display_data = convert_frame_for_tk(
                    jpeg,
                    display_width,
                    display_height,
                )
            else:
                self._put_event("viewer_status", f"Unsupported frame encoding: {encoding}")
                continue
            if display_data is None:
                if not self.missing_decoder_reported:
                    self.missing_decoder_reported = True
                    self._put_event(
                        "viewer_status",
                        "Frame received, but Pillow or ImageMagick is required to display JPEG frames.",
                    )
                continue
            if not self.display_active or self.display_size != (display_width, display_height):
                continue
            with self.frame_condition:
                if frame_stream_epoch != self.frame_stream_epoch:
                    continue
            # Do not compare against the network's latest sequence again
            # after an expensive decode/resize.  New arrivals are expected
            # while that work is in flight; rejecting the completed frame in
            # that case starves display forever whenever conversion is slower
            # than capture.  The pipeline remains latency-bounded: JPEG owns
            # one in-flight conversion plus one replaceable pending frame,
            # H.264 owns a bounded prediction queue/correlation window, and
            # put_viewer_event keeps only one unpainted UI frame.
            if self.last_rendered_encoding != 0 and has_pending_viewer_frame_event(
                self.events,
                self.generation,
            ):
                continue
            if self.last_rendered_encoding != encoding:
                self.last_rendered_encoding = encoding
                codec_name = "H.264" if encoding == FRAME_ENCODING_H264_ANNEX_B else "JPEG"
                self._put_event("viewer_status", f"Rendering {codec_name} stream.")
            decode_ms = (time.monotonic() - received_at) * 1000
            backend_diagnostic = (
                decoder.backend.diagnostic
                if encoding == FRAME_ENCODING_H264_ANNEX_B
                else ""
            )
            self._put_event(
                "viewer_frame",
                (
                    width,
                    height,
                    display_data,
                    decode_ms,
                    display_width,
                    display_height,
                    backend_diagnostic,
                ),
            )

    def _send_inputs(self) -> None:
        while not self.stop_event.is_set():
            with self.input_condition:
                while not self.pending_inputs and not self.stop_event.is_set():
                    self.input_condition.wait(timeout=0.25)
                if self.stop_event.is_set():
                    break
                item = pop_next_input_payload(self.pending_inputs)
                self.input_send_active = item is not None
            if item is None:
                continue

            try:
                sock = self.sock
                session = self.session
                if sock is None or session is None:
                    continue
                with self.write_lock:
                    write_message(sock, session, MESSAGE_INPUT, item[1])
            except Exception as ex:
                if not self.stop_event.is_set():
                    self._put_event("viewer_status", f"Input send failed: {ex}")
                    self._signal_stop_and_release_decoder()
                break
            finally:
                with self.input_condition:
                    self.input_send_active = False
                    self.input_condition.notify_all()

    def _send_capture_target_selections(self) -> None:
        while not self.stop_event.is_set():
            with self.capture_target_condition:
                while (
                    self.pending_capture_target_id is None
                    and not self.stop_event.is_set()
                ):
                    self.capture_target_condition.wait(timeout=0.25)
                if self.stop_event.is_set():
                    break
                target_id = self.pending_capture_target_id
                self.pending_capture_target_id = None
                self.active_capture_target_id = target_id
            if target_id is None:
                continue
            try:
                with self.write_lock:
                    sock = self.sock
                    session = self.session
                    if (
                        self.stop_event.is_set()
                        or sock is None
                        or session is None
                    ):
                        continue
                    write_message(
                        sock,
                        session,
                        MESSAGE_CONTROL,
                        encode_select_capture_target(target_id),
                    )
            except Exception as ex:
                if not self.stop_event.is_set():
                    self._put_event(
                        "viewer_status",
                        f"屏幕切换请求发送失败：{ex}",
                    )
                    self._signal_stop_and_release_decoder()
                break
            finally:
                with self.capture_target_condition:
                    self.active_capture_target_id = None
                    self.capture_target_condition.notify_all()


class RemoteDeskLinuxApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.window_icon: tk.PhotoImage | None = None
        self.root.title("RemoteDesk Linux")
        apply_adaptive_window_geometry(
            self.root,
            preferred_size=(1180, 760),
            minimum_size=(640, 480),
        )
        self.root.configure(bg=APP_BG)
        self._configure_window_identity()
        self._configure_style()
        self.events: "queue.Queue[tuple[str, Any]]" = queue.Queue()
        self.host_process: subprocess.Popen[str] | None = None
        self.host_reader_thread: threading.Thread | None = None
        self.host_stop_thread: threading.Thread | None = None
        self.host_generation = 0
        self.host_stopping_generation: int | None = None
        self.host_log_lengths = BoundedLineLengthTracker(
            HOST_LOG_MAX_CHARACTERS,
            HOST_LOG_TRIM_CHARACTERS,
        )
        self.viewer: ViewerConnection | None = None
        self.viewer_generation = 0
        self.viewer_reconnect_policy = ViewerReconnectPolicy()
        self.viewer_reconnect_after_id: Any | None = None
        self.viewer_reconnect_stable_after_id: Any | None = None
        self.viewer_reconnect_target: tuple[str, int, str] | None = None
        self.viewer_capture_target_state = ViewerCaptureTargetState()
        self.viewer_window: tk.Toplevel | None = None
        self.viewer_window_status: ttk.Label | None = None
        self.viewer_target_bar: ttk.Frame | None = None
        self.viewer_target_controls_anchor: ttk.Frame | None = None
        self.viewer_target_selector: ttk.Combobox | None = None
        self.viewer_target_value: tk.StringVar | None = None
        self.viewer_target_choices: tuple[ViewerCaptureTargetChoice, ...] = ()
        self.viewer_target_programmatic_update = False
        self.viewer_window_send_file_button: ttk.Button | None = None
        self.viewer_window_send_folder_button: ttk.Button | None = None
        self.viewer_file_preview_token = 0
        self.viewer_file_preview_active = False
        self.viewer_file_preview_cancel_event: threading.Event | None = None
        self.frame_label: tk.Label | None = None
        self.last_photo: tk.PhotoImage | None = None
        self.native_presenter_active = False
        self.display_width = 1
        self.display_height = 1
        self.remote_width = 1
        self.remote_height = 1
        self.last_motion_sent = 0.0
        self.last_frame_status_update = 0.0
        self.viewer_pressed_keys = ViewerPressedKeyState()
        self.closing = False
        self.relay_host = None
        self.relay_options = None
        self.viewer_relay_options = None
        self.relay_refresh_generation = 0
        self.relay_refreshing = False
        self.relay_setup_generation = 0
        self.relay_setup_busy = False
        self.relay_setup_cancel = None
        self.relay_admin_operation = None
        self.relay_devices = {}
        self.host_preferences = host_startup.HostPreferences()
        self.host_resume_after_id = None
        self.host_restart_attempt = 0
        self.host_armed = False

        self._build_header(root)
        notebook = ttk.Notebook(root)
        self.main_notebook = notebook
        notebook.pack(fill=tk.BOTH, expand=True, padx=16, pady=(8, 16))
        self._build_host_tab(notebook)
        self._build_viewer_tab(notebook)
        self._build_relay_tab(notebook)
        self._restore_host_preferences()
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.event_poll_after_id = self.root.after(EVENT_POLL_MS, self._poll_events)
        if self.relay_options is not None:
            self.root.after_idle(lambda: None if self.closing else self._refresh_relay())

    def _build_relay_tab(self, notebook):
        tab = self._create_scrollable_tab(notebook, "公网中继")
        load_error = ""
        try:
            self.relay_options = relay.load_settings()
        except (OSError, ValueError, TypeError):
            load_error = "保存的中转配置无法读取，请重新填写。"
        options = self.relay_options
        if options:
            self.relay_device_id = options.device_id
        else:
            try:
                from remotedesk_linux_devices import local_device_id
                self.relay_device_id = local_device_id()
            except (OSError, ValueError):
                self.relay_device_id = str(uuid4())
        self.relay_server = tk.StringVar(value=options.server_address if options else "")
        self.relay_port = tk.StringVar(value=str(options.port if options else 56567))
        self.relay_admin_password = tk.StringVar()
        self.relay_ssh_port = tk.StringVar(value=str(options.ssh_port if options else 22))
        self.relay_admin_user = tk.StringVar(value=options.admin_username if options else 'root')
        self.relay_publish = tk.BooleanVar(value=options.publish if options else True)
        self.relay_password = tk.StringVar()
        self.relay_key_selection = None
        self.relay_server_summary = self._wrapping_label(tab, text='', style='PanelSubtitle.TLabel')
        self.relay_server_summary.pack(fill=tk.X, pady=(0, 8))
        self.relay_manage_button = ttk.Button(tab, text='服务器设置', command=self._toggle_relay_form)
        self.relay_manage_button.pack(anchor=tk.W, pady=(0, 8))
        form = ttk.LabelFrame(tab, text="私有服务器配置", padding=16, style="Panel.TLabelframe")
        self.relay_form = form
        form.pack(fill=tk.X)
        self.relay_setup_controls = []
        for row, label, variable, secret in (
            (0, "服务器地址 / IP", self.relay_server, False),
            (1, "管理员密码", self.relay_admin_password, True)):
            self.relay_setup_controls.append(self._row_entry(form, row, label, variable, show="*" if secret else ""))
        advanced = ttk.Frame(form)
        advanced.grid(row=4, column=0, columnspan=3, sticky=tk.EW)
        self.relay_setup_controls.append(self._row_entry(advanced, 0, "SSH 端口（默认 22）", self.relay_ssh_port))
        self.relay_setup_controls.append(self._row_entry(advanced, 1, "管理员账号（默认 root）", self.relay_admin_user))
        self._wrapping_label(advanced, text="中继配置自动获取，无需填写证书或内部密钥。",
                  wraplength=680, style="PanelSubtitle.TLabel").grid(row=2, column=0, columnspan=2, sticky=tk.W)
        advanced.grid_remove()
        advanced_button = ttk.Button(form, text="展开高级设置")
        def toggle_advanced():
            expanded = bool(advanced.winfo_manager())
            advanced.grid_remove() if expanded else advanced.grid()
            advanced_button.configure(text="展开高级设置" if expanded else "收起高级设置")
        advanced_button.configure(command=toggle_advanced)
        advanced_button.grid(row=3, column=0, columnspan=3, sticky=tk.W, pady=6)
        publish = ttk.Checkbutton(tab, text="允许本机在此服务器上线", variable=self.relay_publish,
                                  command=self._change_relay_publish)
        publish.pack(anchor=tk.W, pady=8)
        self.relay_publish_control = publish
        self.relay_setup_controls.append(publish)
        self._wrapping_label(form, text="填写服务器 root / 管理员密码，不是设备密钥。登录后自动获取配置，管理员密码不保存；已登录时可留空。",
                  wraplength=680, style="PanelSubtitle.TLabel").grid(row=6, column=0, columnspan=3, sticky=tk.W)
        actions = ttk.Frame(form)
        actions.grid(row=7, column=0, columnspan=3, sticky=tk.EW, pady=10)
        save = ttk.Button(actions, text="登录服务器", command=self._save_relay, style="Accent.TButton")
        save.pack(side=tk.LEFT)
        self.relay_setup_controls.append(save)
        self.relay_cancel_button = ttk.Button(actions, text="取消配置", command=self._cancel_relay_setup, state=tk.DISABLED)
        self.relay_cancel_button.pack(side=tk.LEFT, padx=8)
        self.relay_logout_button = ttk.Button(actions, text="退出服务器", command=self._logout_relay)
        self.relay_logout_button.pack(side=tk.LEFT, padx=8)
        self._wrap_action_buttons(actions, (save, self.relay_cancel_button, self.relay_logout_button))
        directory_actions = ttk.Frame(tab)
        directory_actions.pack(fill=tk.X, pady=8)
        ttk.Button(directory_actions, text="刷新在线设备", command=self._refresh_relay).pack(side=tk.LEFT)
        ttk.Button(directory_actions, text="立即上报本机 IP", command=self._report_relay_address).pack(side=tk.LEFT, padx=8)
        self.relay_rename_button = ttk.Button(directory_actions, text="修改共享名称", command=self._rename_relay_device)
        self.relay_rename_button.pack(side=tk.LEFT)
        self._wrap_action_buttons(directory_actions, directory_actions.winfo_children())
        self.relay_status = self._wrapping_label(tab, text=load_error or ("服务器登录已保存，点击刷新查看在线设备。" if options else "尚未登录，请填写服务器地址和管理员密码。"), wraplength=700)
        self.relay_status.pack(fill=tk.X, pady=6)
        self.relay_list = self._device_table(tab, columns=("platform", "status", "address"), show="tree headings", height=7)
        self.relay_list.heading("#0", text="设备")
        self.relay_list.heading("platform", text="平台")
        self.relay_list.heading("status", text="状态")
        self.relay_list.column("#0", width=240)
        self.relay_list.column("platform", width=90)
        self.relay_list.column("status", width=150)
        self.relay_list.heading("address", text="直连 IP / 端口")
        self.relay_list.column("address", width=280)
        self.relay_list.bind("<Double-1>", lambda _event: self._connect_relay())
        self.relay_list.bind('<<TreeviewSelect>>', lambda _event: self._select_relay_key())
        password_form = ttk.Frame(tab)
        password_form.pack(fill=tk.X)
        self._row_entry(password_form, 0, "目标设备密钥", self.relay_password, show="*")
        ttk.Button(tab, text="连接选中设备", command=self._connect_relay, style="Accent.TButton").pack(anchor=tk.W, pady=10)
        ttk.Button(tab, text="查看 / 使用 IP", command=self._request_relay_addresses).pack(anchor=tk.W)
        self._update_relay_form()

    def _toggle_relay_form(self):
        if self.relay_form.winfo_manager():
            self.relay_form.pack_forget()
            self.relay_manage_button.config(text='服务器设置')
        else:
            self.relay_form.pack(fill=tk.X, before=self.relay_publish_control)
            self.relay_manage_button.config(text='收起服务器设置')

    def _update_relay_form(self):
        if not hasattr(self, 'relay_form'): return
        configured = self.relay_options is not None
        self.relay_server_summary.config(text='服务器：' + self.relay_options.server_address + ' · 已保存登录' if configured else '尚未登录服务器')
        self.relay_logout_button.config(state=tk.NORMAL if configured else tk.DISABLED)
        self.relay_publish_control.config(state=tk.NORMAL if configured and not self.relay_setup_busy else tk.DISABLED)
        if configured:
            self.relay_form.pack_forget()
            self.relay_manage_button.config(text='服务器设置')
        else:
            self.relay_form.pack(fill=tk.X, before=self.relay_publish_control)
            self.relay_manage_button.config(text='收起服务器设置')

    def _change_relay_publish(self):
        if self.relay_setup_busy or self.relay_options is None: return
        previous = self.relay_options
        next_options = replace(previous, publish=self.relay_publish.get())
        try:
            relay.save_settings(next_options)
        except (OSError, ValueError):
            self.relay_publish.set(previous.publish)
            self.relay_status.config(text='修改未保存，开关已恢复，请重试。')
            return
        self.relay_options = next_options
        self.relay_refresh_generation += 1
        self.relay_refreshing = False
        self._sync_relay_registration()
        self.relay_status.config(text='已允许本机上线；被控开启后自动发布。' if next_options.publish else '已停止本机中继发布，仍可连接其他设备。')

    def _logout_relay(self):
        if self.relay_setup_busy: return
        if self.viewer_relay_options is not None and (self.viewer is not None or self.viewer_reconnect_after_id is not None):
            self.relay_status.config(text='请先断开当前中继远控会话。'); return
        if not messagebox.askyesno('退出服务器？', '本机将从此服务器下线，设备记录和设备密钥保留。再次接入需要管理员密码。\n\n服务器重装或身份变化时，请先核实，再退出并重新登录。', parent=self.root, default=messagebox.NO): return
        try:
            relay.save_settings(None)
        except (OSError, ValueError):
            self.relay_status.config(text='退出未保存，原配置保持不变，请重试。'); return
        self._cancel_relay_setup()
        self.relay_refresh_generation += 1
        self.relay_refreshing = False
        self.relay_options = None
        self._stop_relay_registration()
        self.relay_server.set(''); self.relay_admin_password.set(''); self.relay_password.set('')
        self.relay_ssh_port.set('22'); self.relay_admin_user.set('root')
        self._show_relay_directory([])
        self._update_relay_form()
        self.relay_status.config(text='已退出服务器，设备记录和设备密钥保留。')

    def _select_relay_key(self):
        selected = self.relay_list.selection()
        target = replace(self.relay_options, device_id=selected[0]) if selected and self.relay_options else None
        key = (relay.device_key_scope(target), target.device_id) if target else None
        if key == getattr(self, 'relay_key_selection', None): return
        self.relay_key_selection = key
        try:
            self.relay_password.set(relay.load_device_key(target) if target else '')
        except Exception:
            self.relay_password.set('')
            self.relay_status.config(text='无法读取此设备保存的密钥，请重新填写。')

    def _report_relay_address(self):
        if self.relay_setup_busy:
            return
        connector = self.relay_host
        requested = connector is not None and connector.request_address_refresh()
        self.relay_status.config(text="已请求上报本机 IP / 端口；稍后刷新在线设备即可查看。" if requested
                                 else "请先启动被控端并启用本机上线，等待连接中转服务器。")

    def _request_relay_addresses(self):
        selection = self.relay_list.selection()
        if self.relay_setup_busy:
            return
        if self.relay_refreshing or self.relay_options is None or not selection:
            self.relay_status.config(text="请刷新并选择一台在线设备。")
            return
        self.relay_refreshing = True
        self.relay_refresh_generation += 1
        generation, options, device_id = self.relay_refresh_generation, self.relay_options, selection[0]
        events = self.events
        self.relay_status.config(text="正在核对设备最新地址……")

        def work():
            try:
                target = next((device for device in relay.list_devices(options) if device["deviceId"] == device_id), None)
            except Exception:
                target = None
            put_ui_event(events, "relay_addresses", (generation, options, target))
        threading.Thread(target=work, name="RemoteDeskRelayAddresses", daemon=True).start()

    def _show_relay_addresses(self, options, target):
        if target is None or not target.get("directAddresses") or not target.get("directPort"):
            self.relay_status.config(text="无法取得最新地址。请更新中转服务器和被控端；仍可使用中转连接。")
            return
        dialog = tk.Toplevel(self.root)
        dialog.title(f"{target['machineName']} · 最新地址")
        dialog.transient(self.root)
        ttk.Label(dialog, text=f"设备 ID：{target['deviceId']}\n仅可达的地址能直连；跨网仍用中转。", padding=12).pack(fill=tk.X)
        addresses = tk.Listbox(dialog, width=52, height=min(8, len(target["directAddresses"])))
        for address in target["directAddresses"]:
            addresses.insert(tk.END, f"{address}:{target['directPort']}")
        addresses.selection_set(0)
        addresses.pack(fill=tk.BOTH, expand=True, padx=12)

        def use():
            choice = addresses.curselection()
            if options != self.relay_options or not choice or self.viewer is not None or self.viewer_reconnect_after_id is not None:
                self.relay_status.config(text="配置已变化或已有会话，请重新选择设备。")
                dialog.destroy()
                return
            self.viewer_host.set(target["directAddresses"][choice[0]])
            self.viewer_port.set(str(target["directPort"]))
            self.viewer_password.set(self.relay_password.get())
            self.main_notebook.select(1)
            dialog.destroy()
        ttk.Button(dialog, text="填入 IP 直连", command=use,
                   state=tk.DISABLED if target["deviceId"] == self.relay_device_id else tk.NORMAL).pack(pady=12)

    def _save_relay(self):
        if self.closing or self.relay_setup_busy:
            return
        try:
            request = relay_login.LoginRequest(self.relay_server.get(), int(self.relay_ssh_port.get().strip() or '22'),
                self.relay_admin_user.get()).validate()
            saved = self.relay_options
            same_ssh = saved is not None and saved.server_address.casefold() == request.server.casefold() and saved.ssh_port == request.ssh_port
            secret = self.relay_admin_password.get()
            reuse = not secret and same_ssh and saved.admin_username == request.username
            if not secret and not reuse:
                self.relay_status.config(text='首次登录或更换服务器时，请输入服务器 root / 管理员密码；不是设备密钥。')
                return
            request = replace(request, expected_identity=saved.ssh_host_key_sha256 if same_ssh else '')
        except (ValueError, relay_login.RelayLoginError):
            self.relay_status.config(text="配置未保存：请检查服务器地址、SSH 端口和管理员账号。")
            return
        self.relay_setup_generation += 1
        self.relay_setup_cancel = threading.Event()
        self.relay_refresh_generation += 1  # Discard directory/address results from before this edit.
        self.relay_refreshing = False
        self._set_relay_setup_busy(True)
        if reuse:
            self._verify_relay_setup(replace(saved, publish=self.relay_publish.get()))
            return
        self.relay_admin_password.set('')
        operation = relay_login.LoginOperation()
        self.relay_admin_operation = operation
        generation, events = self.relay_setup_generation, self.events
        device_id, publish = self.relay_device_id, self.relay_publish.get()
        self.relay_status.config(text='正在登录服务器 SSH 并自动获取中继配置……')
        def work(password):
            try:
                result, error = operation.login(request, password, device_id, publish), ''
            except relay_login.RelayLoginError as failure:
                result, error = None, str(failure)
            except Exception:
                result, error = None, '服务器登录未完成，原配置未更改。'
            finally:
                password = None
                operation.close()
            put_ui_event(events, 'relay_setup', (generation, 'admin', None, result, error))
        threading.Thread(target=work, args=(secret,), name='RemoteDeskRelayLogin', daemon=True).start()
        secret = None

    def _set_relay_setup_busy(self, value):
        self.relay_setup_busy = value
        for control in self.relay_setup_controls:
            control.config(state=tk.DISABLED if value else tk.NORMAL)
        self.relay_cancel_button.config(state=tk.NORMAL if value else tk.DISABLED)
        if hasattr(self, 'relay_publish_control'):
            self.relay_publish_control.config(state=tk.DISABLED if value or self.relay_options is None else tk.NORMAL)

    def _cancel_relay_setup(self):
        operation = getattr(self, 'relay_admin_operation', None)
        if operation is not None:
            operation.close()
            self.relay_admin_operation = None
        if self.relay_setup_cancel is not None:
            self.relay_setup_cancel.set()
            self.relay_setup_cancel = None
        self.relay_setup_generation += 1
        if not self.closing:
            self._set_relay_setup_busy(False)
            self.relay_status.config(text="配置已取消；原配置未更改。")

    def _run_relay_setup(self, stage, target, operation):
        generation, stop, events = self.relay_setup_generation, self.relay_setup_cancel, self.events
        self.relay_status.config(text="正在获取服务器身份（尚未发送密钥）……" if stage == "identity"
                                 else "正在验证公网中继连接……")
        def work():
            try:
                result, error = relay.run_setup_request(operation, stop), ""
            except Exception as failure:
                result, error = None, relay.setup_error_message(failure)
            put_ui_event(events, "relay_setup", (generation, stage, target, result, error))
        threading.Thread(target=work, name="RemoteDeskRelaySetup", daemon=True).start()

    def _confirm_relay_identity(self, draft, pin, replacing=False):
        generation = self.relay_setup_generation
        approved = messagebox.askyesno("更新服务器身份？" if replacing else "信任这台中转服务器？",
            f"{draft.server_address}:{draft.port}\n\n"
            + ("服务器身份与原来不同。请先核实服务器是否重装或更换。\n" if replacing else
               "请确认这是你的服务器，并在可信网络上完成首次连接。\n")
            + f"身份指纹：{pin}\n\n确认后才会发送共享访问密钥；以后自动记住。",
            parent=self.root, default=messagebox.NO)
        if self.closing or generation != self.relay_setup_generation:
            return
        if not approved:
            self._cancel_relay_setup()
            return
        self._verify_relay_setup(draft.with_pin(pin))

    def _verify_relay_setup(self, options):
        self._run_relay_setup("verified", options, lambda: relay.list_devices_async(options))

    def _handle_relay_setup(self, value):
        generation, stage, target, result, error = value
        if self.closing or generation != self.relay_setup_generation or not self.relay_setup_busy:
            return
        if error:
            self._set_relay_setup_busy(False)
            self.relay_status.config(text=error)
        elif stage == "identity":
            self._confirm_relay_identity(target, result)
        elif stage == 'admin':
            self.relay_admin_operation = None
            self._verify_relay_setup(result)
        else:
            try:
                relay.save_settings(target)
            except (OSError, ValueError):
                self._set_relay_setup_busy(False)
                self.relay_status.config(text="验证成功，但配置未保存；请检查配置目录权限。原配置仍保留。")
                return
            self.relay_options = target
            self.relay_server.set(target.server_address)
            self.relay_port.set(str(target.port))
            if hasattr(self, 'relay_ssh_port'):
                self.relay_ssh_port.set(str(target.ssh_port))
                self.relay_admin_user.set(target.admin_username)
            self._set_relay_setup_busy(False)
            self._show_relay_directory(result)
            self._sync_relay_registration()
            self._update_relay_form()
            self.relay_status.config(text=f"服务器已登录，当前 {len(result)} 台在线；root 密码未保存，下次自动连接。")

    def _show_relay_directory(self, devices):
        selected = self.relay_list.selection()
        self.relay_list.delete(*self.relay_list.get_children())
        self.relay_devices = {device["deviceId"]: device for device in devices}
        for device in devices:
            local = device["deviceId"] == self.relay_device_id
            self.relay_list.insert("", tk.END, iid=device["deviceId"], text=device["machineName"],
                values=(device["platform"], "本机（不可自连）" if local else "使用中（可接管）" if device["busy"] else "在线",
                        relay.direct_address_display(device)))
        if selected and selected[0] in self.relay_devices: self.relay_list.selection_set(selected[0])

    def _stop_relay_registration(self):
        connector = getattr(self, "relay_host", None)
        self.relay_host = None
        if connector is not None:
            connector.close(wait=False)

    def _sync_relay_registration(self):
        self._stop_relay_registration()
        options = getattr(self, "relay_options", None)
        process = self.host_process
        if options is None or not options.publish or process is None or process.poll() is not None:
            return
        events = self.events
        connector = relay.RelayHostConnector(options, self.host_running_port,
            self.host_machine_name.get(), status=lambda value: put_ui_event(events, "relay_status", (connector, value)))
        self.relay_host = connector
        connector.start()

    def _refresh_relay(self):
        if self.relay_setup_busy or getattr(self, 'relay_renaming', False):
            return
        if self.relay_options is None:
            self.relay_status.config(text="请先保存有效的中转配置。")
            return
        if self.relay_refreshing:
            return
        self.relay_refreshing = True
        self.relay_refresh_generation += 1
        generation, options = self.relay_refresh_generation, self.relay_options
        # Keep the current selection/key while a fresh directory is being read.
        self.relay_status.config(text="正在读取在线设备……")
        events = self.events
        def work():
            try:
                devices, error = relay.list_devices(options), ""
            except Exception as failure:
                devices, error = [], relay.setup_error_message(failure)
            put_ui_event(events, "relay_directory", (generation, options, devices, error))
        threading.Thread(target=work, name="RemoteDeskRelayDirectory", daemon=True).start()

    def _rename_relay_device(self):
        if self.relay_setup_busy or self.relay_refreshing or getattr(self, 'relay_renaming', False):
            return
        selection = self.relay_list.selection()
        device = self.relay_devices.get(selection[0]) if selection else None
        options = self.relay_options
        if device is None or options is None:
            self.relay_status.config(text="请先刷新并选择要命名的设备（也可选择本机）。")
            return
        if not device.get('canRename'):
            messagebox.showinfo("共享名称", device.get('namingUnavailableReason') or relay.NAMING_UNAVAILABLE, parent=self.root)
            return
        generation = self.relay_refresh_generation
        name = simpledialog.askstring("共享名称 · " + device['machineName'],
            "保存在中继服务器，使用同一服务器的所有设备可见。\n不更改系统名称或设备密钥；清空可恢复系统原名。\n系统原名：" + device.get('originalMachineName', ''),
            initialvalue=device.get('sharedName', ''), parent=self.root)
        if name is None or options != self.relay_options or generation != self.relay_refresh_generation:
            return
        try:
            name = relay.normalize_device_name(name)
        except ValueError as error:
            messagebox.showwarning("名称无效", str(error), parent=self.root)
            return
        self.relay_renaming = True
        self.relay_rename_button.config(state=tk.DISABLED)
        self.relay_status.config(text="正在保存共享名称……")
        events = self.events
        def work():
            error = ""
            try:
                relay.rename_device(relay.replace(options, device_id=device['deviceId']), name)
            except relay.RelayIdentityError as failure:
                error = relay.setup_error_message(failure)
            except Exception as failure:
                error = str(failure) or "保存名称失败，请刷新在线列表核对后重试。"
            put_ui_event(events, "relay_rename", (generation, options, error))
        threading.Thread(target=work, name="RemoteDeskRelayRename", daemon=True).start()

    def _complete_relay_rename(self, value):
        generation, options, error = value
        self.relay_renaming = False
        self.relay_rename_button.config(state=tk.NORMAL)
        if generation != self.relay_refresh_generation or options != self.relay_options:
            return
        if error:
            self.relay_status.config(text="共享名称未确认保存，请刷新列表核对。")
            messagebox.showwarning("共享名称保存失败", error, parent=self.root)
        else:
            self._refresh_relay()

    def _connect_relay(self):
        if self.relay_setup_busy:
            return
        if self.viewer is not None or self.viewer_reconnect_after_id is not None:
            self.relay_status.config(text="请先断开当前远控会话。")
            return
        selection = self.relay_list.selection()
        device = self.relay_devices.get(selection[0]) if selection else None
        if device is None or self.relay_options is None:
            self.relay_status.config(text="请刷新并选择一台在线设备。")
            return
        if device["deviceId"] == self.relay_device_id:
            self.relay_status.config(text="这是本机，不能连接自己。")
            return
        password = self.relay_password.get().strip()
        if not password:
            password = simpledialog.askstring('连接 ' + device['machineName'], '请输入这台设备自己的密钥，不是服务器 root 密码。\n连接成功后自动记住。', show='*', parent=self.root)
            if password is None: return
            password = password.strip()
            if not password or len(password) > 4096:
                self.relay_status.config(text='请输入这台设备的有效密钥。'); return
            self.relay_password.set(password)
        self.viewer_relay_options = relay.replace(self.relay_options, device_id=device["deviceId"])
        options = self.viewer_relay_options
        self.viewer_reconnect_policy.begin()
        self._viewer_capture_state().begin_logical_session()
        self.viewer_reconnect_target = (options.server_address, options.port, password)
        self._start_viewer_attempt(*self.viewer_reconnect_target, reconnecting=False)

    def _configure_window_identity(self) -> None:
        if not APP_ICON.exists():
            return
        try:
            self.window_icon = tk.PhotoImage(file=str(APP_ICON))
            self.root.iconphoto(True, self.window_icon)
        except tk.TclError:
            self.window_icon = None

    def _configure_style(self) -> None:
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(".", font=("DejaVu Sans", 10), background=APP_BG, foreground=TEXT_COLOR)
        # Treeview's default fixed row height clips device names at large DPI.
        row_font = tkfont.Font(root=self.root, font=style.lookup("Treeview", "font"))
        style.configure("Treeview", rowheight=row_font.metrics("linespace") + 10)
        style.configure("TFrame", background=APP_BG)
        style.configure("App.TFrame", background=APP_BG)
        style.configure("Header.TFrame", background=HEADER_BG)
        style.configure(
            "Panel.TLabelframe",
            background=PANEL_BG,
            bordercolor=PANEL_BORDER,
            borderwidth=1,
            relief=tk.SOLID,
        )
        style.configure(
            "Panel.TLabelframe.Label",
            background=PANEL_BG,
            foreground=TEXT_COLOR,
            font=("DejaVu Sans", 10, "bold"),
        )
        style.configure("TNotebook", background=APP_BG, borderwidth=0, tabmargins=(0, 0, 0, 0))
        style.configure("TNotebook.Tab", padding=(20, 10), font=("DejaVu Sans", 10, "bold"))
        style.map(
            "TNotebook.Tab",
            background=[("selected", PANEL_BG), ("active", "#e8eef7")],
            foreground=[("selected", ACCENT_COLOR), ("active", TEXT_COLOR)],
        )
        style.configure("TLabel", background=APP_BG, foreground=TEXT_COLOR)
        style.configure("Panel.TLabel", background=PANEL_BG, foreground=TEXT_COLOR)
        style.configure(
            "PanelSubtitle.TLabel",
            background=PANEL_BG,
            foreground=SUBTLE_TEXT_COLOR,
            font=("DejaVu Sans", 9),
        )
        style.configure("Title.TLabel", background=HEADER_BG, foreground=HEADER_TEXT_COLOR, font=("DejaVu Sans", 19, "bold"))
        style.configure("Subtle.TLabel", background=HEADER_BG, foreground=HEADER_MUTED_COLOR)
        style.configure("TEntry", padding=(9, 7), fieldbackground=PANEL_BG, foreground=TEXT_COLOR, bordercolor=PANEL_BORDER)
        style.map("TEntry", bordercolor=[("focus", ACCENT_COLOR)])
        style.configure("TCombobox", padding=(9, 7), fieldbackground=PANEL_BG, foreground=TEXT_COLOR, bordercolor=PANEL_BORDER)
        style.map("TCombobox", bordercolor=[("focus", ACCENT_COLOR)])
        style.configure("TButton", padding=(14, 8), background="#e8eef7", foreground=TEXT_COLOR, borderwidth=0)
        style.map("TButton", background=[("active", "#dbe4ef"), ("pressed", "#cbd5e1"), ("disabled", "#eef2f7")])
        style.configure("Accent.TButton", padding=(16, 8), foreground="#ffffff", background=ACCENT_COLOR, borderwidth=0)
        style.map(
            "Accent.TButton",
            background=[("active", ACCENT_ACTIVE_COLOR), ("pressed", "#1e40af"), ("disabled", ACCENT_DISABLED_COLOR)],
            foreground=[("disabled", "#f8fafc")],
        )
        style.configure("Danger.TButton", padding=(14, 8), foreground="#ffffff", background=DANGER_COLOR, borderwidth=0)
        style.map(
            "Danger.TButton",
            background=[("active", DANGER_ACTIVE_COLOR), ("pressed", "#991b1b"), ("disabled", "#fee2e2")],
            foreground=[("disabled", "#991b1b")],
        )
        style.configure("Status.TLabel", background=APP_BG, foreground=MUTED_TEXT_COLOR, padding=(0, 7))
        style.configure(
            "StatusCard.TLabel",
            background=PANEL_MUTED_BG,
            foreground=MUTED_TEXT_COLOR,
            padding=(12, 10),
            relief=tk.SOLID,
            borderwidth=1,
            anchor=tk.W,
        )
        style.configure("ViewerToolbar.TFrame", background=VIEWER_PANEL_BG)
        style.configure(
            "ViewerToolbar.TLabel",
            background=VIEWER_PANEL_BG,
            foreground=VIEWER_TEXT_COLOR,
        )
        style.configure("ViewerStatus.TLabel", background=VIEWER_STATUS_BG, foreground="#cbd5e1", padding=(12, 7))
        style.configure("Viewer.TEntry", padding=(9, 7), fieldbackground="#172033", foreground=VIEWER_TEXT_COLOR, bordercolor="#334155")
        style.configure("Viewer.TCombobox", padding=(9, 7), fieldbackground="#172033", foreground=VIEWER_TEXT_COLOR, bordercolor="#334155")
        style.configure("Viewer.TButton", padding=(12, 7), background="#172033", foreground=VIEWER_TEXT_COLOR, borderwidth=0)
        style.map("Viewer.TButton", background=[("active", "#1e293b"), ("pressed", "#334155")])
        style.configure("ViewerDanger.TButton", padding=(12, 7), background="#7f1d1d", foreground="#fee2e2", borderwidth=0)
        style.map("ViewerDanger.TButton", background=[("active", "#991b1b"), ("pressed", "#b91c1c")])

    def _build_header(self, parent: tk.Misc) -> None:
        header = tk.Frame(parent, bg=HEADER_BG, padx=18, pady=14, bd=0, highlightthickness=0)
        header.pack(fill=tk.X)
        brand = tk.Label(
            header,
            text="RD",
            bg=ACCENT_COLOR,
            fg="#ffffff",
            font=("DejaVu Sans", 11, "bold"),
            padx=10,
            pady=8,
        )
        brand.grid(row=0, column=0, rowspan=2, sticky=tk.W, padx=(0, 12))
        title = tk.Label(
            header,
            text="RemoteDesk",
            bg=HEADER_BG,
            fg=HEADER_TEXT_COLOR,
            font=("DejaVu Sans", 19, "bold"),
            anchor=tk.W,
        )
        subtitle = tk.Label(
            header,
            text="Linux · 安全连接、远程控制与文件传输",
            bg=HEADER_BG,
            fg=HEADER_MUTED_COLOR,
            font=("DejaVu Sans", 9),
            anchor=tk.W,
        )
        badge = tk.Label(
            header,
            text="直连 / 私有中继",
            bg="#172554",
            fg="#bfdbfe",
            font=("DejaVu Sans", 9, "bold"),
            padx=10,
            pady=6,
        )
        title.grid(row=0, column=1, sticky=tk.W)
        subtitle.grid(row=1, column=1, sticky=tk.W, pady=(2, 0))
        badge.grid(row=0, column=2, rowspan=2, sticky=tk.E, padx=(16, 0))
        header.columnconfigure(1, weight=1)

        compact_state = {"value": False}

        def update_header_layout(event: tk.Event[Any] | None = None) -> None:
            available_width = int(getattr(event, "width", header.winfo_width()))
            compact = available_width < brand.winfo_reqwidth() + title.winfo_reqwidth() + badge.winfo_reqwidth() + 90
            if compact == compact_state["value"] and available_width > 1:
                return
            compact_state["value"] = compact
            if compact:
                badge.grid_remove()
            else:
                badge.grid()

        header.bind("<Configure>", update_header_layout, add="+")
        header.after_idle(update_header_layout)

    @staticmethod
    def _wrapping_label(parent, **options):
        options.setdefault("wraplength", 400)
        options.setdefault("justify", tk.LEFT)
        label = ttk.Label(parent, **options)
        label._remotedesk_wrap = True
        return label

    @staticmethod
    def _wrap_action_buttons(parent, buttons):
        """Wrap natural-size actions without retaining the old row's width."""
        buttons = tuple(buttons)
        for button in buttons:
            button.pack_forget()
        for index, button in enumerate(buttons):
            button.grid(row=index, column=0, sticky=tk.W, padx=(0, 6), pady=(0, 6))
        previous = None
        def reflow(_event=None):
            nonlocal previous
            available = max(1, parent.winfo_width())
            # Grid columns share their maximum width across rows. Measuring
            # each row separately can make those shared columns wider than
            # the viewport and oscillate indefinitely between two layouts.
            width = max((button.winfo_reqwidth() + 6 for button in buttons), default=1)
            columns = max(1, min(len(buttons), available // width))
            positions = [(index // columns, index % columns) for index in range(len(buttons))]
            signature = tuple(positions)
            if signature == previous:
                return
            previous = signature
            for button in buttons:
                button.grid_forget()
            for button, (row, column) in zip(buttons, positions):
                button.grid(row=row, column=column, sticky=tk.W, padx=(0, 6), pady=(0, 6))
        parent.bind("<Configure>", reflow, add="+")
        parent.after_idle(reflow)

    @staticmethod
    def _device_table(parent, **options):
        """A wide table scrolls inside its own viewport, never the whole page."""
        viewport = ttk.Frame(parent, width=1)
        viewport.pack(fill=tk.X, pady=8)
        viewport.grid_propagate(False)
        viewport.rowconfigure(0, weight=1)
        viewport.columnconfigure(0, weight=1)
        tree = ttk.Treeview(viewport, **options)
        vertical = ttk.Scrollbar(viewport, orient=tk.VERTICAL, command=tree.yview)
        horizontal = ttk.Scrollbar(viewport, orient=tk.HORIZONTAL, command=tree.xview)
        tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        tree.grid(row=0, column=0, sticky=tk.NSEW)
        vertical.grid(row=0, column=1, sticky=tk.NS)
        horizontal.grid(row=1, column=0, sticky=tk.EW)
        def fit_height(_event=None):
            viewport.configure(height=tree.winfo_reqheight() + horizontal.winfo_reqheight())
        tree.bind("<<ThemeChanged>>", fit_height, add="+")
        viewport.after_idle(fit_height)
        return tree

    def _create_scrollable_tab(
        self,
        notebook: ttk.Notebook,
        title: str,
    ) -> ttk.Frame:
        container = ttk.Frame(notebook, style="App.TFrame")
        notebook.add(container, text=title)

        canvas = tk.Canvas(
            container,
            bg=APP_BG,
            bd=0,
            highlightthickness=0,
            takefocus=False,
        )
        scrollbar = ttk.Scrollbar(
            container,
            orient=tk.VERTICAL,
            command=canvas.yview,
        )
        horizontal = ttk.Scrollbar(container, orient=tk.HORIZONTAL, command=canvas.xview)
        canvas.configure(yscrollcommand=scrollbar.set, xscrollcommand=horizontal.set)
        container.columnconfigure(0, weight=1)
        container.rowconfigure(0, weight=1)
        scrollbar.grid(row=0, column=1, sticky=tk.NS)
        canvas.grid(row=0, column=0, sticky=tk.NSEW)
        horizontal.grid(row=1, column=0, sticky=tk.EW)

        content = ttk.Frame(canvas, padding=12, style="App.TFrame")
        content_window = canvas.create_window(
            (0, 0),
            window=content,
            anchor=tk.NW,
        )

        pending_sync: str | None = None

        def sync_scroll_region() -> None:
            nonlocal pending_sync
            if pending_sync is not None:
                canvas.after_cancel(pending_sync)
            pending_sync = None
            # Marked descriptions wrap to the actual viewport, not their old
            # requested width. Row captions and intentionally wide controls
            # retain the horizontal-scroll fallback below.
            def wrap_descriptions(parent):
                for child in parent.winfo_children():
                    if getattr(child, "_remotedesk_wrap", False):
                        left = max(0, child.winfo_rootx() - content.winfo_rootx())
                        length = max(80, canvas.winfo_width() - left * 2 - 28)
                        if int(child.cget("wraplength")) != length:
                            child.configure(wraplength=length)
                    wrap_descriptions(child)
            wrap_descriptions(content)
            # Request sizes can change without the canvas resizing (new device
            # rows, wrapped buttons, larger fonts). Recompute both axes; forcing
            # a too-wide form into the viewport permanently clips its inputs.
            width = max(1, canvas.winfo_width(), content.winfo_reqwidth())
            height = max(1, canvas.winfo_height(), content.winfo_reqheight())
            canvas.itemconfigure(content_window, width=width, height=height)
            canvas.configure(scrollregion=(0, 0, width, height))
            if width > canvas.winfo_width():
                horizontal.grid()
            else:
                horizontal.grid_remove()

        def queue_sync(_event: tk.Event[Any] | None = None) -> None:
            nonlocal pending_sync
            if pending_sync is None:
                pending_sync = canvas.after_idle(sync_scroll_region)

        def is_content(widget: tk.Misc) -> bool:
            return str(widget) == str(content) or str(widget).startswith(str(content) + ".")

        def content_changed(event: tk.Event[Any]) -> None:
            if is_content(event.widget):
                queue_sync()

        def reveal_focus(event: tk.Event[Any]) -> None:
            widget = event.widget
            if not is_content(widget) or widget is content:
                return
            sync_scroll_region()
            for axis in ("x", "y"):
                root_position = getattr(widget, "winfo_root" + axis)()
                canvas_position = getattr(canvas, "winfo_root" + axis)()
                size = widget.winfo_width() if axis == "x" else widget.winfo_height()
                viewport = canvas.winfo_width() if axis == "x" else canvas.winfo_height()
                total = content.winfo_width() if axis == "x" else content.winfo_height()
                offset = getattr(canvas, "canvas" + axis)(0)
                start = root_position - canvas_position + offset
                target = min(offset, start - 8) if start < offset else max(offset, start + min(size, viewport) + 8 - viewport)
                getattr(canvas, axis + "view_moveto")(max(0, target) / max(1, total))

        def scroll_content(event: tk.Event[Any], delta: int | None = None) -> str:
            wheel_delta = delta if delta is not None else int(getattr(event, "delta", 0))
            if wheel_delta:
                canvas.yview_scroll(-3 if wheel_delta > 0 else 3, "units")
            return "break"

        def bind_scroll_tree(widget: tk.Misc) -> None:
            widget.bind("<MouseWheel>", scroll_content, add="+")
            widget.bind(
                "<Button-4>",
                lambda event: scroll_content(event, 120),
                add="+",
            )
            widget.bind(
                "<Button-5>",
                lambda event: scroll_content(event, -120),
                add="+",
            )
            for child in widget.winfo_children():
                bind_scroll_tree(child)

        canvas.bind("<Configure>", queue_sync, add="+")
        # Toplevel bindtags also receive descendants' geometry/focus events,
        # including controls added later. Scope and remove these subscriptions.
        toplevel = content.winfo_toplevel()
        bindings = [(sequence, toplevel.bind(sequence, callback, add="+")) for sequence, callback in (
            ("<Configure>", content_changed), ("<Map>", content_changed), ("<FocusIn>", reveal_focus))]

        def dispose(event: tk.Event[Any]) -> None:
            if event.widget is not content:
                return
            if pending_sync is not None:
                canvas.after_cancel(pending_sync)
            for sequence, binding in bindings:
                toplevel.unbind(sequence, binding)

        content.bind("<Destroy>", dispose, add="+")
        content.after_idle(lambda: bind_scroll_tree(content))
        return content

    def _build_host_tab(self, notebook: ttk.Notebook) -> None:
        tab = self._create_scrollable_tab(notebook, "被控")

        form = ttk.LabelFrame(tab, text="被控设置", padding=16, style="Panel.TLabelframe")
        form.pack(fill=tk.X)
        self.host_password = tk.StringVar(value="")
        self.host_port = tk.StringVar(value="56565")
        self.host_receive_dir = tk.StringVar(value=str(Path.home() / "Downloads" / "RemoteDeskReceived"))
        self.host_capture = tk.StringVar(value="x11")
        self.host_fps = tk.StringVar(value=DEFAULT_HOST_FPS)
        self.host_size = tk.StringVar(value=DEFAULT_HOST_SIZE)
        self.host_machine_name = tk.StringVar(value=socket.gethostname() or "Linux")

        self._wrapping_label(
            form,
            text="设置本机设备密钥后启动被控；其他设备可通过 IP 直连，或登录同一公网中继连接本机。",
            style="PanelSubtitle.TLabel",
            wraplength=760,
            justify=tk.LEFT,
        ).grid(row=0, column=0, columnspan=3, sticky=tk.EW, pady=(0, 10))

        self._row_entry(form, 1, "设备密钥", self.host_password, show="*")
        self._row_entry(form, 2, "监听端口", self.host_port)
        self._row_entry(form, 3, "接收目录", self.host_receive_dir, browse=True)
        self._row_entry(form, 4, "设备名称", self.host_machine_name)

        ttk.Label(form, text="采集方式", style="Panel.TLabel").grid(row=5, column=0, sticky=tk.W, pady=6)
        ttk.Combobox(
            form,
            textvariable=self.host_capture,
            values=("x11", "placeholder"),
            state="readonly",
            width=12,
        ).grid(row=5, column=1, columnspan=2, sticky=tk.EW, pady=6)
        ttk.Label(form, text="目标帧率", style="Panel.TLabel").grid(row=6, column=0, sticky=tk.W, pady=6)
        ttk.Combobox(
            form,
            textvariable=self.host_fps,
            values=HOST_FPS_OPTIONS,
            state="readonly",
            width=12,
        ).grid(row=6, column=1, columnspan=2, sticky=tk.EW, pady=6)
        ttk.Label(form, text="画面尺寸", style="Panel.TLabel").grid(row=7, column=0, sticky=tk.W, pady=6)
        ttk.Combobox(
            form,
            textvariable=self.host_size,
            values=HOST_SIZE_OPTIONS,
            state="readonly",
            width=12,
        ).grid(row=7, column=1, columnspan=2, sticky=tk.EW, pady=6)

        self.host_remember = tk.BooleanVar(value=True)
        self.host_login_start = tk.BooleanVar(value=True)
        ttk.Checkbutton(form, text="加密保存设备密钥与被控状态", variable=self.host_remember,
                        command=self._save_host_preferences).grid(row=8, column=0, columnspan=3, sticky=tk.W, pady=6)
        ttk.Checkbutton(form, text="登录桌面后自动启动", variable=self.host_login_start,
                        command=self._save_host_preferences).grid(row=9, column=0, columnspan=3, sticky=tk.W, pady=6)

        buttons = ttk.Frame(tab, style="App.TFrame")
        buttons.pack(fill=tk.X, pady=(10, 6))
        self.start_host_button = ttk.Button(buttons, text="启动被控", command=self.start_host, style="Accent.TButton")
        self.start_host_button.pack(side=tk.LEFT)
        self.stop_host_button = ttk.Button(
            buttons,
            text="停止被控",
            command=self.stop_host,
            state=tk.DISABLED,
            style="Danger.TButton",
        )
        self.stop_host_button.pack(side=tk.LEFT, padx=(8, 0))
        self._wrap_action_buttons(buttons, (self.start_host_button, self.stop_host_button))

        self.host_status = self._wrapping_label(
            tab,
            text="首次设置设备密钥并启动后会记住被控状态；点击停止后不会自动恢复。",
            style="StatusCard.TLabel",
            justify=tk.LEFT,
        )
        self.host_status.pack(fill=tk.X)
        ttk.Label(tab, text="实时日志", style="Status.TLabel").pack(fill=tk.X, pady=(6, 0))
        self.host_log = tk.Text(
            tab,
            height=20,
            width=1,
            wrap=tk.WORD,
            bg="#111827",
            fg="#d1d5db",
            insertbackground="#e5e7eb",
            relief=tk.FLAT,
            highlightthickness=1,
            highlightbackground="#1e293b",
            highlightcolor="#334155",
            selectbackground="#1d4ed8",
            padx=10,
            pady=8,
        )
        self.host_log.pack(fill=tk.BOTH, expand=True, pady=(6, 0))

    def _build_viewer_tab(self, notebook: ttk.Notebook) -> None:
        tab = self._create_scrollable_tab(notebook, "控制")

        form = ttk.LabelFrame(tab, text="远程连接", padding=16, style="Panel.TLabelframe")
        form.pack(fill=tk.X)
        self.viewer_host = tk.StringVar(value="")
        self.viewer_port = tk.StringVar(value="")
        self.viewer_password = tk.StringVar(value="")
        self._wrapping_label(
            form,
            text="点选附近或已保存设备，或输入 IP 和设备密钥；端口留空自动探测。",
            style="PanelSubtitle.TLabel",
            wraplength=760,
            justify=tk.LEFT,
        ).grid(row=0, column=0, columnspan=3, sticky=tk.EW, pady=(0, 10))
        self._row_entry(form, 1, "主机 / IP", self.viewer_host)
        self._row_entry(form, 2, "端口（留空自动）", self.viewer_port)
        self._row_entry(form, 3, "设备密钥", self.viewer_password, show="*")

        buttons = ttk.Frame(tab, style="App.TFrame")
        buttons.pack(fill=tk.X, pady=(10, 6))
        self.connect_button = ttk.Button(buttons, text="连接", command=self.connect_viewer, style="Accent.TButton")
        self.disconnect_button = ttk.Button(
            buttons,
            text="断开",
            command=self.disconnect_viewer,
            state=tk.DISABLED,
            style="Danger.TButton",
        )
        self.send_file_button = ttk.Button(buttons, text="发送文件", command=self.send_viewer_files, state=tk.DISABLED)
        self.send_folder_button = ttk.Button(buttons, text="发送文件夹", command=self.send_viewer_folder, state=tk.DISABLED)

        text_frame = ttk.Frame(tab)
        text_frame.pack(fill=tk.X, pady=(0, 8))
        self.text_input = tk.StringVar(value="")
        text_entry = ttk.Entry(text_frame, textvariable=self.text_input, width=12)
        text_entry.grid(row=0, column=0, sticky=tk.EW)
        text_entry.bind("<Return>", lambda _event: self.send_viewer_text())
        send_text_button = ttk.Button(text_frame, text="发送文本", command=self.send_viewer_text)
        send_text_button.grid(row=0, column=1, padx=(6, 0))
        text_frame.columnconfigure(0, weight=1)
        viewer_actions = (
            self.connect_button,
            self.disconnect_button,
            self.send_file_button,
            self.send_folder_button,
        )

        self._wrap_action_buttons(buttons, viewer_actions)

        self.viewer_status = self._wrapping_label(
            tab,
            text="未连接。",
            style="StatusCard.TLabel",
            justify=tk.LEFT,
        )
        self.viewer_status.pack(fill=tk.X)
        self.device_panel = DevicePanel(self, tab)
        self.device_panel.pack(fill=tk.X, before=form, pady=(0, 10))

    def _row_entry(
        self,
        parent: ttk.Frame,
        row: int,
        label: str,
        variable: tk.StringVar,
        show: str | None = None,
        browse: bool = False,
    ) -> ttk.Entry:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=0, sticky=tk.W, pady=6, padx=(0, 12))
        entry = ttk.Entry(parent, textvariable=variable, show=show or "", width=12)
        entry.grid(row=row, column=1, sticky=tk.EW, pady=6)
        parent.columnconfigure(1, weight=1)
        if browse:
            ttk.Button(
                parent,
                text="浏览",
                width=4,
                command=lambda: self._browse_directory(variable),
            ).grid(row=row, column=2, sticky=tk.W, padx=(8, 0), pady=6)
        return entry

    def _browse_directory(self, variable: tk.StringVar) -> None:
        selected = filedialog.askdirectory(initialdir=variable.get() or str(Path.home()))
        if selected:
            variable.set(selected)

    def _restore_host_preferences(self) -> None:
        try:
            values = self.host_preferences.load()
            for name in ("password", "port", "receive_dir", "capture", "fps", "size", "machine_name", "remember", "login_start"):
                if name in values:
                    getattr(self, "host_" + name).set(values[name])
            self.host_armed = bool(values.get("armed"))
            if self.host_armed:
                self.host_status.config(text="正在恢复上次开启的被控端…")
                self.host_resume_after_id = self.root.after(350, self._resume_host)
        except Exception:
            self.host_armed = False
            self.host_status.config(text="保存的设备密钥或设置无法读取，未自动启动。请重新填写；不会使用默认设备密钥。")

    def _save_host_preferences(self) -> None:
        if not hasattr(self, "host_preferences"):
            return
        try:
            values = {name: getattr(self, "host_" + name).get()
                      for name in ("port", "receive_dir", "capture", "fps", "size", "machine_name", "remember", "login_start")}
            values["armed"] = bool(self.host_armed)
            self.host_preferences.save(values, self.host_password.get().strip())
            host_startup.set_login_start(bool(values["login_start"] and values["remember"]),
                                         sys.executable, str(Path(__file__).resolve()))
        except Exception:
            self._append_host_log("# 无法保存被控状态或登录启动项，请检查配置目录权限；当前服务不受影响。\n")

    def _resume_host(self) -> None:
        self.host_resume_after_id = None
        if not self.closing and self.host_armed:
            self.start_host(auto=True)

    def _retry_host_after_exit(self) -> None:
        if not getattr(self, "host_armed", False) or self.closing:
            return
        if time.monotonic() - getattr(self, "host_started_at", 0) > 60:
            self.host_restart_attempt = 0
        attempts = getattr(self, "host_restart_attempt", 0)
        if attempts >= 3:
            self._append_host_log("# 被控端连续退出，已停止自动重试；请检查日志后手动启动。\n")
            return
        self.host_restart_attempt = attempts + 1
        self.host_resume_after_id = self.root.after((1, 3, 10)[attempts] * 1000, self._resume_host)
        self._append_host_log(f"# 被控端意外退出，安排第 {attempts + 1}/3 次恢复。\n")

    def start_host(self, auto: bool = False) -> None:
        if self.host_process is not None or self.host_stopping_generation is not None:
            return
        password = self.host_password.get().strip()
        if not password:
            if not auto:
                messagebox.showerror("RemoteDesk", "设备密钥不能为空。")
            return
        receive_dir = Path(self.host_receive_dir.get()).expanduser()
        try:
            receive_dir.mkdir(parents=True, exist_ok=True)
        except OSError as ex:
            messagebox.showerror("RemoteDesk", f"创建接收目录失败：{ex}")
            return
        port = normalize_port(self.host_port.get())
        fps = self.host_fps.get().strip() or DEFAULT_HOST_FPS
        width, height = parse_size(self.host_size.get())
        password_read_fd, password_write_fd = os.pipe()
        command = [
            sys.executable,
            str(HOST_SCRIPT),
            "--host",
            "0.0.0.0",
            "--port",
            str(port),
            "--password-fd",
            str(password_read_fd),
            "--receive-dir",
            str(receive_dir),
            "--machine-name",
            self.host_machine_name.get().strip() or "Linux",
            "--capture",
            self.host_capture.get(),
            "--fps",
            fps,
            "--width",
            str(width),
            "--height",
            str(height),
        ]
        if auto:
            self._append_host_log("# 自动启动 Linux 被控端\n")
        self._append_host_log("$ " + redact_command(command) + "\n")
        process: subprocess.Popen[str] | None = None
        try:
            process = subprocess.Popen(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
                pass_fds=(password_read_fd,),
            )
            os.close(password_read_fd)
            password_read_fd = -1
            with os.fdopen(password_write_fd, "wb", closefd=True) as password_pipe:
                password_write_fd = -1
                password_pipe.write(password.encode("utf-8"))
        except Exception as ex:
            if password_read_fd >= 0:
                os.close(password_read_fd)
            if password_write_fd >= 0:
                os.close(password_write_fd)
            if process is not None:
                process.terminate()
            self.host_process = None
            messagebox.showerror("RemoteDesk", f"启动被控端失败：{ex}")
            return

        self.host_generation += 1
        generation = self.host_generation
        self.host_process = process
        self.host_armed = True
        self.host_started_at = time.monotonic()
        if not auto:
            self.host_restart_attempt = 0
        self._save_host_preferences()
        self.host_running_port = port
        self._sync_relay_registration()
        status_suffix = "（自动启动）" if auto else ""
        self.host_status.config(text=f"被控端正在监听端口 {port}{status_suffix}。")
        self.start_host_button.config(state=tk.DISABLED)
        self.stop_host_button.config(state=tk.NORMAL)
        self.host_reader_thread = threading.Thread(
            target=self._read_host_output,
            args=(self.events, process, generation),
            name="RemoteDeskLinuxHostOutput",
            daemon=True,
        )
        self.host_reader_thread.start()

    def stop_host(self) -> None:
        if not getattr(self, "closing", False):
            self.host_armed = False
            self._save_host_preferences()
        pending = getattr(self, "host_resume_after_id", None)
        if pending is not None:
            self.root.after_cancel(pending)
            self.host_resume_after_id = None
        self._stop_relay_registration()
        process = self.host_process
        if process is None or self.host_stopping_generation is not None:
            return
        generation = self.host_generation
        self.host_stopping_generation = generation
        try:
            self.host_status.config(text="正在停止被控端...")
            self.start_host_button.config(state=tk.DISABLED)
            self.stop_host_button.config(state=tk.DISABLED)
        except tk.TclError:
            pass

        self.host_stop_thread = threading.Thread(
            target=self._stop_host_worker,
            args=(self.events, process, generation),
            name="RemoteDeskLinuxHostStop",
            # Keep process cleanup alive if the Tk window closes immediately.
            daemon=False,
        )
        self.host_stop_thread.start()

    @staticmethod
    def _stop_host_worker(
        events: queue.Queue,
        process: subprocess.Popen[str],
        generation: int,
    ) -> None:
        code, error = RemoteDeskLinuxApp._terminate_host_process(process)
        put_ui_event(
            events,
            "host_stop_completed",
            (generation, process, code, error),
        )

    @staticmethod
    def _terminate_host_process(
        process: subprocess.Popen[str],
    ) -> tuple[int | None, str]:
        error = ""
        try:
            try:
                process.terminate()
            except OSError:
                pass
            try:
                code = process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                process.kill()
                code = process.wait(timeout=2)
        except Exception as ex:
            code = process.poll()
            error = str(ex)
        return code, error

    def stop_host_at_exit(self) -> None:
        """Synchronous exit-only fallback; never called from Tk callbacks."""

        self._stop_relay_registration()
        worker = self.host_stop_thread
        if worker is not None and worker.is_alive():
            worker.join(timeout=6.5)
        process = self.host_process
        if process is None:
            return
        try:
            if process.poll() is None:
                self._terminate_host_process(process)
        except Exception:
            pass

    @staticmethod
    def _read_host_output(
        events: queue.Queue,
        process: subprocess.Popen[str],
        generation: int,
    ) -> None:
        if process.stdout is None:
            return
        for line in process.stdout:
            put_ui_event(events, "host_log", line)
        code = process.wait()
        put_ui_event(
            events,
            "host_exited",
            (generation, process, code),
        )

    def connect_viewer(self) -> None:
        if self.viewer is not None or self.viewer_reconnect_after_id is not None:
            return
        if getattr(self, "device_panel", None) is not None:
            self.device_panel.connect()
            return
        self.viewer_relay_options = None
        self.viewer_pressed_keys.clear()
        host = self.viewer_host.get().strip()
        if not host:
            messagebox.showerror("RemoteDesk", "请填写主机/IP。")
            return
        password = self.viewer_password.get().strip()
        if not password:
            messagebox.showerror("RemoteDesk", "设备密钥不能为空。")
            return
        port = normalize_port(self.viewer_port.get())
        self._begin_direct_viewer(host, port, password)

    def _begin_direct_viewer(self, host, port, password):
        self.viewer_relay_options = None
        self.viewer_pressed_keys.clear()
        self.viewer_reconnect_policy.begin()
        self._viewer_capture_state().begin_logical_session()
        self.viewer_reconnect_target = (host, port, password)
        self._start_viewer_attempt(host, port, password, reconnecting=False)

    def _start_viewer_attempt(
        self,
        host: str,
        port: int,
        password: str,
        *,
        reconnecting: bool,
    ) -> None:
        if self.closing or self.viewer_reconnect_policy.cancelled:
            return
        self.last_photo = None
        self.native_presenter_active = False
        self.remote_width = 1
        self.remote_height = 1
        self.display_width = 1
        self.display_height = 1
        self.last_frame_status_update = 0.0
        self._open_viewer_window(host, port)
        self.viewer_generation += 1
        self._apply_viewer_capture_target_transition(
            self._viewer_capture_state().begin_generation(
                self.viewer_generation
            )
        )
        viewer = ViewerConnection(
            host,
            port,
            password,
            self.events,
            self.viewer_generation,
            relay_options=getattr(self, "viewer_relay_options", None),
        )
        self.viewer = viewer
        if self.frame_label is not None:
            viewer.set_display_size(
                self.frame_label.winfo_width(),
                self.frame_label.winfo_height(),
            )
            try:
                viewer.set_native_presenter_target(
                    int(self.frame_label.winfo_id()),
                    str(self.frame_label.tk.call("tk", "windowingsystem")),
                    os.environ.get("DISPLAY", ""),
                )
            except (tk.TclError, TypeError, ValueError):
                viewer.set_native_presenter_target(0, "", "")
        viewer.start()
        action = "正在重新连接" if reconnecting else "正在连接"
        self._set_viewer_status(f"{action} {host}:{port}...")
        self.connect_button.config(state=tk.DISABLED)
        self.disconnect_button.config(state=tk.NORMAL)
        self._set_viewer_file_action_state(False)

    def disconnect_viewer(self) -> None:
        self.viewer_reconnect_policy.cancel()
        self.viewer_reconnect_target = None
        self._cancel_viewer_reconnect_timer()
        self._cancel_viewer_stability_timer()
        self._cancel_viewer_file_preview()
        viewer = self.viewer
        released_inputs = self._release_pressed_viewer_keys(flush=False)
        # Fence queued events and scheduled callbacks before asynchronous
        # teardown.  A slow decoder/socket close must never stall Tk's thread.
        self.viewer_generation += 1
        self._viewer_capture_state().end_logical_session()
        self.viewer = None
        if viewer is not None:
            threading.Thread(
                target=self._close_viewer_after_input_release,
                args=(viewer, released_inputs),
                name="RemoteDeskViewerClose",
                daemon=True,
            ).start()
        self._close_viewer_window()
        self._set_viewer_status("已断开。")
        self.connect_button.config(state=tk.NORMAL)
        self.disconnect_button.config(state=tk.DISABLED)
        self._set_viewer_file_action_state(False)

    @staticmethod
    def _close_viewer_after_input_release(
        viewer: ViewerConnection,
        released_inputs: int,
    ) -> None:
        try:
            if released_inputs > 0:
                viewer.flush_pending_inputs()
        finally:
            viewer.request_close()
            viewer.close()

    def _cancel_viewer_reconnect_timer(self) -> None:
        after_id = self.viewer_reconnect_after_id
        self.viewer_reconnect_after_id = None
        if after_id is None:
            return
        try:
            self.root.after_cancel(after_id)
        except (tk.TclError, ValueError):
            pass

    def _cancel_viewer_stability_timer(self) -> None:
        after_id = self.viewer_reconnect_stable_after_id
        self.viewer_reconnect_stable_after_id = None
        if after_id is None:
            return
        try:
            self.root.after_cancel(after_id)
        except (tk.TclError, ValueError):
            pass

    def _schedule_viewer_stability_reset(self) -> None:
        self._cancel_viewer_stability_timer()
        owned_viewer = self.viewer
        if owned_viewer is None or self.closing:
            return
        fenced_generation = self.viewer_generation

        def mark_stable() -> None:
            self.viewer_reconnect_stable_after_id = None
            if (
                self.closing
                or self.viewer_reconnect_policy.cancelled
                or fenced_generation != self.viewer_generation
                or self.viewer is not owned_viewer
                or owned_viewer.stop_event.is_set()
            ):
                return
            self.viewer_reconnect_policy.mark_connection_stable()

        self.viewer_reconnect_stable_after_id = self.root.after(
            int(VIEWER_RECONNECT_STABLE_SECONDS * 1000),
            mark_stable,
        )

    def _schedule_viewer_reconnect(self, delay_seconds: float) -> None:
        target = self.viewer_reconnect_target
        if target is None or self.closing:
            return
        fenced_generation = self.viewer_generation

        def reconnect() -> None:
            self.viewer_reconnect_after_id = None
            if (
                self.closing
                or self.viewer_reconnect_policy.cancelled
                or fenced_generation != self.viewer_generation
                or self.viewer is not None
                or self.viewer_reconnect_target != target
            ):
                return
            self._start_viewer_attempt(*target, reconnecting=True)

        self.viewer_reconnect_after_id = self.root.after(
            max(1, int(delay_seconds * 1000)),
            reconnect,
        )

    def _open_viewer_window(self, host: str, port: int) -> None:
        if self.viewer_window is not None:
            try:
                self.viewer_window.deiconify()
                self.viewer_window.lift()
                return
            except tk.TclError:
                self.viewer_window = None
                self.viewer_window_status = None
                self.viewer_target_bar = None
                self.viewer_target_controls_anchor = None
                self.viewer_target_selector = None
                self.viewer_target_value = None
                self.viewer_target_choices = ()
                self.viewer_window_send_file_button = None
                self.viewer_window_send_folder_button = None
                self.frame_label = None

        window = tk.Toplevel(self.root, class_="RemoteDesk")
        window.title(f"RemoteDesk - {host}:{port}")
        apply_adaptive_window_geometry(
            window,
            preferred_size=(1120, 700),
            minimum_size=(480, 320),
            parent=self.root,
        )
        window.configure(bg=VIEWER_BG)
        if self.window_icon is not None:
            try:
                window.iconphoto(True, self.window_icon)
            except tk.TclError:
                pass

        # Reserve the controls before allocating space to the image. A large
        # PhotoImage otherwise consumes the entire pack cavity on window shrink
        # and hides the text entry, clipboard/file buttons and disconnect action.
        footer = ttk.Frame(window, style="ViewerToolbar.TFrame")
        footer.pack(side=tk.BOTTOM, fill=tk.X)
        frame_label = tk.Label(
            window,
            text="正在建立加密连接…\n画面将在握手完成后显示",
            anchor=tk.CENTER,
            bg=VIEWER_BG,
            fg=VIEWER_TEXT_COLOR,
            font=("DejaVu Sans", 12),
            relief=tk.FLAT,
            bd=0,
            takefocus=True,
        )
        frame_label.pack(fill=tk.BOTH, expand=True)
        frame_label.bind("<Motion>", self._viewer_mouse_motion)
        frame_label.bind("<ButtonPress-1>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_DOWN, MOUSE_LEFT))
        frame_label.bind("<ButtonRelease-1>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_UP, MOUSE_LEFT))
        frame_label.bind("<ButtonPress-2>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_DOWN, MOUSE_MIDDLE))
        frame_label.bind("<ButtonRelease-2>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_UP, MOUSE_MIDDLE))
        frame_label.bind("<ButtonPress-3>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_DOWN, MOUSE_RIGHT))
        frame_label.bind("<ButtonRelease-3>", lambda event: self._viewer_mouse_button(event, INPUT_MOUSE_UP, MOUSE_RIGHT))
        frame_label.bind("<MouseWheel>", self._viewer_mouse_wheel)
        frame_label.bind("<Button-4>", lambda event: self._viewer_mouse_wheel(event, delta=120))
        frame_label.bind("<Button-5>", lambda event: self._viewer_mouse_wheel(event, delta=-120))
        frame_label.bind("<KeyPress>", self._viewer_key_press)
        frame_label.bind("<KeyRelease>", self._viewer_key_release)
        frame_label.bind("<FocusOut>", self._viewer_frame_focus_out, add="+")
        frame_label.bind("<Configure>", self._viewer_frame_resized, add="+")

        target_bar = ttk.Frame(
            footer,
            style="ViewerToolbar.TFrame",
            padding=(10, 7, 10, 0),
        )
        ttk.Label(
            target_bar,
            text="远程屏幕",
            style="ViewerToolbar.TLabel",
        ).pack(side=tk.LEFT, padx=(0, 8))
        target_value = tk.StringVar(value="")
        target_selector = ttk.Combobox(
            target_bar,
            textvariable=target_value,
            state=tk.DISABLED,
            style="Viewer.TCombobox",
        )
        target_selector.pack(side=tk.LEFT, fill=tk.X, expand=True)
        target_selector.bind(
            "<<ComboboxSelected>>",
            self._viewer_capture_target_selected,
        )

        controls = ttk.Frame(footer, style="ViewerToolbar.TFrame", padding=(10, 8, 10, 8))
        clipboard_bar = ttk.Frame(footer, style="ViewerToolbar.TFrame", padding=(10, 4))
        clipboard_bar.pack(fill=tk.X)
        clipboard_buttons = []
        for label, action in (("发送本机剪贴板", lambda: self.viewer_clipboard()),
                              ("取回远端剪贴板", lambda: self.viewer_clipboard(read=True)),
                              ("粘贴到远端", lambda: self.viewer_clipboard(paste=True))):
            clipboard_buttons.append(ttk.Button(clipboard_bar, text=label, command=action, style="Viewer.TButton"))
        clipboard_columns = 0
        def update_clipboard_layout(event: Any = None) -> None:
            nonlocal clipboard_columns
            available = max(1, (event.width if event is not None else clipboard_bar.winfo_width()) - 20)
            button_width = max(button.winfo_reqwidth() for button in clipboard_buttons) + 6
            columns = max(1, min(3, available // max(1, button_width)))
            if columns == clipboard_columns:
                return
            clipboard_columns = columns
            for index, button in enumerate(clipboard_buttons):
                clipboard_bar.columnconfigure(index, weight=1 if index < columns else 0)
                button.grid(row=index // columns, column=index % columns, sticky=tk.EW, padx=(0, 6), pady=2)
        clipboard_bar.bind("<Configure>", update_clipboard_layout, add="+")
        clipboard_bar.after_idle(update_clipboard_layout)
        controls.pack(fill=tk.X)
        entry = ttk.Entry(
            controls,
            textvariable=self.text_input,
            style="Viewer.TEntry",
        )
        entry.bind("<Return>", lambda _event: self.send_viewer_text())
        viewer_window_send_file_button = ttk.Button(
            controls,
            text="发送文件",
            command=self.send_viewer_files,
            state=tk.DISABLED,
            style="Viewer.TButton",
        )
        viewer_window_send_folder_button = ttk.Button(
            controls,
            text="发送文件夹",
            command=self.send_viewer_folder,
            state=tk.DISABLED,
            style="Viewer.TButton",
        )
        upscale_button = ttk.Button(controls, text="新版放大：关", style="Viewer.TButton")
        self.viewer_upscale_button = upscale_button
        self.viewer_experimental_upscaling = False

        def toggle_upscaling() -> None:
            viewer = self.viewer
            if viewer is None:
                self._set_viewer_status("请先连接设备；新版放大默认关闭。")
                return
            enabled = not self.viewer_experimental_upscaling
            upscale_button.configure(state=tk.DISABLED)
            threading.Thread(target=viewer.set_experimental_upscaling, args=(enabled,),
                name="RemoteDeskUpscaleSwitch", daemon=True).start()

        upscale_button.configure(command=toggle_upscaling)
        viewer_buttons = (
            ttk.Button(
                controls,
                text="发送文本",
                command=self.send_viewer_text,
                style="Viewer.TButton",
            ),
            viewer_window_send_file_button,
            viewer_window_send_folder_button,
            upscale_button,
            ttk.Button(
                controls,
                text="断开",
                command=self.disconnect_viewer,
                style="ViewerDanger.TButton",
            ),
        )
        toolbar_layout_state = {"mode": ""}

        def update_toolbar_layout(event: tk.Event[Any] | None = None) -> None:
            available_width = int(getattr(event, "width", controls.winfo_width())) - 20
            actions_width = sum(button.winfo_reqwidth() + 8 for button in viewer_buttons)
            mode = "wide" if available_width >= actions_width + entry.winfo_reqwidth() + 8 else "medium" if available_width >= actions_width else "compact"
            if mode == toolbar_layout_state["mode"]:
                return
            toolbar_layout_state["mode"] = mode
            entry.grid_forget()
            for button in viewer_buttons:
                button.grid_forget()
            for column in range(len(viewer_buttons) + 1):
                controls.columnconfigure(column, weight=0)

            if mode == "wide":
                entry.grid(row=0, column=0, sticky=tk.EW, padx=(0, 8))
                controls.columnconfigure(0, weight=1)
                for column, button in enumerate(viewer_buttons, start=1):
                    button.grid(row=0, column=column, padx=(0 if column == 1 else 8, 0))
            elif mode == "medium":
                entry.grid(row=0, column=0, columnspan=len(viewer_buttons), sticky=tk.EW, pady=(0, 8))
                for column, button in enumerate(viewer_buttons):
                    button.grid(row=1, column=column, sticky=tk.EW, padx=(0 if column == 0 else 4, 4))
                    controls.columnconfigure(column, weight=1)
            else:
                entry.grid(row=0, column=0, columnspan=2, sticky=tk.EW, pady=(0, 8))
                for index, button in enumerate(viewer_buttons):
                    button.grid(
                        row=1 + index // 2,
                        column=index % 2,
                        sticky=tk.EW,
                        padx=(0 if index % 2 == 0 else 4, 4 if index % 2 == 0 else 0),
                        pady=(0, 6),
                    )
                controls.columnconfigure(0, weight=1)
                controls.columnconfigure(1, weight=1)

        controls.bind("<Configure>", update_toolbar_layout, add="+")
        controls.after_idle(update_toolbar_layout)

        status = ttk.Label(footer, text="正在连接...", style="ViewerStatus.TLabel")
        status.pack(fill=tk.X)
        window.bind("<Unmap>", self._viewer_window_unmapped, add="+")
        window.protocol("WM_DELETE_WINDOW", self._viewer_window_closed)

        self.viewer_window = window
        self.viewer_window_status = status
        self.viewer_target_bar = target_bar
        self.viewer_target_controls_anchor = controls
        self.viewer_target_selector = target_selector
        self.viewer_target_value = target_value
        self.viewer_target_choices = ()
        self.viewer_window_send_file_button = viewer_window_send_file_button
        self.viewer_window_send_folder_button = viewer_window_send_folder_button
        self.frame_label = frame_label
        try:
            window.update_idletasks()
            if self.viewer is not None:
                self.viewer.set_display_size(frame_label.winfo_width(), frame_label.winfo_height())
            window.lift()
            frame_label.focus_set()
        except tk.TclError:
            pass

    def _viewer_frame_resized(self, event: tk.Event[Any]) -> None:
        viewer = self.viewer
        if viewer is not None:
            viewer.set_display_size(int(event.width), int(event.height))
        if self.native_presenter_active:
            self.display_width, self.display_height = calculate_fitted_image_size(
                self.remote_width,
                self.remote_height,
                max(1, int(event.width)),
                max(1, int(event.height)),
            )
            return
        if (
            self.last_photo is not None
            and (self.last_photo.width() > int(event.width) or self.last_photo.height() > int(event.height))
            and self.frame_label is not None
        ):
            self.last_photo = None
            self.display_width = 1
            self.display_height = 1
            self.frame_label.config(image="", text="正在调整画面...")

    def _viewer_window_closed(self) -> None:
        self.disconnect_viewer()

    def _close_viewer_window(self) -> None:
        window = self.viewer_window
        self.viewer_window = None
        self.viewer_window_status = None
        self.viewer_target_bar = None
        self.viewer_target_controls_anchor = None
        self.viewer_target_selector = None
        self.viewer_target_value = None
        self.viewer_target_choices = ()
        self.viewer_target_programmatic_update = False
        self.viewer_window_send_file_button = None
        self.viewer_window_send_folder_button = None
        self.frame_label = None
        self.last_photo = None
        self.native_presenter_active = False
        self.viewer_pressed_keys.clear()
        if window is not None:
            try:
                window.destroy()
            except tk.TclError:
                pass

    def _set_viewer_status(self, text: str) -> None:
        try:
            self.viewer_status.config(text=text)
        except tk.TclError:
            pass
        if self.viewer_window_status is not None:
            try:
                self.viewer_window_status.config(text=text)
            except tk.TclError:
                self.viewer_window_status = None

    def _viewer_capture_target_selected(self, _event: tk.Event[Any]) -> None:
        if getattr(self, "viewer_target_programmatic_update", False):
            return
        selector = getattr(self, "viewer_target_selector", None)
        if selector is None:
            return
        try:
            index = selector.current()
        except tk.TclError:
            return
        choices = getattr(self, "viewer_target_choices", ())
        if index < 0 or index >= len(choices):
            return
        choice = choices[index]
        if not choice.available or choice.target_id is None:
            self._apply_viewer_capture_target_transition(
                self._viewer_capture_state()._transition()
            )
            return
        transition = self._viewer_capture_state().choose_target(
            self.viewer_generation,
            choice.target_id,
        )
        self._apply_viewer_capture_target_transition(transition)

    def _viewer_capture_state(self) -> ViewerCaptureTargetState:
        state = getattr(self, "viewer_capture_target_state", None)
        if state is None:
            state = ViewerCaptureTargetState()
            state.begin_logical_session()
            generation = getattr(self, "viewer_generation", None)
            if isinstance(generation, int):
                state.begin_generation(generation)
            self.viewer_capture_target_state = state
        return state

    def _apply_viewer_capture_target_transition(
        self,
        transition: ViewerCaptureTargetTransition,
    ) -> None:
        selector = getattr(self, "viewer_target_selector", None)
        target_bar = getattr(self, "viewer_target_bar", None)
        controls_anchor = getattr(
            self,
            "viewer_target_controls_anchor",
            None,
        )
        self.viewer_target_choices = transition.choices
        if selector is not None:
            labels = tuple(choice.label for choice in transition.choices)
            selected_index = next(
                (
                    index
                    for index, choice in enumerate(transition.choices)
                    if choice.target_id == transition.selected_target_id
                ),
                -1,
            )
            self.viewer_target_programmatic_update = True
            try:
                selector.configure(
                    values=labels,
                    state="readonly" if transition.enabled else tk.DISABLED,
                )
                if selected_index >= 0:
                    selector.current(selected_index)
                elif getattr(self, "viewer_target_value", None) is not None:
                    self.viewer_target_value.set("")
            except tk.TclError:
                pass
            finally:
                self.viewer_target_programmatic_update = False
        if target_bar is not None:
            try:
                if transition.visible:
                    if not target_bar.winfo_manager():
                        if controls_anchor is not None:
                            target_bar.pack(
                                fill=tk.X,
                                before=controls_anchor,
                            )
                        else:
                            target_bar.pack(fill=tk.X)
                elif target_bar.winfo_manager():
                    target_bar.pack_forget()
            except tk.TclError:
                pass
        if transition.status:
            self._set_viewer_status(transition.status)
        if transition.select_target_id is None:
            return
        viewer = getattr(self, "viewer", None)
        queued = (
            viewer is not None
            and viewer.generation == self.viewer_generation
            and viewer.queue_capture_target_selection(
                transition.select_target_id
            )
        )
        if not queued:
            failed = self._viewer_capture_state().selection_enqueue_failed(
                self.viewer_generation,
                transition.select_target_id,
            )
            # This failure transition cannot itself schedule a request.
            self._apply_viewer_capture_target_transition(failed)

    def _unpack_viewer_event(self, value: Any) -> Any | None:
        if not isinstance(value, tuple) or len(value) != 2:
            return None
        generation, payload = value
        if generation != self.viewer_generation:
            return None
        return payload

    def _set_viewer_file_action_state(self, enabled: bool) -> None:
        enabled = bool(enabled) and not getattr(
            self,
            "viewer_file_preview_active",
            False,
        ) and not getattr(self, "closing", False)
        state = tk.NORMAL if enabled else tk.DISABLED
        for attribute in (
            "send_file_button",
            "send_folder_button",
            "viewer_window_send_file_button",
            "viewer_window_send_folder_button",
        ):
            button = getattr(self, attribute, None)
            if button is None:
                continue
            try:
                button.config(state=state)
            except tk.TclError:
                pass

    @staticmethod
    def _viewer_has_capability(viewer: Any, capability: int) -> bool:
        if viewer is None:
            return False
        try:
            return bool(viewer.remote_capabilities & capability)
        except (AttributeError, TypeError):
            return False

    def _cancel_viewer_file_preview(self) -> None:
        cancel_event = getattr(self, "viewer_file_preview_cancel_event", None)
        if cancel_event is not None:
            cancel_event.set()
        self.viewer_file_preview_token = (
            int(getattr(self, "viewer_file_preview_token", 0)) + 1
        )
        self.viewer_file_preview_cancel_event = None
        self.viewer_file_preview_active = False

    def _begin_viewer_file_transfer_preview(
        self,
        paths: list[str],
        title: str,
        action_text: str,
        queued_status: str,
    ) -> bool:
        if getattr(self, "viewer_file_preview_active", False):
            self._set_viewer_status("正在读取上一批传输项，请稍候。")
            return False
        viewer = self.viewer
        if not self._viewer_has_capability(
            viewer,
            CAPABILITY_FILE_RECEIVE,
        ):
            self._set_viewer_status("远端未声明文件接收能力。")
            return False

        token = int(getattr(self, "viewer_file_preview_token", 0)) + 1
        generation = self.viewer_generation
        cancel_event = threading.Event()
        self.viewer_file_preview_token = token
        self.viewer_file_preview_cancel_event = cancel_event
        self.viewer_file_preview_active = True
        self._set_viewer_file_action_state(False)
        self._set_viewer_status("正在后台读取传输项大小…")

        worker = threading.Thread(
            target=self._prepare_viewer_file_transfer_preview,
            args=(
                self.events,
                list(paths),
                title,
                action_text,
                queued_status,
                viewer,
                generation,
                token,
                cancel_event,
            ),
            name=VIEWER_FILE_PREVIEW_WORKER_NAME,
            daemon=True,
        )
        worker.start()
        return True

    @staticmethod
    def _prepare_viewer_file_transfer_preview(
        events: queue.Queue,
        paths: list[str],
        title: str,
        action_text: str,
        queued_status: str,
        viewer: ViewerConnection,
        generation: int,
        token: int,
        cancel_event: threading.Event,
    ) -> None:
        normalized_paths: list[str] = []
        items: list[FileTransferPreviewItem] = []
        note = ""
        error = ""
        try:
            normalized_paths, items, note = create_file_transfer_preview(
                paths,
                cancel_event=cancel_event,
            )
            if items:
                directory, location_note = ViewerConnection.get_file_receive_location(viewer, cancel_event)
                items = [replace(item, destination_path=format_remote_receive_destination(item.transfer_name, directory)) for item in items]
                device = getattr(viewer, "remote_device_info", {}).get("machineName", "当前远端设备")
                note = f"接收设备：{device}\n{location_note}\n{note}"
        except TransferCancelledError:
            return
        except Exception as ex:
            error = str(ex)
        if cancel_event.is_set():
            return
        put_viewer_event(
            events,
            "viewer_file_preview_ready",
            generation,
            ViewerFilePreviewResult(
                token,
                viewer,
                tuple(normalized_paths),
                tuple(items),
                note,
                title,
                action_text,
                queued_status,
                error,
            ),
        )

    def _finish_viewer_file_transfer_preview(
        self,
        result: ViewerFilePreviewResult,
    ) -> None:
        cancel_event = getattr(self, "viewer_file_preview_cancel_event", None)
        if (
            not getattr(self, "viewer_file_preview_active", False)
            or result.token != getattr(self, "viewer_file_preview_token", -1)
            or result.viewer is not self.viewer
            or result.viewer.generation != self.viewer_generation
            or self.closing
            or cancel_event is None
            or cancel_event.is_set()
        ):
            return

        try:
            parent = (
                self.viewer_window
                if self.viewer_window is not None
                else self.root
            )
            if result.error:
                messagebox.showerror(
                    "RemoteDesk",
                    f"读取传输项失败：{result.error}",
                    parent=parent,
                )
                return
            if not result.items:
                self._set_viewer_status("没有可发送的文件或文件夹。")
                return
            if not self._show_file_transfer_confirmation_dialog(
                parent,
                result.title,
                result.action_text,
                list(result.items),
                result.note,
            ):
                self._set_viewer_status("已取消文件传输。")
                return
            if (
                result.viewer is not self.viewer
                or result.viewer.generation != self.viewer_generation
                or self.closing
                or cancel_event.is_set()
            ):
                return
            if result.viewer.send_files(list(result.normalized_paths)):
                self._set_viewer_status(result.queued_status)
            else:
                self._set_viewer_status("文件发送未开始。")
        except Exception as ex:
            self._set_viewer_status(f"文件发送失败：{ex}")
        finally:
            if result.token == getattr(self, "viewer_file_preview_token", -1):
                self.viewer_file_preview_cancel_event = None
                self.viewer_file_preview_active = False
                viewer = self.viewer
                self._set_viewer_file_action_state(
                    self._viewer_has_capability(
                        viewer,
                        CAPABILITY_FILE_RECEIVE,
                    )
                )

    def _show_file_transfer_results(self, details: str) -> None:
        parent = self.viewer_window or self.root
        dialog = tk.Toplevel(parent, class_="RemoteDesk")
        dialog.title("文件传输结果")
        dialog.transient(parent)
        dialog.geometry(f"{min(900, dialog.winfo_screenwidth() - 80)}x{min(480, dialog.winfo_screenheight() - 100)}")
        content = ttk.Frame(dialog, padding=12)
        content.pack(fill=tk.BOTH, expand=True)
        content.rowconfigure(0, weight=1)
        content.columnconfigure(0, weight=1)
        text = tk.Text(content, wrap=tk.NONE, width=60, height=12)
        text.insert("1.0", details)
        text.configure(state=tk.DISABLED)
        text.grid(row=0, column=0, sticky="nsew")
        vertical = ttk.Scrollbar(content, orient=tk.VERTICAL, command=text.yview)
        horizontal = ttk.Scrollbar(content, orient=tk.HORIZONTAL, command=text.xview)
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal.grid(row=1, column=0, sticky="ew")
        text.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        ttk.Button(content, text="知道了", command=dialog.destroy).grid(row=2, column=0, sticky="e", pady=(10, 0))
        dialog.bind("<Escape>", lambda _event: dialog.destroy())

    def _show_file_transfer_confirmation_dialog(
        self,
        parent: tk.Misc,
        title: str,
        action_text: str,
        items: list[FileTransferPreviewItem],
        note: str,
    ) -> bool:
        result = {"confirmed": False}
        dialog = tk.Toplevel(parent, class_="RemoteDesk")
        dialog.title(title)
        dialog.configure(bg=APP_BG)
        dialog.resizable(True, True)
        if self.window_icon is not None:
            try:
                dialog.iconphoto(True, self.window_icon)
            except tk.TclError:
                pass

        content = ttk.Frame(dialog, padding=14, style="App.TFrame")
        content.pack(fill=tk.BOTH, expand=True)
        content.columnconfigure(0, weight=1)
        # Reserve the choices before allocating space to the file list. Pack's
        # first-come allocation previously hid both choices at 480x360 / 200%.
        # The two scrollable regions can shrink without losing any file paths.
        content.rowconfigure(1, weight=2)
        content.rowconfigure(2, weight=3)
        total_bytes = sum(max(0, item.size_bytes) for item in items)
        summary = ttk.Label(
            content,
            text=f"{action_text}\n共 {len(items)} 项，合计 {format_transfer_bytes(total_bytes)}。",
            style="Status.TLabel",
            padding=0,
            wraplength=820,
            justify=tk.LEFT,
        )
        summary.grid(row=0, column=0, sticky=tk.EW, pady=(0, 8))

        table_frame = ttk.Frame(content, style="App.TFrame")
        table_frame.grid(row=1, column=0, sticky=tk.NSEW)
        table_frame.grid_propagate(False)
        columns = ("kind", "size", "name", "source", "destination")
        tree = ttk.Treeview(table_frame, columns=columns, show="headings", height=min(10, max(4, len(items))))
        tree.heading("kind", text="类型")
        tree.heading("size", text="大小")
        tree.heading("name", text="文件名")
        tree.heading("source", text="原始位置")
        tree.heading("destination", text="传输后位置")
        tree.column("kind", width=70, minwidth=58, stretch=False)
        tree.column("size", width=90, minwidth=76, stretch=False, anchor=tk.E)
        tree.column("name", width=160, minwidth=100)
        tree.column("source", width=330, minwidth=180)
        tree.column("destination", width=300, minwidth=180)
        vertical_scrollbar = ttk.Scrollbar(table_frame, orient=tk.VERTICAL, command=tree.yview)
        horizontal_scrollbar = ttk.Scrollbar(table_frame, orient=tk.HORIZONTAL, command=tree.xview)
        tree.configure(yscrollcommand=vertical_scrollbar.set, xscrollcommand=horizontal_scrollbar.set)
        tree.grid(row=0, column=0, sticky=tk.NSEW)
        vertical_scrollbar.grid(row=0, column=1, sticky=tk.NS)
        horizontal_scrollbar.grid(row=1, column=0, sticky=tk.EW)
        table_frame.rowconfigure(0, weight=1)
        table_frame.columnconfigure(0, weight=1)
        row_height = int(ttk.Style(dialog).lookup("Treeview", "rowheight") or 24)
        content.rowconfigure(1, minsize=2 * row_height + horizontal_scrollbar.winfo_reqheight())
        for item in items:
            tree.insert(
                "",
                tk.END,
                values=(item.kind, format_transfer_bytes(item.size_bytes), item.transfer_name, item.source_path, item.destination_path),
            )

        details_frame = ttk.Frame(content)
        details_frame.grid(row=2, column=0, sticky=tk.NSEW, pady=(8, 0))
        details_frame.grid_propagate(False)
        details_frame.columnconfigure(0, weight=1)
        details_frame.rowconfigure(0, weight=1)
        selected_details = tk.Text(details_frame, height=3, wrap=tk.CHAR, state=tk.DISABLED)
        details_scroll = ttk.Scrollbar(details_frame, orient=tk.VERTICAL, command=selected_details.yview)
        selected_details.configure(yscrollcommand=details_scroll.set)
        details_scroll.grid(row=0, column=1, sticky=tk.NS)
        selected_details.grid(row=0, column=0, sticky=tk.NSEW)

        def show_selected_details(_event: Any = None) -> None:
            selection = tree.selection()
            if not selection:
                return
            item = items[tree.index(selection[0])]
            selected_details.configure(state=tk.NORMAL)
            selected_details.delete("1.0", tk.END)
            selected_details.insert("1.0", f"文件名：{item.transfer_name}\n接收位置：{item.destination_path}\n原始位置：{item.source_path}"
                                    + (f"\n说明：{note}" if note else ""))
            selected_details.configure(state=tk.DISABLED)
        tree.bind("<<TreeviewSelect>>", show_selected_details)
        if tree.get_children():
            tree.selection_set(tree.get_children()[0])
            show_selected_details()

        buttons = ttk.Frame(content, style="App.TFrame")
        buttons.grid(row=3, column=0, sticky=tk.EW, pady=(10, 0))

        def accept() -> None:
            result["confirmed"] = True
            dialog.destroy()

        def cancel() -> None:
            result["confirmed"] = False
            dialog.destroy()

        ttk.Button(buttons, text="取消", command=cancel).pack(side=tk.RIGHT)
        ttk.Button(buttons, text="开始传输", command=accept, style="Accent.TButton").pack(side=tk.RIGHT, padx=(0, 8))
        dialog.protocol("WM_DELETE_WINDOW", cancel)
        dialog.bind("<Escape>", lambda _event: cancel())
        dialog.bind("<Return>", lambda _event: accept())
        dialog.transient(parent)
        dialog.grab_set()
        dialog.update_idletasks()
        apply_adaptive_window_geometry(
            dialog,
            preferred_size=(
                max(840, dialog.winfo_reqwidth()),
                max(420, min(620, dialog.winfo_reqheight())),
            ),
            minimum_size=(480, 300),
            parent=parent,
        )

        def update_dialog_wrap(event: tk.Event[Any]) -> None:
            wrap_length = max(240, int(event.width) - 28)
            summary.configure(wraplength=wrap_length)

        content.bind("<Configure>", update_dialog_wrap, add="+")
        dialog.wait_window()
        return bool(result["confirmed"])

    def _local_clipboard_text(self) -> str | None:
        try:
            return self.root.clipboard_get()
        except tk.TclError:
            return None

    def viewer_clipboard(self, *, read: bool = False, paste: bool = False,
                         after_copy: bool = False, paste_shift: bool = False) -> None:
        viewer = self.viewer
        if viewer is None:
            return
        baseline = self._local_clipboard_text()
        if not read:
            self._release_pressed_viewer_inputs(flush=True, background_flush=True)
        viewer.request_clipboard(read=read, text=baseline or "", baseline=baseline,
                                 paste=paste, after_copy=after_copy, paste_shift=paste_shift)

    def _apply_viewer_clipboard(self, value: Any) -> None:
        if self.viewer is None:
            return
        request, text = value
        if not text or not self.viewer.can_apply_clipboard(request, self._local_clipboard_text()):
            self._set_viewer_status("剪贴板请求已过期，或本机已复制新内容；未覆盖本机剪贴板。")
            return
        try:
            self.root.clipboard_clear()
            self.root.clipboard_append(text)
            self._set_viewer_status("已将远端文字复制到本机剪贴板。")
        except tk.TclError:
            self._set_viewer_status("写入本机剪贴板失败，请重试。")

    def send_viewer_text(self) -> None:
        viewer = self.viewer
        if viewer is None:
            return
        text = self.text_input.get()
        if not text:
            return
        try:
            sent = viewer.send_text(text)
            self._set_viewer_status(f"Sent {sent} text character(s).")
        except Exception as ex:
            self._set_viewer_status(f"Text input failed: {ex}")

    def send_viewer_files(self) -> None:
        viewer = self.viewer
        if viewer is None:
            return
        if getattr(self, "viewer_file_preview_active", False):
            self._set_viewer_status("正在读取上一批传输项，请稍候。")
            return
        paths = filedialog.askopenfilenames(title="选择要发送到远端的文件")
        if not paths:
            return
        self._begin_viewer_file_transfer_preview(
            list(paths),
            "确认发送文件",
            "即将发送以下文件到远端。",
            f"已加入发送队列：{len(paths)} 个文件。",
        )

    def send_viewer_folder(self) -> None:
        viewer = self.viewer
        if viewer is None:
            return
        if getattr(self, "viewer_file_preview_active", False):
            self._set_viewer_status("正在读取上一批传输项，请稍候。")
            return
        path = filedialog.askdirectory(title="选择要发送到远端的文件夹")
        if not path:
            return
        self._begin_viewer_file_transfer_preview(
            [path],
            "确认发送文件夹",
            "即将发送以下文件夹到远端。",
            "已加入发送队列：1 个文件夹。",
        )

    def _viewer_mouse_motion(self, event: tk.Event[Any]) -> None:
        now = time.monotonic()
        if now - self.last_motion_sent < MOUSE_MOTION_INTERVAL_SECONDS:
            return
        self.last_motion_sent = now
        self._send_pointer_input(INPUT_MOUSE_MOVE, MOUSE_NONE, event)

    def _viewer_mouse_button(self, event: tk.Event[Any], kind: int, button: int) -> None:
        try:
            event.widget.focus_set()
        except tk.TclError:
            pass
        self._send_pointer_input(kind, button, event)

    def _viewer_mouse_wheel(self, event: tk.Event[Any], delta: int | None = None) -> None:
        try:
            event.widget.focus_set()
        except tk.TclError:
            pass
        wheel_delta = delta if delta is not None else getattr(event, "delta", 0)
        self._send_pointer_input(INPUT_MOUSE_WHEEL, MOUSE_NONE, event, data=wheel_delta)

    def _viewer_key_press(self, event: tk.Event[Any]) -> str:
        virtual_key = tk_event_to_windows_virtual_key(event)
        if virtual_key == 0x56 and getattr(event, "state", 0) & 0x4:
            self.viewer_clipboard(paste=True, paste_shift=bool(event.state & 0x1))
            self.viewer_clipboard_paste_key = True
            return "break"
        if virtual_key is not None and self._send_virtual_key_input(
            INPUT_KEY_DOWN,
            virtual_key,
        ):
            self.viewer_pressed_keys.observe_down(virtual_key)
        return "break"

    def _viewer_key_release(self, event: tk.Event[Any]) -> str:
        virtual_key = tk_event_to_windows_virtual_key(event)
        if virtual_key == 0x56 and getattr(self, "viewer_clipboard_paste_key", False):
            self.viewer_clipboard_paste_key = False
            return "break"
        if virtual_key is not None and self._send_virtual_key_input(
            INPUT_KEY_UP,
            virtual_key,
        ):
            self.viewer_pressed_keys.observe_up(virtual_key)
            if virtual_key in (0x43, 0x58) and getattr(event, "state", 0) & 0x4:
                self.viewer_clipboard(read=True, after_copy=True)
        return "break"

    def _viewer_frame_focus_out(self, _event: tk.Event[Any]) -> None:
        self._release_pressed_viewer_inputs(
            flush=True,
            background_flush=True,
        )

    def _viewer_window_unmapped(self, event: tk.Event[Any]) -> None:
        if getattr(event, "widget", None) is self.viewer_window:
            self._release_pressed_viewer_inputs(
                flush=True,
                background_flush=True,
            )

    def _release_pressed_viewer_keys(self, flush: bool = False) -> int:
        return self._release_pressed_viewer_inputs(flush=flush)

    def _release_pressed_viewer_inputs(
        self,
        flush: bool = False,
        background_flush: bool = False,
    ) -> int:
        viewer = self.viewer
        if viewer is None:
            self.viewer_pressed_keys.clear()
            return 0

        released = 0
        for button, x, y in self.viewer_pressed_keys.mouse_buttons_in_release_order():
            try:
                queued = viewer.send_input(
                    INPUT_MOUSE_UP,
                    button=button,
                    x=x,
                    y=y,
                )
            except Exception:
                queued = False
            if queued:
                self.viewer_pressed_keys.observe_pointer(
                    INPUT_MOUSE_UP,
                    button,
                    x,
                    y,
                )
                released += 1
        for virtual_key in self.viewer_pressed_keys.keys_in_release_order():
            if self._send_virtual_key_input(
                INPUT_KEY_UP,
                virtual_key,
                report_failure=False,
            ):
                self.viewer_pressed_keys.observe_up(virtual_key)
                released += 1
        if flush and released:
            if background_flush:
                threading.Thread(
                    target=viewer.flush_pending_inputs,
                    name="RemoteDeskInputReleaseFlush",
                    daemon=True,
                ).start()
            else:
                viewer.flush_pending_inputs()
        return released

    def _send_virtual_key_input(
        self,
        kind: int,
        virtual_key: int,
        report_failure: bool = True,
    ) -> bool:
        viewer = self.viewer
        if viewer is None:
            return False
        if not (viewer.remote_capabilities & CAPABILITY_INPUT_CONTROL):
            if report_failure:
                self._set_viewer_status("Remote does not advertise input control.")
            return False
        try:
            queued = viewer.send_input(kind, data=virtual_key)
            if not queued and report_failure:
                self._set_viewer_status("Input queue is full or connection is not ready.")
            return queued
        except Exception as ex:
            if report_failure:
                self._set_viewer_status(f"Input failed: {ex}")
            return False

    def _send_pointer_input(
        self,
        kind: int,
        button: int,
        event: tk.Event[Any],
        data: int = 0,
    ) -> bool:
        viewer = self.viewer
        if viewer is None:
            return False
        if not (viewer.remote_capabilities & CAPABILITY_INPUT_CONTROL):
            self._set_viewer_status("Remote does not advertise input control.")
            return False
        remote_point = self._pointer_event_to_remote(event)
        if remote_point is None:
            return False
        x, y = remote_point
        try:
            queued = viewer.send_input(kind, button=button, x=x, y=y, data=data)
            if queued:
                self.viewer_pressed_keys.observe_pointer(
                    kind,
                    button,
                    x,
                    y,
                )
            if not queued and kind != INPUT_MOUSE_MOVE:
                self._set_viewer_status("Input queue is full or connection is not ready.")
            return queued
        except Exception as ex:
            self._set_viewer_status(f"Input failed: {ex}")
            return False

    def _pointer_event_to_remote(self, event: tk.Event[Any]) -> tuple[int, int] | None:
        frame_label = self.frame_label
        if (self.last_photo is None and not self.native_presenter_active) or frame_label is None:
            return None
        image_width = max(1, self.display_width)
        image_height = max(1, self.display_height)
        widget_width = max(1, frame_label.winfo_width())
        widget_height = max(1, frame_label.winfo_height())
        image_left = (widget_width - image_width) // 2
        image_top = (widget_height - image_height) // 2
        local_x = int(event.x) - image_left
        local_y = int(event.y) - image_top
        if local_x < 0 or local_y < 0 or local_x >= image_width or local_y >= image_height:
            return None
        x = round(local_x * (self.remote_width - 1) / max(1, image_width - 1))
        y = round(local_y * (self.remote_height - 1) / max(1, image_height - 1))
        return x, y

    def _poll_events(self) -> None:
        latest_frame: tuple[int, int, bytes, float, int, int, str] | None = None
        latest_native_frame: tuple[int, int, float, str] | None = None
        latest_file_preview: ViewerFilePreviewResult | None = None
        for event, value in iter_ui_event_batch(self.events):
            if event == "relay_setup":
                self._handle_relay_setup(value)
            elif event == "relay_status":
                connector, message = value
                if connector is self.relay_host and not self.relay_setup_busy:
                    self.relay_status.config(text=str(message))
            elif event == "relay_addresses":
                generation, options, target = value
                if generation != self.relay_refresh_generation:
                    continue
                self.relay_refreshing = False
                if options != self.relay_options:
                    continue
                self._show_relay_addresses(options, target)
            elif event == "relay_rename":
                self._complete_relay_rename(value)
            elif event == "relay_directory":
                generation, options, devices, error = value
                if generation != self.relay_refresh_generation:
                    continue
                self.relay_refreshing = False
                if options != self.relay_options:
                    self._refresh_relay()
                    continue
                self._show_relay_directory(devices)
                self.relay_status.config(text=error or f"当前 {len(devices)} 台在线；选择设备后输入目标设备密钥。")
            elif event == "host_log":
                self._append_host_log(str(value))
            elif event == "host_exited":
                if not isinstance(value, tuple) or len(value) != 3:
                    continue
                generation, process, code = value
                if (
                    generation != self.host_generation
                    or self.host_process is not process
                    or self.host_stopping_generation == generation
                ):
                    continue
                self.host_process = None
                self.host_reader_thread = None
                self._stop_relay_registration()
                self.host_status.config(text=f"Host exited with code {code}.")
                self.start_host_button.config(state=tk.NORMAL)
                self.stop_host_button.config(state=tk.DISABLED)
                self._retry_host_after_exit()
            elif event == "host_stop_completed":
                if not isinstance(value, tuple) or len(value) != 4:
                    continue
                generation, process, code, error = value
                if (
                    generation != self.host_generation
                    or self.host_process is not process
                    or self.host_stopping_generation != generation
                ):
                    continue
                self.host_process = None
                self.host_reader_thread = None
                self.host_stop_thread = None
                self.host_stopping_generation = None
                status = (
                    f"被控端停止时发生错误：{error}"
                    if error
                    else f"被控端已停止（退出码 {code}）。"
                )
                self.host_status.config(text=status)
                self.start_host_button.config(state=tk.NORMAL)
                self.stop_host_button.config(state=tk.DISABLED)
            elif event == "viewer_clipboard_text":
                clipboard = self._unpack_viewer_event(value)
                if clipboard is not None:
                    self._apply_viewer_clipboard(clipboard)
            elif event == "viewer_file_failure":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._set_viewer_status(str(message))
                    messagebox.showwarning("文件传输未完成", str(message), parent=self.viewer_window or self.root)
            elif event == "viewer_file_results":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._show_file_transfer_results(str(message))
            elif event == "viewer_upscaling":
                enabled = self._unpack_viewer_event(value)
                if enabled is not None:
                    self.viewer_experimental_upscaling = bool(enabled)
                    button = getattr(self, "viewer_upscale_button", None)
                    if button is not None and button.winfo_exists():
                        button.configure(text="新版放大：开" if enabled else "新版放大：关", state=tk.NORMAL)
            elif event == "viewer_status":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._set_viewer_status(str(message))
            elif event == "viewer_error":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._set_viewer_status(f"Connection failed: {message}")
            elif event == "viewer_auth_failed":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._cancel_viewer_stability_timer()
                    self.viewer_reconnect_policy.mark_authentication_failed()
                    self._set_viewer_status(
                        f"身份验证失败，已停止自动重连：{message}"
                    )
            elif event == "viewer_session_replaced":
                message = self._unpack_viewer_event(value)
                if message is not None:
                    self._cancel_viewer_stability_timer()
                    self.viewer_reconnect_policy.cancel()
                    self.viewer_reconnect_target = None
                    self._cancel_viewer_reconnect_timer()
                    self._set_viewer_status(f"连接已结束：{message}")
            elif event == "viewer_reconnect_qualified":
                qualified = self._unpack_viewer_event(value)
                if qualified is not None:
                    self.viewer_reconnect_policy.mark_device_info()
                    self._schedule_viewer_stability_reset()
                    self._apply_viewer_capture_target_transition(
                        self._viewer_capture_state().observe_device_info(
                            self.viewer_generation
                        )
                    )
                    viewer = self.viewer
                    if getattr(self, 'viewer_relay_options', None) is not None and self.viewer_reconnect_target is not None:
                        try:
                            relay.save_device_key(self.viewer_relay_options, self.viewer_reconnect_target[2])
                        except Exception:
                            self.relay_status.config(text='已连接，但此设备的密钥未能保存，请检查配置目录权限。')
                    if getattr(self, "device_panel", None) is not None:
                        self.device_panel.record(getattr(viewer, "remote_device_info", None))
                    self._set_viewer_file_action_state(
                        self._viewer_has_capability(
                            viewer,
                            CAPABILITY_FILE_RECEIVE,
                        )
                    )
            elif event == "viewer_device_identity":
                info = self._unpack_viewer_event(value)
                if getattr(self, "device_panel", None) is not None: self.device_panel.record(info)
            elif event == "viewer_capture_metadata":
                snapshot = self._unpack_viewer_event(value)
                if isinstance(snapshot, ViewerCaptureTargetSnapshot):
                    self._apply_viewer_capture_target_transition(
                        self._viewer_capture_state().observe_snapshot(
                            self.viewer_generation,
                            snapshot,
                        )
                    )
            elif event == "viewer_file_preview_ready":
                preview = self._unpack_viewer_event(value)
                if isinstance(preview, ViewerFilePreviewResult):
                    latest_file_preview = preview
            elif event == "viewer_closed":
                if value != self.viewer_generation:
                    continue
                self._cancel_viewer_stability_timer()
                self._cancel_viewer_file_preview()
                self.viewer = None
                # Invalidate every event still queued by this attempt before
                # deciding whether the logical viewer session continues.
                self.viewer_generation += 1
                self._apply_viewer_capture_target_transition(
                    self._viewer_capture_state().begin_generation(
                        self.viewer_generation
                    )
                )
                self._set_viewer_file_action_state(False)
                latest_frame = None
                latest_native_frame = None
                self.viewer_pressed_keys.clear()
                delay = self.viewer_reconnect_policy.next_delay()
                if delay is not None and not self.closing:
                    self.last_photo = None
                    self.native_presenter_active = False
                    if self.frame_label is not None:
                        try:
                            self.frame_label.config(
                                image="",
                                text=f"连接中断，{delay:g} 秒后重试...",
                            )
                        except tk.TclError:
                            pass
                    self._set_viewer_status(
                        f"连接中断，{delay:g} 秒后自动重连..."
                    )
                    self.connect_button.config(state=tk.DISABLED)
                    self.disconnect_button.config(state=tk.NORMAL)
                    self._schedule_viewer_reconnect(delay)
                else:
                    self.viewer_reconnect_policy.cancel()
                    self.viewer_reconnect_target = None
                    self._close_viewer_window()
                    self.connect_button.config(state=tk.NORMAL)
                    self.disconnect_button.config(state=tk.DISABLED)
            elif event == "viewer_frame":
                frame = self._unpack_viewer_event(value)
                if frame is not None:
                    latest_frame = frame
            elif event == "viewer_native_frame":
                frame = self._unpack_viewer_event(value)
                if frame is not None:
                    latest_native_frame = frame
        if latest_native_frame is not None and self.viewer is not None:
            self._show_native_frame(*latest_native_frame)
        elif latest_frame is not None and self.viewer is not None:
            (
                width,
                height,
                png,
                decode_ms,
                viewport_width,
                viewport_height,
                backend_diagnostic,
            ) = latest_frame
            self._show_frame(
                width,
                height,
                png,
                decode_ms,
                viewport_width,
                viewport_height,
                backend_diagnostic,
            )
        if latest_file_preview is not None:
            self._finish_viewer_file_transfer_preview(latest_file_preview)
        next_poll_ms = (
            EVENT_BACKLOG_POLL_MS
            if not self.events.empty()
            else EVENT_POLL_MS
        )
        self.event_poll_after_id = self.root.after(next_poll_ms, self._poll_events)

    def _append_host_log(self, text: str) -> None:
        self.host_log.insert(tk.END, text)
        removed_characters = self.host_log_lengths.append(text)
        if removed_characters:
            self.host_log.delete(
                "1.0",
                f"1.0+{removed_characters}c",
            )
        self.host_log.see(tk.END)

    def _show_frame(
        self,
        width: int,
        height: int,
        display_data: bytes,
        decode_ms: float = 0.0,
        viewport_width: int = DISPLAY_DEFAULT_WIDTH,
        viewport_height: int = DISPLAY_DEFAULT_HEIGHT,
        backend_diagnostic: str = "",
    ) -> None:
        if self.frame_label is None:
            self._open_viewer_window(self.viewer_host.get().strip() or "RemoteDesk", normalize_port(self.viewer_port.get()))
        frame_label = self.frame_label
        if frame_label is None:
            return
        current_viewport = normalize_viewer_display_size(
            frame_label.winfo_width(),
            frame_label.winfo_height(),
        )
        if current_viewport != (viewport_width, viewport_height):
            return
        try:
            photo = create_tk_frame_photo(display_data)
        except Exception as ex:
            self._set_viewer_status(f"Frame display failed: {ex}")
            return
        self.native_presenter_active = False
        self.last_photo = photo
        self.remote_width = width
        self.remote_height = height
        self.display_width = max(1, photo.width())
        self.display_height = max(1, photo.height())
        frame_label.config(image=photo, text="")
        now = time.monotonic()
        if now - self.last_frame_status_update >= FRAME_STATUS_INTERVAL_SECONDS:
            self.last_frame_status_update = now
            self._set_viewer_status(
                f"Frame {width}x{height}; displayed {self.display_width}x{self.display_height}; "
                f"decode {decode_ms:.0f} ms"
                + (f"; {backend_diagnostic}" if backend_diagnostic else "")
            )

    def _show_native_frame(
        self,
        width: int,
        height: int,
        present_ms: float,
        backend_diagnostic: str,
    ) -> None:
        frame_label = self.frame_label
        if frame_label is None:
            return
        widget_width = max(1, frame_label.winfo_width())
        widget_height = max(1, frame_label.winfo_height())
        if not self.native_presenter_active:
            # Stop Tk from repainting the last compatibility bitmap over the
            # native child surface after mpv takes ownership of this XID.
            self.last_photo = None
            try:
                frame_label.config(image="", text="")
            except tk.TclError:
                return
        self.native_presenter_active = True
        self.remote_width = max(1, int(width))
        self.remote_height = max(1, int(height))
        self.display_width, self.display_height = calculate_fitted_image_size(
            self.remote_width,
            self.remote_height,
            widget_width,
            widget_height,
        )
        now = time.monotonic()
        if now - self.last_frame_status_update >= FRAME_STATUS_INTERVAL_SECONDS:
            self.last_frame_status_update = now
            self._set_viewer_status(
                f"Frame {width}x{height}; displayed {self.display_width}x{self.display_height}; "
                f"native submit {present_ms:.0f} ms; {backend_diagnostic}"
            )

    def close(self) -> None:
        if self.closing:
            return
        self.closing = True
        self._cancel_relay_setup()
        if getattr(self, "device_panel", None) is not None: self.device_panel.close()
        pending_poll = getattr(self, "event_poll_after_id", None)
        if pending_poll is not None:
            self.root.after_cancel(pending_poll)
            self.event_poll_after_id = None
        self.disconnect_viewer()
        self.stop_host()
        try:
            self.root.destroy()
        except tk.TclError:
            pass


def main() -> int:
    try:
        instance = host_startup.AppInstance()
    except BlockingIOError:
        print("RemoteDesk 已在本用户会话中运行。", file=sys.stderr)
        return 0
    atexit.register(instance.close)
    root = tk.Tk(className="RemoteDesk")
    app = RemoteDeskLinuxApp(root)
    atexit.register(app.stop_host_at_exit)

    def close_from_signal(_signum: int, _frame: Any) -> None:
        app.close()

    for signal_name in ("SIGTERM", "SIGINT"):
        signum = getattr(signal, signal_name, None)
        if signum is not None:
            try:
                signal.signal(signum, close_from_signal)
            except Exception:
                pass

    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
