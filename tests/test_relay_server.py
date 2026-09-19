from __future__ import annotations

import asyncio
import json
import socket
import struct
import sys
import unittest
import uuid
from unittest import mock
from pathlib import Path


RELAY_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "relay"
sys.path.insert(0, str(RELAY_SCRIPTS))

import remotedesk_relay_server as relay  # noqa: E402


TOKEN = "ab" * 32


async def write_json(writer: asyncio.StreamWriter, value: dict) -> None:
    payload = json.dumps(value, separators=(",", ":")).encode()
    writer.write(struct.pack(">I", len(payload)) + payload)
    await writer.drain()


async def read_json(reader: asyncio.StreamReader) -> dict:
    length = struct.unpack(">I", await reader.readexactly(4))[0]
    return json.loads(await reader.readexactly(length))


class RelayServerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.relay = relay.RelayServer(
            {
                "access_token": TOKEN,
                "bind": "127.0.0.1",
                "port": 56567,
                "cert_file": "unused",
                "key_file": "unused",
            }
        )
        self.server = await asyncio.start_server(
            self.relay.handle_connection,
            "127.0.0.1",
            0,
        )
        self.port = self.server.sockets[0].getsockname()[1]
        self.clients: list[asyncio.StreamWriter] = []

    async def asyncTearDown(self) -> None:
        for writer in self.clients:
            writer.close()
        for writer in self.clients:
            try:
                await writer.wait_closed()
            except (ConnectionError, asyncio.CancelledError):
                pass
        self.server.close()
        await self.server.wait_closed()

    async def connect(self):
        reader, writer = await asyncio.open_connection(
            "127.0.0.1", self.port
        )
        self.clients.append(writer)
        return reader, writer

    async def register_host(self, device_id: str, **report):
        reader, writer = await self.connect()
        await write_json(
            writer,
            {
                "version": 1,
                "role": "host-control",
                "token": TOKEN,
                "deviceId": device_id,
                "machineName": "Office-PC",
                "platform": "Windows",
                "buildStamp": "20260904000000",
                **report,
            },
        )
        self.assertTrue((await read_json(reader))["ok"])
        return reader, writer

    async def directory(self):
        reader, writer = await self.connect()
        await write_json(writer, dict(version=1, role="directory", token=TOKEN, pageSize=32, offset=0))
        return (await read_json(reader))["devices"]

    async def test_authenticated_health_reports_loaded_version_hash_and_idle_state(self):
        reader, writer = await self.connect()
        await write_json(writer, dict(version=1, role="health", token=TOKEN))
        response = await read_json(reader)
        self.assertTrue(response["ok"])
        self.assertEqual(relay.RELAY_RELEASE_VERSION, response["serverVersion"])
        self.assertEqual(relay.RELAY_SOURCE_SHA256, response["serverSourceSha256"])
        self.assertRegex(response["serverSourceSha256"], r"^[0-9a-f]{64}$")
        self.assertFalse(response["busy"])

    async def test_health_requires_authentication_before_exposing_build(self):
        reader, writer = await self.connect()
        await write_json(writer, dict(version=1, role="health", token="wrong"))
        response = await read_json(reader)
        self.assertFalse(response["ok"])
        self.assertNotIn("serverVersion", response)
        self.assertNotIn("serverSourceSha256", response)

    async def test_health_includes_pending_sessions(self):
        with mock.patch.dict(self.relay.pending, {"pending": object()}):
            reader, writer = await self.connect()
            await write_json(writer, dict(version=1, role="health", token=TOKEN))
            self.assertTrue((await read_json(reader))["busy"])

    async def test_health_source_identity_is_not_reloaded_from_replaced_disk_file(self):
        with mock.patch.object(Path, "read_bytes", return_value=b"replaced file"):
            reader, writer = await self.connect()
            await write_json(writer, dict(version=1, role="health", token=TOKEN))
            response = await read_json(reader)
            self.assertEqual(relay.RELAY_SOURCE_SHA256, response["serverSourceSha256"])

    async def test_address_change_and_custom_port_replace_same_device(self):
        device_id = str(uuid.uuid4())
        reader, writer = await self.register_host(device_id, directAddresses=["192.0.2.3"], directPort=40565)
        original = self.relay.hosts[device_id]
        first = (await self.directory())[0]
        self.assertEqual(["192.0.2.3"], first["directAddresses"])
        self.assertEqual(40565, first["directPort"])
        await write_json(writer, dict(type="heartbeat", directAddresses=["198.51.100.4", "10.1.2.3"], directPort=56565))
        self.assertTrue((await read_json(reader))["addressReporting"])
        devices = await self.directory()
        self.assertEqual(1, len(devices))
        self.assertEqual(device_id, devices[0]["deviceId"])
        self.assertEqual(["198.51.100.4", "10.1.2.3"], devices[0]["directAddresses"])
        self.assertEqual(56565, devices[0]["directPort"])
        self.assertIs(original, self.relay.hosts[device_id])

    async def test_legacy_host_works_and_stale_address_hints_expire(self):
        device_id = str(uuid.uuid4())
        reader, writer = await self.register_host(device_id)
        first = (await self.directory())[0]
        self.assertEqual([], first["directAddresses"])
        self.assertEqual(0, first["directPort"])
        await write_json(writer, dict(type="heartbeat", directAddresses=["192.0.2.3"], directPort=56565))
        await read_json(reader)
        self.relay.hosts[device_id].address_reported_at -= 46
        await write_json(writer, dict(type="heartbeat"))
        await read_json(reader)
        current = (await self.directory())[0]
        self.assertEqual([], current["directAddresses"])
        self.assertEqual(0, current["directPort"])
        self.assertGreaterEqual(current["addressAgeSeconds"], 45)

    async def test_invalid_or_empty_report_clears_hints_without_disconnecting(self):
        device_id = str(uuid.uuid4())
        reader, writer = await self.register_host(device_id, directAddresses=["192.0.2.3"], directPort=56565)
        original = self.relay.hosts[device_id]
        for report in (dict(directAddresses=[], directPort=56565),
                       dict(directAddresses=["https://host/", "127.0.0.1", {}], directPort=True)):
            await write_json(writer, dict(type="heartbeat", **report))
            await read_json(reader)
            self.assertEqual([], (await self.directory())[0]["directAddresses"])
            self.assertIs(original, self.relay.hosts[device_id])

    async def test_new_registration_does_not_inherit_old_ip_and_offline_disappears(self):
        device_id = str(uuid.uuid4())
        await self.register_host(device_id, directAddresses=["192.0.2.3"], directPort=56565)
        _, replacement = await self.register_host(device_id)
        self.assertEqual([], (await self.directory())[0]["directAddresses"])
        replacement.close()
        await replacement.wait_closed()
        for _ in range(50):
            if not await self.directory(): break
            await asyncio.sleep(.01)
        self.assertEqual([], await self.directory())

    async def test_directory_and_opaque_bidirectional_tunnel(self) -> None:
        device_id = str(uuid.uuid4())
        host_reader, _host_writer = await self.register_host(device_id)

        directory_reader, directory_writer = await self.connect()
        await write_json(
            directory_writer,
            {"version": 1, "role": "directory", "token": TOKEN},
        )
        listing = await read_json(directory_reader)
        self.assertTrue(listing["ok"])
        self.assertEqual(device_id, listing["devices"][0]["deviceId"])
        self.assertEqual("Office-PC", listing["devices"][0]["machineName"])
        self.assertNotIn("directAddresses", listing["devices"][0])  # Legacy unpaged response.

        viewer_reader, viewer_writer = await self.connect()
        await write_json(
            viewer_writer,
            {
                "version": 1,
                "role": "viewer",
                "token": TOKEN,
                "deviceId": device_id,
            },
        )
        open_request = await asyncio.wait_for(
            read_json(host_reader), timeout=2
        )
        self.assertEqual("open", open_request["type"])

        data_reader, data_writer = await self.connect()
        await write_json(
            data_writer,
            {
                "version": 1,
                "role": "host-data",
                "token": TOKEN,
                "deviceId": device_id,
                "sessionId": open_request["sessionId"],
            },
        )
        self.assertTrue((await read_json(data_reader))["ok"])
        self.assertTrue((await read_json(viewer_reader))["ok"])

        viewer_writer.write(b"viewer-to-host")
        await viewer_writer.drain()
        self.assertEqual(
            b"viewer-to-host",
            await asyncio.wait_for(
                data_reader.readexactly(len(b"viewer-to-host")), timeout=2
            ),
        )

        data_writer.write(b"host-to-viewer")
        await data_writer.drain()
        self.assertEqual(
            b"host-to-viewer",
            await asyncio.wait_for(
                viewer_reader.readexactly(len(b"host-to-viewer")), timeout=2
            ),
        )

    async def test_rejects_bad_token_and_offline_device(self) -> None:
        reader, writer = await self.connect()
        await write_json(
            writer,
            {"version": 1, "role": "directory", "token": "bad"},
        )
        response = await read_json(reader)
        self.assertFalse(response["ok"])

        viewer_reader, viewer_writer = await self.connect()
        await write_json(
            viewer_writer,
            {
                "version": 1,
                "role": "viewer",
                "token": TOKEN,
                "deviceId": str(uuid.uuid4()),
            },
        )
        response = await read_json(viewer_reader)
        self.assertFalse(response["ok"])
        self.assertIn("不在线", response["error"])

    async def test_new_host_registration_replaces_stale_control(self) -> None:
        device_id = str(uuid.uuid4())
        old_reader, _old_writer = await self.register_host(device_id)
        await self.register_host(device_id)
        self.assertEqual(
            b"",
            await asyncio.wait_for(old_reader.read(1), timeout=2),
        )

    async def test_heartbeat_is_acknowledged(self) -> None:
        reader, writer = await self.register_host(str(uuid.uuid4()))
        await write_json(writer, {"type": "heartbeat"})
        self.assertEqual("heartbeat", (await asyncio.wait_for(read_json(reader), 2))["type"])

    async def test_shutdown_closes_idle_host_without_waiting_for_heartbeat_timeout(self) -> None:
        reader, _ = await self.register_host(str(uuid.uuid4()))
        self.server.close()
        await asyncio.wait_for(self.relay.shutdown(), 2)
        await asyncio.wait_for(self.server.wait_closed(), 2)
        self.assertEqual(b"", await asyncio.wait_for(reader.read(1), 1))
        self.assertEqual({}, self.relay.connections)
        self.assertEqual({}, self.relay.hosts)

    async def test_shutdown_cancels_unfinished_handshake_and_pairing(self) -> None:
        device_id = str(uuid.uuid4())
        host_reader, _ = await self.register_host(device_id)
        _, incomplete_writer = await self.connect()
        incomplete_writer.write(b"\x00")
        await incomplete_writer.drain()
        _, viewer_writer = await self.connect()
        await write_json(viewer_writer, {
            "version": 1, "role": "viewer", "token": TOKEN, "deviceId": device_id,
        })
        await asyncio.wait_for(read_json(host_reader), 2)
        self.server.close()
        await asyncio.wait_for(self.relay.shutdown(), 2)
        await asyncio.wait_for(self.server.wait_closed(), 2)
        self.assertEqual({}, self.relay.connections)
        self.assertEqual({}, self.relay.pending)
        await self.relay.shutdown()  # Repeated shutdown is harmless.

    async def test_connection_cap_rejects_excess_without_queueing(self) -> None:
        self.relay.connection_slots = asyncio.Semaphore(1)
        await self.register_host(str(uuid.uuid4()))
        reader, _writer = await self.connect()
        self.assertEqual(b"", await asyncio.wait_for(reader.read(1), 1))

    async def test_failed_registration_ack_does_not_leave_ghost_device(self) -> None:
        device_id = str(uuid.uuid4())
        reader, writer = await self.connect()
        with mock.patch.object(self.relay, "_write_host", side_effect=ConnectionError("closed")):
            await write_json(writer, {
                "version": 1, "role": "host-control", "token": TOKEN,
                "deviceId": device_id,
            })
            self.assertEqual(b"", await asyncio.wait_for(reader.read(1), 2))
        self.assertNotIn(device_id, self.relay.hosts)

    async def test_pair_timeout_cleans_pending_and_rejects_late_data(self) -> None:
        device_id = str(uuid.uuid4())
        host_reader, _host_writer = await self.register_host(device_id)
        viewer_reader, viewer_writer = await self.connect()
        with mock.patch.object(relay, "PAIR_TIMEOUT_SECONDS", 0.1):
            await write_json(viewer_writer, {
                "version": 1, "role": "viewer", "token": TOKEN,
                "deviceId": device_id,
            })
            opening = await asyncio.wait_for(read_json(host_reader), 2)
            self.assertFalse((await asyncio.wait_for(read_json(viewer_reader), 2))["ok"])
        self.assertEqual({}, self.relay.pending)
        reader, writer = await self.connect()
        await write_json(writer, {
            "version": 1, "role": "host-data", "token": TOKEN,
            "deviceId": device_id, "sessionId": opening["sessionId"],
        })
        self.assertFalse((await asyncio.wait_for(read_json(reader), 2))["ok"])

    async def test_host_loss_during_pairing_cleans_up_immediately(self) -> None:
        device_id = str(uuid.uuid4())
        host_reader, host_writer = await self.register_host(device_id)
        reader, writer = await self.connect()
        await write_json(writer, {
            "version": 1, "role": "viewer", "token": TOKEN,
            "deviceId": device_id,
        })
        await asyncio.wait_for(read_json(host_reader), 2)
        host_writer.close()
        self.assertFalse((await asyncio.wait_for(read_json(reader), 2))["ok"])
        self.assertEqual({}, self.relay.pending)

    async def test_small_tunnel_messages_do_not_wait_to_fill_a_copy_buffer(self) -> None:
        # One byte must be forwarded immediately; read(n) is not readexactly(n).
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        left, right = mock.Mock(), mock.Mock()
        for writer in (left, right):
            writer.drain = mock.AsyncMock()
            writer.wait_closed = mock.AsyncMock()
        with mock.patch.object(relay, "print", create=True) as report:
            task = asyncio.create_task(relay.bridge_streams(first, left, second, right))
            first.feed_data(b"x")
            for _ in range(100):
                if right.write.called:
                    break
                await asyncio.sleep(.001)
            right.write.assert_called_once_with(b"x")
            first.feed_eof()
            await asyncio.wait_for(task, 1)
            report.assert_called_once_with("relay bridge ended: reason=eof direction=first-to-second stage=read",
                                           file=sys.stderr, flush=True)
        for writer in (left, right):
            writer.close.assert_not_called()
            writer.transport.abort.assert_called_once()

    async def test_stalled_tls_writer_closes_both_directions(self) -> None:
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        left, right = mock.Mock(), mock.Mock()
        blocked = asyncio.Event()
        for writer in (left, right):
            writer.drain = mock.AsyncMock(side_effect=blocked.wait)
            writer.wait_closed = mock.AsyncMock()
        first.feed_data(b"unchanged encrypted bytes")
        with (mock.patch.object(relay, "BRIDGE_WRITE_TIMEOUT_SECONDS", .02),
              mock.patch.object(relay, "print", create=True) as report):
            await asyncio.wait_for(relay.bridge_streams(first, left, second, right), 1)
            report.assert_called_once_with("relay bridge ended: reason=drain-timeout direction=first-to-second stage=drain error=TimeoutError",
                                           file=sys.stderr, flush=True)
        right.write.assert_called_once_with(b"unchanged encrypted bytes")
        for writer in (left, right):
            writer.close.assert_not_called()
            writer.transport.abort.assert_called_once()

    async def test_bridge_errors_are_classified_without_logging_exception_messages(self):
        for error, category in ((ConnectionResetError("private token=do-not-log"), "connection-error"),
                                (ValueError("private payload=do-not-log"), "error")):
            with self.subTest(category=category):
                first, second = asyncio.StreamReader(), mock.Mock()
                second.read = mock.AsyncMock(side_effect=error)
                left, right = mock.Mock(), mock.Mock()
                for writer in (left, right):
                    writer.drain = mock.AsyncMock()
                    writer.wait_closed = mock.AsyncMock()
                with mock.patch.object(relay, "print", create=True) as report:
                    await asyncio.wait_for(relay.bridge_streams(first, left, second, right), 1)
                    report.assert_called_once()
                    message = report.call_args.args[0]
                    self.assertIn(f"reason={category}", message)
                    self.assertIn("direction=second-to-first stage=read", message)
                    self.assertIn(f"error={type(error).__name__}", message)
                    self.assertNotIn("private", message)
                    self.assertNotIn("do-not-log", message)
                    self.assertLessEqual(len(message), 256)
                for writer in (left, right):
                    writer.close.assert_not_called()
                    writer.transport.abort.assert_called_once()

    async def test_bridge_external_cancellation_reports_once_and_preserves_cleanup(self):
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        left, right = mock.Mock(), mock.Mock()
        for writer in (left, right):
            writer.drain = mock.AsyncMock()
            writer.wait_closed = mock.AsyncMock()
        with mock.patch.object(relay, "print", create=True) as report:
            task = asyncio.create_task(relay.bridge_streams(first, left, second, right))
            await asyncio.sleep(0)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            report.assert_called_once_with("relay bridge ended: reason=cancelled direction=bridge stage=wait",
                                           file=sys.stderr, flush=True)
        for writer in (left, right):
            writer.transport.abort.assert_called_once()

    async def test_bridge_diagnostic_failure_cannot_break_cleanup(self):
        first, second = asyncio.StreamReader(), asyncio.StreamReader()
        first.feed_eof()
        second.feed_eof()
        left, right = mock.Mock(), mock.Mock()
        for writer in (left, right):
            writer.drain = mock.AsyncMock()
            writer.wait_closed = mock.AsyncMock()
        with mock.patch.object(relay, "print", create=True, side_effect=OSError("stderr unavailable")) as report:
            await asyncio.wait_for(relay.bridge_streams(first, left, second, right), 1)
            report.assert_called_once()
        for writer in (left, right):
            writer.transport.abort.assert_called_once()

    async def test_close_timeout_does_not_poison_later_owner_cleanup(self):
        closed = asyncio.get_running_loop().create_future()
        async def wait_closed():
            await closed
        writer = mock.Mock(wait_closed=mock.AsyncMock(side_effect=wait_closed))
        writer.transport.abort.side_effect = lambda: closed.set_result(None)
        with mock.patch.object(relay, "STREAM_CLOSE_TIMEOUT_SECONDS", .02):
            await asyncio.wait_for(relay.close_writer(writer), 1)
        self.assertFalse(closed.cancelled())
        await asyncio.wait_for(relay.close_writer(writer), 1)
        writer.transport.abort.assert_called_once()

    async def test_tls_close_timeout_aborts_socket_instead_of_leaking_it(self) -> None:
        writer = mock.Mock()
        blocked = asyncio.Event()
        writer.wait_closed = mock.AsyncMock(side_effect=blocked.wait)
        with mock.patch.object(relay, "STREAM_CLOSE_TIMEOUT_SECONDS", .02):
            await asyncio.wait_for(relay.close_writer(writer), 1)
        writer.close.assert_called_once()
        writer.transport.abort.assert_called_once()


class RelayTransportPolicyTests(unittest.TestCase):
    def test_transport_limits_unsent_data_without_fixing_tcp_window(self):
        writer = mock.Mock()
        relay.configure_transport(writer)
        writer.transport.set_write_buffer_limits.assert_called_once_with(high=64 * 1024, low=16 * 1024)
        sock = writer.get_extra_info.return_value
        sock.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        if hasattr(socket, "TCP_NOTSENT_LOWAT"):
            sock.setsockopt.assert_any_call(socket.IPPROTO_TCP, socket.TCP_NOTSENT_LOWAT, 64 * 1024)
        self.assertTrue(all(call.args[0] != socket.SOL_SOCKET for call in sock.setsockopt.call_args_list))

    def test_optional_kernel_features_fall_back_without_breaking_connection(self):
        writer = mock.Mock()
        writer.get_extra_info.return_value.setsockopt.side_effect = OSError("unsupported")
        relay.configure_transport(writer, "bbr")
        writer.transport.set_write_buffer_limits.assert_called_once()

    def test_invalid_congestion_configuration_is_rejected(self):
        for value in (None, True, 123, "bbr\n", "unrecognized", {}):
            with self.subTest(value=value), self.assertRaises(ValueError):
                relay.validate_congestion_control(value)
        for value in ("", "cubic", "bbr"):
            self.assertEqual(value, relay.validate_congestion_control(value))


if __name__ == "__main__":
    unittest.main()
