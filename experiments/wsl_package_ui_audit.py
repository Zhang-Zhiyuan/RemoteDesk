#!/usr/bin/env python3
"""Run existing real-Tk layout scenarios against the shipped package, not source."""
import argparse
import importlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

from wsl_package_fixture import extract_package


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1":
        parser.error("Requires an owned xvfb-run")
    with tempfile.TemporaryDirectory(prefix="remotedesk-package-ui-") as temporary:
        work = Path(temporary)
        extract_package(args.package.resolve(), work)
        os.environ["WAYLAND_DISPLAY"] = ""
        os.environ["XDG_SESSION_TYPE"] = "x11"
        os.environ["REMOTEDESK_AUTO_INSTALL"] = "0"
        sys.dont_write_bytecode = True
        sys.path.insert(0, str(work / "app"))
        module = importlib.import_module("remotedesk_linux_app")
        if not Path(module.__file__).resolve().is_relative_to(work):
            raise RuntimeError("UI audit is not using the extracted release")
        window_manager = subprocess.Popen(["openbox", "--sm-disable"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            # The existing audit disables discovery only; all actual widgets,
            # layout, profile IO, modal dialogs and screenshots remain real.
            import linux_ui_layout_audit
            sys.argv = [sys.argv[0], "--owned-display", "--output", str(args.output.resolve())]
            linux_ui_layout_audit.main()
        finally:
            window_manager.terminate()
            window_manager.wait(timeout=6)
        print(json.dumps({"packageUiImported": True, "scope": "Published package on owned WSL/Xvfb/Openbox"}))


if __name__ == "__main__":
    main()
