"""One-time SSH administrator login. Root passwords never enter stored relay settings."""
from dataclasses import dataclass, replace
import base64
import hashlib
import hmac
import json
from pathlib import Path
import re
import shlex
import socket
import threading
import time

import remotedesk_linux_relay as relay

TIMEOUT = 12
MAX_RESPONSE = 65536


class RelayLoginError(Exception):
    """Only application-owned, credential-free messages may be displayed."""


@dataclass(frozen=True)
class LoginRequest:
    server: str
    ssh_port: int = 22
    username: str = 'root'
    expected_identity: str = ''

    def validate(self):
        server, port = relay._checked_endpoint(self.server, self.ssh_port)
        username = self.username.strip()
        if not re.fullmatch(r'[a-zA-Z_][a-zA-Z0-9_.-]{0,63}', username):
            raise RelayLoginError('服务器管理员账号无效；默认使用 root。')
        identity = normalize_identity(self.expected_identity)
        return replace(self, server=server, ssh_port=port, username=username, expected_identity=identity)


def normalize_identity(value):
    value = value.strip()
    if not value:
        return ''
    raw = value[7:] if value[:7].lower() == 'sha256:' else value
    try:
        decoded = base64.b64decode(raw.rstrip('=') + '=', validate=True)
        if len(decoded) != 32:
            raise ValueError()
    except ValueError as error:
        raise RelayLoginError('已保存的服务器身份无效；原配置未更改。') from error
    return 'SHA256:' + base64.b64encode(decoded).decode('ascii').rstrip('=')


def fingerprint(key_bytes):
    return 'SHA256:' + base64.b64encode(hashlib.sha256(key_bytes).digest()).decode('ascii').rstrip('=')


def verify_identity(expected, key_bytes):
    actual = fingerprint(key_bytes)
    if expected and not hmac.compare_digest(normalize_identity(expected), actual):
        raise RelayLoginError('服务器 SSH 身份已变化，未发送 root 密码；请先核实服务器是否更换或重装。')
    return actual


def read_command(username):
    source = Path(__file__).with_name('read_remotedesk_relay_config.py')
    if not source.is_file():
        source = Path(__file__).resolve().parents[1] / 'relay/read_remotedesk_relay_config.py'
    code = base64.b64encode(source.read_bytes()).decode('ascii')
    command = 'python3 -c ' + shlex.quote("exec(__import__('base64').b64decode('" + code + "'))")
    return command if username == 'root' else "sudo -k -S -p '' -- " + command


def parse_response(text, request, identity, device_id, publish):
    if len(text) > MAX_RESPONSE:
        raise RelayLoginError('服务器返回的配置过大，原配置未更改。')
    try:
        value = json.loads(text)
        if not isinstance(value, dict):
            raise ValueError()
        error = value.get('errorCode')
        if error == 'not_configured':
            raise RelayLoginError('服务器尚未安装中继，请先在 Windows 端使用“部署 / 更新服务器”。')
        if error == 'permission_denied':
            raise RelayLoginError('此账号不能读取中继配置，请使用 root 或有 sudo 权限的管理员。')
        if error or type(value.get('version')) is not int or value['version'] != 1:
            raise ValueError()
        options = relay.RelayOptions(request.server, value['port'], value['accessToken'],
            value['tlsCertificateSha256'], device_id, publish,
            request.ssh_port, request.username, identity).validate()
        return options
    except RelayLoginError:
        raise
    except (KeyError, ValueError, TypeError, AttributeError) as error:
        raise RelayLoginError('服务器中继配置无效；未保存，请检查服务器安装状态。') from error


class LoginOperation:
    def __init__(self):
        self.cancelled = threading.Event()
        self._lock = threading.Lock()
        self._resources = []

    def track(self, resource):
        with self._lock:
            if self.cancelled.is_set():
                resource.close()
                raise RelayLoginError('服务器登录已取消，原配置未更改。')
            self._resources.append(resource)
        return resource

    def close(self):
        with self._lock:
            self.cancelled.set()
            resources, self._resources = self._resources, []
        for resource in reversed(resources):
            try:
                resource.close()
            except Exception:
                pass

    def connect(self, request):
        if self.cancelled.is_set():
            raise RelayLoginError('服务器登录已取消，原配置未更改。')
        # Try alternate DNS answers, including IPv6. Register every socket
        # before TCP starts so cancellation cannot authenticate a late dial.
        addresses = socket.getaddrinfo(request.server, request.ssh_port, type=socket.SOCK_STREAM)
        for family, kind, protocol, _, address in addresses[:4]:
            connection = self.track(socket.socket(family, kind, protocol))
            try:
                connection.settimeout(TIMEOUT)
                connection.connect(address)
                if self.cancelled.is_set():
                    raise RelayLoginError('服务器登录已取消，原配置未更改。')
                return connection
            except OSError:
                connection.close()
        raise RelayLoginError('无法连接服务器 SSH，请检查地址、SSH 端口和网络。原配置未更改。')

    def login(self, request, password, device_id, publish=True):
        request = request.validate()
        if not password or len(password) > 4096 or '\n' in password or '\r' in password or '\0' in password:
            raise RelayLoginError('请输入服务器 root / 管理员密码；不是设备密钥。')
        try:
            import paramiko
        except ImportError as error:
            raise RelayLoginError('缺少 SSH 登录组件，请重新启动并允许安装 python3-paramiko。') from error
        deadline = threading.Timer(40, self.close)
        deadline.daemon = True
        deadline.start()
        try:
            connection = self.connect(request)
            transport = self.track(paramiko.Transport(connection))
            transport.banner_timeout = TIMEOUT
            transport.auth_timeout = TIMEOUT
            transport.start_client(timeout=TIMEOUT)
            identity = verify_identity(request.expected_identity, transport.get_remote_server_key().asbytes())
            if self.cancelled.is_set():
                raise RelayLoginError('服务器登录已取消，原配置未更改。')
            transport.auth_password(request.username, password, fallback=False)
            channel = self.track(transport.open_session(timeout=TIMEOUT))
            channel.settimeout(TIMEOUT)
            channel.exec_command(read_command(request.username))
            if request.username != 'root':
                channel.sendall((password + '\n').encode('utf-8'))
            channel.shutdown_write()
            output = bytearray()
            limit = time.monotonic() + TIMEOUT
            while True:
                if self.cancelled.is_set() or time.monotonic() > limit:
                    raise RelayLoginError('读取服务器配置超时或已取消；原配置未更改。')
                if channel.recv_ready():
                    output.extend(channel.recv(8192))
                    if len(output) > MAX_RESPONSE:
                        raise RelayLoginError('服务器返回的配置过大，原配置未更改。')
                elif channel.recv_stderr_ready():
                    channel.recv_stderr(8192)  # Never log privileged command output.
                elif channel.exit_status_ready():
                    break
                else:
                    self.cancelled.wait(.02)
            if channel.recv_exit_status() != 0:
                raise RelayLoginError('无法读取中继配置；请检查 root / sudo 权限及服务器 Python 3。')
            return parse_response(output, request, identity, device_id, publish)
        except RelayLoginError:
            raise
        except paramiko.AuthenticationException as error:
            raise RelayLoginError('服务器 root / 管理员密码错误，或 SSH 禁止密码登录。') from error
        except Exception as error:
            raise RelayLoginError('无法登录服务器 SSH；请检查服务器地址、SSH 端口和网络。原配置未更改。') from error
        finally:
            password = None
            deadline.cancel()
            self.close()
