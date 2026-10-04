#!/usr/bin/env python3
"""Owned AVD viewer -> separately owned Linux/Xvfb host. Never discovers peers."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

PACKAGE = "com.remotedesk.viewerprobe"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--avd", required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--peer-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not args.serial.startswith("emulator-"):
        raise RuntimeError("Owned Android emulator required")
    prefix = [args.adb, "-s", args.serial]

    def adb(*command):
        return subprocess.run(prefix + list(map(str, command)), capture_output=True, check=True,
            timeout=30).stdout.decode("utf-8").strip()

    if args.avd not in adb("emu", "avd", "name").splitlines():
        raise RuntimeError("Wrong owned AVD")
    args.output.mkdir(parents=True, exist_ok=False)
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    endpoint = json.loads((args.peer_directory / "endpoint.json").read_text(encoding="utf-8-sig"))
    report = {"scope": "Actual Android viewer and real Linux host in independently owned Xvfb, no production session",
        "fileSelection": "Private synthetic ContentProvider callback; real SAF is verified separately", "checks": []}

    def save():
        (args.output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def check(name, passed):
        report["checks"].append({"name": name, "passed": bool(passed)})
        save()
        print(("PASS " if passed else "FAIL ") + name, flush=True)
        if not passed:
            raise AssertionError(name)

    def state():
        try:
            return json.loads(adb("shell", "run-as", PACKAGE, "cat", "files/viewer-state.json"))
        except (subprocess.CalledProcessError, ValueError):
            return {}

    def target():
        return json.loads((args.peer_directory / "target.json").read_text(encoding="utf-8-sig"))

    def wait(predicate, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(.15)
        raise TimeoutError("Owned real-peer verification timed out")

    def action(name, *extras):
        adb("shell", "am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver",
            "--es", "action", name, *extras)

    def remote_tap(point):
        action("remote_tap", "--ef", "x", str(point[0]), "--ef", "y", str(point[1]))

    def snapshot(name):
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-linux-owned.xml")
        xml = adb("shell", "cat", "/data/local/tmp/remotedesk-linux-owned.xml")
        (args.output / (name + ".xml")).write_text(xml, encoding="utf-8")
        (args.output / (name + ".png")).write_bytes(subprocess.run(prefix + ["exec-out", "screencap", "-p"],
            capture_output=True, check=True, timeout=30).stdout)
        return ET.fromstring(xml)

    def tap_label(label):
        tree = snapshot("current-ui")
        nodes = [node for node in tree.iter("node") if node.get("package") == PACKAGE and node.get("text") == label]
        if len(nodes) != 1:
            raise RuntimeError("Expected one fixture control: " + label)
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", nodes[0].get("bounds")))
        adb("shell", "input", "tap", (x1+x2)//2, (y1+y2)//2)

    try:
        adb("shell", "am", "force-stop", PACKAGE)
        adb("shell", "run-as", PACKAGE, "rm", "-f", "files/viewer-state.json")
        with tempfile.TemporaryDirectory(prefix="remotedesk-owned-endpoint-") as directory:
            private = Path(directory) / "endpoint.json"
            private.write_text(json.dumps({"host": "10.0.2.2", "port": endpoint["port"],
                "password": config["password"]}), encoding="utf-8")
            adb("push", private, "/data/local/tmp/remotedesk-r3-owned-endpoint.json")
            adb("shell", "run-as", PACKAGE, "cp", "/data/local/tmp/remotedesk-r3-owned-endpoint.json", "files/interop-endpoint.json")
            adb("shell", "rm", "-f", "/data/local/tmp/remotedesk-r3-owned-endpoint.json")
        adb("shell", "am", "start", "-W", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeLauncher")
        wait(lambda: state().get("frame", [0, 0])[0] == target()["screenWidth"], 30)
        wait(lambda: state().get("presentedFrames", 0) >= 3)
        check("actual Linux screen frames decoded and presented by Android viewer", True)
        snapshot("linux-screen")
        before = target()
        remote_tap(before["button"])
        wait(lambda: target()["clicks"] == before["clicks"] + 1)
        check("Android touch reaches the isolated Linux test button", True)
        remote_tap(before["editor"])
        action("keyboard")
        action("send_text", "--es", "text", "-android-r3")
        wait(lambda: "-android-r3" in target()["text"])
        check("Android IME send reaches the isolated Linux editor", True)
        action("pause_input")
        wait(lambda: state().get("localInputPaused", False))
        clicks = target()["clicks"]
        remote_tap(before["button"])
        time.sleep(.6)
        check("local watch-only blocks Linux button input", target()["clicks"] == clicks)
        receive = args.peer_directory / "receive"
        existing = set(receive.iterdir())
        action("select_files", "--es", "indices", "0,1")
        wait(lambda: state().get("fileBusy", False))
        tree = snapshot("batch-confirmation")
        text = "\n".join(node.get("text", "") for node in tree.iter("node"))
        check("real Linux destination and both names shown before confirmation",
            "确认发送 2 个文件" in text and "合成测试-0.bin" in text and "合成测试-1.bin" in text
            and "位置未确认" not in text and set(receive.iterdir()) == existing)
        tap_label("发送")
        wait(lambda: not state().get("fileBusy", True), 40)
        saved = [path for path in receive.iterdir() if path not in existing and path.is_file()]
        actual = sorted(hashlib.sha256(path.read_bytes()).hexdigest() for path in saved)
        expected = sorted(hashlib.sha256(bytes([index+1]) * ((index+1)*1024)).hexdigest() for index in range(2))
        check("real Linux receiver saves both exact Android document payloads", actual == expected)
        check("Android shows two Linux save receipts while remaining watch-only",
            "已确认保存 2" in state().get("fileBatch", "") and state().get("localInputPaused", False))
        snapshot("batch-results")
        tap_label("知道了")
        action("pause_input")
        wait(lambda: not state().get("localInputPaused", True))
        remote_tap(before["button"])
        wait(lambda: target()["clicks"] == clicks + 1)
        check("explicit local resume restores Linux touch input", True)
        check("no Android probe callback failure", not state().get("failure"))
        report["files"] = [{"name": path.name, "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in saved]
        report["passed"] = True
    except Exception as failure:
        report["failure"] = str(failure)
        report["state"] = state()
        raise
    finally:
        save()
        adb("shell", "am", "force-stop", PACKAGE)
        adb("shell", "run-as", PACKAGE, "rm", "-f", "files/interop-endpoint.json")
        adb("shell", "rm", "-f", "/data/local/tmp/remotedesk-r3-owned-endpoint.json")


if __name__ == "__main__":
    main()
