from __future__ import annotations

import asyncio
from dataclasses import replace
import datetime
import hashlib
import json
import os
from pathlib import Path
import ssl
import struct
import sys
import tempfile
import threading
import unittest
import uuid

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
        await self.start_host()
        devices = await client.list_devices_async(self.options)
        self.assertEqual([dict(deviceId=ID, machineName="Owned Linux target", platform="Linux", busy=False)], devices)

    async def test_bidirectional_tunnel_integrity_and_reconnect(self):
        await self.start_host()
        for _ in range(2):
            viewer = await asyncio.to_thread(client.connect_viewer, self.options)
            try:
                data = os.urandom(131072)
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
