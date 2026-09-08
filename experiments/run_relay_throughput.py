"""Authorized existing relay; hidden SSH password, no server installation."""
import argparse
import json
from pathlib import Path
import subprocess
from relay_public_config import read_config

parser = argparse.ArgumentParser()
parser.add_argument('--host', required=True)
parser.add_argument('--ssh-pin', required=True)
parser.add_argument('--tls-pin', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
output = Path(args.output).resolve()
if output.exists():
    raise SystemExit('Output already exists; refusing to overwrite evidence')
config = read_config(args.host, args.ssh_pin, args.tls_pin)
result = subprocess.run([str(root / 'experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.exe'), 'transport'],
                        input=json.dumps(dict(relay=config, output=str(output))).encode(), timeout=260)
raise SystemExit(result.returncode)
