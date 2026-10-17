package com.remotedesk.agent;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import java.util.concurrent.ConcurrentHashMap;

/** Visible, user-started SSH lifetime only. Never restarts or stores SSH credentials. */
public final class AndroidSshKeepAliveService extends Service {
    private static final String CHANNEL = "remotedesk-ssh-sessions";
    private static final int NOTIFICATION = 56569;
    private static final String STOP = "com.remotedesk.agent.STOP_SSH";
    private static final ConcurrentHashMap<String, Entry> sessions = new ConcurrentHashMap<>();
    private static final class Entry {
        final String name;
        final Intent recall;
        final Runnable stop;
        Entry(String name, Intent recall, Runnable stop) { this.name = name; this.recall = recall; this.stop = stop; }
    }
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final java.util.HashSet<String> ownedIds = new java.util.HashSet<>();
    private PowerManager.WakeLock wake;
    private final Runnable renew = new Runnable() {
        @Override public void run() {
            if (sessions.isEmpty()) return;
            // No screen wake-up. A timeout also releases power if the service
            // stops making progress; normal teardown releases immediately.
            if (wake != null) wake.acquire(10 * 60 * 1000L);
            ui.postDelayed(this, 5 * 60 * 1000L);
        }
    };

    static boolean keep(Context context, String id, String name, Intent recall, Runnable stop) {
        if (sessions.size() >= 4) return false;
        sessions.put(id, new Entry(name, new Intent(recall), stop));
        try { context.startForegroundService(new Intent(context, AndroidSshKeepAliveService.class)); return true; }
        catch (RuntimeException ex) { sessions.remove(id); return false; }
    }

    static void release(Context context, String id) {
        sessions.remove(id);
        if (sessions.isEmpty()) context.stopService(new Intent(context, AndroidSshKeepAliveService.class));
        else try { context.startService(new Intent(context, AndroidSshKeepAliveService.class)); }
        catch (RuntimeException ignored) { } // Existing visible service continues to own the remaining sessions.
    }

    static void closeAll(Context context) {
        for (String id : sessions.keySet()) {
            Entry entry = sessions.remove(id);
            if (entry != null) entry.stop.run();
        }
        context.stopService(new Intent(context, AndroidSshKeepAliveService.class));
    }

    @Override public void onCreate() {
        super.onCreate();
        getSystemService(NotificationManager.class).createNotificationChannel(
            new NotificationChannel(CHANNEL, "SSH 连接", NotificationManager.IMPORTANCE_LOW));
        PowerManager power = getSystemService(PowerManager.class);
        if (power != null) {
            wake = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "RemoteDesk:SshSession");
            wake.setReferenceCounted(false);
        }
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP.equals(intent.getAction())) {
            String id = intent.getStringExtra("session");
            Entry entry = id == null ? null : sessions.remove(id);
            if (entry != null) entry.stop.run();
        }
        var iterator = sessions.entrySet().iterator();
        if (!iterator.hasNext()) { stopSelf(startId); return START_NOT_STICKY; }
        var latest = iterator.next();
        ownedIds.addAll(sessions.keySet());
        PendingIntent recall = PendingIntent.getActivity(this, latest.getKey().hashCode(), latest.getValue().recall,
            PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, latest.getKey().hashCode(),
            new Intent(this, AndroidSshKeepAliveService.class).setAction(STOP).setData(latest.getValue().recall.getData())
                .putExtra("session", latest.getKey()), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, CHANNEL).setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle("SSH 连接保活 · " + sessions.size() + " 个终端")
            .setContentText(latest.getValue().name + " · 点击唤回；在设备列表可切换其他终端")
            .setContentIntent(recall).setOngoing(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PRIVATE)
            .addAction(new Notification.Action.Builder(null, "断开此 SSH", stop).build()).build();
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
            else startForeground(NOTIFICATION, notification);
            ui.removeCallbacks(renew); renew.run();
        } catch (RuntimeException ex) { closeAll(this); stopSelf(startId); }
        return START_NOT_STICKY;
    }

    @Override public void onTaskRemoved(Intent rootIntent) {
        if (rootIntent != null && rootIntent.getData() != null) {
            for (var row : sessions.entrySet()) if (rootIntent.getData().equals(row.getValue().recall.getData())) {
                Entry entry = sessions.remove(row.getKey());
                if (entry != null) entry.stop.run();
            }
        }
        if (sessions.isEmpty()) stopSelf();
        super.onTaskRemoved(rootIntent);
    }

    @Override public void onDestroy() {
        ui.removeCallbacksAndMessages(null);
        if (wake != null && wake.isHeld()) wake.release();
        // A fresh start may already be queued while this old service instance
        // is being destroyed. Do not close a newly registered replacement.
        for (String id : ownedIds) {
            Entry entry = sessions.remove(id);
            if (entry != null) entry.stop.run();
        }
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
