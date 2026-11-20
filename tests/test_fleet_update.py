import asyncio
import base64
import datetime
import hashlib
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

from cryptography import x509
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa, padding
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "scripts/linux"), str(ROOT / "scripts/relay")]
import remotedesk_linux_update as update
import remotedesk_linux_relay as client
import remotedesk_relay_server as server


@unittest.skipUnless(sys.platform.startswith("linux"), "native publisher and portable installation")
class FleetTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls):
        cls.key = rsa.generate_private_key(public_exponent=65537, key_size=4096)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Isolated update fixture")])
        now = datetime.datetime.now(datetime.timezone.utc)
        cls.cert = (x509.CertificateBuilder().issuer_name(name).subject_name(name).public_key(cls.key.public_key())
                    .serial_number(1).not_valid_before(now).not_valid_after(now + datetime.timedelta(days=1))
                    .sign(cls.key, hashes.SHA256()).public_bytes(serialization.Encoding.DER))

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.channel = self.root / "updates"

    def release(self, version):
        manifest, blob = {}, {}
        for platform in sorted(server.UPDATE_PLATFORMS):
            data = (platform + version).encode() * 2000
            sha = hashlib.sha256(data).hexdigest()
            fields = ["RemoteDesk/fleet/v1", version, "20261120000000", platform, str(len(data)), sha]
            signature = self.key.sign("\n".join(fields).encode(), padding.PKCS1v15(), hashes.SHA256())
            manifest[platform] = dict(version=version, platform=platform, build=fields[2], size=len(data),
                                      sha256=sha, signature=base64.b64encode(signature).decode())
            blob[platform] = data
        raw = json.dumps(dict(tickets=manifest)).encode()
        return manifest, blob, len(raw).to_bytes(4, "big") + raw + b"".join(blob.values())

    def test_publication_is_atomic_cleanup_is_scoped_and_tickets_are_bound(self):
        first, _, stream = self.release("1.0.55")
        server.publish_updates(self.channel, io.BytesIO(stream))
        preserved = self.channel / "operator-notes.txt"
        preserved.write_text("keep")
        unrelated_bin = self.channel / ("f" * 64 + ".bin")
        unrelated_bin.write_bytes(b"not a RemoteDesk release")
        original = (self.channel / "current.json").read_bytes()
        second, _, stream = self.release("1.0.56")
        for bad in (stream[:-1], stream[:-1] + bytes([stream[-1] ^ 1])):
            with self.assertRaises(ValueError):
                server.publish_updates(self.channel, io.BytesIO(bad))
            self.assertEqual(original, (self.channel / "current.json").read_bytes())
            self.assertTrue(all((self.channel / (t["sha256"] + ".bin")).is_file() for t in first.values()))
        server.publish_updates(self.channel, io.BytesIO(stream))
        server.publish_updates(self.channel, io.BytesIO(stream))
        self.assertEqual("keep", preserved.read_text())
        self.assertEqual({t["sha256"] + ".bin" for t in second.values()} | {unrelated_bin.name},
                         {p.name for p in self.channel.glob("*.bin")})
        ticket = second["linux-python"]
        update.verify(ticket, "linux-python", "1.0.55", self.cert)
        for field, value in [("version", "1.0.57"), ("build", "20261120000001"), ("sha256", "0" * 64), ("size", 2)]:
            with self.assertRaises(InvalidSignature):
                update.verify(dict(ticket, **{field: value}), "linux-python", "1.0.55", self.cert)
        for platform, version in [("win-x64", "1.0.55"), ("linux-python", "1.0.56"), ("linux-python", "2.0.0")]:
            with self.assertRaises(ValueError):
                update.verify(ticket, platform, version, self.cert)

    async def test_online_trigger_offline_catchup_and_bounded_download(self):
        tickets, files, data = self.release("1.0.55")
        relay = server.RelayServer(dict(access_token="a" * 64, cert_file=str(self.root / "cert"), key_file="unused"))
        listener = await asyncio.start_server(relay.handle_connection, "127.0.0.1", 0)
        port = listener.sockets[0].getsockname()[1]
        options = client.RelayOptions("127.0.0.1", port, "a" * 64, "B" * 64, "12345678-1234-4123-8123-123456789012")
        async def dial(_options):
            return await asyncio.open_connection("127.0.0.1", port)
        writers = []
        async def request(role, **fields):
            reader, writer = await dial(options)
            writers.append(writer)
            reply = await client.hello(reader, writer, options, role, **fields)
            return reader, writer, reply
        host = dict(updatePlatform="linux-python", appVersion="1.0.54", deviceId=options.device_id)
        try:
            reader, _, initial = await request("host-control", **host)
            self.assertNotIn("update", initial)
            server.publish_updates(self.channel, io.BytesIO(data))
            _, _, check = await request("update-check")
            self.assertEqual(1, check["notified"])
            self.assertEqual(tickets["linux-python"], (await client.read_json(reader))["update"])
            _, _, rejoined = await request("host-control", **host)
            self.assertEqual(tickets["linux-python"], rejoined["update"])
            _, _, up_to_date = await request("host-control", **dict(host, appVersion="1.0.55"))
            self.assertNotIn("update", up_to_date)
            with mock.patch.object(client, "connect_tls", dial):
                output = self.root / "download"
                await update.download(options, tickets["linux-python"], output)
                self.assertEqual(files["linux-python"], output.read_bytes())
                payload = self.channel / (tickets["linux-python"]["sha256"] + ".bin")
                payload.write_bytes(b"x" * payload.stat().st_size)
                with self.assertRaises(ValueError):
                    await update.download(options, tickets["linux-python"], self.root / "bad")
        finally:
            for writer in writers:
                writer.close()
            listener.close()
            await relay.shutdown()
            await listener.wait_closed()

    def test_portable_restart_health_failure_and_busy_preserve_old_app(self):
        def make(path, version, healthy=True):
            app = path / "app"
            app.mkdir(parents=True)
            (app / "fleet-version").write_text(version)
            (app / "fleet-publisher.cer").write_bytes(self.cert)
            launcher = path / "remotedesk-linux-app"
            launcher.write_text("#!/bin/sh\nexit " + ("0" if healthy else "1") + "\n")
            launcher.chmod(0o755)
            (app / "fleet-files.json").write_text(json.dumps(update.tree_inventory(path)))
        current = self.root / "installed"
        make(current, "1.0.54")
        for i, (healthy, idle) in enumerate([(False, True), (True, False), (True, True)]):
            folder = self.root / str(i)
            make(folder, "1.0.55", healthy)
            package = self.root / f"{i}.tgz"
            with tarfile.open(package, "w:gz") as archive:
                archive.add(folder, arcname=".")
            args = (package, {"platform": "linux-python", "version": "1.0.55"}, lambda: idle, current / "app")
            if not (healthy and idle):
                with self.assertRaises((update.subprocess.CalledProcessError, BlockingIOError)):
                    update.install_portable(*args)
                self.assertEqual("1.0.54", update.installed(current / "app")[1])
                continue
            extra = current / "my-notes"
            extra.write_text("keep")
            with self.assertRaises(ValueError):
                update.install_portable(*args)
            self.assertEqual("keep", extra.read_text())
            extra.unlink()
            update.install_portable(*args)
            self.assertEqual("1.0.55", update.installed(current / "app")[1])
            self.assertFalse(list(self.root.glob(".remotedesk-stage-*")))
