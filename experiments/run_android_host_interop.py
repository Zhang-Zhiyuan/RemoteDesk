#!/usr/bin/env python3
"""Windows/Linux product viewers -> an authorized Android production host.

Requires the test-only InteractionProbeActivity APK and an unlocked phone.
Refuses to replace a saved host password or an already-running screen share.
The normal screen-recording permission dialog must be confirmed on the device.
The temporary secret is memory-only except the app's own encrypted keystore.
Cleanup force-stops only this test phone's app and clears only the owned secret;
OEMs may require restoring accessibility through their normal Settings UI.
"""
import argparse
import json
from pathlib import Path
import re
import secrets
import subprocess
import time
import xml.etree.ElementTree as ET

from run_physical_interop import ROOT, WINDOWS, run


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True); parser.add_argument("--serial", required=True)
    parser.add_argument("--linux", required=True); parser.add_argument("--linux-stage", required=True)
    parser.add_argument("--phone-ip", required=True); parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"/tmp/remotedesk-interop-[A-Za-z0-9]{8}", args.linux_stage):
        raise RuntimeError("Unrecognized owned Linux staging path")
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)
    adb = [args.adb, "-s", args.serial]
    ssh = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", args.linux]
    preferences = "shared_prefs/remotedesk-agent.xml"
    def adb_run(*command, **kw): return run(adb + list(command), **kw)
    def ui():
        adb_run("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-test-ui.xml")
        return ET.fromstring(adb_run("exec-out", "cat", "/data/local/tmp/remotedesk-test-ui.xml"))
    def tap(node):
        b = list(map(int, re.findall(r"\d+", node.get("bounds", ""))))
        if len(b) != 4 or b[2] <= b[0] or b[3] <= b[1]: raise RuntimeError("Target is not visible")
        adb_run("shell", "input", "tap", str((b[0]+b[2])//2), str((b[1]+b[3])//2))
    def host_ui(): return [node for node in ui().iter("node") if node.get("package") == "com.remotedesk.agent"]
    def read_preferences():
        raw = adb_run("exec-out", "run-as", "com.remotedesk.agent", "cat", preferences)
        if raw.startswith(("cat: " + preferences + ":").encode()) and b"No such file or directory" in raw:
            return ET.Element("map")
        return ET.fromstring(raw)
    def target():
        raw = json.loads(adb_run("exec-out", "run-as", "com.remotedesk.codecprobe", "cat", "files/interaction-probe.json"))
        for key in ("button", "editor"):
            b = raw[key + "Bounds"]; raw[key] = [(b[0]+b[2])/2/(raw["screenWidth"]-1), (b[1]+b[3])/2/(raw["screenHeight"]-1)]
        return raw
    report = {"complete": False, "scope": "direct Wi-Fi LAN; production Android host, real Windows/Linux viewer and OS input target", "pairs": {}}
    def save(): (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    baseline = read_preferences()
    if any(n.get("name") in ("password", "password.keystore.v1") and n.text for n in baseline):
        raise RuntimeError("A saved host password exists; refuse to replace it")
    projection = adb_run("shell", "dumpsys", "media_projection").decode()
    if not re.search(r"Media Projection:\s*null", projection): raise RuntimeError("An existing screen share is active")
    enabled = adb_run("shell", "settings", "get", "secure", "enabled_accessibility_services").decode()
    if "com.remotedesk.agent/com.remotedesk.agent.RemoteDeskAccessibilityService" not in enabled:
        raise RuntimeError("Restore RemoteDesk accessibility in normal Settings before testing host input")
    password = secrets.token_urlsafe(24)
    saved_cipher = None
    owned_password = False
    try:
        adb_run("shell", "am", "start", "-W", "-n", "com.remotedesk.agent/.MainActivity")
        # Locate by the host hint, never the viewer's first password field.
        # Activity reuse may retain scroll position from a previous attempt.
        for _ in range(4):
            nodes = host_ui()
            fields = [n for n in nodes if n.get("class") == "android.widget.EditText" and n.get("hint") == "本机被控口令"]
            if fields and any(n.get("text") == "启动被控端" for n in nodes): break
            if not nodes: raise RuntimeError("A system/OEM dialog covers the product; complete its normal UI first")
            y1, y2 = (2780, 1280) if fields else (1280, 2780)
            adb_run("shell", "input", "swipe", "720", str(y1), "720", str(y2), "350")
        if not fields: raise RuntimeError("No visible product password field; inspect test phone")
        field = fields[0]
        if field.get("text", "") not in ("", "本机被控口令"): raise RuntimeError("Nonempty host password UI; refuse to overwrite")
        tap(field); adb_run("shell", "input", "text", password); adb_run("shell", "input", "keyevent", "4")
        nodes = host_ui()
        start = [n for n in nodes if n.get("text") == "启动被控端"]
        if len(start) != 1: raise RuntimeError("Start-host button is not visible")
        owned_password = True; tap(start[0])
        for _ in range(30):
            saved = read_preferences()
            saved_cipher = next((n.text for n in saved if n.get("name") == "password.keystore.v1"), None)
            if saved_cipher: break
            time.sleep(.1)
        if not saved_cipher: raise RuntimeError("Cannot identify the owned encrypted test password for safe cleanup")
        print("PROJECTION_PENDING: confirm only RemoteDesk's normal screen-sharing permission", flush=True)
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            value = adb_run("shell", "dumpsys", "media_projection").decode()
            if "com.remotedesk.agent" in value: break
            time.sleep(.5)
        else: raise RuntimeError("Screen sharing permission was not completed")
        adb_run("shell", "am", "force-stop", "com.remotedesk.codecprobe")
        adb_run("shell", "am", "start", "-W", "-n", "com.remotedesk.codecprobe/com.remotedesk.agent.InteractionProbeActivity")
        time.sleep(1)
        initial = target()
        if initial["clicks"] or initial["text"]: raise RuntimeError("A fresh owned phone target is required")
        for label in ("WindowsToAndroid", "LinuxToAndroid", "WindowsToAndroidReconnect"):
            before = target()
            config = dict(host=args.phone_ip, port=56565, password=password, button=before["button"], editor=before["editor"],
                          text=label+"中文42", android=True, output=str(output / label))
            print("START " + label, flush=True)
            if label.startswith("Windows"):
                process = subprocess.run([str(WINDOWS), "viewer"], input=json.dumps(config).encode()+b"\n", capture_output=True, timeout=55)
                path = output / label / "viewer.json"
                value = json.loads(path.read_text()) if path.exists() else {"complete": False}
                failure = path.parent / "failure.txt"
                if failure.exists(): value["failure"] = failure.read_text()
                value["processExit"] = process.returncode
            else:
                directory = args.linux_stage + "/" + label
                result = run(ssh + [f"python3 -u {args.linux_stage}/experiments/interop_linux_node.py viewer --output {directory}"],
                             data=json.dumps(config).encode()+b"\n", timeout=50, check=False)
                value = json.loads(run(ssh + ["cat " + directory + "/viewer.json"]))
                run(["scp", "-q", "-r", args.linux + ":" + directory, str(output / label)], timeout=30)
            after = target()
            text = config["text"]
            value["inputVerified"] = (after["clicks"] == before["clicks"]+1 and text in after["text"]
                                      and after["text"].replace(text, "", 1) == before["text"])
            value["target"] = after
            value["complete"] = value.get("complete", False) and value["inputVerified"]
            report["pairs"][label] = value; save()
            print("RESULT " + label + " " + json.dumps({k:v for k,v in value.items() if k in ("complete", "inputVerified", "frames", "encoding", "failure")}), flush=True)
        report["complete"] = all(row["complete"] for row in report["pairs"].values())
    finally:
        if owned_password:
            # Force-stop invalidates the in-memory SharedPreferences cache. Never
            # clear app data, uninstall the production app, or erase other prefs.
            adb_run("shell", "am", "force-stop", "com.remotedesk.agent")
            current = read_preferences()
            item = next((n for n in current if n.get("name") == "password.keystore.v1"), None)
            if item is not None and saved_cipher is not None and item.text == saved_cipher:
                current.remove(item)
                adb_run("exec-in", "run-as", "com.remotedesk.agent", "sh", "-c", "cat > " + preferences,
                        data=ET.tostring(current, encoding="utf-8", xml_declaration=True))
                report["temporaryHostPasswordRemoved"] = True
            elif item is None: report["temporaryHostPasswordRemoved"] = True
            else: report["temporaryHostPasswordRemoved"] = False
            adb_run("shell", "am", "start", "-W", "-n", "com.remotedesk.agent/.MainActivity")
            report["projectionStopped"] = "null" in adb_run("shell", "dumpsys", "media_projection").decode()
            enabled = adb_run("shell", "settings", "get", "secure", "enabled_accessibility_services").decode()
            report["accessibilityNeedsNormalSettingsRestore"] = "com.remotedesk.agent" not in enabled
        save(); print("FINAL " + str(output / "result.json"), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__": raise SystemExit(main())
