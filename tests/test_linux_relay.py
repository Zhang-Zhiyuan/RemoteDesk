from __future__ import annotations

import asyncio
from dataclasses import replace
import datetime
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import sys
import tempfile
import threading
import unittest
import uuid
from unittest import mock

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
sys.path.insert(0, str(ROOT / "scripts/relay"))
import remotedesk_linux_relay as client
import remotedesk_relay_server as server

TOKEN = "linux-relay-regression-token-" * 2
ID = "8220b49b-0f2f-4f87-913a-c3f95091500f"


class RelayOptionsTests(unittest.TestCase):
    def options(self):
        return client.RelayOptions(" host.test ", 56567, TOKEN, "ab" * 32, ID)

    def test_round_trip_normalizes_and_redacts(self):
        options = self.options().validate()
        self.assertEqual("host.test", options.server_address)
        self.assertEqual("AB" * 32, options.tls_certificate_sha256)
        self.assertEqual(options, client.RelayOptions.from_dict(options.to_dict()))
        self.assertNotIn(TOKEN, repr(options))

    def test_rejects_invalid_configuration(self):
        for changes in (dict(server_address="https://host"), dict(port=0), dict(port=True),
                        dict(port=65536), dict(access_token="short"),
                        dict(tls_certificate_sha256="invalid"), dict(device_id="bad")):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                replace(self.options(), **changes).validate()
        for value in ([], None, dict(self.options().to_dict(), port=True),
                      dict(self.options().to_dict(), port=1.5)):
            with self.assertRaises(ValueError):
                client.RelayOptions.from_dict(value)

    def test_atomic_settings_and_private_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings" / "relay.json"
            self.assertIsNone(client.load_settings(path))
            client.save_settings(self.options(), path)
            self.assertEqual(self.options().validate(), client.load_settings(path))
            self.assertEqual([path], list(path.parent.iterdir()))
            if os.name != "nt":
                self.assertEqual(0o600, path.stat().st_mode & 0o777)
            path.write_text("[]")
            with self.assertRaises(ValueError): client.load_settings(path)


class LinuxRelayTransportPolicyTests(unittest.TestCase):
    def test_limits_unsent_data_without_shrinking_tcp_windows(self):
        writer = mock.Mock()
        client.configure_transport(writer)
        writer.transport.set_write_buffer_limits.assert_called_once_with(high=16384, low=4096)
        sock = writer.get_extra_info.return_value
        sock.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        if hasattr(socket, "TCP_NOTSENT_LOWAT"):
            sock.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_NOTSENT_LOWAT, 16384)
        self.assertTrue(all(call.args[0] != socket.SOL_SOCKET for call in sock.setsockopt.call_args_list))

    def test_unsupported_socket_options_keep_working_transport(self):
        writer = mock.Mock()
        writer.get_extra_info.return_value.setsockopt.side_effect = OSError("unsupported kernel option")
        client.configure_transport(writer)
        writer.transport.set_write_buffer_limits.assert_called_once()

    def test_transport_without_socket_still_bounds_plaintext(self):
        writer = mock.Mock()
        writer.get_extra_info.return_value = None
        client.configure_transport(writer)
        writer.transport.set_write_buffer_limits.assert_called_once()

    def test_only_explicit_loopback_policy_sets_small_fixed_windows(self):
        sock = mock.Mock()
        client.configure_loopback_socket(sock)
        sock.setsockopt.assert_any_call(socket.SOL_SOCKET, socket.SO_RCVBUF, 16384)
        sock.setsockopt.assert_any_call(socket.SOL_SOCKET, socket.SO_SNDBUF, 16384)
        sock.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.assertEqual(16384, client.LOOPBACK_READER_LIMIT_BYTES)

    def test_loopback_unsupported_options_are_best_effort(self):
        sock = mock.Mock()
        sock.setsockopt.side_effect = OSError("Unsupported")
        client.configure_loopback_socket(sock)
        self.assertEqual(3, sock.setsockopt.call_count)


class LinuxRelayCloseTests(unittest.IsolatedAsyncioTestCase):
    async def test_successful_close_does_not_abort(self):
        writer = mock.Mock(wait_closed=mock.AsyncMock())
        await client.close_writer(writer)
        writer.close.assert_called_once()
        writer.transport.abort.assert_not_called()
        await client.close_writer(None)

    async def test_failed_close_aborts_socket(self):
        writer = mock.Mock(wait_closed=mock.AsyncMock(side_effect=OSError("dead peer")))
        await client.close_writer(writer)
        writer.transport.abort.assert_called_once()

    async def test_timeout_aborts_without_poisoning_repeat_close(self):
        closed = asyncio.get_running_loop().create_future()
        async def wait_closed():
            await closed
        writer = mock.Mock(wait_closed=mock.AsyncMock(side_effect=wait_closed))
        writer.transport.abort.side_effect = lambda: closed.set_result(None)
        with mock.patch.object(client, "STREAM_CLOSE_TIMEOUT_SECONDS", .02, create=True):
            await asyncio.wait_for(client.close_writer(writer), 3)
        writer.transport.abort.assert_called_once()
        self.assertFalse(closed.cancelled())
        await asyncio.wait_for(client.close_writer(writer), 1)
        self.assertEqual(2, writer.close.call_count)
        writer.transport.abort.assert_called_once()

    async def test_caller_cancellation_aborts_and_remains_cancellation(self):
        waiting = asyncio.Event()
        closed = asyncio.get_running_loop().create_future()
        async def wait_closed():
            waiting.set()
            await closed
        writer = mock.Mock(wait_closed=mock.AsyncMock(side_effect=wait_closed))
        writer.transport.abort.side_effect = lambda: closed.set_result(None)
        task = asyncio.create_task(client.close_writer(writer))
        await waiting.wait()
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        writer.transport.abort.assert_called_once()
        self.assertFalse(closed.cancelled())
        await asyncio.wait_for(client.close_writer(writer), 1)


class LinuxRelayBridgeTests(unittest.IsolatedAsyncioTestCase):
    async def test_stalled_peer_stops_read_ahead_and_closes_both_directions(self):
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        left, right = mock.Mock(), mock.Mock()
        stalled = asyncio.Event()
        for writer in (left, right):
            writer.drain = mock.AsyncMock(side_effect=stalled.wait)
            writer.wait_closed = mock.AsyncMock()
        data = os.urandom(client.COPY_BUFFER_BYTES * 3)
        first.feed_data(data)
        with mock.patch.object(client, "BRIDGE_WRITE_TIMEOUT_SECONDS", .02):
            await asyncio.wait_for(client.bridge((first, left), (second, right)), 1)
        right.write.assert_called_once_with(data[:client.COPY_BUFFER_BYTES])
        left.write.assert_not_called()
        self.assertEqual(data[client.COPY_BUFFER_BYTES:], await first.read(len(data)))
        for writer in (left, right):
            writer.close.assert_called_once()
            writer.transport.set_write_buffer_limits.assert_called_once_with(high=16384, low=4096)

    async def test_cancellation_stops_both_copies_and_releases_writers(self):
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        left, right = mock.Mock(), mock.Mock()
        for writer in (left, right):
            writer.drain = mock.AsyncMock()
            writer.wait_closed = mock.AsyncMock()
        task = asyncio.create_task(client.bridge((first, left), (second, right)))
        await asyncio.sleep(0)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await asyncio.wait_for(task, 1)
        left.close.assert_called_once()
        right.close.assert_called_once()


class LinuxRelayTlsTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        directory = Path(self.temporary.name)
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Owned relay regression")])
        now = datetime.datetime.now(datetime.timezone.utc)
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(now - datetime.timedelta(minutes=1))
                .not_valid_after(now + datetime.timedelta(days=1)).sign(key, hashes.SHA256()))
        cert_path, key_path = directory / "cert.pem", directory / "key.pem"
        cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
        key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM,
                            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        self.tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.tls.load_cert_chain(cert_path, key_path)
        self.relay = server.RelayServer(dict(access_token=TOKEN, cert_file=str(cert_path), key_file=str(key_path)))
        self.tasks, self.writers = set(), set()
        async def tracked(reader, writer):
            self.tasks.add(asyncio.current_task()); self.writers.add(writer)
            try: await self.relay.handle_connection(reader, writer)
            finally: self.tasks.discard(asyncio.current_task()); self.writers.discard(writer)
        self.listener = await asyncio.start_server(tracked, "127.0.0.1", 0, ssl=self.tls)
        pin = hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest()
        self.options = client.RelayOptions("127.0.0.1", self.listener.sockets[0].getsockname()[1], TOKEN, pin, ID).validate()
        self.host = None
        self.extra_servers = []

    async def asyncTearDown(self):
        if self.host:
            await asyncio.to_thread(self.host.close)
            self.assertFalse(self.host.thread.is_alive())
        for listener in [self.listener, *self.extra_servers]:
            listener.close(); await listener.wait_closed()
        for writer in list(self.writers): await client.close_writer(writer)
        for task in list(self.tasks): task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)

    async def wait_for(self, predicate, seconds=5):
        async with asyncio.timeout(seconds):
            while not predicate(): await asyncio.sleep(.02)

    async def start_host(self):
        async def echo(reader, writer):
            try:
                while data := await reader.read(65536):
                    writer.write(data); await writer.drain()
            finally: await client.close_writer(writer)
        local = await asyncio.start_server(echo, "127.0.0.1", 0)
        self.extra_servers.append(local)
        self.host = client.RelayHostConnector(self.options, local.sockets[0].getsockname()[1], "Owned Linux target")
        self.host.start()
        await self.wait_for(self.host.online.is_set)

    async def test_pinned_directory_and_native_host_registration(self):
        self.assertEqual([], await client.list_devices_async(self.options))
        with mock.patch.object(client, "local_direct_addresses", return_value=["192.0.2.3"]):
            await self.start_host()
        devices = await client.list_devices_async(self.options)
        self.assertEqual([dict(deviceId=ID, machineName="Owned Linux target", platform="Linux", busy=False,
                              directAddresses=["192.0.2.3"], directPort=self.host.local_port)], devices)

    async def test_address_refresh_does_not_interrupt_active_encrypted_relay_transport(self):
        with mock.patch.object(client, "local_direct_addresses", return_value=["192.0.2.3"]) as addresses:
            await self.start_host()
            original = self.relay.hosts[ID]
            viewer = await asyncio.to_thread(client.connect_viewer, self.options)
            try:
                for values in (["198.51.100.4", "10.1.2.3"], []):
                    addresses.return_value = values
                    self.assertTrue(self.host.request_address_refresh())
                    await self.wait_for(lambda: self.relay.hosts[ID].direct_addresses == values)
                    devices = await client.list_devices_async(self.options)
                    self.assertEqual(1, len(devices))
                    self.assertEqual(values, devices[0]["directAddresses"])
                    self.assertIs(original, self.relay.hosts[ID])
                    def exchange():
                        viewer.sendall(b"same-session")
                        return viewer.recv(12)
                    self.assertEqual(b"same-session", await asyncio.to_thread(exchange))
            finally:
                viewer.close()

    async def test_old_server_ignores_address_extension_without_breaking_host(self):
        # Real pinned TLS, but an old implementation's registration/heartbeat response.
        with mock.patch.object(server.HostConnection, "update_addresses", lambda *_: None):
            await self.start_host()
            self.assertEqual([], (await client.list_devices_async(self.options))[0]["directAddresses"])
            self.assertTrue(self.host.request_address_refresh())
            self.assertTrue(self.host.online.is_set())

    async def test_real_tls_socket_applies_backpressure_policy(self):
        _, writer = await client.connect_tls(self.options)
        try:
            self.assertEqual((4096, 16384), writer.transport.get_write_buffer_limits())
            sock = writer.get_extra_info("socket")
            self.assertEqual(1, sock.getsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY))
            if hasattr(socket, "TCP_NOTSENT_LOWAT"):
                self.assertEqual(16384, sock.getsockopt(socket.IPPROTO_TCP, socket.TCP_NOTSENT_LOWAT))
        finally:
            await client.close_writer(writer)

    async def test_local_host_connection_is_loopback_and_bounded_before_connect(self):
        accepted = asyncio.Event()
        async def hold(reader, writer):
            accepted.set()
            try:
                await reader.read()
            finally:
                await client.close_writer(writer)
        local = await asyncio.start_server(hold, "127.0.0.1", 0)
        self.extra_servers.append(local)
        reader, writer = await client.connect_local_host(local.sockets[0].getsockname()[1])
        try:
            await asyncio.wait_for(accepted.wait(), 1)
            self.assertEqual(16384, reader._limit)
            sock = writer.get_extra_info("socket")
            self.assertEqual("127.0.0.1", sock.getpeername()[0])
            # Linux reports a doubled accounting budget; Windows reports the request.
            self.assertIn(sock.getsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF), (16384, 32768))
            self.assertIn(sock.getsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF), (16384, 32768))
        finally:
            await client.close_writer(writer)

    async def test_cancelled_local_connect_closes_owned_socket(self):
        sock = mock.Mock()
        with mock.patch.object(client.socket, "socket", return_value=sock), \
                mock.patch.object(asyncio.get_running_loop(), "sock_connect", side_effect=asyncio.CancelledError):
            with self.assertRaises(asyncio.CancelledError):
                await client.connect_local_host(56565)
        sock.close.assert_called_once()

    async def test_small_bidirectional_messages_do_not_wait_to_fill_buffers(self):
        await self.start_host()
        viewer = await asyncio.to_thread(client.connect_viewer, self.options)
        try:
            def exchange():
                viewer.settimeout(2)
                replies = []
                for item in (b"x", "\u4e2d\u6587".encode(), b"\x00\xff", b"end"):
                    viewer.sendall(item)
                    reply = bytearray()
                    while len(reply) < len(item):
                        block = viewer.recv(len(item) - len(reply))
                        if not block: raise EOFError("Small relay message was truncated")
                        reply.extend(block)
                    replies.append(bytes(reply))
                return replies
            self.assertEqual([b"x", "\u4e2d\u6587".encode(), b"\x00\xff", b"end"],
                             await asyncio.wait_for(asyncio.to_thread(exchange), 4))
        finally:
            viewer.close()

    async def test_stopping_host_closes_active_tunnel_and_allows_new_registration(self):
        await self.start_host()
        viewer = await asyncio.to_thread(client.connect_viewer, self.options)
        try:
            await asyncio.to_thread(self.host.close)
            self.assertFalse(self.host.thread.is_alive())
            self.assertEqual(b"", await asyncio.wait_for(asyncio.to_thread(viewer.recv, 1), 3))
            await self.wait_for(lambda: ID not in self.relay.hosts and not self.relay.pending)
            await self.start_host()
            self.assertEqual(ID, (await client.list_devices_async(self.options))[0]["deviceId"])
        finally:
            viewer.close()

    async def test_bidirectional_tunnel_integrity_and_reconnect(self):
        await self.start_host()
        for _ in range(2):
            viewer = await asyncio.to_thread(client.connect_viewer, self.options)
            try:
                data = os.urandom(2 * 1024 * 1024)
                def exchange():
                    viewer.sendall(data)
                    value = bytearray()
                    while len(value) < len(data):
                        block = viewer.recv(len(data) - len(value))
                        if not block: break
                        value.extend(block)
                    return bytes(value)
                self.assertEqual(data, await asyncio.wait_for(asyncio.to_thread(exchange), 5))
                self.assertTrue((await client.list_devices_async(self.options))[0]["busy"])
            finally: viewer.close()
            await self.wait_for(lambda: not self.relay.pending)

    async def test_control_disconnect_re_registers_and_stop_removes_directory(self):
        await self.start_host()
        original = self.relay.hosts[ID]
        original.writer.close()
        await self.wait_for(lambda: ID in self.relay.hosts and self.relay.hosts[ID] is not original)
        await asyncio.to_thread(self.host.close)
        await self.wait_for(lambda: ID not in self.relay.hosts)

    async def test_access_key_failure_is_terminal_for_host(self):
        wrong = replace(self.options, access_token="wrong-token-" * 5)
        with self.assertRaises(client.RelayIdentityError): await client.list_devices_async(wrong)
        self.host = client.RelayHostConnector(wrong, 9)
        self.host.start()
        await self.wait_for(lambda: not self.host.thread.is_alive())
        self.assertFalse(self.host.online.is_set())

    async def test_wrong_pin_never_sends_access_token(self):
        received = []
        async def trap(reader, writer):
            received.append(await reader.read())
            await client.close_writer(writer)
        trap_server = await asyncio.start_server(trap, "127.0.0.1", 0, ssl=self.tls)
        self.extra_servers.append(trap_server)
        wrong = replace(self.options, port=trap_server.sockets[0].getsockname()[1], tls_certificate_sha256="00" * 32)
        with self.assertRaises(client.RelayIdentityError): await client.list_devices_async(wrong)
        await self.wait_for(lambda: bool(received))
        self.assertEqual([b""], received)

    async def test_viewer_cancels_pending_relay_pair(self):
        got_hello, closed = asyncio.Event(), asyncio.Event()
        async def stall(reader, writer):
            try:
                await client.read_json(reader); got_hello.set()
                await reader.read(); closed.set()
            finally: await client.close_writer(writer)
        stall_server = await asyncio.start_server(stall, "127.0.0.1", 0, ssl=self.tls)
        self.extra_servers.append(stall_server)
        options = replace(self.options, port=stall_server.sockets[0].getsockname()[1])
        stop = threading.Event()
        pending = asyncio.create_task(asyncio.to_thread(client.connect_viewer, options, stop))
        await asyncio.wait_for(got_hello.wait(), 3)
        stop.set()
        with self.assertRaises(ConnectionAbortedError): await asyncio.wait_for(pending, 2)
        await asyncio.wait_for(closed.wait(), 3)

    async def test_invalid_or_oversized_json_is_bounded(self):
        for payload in (struct.pack("!I", 0), struct.pack("!I", 65537), struct.pack("!I", 2) + b"[]"):
            reader = asyncio.StreamReader(); reader.feed_data(payload); reader.feed_eof()
            with self.assertRaises(ValueError): await client.read_json(reader)


if __name__ == "__main__": unittest.main()
