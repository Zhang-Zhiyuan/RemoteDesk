package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import org.junit.Test;

public final class AndroidHostLaunchPolicyTest {
    @Test public void openingSettingsOfAlreadyRunningHostNeverNavigates() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        assertEquals(AndroidHostLaunchPolicy.Action.NONE, policy.check(100, true, true));
    }

    @Test public void serviceStartRequestAloneDoesNotNavigate() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.WAIT, policy.check(101, false, true));
        assertTrue(policy.isPending());
    }

    @Test public void waitsForActivityResumptionAndWindowFocus() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.WAIT, policy.check(101, true, false));
        assertEquals(AndroidHostLaunchPolicy.Action.RETURN_TO_DESKTOP, policy.check(102, true, true));
    }

    @Test public void successfulHostNavigatesOnlyOnce() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.RETURN_TO_DESKTOP, policy.check(101, true, true));
        assertFalse(policy.isPending());
        assertEquals(AndroidHostLaunchPolicy.Action.NONE, policy.check(102, true, true));
    }

    @Test public void failedStartupExpiresWithoutLeavingSettings() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.WAIT, policy.check(10099, false, true));
        assertEquals(AndroidHostLaunchPolicy.Action.TIMED_OUT, policy.check(10100, false, true));
        assertFalse(policy.isPending());
    }

    @Test public void lateSuccessAfterDeadlineDoesNotSurpriseUser() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.TIMED_OUT, policy.check(10100, true, true));
        assertEquals(AndroidHostLaunchPolicy.Action.NONE, policy.check(10101, true, true));
    }

    @Test public void pauseStopOrDestroyCancelsPendingNavigation() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        policy.cancel();
        assertFalse(policy.isPending());
        assertEquals(AndroidHostLaunchPolicy.Action.NONE, policy.check(101, true, true));
    }

    @Test public void anotherExplicitStartArmsFreshDeadline() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        policy.cancel();
        policy.begin(20000);
        assertEquals(AndroidHostLaunchPolicy.Action.WAIT, policy.check(20001, false, true));
        assertEquals(AndroidHostLaunchPolicy.Action.RETURN_TO_DESKTOP, policy.check(20002, true, true));
    }

    @Test public void clockDiscontinuityCancelsSafely() {
        AndroidHostLaunchPolicy policy = new AndroidHostLaunchPolicy();
        policy.begin(100);
        assertEquals(AndroidHostLaunchPolicy.Action.TIMED_OUT, policy.check(99, true, true));
    }
}
