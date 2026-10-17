package com.remotedesk.agent;

import android.content.Context;
import android.content.SharedPreferences;
import java.io.IOException;
import java.util.UUID;

final class AndroidRelaySettings {
    private AndroidRelaySettings() { }

    static AndroidRelay.Options load(Context context) throws Exception {
        String encryptedConfiguration = AndroidPasswordStore.loadRelay(context);
        return encryptedConfiguration.isEmpty() ? null : AndroidRelay.Options.parse(encryptedConfiguration);
    }

    static void save(Context context, AndroidRelay.Options options) throws Exception {
        AndroidRelay.Options previous = null;
        try { previous = load(context); } catch (Exception ignored) { }
        AndroidPasswordStore.saveRelay(context, options == null ? "" : options.json().toString());
        // Publication/admin-port changes are independent of outbound SSH. Only
        // changing/removing the actual relay login revokes its live sessions.
        if (!sameSshLogin(previous, options)) AndroidSshKeepAliveService.closeAll(context);
    }

    static boolean sameSshLogin(AndroidRelay.Options previous, AndroidRelay.Options next) {
        return previous != null && next != null && previous.serverAddress.equalsIgnoreCase(next.serverAddress)
            && previous.port == next.port && previous.tlsCertificateSha256.equals(next.tlsCertificateSha256)
            && previous.accessToken.equals(next.accessToken);
    }

    static synchronized String localDeviceId(Context context) throws IOException {
        SharedPreferences preferences = context.getApplicationContext().getSharedPreferences(
            RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE);
        String existing = preferences.getString("relay-device-id", "");
        try { return UUID.fromString(existing).toString(); } catch (RuntimeException ignored) { }
        String created = UUID.randomUUID().toString();
        if (!preferences.edit().putString("relay-device-id", created).commit()) {
            throw new IOException("本机中转设备 ID 无法保存。");
        }
        return created;
    }
}
