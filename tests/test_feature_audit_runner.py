import io
import json
from pathlib import Path
import sys
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "experiments"))
from run_feature_audit import read_relay_config


class FeatureAuditRelayConfigTests(unittest.TestCase):
    def config(self, **changes):
        return dict(serverAddress="relay.example.test", port=56567,
                    accessToken="a" * 64, tlsCertificateSha256="b" * 64, **changes)

    def test_fresh_test_identity_and_validated_pin(self):
        source = self.config(deviceId=str(uuid.uuid4()), publish=False)
        first = read_relay_config(io.StringIO(json.dumps(source)))
        second = read_relay_config(io.StringIO(json.dumps(source)))
        self.assertEqual(source["accessToken"], first["accessToken"])
        self.assertEqual("B" * 64, first["tlsCertificateSha256"])
        self.assertTrue(first["publish"])
        self.assertNotEqual(source["deviceId"], first["deviceId"])
        self.assertNotEqual(first["deviceId"], second["deviceId"])
        self.assertEqual(first["deviceId"], str(uuid.UUID(first["deviceId"])))

    def test_invalid_and_oversized_json_is_rejected_without_echo(self):
        for raw in ("", "[1]", "null", "private-secret-invalid-json", " " * 16385):
            with self.subTest(length=len(raw)):
                with self.assertRaisesRegex(ValueError, "^Invalid relay configuration on stdin; contents omitted$"):
                    read_relay_config(io.StringIO(raw))

    def test_invalid_endpoint_pin_and_token_are_rejected(self):
        for key, value in (("serverAddress", "https://relay.example.test"),
                           ("port", 0), ("port", True), ("port", 65536),
                           ("tlsCertificateSha256", ""), ("accessToken", "short")):
            source = self.config()
            source[key] = value
            with self.subTest(field=key):
                with self.assertRaisesRegex(ValueError, "contents omitted$"):
                    read_relay_config(io.StringIO(json.dumps(source)))

    def test_only_one_line_consumed_before_any_child_starts(self):
        reader = io.StringIO(json.dumps(self.config()) + "\nsecond line\n")
        read_relay_config(reader)
        self.assertEqual("second line\n", reader.read())


if __name__ == "__main__":
    unittest.main()
