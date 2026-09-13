from __future__ import annotations

import json
import sys
import threading
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app


class LinuxUpscalingTests(unittest.TestCase):
    def presenter(self):
        presenter = app.MpvNativeH264Presenter.__new__(app.MpvNativeH264Presenter)
        presenter.upscale_lock = threading.Lock()
        presenter.state_lock = threading.Lock()
        presenter.experimental_upscaling = False
        presenter.original_scale_properties = None
        presenter.activation_failure = ""
        presenter.ipc_socket_path = "owned-test.sock"
        return presenter

    def test_toggle_restores_actual_previous_properties(self):
        presenter = self.presenter()
        original = {"scale": "lanczos", "scale-antiring": 0.25, "cscale": "bilinear"}
        state = original.copy()
        def set_properties(_path, values):
            state.update(values)
            return True
        with mock.patch.object(app.MpvNativeH264Presenter, "is_native_surface_active", new_callable=mock.PropertyMock, return_value=True), \
             mock.patch.object(app, "query_mpv_ipc_properties", side_effect=lambda *_: state.copy()), \
             mock.patch.object(app, "set_mpv_ipc_properties", side_effect=set_properties):
            for _ in range(3):
                self.assertTrue(presenter.set_experimental_upscaling(True))
                self.assertEqual({"scale": "catmull_rom", "scale-antiring": 1.0, "cscale": "bilinear"}, state)
                self.assertTrue(presenter.experimental_upscaling)
                self.assertTrue(presenter.set_experimental_upscaling(False))
                self.assertEqual(original, state)
                self.assertFalse(presenter.experimental_upscaling)

    def test_missing_snapshot_does_not_modify_renderer(self):
        with mock.patch.object(app.MpvNativeH264Presenter, "is_native_surface_active", new_callable=mock.PropertyMock, return_value=True), \
             mock.patch.object(app, "query_mpv_ipc_properties", return_value={"scale": "bilinear"}), \
             mock.patch.object(app, "set_mpv_ipc_properties") as setter:
            self.assertFalse(self.presenter().set_experimental_upscaling(True))
            setter.assert_not_called()

    def test_partial_failure_restores_both_properties(self):
        presenter = self.presenter()
        original = {"scale": "bilinear", "scale-antiring": 0.0, "cscale": "bilinear"}
        with mock.patch.object(app.MpvNativeH264Presenter, "is_native_surface_active", new_callable=mock.PropertyMock, return_value=True), \
             mock.patch.object(app, "query_mpv_ipc_properties", return_value=original), \
             mock.patch.object(app, "set_mpv_ipc_properties", side_effect=[False, True]) as setter:
            self.assertFalse(presenter.set_experimental_upscaling(True))
            self.assertEqual(original, setter.call_args.args[1])
            self.assertFalse(presenter.experimental_upscaling)
            self.assertEqual("", presenter.activation_failure)

    def test_failed_rollback_requests_existing_renderer_recovery(self):
        presenter = self.presenter()
        with mock.patch.object(app.MpvNativeH264Presenter, "is_native_surface_active", new_callable=mock.PropertyMock, return_value=True), \
             mock.patch.object(app, "query_mpv_ipc_properties", return_value={"scale": "bilinear", "scale-antiring": 0.0, "cscale": "bilinear"}), \
             mock.patch.object(app, "set_mpv_ipc_properties", return_value=False):
            self.assertFalse(presenter.set_experimental_upscaling(True))
            self.assertIn("切回原版", presenter.activation_failure)

    def test_software_and_jpeg_paths_are_not_modified(self):
        with mock.patch.object(app.MpvNativeH264Presenter, "is_native_surface_active", new_callable=mock.PropertyMock, return_value=False), \
             mock.patch.object(app, "set_mpv_ipc_properties") as setter:
            self.assertFalse(self.presenter().set_experimental_upscaling(True))
            setter.assert_not_called()

    def test_ipc_requires_every_ack_and_handles_fragmented_replies(self):
        fake = mock.MagicMock()
        fake.__enter__.return_value = fake
        fake.recv.side_effect = [b'{"request_id":1,"err', b'or":"success"}\n',
            b'{"event":"idle"}\n{"request_id":2,"error":"success"}\n']
        with mock.patch.object(app.socket, "AF_UNIX", 1, create=True), mock.patch.object(app.socket, "socket", return_value=fake):
            self.assertTrue(app.set_mpv_ipc_properties("test.sock", {"scale": "catmull_rom", "scale-antiring": 1}))
        requests = [json.loads(line) for line in fake.sendall.call_args.args[0].splitlines()]
        self.assertEqual(["set_property", "scale", "catmull_rom"], requests[0]["command"])

    def test_ipc_rejection_is_not_reported_as_success(self):
        fake = mock.MagicMock()
        fake.__enter__.return_value = fake
        fake.recv.return_value = b'{"request_id":1,"error":"property unavailable"}\n'
        with mock.patch.object(app.socket, "AF_UNIX", 1, create=True), mock.patch.object(app.socket, "socket", return_value=fake):
            self.assertFalse(app.set_mpv_ipc_properties("test.sock", {"scale": "catmull_rom"}))


if __name__ == "__main__":
    unittest.main()
