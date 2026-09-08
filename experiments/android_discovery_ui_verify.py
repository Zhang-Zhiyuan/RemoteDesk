#!/usr/bin/env python3
"""Discovery transport reports and signed-APK UI checks; synthetic credentials on an emulator only."""
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
    parser.add_argument("--output", required=True)
    parser.add_argument("--phase", choices=("collect-probe", "create", "moved"), required=True)
    parser.add_argument("--server")
    parser.add_argument("--old-port", type=int)
    args = parser.parse_args()
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)

    def adb(*command):
        return subprocess.run([args.adb, "-s", args.serial, *map(str, command)], check=True,
                              capture_output=True, timeout=25).stdout.decode("utf-8").strip()

    if args.phase == "collect-probe":
        report = json.loads(adb("exec-out", "run-as", "com.remotedesk.viewerprobe", "cat", "files/discovery-state.json"))
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not report.get("completed") or not all(c["passed"] for c in report["checks"]):
            raise AssertionError("Discovery transport probe did not pass")
        print(json.dumps({"completed":True, "sdk":report["sdk"], "checks":len(report["checks"])}))
        return
    if not re.fullmatch(r"emulator-\d+", args.serial) or not args.server:
        raise ValueError("Credentialed UI checks are restricted to the owned emulator and synthetic peer")
    report = {"scope":"Signed production APK, synthetic TCP/UDP peer via emulator host alias; no OS input", "checks":[]}

    def check(name, passed):
        report["checks"].append({"name":name,"passed":bool(passed)})
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not passed: raise AssertionError(name)

    def peer(): return json.loads(Path(args.server).read_text(encoding="utf-8"))
    port = peer()["port"]; endpoint = f"10.0.2.2:{port}"
    def ui():
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-discovery-ui.xml")
        return ET.fromstring(adb("exec-out", "cat", "/data/local/tmp/remotedesk-discovery-ui.xml"))
    def nodes(tree): return [n for n in tree.iter("node") if n.get("package") == PACKAGE]
    def bounds(node): return list(map(int, re.findall(r"\d+", node.get("bounds", ""))))
    def visible(node):
        b = bounds(node)
        return len(b) == 4 and b[2] > b[0] and b[3] > b[1]
    def tap_node(node):
        if not visible(node): raise RuntimeError("Control is not visible")
        b = bounds(node); adb("shell", "input", "tap", (b[0]+b[2])//2, (b[1]+b[3])//2)
    def find(predicate, scroll=False):
        for attempt in range(7 if scroll else 1):
            found = [n for n in nodes(ui()) if n.get("enabled") == "true" and visible(n) and predicate(n)]
            if len(found) == 1: return found[0]
            if len(found) > 1: raise RuntimeError("Ambiguous test control")
            if scroll:
                width, height = map(int, re.search(r"(\d+)x(\d+)", adb("shell", "wm", "size")).groups())
                adb("shell", "input", "swipe", width//2, int(height*.79), width//2, int(height*.30), 250)
        raise RuntimeError("Expected test control was not visible")
    def tap(text=None, desc=None, scroll=False):
        tap_node(find(lambda n: n.get("text") == text if text is not None else n.get("content-desc") == desc, scroll))
        time.sleep(.25)
    def field(label):
        tree = ui()
        for parent in tree.iter("node"):
            children = list(parent)
            for i, child in enumerate(children[:-1]):
                if child.get("text") == label and child.get("package") == PACKAGE:
                    candidate = children[i+1]
                    if candidate.get("class") == "android.widget.EditText" and visible(candidate): return candidate
        raise RuntimeError("Missing labeled field: " + label)
    def replace(node, value):
        if not re.fullmatch(r"[A-Za-z0-9.:-]{1,128}", value): raise ValueError("Synthetic ASCII only")
        tap_node(node); adb("shell", "input", "keyevent", "KEYCODE_MOVE_END")
        adb("shell", "input", "keyevent", *(["KEYCODE_DEL"]*128))
        adb("shell", "input", "text", value); adb("shell", "input", "keyevent", "KEYCODE_BACK")
    def start():
        adb("shell", "am", "force-stop", PACKAGE)
        adb("shell", "am", "start", "-W", "-n", PACKAGE+"/.MainActivity"); time.sleep(2.5)
    def connected(before):
        deadline = time.monotonic()+15
        while time.monotonic()<deadline:
            if peer()["sessions"]>before: time.sleep(1.5); return
            time.sleep(.2)
        raise TimeoutError("No authenticated synthetic session")
    def leave():
        adb("shell", "input", "keyevent", "KEYCODE_BACK"); tap("断开"); time.sleep(2.2)
    def choose_fixture_if_needed():
        tree = ui()
        if any(n.get("text") == "选择设备 / 端口" for n in nodes(tree)):
            tap_node(find(lambda n: n.get("text", "").startswith("Discovery test PC ·") and endpoint in n.get("text", "")))
    def history(desc_port=port):
        return f"连接 Discovery-Node，直连 10.0.2.2:{desc_port}"
    def discovered():
        return f"发现设备 Discovery test PC，{endpoint}"

    start()
    check("Overwrite update preserves earlier history", any(n.get("content-desc") == "管理连接 Upgrade-Node" for n in nodes(ui())))
    if args.phase == "create":
        replace(field("远端地址"), "10.0.2.2")
        replace(field("连接口令"), "RemoteDesk-synthetic-ui-fixture")
        before = peer()["sessions"]; tap("控制远端", scroll=True); time.sleep(2.2)
        check("Port discovery does not authenticate before choosing the advertised endpoint", peer()["sessions"] == before)
        choose_fixture_if_needed(); connected(before); leave()
        check("IP-only connect uses the advertised custom port", peer()["sessions"]>before)
        start()
        check("Authenticated discovered endpoint is persisted", field("远端地址").get("text") == endpoint)
        tap(desc="管理连接 Discovery test PC", scroll=True); tap("修改备注")
        edit = find(lambda n:n.get("class") == "android.widget.EditText")
        replace(edit, "Discovery-Node"); tap("保存"); start()
        check("Known device IP and port are rediscovered without typing", any(n.get("content-desc") == discovered() for n in nodes(ui())))
        before = peer()["sessions"]
        replace(field("连接口令"), "Deliberately-wrong-global-password")
        tap(desc=discovered()); connected(before); leave()
        check("Selecting a discovered saved device reuses its own credential", peer()["sessions"]>before)
        start(); before=peer()["sessions"]
        tap(desc=history(), scroll=True); connected(before); leave()
        check("History reconnect preserves the custom port and remark", peer()["sessions"]>before)
        start(); before=peer()["sessions"]
        replace(field("远端地址"), "10.0.2.2"); tap("控制远端", scroll=True)
        adb("shell", "input", "keyevent", "KEYCODE_HOME"); time.sleep(3)
        check("Leaving during discovery does not launch a late connection", peer()["sessions"] == before)
        start()
    else:
        if not args.old_port or args.old_port == port: raise ValueError("Need the previous custom port")
        check("Old endpoint remains saved until a new authenticated connection", field("远端地址").get("text") == f"10.0.2.2:{args.old_port}")
        before = peer()["sessions"]; tap(desc=discovered()); time.sleep(.2)
        check("Changed endpoint requires confirmation before credential reuse", any(n.get("text") == "使用新地址连接？" for n in nodes(ui())) and peer()["sessions"] == before)
        tap("取消"); start()
        check("Canceling keeps old history and sends no authentication", field("远端地址").get("text") == f"10.0.2.2:{args.old_port}" and peer()["sessions"] == before)
        tap(desc=history(args.old_port), scroll=True); time.sleep(2.2); choose_fixture_if_needed()
        check("History lookup also detects the changed port", any(n.get("text") == "使用新地址连接？" for n in nodes(ui())))
        tap("更新地址并连接"); connected(before); leave(); start()
        check("Successful relocation updates the endpoint and preserves the remark", field("远端地址").get("text") == endpoint and any(n.get("content-desc") == history() for n in nodes(ui())))
        check("Relocation updates instead of duplicating history", any(n.get("text") == "最近连接 · 2" for n in nodes(ui())))
        before = peer()["sessions"]; tap(desc=discovered()); connected(before); leave(); start()
        check("Relocated saved device reconnects without another confirmation", peer()["sessions"]>before)
    adb("shell", "screencap", "-p", "/data/local/tmp/remotedesk-discovery-check.png")
    adb("pull", "/data/local/tmp/remotedesk-discovery-check.png", str(output/"ui.png"))
    report["completed"] = True; report["port"] = port
    (output/"report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"completed":True,"checks":len(report["checks"]),"port":port}))


if __name__ == "__main__": main()
