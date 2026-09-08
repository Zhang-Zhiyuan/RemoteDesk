#!/usr/bin/env python3
"""New-device and authenticated identity merge checks in a disposable emulator profile."""
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
    for name in ("adb", "serial", "peer-a", "peer-b", "output"): parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"emulator-\d+", args.serial): raise ValueError("Synthetic credentials and process restarts: emulator only")
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)
    report = dict(scope="Signed production APK UI; encrypted synthetic peers, no OS input", checks=[])
    def peer(path): return json.loads(Path(path).read_text(encoding="utf-8"))
    port_a, port_b = peer(args.peer_a)["port"], peer(args.peer_b)["port"]
    initial_sessions_a = peer(args.peer_a)["sessions"]
    def adb(*command):
        return subprocess.run([args.adb, "-s", args.serial, *map(str, command)], capture_output=True,
                              check=True, timeout=25).stdout.decode("utf-8").strip()
    def ui():
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-directory-ui.xml")
        return ET.fromstring(adb("exec-out", "cat", "/data/local/tmp/remotedesk-directory-ui.xml"))
    def nodes(): return [n for n in ui().iter("node") if n.get("package") == PACKAGE]
    def bounds(n): return list(map(int, re.findall(r"\d+", n.get("bounds", ""))))
    def visible(n):
        b = bounds(n); return len(b) == 4 and b[2] > b[0] and b[3] > b[1]
    def click(n):
        b = bounds(n)
        if not visible(n): raise ValueError("Control outside viewport")
        adb("shell", "input", "tap", (b[0]+b[2])//2, (b[1]+b[3])//2)
    def tap(text=None, desc=None, scroll=False):
        width, height = map(int, re.search(r"(\d+)x(\d+)", adb("shell", "wm", "size")).groups())
        for attempt in range(7 if scroll else 1):
            found = [n for n in nodes() if visible(n) and n.get("enabled") == "true" and
                     (n.get("text") == text if text is not None else n.get("content-desc") == desc)]
            if len(found) == 1:
                b = bounds(found[0])
                if scroll and b[1] < height*.06:
                    adb("shell", "input", "swipe", width//2, int(height*.35), width//2, int(height*.57), 250)
                    continue
                click(found[0]); time.sleep(.2); return
            if len(found) > 1: raise AssertionError("Ambiguous control")
            if scroll:
                width, height = map(int, re.search(r"(\d+)x(\d+)", adb("shell", "wm", "size")).groups())
                adb("shell", "input", "swipe", width//2, int(height*.78), width//2, int(height*.3), 250)
        raise AssertionError("Missing control: " + str(text or desc))
    def start():
        adb("shell", "am", "force-stop", PACKAGE)
        adb("shell", "am", "start", "-W", "-n", PACKAGE+"/.MainActivity"); time.sleep(2.5)
    def check(name, success):
        report["checks"].append(dict(name=name, passed=bool(success)))
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not success: raise AssertionError(name)
    def add(port, note):
        start(); tap("新增设备", scroll=True)
        for description, value in (("IP / 主机名", "10.0.2.2"), ("端口（留空自动探测）", str(port)),
                                   ("连接口令", "RemoteDesk-synthetic-ui-fixture"), ("备注（可选）", note)):
            if not value: continue
            field = next(n for n in nodes() if n.get("class") == "android.widget.EditText" and n.get("content-desc") == description)
            if description == "连接口令": check("New-device password is masked", field.get("password") == "true")
            click(field); adb("shell", "input", "text", value)
        adb("shell", "input", "keyevent", "KEYCODE_BACK")
        tap("保存设备"); time.sleep(.5); start()
    def count():
        for n in nodes():
            text = n.get("text", "")
            if text == "最近连接": return 0
            if text.startswith("最近连接 · "): return int(text.rsplit(" ", 1)[1])
        raise AssertionError("History section missing")
    def connect(title, port, path):
        before = peer(path)["sessions"]
        tap(desc=f"连接 {title}，直连 10.0.2.2:{port}", scroll=True)
        deadline = time.monotonic() + 12
        while peer(path)["sessions"] <= before and time.monotonic() < deadline: time.sleep(.2)
        check("Saved explicit port connects without being replaced by discovery", peer(path)["sessions"] > before)
        time.sleep(1)
        check("Identity requested after encrypted authentication", any(c["kind"] == 33 for c in peer(path)["controls"]))
        adb("shell", "input", "keyevent", "KEYCODE_BACK"); tap("断开"); time.sleep(.5); start()

    start(); check("Explicit add-device button exists", any(n.get("text") == "新增设备" for n in nodes()))
    add(port_a, "Directory-Note"); check("Manual add persists without connecting", count() == 1 and peer(args.peer_a)["sessions"] == initial_sessions_a)
    add(port_a, ""); check("Repeated manual endpoint is merged", count() == 1)
    connect("Directory-Note", port_a, args.peer_a)
    add(port_b, ""); check("Unknown identity is not merged just because the IP matches", count() == 2)
    connect(f"10.0.2.2:{port_b}", port_b, args.peer_b)
    check("Same authenticated machine at another port merges automatically", count() == 1)
    check("Merged record keeps its remark and latest endpoint", any(n.get("content-desc") == f"连接 Directory-Note，直连 10.0.2.2:{port_b}" for n in nodes()))
    tap(desc="管理连接 Directory-Note", scroll=True); tap("删除记录"); tap("取消")
    check("Canceling deletion keeps the node", count() == 1)
    tap(desc="管理连接 Directory-Note", scroll=True); tap("删除记录"); tap("删除"); start()
    check("Deleting the merged record persists after restart", count() == 0)
    report["completed"] = True
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(completed=True, checks=len(report["checks"]))))


if __name__ == "__main__": main()
