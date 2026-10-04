"""Owned real relay desktop + native SSH concurrency and local Tk lifecycle audit."""
import argparse
import asyncio
from dataclasses import replace
import gc
import hashlib
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile
import time
from unittest import mock
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_ssh as ssh
import remotedesk_linux_relay as relay
import remotedesk_protocol_probe as wire


async def run(metadata_path, output, transitions=False):
    metadata = json.loads(metadata_path.read_text())
    if metadata.get("owned") is not True or metadata.get("desktopFixture") is not True:
        raise RuntimeError("Only a new owned desktop/SSH fixture is allowed")
    if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or os.environ.get("WAYLAND_DISPLAY"):
        raise RuntimeError("Viewer UI requires its own Xvfb")
    if os.environ.get("DISPLAY") == metadata["display"]:
        raise RuntimeError("Viewer must use a different owned X server from the target")
    output.mkdir(parents=True, exist_ok=True)
    checks, errors, pulses, frames = [], [], [], []
    report = dict(owned=True, physicalHardware=False, viewerDisplay=os.environ["DISPLAY"], targetDisplay=metadata["display"], checks=checks)
    def check(name, condition, **details):
        checks.append(dict(name=name, passed=bool(condition), **details))
        (output / "report.json").write_text(json.dumps(report, indent=2))
        if not condition:
            raise AssertionError(name)
    options = relay.RelayOptions(metadata["serverAddress"], metadata["port"], metadata["token"],
        metadata["tlsCertificateSha256"], metadata["deviceId"]).validate()
    process = heartbeat = ui = root = owned_clipboard = None
    with tempfile.TemporaryDirectory(prefix="owned-desktop-ssh-audit-") as temporary, \
            mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": str(Path(temporary) / "config")}), \
            mock.patch.object(app.DevicePanel, "scan"), mock.patch.object(app.RemoteDeskLinuxApp, "_refresh_relay"), \
            mock.patch.object(app.messagebox, "showerror", side_effect=lambda *args, **kwargs: errors.append(str(args))), \
            mock.patch.object(app.messagebox, "showwarning", side_effect=lambda *args, **kwargs: errors.append(str(args))):
        private = Path(temporary)
        client_options = replace(options, device_id=str(uuid.uuid4()), publish=False)
        relay.save_settings(client_options)
        ssh_root = private / "ssh"
        ssh.configure_terminal_alias("owned-target", metadata["sshUsername"], options, metadata["deviceKey"],
            ssh_directory=ssh_root, identity_file=metadata["linuxKeyPath"])
        (ssh.ssh_config_directory() / "known_hosts").write_text(ssh.host_key_alias(options) + " " + metadata["sshHostKeyPublic"] + "\n")
        root = app.tk.Tk()
        root.report_callback_exception = lambda *args: errors.append(str(args))
        ui = app.RemoteDeskLinuxApp(root)
        ui._show_file_transfer_confirmation_dialog = lambda *args: True  # Explicitly owned fixture files only.
        ui.relay_options = client_options
        ui._show_relay_directory([dict(deviceId=options.device_id, machineName="Owned concurrent target", platform="Linux", busy=False,
            services=["desktop", "ssh"], sshTunneling=True, directAddresses=[], directPort=0)])
        ui.relay_list.selection_set(options.device_id)
        ui.relay_password.set(metadata["deviceKey"])
        def tick():
            pulses.append(time.monotonic())
            if not ui.closing:
                root.after(10, tick)
        tick()
        async def wait(predicate, timeout=15):
            deadline = time.monotonic() + timeout
            while not predicate() and time.monotonic() < deadline:
                root.update()
                await asyncio.sleep(.01)
            root.update()
            if not predicate():
                raise TimeoutError("Owned concurrency condition; status=" + str(ui.viewer_status.cget("text")))
        async def pause(seconds):
            end = time.monotonic() + seconds
            await wait(lambda: time.monotonic() >= end, seconds + 1)
        def watch(viewer):
            original = viewer._put_event
            def observed(kind, value):
                if kind == "viewer_frame":
                    frames.append(time.monotonic())
                original(kind, value)
            viewer._put_event = observed
        last_target = json.loads(Path(metadata["targetPath"]).read_text())
        def target():
            nonlocal last_target
            try:
                last_target = json.loads(Path(metadata["targetPath"]).read_text())
            except (FileNotFoundError, json.JSONDecodeError):
                # DrvFS rename can briefly make a rapidly replaced evidence
                # file unavailable; wait predicates must reread the next tick.
                pass
            return last_target
        async def click(field):
            snapshot = target()
            x, y = snapshot[field]
            viewer = ui.viewer
            for kind in (app.INPUT_MOUSE_DOWN, app.INPUT_MOUSE_UP):
                assert viewer.send_input(kind, app.MOUSE_LEFT, round(x * (ui.remote_width - 1)), round(y * (ui.remote_height - 1)))
            await pause(.15)
        heartbeat_count = 0
        async def echo_ssh():
            nonlocal heartbeat_count
            while True:
                marker = "owned-concurrent-" + str(heartbeat_count)
                process.stdin.write((marker + "\n").encode()); await process.stdin.drain()
                line = await asyncio.wait_for(process.stdout.readline(), 5)
                if line != ("ACK:" + marker + "\n").encode():
                    raise AssertionError("Native SSH heartbeat mismatch")
                heartbeat_count += 1
                await asyncio.sleep(.1)
        async def control(desktop_enabled, ssh_enabled):
            command = dict(id=str(uuid.uuid4()), desktopEnabled=desktop_enabled, sshEnabled=ssh_enabled)
            path = metadata_path.parent / "control.json"
            pending = path.with_suffix(".pending")
            pending.write_text(json.dumps(command)); pending.replace(path)
            await wait(lambda: (path.parent / "control-result.json").exists() and
                json.loads((path.parent / "control-result.json").read_text())["id"] == command["id"])
        try:
            remote = "python3 -u -c " + shlex.quote("import sys; [print('ACK:'+line.strip(), flush=True) for line in sys.stdin]")
            process = await asyncio.create_subprocess_exec("ssh", "-F", str(ssh_root / "config"), "-oBatchMode=yes",
                "-oStrictHostKeyChecking=yes", "-T", "owned-target", remote,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            heartbeat = asyncio.create_task(echo_ssh())
            await wait(lambda: heartbeat_count >= 2 or heartbeat.done())
            if heartbeat.done(): await heartbeat
            ui._connect_relay(); watch(ui.viewer)
            await wait(lambda: len(frames) >= 3 and ui.last_photo is not None and ui.viewer.remote_capabilities & wire.CAPABILITY_INPUT_CONTROL)
            check("same_registered_device_real_desktop_and_native_ssh_live", heartbeat_count >= 3, frames=len(frames), sshPings=heartbeat_count)
            before_clicks = target()["clicks"]
            await click("button")
            await wait(lambda: target()["clicks"] == before_clicks + 1)
            await click("editor")
            marker = "linux-" + uuid.uuid4().hex[:8]
            assert ui.viewer.send_text(marker) == len(marker)
            await wait(lambda: marker in target()["text"])
            check("actual_owned_remote_button_and_text", True)
            auto_text = "owned automatic clipboard " + uuid.uuid4().hex
            root.clipboard_clear(); root.clipboard_append(auto_text); root.update()
            await pause(2)
            await wait(lambda: ui.viewer.clipboard_pending is None)
            assert ui.viewer.request_clipboard(read=True)
            request = ui.viewer.clipboard_pending
            await wait(request.completed.is_set)
            check("real_automatic_clipboard_to_remote_while_ssh_live", request.success and request.text == auto_text)
            remote_text = "owned remote clipboard " + uuid.uuid4().hex
            # A manual clipboard request deliberately resets auto-sync's
            # baseline. First settle that baseline, then simulate an independent
            # copy on the target's owned X server (not another manual request).
            await pause(1.2)
            fixture_environment = dict(part.split(b"=", 1) for part in
                Path(f"/proc/{metadata['processId']}/environ").read_bytes().split(b"\0") if b"=" in part)
            target_environment = dict(os.environ, DISPLAY=metadata["display"],
                                      XAUTHORITY=fixture_environment[b"XAUTHORITY"].decode())
            owned_clipboard = await asyncio.create_subprocess_exec("xclip", "-selection", "clipboard", "-in", "-quiet",
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                env=target_environment)
            owned_clipboard.stdin.write(remote_text.encode()); await owned_clipboard.stdin.drain()
            owned_clipboard.stdin.close()
            await wait(lambda: root.clipboard_get() == remote_text, 8)
            check("real_remote_clipboard_to_local_auto_apply", True)
            ui._toggle_viewer_read_only()
            paused_clicks = target()["clicks"]
            blocked = not ui.viewer.send_input(app.INPUT_MOUSE_DOWN, app.MOUSE_LEFT, 1, 1)
            blocked = blocked and ui.viewer.send_text("MUST-NOT-ARRIVE") == 0
            blocked = blocked and not ui.viewer.request_clipboard(read=False, text="MUST-NOT-ARRIVE")
            root.clipboard_clear(); root.clipboard_append("MUST-NOT-ARRIVE"); root.update()
            await pause(1.8)
            await wait(lambda: ui.viewer.clipboard_pending is None)
            assert ui.viewer.request_clipboard(read=True)
            request = ui.viewer.clipboard_pending
            await wait(request.completed.is_set)
            check("watch_only_blocks_input_manual_and_auto_clipboard_but_ssh_lives", blocked and request.text != "MUST-NOT-ARRIVE"
                  and target()["clicks"] == paused_clicks and "MUST-NOT-ARRIVE" not in target()["text"] and not heartbeat.done())
            ui.viewer.stop_auto_clipboard()
            upload = private / ("linux-concurrent-" + uuid.uuid4().hex[:8] + ".bin")
            upload.write_bytes(bytes(range(256)) * 8192)
            assert ui.viewer.send_files([str(upload)])
            transfer = ui.viewer.transfer_tasks.upload
            await wait(lambda: transfer.terminal)
            received = Path(metadata["receiveDirectory"]) / upload.name
            check("watch_only_explicit_upload_during_desktop_and_ssh", transfer.phase == "已完成" and received.read_bytes() == upload.read_bytes(),
                  bytes=upload.stat().st_size, sha256=hashlib.sha256(upload.read_bytes()).hexdigest())
            destination = private / "returned"
            destination.mkdir()
            assert ui.viewer.transfer_tasks.request_return(destination)[0]
            transfer = ui.viewer.transfer_tasks.download
            await wait(lambda: transfer.terminal)
            archives = list(destination.glob("*.zip"))
            check("confirmed_file_and_directory_return_with_both_sessions_live", transfer.phase == "已完成" and
                  (destination / "return-fixture.txt").read_bytes() == (metadata_path.parent / "return-fixture.txt").read_bytes() and len(archives) == 1)
            with zipfile.ZipFile(archives[0]) as archive:
                check("returned_directory_zip_payload", archive.read("return-folder/nested.txt") ==
                      (metadata_path.parent / "return-folder/nested.txt").read_bytes())
            first_viewer, before_ssh = ui.viewer, heartbeat_count
            ui.disconnect_viewer()
            await wait(lambda: heartbeat_count >= before_ssh + 3)
            check("desktop_disconnect_preserves_native_ssh", not heartbeat.done())
            ui._connect_relay(); watch(ui.viewer)
            before_frames = len(frames)
            await wait(lambda: len(frames) >= before_frames + 3 and ui.viewer.remote_capabilities != 0)
            check("reconnected_desktop_preserves_watch_only_and_native_ssh", ui.viewer_read_only and ui.viewer.read_only and not heartbeat.done())
            if transitions:
                before_ssh = heartbeat_count
                await control(False, True)
                await wait(lambda: heartbeat_count >= before_ssh + 3)
                check("desktop_service_disabled_preserves_existing_ssh", not heartbeat.done())
                await control(True, True)
                ui.disconnect_viewer(); ui._connect_relay(); watch(ui.viewer)
                before_frames = len(frames)
                await wait(lambda: len(frames) >= before_frames + 3)
                await control(True, False)
                await wait(heartbeat.done)
                before_frames = len(frames)
                await wait(lambda: len(frames) >= before_frames + 3)
                check("ssh_service_disabled_closes_only_ssh_desktop_continues", ui.viewer is not None and not ui.viewer.stop_event.is_set())
                await control(True, True)
            else:
                heartbeat.cancel(); await asyncio.gather(heartbeat, return_exceptions=True)
                process.terminate(); await asyncio.wait_for(process.wait(), 5)
                before_frames = len(frames)
                await wait(lambda: len(frames) >= before_frames + 3)
                check("native_ssh_cancel_preserves_desktop", not ui.viewer.stop_event.is_set())
            from PIL import ImageGrab
            ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "concurrent-viewer.png")
            max_gap = max(b - a for a, b in zip(pulses, pulses[1:]))
            check("real_ui_response_and_no_callback_errors", max_gap < .5 and not errors, maxUiGapSeconds=max_gap, uiTicks=len(pulses))
            ui.disconnect_viewer()
            await wait(lambda: not first_viewer.thread.is_alive())
        finally:
            if heartbeat:
                heartbeat.cancel(); await asyncio.gather(heartbeat, return_exceptions=True)
            if process and process.returncode is None:
                process.terminate(); await asyncio.wait_for(process.wait(), 5)
            if owned_clipboard and owned_clipboard.returncode is None:
                owned_clipboard.terminate(); await asyncio.wait_for(owned_clipboard.wait(), 5)
            if ui:
                ui.close()
            ui = root = None
            gc.collect()
    report.update(passed=all(item["passed"] for item in checks), checksTotal=len(checks), privateTempRemoved=True,
                  productionSettingsTouched=False, servicesTransitionsTested=transitions)
    (output / "report.json").write_text(json.dumps(report, indent=2))
    print(f"Owned Linux desktop + SSH: {len(checks)} checks passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--service-transitions", action="store_true", help="Only after other fixture clients confirm they are done")
    arguments = parser.parse_args()
    asyncio.run(run(arguments.metadata.resolve(), arguments.output.resolve(), arguments.service_transitions))
