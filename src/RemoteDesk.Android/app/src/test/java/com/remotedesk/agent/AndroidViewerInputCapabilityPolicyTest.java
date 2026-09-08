package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidViewerInputCapabilityPolicyTest {
    @Test
    public void inputRequiresAuthenticatedDeviceInfoAndCapability() {
        assertFalse(AndroidViewerInputCapabilityPolicy.canSendInput(
            false,
            RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL));
        assertFalse(AndroidViewerInputCapabilityPolicy.canSendInput(true, 0));
        assertTrue(AndroidViewerInputCapabilityPolicy.canSendInput(
            true,
            RemoteDeskProtocol.CAPABILITY_INPUT_CONTROL));
    }

    @Test
    public void readOnlyTouchIsNeverConsumedAsIfItWereActionable() {
        assertFalse(AndroidViewerInputCapabilityPolicy.shouldConsumeTouch(false));
        assertTrue(AndroidViewerInputCapabilityPolicy.shouldConsumeTouch(true));
    }

    @Test
    public void revokeAndRegrantEachFenceCommandsFromThePreviousState() {
        long granted = AndroidViewerInputCapabilityPolicy.nextGeneration(
            0L,
            false,
            true);
        long revoked = AndroidViewerInputCapabilityPolicy.nextGeneration(
            granted,
            true,
            false);
        long regranted = AndroidViewerInputCapabilityPolicy.nextGeneration(
            revoked,
            false,
            true);

        assertEquals(1L, granted);
        assertEquals(2L, revoked);
        assertEquals(3L, regranted);
        assertFalse(AndroidViewerInputCapabilityPolicy.isCurrentCommand(
            true,
            granted,
            regranted));
        assertTrue(AndroidViewerInputCapabilityPolicy.isCurrentCommand(
            true,
            regranted,
            regranted));
        assertFalse(AndroidViewerInputCapabilityPolicy.isCurrentCommand(
            false,
            regranted,
            regranted));
    }

    @Test
    public void generationWrapsWithoutReturningTheUninitializedValue() {
        assertEquals(1L, AndroidViewerInputCapabilityPolicy.nextGeneration(
            Long.MAX_VALUE,
            false,
            true));
    }

    @Test public void repeatedDeviceInfoPreservesActiveGesture() {
        long generation=AndroidViewerInputCapabilityPolicy.nextGeneration(7,true,true);
        assertFalse(AndroidViewerInputCapabilityPolicy.shouldResetGesture(7,generation));
    }
    @Test public void queuedUiObservesRevokeAndRegrantEvenWhenFinallyEnabled() {
        long revoked=AndroidViewerInputCapabilityPolicy.nextGeneration(7,true,false);
        long granted=AndroidViewerInputCapabilityPolicy.nextGeneration(revoked,false,true);
        assertTrue(AndroidViewerInputCapabilityPolicy.shouldResetGesture(7,granted));
    }
    @Test public void freshConnectionAlwaysResetsUiGesture() {
        assertTrue(AndroidViewerInputCapabilityPolicy.shouldResetGesture(-1,0));
        assertTrue(AndroidViewerInputCapabilityPolicy.shouldResetGesture(-1,1));
    }
}
