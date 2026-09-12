#!/usr/bin/env python3
"""Product Windows client / physical Linux host, confined to a fresh Xvfb.

No local clipboard, physical input, installed configuration or service changes.
Key-authenticated SSH only; the test-session password stays in process pipes.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import secrets
import subprocess
import sys
import tarfile
import time
import uuid
from run_physical_interop import ROOT, WINDOWS, run


def read_relay_config(reader):
    """Read a bounded, already-approved relay identity before spawning children."""
    sys.path.insert(0, str(ROOT / "scripts/linux"))
    from remotedesk_linux_relay import RelayOptions
    try:
        raw = reader.readline(16385)
        if len(raw) > 16384:
            raise ValueError()
        value = json.loads(raw)
        if not isinstance(value, dict):
            raise ValueError()
        # Never reuse/replace an installed host's registration identity.
        value["deviceId"] = str(uuid.uuid4())
        value["publish"] = True
        return RelayOptions.from_dict(value).to_dict()
    except (TypeError, ValueError, AttributeError, OverflowError):
        raise ValueError("Invalid relay configuration on stdin; contents omitted") from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--linux", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--relay-stdin", action="store_true",
                        help="Read verified serverAddress/port/accessToken/tlsCertificateSha256 JSON on stdin; never deploys or falls back to LAN")
    args = parser.parse_args()
    relay = read_relay_config(sys.stdin) if args.relay_stdin else None
    route = dict(relay=relay) if relay else {}
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    ssh_options = ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5"]
    ssh = ["ssh", *ssh_options, args.linux]
    report = {"complete": False, "scope": "Actual Windows product client to physical Linux host on fresh owned Xvfb",
              "route": "native public relay TLS/TCP; no LAN fallback" if relay else "direct LAN"}
    remote = ""
    child = None
    log = None
    def sh(command, **kwargs): return run(ssh + [command], **kwargs)
    try:
        remote = sh("mktemp -d /tmp/remotedesk-feature-XXXXXXXX").decode().strip()
        if not re.fullmatch(r"/tmp/remotedesk-feature-[A-Za-z0-9]{8}", remote):
            remote = ""
            raise RuntimeError("Unexpected remote staging path")
        report["ownedRemoteDirectory"] = remote
        archive = output / "sources.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            for path in (ROOT / "scripts/linux").glob("*.py"):
                tar.add(path, arcname=path.relative_to(ROOT))
            path = ROOT / "experiments/interop_linux_node.py"
            tar.add(path, arcname=path.relative_to(ROOT))
        run(["scp", *ssh_options, "-q", str(archive), args.linux + ":" + remote + "/sources.tar.gz"])
        sh(f"tar -xzf {remote}/sources.tar.gz -C {remote}")
        password = secrets.token_urlsafe(24)
        log = (output / "host-process.log").open("wb")
        child = subprocess.Popen(ssh + [f"python3 -u {remote}/experiments/interop_linux_node.py host --output {remote}/host"],
                                 stdin=subprocess.PIPE, stdout=log, stderr=log)
        child.stdin.write(json.dumps(dict(password=password, featureFixtures=True, **route)).encode() + b"\n")
        child.stdin.close()
        endpoint = None
        for _ in range(70):
            if child.poll() is not None: raise RuntimeError("Owned Linux host stopped before startup")
            try:
                endpoint = json.loads(sh(f"cat {remote}/host/endpoint.json"))
                break
            except RuntimeError: time.sleep(.5)
        if not endpoint or not re.fullmatch(r":\d+", endpoint["display"]):
            raise RuntimeError("Owned Linux Xvfb endpoint is unavailable")
        report["endpoint"] = endpoint
        config = dict(host=args.linux.split("@")[-1], port=endpoint["port"], password=password, output=str(output / "client"), **route)
        result = subprocess.run([str(WINDOWS), "features"], input=json.dumps(config).encode(), capture_output=True,
                                timeout=500 if relay else 260)
        print(result.stdout.decode(errors="replace"), flush=True)
        # Read only files from this newly allocated test host. Never touch the
        # user's real graphical-session clipboard or enumerate their directories.
        inspect = f"""
import hashlib, json, os, subprocess, zipfile
from pathlib import Path
root = Path({(remote + '/host')!r})
files = []
for path in sorted((root / 'receive').rglob('*')):
    if not path.is_file(): continue
    row = dict(name=path.name, length=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest())
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as archive:
            row['entries'] = {{entry: hashlib.sha256(archive.read(entry)).hexdigest() for entry in archive.namelist() if not entry.endswith('/')}}
    files.append(row)
clipboard = subprocess.run(['xclip', '-selection', 'clipboard', '-o'], env={{**os.environ, 'DISPLAY': {endpoint['display']!r}}}, capture_output=True, timeout=5)
print(json.dumps(dict(files=files, clipboardMatched=clipboard.returncode == 0 and clipboard.stdout.decode('utf-8') == 'RemoteDesk clipboard 中文😀')))
"""
        verified = json.loads(sh("python3 -", data=inspect.encode("utf-8")))
        report["remoteVerification"] = verified
        expected = hashlib.sha256(bytes(i % 239 for i in range(128 * 1024 + 31))).hexdigest()
        uploads = [row for row in verified["files"] if row["sha256"] == expected]
        empty = [row for row in verified["files"] if row["name"] == "empty.txt" and row["length"] == 0]
        nested = hashlib.sha256("nested 中文😀".encode()).hexdigest()
        directories = [row for row in verified["files"] if list(row.get("entries", {}).values()) == [nested]]
        report["uploadHashesVerified"] = len(uploads) == 2 and len({row["name"] for row in uploads}) == 2
        report["emptyFileVerified"] = len(empty) == 1
        report["directoryContentsVerified"] = len(directories) == 1
        report["client"] = json.loads((output / "client/features.json").read_text())
        report["complete"] = (result.returncode == 0 and report["client"]["complete"] and
            report["uploadHashesVerified"] and report["emptyFileVerified"] and report["directoryContentsVerified"] and verified["clipboardMatched"])
    except Exception as error:
        report["failure"] = type(error).__name__ + ": " + str(error)
    finally:
        if remote:
            try:
                sh(f"touch {remote}/host/stop", check=False)
                if child: child.wait(timeout=15)
                run(["scp", *ssh_options, "-q", "-r", args.linux + ":" + remote + "/host", str(output / "linux-evidence")], timeout=20)
            except Exception as error:
                report["cleanupFailure"] = type(error).__name__ + ": " + str(error)
                report["complete"] = False
        if child and child.poll() is None:
            child.terminate()
            child.wait(timeout=5)
        if log: log.close()
        (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"complete": report["complete"], "failure": report.get("failure"), "output": str(output)}))
    return 0 if report["complete"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
