package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public final class ReliableFeedbackCompatibilityTest {
    @Test public void unknownSignedCapabilityKeepsExistingFeatures() throws Exception {
        int caps = Integer.MIN_VALUE | RemoteDeskProtocol.CAPABILITY_REMOTE_DESKTOP
                | RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN;
        for (byte[] payload : new byte[][] {RemoteDeskTransport.encodeDeviceInfo("fixture", caps),
                RemoteDeskTransport.encodeViewerCapabilities(caps)}) {
            int decoded = RemoteDeskTransport.decodeControl(payload).capabilities;
            assertEquals(caps, decoded);
            assertTrue((decoded & RemoteDeskProtocol.CAPABILITY_REMOTE_DESKTOP) != 0);
            assertTrue((decoded & RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN) != 0);
            assertEquals(0, decoded & RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE);
        }
    }
}
