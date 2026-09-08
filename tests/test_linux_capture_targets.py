from __future__ import annotations

import queue
import sys
import threading
import unittest
from pathlib import Path
from unittest import mock


LINUX_SCRIPTS = Path(__file__).resolve().parents[1] / "scripts" / "linux"
sys.path.insert(0, str(LINUX_SCRIPTS))

import remotedesk_linux_app as app  # noqa: E402
import remotedesk_protocol_probe as protocol  # noqa: E402


TARGET_A = app.ViewerCaptureTarget("display-a", "屏幕 A")
TARGET_B = app.ViewerCaptureTarget("display-b", "屏幕 B")


def snapshot(
    targets: tuple[app.ViewerCaptureTarget, ...],
    selected: app.ViewerCaptureTarget | None,
) -> app.ViewerCaptureTargetSnapshot:
    return app.ViewerCaptureTargetSnapshot(True, targets, selected)


class ViewerCaptureTargetStateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.state = app.ViewerCaptureTargetState()
        self.state.begin_logical_session()
        self.state.begin_generation(10)

    def test_selector_requires_device_info_and_target_list(self) -> None:
        initial = self.state.observe_device_info(10)
        self.assertFalse(initial.visible)
        self.assertFalse(initial.enabled)

        listed = self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        self.assertTrue(listed.visible)
        self.assertTrue(listed.enabled)
        self.assertEqual("display-a", listed.selected_target_id)

    def test_single_screen_and_legacy_host_do_not_show_selector(self) -> None:
        self.state.observe_device_info(10)
        one = self.state.observe_snapshot(
            10,
            snapshot((TARGET_A,), TARGET_A),
        )
        self.assertFalse(one.visible)
        self.assertFalse(one.enabled)

        legacy = app.ViewerCaptureTargetState()
        legacy.begin_logical_session()
        legacy.begin_generation(11)
        legacy.observe_device_info(11)
        self.assertFalse(legacy._transition().visible)
        self.assertFalse(legacy._transition().enabled)

    def test_user_choice_is_sent_once_and_changed_is_authoritative_display(self) -> None:
        self.state.observe_device_info(10)
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )

        selected = self.state.choose_target(10, "display-b")
        duplicate = self.state.choose_target(10, "display-b")
        unchanged = self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        confirmed = self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_B),
        )

        self.assertEqual("display-b", selected.select_target_id)
        self.assertIsNone(duplicate.select_target_id)
        self.assertIsNone(unchanged.select_target_id)
        self.assertEqual("display-a", unchanged.selected_target_id)
        self.assertIsNone(confirmed.select_target_id)
        self.assertEqual("display-b", confirmed.selected_target_id)

    def test_rapid_choice_back_to_current_target_supersedes_queued_request(self) -> None:
        self.state.observe_device_info(10)
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )

        toward_b = self.state.choose_target(10, "display-b")
        back_to_a = self.state.choose_target(10, "display-a")

        self.assertEqual("display-b", toward_b.select_target_id)
        self.assertEqual("display-a", back_to_a.select_target_id)

    def test_reconnect_preserves_desired_target_and_reselects_once(self) -> None:
        self.state.observe_device_info(10)
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        self.state.choose_target(10, "display-b")
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_B),
        )

        disconnected = self.state.begin_generation(11)
        before_list = self.state.observe_device_info(11)
        reselect = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        repeated = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )

        self.assertFalse(disconnected.enabled)
        self.assertIsNone(before_list.select_target_id)
        self.assertEqual("display-b", reselect.select_target_id)
        self.assertIsNone(repeated.select_target_id)

    def test_missing_desired_target_is_explicit_and_never_auto_replaced(self) -> None:
        self.state.observe_device_info(10)
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        self.state.choose_target(10, "display-b")
        self.state.observe_snapshot(
            10,
            snapshot((TARGET_A, TARGET_B), TARGET_B),
        )
        self.state.begin_generation(11)
        self.state.observe_device_info(11)

        missing = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A,), TARGET_A),
        )
        still_missing = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A,), TARGET_A),
        )
        returned = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )

        self.assertTrue(missing.visible)
        self.assertTrue(missing.enabled)
        self.assertEqual("display-b", missing.selected_target_id)
        self.assertIn("不可用", missing.status or "")
        self.assertIsNone(missing.select_target_id)
        self.assertIsNone(still_missing.select_target_id)
        self.assertEqual("display-b", returned.select_target_id)

    def test_old_generation_metadata_and_choice_cannot_mutate_new_state(self) -> None:
        self.state.begin_generation(11)
        self.state.observe_device_info(11)
        current = self.state.observe_snapshot(
            11,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        stale = self.state.observe_snapshot(
            10,
            snapshot((TARGET_B,), TARGET_B),
        )
        stale_choice = self.state.choose_target(10, "display-b")

        self.assertEqual(current.choices, stale.choices)
        self.assertEqual("display-a", stale.selected_target_id)
        self.assertIsNone(stale_choice.select_target_id)
        self.assertIsNone(self.state.desired_target_id)

    def test_duplicate_display_names_have_stable_unambiguous_labels(self) -> None:
        choices = app.build_viewer_capture_target_choices(
            (
                app.ViewerCaptureTarget("one", "屏幕"),
                app.ViewerCaptureTarget("two", "屏幕"),
                app.ViewerCaptureTarget("two", "重复项"),
            ),
            None,
            "",
        )

        self.assertEqual(("屏幕 — one", "屏幕 — two"), tuple(item.label for item in choices))


class ViewerCaptureTargetProtocolTests(unittest.TestCase):
    def test_session_rejection_surfaces_authenticated_busy_reason(self) -> None:
        connection = app.ViewerConnection(
            "127.0.0.1",
            56565,
            "pw",
            queue.Queue(),
            6,
        )

        with self.assertRaisesRegex(
            ConnectionRefusedError,
            "another viewer is active",
        ):
            connection._handle_control(
                protocol.encode_session_rejected(
                    "another viewer is active"
                )
            )

    def test_connection_publishes_cumulative_list_and_changed_snapshot(self) -> None:
        events: queue.Queue[tuple[str, object]] = queue.Queue()
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", events, 7)

        connection._handle_control(
            protocol.encode_capture_target_list(
                [("display-a", "屏幕 A"), ("display-b", "屏幕 B")]
            )
        )
        connection._handle_control(
            protocol.encode_capture_target_changed("display-b", "屏幕 B")
        )

        event, fenced = events.get_nowait()
        self.assertEqual("viewer_capture_metadata", event)
        generation, metadata = fenced
        self.assertEqual(7, generation)
        self.assertEqual((TARGET_A, TARGET_B), metadata.targets)
        self.assertEqual(TARGET_B, metadata.selected_target)

    def test_changed_before_list_still_publishes_one_complete_latest_snapshot(self) -> None:
        events: queue.Queue[tuple[str, object]] = queue.Queue()
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", events, 8)

        connection._handle_control(
            protocol.encode_capture_target_changed("display-b", "屏幕 B")
        )
        connection._handle_control(
            protocol.encode_capture_target_list(
                [("display-a", "屏幕 A"), ("display-b", "屏幕 B")]
            )
        )

        event, fenced = events.get_nowait()
        self.assertEqual("viewer_capture_metadata", event)
        generation, metadata = fenced
        self.assertEqual(8, generation)
        self.assertTrue(metadata.list_received)
        self.assertEqual((TARGET_A, TARGET_B), metadata.targets)
        self.assertEqual(TARGET_B, metadata.selected_target)
        self.assertTrue(events.empty())

    def test_latest_only_sender_writes_existing_select_control(self) -> None:
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", queue.Queue(), 7)
        connection.sock = mock.Mock()
        connection.session = object()
        sent = threading.Event()
        payloads: list[bytes] = []

        def capture_write(
            _sock: object,
            _session: object,
            message_type: int,
            payload: bytes,
        ) -> None:
            self.assertEqual(app.MESSAGE_CONTROL, message_type)
            payloads.append(payload)
            sent.set()

        with mock.patch.object(app, "write_message", side_effect=capture_write):
            connection.capture_target_thread.start()
            self.assertTrue(connection.queue_capture_target_selection("display-b"))
            self.assertTrue(sent.wait(timeout=1.0))
            connection.request_close()
            connection.capture_target_thread.join(timeout=1.0)

        self.assertFalse(connection.capture_target_thread.is_alive())
        self.assertEqual(
            "display-b",
            protocol.decode_control(payloads[0])["targetId"],
        )

    def test_mailbox_cancels_intermediate_choice_when_latest_matches_active(self) -> None:
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", queue.Queue(), 7)
        connection.sock = object()
        connection.session = object()
        connection.active_capture_target_id = "display-a"

        self.assertTrue(connection.queue_capture_target_selection("display-b"))
        self.assertEqual("display-b", connection.pending_capture_target_id)
        self.assertTrue(connection.queue_capture_target_selection("display-a"))
        self.assertIsNone(connection.pending_capture_target_id)

    def test_close_interrupts_sender_waiting_behind_blocked_writer_without_waiting(self) -> None:
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", queue.Queue(), 7)
        connection.sock = mock.Mock()
        connection.session = object()
        connection.write_lock.acquire()
        connection.capture_target_thread.start()
        self.assertTrue(connection.queue_capture_target_selection("display-b"))

        closer = threading.Thread(target=connection.request_close)
        closer.start()
        closer.join(timeout=0.2)
        self.assertFalse(closer.is_alive())
        connection.sock.shutdown.assert_called_once()
        connection.sock.close.assert_called_once()

        connection.write_lock.release()
        connection.capture_target_thread.join(timeout=1.0)
        self.assertFalse(connection.capture_target_thread.is_alive())


class ViewerCaptureTargetUiBindingTests(unittest.TestCase):
    def test_user_selection_queues_once_and_programmatic_update_never_recurses(self) -> None:
        application = app.RemoteDeskLinuxApp.__new__(app.RemoteDeskLinuxApp)
        application.viewer_generation = 4
        application.viewer_capture_target_state = app.ViewerCaptureTargetState()
        application.viewer_capture_target_state.begin_logical_session()
        application.viewer_capture_target_state.begin_generation(4)
        application.viewer_capture_target_state.observe_device_info(4)
        transition = application.viewer_capture_target_state.observe_snapshot(
            4,
            snapshot((TARGET_A, TARGET_B), TARGET_A),
        )
        application.viewer_target_choices = transition.choices
        application.viewer_target_selector = mock.Mock()
        application.viewer_target_selector.current.return_value = 1
        application.viewer_target_bar = None
        application.viewer_target_controls_anchor = None
        application.viewer_target_value = mock.Mock()
        application.viewer_target_programmatic_update = False
        application.viewer = mock.Mock()
        application.viewer.generation = 4
        application.viewer.queue_capture_target_selection.return_value = True
        application._set_viewer_status = mock.Mock()

        application._viewer_capture_target_selected(mock.Mock())
        application.viewer_target_programmatic_update = True
        application._viewer_capture_target_selected(mock.Mock())

        application.viewer.queue_capture_target_selection.assert_called_once_with(
            "display-b"
        )


class CaptureTargetStatusCompatibilityTests(unittest.TestCase):
    def test_exact_final_machine_trailer_is_removed(self) -> None:
        message = (
            "捕获目标暂不可用：屏幕 2。\n"
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|"
            "ZGlzcGxheS0y|5bGP5bmVIDI=|17"
        )

        self.assertEqual(
            "捕获目标暂不可用：屏幕 2。",
            app.strip_capture_target_status_trailer(message),
        )

    def test_similar_middle_duplicate_and_invalid_trailers_are_preserved(self) -> None:
        messages = (
            "RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|1",
            "正文里提到 RemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|1",
            "正文\nRemoteDesk.CaptureTargetStatus/v10|unavailable|QQ==|Qg==|1",
            "正文\nRemoteDesk.CaptureTargetStatus/v1|unavailable-ish|QQ==|Qg==|1",
            "正文\nRemoteDesk.CaptureTargetStatus/v1|unavailable|AAA|Qg==|1",
            "正文\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|1\n尾随正文",
            (
                "正文\nRemoteDesk.CaptureTargetStatus/v1|unavailable|QQ==|Qg==|1"
                "\nRemoteDesk.CaptureTargetStatus/v1|available|QQ==|Qg==|2"
            ),
        )

        for message in messages:
            with self.subTest(message=message):
                self.assertEqual(
                    message,
                    app.strip_capture_target_status_trailer(message),
                )

    def test_clipboard_status_handler_keeps_only_human_readable_body(self) -> None:
        events: queue.Queue[tuple[str, object]] = queue.Queue()
        connection = app.ViewerConnection("127.0.0.1", 56565, "pw", events, 9)
        wire_message = (
            "捕获目标已恢复：屏幕 2。\n"
            "RemoteDesk.CaptureTargetStatus/v1|available|"
            "ZGlzcGxheS0y|5bGP5bmVIDI=|18"
        )

        connection._handle_control(
            protocol.encode_clipboard_status(True, wire_message)
        )

        event, fenced = events.get_nowait()
        self.assertEqual("viewer_status", event)
        self.assertEqual((9, "捕获目标已恢复：屏幕 2。"), fenced)


if __name__ == "__main__":
    unittest.main()
