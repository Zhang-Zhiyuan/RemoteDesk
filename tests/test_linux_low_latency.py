from __future__ import annotations

import json
import sys
import queue
import struct
import threading
import time
import unittest
from collections import deque
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


LINUX_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "linux"
sys.path.insert(0, str(LINUX_SCRIPTS))

import remotedesk_linux_app as app  # noqa: E402
import remotedesk_linux_host as host  # noqa: E402
import remotedesk_protocol_probe as protocol  # noqa: E402


class LinuxLowLatencyPolicyTests(unittest.TestCase):
    def test_frame_parsers_reject_dimensions_above_pixel_budget(self) -> None:
        legacy = struct.pack("<iidd", 8192, 8192, 0.0, 0.0) + b"x"
        video = struct.pack(
            "<iiiidd",
            protocol.FRAME_ENCODING_JPEG,
            8192,
            8192,
            protocol.FRAME_FLAG_KEY_FRAME,
            0.0,
            0.0,
        ) + b"x"

        with self.assertRaisesRegex(protocol.ProtocolError, "pixel budget"):
            protocol.decode_frame(legacy, protocol.MESSAGE_FRAME)
        with self.assertRaisesRegex(protocol.ProtocolError, "pixel budget"):
            protocol.decode_frame(video, protocol.MESSAGE_VIDEO_FRAME)
        with self.assertRaisesRegex(protocol.ProtocolError, "pixel budget"):
            app.parse_legacy_frame(legacy)

    def test_image_conversion_rejects_encoded_dimension_mismatch_before_decode(self) -> None:
        png_header = (
            b"\x89PNG\r\n\x1a\n"
            + struct.pack(">I", 13)
            + b"IHDR"
            + struct.pack(">II", 1, 1)
        )

        self.assertIsNone(
            app.convert_frame_for_tk(
                png_header,
                640,
                360,
                expected_width=2,
                expected_height=1,
            )
        )

    @staticmethod
    def _host_liveness_session(
        timeout_seconds: float,
        clock=time.monotonic,
    ) -> tuple[host.LinuxHostSession, object]:
        class InterruptibleSocket:
            def __init__(self) -> None:
                self.interrupted = threading.Event()
                self.shutdown_calls = 0
                self.close_calls = 0
                self.socket_options: list[tuple[int, int, int]] = []

            def setsockopt(self, level: int, option: int, value: int) -> None:
                # Linux exposes TCP_QUICKACK while Windows commonly does not.
                # Model the socket method so this liveness fake exercises the
                # same post-read path on both platforms.
                self.socket_options.append((level, option, value))

            def shutdown(self, _how: int) -> None:
                self.shutdown_calls += 1
                self.interrupted.set()

            def close(self) -> None:
                self.close_calls += 1
                self.interrupted.set()

        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.sock = InterruptibleSocket()
        session.session = object()
        session.stop_event = threading.Event()
        session.session_stop = threading.Event()
        session.inbound_liveness = host.HostInboundLivenessTracker(
            timeout_seconds,
            clock=clock,
        )
        session._abort_incoming_transfer = mock.Mock()
        session._safe_status = mock.Mock()
        return session, session.sock

    def test_linux_host_blackholed_viewer_is_shutdown_at_inbound_deadline(
        self,
    ) -> None:
        session, fake_socket = self._host_liveness_session(0.05)

        def blocked_read(_sock: object, _session: object) -> tuple[int, bytes]:
            self.assertTrue(fake_socket.interrupted.wait(timeout=1.0))
            raise OSError("socket interrupted by liveness watchdog")

        reader = threading.Thread(target=session._read_loop)
        watchdog = threading.Thread(target=session._inbound_liveness_loop)
        with mock.patch.object(host, "read_message", side_effect=blocked_read):
            reader.start()
            self.assertTrue(
                session.inbound_liveness.is_waiting_for_message()
                or self._wait_until(
                    session.inbound_liveness.is_waiting_for_message,
                    timeout=0.5,
                )
            )
            watchdog.start()
            self.assertTrue(session.session_stop.wait(timeout=1.0))
            reader.join(timeout=1.0)
            watchdog.join(timeout=1.0)

        self.assertFalse(reader.is_alive())
        self.assertFalse(watchdog.is_alive())
        self.assertGreaterEqual(fake_socket.shutdown_calls, 1)
        self.assertGreaterEqual(fake_socket.close_calls, 1)

    def test_linux_host_five_second_pings_keep_inbound_deadline_fresh(
        self,
    ) -> None:
        now = [0.0]
        session, fake_socket = self._host_liveness_session(
            30.0,
            clock=lambda: now[0],
        )
        pongs: list[tuple[int, bytes]] = []
        session._write_message = lambda kind, payload: pongs.append((kind, payload))
        # This test concerns inbound liveness, not background reply scheduling.
        session.heartbeat = SimpleNamespace(
            request=lambda: session._write_message(host.MESSAGE_PONG, b""))
        ping_count = 12

        def next_ping(_sock: object, _session: object) -> tuple[int, bytes]:
            now[0] += 5.0
            if len(pongs) + 1 >= ping_count:
                session.stop_event.set()
            return host.MESSAGE_PING, b""

        with mock.patch.object(host, "read_message", side_effect=next_ping):
            session._read_loop()

        self.assertEqual(
            [(host.MESSAGE_PONG, b"")] * ping_count,
            pongs,
        )
        self.assertEqual(0, fake_socket.shutdown_calls)
        self.assertEqual(0, fake_socket.close_calls)
        self.assertFalse(session.inbound_liveness.expire_if_timed_out(now[0] + 60.0))

    def test_linux_host_does_not_count_long_control_processing_as_silence(
        self,
    ) -> None:
        now = [0.0]
        session, fake_socket = self._host_liveness_session(
            30.0,
            clock=lambda: now[0],
        )
        handled = threading.Event()

        def handle_large_file_control(_kind: int, _payload: bytes) -> None:
            # Model a checksum/archive/disk operation taking much longer than
            # the network deadline after its complete authenticated message
            # has already arrived.
            now[0] = 90.0
            self.assertFalse(session.inbound_liveness.is_waiting_for_message())
            self.assertFalse(session.inbound_liveness.expire_if_timed_out())
            handled.set()
            session.stop_event.set()

        session._handle_message = handle_large_file_control
        with mock.patch.object(
            host,
            "read_message",
            return_value=(host.MESSAGE_CONTROL, b"large-file-progress"),
        ):
            session._read_loop()

        self.assertTrue(handled.is_set())
        self.assertEqual(0, fake_socket.shutdown_calls)
        self.assertEqual(0, fake_socket.close_calls)

    @staticmethod
    def _wait_until(predicate: object, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if predicate():
                return True
            time.sleep(0.001)
        return bool(predicate())

    def test_probe_names_windows_udp_extensions_without_advertising_them(self) -> None:
        windows_udp_extensions = (
            protocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO
            | protocol.CAPABILITY_UDP_VIDEO_CONGESTION_FEEDBACK
            | protocol.CAPABILITY_LOW_LATENCY_UDP_VIDEO_XOR_FEC
            | protocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT
            | protocol.CAPABILITY_LOW_LATENCY_UDP_MOUSE_INPUT_APPLIED_ACK
        )

        self.assertEqual(
            [
                "LowLatencyUdpVideo",
                "UdpVideoCongestionFeedback",
                "LowLatencyUdpVideoXorFec",
                "LowLatencyUdpMouseInput",
                "LowLatencyUdpMouseInputAppliedAck",
            ],
            protocol.capability_names(windows_udp_extensions),
        )
        self.assertEqual(0, host.BASE_HOST_CAPABILITIES & windows_udp_extensions)
        self.assertEqual(
            ["ShortGopH264"],
            protocol.capability_names(
                protocol.CAPABILITY_SHORT_GOP_H264),
        )
        self.assertNotEqual(
            0,
            host.BASE_HOST_CAPABILITIES &
            protocol.CAPABILITY_SHORT_GOP_H264,
        )

    def test_high_frame_rate_h264_is_explicitly_negotiated(self) -> None:
        capability = protocol.CAPABILITY_HIGH_FRAME_RATE_H264

        self.assertEqual(["HighFrameRateH264"], protocol.capability_names(capability))
        self.assertNotEqual(0, host.BASE_HOST_CAPABILITIES & capability)
        self.assertEqual(30.0, host.negotiated_h264_fps(60.0, 0))
        self.assertEqual(60.0, host.negotiated_h264_fps(60.0, capability))
        self.assertEqual(24.0, host.negotiated_h264_fps(24.0, 0))

        compatibility_capabilities = app.linux_viewer_capabilities(False)
        native_capabilities = app.linux_viewer_capabilities(True)
        self.assertEqual(0, compatibility_capabilities & capability)
        self.assertNotEqual(0, native_capabilities & capability)

    def test_linux_host_and_viewer_advertise_high_quality_jpeg(self) -> None:
        capability = protocol.CAPABILITY_HIGH_QUALITY_JPEG

        self.assertEqual(
            ["HighQualityJpeg"],
            protocol.capability_names(capability),
        )
        self.assertNotEqual(0, host.BASE_HOST_CAPABILITIES & capability)
        self.assertNotEqual(0, protocol.BASE_VIEWER_CAPABILITIES & capability)
        self.assertNotEqual(
            0,
            app.linux_viewer_capabilities(False) & capability,
        )

    def test_linux_gui_exposes_real_1440p_and_4k_60fps_modes(self) -> None:
        self.assertIn("60.0", app.HOST_FPS_OPTIONS)
        self.assertIn("2560x1440", app.HOST_SIZE_OPTIONS)
        self.assertIn("3840x2160", app.HOST_SIZE_OPTIONS)
        self.assertEqual("1920x1080", app.DEFAULT_HOST_SIZE)
        self.assertEqual("60.0", app.DEFAULT_HOST_FPS)
        self.assertEqual(60.0, host.DEFAULT_HOST_FPS)
        self.assertEqual((1920, 1080), app.parse_size(""))

    def test_60fps_is_kept_for_hardware_and_safely_reduced_for_jpeg(self) -> None:
        h264 = host.ContinuousHardwareH264Capture(3840, 2160, 60.0, "placeholder")
        uhd_jpeg = host.ContinuousFrameCapture(3840, 2160, 60.0, "placeholder")
        qhd_jpeg = host.ContinuousFrameCapture(2560, 1440, 60.0, "placeholder")
        hd_jpeg = host.ContinuousFrameCapture(1920, 1080, 60.0, "placeholder")

        self.assertEqual(60.0, h264.fps)
        self.assertEqual(12.0, uhd_jpeg.fps)
        self.assertEqual(20.0, qhd_jpeg.fps)
        self.assertEqual(30.0, hd_jpeg.fps)
        self.assertEqual(60.0, host.normalize_host_fps(144.0))
        self.assertEqual(host.DEFAULT_HOST_FPS, host.normalize_host_fps(float("nan")))

    def test_frame_loop_tracks_the_active_negotiated_capture_rate(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.h264_stream_active = False
        session.continuous_capture = SimpleNamespace(fps=12.0)
        session.hardware_h264_capture = SimpleNamespace(fps=60.0)

        self.assertAlmostEqual(1.0 / 12.0, session._frame_interval_seconds())
        session.h264_stream_active = True
        self.assertAlmostEqual(1.0 / 60.0, session._frame_interval_seconds())

    def test_host_applies_runtime_high_frame_rate_capability_update(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.viewer_capabilities = 0
        session.requested_fps = 60.0
        session.hardware_h264_capture = mock.Mock()

        session._handle_message(
            host.MESSAGE_CONTROL,
            protocol.encode_viewer_capabilities(
                protocol.CAPABILITY_HIGH_FRAME_RATE_H264
            ),
        )

        self.assertEqual(
            protocol.CAPABILITY_HIGH_FRAME_RATE_H264,
            session.viewer_capabilities,
        )
        session.hardware_h264_capture.update_fps.assert_called_once_with(60.0)

    def test_x11_display_candidates_prioritize_current_and_deduplicate(self) -> None:
        with mock.patch.object(
            host,
            "DISPLAY_AUTO_CANDIDATES",
            (":0", ":1", ":99"),
        ):
            self.assertEqual(
                (":1", ":0", ":99"),
                host.x11_display_candidates(" :1 "),
            )

    def test_explicit_display_remains_authoritative_without_probing(self) -> None:
        host.clear_input_capability_cache()
        with (
            mock.patch.dict(host.os.environ, {}, clear=True),
            mock.patch.object(host, "detect_usable_x11_display") as detect,
        ):
            state, selected = host.configure_display_environment(" :7 ")
            self.assertEqual("forced", state)
            self.assertEqual(":7", selected)
            self.assertEqual(":7", host.os.environ["DISPLAY"])
            detect.assert_not_called()

    def test_auto_display_prefers_visible_content_over_near_black_current(self) -> None:
        probes = {
            ":1": host.X11DisplayProbe(
                ":1",
                True,
                "near-black",
                "static dark desktop",
            ),
            ":0": host.X11DisplayProbe(
                ":0",
                True,
                "content",
                "visible desktop",
            ),
            ":99": host.X11DisplayProbe(
                ":99",
                False,
                "unusable",
                "closed",
            ),
        }
        with (
            mock.patch.object(host.shutil, "which", return_value="/usr/bin/xdpyinfo"),
            mock.patch.object(host, "probe_x11_display", side_effect=probes.__getitem__),
        ):
            self.assertEqual(":0", host.detect_usable_x11_display(":1"))

    def test_auto_display_keeps_near_black_candidate_when_no_better_choice(self) -> None:
        probes = {
            ":1": host.X11DisplayProbe(
                ":1",
                True,
                "near-black",
                "valid dark wallpaper",
            ),
            ":0": host.X11DisplayProbe(
                ":0",
                False,
                "unusable",
                "closed",
            ),
            ":99": host.X11DisplayProbe(
                ":99",
                False,
                "unusable",
                "closed",
            ),
        }
        with (
            mock.patch.object(host.shutil, "which", return_value="/usr/bin/xdpyinfo"),
            mock.patch.object(host, "probe_x11_display", side_effect=probes.__getitem__),
        ):
            self.assertEqual(":1", host.detect_usable_x11_display(":1"))

    def test_display_probe_timeout_is_bounded_and_reported(self) -> None:
        def run(_command: list[str], **kwargs: object) -> object:
            self.assertEqual(host.DISPLAY_PROBE_TIMEOUT_SECONDS, kwargs["timeout"])
            raise host.subprocess.TimeoutExpired("xdpyinfo", kwargs["timeout"])

        with (
            mock.patch.object(host.shutil, "which", return_value="/usr/bin/xdpyinfo"),
            mock.patch.object(host.subprocess, "run", side_effect=run),
        ):
            probe = host.probe_x11_display(":1")

        self.assertFalse(probe.usable)
        self.assertEqual("unusable", probe.content_state)
        self.assertIn("timed out", probe.detail)

    def test_root_content_probe_classifies_dark_and_visible_samples(self) -> None:
        width, height = host.DISPLAY_CONTENT_PROBE_SIZE
        dark = bytes(width * height)
        visible = bytes([0, 32]) * ((width * height) // 2)

        def classify(sample: bytes) -> tuple[str, str]:
            responses = iter(
                (
                    SimpleNamespace(returncode=0, stdout=b"png"),
                    SimpleNamespace(returncode=0, stdout=sample),
                )
            )
            with (
                mock.patch.object(host.shutil, "which", return_value="/usr/bin/tool"),
                mock.patch.object(host.subprocess, "run", side_effect=lambda *_args, **_kwargs: next(responses)),
            ):
                return host.probe_x11_root_content(":1")

        self.assertEqual("near-black", classify(dark)[0])
        self.assertEqual("content", classify(visible)[0])

    def test_root_content_probe_reports_missing_tools_without_rejecting_display(self) -> None:
        with mock.patch.object(host.shutil, "which", return_value=None):
            state, detail = host.probe_x11_root_content(":1")

        self.assertEqual("unknown", state)
        self.assertIn("missing", detail)

    def test_display_size_refresh_keeps_last_valid_size_on_probe_failure(self) -> None:
        current_size = (2560, 1440)

        self.assertEqual(
            current_size,
            host.select_refreshed_display_size(current_size, None),
        )
        self.assertEqual(
            current_size,
            host.select_refreshed_display_size(current_size, (0, 1080)),
        )
        self.assertEqual(
            (1920, 1080),
            host.select_refreshed_display_size(current_size, (1920, 1080)),
        )

    def test_display_size_refresh_worker_updates_cache_and_stops_promptly(self) -> None:
        update_complete = threading.Event()
        probe_threads: list[threading.Thread] = []

        class FakeContinuousCapture:
            def __init__(self) -> None:
                self.source_sizes: list[tuple[int, int]] = []

            def update_source_size(self, width: int, height: int) -> None:
                self.source_sizes.append((width, height))
                update_complete.set()

        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.geometry_lock = threading.Lock()
        session.display_size = (1280, 720)
        session.display_refresh_stop = threading.Event()
        session.display_refresh_thread = None
        session.continuous_capture = FakeContinuousCapture()
        session.hardware_h264_capture = FakeContinuousCapture()

        def probe_from_worker() -> tuple[int, int]:
            probe_threads.append(threading.current_thread())
            return 1920, 1080

        worker: threading.Thread | None = None
        try:
            with mock.patch.object(host, "probe_display_size", side_effect=probe_from_worker):
                session._start_display_size_refresh_thread()
                worker = session.display_refresh_thread
                self.assertTrue(update_complete.wait(timeout=1.0))
                self.assertEqual((1920, 1080), session.display_size)
                self.assertEqual([(1920, 1080)], session.continuous_capture.source_sizes)
                self.assertEqual([(1920, 1080)], session.hardware_h264_capture.source_sizes)
        finally:
            session._stop_display_size_refresh_thread()

        self.assertIsNotNone(worker)
        self.assertFalse(worker.is_alive())
        self.assertIsNone(session.display_refresh_thread)
        self.assertTrue(session.display_refresh_stop.is_set())
        self.assertEqual([worker], probe_threads)

    def test_display_size_change_restarts_active_capture_without_disabling_it(self) -> None:
        capture = host.ContinuousFrameCapture(1280, 720, 30.0, "placeholder")
        process = mock.Mock()
        process.poll.return_value = None
        reader_thread = mock.Mock()
        reader_thread.is_alive.return_value = True
        capture.source_size_ready = True
        capture.process = process
        capture.reader_thread = reader_thread
        capture.latest_frame = (1280, 720, b"old frame")
        capture.latest_at = time.monotonic()
        capture.disabled = True

        capture.update_source_size(1920, 1080)

        process.terminate.assert_called_once_with()
        process.wait.assert_called_once_with(timeout=1)
        reader_thread.join.assert_called_once_with(timeout=1)
        self.assertEqual((1920, 1080), (capture.source_width, capture.source_height))
        self.assertIsNone(capture.process)
        self.assertIsNone(capture.reader_thread)
        self.assertIsNone(capture.latest_frame)
        self.assertFalse(capture.restarting)
        self.assertFalse(capture.disabled)

    def test_detached_capture_reader_failure_does_not_disable_replacement(self) -> None:
        class BrokenOutput:
            @staticmethod
            def fileno() -> int:
                raise OSError("synthetic detached reader failure")

        capture = host.ContinuousFrameCapture(1280, 720, 30.0, "placeholder")
        detached_process = mock.Mock()
        detached_process.stdout = BrokenOutput()
        capture.process = None

        capture._reader_loop(detached_process)

        self.assertFalse(capture.disabled)

    def test_mjpeg_parser_keeps_frames_larger_than_old_four_mib_limit(self) -> None:
        encoded = (
            host.JPEG_SOI
            + b"x" * (4 * 1024 * 1024 + 1)
            + host.JPEG_EOI
        )
        buffer = bytearray(encoded)

        frames = host.extract_jpeg_frames(buffer)

        self.assertEqual([encoded], frames)
        self.assertEqual(bytearray(), buffer)
        self.assertEqual(
            protocol.MAX_FRAME_PAYLOAD_BYTES - host.LEGACY_FRAME_HEADER_BYTES,
            host.MAX_JPEG_FRAME_BYTES,
        )

    def test_mjpeg_parser_reports_protocol_limit_instead_of_truncating(self) -> None:
        partial = bytearray(host.JPEG_SOI + b"x" * 7)

        with self.assertRaisesRegex(
            host.JpegFrameLimitError,
            "protocol-safe limit of 8 encoded bytes",
        ):
            host.extract_jpeg_frames(partial, max_frame_bytes=8)

        complete = bytearray(host.JPEG_SOI + b"x" * 7 + host.JPEG_EOI)
        with self.assertRaisesRegex(
            host.JpegFrameLimitError,
            "protocol-safe limit of 8 encoded bytes",
        ):
            host.extract_jpeg_frames(complete, max_frame_bytes=8)

    def test_mjpeg_reader_exposes_oversize_failure_state(self) -> None:
        capture = host.ContinuousFrameCapture(3840, 2160, 60.0, "x11")
        process = mock.Mock()
        process.stdout.fileno.return_value = 42
        process.poll.return_value = None
        capture.process = process

        with (
            mock.patch.object(host.os, "read", return_value=b"encoded"),
            mock.patch.object(
                host,
                "extract_jpeg_frames",
                side_effect=host.JpegFrameLimitError(
                    "ffmpeg MJPEG frame exceeded the protocol-safe limit"
                ),
            ),
            mock.patch.object(host, "log") as log,
        ):
            capture._reader_loop(process)

        self.assertTrue(capture.disabled)
        self.assertIn("protocol-safe limit", capture.unavailable_reason or "")
        self.assertIsNone(capture.process)
        process.terminate.assert_called_once_with()
        process.wait.assert_called_once_with(timeout=1)
        self.assertTrue(
            any("protocol-safe limit" in str(call) for call in log.call_args_list)
        )

    def test_continuous_capture_does_not_sleep_after_waiting_for_new_frame(self) -> None:
        self.assertEqual(
            0.0,
            host.calculate_frame_loop_wait_seconds(
                continuous_capture_waited=True,
                sent_frame=False,
                frame_interval=1.0 / 30.0,
                elapsed=0.02,
            ),
        )
        self.assertAlmostEqual(
            0.025,
            host.calculate_frame_loop_wait_seconds(
                continuous_capture_waited=False,
                sent_frame=True,
                frame_interval=1.0 / 30.0,
                elapsed=1.0 / 120.0,
            ),
        )

    def test_protocol_writes_length_and_ciphertext_in_one_send(self) -> None:
        class FakeSocket:
            def __init__(self) -> None:
                self.writes: list[bytes] = []

            def sendall(self, packet: bytes) -> None:
                self.writes.append(packet)

        class FakeSession:
            @staticmethod
            def encrypt(plain: bytes) -> bytes:
                return b"encrypted:" + plain

        sock = FakeSocket()
        protocol.write_message(
            sock,  # type: ignore[arg-type]
            FakeSession(),  # type: ignore[arg-type]
            protocol.MESSAGE_FRAME,
            b"x" * (16 * 1024 + 1),
        )

        self.assertEqual(1, len(sock.writes))
        encrypted_length = int.from_bytes(sock.writes[0][:4], "little", signed=True)
        self.assertEqual(len(sock.writes[0]) - 4, encrypted_length)

    def test_file_transfer_uses_smaller_send_chunk_than_receive_limit(self) -> None:
        self.assertEqual(128 * 1024, protocol.FILE_TRANSFER_CHUNK_BYTES)
        self.assertEqual(
            32 * 1024,
            protocol.RECOMMENDED_FILE_TRANSFER_CHUNK_BYTES,
        )

    def test_decoder_jpeg_parser_handles_markers_across_read_boundaries(self) -> None:
        buffer = bytearray(b"noise\xff")
        self.assertEqual([], app.extract_decoder_jpeg_frames(buffer))
        buffer.extend(b"\xd8payload\xff\xd9\xff\xd8new")
        self.assertEqual(
            [b"\xff\xd8payload\xff\xd9"],
            app.extract_decoder_jpeg_frames(buffer),
        )
        buffer.extend(b"\xff\xd9")
        self.assertEqual(
            [b"\xff\xd8new\xff\xd9"],
            app.extract_decoder_jpeg_frames(buffer),
        )

    def test_h264_access_unit_parser_keeps_config_and_latest_boundary(self) -> None:
        start = b"\x00\x00\x00\x01"
        first = (
            start
            + b"\x09\xf0"
            + start
            + b"\x67\x64\x00\x1f"
            + start
            + b"\x68\xee\x3c\x80"
            + start
            + b"\x65\x88"
        )
        second = start + b"\x09\xf0" + start + b"\x65\x99"
        buffer = bytearray(first[:11])

        self.assertEqual([], host.extract_h264_access_units(buffer))
        buffer.extend(first[11:] + second)
        self.assertEqual([first], host.extract_h264_access_units(buffer))
        self.assertEqual(second, bytes(buffer))

    def test_h264_access_unit_parser_handles_mixed_start_codes_and_large_aus(self) -> None:
        first = (
            b"garbage"
            + b"\x00\x00\x01\x09\xf0"
            + b"\x00\x00\x01\x65"
            + b"x" * 128_000
        )
        second = (
            b"\x00\x00\x00\x01\x09\xf0"
            + b"\x00\x00\x00\x01\x65next"
        )
        buffer = bytearray(first + second)

        frames = host.extract_h264_access_units(buffer)

        self.assertEqual(1, len(frames))
        self.assertTrue(frames[0].startswith(b"\x00\x00\x01\x09"))
        self.assertEqual(second, bytes(buffer))

    def test_h264_recovery_unit_inserts_cached_sps_pps(self) -> None:
        start = b"\x00\x00\x00\x01"
        configured = (
            start
            + b"\x09\xf0"
            + start
            + b"\x67\x64\x00\x1f"
            + start
            + b"\x68\xee\x3c\x80"
            + start
            + b"\x65\x88"
        )
        normalized, sps, pps, flags = host.normalize_h264_access_unit(
            configured,
            None,
            None,
        )
        self.assertEqual(configured, normalized)
        self.assertEqual(
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            flags,
        )

        idr_without_headers = start + b"\x09\xf0" + start + b"\x65\x99"
        normalized, _sps, _pps, flags = host.normalize_h264_access_unit(
            idr_without_headers,
            sps,
            pps,
        )
        self.assertEqual([9, 7, 8, 5], [item[0] for item in host.annex_b_nal_units(normalized)])
        self.assertEqual(
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            flags,
        )

    def test_hardware_encoder_commands_are_all_idr_and_low_latency(self) -> None:
        commands = host.build_h264_hardware_encoder_commands(
            "/usr/bin/ffmpeg",
            ":0",
            1920,
            1080,
            1280,
            720,
            30.0,
            {name for name, _display_name in host.H264_HARDWARE_ENCODERS},
            "/dev/dri/renderD129",
        )

        self.assertEqual(
            ["h264_nvenc", "h264_qsv", "h264_vaapi", "h264_v4l2m2m"],
            [candidate.encoder_name for candidate in commands],
        )
        for candidate in commands:
            command = list(candidate.command)
            self.assertEqual("1", command[command.index("-g") + 1])
            self.assertEqual("0", command[command.index("-bf") + 1])
            self.assertEqual(
                "h264_metadata=aud=insert",
                command[command.index("-bsf:v") + 1],
            )
            self.assertEqual("1", command[command.index("-thread_queue_size") + 1])
            self.assertEqual("pipe:1", command[-1])
            self.assertNotIn("libx264", command)
        nvenc = list(commands[0].command)
        self.assertNotIn("hwupload_cuda", nvenc[nvenc.index("-vf") + 1])
        self.assertTrue(nvenc[nvenc.index("-vf") + 1].endswith("format=nv12"))
        self.assertEqual("h264_nvenc", nvenc[nvenc.index("-c:v") + 1])
        self.assertIn(
            "flags=bicubic+accurate_rnd",
            nvenc[nvenc.index("-vf") + 1],
        )
        self.assertEqual("p4", nvenc[nvenc.index("-preset") + 1])
        self.assertEqual("vbr", nvenc[nvenc.index("-rc") + 1])
        self.assertEqual("1", nvenc[nvenc.index("-spatial-aq") + 1])
        self.assertEqual("1", nvenc[nvenc.index("-aq-strength") + 1])
        self.assertEqual(
            "0",
            nvenc[nvenc.index("-rc-lookahead") + 1],
        )
        vaapi = list(commands[2].command)
        self.assertEqual("/dev/dri/renderD129", vaapi[vaapi.index("-vaapi_device") + 1])
        self.assertIn("hwupload", vaapi[vaapi.index("-vf") + 1])

    def test_4k60_hardware_command_uses_60fps_and_lan_bitrate_budget(self) -> None:
        command = list(
            host.build_h264_hardware_encoder_commands(
                "/usr/bin/ffmpeg",
                ":0",
                3840,
                2160,
                3840,
                2160,
                60.0,
                {"h264_nvenc"},
            )[0].command
        )

        self.assertEqual("60", command[command.index("-framerate") + 1])
        self.assertEqual(
            "format=nv12",
            command[command.index("-vf") + 1],
        )
        self.assertEqual("79626240", command[command.index("-b:v") + 1])
        self.assertEqual("79626240", command[command.index("-maxrate") + 1])
        self.assertEqual(
            39_813_120,
            host.calculate_h264_bitrate(2560, 1440, 60.0),
        )
        self.assertEqual(
            59_719_680,
            host.calculate_h264_bitrate(3840, 2160, 30.0),
        )
        self.assertEqual(
            25_000_000,
            host.calculate_h264_bitrate(
                3840,
                2160,
                60.0,
                max_bitrate_bps=25_000_000,
            ),
        )

    def test_uhd_bitrate_is_monotonic_across_fps_and_resolution(self) -> None:
        frame_rate_bitrates = [
            host.calculate_h264_bitrate(3840, 2160, float(fps))
            for fps in range(1, 121)
        ]
        self.assertEqual(
            frame_rate_bitrates,
            sorted(frame_rate_bitrates),
        )
        resolution_bitrates = [
            host.calculate_h264_bitrate(width, 2160, 60.0)
            for width in range(1600, 4201, 2)
        ]
        self.assertEqual(
            resolution_bitrates,
            sorted(resolution_bitrates),
        )
        self.assertGreaterEqual(
            host.calculate_h264_bitrate(3840, 2160, 31.0),
            host.calculate_h264_bitrate(3840, 2160, 30.0),
        )
        self.assertGreaterEqual(
            host.calculate_h264_bitrate(3840, 2160, 60.0),
            host.calculate_h264_bitrate(3838, 2160, 60.0),
        )

    def test_negotiated_fps_change_restarts_active_hardware_encoder(self) -> None:
        capture = host.ContinuousHardwareH264Capture(3840, 2160, 30.0, "x11")
        process = mock.Mock()
        process.poll.return_value = None
        capture.process = process
        capture.selected_encoder_name = "h264_nvenc"
        capture.latest_frame = host.H264EncodedFrame(
            3840,
            2160,
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            b"\x00\x00\x00\x01\x65",
            1,
            "h264_nvenc",
        )

        self.assertTrue(capture.update_fps(60.0))

        self.assertEqual(60.0, capture.fps)
        self.assertIsNone(capture.process)
        self.assertIsNone(capture.latest_frame)
        process.terminate.assert_called_once_with()
        process.wait.assert_called_once_with(timeout=1.0)
        self.assertFalse(capture.update_fps(60.0))

    def test_hardware_encoder_inventory_filters_unavailable_candidates(self) -> None:
        completed = SimpleNamespace(
            returncode=0,
            stdout=" V....D h264_qsv Intel QSV\n V....D h264_vaapi VAAPI\n",
        )
        with mock.patch.object(host.subprocess, "run", return_value=completed):
            encoders = host.detect_ffmpeg_h264_encoders("/usr/bin/ffmpeg")

        self.assertEqual({"h264_qsv", "h264_vaapi"}, encoders)

    def test_stalled_hardware_encoder_is_detached_and_terminated_for_failover(self) -> None:
        capture = host.ContinuousHardwareH264Capture(1280, 720, 30.0, "x11")
        process = mock.Mock()
        process.poll.return_value = None
        capture.process = process
        capture.selected_encoder_name = "h264_nvenc"
        capture.latest_at = 10.0
        capture.latest_frame = host.H264EncodedFrame(
            1280,
            720,
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            b"\x00\x00\x00\x01\x65",
            1,
            "h264_nvenc",
        )

        self.assertTrue(
            capture.expire_stalled_encoder_if_needed(
                10.0 + host.H264_STALE_FRAME_SECONDS + 0.001,
            )
        )

        process.terminate.assert_called_once_with()
        process.wait.assert_called_once_with(timeout=1.0)
        self.assertIsNone(capture.process)
        self.assertIsNone(capture.selected_encoder_name)
        self.assertIsNone(capture.latest_frame)

    def test_dead_active_encoder_is_treated_as_failover_pending(self) -> None:
        capture = host.ContinuousHardwareH264Capture(1280, 720, 30.0, "x11")
        process = mock.Mock()
        process.poll.return_value = 1
        selector = mock.Mock()
        selector.is_alive.return_value = True
        capture.desired = True
        capture.process = process
        capture.selected_encoder_name = "h264_nvenc"
        capture.selection_thread = selector

        self.assertTrue(capture.is_starting())

    def test_viewer_h264_negotiation_starts_hardware_and_jpeg_request_stops_it(self) -> None:
        hardware = mock.Mock()
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.viewer_video_codecs = host.VIDEO_CODEC_JPEG
        session.viewer_info_received = False
        session.h264_stream_active = False
        session.last_h264_frame_id = None
        session.hardware_h264_capture = hardware

        session._handle_viewer_info(host.VIDEO_CODEC_JPEG | host.VIDEO_CODEC_H264_ANNEX_B)
        hardware.request_start.assert_called_once_with()
        session._handle_viewer_info(host.VIDEO_CODEC_JPEG)
        hardware.stop.assert_called_once_with()
        self.assertEqual(host.VIDEO_CODEC_JPEG, session.viewer_video_codecs)

    def test_h264_send_uses_video_frame_message_and_pauses_jpeg_capture(self) -> None:
        encoded = b"\x00\x00\x00\x01\x09\xf0\x00\x00\x00\x01\x65\x88"
        h264_frame = host.H264EncodedFrame(
            1280,
            720,
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            encoded,
            9,
            "h264_nvenc",
        )
        hardware = mock.Mock()
        hardware.fps = 30.0
        hardware.read_frame.return_value = h264_frame
        jpeg = mock.Mock()
        jpeg.fps = 30.0
        jpeg.is_running.return_value = True
        messages: list[tuple[int, bytes]] = []
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.viewer_video_codecs = host.VIDEO_CODEC_JPEG | host.VIDEO_CODEC_H264_ANNEX_B
        session.viewer_info_received = True
        session.h264_stream_active = False
        session.last_h264_frame_id = None
        session.hardware_h264_capture = hardware
        session.continuous_capture = jpeg
        session.continuous_frame_ready = True
        session.last_continuous_frame_id = 4
        session.geometry_lock = threading.Lock()
        session.last_frame_width = 1
        session.last_frame_height = 1
        session.args = SimpleNamespace(fps=30.0)
        session._write_message = lambda message_type, payload: messages.append((message_type, payload))

        self.assertTrue(session._send_frame())

        self.assertEqual(1, len(messages))
        self.assertEqual(host.MESSAGE_VIDEO_FRAME, messages[0][0])
        decoded = protocol.decode_frame(messages[0][1], protocol.MESSAGE_VIDEO_FRAME)
        self.assertEqual("H264AnnexB", decoded["encoding"])
        self.assertEqual(len(encoded), decoded["encodedBytes"])
        self.assertEqual(
            host.FRAME_FLAG_KEY_FRAME | host.FRAME_FLAG_CODEC_CONFIG,
            decoded["flags"],
        )
        jpeg.pause.assert_called_once_with()
        self.assertTrue(session.h264_stream_active)
        self.assertEqual(9, session.last_h264_frame_id)

    @staticmethod
    def startup_preview_session():
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.geometry_lock = threading.Lock()
        session.viewer_video_codecs = host.VIDEO_CODEC_JPEG
        session.viewer_info_received = False
        session.startup_jpeg_sent = False
        session.codec_negotiation_deadline = float("inf")
        session.h264_stream_active = False
        session.last_h264_frame_id = None
        session.continuous_frame_ready = True
        session.last_continuous_frame_id = None
        session.continuous_capture = mock.Mock(fps=30.0)
        session.continuous_capture.read_frame.return_value = (640, 360, b"\xff\xd8full-quality\xff\xd9", 1)
        session.hardware_h264_capture = mock.Mock(fps=30.0)
        session.hardware_h264_capture.read_frame.return_value = None
        session.hardware_h264_capture.is_running.return_value = False
        session.hardware_h264_capture.is_starting.return_value = True
        session._write_message = mock.Mock()
        return session

    def test_startup_sends_one_unchanged_jpeg_until_codec_negotiation(self):
        session = self.startup_preview_session()
        self.assertTrue(session._send_frame())
        for _ in range(60):
            self.assertFalse(session._send_frame())
        session._write_message.assert_called_once()
        message_type, payload = session._write_message.call_args.args
        self.assertEqual(host.MESSAGE_FRAME, message_type)
        self.assertTrue(payload.endswith(b"\xff\xd8full-quality\xff\xd9"))
        session.continuous_capture.read_frame.assert_called_once()

    def test_old_client_without_viewer_info_resumes_jpeg_after_grace(self):
        session = self.startup_preview_session()
        self.assertTrue(session._send_frame())
        session.codec_negotiation_deadline = 0
        self.assertTrue(session._send_frame())
        self.assertTrue(session._send_frame())
        self.assertEqual(3, session._write_message.call_count)

    def test_explicit_jpeg_selection_never_throttles_normal_stream(self):
        session = self.startup_preview_session()
        session.viewer_info_received = True
        for _ in range(5): self.assertTrue(session._send_frame())
        self.assertEqual(5, session._write_message.call_count)

    def test_hardware_startup_does_not_queue_more_jpeg_behind_preview(self):
        session = self.startup_preview_session()
        self.assertTrue(session._send_frame())
        session.viewer_info_received = True
        session.viewer_video_codecs |= host.VIDEO_CODEC_H264_ANNEX_B
        for _ in range(60): self.assertFalse(session._send_frame())
        session._write_message.assert_called_once()
        session.hardware_h264_capture.is_starting.return_value = False
        self.assertTrue(session._send_frame())
        self.assertEqual(2, session._write_message.call_count)

    def test_h264_can_start_directly_without_waiting_for_a_jpeg_preview(self):
        session = self.startup_preview_session()
        session.viewer_info_received = True
        session.viewer_video_codecs |= host.VIDEO_CODEC_H264_ANNEX_B
        session.hardware_h264_capture.read_frame.return_value = host.H264EncodedFrame(
            640, 360, 3, b"\x00\x00\x01\x65\x88", 1, "owned-test")
        self.assertTrue(session._send_frame())
        self.assertEqual(host.MESSAGE_VIDEO_FRAME, session._write_message.call_args.args[0])
        session.continuous_capture.read_frame.assert_not_called()

    def test_h264_only_session_fails_when_all_hardware_encoders_are_exhausted(self) -> None:
        hardware = mock.Mock()
        hardware.fps = 30.0
        hardware.read_frame.return_value = None
        hardware.is_running.return_value = False
        hardware.is_starting.return_value = False
        hardware.has_exhausted_candidates.return_value = True
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.viewer_video_codecs = host.VIDEO_CODEC_H264_ANNEX_B
        session.viewer_info_received = True
        session.h264_stream_active = False
        session.last_h264_frame_id = None
        session.hardware_h264_capture = hardware
        session.session_stop = threading.Event()
        session.args = SimpleNamespace(fps=30.0)
        session._safe_status = mock.Mock()

        with self.assertRaisesRegex(host.ProtocolError, "did not advertise JPEG"):
            session._send_frame()

        self.assertTrue(session.session_stop.is_set())
        session._safe_status.assert_called_once()
        self.assertFalse(session._safe_status.call_args.args[0])

    def test_session_fails_when_viewer_advertises_no_supported_video_codec(self) -> None:
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.viewer_video_codecs = 0
        session.viewer_info_received = True
        session.h264_stream_active = False
        session.session_stop = threading.Event()
        session.args = SimpleNamespace(fps=30.0)
        session._safe_status = mock.Mock()

        with self.assertRaisesRegex(host.ProtocolError, "no mutually supported video codec"):
            session._send_frame()

        self.assertTrue(session.session_stop.is_set())
        session._safe_status.assert_called_once()
        self.assertFalse(session._safe_status.call_args.args[0])

    def test_exhausted_hardware_encoder_falls_back_when_viewer_supports_jpeg(self) -> None:
        hardware = mock.Mock()
        hardware.fps = 30.0
        hardware.read_frame.return_value = None
        hardware.is_running.return_value = False
        hardware.is_starting.return_value = False
        hardware.has_exhausted_candidates.return_value = True
        jpeg = mock.Mock()
        jpeg.fps = 30.0
        jpeg.read_frame.return_value = (640, 360, b"\xff\xd8frame\xff\xd9", 7)
        messages: list[tuple[int, bytes]] = []
        session = host.LinuxHostSession.__new__(host.LinuxHostSession)
        session.video_lock = threading.Lock()
        session.viewer_video_codecs = host.VIDEO_CODEC_H264_ANNEX_B | host.VIDEO_CODEC_JPEG
        session.viewer_info_received = True
        session.h264_stream_active = False
        session.last_h264_frame_id = None
        session.hardware_h264_capture = hardware
        session.continuous_capture = jpeg
        session.continuous_frame_ready = True
        session.last_continuous_frame_id = None
        session.geometry_lock = threading.Lock()
        session.last_frame_width = 1
        session.last_frame_height = 1
        session.session_stop = threading.Event()
        session.capture_warmup_until = 0.0
        session.frame_width = 640
        session.frame_height = 360
        session.args = SimpleNamespace(fps=30.0, capture="x11")
        session._write_message = lambda message_type, payload: messages.append((message_type, payload))

        self.assertTrue(session._send_frame())

        self.assertFalse(session.session_stop.is_set())
        self.assertEqual(host.MESSAGE_FRAME, messages[0][0])

    def test_ffmpeg_hardware_inventory_parser_ignores_headers(self) -> None:
        hwaccels = app.parse_ffmpeg_hwaccels(
            """
Hardware acceleration methods:
cuda
vaapi
qsv
drm
"""
        )
        decoders = app.parse_ffmpeg_decoders(
            """
Decoders:
 V..... = Video
 VFS..D h264                 H.264 / AVC
 V....D h264_qsv             H.264 / AVC (Intel Quick Sync Video)
 V..... h264_cuvid           Nvidia CUVID H264 decoder
 V..... h264_v4l2m2m         V4L2 mem2mem H.264 decoder wrapper
"""
        )

        self.assertEqual(frozenset({"cuda", "vaapi", "qsv", "drm"}), hwaccels)
        self.assertEqual(
            frozenset({"h264", "h264_qsv", "h264_cuvid", "h264_v4l2m2m"}),
            decoders,
        )

    def test_mpv_native_hwdec_probe_uses_only_non_copy_backends_in_priority_order(self) -> None:
        available = app.parse_mpv_hwdec_help(
            """
Available hardware decoders:
  nvdec
  nvdec-copy
  vaapi
  vaapi-copy
  qsv
  drm
  v4l2m2m-copy
"""
        )

        self.assertEqual(frozenset({"nvdec", "vaapi", "drm"}), available)
        self.assertEqual(
            ["nvdec", "vaapi", "drm"],
            [
                backend.key
                for backend in app.select_mpv_native_h264_backends(available)
            ],
        )

    def test_mpv_native_presenter_requires_real_x11_or_xwayland_window(self) -> None:
        self.assertTrue(
            app.is_mpv_native_presenter_environment(1234, "x11", ":0")
        )
        self.assertFalse(
            app.is_mpv_native_presenter_environment(0, "x11", ":0")
        )
        self.assertFalse(
            app.is_mpv_native_presenter_environment(1234, "wayland", ":0")
        )
        self.assertFalse(
            app.is_mpv_native_presenter_environment(1234, "x11", "")
        )

    def test_mpv_native_presenter_command_embeds_xid_and_forbids_copyback(self) -> None:
        backend = app.select_mpv_native_h264_backends({"nvdec"})[0]

        command = app.build_mpv_native_h264_presenter_command(
            "/usr/bin/mpv",
            98765,
            backend,
            "/run/user/1000/remotedesk-test.sock",
        )
        combined = " ".join(command).lower()

        self.assertEqual("/usr/bin/mpv", command[0])
        self.assertNotIn("--no-terminal", command)
        self.assertIn("--input-terminal=no", command)
        self.assertIn(
            "--input-ipc-server=/run/user/1000/remotedesk-test.sock",
            command,
        )
        self.assertIn("--msg-level=all=warn,cplayer=info,vd=v,vo=v", command)
        self.assertIn("--wid=98765", command)
        self.assertIn("--vo=gpu-next", command)
        self.assertIn("--gpu-context=x11egl", command)
        self.assertIn("--gpu-api=opengl", command)
        self.assertIn("--hwdec=nvdec", command)
        self.assertIn("--gpu-hwdec-interop=cuda", command)
        self.assertIn("--profile=low-latency", command)
        self.assertIn("--cache=no", command)
        self.assertIn("--demuxer-lavf-analyzeduration=0", command)
        self.assertIn("--demuxer-lavf-probesize=32", command)
        self.assertIn("--demuxer-lavf-probe-info=no", command)
        self.assertIn("--untimed", command)
        self.assertIn("--video-latency-hacks=yes", command)
        self.assertIn("--swapchain-depth=1", command)
        self.assertIn("--opengl-swapinterval=0", command)
        self.assertEqual("-", command[-1])
        self.assertNotIn("hwdownload", combined)
        self.assertNotIn("nvdec-copy", combined)

    def test_mpv_native_activation_requires_non_copy_decoder_and_surface_interop(self) -> None:
        nvdec = app.select_mpv_native_h264_backends({"nvdec"})[0]
        vaapi = app.select_mpv_native_h264_backends({"vaapi"})[0]

        self.assertEqual(
            "nvdec",
            app.parse_mpv_active_hardware_decoder(
                "[vd] Using hardware decoding (nvdec)."
            ),
        )
        self.assertEqual(
            "nvdec-copy",
            app.parse_mpv_active_hardware_decoder(
                "[vd] Using hardware decoding (nvdec-copy)."
            ),
        )
        self.assertTrue(
            app.mpv_log_confirms_native_surface(
                "[vo/gpu-next/cuda] CUDA hwdec interop initialized",
                nvdec,
            )
        )
        self.assertTrue(
            app.mpv_log_confirms_native_surface(
                "[vo/gpu-next] using EGL dmabuf interop",
                vaapi,
            )
        )
        self.assertFalse(
            app.mpv_log_confirms_native_surface(
                "VO: [gpu-next] 1920x1080 nv12 on llvmpipe",
                nvdec,
            )
        )
        failed_cuda_interop = (
            "[vo/gpu-next/cuda] CUDA interop initialization failed "
            "-> CUDA_ERROR_INVALID_DEVICE"
        )
        self.assertFalse(
            app.mpv_log_confirms_native_surface(failed_cuda_interop, nvdec)
        )
        self.assertTrue(
            app.mpv_log_reports_native_surface_failure(
                failed_cuda_interop,
                nvdec,
            )
        )

        presenter = app.MpvNativeH264Presenter.__new__(
            app.MpvNativeH264Presenter
        )
        presenter.backend = nvdec
        presenter.state_lock = threading.Lock()
        presenter.decoder_confirmed = False
        presenter.surface_confirmed = False
        presenter.activation_failure = ""
        presenter.error_tail = ""
        presenter._record_log_line("[vd] Using hardware decoding (nvdec-copy).")

        self.assertFalse(presenter.decoder_confirmed)
        self.assertIn("expected non-copy nvdec", presenter.activation_failure)

        presenter.activation_failure = ""
        presenter._record_log_line(failed_cuda_interop)
        self.assertFalse(presenter.surface_confirmed)
        self.assertIn("surface interop failed", presenter.activation_failure)

    def test_mpv_ipc_properties_are_authoritative_for_native_activation(self) -> None:
        response = (
            b'{"event":"start-file"}\n'
            b'{"data":"nvdec","request_id":7,"error":"success"}\n'
        )
        self.assertEqual(
            "nvdec",
            app.parse_mpv_ipc_property_response(response, 7),
        )
        self.assertIsNone(app.parse_mpv_ipc_property_response(response, 8))

        ipc_socket = mock.MagicMock()
        ipc_socket.recv.return_value = (
            b'{"data":"nvdec","request_id":1,"error":"success"}\n'
            b'{"data":"cuda","request_id":2,"error":"success"}\n'
        )
        ipc_context = mock.MagicMock()
        ipc_context.__enter__.return_value = ipc_socket
        with (
            mock.patch.object(app.socket, "AF_UNIX", 1, create=True),
            mock.patch.object(app.socket, "socket", return_value=ipc_context),
        ):
            properties = app.query_mpv_ipc_properties(
                "/tmp/remotedesk-unit-test.sock",
                ("hwdec-current", "hwdec-interop"),
            )

        self.assertEqual(
            {
                "hwdec-current": "nvdec",
                "hwdec-interop": "cuda",
            },
            properties,
        )
        request_lines = ipc_socket.sendall.call_args.args[0].splitlines()
        self.assertEqual(2, len(request_lines))
        self.assertEqual(
            {"hwdec-current", "hwdec-interop"},
            {
                json.loads(request_line)["command"][1]
                for request_line in request_lines
            },
        )

        presenter = app.MpvNativeH264Presenter.__new__(
            app.MpvNativeH264Presenter
        )
        presenter.backend = app.select_mpv_native_h264_backends({"nvdec"})[0]
        presenter.stop_event = threading.Event()
        presenter.first_submission_event = threading.Event()
        presenter.first_submission_event.set()
        presenter.state_lock = threading.Lock()
        presenter.first_submission_at = time.monotonic()
        presenter.ipc_socket_path = "/tmp/remotedesk-unit-test.sock"
        presenter.decoder_confirmed = False
        presenter.surface_confirmed = False
        presenter.activation_failure = ""

        with mock.patch.object(
            app,
            "query_mpv_ipc_properties",
            return_value={
                "hwdec-current": "nvdec",
                "hwdec-interop": "cuda",
            },
        ) as query:
            presenter._monitor_ipc_loop()

        self.assertTrue(presenter.decoder_confirmed)
        self.assertTrue(presenter.surface_confirmed)
        self.assertEqual("", presenter.activation_failure)
        query.assert_called_once_with(
            "/tmp/remotedesk-unit-test.sock",
            ("hwdec-current", "hwdec-interop"),
        )

        presenter.decoder_confirmed = False
        presenter.surface_confirmed = False
        presenter.activation_failure = ""
        with mock.patch.object(
            app,
            "query_mpv_ipc_properties",
            return_value={
                "hwdec-current": "nvdec-copy",
                "hwdec-interop": "cuda",
            },
        ):
            presenter._monitor_ipc_loop()

        self.assertFalse(presenter.decoder_confirmed)
        self.assertIn("hwdec-current=nvdec-copy", presenter.activation_failure)

    def test_native_presenter_close_reaps_process_after_worker_sets_stop(self) -> None:
        presenter = app.MpvNativeH264Presenter.__new__(
            app.MpvNativeH264Presenter
        )
        presenter.stop_event = threading.Event()
        presenter.stop_event.set()
        presenter.first_submission_event = threading.Event()
        presenter.close_lock = threading.Lock()
        presenter.closed = False
        presenter.frame_condition = threading.Condition()
        presenter.pending_frames = deque(
            [app.NativeH264InputFrame(b"pending", True)]
        )
        presenter.process = mock.Mock()
        presenter.process.poll.return_value = None
        presenter.process.wait.return_value = 0
        presenter.writer_thread = mock.Mock()
        presenter.writer_thread.is_alive.return_value = False
        presenter.log_thread = mock.Mock()
        presenter.log_thread.is_alive.return_value = False
        presenter.ipc_thread = mock.Mock()
        presenter.ipc_thread.is_alive.return_value = False
        presenter.ipc_socket_path = "/tmp/remotedesk-close-unit-test.sock"

        presenter.close()
        presenter.close()

        self.assertTrue(presenter.closed)
        self.assertEqual([], list(presenter.pending_frames))
        presenter.process.terminate.assert_called_once_with()
        presenter.process.stdin.close.assert_called_once_with()

    def test_native_presenter_input_queue_is_latest_first_and_bounded(self) -> None:
        pending: deque[app.NativeH264InputFrame] = deque()
        first_recovery = app.NativeH264InputFrame(b"recovery-1", True)
        newest_recovery = app.NativeH264InputFrame(b"recovery-2", True)

        self.assertTrue(
            app.enqueue_native_h264_input(pending, first_recovery, 4).accepted
        )
        pending.append(app.NativeH264InputFrame(b"prediction", False))
        self.assertTrue(
            app.enqueue_native_h264_input(pending, newest_recovery, 4).accepted
        )
        self.assertEqual([newest_recovery], list(pending))

        self.assertEqual(2, app.MAX_NATIVE_PRESENTER_H264_FRAMES)
        for index in range(app.MAX_NATIVE_PRESENTER_H264_FRAMES - 1):
            result = app.enqueue_native_h264_input(
                pending,
                app.NativeH264InputFrame(f"p{index}".encode(), False),
                app.MAX_NATIVE_PRESENTER_H264_FRAMES,
            )
            self.assertTrue(result.accepted)
        overflow = app.enqueue_native_h264_input(
            pending,
            app.NativeH264InputFrame(b"overflow", False),
            app.MAX_NATIVE_PRESENTER_H264_FRAMES,
        )

        self.assertFalse(overflow.accepted)
        self.assertTrue(overflow.request_recovery)
        self.assertEqual([], list(pending))

    def test_native_presenter_process_exit_falls_back_and_requests_recovery(self) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", events, 2)
        backend = app.select_mpv_native_h264_backends({"nvdec"})[0]
        presenter = mock.Mock()
        presenter.backend = backend
        presenter.is_running = False
        presenter.failure_detail = "mpv exited with code 2"
        viewer.native_h264_presenter_candidates = (backend,)
        viewer.native_h264_presenter = presenter
        viewer.reported_native_h264_presenter_backend = backend.key
        viewer.advertised_viewer_capabilities = app.linux_viewer_capabilities(True)
        prediction_frame = (
            1,
            app.FRAME_ENCODING_H264_ANNEX_B,
            1920,
            1080,
            0,
            b"\x00\x00\x00\x01\x41",
            time.monotonic(),
        )

        with mock.patch.object(viewer, "_request_video_key_frame_if_due") as request:
            disposition = viewer._try_present_native_h264(prediction_frame)

        self.assertEqual("wait-recovery", disposition)
        self.assertIsNone(viewer._get_native_h264_presenter())
        presenter.close.assert_called_once_with()
        request.assert_called_once_with()
        self.assertEqual(
            0,
            viewer.advertised_viewer_capabilities
            & protocol.CAPABILITY_HIGH_FRAME_RATE_H264,
        )
        statuses = [
            value[1]
            for event, value in list(events.queue)
            if event == "viewer_status"
        ]
        self.assertTrue(any("回退 FFmpeg GPU→CPU 回读/Tk" in text for text in statuses))

    def test_native_activation_allows_only_one_compatibility_preview(self) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", events, 2)
        backend = app.select_mpv_native_h264_backends({"nvdec"})[0]
        presenter = mock.Mock()
        presenter.backend = backend
        presenter.is_running = True
        presenter.activation_timed_out.return_value = False
        presenter.submit.return_value = app.NativeH264SubmitResult(True, False)
        presenter.is_native_surface_active = False
        presenter.claim_compatibility_preview.side_effect = [True, False]
        viewer.native_h264_presenter_candidates = (backend,)
        viewer.native_h264_presenter = presenter
        recovery_frame = (
            1,
            app.FRAME_ENCODING_H264_ANNEX_B,
            1920,
            1080,
            app.FRAME_FLAG_KEY_FRAME | app.FRAME_FLAG_CODEC_CONFIG,
            b"\x00\x00\x00\x01\x67",
            time.monotonic(),
        )
        prediction_frame = (
            2,
            app.FRAME_ENCODING_H264_ANNEX_B,
            1920,
            1080,
            0,
            b"\x00\x00\x00\x01\x41",
            time.monotonic(),
        )

        self.assertEqual(
            "pending-preview",
            viewer._try_present_native_h264(recovery_frame),
        )
        self.assertEqual(
            "pending",
            viewer._try_present_native_h264(prediction_frame),
        )
        self.assertEqual(2, presenter.submit.call_count)
        self.assertEqual(
            0.04,
            app.h264_decode_timeout_for_native_disposition("pending-preview"),
        )
        self.assertEqual(
            app.H264_DECODE_TIMEOUT_SECONDS,
            app.h264_decode_timeout_for_native_disposition("fallback"),
        )

    def test_verified_native_surface_enables_high_frame_rate_capability(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        backend = app.select_mpv_native_h264_backends({"nvdec"})[0]
        presenter = mock.Mock()
        presenter.backend = backend
        presenter.is_running = True
        presenter.activation_timed_out.return_value = False
        presenter.submit.return_value = app.NativeH264SubmitResult(True, False)
        presenter.is_native_surface_active = True
        viewer.native_h264_presenter_candidates = (backend,)
        viewer.native_h264_presenter = presenter
        viewer.advertised_viewer_capabilities = app.linux_viewer_capabilities(False)
        frame = (
            1,
            app.FRAME_ENCODING_H264_ANNEX_B,
            3840,
            2160,
            app.FRAME_FLAG_KEY_FRAME | app.FRAME_FLAG_CODEC_CONFIG,
            b"\x00\x00\x00\x01\x67",
            time.monotonic(),
        )

        self.assertEqual("presented", viewer._try_present_native_h264(frame))
        self.assertNotEqual(
            0,
            viewer.advertised_viewer_capabilities
            & protocol.CAPABILITY_HIGH_FRAME_RATE_H264,
        )

    def test_native_and_tk_frame_events_share_one_latest_ui_slot(self) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()

        app.put_viewer_event(events, "viewer_frame", 9, ("old",))
        app.put_viewer_event(events, "viewer_native_frame", 9, ("new",))

        self.assertEqual(1, events.qsize())
        self.assertEqual(
            ("viewer_native_frame", (9, ("new",))),
            events.get_nowait(),
        )

    def test_hundred_thousand_log_lines_and_viewer_events_stay_bounded(
        self,
    ) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()

        for index in range(100_000):
            app.put_ui_event(
                events,
                "host_log",
                f"{index:06d} host log line\n",
            )
            app.put_viewer_event(
                events,
                "viewer_status",
                7,
                f"status {index}",
            )
            app.put_viewer_event(
                events,
                "viewer_frame",
                7,
                (index,),
            )

        self.assertEqual(3, events.qsize())
        pending_log = next(
            value for event, value in events.queue if event == "host_log"
        )
        self.assertIsInstance(pending_log, app.BoundedLineBuffer)
        self.assertLessEqual(
            pending_log.character_count,
            app.PENDING_HOST_LOG_MAX_CHARACTERS,
        )
        self.assertTrue(str(pending_log).endswith("99999 host log line\n"))

        tracker = app.BoundedLineLengthTracker(
            app.HOST_LOG_MAX_CHARACTERS,
            app.HOST_LOG_TRIM_CHARACTERS,
        )
        trim_batches = 0
        for index in range(100_000):
            trim_batches += bool(
                tracker.append(f"{index:06d} host log line\n")
            )
        self.assertLessEqual(
            tracker.character_count,
            app.HOST_LOG_MAX_CHARACTERS,
        )
        self.assertLess(trim_batches, 20)

    def test_ui_event_batch_prioritizes_terminal_and_honors_budgets(
        self,
    ) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        for generation in range(300):
            app.put_viewer_event(
                events,
                "viewer_status",
                generation,
                f"status {generation}",
            )
            app.put_viewer_event(
                events,
                "viewer_frame",
                generation,
                (generation,),
            )
        app.put_viewer_event(
            events,
            "viewer_auth_failed",
            999,
            "bad password",
        )
        app.put_ui_event(events, "viewer_closed", 999)

        clock_values = iter((0.0, 0.010))
        first_batch = app.dequeue_ui_event_batch(
            events,
            maximum_events=100,
            budget_seconds=0.006,
            clock=lambda: next(clock_values),
        )
        self.assertEqual(
            [("viewer_auth_failed", (999, "bad password"))],
            first_batch,
        )

        second_batch = app.dequeue_ui_event_batch(
            events,
            maximum_events=7,
            budget_seconds=999.0,
            clock=lambda: 0.0,
        )
        self.assertEqual(("viewer_closed", 999), second_batch[0])
        self.assertEqual(7, len(second_batch))
        self.assertLessEqual(events.qsize(), app.UI_EVENT_MAX_PENDING)

    def test_h264_decoder_policy_prefers_available_hardware_then_software(self) -> None:
        backends = app.select_ffmpeg_h264_decoder_backends(
            frozenset({"cuda", "vaapi", "qsv", "drm"}),
            frozenset({"h264", "h264_cuvid", "h264_qsv", "h264_v4l2m2m"}),
        )

        self.assertEqual(
            ["cuda-nvdec", "vaapi", "qsv", "v4l2m2m", "drm", "software"],
            [backend.key for backend in backends],
        )
        self.assertTrue(all(backend.hardware for backend in backends[:-1]))
        self.assertFalse(backends[-1].hardware)
        self.assertEqual(4, app.h264_decoder_miss_threshold(backends[0]))
        self.assertEqual(8, app.h264_decoder_miss_threshold(backends[-1]))
        qsv_command = app.build_ffmpeg_h264_decoder_command(
            "/usr/bin/ffmpeg",
            backends[2],
        )
        self.assertEqual("1", qsv_command[qsv_command.index("-async_depth") + 1])

    def test_h264_decoder_policy_does_not_invent_unlisted_backends(self) -> None:
        backends = app.select_ffmpeg_h264_decoder_backends(
            frozenset({"cuda", "vaapi", "qsv", "drm"}),
            frozenset({"h264"}),
        )

        self.assertEqual(
            ["cuda-nvdec", "vaapi", "qsv", "drm", "software"],
            [backend.key for backend in backends],
        )
        self.assertEqual("h264", backends[0].decoder_name)
        self.assertNotIn("v4l2m2m", [backend.key for backend in backends])

    def test_failed_h264_backend_is_skipped_without_looping(self) -> None:
        backends = app.select_ffmpeg_h264_decoder_backends(
            frozenset({"cuda", "vaapi"}),
            frozenset({"h264", "h264_cuvid"}),
        )

        self.assertEqual(
            "vaapi",
            app.choose_next_h264_decoder_backend(
                backends,
                {"cuda-nvdec"},
            ).key,
        )
        self.assertEqual(
            "software",
            app.choose_next_h264_decoder_backend(
                backends,
                {"cuda-nvdec", "vaapi"},
            ).key,
        )
        self.assertIsNone(
            app.choose_next_h264_decoder_backend(
                backends,
                {backend.key for backend in backends},
            )
        )

    def test_hardware_decoder_command_is_low_latency_and_explicitly_downloads(self) -> None:
        backend = app.select_ffmpeg_h264_decoder_backends(
            frozenset({"cuda"}),
            frozenset({"h264_cuvid", "h264"}),
        )[0]

        command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)

        self.assertEqual("/usr/bin/ffmpeg", command[0])
        self.assertEqual("discardcorrupt", command[command.index("-fflags") + 1])
        self.assertIn("low_delay", command)
        self.assertEqual(
            str(128 * 1024 * 1024),
            command[command.index("-max_alloc") + 1],
        )
        self.assertEqual("cuda", command[command.index("-hwaccel") + 1])
        self.assertEqual("h264_cuvid", command[command.index("-c:v") + 1])
        self.assertEqual(
            "hwdownload,format=nv12",
            command[command.index("-vf") + 1],
        )
        self.assertEqual("pipe:0", command[command.index("-i") + 1])
        self.assertEqual("pipe:1", command[-1])
        self.assertIn("GPU→CPU 回读", backend.diagnostic)

    def test_all_decoder_backends_retain_the_initial_recovery_packet(self) -> None:
        backends = app.select_ffmpeg_h264_decoder_backends(
            frozenset({"cuda", "vaapi", "qsv", "drm"}),
            frozenset({"h264", "h264_cuvid", "h264_qsv", "h264_v4l2m2m"}),
        )
        for backend in backends:
            with self.subTest(backend=backend.key):
                command = app.build_ffmpeg_h264_decoder_command("ffmpeg", backend)
                flags = command[command.index("-fflags") + 1].split("+")
                self.assertNotIn("nobuffer", flags)
                self.assertIn("discardcorrupt", flags)
                self.assertEqual("32", command[command.index("-probesize") + 1])
                self.assertEqual("0", command[command.index("-analyzeduration") + 1])
                self.assertEqual(4, app.MAX_H264_DECODER_OUTSTANDING_CORRELATIONS)

    def test_v4l2m2m_decoder_does_not_add_invalid_generic_hwaccel(self) -> None:
        backend = app.select_ffmpeg_h264_decoder_backends(
            frozenset(),
            frozenset({"h264", "h264_v4l2m2m"}),
        )[0]

        command = app.build_ffmpeg_h264_decoder_command("/usr/bin/ffmpeg", backend)

        self.assertEqual("v4l2m2m", backend.key)
        self.assertNotIn("-hwaccel", command)
        self.assertNotIn("-vf", command)
        self.assertEqual("h264_v4l2m2m", command[command.index("-c:v") + 1])

    def test_h264_decoder_output_keeps_fifo_au_correlation_after_timeout(self) -> None:
        decoder = app.H264AnnexBDecoder.__new__(app.H264AnnexBDecoder)
        decoder.stop_event = threading.Event()
        decoder.frame_condition = threading.Condition()
        decoder.submitted_correlations = deque(["first-au"])
        decoder.pending_jpegs = deque()

        self.assertIsNone(decoder._wait_for_next_jpeg(0.0))
        decoder.submitted_correlations.append("second-au")
        decoder._publish_jpeg(b"\xff\xd8first\xff\xd9")
        decoder._publish_jpeg(b"\xff\xd8second\xff\xd9")

        first = decoder._wait_for_next_jpeg(0.0)
        second = decoder._wait_for_next_jpeg(0.0)
        self.assertEqual(("first-au", b"\xff\xd8first\xff\xd9"), (first.correlation, first.jpeg))
        self.assertEqual(("second-au", b"\xff\xd8second\xff\xd9"), (second.correlation, second.jpeg))

    def test_h264_decoder_correlation_cap_rejects_fifth_without_remapping(
        self,
    ) -> None:
        decoder = app.H264AnnexBDecoder.__new__(app.H264AnnexBDecoder)
        decoder.stop_event = threading.Event()
        decoder.write_lock = threading.Lock()
        decoder.frame_condition = threading.Condition()
        decoder.submitted_correlations = deque()
        decoder.pending_jpegs = deque()
        decoder.correlation_overflowed = False
        decoder.process = mock.Mock()
        decoder.process.poll.return_value = None
        decoder.process.stdin = mock.Mock()
        correlations = [f"au-{index}" for index in range(5)]

        for correlation in correlations:
            self.assertIsNone(
                decoder.decode_correlated(
                    b"\x00\x00\x00\x01\x65",
                    correlation,
                    timeout_seconds=0.0,
                )
            )

        self.assertEqual(
            app.MAX_H264_DECODER_OUTSTANDING_CORRELATIONS,
            4,
        )
        self.assertEqual(4, decoder.process.stdin.write.call_count)
        self.assertEqual(correlations[:4], list(decoder.submitted_correlations))
        self.assertNotIn(correlations[4], decoder.submitted_correlations)
        self.assertTrue(decoder.correlation_overflowed)
        self.assertEqual(4, decoder.outstanding_correlation_count)

        for index in range(4):
            decoder._publish_jpeg(
                b"\xff\xd8" + bytes([index]) + b"\xff\xd9"
            )
        decoded = [
            decoder._wait_for_next_jpeg(0.0)
            for _ in range(4)
        ]

        self.assertEqual(
            correlations[:4],
            [frame.correlation for frame in decoded],
        )
        self.assertEqual(
            [0, 1, 2, 3],
            [frame.jpeg[2] for frame in decoded],
        )
        self.assertEqual(0, decoder.outstanding_correlation_count)

    def test_h264_freshness_uses_submission_order_across_network_sequence_gaps(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        first = viewer._next_h264_decode_token(0, 1280, 720, 1.0)
        second = viewer._next_h264_decode_token(5, 1280, 720, 2.0)
        third = viewer._next_h264_decode_token(7, 1280, 720, 3.0)

        self.assertGreater(second.sequence - first.sequence, 1)
        self.assertEqual((0, 1, 2), (first.submission_id, second.submission_id, third.submission_id))
        self.assertEqual(2, viewer.latest_h264_submission_id)
        self.assertTrue(
            app.is_h264_submission_fresh(
                first.submission_id,
                second.submission_id,
                max_lag=1,
            )
        )
        self.assertFalse(
            app.is_h264_submission_fresh(
                first.submission_id,
                third.submission_id,
                max_lag=1,
            )
        )
        self.assertTrue(
            app.is_h264_submission_fresh(
                second.submission_id,
                third.submission_id,
                max_lag=1,
            )
        )

    def test_slow_jpeg_conversion_presents_without_building_fifo_latency(self) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            11,
        )
        rendered: list[bytes] = []
        converted: list[bytes] = []
        second_rendered = threading.Event()

        def jpeg_payload(marker: int) -> bytes:
            return (
                struct.pack("<iidd", 1280, 720, 0.0, 0.0)
                + b"\xff\xd8"
                + bytes([marker])
                + b"\xff\xd9"
            )

        def slow_convert(
            encoded: bytes,
            _width: int,
            _height: int,
        ) -> bytes:
            converted.append(encoded)
            if len(converted) == 1:
                # Simulate a capture producer outrunning one expensive
                # resize.  pending_frame must retain only marker 5.
                for marker in range(1, 6):
                    viewer._handle_frame(jpeg_payload(marker), legacy=True)
                time.sleep(0.01)
            return b"\x89PNG\r\n\x1a\n" + encoded

        def capture_event(event: str, value: object) -> None:
            if event != "viewer_frame":
                return
            frame_value = value
            assert isinstance(frame_value, tuple)
            rendered.append(frame_value[2])
            if len(rendered) >= 2:
                second_rendered.set()

        viewer._put_event = capture_event  # type: ignore[method-assign]
        with mock.patch.object(
            app,
            "convert_frame_for_tk",
            side_effect=slow_convert,
        ):
            viewer.decoder_thread.start()
            viewer._handle_frame(jpeg_payload(0), legacy=True)
            self.assertTrue(second_rendered.wait(timeout=1.0))
            viewer.stop_event.set()
            with viewer.frame_condition:
                viewer.frame_condition.notify_all()
            viewer.decoder_thread.join(timeout=1.0)

        self.assertFalse(viewer.decoder_thread.is_alive())
        self.assertEqual(6, viewer.frame_sequence)
        self.assertEqual(2, len(converted))
        self.assertEqual(0, converted[0][2])
        self.assertEqual(5, converted[1][2])
        self.assertEqual(2, len(rendered))

    def test_real_fifo_depth_two_h264_pipeline_remains_displayable(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            12,
        )
        rendered_markers: list[int] = []
        converted_markers: list[int] = []
        second_rendered = threading.Event()
        backend = app.FfmpegH264DecoderBackend(
            "test",
            "Test decoder",
            "h264",
            False,
        )

        class DepthTwoFifoDecoder:
            is_running = True
            failure_detail = ""
            correlation_overflowed = False

            def __init__(self) -> None:
                self.backend = backend
                self.correlations: deque[app.H264DecodeFrameToken] = deque()
                self.submitted_ids: list[int] = []
                self.output_ids: list[int] = []

            def decode_correlated(
                self,
                _encoded: bytes,
                correlation: app.H264DecodeFrameToken,
                _timeout: float,
            ) -> app.CorrelatedH264Jpeg | None:
                self.submitted_ids.append(correlation.submission_id)
                self.correlations.append(correlation)
                if len(self.correlations) <= 2:
                    return None
                completed = self.correlations.popleft()
                self.output_ids.append(completed.submission_id)
                return app.CorrelatedH264Jpeg(
                    completed,
                    b"\xff\xd8"
                    + bytes([completed.submission_id])
                    + b"\xff\xd9",
                )

        decoder = DepthTwoFifoDecoder()

        def h264_payload(marker: int, recovery: bool) -> bytes:
            return protocol.encode_video_frame(
                1280,
                720,
                (
                    app.FRAME_FLAG_KEY_FRAME
                    | app.FRAME_FLAG_CODEC_CONFIG
                    if recovery
                    else 0
                ),
                b"\x00\x00\x00\x01\x67" + bytes([marker]),
            )

        def convert(
            encoded: bytes,
            _width: int,
            _height: int,
        ) -> bytes:
            converted_markers.append(encoded[2])
            return b"png-" + encoded[2:3]

        def capture_event(event: str, value: object) -> None:
            if event != "viewer_frame":
                return
            frame_value = value
            assert isinstance(frame_value, tuple)
            rendered_markers.append(frame_value[2][-1])
            if len(rendered_markers) >= 2:
                viewer.stop_event.set()
                second_rendered.set()

        viewer._put_event = capture_event  # type: ignore[method-assign]
        with (
            mock.patch.object(
                viewer,
                "_try_present_native_h264",
                return_value="fallback",
            ),
            mock.patch.object(
                viewer,
                "_get_or_create_h264_decoder",
                return_value=decoder,
            ),
            mock.patch.object(
                app,
                "convert_frame_for_tk",
                side_effect=convert,
            ),
        ):
            viewer._handle_frame(h264_payload(0, recovery=True), legacy=False)
            for marker in range(1, 4):
                viewer._handle_frame(
                    h264_payload(marker, recovery=False),
                    legacy=False,
                )
            viewer.decoder_thread.start()
            self.assertTrue(second_rendered.wait(timeout=1.0))
            with viewer.frame_condition:
                viewer.frame_condition.notify_all()
            viewer.decoder_thread.join(timeout=1.0)

        self.assertFalse(viewer.decoder_thread.is_alive())
        self.assertEqual(4, viewer.frame_sequence)
        self.assertEqual([0, 1, 2, 3], decoder.submitted_ids)
        self.assertEqual([0, 1], decoder.output_ids)
        self.assertEqual([0, 1], converted_markers)
        self.assertEqual([0, 1], rendered_markers)
        self.assertEqual(2, app.MAX_H264_CORRELATED_SUBMISSION_LAG)

    def test_continuous_lag_three_h264_outputs_accumulate_and_rotate(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            13,
        )
        backend = app.FfmpegH264DecoderBackend(
            "stale",
            "Stale decoder",
            "h264",
            False,
        )
        next_backend = app.FfmpegH264DecoderBackend(
            "next",
            "Next decoder",
            "h264",
            False,
        )
        rotation_observed = threading.Event()
        rotation_reasons: list[str] = []
        rendered: list[object] = []

        def h264_payload(marker: int, recovery: bool = False) -> bytes:
            return protocol.encode_video_frame(
                1280,
                720,
                (
                    app.FRAME_FLAG_KEY_FRAME
                    | app.FRAME_FLAG_CODEC_CONFIG
                    if recovery
                    else 0
                ),
                b"\x00\x00\x00\x01\x41" + bytes([marker % 256]),
            )

        class LagThreeFifoDecoder:
            is_running = True
            failure_detail = ""
            correlation_overflowed = False

            def __init__(self) -> None:
                self.backend = backend
                self.correlations: deque[app.H264DecodeFrameToken] = deque()
                self.calls = 0
                self.closed = False

            def decode_correlated(
                self,
                _encoded: bytes,
                correlation: app.H264DecodeFrameToken,
                _timeout: float,
            ) -> app.CorrelatedH264Jpeg | None:
                self.calls += 1
                self.correlations.append(correlation)
                # Sustain the producer while the decoder consumes.  There is
                # always at most one new pending network frame.
                viewer._handle_frame(
                    h264_payload(self.calls),
                    legacy=False,
                )
                if len(self.correlations) <= 3:
                    return None
                completed = self.correlations.popleft()
                return app.CorrelatedH264Jpeg(
                    completed,
                    b"\xff\xd8stale\xff\xd9",
                )

            def close(self) -> None:
                self.closed = True

        decoder = LagThreeFifoDecoder()
        viewer.h264_decoder = decoder  # type: ignore[assignment]
        viewer.h264_decoder_candidates = (backend, next_backend)
        original_rotate = viewer._rotate_h264_decoder

        def record_rotation(
            reason: str,
            failed_backend: app.FfmpegH264DecoderBackend | None = None,
        ) -> bool:
            rotation_reasons.append(reason)
            result = original_rotate(reason, failed_backend)
            rotation_observed.set()
            return result

        viewer._rotate_h264_decoder = record_rotation  # type: ignore[method-assign]
        viewer._put_event = (  # type: ignore[method-assign]
            lambda event, value: rendered.append(value)
            if event == "viewer_frame"
            else None
        )
        with mock.patch.object(
            viewer,
            "_try_present_native_h264",
            return_value="fallback",
        ):
            viewer.decoder_thread.start()
            viewer._handle_frame(
                h264_payload(0, recovery=True),
                legacy=False,
            )
            self.assertTrue(rotation_observed.wait(timeout=1.0))
            viewer.stop_event.set()
            with viewer.frame_condition:
                viewer.frame_condition.notify_all()
            viewer.decoder_thread.join(timeout=1.0)

        self.assertFalse(viewer.decoder_thread.is_alive())
        self.assertEqual(
            app.H264_DECODE_MISS_FALLBACK_THRESHOLD,
            decoder.calls,
        )
        self.assertTrue(decoder.closed)
        self.assertEqual([], rendered)
        self.assertEqual(1, len(rotation_reasons))
        self.assertIn("only stale correlated frames", rotation_reasons[0])
        self.assertIn(backend.key, viewer.failed_h264_decoder_backends)

    def test_jpeg_epoch_drops_old_conversion_across_a_b_a_switch(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            14,
        )
        first_conversion_started = threading.Event()
        release_first_conversion = threading.Event()
        latest_rendered = threading.Event()
        converted_markers: list[int] = []
        rendered_markers: list[int] = []

        def jpeg_payload(marker: int) -> bytes:
            return (
                struct.pack("<iidd", 1280, 720, 0.0, 0.0)
                + b"\xff\xd8"
                + bytes([marker])
                + b"\xff\xd9"
            )

        def h264_recovery_payload() -> bytes:
            return protocol.encode_video_frame(
                1280,
                720,
                app.FRAME_FLAG_KEY_FRAME | app.FRAME_FLAG_CODEC_CONFIG,
                b"\x00\x00\x00\x01\x67\x00",
            )

        def blocked_convert(
            encoded: bytes,
            _width: int,
            _height: int,
        ) -> bytes:
            marker = encoded[2]
            converted_markers.append(marker)
            if marker == 0:
                first_conversion_started.set()
                self.assertTrue(release_first_conversion.wait(timeout=1.0))
            return b"png-" + bytes([marker])

        def capture_event(event: str, value: object) -> None:
            if event != "viewer_frame":
                return
            frame_value = value
            assert isinstance(frame_value, tuple)
            rendered_markers.append(frame_value[2][-1])
            viewer.stop_event.set()
            latest_rendered.set()

        viewer._put_event = capture_event  # type: ignore[method-assign]
        with mock.patch.object(
            app,
            "convert_frame_for_tk",
            side_effect=blocked_convert,
        ):
            viewer.decoder_thread.start()
            viewer._handle_frame(jpeg_payload(0), legacy=True)
            self.assertTrue(first_conversion_started.wait(timeout=1.0))

            # Codec A -> B -> A while the old A frame is still converting.
            viewer._handle_frame(h264_recovery_payload(), legacy=False)
            for marker in range(1, 6):
                viewer._handle_frame(jpeg_payload(marker), legacy=True)
            release_first_conversion.set()

            self.assertTrue(latest_rendered.wait(timeout=1.0))
            with viewer.frame_condition:
                viewer.frame_condition.notify_all()
            viewer.decoder_thread.join(timeout=1.0)

        self.assertFalse(viewer.decoder_thread.is_alive())
        self.assertEqual(7, viewer.frame_sequence)
        self.assertEqual(3, viewer.frame_stream_epoch)
        self.assertEqual([0, 5], converted_markers)
        self.assertEqual([5], rendered_markers)

    def test_h264_decoder_input_adds_explicit_access_unit_boundary(self) -> None:
        encoded = b"\x00\x00\x00\x01\x65\x88"

        terminated = app.terminate_h264_access_unit(encoded)

        self.assertEqual(encoded + app.H264_AUD_BOUNDARY, terminated)
        self.assertEqual(terminated, app.terminate_h264_access_unit(terminated))

    def test_viewer_cold_start_ignores_prediction_frames_until_full_recovery(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        prediction = protocol.encode_video_frame(1280, 720, 0, b"\x00\x00\x00\x01\x41")
        key_without_config = protocol.encode_video_frame(
            1280,
            720,
            app.FRAME_FLAG_KEY_FRAME,
            b"\x00\x00\x00\x01\x65",
        )
        recovery = protocol.encode_video_frame(
            1280,
            720,
            app.FRAME_FLAG_KEY_FRAME | app.FRAME_FLAG_CODEC_CONFIG,
            b"\x00\x00\x00\x01\x67\x00\x00\x00\x01\x65",
        )

        with mock.patch.object(viewer, "_request_video_key_frame_if_due") as request:
            viewer._handle_frame(prediction, legacy=False)
            viewer._handle_frame(key_without_config, legacy=False)

        self.assertTrue(viewer.waiting_for_h264_recovery)
        self.assertEqual([], list(viewer.pending_h264_frames))
        self.assertIsNone(viewer._get_h264_decoder())
        self.assertEqual(2, request.call_count)

        viewer._handle_frame(recovery, legacy=False)

        self.assertFalse(viewer.waiting_for_h264_recovery)
        self.assertEqual(1, len(viewer.pending_h264_frames))
        self.assertTrue(app.is_h264_recovery_frame(viewer.pending_h264_frames[0]))

    def test_viewer_natural_disconnect_releases_owned_decoder(self) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", events, 3, allow_self_connection_for_testing=True)
        decoder = mock.Mock()
        with viewer.h264_decoder_lock:
            viewer.h264_decoder = decoder
        sock = mock.MagicMock()
        sock.__enter__.return_value = sock

        with (
            mock.patch.object(app, "find_ffmpeg", return_value=None),
            mock.patch.object(app.socket, "create_connection", return_value=sock),
            mock.patch.object(app, "configure_low_latency_socket"),
            mock.patch.object(app, "authenticate", return_value=object()),
            mock.patch.object(app, "write_message"),
            mock.patch.object(app, "read_message", side_effect=EOFError),
        ):
            viewer._run()

        self.assertTrue(viewer.stop_event.is_set())
        self.assertIsNone(viewer._get_h264_decoder())
        decoder.close.assert_called_once_with()
        self.assertIn(("viewer_closed", 3), list(events.queue))

    def test_viewer_reconnect_policy_requires_device_info_and_uses_capped_backoff(
        self,
    ) -> None:
        policy = app.ViewerReconnectPolicy()

        self.assertIsNone(policy.next_delay())
        policy.begin()
        self.assertIsNone(policy.next_delay())
        policy.mark_device_info()

        self.assertEqual(
            [0.5, 1.0, 2.0, 4.0, 8.0, 10.0, 10.0],
            [policy.next_delay() for _ in range(7)],
        )

    def test_viewer_reconnect_policy_stops_on_auth_failure_or_manual_cancel(
        self,
    ) -> None:
        policy = app.ViewerReconnectPolicy()
        policy.begin()
        policy.mark_device_info()
        policy.mark_authentication_failed()
        self.assertIsNone(policy.next_delay())

        policy.begin()
        policy.mark_device_info()
        policy.cancel()
        self.assertIsNone(policy.next_delay())

    def test_immediate_post_device_info_disconnects_keep_increasing_backoff(
        self,
    ) -> None:
        policy = app.ViewerReconnectPolicy()
        policy.begin()

        delays = []
        for _ in range(4):
            policy.mark_device_info()
            delays.append(policy.next_delay())

        self.assertEqual([0.5, 1.0, 2.0, 4.0], delays)

    def test_generation_fenced_five_second_stability_resets_backoff(self) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        scheduled: list[tuple[int, object]] = []
        application.root = mock.Mock()
        application.root.after.side_effect = (
            lambda delay, action: scheduled.append((delay, action))
            or f"stable-{len(scheduled)}"
        )
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_policy.mark_device_info()
        self.assertEqual(0.5, application.viewer_reconnect_policy.next_delay())
        self.assertEqual(1.0, application.viewer_reconnect_policy.next_delay())
        application.viewer_reconnect_stable_after_id = None
        application.viewer_generation = 61
        application.viewer = mock.Mock()
        application.viewer.stop_event = threading.Event()
        application.closing = False

        application._schedule_viewer_stability_reset()

        self.assertEqual(
            int(app.VIEWER_RECONNECT_STABLE_SECONDS * 1000),
            scheduled[0][0],
        )
        self.assertEqual(2, application.viewer_reconnect_policy.retry_index)
        scheduled[0][1]()  # type: ignore[operator]
        self.assertEqual(0.5, application.viewer_reconnect_policy.next_delay())

        # A timer owned by the prior connection must not reset the new
        # generation's accumulated backoff even if cancellation races.
        application.viewer_reconnect_policy.retry_index = 3
        application.viewer = mock.Mock()
        application.viewer.stop_event = threading.Event()
        application._schedule_viewer_stability_reset()
        stale_callback = scheduled[1][1]
        application.viewer_generation += 1
        application.viewer = mock.Mock()
        stale_callback()  # type: ignore[operator]
        self.assertEqual(3, application.viewer_reconnect_policy.retry_index)

        application.viewer_reconnect_policy.retry_index = 4
        application.viewer = mock.Mock()
        application.viewer.stop_event = threading.Event()
        application._schedule_viewer_stability_reset()
        stopped_callback = scheduled[2][1]
        application.viewer.stop_event.set()
        stopped_callback()  # type: ignore[operator]
        self.assertEqual(4, application.viewer_reconnect_policy.retry_index)

    def test_viewer_device_info_emits_generation_fenced_reconnect_qualification(
        self,
    ) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            events,
            17,
        )

        viewer._handle_control(
            protocol.encode_device_info(
                "Linux host",
                "Linux",
                protocol.CAPABILITY_FILE_RECEIVE,
            )
        )

        self.assertIn(
            ("viewer_reconnect_qualified", (17, True)),
            list(events.queue),
        )

    def test_session_replacement_is_a_terminal_viewer_signal(self) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            18,
        )

        with self.assertRaisesRegex(
            app.SessionRejectedError,
            "另一台查看端接管",
        ):
            viewer._handle_control(
                protocol.encode_session_rejected(
                    "此连接已被另一台查看端接管；已停止自动重连。"
                )
            )

    def test_viewer_authentication_rejection_is_terminal_for_reconnect(
        self,
    ) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "wrong",
            events,
            18,
            allow_self_connection_for_testing=True,
        )
        sock = mock.MagicMock()
        sock.__enter__.return_value = sock

        with (
            mock.patch.object(app, "find_ffmpeg", return_value=None),
            mock.patch.object(app.socket, "create_connection", return_value=sock),
            mock.patch.object(app, "configure_low_latency_socket"),
            mock.patch.object(
                app,
                "authenticate",
                side_effect=PermissionError("bad password"),
            ),
        ):
            viewer._run()

        queued = list(events.queue)
        self.assertIn(
            ("viewer_auth_failed", (18, "bad password")),
            queued,
        )
        self.assertIn(("viewer_closed", 18), queued)
        self.assertFalse(any(event == "viewer_error" for event, _ in queued))

    def test_viewer_liveness_watchdog_interrupts_while_write_lock_is_held(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            19,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat()
        writer_done = threading.Event()

        def blocked_ping() -> None:
            try:
                viewer._write_heartbeat_ping(time.monotonic())
            finally:
                writer_done.set()

        viewer.write_lock.acquire()
        writer = threading.Thread(target=blocked_ping, daemon=True)
        try:
            writer.start()
            self.assertTrue(
                self._wait_until(
                    lambda: viewer.awaiting_pong_since is not None,
                    timeout=0.5,
                )
            )
            with viewer.heartbeat_lock:
                viewer.awaiting_pong_since = (
                    time.monotonic()
                    - app.VIEWER_HEARTBEAT_TIMEOUT_SECONDS
                    - 1.0
                )
                viewer.last_message_received_at = viewer.awaiting_pong_since
            viewer.liveness_thread.start()

            self.assertTrue(viewer.stop_event.wait(timeout=0.5))
            viewer.sock.shutdown.assert_called()
            viewer.sock.close.assert_called()
            self.assertFalse(writer_done.is_set())
        finally:
            viewer.write_lock.release()
            writer.join(timeout=1.0)
            viewer.liveness_thread.join(timeout=1.0)

        self.assertTrue(writer_done.is_set())
        self.assertFalse(viewer.liveness_thread.is_alive())

    def test_scheduled_viewer_reconnect_is_fenced_by_generation(self) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        callback: list[object] = []
        application.root = mock.Mock()
        application.root.after.side_effect = (
            lambda _delay, action: callback.append(action) or "timer-1"
        )
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_policy.mark_device_info()
        application.viewer_reconnect_after_id = None
        application.viewer_reconnect_stable_after_id = None
        application.viewer_reconnect_target = ("host", 56565, "1")
        application.viewer_generation = 23
        application.viewer = None
        application.closing = False
        application._start_viewer_attempt = mock.Mock()

        application._schedule_viewer_reconnect(0.5)
        application.viewer_generation += 1
        callback[0]()  # type: ignore[operator]

        application._start_viewer_attempt.assert_not_called()
        self.assertIsNone(application.viewer_reconnect_after_id)

    def test_viewer_closed_after_device_info_schedules_first_retry_without_closing_ui(
        self,
    ) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.root = mock.Mock()
        application.root.after.return_value = "retry-timer"
        application.events = queue.Queue()
        application.events.put(
            ("viewer_reconnect_qualified", (41, True))
        )
        application.events.put(("viewer_closed", 41))
        application.viewer = mock.Mock()
        application.viewer_generation = 41
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_after_id = None
        application.viewer_reconnect_stable_after_id = None
        application.viewer_reconnect_target = ("host", 56565, "1")
        application.closing = False
        application.frame_label = None
        application.last_photo = mock.Mock()
        application.native_presenter_active = True
        application.viewer_pressed_keys = mock.Mock()
        application.connect_button = mock.Mock()
        application.disconnect_button = mock.Mock()
        application.send_file_button = mock.Mock()
        application.send_folder_button = mock.Mock()
        application._set_viewer_status = mock.Mock()
        application._close_viewer_window = mock.Mock()

        application._poll_events()

        self.assertIsNone(application.viewer)
        self.assertEqual(42, application.viewer_generation)
        self.assertEqual("retry-timer", application.viewer_reconnect_after_id)
        self.assertEqual(
            [
                int(app.VIEWER_RECONNECT_STABLE_SECONDS * 1000),
                500,
                app.EVENT_POLL_MS,
            ],
            [call.args[0] for call in application.root.after.call_args_list],
        )
        application._close_viewer_window.assert_not_called()
        application.disconnect_button.config.assert_called_with(state=app.tk.NORMAL)

    def test_bad_password_on_retry_revokes_prior_reconnect_qualification(
        self,
    ) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.root = mock.Mock()
        application.events = queue.Queue()
        application.events.put(
            ("viewer_auth_failed", (51, "authentication rejected"))
        )
        application.events.put(("viewer_closed", 51))
        application.viewer = mock.Mock()
        application.viewer_generation = 51
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_policy.mark_device_info()
        application.viewer_reconnect_after_id = None
        application.viewer_reconnect_stable_after_id = None
        application.viewer_reconnect_target = ("host", 56565, "old-password")
        application.closing = False
        application.frame_label = None
        application.last_photo = None
        application.native_presenter_active = False
        application.viewer_pressed_keys = mock.Mock()
        application.connect_button = mock.Mock()
        application.disconnect_button = mock.Mock()
        application.send_file_button = mock.Mock()
        application.send_folder_button = mock.Mock()
        application._set_viewer_status = mock.Mock()
        application._close_viewer_window = mock.Mock()

        application._poll_events()

        self.assertTrue(application.viewer_reconnect_policy.cancelled)
        self.assertIsNone(application.viewer_reconnect_target)
        self.assertIsNone(application.viewer_reconnect_after_id)
        self.assertEqual(
            [app.EVENT_POLL_MS],
            [call.args[0] for call in application.root.after.call_args_list],
        )
        application._close_viewer_window.assert_called_once_with()

    def test_replaced_viewer_closes_without_automatic_reconnect(self) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.root = mock.Mock()
        application.events = queue.Queue()
        application.events.put(
            (
                "viewer_session_replaced",
                (
                    61,
                    "此连接已被另一台查看端接管；已停止自动重连。",
                ),
            )
        )
        application.events.put(("viewer_closed", 61))
        application.viewer = mock.Mock()
        application.viewer_generation = 61
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_policy.mark_device_info()
        application.viewer_reconnect_after_id = None
        application.viewer_reconnect_stable_after_id = None
        application.viewer_reconnect_target = ("host", 56565, "1")
        application.closing = False
        application.frame_label = None
        application.last_photo = mock.Mock()
        application.native_presenter_active = True
        application.viewer_pressed_keys = mock.Mock()
        application.connect_button = mock.Mock()
        application.disconnect_button = mock.Mock()
        application.send_file_button = mock.Mock()
        application.send_folder_button = mock.Mock()
        application._set_viewer_status = mock.Mock()
        application._close_viewer_window = mock.Mock()

        application._poll_events()

        self.assertTrue(application.viewer_reconnect_policy.cancelled)
        self.assertIsNone(application.viewer_reconnect_target)
        self.assertIsNone(application.viewer_reconnect_after_id)
        application._close_viewer_window.assert_called_once_with()
        application._set_viewer_status.assert_any_call(
            "连接已结束：此连接已被另一台查看端接管；已停止自动重连。"
        )

    def test_manual_disconnect_cancels_retry_and_dispatches_teardown_off_ui_thread(
        self,
    ) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.root = mock.Mock()
        owned_viewer = mock.Mock()
        application.viewer = owned_viewer
        application.viewer_generation = 31
        application.viewer_reconnect_policy = app.ViewerReconnectPolicy()
        application.viewer_reconnect_policy.begin()
        application.viewer_reconnect_policy.mark_device_info()
        application.viewer_reconnect_after_id = "retry-timer"
        application.viewer_reconnect_stable_after_id = None
        application.viewer_reconnect_target = ("host", 56565, "1")
        application._release_pressed_viewer_keys = mock.Mock(return_value=0)
        application._close_viewer_window = mock.Mock()
        application._set_viewer_status = mock.Mock()
        application.connect_button = mock.Mock()
        application.disconnect_button = mock.Mock()
        application.send_file_button = mock.Mock()
        application.send_folder_button = mock.Mock()
        close_thread = mock.Mock()

        with mock.patch.object(app.threading, "Thread", return_value=close_thread) as create_thread:
            application.disconnect_viewer()

        self.assertTrue(application.viewer_reconnect_policy.cancelled)
        self.assertIsNone(application.viewer_reconnect_target)
        self.assertIsNone(application.viewer_reconnect_after_id)
        self.assertEqual(32, application.viewer_generation)
        self.assertIsNone(application.viewer)
        application.root.after_cancel.assert_called_once_with("retry-timer")
        create_thread.assert_called_once()
        self.assertIs(
            application._close_viewer_after_input_release,
            create_thread.call_args.kwargs["target"],
        )
        self.assertEqual(
            (owned_viewer, 0),
            create_thread.call_args.kwargs["args"],
        )
        owned_viewer.request_close.assert_not_called()
        close_thread.start.assert_called_once_with()

    def test_stop_host_returns_while_process_wait_runs_in_background(self) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.events = queue.Queue()
        application.host_generation = 71
        application.host_stopping_generation = None
        application.host_stop_thread = None
        application.host_status = mock.Mock()
        application.start_host_button = mock.Mock()
        application.stop_host_button = mock.Mock()
        process = mock.Mock()
        application.host_process = process
        wait_entered = threading.Event()
        release_wait = threading.Event()

        def blocked_wait(*, timeout: float) -> int:
            self.assertEqual(4, timeout)
            wait_entered.set()
            self.assertTrue(release_wait.wait(timeout=1.0))
            return 0

        process.wait.side_effect = blocked_wait
        try:
            application.stop_host()
            self.assertTrue(wait_entered.wait(timeout=0.5))
            worker = application.host_stop_thread
            self.assertIsNotNone(worker)
            assert worker is not None
            self.assertTrue(worker.is_alive())
            application.host_status.config.assert_called_once_with(
                text="正在停止被控端..."
            )

            # A repeated click while the first worker is blocked is a no-op.
            application.stop_host()
            process.terminate.assert_called_once_with()
            self.assertIs(worker, application.host_stop_thread)
        finally:
            release_wait.set()
            if application.host_stop_thread is not None:
                application.host_stop_thread.join(timeout=1.0)

        self.assertFalse(worker.is_alive())
        self.assertIn(
            ("host_stop_completed", (71, process, 0, "")),
            list(application.events.queue),
        )

    def test_viewer_heartbeat_keeps_idle_session_active_past_thirty_seconds(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            1,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)

        with mock.patch.object(app, "write_message") as write_message:
            for now in range(5, 36, 5):
                self.assertTrue(viewer._heartbeat_step(float(now)))
                self.assertTrue(
                    viewer._handle_heartbeat_message(protocol.MESSAGE_PONG)
                )

        self.assertEqual(7, write_message.call_count)
        self.assertTrue(
            all(
                call.args[2] == protocol.MESSAGE_PING
                and call.args[3] == b""
                for call in write_message.call_args_list
            )
        )
        self.assertFalse(viewer.stop_event.is_set())

    def test_viewer_heartbeat_answers_ping_and_tracks_pong(self) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            1,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)

        with mock.patch.object(app, "write_message") as write_message:
            self.assertTrue(
                viewer._handle_heartbeat_message(protocol.MESSAGE_PING)
            )
            self.assertTrue(viewer._heartbeat_step(5.0))
            with viewer.heartbeat_lock:
                self.assertEqual(5.0, viewer.awaiting_pong_since)
            self.assertTrue(
                viewer._handle_heartbeat_message(protocol.MESSAGE_PONG)
            )
            with viewer.heartbeat_lock:
                self.assertIsNone(viewer.awaiting_pong_since)

        self.assertEqual(
            [protocol.MESSAGE_PONG, protocol.MESSAGE_PING],
            [call.args[2] for call in write_message.call_args_list],
        )

    def test_viewer_heartbeat_timeout_closes_unresponsive_session(self) -> None:
        events: "queue.Queue[tuple[str, object]]" = queue.Queue()
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            events,
            4,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)

        with mock.patch.object(app, "write_message") as write_message:
            self.assertTrue(viewer._heartbeat_step(5.0))
            self.assertFalse(
                viewer._heartbeat_step(
                    5.0 + app.VIEWER_HEARTBEAT_TIMEOUT_SECONDS
                )
            )

        write_message.assert_called_once()
        self.assertTrue(viewer.stop_event.is_set())
        statuses = [
            value[1]
            for event, value in list(events.queue)
            if event == "viewer_status"
        ]
        self.assertTrue(any("heartbeat timed out" in text for text in statuses))

    def test_authenticated_messages_keep_slow_relay_alive_while_pong_is_queued(self):
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)
        with mock.patch.object(app, "write_message"):
            self.assertTrue(viewer._heartbeat_step(5.0))
            for now in range(10, 91, 5):
                viewer._mark_message_received(float(now))
                self.assertTrue(viewer._heartbeat_step(float(now)))
        self.assertFalse(viewer.stop_event.is_set())
        # Complete authenticated traffic proves inbound liveness, but does not
        # fabricate a Pong or clear its separate outstanding-probe state.
        self.assertEqual(5.0, viewer.awaiting_pong_since)
        self.assertTrue(viewer._liveness_step(107.9))
        self.assertFalse(viewer._liveness_step(108.0))
        viewer.sock.shutdown.assert_called_once()

    def test_unanswered_probe_still_expires_after_last_authenticated_message(self):
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)
        with mock.patch.object(app, "write_message"):
            self.assertTrue(viewer._heartbeat_step(5.0))
        viewer._mark_message_received(20.0)
        self.assertTrue(viewer._liveness_step(37.99))
        self.assertFalse(viewer._liveness_step(38.0))

    def test_late_authenticated_message_does_not_reactivate_stopped_heartbeat(self):
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        viewer._activate_heartbeat(0.0)
        viewer._deactivate_heartbeat()
        viewer._mark_message_received(50.0)
        self.assertFalse(viewer.heartbeat_active)
        self.assertEqual(0.0, viewer.last_message_received_at)

    def test_only_complete_authenticated_reads_refresh_viewer_liveness(self):
        for reads, expected in (([EOFError()], 0),
                                ([protocol.ProtocolError("invalid authentication tag")], 0),
                                ([(protocol.MESSAGE_FRAME, b"authenticated fixture"), EOFError()], 1)):
            with self.subTest(expected=expected, error=type(reads[-1]).__name__):
                viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1, allow_self_connection_for_testing=True)
                sock = mock.MagicMock()
                sock.__enter__.return_value = sock
                with (mock.patch.object(app, "find_ffmpeg", return_value=None),
                      mock.patch.object(app.socket, "create_connection", return_value=sock),
                      mock.patch.object(app, "authenticate", return_value=object()),
                      mock.patch.object(app, "configure_low_latency_socket"),
                      mock.patch.object(app, "read_message", side_effect=reads),
                      mock.patch.object(viewer, "_send_control"),
                      mock.patch.object(viewer, "_handle_frame"),
                      mock.patch.object(viewer, "_mark_message_received", wraps=viewer._mark_message_received) as mark):
                    viewer._run()
                self.assertEqual(expected, mark.call_count)

    def test_viewer_close_stops_heartbeat_without_late_writes(self) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            1,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)
        viewer.heartbeat_thread.start()

        viewer.close()

        self.assertFalse(viewer.heartbeat_thread.is_alive())
        with mock.patch.object(app, "write_message") as write_message:
            self.assertFalse(viewer._heartbeat_step(5.0))
            self.assertTrue(
                viewer._handle_heartbeat_message(protocol.MESSAGE_PING)
            )
        write_message.assert_not_called()

    def test_viewer_heartbeat_does_not_hold_state_lock_during_wire_write(
        self,
    ) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            1,
        )
        viewer.sock = mock.Mock()
        viewer.session = object()
        viewer._activate_heartbeat(0.0)
        write_entered = threading.Event()
        release_write = threading.Event()

        def block_wire_write(*_args: object) -> None:
            write_entered.set()
            self.assertTrue(release_write.wait(1.0))

        with mock.patch.object(
            app,
            "write_message",
            side_effect=block_wire_write,
        ):
            writer = threading.Thread(
                target=viewer._write_heartbeat_ping,
                args=(5.0,),
                daemon=True,
            )
            writer.start()
            self.assertTrue(write_entered.wait(1.0))

            cleanup = threading.Thread(
                target=viewer._deactivate_heartbeat,
                daemon=True,
            )
            cleanup.start()
            cleanup.join(timeout=0.25)
            self.assertFalse(
                cleanup.is_alive(),
                "receive-side cleanup must not wait for heartbeat socket I/O",
            )

            release_write.set()
            writer.join(timeout=1.0)

        self.assertFalse(writer.is_alive())

    def test_decoder_created_during_close_is_closed_without_being_published(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        backend = app.FfmpegH264DecoderBackend(
            "test-hardware",
            "Test Hardware",
            "h264",
            True,
        )
        viewer.h264_decoder_candidates = (backend,)
        viewer.ffmpeg_path = "/usr/bin/ffmpeg"
        creation_started = threading.Event()
        allow_creation = threading.Event()
        created_decoder = mock.Mock()
        result: list[object | None] = []

        def create_decoder(*_args: object) -> object:
            creation_started.set()
            self.assertTrue(allow_creation.wait(timeout=1.0))
            return created_decoder

        with mock.patch.object(
            app.H264AnnexBDecoder,
            "try_create",
            side_effect=create_decoder,
        ):
            creator = threading.Thread(
                target=lambda: result.append(viewer._get_or_create_h264_decoder())
            )
            creator.start()
            self.assertTrue(creation_started.wait(timeout=1.0))
            viewer.close()
            allow_creation.set()
            creator.join(timeout=1.0)

        self.assertFalse(creator.is_alive())
        self.assertEqual([None], result)
        self.assertIsNone(viewer._get_h264_decoder())
        created_decoder.close.assert_called_once_with()

    def test_decoder_worker_finally_takes_and_closes_owned_decoder(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        decoder = mock.Mock()
        with viewer.h264_decoder_lock:
            viewer.h264_decoder = decoder
        viewer.stop_event.set()

        viewer._decode_frames()

        self.assertIsNone(viewer._get_h264_decoder())
        decoder.close.assert_called_once_with()

    def test_viewer_close_called_from_decoder_worker_never_joins_itself(self) -> None:
        viewer = app.ViewerConnection("127.0.0.1", 56565, "1", queue.Queue(), 1)
        viewer.decoder_thread = threading.Thread(target=viewer.close)

        viewer.decoder_thread.start()
        viewer.decoder_thread.join(timeout=1.0)

        self.assertFalse(viewer.decoder_thread.is_alive())
        self.assertTrue(viewer.stop_event.is_set())

    def test_host_input_queue_preserves_release_when_reliable_queue_is_full(self) -> None:
        queued: deque[host.InputCommand] = deque(
            [
                host.InputCommand(host.INPUT_KEY_DOWN, 0, 0, 0, 65),
                host.InputCommand(host.INPUT_KEY_DOWN, 0, 0, 0, 66),
            ]
        )

        self.assertFalse(
            host.enqueue_input_command(
                queued,
                host.InputCommand(host.INPUT_KEY_DOWN, 0, 0, 0, 67),
                max_items=2,
            )
        )
        self.assertTrue(
            host.enqueue_input_command(
                queued,
                host.InputCommand(host.INPUT_KEY_UP, 0, 0, 0, 65),
                max_items=2,
            )
        )
        self.assertEqual(
            [host.INPUT_KEY_DOWN, host.INPUT_KEY_DOWN, host.INPUT_KEY_UP],
            [command.kind for command in queued],
        )

    def test_viewer_input_queue_replaces_motion_before_reliable_events(self) -> None:
        queued: deque[tuple[int, bytes]] = deque(
            [
                (app.INPUT_KEY_DOWN, b"down"),
                (app.INPUT_MOUSE_MOVE, b"old-motion"),
            ]
        )

        self.assertTrue(
            app.enqueue_input_payload(
                queued,
                (app.INPUT_MOUSE_UP, b"release"),
                max_items=2,
            )
        )
        self.assertEqual(
            [(app.INPUT_KEY_DOWN, b"down"), (app.INPUT_MOUSE_UP, b"release")],
            list(queued),
        )

    def test_linux_viewer_uses_portable_left_and_right_modifier_vks(self) -> None:
        expected = {
            "Shift_L": 0xA0,
            "Shift_R": 0xA1,
            "Control_L": 0xA2,
            "Control_R": 0xA3,
            "Alt_L": 0xA4,
            "Alt_R": 0xA5,
            "ISO_Level3_Shift": 0xA5,
        }

        for keysym, virtual_key in expected.items():
            with self.subTest(keysym=keysym):
                event = SimpleNamespace(keysym=keysym, keycode=255)
                self.assertEqual(
                    virtual_key,
                    app.tk_event_to_windows_virtual_key(event),
                )

        self.assertIsNone(
            app.tk_event_to_windows_virtual_key(
                SimpleNamespace(keysym="XF86UnmappedUnitTest", keycode=38)
            )
        )
        self.assertEqual("Shift_L", host.x11_key_name(0xA0))
        self.assertEqual("Shift_R", host.xdotool_key_name(0xA1))
        self.assertEqual("Control_L", host.x11_key_name(0xA2))
        self.assertEqual("Control_R", host.xdotool_key_name(0xA3))
        self.assertEqual("Alt_L", host.x11_key_name(0xA4))
        self.assertEqual("Alt_R", host.xdotool_key_name(0xA5))

    def test_linux_host_decodes_valid_keyboard_scan_metadata(self) -> None:
        command = host.decode_input_payload(
            struct.pack(
                "<BBiii",
                host.INPUT_KEY_DOWN,
                0,
                0x1D,
                host.REMOTE_KEYBOARD_HAS_SCAN_CODE
                | host.REMOTE_KEYBOARD_EXTENDED,
                0x11,
            )
        )

        self.assertEqual(0x1D, command.x)
        self.assertEqual(
            host.REMOTE_KEYBOARD_HAS_SCAN_CODE
            | host.REMOTE_KEYBOARD_EXTENDED,
            command.y,
        )

        invalid_metadata = (
            (0, host.REMOTE_KEYBOARD_HAS_SCAN_CODE),
            (0x1D, 0),
            (0, host.REMOTE_KEYBOARD_EXTENDED),
            (0x1D, 0x40),
        )
        for scan_code, flags in invalid_metadata:
            with self.subTest(scan_code=scan_code, flags=flags):
                with self.assertRaises(host.ProtocolError):
                    host.decode_input_payload(
                        struct.pack(
                            "<BBiii",
                            host.INPUT_KEY_DOWN,
                            0,
                            scan_code,
                            flags,
                            0x41,
                        )
                    )

    def test_linux_host_uses_physical_modifier_and_keypad_identity(self) -> None:
        has_scan = host.REMOTE_KEYBOARD_HAS_SCAN_CODE
        extended = host.REMOTE_KEYBOARD_EXTENDED

        cases = (
            (0x10, 0x2A, has_scan, "Shift_L"),
            (0x10, 0x36, has_scan, "Shift_R"),
            (0x11, 0x1D, has_scan, "Control_L"),
            (0x11, 0x1D, has_scan | extended, "Control_R"),
            (0x12, 0x38, has_scan, "Alt_L"),
            (0x12, 0x38, has_scan | extended, "Alt_R"),
            (0x0D, 0x1C, has_scan | extended, "KP_Enter"),
            (0x2D, 0x52, has_scan, "KP_Insert"),
            (0x2D, 0x52, has_scan | extended, "Insert"),
            (0xBF, 0x35, has_scan, "slash"),
            (0x6F, 0x35, has_scan | extended, "KP_Divide"),
        )
        for virtual_key, scan_code, flags, expected in cases:
            with self.subTest(expected=expected):
                self.assertEqual(
                    expected,
                    host.input_command_key_name(
                        host.InputCommand(
                            host.INPUT_KEY_DOWN,
                            0,
                            scan_code,
                            flags,
                            virtual_key,
                        )
                    ),
                )

        self.assertEqual("semicolon", host.x11_key_name(0xBA))
        self.assertEqual("backslash", host.xdotool_key_name(0xDC))
        self.assertEqual("KP_0", host.x11_key_name(0x60))
        self.assertEqual("KP_Decimal", host.xdotool_key_name(0x6E))

    def test_pointer_capability_probe_moves_one_pixel_and_restores(self) -> None:
        position = [99, 50]
        moves: list[tuple[int, int]] = []
        sync_count = 0

        def move(x: int, y: int) -> bool:
            moves.append((x, y))
            position[:] = [x, y]
            return True

        def synchronize() -> None:
            nonlocal sync_count
            sync_count += 1

        available, detail = host.verify_pointer_motion_capability(
            "test-backend",
            lambda: (position[0], position[1]),
            move,
            synchronize,
            (100, 80),
        )

        self.assertTrue(available, detail)
        self.assertEqual([(98, 50), (99, 50)], moves)
        self.assertEqual([99, 50], position)
        self.assertEqual(3, sync_count)
        self.assertIn("verified and restored", detail)

    def test_pointer_capability_probe_rejects_acknowledged_noop(self) -> None:
        position = (20, 30)
        moves: list[tuple[int, int]] = []

        available, detail = host.verify_pointer_motion_capability(
            "XTest",
            lambda: position,
            lambda x, y: moves.append((x, y)) is None,
            lambda: None,
            (100, 80),
        )

        self.assertFalse(available)
        self.assertEqual([(21, 30)], moves)
        self.assertIn("reported successful motion", detail)
        self.assertIn("synthetic input is not effective", detail)

    def test_pointer_capability_probe_does_not_overwrite_concurrent_motion(self) -> None:
        queried_positions = iter(((10, 20), (11, 20), (40, 50), (40, 50)))
        moves: list[tuple[int, int]] = []

        available, detail = host.verify_pointer_motion_capability(
            "XTest",
            lambda: next(queried_positions),
            lambda x, y: moves.append((x, y)) is None,
            lambda: None,
            (100, 80),
        )

        self.assertFalse(available)
        self.assertEqual([(11, 20)], moves)
        self.assertIn("changed concurrently to (40, 50)", detail)
        self.assertIn("left unchanged", detail)

    def test_pointer_probe_confirms_again_immediately_before_restore(self) -> None:
        queried_positions = iter(
            (
                (10, 20),
                (11, 20),
                (11, 20),
                (40, 50),
                (40, 50),
            )
        )
        moves: list[tuple[int, int]] = []

        available, detail = host.verify_pointer_motion_capability(
            "XTest",
            lambda: next(queried_positions),
            lambda x, y: moves.append((x, y)) is None,
            lambda: None,
            (100, 80),
        )

        self.assertFalse(available)
        self.assertEqual([(11, 20)], moves)
        self.assertIn("changed concurrently to (40, 50)", detail)

    def test_xdotool_capability_requires_observed_motion_and_restores(self) -> None:
        position = [10, 20]
        move_commands: list[tuple[int, int]] = []

        def run(command: list[str], **_kwargs: object) -> object:
            action = command[1]
            if action == "getdisplaygeometry":
                return SimpleNamespace(returncode=0, stdout="100 80\n", stderr="")
            if action == "getmouselocation":
                return SimpleNamespace(
                    returncode=0,
                    stdout=f"X={position[0]}\nY={position[1]}\n",
                    stderr="",
                )
            if action == "mousemove":
                point = int(command[2]), int(command[3])
                move_commands.append(point)
                position[:] = point
                return SimpleNamespace(returncode=0, stdout="", stderr="")
            raise AssertionError(command)

        with (
            mock.patch.object(host.shutil, "which", return_value="/usr/bin/xdotool"),
            mock.patch.dict(host.os.environ, {"DISPLAY": ":99"}),
            mock.patch.object(host.subprocess, "run", side_effect=run),
        ):
            available, detail = host.xdotool_input_status()

        self.assertTrue(available, detail)
        self.assertEqual([(11, 20), (10, 20)], move_commands)
        self.assertEqual([10, 20], position)

    def test_input_capability_does_not_advertise_wslg_noop(self) -> None:
        host.clear_input_capability_cache()
        try:
            with (
                mock.patch.dict(host.os.environ, {"DISPLAY": ":0"}),
                mock.patch.object(
                    host,
                    "native_x11_input_status",
                    return_value=(
                        False,
                        "XTest reported successful motion but XQueryPointer did not move",
                    ),
                ),
                mock.patch.object(
                    host,
                    "xdotool_input_status",
                    return_value=(
                        False,
                        "xdotool reported successful motion but the pointer did not move",
                    ),
                ),
            ):
                available, detail = host.probe_input_control_status(
                    force=True
                )
                capabilities = host.get_host_capabilities()
        finally:
            host.clear_input_capability_cache()

        self.assertFalse(available)
        self.assertEqual(0, capabilities & host.CAPABILITY_INPUT_CONTROL)
        self.assertIn("XQueryPointer did not move", detail)
        self.assertIn("xdotool reported successful motion", detail)

    def test_capability_reads_and_discovery_path_never_start_pointer_probe(
        self,
    ) -> None:
        host.clear_input_capability_cache()
        try:
            with (
                mock.patch.dict(host.os.environ, {"DISPLAY": ":0"}),
                mock.patch.object(
                    host,
                    "native_x11_input_status",
                ) as native_probe,
                mock.patch.object(
                    host,
                    "xdotool_input_status",
                ) as xdotool_probe,
            ):
                available, detail = host.input_control_status()
                capabilities = host.get_host_capabilities()

            self.assertFalse(available)
            self.assertIn("not been actively verified", detail)
            self.assertEqual(
                0,
                capabilities & host.CAPABILITY_INPUT_CONTROL,
            )
            native_probe.assert_not_called()
            xdotool_probe.assert_not_called()
        finally:
            host.clear_input_capability_cache()

    def test_active_pointer_probe_reuses_process_cooldown_cache(self) -> None:
        host.clear_input_capability_cache()
        try:
            with (
                mock.patch.dict(host.os.environ, {"DISPLAY": ":0"}),
                mock.patch.object(
                    host,
                    "native_x11_input_status",
                    return_value=(True, "XTest verified"),
                ) as native_probe,
                mock.patch.object(
                    host,
                    "xdotool_input_status",
                ) as xdotool_probe,
            ):
                first = host.probe_input_control_status()
                second = host.probe_input_control_status()

            self.assertEqual((True, "XTest verified"), first)
            self.assertEqual(first, second)
            native_probe.assert_called_once_with()
            xdotool_probe.assert_not_called()
        finally:
            host.clear_input_capability_cache()

    def test_session_capabilities_follow_the_retained_input_controller(self) -> None:
        self.assertEqual(
            0,
            host.get_host_capabilities(False) & host.CAPABILITY_INPUT_CONTROL,
        )
        self.assertNotEqual(
            0,
            host.get_host_capabilities(True) & host.CAPABILITY_INPUT_CONTROL,
        )

    def test_linux_host_pressed_state_releases_physical_inputs_in_reverse(self) -> None:
        has_scan = host.REMOTE_KEYBOARD_HAS_SCAN_CODE
        extended = host.REMOTE_KEYBOARD_EXTENDED
        left_control = host.InputCommand(
            host.INPUT_KEY_DOWN,
            0,
            0x1D,
            has_scan,
            0x11,
        )
        right_control = host.InputCommand(
            host.INPUT_KEY_DOWN,
            0,
            0x1D,
            has_scan | extended,
            0x11,
        )
        letter_a = host.InputCommand(
            host.INPUT_KEY_DOWN,
            0,
            0x1E,
            has_scan,
            0x41,
        )
        state = host.HostPressedInputState()
        state.observe(left_control)
        state.observe(right_control)
        state.observe(
            host.InputCommand(
                host.INPUT_MOUSE_DOWN,
                host.MOUSE_LEFT,
                10,
                20,
                0,
            )
        )
        state.observe(
            host.InputCommand(
                host.INPUT_MOUSE_MOVE,
                0,
                30,
                40,
                0,
            )
        )
        state.observe(letter_a)
        state.observe(letter_a)
        state.observe(
            host.InputCommand(
                host.INPUT_KEY_UP,
                0,
                left_control.x,
                left_control.y,
                left_control.data,
            )
        )

        releases = state.take_release_commands()

        self.assertEqual(
            (
                host.InputCommand(
                    host.INPUT_KEY_UP,
                    0,
                    letter_a.x,
                    letter_a.y,
                    letter_a.data,
                ),
                host.InputCommand(
                    host.INPUT_MOUSE_UP,
                    host.MOUSE_LEFT,
                    30,
                    40,
                    0,
                ),
                host.InputCommand(
                    host.INPUT_KEY_UP,
                    0,
                    right_control.x,
                    right_control.y,
                    right_control.data,
                ),
            ),
            releases,
        )
        self.assertEqual(0, state.pressed_key_count)
        self.assertEqual(0, state.pressed_mouse_button_count)
        self.assertEqual((), state.take_release_commands())

    def test_linux_host_input_worker_tracks_only_successful_injection(self) -> None:
        command = host.InputCommand(
            host.INPUT_KEY_DOWN,
            0,
            0,
            0,
            0x41,
        )

        for applied, expected_count in ((True, 1), (False, 0)):
            with self.subTest(applied=applied):
                session = host.LinuxHostSession.__new__(
                    host.LinuxHostSession
                )
                session.input_condition = threading.Condition()
                session.input_queue = deque([command])
                session.input_stop = True
                session.input_controller = mock.Mock()
                session.input_controller.apply.return_value = applied
                session.input_state = host.HostPressedInputState()
                session.input_unavailable_reported = False
                session._safe_status = mock.Mock()
                session.geometry_lock = threading.Lock()
                session.last_frame_width = 1920
                session.last_frame_height = 1080
                session.display_size = (1920, 1080)

                session._input_loop()

                self.assertEqual(
                    expected_count,
                    session.input_state.pressed_key_count,
                )

    def test_linux_host_stop_releases_held_inputs_before_controller_close(self) -> None:
        has_scan = host.REMOTE_KEYBOARD_HAS_SCAN_CODE
        extended = host.REMOTE_KEYBOARD_EXTENDED
        session = host.LinuxHostSession.__new__(
            host.LinuxHostSession
        )
        session.input_condition = threading.Condition()
        session.input_queue = deque(
            [
                host.InputCommand(
                    host.INPUT_KEY_DOWN,
                    0,
                    0,
                    0,
                    0x42,
                )
            ]
        )
        session.input_stop = False
        session.input_thread = None
        session.input_controller = mock.Mock()
        session.input_controller.apply.return_value = True
        session.input_state = host.HostPressedInputState()
        session.input_state.observe(
            host.InputCommand(
                host.INPUT_KEY_DOWN,
                0,
                0x1D,
                has_scan | extended,
                0x11,
            )
        )
        session.input_state.observe(
            host.InputCommand(
                host.INPUT_MOUSE_DOWN,
                host.MOUSE_LEFT,
                120,
                80,
                0,
            )
        )
        session.geometry_lock = threading.Lock()
        session.last_frame_width = 1920
        session.last_frame_height = 1080
        session.display_size = (1920, 1080)

        session._stop_input_thread()

        released_commands = [
            call.args[0]
            for call in session.input_controller.apply.call_args_list
        ]
        self.assertEqual(
            [
                host.InputCommand(
                    host.INPUT_MOUSE_UP,
                    host.MOUSE_LEFT,
                    120,
                    80,
                    0,
                ),
                host.InputCommand(
                    host.INPUT_KEY_UP,
                    0,
                    0x1D,
                    has_scan | extended,
                    0x11,
                ),
            ],
            released_commands,
        )
        self.assertEqual([], list(session.input_queue))
        session.input_controller.close.assert_called_once_with()

    def test_viewer_pressed_key_state_releases_in_reverse_press_order(self) -> None:
        state = app.ViewerPressedKeyState()
        state.observe_down(0xA2)
        state.observe_down(0x43)
        state.observe_down(0xA2)

        self.assertEqual(2, state.count)
        self.assertEqual((0x43, 0xA2), state.keys_in_release_order())

        state.observe_up(0x43)
        self.assertEqual((0xA2,), state.keys_in_release_order())
        state.clear()
        self.assertEqual(0, state.count)

    def test_linux_viewer_disconnect_release_flushes_keyups_in_reverse_order(
        self,
    ) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.viewer_pressed_keys = app.ViewerPressedKeyState()
        controller.viewer_pressed_keys.observe_down(0xA2)
        controller.viewer_pressed_keys.observe_down(0x43)
        controller._set_viewer_status = mock.Mock()
        controller.viewer = mock.Mock()
        controller.viewer.remote_capabilities = app.CAPABILITY_INPUT_CONTROL
        controller.viewer.send_input.return_value = True

        released = controller._release_pressed_viewer_keys(flush=True)

        self.assertEqual(2, released)
        self.assertEqual(0, controller.viewer_pressed_keys.count)
        self.assertEqual(
            [
                mock.call(app.INPUT_KEY_UP, data=0x43),
                mock.call(app.INPUT_KEY_UP, data=0xA2),
            ],
            controller.viewer.send_input.call_args_list,
        )
        controller.viewer.flush_pending_inputs.assert_called_once_with()

    def test_viewer_input_flush_waits_for_active_wire_write(self) -> None:
        viewer = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "1",
            queue.Queue(),
            1,
        )
        viewer.sock = mock.sentinel.sock
        viewer.session = mock.sentinel.session
        write_started = threading.Event()
        allow_write = threading.Event()
        flush_finished = threading.Event()
        flush_result: list[bool] = []

        def blocked_write(*_args: object) -> None:
            write_started.set()
            self.assertTrue(allow_write.wait(timeout=1.0))

        def flush() -> None:
            flush_result.append(viewer.flush_pending_inputs(timeout_seconds=1.0))
            flush_finished.set()

        with mock.patch.object(app, "write_message", side_effect=blocked_write):
            viewer.input_thread.start()
            self.assertTrue(viewer.send_input(app.INPUT_KEY_UP, data=0xA2))
            self.assertTrue(write_started.wait(timeout=1.0))
            flush_thread = threading.Thread(target=flush)
            flush_thread.start()

            self.assertFalse(flush_finished.wait(timeout=0.02))
            allow_write.set()
            self.assertTrue(flush_finished.wait(timeout=1.0))
            flush_thread.join(timeout=1.0)

            viewer.stop_event.set()
            with viewer.input_condition:
                viewer.input_condition.notify_all()
            viewer.input_thread.join(timeout=1.0)

        self.assertEqual([True], flush_result)
        self.assertFalse(viewer.input_thread.is_alive())

    def test_folder_preview_runs_off_ui_thread_and_is_single_flight(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.events = queue.Queue()
        controller.viewer_generation = 61
        controller.viewer_file_preview_token = 0
        controller.viewer_file_preview_active = False
        controller.viewer_file_preview_cancel_event = None
        controller.viewer = mock.Mock()
        controller.viewer.generation = 61
        controller.viewer.remote_capabilities = app.CAPABILITY_FILE_RECEIVE
        controller._set_viewer_file_action_state = mock.Mock()
        controller._set_viewer_status = mock.Mock()
        preview_started = threading.Event()
        allow_preview = threading.Event()

        def blocked_preview(
            _paths: list[str],
            cancel_event: threading.Event | None = None,
        ) -> tuple[list[str], list[app.FileTransferPreviewItem], str]:
            preview_started.set()
            self.assertTrue(allow_preview.wait(timeout=1.0))
            if cancel_event is not None and cancel_event.is_set():
                raise app.TransferCancelledError("cancelled")
            return ["/large"], [], ""

        with mock.patch.object(
            app,
            "create_file_transfer_preview",
            side_effect=blocked_preview,
        ):
            started_at = time.monotonic()
            self.assertTrue(
                controller._begin_viewer_file_transfer_preview(
                    ["/large"],
                    "title",
                    "action",
                    "queued",
                )
            )
            self.assertLess(time.monotonic() - started_at, 0.1)
            self.assertTrue(preview_started.wait(timeout=0.5))
            self.assertFalse(
                controller._begin_viewer_file_transfer_preview(
                    ["/second"],
                    "title",
                    "action",
                    "queued",
                )
            )
            allow_preview.set()
            self.assertTrue(
                self._wait_until(
                    lambda: not controller.events.empty(),
                    timeout=1.0,
                )
            )

        self.assertTrue(controller.viewer_file_preview_active)
        controller._set_viewer_file_action_state.assert_called_once_with(False)

    def test_file_preview_old_generation_and_close_are_fenced(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        old_viewer = mock.Mock()
        old_viewer.generation = 71
        controller.viewer = mock.Mock()
        controller.viewer.generation = 72
        controller.viewer_generation = 72
        controller.viewer_file_preview_token = 9
        controller.viewer_file_preview_active = True
        controller.viewer_file_preview_cancel_event = threading.Event()
        controller.closing = False
        controller._show_file_transfer_confirmation_dialog = mock.Mock()
        controller._set_viewer_file_action_state = mock.Mock()
        controller._set_viewer_status = mock.Mock()
        result = app.ViewerFilePreviewResult(
            9,
            old_viewer,
            ("/old",),
            (
                app.FileTransferPreviewItem(
                    "文件",
                    "/old",
                    "old",
                    1,
                    "remote/old",
                ),
            ),
            "",
            "title",
            "action",
            "queued",
        )

        controller._finish_viewer_file_transfer_preview(result)
        controller._show_file_transfer_confirmation_dialog.assert_not_called()
        old_viewer.send_files.assert_not_called()

        cancel_event = controller.viewer_file_preview_cancel_event
        controller._cancel_viewer_file_preview()
        self.assertTrue(cancel_event.is_set())
        self.assertFalse(controller.viewer_file_preview_active)

    def test_file_preview_success_sends_only_owned_viewer_and_reenables_actions(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        viewer = mock.Mock()
        viewer.generation = 73
        viewer.remote_capabilities = app.CAPABILITY_FILE_RECEIVE
        viewer.send_files.return_value = True
        controller.viewer = viewer
        controller.viewer_generation = 73
        controller.viewer_file_preview_token = 11
        controller.viewer_file_preview_active = True
        controller.viewer_file_preview_cancel_event = threading.Event()
        controller.viewer_window = mock.Mock()
        controller.root = mock.Mock()
        controller.closing = False
        controller._show_file_transfer_confirmation_dialog = mock.Mock(return_value=True)
        controller._set_viewer_file_action_state = mock.Mock()
        controller._set_viewer_status = mock.Mock()
        result = app.ViewerFilePreviewResult(
            11,
            viewer,
            ("/owned",),
            (
                app.FileTransferPreviewItem(
                    "文件",
                    "/owned",
                    "owned",
                    1,
                    "remote/owned",
                ),
            ),
            "",
            "title",
            "action",
            "queued",
        )

        controller._finish_viewer_file_transfer_preview(result)

        viewer.send_files.assert_called_once_with(["/owned"])
        controller._set_viewer_status.assert_called_with("queued")
        self.assertFalse(controller.viewer_file_preview_active)
        self.assertIsNone(controller.viewer_file_preview_cancel_event)
        controller._set_viewer_file_action_state.assert_called_once_with(True)

    def test_file_actions_stay_disabled_during_background_preview(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.viewer_file_preview_active = True
        controller.closing = False
        controller.send_file_button = mock.Mock()
        controller.send_folder_button = mock.Mock()
        controller.viewer_window_send_file_button = mock.Mock()
        controller.viewer_window_send_folder_button = mock.Mock()

        controller._set_viewer_file_action_state(True)

        for button in (
            controller.send_file_button,
            controller.send_folder_button,
            controller.viewer_window_send_file_button,
            controller.viewer_window_send_folder_button,
        ):
            button.config.assert_called_once_with(state=app.tk.DISABLED)

    def test_mouse_down_is_tracked_only_after_queue_acceptance_and_released(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller.viewer_pressed_keys = app.ViewerPressedKeyState()
        controller.viewer = mock.Mock()
        controller.viewer.remote_capabilities = app.CAPABILITY_INPUT_CONTROL
        controller.viewer.send_input.side_effect = [False, True, True]
        controller._pointer_event_to_remote = mock.Mock(return_value=(120, 80))
        controller._set_viewer_status = mock.Mock()
        event = SimpleNamespace()

        self.assertFalse(
            controller._send_pointer_input(
                app.INPUT_MOUSE_DOWN,
                app.MOUSE_LEFT,
                event,
            )
        )
        self.assertEqual(0, controller.viewer_pressed_keys.mouse_button_count)
        self.assertTrue(
            controller._send_pointer_input(
                app.INPUT_MOUSE_DOWN,
                app.MOUSE_LEFT,
                event,
            )
        )
        self.assertEqual(1, controller.viewer_pressed_keys.mouse_button_count)

        released = controller._release_pressed_viewer_inputs()

        self.assertEqual(1, released)
        self.assertEqual(0, controller.viewer_pressed_keys.mouse_button_count)
        self.assertEqual(
            mock.call(
                app.INPUT_MOUSE_UP,
                button=app.MOUSE_LEFT,
                x=120,
                y=80,
            ),
            controller.viewer.send_input.call_args_list[-1],
        )

    def test_focus_loss_flushes_input_release_off_ui_thread(self) -> None:
        controller = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        controller._release_pressed_viewer_inputs = mock.Mock(return_value=1)

        controller._viewer_frame_focus_out(SimpleNamespace())

        controller._release_pressed_viewer_inputs.assert_called_once_with(
            flush=True,
            background_flush=True,
        )

    def test_disconnect_flushes_releases_before_transport_close(self) -> None:
        viewer = mock.Mock()
        calls: list[str] = []
        viewer.flush_pending_inputs.side_effect = lambda: calls.append("flush")
        viewer.request_close.side_effect = lambda: calls.append("request-close")
        viewer.close.side_effect = lambda: calls.append("close")

        app.RemoteDeskLinuxApp._close_viewer_after_input_release(viewer, 1)

        self.assertEqual(["flush", "request-close", "close"], calls)

    def test_h264_overflow_drops_orphaned_prediction_frames(self) -> None:
        frames = deque(self._frame(app.FRAME_FLAG_KEY_FRAME if index == 0 else 0) for index in range(5))

        request_recovery = app.enqueue_h264_frame(
            frames,
            self._frame(0),
            max_queued_frames=4,
        )

        self.assertTrue(request_recovery)
        self.assertEqual([], list(frames))

    def test_h264_overflow_starts_at_latest_recovery_frame(self) -> None:
        frames = [self._frame(0) for _ in range(5)]
        frames[3] = self._frame(app.FRAME_FLAG_KEY_FRAME | app.FRAME_FLAG_CODEC_CONFIG)

        start_index, recoverable = app.find_h264_queue_start_index(
            frames,
            max_queued_frames=4,
        )

        self.assertEqual(3, start_index)
        self.assertTrue(recoverable)
        self.assertEqual(4, app.MAX_QUEUED_H264_FRAMES)

    @staticmethod
    def _frame(flags: int) -> tuple[int, int, int, int, int, bytes, float]:
        return (
            0,
            app.FRAME_ENCODING_H264_ANNEX_B,
            1280,
            720,
            flags,
            b"\x00\x00\x00\x01\x09",
            time.monotonic(),
        )


if __name__ == "__main__":
    unittest.main()
