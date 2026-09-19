#!/usr/bin/env python3
"""Actual Android viewer UI + encrypted synthetic peer; never reads a real PC clipboard."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire

PHONE_TEXT = "手机复制中文😀\r\n第二行\t缩进\n"
REMOTE_TEXT = "远端复制繁體中文😀\r\n回传第二行\n"
PACKAGE = "com.remotedesk.viewerprobe"


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--files", action="store_true", help="Also exercise the real SAF picker and confirmed upload UI")
    parser.add_argument("--mumu-manager", type=Path,
        help="Optional installed MuMuManager.exe; verifies a loopback serial is its running emulator")
    parser.add_argument("--file-silence-seconds", type=float, default=0,
        help="Pause fixture reads and frames during upload to test the bounded file watchdog allowance")
    parser.add_argument("--upload-only", action="store_true")
    parser.add_argument("--legacy-file-watchdog", action="store_true",
        help="Probe-only negative control: disable the upload lease to restore the old 18-second policy")
    parser.add_argument("--legacy-no-file-receipt", action="store_true",
        help="Synthetic old peer omits save receipts; test the bounded queued-tail drain")
    args = parser.parse_args()
    if not 0 <= args.file_silence_seconds <= 120 or (args.file_silence_seconds or args.upload_only) and not args.files:
        parser.error("File silence (0..120 seconds) and upload-only require --files")
    if not args.serial.startswith("emulator-"):
        if not args.mumu_manager or not args.mumu_manager.is_file() or not args.serial.startswith("127.0.0.1:"):
            raise RuntimeError("This clipboard test is restricted to a disposable emulator")
        instances = json.loads(subprocess.run([str(args.mumu_manager), "info", "-v", "all"],
            capture_output=True, check=True, timeout=15, encoding="utf-8").stdout)
        if not any(instance.get("is_android_started") and instance.get("adb_host_ip") == "127.0.0.1"
            and args.serial == "127.0.0.1:" + str(instance.get("adb_port")) for instance in instances.values()):
            raise RuntimeError("The selected serial is not a running MuMu emulator reported by its manager")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    adb_prefix = [args.adb, "-s", args.serial]
    report = {"scope": "Android product viewer code/UI, real Android clipboard, authenticated synthetic TCP peer; no real remote OS input", "checks": []}
    stop = threading.Event()
    frames_paused = threading.Event()
    peer = {"gets": 0, "sets": [], "inputs": [], "clipboard": REMOTE_TEXT, "file_starts": 0, "files": []}
    file_data = bytes(range(256)) * (12 * 1024 if args.file_silence_seconds and not args.legacy_no_file_receipt else 1024) + "中文😀\r\n".encode("utf-8")
    fixture = output / ("RemoteDesk-upload-" + uuid.uuid4().hex[:8] + ".bin")
    device_fixture = "/sdcard/Download/" + fixture.name
    if args.files:
        fixture.write_bytes(file_data)
        (output / "received").mkdir()
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(20)
    port = listener.getsockname()[1]
    image = io.BytesIO()
    Image.new("RGB", (1280, 720), "#e2e8f0").save(image, format="JPEG")
    frame = struct.pack("<iidd", 1280, 720, 0, 0) + image.getvalue()

    def save():
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def check(name, passed):
        report["checks"].append({"name": name, "passed": bool(passed)})
        save()
        print(("PASS " if passed else "FAIL ") + name, flush=True)
        if not passed:
            raise AssertionError(name)

    def adb(*command):
        return subprocess.run(adb_prefix + list(map(str, command)), check=True, capture_output=True, timeout=25).stdout.decode("utf-8").strip()

    def server():
        incoming = None
        try:
            with listener.accept()[0] as client:
                client.settimeout(20)
                session = wire.authenticate_server(client, "RemoteDesk-synthetic-ui-fixture")
                write_lock = threading.Lock()
                def send(kind, payload):
                    with write_lock:
                        wire.write_message(client, session, kind, payload)
                caps = wire.CAPABILITY_REMOTE_DESKTOP | wire.CAPABILITY_INPUT_CONTROL | wire.CAPABILITY_CLIPBOARD_TEXT
                if args.files:
                    caps |= wire.CAPABILITY_FILE_RECEIVE | wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_CANCEL | wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                    if args.legacy_no_file_receipt: caps &= ~wire.CAPABILITY_FILE_TRANSFER_RECEIPT
                send(wire.MESSAGE_CONTROL, wire.encode_device_info("Clipboard test PC", "Windows", caps))
                def frames():
                    try:
                        while not stop.wait(.1):
                            if not frames_paused.is_set(): send(wire.MESSAGE_FRAME, frame)
                    except OSError:
                        pass
                threading.Thread(target=frames, daemon=True).start()
                held = set()
                while not stop.is_set():
                    kind, payload = wire.read_message(client, session)
                    if kind == wire.MESSAGE_PING:
                        send(wire.MESSAGE_PONG, payload)
                    elif kind == wire.MESSAGE_INPUT:
                        event = struct.unpack("<BBiii", payload)
                        peer["inputs"].append(event)
                        if event[0] == 5:
                            if event[4] == 0x43 and 0x11 in held:
                                peer["clipboard"] = REMOTE_TEXT
                            held.add(event[4])
                        elif event[0] == 6:
                            held.discard(event[4])
                    elif kind == wire.MESSAGE_CONTROL:
                        control = wire.decode_control(payload, include_clipboard_text=True)
                        if control["kind"] == wire.CONTROL_CLIPBOARD_SET_TEXT:
                            peer["clipboard"] = control["text"]
                            peer["sets"].append(sha(control["text"]))
                            time.sleep(.3)  # Slow real-world system clipboard writes.
                            send(wire.MESSAGE_CONTROL, wire.encode_clipboard_status(True, "Clipboard updated."))
                        elif control["kind"] == wire.CONTROL_CLIPBOARD_GET_TEXT:
                            peer["gets"] += 1
                            send(wire.MESSAGE_CONTROL, wire.encode_clipboard_text(peer["clipboard"]))
                        elif control["kind"] == wire.CONTROL_FILE_TRANSFER_START:
                            peer["file_starts"] += 1
                            incoming = wire.start_incoming_file_transfer(payload, output / "received", True)
                            if args.file_silence_seconds:
                                frames_paused.set()
                                paused_at = time.monotonic()
                                stop.wait(args.file_silence_seconds)
                                peer["file_silence_seconds"] = time.monotonic() - paused_at
                                frames_paused.clear()
                        elif control["kind"] == wire.CONTROL_FILE_TRANSFER_CHUNK:
                            wire.write_incoming_file_chunk(payload, incoming)
                        elif control["kind"] == wire.CONTROL_FILE_TRANSFER_CHECKSUM:
                            wire.set_expected_file_checksum(payload, incoming)
                        elif control["kind"] == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                            saved = wire.complete_incoming_file_transfer(payload, incoming)
                            incoming = None
                            peer["files"].append(str(saved["path"]))
                            time.sleep(.8)
                            if not args.legacy_no_file_receipt:
                                send(wire.MESSAGE_CONTROL, wire.encode_file_transfer_receipt(control["transferId"], True, "文件已保存到测试接收目录"))
                        elif control["kind"] == wire.CONTROL_FILE_TRANSFER_CANCEL and incoming is not None:
                            wire.cancel_incoming_file_transfer(payload, incoming)
                            incoming = None
        except (OSError, EOFError):
            pass
        except Exception as failure:
            peer["failure"] = type(failure).__name__
        finally:
            if incoming is not None: incoming.abort()
            stop.set()

    def wait(predicate, seconds=15):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            if peer.get("failure"):
                raise RuntimeError(peer["failure"])
            time.sleep(.1)
        raise TimeoutError("Clipboard fixture condition not reached")

    def state():
        try:
            return json.loads(adb("shell", "run-as", PACKAGE, "cat", "files/viewer-state.json"))
        except (subprocess.CalledProcessError, ValueError):
            return {}

    def action(name):
        adb("shell", "am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver", "--es", "action", name)
        time.sleep(.3)

    def clipboard_hash():
        action("clipboard_snapshot")
        return adb("shell", "run-as", PACKAGE, "cat", "files/clipboard-sha256.txt")

    def tap(text):
        labels = (text,) if isinstance(text, str) else text
        for attempt in range(3):
            # Never use a previous page's bounds after a failed snapshot.
            adb("shell", "rm", "-f", "/data/local/tmp/remotedesk-clipboard-test.xml")
            try:
                adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-clipboard-test.xml")
                break
            except subprocess.CalledProcessError as failure:
                # MuMu's uiautomator process can crash during an Activity transition.
                # Retry only this read-only snapshot; never replay an input gesture.
                if failure.returncode in (135, 139) and b"dumped to:" in failure.stdout:
                    report["toolWarning"] = "MuMu uiautomator crashed on exit after writing a fresh XML snapshot; only the fresh scoped tree was used"
                    break
                if failure.returncode not in (135, 139) or attempt == 2:
                    raise
                time.sleep(.4)
        tree = ET.fromstring(adb("shell", "cat", "/data/local/tmp/remotedesk-clipboard-test.xml"))
        allowed = {PACKAGE, "com.android.documentsui", "com.google.android.documentsui"}
        matches = [node for node in tree.iter("node") if node.get("package") in allowed
            and (node.get("text") in labels or node.get("content-desc") in labels)]
        if len(matches) != 1:
            (output / "failed-ui.xml").write_bytes(ET.tostring(tree, encoding="utf-8"))
            (output / "failed-ui.png").write_bytes(subprocess.run(adb_prefix + ["exec-out", "screencap", "-p"],
                capture_output=True, check=True, timeout=25).stdout)
            raise RuntimeError("Expected one visible control: " + str(text) + "; found " + str(len(matches)))
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", matches[0].get("bounds")))
        adb("shell", "input", "tap", (x1+x2)//2, (y1+y2)//2)

    def clipboard_menu(item):
        tap("更多")
        tap("文字剪贴板")
        tap(item)

    worker = threading.Thread(target=server, daemon=True)
    worker.start()
    try:
        adb("reverse", "tcp:" + str(port), "tcp:" + str(port))
        adb("shell", "am", "start", "-W", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeLauncher", "--ei", "port", port,
            "--ez", "legacyFileWatchdogProbe", str(args.legacy_file_watchdog).lower())
        report["legacyFileWatchdogProbe"] = args.legacy_file_watchdog
        report["legacyNoFileReceipt"] = args.legacy_no_file_receipt
        wait(lambda: state().get("frame", [0, 0])[0] == 1280)
        check("actual viewer receives encrypted fixture frames", True)
        if args.files:
            adb("push", str(fixture), device_fixture)
            tap("更多"); tap("发送文件…")
            wait(lambda: any("mCurrentFocus=" in line and ".documentsui/" in line
                for line in adb("shell", "dumpsys", "window").splitlines()))
            adb("shell", "input", "keyevent", "4")
            wait(lambda: any("mCurrentFocus=" in line and PACKAGE + "/" in line
                for line in adb("shell", "dumpsys", "window").splitlines()))
            check("cancel system file picker sends no file data", peer["file_starts"] == 0)
            def select_file():
                tap("更多"); tap("发送文件…")
                try:
                    tap(fixture.name)
                except RuntimeError:
                    tap(("Show roots", "显示根目录"))
                    tap(("Downloads", "下载"))
                    tap(fixture.name)
            select_file()
            tap("取消")
            check("cancel upload confirmation sends no file data", peer["file_starts"] == 0)
            select_file()
            original_owner = state().get("ownerGeneration")
            tap("发送")
            if args.legacy_no_file_receipt:
                wait(lambda: "对方是旧版" in state().get("status", ""))
                queued = state()
                check("legacy upload reports unconfirmed save while a bounded tail drain remains active",
                    not queued.get("fileBusy", True) and queued.get("fileLegacyDrain", False))
            wait(lambda: len(peer["files"]) == 1, 15 + args.file_silence_seconds)
            check("Android picker upload reaches actual receiver with exact bytes", Path(peer["files"][0]).read_bytes() == file_data)
            wait(lambda: ("对方是旧版" if args.legacy_no_file_receipt else "文件已保存到测试接收目录") in state().get("status", ""))
            tap("知道了")
            check("legacy result does not pretend to have a remote save receipt" if args.legacy_no_file_receipt else
                "Android upload completion is confirmed by remote save receipt", True)
            if args.file_silence_seconds:
                check("upload survives delayed reading and missing inbound frames without reconnect",
                    peer.get("file_silence_seconds", 0) >= args.file_silence_seconds and state().get("ownerGeneration") == original_owner)
                report["fileSilenceSeconds"] = peer["file_silence_seconds"]
                report["fileBytes"] = len(file_data)
            if args.upload_only:
                report["complete"] = True
                save()
                return
        action("clipboard_seed")
        check("real Android local clipboard seeded without character loss", clipboard_hash() == sha(PHONE_TEXT))
        clipboard_menu("发送手机剪贴板到远端")
        wait(lambda: len(peer["sets"]) == 1)
        check("Android send clipboard UI transmits exact UTF-8 text", peer["sets"][0] == sha(PHONE_TEXT))
        wait(lambda: "已写入远端" in state().get("status", ""))
        peer["clipboard"] = REMOTE_TEXT
        clipboard_menu("取回远端文字到手机")
        wait(lambda: "已将远端文字" in state().get("status", ""))
        check("Android receive clipboard UI writes real system clipboard", clipboard_hash() == sha(REMOTE_TEXT))
        peer["clipboard"] = ""
        clipboard_menu("取回远端文字到手机")
        wait(lambda: "保持不变" in state().get("status", ""))
        check("empty remote result preserves Android local clipboard", clipboard_hash() == sha(REMOTE_TEXT))
        action("clipboard_seed")
        clipboard_menu("粘贴手机文字到远端输入框")
        wait(lambda: any(item[0] == 5 and item[4] == 0x56 for item in peer["inputs"]))
        check("paste UI sends full clipboard and native paste shortcut", peer["sets"][-1] == sha(PHONE_TEXT))
        tap("键盘")
        previous_gets = peer["gets"]
        tap("Ctrl+C")
        wait(lambda: peer["gets"] > previous_gets)
        wait(lambda: "已将远端文字" in state().get("status", ""))
        check("Ctrl+C automatically returns the remote clipboard", clipboard_hash() == sha(REMOTE_TEXT))
        check("no viewer exception", not state().get("failure"))
        report["complete"] = True
        save()
    except Exception as failure:
        report["failure"] = type(failure).__name__
        report["lastViewerState"] = state()
        report["peerFailure"] = peer.get("failure", "")
        save()
        raise
    finally:
        stop.set()
        adb("shell", "am", "force-stop", PACKAGE)
        adb("reverse", "--remove", "tcp:" + str(port))
        listener.close()
        worker.join(2)
        if args.files:
            adb("shell", "rm", "-f", device_fixture)


if __name__ == "__main__":
    main()
