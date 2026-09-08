from __future__ import annotations

import asyncio
import json
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

    async def register_host(self, device_id: str):
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
            },
        )
        self.assertTrue((await read_json(reader))["ok"])
        return reader, writer

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


if __name__ == "__main__":
    unittest.main()
