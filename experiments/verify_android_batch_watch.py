#!/usr/bin/env python3
"""Owned AVD only: actual viewer UI + synthetic private documents + encrypted loopback peer."""
import argparse
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
import xml.etree.ElementTree as ET
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_protocol_probe as wire

PACKAGE = "com.remotedesk.viewerprobe"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--avd", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        raise RuntimeError("Only an explicitly owned AVD is permitted")
    prefix = [args.adb, "-s", args.serial]

    def adb(*command):
        return subprocess.run(prefix + list(map(str, command)), check=True, capture_output=True, timeout=30).stdout.decode("utf-8").strip()

    if args.avd not in adb("emu", "avd", "name").splitlines():
        raise RuntimeError("Serial does not match the owned AVD")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    received = output / "received"
    received.mkdir()
    report = {"scope": "Owned AVD, isolated viewerprobe product code; synthetic documents and authenticated loopback peer only", "checks": []}
    peer = {"inputs": [], "sets": [], "starts": [], "files": [], "cancelled": [], "connections": 0}
    stop = threading.Event()
    delay_clipboard = threading.Event()
    slow_files = threading.Event()
    omit_receipts = threading.Event()
    active = {}
    caps = (wire.CAPABILITY_REMOTE_DESKTOP | wire.CAPABILITY_INPUT_CONTROL | wire.CAPABILITY_CLIPBOARD_TEXT |
        wire.CAPABILITY_FILE_RECEIVE | wire.CAPABILITY_FILE_CHECKSUM | wire.CAPABILITY_FILE_TRANSFER_CANCEL |
        wire.CAPABILITY_FILE_TRANSFER_RECEIPT | wire.CAPABILITY_FILE_RECEIVE_LOCATION)
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(2)
    listener.settimeout(.5)
    port = listener.getsockname()[1]
    image = io.BytesIO()
    Image.new("RGB", (1280, 720), "#dbe7ef").save(image, format="JPEG")
    frame = struct.pack("<iidd", 1280, 720, 0, 0) + image.getvalue()

    def save():
        report["peer"] = peer
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def check(name, passed):
        report["checks"].append({"name": name, "passed": bool(passed)})
        save()
        print(("PASS " if passed else "FAIL ") + name, flush=True)
        if not passed:
            raise AssertionError(name)

    def server():
        while not stop.is_set():
            try:
                client = listener.accept()[0]
            except socket.timeout:
                continue
            incoming = None
            ended = threading.Event()
            try:
                with client:
                    client.settimeout(20)
                    session = wire.authenticate_server(client, "RemoteDesk-synthetic-ui-fixture")
                    write_lock = threading.Lock()

                    def send(kind, payload):
                        with write_lock:
                            wire.write_message(client, session, kind, payload)

                    active.update(client=client, send=send)
                    peer["connections"] += 1
                    send(wire.MESSAGE_CONTROL, wire.encode_device_info("合成测试电脑", "Windows", caps))

                    def frames(connection_send=send, connection_ended=ended):
                        try:
                            while not connection_ended.wait(.2) and not stop.is_set():
                                connection_send(wire.MESSAGE_FRAME, frame)
                        except OSError:
                            pass

                    threading.Thread(target=frames, daemon=True).start()
                    while not stop.is_set():
                        kind, payload = wire.read_message(client, session)
                        if kind == wire.MESSAGE_PING:
                            send(wire.MESSAGE_PONG, payload)
                        elif kind == wire.MESSAGE_INPUT:
                            peer["inputs"].append(list(struct.unpack("<BBiii", payload)))
                        elif kind == wire.MESSAGE_CONTROL:
                            control = wire.decode_control(payload, include_clipboard_text=True)
                            code = control["kind"]
                            if code == wire.CONTROL_CLIPBOARD_SET_TEXT:
                                peer["sets"].append(control["text"])
                                if delay_clipboard.is_set():
                                    time.sleep(4)
                                send(wire.MESSAGE_CONTROL, wire.encode_clipboard_status(True, "Synthetic clipboard stored"))
                            elif code == wire.CONTROL_FILE_RECEIVE_LOCATION_REQUEST:
                                send(wire.MESSAGE_CONTROL, wire.encode_file_receive_location(control["transferId"], True,
                                    "C:\\SyntheticReceiver\\Files", "仅合成测试目录"))
                            elif code == wire.CONTROL_FILE_TRANSFER_START:
                                peer["starts"].append(control)
                                incoming = wire.start_incoming_file_transfer(payload, received, True)
                            elif code == wire.CONTROL_FILE_TRANSFER_CHUNK:
                                wire.write_incoming_file_chunk(payload, incoming)
                                if slow_files.is_set():
                                    time.sleep(.045)
                            elif code == wire.CONTROL_FILE_TRANSFER_CHECKSUM:
                                wire.set_expected_file_checksum(payload, incoming)
                            elif code == wire.CONTROL_FILE_TRANSFER_COMPLETE:
                                saved = wire.complete_incoming_file_transfer(payload, incoming)
                                incoming = None
                                peer["files"].append(saved["path"])
                                if not omit_receipts.is_set():
                                    send(wire.MESSAGE_CONTROL, wire.encode_file_transfer_receipt(control["transferId"], True,
                                        "已保存到合成接收目录：" + saved["name"]))
                            elif code == wire.CONTROL_FILE_TRANSFER_CANCEL:
                                peer["cancelled"].append(control["transferId"])
                                if incoming is not None:
                                    wire.cancel_incoming_file_transfer(payload, incoming)
                                    incoming = None
            except (OSError, EOFError):
                pass
            except Exception as error:
                peer["failure"] = repr(error)
            finally:
                ended.set()
                if incoming is not None:
                    incoming.abort()

    def state():
        try:
            return json.loads(adb("shell", "run-as", PACKAGE, "cat", "files/viewer-state.json"))
        except (ValueError, subprocess.CalledProcessError):
            return {}

    def wait(predicate, seconds=20):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            if peer.get("failure"):
                raise RuntimeError(peer["failure"])
            time.sleep(.1)
        raise TimeoutError("Fixture condition not reached")

    def action(name, *extras):
        adb("shell", "am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver",
            "--es", "action", name, *extras)
        time.sleep(.3)

    def tree():
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-batch-watch.xml")
        return ET.fromstring(adb("shell", "cat", "/data/local/tmp/remotedesk-batch-watch.xml"))

    def tap(label):
        root = tree()
        nodes = [n for n in root.iter("node") if n.get("package") == PACKAGE and n.get("text") == label]
        if len(nodes) != 1:
            (output / "failed-ui.xml").write_bytes(ET.tostring(root))
            raise RuntimeError("Expected one control " + label + "; found " + str(len(nodes)))
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", nodes[0].get("bounds")))
        adb("shell", "input", "tap", (x1+x2)//2, (y1+y2)//2)
        time.sleep(.2)

    def screenshot(name):
        (output / (name + ".png")).write_bytes(subprocess.run(prefix + ["exec-out", "screencap", "-p"],
            check=True, capture_output=True, timeout=30).stdout)
        (output / (name + ".json")).write_text(json.dumps(state(), ensure_ascii=False, indent=2), encoding="utf-8")

    def select(indices, single=False):
        action("select_files", "--es", "indices", indices, "--ez", "single", str(single).lower())
        wait(lambda: any(n.get("text", "").startswith("确认发送 ") for n in tree().iter("node")))

    threading.Thread(target=server, daemon=True).start()
    try:
        adb("shell", "am", "force-stop", PACKAGE)
        adb("reverse", "tcp:" + str(port), "tcp:" + str(port))
        adb("shell", "am", "start", "-W", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeLauncher", "--ei", "port", port)
        wait(lambda: state().get("geometryReady") and state().get("frame", [0])[0] == 1280)
        check("actual viewer receives authenticated synthetic frames", True)

        action("drag"); action("key_down")
        wait(lambda: any(e[0] == 2 for e in peer["inputs"]) and any(e[0] == 5 for e in peer["inputs"]))
        action("pause_input")
        wait(lambda: any(e[0] == 3 for e in peer["inputs"]) and any(e[0] == 6 for e in peer["inputs"]))
        check("pause releases transmitted mouse drag and held Ctrl", not state().get("lockedDrag"))
        before = len(peer["inputs"])
        action("clipboard_seed"); action("clipboard_paste"); action("clipboard_send"); action("paused_keyboard")
        action("remote_tap", "--ef", "x", ".4", "--ef", "y", ".5")
        zoom = state().get("zoom", 1)
        action("pinch")
        check("paused touch, text, Ctrl+V, navigation and remote clipboard writes emit nothing", len(peer["inputs"]) == before and not peer["sets"])
        check("local pinch zoom remains available while input is paused", state().get("zoom", 1) > zoom)
        screenshot("watch-only")

        active["send"](wire.MESSAGE_CONTROL, wire.encode_device_info("合成测试电脑", "Android", caps))
        time.sleep(.8)
        action("paused_keyboard")
        check("capability/platform updates cannot re-enable local input", state().get("localInputPaused") and not state().get("keyboardEnabled"))
        check("Android navigation also remains blocked while paused", len(peer["inputs"]) == before)
        old_owner = state()["ownerGeneration"]
        active["client"].shutdown(socket.SHUT_RDWR)
        wait(lambda: peer["connections"] >= 2 and state().get("ownerGeneration", 0) > old_owner and state().get("geometryReady"), 30)
        check("automatic reconnect preserves only-watch state", state().get("localInputPaused") and not state().get("keyboardEnabled"))

        connections = peer["connections"]
        action("recreate_viewer")
        wait(lambda: peer["connections"] > connections and state().get("geometryReady"), 30)
        check("Activity saved state preserves the local pause choice", state().get("localInputPaused") and not state().get("keyboardEnabled"))

        select("0,1,0")
        ui = ET.tostring(tree(), encoding="unicode")
        check("ClipData plus fallback deduplicates and confirms both names, sizes and destination", "确认发送 2 个文件" in ui and "合成测试-0.bin" in ui and "合成测试-1.bin" in ui and "SyntheticReceiver" in ui)
        screenshot("batch-confirmation")
        tap("取消")
        check("cancelled batch confirmation sends no file start or bytes", len(peer["starts"]) == 0)
        select("0,1,0"); tap("发送")
        wait(lambda: len(peer["files"]) == 2 and not state().get("fileBusy", True))
        check("confirmed files continue sequentially in only-watch with per-file save receipts", "已确认保存 2" in state().get("fileBatch", "") and len(peer["starts"]) == 2)
        check("uploaded files retain exact distinct synthetic bytes", Path(peer["files"][0]).read_bytes() == bytes([1])*1024 and Path(peer["files"][1]).read_bytes() == bytes([2])*2048)
        check("completed file queue never triggers remote clipboard or paste", len(peer["inputs"]) == before and not peer["sets"])
        screenshot("batch-results"); tap("知道了")

        starts = len(peer["starts"])
        select("0,1")
        action("block_writes")
        tap("发送")
        wait(lambda: state().get("fileSenderActive"))
        tap("取消剩余")
        action("unblock_writes")
        wait(lambda: not state().get("fileBusy", True))
        check("cancel rechecks queued file START after acquiring the shared write lock", len(peer["starts"]) == starts)
        tap("知道了")

        slow_files.set()
        starts = len(peer["starts"])
        select("2,1"); tap("发送")
        wait(lambda: len(peer["starts"]) > starts)
        screenshot("batch-progress")
        tap("取消剩余")
        wait(lambda: not state().get("fileBusy", True), 25)
        check("cancelling active batch leaves later file unstarted", len(peer["starts"]) == starts + 1 and "取消 2" in state().get("fileBatch", ""))
        screenshot("batch-cancelled"); tap("知道了")
        wait(lambda: len(peer["cancelled"]) == 1)

        active["send"](wire.MESSAGE_CONTROL, wire.encode_device_info("无取消能力合成电脑", "Windows", caps & ~wire.CAPABILITY_FILE_TRANSFER_CANCEL))
        time.sleep(.5)
        starts, saved, cancelled = len(peer["starts"]), len(peer["files"]), len(peer["cancelled"])
        select("2,1"); tap("发送")
        wait(lambda: len(peer["starts"]) == starts + 1)
        tap("取消剩余")
        screenshot("legacy-finishing-current")
        check("legacy peer clearly explains that only pending files are cancelled", "当前文件将继续完成" in state().get("fileBatch", ""))
        wait(lambda: not state().get("fileBusy", True) and len(peer["files"]) == saved + 1, 30)
        check("peer without Cancel finishes the active file, withholds later file and sends no unsupported Cancel",
            len(peer["starts"]) == starts + 1 and len(peer["cancelled"]) == cancelled and "已确认保存 1" in state().get("fileBatch", "") and "取消 1" in state().get("fileBatch", ""))
        check("legacy active file completed with exact bytes", Path(peer["files"][-1]).read_bytes() == bytes([3]) * (8 * 1024 * 1024))
        screenshot("legacy-cancel-results"); tap("知道了")
        slow_files.clear()

        omit_receipts.set()
        active["send"](wire.MESSAGE_CONTROL, wire.encode_device_info("旧版合成电脑", "Windows", caps & ~wire.CAPABILITY_FILE_TRANSFER_RECEIPT))
        time.sleep(.5)
        select("0", single=True); tap("发送")
        wait(lambda: not state().get("fileBusy", True))
        check("single URI fallback still works and legacy send is not claimed as saved", "已确认保存 0 · 已发送待核实 1" in state().get("fileBatch", ""))
        screenshot("legacy-unconfirmed"); tap("知道了")

        action("pause_input")
        action("block_writes")
        action("clipboard_paste")
        action("key_down")
        action("pause_input")
        action("unblock_writes")
        time.sleep(.7)
        check("revoked queued key and clipboard SET are rechecked after acquiring the write lock", len(peer["inputs"]) == before and not peer["sets"])
        action("pause_input")
        delay_clipboard.set()
        action("clipboard_paste")
        wait(lambda: len(peer["sets"]) == 1)
        action("pause_input"); action("pause_input")
        time.sleep(4.5)
        check("pause then resume invalidates delayed clipboard paste acknowledgement", len(peer["inputs"]) == before)
        action("remote_tap", "--ef", "x", ".5", "--ef", "y", ".5")
        wait(lambda: len(peer["inputs"]) > before)
        check("explicit resume allows fresh input without replaying revoked Ctrl+V", not any(e[0] == 5 and e[4] == 0x56 for e in peer["inputs"]))
        check("probe completed without Java callback failures", not state().get("failure"))
        report["passed"] = True
    finally:
        screenshot("final")
        stop.set()
        if active.get("client"):
            try:
                active["client"].shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        adb("shell", "am", "force-stop", PACKAGE)
        adb("reverse", "--remove", "tcp:" + str(port))
        save()


if __name__ == "__main__":
    main()
