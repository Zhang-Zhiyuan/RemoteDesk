from __future__ import annotations

import io
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_dependencies as deps


class LinuxDependencyTests(unittest.TestCase):
    def setUp(self):
        self.environment = mock.patch.dict(os.environ, {}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.ffmpeg = deps.MissingDependency("ffmpeg", *deps.DEPENDENCIES["ffmpeg"])

    def test_install_only_fixed_missing_packages_and_refuse_removals(self):
        with mock.patch.object(deps.os.path, "isfile", return_value=True), mock.patch.object(deps.os, "access", return_value=True):
            command = deps.install_command([self.ffmpeg, self.ffmpeg])
        self.assertEqual("/usr/bin/apt-get", command[0])
        self.assertIn("--no-remove", command)
        self.assertIn("--no-install-recommends", command)
        self.assertIn("DPkg::Lock::Timeout=30", command)
        self.assertEqual(["ffmpeg"], command[command.index("install") + 1:])
        self.assertNotIn("upgrade", command)
        self.assertNotIn("--allow-unauthenticated", command)

    def test_untrusted_package_arguments_are_rejected(self):
        for package in ("ffmpeg-", "ffmpeg;id", "--allow-unauthenticated", "ffmpeg=1", "*", ""):
            with self.subTest(package=package), self.assertRaises(ValueError):
                deps.install_command([deps.MissingDependency("x", "x", package)])

    def test_empty_package_list_is_rejected(self):
        with self.assertRaises(ValueError):
            deps.install_command([])

    def test_unsupported_distribution_is_explicit(self):
        with mock.patch.object(deps.os.path, "isfile", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "Debian / Ubuntu"):
                deps.install_command([self.ffmpeg])

    def test_system_tools_do_not_inherit_bundled_python_or_ld_paths(self):
        with mock.patch.dict(os.environ, {"PYTHONHOME": "/private", "PYTHONPATH": "/private", "LD_LIBRARY_PATH": "/private",
            "LD_PRELOAD": "bad.so", "APT_CONFIG": "/tmp/apt.conf", "BASH_ENV": "bad", "ENV": "bad",
            "PATH": "/untrusted", "DISPLAY": ":99", "XAUTHORITY": "/tmp/test-auth"}):
            environment = deps.system_environment()
        self.assertEqual(deps.SYSTEM_PATH, environment["PATH"])
        self.assertEqual(":99", environment["DISPLAY"])
        self.assertEqual("/tmp/test-auth", environment["XAUTHORITY"])
        for name in ("PYTHONHOME", "PYTHONPATH", "LD_LIBRARY_PATH", "LD_PRELOAD", "APT_CONFIG", "BASH_ENV", "ENV"):
            self.assertNotIn(name, environment)

    def test_graphical_elevation_only_wraps_package_manager(self):
        with mock.patch.object(deps.os, "geteuid", return_value=1000, create=True), mock.patch.object(deps.os, "access", return_value=True):
            command = deps.elevated_command(["/usr/bin/apt-get", "install", "ffmpeg"], True)
        self.assertEqual(["/usr/bin/pkexec", "--disable-internal-agent", "/usr/bin/apt-get", "install", "ffmpeg"], command)
        self.assertNotIn("sh", command)

    def test_terminal_elevation_uses_sudo_not_password_storage(self):
        with mock.patch.object(deps.os, "geteuid", return_value=1000, create=True), mock.patch.object(deps.os, "access", return_value=True), mock.patch.object(deps.sys.stdin, "isatty", return_value=True):
            command = deps.elevated_command(["/usr/bin/apt-get", "install", "ffmpeg"], False)
        self.assertEqual(["/usr/bin/sudo", "--", "/usr/bin/apt-get", "install", "ffmpeg"], command)

    def test_no_graphical_agent_or_terminal_cannot_elevate(self):
        with mock.patch.object(deps.os, "geteuid", return_value=1000, create=True), mock.patch.object(deps.os, "access", return_value=False), mock.patch.object(deps.sys.stdin, "isatty", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "polkit"):
                deps.elevated_command(["/usr/bin/apt-get"], True)

    def test_already_root_does_not_relaunch_app_or_sudo(self):
        with mock.patch.object(deps.os, "geteuid", return_value=0, create=True):
            self.assertEqual(["/usr/bin/apt-get"], deps.elevated_command(["/usr/bin/apt-get"], False))

    def test_complete_environment_never_prompts_or_installs(self):
        with mock.patch.object(deps, "detect_missing", return_value=[]), mock.patch.object(deps, "dialog") as dialog, mock.patch.object(deps, "run_installer") as install:
            self.assertEqual(0, deps.prepare_runtime("app"))
        dialog.assert_not_called()
        install.assert_not_called()

    def test_declined_install_does_not_request_admin(self):
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), mock.patch.object(deps, "dialog", return_value=False), mock.patch.object(deps, "elevated_command") as elevate:
            self.assertEqual(deps.CANCELLED, deps.prepare_runtime(graphical=True))
        elevate.assert_not_called()

    def test_installed_dependencies_are_rechecked_in_same_runtime(self):
        with mock.patch.object(deps, "detect_missing", side_effect=[[self.ffmpeg], []]) as check, mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get"]), mock.patch.object(deps, "dialog", return_value=True), mock.patch.object(deps, "elevated_command", return_value=["/usr/bin/pkexec", "/usr/bin/apt-get"]), mock.patch.object(deps, "run_installer", return_value=(0, "done")) as install:
            self.assertEqual(0, deps.prepare_runtime("app", graphical=True))
        self.assertEqual([mock.call("app"), mock.call("app")], check.call_args_list)
        install.assert_called_once()

    def test_successful_apt_but_missing_module_is_failure_without_install_loop(self):
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]) as check, mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get"]), mock.patch.object(deps, "dialog", return_value=True) as dialog, mock.patch.object(deps, "elevated_command", return_value=["/usr/bin/pkexec", "/usr/bin/apt-get"]), mock.patch.object(deps, "run_installer", return_value=(0, "done")) as install:
            self.assertEqual(deps.UNAVAILABLE, deps.prepare_runtime(graphical=True))
        self.assertEqual(2, check.call_count)
        install.assert_called_once()
        self.assertIn("仍有缺项", dialog.call_args.args[0])

    def test_polkit_cancel_is_not_reported_as_install_success(self):
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get"]), mock.patch.object(deps, "dialog", return_value=True) as dialog, mock.patch.object(deps, "elevated_command", return_value=["/usr/bin/pkexec", "/usr/bin/apt-get"]), mock.patch.object(deps, "run_installer", return_value=(126, "")):
            self.assertEqual(deps.CANCELLED, deps.prepare_runtime(graphical=True))
        self.assertEqual(1, dialog.call_count)

    def test_apt_failure_includes_real_diagnostic_and_retry_command(self):
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), mock.patch.object(deps, "dialog", return_value=True) as dialog, mock.patch.object(deps, "elevated_command", return_value=["/usr/bin/pkexec", "/usr/bin/apt-get"]), mock.patch.object(deps, "run_installer", return_value=(100, "Could not get lock")):
            self.assertEqual(deps.UNAVAILABLE, deps.prepare_runtime(graphical=True))
        self.assertIn("Could not get lock", dialog.call_args.args[0])
        self.assertIn("sudo /usr/bin/apt-get install ffmpeg", dialog.call_args.args[0])

    def test_unattended_opt_out_never_prompts_or_installs(self):
        with mock.patch.dict(os.environ, {"REMOTEDESK_AUTO_INSTALL": "0"}), mock.patch.object(deps, "detect_missing") as check, mock.patch.object(deps, "dialog") as dialog:
            self.assertEqual(0, deps.prepare_runtime())
        check.assert_not_called()
        dialog.assert_not_called()

    def test_host_help_never_probes_or_installs_desktop_dependencies(self):
        import contextlib
        import runpy

        host_script = Path(__file__).resolve().parents[1] / "scripts/linux/remotedesk_linux_host.py"
        for help_argument in ("-h", "--help"):
            with self.subTest(argument=help_argument), \
                    mock.patch.object(sys, "argv", [str(host_script), help_argument]), \
                    mock.patch.object(sys, "platform", "linux"), \
                    mock.patch.object(deps, "prepare_runtime") as prepare, \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                with self.assertRaises(SystemExit) as exit_result:
                    runpy.run_path(str(host_script), run_name="__main__")
                self.assertEqual(0, exit_result.exception.code)
                self.assertIn("--capture {x11,placeholder}", output.getvalue())
                prepare.assert_not_called()

    def test_noninteractive_consent_defaults_to_no(self):
        with mock.patch.object(deps.sys.stdin, "isatty", return_value=False):
            self.assertFalse(deps.dialog("missing", question=True, graphical=False))

    def test_probe_uses_current_interpreter_and_reports_broken_ffmpeg(self):
        results = [subprocess.CompletedProcess([], 0, json.dumps({"pillow": "missing PIL"}), ""),
                   subprocess.CompletedProcess([], 1, "", "missing shared library")]
        with mock.patch.object(deps.subprocess, "run", side_effect=results) as run, mock.patch.object(deps.shutil, "which", side_effect=lambda name: "/usr/bin/" + name):
            missing = deps.detect_missing("app")
        self.assertEqual({"pillow", "ffmpeg"}, {item.key for item in missing})
        self.assertEqual(sys.executable, run.call_args_list[0].args[0][0])
        self.assertEqual("app", run.call_args_list[0].args[0][-1])

    def test_clipboard_alternatives_and_wayland_are_checked(self):
        with mock.patch.dict(os.environ, {"WAYLAND_DISPLAY": "wayland-0"}), mock.patch.object(deps.subprocess, "run", side_effect=[subprocess.CompletedProcess([], 0, "{}", ""), subprocess.CompletedProcess([], 0, "version", "")]), mock.patch.object(deps.shutil, "which", side_effect=lambda name: None if name in ("xclip", "wl-paste") else "/usr/bin/" + name):
            missing = deps.detect_missing()
        self.assertEqual(["wayland_clipboard"], [item.key for item in missing])

    def test_broken_interpreter_is_not_hidden_by_auto_install(self):
        with mock.patch.object(deps.subprocess, "run", return_value=subprocess.CompletedProcess([], 127, "", "GLIBC not found")):
            with self.assertRaisesRegex(RuntimeError, "GLIBC"):
                deps.detect_missing()

    def test_native_x11_still_requires_unicode_text_helper(self):
        with mock.patch.object(deps.subprocess, "run", side_effect=[
            subprocess.CompletedProcess([], 0, "{}", ""),
            subprocess.CompletedProcess([], 0, "version", "")
        ]), mock.patch.object(deps.shutil, "which", side_effect=lambda name:
            None if name == "xdotool" else "/usr/bin/" + name):
            missing = deps.detect_missing("host")
        self.assertEqual(["xdotool"], [item.key for item in missing])
        self.assertEqual("xdotool", missing[0].package)

    def test_invalid_probe_json_is_rejected(self):
        for value in ("bad json", "[]", '{"untrusted": "package"}'):
            with self.subTest(value=value), mock.patch.object(deps.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, value, "")):
                with self.assertRaises(RuntimeError):
                    deps.detect_missing()

    def test_installer_output_is_bounded_and_never_uses_shell(self):
        process = mock.Mock()
        process.stdout = io.StringIO("ok\n" * 10000)
        process.wait.return_value = 0
        process.poll.return_value = 0
        with mock.patch.object(deps.subprocess, "Popen", return_value=process) as start, mock.patch("builtins.print"):
            code, tail = deps.run_installer(["/usr/bin/apt-get", "install", "ffmpeg"], False)
        self.assertEqual(0, code)
        self.assertLessEqual(len(tail), 5000)
        self.assertNotIn("shell", start.call_args.kwargs)

    def test_packaging_includes_dependency_checker_and_cancel_handling(self):
        repo = Path(__file__).resolve().parents[1]
        publish = (repo / "scripts/Publish-RemoteDesk.ps1").read_text(encoding="utf-8-sig")
        self.assertIn('scripts\\linux\\remotedesk_linux_dependencies.py', publish)
        self.assertEqual(2, publish.count('if [ "$code" -eq 125 ]; then'))
        for name, marker in (("remotedesk_linux_app.py", "import tkinter as tk"),
                             ("remotedesk_linux_host.py", "from remotedesk_protocol_probe import")):
            source = (repo / "scripts/linux" / name).read_text(encoding="utf-8-sig")
            self.assertLess(source.index('prepare_runtime('), source.index(marker))


if __name__ == "__main__":
    unittest.main()
