package com.remotedesk.agent;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.app.KeyguardManager;
import android.content.Context;
import android.content.Intent;
import android.graphics.Path;
import android.graphics.Rect;
import android.os.Handler;
import android.os.PowerManager;
import android.view.accessibility.AccessibilityNodeInfo;
import java.util.Arrays;
import java.util.function.BooleanSupplier;

// Normal keyguard interaction with an optional locally stored PIN. No keyguard bypass.
final class AndroidRemoteUnlock {
    private static final String PREF_ATTEMPT_BLOCKED = "unlock.attemptBlocked";
    private final RemoteDeskAccessibilityService service;
    private final Handler handler;
    private BooleanSupplier authorized;
    private long generation;
    private volatile boolean enteringPin;
    private char[] activePin;
    private long nextWakeAt;

    AndroidRemoteUnlock(RemoteDeskAccessibilityService service, Handler handler) {
        this.service = service;
        this.handler = handler;
    }

    static boolean isLocked(Context context) {
        KeyguardManager manager = context.getSystemService(KeyguardManager.class);
        return manager != null && manager.isKeyguardLocked();
    }

    static boolean isScreenOff(Context context) {
        PowerManager manager = context.getSystemService(PowerManager.class);
        return manager != null && !manager.isInteractive();
    }

    boolean enteringPin() { return enteringPin; }

    void request(BooleanSupplier lease) {
        if (!lease.getAsBoolean() || (!isScreenOff(service) && !isLocked(service)) || enteringPin) return;
        long now = android.os.SystemClock.elapsedRealtime();
        if (now < nextWakeAt) return;
        nextWakeAt = now + 3000;
        authorized = lease;
        long current = ++generation;
        trace("Authenticated wake requested.");
        try {
            service.startActivity(new Intent(service, RemoteDeskWakeActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_NO_ANIMATION)
                .putExtra("generation", current));
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Android did not allow the authenticated remote wake request.", ex);
        }
    }

    boolean isAuthorized(long expectedGeneration) {
        return expectedGeneration == generation && authorized != null && authorized.getAsBoolean() &&
            RemoteDeskForegroundService.isHostRunning();
    }

    void onKeyguardRequested(long expectedGeneration) {
        if (!isAuthorized(expectedGeneration) || enteringPin) return;
        if (service.getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE)
                .getBoolean(PREF_ATTEMPT_BLOCKED, false)) {
            trace("Automatic unlock is blocked until local unlock.");
            return;
        }
        String stored = AndroidPasswordStore.loadUnlockPin(service);
        if (!AndroidPinUnlockPolicy.validPin(stored)) { trace("No usable local PIN configured."); return; }
        char[] pin = stored.toCharArray();
        activePin = pin;
        enteringPin = true;
        trace("Waiting for the system PIN keypad.");
        waitForKeypad(expectedGeneration, pin, 0);
    }

    void observeUnlocked() {
        if (!isLocked(service)) setAttemptBlocked(service, false);
    }

    void cancel() {
        generation++;
        authorized = null;
        if (activePin != null) finishPin(activePin);
    }

    static void setAttemptBlocked(Context context, boolean blocked) {
        android.content.SharedPreferences prefs = context.getSharedPreferences(
            RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE);
        if (prefs.getBoolean(PREF_ATTEMPT_BLOCKED, false) != blocked)
            prefs.edit().putBoolean(PREF_ATTEMPT_BLOCKED, blocked).commit();
    }

    private void waitForKeypad(long expected, char[] pin, int checks) {
        if (!isAuthorized(expected) || !isLocked(service)) { finishPin(pin); return; }
        Keypad keypad = readKeypad();
        if (keypad != null && keypad.empty && keypad.complete()) {
            // Persist before the first digit. Reconnect/process restart cannot repeat a failed PIN.
            if (!service.getSharedPreferences(RemoteDeskForegroundService.PREFS_NAME, Context.MODE_PRIVATE)
                    .edit().putBoolean(PREF_ATTEMPT_BLOCKED, true).commit()) {
                finishPin(pin);
                return;
            }
            enterDigit(expected, pin, 0);
        } else if (checks == 3 && keypad != null && keypad.lockPage && keypad.digitCount() == 0 && !keypad.screen.isEmpty()) {
            // Some OEM keyguards acknowledge requestDismissKeyguard without
            // opening their PIN page. Perform the same one upward swipe as the
            // owner, only on a positively identified system lock screen.
            trace("Opening the system PIN page with an authenticated lock-screen swipe.");
            Rect screen = keypad.screen;
            Path path = new Path();
            path.moveTo(screen.centerX(), screen.top + screen.height() * 0.83f);
            path.lineTo(screen.centerX(), screen.top + screen.height() * 0.23f);
            dispatch(path, 450, () -> handler.postDelayed(() -> waitForKeypad(expected, pin, checks + 1), 500), () -> finishPin(pin));
        } else if (checks < 12) {
            handler.postDelayed(() -> waitForKeypad(expected, pin, checks + 1), 300);
        } else {
            trace("Automatic unlock skipped: no recognized empty system PIN keypad.");
            trace(keypad == null ? "No accessible system keyguard window."
                : "Keyguard keypad: recognized buttons=" + keypad.digitCount() + ", empty field=" + keypad.empty + ".");
            finishPin(pin);
        }
    }

    private void enterDigit(long expected, char[] pin, int index) {
        enterDigit(expected, pin, index, 0);
    }

    private void enterDigit(long expected, char[] pin, int index, int fieldChecks) {
        if (!isAuthorized(expected) || !isLocked(service)) { finishPin(pin); return; }
        if (index == pin.length) {
            // Realme auto-submits its fixed-length PIN. For other layouts only use a
            // positively identified system Enter button, never a guessed coordinate.
            Keypad keypad = readKeypad();
            if (keypad != null && keypad.enter != null)
                tap(keypad.enter, () -> verifyUnlock(pin), () -> finishPin(pin));
            else verifyUnlock(pin);
            return;
        }
        Keypad keypad = readKeypad();
        if (keypad == null || !keypad.complete()) { trace("PIN entry stopped: system keypad changed."); finishPin(pin); return; }
        AndroidPinUnlockPolicy.InputDecision decision = AndroidPinUnlockPolicy.inputDecision(keypad.entered, index, fieldChecks);
        if (decision != AndroidPinUnlockPolicy.InputDecision.SEND) {
            // A completed gesture may precede the accessibility text event.
            // Wait only for its acknowledgement; never resend a PIN digit.
            if (decision == AndroidPinUnlockPolicy.InputDecision.WAIT) {
                handler.postDelayed(() -> enterDigit(expected, pin, index, fieldChecks + 1), 100);
            } else { trace("PIN entry stopped: concurrent input or unacknowledged system field."); finishPin(pin); }
            return;
        }
        Rect target = keypad.digits[pin[index] - '0'];
        tap(target, () -> handler.postDelayed(() -> enterDigit(expected, pin, index + 1), 100), () -> finishPin(pin));
    }

    private void verifyUnlock(char[] pin) {
        handler.postDelayed(() -> {
            if (!isLocked(service)) {
                setAttemptBlocked(service, false);
                trace("Authenticated remote PIN unlock completed.");
            } else trace("Automatic PIN unlock did not complete; automatic retries are blocked until local unlock.");
            finishPin(pin);
        }, 900);
    }

    private void finishPin(char[] pin) {
        Arrays.fill(pin, '\0');
        if (activePin == pin) { activePin = null; enteringPin = false; }
    }

    private void tap(Rect target, Runnable completed, Runnable cancelled) {
        Path path = new Path();
        path.moveTo(target.centerX(), target.centerY());
        dispatch(path, 60, completed, cancelled);
    }

    private void dispatch(Path path, int duration, Runnable completed, Runnable cancelled) {
        GestureDescription gesture = new GestureDescription.Builder()
            .addStroke(new GestureDescription.StrokeDescription(path, 0, duration)).build();
        try {
            boolean sent = service.dispatchGesture(gesture, new AccessibilityService.GestureResultCallback() {
                @Override public void onCompleted(GestureDescription value) { completed.run(); }
                @Override public void onCancelled(GestureDescription value) { cancelled.run(); }
            }, handler);
            if (!sent) cancelled.run();
        } catch (RuntimeException ex) { trace("System rejected unlock gesture."); cancelled.run(); }
    }

    static void trace(String message) {
        // State transitions only; never PIN digits, node text or key coordinates.
        AndroidSessionLog.info(message);
        android.util.Log.i("RemoteDeskUnlock", message);
    }

    @SuppressWarnings("deprecation")
    private Keypad readKeypad() {
        AccessibilityNodeInfo root;
        try { root = service.getRootInActiveWindow(); }
        catch (RuntimeException ex) { return null; }
        if (root == null) return null;
        try {
            if (!root.refresh()) return null;
            if (!"com.android.systemui".contentEquals(root.getPackageName() == null ? "" : root.getPackageName())) return null;
            Keypad keypad = new Keypad();
            root.getBoundsInScreen(keypad.screen);
            collect(root, keypad, new int[] {0});
            return keypad;
        } catch (RuntimeException ex) {
            return null;
        } finally { root.recycle(); }
    }

    @SuppressWarnings("deprecation")
    private static void collect(AccessibilityNodeInfo node, Keypad keypad, int[] visited) {
        if (++visited[0] > 300) return;
        if (node.isVisibleToUser() && node.isEnabled()) {
            String id = node.getViewIdResourceName();
            if (AndroidPinUnlockPolicy.enteredDigitCount(node.getContentDescription()) >= 0 ||
                    (id != null && id.endsWith(":id/pinEntry"))) {
                // Refresh the field itself: refreshing its root does not evict
                // cached descendants on all OEM accessibility implementations.
                if (!node.refresh()) return;
            }
            int entered = AndroidPinUnlockPolicy.enteredDigitCount(node.getContentDescription());
            if (entered >= 0) { keypad.entered = entered; keypad.empty = entered == 0; }
            if ("com.android.systemui:id/lock_icon_view".equals(id)) keypad.lockPage = true;
            if (id != null && id.endsWith(":id/pinEntry") && node.isPassword() && node.getText() != null) {
                keypad.entered = node.getText().length();
                keypad.empty = keypad.entered == 0;
            }
            if (node.isClickable()) {
                int digit = AndroidPinUnlockPolicy.digitLabel(node.getContentDescription());
                if (digit < 0) digit = AndroidPinUnlockPolicy.digitLabel(node.getText());
                Rect bounds = new Rect();
                node.getBoundsInScreen(bounds);
                if (!bounds.isEmpty()) {
                    if (digit >= 0) keypad.digits[digit] = bounds;
                    if (id != null && (id.endsWith(":id/key_enter") || id.endsWith(":id/key_enter_text"))) keypad.enter = bounds;
                }
            }
        }
        for (int i = 0; i < node.getChildCount() && visited[0] <= 300; i++) {
            AccessibilityNodeInfo child = node.getChild(i);
            if (child != null) try { collect(child, keypad, visited); } finally { child.recycle(); }
        }
    }

    private static final class Keypad {
        final Rect[] digits = new Rect[10];
        boolean empty;
        int entered = -1;
        Rect enter;
        boolean lockPage;
        final Rect screen = new Rect();
        int digitCount() { int count = 0; for (Rect digit : digits) if (digit != null) count++; return count; }
        boolean complete() { for (Rect digit : digits) if (digit == null) return false; return true; }
    }
}
