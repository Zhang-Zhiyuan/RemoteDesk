from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "experiments"))
from android_viewer_ui_verify import StableLayout


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
