"""Independent SSH gates, relay streams and native-terminal ownership boundaries."""
from __future__ import annotations

import asyncio
from dataclasses import replace
import os
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_ssh as ssh
import remotedesk_linux_relay as relay
import remotedesk_linux_startup as startup
import test_linux_relay as fixtures

ID = "01234567-89ab-cdef-0123-456789abcdef"
KEY = "test-device-key"
NONCE = bytes(range(32))


class SshGateTests(unittest.IsolatedAsyncioTestCase):
    def writer(self):
        return mock.Mock(drain=mock.AsyncMock(), wait_closed=mock.AsyncMock())

    def test_cross_language_golden_vectors(self):
        self.assertEqual("1f3698cd93105499cee01ff797ef4a2e5b67f88602361194c42e7e818003d145",
                         ssh.gate_proof(KEY, "server", ID, NONCE).hex())
        self.assertEqual("2cd865b69b18ae31b0848a8cf9d76c5ce72a013a2844355f8514bfbeca4da792",
                         ssh.gate_proof(KEY, "client", ID, NONCE).hex())

    def test_noncanonical_identity_empty_key_and_role_are_rejected(self):
        for key, role, identity, nonce in (("", "server", ID, NONCE), (KEY, "bad", ID, NONCE),
                (KEY, "server", ID.upper(), NONCE), (KEY, "client", ID, b"short")):
            with self.subTest(role=role, identity=identity), self.assertRaises(ValueError):
                ssh.gate_proof(key, role, identity, nonce)

    async def test_bad_client_proof_never_connects_local_port(self):
        reader, writer, connect = asyncio.StreamReader(), self.writer(), mock.AsyncMock()
        reader.feed_data(b"bad!" * 8)
        with self.assertRaises(PermissionError):
            await ssh.accept_gate(reader, writer, ID, KEY, 22, connect)
        connect.assert_not_awaited()
        self.assertEqual(b"\0", writer.write.call_args.args[0])

    async def test_authentication_precedes_loopback_only_connect(self):
        reader, writer = asyncio.StreamReader(), self.writer()
        reader.feed_data(ssh.gate_proof(KEY, "client", ID, NONCE))
        local = (asyncio.StreamReader(), self.writer())
        connect = mock.AsyncMock(return_value=local)
        with mock.patch.object(ssh.secrets, "token_bytes", return_value=NONCE):
            self.assertIs(local, await ssh.accept_gate(reader, writer, ID, KEY, 45678, connect))
        connect.assert_awaited_once_with("127.0.0.1", 45678)
        self.assertEqual(104, len(writer.write.call_args_list[0].args[0]))
        self.assertEqual(b"\1", writer.write.call_args_list[1].args[0])

    async def test_authenticated_but_missing_sshd_has_distinct_status(self):
        reader, writer = asyncio.StreamReader(), self.writer()
        reader.feed_data(ssh.gate_proof(KEY, "client", ID, NONCE))
        connect = mock.AsyncMock(side_effect=OSError("synthetic"))
        with mock.patch.object(ssh.secrets, "token_bytes", return_value=NONCE), self.assertRaises(ConnectionError):
            await ssh.accept_gate(reader, writer, ID, KEY, 22, connect)
        self.assertEqual(b"\2", writer.write.call_args.args[0])
        self.assertEqual([mock.call("127.0.0.1", 22), mock.call("::1", 22)], connect.await_args_list)

    async def test_ipv6_only_loopback_fallback_after_authentication(self):
        reader, writer = asyncio.StreamReader(), self.writer()
        reader.feed_data(ssh.gate_proof(KEY, "client", ID, NONCE))
        local = (asyncio.StreamReader(), self.writer())
        connect = mock.AsyncMock(side_effect=[ConnectionRefusedError(), local])
        with mock.patch.object(ssh.secrets, "token_bytes", return_value=NONCE):
            self.assertIs(local, await ssh.accept_gate(reader, writer, ID, KEY, 2222, connect))
        self.assertEqual([mock.call("127.0.0.1", 2222), mock.call("::1", 2222)], connect.await_args_list)
        self.assertEqual(b"\1", writer.write.call_args.args[0])

    async def test_spoofed_server_or_identity_receives_no_client_proof(self):
        for identity, key in ((ID, "wrong-key"), (fixtures.ID, KEY)):
            reader, writer = asyncio.StreamReader(), self.writer()
            reader.feed_data(b"RDS1" + NONCE + identity.encode() + ssh.gate_proof(key, "server", identity, NONCE))
            with self.assertRaises(PermissionError):
                await ssh.authenticate_gate(reader, writer, ID, KEY)
            writer.write.assert_not_called()

    async def test_gate_timeout_includes_slow_local_connect(self):
        reader, writer, cancelled = asyncio.StreamReader(), self.writer(), asyncio.Event()
        reader.feed_data(ssh.gate_proof(KEY, "client", ID, NONCE))
        async def stall(*_):
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        with mock.patch.object(ssh.secrets, "token_bytes", return_value=NONCE), mock.patch.object(ssh, "HOST_DEADLINE", .03):
            with self.assertRaises(TimeoutError):
                await ssh.accept_gate(reader, writer, ID, KEY, 22, stall)
        self.assertTrue(cancelled.is_set())

    async def test_unconfirmed_service_does_not_enter_gate_and_closes_transport(self):
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        writer = self.writer()
        with mock.patch.object(relay, "connect_tls", mock.AsyncMock(return_value=(asyncio.StreamReader(), writer))), \
                mock.patch.object(relay, "hello", mock.AsyncMock(return_value={"ok": True, "type": "paired"})), \
                mock.patch.object(ssh, "authenticate_gate", mock.AsyncMock()) as gate:
            with self.assertRaises(PermissionError):
                await ssh.connect_ssh(options, KEY)
        gate.assert_not_awaited()
        writer.transport.abort.assert_called_once()


class SshNativeArgumentsTests(unittest.TestCase):
    def options(self):
        return relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()

    def test_alias_stable_and_not_ephemeral_port(self):
        options = self.options()
        with mock.patch.object(ssh.shutil, "which", return_value="/usr/bin/ssh"):
            one = ssh.ssh_arguments(options, "testuser", 45678, "/tmp/owned token", "/tmp/owned known hosts")
            two = ssh.ssh_arguments(options, "testuser", 45679, "/tmp/owned token", "/tmp/owned known hosts")
        self.assertEqual([x for x in one if x.startswith("HostKeyAlias=")],
                         [x for x in two if x.startswith("HostKeyAlias=")])
        self.assertIn("StrictHostKeyChecking=ask", one)
        self.assertIn("ClearAllForwardings=yes", one)
        self.assertNotIn(KEY, " ".join(one))
        self.assertNotIn(options.access_token, " ".join(one))
        self.assertNotEqual(ssh.host_key_alias(options), ssh.host_key_alias(replace(options, tls_certificate_sha256="cd" * 32)))

    def test_username_is_an_argument_not_a_command(self):
        with mock.patch.object(ssh.shutil, "which", return_value="/usr/bin/ssh"):
            for username in ("", "-oProxyCommand=bad", "user name", "user\nname", "user;command", 'user"name'):
                with self.assertRaises(ValueError):
                    ssh.ssh_arguments(self.options(), username, 1234, "/tmp/a", "/tmp/b")
            args = ssh.ssh_arguments(self.options(), "domain\\user", 1234, "/tmp/a", "/tmp/b")
            self.assertEqual(["-l", "domain\\user", "--", "remotedesk.invalid"], args[-4:])

    @unittest.skipUnless(sys.platform.startswith("linux"), "native OpenSSH option parser")
    def test_real_ssh_parser_preserves_space_paths_and_disables_escape_shell(self):
        args = ssh.ssh_arguments(self.options(), "owned", 12345, "/tmp/owned %token", "/tmp/owned space/known hosts")
        result = subprocess.run([args[0], "-G", *args[1:]], capture_output=True, text=True, check=True)
        self.assertIn("userknownhostsfile /tmp/owned space/known hosts\n", result.stdout)
        self.assertIn("escapechar none\n", result.stdout)
        self.assertIn("forwardx11 no\n", result.stdout)
        self.assertIn("%%token", result.stdout)

    def test_missing_native_terminal_is_actionable_not_fake_terminal(self):
        with mock.patch.object(ssh.shutil, "which", return_value=None), self.assertRaises(FileNotFoundError):
            ssh.terminal_arguments(["ssh"])

    def test_native_known_hosts_path_rejects_environment_expansion_and_controls(self):
        with mock.patch.object(ssh.shutil, "which", return_value="/usr/bin/ssh"):
            for path in ("/tmp/${REMOTEDESK_OWNED_ALT}/known_hosts", "/tmp/known\nhosts", "/tmp/known\0hosts"):
                with self.subTest(path=path), self.assertRaises(ssh.AliasConfigurationError):
                    ssh.ssh_arguments(self.options(), "owned", 12345, "/tmp/token", path)
            arguments = ssh.ssh_arguments(self.options(), "owned", 12345, "/tmp/token", "/tmp/literal %h with spaces/known_hosts")
            self.assertIn('UserKnownHostsFile="/tmp/literal %%h with spaces/known_hosts"', arguments)

    def test_native_terminal_validates_actual_records_directory_before_worker_start(self):
        session = ssh.TerminalSession(self.options(), KEY, "owned",
                                      config_directory="/tmp/${REMOTEDESK_OWNED_ALT}")
        with mock.patch.object(ssh.shutil, "which", return_value="/usr/bin/ssh"), \
                mock.patch.object(ssh.threading, "Thread") as worker, \
                mock.patch.object(ssh, "terminal_arguments") as terminal:
            with self.assertRaises(ssh.AliasConfigurationError):
                session.start()
        worker.assert_not_called()
        terminal.assert_not_called()
        self.assertIsNone(session.thread)

    def test_selected_private_key_is_a_literal_argument_and_disables_agent_identity_mix(self):
        arguments = ssh.ssh_arguments(self.options(), "owned", 12345, "/tmp/token", "/tmp/records",
                                      identity_file="/tmp/key with space;literal")
        self.assertEqual("/tmp/key with space;literal", arguments[arguments.index("-i") + 1])
        self.assertIn("IdentitiesOnly=yes", arguments)
        self.assertIn("IdentityAgent=none", arguments)

    @unittest.skipUnless(sys.platform.startswith("linux"), "private Unix profiles")
    def test_username_profiles_are_scoped_to_pin_uuid_and_bounded_to_fifty(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "private"
            options = self.options()
            self.assertEqual("", ssh.load_username(options, folder))
            for index in range(51):
                target = replace(options, device_id=str(__import__("uuid").UUID(int=index + 1)))
                ssh.save_username(target, f"user{index}", folder)
            self.assertEqual("", ssh.load_username(replace(options, device_id=str(__import__("uuid").UUID(int=1))), folder))
            self.assertEqual("user50", ssh.load_username(target, folder))
            self.assertEqual("", ssh.load_username(replace(target, tls_certificate_sha256="cd" * 32), folder))
            value = json.loads((folder / "logins.json").read_text())
            self.assertEqual(50, len(value["logins"]))
            self.assertEqual(0o600, (folder / "logins.json").stat().st_mode & 0o777)
            self.assertNotIn(options.access_token, (folder / "logins.json").read_text())

    @unittest.skipUnless(sys.platform.startswith("linux"), "private Unix profiles")
    def test_corrupt_or_unsafe_username_profiles_are_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / "private"
            ssh.save_username(self.options(), "owned", folder)
            records = folder / "logins.json"
            records.write_text("bad-json")
            with self.assertRaises(ValueError):
                ssh.save_username(self.options(), "other", folder)
            self.assertEqual("bad-json", records.read_text())
            records.chmod(0o644)
            with self.assertRaises(PermissionError):
                ssh.load_username(self.options(), folder)

    @unittest.skipUnless(sys.platform.startswith("linux"), "private Unix state")
    def test_ssh_enable_persists_only_with_remembered_device_key(self):
        with tempfile.TemporaryDirectory() as directory:
            store = startup.HostPreferences(Path(directory) / "private")
            for remember in (False, True):
                store.save(dict(remember=remember, armed=False, ssh_enabled=True, ssh_port="2222"), KEY)
                value = store.load()
                self.assertEqual(remember, value["ssh_enabled"])
                self.assertFalse(value["armed"])
                self.assertEqual("2222", value["ssh_port"])


class SshRelayIntegrationTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp = fixtures.LinuxRelayTlsTests.asyncSetUp
    asyncTearDown = fixtures.LinuxRelayTlsTests.asyncTearDown
    wait_for = fixtures.LinuxRelayTlsTests.wait_for

    async def start_ssh_host(self, desktop=True):
        self.local_connections = 0
        self.local_tasks = set()
        async def echo(reader, writer):
            task = asyncio.current_task()
            self.local_tasks.add(task)
            self.local_connections += 1
            try:
                while data := await reader.read(65536):
                    writer.write(data)
                    await writer.drain()
            finally:
                await relay.close_writer(writer, abort=True)
                self.local_tasks.discard(task)
        listener = await asyncio.start_server(echo, "127.0.0.1", 0)
        self.extra_servers.append(listener)
        self.ssh_port = listener.sockets[0].getsockname()[1]
        self.host = relay.RelayHostConnector(self.options, self.ssh_port, "Owned SSH target",
            ssh_enabled=True, ssh_port=self.ssh_port, device_key=KEY, desktop_enabled=desktop)
        self.host.start()
        await self.wait_for(self.host.online.is_set)

    async def exchange(self, connection, value=b"independent-native-ssh-bytes\0\xff"):
        reader, writer = connection
        writer.write(value)
        await writer.drain()
        self.assertEqual(value, await asyncio.wait_for(reader.readexactly(len(value)), 2))

    async def test_pure_ssh_registration_and_no_desktop_fallback(self):
        await self.start_ssh_host(desktop=False)
        devices = await relay.list_devices_async(self.options)
        self.assertEqual(["ssh"], devices[0]["services"])
        self.assertTrue(devices[0]["sshTunneling"])
        self.assertEqual([], devices[0]["directAddresses"])
        self.assertEqual(0, devices[0]["directPort"])
        connection = await ssh.connect_ssh(self.options, KEY)
        try:
            await self.exchange(connection)
            with self.assertRaises((ConnectionError, OSError)):
                await asyncio.to_thread(relay.connect_viewer, self.options)
        finally:
            await relay.close_writer(connection[1], abort=True)

    async def test_wrong_device_key_never_reaches_sshd(self):
        await self.start_ssh_host()
        with self.assertRaises(PermissionError):
            await ssh.connect_ssh(self.options, "wrong-key")
        self.assertEqual(0, self.local_connections)

    async def test_four_ssh_sessions_do_not_take_over_busy_desktop(self):
        await self.start_ssh_host()
        desktop = await asyncio.to_thread(relay.connect_viewer, self.options)
        connections = []
        try:
            for _ in range(4):
                connections.append(await ssh.connect_ssh(self.options, KEY))
            with self.assertRaises((ConnectionError, OSError)):
                await ssh.connect_ssh(self.options, KEY)
            for connection in connections:
                await self.exchange(connection)
            def roundtrip():
                desktop.sendall(b"desktop-still-alive")
                return desktop.recv(19)
            self.assertEqual(b"desktop-still-alive", await asyncio.to_thread(roundtrip))
        finally:
            desktop.close()
            for _reader, writer in connections:
                await relay.close_writer(writer, abort=True)

    async def test_disabling_ssh_immediately_closes_bridges_not_desktop(self):
        await self.start_ssh_host()
        desktop = await asyncio.to_thread(relay.connect_viewer, self.options)
        connection = await ssh.connect_ssh(self.options, KEY)
        original = self.relay.hosts[self.options.device_id]
        try:
            self.host.configure_services(desktop_enabled=True, ssh_enabled=False, ssh_port=self.ssh_port, device_key=KEY)
            self.assertEqual(b"", await asyncio.wait_for(connection[0].read(1), 1))
            await self.wait_for(lambda: set(self.relay.hosts[self.options.device_id].services) == {"desktop"})
            self.assertIs(original, self.relay.hosts[self.options.device_id])
            def roundtrip():
                desktop.sendall(b"live")
                return desktop.recv(4)
            self.assertEqual(b"live", await asyncio.to_thread(roundtrip))
        finally:
            desktop.close()
            await relay.close_writer(connection[1], abort=True)

    async def test_desktop_stop_and_start_keep_same_live_ssh_registration(self):
        await self.start_ssh_host()
        connection = await ssh.connect_ssh(self.options, KEY)
        original = self.relay.hosts[self.options.device_id]
        try:
            for desktop in (False, True):
                self.host.configure_services(desktop_enabled=desktop, ssh_enabled=True, ssh_port=self.ssh_port, device_key=KEY)
                await self.wait_for(lambda: ("desktop" in self.relay.hosts[self.options.device_id].services) == desktop)
                self.assertIs(original, self.relay.hosts[self.options.device_id])
                await self.exchange(connection)
        finally:
            await relay.close_writer(connection[1], abort=True)

    async def test_key_change_revokes_existing_sessions_and_old_key(self):
        await self.start_ssh_host()
        old = await ssh.connect_ssh(self.options, KEY)
        try:
            self.host.configure_services(desktop_enabled=True, ssh_enabled=True, ssh_port=self.ssh_port, device_key="new-key")
            self.assertEqual(b"", await asyncio.wait_for(old[0].read(1), 1))
            with self.assertRaises(PermissionError):
                await ssh.connect_ssh(self.options, KEY)
            new = await ssh.connect_ssh(self.options, "new-key")
            try:
                await self.exchange(new)
            finally:
                await relay.close_writer(new[1], abort=True)
        finally:
            await relay.close_writer(old[1], abort=True)

    async def test_cancelling_pending_gate_frees_capacity_for_next_session(self):
        await self.start_ssh_host()
        reader, writer = await relay.connect_tls(self.options)
        await relay.hello(reader, writer, self.options, "ssh-viewer", deviceId=self.options.device_id)
        self.assertEqual(b"RDS1", await reader.readexactly(4))
        await relay.close_writer(writer, abort=True)
        await self.wait_for(lambda: not self.host.ssh_tasks)
        self.assertEqual(0, self.local_connections)
        connection = await ssh.connect_ssh(self.options, KEY)
        try:
            await self.exchange(connection)
        finally:
            await relay.close_writer(connection[1], abort=True)


@unittest.skipUnless(sys.platform.startswith("linux"), "owned Unix subprocess")
class SshTerminalLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_alias_gate_verification_success_closes_owned_bridge_and_releases_key(self):
        import threading
        completed = []
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        verification = ssh.GateVerification(options, KEY, completed.append)
        writer = mock.Mock()
        with mock.patch.object(ssh, "connect_ssh", new=mock.AsyncMock(return_value=(mock.Mock(), writer))), \
                mock.patch.object(relay, "close_writer", new=mock.AsyncMock()) as close:
            verification.start()
            await asyncio.wait_for(asyncio.to_thread(verification.thread.join), 2)
        self.assertEqual([True], completed)
        self.assertEqual("", verification.device_key)
        close.assert_awaited_once_with(writer, abort=True)

    async def test_alias_gate_verification_cancel_interrupts_pending_connection(self):
        import threading
        entered, completed = threading.Event(), []
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        async def gate(*args):
            entered.set()
            await asyncio.Event().wait()
        verification = ssh.GateVerification(options, KEY, completed.append)
        with mock.patch.object(ssh, "connect_ssh", side_effect=gate):
            verification.start()
            self.assertTrue(await asyncio.to_thread(entered.wait, 1))
            verification.close()
            await asyncio.wait_for(asyncio.to_thread(verification.thread.join), 2)
        self.assertEqual([False], completed)
        self.assertEqual("", verification.device_key)

    async def test_selected_percent_key_uses_owned_symlink_not_openssh_expansion_and_is_removed(self):
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        aliases = []
        original = ssh.ssh_arguments
        def arguments(options, username, port, token_path, known_hosts, identity_file=None):
            aliases.append(Path(identity_file))
            self.assertTrue(identity_file.is_symlink())
            self.assertEqual(identity, identity_file.resolve())
            self.assertNotIn("%", str(identity_file))
            return original(options, username, port, token_path, known_hosts, identity_file)
        with tempfile.TemporaryDirectory() as directory:
            identity = Path(directory) / "owned %h key"
            identity.write_bytes(b"synthetic-private-key-not-parsed")
            session = ssh.TerminalSession(options, KEY, "owned", config_directory=Path(directory) / "private", identity_file=identity)
            with mock.patch.object(ssh, "terminal_arguments", return_value=["/bin/true"]), \
                    mock.patch.object(ssh, "ssh_arguments", side_effect=arguments):
                await asyncio.wait_for(session._run(), 2)
            self.assertEqual(b"synthetic-private-key-not-parsed", identity.read_bytes())
            self.assertEqual(1, len(aliases))
            self.assertFalse(aliases[0].exists())

    async def test_cancel_after_local_adapter_connect_closes_handler_before_listener_wait(self):
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        adapter, gate_entered = {}, asyncio.Event()
        original = ssh.ssh_arguments
        def arguments(options, username, port, token_path, known_hosts, identity_file=None):
            adapter.update(port=port, token=token_path.read_bytes())
            return original(options, username, port, token_path, known_hosts, identity_file)
        async def gate(*_):
            gate_entered.set()
            await asyncio.Event().wait()
        with tempfile.TemporaryDirectory() as directory:
            session = ssh.TerminalSession(options, KEY, "owned", config_directory=Path(directory) / "private")
            with mock.patch.object(ssh, "terminal_arguments", return_value=["/bin/sleep", "60"]), \
                    mock.patch.object(ssh, "ssh_arguments", side_effect=arguments), \
                    mock.patch.object(ssh, "connect_ssh", side_effect=gate):
                task = asyncio.create_task(session._run())
                while not adapter:
                    await asyncio.sleep(.005)
                reader, writer = await asyncio.open_connection("127.0.0.1", adapter["port"])
                writer.write(adapter["token"])
                await writer.drain()
                await asyncio.wait_for(gate_entered.wait(), 1)
                session.close()
                with self.assertRaises(asyncio.CancelledError):
                    await asyncio.wait_for(task, 2)
                self.assertEqual(b"", await asyncio.wait_for(reader.read(1), 1))
                writer.close()
                await writer.wait_closed()
                self.assertIsNotNone(session.process.poll())

    async def test_terminal_exiting_before_proxy_does_not_leave_listener_for_30_seconds(self):
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        with tempfile.TemporaryDirectory() as directory:
            session = ssh.TerminalSession(options, KEY, "owned", config_directory=Path(directory) / "private")
            with mock.patch.object(ssh, "terminal_arguments", return_value=["/bin/true"]):
                await asyncio.wait_for(session._run(), 2)
            self.assertEqual(0, session.process.returncode)

    async def test_session_cancel_reaps_only_its_owned_process(self):
        options = relay.RelayOptions("127.0.0.1", 12345, fixtures.TOKEN, "ab" * 32, ID).validate()
        with tempfile.TemporaryDirectory() as directory:
            session = ssh.TerminalSession(options, KEY, "owned", config_directory=Path(directory) / "private")
            with mock.patch.object(ssh, "terminal_arguments", return_value=["/bin/sleep", "60"]):
                task = asyncio.create_task(session._run())
                while session.process is None:
                    await asyncio.sleep(.005)
                session.close()
                with self.assertRaises(asyncio.CancelledError):
                    await asyncio.wait_for(task, 2)
            self.assertIsNotNone(session.process.poll())


if __name__ == "__main__":
    unittest.main()
