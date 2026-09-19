#!/usr/bin/env python3
"""Run the shipped Linux host on an owned Xvfb, using only synthetic fixtures."""
import argparse
import inspect
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import secrets
import signal
import socket
import subprocess
import sys
import tarfile
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]


def allow_owned_loopback_viewer(app):
    """Explicit fixture-only opt-in; never changes a product setting or source."""
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        raise RuntimeError("An owned Xvfb fixture is required")
    original = app.ViewerConnection
    if "allow_self_connection_for_testing" not in inspect.signature(original).parameters:
        return False  # Older baseline packages predate the guard.

    class OwnedFixtureViewer(original):
        def __init__(self, *args, **kwargs):
            kwargs["allow_self_connection_for_testing"] = True
            super().__init__(*args, **kwargs)

    app.ViewerConnection = OwnedFixtureViewer
    return True


def extract_package(package, directory):
    with tarfile.open(package, "r:gz") as archive:
        for member in archive.getmembers():
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or not (member.isdir() or member.isfile()):
                raise ValueError("Unsafe package member")
        archive.extractall(directory, filter="data")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seconds", type=int, default=1800)
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or not os.environ.get("DISPLAY"):
        parser.error("Requires a caller-owned xvfb-run; never use a user display")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    package = args.package.resolve()
    report = {"complete": False, "scope": "Published Linux host, WSL, owned Xvfb and synthetic files only",
              "packageSha256": hashlib.sha256(package.read_bytes()).hexdigest()}
    children, handles = [], []
    stopped = False
    temporary_manager = None
    def request_stop(*_):
        nonlocal stopped
        stopped = True
    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    try:
        temporary_manager = tempfile.TemporaryDirectory(prefix="remotedesk-wsl-package-")
        work = Path(temporary_manager.name)
        extract_package(package, work)
        env = {**os.environ, "WAYLAND_DISPLAY": "", "XDG_SESSION_TYPE": "x11",
               "XDG_CONFIG_HOME": str(work / "config"), "XDG_CACHE_HOME": str(work / "cache"),
               "REMOTEDESK_AUTO_INSTALL": "0", "PYTHONDONTWRITEBYTECODE": "1"}
        (work / "config").mkdir(mode=0o700)
        (work / "cache").mkdir(mode=0o700)
        def launch(command, name, **options):
            log = (output / (name + ".log")).open("w", encoding="utf-8")
            handles.append(log)
            process = subprocess.Popen(command, stdout=log, stderr=log, env=env,
                                       start_new_session=True, **options)
            children.append(process)
            return process
        launch(["openbox", "--sm-disable"], "openbox")
        launch([sys.executable, "-B", str(ROOT / "experiments/interop_linux_node.py"),
                "source", "--output", str(output)], "source")
        deadline = time.monotonic() + 12
        while not (output / "target.json").exists():
            if time.monotonic() >= deadline or children[-1].poll() is not None:
                raise RuntimeError("Owned synthetic source did not start")
            time.sleep(.05)
        with socket.socket() as reservation:
            reservation.bind(("0.0.0.0", 0))
            port = reservation.getsockname()[1]
        password = secrets.token_urlsafe(24)
        returned = output / "return-fixture-中文.txt"
        returned.write_text("RemoteDesk owned package return 中文😀\r\nsecond line\n", encoding="utf-8", newline="")
        host = launch([sys.executable, "-u", "-B", str(work / "app/remotedesk_linux_host.py"),
                       "--host", "0.0.0.0", "--port", str(port), "--password-fd", "0",
                       "--no-discovery", "--display", os.environ["DISPLAY"],
                       "--width", "1920", "--height", "1080", "--fps", "30",
                       "--serve-seconds", str(args.seconds), "--machine-name", "Owned-WSL-1.0.27",
                       "--receive-dir", str(output / "receive"), "--return-file", str(returned)],
                      "host", stdin=subprocess.PIPE)
        host.stdin.write(password.encode("utf-8")); host.stdin.close()
        deadline = time.monotonic() + 15
        while "host listening on" not in (output / "host.log").read_text(encoding="utf-8"):
            if time.monotonic() >= deadline or host.poll() is not None:
                raise RuntimeError("Published host failed to start; see host.log")
            time.sleep(.05)
        address = subprocess.check_output(["hostname", "-I"], text=True).split()[0]
        endpoint = {"host": address, "port": port, "display": os.environ["DISPLAY"],
                    "xauthority": os.environ.get("XAUTHORITY", ""), "route": "owned WSL direct TCP",
                    "packageRoot": str(work), "receiveDirectory": str(output / "receive"),
                    "targetSnapshot": str(output / "target.json"), "hostPid": host.pid}
        (output / "endpoint.json").write_text(json.dumps(endpoint, indent=2), encoding="utf-8")
        # This is a new random fixture secret, never an installed/user secret.
        # Keep it out of messages, argv and the normal evidence report.
        (output / "client-input.json").write_text(json.dumps({"host": address, "port": port,
            "password": password, "output": str(output / "client")}), encoding="utf-8")
        (output / "client-input.json").chmod(0o600)
        password = None
        print(json.dumps({"ready": True, "host": address, "port": port,
                          "fixtureConfig": str(output / "client-input.json")}), flush=True)
        report.update(ready=True, endpoint=endpoint)
        deadline = time.monotonic() + args.seconds
        while not stopped and host.poll() is None and time.monotonic() < deadline and not (output / "stop").exists():
            time.sleep(.2)
        host_exit = host.poll()
        report["hostExitCodeBeforeCleanup"] = host_exit
        if host_exit is not None and host_exit != 0:
            raise RuntimeError("Owned host exited abnormally")
        if host_exit is not None and not stopped and not (output / "stop").exists() and time.monotonic() < deadline - 5:
            raise RuntimeError("Owned host exited before its bounded test duration")
        report["stopReason"] = "requested" if stopped or (output / "stop").exists() else "bounded-duration"
        report["complete"] = True
    except Exception as error:
        report["errorType"] = type(error).__name__
        report["error"] = str(error)
    finally:
        for child in reversed(children):
            # Every launch owns a new session; terminate the whole group even
            # when the immediate Python parent already exited unexpectedly.
            try:
                os.killpg(child.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            if child.poll() is None:
                try:
                    child.wait(timeout=6)
                except subprocess.TimeoutExpired:
                    if os.getpgid(child.pid) == child.pid:
                        os.killpg(child.pid, signal.SIGKILL)
                    child.wait(timeout=3)
        if temporary_manager is not None:
            temporary_manager.cleanup()
        for handle in handles:
            handle.close()
        report["ownedProcessesStopped"] = all(child.poll() is not None for child in children)
        (output / "client-input.json").unlink(missing_ok=True)
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
