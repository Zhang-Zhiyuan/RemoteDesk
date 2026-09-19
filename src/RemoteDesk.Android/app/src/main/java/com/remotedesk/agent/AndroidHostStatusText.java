package com.remotedesk.agent;

/** Mode-aware instructions; selected mode describes only the next host start. */
final class AndroidHostStatusText {
    private AndroidHostStatusText() { }

    static String startAction(boolean compatible, boolean accessibilityAvailable, boolean capturePaused) {
        if (compatible) return accessibilityAvailable ? "启动兼容被控" : "开启无障碍权限";
        return capturePaused ? "重新授权，恢复被控" : "启动被控端";
    }

    static String idleHeadline(boolean serviceRunning, boolean compatible,
            boolean accessibilityAvailable, boolean capturePaused) {
        if (compatible) {
            if (!accessibilityAvailable) {
                return "兼容被控需要无障碍权限\n点击“开启无障碍权限”进入系统设置；已开启但未连接时，请关闭后重新开启。";
            }
            return serviceRunning
                ? "兼容被控待启动\n点击启动兼容被控，无需录屏授权；设备发现仍在运行。"
                : "已打开，可被局域网扫描\n点击启动兼容被控，无需录屏授权";
        }
        if (serviceRunning && capturePaused) {
            return "屏幕录制已停止\nH.264 录屏授权已结束，请重新授权恢复；设备发现仍在运行。";
        }
        return serviceRunning ? "发现常驻中" : "已打开，可被局域网扫描\n等待屏幕录制授权";
    }

    static String projectionStatus(boolean hostRunning, boolean activeCompatible,
            boolean projectionGranted, boolean selectedCompatible, boolean capturePaused) {
        // A preference change cannot rename an already-running capture backend.
        if (hostRunning) {
            if (activeCompatible) return "无障碍兼容模式，无需重复录屏授权";
            if (projectionGranted) return "已授权并运行";
            return "H.264 录屏不可用，请停止后重新授权";
        }
        if (selectedCompatible) return "使用无障碍截图，无需录屏授权";
        return capturePaused ? "已停止，请重新授权" : "启动被控端时会请求";
    }

    static String withStartFailure(String failure, String nextStep) {
        if (failure == null || failure.trim().isEmpty()) return nextStep;
        return "上次启动失败：" + failure + "\n" + nextStep;
    }

    static String presenceStatus(boolean compatible, boolean accessibilityAvailable, boolean capturePaused) {
        if (compatible) return accessibilityAvailable
            ? "发现常驻中，打开 RemoteDesk 启动兼容被控"
            : "发现常驻中，请在手机上开启无障碍权限";
        return capturePaused ? "录屏已停止，打开 RemoteDesk 重新授权" : "发现常驻中，等待屏幕录制授权";
    }

    static String returnToSettingsHint() {
        return "被控保持运行，可从 RemoteDesk 应用图标返回设置；通知可用时也可从通知返回。";
    }
}
