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
