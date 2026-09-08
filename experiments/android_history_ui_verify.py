#!/usr/bin/env python3
"""Signed product APK history checks on an owned emulator; public synthetic peer only."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

PACKAGE = "com.remotedesk.agent"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--upgrade-only", action="store_true", help="Check the remaining synthetic 7412 node after an APK overwrite update")
    args = parser.parse_args()
    if not re.fullmatch(r"emulator-\d+", args.serial):
        raise ValueError("This test overwrites synthetic viewer fields and restarts the app; emulator only")
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"scope": "Signed production APK on owned emulator; encrypted synthetic TCP peer, no remote OS input", "checks": []}

    def adb(*command):
        return subprocess.run([args.adb, "-s", args.serial, *map(str, command)], check=True,
                              capture_output=True, timeout=25).stdout.decode("utf-8").strip()

    def check(name, passed):
        report["checks"].append({"name": name, "passed": bool(passed)})
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not passed:
            raise AssertionError(name)

    def ui():
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-history-ui.xml")
        return ET.fromstring(adb("shell", "cat", "/data/local/tmp/remotedesk-history-ui.xml"))

    def nodes(tree):
        return [n for n in tree.iter("node") if n.get("package") == PACKAGE]

    def tap_node(node):
        bounds = list(map(int, re.findall(r"\d+", node.get("bounds", ""))))
        if len(bounds) != 4 or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
            raise RuntimeError("Test control is outside visible bounds")
        adb("shell", "input", "tap", (bounds[0]+bounds[2])//2, (bounds[1]+bounds[3])//2)

    def tap(text=None, desc=None):
        found = [n for n in nodes(ui()) if n.get("enabled") == "true" and
                 (n.get("text") == text if text is not None else n.get("content-desc") == desc)]
        if len(found) != 1:
            raise RuntimeError("Expected exactly one test control: " + str(text or desc))
        tap_node(found[0])
        time.sleep(.3)

    def field(label):
        tree = ui()
        for parent in tree.iter("node"):
            children = list(parent)
            for i, child in enumerate(children[:-1]):
                if child.get("text") == label and child.get("package") == PACKAGE:
                    candidate = children[i+1]
                    if candidate.get("class") == "android.widget.EditText":
                        return candidate
        raise RuntimeError("Labeled field unavailable: " + label)

    def replace(node, value):
        if not re.fullmatch(r"[A-Za-z0-9.:-]{1,128}", value):
            raise ValueError("Synthetic ASCII only")
        tap_node(node)
        adb("shell", "input", "keyevent", "KEYCODE_MOVE_END")
        adb("shell", "input", "keyevent", *(["KEYCODE_DEL"] * 128))
        adb("shell", "input", "text", value)
        adb("shell", "input", "keyevent", "KEYCODE_BACK")

    def start(restart=False):
        if restart:
            adb("shell", "am", "force-stop", PACKAGE)
        adb("shell", "am", "start", "-W", "-n", PACKAGE + "/.MainActivity")
        time.sleep(.8)

    def peer():
        return json.loads(Path(args.server).read_text(encoding="utf-8"))

    def await_session(before):
        deadline = time.monotonic()+15
        while time.monotonic() < deadline:
            if peer()["sessions"] > before:
                time.sleep(1.5)
                return
            time.sleep(.2)
        raise TimeoutError("No authenticated synthetic session")

    def leave():
        adb("shell", "input", "keyevent", "KEYCODE_BACK")
        tap("断开")
        time.sleep(.5)

    start(restart=True)
    if args.upgrade_only:
        check("Overwrite update preserves the previously saved node", any(n.get("text") == "最近连接 · 1" for n in nodes(ui())))
        check("Overwrite update restores the node address", field("远端地址").get("text") == "127.0.0.1:7412")
        tap(desc="管理连接 Synthetic test PC"); tap("修改备注")
        edits = [n for n in nodes(ui()) if n.get("class") == "android.widget.EditText"]
        check("Updated remark dialog is editable", len(edits) == 1)
        replace(edits[0], "Upgrade-Node"); tap("保存")
        start(restart=True)
        check("Updated remark persists after restart", any(n.get("content-desc") == "管理连接 Upgrade-Node" for n in nodes(ui())))
        before = peer()["sessions"]; tap(desc="连接 Upgrade-Node，直连 127.0.0.1:7412"); await_session(before); leave()
        check("Updated APK reconnects with the preserved encrypted credential", peer()["sessions"] > before)
        report["completed"] = True
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"completed": True, "checks": len(report["checks"])}))
        return
    check("Fresh install shows an empty recent-connections section", any(n.get("text") == "最近连接" for n in nodes(ui())))
    replace(field("远端地址"), "127.0.0.1:7411")
    replace(field("连接口令"), "RemoteDesk-synthetic-ui-fixture")
    before = peer()["sessions"]; tap("控制远端"); await_session(before); leave()
    check("Successful authenticated connection creates history", any(n.get("text") == "最近连接 · 1" for n in nodes(ui())))
    tap(desc="管理连接 Synthetic test PC"); tap("修改备注")
    edits = [n for n in nodes(ui()) if n.get("class") == "android.widget.EditText"]
    check("Remark dialog has one editable field", len(edits) == 1)
    replace(edits[0], "History-Node-A"); tap("保存")
    start(restart=True)
    check("Remark and history survive process restart", any(n.get("content-desc") == "管理连接 History-Node-A" for n in nodes(ui())))
    check("Last successful direct address is restored", field("远端地址").get("text") == "127.0.0.1:7411")
    replace(field("远端地址"), "127.0.0.1:7412")
    before = peer()["sessions"]; tap("控制远端"); await_session(before); leave()
    check("A different port creates a separate node", any(n.get("text") == "最近连接 · 2" for n in nodes(ui())))
    replace(field("远端地址"), "127.0.0.1:7419")
    replace(field("连接口令"), "Deliberately-wrong-fixture-password")
    tap("控制远端"); time.sleep(2); leave()
    check("Connection failure does not add a recent node", any(n.get("text") == "最近连接 · 2" for n in nodes(ui())))
    before = peer()["sessions"]; tap(desc="连接 History-Node-A，直连 127.0.0.1:7411"); await_session(before); leave()
    check("One-tap history reconnect uses its own credential, not the wrong global password", peer()["sessions"] > before)
    check("Reconnect deduplicates the existing node and preserves its remark", any(n.get("text") == "最近连接 · 2" for n in nodes(ui())) and
          any(n.get("content-desc") == "管理连接 History-Node-A" for n in nodes(ui())))
    tap(desc="管理连接 History-Node-A"); tap("删除记录"); tap("取消")
    check("Canceling deletion preserves the record", any(n.get("content-desc") == "管理连接 History-Node-A" for n in nodes(ui())))
    tap(desc="管理连接 History-Node-A"); tap("删除记录"); tap("删除")
    start(restart=True)
    check("Confirmed deletion persists without deleting the other node", any(n.get("text") == "最近连接 · 1" for n in nodes(ui())) and
          not any(n.get("content-desc") == "管理连接 History-Node-A" for n in nodes(ui())))
    before = peer()["sessions"]; tap(desc="连接 Synthetic test PC，直连 127.0.0.1:7412"); await_session(before); leave()
    check("The remaining node still reconnects after restart and deletion", peer()["sessions"] > before)
    adb("shell", "screencap", "-p", "/data/local/tmp/remotedesk-history-check.png")
    adb("pull", "/data/local/tmp/remotedesk-history-check.png", str(output / "history.png"))
    report["completed"] = True
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"completed": True, "checks": len(report["checks"])}))


if __name__ == "__main__":
    main()
