"""Real native SSH alias/stdio/forwarding against only an owned fixture."""
import argparse
import asyncio
from dataclasses import replace
import hashlib
import json
import os
from pathlib import Path
import shlex
import socket
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
import remotedesk_linux_relay as relay
import remotedesk_linux_ssh as ssh


async def run(metadata_path, output, gui_verification=False):
    metadata = json.loads(metadata_path.read_text())
    assert metadata.get("owned") is True and metadata.get("allowForwarding") is True
    output.mkdir(parents=True, exist_ok=True)
    checks = []
    processes = []
    original_xdg = os.environ.get("XDG_CONFIG_HOME")
    def check(name, condition, **details):
        checks.append(dict(name=name, passed=bool(condition), **details))
        (output / "report.json").write_text(json.dumps(dict(owned=True, checks=checks), indent=2))
        if not condition:
            raise AssertionError(name)
    with tempfile.TemporaryDirectory(prefix="owned-native-ssh-alias-") as temporary:
        private = Path(temporary)
        os.environ["XDG_CONFIG_HOME"] = str(private / "config %h $d with spaces")
        options = relay.RelayOptions(metadata["serverAddress"], metadata["port"], metadata["token"],
            metadata["tlsCertificateSha256"], metadata["deviceId"]).validate()
        relay.save_settings(options)
        ssh_root = private / "ssh with spaces"
        selected_key = private / "identity %h %d %% literal with spaces"
        selected_key.write_bytes(Path(metadata["linuxKeyPath"]).read_bytes())
        selected_key.chmod(0o600)
        configured = ssh.configure_terminal_alias("5090", metadata["sshUsername"], options,
            metadata["deviceKey"], ssh_directory=ssh_root, identity_file=selected_key)
        if gui_verification:
            if os.environ.get("REMOTEDESK_ISOLATED_XVFB") != "1" or os.environ.get("WAYLAND_DISPLAY"):
                raise RuntimeError("GUI probe requires explicitly owned Xvfb with Wayland unset")
            import gc
            from unittest import mock
            import remotedesk_linux_app as app
            root = app.tk.Tk()
            errors, pulses = [], []
            root.report_callback_exception = lambda *args: errors.append(str(args))
            configure = ssh.configure_terminal_alias
            def configure_owned(*args, **kwargs):
                return configure(*args, **kwargs, ssh_directory=ssh_root)
            with mock.patch.object(app.DevicePanel, "scan"), mock.patch.object(app.RemoteDeskLinuxApp, "_refresh_relay"):
                ui = app.RemoteDeskLinuxApp(root)
                ui.relay_options = options
                ui._show_relay_directory([dict(deviceId=options.device_id, machineName="Owned alias fixture", platform="Linux",
                    busy=False, sshTunneling=True, services=["ssh"], directAddresses=[], directPort=0)])
                ui.relay_list.selection_set(options.device_id)
                ui.relay_password.set(metadata["deviceKey"])
                async def settle():
                    deadline = time.monotonic() + 25
                    while ui.ssh_alias_pending is not None and time.monotonic() < deadline:
                        root.update(); pulses.append(time.monotonic()); await asyncio.sleep(.01)
                    root.update()
                    assert ui.ssh_alias_pending is None
                try:
                    with mock.patch.object(ui, "_ask_terminal_alias", return_value="5090"), \
                            mock.patch.object(ui, "_ask_ssh_login", return_value=(metadata["sshUsername"], str(selected_key))), \
                            mock.patch.object(app.messagebox, "askokcancel", return_value=True), \
                            mock.patch.object(app.messagebox, "showerror") as error, \
                            mock.patch.object(ssh, "configure_terminal_alias", side_effect=configure_owned) as saved:
                        ui._configure_terminal_ssh_alias()
                        check("GUI_configuration_not_written_before_real_gate_completes", saved.call_count == 0 and ui.ssh_alias_pending is not None)
                        await settle()
                        check("GUI_real_TLS_gate_verified_then_saved_responsive", saved.call_count == 1 and error.call_count == 0 and len(pulses) >= 1,
                              eventLoopPulses=len(pulses), configurationWrites=saved.call_count, errorDialogs=error.call_count)
                        snapshot = (ssh.ssh_config_directory() / "aliases.json").read_bytes()
                        ui.relay_password.set("owned-deliberately-wrong-device-key")
                        ui._configure_terminal_ssh_alias()
                        await settle()
                        check("GUI_wrong_key_real_gate_preserves_previous_alias_and_key", saved.call_count == 1 and error.call_count == 1 and
                            (ssh.ssh_config_directory() / "aliases.json").read_bytes() == snapshot and relay.load_device_key(options) == metadata["deviceKey"])
                        from PIL import ImageGrab
                        ImageGrab.grab(xdisplay=os.environ["DISPLAY"]).save(output / "alias-gui-verified.png")
                    check("GUI_no_callback_errors", not errors)
                finally:
                    ui.close()
                    del ui, root
                    gc.collect()
        known = ssh.ssh_config_directory() / "known_hosts"
        known.write_text(metadata["hostKeyAlias"] + " " + metadata["sshHostKeyPublic"] + "\n")
        # The helper must derive its private state from the generated command,
        # not a running GUI or the test runner's inherited XDG environment.
        native_env = dict(os.environ, XDG_CONFIG_HOME=str(private / "unrelated-environment"), DISPLAY="")
        base = ["-F", str(ssh_root / "config"),
                "-oBatchMode=yes", "-oStrictHostKeyChecking=yes", "-oConnectTimeout=8"]
        async def command(executable, args, data=None):
            process = await asyncio.create_subprocess_exec(executable, *args, env=native_env,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            processes.append(process)
            stdout, stderr = await asyncio.wait_for(process.communicate(data), 30)
            if process.returncode:
                (output / "native-error.log").write_bytes(stderr)
            return process.returncode, stdout, stderr
        try:
            code, stdout, _ = await command("ssh", [*base, "-T", "5090", "printf native-alias-ready"])
            check("standalone_no_gui_native_T_and_percent_space_profile_path", code == 0 and stdout == b"native-alias-ready")
            check("selected_IdentityFile_with_literal_percent_tokens_without_i", code == 0 and b"PRIVATE KEY" not in (ssh_root / "remotedesk_aliases.conf").read_bytes())
            payload = bytes(range(256)) * 16384
            remote = "python3 -c " + shlex.quote("import sys; data=sys.stdin.buffer.read(); sys.stdout.buffer.write(data); sys.stdout.buffer.flush()")
            code, stdout, stderr = await command("ssh", [*base, "-T", "5090", remote], payload)
            check("bidirectional_4MiB_binary_stdin_stdout_without_proxy_logging", code == 0 and stdout == payload,
                  bytes=len(stdout), sha256=hashlib.sha256(stdout).hexdigest())
            code, stdout, _ = await command("ssh", [*base, "-tt", "5090", "tty; printf '\\344\\270\\255\\346\\226\\207-PTY'"])
            check("native_PTY_unicode", code == 0 and b"/dev/pts/" in stdout and "中文-PTY".encode() in stdout)
            async def echo(reader, writer):
                try:
                    while block := await reader.read(16384):
                        writer.write(block); await writer.drain()
                finally:
                    writer.close(); await writer.wait_closed()
            server = await asyncio.start_server(echo, "127.0.0.1", 0)
            echo_port = server.sockets[0].getsockname()[1]
            try:
                for dynamic in (False, True):
                    with socket.socket() as reserved:
                        reserved.bind(("127.0.0.1", 0)); local_port = reserved.getsockname()[1]
                    flag = ["-D", f"127.0.0.1:{local_port}"] if dynamic else ["-L", f"127.0.0.1:{local_port}:127.0.0.1:{echo_port}"]
                    process = await asyncio.create_subprocess_exec("ssh", *base, "-oExitOnForwardFailure=yes", "-N", *flag, "5090",
                        env=native_env, stdin=asyncio.subprocess.DEVNULL, stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.PIPE)
                    processes.append(process)
                    for _ in range(200):
                        if process.returncode is not None:
                            raise AssertionError((await process.stderr.read()).decode())
                        try:
                            reader, writer = await asyncio.open_connection("127.0.0.1", local_port)
                            break
                        except OSError:
                            await asyncio.sleep(.025)
                    else:
                        raise TimeoutError("Owned local forwarding listener")
                    try:
                        if dynamic:
                            writer.write(b"\x05\x01\x00"); await writer.drain()
                            assert await asyncio.wait_for(reader.readexactly(2), 5) == b"\x05\x00"
                            writer.write(b"\x05\x01\x00\x01\x7f\x00\x00\x01" + echo_port.to_bytes(2, "big")); await writer.drain()
                            assert (await asyncio.wait_for(reader.readexactly(10), 5))[:2] == b"\x05\x00"
                        expected = (b"owned-SOCKS" if dynamic else b"owned-local-forward") * 4000
                        writer.write(expected); await writer.drain()
                        received = await asyncio.wait_for(reader.readexactly(len(expected)), 5)
                        check("native_D_SOCKS_echo" if dynamic else "native_L_echo", received == expected, bytes=len(received))
                    finally:
                        writer.close(); await writer.wait_closed()
                        process.terminate(); await asyncio.wait_for(process.wait(), 5)
                    check("D_cancel_reaps_native_client" if dynamic else "L_cancel_reaps_native_client", process.returncode is not None)
            finally:
                server.close(); await server.wait_closed()
            upload, remote_file, download = (private / name for name in ("upload", "remote-file", "download"))
            upload.write_bytes(bytes(range(256)) * 33)
            batch = f'put "{upload}" "{remote_file}"\nget "{remote_file}" "{download}"\nquit\n'.encode()
            code, _, _ = await command("sftp", [*base, "-b", "-", "5090"], batch)
            check("standard_sftp_put_get", code == 0 and upload.read_bytes() == download.read_bytes(), bytes=download.stat().st_size)
            code, _, _ = await command("scp", [*base, str(upload), "5090:" + str(private / "scp-file")])
            check("standard_scp", code == 0 and (private / "scp-file").read_bytes() == upload.read_bytes())
            relay.save_settings(None)
            code, stdout, stderr = await command("ssh", [*base, "-T", "5090", "printf must-not-arrive"])
            check("logout_fails_closed_no_stdout", code != 0 and stdout == b"" and "退出中继".encode() in stderr)
            relay.save_settings(replace(options, tls_certificate_sha256="a1" * 32))
            code, stdout, _ = await command("ssh", [*base, "-T", "5090", "printf must-not-arrive"])
            check("changed_relay_pin_fails_closed_no_stdout", code != 0 and stdout == b"")
            relay.save_settings(options)
            ssh.configure_terminal_alias("5090", "", remove=True, ssh_directory=ssh_root)
            code, stdout, _ = await command(sys.executable, [ssh.__file__, "--stdio", configured["profileId"]])
            check("removed_profile_cannot_connect", code != 0 and stdout == b"")
        finally:
            for process in processes:
                if process.returncode is None:
                    process.terminate()
                    await asyncio.wait_for(process.wait(), 5)
            if original_xdg is None:
                os.environ.pop("XDG_CONFIG_HOME", None)
            else:
                os.environ["XDG_CONFIG_HOME"] = original_xdg
    (output / "report.json").write_text(json.dumps(dict(owned=True, checks=checks, passed=all(item["passed"] for item in checks),
        checksTotal=len(checks), privateTempRemoved=True, productionConfigTouched=False), indent=2))
    print(f"Owned native alias probe: {len(checks)} checks passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--gui-verification", action="store_true")
    arguments = parser.parse_args()
    asyncio.run(run(arguments.metadata.resolve(), arguments.output.resolve(), arguments.gui_verification))
