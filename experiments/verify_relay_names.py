"""Fixture-only public relay naming. Configuration is piped in memory, never logged."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/linux'))
import remotedesk_linux_relay as relay


def main(mode, config):
    if not config.get('expectedServer') or config['serverAddress'] != config['expectedServer']:
        raise RuntimeError('Unexpected public fixture server')
    options = relay.RelayOptions.from_dict(config)
    observer = relay.replace(options, device_id=str(uuid.uuid4()))
    checks = []
    def check(name, value):
        checks.append(dict(name=name, passed=bool(value)))
        if not value:
            raise RuntimeError(name)
    def target():
        return next(item for item in relay.list_devices(observer) if item['deviceId'] == options.device_id)
    if mode == 'linux':
        check('real Linux runtime', sys.platform.startswith('linux'))
        check('Windows shared name visible to Linux', target()['sharedName'] == 'Windows 共享测试机')
        relay.rename_device(options, 'Linux 广州工作站')
        check('independent Linux observer sees Chinese name', target()['machineName'] == 'Linux 广州工作站')
        # Only synthetic result fields are emitted; never configuration or exception details.
        print('LINUX_NAMING_PASS')
        return checks

    if mode != 'android':
        raise RuntimeError('Invalid fixture mode')
    serial, package = 'emulator-5560', 'com.remotedesk.relayhostprobe'
    adb = str(Path(os.environ['LOCALAPPDATA']) / 'Android/Sdk/platform-tools/adb.exe')
    def run(*args, data=None):
        result = subprocess.run([adb, '-s', serial, *args], input=data, capture_output=True, timeout=25)
        if result.returncode:
            raise RuntimeError('Owned emulator command failed')
        return result.stdout.decode('utf-8').strip()
    if run('shell', 'getprop', 'ro.kernel.qemu') != '1':
        raise RuntimeError('This UI probe is restricted to the owned emulator')
    run('shell', 'am', 'force-stop', package)
    run('shell', 'run-as', package, 'mkdir', '-p', 'files')
    run('shell', "run-as " + package + " sh -c 'cat > files/relay-name-endpoint.json'", data=json.dumps(config).encode())
    run('shell', 'am', 'start', '-n', package + '/com.remotedesk.agent.RelayNameProbeActivity')

    def nodes():
        run('shell', 'uiautomator', 'dump', '/data/local/tmp/rd-relay-names.xml')
        text = run('shell', 'cat', '/data/local/tmp/rd-relay-names.xml')
        return list(ET.fromstring(text).iter('node'))
    def click_node(node):
        numbers = list(map(int, re.findall(r'\d+', node.attrib['bounds'])))
        run('shell', 'input', 'tap', str((numbers[0] + numbers[2]) // 2), str((numbers[1] + numbers[3]) // 2))
    def click(predicate):
        for _ in range(8):
            item = next((n for n in nodes() if predicate(n.attrib) and n.attrib.get('enabled') == 'true'), None)
            if item is not None:
                click_node(item)
                return
            run('shell', 'input', 'swipe', '165', '550', '165', '250', '220')
            time.sleep(.3)
        raise RuntimeError('Owned naming control not found')
    def rename_dialog(expected):
        # Scroll back to the beginning after a previous directory refresh.
        run('shell', 'input', 'swipe', '165', '250', '165', '550', '180')
        click(lambda a: a.get('content-desc') == '共享名称 · ' + expected)
        time.sleep(.4)
        check('dialog explains server-wide sharing', any('同一服务器' in n.attrib.get('text', '') for n in nodes()))
    def enter(text):
        click(lambda a: a.get('class') == 'android.widget.EditText')
        run('shell', 'input', 'keyevent', '123') # Move to end; clear just the owned dialog's field.
        run('shell', 'input', 'keyevent', *(['67'] * 80))
        if text:
            run('shell', 'input', 'text', text)
    def wait_name(expected):
        for _ in range(10):
            if target()['sharedName'] == expected:
                return
            time.sleep(.5)
        raise RuntimeError('Public directory did not reflect Android name')
    try:
        time.sleep(2)
        rename_dialog('Linux 广州工作站')
        enter('Android-Shared-Name')
        click(lambda a: a.get('text') == '保存并同步')
        wait_name('Android-Shared-Name')
        check('Android UI saves a name visible to Linux over public relay', True)
        time.sleep(1)
        rename_dialog('Android-Shared-Name')
        enter('Canceled-Name')
        click(lambda a: a.get('text') == '取消')
        check('cancel does not publish a name', target()['sharedName'] == 'Android-Shared-Name')
        rename_dialog('Android-Shared-Name')
        enter('')
        click(lambda a: a.get('text') == '保存并同步')
        wait_name('')
        check('empty name restores original system name for other clients', target()['machineName'] == config['originalName'])
    except Exception:
        snapshot = subprocess.run([adb, '-s', serial, 'exec-out', 'screencap', '-p'], capture_output=True, timeout=20)
        if snapshot.returncode == 0:
            (Path(config['output']) / 'android-names-failure.png').write_bytes(snapshot.stdout)
        raise
    finally:
        run('shell', 'am', 'force-stop', package)
        run('shell', 'rm', '-f', '/data/local/tmp/rd-relay-names.xml')
    print('ANDROID_NAMING_PASS')
    return checks


if __name__ == '__main__':
    config = json.loads(sys.stdin.readline())
    mode = sys.argv[1]
    try:
        checks = main(mode, config)
        if mode == 'android':
            path = Path(config['output']) / 'android-names.json'
            path.write_text(json.dumps(dict(complete=True, checks=checks), ensure_ascii=False, indent=2), encoding='utf-8')
    except Exception as error:
        # Keep details sanitized: no config, device list, root password or access token in evidence.
        if mode == 'android':
            (Path(config['output']) / 'android-names.json').write_text(json.dumps(dict(complete=False,
                errorType=type(error).__name__, stage=str(error) if isinstance(error, RuntimeError) else 'probe failed')))
        print('NAMING_FAILED:' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
