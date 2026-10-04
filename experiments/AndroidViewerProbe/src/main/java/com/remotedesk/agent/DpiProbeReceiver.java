package com.remotedesk.agent;

public final class DpiProbeReceiver extends android.content.BroadcastReceiver {
    @Override public void onReceive(android.content.Context context, android.content.Intent intent) {
        try { DpiUiProbe.command(intent); }
        catch (Exception failure) { throw new IllegalStateException(failure); }
    }
}
