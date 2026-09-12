#!/usr/bin/env python3
"""Inspect actual Surface geometry while only controlling the synthetic viewer.

Requires the separate ViewerProbe APK, connected via ADB reverse to the owned
loopback fixture. No production preferences, permissions, or remote OS input.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time

PACKAGE = "com.remotedesk.viewerprobe"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--expect-native", action="store_true")
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    report = {"scope": ("emulator" if args.serial.startswith("emulator-") else "physical Android")
              + ": actual viewer Surface, synthetic encrypted loopback desktop",
              "expectNativeBuffer": args.expect_native, "phases": []}

    def adb(*command):
        result = subprocess.run([args.adb, "-s", args.serial, *command],
                                capture_output=True, check=True, timeout=25)
        return result.stdout.decode("utf-8").strip()

    def state():
        result = json.loads(adb("shell", "run-as", PACKAGE, "cat", "files/viewer-state.json"))
        if result.get("failure"):
            raise AssertionError(result["failure"])
        return result

    def action(name):
        adb("shell", "am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver",
            "--es", "action", name)

    def phase(name, predicate):
        deadline = time.monotonic() + 15
        matched = None
        geometry = None
        while time.monotonic() < deadline:
            current = state()
            good = current["h264"] and current["geometryReady"] and predicate(current)
            signature = tuple(str(current[key]) for key in
                              ("viewport", "imeInset", "frame", "surfaceFrame", "surfaceView", "scale", "zoom"))
            if signature != geometry:
                geometry = signature
                matched = None
            if good:
                # Wait for layout / Surface callbacks to settle, not only the
                # first UI-thread snapshot immediately following an action.
                if matched is not None and time.monotonic() - matched >= 1:
                    if args.expect_native and current["surfaceFrame"] != current["frame"]:
                        raise AssertionError("Surface buffer is not source-sized: " + str(current))
                    report["phases"].append({"name": name, "state": current})
                    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                    print(name + ": " + json.dumps({key: current[key] for key in
                          ("frame", "surfaceFrame", "surfaceView", "scale", "surfaceChanges")}), flush=True)
                    return current
                if matched is None:
                    matched = time.monotonic()
            else:
                matched = None
            time.sleep(.2)
        raise TimeoutError(name + ": " + str(current))

    initial = phase("initial_fit", lambda s: not s["keyboard"])
    action("original_size")
    phase("original_pixels", lambda s: abs(s["scale"] - 1) < .001)
    action("keyboard")
    phase("original_with_keyboard", lambda s: s["keyboard"] and s["imeInset"] > 0 and abs(s["scale"] - 1) < .001)
    action("fit_size")
    phase("fit_with_keyboard", lambda s: s["keyboard"] and s["zoom"] == 1)
    action("keyboard")
    phase("fit_keyboard_closed", lambda s: not s["keyboard"] and s["imeInset"] == 0)
    action("landscape")
    landscape = phase("landscape_fit", lambda s: s["viewport"][2] - s["viewport"][0] > s["viewport"][3] - s["viewport"][1]
                      and s["zoom"] == 1 and s["imeInset"] == 0)
    action("keyboard")
    typing = phase("landscape_fit_keyboard_readable", lambda s: s["keyboard"] and s["imeInset"] > 0
                   and abs(s["scale"] - landscape["scale"]) < .002)
    (output / "landscape-keyboard.png").write_bytes(subprocess.run(
        [args.adb, "-s", args.serial, "exec-out", "screencap", "-p"],
        capture_output=True, check=True, timeout=25).stdout)
    if not (typing["composerImeOptions"] & 0x02000000):
        raise AssertionError("Remote composer must request non-fullscreen IME")
    action("keyboard")
    phase("landscape_fit_restored", lambda s: not s["keyboard"] and s["imeInset"] == 0
          and s["zoom"] == 1 and abs(s["scale"] - min(
              1, (s["viewport"][2] - s["viewport"][0]) / s["frame"][0],
              (s["viewport"][3] - s["viewport"][1]) / s["frame"][1])) < .002)
    action("original_size")
    phase("landscape_original_pixels", lambda s: abs(s["scale"] - 1) < .001)
    action("portrait")
    final = phase("portrait_original_pixels", lambda s: s["viewport"][2] - s["viewport"][0] < s["viewport"][3] - s["viewport"][1]
                  and abs(s["scale"] - 1) < .001)
    if final["ownerGeneration"] != initial["ownerGeneration"]:
        raise AssertionError("Window-only transforms reconnected the transport")
    report["surfaceChangesDuringTest"] = final["surfaceChanges"] - initial["surfaceChanges"]
    report["completed"] = True
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
