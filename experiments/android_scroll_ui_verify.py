#!/usr/bin/env python3
"""Multi-pointer MotionEvent regression, including Android-batched reversals.

Uses the isolated viewer probe and encrypted synthetic peer, never injects OS input.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"scope": "Actual viewer MotionEvents against synthetic encrypted peer; not human touch-feel or remote OS scrolling",
              "device": "emulator" if args.serial.startswith("emulator-") else "physical phone", "checks": []}
    package = "com.remotedesk.viewerprobe"

    def adb(*command):
        return subprocess.run([args.adb, "-s", args.serial, *map(str, command)], check=True,
                              capture_output=True, timeout=25).stdout.decode("utf-8").strip()

    def state():
        return json.loads(adb("shell", "run-as", package, "cat", "files/viewer-state.json"))

    def peer():
        return json.loads(Path(args.server).read_text(encoding="utf-8"))

    def check(name, passed, details=None):
        report["checks"].append({"name": name, "passed": bool(passed), "details": details})
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if not passed:
            raise AssertionError(name)

    deadline = time.monotonic()+20
    initial = {}
    while time.monotonic() < deadline:
        try:
            initial = state()
        except (subprocess.CalledProcessError, json.JSONDecodeError):
            # A freshly installed probe writes its first snapshot after onCreate.
            time.sleep(.3)
            continue
        if initial.get("geometryReady") and initial.get("mouseEnabled"):
            break
        time.sleep(.3)
    check("Ready, control-capable 1080p viewer", initial.get("geometryReady") and initial["frame"] == [1920,1080] and initial.get("mouseEnabled"))
    for trackpad in (True, False):
        for name in ("jitter", "horizontal", "reverse_batch", "replace", "pinch_drift"):
            offset = len(peer()["events"])
            adb("shell", "am", "broadcast", "-n", package + "/com.remotedesk.agent.ViewerProbeReceiver",
                "--es", "action", "gesture_case", "--es", "case", name, "--ez", "trackpad", str(trackpad).lower())
            time.sleep(.9)
            current = state()
            events = peer()["events"][offset:]
            wheels = [e for e in events if e[0] == 4]
            prefix = ("Trackpad" if trackpad else "Direct touch") + " / " + name
            check(prefix + " has no click, drag or diagnostic failure", not current.get("failure") and not any(e[0] in (2,3) for e in events))
            check(prefix + " keeps zoom intent stable", abs(current["zoom"] - (2 if name == "pinch_drift" else 1)) < .02)
            if name in ("horizontal", "pinch_drift"):
                check(prefix + " does not scroll", not wheels)
            elif name == "reverse_batch":
                check(prefix + " preserves BOTH directions inside one batched MotionEvent", any(e[4] > 0 for e in wheels) and any(e[4] < 0 for e in wheels), [e[4] for e in wheels])
            else:
                expected = 480 if name == "jitter" else 360
                check(prefix + " has density-independent bounded wheel distance", sum(e[4] for e in wheels) == expected and all(e[4] % 120 == 0 and 0 < e[4] <= 720 for e in wheels), [e[4] for e in wheels])
            if wheels:
                check(prefix + " targets the intended remote area", all(abs(e[2] - 960) <= 1 and abs(e[3] - 540) <= 1 for e in wheels))
    report["completed"] = True
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"completed": True, "checks": len(report["checks"])}))


if __name__ == "__main__":
    main()
