#!/usr/bin/env python3
"""Inspect product pages on an explicitly selected device; no remote input.

Only navigates RemoteDesk, scrolls its page and opens/cancels its new-device
dialog. No keys, server login, OS permissions, Wi-Fi or capture are changed.
"""
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
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = dict(device=args.serial, physical=not args.serial.startswith("emulator-"), checks=[])

    def adb(*command):
        return subprocess.run([args.adb, "-s", args.serial, *map(str, command)],
            capture_output=True, check=True, timeout=30).stdout

    def tree():
        focus = adb("shell", "dumpsys", "window").decode("utf-8", "replace")
        if not any("mCurrentFocus=" in line and PACKAGE + "/" in line for line in focus.splitlines()):
            raise RuntimeError("Another window owns focus; refusing input through an overlay")
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-main-ui-audit.xml")
        xml = adb("exec-out", "cat", "/data/local/tmp/remotedesk-main-ui-audit.xml")
        nodes = [node for node in ET.fromstring(xml).iter("node") if node.get("package") == PACKAGE]
        if not nodes:
            raise RuntimeError("RemoteDesk is not visible; refusing unscoped input")
        return nodes

    def bounds(node):
        return tuple(map(int, re.findall(r"-?\d+", node.get("bounds", ""))))

    def find(nodes, text, description=False):
        return [n for n in nodes if n.get("content-desc" if description else "text") == text]

    def tap(nodes, text, description=False):
        # Discovery/status updates can move rows between dump and click. Locate
        # again just before input instead of tapping the previous page's bounds.
        nodes = tree()
        found = find(nodes, text, description)
        found = [n for n in found if n.get("class") == "android.widget.Button"]
        if len(found) != 1:
            raise AssertionError(f"Expected one visible control: {text}")
        x1, y1, x2, y2 = bounds(found[0])
        if x2 - x1 < 20 or y2 - y1 < 20:
            raise AssertionError("Truncated touch target: " + text)
        adb("shell", "input", "tap", (x1 + x2) // 2, (y1 + y2) // 2)
        time.sleep(.25)

    def capture(name, nodes):
        # Password contents and other apps' XML are never saved in the report.
        controls = [dict(text=n.get("text"), bounds=bounds(n)) for n in nodes
            if n.get("class") == "android.widget.Button" and n.get("password") != "true"]
        (output / (name + ".png")).write_bytes(adb("exec-out", "screencap", "-p"))
        report["checks"].append(dict(name=name, controls=controls, passed=True))

    adb("shell", "am", "start", "-W", "-n", PACKAGE + "/.MainActivity")
    initial = tree()
    selected = next((n.get("content-desc") for n in initial if n.get("selected") == "true"
        and n.get("content-desc") in ("设备", "本机被控", "设置")), "设备")
    try:
        for page, name in (("设备", "devices"), ("本机被控", "host"), ("设置", "settings")):
            tap(tree(), page, True)
            nodes = tree()
            if not any(n.get("selected") == "true" for n in find(nodes, page, True)):
                raise AssertionError("Navigation did not select " + page)
            capture(name, nodes)
        tap(tree(), "设备", True)
        nodes = tree()
        for _ in range(10):
            if find(nodes, "新增设备"):
                break
            scroll = next(n for n in nodes if n.get("class") == "android.widget.ScrollView")
            x1, y1, x2, y2 = bounds(scroll)
            adb("shell", "input", "swipe", (x1+x2)//2, y2-30, (x1+x2)//2, y1+30, 300)
            nodes = tree()
        tap(nodes, "新增设备")
        nodes = tree()
        if not find(nodes, "保存设备"):
            if find(nodes, "取消"):
                tap(nodes, "取消")
            raise AssertionError("New-device dialog did not open; no result certified")
        capture("new-device-dialog", nodes)
        tap(nodes, "取消")
        nodes = tree()
        for _ in range(16):
            if find(nodes, "展开高级设置"):
                break
            scroll = next(n for n in nodes if n.get("class") == "android.widget.ScrollView")
            x1, y1, x2, y2 = bounds(scroll)
            adb("shell", "input", "swipe", (x1+x2)//2, y2-30, (x1+x2)//2, y1+30, 300)
            nodes = tree()
        if find(nodes, "展开高级设置"):
            capture("relay-login", nodes)
            tap(nodes, "展开高级设置")
            nodes = tree()
            if not find(nodes, "收起高级设置"):
                raise AssertionError("Advanced button did not describe the expanded state")
            capture("relay-advanced", nodes)
            tap(nodes, "收起高级设置")
        else:
            report["advancedSkipped"] = "Saved login hides configuration; login unchanged"
        report["complete"] = True
    finally:
        try:
            tap(tree(), selected, True)
        finally:
            (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))


if __name__ == "__main__":
    main()
