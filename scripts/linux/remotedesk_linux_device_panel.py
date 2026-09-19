"""Tk device directory. All network and encrypted disk work stays off Tk's thread."""
import concurrent.futures
import queue
import threading
import time
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog
import remotedesk_linux_devices as model


class DevicePanel(ttk.LabelFrame):
    def __init__(self, app, parent):
        super().__init__(parent, text="附近 / 已保存设备", padding=12, style="Panel.TLabelframe")
        self.app = app; self.store = model.Store(); self.book = model.Book(); self.nearby = []
        self.messages = queue.Queue(); self.storage = concurrent.futures.ThreadPoolExecutor(max_workers=1)
        self.storage_callbacks = {}; self.storage_generation = 0; self.scan_callback = None
        self.epoch = 0; self.scanner = None; self.busy = False; self.closed = False; self.refreshed = 0
        self.network_slots = threading.BoundedSemaphore(2)
        self.selected_id = ""; self.recorded_generation = None; self.connection_auto = True; self.ready = False
        actions = ttk.Frame(self); actions.pack(fill=tk.X)
        for title, action in (("查找附近设备", lambda:self.scan()), ("探测此 IP 的端口", self.detect),
                              ("新增设备", self.add), ("修改备注", self.rename), ("删除记录", self.remove)):
            ttk.Button(actions, text=title, command=action).pack(side=tk.LEFT, padx=(0, 5))
        app._wrap_action_buttons(actions, actions.winfo_children())
        self.status = app._wrapping_label(self, text="正在读取已保存设备…"); self.status.pack(fill=tk.X, pady=6)
        self.tree = app._device_table(self, columns=("address", "state"), show="tree headings", height=4, selectmode="browse")
        self.tree.heading("#0", text="设备 / 备注"); self.tree.heading("address", text="IP / 端口"); self.tree.heading("state", text="状态")
        self.tree.column("#0", width=220); self.tree.column("address", width=210); self.tree.column("state", width=180)
        self.rows = {}
        # Programmatic selection during refresh must not overwrite edited fields.
        self.tree.bind("<ButtonRelease-1>", self.fill_selection)
        self.tree.bind("<KeyRelease-Up>", self.fill_selection)
        self.tree.bind("<KeyRelease-Down>", self.fill_selection)
        self.tree.bind("<Double-1>", lambda _: self.connect_selected())
        ttk.Button(self, text="连接选中设备", command=self.connect_selected, style="Accent.TButton").pack(anchor=tk.E, pady=(7, 0))
        self.mutate(None)
        self.after_id = self.after(60, self.poll)

    def mutate(self, action, callback=None):
        if self.closed: return
        self.storage_generation += 1; generation = self.storage_generation
        if callback: self.storage_callbacks[generation] = callback
        # Worker closures and queued results must not own Tk widgets/callbacks.
        # Otherwise the final widget reference can be released on a worker after
        # window close, aborting Tcl with "async handler deleted by wrong thread".
        store, messages = self.store, self.messages
        def run():
            try:
                book = store.load(); value = action(book) if action else None
                if action: store.save(book)
                messages.put(("book", book, value, generation))
            except Exception:
                messages.put(("error", "设备记录无法读取或保存；请检查配置目录权限，不影响手动连接。", generation))
        self.storage.submit(run)

    def poll(self):
        if self.closed: return
        for _ in range(32):
            try: row = self.messages.get_nowait()
            except queue.Empty: break
            if row[0] == "book":
                first = not self.ready; self.ready = True; self.book = row[1]; self.render()
                if not self.busy:
                    self.status.config(text=f"已保存 {len(self.book.nodes)} 个设备。双击设备可连接。" if self.book.nodes
                                       else "暂无已保存设备，可新增设备或查找附近设备。")
                if first and self.book.nodes and not self.app.viewer_host.get(): self.fill(self.book.nodes[0])
                callback = self.storage_callbacks.pop(row[3], None)
                if callback: callback(row[2])
            elif row[0] == "error":
                self.storage_callbacks.pop(row[2], None); self.status.config(text=row[1])
            elif row[0] == "scan" and row[1] == self.epoch:
                self.busy = False; self.scanner = None; self.nearby = row[2]; self.refreshed = time.monotonic(); self.render()
                self.status.config(text=f"发现 {len(self.nearby)} 个设备 / 端口。双击连接。" if self.nearby else "未发现设备。同网段可自动发现；跨网段可填 IP 探测端口，或使用中转在线列表。")
                callback, self.scan_callback = self.scan_callback, None
                if callback: callback(self.nearby)
        visible = self.winfo_viewable() and self.app.root.state() != "iconic"
        if self.busy and (time.monotonic() > self.scan_deadline or not visible or self.app.viewer is not None):
            timed_out = time.monotonic() > self.scan_deadline; callback = self.scan_callback
            self.cancel_scan(); self.refreshed = time.monotonic()
            if timed_out:
                self.status.config(text="发现超时；可以手动填写地址和端口。")
                if callback: callback([])
        if visible and not self.busy and self.app.viewer is None and time.monotonic()-self.refreshed >= 30: self.scan()
        self.after_id = self.after(100, self.poll)

    def cancel_scan(self):
        self.epoch += 1; self.busy = False; self.scan_callback = None
        if self.scanner: self.scanner.close(); self.scanner = None

    def scan(self, target=None, callback=None):
        if self.closed or self.app.viewer is not None: return
        if not self.network_slots.acquire(blocking=False):
            self.status.config(text="系统仍在解析上一次地址；稍后重试，或填写 IP 和端口手动连接。")
            self.refreshed = time.monotonic()
            if callback: callback([])
            return
        self.cancel_scan(); self.busy = True; generation = self.epoch; scanner = self.scanner = model.Scanner()
        self.scan_deadline = time.monotonic()+6; self.scan_callback = callback
        self.status.config(text="正在查找设备和实际端口，不发送设备密钥…")
        nodes = tuple(self.book.nodes)
        slots, messages = self.network_slots, self.messages
        def run():
            try: found = scanner.scan(target, nodes)
            except Exception: found = []
            finally: slots.release()
            messages.put(("scan", generation, found))
        threading.Thread(target=run, name="RemoteDeskDiscovery", daemon=True).start()

    def render(self):
        selected = self.tree.selection(); self.tree.delete(*self.tree.get_children()); self.rows = {}; seen = set()
        for device in self.nearby:
            key = device.device_id or device.address
            if key in seen: continue
            seen.add(key)
            saved = next((n for n in self.book.nodes if device.device_id and device.device_id == n.device_id), None) or \
                next((n for n in self.book.nodes if model.same_machine(n, device)), None)
            row_id = "saved:"+saved.id if saved else "found:"+device.address
            if row_id in self.rows: continue
            self.rows[row_id] = (saved, device)
            self.tree.insert("", tk.END, iid=row_id, text=saved.title if saved else device.name or device.host,
                             values=(device.address, "可连接" if device.listening else "被控未启动"))
        for node in self.book.nodes:
            key = "saved:"+node.id
            if key in self.rows: continue
            self.rows[key] = (node, None)
            self.tree.insert("", tk.END, iid=key, text=node.title, values=(node.address, "已保存 · 可自动探测" if node.auto_port else "已保存"))
        if selected and selected[0] in self.rows: self.tree.selection_set(selected[0])

    def selection(self):
        selection = self.tree.selection()
        return self.rows.get(selection[0], (None, None)) if selection else (None, None)
    def fill(self, node):
        self.selected_id = node.id; self.app.viewer_host.set(node.host)
        self.app.viewer_port.set("" if node.auto_port else str(node.port)); self.app.viewer_password.set(node.password)
    def fill_selection(self, _=None):
        if self.app.viewer is not None: return
        node, device = self.selection()
        if node: self.fill(node)  # Keep the saved endpoint until relocation is confirmed.
        elif device:
            self.selected_id = ""; self.app.viewer_host.set(device.host); self.app.viewer_port.set(str(device.port)); self.app.viewer_password.set("")
    def connect_selected(self):
        node, device = self.selection()
        if not node and not device: return
        self.fill_selection()
        if not node:
            value = simpledialog.askstring("连接设备", f"{device.name or device.address}\n请输入对方设备的设备密钥：", show="*", parent=self.app.root)
            if not value: return
            self.app.viewer_password.set(value)
        self.app.connect_viewer()

    def detect(self):
        try: host, _, _ = model.endpoint(self.app.viewer_host.get())
        except ValueError as error: self.status.config(text=str(error)); return
        self.scan(host)

    def connect(self):
        try:
            host, port, explicit = model.endpoint(self.app.viewer_host.get(), self.app.viewer_port.get())
            password = self.app.viewer_password.get().strip()
            if not password: raise ValueError("请输入对方设备的设备密钥。")
        except ValueError as error: messagebox.showerror("RemoteDesk", str(error), parent=self.app.root); return
        self.connection_auto = not explicit
        node = self.book.find(self.selected_id)
        if node and node.host != host: node = None; self.selected_id = ""
        original = (self.app.viewer_host.get(), self.app.viewer_port.get(), self.app.viewer_password.get())
        def begin(device=None):
            if self.closed or self.app.viewer is not None or original != (self.app.viewer_host.get(), self.app.viewer_port.get(), self.app.viewer_password.get()): return
            new_host, new_port = (device.host, device.port) if device else (host, port)
            if node and (node.host != new_host or node.port != new_port):
                if not messagebox.askokcancel("设备地址已变化", f"{node.title}\n原地址：{node.address}\n新地址：{new_host}:{new_port}\n\n请确认这是你的设备。成功连接后保留备注并合并记录。", parent=self.app.root): return
            self.app.viewer_host.set(new_host)
            self.app.viewer_port.set("" if self.connection_auto else str(new_port))
            self.app._begin_direct_viewer(new_host, new_port, password)
        if explicit: begin(); return
        def resolved(found, fallback=True):
            options = model.candidates(node, found) if node else [d for d in found if d.listening]
            if not options and node and fallback:
                self.scan(host, lambda more:resolved(more, False)); return
            if not options: begin(); return
            chosen = self.choose(options)
            if chosen: begin(chosen)
        self.scan(None if node else host, resolved)

    def choose(self, devices):
        if not devices: return None
        if len(devices) == 1: return devices[0]
        from remotedesk_linux_app import apply_adaptive_window_geometry
        dialog = tk.Toplevel(self.app.root); dialog.title("选择设备 / 端口"); dialog.transient(self.app.root)
        dialog.columnconfigure(0, weight=1); dialog.rowconfigure(0, weight=1)
        content = ttk.Frame(dialog, padding=12)
        content.grid(row=0, column=0, sticky=tk.NSEW)
        content.columnconfigure(0, weight=1); content.rowconfigure(0, weight=1)
        tree = ttk.Treeview(content, columns=("address",), show="tree headings", height=8, selectmode="browse")
        tree.heading("#0", text="设备"); tree.heading("address", text="IP / 端口")
        tree.column("#0", width=330, minwidth=120); tree.column("address", width=240, minwidth=160)
        vertical = ttk.Scrollbar(content, orient=tk.VERTICAL, command=tree.yview)
        horizontal = ttk.Scrollbar(content, orient=tk.HORIZONTAL, command=tree.xview)
        tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        tree.grid(row=0, column=0, sticky=tk.NSEW); vertical.grid(row=0, column=1, sticky=tk.NS)
        horizontal.grid(row=1, column=0, sticky=tk.EW)
        details = tk.Text(content, height=2, width=1, wrap=tk.CHAR, state=tk.DISABLED)
        details_scroll = ttk.Scrollbar(content, orient=tk.VERTICAL, command=details.yview)
        details.configure(yscrollcommand=details_scroll.set)
        details.grid(row=2, column=0, sticky=tk.EW, pady=(8, 0))
        details_scroll.grid(row=2, column=1, sticky=tk.NS, pady=(8, 0))
        result = []
        for index, device in enumerate(devices):
            tree.insert("", tk.END, iid=str(index), text=device.name or device.host, values=(device.address,))
        def selected(_event=None):
            selection = tree.selection()
            if not selection: return
            device = devices[int(selection[0])]
            details.configure(state=tk.NORMAL); details.delete("1.0", tk.END)
            details.insert("1.0", f"地址：{device.address}\n设备：{device.name or device.host}")
            details.configure(state=tk.DISABLED)
        def accept(_event=None):
            selection = tree.selection()
            if selection:
                result.append(devices[int(selection[0])]); dialog.destroy()
        tree.bind("<<TreeviewSelect>>", selected)
        tree.bind("<Return>", accept); tree.bind("<Double-1>", accept)
        tree.selection_set("0"); tree.focus("0"); selected()
        actions = ttk.Frame(dialog, padding=(12, 0, 12, 12)); actions.grid(row=1, column=0, sticky=tk.EW)
        ttk.Button(actions, text="取消", command=dialog.destroy, width=6).pack(side=tk.RIGHT)
        ttk.Button(actions, text="连接此设备", command=accept, width=10, style="Accent.TButton").pack(side=tk.RIGHT, padx=(0, 8))
        dialog.bind("<Escape>", lambda _event: dialog.destroy())
        apply_adaptive_window_geometry(dialog, preferred_size=(720, 460), minimum_size=(360, 300), parent=self.app.root)
        tree.focus_set()
        dialog.grab_set(); self.app.root.wait_window(dialog)
        return result[0] if result else None

    def add(self):
        from remotedesk_linux_app import APP_BG, apply_adaptive_window_geometry, normalize_tk_ui_scale
        dialog = tk.Toplevel(self.app.root); dialog.title("新增设备"); dialog.transient(self.app.root)
        dialog.configure(bg=APP_BG)
        dialog.columnconfigure(1, weight=1)
        values = [tk.StringVar() for _ in range(4)]
        fields = []
        for i, title in enumerate(("IP / 主机名", "端口（留空自动探测）", "设备密钥", "备注（可选）")):
            ttk.Label(dialog, text=title, wraplength=180, justify=tk.LEFT).grid(row=i, column=0, padx=14, pady=7, sticky=tk.W)
            entry = ttk.Entry(dialog, textvariable=values[i], show="*" if i == 2 else "", width=12)
            entry.grid(row=i, column=1, padx=14, pady=7, sticky=tk.EW); fields.append(entry)
        def save():
            try:
                host, port, explicit = model.endpoint(values[0].get(), values[1].get()); password = values[2].get(); note = values[3].get()
                if not password.strip(): raise ValueError("请输入设备密钥。")
                self.mutate(lambda book:book.remember(host, port, password, remark=note or None, auto_port=not explicit), self.fill)
                dialog.destroy()
            except ValueError as ex:
                messagebox.showwarning("无法保存设备", str(ex), parent=dialog)
        ttk.Button(dialog, text="保存设备", command=save, style="Accent.TButton").grid(row=4, column=1, pady=14)
        ttk.Button(dialog, text="取消", command=dialog.destroy, width=6).grid(row=4, column=0, pady=14)
        dialog.bind("<Escape>", lambda _event: dialog.destroy())
        dialog.update_idletasks()
        scale = normalize_tk_ui_scale(float(dialog.tk.call("tk", "scaling")))
        # Requested widget height already includes the user's font scaling.
        # Convert it back to logical units instead of scaling it twice.
        logical_height = round(dialog.winfo_reqheight() / scale)
        apply_adaptive_window_geometry(dialog, preferred_size=(round(620 / scale), logical_height),
                                       minimum_size=(round(360 / scale), logical_height), parent=self.app.root)
        fields[0].focus_set()
        dialog.grab_set(); self.app.root.wait_window(dialog)
        for value in values: value.set("")
    def rename(self):
        node, _ = self.selection()
        if not node: return
        text = simpledialog.askstring("修改备注", "备注：", initialvalue=node.remark, parent=self.app.root)
        if text is not None: self.mutate(lambda book:book.rename(node.id, text))
    def remove(self):
        node, _ = self.selection()
        if node and messagebox.askokcancel("删除设备记录", f"删除 {node.title} 的地址和已保存的设备密钥？不会影响远端设备。", parent=self.app.root):
            self.mutate(lambda book:book.remove(node.id))

    def record(self, info):
        if self.closed or not isinstance(info, dict) or self.app.viewer_relay_options is not None: return
        generation = self.app.viewer_generation
        if self.recorded_generation == generation: return
        device_id = model.identity(info.get("deviceId"))
        if info.get("capabilities", 0) & model.IDENTITY_CAPABILITY and not device_id: return
        target = self.app.viewer_reconnect_target
        if not target: return
        self.recorded_generation = generation
        host, port, password = target; previous = self.selected_id; auto = self.connection_auto
        def saved(node):
            if node and self.app.viewer_generation == generation: self.selected_id = node.id
        self.mutate(lambda book:book.remember(host, port, password, info.get("machineName", ""), device_id, previous,
                                             auto_port=auto), saved)
    def close(self):
        if self.closed: return
        self.closed = True; self.cancel_scan(); self.after_cancel(self.after_id)
        self.storage_callbacks.clear()
        self.app = None
        # Finish accepted writes without keeping this panel or blocking the UI.
        self.storage.shutdown(wait=False)
