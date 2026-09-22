from __future__ import annotations

import collections
import sys
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_app as app


class AndroidNavigationTests(unittest.TestCase):
    def viewer(self):
        return SimpleNamespace(input_condition=threading.Condition(), stop_event=threading.Event(),
            session=object(), sock=object(), remote_device_info={"platform": "Android"},
            remote_capabilities=app.CAPABILITY_INPUT_CONTROL, input_pressed_keys=set(),
            pending_inputs=collections.deque())

    def test_balanced_existing_keys(self):
        for key in (0x1B, 0x24, 0x7B):
            v = self.viewer()
            self.assertTrue(app.ViewerConnection.send_android_navigation(v, key))
            self.assertEqual([(kind, app.encode_input(kind, data=key))
                              for kind in (app.INPUT_KEY_DOWN, app.INPUT_KEY_UP)], list(v.pending_inputs))

    def test_rejects_other_platforms_keys_and_stale_connections(self):
        for platform in ("", "Windows", "Linux"):
            v = self.viewer(); v.remote_device_info["platform"] = platform
            self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x1B))
            self.assertFalse(v.pending_inputs)
        for key in (8, 9, 0x5B, 0x7A):
            self.assertFalse(app.ViewerConnection.send_android_navigation(self.viewer(), key))
        for unavailable in ("session", "sock"):
            v = self.viewer(); setattr(v, unavailable, None)
            self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x24))
        v = self.viewer(); v.stop_event.set()
        self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x24))
        v = self.viewer(); v.remote_capabilities = 0
        self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x24))

    def test_never_sends_half_a_pair_or_a_modified_global_key(self):
        for count in (app.INPUT_QUEUE_LIMIT - 1, app.INPUT_QUEUE_LIMIT):
            v = self.viewer(); v.pending_inputs.extend([(1, b"owned")] * count)
            before = list(v.pending_inputs)
            self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x7B))
            self.assertEqual(before, list(v.pending_inputs))
        v = self.viewer(); v.input_pressed_keys.add(0x11)
        self.assertFalse(app.ViewerConnection.send_android_navigation(v, 0x24))
        self.assertFalse(v.pending_inputs)


if __name__ == "__main__":
    unittest.main()
