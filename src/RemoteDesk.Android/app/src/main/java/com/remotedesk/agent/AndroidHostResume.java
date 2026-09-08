package com.remotedesk.agent;

import android.content.Context;
import android.content.Intent;

final class AndroidHostResume {
    static final String PREF_COMPATIBLE = "host.accessibilityCapture";
    private static final String PREF_ARMED = "host.resumeCompatible";

    static boolean compatibleSelected(Context context) {
        return context.getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE)
            .getBoolean(PREF_COMPATIBLE, false);
    }

    static boolean shouldResume(Context context) {
        return compatibleSelected(context) && context.getSharedPreferences(
            RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE).getBoolean(PREF_ARMED, false);
    }

    static void setArmed(Context context, boolean armed) {
        context.getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putBoolean(PREF_ARMED, armed).apply();
    }

    static void tryResume(Context context) {
        if (!shouldResume(context) || RemoteDeskForegroundService.isHostRunning() ||
            !RemoteDeskAccessibilityService.canCaptureScreen()) return;
        try {
            context.startForegroundService(new Intent(context, RemoteDeskForegroundService.class)
                .putExtra(RemoteDeskForegroundService.EXTRA_COMPATIBLE, true)
                .putExtra(RemoteDeskForegroundService.EXTRA_AUTO_RESUME, true));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Android deferred compatible host resume; open the app to resume.", ex);
        }
    }
}
