from __future__ import annotations

import queue
import sys
import unittest
from pathlib import Path


LINUX_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "linux"
sys.path.insert(0, str(LINUX_SCRIPTS))

import remotedesk_linux_app as app  # noqa: E402


class LinuxAdaptiveLayoutTests(unittest.TestCase):
    def test_preferred_window_is_centered_when_it_fits(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (0, 0, 1920, 1080),
            (1180, 760),
            (640, 480),
        )

        self.assertEqual((1180, 760), (geometry.width, geometry.height))
        self.assertEqual((370, 160), (geometry.x, geometry.y))
        self.assertEqual((640, 480), (geometry.minimum_width, geometry.minimum_height))

    def test_small_screen_clamps_size_minimum_and_margin(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (0, 0, 800, 600),
            (1180, 760),
            (920, 640),
        )

        self.assertEqual((752, 552), (geometry.width, geometry.height))
        self.assertEqual((752, 552), (geometry.minimum_width, geometry.minimum_height))
        self.assertEqual((24, 24), (geometry.x, geometry.y))

    def test_owner_centering_is_clamped_on_negative_origin_display(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (-1280, 0, 1280, 1024),
            (960, 560),
            (480, 300),
            anchor_bounds=(-1400, 800, 400, 300),
        )

        self.assertEqual(-1256, geometry.x)
        self.assertEqual(440, geometry.y)
        self.assertEqual("960x560+-1256+440", geometry.to_tk_geometry())

    def test_tiny_virtual_screen_still_produces_positive_bounds(self) -> None:
        geometry = app.calculate_adaptive_window_geometry(
            (10, 20, 1, 1),
            (1180, 760),
            (640, 480),
        )

        self.assertEqual((1, 1, 10, 20), (geometry.width, geometry.height, geometry.x, geometry.y))
        self.assertEqual((1, 1), (geometry.minimum_width, geometry.minimum_height))

    def test_viewer_conversion_target_tracks_window_and_caps_extremes(self) -> None:
        self.assertIsNone(app.normalize_viewer_display_size(1, 1))
        self.assertEqual((640, 360), app.normalize_viewer_display_size(640, 360))
        self.assertEqual((3840, 2160), app.normalize_viewer_display_size(8000, 5000))

        connection = app.ViewerConnection("127.0.0.1", 56565, "test", queue.Queue(), 1)
        connection.set_display_size(640, 360)
        connection.set_display_size(1, 1)
        self.assertEqual((640, 360), connection.display_size)
        self.assertFalse(connection.display_active)

    def test_tk_scale_is_normalized_against_96_dpi(self) -> None:
        self.assertAlmostEqual(1.0, app.normalize_tk_ui_scale(96.0 / 72.0))
        self.assertAlmostEqual(2.0, app.normalize_tk_ui_scale(192.0 / 72.0))
        self.assertEqual(1.0, app.normalize_tk_ui_scale(float("nan")))
        self.assertEqual(3.0, app.normalize_tk_ui_scale(10.0))

    def test_viewer_image_fits_and_can_expand_with_window(self) -> None:
        self.assertEqual((800, 450), app.calculate_fitted_image_size(1920, 1080, 800, 600))
        self.assertEqual((1920, 1080), app.calculate_fitted_image_size(960, 540, 1920, 1080))
        self.assertEqual((338, 600), app.calculate_fitted_image_size(1080, 1920, 800, 600))

    def test_xrandr_monitors_are_parsed_and_selected_by_anchor(self) -> None:
        monitors = app.parse_xrandr_monitor_bounds(
            "Monitors: 2\n"
            " 0: +*eDP-1 1920/309x1080/174+0+0 eDP-1\n"
            " 1: +HDMI-1 1280/300x1024/220+-1280+0 HDMI-1\n"
        )

        self.assertEqual([(0, 0, 1920, 1080), (-1280, 0, 1280, 1024)], monitors)
        self.assertEqual((-1280, 0, 1280, 1024), app.choose_monitor_bounds(monitors, -400, 500))
        self.assertEqual((0, 0, 1920, 1080), app.choose_monitor_bounds(monitors, 2500, 500))


if __name__ == "__main__":
    unittest.main()
