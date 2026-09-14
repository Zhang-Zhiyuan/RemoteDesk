package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;

public final class RelayPathStabilityTest {
    @Test public void sharedCrossPlatformVectors() throws Exception {
        Path root = Path.of("").toAbsolutePath();
        while (root != null && !Files.isRegularFile(root.resolve("tests/data/relay-path-stability.tsv"))) root = root.getParent();
        assertNotNull("shared policy fixture", root);
        RelayPathStability state = new RelayPathStability();
        for (String line : Files.readAllLines(root.resolve("tests/data/relay-path-stability.tsv"))) {
            if (line.startsWith("#") || line.trim().isEmpty()) continue;
            String[] fields = line.split(" +");
            long now = Long.parseLong(fields[1]);
            switch (fields[0]) {
                case "reset": state = new RelayPathStability(); state.connected(fields[2], now, null); break;
                case "round": state.observeRound(now, Map.of("wifi", value(fields[2]), "wired", value(fields[3]))); break;
                case "connected": state.connected(fields[2], now, fields[3].equals("-") ? null : fields[3]); break;
                default: fail(line);
            }
            assertEquals(line, fields[fields.length - 1], state.preferred(now));
        }
    }
    private static double value(String value) {
        return value.equals("inf") ? Double.POSITIVE_INFINITY : value.equals("nan") ? Double.NaN : Double.parseDouble(value);
    }

    @Test public void probeFrequencyAndIdleHistoryAreBounded() {
        RelayPathStability state = new RelayPathStability();
        state.connected("wifi", 0, null);
        assertTrue(state.beginProbe(0));
        assertFalse(state.beginProbe(29999));
        assertTrue(state.beginProbe(30000));
        assertEquals("wifi", state.preferred(599999));
        assertNull(state.preferred(1199999));
    }

    @Test public void marginRequiresAbsoluteAndRelativeImprovement() {
        assertTrue(RelayPathStability.worthSwitching(20, 12));
        assertTrue(RelayPathStability.worthSwitching(40, 30));
        assertFalse(RelayPathStability.worthSwitching(100, 80));
        assertFalse(RelayPathStability.worthSwitching(20, 14));
        assertFalse(RelayPathStability.worthSwitching(40, Double.NaN));
        assertFalse(RelayPathStability.worthSwitching(40, Double.POSITIVE_INFINITY));
    }
}
