"""Shared relay labels: real wire operations, persistence and independent clients."""
import asyncio
import json
from pathlib import Path
import sys
import struct
import tempfile
import threading
import unittest
from unittest import mock
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts' / 'relay'))
sys.path.insert(0, str(ROOT / 'scripts' / 'linux'))
import remotedesk_relay_server as server
import remotedesk_linux_relay as client

TOKEN = 'ab' * 32


class RelayNamesTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='remotedesk-names-')
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / 'device-names.json'
        self.config = dict(access_token=TOKEN, cert_file='unused', key_file='unused', device_names_file=str(self.path))
        self.relay = server.RelayServer(self.config)
        self.listener = await asyncio.start_server(self.relay.handle_connection, '127.0.0.1', 0)
        self.port = self.listener.sockets[0].getsockname()[1]
        self.writers = []
        self.device_id = str(uuid.uuid4())
        self.options = client.RelayOptions('127.0.0.1', self.port, TOKEN, 'a' * 64, self.device_id)

    async def asyncTearDown(self):
        await self.relay.shutdown()
        self.listener.close()
        await self.listener.wait_closed()
        for writer in self.writers:
            await server.close_writer(writer)

    async def open(self, _options=None):
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        self.writers.append(writer)
        return reader, writer

    async def exchange(self, role, **fields):
        reader, writer = await self.open()
        # ASCII escaping also permits deliberately invalid UTF-16 fixtures to reach server validation.
        payload = json.dumps(dict(version=1, token=TOKEN, role=role, **fields), ensure_ascii=True).encode()
        writer.write(struct.pack('>I', len(payload)) + payload)
        await writer.drain()
        return await asyncio.wait_for(server.read_json(reader), 3)

    async def host(self, device_id=None, name='Original-PC', **fields):
        return await self.exchange('host-control', deviceId=device_id or self.device_id,
                                   machineName=name, platform='test', **fields)

    async def rename(self, name, device_id=None):
        return await self.exchange('rename-device', deviceId=device_id or self.device_id, name=name)

    async def directory(self, legacy=False):
        return await self.exchange('directory', **({} if legacy else dict(pageSize=32, offset=0)))

    async def test_shared_label_seen_by_an_independent_linux_client_and_legacy_directory(self):
        await self.host()
        with mock.patch.object(client, 'connect_tls', side_effect=self.open):
            await client.rename_device_async(self.options, '  广州工作机 🖥  ')
            devices = await client.list_devices_async(client.replace(self.options, device_id=str(uuid.uuid4())))
        self.assertEqual('广州工作机 🖥', devices[0]['machineName'])
        self.assertEqual('广州工作机 🖥', devices[0]['sharedName'])
        self.assertEqual('Original-PC', devices[0]['originalMachineName'])
        self.assertTrue(devices[0]['canRename'])
        legacy = (await self.directory(legacy=True))['devices'][0]
        self.assertEqual('广州工作机 🖥', legacy['machineName'])
        self.assertNotIn('sharedName', legacy)

    async def test_label_survives_dhcp_reregistration_and_server_recreation(self):
        await self.host(directAddresses=['192.0.2.1'], directPort=56565)
        self.assertTrue((await self.rename('实验室'))['ok'])
        await self.host(name='Changed-OS-Name', directAddresses=['192.0.2.2'], directPort=40565)
        self.relay.device_names = server.RelayServer(self.config).device_names
        device = (await self.directory())['devices'][0]
        self.assertEqual('实验室', device['machineName'])
        self.assertEqual('Changed-OS-Name', device['originalMachineName'])
        self.assertEqual(['192.0.2.2'], device['directAddresses'])
        self.assertEqual(40565, device['directPort'])

    async def test_clear_restores_system_name_without_changing_device_identity(self):
        await self.host()
        await self.rename('临时备注')
        self.assertTrue((await self.rename('   '))['ok'])
        device = (await self.directory())['devices'][0]
        self.assertEqual('Original-PC', device['machineName'])
        self.assertEqual('', device['sharedName'])
        self.assertEqual(self.device_id, device['deviceId'])
        self.assertEqual({}, server.load_device_names(self.path))

    async def test_wrong_token_cannot_read_or_mutate_name(self):
        await self.host()
        await self.rename('保留')
        before = self.path.read_bytes()
        reader, writer = await self.open()
        await server.write_json(writer, dict(version=1, role='rename-device', token='ff' * 32,
                                             deviceId=self.device_id, name='禁止'))
        self.assertFalse((await server.read_json(reader))['ok'])
        self.assertEqual(before, self.path.read_bytes())

    async def test_unknown_device_and_invalid_names_do_not_create_records(self):
        self.assertFalse((await self.rename('未知'))['ok'])
        await self.host()
        for invalid in (None, 1, 'a' * 81, '🙂' * 41, 'a\nb', 'a\u202eb', '\ud800'):
            with self.subTest(value=repr(invalid)):
                self.assertFalse((await self.rename(invalid))['ok'])
        self.assertFalse(self.path.exists())
        self.assertEqual({}, self.relay.device_names)

    async def test_concurrent_clients_do_not_lose_other_devices_names(self):
        ids = [str(uuid.uuid4()) for _ in range(12)]
        for device_id in ids:
            await self.host(device_id)
        replies = await asyncio.gather(*(self.rename('节点-' + str(i), device_id) for i, device_id in enumerate(ids)))
        self.assertTrue(all(reply['ok'] for reply in replies))
        self.assertEqual({key: '节点-' + str(i) for i, key in enumerate(ids)}, server.load_device_names(self.path))

    async def test_disk_failure_does_not_change_memory_or_original_file(self):
        await self.host()
        await self.rename('旧名称')
        before = self.path.read_bytes()
        with mock.patch.object(server.os, 'replace', side_effect=PermissionError('fixture denied')):
            response = await self.rename('不可保存')
        self.assertFalse(response['ok'])
        self.assertIn('保存失败', response['error'])
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual('旧名称', self.relay.device_names[self.device_id])
        self.assertEqual([self.path], list(self.path.parent.iterdir()))

    async def test_corrupt_database_keeps_relay_available_but_naming_read_only(self):
        await self.host()
        self.path.write_text('{broken', encoding='utf-8')
        with mock.patch('sys.stderr'):
            restored = server.RelayServer(self.config)
        self.relay.names_error = restored.names_error
        response = await self.directory()
        self.assertTrue(response['ok'])
        self.assertFalse(response['deviceNaming'])
        self.assertIn('无法读取', response['deviceNamingError'])
        self.assertFalse((await self.rename('不可覆盖'))['ok'])
        self.assertEqual('{broken', self.path.read_text())

    async def test_canceled_save_finishes_under_lock_before_a_later_write(self):
        await self.host()
        entered, release = threading.Event(), threading.Event()
        original_save = server.save_device_names
        def delayed_save(path, values):
            entered.set()
            if not release.wait(3):
                raise TimeoutError('fixture release timeout')
            original_save(path, values)
        writer = mock.Mock(drain=mock.AsyncMock())
        with mock.patch.object(server, 'save_device_names', side_effect=delayed_save):
            task = asyncio.create_task(self.relay.handle_rename_device(writer, dict(deviceId=self.device_id, name='已落盘')))
            try:
                self.assertTrue(await asyncio.to_thread(entered.wait, 2))
                task.cancel()
                await asyncio.sleep(0)
                self.assertTrue(self.relay.names_lock.locked())
            finally:
                release.set()
                with self.assertRaises(asyncio.CancelledError):
                    await task
        self.assertEqual('已落盘', self.relay.device_names[self.device_id])
        self.assertEqual(self.relay.device_names, server.load_device_names(self.path))

    async def test_linux_rejects_unmatched_receipt_and_sanitizes_errors(self):
        with mock.patch.object(client, 'connect_tls', side_effect=self.open), \
             mock.patch.object(client, 'read_json', mock.AsyncMock(return_value=dict(ok=True, deviceId=str(uuid.uuid4()), sharedName='a'))):
            with self.assertRaisesRegex(ValueError, '未确认'):
                await client.rename_device_async(self.options, 'a')
        self.assertNotIn(TOKEN, client.naming_error_message(TOKEN))
        self.assertIn('磁盘', client.naming_error_message('中继握手无效：设备名称保存失败。'))


class RelayNameValidationTests(unittest.TestCase):
    def test_server_and_linux_use_same_utf16_boundaries(self):
        for name in ('', ' ', 'a' * 80, '🙂' * 40, '  广州主机  ', '\u3000手机\u3000'):
            self.assertEqual(server.normalize_shared_name(name), client.normalize_device_name(name))
        for name in ('a' * 81, '🙂' * 41, 'a\0b', 'a\u2028b', '\udfff', 'a\u200bb'):
            for normalize in (server.normalize_shared_name, client.normalize_device_name):
                with self.assertRaises(ValueError):
                    normalize(name)

    def test_database_limits_and_invalid_metadata_fail_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'names.json'
            for data in ([], dict(version=2, names={}), dict(version=1, names={'bad-uuid': 'name'})):
                path.write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    server.load_device_names(path)
