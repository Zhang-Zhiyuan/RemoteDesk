"""Keep a stopped/frozen Linux viewer from passing an interop audit."""
import importlib.util
from pathlib import Path
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location(
    "linux_interop_node", Path(__file__).resolve().parents[1] / "experiments/interop_linux_node.py")
node = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(node)


class LinuxViewerEvidenceTests(unittest.TestCase):
    def test_owned_xvfb_does_not_reset_when_its_last_probe_client_closes(self):
        output = mock.MagicMock()
        with mock.patch.object(node.os, "pipe", return_value=(31, 32)), \
                mock.patch.object(node.os, "close"), \
                mock.patch.object(node.os, "read", return_value=b"121\n"), \
                mock.patch.object(node.os, "environ", {}), \
                mock.patch.object(node.select, "select", return_value=([31], [], [])), \
                mock.patch.object(node.subprocess, "Popen") as start:
            node.start_xvfb(output)
        command = start.call_args.args[0]
        self.assertIn("-noreset", command)
        self.assertEqual(["-nolisten", "tcp"], command[-2:])
        self.assertEqual((32,), start.call_args.kwargs["pass_fds"])

    def test_active_sustained_recent_presentation_passes(self):
        evidence = node.linux_viewer_continuity(True, 5.0, 89.0, 90.0)
        self.assertTrue(evidence["continuous"])
        self.assertEqual(1.0, evidence["lastFrameAgeSeconds"])

    def test_closed_transport_never_passes_even_with_recent_frames(self):
        self.assertFalse(node.linux_viewer_continuity(False, 5.0, 89.0, 90.0)["continuous"])

    def test_startup_burst_then_frozen_video_never_passes(self):
        self.assertFalse(node.linux_viewer_continuity(True, 5.0, 27.0, 90.0)["continuous"])

    def test_missing_or_short_presentation_is_not_sustained(self):
        for first, last in ((None, None), (89.0, 89.0), (86.0, 89.0)):
            with self.subTest(first=first, last=last):
                self.assertFalse(node.linux_viewer_continuity(True, first, last, 90.0)["continuous"])

    def test_future_timestamp_is_not_accepted_as_fresh(self):
        self.assertFalse(node.linux_viewer_continuity(True, 5.0, 91.0, 90.0)["continuous"])


if __name__ == "__main__":
    unittest.main()
