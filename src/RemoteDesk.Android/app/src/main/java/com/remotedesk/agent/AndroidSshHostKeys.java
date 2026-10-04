package com.remotedesk.agent;

import android.content.Context;
import android.content.SharedPreferences;
import java.io.IOException;

/** Public host-key pins only. Deliberately separate from relay-administrator SSH trust. */
final class AndroidSshHostKeys {
    private static final Object WRITE_LOCK = new Object();
    private final SharedPreferences preferences;
    AndroidSshHostKeys(Context context) { preferences = context.getSharedPreferences("ssh_target_host_keys", Context.MODE_PRIVATE); }
    String get(String alias) { return preferences.getString(alias, ""); }
    void forget(String alias) throws IOException {
        synchronized (WRITE_LOCK) {
            if (!preferences.edit().remove(alias).commit()) throw new IOException("无法忘记此 SSH 主机身份。");
        }
    }
    void trustFirst(String alias, String fingerprint) throws IOException {
        synchronized (WRITE_LOCK) {
        String previous = get(alias);
        if (!previous.isEmpty() && !previous.equals(fingerprint)) throw new IOException("SSH 主机密钥已变化，已拒绝覆盖。");
        if (previous.isEmpty() && preferences.getAll().size() >= 256) throw new IOException("已保存的 SSH 主机身份过多。");
        if (!preferences.edit().putString(alias, fingerprint).commit()) throw new IOException("无法保存 SSH 主机身份，连接已取消。");
        }
    }
}
