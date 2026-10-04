"""Session-owned file task progress and bounded remote-file receive worker."""
from __future__ import annotations

from collections import OrderedDict
from dataclasses import dataclass, field, replace
from pathlib import Path
import queue
import threading
import time
from uuid import uuid4

import remotedesk_protocol_probe as wire


@dataclass
class TransferTask:
    id: str
    direction: str
    name: str
    phase: str = "准备中"
    completed_bytes: int = 0
    total_bytes: int | None = None
    detail: str = ""
    terminal: bool = False
    cancellable: bool = True
    cancel: threading.Event = field(default_factory=threading.Event)
    updated_at: float = 0.0
    transfer_id: str = ""


class TransferStop:
    def __init__(self, session_stop, task):
        self.session_stop, self.task = session_stop, task

    def is_set(self):
        return self.session_stop.is_set() or self.task.cancel.is_set()


class ViewerTransferTasks:
    RECEIVE_QUEUE_BACKPRESSURE_SECONDS = .25
    FILE_KINDS = frozenset((wire.CONTROL_FILE_TRANSFER_START, wire.CONTROL_FILE_TRANSFER_CHUNK,
        wire.CONTROL_FILE_TRANSFER_CHECKSUM, wire.CONTROL_FILE_TRANSFER_COMPLETE,
        wire.CONTROL_FILE_TRANSFER_CANCEL, wire.CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW))
    # Shared legacy protocol vocabulary (RemoteViewerClient's returned-file
    # terminal policy). A per-item false status is NOT an end-of-batch marker.
    RETURN_BATCH_END_PREFIXES = ("远端文件回传完成", "远端文件回传失败")
    RETURN_REQUEST_FAILURE_PREFIXES = (
        "远端剪贴板没有可回传", "远端剪贴板文件不存在", "远端剪贴板包含文件夹",
        "读取远端文件剪贴板失败", "等待远程复制更新剪贴板超时", "远端文件回传已拒绝",
        "远端文件回传已取消", "远端文件回传进行中", "已有远端文件回传清单等待确认",
        "没有等待确认的远端文件回传", "Linux has no return files",
        "Linux could not prepare return files", "Linux could not return files",
        "Linux return file transfer cancelled", "Linux return file transfer failed",
        "Linux return file list contains no readable files", "Linux has no pending return files")

    def __init__(self, viewer):
        self.viewer = viewer
        self.lock = threading.RLock()
        self.tasks = OrderedDict()
        self.upload = None
        self.download = None
        self.receive_queue = queue.Queue(maxsize=32)
        self.worker = None
        self.closed = threading.Event()
        self.receive_overflow = threading.Event()
        self.receive_directory = None
        self.receive_confirmed = False
        self.receive_request_sent = False
        self.receive_transfer = None
        self.receive_budget = wire.IncomingFileTransferBudget()
        self.receive_paths = []
        self.receive_expected = None
        self.receive_failed = False
        self.return_deadline = 0.0
        self.return_timeout = 120.0

    def snapshot(self):
        with self.lock:
            return tuple(replace(task) for task in self.tasks.values())

    def _new(self, direction, name):
        with self.lock:
            task = TransferTask(uuid4().hex, direction, name)
            self.tasks[task.id] = task
            for key in list(self.tasks):
                if len(self.tasks) <= 100:
                    break
                if self.tasks[key].terminal:
                    del self.tasks[key]
            self.update(task)
            return task

    def update(self, task, *, force=True, **changes):
        if task is None:
            return
        with self.lock:
            if task.terminal and not changes.get("terminal", False):
                return
            for key, value in changes.items():
                setattr(task, key, value)
            now = time.monotonic()
            if force or now - task.updated_at >= .15:
                task.updated_at = now
                self.viewer._put_event("viewer_transfer_task", (self.viewer, replace(task)))

    def finish(self, task, phase, detail=""):
        self.update(task, phase=phase, detail=detail, terminal=True, cancellable=False)

    def begin_upload(self, paths):
        with self.lock:
            if self.upload is None or self.upload.terminal:
                self.upload = self._new("发送", f"{len(paths)} 项")
            return self.upload

    def check_upload(self):
        task = self.upload
        if self.viewer.stop_event.is_set():
            raise wire.TransferCancelledError("连接已结束")
        if task is not None and task.cancel.is_set():
            raise wire.TransferCancelledError("已取消文件任务")

    def cancel_task(self, task_id):
        with self.lock:
            task = self.tasks.get(task_id)
            if task is None or task.terminal:
                return False, "此任务已结束。"
            if not task.cancellable:
                return False, task.detail or "当前阶段或远端版本不支持安全取消；桌面会话保持连接。"
            task.cancel.set()
            self.update(task, phase="正在取消", cancellable=False,
                        detail="正在停止本批文件；桌面会话保持连接。")
            return True, "已请求取消此文件任务。"

    def request_return(self, directory, *, legacy_confirmed=False):
        with self.lock:
            if self.closed.is_set() or self.viewer.stop_event.is_set():
                return False, "连接已结束。"
            if not self.viewer.remote_capabilities & wire.CAPABILITY_FILE_SEND:
                return False, "远端不支持文件取回。"
            if self.download is not None:
                return False, "上一批取回尚未收到结束确认；请等待，桌面仍可使用。"
            if self.upload is not None and not self.upload.terminal:
                return False, "发送任务正在进行，请完成或取消后再取回文件。"
            preview = bool(self.viewer.remote_capabilities & wire.CAPABILITY_FILE_TRANSFER_PREVIEW)
            if not preview and not legacy_confirmed:
                return False, "旧版远端不能提供文件清单，请先确认接收目录。"
            self.receive_directory = Path(directory)
            self.receive_confirmed = not preview
            self.receive_request_sent = False
            self.receive_expected = None
            self.receive_failed = False
            self.receive_paths = []
            self.receive_budget = wire.IncomingFileTransferBudget()
            self.receive_overflow.clear()
            task = self.download = self._new("取回", "远端剪贴板文件")
            can_cancel = bool(self.viewer.remote_capabilities & wire.CAPABILITY_FILE_TRANSFER_CANCEL)
            self.update(task, phase="等待远端清单" if preview else "等待远端文件", cancellable=can_cancel or preview,
                        detail=f"保存到：{self.receive_directory}" + ("" if can_cancel else "；旧版远端不支持传输中取消。"))
            self.return_deadline = time.monotonic() + self.return_timeout
            if self.worker is None:
                self.worker = threading.Thread(target=self._receive_loop, name="RemoteDeskFileReceive", daemon=True)
                self.worker.start()
            self.receive_queue.put_nowait(("request", task.id))
            return True, "已请求远端文件清单。"

    def confirm_return(self, task_id, accepted):
        with self.lock:
            task = self.download
            if task is None or task.id != task_id or task.terminal or task.cancel.is_set():
                return False
            if not accepted:
                self.cancel_task(task_id)
                return True
            self.receive_queue.put_nowait(("confirm", task_id))
            return True

    def accept_control(self, payload):
        if not payload:
            return False
        kind = payload[0]
        if kind == wire.CONTROL_FILE_TRANSFER_CANCEL:
            control = wire.decode_control(payload)
            with self.lock:
                task = self.upload
                if task is not None and not task.terminal and task.transfer_id == control.get("transferId"):
                    task.cancel.set()
                    self.update(task, phase="远端已取消", detail=control.get("statusMessage", ""), cancellable=False)
                    return True
        if kind not in self.FILE_KINDS and kind != wire.CONTROL_FILE_TRANSFER_STATUS:
            return False
        with self.lock:
            task = self.download
            if task is None:
                # Unrequested file bytes never create files on this machine.
                return kind in self.FILE_KINDS
        # Short disk/fsync scheduling stalls are normal. Propagate bounded TCP
        # backpressure instead of cancelling an otherwise healthy batch on the
        # first full queue. Never wait with lock held: the disk worker needs it
        # to publish progress and drain this queue. The network reader remains
        # interruptible and a sustained stall still cancels safely.
        deadline = time.monotonic() + self.RECEIVE_QUEUE_BACKPRESSURE_SECONDS
        while not self.closed.is_set() and not self.viewer.stop_event.is_set():
            if (task.cancel.is_set() or task.terminal) and kind != wire.CONTROL_FILE_TRANSFER_STATUS:
                break
            try:
                self.receive_queue.put(("control", (task.id, payload)), timeout=.025)
                break
            except queue.Full:
                if time.monotonic() >= deadline:
                    self.receive_overflow.set()
                    break
        return kind in self.FILE_KINDS

    def close(self):
        self.closed.set()
        try:
            self.receive_queue.put_nowait(("close", None))
        except queue.Full:
            pass
        with self.lock:
            for task in self.tasks.values():
                if not task.terminal:
                    task.cancel.set()
                    self.finish(task, "连接已结束", "未完成的任务不会在重连后自动继续。")

    def _write(self, payload):
        if not self.closed.is_set() and not self.viewer.stop_event.is_set():
            self.viewer._write_file_control(payload)

    def _abort_incoming(self):
        transfer, self.receive_transfer = self.receive_transfer, None
        if transfer is not None:
            transfer.abort()

    def _receipt(self, payload, success, message):
        transfer_id = wire.decode_control(payload).get("transferId", "")
        self._write(wire.encode_file_transfer_receipt(transfer_id, success, message)
                    if self.viewer.remote_capabilities & wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                    else wire.encode_file_transfer_status(success, message))

    def _receive_loop(self):
        cancel_sent_for = None
        try:
            while not self.closed.is_set() and not self.viewer.stop_event.is_set():
                task = self.download
                if task is not None:
                    if self.receive_overflow.is_set():
                        task.cancel.set()
                        self.update(task, detail="本机写入队列已满，正在取消本批取回。")
                    if task.cancel.is_set() and cancel_sent_for != task.id:
                        self._abort_incoming()
                        if not self.receive_request_sent:
                            # No peer batch exists to drain when cancellation won
                            # before REQUEST acquired the transport write lock.
                            self.finish(task, "已取消", "取回请求尚未发送，已在本机取消；桌面仍可使用。")
                            with self.lock:
                                if self.download is task:
                                    self.download = None
                            continue
                        cancel_sent_for = task.id
                        self.return_deadline = time.monotonic() + self.return_timeout
                        try:
                            self._write(wire.encode_file_transfer_reject_clipboard_files())
                        except Exception as error:
                            self.finish(task, "取消确认失败", f"{error}\n本会话暂停后续取回以防串批；桌面仍可使用。")
                    if not task.terminal and time.monotonic() > self.return_deadline:
                        self._abort_incoming()
                        task.cancel.set()
                        self.finish(task, "等待远端结束超时", "远端尚未确认结束，本会话暂停后续取回以防串批；桌面仍可使用。")
                        continue
                try:
                    # No periodic wake-ups after a completed batch. close() wakes
                    # this worker explicitly; active batches still watch deadlines.
                    kind, value = self.receive_queue.get(timeout=.1 if task is not None and not task.terminal else None)
                except queue.Empty:
                    continue
                if kind == "close" or self.closed.is_set():
                    break
                task = self.download
                if task is None:
                    continue
                try:
                    if kind == "request" and value == task.id:
                        if not task.cancel.is_set():
                            self._write(wire.encode_file_transfer_request_clipboard_files())
                            self.receive_request_sent = True
                    elif kind == "confirm" and value == task.id and not task.cancel.is_set():
                        self.receive_confirmed = True
                        supported = bool(self.viewer.remote_capabilities & wire.CAPABILITY_FILE_TRANSFER_CANCEL)
                        self.update(task, phase="等待文件", cancellable=supported,
                            detail=f"保存到：{self.receive_directory}" + ("" if supported else "；旧版远端不支持传输中取消。"))
                        self._write(wire.encode_file_transfer_confirm_clipboard_files())
                    elif kind == "control" and value[0] == task.id:
                        self._receive_control(task, value[1])
                except Exception as error:
                    self._abort_incoming()
                    if kind == "request" and not isinstance(error, wire.TransferCancelledError):
                        # A transport failure may have written some/all of REQUEST;
                        # retain the barrier unless we know no frame was started.
                        self.receive_request_sent = True
                    self.receive_failed = True
                    task.cancel.set()
                    self.update(task, phase="取回失败，正在取消", detail=str(error), cancellable=False)
        finally:
            self._abort_incoming()

    def _receive_control(self, task, payload):
        control = wire.decode_control(payload)
        kind = control["kind"]
        self.return_deadline = time.monotonic() + self.return_timeout
        if kind == wire.CONTROL_FILE_TRANSFER_STATUS:
            message = control.get("statusMessage", "")
            terminal = (message.startswith(self.RETURN_BATCH_END_PREFIXES)
                        or (not control.get("success") and message.startswith(self.RETURN_REQUEST_FAILURE_PREFIXES)))
            if terminal:
                complete = (self.receive_confirmed and self.receive_transfer is None
                            and not self.receive_expected and not self.receive_failed and bool(self.receive_paths))
                self._abort_incoming()
                phase = "已取消" if task.cancel.is_set() else "已完成" if control.get("success") and complete else "未完成"
                if control.get("success") and not complete and not task.cancel.is_set():
                    message += "\n本机未完整收到并保存已确认清单，不能确认本批成功。"
                self.finish(task, phase, message + ("\n" + "\n".join(self.receive_paths) if self.receive_paths else ""))
                with self.lock:
                    if self.download is task:
                        self.download = None
            elif not control.get("success"):
                self.receive_failed = True
                self.update(task, phase="单项失败，等待本批结束", detail=message)
            return
        if task.cancel.is_set() or task.terminal:
            return
        if kind == wire.CONTROL_FILE_TRANSFER_CLIPBOARD_FILES_PREVIEW:
            if self.receive_expected is not None or self.receive_confirmed:
                raise wire.ProtocolError("远端重复或更改了待确认清单，已拒绝。")
            items = control.get("previewItems", [])
            if not items or any(not item["transferName"].strip() or item["kind"] not in ("文件", "文件夹")
                    or item["sizeBytes"] > wire.MAX_FILE_TRANSFER_BYTES
                    or (item["kind"] == "文件夹" and not item["transferName"].lower().endswith(".zip"))
                    for item in items):
                raise wire.ProtocolError("远端清单的名称、类型、文件夹 ZIP 后缀或大小无效，已拒绝。")
            # Match the wire name before sanitization: distinct unsafe names must
            # not alias one confirmed local filename. Keep duplicates as counts.
            self.receive_expected = [(item["transferName"], item["kind"], item["sizeBytes"]) for item in items]
            self.update(task, phase="等待确认", detail=f"保存到：{self.receive_directory}")
            self.viewer._put_event("viewer_return_preview", (task.id, items, str(self.receive_directory), control.get("statusMessage", "")))
            return
        if not self.receive_confirmed:
            raise wire.ProtocolError("远端在确认清单前发送了文件，已拒绝。")
        if kind == wire.CONTROL_FILE_TRANSFER_START:
            name = wire.sanitize_file_name(control["fileName"])
            if self.receive_transfer is not None:
                raise wire.ProtocolError("远端上一文件尚未结束。")
            if self.receive_expected is not None:
                expected = next((item for item in self.receive_expected if item[0] == control["fileName"]), None)
                if expected is None:
                    raise wire.ProtocolError("远端文件名称、大小或数量与已确认清单不符。")
                _name, item_kind, source_size = expected
                if item_kind == "文件" and control["fileLength"] != source_size:
                    raise wire.ProtocolError("远端文件大小与已确认清单不符。")
                # Windows defers folder traversal until confirmation (size=0);
                # Linux advertises source bytes, not compressed archive bytes.
                # Match FileTransferConfirmation.GetMaximumDirectoryArchiveSize.
                if item_kind == "文件夹" and source_size > 0:
                    overhead = max(16 * 1024 * 1024, min(64 * 1024 * 1024, source_size // 10))
                    maximum = min(wire.MAX_FILE_TRANSFER_BYTES, source_size + overhead)
                    if control["fileLength"] > maximum:
                        raise wire.ProtocolError("远端文件夹压缩包明显大于已确认清单，已拒绝。")
                self.receive_expected.remove(expected)
            self.receive_transfer = wire.start_incoming_file_transfer(payload, self.receive_directory,
                bool(self.viewer.remote_capabilities & wire.CAPABILITY_FILE_CHECKSUM), self.receive_budget)
            self.update(task, name=name, phase="接收中", completed_bytes=0, total_bytes=control["fileLength"])
            return
        transfer = self.receive_transfer
        if transfer is None:
            raise wire.ProtocolError("没有对应的已确认接收任务。")
        if kind == wire.CONTROL_FILE_TRANSFER_CHUNK:
            wire.write_incoming_file_chunk(payload, transfer)
            self.update(task, force=False, completed_bytes=transfer.bytes_received)
        elif kind == wire.CONTROL_FILE_TRANSFER_CHECKSUM:
            wire.set_expected_file_checksum(payload, transfer)
            self.update(task, phase="校验中", completed_bytes=transfer.bytes_received)
        elif kind == wire.CONTROL_FILE_TRANSFER_COMPLETE:
            with self.lock:
                if task.cancel.is_set():
                    return
                self.update(task, phase="保存中", completed_bytes=transfer.bytes_received, cancellable=False,
                            detail="正在提交已校验的文件，当前保存步骤无法撤回。")
            completed = wire.complete_incoming_file_transfer(payload, transfer)
            self.receive_transfer = None
            self.receive_paths.append(str(completed["path"]))
            self._receipt(payload, True, f"文件已保存到 Linux：{completed['path']}")
            self.update(task, phase="等待本批结束", detail="\n".join(self.receive_paths),
                        cancellable=bool(self.viewer.remote_capabilities & wire.CAPABILITY_FILE_TRANSFER_CANCEL))
        elif kind == wire.CONTROL_FILE_TRANSFER_CANCEL:
            reason = wire.cancel_incoming_file_transfer(payload, transfer)
            self.receive_transfer = None
            self.receive_failed = True
            self.update(task, phase="远端取消，等待本批结束", detail=reason)


class TransferTaskPanel:
    """A nonmodal, scrollable task list; callbacks always retain their owner."""
    def __init__(self, app):
        import tkinter as tk
        from tkinter import font as tkfont, ttk
        self.app = app
        self.rows = OrderedDict()
        self._tkfont = tkfont
        self.window = tk.Toplevel(app.root, class_="RemoteDesk")
        self.window.title("传输任务")
        self.window.geometry("800x440")
        self.window.minsize(420, 280)
        self.window.columnconfigure(0, weight=1)
        self.window.rowconfigure(0, weight=1)
        frame = ttk.Frame(self.window, padding=10)
        frame.grid(sticky="nsew")
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(0, weight=2)
        frame.rowconfigure(3, weight=1)
        self._style = ttk.Style(self.window)
        self.tree = ttk.Treeview(frame, columns=("direction", "phase", "bytes"), height=1,
                                 selectmode="browse", style="TransferTasks.Treeview")
        for column, title in (("#0", "文件 / 任务"), ("direction", "方向"), ("phase", "阶段"), ("bytes", "当前文件字节数")):
            self.tree.heading(column, text=title)
        self._configure_metrics()
        self.tree.bind("<<ThemeChanged>>", self._configure_metrics)
        self.tree.grid(row=0, column=0, sticky="nsew")
        y = ttk.Scrollbar(frame, orient="vertical", command=self.tree.yview)
        y.grid(row=0, column=1, sticky="ns")
        x = ttk.Scrollbar(frame, orient="horizontal", command=self.tree.xview)
        x.grid(row=1, column=0, sticky="ew")
        self.tree.configure(yscrollcommand=y.set, xscrollcommand=x.set)
        self.progress = ttk.Progressbar(frame, mode="determinate", maximum=1)
        self.progress.grid(row=2, column=0, columnspan=2, sticky="ew", pady=(8, 0))
        self.details = tk.Text(frame, height=1, wrap="word", state="disabled")
        self.detail_task_id = None
        self.detail_text = ""
        self.details.grid(row=3, column=0, columnspan=2, sticky="nsew", pady=8)
        self.cancel_button = ttk.Button(frame, text="取消选中任务", command=self.cancel, state="disabled")
        self.cancel_button.grid(row=4, column=0, sticky="e")
        self.tree.bind("<<TreeviewSelect>>", self.select)
        self.window.protocol("WM_DELETE_WINDOW", self.close)
        self.window.bind("<Escape>", lambda _event: self.close())

    def update(self, owner, task):
        self.rows[task.id] = (owner, task)
        values = (task.direction, task.phase, f"{task.completed_bytes:,} / {task.total_bytes:,}" if task.total_bytes is not None else f"{task.completed_bytes:,} / 待确定")
        self._fit_values(values)
        if self.tree.exists(task.id):
            self.tree.item(task.id, text=task.name, values=values)
        else:
            self.tree.insert("", "end", iid=task.id, text=task.name, values=values)
        if not self.tree.selection():
            self.tree.selection_set(task.id)
        for key in list(self.rows):
            if len(self.rows) <= 100:
                break
            if self.rows[key][1].terminal:
                del self.rows[key]
                self.tree.delete(key)
        self.select()

    def _configure_metrics(self, _event=None):
        # Treeview columns/rowheight use physical pixels, unlike point fonts.
        # Measure the inherited fonts so real X-server DPI and font changes do
        # not clip statuses inside their cells even after horizontal scrolling.
        self._row_font = self._tkfont.Font(root=self.window,
                                          font=self._style.lookup("TransferTasks.Treeview", "font"))
        heading_font = self._tkfont.Font(root=self.window,
                                       font=self._style.lookup("TransferTasks.Treeview.Heading", "font"))
        row_height = self._row_font.metrics("linespace") + 10
        if int(self._style.lookup("TransferTasks.Treeview", "rowheight") or 0) != row_height:
            self._style.configure("TransferTasks.Treeview", rowheight=row_height)
        self._measured_values = {}
        for column in ("#0", "direction", "phase", "bytes"):
            minimum = heading_font.measure(self.tree.heading(column, "text")) + 16
            # Names are deliberately bounded: complete paths are in the detail
            # pane. Status and byte columns grow to fit their actual values.
            width = max(minimum, self._row_font.measure("0") * 28) if column == "#0" else minimum
            self.tree.column(column, minwidth=minimum, width=width)
        for task_id in self.tree.get_children():
            self._fit_values(self.tree.item(task_id, "values"))

    def _fit_values(self, values):
        for column, value in zip(("direction", "phase", "bytes"), values):
            if self._measured_values.get(column) == value:
                continue
            self._measured_values[column] = value
            minimum = self._row_font.measure(value) + 16
            if minimum > self.tree.column(column, "minwidth"):
                self.tree.column(column, minwidth=minimum,
                                 width=max(minimum, self.tree.column(column, "width")))

    def select(self, _event=None):
        selection = self.tree.selection()
        row = self.rows.get(selection[0]) if selection else None
        if row is None:
            return
        owner, task = row
        enabled = owner is self.app.viewer and not owner.stop_event.is_set() and not task.terminal and task.cancellable
        self.progress.configure(maximum=max(1, task.total_bytes or 0), value=task.completed_bytes)
        self.cancel_button.configure(state="normal" if enabled else "disabled")
        self._set_details(task.id, task.detail or "准备阶段尚无可报告的字节数；取消只影响本批文件。")

    def _set_details(self, task_id, text):
        same_task = task_id == self.detail_task_id
        previous = self.detail_text
        if same_task and text == previous:
            return  # Byte-only progress must not disturb path copying/scrolling.
        selected = None
        top = 0
        horizontal = self.details.xview()[0]
        if same_task:
            ranges = self.details.tag_ranges("sel")
            if ranges:
                selected = tuple(len(self.details.get("1.0", index)) for index in ranges[:2])
            top = len(self.details.get("1.0", self.details.index("@0,0")))
        prefix = 0
        limit = min(len(previous), len(text))
        while prefix < limit and previous[prefix] == text[prefix]:
            prefix += 1
        suffix = 0
        while suffix < limit - prefix and previous[-suffix - 1] == text[-suffix - 1]:
            suffix += 1
        def map_unchanged(start, end):
            if end <= prefix:
                return start, end
            if start >= len(previous) - suffix:
                delta = len(text) - len(previous)
                return start + delta, end + delta
            return None
        def index(offset):
            # Text index arithmetic counts Unicode characters, unlike Tcl's
            # string length (UTF-16 units for astral characters such as emoji).
            return f"1.0 + {offset} chars"
        preserved = map_unchanged(*selected) if selected is not None else None
        mapped_top = map_unchanged(top, top) if same_task else None
        self.details.configure(state="normal")
        self.details.delete("1.0", "end")
        self.details.insert("1.0", text)
        if preserved is not None:
            self.details.tag_add("sel", index(preserved[0]), index(preserved[1]))
        self.details.configure(state="disabled")
        if same_task:
            self.details.yview(index(mapped_top[0] if mapped_top is not None else min(top, len(text))))
            self.details.xview_moveto(horizontal)
        else:
            self.details.yview_moveto(0)
            self.details.xview_moveto(0)
        self.detail_task_id, self.detail_text = task_id, text

    def cancel(self):
        selection = self.tree.selection()
        row = self.rows.get(selection[0]) if selection else None
        if row is None:
            return
        owner, task = row
        if owner is not self.app.viewer:
            self.app._set_viewer_status("这是上一连接的任务，不能取消当前连接的文件。")
            return
        _cancelled, message = owner.transfer_tasks.cancel_task(task.id)
        self.app._set_viewer_status(message)
        self.select()

    def close(self):
        if getattr(self.app, "transfer_task_panel", None) is self:
            self.app.transfer_task_panel = None
        self.window.destroy()
