package com.remotedesk.agent;

import java.util.List;

final class AndroidConnectionInfoFormatter {
    private AndroidConnectionInfoFormatter() {
    }

    static String format(
        List<String> addresses,
        String headline,
        boolean hasPassword,
        boolean inputEnabled,
        String screenCaptureStatus,
        String h264Status) {
        return "RemoteDesk Android\n" +
            "地址：" + String.join(", ", addresses) + "\n" +
            "连接端口：" + RemoteDeskProtocol.HOST_PORT + "\n" +
            "发现端口：" + RemoteDeskProtocol.DISCOVERY_PORT + "\n" +
            "状态：" + normalize(headline) + "\n" +
            "口令：" + (hasPassword ? "已设置" : "未设置") + "\n" +
            "屏幕录制：" + normalize(screenCaptureStatus) + "\n" +
            "无障碍输入：" + (inputEnabled ? "已启用" : "未启用") + "\n" +
            "H.264编码：" + normalize(h264Status);
    }

    private static String normalize(String value) {
        if (value == null || value.trim().isEmpty()) {
            return "未知";
        }

        return value.replace('\r', ' ').replace('\n', ' ').trim();
    }
}
