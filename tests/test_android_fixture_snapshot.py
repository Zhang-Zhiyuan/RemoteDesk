"""Windows evidence-file sharing must not look like a product disconnect."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "android_viewer_fixture_server", Path(__file__).resolve().parents[1]
    / "experiments/android_viewer_fixture_server.py")
fixture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fixture)


class SnapshotTests(unittest.TestCase):
    def test_complete_unicode_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            fixture.save_snapshot(path, {"text": "中文😀"})
            self.assertEqual(json.loads(path.read_text(encoding="utf-8")), {"text": "中文😀"})
            self.assertFalse(path.with_suffix(".tmp").exists())

    def test_transient_windows_reader_is_retried_without_truncation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            fixture.save_snapshot(path, {"old": True})
            original = Path.replace
            calls = []

            def temporarily_busy(temporary, destination):
                calls.append(1)
                if len(calls) <= 2:
                    self.assertEqual(json.loads(path.read_text()), {"old": True})
                    raise PermissionError("sharing violation")
                return original(temporary, destination)

            with patch.object(Path, "replace", temporarily_busy):
                fixture.save_snapshot(path, {"new": True})
            self.assertEqual(len(calls), 3)
            self.assertEqual(json.loads(path.read_text()), {"new": True})

    def test_persistent_denial_is_bounded_and_preserves_previous_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "state.json"
            fixture.save_snapshot(path, {"old": True})
            with patch.object(Path, "replace", side_effect=PermissionError("denied")) as replace:
                with self.assertRaises(PermissionError):
                    fixture.save_snapshot(path, {"new": True}, retry_seconds=0)
            self.assertEqual(replace.call_count, 1)
            self.assertEqual(json.loads(path.read_text()), {"old": True})

    def test_unrelated_io_error_is_not_hidden(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(Path, "replace", side_effect=OSError("disk failure")) as replace:
                with self.assertRaises(OSError):
                    fixture.save_snapshot(Path(directory) / "state.json", {})
            self.assertEqual(replace.call_count, 1)


if __name__ == "__main__":
    unittest.main()
