package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public final class AndroidUiThemeTest {
    @Test
    public void resolveStatusToneUsesInformationalToneForIdleState() {
        assertEquals(
            AndroidUiTheme.StatusTone.INFO,
            AndroidUiTheme.resolveStatusTone("等待屏幕录制授权"));
    }

    @Test
    public void resolveStatusToneUsesSuccessToneForConnectedState() {
        assertEquals(
            AndroidUiTheme.StatusTone.SUCCESS,
            AndroidUiTheme.resolveStatusTone("已连接，等待远端画面"));
    }

    @Test
    public void resolveStatusToneUsesDangerToneForFailureState() {
        assertEquals(
            AndroidUiTheme.StatusTone.DANGER,
            AndroidUiTheme.resolveStatusTone("连接失败：口令错误"));
    }
}
