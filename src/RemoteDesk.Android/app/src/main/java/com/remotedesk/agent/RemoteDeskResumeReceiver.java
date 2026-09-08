package com.remotedesk.agent;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public final class RemoteDeskResumeReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        if (Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction()) ||
            Intent.ACTION_MY_PACKAGE_REPLACED.equals(intent.getAction())) AndroidHostResume.tryResume(context);
    }
}
