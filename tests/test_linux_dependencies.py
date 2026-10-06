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
        preflight = mock.patch.object(deps, "preflight_install")
        self.preflight = preflight.start()
        self.addCleanup(preflight.stop)
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
        self.preflight.assert_not_called()

    def test_declined_install_does_not_request_admin(self):
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), mock.patch.object(deps, "dialog", return_value=False), mock.patch.object(deps, "elevated_command") as elevate:
            self.assertEqual(deps.CANCELLED, deps.prepare_runtime(graphical=True))
        elevate.assert_not_called()

    def test_installed_dependencies_are_rechecked_in_same_runtime(self):
        with mock.patch.object(deps, "detect_missing", side_effect=[[self.ffmpeg], []]) as check, mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get"]), mock.patch.object(deps, "dialog", return_value=True), mock.patch.object(deps, "elevated_command", return_value=["/usr/bin/pkexec", "/usr/bin/apt-get"]), mock.patch.object(deps, "run_installer", return_value=(0, "done")) as install:
            self.assertEqual(0, deps.prepare_runtime("app", graphical=True))
        self.assertEqual([mock.call("app"), mock.call("app")], check.call_args_list)
        install.assert_called_once()
        self.assertEqual(2, self.preflight.call_count)

    def test_invalid_install_plan_never_asks_for_consent_or_admin(self):
        self.preflight.side_effect = RuntimeError("Ubuntu source mismatch")
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), \
                mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), \
                mock.patch.object(deps, "dialog") as dialog, \
                mock.patch.object(deps, "elevated_command") as elevate, \
                mock.patch.object(deps, "run_installer") as install:
            self.assertEqual(deps.UNAVAILABLE, deps.prepare_runtime(graphical=True))
        elevate.assert_not_called()
        install.assert_not_called()
        dialog.assert_called_once()
        self.assertFalse(dialog.call_args.kwargs["question"])
        self.assertIn("Ubuntu source mismatch", dialog.call_args.args[0])

    def test_plan_is_rechecked_after_consent_before_elevation(self):
        self.preflight.side_effect = [None, RuntimeError("Package state changed")]
        with mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), \
                mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), \
                mock.patch.object(deps, "dialog", return_value=True) as dialog, \
                mock.patch.object(deps, "elevated_command") as elevate, \
                mock.patch.object(deps, "run_installer") as install:
            self.assertEqual(deps.UNAVAILABLE, deps.prepare_runtime(graphical=True))
        self.assertEqual([True, False], [call.kwargs["question"] for call in dialog.call_args_list])
        self.assertEqual(2, self.preflight.call_count)
        elevate.assert_not_called()
        install.assert_not_called()

    def test_readonly_install_check_reports_missing_and_plan_failure_without_dialogs(self):
        import contextlib
        self.preflight.side_effect = RuntimeError("held broken packages")
        with mock.patch.object(sys, "argv", ["dependencies", "--check-install"]), \
                mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), \
                mock.patch.object(deps, "install_command", return_value=["/usr/bin/apt-get", "install", "ffmpeg"]), \
                mock.patch.object(deps, "dialog") as dialog, \
                mock.patch.object(deps, "run_installer") as install, \
                contextlib.redirect_stdout(io.StringIO()) as stdout:
            self.assertEqual(deps.UNAVAILABLE, deps.main())
        report = json.loads(stdout.getvalue())
        self.assertFalse(report["installable"])
        self.assertEqual("ffmpeg", report["missing"][0]["key"])
        self.assertEqual("held broken packages", report["error"])
        dialog.assert_not_called()
        install.assert_not_called()

    def test_existing_readonly_check_does_not_run_apt_plan(self):
        import contextlib
        with mock.patch.object(sys, "argv", ["dependencies", "--check"]), \
                mock.patch.object(deps, "detect_missing", return_value=[self.ffmpeg]), \
                contextlib.redirect_stdout(io.StringIO()) as stdout:
            self.assertEqual(1, deps.main())
        self.assertNotIn("installable", json.loads(stdout.getvalue()))
        self.preflight.assert_not_called()

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
        self.preflight.assert_not_called()

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


class LinuxDependencyPreflightTests(unittest.TestCase):
    def setUp(self):
        environment = mock.patch.dict(os.environ, {}, clear=True)
        environment.start()
        self.addCleanup(environment.stop)
        distribution = mock.patch.object(deps.platform, "freedesktop_os_release", return_value={
            "ID": "ubuntu", "PRETTY_NAME": "Ubuntu 22.04 LTS", "VERSION_ID": "22.04", "VERSION_CODENAME": "jammy"})
        self.distribution = distribution.start()
        self.addCleanup(distribution.stop)
        self.command = ["/usr/bin/apt-get", "--assume-yes", "--no-remove", "--no-install-recommends", "install", "xclip"]

    def run_plan(self, output, code=0, error=""):
        with mock.patch.object(deps.subprocess, "run", return_value=subprocess.CompletedProcess([], code, output, error)) as run:
            deps.preflight_install(self.command)
        return run

    def test_matching_ubuntu_plan_uses_only_readonly_unprivileged_simulation(self):
        with mock.patch.dict(os.environ, {"LD_LIBRARY_PATH": "/private", "PYTHONHOME": "/private", "LC_ALL": "zh_CN.UTF-8"}):
            run = self.run_plan("Inst xclip (0.13-1 Ubuntu:22.04/jammy [amd64])\nConf xclip (0.13-1 Ubuntu:22.04/jammy [amd64])\n")
        self.assertEqual(["/usr/bin/apt-get", "--simulate", *self.command[1:]], run.call_args.args[0])
        options = run.call_args.kwargs
        self.assertEqual(20, options["timeout"])
        self.assertEqual(subprocess.DEVNULL, options["stdin"])
        self.assertEqual("C", options["env"]["LC_ALL"])
        self.assertNotIn("LD_LIBRARY_PATH", options["env"])
        self.assertNotIn("PYTHONHOME", options["env"])
        self.assertNotIn("shell", options)

    def test_matching_updates_security_and_backports_are_allowed(self):
        for suffix in ("", "-updates", "-security", "-backports"):
            with self.subTest(suffix=suffix):
                self.run_plan(f"Inst xclip:amd64 (0.13-1 Ubuntu:22.04/jammy{suffix} [amd64])\n")

    def test_4090_resolver_failure_is_rejected_before_install(self):
        with self.assertRaisesRegex(RuntimeError, "软件源无法满足") as caught:
            self.run_plan("python3-tk : Depends: python3 (< 3.10) but 3.10.4-0ubuntu2 is to be installed", 100,
                          "E: Unable to correct problems, you have held broken packages.")
        self.assertIn("Ubuntu 22.04", str(caught.exception))
        self.assertIn("python3 (< 3.10)", str(caught.exception))
        self.assertIn("尚未申请管理员权限", str(caught.exception))

    def test_4090_successful_resolution_still_rejects_focal_candidates(self):
        with self.assertRaisesRegex(RuntimeError, "不匹配") as caught:
            self.run_plan("Inst xclip (0.13-1 Ubuntu:20.04/focal [amd64])\n")
        self.assertIn("jammy", str(caught.exception))
        self.assertIn("xclip：Ubuntu 20.04 / focal", str(caught.exception))

    def test_transitive_foreign_release_is_also_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "libxdo3"):
            self.run_plan("Inst xclip (0.13-1 Ubuntu:22.04/jammy [amd64])\n"
                          "Inst libxdo3 (1:3.0 Ubuntu:20.04/focal-updates [amd64])\n")

    def test_unrelated_foreign_source_does_not_block_matching_install_plan(self):
        self.run_plan("source Ubuntu:20.04/focal is configured but unused\nInst xclip (0.13-1 Ubuntu:22.04/jammy [amd64])\n")

    def test_ubuntu_derivative_uses_ubuntu_codename_not_its_own(self):
        self.distribution.return_value = {"ID": "linuxmint", "VERSION_CODENAME": "virginia", "UBUNTU_CODENAME": "jammy"}
        self.run_plan("Inst xclip (0.13-1 Ubuntu:22.04/jammy [amd64])\n")
        with self.assertRaisesRegex(RuntimeError, "不匹配"):
            self.run_plan("Inst xclip (0.13-1 Ubuntu:20.04/focal [amd64])\n")

    def test_debian_origin_is_not_misidentified_as_wrong_ubuntu(self):
        self.distribution.return_value = {"ID": "debian", "VERSION_CODENAME": "bookworm"}
        self.run_plan("Inst xclip (0.13-2 Debian:12.0/stable [amd64])\n")

    def test_same_prefix_does_not_make_a_different_codename_valid(self):
        with self.assertRaisesRegex(RuntimeError, "不匹配"):
            self.run_plan("Inst xclip (0.13-1 Ubuntu:22.04/jammyother [amd64])\n")

    def test_driver_or_kernel_changes_are_not_app_dependency_repairs(self):
        for package in ("nvidia-driver-550", "libnvidia-encode-550:amd64", "cuda-drivers", "libcuda1",
                        "linux-image-generic", "linux-modules-nvidia-550", "linux-headers-generic"):
            with self.subTest(package=package), self.assertRaisesRegex(RuntimeError, "驱动或内核"):
                self.run_plan(f"Inst xclip (1 Ubuntu:22.04/jammy [amd64])\nInst {package} (1 Ubuntu:22.04/jammy [amd64])\n")

    def test_unrelated_libraries_can_be_installed_as_dependencies(self):
        self.run_plan("Inst libxdo3 (1:3.0 Ubuntu:22.04/jammy [amd64])\nInst xclip (1 Ubuntu:22.04/jammy [amd64])\n")

    def test_pending_driver_configuration_is_also_out_of_scope(self):
        with self.assertRaisesRegex(RuntimeError, "驱动或内核"):
            self.run_plan("Inst xclip (1 Ubuntu:22.04/jammy [amd64])\nConf nvidia-driver-550 (1 Ubuntu:22.04/jammy [amd64])\n")

    def test_package_removal_is_rejected_even_if_process_returns_success(self):
        with self.assertRaisesRegex(RuntimeError, "删除已有软件"):
            self.run_plan("Remv important-service [1.0]\nInst xclip (1 Ubuntu:22.04/jammy [amd64])\n")

    def test_noop_install_does_not_repeat_consent_for_broken_runtime(self):
        with self.assertRaisesRegex(RuntimeError, "没有可安装"):
            self.run_plan("xclip is already the newest version.\n0 upgraded, 0 newly installed, 0 to remove.\n")

    def test_missing_os_release_does_not_break_an_otherwise_valid_plan(self):
        self.distribution.side_effect = OSError("no os-release")
        self.run_plan("Inst xclip (1 Debian:12.0/stable [amd64])\n")

    def test_timeout_or_missing_apt_never_becomes_permission_request(self):
        for error in (subprocess.TimeoutExpired(["apt-get"], 20), FileNotFoundError("apt-get missing")):
            with self.subTest(error=type(error).__name__), \
                    mock.patch.object(deps.subprocess, "run", side_effect=error), \
                    self.assertRaisesRegex(RuntimeError, "安装前检查未完成"):
                deps.preflight_install(self.command)

    def test_failure_diagnostic_is_bounded(self):
        with self.assertRaises(RuntimeError) as caught:
            self.run_plan("x" * 20000, 100, "resolver failed")
        self.assertLess(len(str(caught.exception)), 4000)
        self.assertIn("resolver failed", str(caught.exception))

    def test_invalid_install_command_cannot_be_run_as_preflight(self):
        for command in ([], ["/bin/sh", "install", "xclip"], ["/usr/bin/apt-get", "upgrade"],
                        ["/usr/bin/apt-get", "install"], ["/usr/bin/apt-get", "install", "xclip;id"]):
            with self.subTest(command=command), mock.patch.object(deps.subprocess, "run") as run, self.assertRaises(ValueError):
                deps.preflight_install(command)
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
