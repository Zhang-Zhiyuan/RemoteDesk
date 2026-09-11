package com.remotedesk.agent;

/** Real product relay panel in an isolated application. Test settings consumed privately. */
public final class RelayNameProbeActivity extends android.app.Activity {
    private AndroidRelayPanel panel;
    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        try {
            java.io.File endpoint = new java.io.File(getFilesDir(), "relay-name-endpoint.json");
            org.json.JSONObject value = new org.json.JSONObject(new String(java.nio.file.Files.readAllBytes(endpoint.toPath()),
                java.nio.charset.StandardCharsets.UTF_8));
            AndroidRelay.Options options = AndroidRelay.Options.parse(value.toString());
            String expectedServer = value.optString("expectedServer", "");
            if (expectedServer.isEmpty() || !expectedServer.equals(options.serverAddress))
                throw new IllegalStateException("Unexpected relay fixture");
            AndroidRelaySettings.save(this, options.target(java.util.UUID.randomUUID().toString()));
            if (!endpoint.delete()) throw new IllegalStateException("Unable to consume private test settings");
            panel = new AndroidRelayPanel(this, (target, name, editKey) -> { }, null);
            android.widget.ScrollView scroll = new android.widget.ScrollView(this);
            scroll.setFillViewport(true); scroll.addView(panel); setContentView(scroll);
            getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } catch (Exception error) { throw new IllegalStateException("Owned naming panel setup failed", error); }
    }
    @Override public void onResume() { super.onResume(); if (panel != null) panel.active(true); }
    @Override public void onPause() { if (panel != null) panel.active(false); super.onPause(); }
    @Override public void onDestroy() { if (panel != null) panel.close(); super.onDestroy(); }
}
