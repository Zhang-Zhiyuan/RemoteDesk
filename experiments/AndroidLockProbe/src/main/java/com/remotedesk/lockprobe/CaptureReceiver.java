package com.remotedesk.lockprobe;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class CaptureReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        ScreenshotService.captureOnce();
    }
}
