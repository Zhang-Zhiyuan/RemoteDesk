"""Lossless local display handoff; no changes to the network image formats."""
import io
import os
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app


class DisplayHandoffTests(unittest.TestCase):
    def setUp(self):
        self.Image = app.find_pillow_image()
        if self.Image is None:
            self.skipTest("Pillow is required for pixel equivalence checks")
        self.source = self.Image.frombytes("RGB", (64, 40),
            bytes((index * 37) % 256 for index in range(64 * 40 * 3)))

    def encoded(self, image_format="JPEG"):
        output = io.BytesIO()
        self.source.save(output, format=image_format)
        return output.getvalue()

    def test_ppm_and_previous_png_handoff_have_identical_rgb_pixels(self):
        for image_format in ("JPEG", "PNG"):
            for viewport in ((64, 40), (31, 19), (256, 100)):
                with self.subTest(image_format=image_format, viewport=viewport):
                    encoded = self.encoded(image_format)
                    previous = app.convert_frame_with_pillow(encoded, *viewport)
                    current = app.convert_frame_for_tk(encoded, *viewport)
                    self.assertTrue(current.startswith(b"P6\n"))
                    with (self.Image.open(io.BytesIO(previous)) as old,
                          self.Image.open(io.BytesIO(current)) as new):
                        self.assertEqual(old.size, new.size)
                        self.assertEqual(old.tobytes(), new.tobytes())
                        self.assertLessEqual(len(current), new.width * new.height * 3 + 32)

    def test_viewer_path_does_not_compress_png(self):
        encoded = self.encoded()
        original_save = self.Image.Image.save
        formats = []

        def save(image, output, **options):
            formats.append(options["format"])
            return original_save(image, output, **options)

        with mock.patch.object(self.Image.Image, "save", save):
            self.assertIsNotNone(app.convert_frame_for_tk(encoded, 64, 40))
        self.assertEqual(["PPM"], formats)

    def test_rgb_frames_do_not_allocate_a_redundant_conversion_copy(self):
        encoded = self.encoded()
        with mock.patch.object(self.Image.Image, "convert",
                               side_effect=AssertionError("RGB copy is redundant")):
            self.assertIsNotNone(app.convert_frame_for_tk(encoded, 64, 40))
            self.assertIsNotNone(app.convert_frame_for_tk(encoded, 31, 19))

    def test_copy_elision_keeps_old_pipeline_pixels_at_different_resolutions(self):
        for image_format in ("JPEG", "PNG"):
            for source_size, viewport in (((1920, 1080), (1280, 720)),
                                          ((2560, 1440), (1920, 1080)),
                                          ((3840, 2160), (1366, 768)),
                                          ((734, 1600), (900, 600))):
                with self.subTest(format=image_format, source=source_size, viewport=viewport):
                    source = self.source.resize(source_size)
                    buffer = io.BytesIO()
                    source.save(buffer, format=image_format)
                    encoded = buffer.getvalue()
                    with self.Image.open(io.BytesIO(encoded)) as decoded:
                        old = decoded.convert("RGB").resize(
                            app.calculate_fitted_image_size(*source_size, *viewport),
                            self.Image.Resampling.LANCZOS)
                    current = app.convert_frame_for_tk(encoded, *viewport, *source_size)
                    self.assertIsNotNone(current)
                    with self.Image.open(io.BytesIO(current)) as new:
                        self.assertEqual(old.size, new.size)
                        self.assertEqual(old.tobytes(), new.tobytes())
                    old.close()
                    source.close()

    def test_non_rgb_images_still_convert_correctly(self):
        for mode, image_format in (("L", "JPEG"), ("CMYK", "JPEG"),
                                   ("RGBA", "PNG"), ("P", "PNG")):
            with self.subTest(mode=mode):
                buffer = io.BytesIO()
                self.source.convert(mode).save(buffer, format=image_format)
                encoded = buffer.getvalue()
                with self.Image.open(io.BytesIO(encoded)) as decoded:
                    expected = decoded.convert("RGB").tobytes()
                current = app.convert_frame_for_tk(encoded, 64, 40)
                self.assertIsNotNone(current)
                with self.Image.open(io.BytesIO(current)) as new:
                    self.assertEqual(expected, new.tobytes())

    def test_network_dimension_validation_still_precedes_conversion(self):
        with mock.patch.object(app, "convert_frame_with_pillow") as convert:
            self.assertIsNone(app.convert_frame_for_tk(self.encoded(), 64, 40, 32, 20))
            self.assertIsNone(app.convert_frame_for_tk(self.encoded(), 64, 40, 64, None))
            self.assertIsNone(app.convert_frame_for_tk(b"invalid", 64, 40))
            convert.assert_not_called()

    def test_ppm_is_local_only_and_not_accepted_as_network_image(self):
        ppm = app.convert_frame_for_tk(self.encoded(), 64, 40)
        with self.assertRaises(app.ProtocolError):
            app.inspect_encoded_image_dimensions(ppm)
        self.assertIsNone(app.convert_frame_for_tk(ppm, 64, 40))

    def test_png_compatibility_fallback_survives_missing_pillow(self):
        encoded = self.encoded("PNG")
        with mock.patch.object(app, "find_pillow_image", return_value=None):
            self.assertEqual(encoded, app.convert_frame_for_tk(encoded, 64, 40))

    def test_imagemagick_png_fallback_still_works(self):
        encoded, fallback = self.encoded(), self.encoded("PNG")
        with (mock.patch.object(app, "find_pillow_image", return_value=None),
              mock.patch.object(app, "find_image_converter", return_value=("convert",)),
              mock.patch.object(app.subprocess, "run", return_value=SimpleNamespace(
                  returncode=0, stdout=fallback))):
            self.assertEqual(fallback, app.convert_frame_for_tk(encoded, 64, 40))

    def test_converter_does_not_allow_a_lossy_handoff_format(self):
        self.assertIsNone(app.convert_frame_with_pillow(
            self.encoded(), 64, 40, output_format="JPEG"))

    def test_tk_receives_binary_data_with_explicit_format(self):
        master = object()
        for prefix, expected in ((b"P6\n", "ppm"), (b"\x89PNG", "png")):
            with mock.patch.object(app.tk, "PhotoImage") as photo:
                data = prefix + b"\x00\xff\x80\r\n"
                app.create_tk_frame_photo(data, master=master)
                photo.assert_called_once_with(master=master, data=data, format=expected)


@unittest.skipUnless(os.environ.get("REMOTEDESK_RUN_TK_TESTS") == "1",
                     "Set REMOTEDESK_RUN_TK_TESTS=1 on an owned display")
class RealTkDisplayHandoffTests(unittest.TestCase):
    setUp = DisplayHandoffTests.setUp
    encoded = DisplayHandoffTests.encoded

    def test_actual_tk_png_and_ppm_pixels_match(self):
        root = app.tk.Tk()
        root.withdraw()
        try:
            for viewport in ((64, 40), (31, 19)):
                encoded = self.encoded("PNG")
                png = app.convert_frame_with_pillow(encoded, *viewport)
                ppm = app.convert_frame_for_tk(encoded, *viewport)
                previous = app.create_tk_frame_photo(png, master=root)
                current = app.create_tk_frame_photo(ppm, master=root)
                self.assertEqual((previous.width(), previous.height()),
                                 (current.width(), current.height()))
                for y in range(previous.height()):
                    for x in range(previous.width()):
                        self.assertEqual(previous.get(x, y), current.get(x, y))
        finally:
            root.destroy()


if __name__ == "__main__":
    unittest.main()
