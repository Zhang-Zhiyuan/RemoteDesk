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
python3 - "$SERVER_SOURCE" <<'PY'
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    compile(stream.read(), sys.argv[1], "exec")
PY

SERVICE_USER="remotedesk-relay"
STATE_DIR="/var/lib/remotedesk-relay"
CONFIG_DIR="/etc/remotedesk-relay"
INSTALL_DIR="/usr/local/lib/remotedesk-relay"
CONFIG_FILE="$CONFIG_DIR/config.json"
CERT_FILE="$CONFIG_DIR/relay.crt"
KEY_FILE="$CONFIG_DIR/relay.key"
SERVER_FILE="$INSTALL_DIR/remotedesk_relay_server.py"
UNIT_FILE="/etc/systemd/system/remotedesk-relay.service"
INSTALLED=false
NEEDS_RESTART=false

mkdir -p "$CONFIG_DIR"
exec 9>"$CONFIG_DIR/.install.lock"
flock -w 120 9

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --home-dir "$STATE_DIR" --create-home \
        --shell /usr/sbin/nologin "$SERVICE_USER"
fi

mkdir -p "$STATE_DIR" "$CONFIG_DIR" "$INSTALL_DIR"
if [ ! -f "$CONFIG_FILE" ]; then
    INSTALLED=true
fi

if [ ! -s "$CERT_FILE" ] || [ ! -s "$KEY_FILE" ]; then
    openssl req -x509 -newkey rsa:3072 -sha256 -nodes \
        -keyout "$KEY_FILE" -out "$CERT_FILE" -days 3650 \
        -subj "/CN=RemoteDesk Private Relay" >/dev/null 2>&1
    INSTALLED=true
    NEEDS_RESTART=true
fi

CONFIG_CHANGED="$(python3 - "$CONFIG_FILE" "$RELAY_PORT" \
    "$CERT_FILE" "$KEY_FILE" 3<<TOKEN_INPUT <<'PY'
$PROPOSED_TOKEN
TOKEN_INPUT
import json
import os
import sys

path, port, cert_file, key_file = sys.argv[1:]
with os.fdopen(3, encoding="ascii") as stream:
    token = stream.read().strip()
previous = None
if os.path.exists(path):
    with open(path, encoding="utf-8") as stream:
        previous = json.load(stream)
    token = previous.get("access_token")
    if not isinstance(token, str) or len(token) < 32:
        raise ValueError("Existing relay configuration has an invalid access token; refusing to overwrite it.")
config = dict(previous or {})
config.update(access_token=token, bind="0.0.0.0", port=int(port),
              cert_file=cert_file, key_file=key_file)
if config != previous:
    temporary = path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as stream:
        json.dump(config, stream, indent=2)
        stream.write("\n")
    os.replace(temporary, path)
print("true" if config != previous else "false")
PY
)"
[ "$CONFIG_CHANGED" = false ] || NEEDS_RESTART=true
unset PROPOSED_TOKEN

if ! cmp -s "$SERVER_SOURCE" "$SERVER_FILE"; then
    install -m 0755 "$SERVER_SOURCE" "$SERVER_FILE.tmp"
    mv -f "$SERVER_FILE.tmp" "$SERVER_FILE"
    NEEDS_RESTART=true
fi

chown -R root:"$SERVICE_USER" "$CONFIG_DIR" "$INSTALL_DIR"
chmod 0750 "$CONFIG_DIR" "$INSTALL_DIR"
chmod 0640 "$CONFIG_FILE" "$CERT_FILE"
chmod 0640 "$KEY_FILE"

cat > "$UNIT_FILE.tmp" <<'UNIT'
[Unit]
Description=RemoteDesk private relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=remotedesk-relay
Group=remotedesk-relay
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
ExecStart=/usr/bin/python3 /usr/local/lib/remotedesk-relay/remotedesk_relay_server.py /etc/remotedesk-relay/config.json
Restart=always
RestartSec=2
NoNewPrivileges=true
PrivateTmp=true
PrivateDevices=true
ProtectSystem=strict
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
UNIT

chmod 0644 "$UNIT_FILE.tmp"
if ! cmp -s "$UNIT_FILE.tmp" "$UNIT_FILE"; then
    mv -f "$UNIT_FILE.tmp" "$UNIT_FILE"
    systemctl daemon-reload
    NEEDS_RESTART=true
else
    rm -f "$UNIT_FILE.tmp"
fi
systemctl enable remotedesk-relay.service >/dev/null
if [ "$NEEDS_RESTART" = true ] || ! systemctl is-active --quiet remotedesk-relay.service; then
    systemctl restart remotedesk-relay.service
    sleep 1
fi
systemctl is-active --quiet remotedesk-relay.service

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
    ufw allow "$RELAY_PORT/tcp" comment 'RemoteDesk relay' >/dev/null
fi
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="$RELAY_PORT/tcp" >/dev/null
    firewall-cmd --reload >/dev/null
fi

python3 - "$CONFIG_FILE" "$INSTALLED" <<'PY'
import hashlib
import json
import ssl
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    config = json.load(stream)
with open(config["cert_file"], encoding="ascii") as stream:
    certificate = ssl.PEM_cert_to_DER_cert(stream.read())
print("REMOTEDESK_RELAY_RESULT=" + json.dumps({
    "installed": sys.argv[2] == "true",
    "port": config["port"],
    "accessToken": config["access_token"],
    "tlsCertificateSha256": hashlib.sha256(certificate).hexdigest().upper(),
}))
PY
