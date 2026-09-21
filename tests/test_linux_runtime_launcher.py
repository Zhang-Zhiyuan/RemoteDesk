"""The portable bundle must not require replacing a target's glibc/Python."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'scripts/linux/remotedesk_linux_runtime.sh'


@unittest.skipUnless(os.name == 'posix' and Path('/usr/bin/python3').exists(),
                     'requires the distribution Python and POSIX shell')
class LinuxRuntimeLauncherTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='remotedesk-runtime-test-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / 'bundle with spaces'
        (self.root / 'app').mkdir(parents=True)
        (self.root / 'runtime/bin').mkdir(parents=True)
        self.helper = self.root / 'app/remotedesk_linux_runtime.sh'
        self.helper.write_text(RUNTIME.read_text(), encoding='utf-8')
        self.script = self.root / 'app/owned.py'
        self.script.write_text('import json, os, sys\nprint(json.dumps(dict(argv=sys.argv[1:], '
                               'pid=os.getpid(), env={k: os.environ.get(k) for k in ("PYTHONHOME", "PYTHONPATH", '
                               '"LD_LIBRARY_PATH", "LD_PRELOAD")}, interpreter=sys.executable)))\n',
                               encoding='utf-8')

    def private(self, text):
        path = self.root / 'runtime/bin/python'
        path.write_text('#!/bin/sh\n' + text, encoding='utf-8')
        path.chmod(0o700)

    def launch(self, *arguments, setup=''):
        return subprocess.run(['/bin/sh', '-c', setup + '. "$1"; shift; remotedesk_python "$@"',
                               'owned-launcher', str(self.helper), str(self.root),
                               str(self.script), *arguments], text=True, capture_output=True, timeout=10)

    def test_missing_private_runtime_uses_distribution_python(self):
        result = self.launch('one two', '中文', '$(not-a-command)')
        self.assertEqual(0, result.returncode, result.stderr)
        payload = json.loads(result.stdout)
        self.assertEqual('/usr/bin/python3', payload['interpreter'])
        self.assertEqual(['one two', '中文', '$(not-a-command)'], payload['argv'])
        self.assertEqual(str(self.root / 'app'), payload['env']['PYTHONPATH'])
        self.assertIsNone(payload['env']['PYTHONHOME'])
        self.assertIsNone(payload['env']['LD_LIBRARY_PATH'])
        self.assertEqual('', result.stderr)

    def test_incompatible_elf_falls_back_before_loading_application(self):
        self.private('echo "GLIBC_2.38 not found" >&2\nexit 1\n')
        result = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('系统 Python', result.stderr)
        self.assertNotIn('GLIBC_2.38', result.stderr)
        self.assertIsNone(json.loads(result.stdout)['env']['PYTHONHOME'])

    def test_inherited_private_or_conda_paths_do_not_pollute_fallback(self):
        # Set these AFTER starting /bin/sh, so the test shell itself is clean.
        result = self.launch(setup='export PYTHONHOME=/not-a-python PYTHONPATH=/wrong-stdlib '
                             'LD_LIBRARY_PATH=/wrong-libs LD_PRELOAD=/no-library; ')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({'PYTHONHOME': None, 'PYTHONPATH': str(self.root / 'app'),
                          'LD_LIBRARY_PATH': None, 'LD_PRELOAD': None}, json.loads(result.stdout)['env'])

    def test_healthy_private_runtime_remains_preferred(self):
        # -E lets the distribution Python emulate a relocated private binary,
        # while still reporting the environment supplied by the real launcher.
        self.private('exec /usr/bin/python3 -E "$@"\n')
        result = self.launch()
        self.assertEqual(0, result.returncode, result.stderr)
        env = json.loads(result.stdout)['env']
        self.assertEqual(str(self.root / 'runtime'), env['PYTHONHOME'])
        self.assertEqual(str(self.root / 'runtime/lib'), env['LD_LIBRARY_PATH'])
        self.assertEqual('', result.stderr)

    def test_application_failure_is_not_retried_in_a_second_runtime(self):
        self.private('if [ "$2" = "-c" ]; then exit 0; fi\necho owned-application-started\nexit 17\n')
        result = self.launch()
        self.assertEqual(17, result.returncode)
        self.assertEqual('owned-application-started\n', result.stdout)
        self.assertEqual('', result.stderr)

    def test_child_environment_does_not_leak_to_parent_dialogs_or_tools(self):
        result = subprocess.run(['/bin/sh', '-c',
            '. "$1"; (remotedesk_python "$2" "$3") >/dev/null; printf "%s" "${PYTHONHOME-unset}"',
            'owned-launcher', str(self.helper), str(self.root), str(self.script)],
            capture_output=True, text=True, timeout=10,
            env={k: v for k, v in os.environ.items() if k != 'PYTHONHOME'})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('unset', result.stdout)

    def test_host_exec_preserves_pid_so_stop_does_not_leave_an_orphan(self):
        result = subprocess.run(['/bin/sh', '-c',
            'printf "%s\\n" "$$"; . "$1"; remotedesk_python "$2" "$3"',
            'owned-launcher', str(self.helper), str(self.root), str(self.script)],
            capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        launcher_pid, child_report = result.stdout.splitlines()
        self.assertEqual(int(launcher_pid), json.loads(child_report)['pid'])


class RuntimePackagingTests(unittest.TestCase):
    def test_portable_installed_and_system_python_launchers_use_same_selector(self):
        publish = (ROOT / 'scripts/Publish-RemoteDesk.ps1').read_text(encoding='utf-8-sig')
        self.assertEqual(8, publish.count('remotedesk_python "') + publish.count('remotedesk_python /opt'))
        self.assertIn('scripts/linux/remotedesk_linux_runtime.sh', publish)
        system = (ROOT / 'scripts/Build-LinuxSystemPackage.ps1').read_text(encoding='utf-8-sig')
        self.assertIn('app/remotedesk_linux_runtime.sh', system)
        self.assertIn('remotedesk_python', (ROOT / 'scripts/linux/remotedesk-linux-app').read_text())


if __name__ == '__main__':
    unittest.main()
