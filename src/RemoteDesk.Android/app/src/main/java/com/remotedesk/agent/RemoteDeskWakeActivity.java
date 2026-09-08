package com.remotedesk.agent;

import android.app.Activity;
import android.app.KeyguardManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.WindowManager;
import android.widget.TextView;

public final class RemoteDeskWakeActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable timeout = this::finish;
    private boolean requested;
    private long generation;

    @Override @SuppressWarnings("deprecation") protected void onCreate(Bundle state) {
        super.onCreate(state);
        generation = getIntent().getLongExtra("generation", 0);
        if (!RemoteDeskAccessibilityService.isWakeAuthorized(generation)) { finish(); return; }
        if (Build.VERSION.SDK_INT >= 27) { setShowWhenLocked(true); setTurnScreenOn(true); }
        else getWindow().addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        AndroidRemoteUnlock.trace("Authenticated wake activity created.");
        TextView message = new TextView(this);
        message.setText("已验证的 RemoteDesk 连接正在唤醒手机");
        message.setGravity(Gravity.CENTER);
        setContentView(message);
        handler.postDelayed(timeout, 15000);
    }

    @Override protected void onResume() {
        super.onResume();
        if (requested || isFinishing()) return;
        requested = true;
        handler.postDelayed(() -> {
            if (!RemoteDeskAccessibilityService.isWakeAuthorized(generation)) { AndroidRemoteUnlock.trace("Wake authorization expired."); finish(); return; }
            KeyguardManager manager = getSystemService(KeyguardManager.class);
            if (manager == null || !manager.isKeyguardLocked()) { AndroidRemoteUnlock.trace("Keyguard is already dismissed."); finish(); return; }
            AndroidRemoteUnlock.trace("Requesting normal keyguard dismissal.");
            manager.requestDismissKeyguard(this, new KeyguardManager.KeyguardDismissCallback() {
                @Override public void onDismissSucceeded() { AndroidRemoteUnlock.trace("Keyguard dismissal succeeded."); finish(); }
                @Override public void onDismissCancelled() { AndroidRemoteUnlock.trace("Keyguard dismissal cancelled."); finish(); }
                @Override public void onDismissError() { AndroidRemoteUnlock.trace("Keyguard dismissal unavailable."); finish(); }
            });
            RemoteDeskAccessibilityService.onKeyguardRequested(generation);
        }, 500);
    }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
