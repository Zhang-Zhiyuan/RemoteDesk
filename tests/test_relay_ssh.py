"""SSH routing uses independent service identity and never desktop ownership."""
import asyncio
import unittest
import uuid
import test_relay_server as fixtures
from test_relay_server import read_json, write_json, relay, TOKEN


class RelaySshTests(unittest.IsolatedAsyncioTestCase):
    asyncSetUp = fixtures.RelayServerTests.asyncSetUp
    asyncTearDown = fixtures.RelayServerTests.asyncTearDown
    connect = fixtures.RelayServerTests.connect
    register_host = fixtures.RelayServerTests.register_host
    directory = fixtures.RelayServerTests.directory

    async def request(self, device_id, role="ssh-viewer"):
        reader, writer = await self.connect()
        await write_json(writer, dict(version=1, role=role, token=TOKEN, deviceId=device_id))
        return reader, writer

    async def pair(self, device_id, control, service="ssh"):
        viewer, viewer_writer = await self.request(device_id, "ssh-viewer" if service == "ssh" else "viewer")
        message = await asyncio.wait_for(read_json(control), 2)
        self.assertEqual("open-ssh" if service == "ssh" else "open", message["type"])
        host, host_writer = await self.connect()
        await write_json(host_writer, dict(version=1, role="host-data", token=TOKEN,
                                          deviceId=device_id, sessionId=message["sessionId"], service=service))
        for reader in (host, viewer):
            reply = await asyncio.wait_for(read_json(reader), 2)
            self.assertTrue(reply["ok"])
            self.assertEqual(service, reply.get("service", "desktop"))
        return viewer, viewer_writer, host, host_writer

    async def test_legacy_host_cannot_be_used_as_ssh_or_receive_desktop_open(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device)
        reader, _ = await self.request(device)
        self.assertFalse((await read_json(reader))["ok"])
        self.assertEqual(["desktop"], (await self.directory())[0]["services"])
        with self.assertRaises(asyncio.TimeoutError):
            await asyncio.wait_for(control.read(1), .05)

    async def test_pure_ssh_host_rejects_desktop_without_open(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["ssh"])
        reader, _ = await self.request(device, "viewer")
        self.assertFalse((await read_json(reader))["ok"])
        self.assertEqual(["ssh"], (await self.directory())[0]["services"])
        with self.assertRaises(asyncio.TimeoutError):
            await asyncio.wait_for(control.read(1), .05)

    async def test_ssh_bytes_and_close_leave_parallel_desktop_intact(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["desktop", "ssh"])
        desktop, dw, desktop_host, dhw = await self.pair(device, control, "desktop")
        ssh, sw, ssh_host, shw = await self.pair(device, control)
        sw.write(b"SSH-2.0-viewer\r\n"); await sw.drain()
        self.assertEqual(b"SSH-2.0-viewer\r\n", await ssh_host.readexactly(16))
        shw.write(b"SSH-2.0-host\r\n"); await shw.drain()
        self.assertEqual(b"SSH-2.0-host\r\n", await ssh.readexactly(14))
        sw.close(); await sw.wait_closed()
        self.assertEqual(b"", await asyncio.wait_for(ssh_host.read(1), 2))
        dw.write(b"desktop-still-open"); await dw.drain()
        self.assertEqual(b"desktop-still-open", await desktop_host.readexactly(18))
        dhw.write(b"alive"); await dhw.drain()
        self.assertEqual(b"alive", await desktop.readexactly(5))

    async def test_ssh_does_not_set_desktop_busy(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["ssh"])
        await self.pair(device, control)
        row = (await self.directory())[0]
        self.assertFalse(row["busy"])
        self.assertEqual(1, row["sshSessions"])
        reader, writer = await self.connect()
        await write_json(writer, dict(version=1, role="health", token=TOKEN))
        health = await read_json(reader)
        self.assertTrue(health["busy"])
        self.assertTrue(health["sshTunneling"])

    async def test_four_pending_ssh_limit_does_not_block_desktop(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["desktop", "ssh"])
        for _ in range(4):
            await self.request(device)
            self.assertEqual("open-ssh", (await read_json(control))["type"])
        reader, _ = await self.request(device)
        self.assertFalse((await read_json(reader))["ok"])
        await self.pair(device, control, "desktop")

    async def test_wrong_data_service_cannot_pair_then_correct_service_can(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["ssh"])
        viewer, _ = await self.request(device)
        message = await read_json(control)
        wrong, writer = await self.connect()
        await write_json(writer, dict(version=1, role="host-data", token=TOKEN,
                                     deviceId=device, sessionId=message["sessionId"]))
        self.assertFalse((await read_json(wrong))["ok"])
        right, writer = await self.connect()
        await write_json(writer, dict(version=1, role="host-data", token=TOKEN,
                                     deviceId=device, sessionId=message["sessionId"], service="ssh"))
        self.assertTrue((await read_json(right))["ok"])
        self.assertTrue((await read_json(viewer))["ok"])

    async def test_disable_capability_rejects_pending_data(self):
        device = str(uuid.uuid4())
        control, cw = await self.register_host(device, services=["ssh"])
        await self.request(device)
        message = await read_json(control)
        await write_json(cw, dict(type="heartbeat", services=[]))
        await read_json(control)
        data, writer = await self.connect()
        await write_json(writer, dict(version=1, role="host-data", token=TOKEN,
                                     deviceId=device, sessionId=message["sessionId"], service="ssh"))
        self.assertFalse((await read_json(data))["ok"])
        self.assertEqual([], (await self.directory())[0]["services"])

    async def test_replaced_registration_cannot_pair_old_session(self):
        device = str(uuid.uuid4())
        control, _ = await self.register_host(device, services=["ssh"])
        await self.request(device)
        message = await read_json(control)
        await self.register_host(device, services=["ssh"])
        data, writer = await self.connect()
        await write_json(writer, dict(version=1, role="host-data", token=TOKEN,
                                     deviceId=device, sessionId=message["sessionId"], service="ssh"))
        self.assertFalse((await read_json(data))["ok"])

    def test_service_values_are_explicit_bounded_allowlist(self):
        for value in (None, "ssh", {}, ["tcp"], [22], ["ssh"] * 3):
            with self.subTest(value=value), self.assertRaises(ValueError):
                relay.normalize_services(value)
        self.assertEqual(frozenset(), relay.normalize_services([]))


if __name__ == "__main__":
    unittest.main()
