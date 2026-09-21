import asyncio
from dataclasses import replace
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
import remotedesk_linux_relay as relay

WIFI = relay.RelayNetworkPath("wlan0", 3, "10.16.169.189", "8.138.5.232", "gateway")
WIRED = relay.RelayNetworkPath("eth0", 18, "10.7.9.79", "8.138.5.232", "gateway")
OPTIONS = relay.RelayOptions("8.138.5.232", 56567, "a" * 64, "b" * 64,
                             "4d623300-3405-4513-99c0-ef37df5d1f21")


class RelayPathTests(unittest.IsolatedAsyncioTestCase):
    async def test_fast_verified_path_wins_and_late_success_is_closed(self):
        release = asyncio.Event()
        loser, winner = (None, mock.Mock()), (None, mock.Mock())
        async def connect(options, path):
            if path is None:
                try:
                    await release.wait()
                except asyncio.CancelledError:
                    return loser
            return winner
        path, result = await relay.RelayNetworkPathSelector.race(OPTIONS, [None, WIFI], connect)
        self.assertEqual(WIFI, path)
        self.assertIs(winner, result)
        await asyncio.sleep(.01)
        loser[1].transport.abort.assert_called_once()
        winner[1].transport.abort.assert_not_called()

    async def test_wrong_certificate_cannot_win(self):
        async def connect(options, path):
            if path is not None:
                raise relay.RelayIdentityError("pin")
            await asyncio.sleep(.01)
            return None, mock.Mock()
        path, _ = await relay.RelayNetworkPathSelector.race(OPTIONS, [WIFI, None], connect)
        self.assertIsNone(path)

    async def test_all_failures_preserve_identity_error(self):
        async def connect(options, path):
            raise relay.RelayIdentityError("pin") if path else OSError("network")
        with self.assertRaises(relay.RelayIdentityError):
            await relay.RelayNetworkPathSelector.race(OPTIONS, [None, WIFI], connect)

    async def test_deadline_does_not_mask_completed_identity_rejection(self):
        rejected = asyncio.Event()
        async def connect(options, path):
            if path is not None:
                # Expire only after the identity rejection actually happened.
                # A 50 ms wall-clock budget could expire during discovery when
                # the full cross-platform build saturated the test machine.
                rejected.set()
                raise relay.RelayIdentityError("pin")
            await asyncio.Event().wait()
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(return_value=[WIFI]), connect)
        original_connect = selector.connect
        async def expire_after_rejection(options):
            task = asyncio.create_task(original_connect(options))
            try:
                await rejected.wait()
                return await asyncio.wait_for(task, 0)
            finally:
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
        selector.connect = expire_after_rejection
        with mock.patch.object(relay, "_relay_path_selector", selector):
            with self.assertRaises(relay.RelayIdentityError):
                await relay.list_devices_async(OPTIONS)

    async def test_explicit_cancel_with_identity_rejection_remains_cancelled(self):
        rejected = asyncio.Event()
        async def connect(options, path):
            if path is not None:
                rejected.set()
                raise relay.RelayIdentityError("pin")
            await asyncio.Event().wait()
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(return_value=[WIFI]), connect)
        with mock.patch.object(relay, "_relay_path_selector", selector):
            task = asyncio.create_task(relay.list_devices_async(OPTIONS))
            await rejected.wait()
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task

    async def test_deadline_without_identity_failure_remains_timeout(self):
        async def connect(options, path):
            await asyncio.Event().wait()
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(return_value=[WIFI]), connect)
        with mock.patch.object(relay, "_relay_path_selector", selector), mock.patch.object(relay, "TIMEOUT", .05):
            with self.assertRaises(TimeoutError):
                await relay.list_devices_async(OPTIONS)

    async def test_primary_failure_does_not_wait_for_head_start(self):
        async def connect(options, path):
            if path:
                raise PermissionError("SO_BINDTODEVICE unavailable")
            return None, mock.Mock()
        path, _ = await asyncio.wait_for(
            relay.RelayNetworkPathSelector.race(OPTIONS, [WIFI, None], connect, 60), 1)
        self.assertIsNone(path)

    async def test_cancellation_releases_all_tasks(self):
        stopped = []
        async def connect(options, path):
            try:
                await asyncio.Event().wait()
            finally:
                stopped.append(path)
        task = asyncio.create_task(relay.RelayNetworkPathSelector.race(OPTIONS, [None, WIFI], connect))
        await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        await asyncio.sleep(.01)
        self.assertEqual(2, len(stopped))

    async def test_healthy_preference_survives_minute_but_expires_after_ten_minutes_idle(self):
        clock, calls = [0], []
        async def paths(options):
            return [WIFI, WIRED]
        async def connect(options, path):
            calls.append(path)
            if path != WIFI:
                await asyncio.Event().wait()
            return None, mock.Mock()
        selector = relay.RelayNetworkPathSelector(paths, connect, lambda: clock[0])
        await selector.connect(OPTIONS)
        await asyncio.sleep(.01)
        self.assertEqual(3, len(calls))
        calls.clear()
        clock[0] = 30
        await selector.connect(OPTIONS)
        await asyncio.sleep(.01)
        self.assertEqual([WIFI], calls)
        calls.clear()
        clock[0] = 61
        await selector.connect(OPTIONS)
        await asyncio.sleep(.01)
        self.assertEqual([WIFI], calls)
        calls.clear()
        clock[0] = 661
        await selector.connect(OPTIONS)
        self.assertEqual(3, len(calls))

    async def test_address_change_invalidates_preference(self):
        current, calls = [[WIFI, WIRED]], []
        async def paths(options):
            return current[0]
        async def connect(options, path):
            calls.append(path)
            if path is None or path.interface != "wlan0":
                await asyncio.Event().wait()
            return None, mock.Mock()
        selector = relay.RelayNetworkPathSelector(paths, connect)
        await selector.connect(OPTIONS)
        await asyncio.sleep(.01)
        calls.clear()
        current[0] = [replace(WIFI, local_address="10.16.169.190"), WIRED]
        await selector.connect(OPTIONS)
        self.assertEqual(3, len(calls))

    async def test_foreground_failure_bypasses_preference_hold(self):
        phase, calls = [0], []
        async def connect(options, path):
            calls.append(path)
            if phase[0] == 0 and path != WIFI:
                await asyncio.Event().wait()
            if phase[0] == 1:
                if path == WIFI:
                    raise OSError("offline")
                if path != WIRED:
                    await asyncio.Event().wait()
            return None, mock.Mock()
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(return_value=[WIFI, WIRED]), connect)
        await selector.connect(OPTIONS)
        phase[0] = 1
        await selector.connect(OPTIONS)
        calls.clear()
        phase[0] = 2
        await selector.connect(OPTIONS)
        self.assertEqual([WIRED], calls)

    async def test_probe_round_closes_only_owned_connections(self):
        live, measured = (None, mock.Mock()), []
        async def attempt(options, path):
            connection = None, mock.Mock()
            measured.append(connection)
            return connection
        await relay.RelayNetworkPathSelector().probe_round(relay.RelayPathStability(), OPTIONS, [WIFI, WIRED], attempt)
        self.assertEqual(2, len(measured))
        for connection in measured:
            connection[1].transport.abort.assert_called_once()
        live[1].transport.abort.assert_not_called()

    async def test_probe_timeout_retains_ownership_of_late_native_result(self):
        expired, release = asyncio.Event(), asyncio.Event()
        late = None, mock.Mock()
        async def attempt(options, path):
            try:
                await release.wait()
            except asyncio.CancelledError:
                expired.set()
                await release.wait()
            return late
        task = asyncio.create_task(relay.RelayNetworkPathSelector().probe_round(
            relay.RelayPathStability(), OPTIONS, [WIFI], attempt, timeout=.03))
        try:
            await asyncio.wait_for(expired.wait(), 2)
            self.assertFalse(task.done())
        finally:
            release.set()
        await asyncio.wait_for(task, 2)
        late[1].transport.abort.assert_called_once()

    async def test_private_and_ipv6_targets_do_not_probe_interfaces(self):
        with mock.patch.object(relay, "_local_relay_interfaces") as interfaces:
            for host in ["localhost", "127.0.0.1", "10.7.163.74", "192.168.1.2", "::1", "2001:4860:4860::8888"]:
                self.assertEqual([], await relay.relay_network_paths(replace(OPTIONS, server_address=host)))
            interfaces.assert_not_called()

    async def test_single_interface_uses_one_original_connection(self):
        connect = mock.AsyncMock(return_value=(None, mock.Mock()))
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(return_value=[]), connect)
        await selector.connect(OPTIONS)
        connect.assert_awaited_once_with(OPTIONS, None)

    async def test_slow_optional_discovery_does_not_block_system_connection(self):
        release, finished = asyncio.Event(), asyncio.Event()
        async def paths(options):
            try:
                await release.wait()
            except asyncio.CancelledError:
                await release.wait()  # Simulate a resolver that ignores cancellation.
            finally:
                finished.set()
            raise OSError("late DNS error")
        connect = mock.AsyncMock(return_value=(None, mock.Mock()))
        selector = relay.RelayNetworkPathSelector(paths, connect, discovery_seconds=.02)
        try:
            await asyncio.wait_for(selector.connect(OPTIONS), 1)
            self.assertFalse(finished.is_set())
            connect.assert_awaited_once_with(OPTIONS, None)
        finally:
            release.set()
            await asyncio.wait_for(finished.wait(), 1)

    async def test_optional_discovery_error_preserves_tls_validation(self):
        connect = mock.AsyncMock(side_effect=relay.RelayIdentityError("pin"))
        selector = relay.RelayNetworkPathSelector(mock.AsyncMock(side_effect=OSError("ioctl")), connect)
        with self.assertRaises(relay.RelayIdentityError):
            await selector.connect(OPTIONS)
        connect.assert_awaited_once_with(OPTIONS, None)

    async def test_cancelled_discovery_does_not_start_a_connection(self):
        started = asyncio.Event()
        async def paths(options):
            started.set()
            await asyncio.Event().wait()
        connect = mock.AsyncMock()
        selector = relay.RelayNetworkPathSelector(paths, connect, discovery_seconds=10)
        task = asyncio.create_task(selector.connect(OPTIONS))
        await asyncio.wait_for(started.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
