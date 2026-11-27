"""Regressions for SSH public-key enrollment in a single Linux account."""
import base64
import concurrent.futures
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))


@unittest.skipUnless(sys.platform.startswith("linux"), "Linux account key files")
class AuthorizedKeyTests(unittest.TestCase):
    @staticmethod
    def key(seed):
        kind = b"ssh-ed25519"
        return kind + b" " + base64.b64encode(len(kind).to_bytes(4, "big") + kind + b"\0\0\0 " + bytes([seed]) * 32)

    def test_concurrent_additions_are_idempotent_and_preserve_existing_bytes(self):
        from remotedesk_authorize_key import authorize
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / ".ssh"
            root.mkdir(mode=0o700)
            path = root / "authorized_keys"
            before = b'# keep this comment\nfrom="10.0.0.0/8",restrict ' + self.key(0) + b" existing"
            path.write_bytes(before)
            authorize(folder, self.key(0))
            self.assertEqual(before, path.read_bytes())
            with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
                list(pool.map(lambda seed: authorize(folder, self.key(seed)), [1, 2, 1, 2, 3, 3, 2]))
            self.assertTrue(path.read_bytes().startswith(before + b"\n"))
            self.assertCountEqual(path.read_bytes()[len(before)+1:].splitlines(),
                                  [self.key(seed) + b" RemoteDesk" for seed in (1, 2, 3)])

    def test_refuses_links_and_invalid_key_before_creating_files(self):
        from remotedesk_authorize_key import authorize
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / ".ssh"
            with self.assertRaises(ValueError):
                authorize(folder, b"ssh-rsa " + self.key(1).split()[1])
            self.assertFalse(root.exists())
            root.mkdir(mode=0o700)
            target = Path(folder) / "other"
            target.write_bytes(b"untouched")
            for link in (os.symlink, os.link):
                link(target, root / "authorized_keys")
                with self.assertRaises(OSError):
                    authorize(folder, self.key(1))
                (root / "authorized_keys").unlink()
            self.assertEqual(b"untouched", target.read_bytes())
