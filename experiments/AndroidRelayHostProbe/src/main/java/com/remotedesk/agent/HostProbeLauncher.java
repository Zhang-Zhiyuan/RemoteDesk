package com.remotedesk.agent;

/** Opt-in diagnostic setup in a separate app/keystore. Not shipped in the product. */
public final class HostProbeLauncher extends android.app.Activity {
    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        try {
            java.io.File endpoint = new java.io.File(getFilesDir(), "interop-endpoint.json");
            if (endpoint.isFile()) {
                if (!AndroidPasswordStore.load(this).isEmpty()) throw new IllegalStateException("Existing test host password; refuse to overwrite");
                org.json.JSONObject config = new org.json.JSONObject(new String(
                    java.nio.file.Files.readAllBytes(endpoint.toPath()), java.nio.charset.StandardCharsets.UTF_8));
                AndroidPasswordStore.save(this, config.getString("password"));
                AndroidRelaySettings.save(this, AndroidRelay.Options.parse(config.getJSONObject("relay").toString()));
                if (!endpoint.delete()) throw new IllegalStateException("Unable to consume owned endpoint file");
            }
            // All screen-sharing and accessibility grants still use normal
            // product/system UI; importing a test endpoint cannot grant either.
            startActivity(new android.content.Intent(this, MainActivity.class));
        } catch (Exception failure) { throw new IllegalStateException("Test host setup failed", failure); }
        finish();
    }
}
