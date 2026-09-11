#!/bin/sh
set -eu
umask 077

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this installer as root or through sudo." >&2
    exit 2
fi

SERVER_SOURCE="${1:-}"
if [ -z "$SERVER_SOURCE" ] || [ ! -f "$SERVER_SOURCE" ]; then
    echo "Relay server source is missing." >&2
    exit 2
fi

IFS= read -r PROPOSED_TOKEN
IFS= read -r RELAY_PORT

case "$PROPOSED_TOKEN" in
    *[!0-9a-fA-F]*|'')
        echo "Relay token must be hexadecimal." >&2
        exit 2
        ;;
esac
if [ "${#PROPOSED_TOKEN}" -lt 64 ]; then
    echo "Relay token is too short." >&2
    exit 2
fi
case "$RELAY_PORT" in
    *[!0-9]*|'')
        echo "Relay port is invalid." >&2
        exit 2
        ;;
esac
if [ "$RELAY_PORT" -lt 1 ] || [ "$RELAY_PORT" -gt 65535 ]; then
    echo "Relay port is invalid." >&2
    exit 2
fi

install_packages() {
    need_python=false
    need_openssl=false
    need_useradd=false
    command -v python3 >/dev/null 2>&1 || need_python=true
    command -v openssl >/dev/null 2>&1 || need_openssl=true
    command -v useradd >/dev/null 2>&1 || need_useradd=true
    if [ "$need_python" = false ] && \
       [ "$need_openssl" = false ] && \
       [ "$need_useradd" = false ]; then
        return 0
    fi

    if command -v apt-get >/dev/null 2>&1; then
        packages=""
        [ "$need_python" = false ] || packages="$packages python3"
        [ "$need_openssl" = false ] || packages="$packages openssl"
        [ "$need_useradd" = false ] || packages="$packages passwd"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y $packages
    elif command -v dnf >/dev/null 2>&1; then
        packages=""
        [ "$need_python" = false ] || packages="$packages python3"
        [ "$need_openssl" = false ] || packages="$packages openssl"
        [ "$need_useradd" = false ] || packages="$packages shadow-utils"
        dnf install -y $packages
    elif command -v yum >/dev/null 2>&1; then
        packages=""
        [ "$need_python" = false ] || packages="$packages python3"
        [ "$need_openssl" = false ] || packages="$packages openssl"
        [ "$need_useradd" = false ] || packages="$packages shadow-utils"
        yum install -y $packages
    else
        echo "Please install Python 3 and OpenSSL first." >&2
        exit 3
    fi
}

install_packages
python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else "Python 3.8 or newer is required.")'
if ! command -v systemctl >/dev/null 2>&1; then
    echo "This relay installer requires a systemd-based Linux server." >&2
    exit 3
fi
systemctl --system show-environment >/dev/null
command -v flock >/dev/null 2>&1 || { echo "Install util-linux (flock) first." >&2; exit 3; }
if [ -L /etc/remotedesk-relay ] || [ -L /etc/remotedesk-relay/.install.lock ]; then
    echo "Refusing a symlinked relay configuration directory or installation lock." >&2
    exit 3
fi
if [ -e /etc/remotedesk-relay/.install.lock ] && [ ! -f /etc/remotedesk-relay/.install.lock ]; then
    echo "Refusing a non-regular installation lock." >&2
    exit 3
fi
mkdir -p /etc/remotedesk-relay
exec 9>/etc/remotedesk-relay/.install.lock
flock -w 120 9
# The helper is embedded so existing two-file SSH/manual installers keep working.
# Neither a relay token nor an administrator password is passed in argv.
exec python3 - "$SERVER_SOURCE" "$RELAY_PORT" 3<<TOKEN_INPUT <<'REMOTEDESK_DEPLOY_PY'
$PROPOSED_TOKEN
TOKEN_INPUT
from __future__ import annotations

import ast
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import ssl
import stat
import struct
import subprocess
import sys
import tempfile
import time

SERVICE = "remotedesk-relay.service"
SERVICE_USER = "remotedesk-relay"
CONFIG_DIR = Path("/etc/remotedesk-relay")
STATE_DIR = Path("/var/lib/remotedesk-relay")
INSTALL_DIR = Path("/usr/local/lib/remotedesk-relay")
CONFIG = CONFIG_DIR / "config.json"
CERT = CONFIG_DIR / "relay.crt"
KEY = CONFIG_DIR / "relay.key"
SERVER = INSTALL_DIR / "remotedesk_relay_server.py"
UNIT = Path("/etc/systemd/system/remotedesk-relay.service")
METADATA = STATE_DIR / "deployment.json"
PENDING = STATE_DIR / "deployment-pending.json"
FILES = (CONFIG, CERT, KEY, SERVER, UNIT, METADATA)
HEALTH_TIMEOUT = 12


class DeploymentError(Exception):
    pass


def command(*args, check=True):
    result = subprocess.run(args, capture_output=True, text=True, timeout=45)
    if check and result.returncode:
        # Remote command output may contain config contents; never echo it.
        raise DeploymentError("Command failed: " + args[0] + " (exit " + str(result.returncode) + ").")
    return result


def regular_file(path):
    if path.is_symlink() or (path.exists() and not path.is_file()):
        raise DeploymentError("Refusing non-regular installation file: " + str(path))


def version_tuple(value):
    if not isinstance(value, str) or not re.fullmatch(r"(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})", value):
        raise DeploymentError("Invalid relay release version.")
    return tuple(map(int, value.split(".")))


def identity(path, required=False):
    regular_file(path)
    if not path.exists():
        if required:
            raise DeploymentError("Relay source is missing.")
        return ("0.0.0", "")
    data = path.read_bytes()
    if len(data) > 2 * 1024 * 1024:
        raise DeploymentError("Relay source is too large.")
    tree = ast.parse(data, filename=str(path))
    compile(tree, str(path), "exec")
    values = [node.value for node in tree.body if isinstance(node, ast.Assign)
              and any(isinstance(target, ast.Name) and target.id == "RELAY_RELEASE_VERSION" for target in node.targets)]
    if not values:
        if required:
            raise DeploymentError("Incoming relay has no release version; upgrade this client.")
        return ("0.0.0", hashlib.sha256(data).hexdigest())  # Pre-1.0.8 migration.
    if len(values) != 1:
        raise DeploymentError("Ambiguous relay release version.")
    value = ast.literal_eval(values[0])
    version_tuple(value)
    return (value, hashlib.sha256(data).hexdigest())


def json_bytes(value):
    return (json.dumps(value, indent=2) + "\n").encode("utf-8")


def atomic_write(path, data, mode, owner=None):
    regular_file(path)
    fd, temporary = tempfile.mkstemp(prefix="." + path.name + "-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        if owner is not None:
            command("chown", owner, temporary)
        os.replace(temporary, path)
        # Persist the rename as well as the contents before restarting systemd.
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def read_config():
    value = json.loads(CONFIG.read_text(encoding="utf-8"))
    if (not isinstance(value, dict) or not isinstance(value.get("access_token"), str)
            or len(value["access_token"]) < 32 or type(value.get("port")) is not int
            or not 1 <= value["port"] <= 65535):
        raise DeploymentError("Existing relay configuration is invalid; refusing to overwrite it.")
    if value.get("cert_file") != str(CERT) or value.get("key_file") != str(KEY):
        raise DeploymentError("Custom certificate paths require a manual relay upgrade.")
    if not CERT.exists() or not KEY.exists() or not CERT.stat().st_size or not KEY.stat().st_size:
        raise DeploymentError("Existing relay certificate/key is missing; restore its backup instead of changing identity.")
    return value


def exchange(config, request, timeout=2):
    deadline = time.monotonic() + timeout
    expected_cert = ssl.PEM_cert_to_DER_cert(Path(config["cert_file"]).read_text(encoding="ascii"))
    context = ssl.create_default_context(cafile=config["cert_file"])
    context.check_hostname = False
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    bind = config.get("bind", "0.0.0.0")
    target = {"0.0.0.0": "127.0.0.1", "::": "::1"}.get(bind, bind)
    ipaddress.ip_address(target)  # Health checks never resolve an arbitrary hostname.
    with socket.create_connection((target, config["port"]), timeout=timeout) as tcp:
        with context.wrap_socket(tcp, server_hostname=None) as tls:
            if tls.getpeercert(binary_form=True) != expected_cert:
                raise DeploymentError("Relay TLS certificate mismatch.")
            # Send the token only after authenticating the configured certificate.
            payload = json.dumps(dict(version=1, token=config["access_token"], **request)).encode("utf-8")
            tls.settimeout(max(0.001, deadline - time.monotonic()))
            tls.sendall(struct.pack(">I", len(payload)) + payload)
            def read_exact(count):
                chunks = bytearray()
                while len(chunks) < count:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("Relay health response timed out.")
                    tls.settimeout(remaining)
                    chunk = tls.recv(count - len(chunks))
                    if not chunk:
                        raise DeploymentError("Relay health response was truncated.")
                    chunks.extend(chunk)
                return bytes(chunks)
            length = struct.unpack(">I", read_exact(4))[0]
            if not 0 < length <= 65536:
                raise DeploymentError("Relay health response is oversized or empty.")
            data = read_exact(length)
            value = json.loads(data)
            if not isinstance(value, dict) or value.get("ok") is not True:
                raise DeploymentError("Relay authentication/health check failed.")
            return value


def status(config, expected):
    if version_tuple(expected[0]) >= (1, 0, 8):
        value = exchange(config, {"role": "health"})
        if (value.get("serverVersion") != expected[0] or value.get("serverSourceSha256") != expected[1]
                or type(value.get("busy")) is not bool):
            raise DeploymentError("Running relay does not match the installed version/source.")
        return value
    # Legacy servers have no health operation. Use their authenticated directory,
    # checking every page so an active viewer after the first 32 hosts is not missed.
    offset, seen, busy = 0, set(), False
    deadline = time.monotonic() + HEALTH_TIMEOUT
    for _ in range(16):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise DeploymentError("Legacy relay directory health check timed out.")
        value = exchange(config, {"role": "directory", "offset": offset, "pageSize": 32}, min(2, remaining))
        devices = value.get("devices")
        if not isinstance(devices, list) or any(not isinstance(device, dict) or type(device.get("busy")) is not bool for device in devices):
            raise DeploymentError("Invalid legacy relay directory.")
        busy = busy or any(device["busy"] for device in devices)
        following = value.get("nextOffset")
        if following is None:
            return {"busy": busy}
        if type(following) is not int or following <= offset or following >= 512 or following in seen:
            raise DeploymentError("Invalid legacy relay directory pagination.")
        seen.add(following)
        offset = following
    raise DeploymentError("Legacy relay directory exceeded its bound.")


def wait_healthy(config, expected):
    deadline = time.monotonic() + HEALTH_TIMEOUT
    successes = 0
    while True:
        try:
            result = status(config, expected)
            if command("systemctl", "is-active", "--quiet", SERVICE, check=False).returncode == 0:
                successes += 1
                if successes >= 2:
                    return result
            else:
                successes = 0
        except (OSError, ValueError, DeploymentError):
            successes = 0
        if time.monotonic() >= deadline:
            raise DeploymentError("Relay failed TLS/authenticated runtime-version health check.")
        time.sleep(0.2)


def unit_bytes():
    return ("""[Unit]
Description=RemoteDesk private relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=remotedesk-relay
Group=remotedesk-relay
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
ExecStart=/usr/bin/python3 """ + str(SERVER) + " " + str(CONFIG) + """
Restart=always
RestartSec=2
TimeoutStopSec=10
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
StateDirectory=remotedesk-relay-names
StateDirectoryMode=0700
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
MemoryDenyWriteExecute=true
RestrictAddressFamilies=AF_INET AF_INET6
LimitNOFILE=4096

[Install]
WantedBy=multi-user.target
""").encode("utf-8")


def restore(snapshot, was_active, was_enabled, old_config, old_identity):
    # Stop the candidate before restoring any of its inputs.
    command("systemctl", "stop", SERVICE, check=False)
    for path, previous in snapshot.items():
        if previous is None:
            regular_file(path)
            path.unlink(missing_ok=True)
        else:
            data, mode, owner = previous
            atomic_write(path, data, mode, owner)
    command("systemctl", "daemon-reload")
    if was_enabled:
        command("systemctl", "enable", SERVICE)
    else:
        command("systemctl", "disable", SERVICE, check=False)
    if was_active:
        command("systemctl", "restart", SERVICE)
        wait_healthy(old_config, old_identity)


def deploy(source, port, proposed_token):
    incoming = identity(source, required=True)
    for directory in (CONFIG_DIR, STATE_DIR, INSTALL_DIR):
        if directory.is_symlink() or (directory.exists() and not directory.is_dir()):
            raise DeploymentError("Refusing non-directory installation path.")
    for path in (*FILES, PENDING):
        regular_file(path)
    if PENDING.exists():
        raise DeploymentError("An interrupted deployment needs recovery; see " + str(PENDING))
    installed = identity(SERVER)
    previous_metadata = json.loads(METADATA.read_text()) if METADATA.exists() else {}
    floor = max(version_tuple(installed[0]), version_tuple(previous_metadata.get("version", "0.0.0")))
    if version_tuple(incoming[0]) < floor:
        raise DeploymentError("Refusing relay downgrade; update this client before configuring the server.")
    # Same-version source changes are ambiguous, not silent hot-fixes.
    if incoming[0] == installed[0] and incoming[1] != installed[1]:
        raise DeploymentError("Relay source changed without a version bump; use a newer release.")
    if previous_metadata.get("version") == incoming[0] and previous_metadata.get("sha256") != incoming[1]:
        raise DeploymentError("Relay source differs from the last verified release; use a newer release.")
    previous = read_config() if CONFIG.exists() else None
    active = command("systemctl", "is-active", "--quiet", SERVICE, check=False).returncode == 0
    enabled = command("systemctl", "is-enabled", "--quiet", SERVICE, check=False).returncode == 0
    if active and previous is None:
        raise DeploymentError("Running relay has no readable configuration; refusing to replace it.")
    config = dict(previous or {})
    config.update(access_token=previous["access_token"] if previous else proposed_token,
                  bind=config.get("bind", "0.0.0.0"), port=port, cert_file=str(CERT), key_file=str(KEY),
                  device_names_file="/var/lib/remotedesk-relay-names/device-names.json")
    config_data = CONFIG.read_bytes() if previous == config else json_bytes(config)
    desired_unit = unit_bytes()
    changed = (incoming != installed or previous != config or not UNIT.exists() or UNIT.read_bytes() != desired_unit)
    if previous and active:
        health = status(previous, installed)
        if changed and health["busy"]:
            raise DeploymentError("Relay has an active/pending session; retry after it finishes.")
    if not changed and active:
        # No restart, backup growth, token rotation or needless rewrite on reconfiguration.
        return config, incoming, False
    if command("id", SERVICE_USER, check=False).returncode:
        command("useradd", "--system", "--home-dir", str(STATE_DIR), "--create-home", "--shell", "/usr/sbin/nologin", SERVICE_USER)
    for directory in (STATE_DIR, CONFIG_DIR, INSTALL_DIR):
        directory.mkdir(parents=True, exist_ok=True)
    command("chown", "root:root", str(STATE_DIR))
    os.chmod(STATE_DIR, 0o700)
    backup = Path(tempfile.mkdtemp(prefix="update-", dir=STATE_DIR))
    os.chmod(backup, 0o700)
    snapshot = {}
    inventory = {}
    for path in FILES:
        if path.exists():
            attrs = path.stat()
            data, mode, owner = path.read_bytes(), stat.S_IMODE(attrs.st_mode), str(attrs.st_uid) + ":" + str(attrs.st_gid)
            snapshot[path] = (data, mode, owner)
            atomic_write(backup / path.name, data, 0o600)
            inventory[str(path)] = {"file": path.name, "mode": mode, "owner": owner}
        else:
            snapshot[path] = None
            inventory[str(path)] = None
    atomic_write(backup / "restore.json", json_bytes({"files": inventory, "wasActive": active, "wasEnabled": enabled}), 0o600)
    if active and status(previous, installed)["busy"]:
        # Snapshot preparation can take time. Recheck before changing live files;
        # an aborted preflight must never restart an otherwise healthy service.
        raise DeploymentError("A relay session started; update was not activated.")
    atomic_write(PENDING, json_bytes({"backup": str(backup), "version": incoming[0]}), 0o600)
    try:
        if not previous:
            if CERT.exists() != KEY.exists():
                raise DeploymentError("Incomplete existing TLS identity; restore its missing file first.")
            if not CERT.exists():
                command("openssl", "req", "-x509", "-newkey", "rsa:3072", "-sha256", "-nodes",
                        "-keyout", str(backup / "new.key"), "-out", str(backup / "new.crt"), "-days", "3650",
                        "-subj", "/CN=RemoteDesk Private Relay")
                atomic_write(CERT, (backup / "new.crt").read_bytes(), 0o640, "root:" + SERVICE_USER)
                atomic_write(KEY, (backup / "new.key").read_bytes(), 0o640, "root:" + SERVICE_USER)
                (backup / "new.key").unlink()
                (backup / "new.crt").unlink()
        atomic_write(CONFIG, config_data, 0o640, "root:" + SERVICE_USER)
        atomic_write(SERVER, source.read_bytes(), 0o755, "root:" + SERVICE_USER)
        atomic_write(UNIT, desired_unit, 0o644, "root:root")
        for directory in (CONFIG_DIR, INSTALL_DIR):
            command("chown", "root:" + SERVICE_USER, str(directory))
            os.chmod(directory, 0o750)
        command("chown", "root:" + SERVICE_USER, str(CERT), str(KEY))
        os.chmod(CERT, 0o640)
        os.chmod(KEY, 0o640)
        command("systemctl", "daemon-reload")
        command("systemctl", "restart", SERVICE)
        wait_healthy(config, incoming)
        command("systemctl", "enable", SERVICE)
        if shutil.which("ufw") and command("ufw", "status", check=False).stdout.startswith("Status: active"):
            command("ufw", "allow", str(port) + "/tcp", "comment", "RemoteDesk relay")
        if shutil.which("firewall-cmd") and not command("firewall-cmd", "--state", check=False).returncode:
            command("firewall-cmd", "--permanent", "--add-port=" + str(port) + "/tcp")
            command("firewall-cmd", "--reload")
        atomic_write(METADATA, json_bytes({"version": incoming[0], "sha256": incoming[1], "previousBackup": str(backup)}), 0o600)
        PENDING.unlink()
    except BaseException:
        # A second signal must not interrupt recovery halfway through.
        for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            signal.signal(signum, signal.SIG_IGN)
        try:
            restore(snapshot, active, enabled, previous, installed)
            PENDING.unlink()
            print("Relay update failed; previous files/service restored. Backup: " + str(backup), file=sys.stderr)
        except BaseException:
            print("Relay rollback needs manual recovery; backup: " + str(backup), file=sys.stderr)
        raise
    return config, incoming, previous is None


def main():
    def interrupted(signum, _frame):
        raise DeploymentError("Relay deployment interrupted by signal " + str(signum))
    for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(signum, interrupted)
    try:
        with os.fdopen(3, encoding="ascii") as stream:
            token = stream.read(4096).strip()
        config, current, installed = deploy(Path(sys.argv[1]), int(sys.argv[2]), token)
        certificate = ssl.PEM_cert_to_DER_cert(CERT.read_text(encoding="ascii"))
        print("REMOTEDESK_RELAY_RESULT=" + json.dumps({
            "installed": installed, "port": config["port"], "accessToken": config["access_token"],
            "tlsCertificateSha256": hashlib.sha256(certificate).hexdigest().upper(),
            "serverVersion": current[0], "serverSourceSha256": current[1], "healthVerified": True,
        }))
        return 0
    except Exception as error:
        print(str(error) if isinstance(error, DeploymentError) else "Relay deployment failed: " + type(error).__name__, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
REMOTEDESK_DEPLOY_PY
