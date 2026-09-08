package com.remotedesk.agent;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.util.Arrays;

import org.junit.Test;

public final class AndroidConnectionInfoFormatterTest {
    @Test
    public void formatIncludesAddressesPortsAndStatus() {
        String text = AndroidConnectionInfoFormatter.format(
            Arrays.asList("10.0.0.2", "192.168.1.9"),
            "已打开，可被局域网扫描\n等待屏幕录制授权",
            true,
            false,
            "启动被控端时会请求",
            "可用：硬件编码，CBR/VBR");

        assertTrue(text.contains("RemoteDesk Android"));
        assertTrue(text.contains("10.0.0.2, 192.168.1.9"));
        assertTrue(text.contains("连接端口：" + RemoteDeskProtocol.HOST_PORT));
        assertTrue(text.contains("发现端口：" + RemoteDeskProtocol.DISCOVERY_PORT));
        assertTrue(text.contains("状态：已打开，可被局域网扫描 等待屏幕录制授权"));
        assertTrue(text.contains("口令：已设置"));
        assertTrue(text.contains("无障碍输入：未启用"));
        assertTrue(text.contains("H.264编码：可用：硬件编码，CBR/VBR"));
    }

    @Test
    public void formatNormalizesMissingValues() {
        String text = AndroidConnectionInfoFormatter.format(
            Arrays.asList("未获取到局域网 IPv4"),
            "",
            false,
            true,
            null,
            "检测失败，将使用 JPEG");

        assertTrue(text.contains("状态：未知"));
        assertTrue(text.contains("口令：未设置"));
        assertTrue(text.contains("屏幕录制：未知"));
        assertTrue(text.contains("无障碍输入：已启用"));
        assertFalse(text.contains("\r"));
    }
}
