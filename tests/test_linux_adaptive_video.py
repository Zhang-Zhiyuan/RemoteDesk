from pathlib import Path
import sys
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_host as host
import remotedesk_protocol_probe as protocol


class AdaptiveVideoTests(unittest.TestCase):
    def test_control_reply_precedes_the_next_frame_after_a_blocked_write(self):
        priority = host.HostWritePriority()
        order = []
        def write(label, video):
            with priority.enter(video):
                order.append(label)
        with priority.enter(True):
            video = threading.Thread(target=write, args=("next-frame", True))
            control = threading.Thread(target=write, args=("clipboard-ack", False))
            video.start()
            control.start()
            with priority.condition:
                self.assertTrue(priority.condition.wait_for(lambda: priority.waiting_controls == 1, timeout=2))
            self.assertEqual([], order)
        for thread in (video, control):
            thread.join(timeout=2)
            self.assertFalse(thread.is_alive())
        self.assertEqual(["clipboard-ack", "next-frame"], order)

    def test_failed_write_releases_priority_for_the_next_control(self):
        priority = host.HostWritePriority()
        with self.assertRaises(OSError):
            with priority.enter(True):
                raise OSError("closed socket")
        with priority.enter(False):
            self.assertTrue(priority.active)
        self.assertFalse(priority.active)

    def reduce(self, controller):
        self.assertFalse(controller.observe(1, 9.5, 102, 19))
        self.assertFalse(controller.observe(1, 9.5, 102, 19))
        self.assertTrue(controller.observe(1, 9.5, 102, 19))
        self.assertEqual((1920, 1080), controller.output_size)

    def test_bandwidth_fallback_preserves_ceiling_and_stops_failed_recovery_loops(self):
        controller = host.AdaptiveH264ResolutionController(3840, 2160, 30)
        self.reduce(controller)
        self.assertEqual((3840, 2160), controller.requested_size)
        for _ in range(44):
            self.assertFalse(controller.observe(1, 30, .1, 11))
        self.assertTrue(controller.observe(1, 30, .1, 11))
        self.reduce(controller)
        for _ in range(3600):
            self.assertFalse(controller.observe(1, 30, .1, 11))

    def test_static_desktop_invalid_metrics_and_spikes_do_not_lower_quality(self):
        controller = host.AdaptiveH264ResolutionController(3840, 2160, 30)
        for sample in [(1, 2, .1, 1), (1, 30, 102, 20), (1, float("nan"), 102, 10),
                       (1, 10, 102, 0), (20, 10, 102, 20), (1, 30, .1, 11)] * 10:
            self.assertFalse(controller.observe(*sample))

    def test_fixed_or_small_output_remains_unchanged(self):
        for controller in (host.AdaptiveH264ResolutionController(3840, 2160, 30, False),
                           host.AdaptiveH264ResolutionController(1280, 720, 30)):
            for _ in range(20):
                self.assertFalse(controller.observe(1, 9, 102, 19))

    def test_aspect_orientation_even_dimensions_and_no_upscale(self):
        for before, after in [((3840, 2160), (1920, 1080)), ((2160, 3840), (1080, 1920)),
                              ((3440, 1440), (1920, 802)), ((2560, 1600), (1728, 1080)),
                              ((1280, 720), (1280, 720)), ((734, 1600), (734, 1600))]:
            self.assertEqual(after, host.AdaptiveH264ResolutionController.fit_full_hd(*before))

    def test_encoder_restart_drops_old_generation_and_keeps_source_size(self):
        capture = host.ContinuousHardwareH264Capture(3840, 2160, 30, "placeholder")
        capture.latest_frame = object()
        capture.cached_sps = b"old"
        with mock.patch.object(capture, "_ensure_selection_thread_locked"), \
                mock.patch.object(capture, "_terminate_process"):
            self.assertTrue(capture.update_output_size(1920, 1080))
            self.assertEqual((1920, 1080), (capture.width, capture.height))
            self.assertEqual((3840, 2160), (capture.source_width, capture.source_height))
            self.assertEqual(1, capture.generation)
            self.assertIsNone(capture.latest_frame)
            self.assertIsNone(capture.cached_sps)
            self.assertFalse(capture.update_output_size(1920, 1080))

    def test_protocol_write_metrics_exclude_encryption_without_changing_bytes(self):
        sock, session = mock.Mock(), mock.Mock()
        session.encrypt.return_value = b"encrypted"
        with mock.patch.object(protocol.time, "monotonic", side_effect=[10.0, 10.125]):
            self.assertEqual(.125, protocol.write_message(sock, session, protocol.MESSAGE_PING, b""))
        sock.sendall.assert_called_once_with(b"\x09\x00\x00\x00encrypted")


if __name__ == "__main__":
    unittest.main()
