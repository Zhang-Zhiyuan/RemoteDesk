#!/usr/bin/env python3
"""Exercise production Linux clipboard sync on an explicitly owned Xvfb.

The other endpoint must be our private-window-station Windows test fixture.
No user desktop, real host, file clipboard or arbitrary command is accessed.
"""
import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import uuid

from wsl_package_fixture import ROOT, extract_package, allow_owned_loopback_viewer


def write_json(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def editor_target(directory, assert_selection=False):
    """A separate real X11 clipboard owner, with native copy/paste menus."""
    import tkinter as tk
    root = tk.Tk()
    root.title("RemoteDesk owned clipboard target")
    root.geometry("620x380+890+40")
    field = tk.Text(root, font=("sans", 15), wrap=tk.WORD)
    field.pack(fill=tk.BOTH, expand=True)
    counts = {"copy": 0, "paste": 0}
    menu = tk.Menu(root, tearoff=False)

    def action(kind):
        if kind == "copy" and assert_selection:
            # Explicit alternative fixture contract: issue a real ownership
            # change through Tk's own connection. Plain Tk may optimize away a
            # same-value copy and expose neither a change event nor TIMESTAMP.
            # Never hide that original negative result with this variant.
            root.selection_clear(selection="CLIPBOARD")
        field.event_generate("<<Copy>>" if kind == "copy" else "<<Paste>>")
        counts[kind] += 1

    menu.add_command(label="复制 Copy", command=lambda: action("copy"))
    menu.add_command(label="粘贴 Paste", command=lambda: action("paste"))

    def show_menu(event):
        field.focus_force()
        menu.post(event.x_root, event.y_root)
        root.update_idletasks()
        write_json(directory / "menu.json", {
            "copy": [menu.winfo_rootx() + menu.winfo_width() // 2,
                     menu.winfo_rooty() + menu.yposition(0) + 9],
            "paste": [menu.winfo_rootx() + menu.winfo_width() // 2,
                      menu.winfo_rooty() + menu.yposition(1) + 9]})

    field.bind("<Button-3>", show_menu)

    def tick():
        for request_path in sorted((directory / "commands").glob("*.json")):
            request = json.loads(request_path.read_text(encoding="utf-8"))
            request_path.unlink()
            action_name = request["action"]
            if action_name in ("select", "clear"):
                root.lift()
                field.delete("1.0", tk.END)
                if action_name == "select":
                    field.insert("1.0", request["text"])
                    field.tag_add(tk.SEL, "1.0", "end-1c")
                field.focus_force()
            elif action_name == "close":
                root.after_idle(root.destroy)
            elif action_name == "stall":
                seconds = float(request.get("seconds", 2))
                if not 0 <= seconds <= 3:
                    raise ValueError("Owned clipboard stall is bounded to three seconds")
                root.after(10, lambda: time.sleep(seconds))
            elif action_name != "state":
                raise ValueError("Unsupported owned target action")
            write_json(directory / "responses" / request_path.name, {
                "success": True, "text": field.get("1.0", "end-1c"), "counts": counts,
                "point": [field.winfo_rootx() + 60, field.winfo_rooty() + 60]})
        root.after(25, tick)

    root.after(25, tick)
    root.mainloop()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor-target", type=Path)
    parser.add_argument("--package", type=Path)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--control-dir", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--baseline", action="store_true", help="Negative control with unmodified shipped code")
    parser.add_argument("--assert-selection", action="store_true", help="Labelled fixture variant: Copy releases/reasserts X11 selection ownership")
    parser.add_argument("--manual-recopy", action="store_true", help="Verify explicit Send button for an unobservable plain-Tk same-value recopy")
    args = parser.parse_args()
    if args.assert_selection and args.manual_recopy:
        parser.error("Choose either observable reassertion or the explicit manual fallback")
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        parser.error("An explicitly caller-owned Xvfb is required")
    if args.editor_target:
        editor_target(args.editor_target, args.assert_selection)
        return 0
    if not all((args.package, args.config, args.control_dir, args.output)):
        parser.error("package, config, control-dir and output are required")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    control = args.control_dir.resolve()
    # A dedicated fixture directory, not an arbitrary external mailbox.
    if not (control / "commands").is_dir() or not (control / "responses").is_dir():
        parser.error("Missing owned fixture command/response directories")
    report = {"complete": False, "baseline": args.baseline, "checks": [],
              "scope": "Actual Linux viewer and external X11 owner/native context menus; private Windows system clipboard"}
    report["copyExplicitlyReassertsSelection"] = args.assert_selection
    report["sameValueRecopyUsesExplicitSendButton"] = args.manual_recopy
    ui = root = None
    children = []
    with tempfile.TemporaryDirectory(prefix="remotedesk-clipboard-audit-") as temporary:
        work = Path(temporary)
        os.environ.update(XDG_CONFIG_HOME=str(work / "config"), XDG_CACHE_HOME=str(work / "cache"),
                          WAYLAND_DISPLAY="", XDG_SESSION_TYPE="x11", REMOTEDESK_AUTO_INSTALL="0")
        (work / "config").mkdir(mode=0o700)
        if args.baseline:
            extract_package(args.package.resolve(), work / "package")
        sys.dont_write_bytecode = True
        sys.path.insert(0, str(work / "package/app" if args.baseline else ROOT / "scripts/linux"))
        app = importlib.import_module("remotedesk_linux_app")
        report["explicitOwnedLoopbackFixture"] = allow_owned_loopback_viewer(app)
        report["appSourceSha256"] = hashlib.sha256(Path(app.__file__).read_bytes()).hexdigest()
        from PIL import ImageGrab
        target = work / "editor"
        for folder in (target / "commands", target / "responses"):
            folder.mkdir(parents=True)
        try:
            children.append(subprocess.Popen(["openbox", "--sm-disable"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
            children.append(subprocess.Popen([sys.executable, "-B", str(Path(__file__).resolve()), "--editor-target", str(target),
                                             *(["--assert-selection"] if args.assert_selection else [])],
                                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
            root = app.tk.Tk()
            ui = app.RemoteDeskLinuxApp(root)
            ui.device_panel.refreshed = time.monotonic() + 3600
            root.geometry("850x650+0+0")
            errors = []
            statuses = []
            set_status = ui._set_viewer_status
            def status(value):
                statuses.append(str(value))
                set_status(value)
            ui._set_viewer_status = status
            root.report_callback_exception = lambda typ, value, tb: errors.append(typ.__name__ + ": " + str(value))

            def pump(predicate, timeout=12):
                deadline = time.monotonic() + timeout
                while time.monotonic() < deadline:
                    root.update()
                    if errors:
                        raise RuntimeError(errors[0])
                    value = predicate()
                    if value:
                        return value
                    time.sleep(.01)
                raise TimeoutError("Owned clipboard condition timed out")

            def settle(seconds):
                end = time.monotonic() + seconds
                pump(lambda: time.monotonic() >= end, seconds + 2)

            def command(directory, action_name, **fields):
                request_id = uuid.uuid4().hex
                response = directory / "responses" / (request_id + ".json")
                write_json(directory / "commands" / response.name, {"id": request_id, "action": action_name, **fields})
                pump(response.exists)
                # WSL-mounted files can transiently fail a read around rename.
                def read():
                    try:
                        return json.loads(response.read_text(encoding="utf-8-sig"))
                    except (OSError, json.JSONDecodeError):
                        return None
                result = pump(read)
                if not result.get("success"):
                    raise RuntimeError("Owned fixture command failed: " + action_name)
                return result

            def check(name, passed, **details):
                report["checks"].append({"name": name, "passed": bool(passed), **details})
                write_json(output / "report.json", report)
                if not passed:
                    raise AssertionError(name)

            def click(point, button):
                subprocess.run(["xdotool", "mousemove", "--sync", *map(str, point), "mousedown", str(button)], check=True, timeout=3)
                settle(.06)
                subprocess.run(["xdotool", "mouseup", str(button)], check=True, timeout=3)

            def descendants(widget):
                for child in widget.winfo_children():
                    yield child
                    yield from descendants(child)

            def menu_action(kind):
                state = command(target, "state")
                menu_file = target / "menu.json"
                menu_file.unlink(missing_ok=True)
                click(state["point"], 3)
                pump(menu_file.exists)
                point = json.loads(menu_file.read_text(encoding="utf-8"))[kind]
                click(point, 1)
                return pump(lambda: (value if (value := command(target, "state"))["counts"][kind] > state["counts"][kind] else None))

            def local_copy(text):
                command(target, "select", text=text)
                menu_action("copy")

            def local_paste():
                command(target, "clear")
                return menu_action("paste")["text"]

            def remote_text():
                return command(control, "get-text")["text"]

            def counters():
                return command(control, "get-counters")["counters"]

            def connect():
                ui.viewer_host.set(config["host"])
                ui.viewer_port.set(str(config["port"]))
                ui.viewer_password.set(config["password"])
                ui.connect_button.invoke()
                pump(lambda: ui.viewer is not None and ui.last_photo is not None and ui.remote_width > 1, 35)
                return ui.viewer

            local_baseline = "Existing local baseline 保留😀"
            remote_baseline = "Existing remote baseline 不覆盖😀"
            local_copy(local_baseline)
            command(control, "set-text", text=remote_baseline)
            first = connect()
            snapshot_calls = []
            local_reads = []
            snapshot_gate = threading.Event()
            snapshot_gate.set()
            snapshot_waiting = threading.Event()
            if not args.baseline:
                read_local = first._read_auto_local_clipboard
                def observe_local():
                    started = time.monotonic()
                    try:
                        value = read_local()
                    except Exception as error:
                        local_reads.append({"error": type(error).__name__ + ": " + str(error)})
                        raise
                    local_reads.append({"seconds": time.monotonic() - started, "available": value is not None,
                        "revision": None if value is None else value.revision,
                        "owner": None if value is None else value.owner,
                        "timestamp": None if value is None else value.ownership,
                        "canReplace": None if value is None else value.can_replace})
                    return value
                first._read_auto_local_clipboard = observe_local
                request_snapshot = first._request_clipboard_snapshot
                def observe_snapshot(revision):
                    if not snapshot_gate.is_set():
                        snapshot_waiting.set()
                        if not snapshot_gate.wait(5):
                            raise TimeoutError("Owned fault-injection barrier expired")
                    started = time.monotonic()
                    reply = request_snapshot(revision)
                    snapshot_calls.append({"seconds": time.monotonic() - started,
                        "revision": None if reply is None else reply.get("revision"),
                        "changed": False if reply is None else reply.get("changed"),
                        "success": reply is not None})
                    return reply
                first._request_clipboard_snapshot = observe_snapshot
            settle(3)
            check("connect-baselines-preserve-both-clipboards", remote_text() == remote_baseline and local_paste() == local_baseline)
            copied = "本机真实右键复制 中文😀\n第二行\tend"
            before = counters()
            local_copy(copied)
            if args.baseline:
                settle(3)
                check("negative-control-confirms-missing-auto-sync", remote_text() == remote_baseline,
                      featureSupported=False, scope="Baseline failure is reproduced, not a functional pass")
                report["complete"] = True
                return 0
            pump(lambda: remote_text() == copied, 18)
            check("native-local-context-copy-auto-reaches-private-windows-clipboard", True,
                  sha256=hashlib.sha256(copied.encode()).hexdigest())
            settle(2)
            # Observe the original production request unmodified. A deliberately
            # delayed old reply must not win over a fresh same-text native copy.
            late_text = "Deliberately delayed old remote copy 旧回复😀"
            delay_before = counters()["snapshotDelays"]
            snapshot_gate.clear()
            pump(snapshot_waiting.is_set, 8)
            calls_before = len(snapshot_calls)
            command(control, "set-text", text=late_text)
            command(control, "set-snapshot-delay", delayMs=3000)
            snapshot_gate.set()
            pump(lambda: counters()["snapshotDelays"] > delay_before, 12)
            local_copy(copied)
            if args.manual_recopy:
                send_button = next(widget for widget in descendants(ui.viewer_window)
                    if isinstance(widget, app.ttk.Button) and widget.cget("text") == "发送本机剪贴板")
                send_button.invoke()  # Real product button callback, not a direct wire write.
                manual_request = first.clipboard_latest
                pump(lambda: manual_request is not None and manual_request.completed.is_set(), 12)
                check("plain-tk-unobservable-recopy-explicit-send-is-acknowledged", manual_request.success)
            pump(lambda: any(call["seconds"] >= 2.4 for call in snapshot_calls[calls_before:]), 12)
            delayed = next(call for call in snapshot_calls[calls_before:] if call["seconds"] >= 2.4)
            check("fault-injection-really-delayed-changed-old-remote-snapshot", delayed["success"] and delayed["changed"] and
                  delayed["revision"] == hashlib.sha256(late_text.encode()).hexdigest(), reply=delayed)
            settle(1.5)
            recopy_result = local_paste()
            check("same-text-recopy-is-not-overwritten-by-late-remote-snapshot", recopy_result == copied,
                  requiresObservableX11CopyEvent=args.assert_selection,
                  expectedSha256=hashlib.sha256(copied.encode()).hexdigest(),
                  pastedSha256=hashlib.sha256(recopy_result.encode()).hexdigest())
            if args.manual_recopy:
                check("explicit-send-selects-local-direction-without-echoing-old-reply", remote_text() == copied)
            else:
                check("simultaneous-copies-are-preserved-with-conflict-hint", remote_text() == late_text and
                      any("两端都复制" in value for value in statuses))
            copied = "Conflict resolved by new native local copy 中文😀"
            local_copy(copied)
            pump(lambda: remote_text() == copied, 18)
            check("copy-after-conflict-resumes-auto-sync", True)
            settle(2)
            changed = "远端系统剪贴板更新 中文😀\n无需快捷键"
            before_remote = counters()
            command(control, "set-text", text=changed)
            settle(3)
            pasted = local_paste()
            check("remote-copy-auto-syncs-and-native-local-context-paste-works", pasted == changed)
            settle(3)
            after_remote = counters()
            check("remote-apply-and-idle-do-not-echo-set", after_remote["clipboardSet"] == before_remote["clipboardSet"],
                  before=before_remote, after=after_remote)
            # A clipboard owner may stop responding. Only this separate owned
            # editor is deliberately stalled; the actual viewer must keep pumping.
            stall_text = "Slow external clipboard owner 有界读取"
            local_copy(stall_text)
            pump(lambda: remote_text() == stall_text, 18)
            beats = []
            heartbeat_running = True
            def heartbeat():
                if heartbeat_running:
                    beats.append(time.monotonic())
                    root.after(25, heartbeat)
            heartbeat()
            previous_frames = first.frame_sequence
            command(target, "stall", seconds=2.5)
            settle(3.5)
            heartbeat_running = False
            gaps = [right - left for left, right in zip(beats, beats[1:])]
            check("unresponsive-external-clipboard-owner-does-not-block-tk-or-video", len(beats) >= 25 and
                  max(gaps, default=99) < .65 and first.frame_sequence > previous_frames + 5,
                  ticks=len(beats), maxGapSeconds=max(gaps, default=99), frames=first.frame_sequence - previous_frames)
            command(control, "set-text", text="")
            settle(3)
            check("empty-remote-clipboard-does-not-erase-local", local_paste() == stall_text)
            disconnected = "Disconnected local copy 不外发"
            ui.disconnect_button.invoke()
            pump(lambda: first.stop_event.is_set())
            local_copy(disconnected)
            settle(2)
            check("disconnected-context-copy-is-not-sent", remote_text() == "")
            command(control, "set-text", text="Reconnect remote baseline")
            second = connect()
            settle(3)
            check("reconnect-uses-fresh-session-and-preserves-new-baselines", second is not first and
                  local_paste() == disconnected and remote_text() == "Reconnect remote baseline")
            final_copy = "New session right-click copy 新会话😀"
            local_copy(final_copy)
            pump(lambda: remote_text() == final_copy, 18)
            check("new-session-context-copy-syncs", True)
            ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "final.png")
            check("no-tk-callback-errors", not errors)
            report["complete"] = True
        except Exception as error:
            report["failure"] = type(error).__name__ + ": " + str(error)
            if ui is not None:
                report["statuses"] = statuses[-30:]
                report["localReads"] = locals().get("local_reads", [])[-20:]
                report["snapshotCalls"] = locals().get("snapshot_calls", [])[-20:]
                if ui.viewer is not None and not args.baseline:
                    report["autoThread"] = {"alive": ui.viewer.clipboard_auto_thread.is_alive(),
                        "stop": ui.viewer.clipboard_auto_stop.is_set(),
                        "capabilities": ui.viewer.remote_capabilities}
                try:
                    ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "failure.png")
                except Exception:
                    pass
        finally:
            if ui is not None:
                ui.close()
            for child in reversed(children):
                if child.poll() is None:
                    child.terminate()
                    try:
                        child.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        child.kill()
                        child.wait(timeout=3)
            report["ownedChildrenStopped"] = all(child.poll() is not None for child in children)
            write_json(output / "report.json", report)
    print(json.dumps({"complete": report["complete"], "checks": len(report["checks"]), "failure": report.get("failure")}))
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
