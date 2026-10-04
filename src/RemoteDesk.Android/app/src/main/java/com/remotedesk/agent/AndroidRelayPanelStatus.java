package com.remotedesk.agent;

/** UI-thread feedback: background directory refresh must not hide a failed save. */
final class AndroidRelayPanelStatus {
    private String setupFailure = "";
    private boolean persistenceCompleted;

    void beginSetup() { setupFailure = ""; }
    void failed(String message) { failed(message, false); }
    void failed(String message, boolean persistenceCompleted) {
        setupFailure = message == null ? "" : message;
        this.persistenceCompleted = persistenceCompleted;
    }

    String display(String directoryStatus) {
        String current = directoryStatus == null ? "" : directoryStatus;
        if (setupFailure.isEmpty()) return current;
        return (persistenceCompleted ? "" : "配置未保存：") + setupFailure +
            (current.isEmpty() ? "" : "\n" + current);
    }
}
