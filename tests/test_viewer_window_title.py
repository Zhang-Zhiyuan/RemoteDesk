import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app


class ViewerWindowTitleTests(unittest.TestCase):
    def test_direct_names_and_ipv6_are_unambiguous(self):
        self.assertEqual("RemoteDesk - PC (10.0.0.2:4567) · IP 直连",
                         app.format_viewer_window_title("10.0.0.2", 4567, "PC"))
        self.assertEqual("RemoteDesk - [::1]:56565 · IP 直连",
                         app.format_viewer_window_title("::1", 56565))
        self.assertEqual("RemoteDesk - [::1]:56565 · IP 直连",
                         app.format_viewer_window_title("[::1]", 56565))

    def test_relay_does_not_display_server_as_the_remote_device(self):
        self.assertEqual("RemoteDesk - 测试电脑 · 公网中继",
                         app.format_viewer_window_title("relay.invalid", 56567, "测试电脑", "device-a"))
        self.assertEqual("RemoteDesk - device-a · 公网中继",
                         app.format_viewer_window_title("relay.invalid", 56567, "", "device-a"))

    def test_untrusted_labels_are_single_line_and_bounded(self):
        self.assertEqual("RemoteDesk - AB · 公网中继",
                         app.format_viewer_window_title("unused", 1, "\nA\u202eB\u2028", "a"))
        self.assertLess(len(app.format_viewer_window_title("unused", 1, "😀" * 1000, "a")), 150)

    def test_active_relay_identity_wins_over_another_selected_device(self):
        ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui.viewer_relay_options = SimpleNamespace(device_id="active")
        ui.viewer_reconnect_target = ("relay.invalid", 56567, "not-for-display")
        ui.relay_devices = {"active": {"machineName": "PC", "sharedName": "工作站"},
                            "selected": {"machineName": "Wrong target"}}
        ui.viewer_window = mock.Mock()
        ui._refresh_viewer_window_title({"machineName": "Original PC"})
        ui.viewer_window.title.assert_called_once_with("RemoteDesk - 工作站 · 公网中继")

    def test_direct_title_uses_frozen_connection_and_authenticated_name(self):
        ui = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        ui.viewer_reconnect_target = ("10.0.0.2", 4567, "not-for-display")
        ui.viewer_host = mock.Mock()
        ui.viewer_window = mock.Mock()
        ui._refresh_viewer_window_title({"machineName": "New name"})
        ui.viewer_window.title.assert_called_once_with("RemoteDesk - New name (10.0.0.2:4567) · IP 直连")
        ui.viewer_host.get.assert_not_called()
