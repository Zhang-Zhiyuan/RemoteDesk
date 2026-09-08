import sys
from pathlib import Path
import unittest
from unittest import mock
from collections import deque
import threading
import io

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts/linux"))
import remotedesk_linux_app as app


class JetsonDecoderTests(unittest.TestCase):
    def test_explicit_jetson_decoder_precedes_desktop_cuda(self):
        backends = app.select_ffmpeg_h264_decoder_backends({"cuda"}, {"h264", "h264_cuvid", "h264_nvv4l2dec"})
        self.assertEqual("jetson-nvv4l2", backends[0].key)
        self.assertEqual("h264_nvv4l2dec", backends[0].decoder_name)
        self.assertTrue(backends[0].hardware)
        self.assertEqual("software", backends[-1].key)

    def test_regular_desktop_cuda_priority_is_unchanged(self):
        backends = app.select_ffmpeg_h264_decoder_backends({"cuda"}, {"h264", "h264_cuvid"})
        self.assertEqual("cuda-nvdec", backends[0].key)
        self.assertNotIn("jetson-nvv4l2", [item.key for item in backends])

    def test_jetson_does_not_require_desktop_cuda_hwaccel(self):
        backends = app.select_ffmpeg_h264_decoder_backends(set(), {"H264_NVV4L2DEC", "h264"})
        self.assertEqual("jetson-nvv4l2", backends[0].key)
        self.assertIsNone(backends[0].hwaccel)
        self.assertIsNone(backends[0].hwaccel_output_format)

    def test_jetson_command_uses_cpu_visible_frames_without_cuda_download(self):
        backend = app.select_ffmpeg_h264_decoder_backends(set(), {"h264_nvv4l2dec"})[0]
        command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)
        self.assertIn("h264_nvv4l2dec", command)
        self.assertNotIn("-hwaccel", command)
        self.assertNotIn("hwdownload", " ".join(command))
        self.assertEqual("discardcorrupt", command[command.index("-fflags") + 1])
        self.assertEqual("settb=expr=1/60,setpts=N", command[command.index("-vf") + 1])
        self.assertEqual("1:60", command[command.index("-enc_time_base") + 1])
        self.assertEqual("0", command[command.index("-vsync") + 1])

    def test_desktop_decoder_timestamp_options_remain_unchanged(self):
        for backend in app.select_ffmpeg_h264_decoder_backends({"cuda"}, {"h264", "h264_cuvid"}):
            command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)
            self.assertNotIn("-enc_time_base", command)
            self.assertNotIn("setpts=N", " ".join(command))

    def test_no_decoder_is_invented_when_ffmpeg_does_not_list_one(self):
        self.assertEqual((), app.select_ffmpeg_h264_decoder_backends(set(), set()))

    def test_jetson_fixed_three_frame_pipeline_is_not_misclassified_as_stale(self):
        backend = app.select_ffmpeg_h264_decoder_backends(set(), {"h264_nvv4l2dec"})[0]
        lag = app.h264_correlated_submission_lag(backend)
        self.assertEqual(3, lag)
        self.assertTrue(app.is_h264_submission_fresh(1, 4, lag))
        self.assertFalse(app.is_h264_submission_fresh(1, 5, lag))
        self.assertLess(lag, app.MAX_H264_DECODER_OUTSTANDING_CORRELATIONS)

    def test_other_backends_keep_two_frame_limit(self):
        for backend in app.select_ffmpeg_h264_decoder_backends({"cuda"}, {"h264", "h264_cuvid"}):
            self.assertEqual(2, app.h264_correlated_submission_lag(backend))
            self.assertFalse(app.is_h264_submission_fresh(1, 4, app.h264_correlated_submission_lag(backend)))

    def test_ready_output_frees_capacity_before_new_submission_without_expanding_queue(self):
        decoder = app.H264AnnexBDecoder.__new__(app.H264AnnexBDecoder)
        decoder.stop_event = threading.Event()
        decoder.write_lock = threading.Lock()
        decoder.frame_condition = threading.Condition()
        decoder.submitted_correlations = deque(["second", "third", "fourth"])
        ready = app.CorrelatedH264Jpeg("first", b"\xff\xd8first\xff\xd9")
        decoder.pending_jpegs = deque([ready])
        decoder.correlation_overflowed = False
        decoder.process = mock.Mock()
        decoder.process.poll.return_value = None
        actual = decoder.decode_correlated(b"\x00\x00\x00\x01\x65", "fifth", 0)
        self.assertIs(ready, actual)
        self.assertEqual(["second", "third", "fourth", "fifth"], list(decoder.submitted_correlations))
        self.assertFalse(decoder.correlation_overflowed)
        self.assertEqual(4, decoder.outstanding_correlation_count)
        decoder.process.stdin.write.assert_called_once()

    def test_local_tk_png_uses_fast_lossless_packing(self):
        Image = app.find_pillow_image()
        if Image is None:
            self.skipTest("Pillow is not installed in this test environment")
        source = Image.new("RGB", (64, 40), (21, 32, 48))
        source.putpixel((10, 12), (220, 15, 99))
        encoded = io.BytesIO()
        source.save(encoded, format="JPEG", quality=90)
        with Image.open(io.BytesIO(encoded.getvalue())) as decoded:
            expected = decoded.convert("RGB").tobytes()
        original = Image.Image.save
        calls = []
        def save(image, output, **kwargs):
            calls.append(kwargs)
            return original(image, output, **kwargs)
        with mock.patch.object(Image.Image, "save", save):
            png = app.convert_frame_with_pillow(encoded.getvalue(), 64, 40)
        self.assertEqual(1, calls[0]["compress_level"])
        with Image.open(io.BytesIO(png)) as actual:
            self.assertEqual(expected, actual.convert("RGB").tobytes())


if __name__ == "__main__":
    unittest.main()
