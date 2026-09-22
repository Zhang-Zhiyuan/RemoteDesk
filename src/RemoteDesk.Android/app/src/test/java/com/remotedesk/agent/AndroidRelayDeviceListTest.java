package com.remotedesk.agent;

import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayDeviceListTest {
    private AndroidRelay.Device device(String id, String name, boolean busy, int port) {
        return new AndroidRelay.Device(id, name, "Linux", busy, List.of("10.0.0.2"), port);
    }
    @Test public void identicalRefreshDoesNotRecreateViews() {
        assertTrue(AndroidRelayDeviceList.same(List.of(device("a", "主机", false, 56565)), List.of(device("a", "主机", false, 56565))));
        assertTrue(AndroidRelayDeviceList.same(List.of(), List.of()));
    }
    @Test public void changesVisibleToUserMustRefreshActionsAndRows() {
        List<AndroidRelay.Device> before = List.of(device("a", "主机", false, 56565));
        for (AndroidRelay.Device changed : List.of(device("b", "主机", false, 56565), device("a", "改名", false, 56565),
                device("a", "主机", true, 56565), device("a", "主机", false, 40565),
                new AndroidRelay.Device("a", "主机", "Android", false, List.of("10.0.0.2"), 56565),
                new AndroidRelay.Device("a", "主机", "Linux", false, List.of("10.0.0.3"), 56565),
                new AndroidRelay.Device("a", "主机", "Linux", false, List.of("10.0.0.2"), 56565, "共享", "系统名称", true, "")))
            assertFalse(AndroidRelayDeviceList.same(before, List.of(changed)));
        assertFalse(AndroidRelayDeviceList.same(before, List.of()));
        assertFalse(AndroidRelayDeviceList.same(before, List.of(before.get(0), device("b", "新增", false, 56565))));
    }
}
