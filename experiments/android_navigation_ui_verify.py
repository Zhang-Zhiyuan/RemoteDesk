#!/usr/bin/env python3
"""Check the signed APK's navigation on an owned emulator, without remote input."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

PACKAGE = 'com.remotedesk.agent'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--adb', required=True)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--history', default='')
    args = parser.parse_args()
    if not re.fullmatch(r'emulator-\d+', args.serial):
        raise ValueError('This navigation test is restricted to an owned emulator')
    output = args.output.resolve()
    if not output.is_relative_to(Path(__file__).resolve().parents[1] / 'artifacts'):
        raise ValueError('Evidence must stay inside a named artifacts subdirectory')
    output.mkdir(parents=True, exist_ok=False)
    report = dict(scope='Signed product APK navigation; no capture or remote OS input', checks=[])

    def adb(*command):
        return subprocess.run([args.adb, '-s', args.serial, *map(str, command)],
                              check=True, capture_output=True, timeout=25).stdout

    def check(name, passed):
        report['checks'].append(dict(name=name, passed=bool(passed)))
        (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        if not passed:
            raise AssertionError(name)

    def ui(name):
        adb('shell', 'uiautomator', 'dump', '/data/local/tmp/remotedesk-navigation-ui.xml')
        xml = adb('exec-out', 'cat', '/data/local/tmp/remotedesk-navigation-ui.xml')
        (output / (name + '.xml')).write_bytes(xml)
        return ET.fromstring(xml)

    def text(tree):
        return '\n'.join(n.get('text', '') for n in tree.iter('node') if n.get('package') == PACKAGE)

    def select(page, name):
        tree = ui(name + '-before')
        nodes = [n for n in tree.iter('node') if n.get('content-desc') == page and n.get('package') == PACKAGE]
        check(name + '-navigation-visible', len(nodes) == 1)
        bounds = list(map(int, re.findall(r'\d+', nodes[0].get('bounds', ''))))
        check(name + '-touch-target', len(bounds) == 4 and bounds[2] - bounds[0] >= 48 and bounds[3] - bounds[1] >= 48)
        adb('shell', 'input', 'tap', (bounds[0] + bounds[2]) // 2, (bounds[1] + bounds[3]) // 2)
        tree = ui(name)
        check(name + '-selected', any(n.get('content-desc') == page and n.get('selected') == 'true' for n in tree.iter('node')))
        (output / (name + '.png')).write_bytes(adb('exec-out', 'screencap', '-p'))
        return tree

    rotation = adb('shell', 'settings', 'get', 'system', 'user_rotation').decode().strip()
    automatic = adb('shell', 'settings', 'get', 'system', 'accelerometer_rotation').decode().strip()
    try:
        adb('shell', 'am', 'force-stop', PACKAGE)
        adb('shell', 'settings', 'put', 'system', 'accelerometer_rotation', '0')
        adb('shell', 'settings', 'put', 'system', 'user_rotation', '0')
        adb('shell', 'am', 'start', '-W', '-n', PACKAGE + '/.MainActivity')
        tree = select('设备', 'devices')
        check('controller-does-not-demand-screen-capture', '控制其他设备无需本机录屏授权' in text(tree))
        check('devices-are-separated-from-host-permissions', '免重复录屏授权' not in text(tree))
        tree = select('本机被控', 'host')
        check('local-host-has-its-own-page', '共享这台 Android' in text(tree) and '连接另一台设备' not in text(tree))
        tree = select('设置', 'settings')
        check('readiness-is-in-settings', '就绪检查' in text(tree) and '连接另一台设备' not in text(tree))
        adb('shell', 'settings', 'put', 'system', 'user_rotation', '1')
        time.sleep(.8)
        tree = ui('landscape-settings')
        check('rotation-preserves-selected-page', any(n.get('content-desc') == '设置' and n.get('selected') == 'true' for n in tree.iter('node')))
        select('设备', 'landscape-devices')
        adb('shell', 'settings', 'put', 'system', 'user_rotation', '0')
        time.sleep(.8)
        tree = select('设备', 'portrait-restored')
        if args.history:
            found = args.history in text(tree)
            for attempt in range(9):
                if found:
                    break
                adb('shell', 'input', 'swipe', 160, 520, 160, 160, 320)
                tree = ui('history-' + str(attempt))
                found = args.history in text(tree)
            check('overwrite-upgrade-keeps-history', found)
        print(json.dumps(report, ensure_ascii=True))
    finally:
        for key, value in (('user_rotation', rotation), ('accelerometer_rotation', automatic)):
            if value == 'null':
                adb('shell', 'settings', 'delete', 'system', key)
            else:
                adb('shell', 'settings', 'put', 'system', key, value)


if __name__ == '__main__':
    main()
