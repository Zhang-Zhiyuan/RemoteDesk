#!/usr/bin/env python3
"""Real Android product viewer rejects bad relay identity/key, without real secrets."""
import argparse
import json
from pathlib import Path
import time
import sys
import uuid

from run_physical_interop import run


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser()
    for name in ("adb", "serial", "server", "tls-pin", "output"): parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve(); output.mkdir(parents=True, exist_ok=False)
    adb = [args.adb, "-s", args.serial]
    package = "com.remotedesk.viewerprobe"
    report = {"complete": False, "scope": "physical Android viewer through public relay; invalid fixture credentials only", "checks": {}}
    try:
        for label, pin, expected in (("wrongPin", "0" * 64, "身份校验失败"), ("wrongAccessKey", args.tls_pin, "服务器登录已失效")):
            run(adb + ["shell", "am", "force-stop", package])
            run(adb + ["shell", "run-as", package, "mkdir", "-p", "files"])
            config = dict(host=args.server, port=56567, password="owned-unusable-test-password",
                relay=dict(serverAddress=args.server, port=56567, accessToken="owned-invalid-access-key-" * 3,
                    tlsCertificateSha256=pin, deviceId=str(uuid.uuid4()), publish=False))
            run(adb + ["exec-in", "run-as", package, "sh", "-c", "cat > files/interop-endpoint.json"],
                data=json.dumps(config).encode())
            run(adb + ["shell", "am", "start", "-W", "-n", package + "/com.remotedesk.agent.ViewerProbeLauncher"])
            deadline = time.monotonic() + 30
            state = {}
            while time.monotonic() < deadline:
                time.sleep(.3)
                try: state = json.loads(run(adb + ["exec-out", "run-as", package, "cat", "files/viewer-state.json"]))
                except Exception: continue
                if expected in state.get("status", ""): break
            time.sleep(3)
            final = json.loads(run(adb + ["exec-out", "run-as", package, "cat", "files/viewer-state.json"]))
            passed = expected in final.get("status", "") and final.get("ownerGeneration") == 0 and not final.get("geometryReady")
            report["checks"][label] = dict(passed=passed, status=final.get("status"), ownerGeneration=final.get("ownerGeneration"))
            print(json.dumps({label: report["checks"][label]}, ensure_ascii=False), flush=True)
        report["complete"] = all(item["passed"] for item in report["checks"].values())
    finally:
        run(adb + ["shell", "am", "force-stop", package], check=False)
        (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if report["complete"] else 1


if __name__ == "__main__": raise SystemExit(main())
