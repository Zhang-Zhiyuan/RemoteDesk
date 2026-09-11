"""Authorized server update/fixture test. Admin password uses a masked prompt, not argv/files."""
import argparse
import getpass
import json
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--server', required=True, help='Explicitly selected owned relay; must match saved settings')
parser.add_argument('--deploy', action='store_true')
parser.add_argument('--android', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = root / 'artifacts/relay-names-20260912/public'
output.mkdir(parents=True, exist_ok=True)
value = dict(output=str(output), deploy=args.deploy, android=args.android, expectedServer=args.server)
if args.deploy:
    value['password'] = getpass.getpass('Authorized relay root password (hidden): ')
result = subprocess.run(['dotnet', str(root / 'experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.dll'),
                         'relay-names'], input=json.dumps(value), capture_output=True, text=True, encoding='utf-8', timeout=340)
if result.returncode:
    print('Naming probe failed; no credentials logged. Check redacted evidence.')
    # Keep useful installer failures without persisting untrusted remote output.
    for marker in ('active/pending session', 'version bump', 'downgrade', 'SSH', 'Owned cross-platform', 'naming capability'):
        if marker in result.stderr:
            print('Failure category: ' + marker)
else:
    print(result.stdout.strip())
sys.exit(result.returncode)
