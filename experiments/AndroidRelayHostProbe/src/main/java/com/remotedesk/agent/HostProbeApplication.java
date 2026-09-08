package com.remotedesk.agent;

public final class HostProbeApplication extends android.app.Application {
    private final android.os.Handler handler = new android.os.Handler(android.os.Looper.getMainLooper());
    @Override public void onCreate() { super.onCreate(); handler.post(this::snapshot); }
    private void snapshot() {
        android.util.AtomicFile file = new android.util.AtomicFile(new java.io.File(getFilesDir(), "host-state.json"));
        java.io.FileOutputStream output = null;
        try {
            org.json.JSONObject state = new org.json.JSONObject()
                .put("hostRunning", RemoteDeskForegroundService.isHostRunning())
                .put("accessibility", RemoteDeskAccessibilityService.isEnabled())
                .put("relay", RemoteDeskForegroundService.getRelayStatus());
            output = file.startWrite();
            output.write(state.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
            file.finishWrite(output);
        } catch (Exception ex) { if (output != null) file.failWrite(output); }
        handler.postDelayed(this::snapshot, 1000);
    }
}
