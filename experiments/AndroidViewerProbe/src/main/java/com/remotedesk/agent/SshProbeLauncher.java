package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import org.json.JSONObject;
import java.nio.file.Files;
import java.io.File;

/** Owned fixture credentials are read from the isolated probe's external files, never command arguments. */
public final class SshProbeLauncher extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        try {
            JSONObject data = getIntent().getBooleanExtra("uiOnly", false)
                ? new JSONObject().put("port", 1).put("token", "owned-ui-only-no-network".repeat(3))
                    .put("tlsCertificateSha256", "A".repeat(64)).put("deviceId", "01234567-89ab-cdef-0123-456789abcdef")
                    .put("deviceKey", "owned-ui-only-key").put("sshUsername", "")
                : new JSONObject(new String(Files.readAllBytes(new File(getFilesDir(), "ssh-fixture.json").toPath()), java.nio.charset.StandardCharsets.UTF_8));
            SshUiProbe.fixture = data;
            AndroidRelay.Options options = new AndroidRelay.Options("10.0.2.2", data.getInt("port"), data.getString("token"),
                data.getString("tlsCertificateSha256"), "abcdef01-2345-6789-abcd-ef0123456789", false);
            boolean recallOnly = getIntent().getBooleanExtra("recallOnly", false);
            if (!recallOnly) AndroidRelaySettings.save(this, options);
            AndroidRelay.Options target = options.target(data.getString("deviceId"));
            if (!recallOnly) AndroidConnectionHistoryStore.remember(this, "", target.serverAddress, target.port,
                data.getString("deviceKey"), target, "隔离 SSH 中文主机");
            if (getIntent().getBooleanExtra("clearTrust", false)) getSharedPreferences("ssh_target_host_keys", MODE_PRIVATE).edit().clear().commit();
            AndroidSshTerminalActivity.launch(this, target, getIntent().getBooleanExtra("longName", false)
                ? "会议室主控电脑与图形设计工作站".repeat(10) : "隔离 SSH 中文主机");
            finish();
        } catch (Exception ex) { throw new IllegalStateException("Owned SSH fixture setup failed", ex); }
    }
}
