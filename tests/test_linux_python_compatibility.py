import asyncio
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/linux'))
import remotedesk_linux_relay as relay


class CancellationCompatibilityTests(unittest.TestCase):
    def test_legacy_task_without_cancelling_api(self):
        task = SimpleNamespace(cancel=mock.Mock())
        with mock.patch.object(relay.asyncio, 'current_task', return_value=task):
            self.assertFalse(relay._path_attempt_cancel_requested())
            relay._cancel_path_attempt(task)
            self.assertTrue(relay._path_attempt_cancel_requested())
            task.cancel.assert_called_once()

    def test_modern_external_cancellation_is_also_observed(self):
        task = SimpleNamespace(cancelling=lambda: 1)
        with mock.patch.object(relay.asyncio, 'current_task', return_value=task):
            self.assertTrue(relay._path_attempt_cancel_requested())


class TimeoutCompatibilityTests(unittest.IsolatedAsyncioTestCase):
    async def test_legacy_asyncio_timeout_becomes_public_timeout_error(self):
        class LegacyTimeout(Exception):
            pass
        with mock.patch.object(relay.asyncio, 'TimeoutError', LegacyTimeout), \
                mock.patch.object(relay.asyncio, 'wait_for', side_effect=LegacyTimeout):
            with self.assertRaises(TimeoutError):
                await relay._relay_deadline(None)

    async def test_legacy_timeout_preserves_identity_failure(self):
        class LegacyTimeout(Exception):
            pass
        timeout = LegacyTimeout()
        timeout.__cause__ = relay.RelayIdentityError('owned-pin-failure')
        with mock.patch.object(relay.asyncio, 'TimeoutError', LegacyTimeout), \
                mock.patch.object(relay.asyncio, 'wait_for', side_effect=timeout):
            with self.assertRaises(relay.RelayIdentityError):
                await relay._relay_deadline(None)

    async def test_explicit_cancel_never_becomes_a_timeout(self):
        with mock.patch.object(relay.asyncio, 'wait_for', side_effect=asyncio.CancelledError):
            with self.assertRaises(asyncio.CancelledError):
                await relay._relay_deadline(None)

    async def test_recreated_cancelled_error_preserves_its_identity_context(self):
        timeout = asyncio.TimeoutError()
        timeout.__cause__ = asyncio.CancelledError()
        timeout.__cause__.__context__ = relay.RelayIdentityError('owned-pin-failure')
        with mock.patch.object(relay.asyncio, 'wait_for', side_effect=timeout):
            with self.assertRaises(relay.RelayIdentityError):
                await relay._relay_deadline(None)

    async def test_cyclic_exception_context_cannot_hang_timeout_reporting(self):
        timeout = asyncio.TimeoutError()
        timeout.__cause__ = asyncio.CancelledError()
        timeout.__cause__.__context__ = timeout.__cause__
        with mock.patch.object(relay.asyncio, 'wait_for', side_effect=timeout):
            with self.assertRaises(TimeoutError):
                await relay._relay_deadline(None)


if __name__ == '__main__':
    unittest.main()
