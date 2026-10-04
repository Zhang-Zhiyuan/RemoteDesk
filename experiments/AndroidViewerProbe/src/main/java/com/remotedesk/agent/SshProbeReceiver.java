package com.remotedesk.agent;
public final class SshProbeReceiver extends android.content.BroadcastReceiver {
    @Override public void onReceive(android.content.Context context, android.content.Intent intent) {
        try { SshUiProbe.command(intent); } catch(Exception ex) { throw new IllegalStateException(ex); }
    }
}
