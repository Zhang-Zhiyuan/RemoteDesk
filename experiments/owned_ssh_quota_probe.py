"""Four real native SSH sessions plus a live desktop on one owned relay device."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import queue
import shlex
import sys
import tempfile
import time
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_app as app
import remotedesk_linux_ssh as ssh
import remotedesk_linux_relay as relay


async def run(metadata_path, output):
    metadata = json.loads(metadata_path.read_text())
    if metadata.get("owned") is not True or metadata.get("desktopFixture") is not True:
        raise RuntimeError("Only owned shared desktop/SSH fixture allowed")
    output.mkdir(parents=True, exist_ok=True)
    checks, processes, frames = [], [], []
    options = relay.RelayOptions(metadata["serverAddress"], metadata["port"], metadata["token"],
        metadata["tlsCertificateSha256"], metadata["deviceId"]).validate()
    def check(name, passed, **details):
        checks.append(dict(name=name, passed=bool(passed), **details))
        if not passed: raise AssertionError(name)
    events = queue.Queue()
    viewer = app.ViewerConnection("127.0.0.1", metadata["desktopPort"], metadata["deviceKey"], events, 1,
                                  relay_options=options, allow_self_connection_for_testing=True)
    viewer.stop_auto_clipboard()
    original_event = viewer._put_event
    def event(kind, value):
        if kind == "viewer_frame": frames.append(time.monotonic())
        original_event(kind, value)
    viewer._put_event = event
    async def wait(predicate, seconds=8):
        deadline = time.monotonic() + seconds
        while not predicate() and time.monotonic() < deadline:
            while not events.empty(): events.get_nowait()
            await asyncio.sleep(.025)
        if not predicate(): raise TimeoutError("Owned quota condition")
    with tempfile.TemporaryDirectory(prefix="owned-native-ssh-quota-") as temporary, \
            mock.patch.dict(os.environ, {"XDG_CONFIG_HOME": str(Path(temporary) / "config")}):
        relay.save_settings(options)
        directory = Path(temporary) / "ssh"
        ssh.configure_terminal_alias("owned-quota", metadata["sshUsername"], options, metadata["deviceKey"],
            identity_file=metadata["linuxKeyPath"], ssh_directory=directory)
        (ssh.ssh_config_directory() / "known_hosts").write_text(ssh.host_key_alias(options) + " " + metadata["sshHostKeyPublic"] + "\n")
        async def launch():
            command = "python3 -u -c " + shlex.quote("import sys; print('READY', flush=True); [print(line.strip(), flush=True) for line in sys.stdin]")
            child = await asyncio.create_subprocess_exec("ssh", "-F", str(directory / "config"), "-oBatchMode=yes",
                "-oStrictHostKeyChecking=yes", "-T", "owned-quota", command,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            processes.append(child)
            return child
        try:
            viewer.start(); await wait(lambda: len(frames) >= 2)
            for index in range(4):
                child = await launch()
                check("native_ssh_session_" + str(index + 1), await asyncio.wait_for(child.stdout.readline(), 8) == b"READY\n")
            before = len(frames)
            fifth = await launch()
            stdout, _stderr = await asyncio.wait_for(fifth.communicate(), 8)
            check("fifth_ssh_rejected_without_native_stdout", fifth.returncode != 0 and stdout == b"")
            for index, child in enumerate(processes[:4]):
                marker = ("owned-slot-" + str(index) + "\n").encode()
                child.stdin.write(marker); await child.stdin.drain()
                check("existing_slot_" + str(index + 1) + "_survives_rejection", await asyncio.wait_for(child.stdout.readline(), 5) == marker)
            await wait(lambda: len(frames) >= before + 2)
            check("desktop_continues_through_full_ssh_quota_and_rejection", not viewer.stop_event.is_set(), frames=len(frames))
            processes[0].terminate(); await asyncio.wait_for(processes[0].wait(), 5)
            await asyncio.sleep(.25)
            replacement = await launch()
            check("closed_slot_is_reclaimed_for_new_authenticated_ssh", await asyncio.wait_for(replacement.stdout.readline(), 8) == b"READY\n")
        finally:
            for child in processes:
                if child.returncode is None:
                    child.terminate(); await asyncio.wait_for(child.wait(), 5)
            await asyncio.to_thread(viewer.close)
    report = dict(owned=True, physicalHardware=False, checks=checks, checksTotal=len(checks), passed=all(item["passed"] for item in checks),
                  nativeSshClientsReaped=all(child.returncode is not None for child in processes), desktopViewerStopped=not viewer.thread.is_alive(),
                  productionConfigTouched=False, privateTempRemoved=True)
    (output / "report.json").write_text(json.dumps(report, indent=2))
    print(f"Owned real native SSH quota: {len(checks)} checks passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(run(args.metadata.resolve(), args.output.resolve()))
