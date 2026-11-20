package com.remotedesk.agent;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import org.json.JSONObject;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.Signature;
import java.security.cert.CertificateFactory;
import java.util.Base64;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import javax.net.ssl.SSLSocket;

public final class AndroidFleetUpdate extends BroadcastReceiver {
    static final AtomicInteger viewers = new AtomicInteger();
    private static final AtomicBoolean working = new AtomicBoolean();
    private static final int NOTICE = 3256;
    private static final String CHANNEL = "remotedesk_updates";
    private static final String VERSION = "(?:0|[1-9][0-9]{0,4})(?:\\.(?:0|[1-9][0-9]{0,4})){2}";
    private static volatile long retryAfter;
    private static volatile Intent confirmation;

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("fleet-updates", Context.MODE_PRIVATE);
    }
    static boolean enabled(Context context) { return prefs(context).getBoolean("enabled", true); }
    static void setEnabled(Context context, boolean value) {
        prefs(context).edit().putBoolean("enabled", value).apply();
        if (!value) {
            int id = prefs(context).getInt("session", -1);
            if (id >= 0) try { context.getPackageManager().getPackageInstaller().abandonSession(id); }
                catch (RuntimeException ignored) { }
            clearPending(context);
        }
    }
    private static void clearPending(Context context) {
        confirmation = null;
        prefs(context).edit().remove("session").remove("committed").remove("started").apply();
        context.getSystemService(NotificationManager.class).cancel(NOTICE);
    }
    static boolean needsSetting(Context context) {
        return !context.getPackageManager().canRequestPackageInstalls();
    }
    static String actionLabel(Context context) {
        if (needsSetting(context)) return "允许 RemoteDesk 安装新版";
        return confirmation == null ? "" : "确认安装已下载的新版";
    }
    static void openAction(Context context) {
        Intent intent = needsSetting(context) ? new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
            Uri.parse("package:" + context.getPackageName())) : confirmation;
        if (intent != null) context.startActivity(intent);
    }

    static void verify(JSONObject ticket, String installed, java.security.PublicKey publisher) throws Exception {
        String version = ticket.getString("version"), build = ticket.getString("build"), hash = ticket.getString("sha256");
        long size = ticket.getLong("size");
        if (!version.matches(VERSION) || !installed.matches(VERSION) || !"android".equals(ticket.getString("platform")) ||
            !build.matches("[0-9]{14}") || !hash.matches("[0-9a-f]{64}") || size <= 0 || size > 268435456L)
            throw new SecurityException("更新清单格式无效。");
        String[] a = version.split("\\."), b = installed.split("\\.");
        int comparison = 0;
        for (int i = 0; i < 3 && comparison == 0; i++) comparison = Integer.compare(Integer.parseInt(a[i]), Integer.parseInt(b[i]));
        if (comparison <= 0) throw new SecurityException("该版本已经安装。");
        Signature verifier = Signature.getInstance("SHA256withRSA");
        verifier.initVerify(publisher);
        verifier.update(String.join("\n", "RemoteDesk/fleet/v1", version, build, "android", Long.toString(size), hash)
            .getBytes(StandardCharsets.US_ASCII));
        if (!verifier.verify(Base64.getDecoder().decode(ticket.getString("signature")))) throw new SecurityException("更新签名无效。");
    }

    static void offer(Context context, AndroidRelay.Options options, JSONObject ticket, BooleanSupplier idle, Consumer<String> status) {
        if (context == null || ticket == null || !enabled(context) || !idle.getAsBoolean() || BuildConfig.DEBUG ||
            android.os.SystemClock.elapsedRealtime() < retryAfter || !working.compareAndSet(false, true)) return;
        AndroidRelay.thread("RelayPackageUpdate", () -> {
            try {
                try (InputStream cert = context.getAssets().open("fleet-publisher.cer")) {
                    verify(ticket, BuildConfig.VERSION_NAME, CertificateFactory.getInstance("X.509").generateCertificate(cert).getPublicKey());
                }
                if (needsSetting(context)) {
                    status.accept("有新版可用。请在中继设置中点“允许 RemoteDesk 安装新版”。");
                    retryAfter = android.os.SystemClock.elapsedRealtime() + 30000;
                    return;
                }
                PackageInstaller installer = context.getPackageManager().getPackageInstaller();
                SharedPreferences state = prefs(context);
                if (state.getLong("retryAt", 0) > System.currentTimeMillis()) return;
                int previous = state.getInt("session", -1);
                if (previous >= 0 && installer.getSessionInfo(previous) != null) {
                    long age = System.currentTimeMillis() - state.getLong("started", 0);
                    if (state.getBoolean("committed", false) && age >= 0 && age < 86400000L) return;
                    installer.abandonSession(previous);
                    clearPending(context);
                }
                install(context, options, ticket, installer, idle, status);
            } catch (Exception ex) {
                retryAfter = android.os.SystemClock.elapsedRealtime() + 120000;
                status.accept("自动更新稍后重试：" + ex.getMessage());
            } finally { working.set(false); }
        }).start();
    }

    private static void install(Context context, AndroidRelay.Options options, JSONObject ticket, PackageInstaller installer,
                                BooleanSupplier idle, Consumer<String> status) throws Exception {
        long size = ticket.getLong("size");
        PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        params.setAppPackageName(context.getPackageName());
        params.setSize(size);
        if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED);
        int id = installer.createSession(params);
        boolean committed = false;
        prefs(context).edit().putInt("session", id).putBoolean("committed", false)
            .putLong("started", System.currentTimeMillis()).apply();
        try (PackageInstaller.Session session = installer.openSession(id);
             AndroidRelayNetworkSelector.Dial dial = new AndroidRelayNetworkSelector.Dial();
             SSLSocket socket = AndroidRelayNetworkSelector.connect(context, options, dial)) {
            socket.setSoTimeout(30000);
            JSONObject reply = AndroidRelay.exchange(socket, AndroidRelay.request(options, "update-package")
                .put("updatePlatform", "android").put("sha256", ticket.getString("sha256")));
            if (reply.getLong("size") != size) throw new SecurityException("更新包长度有误。");
            MessageDigest hash = MessageDigest.getInstance("SHA-256");
            long deadline = android.os.SystemClock.elapsedRealtime() + 600000;
            try (OutputStream output = session.openWrite("base.apk", 0, size)) {
                byte[] buffer = new byte[65536];
                long remaining = size;
                while (remaining > 0) {
                    if (!idle.getAsBoolean() || !enabled(context) || android.os.SystemClock.elapsedRealtime() > deadline) return;
                    int count = socket.getInputStream().read(buffer, 0, (int)Math.min(buffer.length, remaining));
                    if (count < 0) throw new java.io.EOFException("更新下载中断。");
                    hash.update(buffer, 0, count);
                    output.write(buffer, 0, count);
                    remaining -= count;
                }
                session.fsync(output);
            }
            StringBuilder actual = new StringBuilder();
            for (byte value : hash.digest()) actual.append(String.format(java.util.Locale.ROOT, "%02x", value));
            if (!actual.toString().equals(ticket.getString("sha256"))) throw new SecurityException("安装包 SHA-256 校验失败。");
            if (!idle.getAsBoolean() || !enabled(context)) return;
            Intent result = new Intent(context, AndroidFleetUpdate.class).setAction("com.remotedesk.agent.FLEET_INSTALL");
            int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
            PendingIntent callback = PendingIntent.getBroadcast(context, id, result, flags);
            prefs(context).edit().putBoolean("committed", true).apply();
            session.commit(callback.getIntentSender());
            committed = true;
            status.accept("新版已通过校验，正在等待系统安装。");
        } finally {
            if (!committed) {
                installer.abandonSession(id);
                clearPending(context);
            }
        }
    }

    @Override public void onReceive(Context context, Intent result) {
        if (!"com.remotedesk.agent.FLEET_INSTALL".equals(result.getAction()) ||
            result.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1) != prefs(context).getInt("session", -2)) return;
        int status = result.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION && enabled(context)) {
            @SuppressWarnings("deprecation") Intent prompt = result.getParcelableExtra(Intent.EXTRA_INTENT);
            if (prompt == null) return;
            confirmation = prompt;
            NotificationManager manager = context.getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel(CHANNEL, "软件更新", NotificationManager.IMPORTANCE_DEFAULT));
            PendingIntent tap = PendingIntent.getActivity(context, NOTICE, prompt, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
            manager.notify(NOTICE, new Notification.Builder(context, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download_done)
                .setContentTitle("RemoteDesk 更新已就绪").setContentText("点此在系统安装器中确认。").setContentIntent(tap).build());
        } else {
            clearPending(context);
            if (status != PackageInstaller.STATUS_SUCCESS)
                prefs(context).edit().putLong("retryAt", System.currentTimeMillis() + 3600000).apply();
        }
    }
}
