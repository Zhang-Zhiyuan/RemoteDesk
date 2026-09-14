#!/usr/bin/env python3
"""Physical-phone UI regression against the separately installed synthetic viewer probe.

Never launches production RemoteDesk or controls a real remote desktop. The caller
must start android_viewer_fixture_server.py and reverse the probe's port first.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from android_viewer_fixture_server import save_snapshot

PACKAGE = "com.remotedesk.viewerprobe"


def toolbar_swipe(nodes, label):
    """Return a bounded gesture on this probe's overflow strip, never its desktop."""
    labels = {"更多", "键盘", "鼠标", "屏幕", "缩放", "触控板", "直接触摸", "新版放大"}
    if label not in labels:
        return None
    strips = [n for n in nodes if n.get("package") == PACKAGE and
              n.get("class") == "android.widget.HorizontalScrollView" and
              n.get("content-desc") == "远程操作栏，左右滑动可查看所有按钮"]
    if len(strips) != 1:
        return None
    bounds = re.fullmatch(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", strips[0].get("bounds", ""))
    if bounds is None:
        return None
    left, top, right, bottom = map(int, bounds.groups())
    if right - left < 32 or bottom <= top:
        return None
    inset = max(8, (right - left) // 10)
    start, end = (right-inset, left+inset) if label in {"更多", "新版放大"} else (left+inset, right-inset)
    return start, (top+bottom)//2, end, (top+bottom)//2


class StableLayout:
    """Wait out actual IME/layout animation without relaxing any UI assertion."""
    def __init__(self, seconds):
        self.seconds = seconds
        self.signature = None
        self.since = None

    def ready(self, value, now):
        signature = tuple(tuple(value.get(key, ())) for key in
                          ("frame", "viewport", "dock", "keyboardPanel")) + (
                              value.get("keyboard"), value.get("imeInset"),
                              value.get("scale"), value.get("ownerGeneration"))
        if signature != self.signature:
            self.signature = signature
            self.since = now
        return now - self.since >= self.seconds


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True)
    parser.add_argument("--serial", required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--suite", choices=("standard","edges","all","fullscreen"), default="standard",
                        help="fullscreen resumes the final checks with the probe already in fullscreen")
    parser.add_argument("--record-failures", action="store_true", help="Collect assertion failures for a before-fix baseline; still stop on UI/transport errors")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    device_kind = "Android emulator" if args.serial.startswith("emulator-") else "physical Android device"
    report = {"scope": device_kind + " UI, actual viewer code, encrypted synthetic TCP peer; no remote OS input", "checks": []}

    def adb(*command, binary=False):
        value = subprocess.run([args.adb, "-s", args.serial, *map(str, command)], capture_output=True, timeout=25, check=True)
        return value.stdout if binary else value.stdout.decode("utf-8").strip()

    def state():
        return json.loads(adb("shell", "run-as", PACKAGE, "cat", "files/viewer-state.json"))

    def server():
        return json.loads(Path(args.server).read_text(encoding="utf-8"))

    def save():
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def check(name, condition, detail=None):
        report["checks"].append({"name": name, "passed": bool(condition), "detail": detail})
        save()
        print(("PASS " if condition else "FAIL ") + name, flush=True)
        if not condition and not args.record_failures:
            raise AssertionError(name + ": " + str(detail))

    def wait(predicate, seconds=12, stable_seconds=0):
        end = time.monotonic() + seconds
        stable = StableLayout(stable_seconds)
        while time.monotonic() < end:
            data = state()
            if data.get("failure"):
                raise RuntimeError(data["failure"])
            if predicate(data) and stable.ready(data, time.monotonic()):
                return data
            time.sleep(.3)
        raise TimeoutError(str(data))

    def action(name):
        adb("shell", "am", "broadcast", "-n", PACKAGE + "/com.remotedesk.agent.ViewerProbeReceiver", "--es", "action", name)
        time.sleep(.6)

    def command(name):
        if not server().get("testControls"):
            raise RuntimeError("Start the synthetic peer with --allow-test-controls")
        command_file=Path(args.server).resolve().parent / "fixture-command.json"
        sequence=time.time_ns()
        # The Windows peer can be reading the previous command during replace.
        # Reuse the bounded atomic-snapshot retry, never truncate its live file.
        save_snapshot(command_file, {"sequence":sequence,"action":name})
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            if any(c["sequence"]==sequence for c in server()["commands"]):
                return
            time.sleep(.05)
        raise TimeoutError("Peer did not acknowledge " + name)

    def fit():
        tap("更多"); tap("画面缩放", prefix=True); tap("适应窗口 · 重置位置")

    def assert_finished():
        report["completed"]=True
        if not all(c["passed"] for c in report["checks"]):
            raise AssertionError("One or more recorded checks failed; see report.json")

    def ui():
        adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-test-ui.xml")
        data = ET.fromstring(adb("shell", "cat", "/data/local/tmp/remotedesk-test-ui.xml"))
        # Fresh devices can show an OS tutorial on first immersive entry.
        # Acknowledge only that identified tutorial, never a generic OK or a
        # permission dialog, and only while this test activity owns app focus.
        nodes = list(data.iter("node"))
        intro = [n for n in nodes if n.get("package") in ("android", "com.android.systemui") and
                 n.get("resource-id") == n.get("package") + ":id/immersive_cling_title"]
        intro_package = intro[0].get("package") if len(intro) == 1 else None
        okay = [n for n in nodes if intro_package and n.get("package") == intro_package and
                n.get("resource-id") == intro_package + ":id/ok" and n.get("enabled") == "true"]
        if len(intro) == 1 and len(okay) == 1:
            windows = adb("shell", "dumpsys", "window", "windows")
            focused = next((line for line in windows.splitlines() if "mFocusedApp=" in line), "")
            if not focused:
                displays = adb("shell", "dumpsys", "window", "displays")
                focused = next((line for line in displays.splitlines() if "mFocusedApp=" in line), "")
            if PACKAGE not in focused or "ImmersiveModeConfirmation" not in windows:
                raise RuntimeError("Fullscreen tutorial does not belong to the focused probe")
            bounds = list(map(int, re.findall(r"\d+", okay[0].get("bounds", ""))))
            if len(bounds) != 4 or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
                raise RuntimeError("Fullscreen tutorial button has no visible bounds")
            adb("shell", "input", "tap", (bounds[0]+bounds[2])//2, (bounds[1]+bounds[3])//2)
            report["systemFullscreenTutorialAcknowledged"] = True
            time.sleep(.4)
            adb("shell", "uiautomator", "dump", "/data/local/tmp/remotedesk-test-ui.xml")
            data = ET.fromstring(adb("shell", "cat", "/data/local/tmp/remotedesk-test-ui.xml"))
        if not any(n.get("package") == PACKAGE for n in data.iter("node")):
            raise RuntimeError("Probe UI not foreground; refusing to tap another app")
        return [n for n in data.iter("node") if n.get("package") == PACKAGE]

    def tap(text, prefix=False):
        def find(nodes):
            return [n for n in nodes if (n.get("text", "").startswith(text) if prefix else n.get("text") == text)]
        nodes = ui()
        matches = find(nodes)
        # The product toolbar intentionally overflows on narrow/high-density
        # phones. Reveal only its allowlisted controls, never swipe the desktop
        # or another application's view to make a missing-control check pass.
        if not matches:
            for _ in range(3):
                gesture = toolbar_swipe(nodes, text)
                if gesture is None:
                    break
                adb("shell", "input", "swipe", *gesture, 300)
                time.sleep(.3)
                nodes = ui()
                matches = find(nodes)
                if matches:
                    break
        if len(matches) != 1:
            raise RuntimeError("Expected unique visible probe control: " + text)
        bounds = list(map(int, re.findall(r"\d+", matches[0].get("bounds"))))
        adb("shell", "input", "tap", (bounds[0]+bounds[2])//2, (bounds[1]+bounds[3])//2)
        time.sleep(.4)

    def photo(name):
        (output / (name + ".png")).write_bytes(adb("exec-out", "screencap", "-p", binary=True))
        (output / (name + ".json")).write_text(json.dumps(state(), ensure_ascii=False, indent=2), encoding="utf-8")

    def events_after(index):
        time.sleep(.4)
        return server()["events"][index:]

    def swipe():
        x1,y1,x2,y2 = state()["viewport"]
        adb("shell", "input", "swipe", (x1+x2)//2-100, (y1+y2)//2, (x1+x2)//2+180, (y1+y2)//2+10, 300)

    def fullscreen_checks(session, already_fullscreen=False):
        if not already_fullscreen:
            tap("更多"); tap("全屏显示")
        check("Fullscreen retains toolbar restore control", any(n.get("text") == "工具栏" for n in ui()))
        photo("fullscreen")
        tap("工具栏")
        adb("shell", "input", "keyevent", 4)
        check("Back asks before disconnecting", any(n.get("text") == "断开远程连接？" for n in ui()))
        tap("继续控制")
        check("UI operations preserve the TCP session", server()["sessions"] == session, {"start":session,"end":server()["sessions"]})
        action("portrait"); photo("final-portrait")
        tap("键盘"); keyboard = wait(lambda s: s["keyboard"] and s["imeInset"] > 0, stable_seconds=.6)
        check("Portrait keyboard preserves a visible desktop", keyboard["viewport"][3]-keyboard["viewport"][1] >= 64*keyboard["density"], keyboard["viewport"])
        photo("keyboard-portrait"); tap("收起")

    try:
        if args.suite == "fullscreen":
            fullscreen_checks(server()["sessions"], already_fullscreen=True)
            assert_finished(); return
        action("portrait")
        first = wait(lambda s: s["h264"] and "FPS 0.0" not in s["health"] and s["viewport"][2] < s["viewport"][3])
        if args.suite in ("edges","all"):
            fit()
            if not state()["trackpad"]: action("mode")
            start=len(server()["events"]); action("drag")
            wait(lambda s:s["lockedDrag"])
            command("info_repeat"); time.sleep(.8)
            check("Repeated DeviceInfo does not cancel a held drag",state()["lockedDrag"],events_after(start))
            action("cancel")
            check("Canceled drag has one press and one release",[e[:2] for e in events_after(start)]==[[2,1],[3,1]])

            action("mode"); start=len(server()["events"]); action("letterbox_pinch")
            check("Direct-mode pinch can start in letterbox",state()["zoom"]>1.5 and not events_after(start),state()["zoom"])
            fit(); action("mode")

            action("drag"); command("readonly")
            readonly=wait(lambda s:not s["keyboardEnabled"] and not s["mouseEnabled"])
            check("Read-only transition clears held drag",not readonly["lockedDrag"],readonly["health"])
            start=len(server()["events"]); swipe(); action("pinch")
            check("Read-only touch stays local",not events_after(start) and state()["zoom"]>1.5)
            command("control"); wait(lambda s:s["keyboardEnabled"])
            check("Regrant never resumes an old drag",not state()["lockedDrag"])
            fit()

            action("drag"); start=len(server()["events"]); command("hold_screen")
            pending=wait(lambda s:not s["geometryReady"] and s["frame"]==[0,0])
            check("Pending screen hides the previous Surface",not pending["h264"] and pending["surfaceAlpha"]==0,pending)
            release=events_after(start)
            check("Screen transition releases held mouse",[e[:2] for e in release]==[[3,1]],release)
            start=len(server()["events"]); swipe(); action("pinch")
            check("Pending screen blocks new input",not events_after(start))
            command("resume"); wait(lambda s:s["geometryReady"] and s["h264"])

            action("drag"); old_owner=state()["ownerGeneration"]
            command("drop"); disconnected=wait(lambda s:s["ownerGeneration"]==0)
            check("Disconnect clears drag and disables input",not disconnected["lockedDrag"] and not disconnected["keyboardEnabled"],disconnected)
            renewed=wait(lambda s:s["ownerGeneration"]>old_owner and s["geometryReady"] and s["h264"],20)
            check("Connection recovers with a fresh input owner",not renewed["lockedDrag"] and renewed["keyboardEnabled"],renewed["ownerGeneration"])
            offset=server()["sessionStarts"][-1]["eventOffset"]
            check("Old input is not replayed after reconnect",not server()["events"][offset:])
            start=len(server()["events"]); swipe()
            check("New connection accepts fresh pointer movement",bool(events_after(start)))
            photo("edges-recovered")
            if args.suite=="edges":
                assert_finished(); return
            fit(); first=state()
        check("JPEG warm-up transitions to presented H264", first["frame"] == [1920,1080], first["health"])
        photo("portrait")
        session = server()["sessions"]

        start = len(server()["events"]); swipe(); moved = events_after(start)
        check("Trackpad swipe moves without mouse-down", len(moved) > 0 and all(e[0] == 1 for e in moved), moved)
        start = len(server()["events"])
        x1,y1,x2,y2 = state()["viewport"]
        adb("shell", "input", "tap", (x1+x2)//2, (y1+y2)//2)
        clicked = events_after(start)
        check("Trackpad tap sends balanced left click", [e[:2] for e in clicked] == [[2,1],[3,1]], clicked)
        start = len(server()["events"]); action("scroll"); scrolled = events_after(start)
        check("Two-finger scroll sends wheel only", len(scrolled) > 0 and all(e[0] == 4 for e in scrolled), scrolled)
        start = len(server()["events"]); action("pinch"); pinched = state()
        check("Pinch zoom stays local", pinched["zoom"] > 1.5 and not events_after(start), pinched["zoom"])

        tap("鼠标"); start = len(server()["events"]); tap("右键"); clicked = events_after(start)
        check("Explicit right button is balanced", [e[:2] for e in clicked] == [[2,2],[3,2]], clicked)
        start = len(server()["events"]); tap("拖动锁定"); swipe()
        check("Locked drag survives finger lift", state()["lockedDrag"], events_after(start))
        tap("触控板"); released = events_after(start)
        check("Mode change releases locked drag", not state()["lockedDrag"] and not state()["trackpad"] and released[-1][:2] == [3,1], released)
        start = len(server()["events"]); swipe(); dragged = events_after(start)
        check("Direct-touch swipe sends one balanced drag", sum(e[0] == 2 for e in dragged) == 1 and sum(e[0] == 3 for e in dragged) == 1, dragged)
        tap("直接触摸"); tap("鼠标")

        tap("更多"); tap("屏幕方向"); tap("横屏")
        # Visibility changes precede the next Android measure/layout pass. A
        # snapshot can already say GONE while still reporting portrait bounds.
        landscape = wait(lambda s: s["viewport"][2] > s["viewport"][3] and s["healthVisible"] == 8 and
                         s["dock"][3]-s["dock"][1] < first["dock"][3]-first["dock"][1])
        check("Landscape compact chrome remeasures", landscape["dock"][3]-landscape["dock"][1] < first["dock"][3]-first["dock"][1], landscape["dock"])
        photo("landscape")
        tap("更多"); tap("画面缩放", prefix=True); tap("原始像素 1:1")
        check("Original size means one source pixel per physical pixel", abs(state()["scale"]-1) < .01, state()["scale"])

        tap("键盘"); keyboard = wait(lambda s: s["keyboard"] and s["imeInset"] > 0, stable_seconds=.6)
        check("Native IME leaves composer above keyboard", keyboard["keyboardPanel"][3] <= keyboard["dock"][3] and keyboard["keyboardPanel"][3] > keyboard["keyboardPanel"][1], keyboard)
        check("Landscape keyboard preserves a visible desktop", keyboard["viewport"][3]-keyboard["viewport"][1] >= 32*keyboard["density"], keyboard["viewport"])
        check("Keyboard resize keeps original pixels readable", abs(keyboard["scale"]-1) < .01, keyboard["scale"])
        check("Empty composer disables Send",keyboard["draftLength"]==0 and not keyboard["sendEnabled"])
        start = len(server()["events"]); action("compose")
        check("Chinese IME composition is not sent prematurely", not events_after(start))
        check("Composed text enables Send",state()["sendEnabled"])
        photo("keyboard-landscape")
        tap("发送"); committed = events_after(start)
        check("Chinese and emoji are committed exactly once", [e[4] for e in committed if e[0] == 7] == [ord(c) for c in "中文测试😀"], committed)
        check("Successful Send clears draft and disables button",state()["draftLength"]==0 and not state()["sendEnabled"])
        tap("按键"); start = len(server()["events"]); tap("Ctrl+A"); shortcut = events_after(start)
        check("Shortcut modifier order is balanced", [(e[0],e[4]) for e in shortcut] == [(5,17),(5,65),(6,65),(6,17)], shortcut)
        tap("收起"); wait(lambda s: not s["keyboard"] and s["imeInset"] == 0)

        tap("更多"); tap("画质：", prefix=True); tap("文字清晰 · JPEG（带宽更高）")
        jpeg = wait(lambda s: not s["h264"] and "JPEG" in s["health"])
        check("H264 to JPEG switch presents", jpeg["frame"] == [1920,1080], jpeg["health"])
        tap("屏幕"); tap("测试屏幕 2 · 720P")
        secondary = wait(lambda s: s["frame"] == [1280,720])
        check("Screen switch updates coordinate dimensions", secondary["frame"] == [1280,720], secondary["frame"])
        photo("screen-two-jpeg")
        tap("更多"); tap("画质：", prefix=True); tap("流畅 · H.264 硬件优先")
        h264 = wait(lambda s: s["h264"] and "H.264" in s["health"] and "FPS 0.0" not in s["health"])
        check("JPEG to H264 recreates usable Surface", h264["frame"] == [1920,1080], h264["health"])
        tap("屏幕"); tap("测试屏幕 1 · 1080P")
        time.sleep(2)
        h264 = wait(lambda s: s["h264"] and "已启用" in s["status"])
        check("Same-size H264 screen change presents again", h264["frame"] == [1920,1080], h264["health"])

        fullscreen_checks(session)
        assert_finished()
    finally:
        report["server"] = server()
        report["lastState"] = state()
        save()


if __name__ == "__main__":
    main()
