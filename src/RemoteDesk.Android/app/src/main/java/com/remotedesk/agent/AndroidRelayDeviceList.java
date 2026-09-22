package com.remotedesk.agent;

import java.util.List;
import java.util.Objects;

/** Compare every field used by an online row or its actions, without volatile timestamps. */
final class AndroidRelayDeviceList {
    private AndroidRelayDeviceList() { }

    static boolean same(List<AndroidRelay.Device> previous, List<AndroidRelay.Device> next) {
        if (previous.size() != next.size()) return false;
        for (int i = 0; i < previous.size(); i++) {
            AndroidRelay.Device a = previous.get(i), b = next.get(i);
            if (!Objects.equals(a.deviceId, b.deviceId) || !Objects.equals(a.name, b.name) ||
                !Objects.equals(a.platform, b.platform) || a.busy != b.busy ||
                !a.directAddresses.equals(b.directAddresses) || a.directPort != b.directPort ||
                !Objects.equals(a.sharedName, b.sharedName) || !Objects.equals(a.originalName, b.originalName) ||
                a.canRename != b.canRename || !Objects.equals(a.namingUnavailableReason, b.namingUnavailableReason)) return false;
        }
        return true;
    }
}
