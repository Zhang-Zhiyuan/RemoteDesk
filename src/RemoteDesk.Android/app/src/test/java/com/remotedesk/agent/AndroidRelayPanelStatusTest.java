package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidRelayPanelStatusTest {
    @Test public void directoryRefreshCannotHideRejectedConfiguration() {
        AndroidRelayPanelStatus status = new AndroidRelayPanelStatus();
        status.failed("中转访问密钥被拒绝");
        assertEquals("配置未保存：中转访问密钥被拒绝\n正在读取在线设备", status.display("正在读取在线设备"));
        assertEquals("配置未保存：中转访问密钥被拒绝\n在线设备 0 台", status.display("在线设备 0 台"));
    }

    @Test public void anotherDirectoryFailureStillRetainsTheSaveFailure() {
        AndroidRelayPanelStatus status = new AndroidRelayPanelStatus();
        status.failed("无法保存配置");
        assertEquals("配置未保存：无法保存配置\n目录读取失败", status.display("目录读取失败"));
    }

    @Test public void newAttemptOrExplicitClearResetsOldFailure() {
        AndroidRelayPanelStatus status = new AndroidRelayPanelStatus();
        status.failed("wrong key"); status.beginSetup();
        assertEquals("验证中", status.display("验证中"));
        assertEquals("已保存", status.display("已保存"));
    }

    @Test public void latestFailureReplacesThePreviousOne() {
        AndroidRelayPanelStatus status = new AndroidRelayPanelStatus();
        status.failed("old"); status.failed("new");
        assertEquals("配置未保存：new", status.display(""));
    }

    @Test public void noFailureLeavesNormalStatusUnchanged() {
        AndroidRelayPanelStatus status = new AndroidRelayPanelStatus();
        assertEquals("在线", status.display("在线"));
        assertEquals("", status.display(null));
        status.failed(null); assertEquals("在线", status.display("在线"));
    }
}
