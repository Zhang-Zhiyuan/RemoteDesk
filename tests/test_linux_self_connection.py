"""Self-connection guards; owned sockets only, no capture, input, or user state."""
from pathlib import Path
import ctypes
import json
import queue
import socket
import sys
import threading
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app
import remotedesk_linux_device_panel as panel
import remotedesk_linux_devices as devices
import remotedesk_protocol_probe as protocol

LOCAL_ID = "00112233-4455-6677-8899-aabbccddeeff"
OTHER_ID = "10112233-4455-6677-8899-aabbccddeeff"


class SelfConnectionTests(unittest.TestCase):
    def test_normalization_covers_mapped_scoped_and_invalid_addresses(self):
        for value, expected in (("::ffff:192.0.2.7", "192.0.2.7"),
                                ("fe80::7%2", "fe80::7%2"), ("::1", "::1")):
            self.assertEqual(devices.normalized_ip(expected), devices.normalized_ip(value))
        self.assertIsNone(devices.normalized_ip("not-an-ip"))

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux getifaddrs ABI")
    def test_native_interface_inventory_has_both_loopbacks_and_only_addresses(self):
        addresses = devices.local_ip_addresses()
        self.assertIn(devices.normalized_ip("127.0.0.1"), addresses)
        self.assertIn(devices.normalized_ip("::1"), addresses)
        self.assertNotIn(None, addresses)
        self.assertTrue(all(devices.normalized_ip(value) == value for value in addresses))
        # Warmed ABI pointer classes must not grow on every 30-second scan.
        before = len(ctypes._pointer_type_cache)
        for _ in range(10): devices.local_ip_addresses()
        self.assertEqual(before, len(ctypes._pointer_type_cache))

    def test_multihomed_ipv4_ipv6_and_mapped_peers_are_rejected(self):
        own = {devices.normalized_ip(value) for value in ("10.1.2.3", "172.20.4.5", "2001:db8::7", "fe80::9")}
        for peer in ("127.0.0.9", "::1", "0.0.0.0", "::", "10.1.2.3", "172.20.4.5",
                     "::ffff:172.20.4.5", "2001:db8::7", "fe80::9%eth1"):
            with self.subTest(peer=peer), mock.patch.object(devices, "local_ip_addresses", return_value=own):
                sock = mock.Mock()
                sock.getpeername.return_value = (peer, 56565)
                sock.getsockname.return_value = ("198.51.100.17", 49152)
                with self.assertRaises(devices.SelfConnectionError):
                    devices.reject_local_socket(sock)

    def test_equal_actual_peer_and_source_needs_no_inventory_or_dns(self):
        sock = mock.Mock()
        sock.getpeername.return_value = ("::ffff:10.1.2.3", 56565)
        sock.getsockname.return_value = ("10.1.2.3", 49152)
        with mock.patch.object(devices, "local_ip_addresses") as inventory:
            with self.assertRaises(devices.SelfConnectionError):
                devices.reject_local_socket(sock)
            inventory.assert_not_called()

    def test_link_local_scope_distinguishes_interfaces_and_reads_socket_scope(self):
        own = {devices.normalized_ip("fe80::7%2")}
        self.assertFalse(devices.is_local_ip("fe80::7%3", own))
        self.assertTrue(devices.is_local_ip("fe80::7%2", own))
        self.assertTrue(devices.is_local_ip("fe80::7", own))
        with mock.patch.object(devices.socket, "if_nametoindex", return_value=2):
            self.assertTrue(devices.is_local_ip("fe80::7%owned", own))
        sock = mock.Mock()
        sock.getpeername.return_value = ("fe80::7", 56565, 0, 3)
        sock.getsockname.return_value = ("fe80::9", 49152, 0, 3)
        with mock.patch.object(devices, "local_ip_addresses", return_value=own):
            devices.reject_local_socket(sock)
            sock.getpeername.return_value = ("fe80::7", 56565, 0, 2)
            with self.assertRaises(devices.SelfConnectionError): devices.reject_local_socket(sock)

    def test_remote_peer_is_not_rejected_just_because_source_is_local(self):
        sock = mock.Mock()
        sock.getpeername.return_value = ("203.0.113.9", 56565)
        sock.getsockname.return_value = ("192.0.2.9", 49152)
        with mock.patch.object(devices, "local_ip_addresses", return_value={devices.normalized_ip("192.0.2.9")}), \
                mock.patch.object(devices.socket, "getaddrinfo", side_effect=AssertionError("must not resolve names")):
            devices.reject_local_socket(sock)

    def test_unreadable_interfaces_fail_closed_without_mislabeling_as_self(self):
        sock = mock.Mock()
        sock.getpeername.return_value = ("203.0.113.9", 56565)
        sock.getsockname.return_value = ("192.0.2.9", 49152)
        with mock.patch.object(devices, "local_ip_addresses", side_effect=OSError("inventory unavailable")):
            with self.assertRaises(OSError): devices.reject_local_socket(sock)

    def test_local_identity_is_canonical_and_unknown_identity_is_not_self(self):
        with self.assertRaises(devices.SelfConnectionError):
            devices.reject_local_identity(LOCAL_ID.upper(), LOCAL_ID)
        for remote_id, local_id in (("", ""), ("not-an-id", ""), (OTHER_ID, LOCAL_ID)):
            devices.reject_local_identity(remote_id, local_id)

    def _run_with_mock_transport(self, **kwargs):
        viewer = app.ViewerConnection("owned.invalid", 56565, "synthetic-key", queue.Queue(), 7, **kwargs)
        sock = mock.MagicMock()
        sock.__enter__.return_value = sock
        sock.getpeername.return_value = ("127.0.0.1", 56565)
        sock.getsockname.return_value = ("127.0.0.1", 49152)
        with mock.patch.object(app, "find_ffmpeg", return_value=None), \
                mock.patch.object(app.socket, "create_connection", return_value=sock) as direct, \
                mock.patch.object(app.relay, "connect_viewer", return_value=sock) as relayed, \
                mock.patch.object(app, "authenticate", side_effect=PermissionError("fixture auth stop")) as auth:
            viewer._run()
        return viewer, direct, relayed, auth

    def test_default_blocks_before_authentication_and_emits_terminal_event(self):
        viewer, direct, _, auth = self._run_with_mock_transport()
        direct.assert_called_once(); auth.assert_not_called()
        self.assertIn(("viewer_self_rejected", (7, devices.SELF_CONNECTION_MESSAGE)), list(viewer.events.queue))
        self.assertEqual(("viewer_closed", 7), list(viewer.events.queue)[-1])

    def test_explicit_fixture_optin_is_not_enabled_by_default(self):
        viewer, direct, _, auth = self._run_with_mock_transport(allow_self_connection_for_testing=True)
        direct.assert_called_once(); auth.assert_called_once()
        self.assertNotIn("viewer_self_rejected", [name for name, _ in viewer.events.queue])

    def test_known_direct_self_identity_blocks_before_opening_socket(self):
        viewer, direct, relayed, auth = self._run_with_mock_transport(local_device_id=LOCAL_ID, expected_device_id=LOCAL_ID)
        direct.assert_not_called(); relayed.assert_not_called(); auth.assert_not_called()
        self.assertIn("viewer_self_rejected", [name for name, _ in viewer.events.queue])

    def test_relay_local_bridge_is_not_rejected_by_loopback_address(self):
        _, direct, relayed, auth = self._run_with_mock_transport(
            local_device_id=LOCAL_ID, relay_options=SimpleNamespace(device_id=OTHER_ID))
        direct.assert_not_called(); relayed.assert_called_once(); auth.assert_called_once()

    def test_relay_own_target_identity_is_rejected_before_tunnel(self):
        _, direct, relayed, auth = self._run_with_mock_transport(
            local_device_id=LOCAL_ID, relay_options=SimpleNamespace(device_id=LOCAL_ID))
        direct.assert_not_called(); relayed.assert_not_called(); auth.assert_not_called()

    def test_late_authenticated_identity_rejects_hairpin_before_publishing_identity(self):
        viewer = app.ViewerConnection("203.0.113.9", 56565, "fixture", queue.Queue(), 2, local_device_id=LOCAL_ID)
        viewer.identity_requested = True
        with self.assertRaises(devices.SelfConnectionError):
            viewer._handle_control(protocol.encode_device_identity(LOCAL_ID))
        self.assertFalse(any(name == "viewer_device_identity" for name, _ in viewer.events.queue))

    def test_real_loopback_alias_and_ipv6_connections_close_without_auth_bytes(self):
        # An owned listener proves the guard uses the connected address; it does
        # not authenticate, start a host, capture a frame, or inject any input.
        for target in ("127.0.0.1", "localhost", "::ffff:127.0.0.1", "::1"):
            with self.subTest(target=target):
                family = socket.AF_INET6 if target == "::1" else socket.AF_INET
                received, failures = [], []
                with socket.socket(family) as listener:
                    listener.bind(("::1" if family == socket.AF_INET6 else "127.0.0.1", 0))
                    listener.listen(1); listener.settimeout(3)
                    def receive():
                        try:
                            with listener.accept()[0] as accepted:
                                accepted.settimeout(3); received.append(accepted.recv(4096))
                        except Exception as error: failures.append(str(error))
                    worker = threading.Thread(target=receive, daemon=True); worker.start()
                    viewer = app.ViewerConnection(target, listener.getsockname()[1], "never-sent-test-key", queue.Queue(), 3)
                    with mock.patch.object(app, "find_ffmpeg", return_value=None), mock.patch.object(app, "authenticate") as auth:
                        viewer._run()
                    worker.join(4)
                    self.assertFalse(worker.is_alive()); self.assertEqual([], failures)
                    self.assertEqual([b""], received); auth.assert_not_called()
                    self.assertIn("viewer_self_rejected", [name for name, _ in viewer.events.queue])

    def test_explicit_scan_never_probes_own_loopback_or_interface(self):
        for target in ("127.0.0.1", "10.1.2.3"):
            with self.subTest(target=target), \
                    mock.patch.object(devices, "interfaces", return_value=({"255.255.255.255"}, {target})), \
                    mock.patch.object(devices, "local_ip_addresses", return_value={devices.normalized_ip(target)}), \
                    mock.patch.object(devices.socket, "getaddrinfo", return_value=[(2, 2, 17, "", (target, 0))]), \
                    mock.patch.object(devices.socket, "socket") as opened:
                self.assertEqual([], devices.Scanner().scan(target, seconds=.01))
                opened.assert_not_called()

    def test_discovery_does_not_return_own_identity_even_through_other_address(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as responder, socket.socket() as listener:
            responder.bind(("127.0.0.1", 0)); responder.settimeout(2)
            listener.bind(("127.0.0.1", 0)); listener.listen(1); listener.settimeout(.05)
            advertised_port = listener.getsockname()[1]
            errors = []
            def respond():
                try:
                    _, peer = responder.recvfrom(8192)
                    responder.sendto(json.dumps(dict(Type="RemoteDesk.Discover.Response.v1", Port=advertised_port,
                                                     DeviceId=LOCAL_ID, IsHostRunning=True)).encode(), peer)
                except Exception as error: errors.append(str(error))
            worker = threading.Thread(target=respond, daemon=True); worker.start()
            scanner = devices.Scanner(local_device_id=LOCAL_ID, allow_self_connection_for_testing=True)
            with mock.patch.object(devices, "interfaces", return_value=(set(), set())):
                found = scanner.scan("127.0.0.1", discovery_ports=(responder.getsockname()[1],), host_ports=(advertised_port,), seconds=.15)
            worker.join(3)
            self.assertFalse(worker.is_alive()); self.assertEqual([], errors); self.assertEqual([], found)
            # The fallback must not erase a known local ID and re-add this
            # endpoint as an anonymous RDK1 device, even behind a forwarder.
            with self.assertRaises(socket.timeout): listener.accept()

    def test_saved_device_panel_preserves_known_identity_for_guard(self):
        device_panel = panel.DevicePanel.__new__(panel.DevicePanel)
        device_panel.closed = False; device_panel.selected_id = ""
        device_panel.book = devices.Book()
        saved = device_panel.book.remember("pc.invalid", 56565, "fixture", device_id=LOCAL_ID, auto_port=False)
        device_panel.selected_id = saved.id
        device_panel.app = SimpleNamespace(
            viewer=None, viewer_host=mock.Mock(), viewer_port=mock.Mock(), viewer_password=mock.Mock(),
            _begin_direct_viewer=mock.Mock())
        device_panel.app.viewer_host.get.return_value = "pc.invalid"
        device_panel.app.viewer_port.get.return_value = "56565"
        device_panel.app.viewer_password.get.return_value = "fixture"
        device_panel.connect()
        device_panel.app._begin_direct_viewer.assert_called_once_with("pc.invalid", 56565, "fixture", device_id=LOCAL_ID)
        device_panel.app._begin_direct_viewer.reset_mock()
        device_panel.app.viewer_host.get.return_value = "different-pc.invalid"
        device_panel.connect()
        device_panel.app._begin_direct_viewer.assert_called_once_with("different-pc.invalid", 56565, "fixture", device_id="")
        self.assertEqual("", device_panel.selected_id)

    def test_selected_self_device_never_prompts_for_password(self):
        device_panel = panel.DevicePanel.__new__(panel.DevicePanel)
        device_panel.app = SimpleNamespace(relay_device_id=LOCAL_ID, root=None)
        device_panel.status = mock.Mock(); device_panel.fill_selection = mock.Mock()
        device_panel.selection = mock.Mock(return_value=(None, devices.Device("203.0.113.9", 56565, device_id=LOCAL_ID)))
        with mock.patch.object(panel.simpledialog, "askstring") as prompt:
            device_panel.connect_selected()
        prompt.assert_not_called(); device_panel.fill_selection.assert_not_called()
        device_panel.status.config.assert_called_once_with(text=devices.SELF_CONNECTION_MESSAGE)

    def test_dns_failure_is_not_a_terminal_self_rejection(self):
        viewer = app.ViewerConnection("owned.invalid", 56565, "fixture", queue.Queue(), 4)
        with mock.patch.object(app, "find_ffmpeg", return_value=None), \
                mock.patch.object(app.socket, "create_connection", side_effect=socket.gaierror("fixture DNS failure")), \
                mock.patch.object(app, "authenticate") as auth:
            viewer._run()
        auth.assert_not_called()
        self.assertIn(("viewer_error", (4, "fixture DNS failure")), list(viewer.events.queue))
        self.assertNotIn("viewer_self_rejected", [name for name, _ in viewer.events.queue])

    def test_app_attempt_enables_guard_for_initial_and_reconnect_paths(self):
        for reconnecting in (False, True):
            controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
            controller.closing = False; controller.viewer_reconnect_policy = app.ViewerReconnectPolicy()
            controller.viewer_reconnect_policy.begin(); controller.viewer_generation = 5
            controller.events = queue.Queue(); controller.frame_label = None
            controller.connect_button = mock.Mock(); controller.disconnect_button = mock.Mock()
            controller.relay_device_id = LOCAL_ID; controller.viewer_reconnect_device_id = OTHER_ID
            controller._open_viewer_window = mock.Mock(); controller._viewer_capture_state = mock.Mock()
            controller._apply_viewer_capture_target_transition = mock.Mock(); controller._set_viewer_status = mock.Mock()
            with mock.patch.object(app.ViewerConnection, "start"), \
                    mock.patch.object(app, "ViewerConnection", wraps=app.ViewerConnection) as factory:
                controller._start_viewer_attempt("203.0.113.9", 56565, "fixture", reconnecting=reconnecting)
                self.assertFalse(controller.viewer.allow_self_connection_for_testing)
                self.assertEqual(LOCAL_ID, controller.viewer.local_device_id)
                self.assertEqual(OTHER_ID, controller.viewer.expected_device_id)
                self.assertNotIn("allow_self_connection_for_testing", factory.call_args.kwargs)

    def test_rejection_cancels_only_current_logical_session_and_can_start_again(self):
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        controller.viewer_reconnect_policy.begin(); controller.viewer_reconnect_policy.mark_device_info()
        controller.viewer_reconnect_target = ("localhost", 56565, "fixture")
        controller._cancel_viewer_stability_timer = mock.Mock(); controller._cancel_viewer_reconnect_timer = mock.Mock()
        controller._set_viewer_status = mock.Mock()
        controller._reject_self_viewer(devices.SELF_CONNECTION_MESSAGE)
        self.assertIsNone(controller.viewer_reconnect_policy.next_delay())
        self.assertIsNone(controller.viewer_reconnect_target)
        controller._cancel_viewer_reconnect_timer.assert_called_once()
        controller.viewer_reconnect_policy.begin(); controller.viewer_reconnect_policy.mark_device_info()
        self.assertIsNotNone(controller.viewer_reconnect_policy.next_delay())

    def test_known_saved_self_device_stops_before_window_or_connection(self):
        for reconnecting in (False, True):
            controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
            controller.closing = False; controller.viewer_reconnect_policy = app.ViewerReconnectPolicy()
            controller.viewer_reconnect_policy.begin(); controller.relay_device_id = LOCAL_ID
            controller.viewer_reconnect_device_id = LOCAL_ID; controller._reject_self_viewer = mock.Mock()
            controller._open_viewer_window = mock.Mock()
            with mock.patch.object(app, "ViewerConnection") as factory:
                controller._start_viewer_attempt("changed-address.invalid", 56565, "fixture", reconnecting=reconnecting)
                factory.assert_not_called(); controller._open_viewer_window.assert_not_called()
                controller._reject_self_viewer.assert_called_once_with(devices.SELF_CONNECTION_MESSAGE)

    def test_terminal_self_event_survives_bounded_ui_queue_pressure(self):
        events = queue.Queue(maxsize=2)
        events.put(("viewer_frame", (9, "old"))); events.put(("viewer_status", (9, "noise")))
        app.put_ui_event(events, "viewer_self_rejected", (9, devices.SELF_CONNECTION_MESSAGE))
        self.assertIn(("viewer_self_rejected", (9, devices.SELF_CONNECTION_MESSAGE)), list(events.queue))


if __name__ == "__main__":
    unittest.main()
