#!/usr/bin/env python3
"""Read-only product Linux SSH enrollment + real relay directory check."""
import argparse
from dataclasses import replace
import getpass
import json
from pathlib import Path
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/linux'))
import remotedesk_linux_relay as relay
import remotedesk_linux_relay_login as login


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--fixture', type=Path)
    parser.add_argument('--output', required=True)
    parser.add_argument('--identity', default='')
    parser.add_argument('--wrong-password', action='store_true')
    parser.add_argument('--wrong-identity', action='store_true')
    args = parser.parse_args()
    output = Path(args.output).resolve()
    if not output.is_relative_to(ROOT / 'artifacts') or output.exists():
        raise ValueError('Use a new evidence file inside artifacts')
    if not args.fixture and (args.wrong_password or args.wrong_identity):
        raise ValueError('Negative probes are restricted to the owned fixture')
    request = login.LoginRequest('8.138.5.232', expected_identity=args.identity)
    if args.fixture:
        fixture = json.loads(args.fixture.read_text())
        request = login.LoginRequest('127.0.0.1', fixture['sshPort'], expected_identity=fixture['sshIdentity'])
        password = 'owned-test-root-password' if not args.wrong_password else 'intentionally-wrong-fixture-password'
        if args.wrong_identity:
            request = replace(request, expected_identity='SHA256:' + 'A' * 43)
    else:
        password = getpass.getpass('Authorized server root password (not stored): ')
    result = dict(complete=False)
    try:
        options = login.LoginOperation().login(request, password, str(uuid.uuid4()), False)
        devices = relay.list_devices(options)
        result.update(complete=True, port=options.port, onlineDevices=len(devices),
                      rootPasswordStored=any('password' in name.lower() for name in options.to_dict()))
    except login.RelayLoginError as error:
        result['failure'] = str(error)
    except Exception as error:
        result['failureType'] = type(error).__name__
    finally:
        password = None
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=True), flush=True)
    return 0 if result['complete'] == (not args.wrong_password and not args.wrong_identity) else 1


if __name__ == '__main__':
    raise SystemExit(main())
