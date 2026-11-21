package com.remotedesk.agent;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.ResultReceiver;
import java.util.function.Consumer;

/** Keeps the authenticated device connection alive while the system document picker covers the viewer. */
public final class ViewerFilePickerService extends Service {
    private static final String CHANNEL = "remotedesk-file-picker";
    private static final String READY = "ready";
    private static final int NOTIFICATION = 56570;
    // Activity callbacks and service lifecycle callbacks run on the same main looper.
    private static int pickers;
    private static ViewerFilePickerService active;
    private int lastStart;

    static void acquire(Context context, Consumer<Boolean> onReady) {
        pickers++;
        ResultReceiver ready = new ResultReceiver(new Handler(Looper.getMainLooper())) {
            @Override protected void onReceiveResult(int result, Bundle ignored) { onReady.accept(result == 1); }
        };
        try {
            context.startForegroundService(new Intent(context, ViewerFilePickerService.class).putExtra(READY, ready));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not start the document picker connection service.", ex);
            ready.send(0, null);
        }
    }

    static void release() {
        if (pickers > 0 && --pickers == 0 && active != null) active.stopSelfResult(active.lastStart);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        lastStart = startId;
        ResultReceiver ready = intent == null ? null : intent.getParcelableExtra(READY);
        try {
            getSystemService(NotificationManager.class).createNotificationChannel(
                new NotificationChannel(CHANNEL, "选择要发送的文件", NotificationManager.IMPORTANCE_LOW));
            Notification note = new Notification.Builder(this, CHANNEL).setSmallIcon(R.mipmap.ic_launcher)
                .setContentTitle("正在选择要发送的文件").setContentText("RemoteDesk 正在保持与接收设备的连接。")
                .setOnlyAlertOnce(true).setOngoing(true).setVisibility(Notification.VISIBILITY_PRIVATE).build();
            if (Build.VERSION.SDK_INT >= 29) startForeground(NOTIFICATION, note, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
            else startForeground(NOTIFICATION, note);
            active = this;
            if (ready != null) ready.send(1, null);
            if (pickers == 0) stopSelfResult(startId);
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Could not keep the selected device connected for the document picker.", ex);
            if (ready != null) ready.send(0, null);
            stopSelfResult(startId);
        }
        return START_NOT_STICKY;
    }

    @Override public void onDestroy() {
        if (active == this) active = null;
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
