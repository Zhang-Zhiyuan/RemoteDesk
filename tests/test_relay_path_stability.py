import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "linux"))
from remotedesk_linux_relay import RelayPathStability


class RelayPathStabilityTests(unittest.TestCase):
    def test_shared_cross_platform_vectors(self):
        state = RelayPathStability()
        fixture = Path(__file__).resolve().parent / "data" / "relay-path-stability.tsv"
        for line in fixture.read_text(encoding="utf-8").splitlines():
            if not line or line.startswith("#"):
                continue
            fields = line.split()
            now = int(fields[1])
            if fields[0] == "reset":
                state = RelayPathStability()
                state.connected(fields[2], now)
            elif fields[0] == "round":
                state.observe_round(now, {"wifi": float(fields[2]), "wired": float(fields[3])})
            elif fields[0] == "connected":
                state.connected(fields[2], now, None if fields[3] == "-" else fields[3])
            else:
                self.fail(line)
            self.assertEqual(fields[-1], state.preferred(now), line)

    def test_probe_frequency_and_idle_history_are_bounded(self):
        state = RelayPathStability()
        state.connected("wifi", 0)
        self.assertTrue(state.begin_probe(0))
        self.assertFalse(state.begin_probe(29999))
        self.assertTrue(state.begin_probe(30000))
        self.assertEqual("wifi", state.preferred(599999))
        self.assertIsNone(state.preferred(1199999))

    def test_margin_requires_absolute_and_relative_improvement(self):
        for current, candidate, expected in [(20, 12, True), (40, 30, True), (100, 80, False),
                                             (20, 14, False), (40, math.nan, False), (40, math.inf, False)]:
            self.assertEqual(expected, RelayPathStability.worth_switching(current, candidate))
