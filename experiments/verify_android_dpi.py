#!/usr/bin/env python3
"""Actual Android system density/font/rotation, scoped to an explicitly owned AVD."""
import argparse
import json
import io
from pathlib import Path
import re
import shlex
import subprocess
import socket
import struct
import sys
import threading
import time
import xml.etree.ElementTree as ET

PACKAGE = "com.remotedesk.viewerprobe"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--avd", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--density", type=int, choices=(320, 440, 560), required=True)
    parser.add_argument("--font", type=float, choices=(1, 1.5, 2), required=True)
    parser.add_argument("--landscape", action="store_true")
    parser.add_argument("--dialogs-only", action="store_true")
    parser.add_argument("--viewer", action="store_true")
    parser.add_argument("--rename-only", action="store_true")
    parser.add_argument("--large-screen", action="store_true", help="Owned virtual display 1600x2560; restored separately at cleanup")
    args = parser.parse_args()
    prefix = [args.adb, "-s", args.serial]
    if not args.serial.startswith("emulator-"):
        raise RuntimeError("Owned emulator required")

    def adb(*command):
        return subprocess.run(prefix + list(map(str, command)), capture_output=True, check=True,
            timeout=30).stdout.decode("utf-8").strip()

    def shell(*command):
        return adb("shell", " ".join(shlex.quote(str(item)) for item in command))

    if args.avd not in adb("emu", "avd", "name").splitlines():
        raise RuntimeError("Serial is not the owned AVD")
    args.output.mkdir(parents=True, exist_ok=False)
    report = {"densityDpi": args.density, "fontScale": args.font,
        "orientation": "landscape" if args.landscape else "portrait", "snapshots": [], "checks": []}
    stop = threading.Event()
    listener = None
    port = None

    def check(name, passed):
        report["checks"].append({"name": name, "passed": bool(passed)})
        if not passed:
            raise AssertionError(name)

    def viewer_peer():
        nonlocal listener, port
        from PIL import Image
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
        import remotedesk_protocol_probe as wire
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0)); listener.listen(1); listener.settimeout(.5)
        port = listener.getsockname()[1]
        report["peerPort"] = port
        picture = io.BytesIO(); Image.new("RGB", (1280, 720), "#dbe7ef").save(picture, format="JPEG")
        frame = struct.pack("<iidd", 1280, 720, 0, 0) + picture.getvalue()
        def serve():
            while not stop.is_set():
                try:
                    client = listener.accept()[0]
                except (socket.timeout, OSError):
                    continue
                ended = threading.Event()
                try:
                    with client:
                        client.settimeout(10)
                        session = wire.authenticate_server(client, "RemoteDesk-synthetic-ui-fixture")
                        lock = threading.Lock()
                        def send(kind, payload):
                            with lock: wire.write_message(client, session, kind, payload)
                        caps = wire.CAPABILITY_REMOTE_DESKTOP | wire.CAPABILITY_INPUT_CONTROL | wire.CAPABILITY_FILE_RECEIVE | wire.CAPABILITY_FILE_RECEIVE_LOCATION
                        send(wire.MESSAGE_CONTROL, wire.encode_device_info("会议室主控电脑与图形设计工作站", "Windows", caps))
                        def frames(connection_send=send, connection_ended=ended):
                            try:
                                while not stop.is_set() and not connection_ended.wait(.2): connection_send(wire.MESSAGE_FRAME, frame)
                            except OSError: pass
                        threading.Thread(target=frames, daemon=True).start()
                        while not stop.is_set():
                            kind, payload = wire.read_message(client, session)
                            if kind == wire.MESSAGE_PING: send(wire.MESSAGE_PONG, payload)
                            elif kind == wire.MESSAGE_CONTROL:
                                control = wire.decode_control(payload)
                                if control["kind"] == wire.CONTROL_FILE_RECEIVE_LOCATION_REQUEST:
                                    send(wire.MESSAGE_CONTROL, wire.encode_file_receive_location(control["transferId"], True,
                                        "C:\\SyntheticReceiver\\会议室接收文件目录", "仅隔离合成目录；不发送文件"))
                                elif control["kind"] == wire.CONTROL_FILE_TRANSFER_START:
                                    report["unexpectedFileStart"] = True
                except (OSError, EOFError): pass
                except Exception as failure: report["peerFailure"] = repr(failure)
                finally: ended.set()
        threading.Thread(target=serve, daemon=True).start()

    def read_state():
        try:
            return json.loads(shell("run-as", PACKAGE, "cat", "files/dpi-ui.json"))
        except (ValueError, subprocess.CalledProcessError):
            return {}

    def wait(predicate, timeout=15):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            result = predicate()
            if result:
                return result
            time.sleep(.15)
        raise TimeoutError("DPI fixture state did not become ready")

    def action(name, run_id, text=None):
        command = ["am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.DpiProbeReceiver",
            "--es", "action", name, "--es", "runId", run_id]
        if text is not None:
            command += ["--es", "text", text]
        shell(*command)
        return wait(lambda: (state if (state := read_state()).get("runId") == run_id else None))

    def capture(name, action_name="snapshot", text=None):
        state = action(action_name, name, text)
        (args.output / (name + ".json")).write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
        (args.output / (name + ".png")).write_bytes(subprocess.run(prefix + ["exec-out", "screencap", "-p"],
            capture_output=True, check=True, timeout=30).stdout)
        report["snapshots"].append(name)
        return state

    def tap(label):
        shell("uiautomator", "dump", "/data/local/tmp/remotedesk-dpi.xml")
        tree = ET.fromstring(shell("cat", "/data/local/tmp/remotedesk-dpi.xml"))
        (args.output / "last-ui.xml").write_text(ET.tostring(tree, encoding="unicode"), encoding="utf-8")
        matches = [node for node in tree.iter("node") if node.get("package") == PACKAGE
            and (node.get("text") == label or node.get("content-desc") == label)]
        if len(matches) != 1:
            raise RuntimeError("Expected one visible label: " + label + ", matches=" + str(len(matches)))
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", matches[0].get("bounds")))
        shell("input", "tap", (x1+x2)//2, (y1+y2)//2)
        time.sleep(.25)

    def with_ime(name, hint):
        state = capture(name + "-reveal", "reveal", hint)
        inputs = [view for window in state["windows"] for view in window["views"]
            if view.get("class") == "EditText" and view.get("hint") == hint and view.get("onScreen")]
        check(name + " input is visible with natural text height", inputs and inputs[-1]["contentHeight"] >= inputs[-1]["textHeight"])
        x1, y1, x2, y2 = inputs[-1]["visibleRect"]
        shell("input", "tap", (x1+x2)//2, (y1+y2)//2)
        time.sleep(.7)
        state = capture(name, "focus", hint)
        if not any(any(window.get("imeInsets", [])) for window in state["windows"]):
            time.sleep(2)
            state = capture(name + "-settled", "focus", hint)
        ime = shell("dumpsys", "input_method")
        (args.output / (name + "-input-method.txt")).write_text(ime, encoding="utf-8")
        check(name + " actual system IME shown", "mInputShown=true" in ime)
        return state

    def hide_ime(state):
        # Landscape extract-mode IME covers the Activity without setting its IME insets.
        if any(any(window.get("imeInsets", [])) for window in state["windows"]) or "mInputShown=true" in shell("dumpsys", "input_method"):
            shell("input", "keyevent", 4)
            time.sleep(.3)

    def close_validated_key(name):
        # setError owns a non-focusable popup above the dialog; its overlay can
        # intercept a coordinate tap on Cancel. The normal system Back path
        # dismisses it/the dialog, then the remaining Cancel button if needed.
        shell("input", "keyevent", 4)
        state = capture(name + "-back")
        if any(view.get("text") == "连接设备" for window in state["windows"] for view in window["views"]):
            tap("取消")

    try:
        shell("am", "force-stop", PACKAGE)
        if args.large_screen:
            shell("wm", "size", "1600x2560")
        shell("wm", "density", args.density)
        shell("settings", "put", "system", "font_scale", args.font)
        time.sleep(1) # Let density/font configuration broadcasts finish before locking orientation.
        # Set rotation mode+value atomically. Separate Settings writes can be
        # overwritten by WindowManager's outstanding sensor-to-user transition.
        shell("wm", "set-user-rotation", "lock", 1 if args.landscape else 0)
        wanted_rotation = str(1 if args.landscape else 0)
        for attempt in range(6):
            time.sleep(.5)
            if shell("settings", "get", "system", "user_rotation") == wanted_rotation:
                break
            shell("wm", "set-user-rotation", "lock", wanted_rotation)
        else:
            raise AssertionError("Owned emulator did not accept the requested rotation lock")
        report["system"] = {"size": shell("wm", "size"), "density": shell("wm", "density"),
            "fontScale": shell("settings", "get", "system", "font_scale"),
            "rotation": shell("settings", "get", "system", "user_rotation")}
        shell("run-as", PACKAGE, "rm", "-f", "files/dpi-ui.json")
        if args.viewer:
            viewer_peer()
            adb("reverse", "tcp:" + str(port), "tcp:" + str(port))
            shell("am", "start", "-W", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeLauncher", "--ez", "dpiProbe", "true", "--ei", "port", port)
            wait(lambda: read_state().get("runId") == "ready")
            time.sleep(2)
            state = capture("viewer-toolbar")
            bounds = state["windows"][0]["bounds"]
            check("actual viewer window matches requested orientation", (bounds[2]-bounds[0] > bounds[3]-bounds[1]) == args.landscape)
            check("actual viewer resources match system density and font", state["densityDpi"] == args.density and state["fontScale"] == args.font)
            for label in ("仅观看：关", "更多", "键盘"):
                views = [view for window in state["windows"] for view in window["views"] if view.get("text") == label]
                check(label + " visible and unclipped", views and views[0]["onScreen"] and views[0]["contentHeight"] >= views[0]["textHeight"] and not views[0]["ellipsis"])
            tap("仅观看：关"); capture("viewer-watch-only"); tap("仅观看：开")
            tap("键盘")
            ime_state = with_ime("viewer-ime", "输入文字")
            composer = next(view for window in ime_state["windows"] for view in window["views"] if view.get("hint") == "输入文字")
            check("short composer hint keeps full accessible meaning", composer.get("description") == "输入文字后发送到远端")
            hide_ime(ime_state)
            tap("收起")
            shell("am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver", "--es", "action", "select_files", "--es", "indices", "0,1")
            time.sleep(2)
            state = capture("file-confirmation")
            title = [view for window in state["windows"] for view in window["views"] if view.get("text") == "确认发送 2 个文件"]
            check("file confirmation title complete", title and not title[-1]["ellipsis"] and title[-1]["textHeight"] <= title[-1]["contentHeight"])
            for label in ("发送", "取消"):
                buttons = [view for view in state["windows"][-1]["views"] if view.get("text") == label]
                check("file confirmation " + label + " reachable", buttons and buttons[-1]["onScreen"] and buttons[-1]["contentHeight"] >= buttons[-1]["textHeight"])
            # Start inside the message viewport, not the adjacent button bar.
            # Scroll the real widget to the final safety/receipt note.
            reached_end = False
            for attempt in range(30):
                message = next(view for view in state["windows"][-1]["views"] if view.get("text", "").startswith("共 2 个文件"))
                rect = message["visibleRect"]
                if message["bounds"][3] <= rect[3] + 1:
                    reached_end = True
                    break
                x = (rect[0] + rect[2])//2
                shell("input", "swipe", x, rect[3]-10, x, rect[1]+10, 200)
                state = action("snapshot", "file-scroll-" + str(attempt))
            capture("file-confirmation-scrolled")
            check("file confirmation details scroll to the final safety note", reached_end)
            tap("取消")
            check("cancel does not send a document", not report.get("unexpectedFileStart") and not report.get("peerFailure"))
            report["completed"] = True
            return
        shell("am", "start", "-W", "-n", PACKAGE + "/com.remotedesk.agent.MainActivity", "--ez", "dpiProbe", "true")
        wait(lambda: read_state().get("runId") == "ready")
        state = capture("main-devices")
        bounds = state["windows"][0]["bounds"]
        check("actual main window matches requested orientation", (bounds[2]-bounds[0] > bounds[3]-bounds[1]) == args.landscape)
        check("actual main resources match system density and font", state["densityDpi"] == args.density and state["fontScale"] == args.font)
        if args.rename_only:
            capture("relay-name", "relay-name")
            hide_ime(with_ime("relay-name-ime", "输入共享名称（可留空）"))
            tap("取消")
            report["completed"] = True
            return
        if not args.dialogs_only:
            tap("被控"); capture("main-host")
            tap("设置"); capture("main-settings")
            tap("设备")
            capture("main-relay", "reveal", "登录服务器")
            hide_ime(with_ime("main-relay-ime", "服务器管理员密码"))
        capture("direct-key", "direct-key")
        hide_ime(with_ime("direct-key-ime", "对方设备的设备密钥"))
        tap("连接")
        state = capture("direct-key-validation")
        check("empty direct key stays in dialog with validation", any(view.get("error") for window in state["windows"] for view in window["views"]))
        close_validated_key("direct-key")
        capture("relay-key", "relay-key")
        hide_ime(with_ime("relay-key-ime", "这台设备的密钥，不是服务器密码"))
        tap("连接")
        state = capture("relay-key-validation")
        check("empty relay key stays in dialog with validation", any(view.get("error") for window in state["windows"] for view in window["views"]))
        close_validated_key("relay-key")
        capture("add-reveal", "reveal", "新增设备")
        tap("新增设备")
        capture("add-device")
        hide_ime(with_ime("add-device-ime", "备注（可选）"))
        tap("取消")
        capture("relay-name", "relay-name")
        hide_ime(with_ime("relay-name-ime", "输入共享名称（可留空）"))
        tap("取消")
        report["completed"] = True
    except Exception as failure:
        report["failure"] = str(failure)
        raise
    finally:
        (args.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        shell("am", "force-stop", PACKAGE)
        stop.set()
        if listener is not None: listener.close()
        if port is not None: adb("reverse", "--remove", "tcp:" + str(port))


if __name__ == "__main__":
    main()
