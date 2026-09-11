from pathlib import Path
import json
import socket
import sys
import tempfile
import threading
import unittest
from unittest import mock
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_devices as devices
import remotedesk_protocol_probe as protocol
import remotedesk_linux_app as app

ID = "00112233-4455-6677-8899-aabbccddeeff"


class DeviceModelTests(unittest.TestCase):
    def test_endpoints(self):
        for raw, port, expected in [("pc.", "", ("pc", 56565, False)), ("pc:45678", "", ("pc", 45678, True)),
                                    ("[::1]:45678", "56565", ("::1", 45678, True)), ("::1", "", ("::1", 56565, False))]:
            self.assertEqual(expected, devices.endpoint(raw, port))
        for raw, port in [(".", ""), ("https://pc", ""), ("pc:", ""), ("pc", "65536")]:
            with self.assertRaises(ValueError): devices.endpoint(raw, port)

    def test_identity_merges_changed_endpoint_with_note_and_latest_password(self):
        book = devices.Book()
        old = book.remember("old", 56565, "old-secret", "PC", ID, remark="工作机")
        book.remember("new", 45678, "new-secret", "PC", ID.upper())
        restored = devices.Book.decode(book.encode())
        self.assertEqual(1, len(restored.nodes)); node = restored.nodes[0]
        self.assertEqual((old.id, "工作机", "new", 45678, "new-secret", ID),
                         (node.id, node.remark, node.host, node.port, node.password, node.device_id))
        self.assertNotIn("new-secret", repr(node))

    def test_name_is_not_identity_and_deleted_node_stays_deleted(self):
        book = devices.Book()
        old = book.remember("one", 56565, "one", "PC", ID)
        book.remember("two", 56565, "two", "PC", str(uuid.uuid4()))
        self.assertEqual(2, len(book.nodes)); book.remove(old.id)
        self.assertIsNone(book.remember("new", 45678, "one", device_id=ID, previous_id=old.id))
        self.assertEqual(1, len(book.nodes))

    def test_reused_endpoint_keeps_distinct_authenticated_machines_after_restart(self):
        book = devices.Book()
        first = book.remember("pc", 56565, "first-secret", device_id=ID, remark="第一台")
        other_id = str(uuid.uuid4())
        second = book.remember("pc", 56565, "second-secret", device_id=other_id)
        restored = devices.Book.decode(book.encode())
        self.assertEqual(2, len(restored.nodes))
        self.assertEqual("第一台", restored.find(first.id).remark)
        self.assertEqual("first-secret", restored.find(first.id).password)
        self.assertEqual("", restored.find(second.id).remark)
        self.assertEqual(other_id, restored.find(second.id).device_id)

    def test_previous_node_is_not_rewritten_when_authenticated_identity_changes(self):
        book = devices.Book()
        first = book.remember("old", 56565, "first-secret", device_id=ID, remark="第一台")
        second = book.remember("new", 45678, "second-secret", device_id=str(uuid.uuid4()), previous_id=first.id)
        self.assertEqual(2, len(book.nodes))
        self.assertNotEqual(first.id, second.id)
        self.assertEqual(first, book.find(first.id))
        self.assertEqual("", second.remark)

    def test_manual_endpoint_update_does_not_bridge_conflicting_identities(self):
        book = devices.Book()
        first = book.remember("pc", 56565, "first", device_id=ID, remark="保留")
        second = book.remember("pc", 56565, "second", device_id=str(uuid.uuid4()))
        updated = book.remember("pc", 56565, "manual")
        self.assertEqual(2, len(book.nodes))
        self.assertEqual(second.id, updated.id)
        self.assertEqual(second.device_id, updated.device_id)
        self.assertEqual(first, book.find(first.id))

    def test_manual_add_deduplicates_endpoint_and_retains_note(self):
        book = devices.Book(); old = book.remember("PC.", 45678, "old", remark="备注", auto_port=False)
        book.remember("pc", 45678, "new", auto_port=False)
        self.assertEqual(1, len(book.nodes)); self.assertEqual(old.id, book.nodes[0].id)
        self.assertEqual("备注", book.nodes[0].remark); self.assertFalse(book.nodes[0].auto_port)

    def test_corrupt_and_future_book_do_not_silently_reset(self):
        book = devices.Book(); book.remember("one", 56565, "test")
        value = json.loads(book.encode()); value["nodes"].append(dict(value["nodes"][0], host="two"))
        for raw in (b"broken", b'{"version":2,"nodes":[]}', json.dumps(value).encode()):
            with self.assertRaises(ValueError): devices.Book.decode(raw)

    def test_identity_wire_and_strict_consumption(self):
        payload = protocol.encode_device_identity(ID)
        self.assertEqual(bytes([34, 36]) + ID.encode(), payload)
        self.assertEqual(ID, protocol.decode_control(payload)["deviceId"])
        self.assertEqual(33, protocol.decode_control(bytes([33]))["kind"])
        for bad in (bytes([33, 0]), payload + bytes([0]), bytes([34, 1, 120])):
            with self.assertRaises((protocol.ProtocolError, ValueError)): protocol.decode_control(bad)

    def test_viewer_negotiates_identity_once_without_changing_legacy_handshake(self):
        for supported in (False, True):
            viewer = object.__new__(app.ViewerConnection)
            viewer._send_control = mock.Mock(); viewer._put_event = mock.Mock()
            info = protocol.encode_device_info("PC", "Windows", protocol.CAPABILITY_DEVICE_IDENTITY if supported else 0)
            viewer._handle_control(info); viewer._handle_control(info)
            if supported:
                viewer._send_control.assert_called_once_with(bytes([33]))
                viewer._handle_control(protocol.encode_device_identity(ID))
                self.assertEqual(ID, viewer.remote_device_info["deviceId"])
                viewer._put_event.assert_called_with("viewer_device_identity", viewer.remote_device_info)
            else:
                viewer._send_control.assert_not_called()
                viewer._handle_control(protocol.encode_device_identity(ID))
                self.assertFalse(viewer.remote_device_info["deviceId"])

    def test_discovery_treats_source_as_address_and_validates_metadata(self):
        value = dict(Type="RemoteDesk.Discover.Response.v1", Port=45678, MachineName="PC", Address="evil",
                     DeviceId=ID, IsHostRunning=True)
        found = devices.parse_response(json.dumps(value).encode(), "10.0.0.8")
        self.assertEqual(("10.0.0.8", 45678, ID), (found.host, found.port, found.device_id))
        for mutation in (dict(Port=True), dict(Port=65536), dict(IsHostRunning="true")):
            self.assertIsNone(devices.parse_response(json.dumps(dict(value, **mutation)).encode(), "10.0.0.8"))
        self.assertIsNone(devices.parse_response(b"x" * 8193, "10.0.0.8"))

    def test_explicit_udp_detects_custom_port_without_credentials(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as responder:
            responder.bind(("127.0.0.1", 0)); responder.settimeout(2)
            requests = []; errors = []
            def serve():
                try:
                    data, peer = responder.recvfrom(8192); requests.append(data)
                    responder.sendto(json.dumps(dict(Type="RemoteDesk.Discover.Response.v1", Port=45678, DeviceId=ID)).encode(), peer)
                except Exception as error: errors.append(error)
            thread = threading.Thread(target=serve); thread.start()
            scanner = devices.Scanner()
            with mock.patch.object(devices, "interfaces", return_value=(set(), set())):
                found = scanner.scan("127.0.0.1", discovery_ports=(responder.getsockname()[1],), seconds=.3)
            thread.join(3)
            self.assertFalse(errors); self.assertEqual([devices.REQUEST], requests)
            self.assertEqual(45678, found[0].port); self.assertEqual(ID, found[0].device_id)

    def test_tcp_fallback_only_reads_product_banner(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0)); listener.listen(); listener.settimeout(3)
            received = []; errors = []
            def serve():
                try:
                    peer, _ = listener.accept()
                    with peer:
                        peer.settimeout(2); peer.sendall(b"RDK1"); received.append(peer.recv(1))
                except Exception as error: errors.append(error)
            thread = threading.Thread(target=serve); thread.start()
            with mock.patch.object(devices, "interfaces", return_value=(set(), set())):
                found = devices.Scanner().scan("127.0.0.1", discovery_ports=(), host_ports=(listener.getsockname()[1],), seconds=.05)
            thread.join(3)
            self.assertFalse(errors); self.assertEqual([b""], received); self.assertEqual(1, len(found))
            self.assertFalse(found[0].advertised)


@unittest.skipUnless(sys.platform.startswith("linux"), "Unix private-file permissions")
class DeviceStoreTests(unittest.TestCase):
    def test_encrypted_restart_and_corruption_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            store = devices.Store(Path(folder) / "private"); book = devices.Book()
            book.remember("pc", 56565, "test-sensitive-password", remark="中文")
            store.save(book); raw = (store.directory / "book").read_bytes()
            self.assertNotIn(b"test-sensitive-password", raw)
            self.assertEqual("中文", devices.Store(store.directory).load().nodes[0].remark)
            self.assertEqual(0o700, store.directory.stat().st_mode & 0o777)
            for name in ("book", "key"): self.assertEqual(0o600, (store.directory / name).stat().st_mode & 0o777)
            (store.directory / "book").write_bytes(b"corrupt")
            with self.assertRaises(Exception): store.load()
            self.assertEqual(b"corrupt", (store.directory / "book").read_bytes())

    def test_local_identity_is_private_and_stable(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder) / "private"
            first = devices.local_device_id(directory)
            self.assertTrue(devices.identity(first)); self.assertEqual(first, devices.local_device_id(directory))
            self.assertEqual(0o600, (directory / "identity").stat().st_mode & 0o777)


if __name__ == "__main__": unittest.main()
