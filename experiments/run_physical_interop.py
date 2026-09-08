#!/usr/bin/env python3
"""Authorized LAN integration test; retains evidence, never a device password.

Uses production host/client code, owned synthetic OS input targets, an isolated
Android viewer APK, and key-authenticated SSH. The installed Windows host is
not reused or stopped. This deliberately does not change firewall rules.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tarfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
WINDOWS = ROOT / "experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.exe"


def run(command, data=None, timeout=20, check=True):
    result = subprocess.run(command, input=data, capture_output=True, timeout=timeout,
                            **({"stdin": subprocess.DEVNULL} if data is None else {}))
    if check and result.returncode:
        # Never echo argv/stdin: it could contain an ephemeral test credential.
        raise RuntimeError(result.stderr.decode(errors="replace")[-1500:] or result.stdout.decode(errors="replace")[-1500:])
    return result.stdout


def require_foreground_phone_target(raw):
    if raw.get("foregroundOwned") is not True:
        raise RuntimeError("Owned Android input target is not foreground; no further input permitted")
    return raw


def required_android_relay_probes(pairs):
    required = {}
    if any(pair.startswith("AndroidTo") for pair in pairs):
        required["com.remotedesk.viewerprobe"] = "experiments/AndroidViewerProbe/build/outputs/apk/debug/RemoteDeskAndroidViewerProbe-debug.apk"
    if any(pair.endswith("ToAndroid") for pair in pairs):
        required["com.remotedesk.relayhostprobe"] = "experiments/AndroidRelayHostProbe/build/outputs/apk/debug/RemoteDeskAndroidRelayHostProbe-debug.apk"
    return required


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", required=True); parser.add_argument("--serial", required=True)
    parser.add_argument("--linux", required=True, help="key-authenticated user@address")
    parser.add_argument("--windows-ip", required=True); parser.add_argument("--output", required=True)
    parser.add_argument("--relay-server", help="Use native product relay transport in every selected direction, never LAN fallback")
    parser.add_argument("--relay-ssh-pin")
    parser.add_argument("--relay-tls-pin")
    parser.add_argument("--android-host", action="store_true", help="Wait for normal Android host permission setup after desktop tests")
    parser.add_argument("--android-target-package", choices=("com.remotedesk.codecprobe", "com.remotedesk.relayhostprobe"),
                        default="com.remotedesk.codecprobe", help="Owned interaction fixture for an already-running direct Android host; never changes its password")
    parser.add_argument("--windows-host-gate", action="store_true", help="Wait for owned windows-host/continue after handling test-app firewall dialog")
    parser.add_argument("--pairs", nargs="+", choices=("WindowsToLinux", "AndroidToLinux", "LinuxToWindows", "AndroidToWindows", "WindowsToAndroid", "LinuxToAndroid"),
                        default=["WindowsToLinux", "AndroidToLinux", "LinuxToWindows", "AndroidToWindows"])
    args = parser.parse_args()
    if any(pair.endswith("ToAndroid") for pair in args.pairs) and not args.android_host:
        parser.error("Android target directions require --android-host")
    selected = set(args.pairs)
    if args.android_host and not selected & {"WindowsToAndroid", "LinuxToAndroid"}:
        selected.update(("WindowsToAndroid", "LinuxToAndroid"))
    # Consume piped credentials before spawning SSH. Without this, a child SSH
    # command inherits stdin and can consume the future Android endpoint JSON.
    direct_android_endpoint = None
    if args.android_host and not args.relay_server:
        print("ANDROID_HOST_ENDPOINT: reading authorized endpoint from stdin; keeping existing host settings", flush=True)
        direct_android_endpoint = json.loads(sys.stdin.readline())
    relay_config = None
    relay_ids = {platform: str(uuid.uuid4()) for platform in ("Windows", "Linux", "Android")}
    if args.relay_server:
        if not args.relay_ssh_pin or not args.relay_tls_pin: parser.error("Both verified fingerprints are required")
        from relay_public_config import read_config
        relay_config = read_config(args.relay_server, args.relay_ssh_pin, args.relay_tls_pin)
        sys.path.insert(0, str(ROOT / "scripts/linux"))
        import remotedesk_linux_relay as native_relay
    def relay_for(platform):
        return dict(relay_config, deviceId=relay_ids[platform], publish=True) if relay_config else None
    def route_config(platform): return {"relay": relay_for(platform)} if relay_config else {}
    route_label = "native public relay TLS/TCP; no UDP/LAN fallback" if relay_config else "direct LAN"
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)
    ssh = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", args.linux]
    adb = [args.adb, "-s", args.serial]
    linux_ip = args.linux.split("@")[-1]
    password = secrets.token_urlsafe(24)
    report = {"scope": "physical devices, direct LAN unless explicitly stated; owned synthetic OS targets",
              "pairs": {}, "complete": False}
    if relay_config:
        report.update(scope="Physical Windows/Linux/Android product clients and owned OS targets through public TLS relay",
                      relayServer=args.relay_server, relayPort=relay_config["port"], sshIdentityVerified=True,
                      tlsCertificateSha256=args.relay_tls_pin, deviceIds=relay_ids, directoryEvidence=[])
        hashes = {}
        required_apks = required_android_relay_probes(selected)
        for relative in (
            "experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.dll",
            "experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.dll",
            "scripts/linux/remotedesk_linux_app.py", "scripts/linux/remotedesk_linux_host.py", "scripts/linux/remotedesk_linux_relay.py",
            *required_apks.values()):
            hashes[relative] = hashlib.sha256((ROOT / relative).read_bytes()).hexdigest()
        report["buildSha256"] = hashes
        installed = {}
        for package in required_apks:
            apk_paths = run(adb + ["shell", "pm", "path", package]).decode().splitlines()
            if len(apk_paths) != 1 or not apk_paths[0].startswith("package:/data/app/"):
                raise RuntimeError("Unexpected installed diagnostic APK path")
            installed[package] = hashlib.sha256(run(adb + ["exec-out", "cat", apk_paths[0][8:]])).hexdigest()
        report["installedAndroidSha256"] = installed
        for package, relative in required_apks.items():
            if package in installed and installed[package] != hashes[relative]:
                raise RuntimeError("Installed diagnostic APK does not match this build: " + package)
    report["selectedPairs"] = sorted(selected)
    children = []; opened = []; remote = ""
    def emit(message): print(message, flush=True)
    def sh(command, **kw): return run(ssh + [command], **kw)
    def remote_json(path): return json.loads(sh("cat " + path))
    def write_report():
        (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    def wait_online(platform):
        if not relay_config: return
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            devices = native_relay.list_devices(native_relay.RelayOptions.from_dict(relay_for(platform)))
            own = [device for device in devices if device["deviceId"] == relay_ids[platform]]
            if own:
                report["directoryEvidence"].append(dict(phase=platform + "Host", devices=own)); write_report(); return
            time.sleep(1)
        raise RuntimeError(platform + " native host did not appear in public relay directory")
    def inserted(before, after, text):
        return text in after and after.replace(text, "", 1) == before
    def pair(label, callback):
        if label not in selected: return
        emit("START " + label)
        try: report["pairs"][label] = callback()
        except Exception as error: report["pairs"][label] = {"complete": False, "failure": str(error)}
        write_report(); emit("RESULT " + label + " " + json.dumps({k: v for k, v in report["pairs"][label].items()
            if k in ("complete", "inputVerified", "failure", "frames", "encoding", "continuousRendering", "target")}, ensure_ascii=False))
    def state(): return json.loads(run(adb + ["exec-out", "run-as", "com.remotedesk.viewerprobe", "cat", "files/viewer-state.json"]))
    def broadcast(action, *extras):
        run(adb + ["shell", "am", "broadcast", "-n", "com.remotedesk.viewerprobe/com.remotedesk.agent.ViewerProbeReceiver",
                   "--es", "action", action, *extras])
    def android_view(label, host, port, target, read_target, platform):
        before = read_target()
        run(adb + ["shell", "am", "force-stop", "com.remotedesk.viewerprobe"])
        run(adb + ["shell", "run-as", "com.remotedesk.viewerprobe", "mkdir", "-p", "files"])
        endpoint = json.dumps({"host": relay_config["serverAddress"] if relay_config else host,
                               "port": relay_config["port"] if relay_config else port,
                               "password": password, **route_config(platform)}).encode()
        run(adb + ["exec-in", "run-as", "com.remotedesk.viewerprobe", "sh", "-c", "cat > files/interop-endpoint.json"], data=endpoint)
        run(adb + ["shell", "am", "start", "-W", "-n", "com.remotedesk.viewerprobe/com.remotedesk.agent.ViewerProbeLauncher"])
        samples = []
        try:
            deadline = time.monotonic() + (90 if relay_config else 30)
            while time.monotonic() < deadline:
                time.sleep(.8)
                snapshot = state(); samples.append(snapshot)
                if snapshot.get("geometryReady") and snapshot["frame"][0] > 0 and snapshot.get("mouseEnabled"): break
            else: raise RuntimeError("Android viewer did not present an interactive frame: " + json.dumps(samples[-1], ensure_ascii=False))
            broadcast("landscape"); time.sleep(1.5)
            for key in ("button", "editor"):
                broadcast("remote_tap", "--ef", "x", str(target[key][0]), "--ef", "y", str(target[key][1])); time.sleep(.6)
            broadcast("keyboard"); time.sleep(.5)
            # ASCII ADB command transport. The probe commits a fixed Chinese /
            # emoji prefix through the product composer's native InputConnection.
            broadcast("send_text", "--es", "text", label + "42")
            for _ in range(40 if relay_config else 12): time.sleep(.7); samples.append(state())
            after = read_target()
            (output / (label + ".png")).write_bytes(run(adb + ["exec-out", "screencap", "-p"]))
            (output / (label + "-samples.json")).write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding="utf-8")
            moving = any(float(m.group(1)) > 0 for s in samples for m in [re.search(r"FPS\s*([\d.]+)", s.get("health", ""))] if m)
            valid_input = after["clicks"] == before["clicks"] + 1 and inserted(before["text"], after["text"], "中文测试😀" + label + "42")
            route_verified = not relay_config or "公网中转" in samples[-1].get("health", "")
            return {"complete": moving and valid_input and route_verified and not samples[-1]["failure"], "inputVerified": valid_input,
                    "continuousRendering": moving, "target": after, "lastState": samples[-1], "route": route_label,
                    "routeVerified": route_verified}
        finally: run(adb + ["shell", "am", "force-stop", "com.remotedesk.viewerprobe"], check=False)
    def windows_view(label, host, port, target, read_target, platform, android=False):
        before = read_target(); folder = output / label
        config = dict(host=host, port=port, password=password, output=str(folder), android=android,
                      button=target["button"], editor=target["editor"], text=label + "中文42", **route_config(platform))
        result = subprocess.run([str(WINDOWS), "viewer"], input=json.dumps(config).encode(), capture_output=True, timeout=150 if relay_config else 50)
        after = read_target()
        value = json.loads((folder / "viewer.json").read_text()) if (folder / "viewer.json").exists() else {}
        value["inputVerified"] = after["clicks"] == before["clicks"] + 1 and inserted(before["text"], after["text"], config["text"])
        value["target"] = after; value["route"] = route_label
        value["complete"] = result.returncode == 0 and value.get("complete", False) and value["inputVerified"]
        if (folder / "failure.txt").exists(): value["failure"] = (folder / "failure.txt").read_text()
        return value
    def linux_view(label, host, port, target, read_target, platform):
        before = read_target(); directory = remote + "/" + label
        config = dict(host=host, port=port, password=password, button=target["button"], editor=target["editor"], text=label + "中文42", **route_config(platform))
        sh(f"python3 -u {remote}/experiments/interop_linux_node.py viewer --output {directory}",
           data=json.dumps(config).encode() + b"\n", timeout=120 if relay_config else 50, check=False)
        value = remote_json(directory + "/viewer.json"); after = read_target()
        value["inputVerified"] = after["clicks"] == before["clicks"] + 1 and inserted(before["text"], after["text"], config["text"])
        value["target"] = after; value["route"] = route_label
        value["complete"] = value["complete"] and value["inputVerified"]
        return value
    try:
        remote = sh("mktemp -d /tmp/remotedesk-interop-XXXXXXXX").decode().strip()
        if not re.fullmatch(r"/tmp/remotedesk-interop-[A-Za-z0-9]{8}", remote): raise RuntimeError("Unexpected staging path")
        report["remoteOwnedDirectory"] = remote; write_report()
        archive = output / "sources.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            for path in (ROOT / "scripts/linux").glob("*.py"): tar.add(path, arcname=path.relative_to(ROOT))
            path = ROOT / "experiments/interop_linux_node.py"; tar.add(path, arcname=path.relative_to(ROOT))
        run(["scp", "-q", str(archive), args.linux + ":" + remote + "/sources.tar.gz"])
        sh(f"tar -xzf {remote}/sources.tar.gz -C {remote}")
        if selected & {"WindowsToLinux", "AndroidToLinux"}:
            log = (output / "linux-host-process.log").open("wb"); opened.append(log)
            child = subprocess.Popen(ssh + [f"python3 -u {remote}/experiments/interop_linux_node.py host --output {remote}/host"],
                                     stdin=subprocess.PIPE, stdout=log, stderr=log)
            children.append(child); child.stdin.write(json.dumps({"password": password, **route_config("Linux")}).encode()+b"\n"); child.stdin.close()
            for _ in range(100 if relay_config else 45):
                time.sleep(.5)
                try: endpoint = remote_json(remote + "/host/endpoint.json"); break
                except Exception: pass
            else: raise RuntimeError("Linux host was not ready")
            port = endpoint["port"]; emit("Linux owned host listening on " + str(port))
            wait_online("Linux")
            target_reader = lambda: remote_json(remote + "/host/target.json")
            target = target_reader()
            pair("WindowsToLinux", lambda: windows_view("WindowsToLinux", linux_ip, port, target, target_reader, "Linux"))
            pair("AndroidToLinux", lambda: android_view("AndroidToLinux", linux_ip, port, target, target_reader, "Linux"))
            sh(f"touch {remote}/host/stop"); child.wait(timeout=15)
        if selected & {"LinuxToWindows", "AndroidToWindows"}:
            with socket.socket() as reservation:
                reservation.bind(("0.0.0.0", 0)); port = reservation.getsockname()[1]
            folder = output / "windows-host"; folder.mkdir()
            log = (folder / "process.log").open("wb"); opened.append(log)
            child = subprocess.Popen([str(WINDOWS), "host"], stdin=subprocess.PIPE, stdout=log, stderr=log)
            children.append(child)
            child.stdin.write(json.dumps(dict(port=port, password=password, output=str(folder), scale=50, **route_config("Windows"))).encode()+b"\n"); child.stdin.close()
            emit("WINDOWS_TARGET_STARTING: owned interactive window must be foreground before capture starts")
            for _ in range(700):
                if (folder / "target.json").exists(): break
                if child.poll() is not None or (folder / "failure.txt").exists():
                    raise RuntimeError("Windows target did not obtain safe foreground ownership")
                time.sleep(.1)
            else: raise RuntimeError("Windows owned host failed to start")
            wait_online("Windows")
            if args.windows_host_gate:
                emit("WINDOWS_HOST_GATE: handle only this probe's firewall prompt, then create " + str(folder / "continue"))
                deadline = time.monotonic() + 300
                while not (folder / "continue").exists() and time.monotonic() < deadline: time.sleep(.5)
                if not (folder / "continue").exists(): raise RuntimeError("Windows gate timed out")
            def target_reader():
                if child.poll() is not None or (folder / "failure.txt").exists():
                    raise RuntimeError("Windows owned target stopped; no further input permitted")
                value = json.loads((folder / "target.json").read_text())
                if not value.get("foregroundOwned"):
                    raise RuntimeError("Windows owned target is not foreground; no further input permitted")
                return value
            target = target_reader()
            pair("LinuxToWindows", lambda: linux_view("LinuxToWindows", args.windows_ip, port, target, target_reader, "Windows"))
            pair("AndroidToWindows", lambda: android_view("AndroidToWindows", args.windows_ip, port, target, target_reader, "Windows"))
            (folder / "stop").touch(); child.wait(timeout=15)
        if args.android_host:
            # Human/agent operates only normal product + system permission UI.
            # This gate never starts screen recording or modifies production prefs.
            if relay_config:
                projection = run(adb + ["shell", "dumpsys", "media_projection"]).decode()
                if not re.search(r"Media Projection:\s*null", projection): raise RuntimeError("Existing screen share must be preserved")
                package = "com.remotedesk.relayhostprobe"
                run(adb + ["shell", "run-as", package, "mkdir", "-p", "files"])
                endpoint = json.dumps(dict(password=password, **route_config("Android"))).encode()
                run(adb + ["exec-in", "run-as", package, "sh", "-c", "cat > files/interop-endpoint.json"], data=endpoint)
                run(adb + ["shell", "am", "start", "-W", "-n", package + "/com.remotedesk.agent.HostProbeLauncher"])
                emit("ANDROID_HOST_GATE: normal test-app accessibility + Start host + screen-sharing consent required; waiting up to 10 minutes")
                deadline = time.monotonic() + 600
                while time.monotonic() < deadline:
                    time.sleep(1)
                    try:
                        status = json.loads(run(adb + ["exec-out", "run-as", package, "cat", "files/host-state.json"]))
                        if status.get("hostRunning") and status.get("accessibility") and "已上线" in status.get("relay", ""): break
                    except Exception: pass
                else: raise RuntimeError("Normal phone test-host permission/startup gate timed out")
                wait_online("Android")
                report["androidNativeHost"] = status
                config = dict(host=args.relay_server, port=relay_config["port"])
            else:
                emit("ANDROID_HOST_GATE: provide authorized host endpoint via stdin JSON (password, host, port); no credential is written to report")
                config = direct_android_endpoint; password = config.pop("password")
                package = args.android_target_package
                # A signed installed host cannot (and must not) expose its private
                # preferences via run-as. Reuse the authorized endpoint from stdin
                # and launch only our separate, allowlisted synthetic input target.
                run(adb + ["shell", "am", "force-stop", package])
            launch = run(adb + ["shell", "am", "start", "-W", "-n", package + "/com.remotedesk.agent.InteractionProbeActivity",
                                "--ez", "guardHostedInput", "true" if relay_config else "false"])
            (output / "android-target-launch.log").write_bytes(launch)
            if b"Error:" in launch:
                raise RuntimeError("Owned Android input Activity failed to launch; see android-target-launch.log")
            def phone_target():
                raw = require_foreground_phone_target(json.loads(run(adb + ["exec-out", "run-as", package, "cat", "files/interaction-probe.json"])))
                for key in ("button", "editor"):
                    b = raw[key + "Bounds"]; raw[key] = [(b[0]+b[2])/2/(raw["screenWidth"]-1), (b[1]+b[3])/2/(raw["screenHeight"]-1)]
                return raw
            for _ in range(20):
                try:
                    target = phone_target(); break
                except RuntimeError:
                    time.sleep(.25)
            else:
                raise RuntimeError("Owned Android input target did not obtain safe foreground ownership")
            pair("WindowsToAndroid", lambda: windows_view("WindowsToAndroid", config["host"], config["port"], target, phone_target, "Android", True))
            pair("LinuxToAndroid", lambda: linux_view("LinuxToAndroid", config["host"], config["port"], target, phone_target, "Android"))
        report["complete"] = set(report["pairs"]) == selected and all(v["complete"] for v in report["pairs"].values())
    finally:
        if remote:
            sh(f"touch {remote}/host/stop", check=False)
        if (output / "windows-host").exists(): (output / "windows-host/stop").touch()
        for child in children:
            try: child.wait(timeout=15)
            except subprocess.TimeoutExpired: child.terminate(); child.wait(timeout=5)
        for log in opened: log.close()
        run(adb + ["shell", "am", "force-stop", "com.remotedesk.viewerprobe"], check=False)
        if relay_config and args.android_host:
            run(adb + ["shell", "am", "force-stop", "com.remotedesk.relayhostprobe"], check=False)
        if remote:
            # Keep the bounded remote evidence directory for inspection; cleanup
            # requires explicit path validation after evidence has been copied.
            run(["scp", "-q", "-r", args.linux + ":" + remote, str(output / "linux-evidence")], timeout=40, check=False)
        write_report(); emit("FINAL " + str(output / "result.json"))
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
