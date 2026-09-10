"""No devices/processes required: protect diagnostic stdin and owned targets."""
import contextlib
import importlib.util
import io
from pathlib import Path
import subprocess
import unittest
from unittest import mock


SPEC = importlib.util.spec_from_file_location(
    "interop_runner", Path(__file__).resolve().parents[1] / "experiments/run_physical_interop.py")
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class InteropRunnerTests(unittest.TestCase):
    def test_h264_requirement_rejects_otherwise_healthy_jpeg(self):
        for encoding in (1, "Jpeg", None):
            value = runner.require_h264_evidence(dict(complete=True, encoding=encoding), True)
            self.assertFalse(value["complete"])
            self.assertFalse(value["h264Active"])

    def test_windows_and_linux_h264_encoding_require_real_interop_success(self):
        for encoding in (2, "H264AnnexB"):
            self.assertTrue(runner.require_h264_evidence(dict(complete=True, encoding=encoding), True)["complete"])
            self.assertFalse(runner.require_h264_evidence(dict(complete=False, encoding=encoding), True)["complete"])
            self.assertFalse(runner.require_h264_evidence(dict(encoding=encoding), True)["complete"])

    def test_explicit_jpeg_compatibility_interop_still_qualifies_without_h264_requirement(self):
        value = runner.require_h264_evidence(dict(complete=True, encoding=1), False)
        self.assertTrue(value["complete"])
        self.assertFalse(value["h264Active"])

    @staticmethod
    def rendering_samples():
        return [dict(sampleUptimeMs=i * 1000, receivedFrames=i * 5,
                     presentedFrames=i * 4, ownerGeneration=1, geometryReady=True,
                     failure="", health="FPS 4.0") for i in range(12)]

    def test_android_rendering_requires_real_recent_counter_growth(self):
        result = runner.android_rendering_evidence(self.rendering_samples())
        self.assertTrue(result["continuous"])
        self.assertEqual(44, result["presentedDelta"])
        self.assertEqual(11000, result["observationMs"])
        self.assertEqual(4, result["presentedFps"])
        self.assertEqual(4, result["recentPresentedFps"])

    def test_continuous_but_slow_video_is_not_a_performance_pass(self):
        result = runner.android_rendering_evidence(self.rendering_samples())
        self.assertTrue(runner.rendering_meets_minimum(result, 0))
        self.assertTrue(runner.rendering_meets_minimum(result, 3))
        self.assertFalse(runner.rendering_meets_minimum(result, 15))

    def test_slow_recent_window_cannot_hide_behind_startup_average(self):
        value = dict(continuous=True, presentedFps=30, recentPresentedFps=.4)
        self.assertFalse(runner.rendering_meets_minimum(value, 15))
        self.assertFalse(runner.rendering_meets_minimum(dict(continuous=False), 0))

    def test_startup_frames_then_frozen_video_is_not_continuous(self):
        samples = self.rendering_samples()
        for sample in samples[2:]:
            sample.update(receivedFrames=5, presentedFrames=4)
        self.assertFalse(runner.android_rendering_evidence(samples)["continuous"])

    def test_fps_label_without_counters_never_qualifies(self):
        self.assertFalse(runner.android_rendering_evidence([
            dict(health="FPS 30.0", geometryReady=True, ownerGeneration=1)])["continuous"])

    def test_rendering_reconnect_counter_reset_needs_new_observation(self):
        samples = self.rendering_samples()
        samples[-1].update(ownerGeneration=2, receivedFrames=1, presentedFrames=1)
        self.assertFalse(runner.android_rendering_evidence(samples)["continuous"])

    def test_rendering_stale_failure_or_unready_final_sample_never_qualifies(self):
        for last in (dict(failure="stopped"), dict(geometryReady=False), dict(sampleUptimeMs=0),
                     dict(presentedFrames=0), dict(receivedFrames=0)):
            samples = self.rendering_samples()
            samples[-1].update(last)
            self.assertFalse(runner.android_rendering_evidence(samples)["continuous"])

    def test_frames_received_without_presentation_do_not_qualify(self):
        samples = self.rendering_samples()
        for sample in samples:
            sample["presentedFrames"] = 1
        self.assertFalse(runner.android_rendering_evidence(samples)["continuous"])

    def test_short_or_empty_observation_is_not_long_running_evidence(self):
        for samples in ([], self.rendering_samples()[:1], self.rendering_samples()[:4]):
            self.assertFalse(runner.android_rendering_evidence(samples)["continuous"])

    def test_android_endpoint_uses_verified_raw_stdin_not_exec_in(self):
        endpoint = b'{"password":"synthetic-value"}'
        with mock.patch.object(runner, "run", side_effect=[b"", endpoint]) as child:
            runner.write_android_endpoint(["adb", "-s", "emulator-5582"], "com.remotedesk.viewerprobe", endpoint)
        write, read = child.call_args_list
        self.assertIn("-T", write.args[0])
        self.assertNotIn("exec-in", write.args[0])
        self.assertEqual(endpoint, write.kwargs["data"])
        self.assertNotIn("synthetic-value", " ".join(write.args[0]))
        self.assertEqual("files/interop-endpoint.json", read.args[0][-1])

    def test_android_endpoint_mismatch_is_cleaned_without_echoing_credentials(self):
        with mock.patch.object(runner, "run", side_effect=[b"", b"unexpected-private-content", b""]) as child:
            with self.assertRaisesRegex(RuntimeError, "^Diagnostic endpoint write verification failed$"):
                runner.write_android_endpoint(["adb"], "com.remotedesk.viewerprobe", b"expected-private-content")
        self.assertEqual(["rm", "-f", "files/interop-endpoint.json"], child.call_args_list[-1].args[0][-3:])

    def test_android_endpoint_never_writes_production_app(self):
        with mock.patch.object(runner, "run") as child:
            with self.assertRaises(ValueError):
                runner.write_android_endpoint(["adb"], "com.remotedesk.agent", b"fixture")
            child.assert_not_called()

    def test_relay_batch_does_not_require_unrelated_test_apks(self):
        self.assertEqual({}, runner.required_android_relay_probes({"WindowsToLinux", "LinuxToWindows"}))
        self.assertEqual({"com.remotedesk.viewerprobe"}, set(runner.required_android_relay_probes({"AndroidToLinux"})))
        self.assertEqual({"com.remotedesk.relayhostprobe"}, set(runner.required_android_relay_probes({"WindowsToAndroid"})))

    def test_full_relay_matrix_still_checks_both_test_apks(self):
        self.assertEqual({"com.remotedesk.viewerprobe", "com.remotedesk.relayhostprobe"},
                         set(runner.required_android_relay_probes({"AndroidToLinux", "LinuxToAndroid"})))

    def test_stale_or_background_phone_target_cannot_authorize_input(self):
        for value in ({"clicks": 2}, {"foregroundOwned": False}, {"foregroundOwned": "true"}):
            with self.subTest(value=value), self.assertRaisesRegex(RuntimeError, "not foreground"):
                runner.require_foreground_phone_target(value)

    def test_foreground_phone_target_preserves_observed_input(self):
        value = {"foregroundOwned": True, "clicks": 1, "text": "owned synthetic text"}
        self.assertIs(value, runner.require_foreground_phone_target(value))

    def test_child_without_payload_cannot_consume_parent_credentials(self):
        completed = subprocess.CompletedProcess([], 0, b"ok", b"")
        with mock.patch.object(runner.subprocess, "run", return_value=completed) as child:
            self.assertEqual(b"ok", runner.run(["ssh", "owned-test-host", "true"]))
        self.assertEqual(subprocess.DEVNULL, child.call_args.kwargs["stdin"])

    def test_explicit_payload_uses_only_its_own_pipe(self):
        completed = subprocess.CompletedProcess([], 0, b"ok", b"")
        with mock.patch.object(runner.subprocess, "run", return_value=completed) as child:
            runner.run(["owned-probe"], data=b"synthetic-test-payload")
        self.assertEqual(b"synthetic-test-payload", child.call_args.kwargs["input"])
        self.assertNotIn("stdin", child.call_args.kwargs)

    def test_error_does_not_echo_arguments_or_input(self):
        completed = subprocess.CompletedProcess([], 1, b"", b"bounded test failure")
        with mock.patch.object(runner.subprocess, "run", return_value=completed):
            with self.assertRaisesRegex(RuntimeError, "^bounded test failure$"):
                runner.run(["probe", "argument-not-for-logs"], data=b"stdin-not-for-logs")

    def test_missing_android_endpoint_stops_before_any_child(self):
        argv = ["probe", "--adb", "adb", "--serial", "test", "--linux", "test@host",
                "--windows-ip", "127.0.0.1", "--output", "must-not-be-created", "--android-host"]
        with mock.patch.object(runner.sys, "argv", argv), mock.patch.object(runner.sys, "stdin", io.StringIO("")), \
                mock.patch.object(runner.sys, "stdout", mock.Mock()), \
                mock.patch.object(runner.subprocess, "run") as child:
            with self.assertRaises(runner.json.JSONDecodeError):
                runner.main()
            child.assert_not_called()

    def test_production_app_cannot_be_used_as_synthetic_input_target(self):
        argv = ["probe", "--adb", "adb", "--serial", "test", "--linux", "test@host",
                "--windows-ip", "127.0.0.1", "--output", "must-not-be-created",
                "--android-target-package", "com.remotedesk.agent"]
        with mock.patch.object(runner.sys, "argv", argv), mock.patch.object(runner.sys, "stdout", mock.Mock()), \
                contextlib.redirect_stderr(io.StringIO()), mock.patch.object(runner.subprocess, "run") as child:
            with self.assertRaises(SystemExit):
                runner.main()
            child.assert_not_called()


if __name__ == "__main__":
    unittest.main()
