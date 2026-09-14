from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "experiments"))
from android_viewer_ui_verify import PACKAGE, StableLayout, toolbar_swipe


class ToolbarSwipeTests(unittest.TestCase):
    def strip(self, **changes):
        return {"package": PACKAGE, "class": "android.widget.HorizontalScrollView",
                "content-desc": "远程操作栏，左右滑动可查看所有按钮", "bounds": "[20,800][420,860]", **changes}

    def test_reveal_far_controls_only_within_owned_strip(self):
        for label in ("更多", "新版放大"):
            self.assertEqual((380, 830, 60, 830), toolbar_swipe([self.strip()], label))
        for label in ("键盘", "鼠标", "屏幕", "缩放", "触控板", "直接触摸"):
            self.assertEqual((60, 830, 380, 830), toolbar_swipe([self.strip()], label))

    def test_unrelated_ambiguous_or_invalid_views_are_not_swiped(self):
        for nodes in ([], [self.strip(), self.strip()], [self.strip(package="another.application")],
                      [self.strip(**{"content-desc": "remote desktop"})],
                      [self.strip(bounds="[20,800][20,860]")], [self.strip(bounds="[20,860][420,800]")],
                      [self.strip(bounds="[-20,800][420,860]")], [self.strip(bounds="invalid")]):
            with self.subTest(nodes=nodes):
                self.assertIsNone(toolbar_swipe(nodes, "更多"))
        self.assertIsNone(toolbar_swipe([self.strip()], "删除"))


class StableLayoutTests(unittest.TestCase):
    def state(self):
        return dict(frame=[1920,1080], viewport=[0,24,640,66], dock=[0,66,640,124],
                    keyboardPanel=[8,71,632,119], keyboard=True, imeInset=196,
                    scale=1, ownerGeneration=1)

    def test_animation_must_settle_before_geometry_is_asserted(self):
        stable = StableLayout(.6)
        value = self.state()
        value["viewport"] = [0,24,640,48]
        self.assertFalse(stable.ready(value, 0))
        self.assertFalse(stable.ready(value, .3))
        self.assertFalse(stable.ready(self.state(), .6))
        self.assertFalse(stable.ready(self.state(), .9))
        self.assertTrue(stable.ready(self.state(), 1.3))

    def test_bad_but_stable_geometry_is_not_filtered_into_a_fake_pass(self):
        stable = StableLayout(.6)
        value = self.state()
        value["scale"] = .31
        self.assertFalse(stable.ready(value, 0))
        self.assertTrue(stable.ready(value, .7))
        # The caller still sees the bad scale and must fail its 1:1 assertion.
        self.assertEqual(.31, value["scale"])

    def test_new_connection_resets_the_stability_window(self):
        stable = StableLayout(.6)
        value = self.state()
        self.assertFalse(stable.ready(value, 0))
        value["ownerGeneration"] = 2
        self.assertFalse(stable.ready(value, 1))
        self.assertTrue(stable.ready(value, 1.7))

    def test_zero_delay_preserves_existing_wait_behavior(self):
        self.assertTrue(StableLayout(0).ready(self.state(), 0))


if __name__ == "__main__":
    unittest.main()
