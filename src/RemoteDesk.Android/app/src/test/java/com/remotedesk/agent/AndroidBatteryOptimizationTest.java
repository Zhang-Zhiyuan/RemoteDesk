package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidBatteryOptimizationTest {
    @Test
    public void supportsBatteryOptimizationBypassStartsAtAndroidMarshmallow() {
        assertFalse(AndroidBatteryOptimization.supportsBatteryOptimizationBypass(22));
        assertTrue(AndroidBatteryOptimization.supportsBatteryOptimizationBypass(23));
    }

    @Test
    public void formatStatusExplainsUserActionWhenOptimizationMayApply() {
        assertTrue(AndroidBatteryOptimization.formatStatus(false, false).contains("无需"));
        assertTrue(AndroidBatteryOptimization.formatStatus(true, true).contains("已忽略"));
        assertTrue(AndroidBatteryOptimization.formatStatus(true, true).contains("厂商后台限制"));
        assertTrue(AndroidBatteryOptimization.formatStatus(true, false).contains("建议允许"));
    }
}
