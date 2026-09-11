package com.remotedesk.agent;

/** UI-thread feedback: background directory refresh must not hide a failed save. */
final class AndroidRelayPanelStatus {
    private String setupFailure = "";

    void beginSetup() { setupFailure = ""; }
    void failed(String message) { setupFailure = message == null ? "" : message; }

    String display(String directoryStatus) {
        String current = directoryStatus == null ? "" : directoryStatus;
        if (setupFailure.isEmpty()) return current;
        return "配置未保存：" + setupFailure + (current.isEmpty() ? "" : "\n" + current);
    }
}
