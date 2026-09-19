package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import org.junit.Test;

public final class AndroidHostStatusTextTest {
    @Test public void pausedH264RequestsFreshAuthorization() {
        assertEquals("重新授权，恢复被控", AndroidHostStatusText.startAction(false, true, true));
        assertTrue(AndroidHostStatusText.idleHeadline(true, false, true, true).contains("H.264"));
        assertFalse(AndroidHostStatusText.idleHeadline(true, false, true, true).contains("锁屏"));
    }

    @Test public void switchingPausedH264ToReadyCompatibleDoesNotRequestProjection() {
        assertEquals("启动兼容被控", AndroidHostStatusText.startAction(true, true, true));
        String headline = AndroidHostStatusText.idleHeadline(true, true, true, true);
        assertTrue(headline.contains("兼容被控待启动"));
        assertTrue(headline.contains("无需录屏授权"));
        assertFalse(headline.contains("重新授权"));
    }

    @Test public void switchingPausedH264ToUnavailableCompatibleExplainsAccessibility() {
        assertEquals("开启无障碍权限", AndroidHostStatusText.startAction(true, false, true));
        String headline = AndroidHostStatusText.idleHeadline(true, true, false, true);
        assertTrue(headline.contains("系统设置"));
        assertTrue(headline.contains("无障碍"));
        assertFalse(headline.contains("录屏"));
    }

    @Test public void stoppedCompatibleStillOffersTheChosenBackend() {
        assertEquals("启动兼容被控", AndroidHostStatusText.startAction(true, true, false));
        assertTrue(AndroidHostStatusText.idleHeadline(false, true, true, false).contains("无需录屏授权"));
    }

    @Test public void initialH264StillExplainsProjection() {
        assertEquals("启动被控端", AndroidHostStatusText.startAction(false, false, false));
        assertTrue(AndroidHostStatusText.idleHeadline(false, false, false, false).contains("等待屏幕录制授权"));
    }

    @Test public void runningH264CannotBeRenamedByCompatiblePreference() {
        assertEquals("已授权并运行", AndroidHostStatusText.projectionStatus(true, false, true, true, false));
    }

    @Test public void runningCompatibleCannotBeRenamedByH264Preference() {
        assertEquals("无障碍兼容模式，无需重复录屏授权",
            AndroidHostStatusText.projectionStatus(true, true, false, false, false));
    }

    @Test public void missingLiveH264GrantDoesNotDescribeFutureCompatibleAsActive() {
        assertTrue(AndroidHostStatusText.projectionStatus(true, false, false, true, true).startsWith("H.264"));
    }

    @Test public void readinessAndCopiedInfoPreferSelectedModeOnlyWhenStopped() {
        assertEquals("使用无障碍截图，无需录屏授权",
            AndroidHostStatusText.projectionStatus(false, false, false, true, true));
        assertEquals("已停止，请重新授权",
            AndroidHostStatusText.projectionStatus(false, false, false, false, true));
    }

    @Test public void presenceNotificationAndDiscoveryDoNotInventProjectionRequirement() {
        assertTrue(AndroidHostStatusText.presenceStatus(true, false, true).contains("无障碍权限"));
        assertFalse(AndroidHostStatusText.presenceStatus(true, false, true).contains("录屏"));
        assertTrue(AndroidHostStatusText.presenceStatus(true, true, true).contains("启动兼容被控"));
        assertTrue(AndroidHostStatusText.presenceStatus(false, true, true).contains("重新授权"));
    }

    @Test public void deniedNotificationStillHasAnAvailableReturnRoute() {
        assertTrue(AndroidHostStatusText.returnToSettingsHint().contains("应用图标"));
        assertTrue(AndroidHostStatusText.returnToSettingsHint().contains("通知可用时"));
    }

    @Test public void previousFailureDoesNotHideTheNextModeInstruction() {
        String nextStep = AndroidHostStatusText.idleHeadline(false, true, false, true);
        String text = AndroidHostStatusText.withStartFailure("H.264 failed", nextStep);
        assertTrue(text.contains("上次启动失败：H.264 failed"));
        assertTrue(text.contains("开启无障碍权限"));
        assertEquals(nextStep, AndroidHostStatusText.withStartFailure("", nextStep));
    }
}
