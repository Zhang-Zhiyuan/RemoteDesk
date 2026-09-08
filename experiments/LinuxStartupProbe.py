"""Run under xvfb-run on the owned Linux test machine; all configuration is temporary."""
from pathlib import Path
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time

root_directory = Path(__file__).resolve().parents[1]
source_directory = Path(sys.argv[1]) if len(sys.argv) > 1 else root_directory / "scripts" / "linux"
sys.path.insert(0, str(source_directory))
import remotedesk_linux_app as app
import remotedesk_linux_startup as startup


def pump(root, predicate, seconds=12):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        root.update()
        if predicate():
            return
        time.sleep(0.025)
    raise AssertionError("Timed out waiting for the owned host process")


def frame_check(port, password):
    probe = source_directory / "remotedesk_protocol_probe.py"
    result = subprocess.run([sys.executable, str(probe), "--host", "127.0.0.1", "--port", str(port),
                             "--password-fd", "0", "--duration", "2", "--frames", "3", "--json"],
                            input=password, text=True, capture_output=True, timeout=15)
    if result.returncode:
        raise AssertionError("Authenticated frame probe failed: " + result.stdout + result.stderr)
    data = json.loads(result.stdout)
    assert data["authenticated"] and data["frames"]
    return len(data["frames"])


def main():
    with tempfile.TemporaryDirectory(prefix="remotedesk-gui-startup-") as directory:
        os.environ["XDG_CONFIG_HOME"] = directory
        os.environ["REMOTEDESK_AUTO_INSTALL"] = "0"
        password = secrets.token_urlsafe(24)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        result = {}
        for stage in ("initial", "restart", "after_stop"):
            window = app.tk.Tk(className="RemoteDesk")
            controller = app.RemoteDeskLinuxApp(window)
            try:
                if stage == "initial":
                    controller.host_password.set(password)
                    controller.host_port.set(str(port))
                    controller.host_receive_dir.set(str(Path(directory) / "received"))
                    controller.host_capture.set("x11")
                    controller.host_login_start.set(True)
                    controller.start_host()
                if stage == "after_stop":
                    deadline = time.monotonic() + 1
                    while time.monotonic() < deadline:
                        window.update()
                        time.sleep(0.025)
                    assert controller.host_process is None and not controller.host_armed
                    result["explicit_stop_persisted"] = True
                    continue
                pump(window, lambda: controller.host_process is not None)
                # Wait for the child's bind, without passing any test credentials in argv.
                time.sleep(1)
                result[stage + "_authenticated_frames"] = frame_check(port, password)
                if stage == "restart":
                    assert controller.host_password.get() == password
                    previous = controller.host_process
                    previous.kill()  # Only this probe's child; exercise unexpected-exit recovery.
                    pump(window, lambda: controller.host_process is not None and controller.host_process is not previous)
                    time.sleep(1)
                    result["crash_recovery_authenticated_frames"] = frame_check(port, password)
                    controller.stop_host()
                    pump(window, lambda: controller.host_process is None)
                    assert not startup.HostPreferences().load()["armed"]
                    result["remembered_password"] = True
                desktop = Path(directory) / "autostart/remotedesk-host.desktop"
                subprocess.run(["desktop-file-validate", str(desktop)], check=True, capture_output=True)
                result["desktop_entry_valid"] = True
            finally:
                controller.close()
                controller.stop_host_at_exit()
        print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
