from pathlib import Path
import asyncio
import json
import sys
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
sys.path.insert(0, str(ROOT / "scripts/relay"))
import remotedesk_linux_relay as client
import remotedesk_relay_server as server


class RelayAddressReportTests(unittest.TestCase):
    def assert_report(self, report, addresses, port):
        self.assertEqual((addresses, port), server.normalize_address_report(report))
        self.assertEqual(dict(directAddresses=addresses, directPort=port), client.normalize_address_report(report))

    def test_literal_validation_is_identical_on_client_and_server(self):
        valid = ["10.1.2.3", "192.168.1.2", "100.64.0.1", "198.51.100.42"]
        invalid = [None, {}, 42, "", "localhost", "127.0.0.1", "0.1.2.3", "169.254.1.2", "224.0.0.1",
                   "255.255.255.255", "::1", "::ffff:10.1.2.3", "010.1.2.3", "10.1.2.3:1234", "10.1.2.3\n", "127.1", "0x7f000001"]
        for value in valid:
            with self.subTest(value=value): self.assert_report(dict(directAddresses=[value], directPort=40565), [value], 40565)
        for value in invalid:
            with self.subTest(value=value): self.assert_report(dict(directAddresses=[value], directPort=40565), [], 0)

    def test_malformed_or_legacy_optional_data_is_nonfatal(self):
        self.assert_report({}, [], 0)
        for port in (True, False, None, "56565", 56565.0, 0, 65536, -1):
            self.assert_report(dict(directAddresses=["192.0.2.3"], directPort=port), [], 0)
        for addresses in (None, {}, "192.0.2.3", [None]):
            self.assert_report(dict(directAddresses=addresses, directPort=56565), [], 0)

    def test_bounded_deduplicated_report(self):
        values = ["10.1.2.3", "10.1.2.3", "bad"] + [f"192.0.2.{i}" for i in range(1, 50)]
        self.assert_report(dict(directAddresses=values, directPort=56565), ["10.1.2.3"] + [f"192.0.2.{i}" for i in range(1, 8)], 56565)

    def test_local_enumeration_has_no_dns_dependency_and_tolerates_missing_platform_api(self):
        with mock.patch.object(client.socket, "getaddrinfo", side_effect=AssertionError("must not use DNS")), \
                mock.patch.object(client.socket, "if_nameindex", side_effect=OSError("network changed"), create=True):
            self.assertEqual([], client.local_direct_addresses())

    def test_display_never_treats_nat_source_as_host_port(self):
        self.assertIn("仍可中转", client.direct_address_display(dict(publicAddress="198.51.100.9", publicPort=1234)))
        self.assertEqual("192.0.2.3:40565", client.direct_address_display(dict(directAddresses=["192.0.2.3"], directPort=40565)))


class RelayAddressDirectoryTests(unittest.IsolatedAsyncioTestCase):
    async def test_pagination_keeps_maximum_size_unicode_metadata_below_frame_limit(self):
        service = server.RelayServer(dict(access_token="a" * 64, cert_file="unused", key_file="unused"))
        for index in range(512):
            writer = mock.Mock()
            writer.is_closing.return_value = False
            device_id = str(__import__("uuid").UUID(int=index + 1))
            host = server.HostConnection(device_id, "桌面😀" * 40, "手机😀" * 13, None, None, writer)
            host.update_addresses(dict(directAddresses=[f"192.168.100.{n}" for n in range(100, 108)], directPort=56565))
            service.hosts[device_id] = host
        collected = []
        for offset in range(0, 512, 32):
            writer = mock.Mock(drain=mock.AsyncMock())
            await service.handle_directory(writer, dict(pageSize=32, offset=offset))
            frame = writer.write.call_args.args[0]
            self.assertLessEqual(len(frame) - 4, server.MAX_HANDSHAKE_BYTES)
            page = json.loads(frame[4:])
            collected.extend(item["deviceId"] for item in page["devices"])
            self.assertEqual(offset + 32 if offset < 480 else None, page["nextOffset"])
        self.assertEqual(512, len(set(collected)))

    async def test_client_pagination_deduplicates_and_preserves_legacy_response(self):
        options = client.RelayOptions("example.test", 56567, "a" * 64, "ab" * 32,
                                      "8220b49b-0f2f-4f87-913a-c3f95091500f")
        first = dict(deviceId="8220b49b-0f2f-4f87-913a-c3f95091500f", machineName="first",
                     directAddresses=["192.0.2.3"], directPort=56565)
        second = dict(first, deviceId="9220b49b-0f2f-4f87-913a-c3f95091500f", machineName="second")
        with mock.patch.object(client, "_directory_page", new=mock.AsyncMock(side_effect=[
            dict(devices=[first], nextOffset=32), dict(devices=[first, second])])) as page:
            result = await client.list_devices_async(options)
            self.assertEqual(2, len(result))
            self.assertEqual([0, 32], [call.args[1] for call in page.call_args_list])
        for cursor in (True, "32", 32.0, 0, -1, 512):
            with mock.patch.object(client, "_directory_page", new=mock.AsyncMock(return_value=dict(devices=[], nextOffset=cursor))):
                with self.assertRaises(ValueError): await client.list_devices_async(options)

    async def test_shared_directory_deadline_cancels_stalled_page(self):
        async def stall(*_):
            await asyncio.Event().wait()
        options = client.RelayOptions("example.test", 56567, "a" * 64, "ab" * 32,
                                      "8220b49b-0f2f-4f87-913a-c3f95091500f")
        with mock.patch.object(client, "TIMEOUT", .02), mock.patch.object(client, "_directory_page", side_effect=stall):
            with self.assertRaises(TimeoutError): await client.list_devices_async(options)
