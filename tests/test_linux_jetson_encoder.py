"""Hermetic Jetson encoder discovery and stream-contract regressions."""
import subprocess
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_host as host


class JetsonEncoderTests(unittest.TestCase):
    def discover(self, *, platform="linux", tegra=True, device=True, launch=True, result=0, error=None):
        with mock.patch.object(host.sys, "platform", platform), \
                mock.patch.object(host.Path, "is_file", return_value=tegra), \
                mock.patch.object(host.Path, "exists", return_value=device), \
                mock.patch.object(host.shutil, "which", side_effect=lambda name: "/usr/bin/" + name if launch else None), \
                mock.patch.object(host.subprocess, "run", return_value=SimpleNamespace(returncode=result), side_effect=error) as run:
            found = host.find_jetson_gstreamer_encoder()
            return found, run.call_args_list

    def test_discovery_does_not_probe_non_jetson_or_missing_tools(self):
        for options in ({"platform": "win32"}, {"tegra": False}, {"device": False}, {"launch": False}):
            with self.subTest(options=options):
                found, calls = self.discover(**options)
                self.assertIsNone(found)
                self.assertEqual([], calls)

    def test_discovery_checks_plugins_without_capturing_or_installing(self):
        found, calls = self.discover()
        self.assertEqual("/usr/bin/gst-launch-1.0", found)
        self.assertEqual(["ximagesrc", "videoconvert", "videoscale", "nvvidconv", "nvv4l2h264enc", "h264parse"],
                         [call.args[0][1] for call in calls])
        for call in calls:
            self.assertEqual("/usr/bin/gst-inspect-1.0", call.args[0][0])
            self.assertEqual(subprocess.DEVNULL, call.kwargs["stdin"])
            self.assertEqual(2, call.kwargs["timeout"])

    def test_discovery_failures_keep_optional_path_unavailable(self):
        for options in ({"result": 1}, {"error": OSError("missing")},
                        {"error": subprocess.TimeoutExpired("gst-inspect-1.0", 2)}):
            with self.subTest(options=options):
                found, calls = self.discover(**options)
                self.assertIsNone(found)
                self.assertEqual(1, len(calls))

    def command(self, display=":0", source=(1920, 1080), output=(1920, 1080), fps=30.0, cap=100_000_000):
        return host.build_jetson_h264_encoder_command("/usr/bin/gst-launch-1.0", display, *source, *output, fps, cap)

    def test_pipeline_rejects_display_syntax_injection_and_nonlocal_capture(self):
        for display in ("", "localhost:10.0", ":0 ! fakesrc ! filesink location=/tmp/x", ":0\n", ":-1", "tcp/host:0"):
            with self.subTest(display=display):
                self.assertIsNone(self.command(display=display))
        self.assertIsNone(self.command(source=(0, 1080)))
        self.assertIsNotNone(self.command(display=":101.0"))

    def test_native_pipeline_preserves_resolution_bitrate_and_recovery_contract(self):
        candidate = self.command()
        self.assertEqual("jetson-gstreamer", candidate.encoder_name)
        args = candidate.command
        for value in ("video/x-raw,format=NV12,width=1920,height=1080,pixel-aspect-ratio=1/1,colorimetry=bt709",
                      "bitrate=" + str(host.calculate_h264_bitrate(1920, 1080, 30.0)),
                      "endx=1919", "endy=1079", "iframeinterval=1", "idrinterval=1", "num-B-Frames=0",
                      "insert-sps-pps=true", "insert-aud=true", "insert-vui=true", "profile=4", "fd=1", "sync=false",
                      "video/x-raw(memory:NVMM),format=NV12,colorimetry=bt709",
                      "video/x-h264,stream-format=byte-stream,alignment=au"):
            self.assertIn(value, args)
        self.assertNotIn("videoscale", args)
        self.assertNotIn("queue", args)
        self.assertNotIn("libx264", args)
        self.assertNotIn("maxperf-enable=true", args)

    def test_resize_preserves_aspect_with_high_quality_filter_and_even_dimensions(self):
        args = self.command(output=(1281, 721), fps=29.97, cap=8_000_000).command
        for value in ("videoscale", "method=3", "add-borders=true", "video/x-raw,framerate=2997/100",
                      "video/x-raw,format=NV12,width=1280,height=720,pixel-aspect-ratio=1/1,colorimetry=bt709",
                      "bitrate=" + str(host.calculate_h264_bitrate(1280, 720, 29.97, 8_000_000))):
            self.assertIn(value, args)

    def test_native_startup_failure_keeps_existing_ffmpeg_fallback_order(self):
        with mock.patch.object(host.shutil, "which", return_value="/usr/bin/ffmpeg"), \
                mock.patch.object(host, "find_jetson_gstreamer_encoder", return_value="/usr/bin/gst-launch-1.0") as discovery, \
                mock.patch.object(host, "detect_ffmpeg_h264_encoders", return_value={"h264_nvenc"}), \
                mock.patch.object(host.subprocess, "Popen", side_effect=OSError("synthetic unavailable encoder")) as start, \
                mock.patch.dict(host.os.environ, {"DISPLAY": ":1"}), mock.patch.object(host, "log"):
            capture = host.ContinuousHardwareH264Capture(1920, 1080, 30, "x11")
            capture.desired = capture.source_size_ready = True
            capture._selection_loop()
            self.assertTrue(capture.exhausted)
            self.assertIsNone(capture.latest_frame)
            self.assertEqual(["/usr/bin/gst-launch-1.0", "/usr/bin/ffmpeg"], [call.args[0][0] for call in start.call_args_list])
            discovery.assert_called_once_with()
            capture.close()

    def test_native_encoder_is_considered_without_ffmpeg_and_failure_exhausts_safely(self):
        with mock.patch.object(host.shutil, "which", return_value=None), \
                mock.patch.object(host, "find_jetson_gstreamer_encoder", return_value="/usr/bin/gst-launch-1.0"), \
                mock.patch.object(host.subprocess, "Popen", side_effect=OSError("synthetic unavailable encoder")) as start, \
                mock.patch.dict(host.os.environ, {"DISPLAY": ":1"}), mock.patch.object(host, "log"):
            capture = host.ContinuousHardwareH264Capture(1920, 1080, 30, "x11")
            capture.desired = capture.source_size_ready = True
            capture._selection_loop()
            self.assertTrue(capture.exhausted)
            self.assertIsNone(capture.latest_frame)
            self.assertEqual("/usr/bin/gst-launch-1.0", start.call_args.args[0][0])
            start.assert_called_once()
            capture.close()


if __name__ == "__main__":
    unittest.main()
