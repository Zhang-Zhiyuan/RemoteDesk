#!/usr/bin/env python3
"""Read existing relay enrollment data over authenticated SSH; never deploy or rotate it."""
import hashlib
import json
from pathlib import Path
import ssl

MAX_FILE_BYTES = 65536


def read_configuration(path=Path('/etc/remotedesk-relay/config.json')):
    try:
        if not path.is_file():
            return {'errorCode': 'not_configured'}
        with path.open('rb') as stream:
            encoded = stream.read(MAX_FILE_BYTES + 1)
        if len(encoded) > MAX_FILE_BYTES:
            raise ValueError()
        config = json.loads(encoded)
        token, port = config.get('access_token'), config.get('port')
        if not isinstance(token, str) or not 32 <= len(token) <= 4096 or type(port) is not int or not 1 <= port <= 65535:
            raise ValueError()
        certificate_path = Path(config['cert_file'])
        with certificate_path.open('rb') as stream:
            pem = stream.read(MAX_FILE_BYTES + 1)
        if len(pem) > MAX_FILE_BYTES:
            raise ValueError()
        certificate = ssl.PEM_cert_to_DER_cert(pem.decode('ascii'))
        return {'version': 1, 'port': port, 'accessToken': token,
                'tlsCertificateSha256': hashlib.sha256(certificate).hexdigest().upper()}
    except PermissionError:
        return {'errorCode': 'permission_denied'}
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return {'errorCode': 'invalid_configuration'}


if __name__ == '__main__':
    print(json.dumps(read_configuration(), separators=(',', ':')))
