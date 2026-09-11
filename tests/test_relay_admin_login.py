import base64
from dataclasses import replace
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/linux'))
import remotedesk_linux_relay as relay
import remotedesk_linux_relay_login as login

spec = importlib.util.spec_from_file_location('relay_login_reader', ROOT / 'scripts/relay/read_remotedesk_relay_config.py')
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)

TOKEN = 'owned-test-relay-token-not-an-admin-password'
ID = '2747fce3-76fa-4458-ac71-8929786b6bdf'
PIN = 'AB' * 32
SSH = login.fingerprint(b'owned-ssh-key')
REQUEST = login.LoginRequest('relay.test', 2222)
RESPONSE = dict(version=1, port=56567, accessToken=TOKEN, tlsCertificateSha256=PIN)


class RelayAdminLoginPolicyTests(unittest.TestCase):
    def test_returned_port_and_token_are_internal_not_a_device_or_root_password(self):
        options = login.parse_response(json.dumps(RESPONSE), REQUEST, SSH, ID, False)
        self.assertEqual((56567, TOKEN, 2222, 'root', SSH, ID, False),
            (options.port, options.access_token, options.ssh_port, options.admin_username, options.ssh_host_key_sha256, options.device_id, options.publish))
        self.assertNotIn(TOKEN, repr(options))
        self.assertFalse(any('password' in name.lower() for name in options.to_dict()))
        self.assertEqual(options, relay.RelayOptions.from_dict(options.to_dict()))

    def test_old_settings_do_not_require_reentry_or_gain_a_root_password(self):
        options = relay.RelayOptions.from_dict(dict(serverAddress='relay.test', port=56567,
            accessToken=TOKEN, tlsCertificateSha256=PIN, deviceId=ID))
        self.assertEqual(TOKEN, options.access_token)
        self.assertEqual(22, options.ssh_port)
        self.assertEqual('', options.ssh_host_key_sha256)
        self.assertEqual('root', options.admin_username)

    def test_bad_response_types_or_secrets_are_not_echoed(self):
        cases = [None, [], {}, dict(RESPONSE, version=True), dict(RESPONSE, port='56567'),
            dict(RESPONSE, port=True), dict(RESPONSE, port=65536), dict(RESPONSE, accessToken='0'),
            dict(RESPONSE, tlsCertificateSha256='invalid')]
        for value in cases:
            with self.subTest(value=value), self.assertRaises(login.RelayLoginError) as error:
                login.parse_response(json.dumps(value), REQUEST, SSH, ID, True)
            self.assertNotIn(TOKEN, str(error.exception))
        with self.assertRaises(login.RelayLoginError):
            login.parse_response('X' * 65537, REQUEST, SSH, ID, True)

    def test_not_installed_and_unprivileged_login_are_actionable(self):
        for code, expected in [('not_configured', '部署 / 更新服务器'), ('permission_denied', 'root'), ('secret-returned-by-peer', '配置无效')]:
            with self.subTest(code=code), self.assertRaisesRegex(login.RelayLoginError, expected) as error:
                login.parse_response(json.dumps(dict(errorCode=code, password='never-echo')), REQUEST, SSH, ID, True)
            self.assertNotIn('never-echo', str(error.exception))
            self.assertNotIn('secret-returned-by-peer', str(error.exception))

    def test_identity_is_normalized_without_case_folding_digest(self):
        self.assertEqual(SSH, login.normalize_identity(SSH + '='))
        self.assertEqual(SSH, login.normalize_identity(SSH[7:]))
        self.assertEqual('', login.normalize_identity(''))
        for value in ('bad', 'SHA256:', 'SHA256:' + 'A' * 64):
            with self.subTest(value=value), self.assertRaises(login.RelayLoginError):
                login.normalize_identity(value)

    def test_changed_identity_is_rejected_before_any_password_method(self):
        self.assertEqual(SSH, login.verify_identity('', b'owned-ssh-key'))
        self.assertEqual(SSH, login.verify_identity(SSH, b'owned-ssh-key'))
        with self.assertRaisesRegex(login.RelayLoginError, '未发送 root 密码'):
            login.verify_identity(SSH, b'changed-key')

    def test_read_only_command_has_no_user_password_or_server_mutations(self):
        command = login.read_command('root')
        self.assertTrue(command.startswith('python3 -c '))
        self.assertTrue(login.read_command('admin').startswith("sudo -k -S -p '' -- python3 -c "))
        for denied in ('systemctl', 'install.sh', 'access_token=', 'rm ', 'password'):
            self.assertNotIn(denied, command)
        import shlex
        decoded_command = shlex.split(command)[2]
        encoded = decoded_command.split("b64decode('", 1)[1].split("'", 1)[0]
        self.assertEqual((ROOT / 'scripts/relay/read_remotedesk_relay_config.py').read_bytes(), base64.b64decode(encoded))

    def test_cancel_closes_resources_and_blocks_late_registration(self):
        operation = login.LoginOperation()
        first, late = mock.Mock(), mock.Mock()
        operation.track(first)
        operation.close()
        first.close.assert_called_once()
        with self.assertRaises(login.RelayLoginError):
            operation.track(late)
        late.close.assert_called_once()

    def test_invalid_login_request_is_rejected_locally(self):
        for request in (replace(REQUEST, ssh_port=True), replace(REQUEST, ssh_port=0),
            replace(REQUEST, server='https://host'), replace(REQUEST, username='root;id')):
            with self.subTest(request=request), self.assertRaises((ValueError, login.RelayLoginError)):
                request.validate()

    def test_tcp_dial_falls_back_to_next_ipv4_or_ipv6_answer(self):
        first, second = mock.Mock(), mock.Mock()
        first.connect.side_effect = OSError('unreachable')
        answers = [(login.socket.AF_INET6, login.socket.SOCK_STREAM, 0, '', ('::1', 22, 0, 0)),
                   (login.socket.AF_INET, login.socket.SOCK_STREAM, 0, '', ('127.0.0.1', 22))]
        operation = login.LoginOperation()
        with mock.patch.object(login.socket, 'getaddrinfo', return_value=answers), \
                mock.patch.object(login.socket, 'socket', side_effect=[first, second]):
            self.assertIs(second, operation.connect(REQUEST))
        first.close.assert_called_once()
        second.connect.assert_called_once_with(('127.0.0.1', 22))
        operation.close()

    def test_cancel_during_dns_cannot_open_or_authenticate_late_socket(self):
        operation, late = login.LoginOperation(), mock.Mock()
        def resolve(*args, **kwargs):
            operation.close()
            return [(login.socket.AF_INET, login.socket.SOCK_STREAM, 0, '', ('127.0.0.1', 22))]
        with mock.patch.object(login.socket, 'getaddrinfo', side_effect=resolve), \
                mock.patch.object(login.socket, 'socket', return_value=late), \
                self.assertRaises(login.RelayLoginError):
            operation.connect(REQUEST)
        late.connect.assert_not_called()
        late.close.assert_called_once()


class RelayLoginReaderTests(unittest.TestCase):
    def test_permission_denied_while_locating_config_is_actionable(self):
        path = mock.Mock()
        path.is_file.side_effect = PermissionError()
        self.assertEqual({'errorCode': 'permission_denied'}, reader.read_configuration(path))

    def test_missing_configuration_is_not_deployed_automatically(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            self.assertEqual({'errorCode': 'not_configured'}, reader.read_configuration(path))
            self.assertFalse(path.exists())

    def test_reader_returns_existing_token_and_certificate_without_reading_private_key(self):
        with tempfile.TemporaryDirectory() as directory:
            path, certificate = Path(directory) / 'config.json', Path(directory) / 'relay.crt'
            certificate.write_text('-----BEGIN CERTIFICATE-----\nYWJj\n-----END CERTIFICATE-----\n')
            config = dict(access_token=TOKEN, port=45678, cert_file=str(certificate), key_file=str(Path(directory) / 'does-not-exist.key'))
            path.write_text(json.dumps(config))
            before = path.stat().st_mtime_ns, path.read_bytes(), certificate.stat().st_mtime_ns
            result = reader.read_configuration(path)
            self.assertEqual(dict(version=1, port=45678, accessToken=TOKEN,
                tlsCertificateSha256=hashlib.sha256(b'abc').hexdigest().upper()), result)
            self.assertEqual(before, (path.stat().st_mtime_ns, path.read_bytes(), certificate.stat().st_mtime_ns))

    def test_invalid_or_oversized_config_never_echoes_its_content(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            for value in ['null', '[]', '{"access_token":"do-not-echo","port":true}', 'private' * 10000]:
                path.write_text(value)
                self.assertEqual({'errorCode': 'invalid_configuration'}, reader.read_configuration(path))


if __name__ == '__main__':
    unittest.main()
