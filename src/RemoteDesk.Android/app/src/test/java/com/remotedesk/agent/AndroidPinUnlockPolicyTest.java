package com.remotedesk.agent;

import static org.junit.Assert.*;
import org.junit.Test;

public final class AndroidPinUnlockPolicyTest {
    @Test public void waitsForDelayedAcknowledgementWithoutResendingDigits() {
        assertEquals(AndroidPinUnlockPolicy.InputDecision.SEND, AndroidPinUnlockPolicy.inputDecision(2, 2, 0));
        assertEquals(AndroidPinUnlockPolicy.InputDecision.WAIT, AndroidPinUnlockPolicy.inputDecision(1, 2, 0));
        assertEquals(AndroidPinUnlockPolicy.InputDecision.WAIT, AndroidPinUnlockPolicy.inputDecision(1, 2, 7));
        assertEquals(AndroidPinUnlockPolicy.InputDecision.STOP, AndroidPinUnlockPolicy.inputDecision(1, 2, 8));
        assertEquals(AndroidPinUnlockPolicy.InputDecision.STOP, AndroidPinUnlockPolicy.inputDecision(3, 2, 0));
    }

    @Test public void acceptsOnlyAsciiNumericPinWithoutCoercion() {
        assertTrue(AndroidPinUnlockPolicy.validPin("2345"));
        assertTrue(AndroidPinUnlockPolicy.validPin("1234567890123456"));
        for (String value : new String[] {null, "", "123", "12345678901234567", "12a4", "１２３４", " 1234"})
            assertFalse(AndroidPinUnlockPolicy.validPin(value));
    }

    @Test public void rejectsLabelsThatCouldBeOtherButtons() {
        assertEquals(1, AndroidPinUnlockPolicy.digitLabel("1"));
        assertEquals(0, AndroidPinUnlockPolicy.digitLabel("0"));
        for (String value : new String[] {null, "", "1 notification", "11", "Emergency", "１２"})
            assertEquals(-1, AndroidPinUnlockPolicy.digitLabel(value));
    }

    @Test public void requiresKnownEmptyPinEntryAndDetectsCompetingInput() {
        assertTrue(AndroidPinUnlockPolicy.explicitlyEmptyPrompt("密码栏，已输入 0 个值，共 6 个值"));
        assertTrue(AndroidPinUnlockPolicy.explicitlyEmptyPrompt("PIN 0 of 6"));
        assertFalse(AndroidPinUnlockPolicy.explicitlyEmptyPrompt("密码栏，已输入 2 个值，共 6 个值"));
        assertEquals(2, AndroidPinUnlockPolicy.enteredDigitCount("密码栏，已输入 2 个值，共 6 个值"));
        assertFalse(AndroidPinUnlockPolicy.explicitlyEmptyPrompt(""));
        assertFalse(AndroidPinUnlockPolicy.explicitlyEmptyPrompt(null));
        assertFalse(AndroidPinUnlockPolicy.explicitlyEmptyPrompt("Phone 0"));
    }
}
