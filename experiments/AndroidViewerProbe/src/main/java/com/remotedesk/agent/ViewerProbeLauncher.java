package com.remotedesk.agent;
import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

public final class ViewerProbeLauncher extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        try {
            // Public fixture token, not a device/user credential. Peer is only
            // a synthetic loopback server and cannot inject OS input.
            java.io.File endpoint = new java.io.File(getFilesDir(), "interop-endpoint.json");
            org.json.JSONObject config = endpoint.isFile()
                ? new org.json.JSONObject(new String(java.nio.file.Files.readAllBytes(endpoint.toPath()), java.nio.charset.StandardCharsets.UTF_8))
                : new org.json.JSONObject();
            // An authorized physical test can supply an ephemeral endpoint via
            // adb run-as stdin. Never read the production app's credentials.
            AndroidPasswordStore.saveViewer(this, config.optString("password", "RemoteDesk-synthetic-ui-fixture"));
            if (endpoint.exists() && !endpoint.delete()) throw new IllegalStateException("Unable to remove temporary endpoint");
            getSharedPreferences("RemoteDeskViewerActivity", MODE_PRIVATE).edit().clear().apply();
            Intent viewer = new Intent(this, RemoteDeskViewerActivity.class)
                .putExtra(RemoteDeskViewerActivity.EXTRA_HOST, config.optString("host", "127.0.0.1"))
                .putExtra(RemoteDeskViewerActivity.EXTRA_PORT, config.optInt("port", getIntent().getIntExtra("port", 7411)))
                .putExtra("legacyFileWatchdogProbe", getIntent().getBooleanExtra("legacyFileWatchdogProbe", false));
            if (config.has("relay")) {
                AndroidRelay.Options target = AndroidRelay.Options.parse(config.getJSONObject("relay").toString());
                AndroidRelaySettings.save(this, target.target(AndroidRelaySettings.localDeviceId(this)));
                viewer.putExtra(RemoteDeskViewerActivity.EXTRA_RELAY_DEVICE_ID, target.deviceId);
            } else AndroidRelaySettings.save(this, null);
            startActivity(viewer);
        } catch (Exception failure) { throw new IllegalStateException(failure); }
        finish();
    }
}
