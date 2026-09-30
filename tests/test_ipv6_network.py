from pathlib import Path
import json
import socket
import sys
import threading
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts/linux"))
sys.path.insert(0, str(ROOT / "scripts/relay"))
import remotedesk_linux_devices as devices
import remotedesk_linux_host as host
import remotedesk_linux_relay as relay
import remotedesk_relay_server as server


class Ipv6NetworkTests(unittest.TestCase):
    def test_reports_are_normalized_identically_and_reject_foreign_scopes(self):
        values = ["2001:0DB8:0:0:0:0:0:1", "2001:db8::1", "fd12::ABCD", "192.0.2.1"]
        report = dict(directAddresses=values, directPort=40565)
        expected = ["2001:db8::1", "fd12::abcd", "192.0.2.1"]
        self.assertEqual((expected, 40565), server.normalize_address_report(report))
        self.assertEqual(dict(directAddresses=expected, directPort=40565), relay.normalize_address_report(report))
        for value in ["::", "::1", "::2", "::ffff:192.0.2.1", "fe80::1", "fe80::1%19", "2001:db8::1%19",
                      "ff02::1", "fec0::1", "[2001:db8::1]", "2001:db8::1\n", "host.invalid"]:
            with self.subTest(value=value):
                self.assertEqual(([], 0), server.normalize_address_report(dict(directAddresses=[value], directPort=40565)))
        self.assertIn("[2001:db8::1]:40565", relay.direct_address_display(report))

    def test_dual_stack_host_listener_accepts_both_families(self):
        self.assertTrue(socket.has_dualstack_ipv6(), "This runtime fixture requires IPv6")
        with host.create_host_listener("0.0.0.0", 0) as listener:
            listener.settimeout(3)
            port = listener.getsockname()[1]
            for address in ("127.0.0.1", "::1"):
                with devices.connect_tcp(address, port, timeout=3) as client:
                    accepted, _ = listener.accept()
                    with accepted:
                        accepted.sendall("IPv6 中文".encode())
                        self.assertEqual("IPv6 中文".encode(), client.recv(1024))

    def test_many_virtual_adapters_do_not_hide_ipv6(self):
        report = dict(directAddresses=[f"192.0.2.{i}" for i in range(1, 25)] + ["2001:db8::1"], directPort=40565)
        addresses, port = server.normalize_address_report(report)
        self.assertEqual(8, len(addresses)); self.assertEqual("2001:db8::1", addresses[-1])
        self.assertEqual(dict(directAddresses=addresses, directPort=port), relay.normalize_address_report(report))

    def test_explicit_ipv4_bind_and_disabled_ipv6_keep_working(self):
        with mock.patch.object(socket, "has_dualstack_ipv6", return_value=False):
            with host.create_host_listener("0.0.0.0", 0) as listener:
                self.assertEqual(socket.AF_INET, listener.family)
        with host.create_host_listener("127.0.0.1", 0) as listener:
            self.assertEqual("127.0.0.1", listener.getsockname()[0])

    def test_failed_ipv6_falls_back_and_cancel_does_not_resolve(self):
        with socket.create_server(("127.0.0.1", 0)) as listener:
            port = listener.getsockname()[1]
            answers = [(socket.AF_INET6, socket.SOCK_STREAM, 6, "", ("::1", port, 0, 0)),
                       (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("127.0.0.1", port))]
            with mock.patch.object(socket, "getaddrinfo", return_value=answers):
                with devices.connect_tcp("fixture.invalid", port, timeout=3) as client:
                    self.assertEqual("127.0.0.1", client.getpeername()[0])
        stop = threading.Event(); stop.set()
        with mock.patch.object(socket, "getaddrinfo", side_effect=AssertionError("cancelled DNS")):
            with self.assertRaisesRegex(OSError, "取消"): devices.connect_tcp("fixture.invalid", 1, stop_event=stop)

    def test_ipv6_udp_discovery_and_tcp_fallback_use_real_sockets(self):
        with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as udp:
            udp.bind(("::1", 0)); udp.settimeout(3)
            errors = []
            def respond():
                try:
                    request, endpoint = udp.recvfrom(8192)
                    self.assertEqual(devices.REQUEST, request)
                    udp.sendto(json.dumps(dict(Type="RemoteDesk.Discover.Response.v1", MachineName="IPv6 fixture",
                        Port=12345, IsHostRunning=True, Platform="Linux")).encode(), endpoint)
                except Exception as error: errors.append(error)
            worker = threading.Thread(target=respond); worker.start()
            found = devices.Scanner(allow_self_connection_for_testing=True).scan("::1", discovery_ports=(udp.getsockname()[1],),
                host_ports=(12345,), seconds=.4)
            worker.join(4)
            self.assertFalse(worker.is_alive()); self.assertEqual([], errors)
            self.assertTrue(any(d.host == "::1" and d.port == 12345 for d in found))
        with socket.create_server(("::1", 0), family=socket.AF_INET6) as listener:
            listener.settimeout(3)
            def banner():
                peer, _ = listener.accept()
                with peer: peer.sendall(b"RDK1")
            worker = threading.Thread(target=banner); worker.start()
            found = devices.Scanner(allow_self_connection_for_testing=True).scan("::1", discovery_ports=(),
                host_ports=(listener.getsockname()[1],), seconds=0)
            worker.join(4)
            self.assertFalse(worker.is_alive())
            self.assertTrue(any(d.host == "::1" and d.port == listener.getsockname()[1] for d in found))

    def test_host_ipv6_discovery_socket_can_receive_unicast(self):
        sockets = host.create_discovery_sockets("::1", 0)
        try:
            with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as client:
                client.sendto(devices.REQUEST, sockets[0].getsockname())
                self.assertEqual(devices.REQUEST, sockets[0].recvfrom(1024)[0])
        finally:
            for sock in sockets: sock.close()
