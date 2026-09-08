#!/usr/bin/env python3
"""Check only owned Windows fixtures: initial focus and stop on focus loss.

No viewer authenticates and no remote input is sent. Existing product processes
and user windows are never selected as a stimulus. Host secrets stay on stdin.
"""
import argparse
import json
from pathlib import Path
import secrets
import socket
import subprocess
import time

from run_physical_interop import WINDOWS


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    output = Path(parser.parse_args().output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    children, streams = [], []
    report = {"complete": False, "scope": "two owned Windows fixtures; no viewers or remote input"}
    try:
        for name in ("target", "owned-focus-stimulus"):
            folder = output / name
            folder.mkdir()
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            log = (folder / "process.log").open("wb")
            streams.append(log)
            child = subprocess.Popen([str(WINDOWS), "host"], stdin=subprocess.PIPE, stdout=log, stderr=log)
            children.append((child, folder))
            child.stdin.write(json.dumps(dict(port=port, password=secrets.token_urlsafe(24),
                                             scale=50, output=str(folder))).encode() + b"\n")
            child.stdin.close()
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                if child.poll() is not None:
                    raise RuntimeError("Owned fixture stopped before readiness")
                if (folder / "target.json").exists():
                    if json.loads((folder / "target.json").read_text()).get("foregroundOwned"):
                        break
                time.sleep(.1)
            else:
                raise RuntimeError("Owned fixture did not obtain foreground")
            if name == "target":
                report["initialForegroundConfirmed"] = True
        original, folder = children[0]
        code = original.wait(timeout=8)
        failure = (folder / "failure.txt").read_text()
        report["foregroundLossStoppedHost"] = code == 1 and failure.startswith("Safety stop:")
        report["complete"] = report["initialForegroundConfirmed"] and report["foregroundLossStoppedHost"]
    finally:
        for child, folder in children:
            (folder / "stop").touch()
            try:
                child.wait(timeout=8)
            except subprocess.TimeoutExpired:
                child.terminate()
                child.wait(timeout=5)
        for stream in streams:
            stream.close()
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report), flush=True)
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
