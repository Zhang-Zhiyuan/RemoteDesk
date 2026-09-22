package com.remotedesk.agent;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.accessibilityservice.GestureDescription;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.Build;
import android.graphics.Bitmap;
import android.hardware.HardwareBuffer;
import android.view.Display;
import java.util.concurrent.Executor;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;

public final class RemoteDeskAccessibilityService extends AccessibilityService {
    interface GestureDispatchCallback {
        boolean shouldDispatch();

        void onCompleted();

        void onCancelled();

        void onRejected();
    }

    private static volatile RemoteDeskAccessibilityService instance;

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final AndroidRemoteUnlock remoteUnlock = new AndroidRemoteUnlock(this, mainHandler);

    static void requestRemoteWake(java.util.function.BooleanSupplier authorized) {
        RemoteDeskAccessibilityService service = instance;
        if (service != null) service.mainHandler.post(() -> {
            if (instance == service) service.remoteUnlock.request(authorized);
        });
    }

    static boolean isWakeAuthorized(long generation) {
        RemoteDeskAccessibilityService service = instance;
        return service != null && service.remoteUnlock.isAuthorized(generation);
    }

    static void onKeyguardRequested(long generation) {
        RemoteDeskAccessibilityService service = instance;
        if (service != null) service.remoteUnlock.onKeyguardRequested(generation);
    }

    static boolean isEnteringUnlockPin() {
        RemoteDeskAccessibilityService service = instance;
        return service != null && service.remoteUnlock.enteringPin();
    }

    static void cancelRemoteUnlock() {
        RemoteDeskAccessibilityService service = instance;
        if (service != null) service.mainHandler.post(service.remoteUnlock::cancel);
    }

    static boolean isEnabled() {
        return instance != null;
    }

    interface ScreenCallback {
        void onBitmap(Bitmap bitmap);
        void onFailure(int error);
    }

    static boolean canCaptureScreen() {
        return canCaptureScreen(instance);
    }

    private static boolean canCaptureScreen(RemoteDeskAccessibilityService service) {
        if (Build.VERSION.SDK_INT < 30 || service == null || instance != service) return false;
        try {
            AccessibilityServiceInfo info = service.getServiceInfo();
            return info != null &&
                (info.getCapabilities() & AccessibilityServiceInfo.CAPABILITY_CAN_TAKE_SCREENSHOT) != 0;
        } catch (RuntimeException ex) {
            // The system may unbind accessibility while a capture worker is checking it.
            return false;
        }
    }

    static boolean requestScreenshot(Executor executor, ScreenCallback callback) {
        RemoteDeskAccessibilityService service = instance;
        if (Build.VERSION.SDK_INT < 30 || !canCaptureScreen(service)) return false;
        try {
            service.takeScreenshot(Display.DEFAULT_DISPLAY, executor, new TakeScreenshotCallback() {
                @Override public void onSuccess(ScreenshotResult result) {
                    Bitmap hardware = null;
                    Bitmap bitmap = null;
                    try (HardwareBuffer buffer = result.getHardwareBuffer()) {
                        if (instance != service) {
                            callback.onFailure(ERROR_TAKE_SCREENSHOT_NO_ACCESSIBILITY_ACCESS);
                            return;
                        }
                        hardware = Bitmap.wrapHardwareBuffer(buffer, result.getColorSpace());
                        if (hardware == null) throw new IllegalStateException("Missing screenshot buffer");
                        bitmap = hardware.copy(Bitmap.Config.ARGB_8888, false);
                        if (bitmap == null) throw new IllegalStateException("Could not copy screenshot buffer");
                    } catch (RuntimeException ex) {
                        callback.onFailure(1);
                    } finally { if (hardware != null) hardware.recycle(); }
                    if (bitmap != null) callback.onBitmap(bitmap);
                }
                @Override public void onFailure(int error) { callback.onFailure(error); }
            });
            return true;
        } catch (RuntimeException ex) { return false; }
    }

    static boolean dispatchGestureFromAnyThread(GestureDescription gesture) {
        return dispatchGestureFromAnyThread(gesture, null);
    }

    static boolean dispatchGestureFromAnyThread(
        GestureDescription gesture,
        GestureDispatchCallback callback) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance != service || !shouldDispatchGesture(callback)) {
                notifyGestureRejected(callback);
                return;
            }
            boolean dispatched;
            try {
                dispatched = service.dispatchGesture(
                    gesture,
                    callback == null
                        ? null
                        : new GestureResultCallback() {
                            @Override
                            public void onCompleted(GestureDescription completedGesture) {
                                notifyGestureCompleted(callback);
                            }

                            @Override
                            public void onCancelled(GestureDescription cancelledGesture) {
                                notifyGestureCancelled(callback);
                            }
                        },
                    service.mainHandler);
            } catch (RuntimeException ex) {
                AndroidSessionLog.error("Accessibility gesture dispatch failed.", ex);
                dispatched = false;
            }
            if (!dispatched) {
                notifyGestureRejected(callback);
            }
        });
    }

    static boolean performGlobalActionFromAnyThread(int action) {
        return performGlobalActionFromAnyThread(action, () -> true);
    }

    static boolean performGlobalActionFromAnyThread(int action, java.util.function.BooleanSupplier authorized) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance != service || !authorized.getAsBoolean()) return;
            try { service.performGlobalAction(action); }
            catch (RuntimeException ex) { AndroidSessionLog.error("Accessibility global action failed.", ex); }
        });
    }

    static boolean inputTextFromAnyThread(String text) {
        return inputTextFromAnyThread(text, () -> true);
    }

    static boolean inputTextFromAnyThread(String text, java.util.function.BooleanSupplier authorized) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null || text == null || text.isEmpty()) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance == service && authorized.getAsBoolean()) service.replaceFocusedText(text, false);
        });
    }

    static boolean clipboardActionFromAnyThread(int virtualKey, java.util.function.BooleanSupplier authorized) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null) return false;
        return service.mainHandler.post(() -> {
            if (instance == service && authorized.getAsBoolean()) service.performClipboardAction(virtualKey);
        });
    }

    @SuppressWarnings("deprecation")
    private void performClipboardAction(int virtualKey) {
        AccessibilityNodeInfo node = findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        if (node == null) return;
        try {
            if (!node.refresh() || !node.isFocused()) return;
            if (virtualKey != 0x56 && node.isPassword()) return;
            if (virtualKey == 0x41) {
                CharSequence text = node.getText();
                if (text == null || node.isShowingHintText()) return;
                Bundle arguments = new Bundle();
                arguments.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, 0);
                arguments.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, text.length());
                node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, arguments);
            } else {
                int action = virtualKey == 0x56 ? AccessibilityNodeInfo.ACTION_PASTE :
                    virtualKey == 0x58 ? AccessibilityNodeInfo.ACTION_CUT : AccessibilityNodeInfo.ACTION_COPY;
                node.performAction(action);
            }
        } catch (RuntimeException failure) {
            AndroidSessionLog.error("Focused clipboard action failed.", failure);
        } finally { node.recycle(); }
    }

    static boolean deleteTextBeforeCursorFromAnyThread() {
        return deleteTextBeforeCursorFromAnyThread(() -> true);
    }

    static boolean deleteTextBeforeCursorFromAnyThread(java.util.function.BooleanSupplier authorized) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance == service && authorized.getAsBoolean()) service.replaceFocusedText("", true);
        });
    }

    @Override
    protected void onServiceConnected() {
        instance = null;
        AndroidInputInjector.onAccessibilityServiceUnavailable();
        if (Build.VERSION.SDK_INT >= 33) {
            AccessibilityServiceInfo info = getServiceInfo();
            if (info != null) {
                info.flags |= AccessibilityServiceInfo.FLAG_INPUT_METHOD_EDITOR;
                setServiceInfo(info);
            }
        }
        instance = this;
        AndroidHostResume.tryResume(this);
    }

    @Override
    public void onDestroy() {
        detachInstance();
        super.onDestroy();
    }

    @Override
    public boolean onUnbind(Intent intent) {
        detachInstance();
        return super.onUnbind(intent);
    }

    private void detachInstance() {
        remoteUnlock.cancel();
        if (instance == this) {
            instance = null;
            AndroidInputInjector.onAccessibilityServiceUnavailable();
        }
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        remoteUnlock.observeUnlocked();
    }

    @Override
    public void onInterrupt() {
        AndroidInputInjector.onAccessibilityServiceUnavailable();
    }

    private static void notifyGestureCompleted(GestureDispatchCallback callback) {
        if (callback == null) {
            return;
        }
        try {
            callback.onCompleted();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Accessibility completion callback failed.", ex);
        }
    }

    private static boolean shouldDispatchGesture(GestureDispatchCallback callback) {
        if (callback == null) {
            return true;
        }
        try {
            return callback.shouldDispatch();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Accessibility dispatch gate failed.", ex);
            return false;
        }
    }

    private static void notifyGestureCancelled(GestureDispatchCallback callback) {
        if (callback == null) {
            return;
        }
        try {
            callback.onCancelled();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Accessibility cancellation callback failed.", ex);
        }
    }

    private static void notifyGestureRejected(GestureDispatchCallback callback) {
        if (callback == null) {
            return;
        }
        try {
            callback.onRejected();
        } catch (RuntimeException ex) {
            AndroidSessionLog.error("Accessibility rejection callback failed.", ex);
        }
    }

    @SuppressWarnings("deprecation")
    private void replaceFocusedText(String insertedText, boolean deleteBeforeCursor) {
        AccessibilityNodeInfo node = findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        if (node == null) {
            return;
        }

        try {
            // A burst of input can outrun TYPE_VIEW_TEXT_CHANGED events. Bypass
            // the node cache before each edit or later characters overwrite
            // earlier ones using the same stale value/selection.
            if (!node.refresh() || !node.isFocused() || !node.isEditable()) {
                return;
            }
            if (Build.VERSION.SDK_INT >= 33 && commitFocusedInput(node, insertedText, deleteBeforeCursor)) return;
            // Password nodes may expose only bullet characters. Never rebuild
            // the real password using that masked value on older/OEM editors.
            if (node.isPassword() && !AndroidFocusedTextEdit.canReplacePassword(
                    node.getText(), node.isShowingHintText(), node.getTextSelectionStart(), node.getTextSelectionEnd())) return;
            AndroidFocusedTextEdit edit = AndroidFocusedTextEdit.create(
                node.getText(), node.isShowingHintText(), node.getTextSelectionStart(),
                node.getTextSelectionEnd(), insertedText, deleteBeforeCursor);
            Bundle setTextArgs = new Bundle();
            setTextArgs.putCharSequence(
                AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,
                edit.text);
            if (node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, setTextArgs)) {
                Bundle selectionArgs = new Bundle();
                selectionArgs.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, edit.cursor);
                selectionArgs.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, edit.cursor);
                node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, selectionArgs);
            }
        } catch (RuntimeException error) {
            // Do not log the field's text or the inserted text.
            AndroidSessionLog.info("Accessibility focused text edit failed: " + error.getClass().getSimpleName());
        } finally {
            node.recycle();
        }
    }

    @androidx.annotation.RequiresApi(33)
    private boolean commitFocusedInput(AccessibilityNodeInfo node, String text, boolean delete) {
        android.accessibilityservice.InputMethod method = getInputMethod();
        if (method == null) return false;
        android.view.inputmethod.EditorInfo editor = method.getCurrentInputEditorInfo();
        android.accessibilityservice.InputMethod.AccessibilityInputConnection connection = method.getCurrentInputConnection();
        if (editor == null || connection == null || !android.text.TextUtils.equals(editor.packageName, node.getPackageName())) return false;
        // Let the editor retain its real contents, selection, composition and
        // password transformation. Do not route secrets through the clipboard.
        if (delete) {
            long now = android.os.SystemClock.uptimeMillis();
            for (int action : new int[] {android.view.KeyEvent.ACTION_DOWN, android.view.KeyEvent.ACTION_UP})
                connection.sendKeyEvent(new android.view.KeyEvent(now, now, action,
                    android.view.KeyEvent.KEYCODE_DEL, 0, 0, android.view.KeyCharacterMap.VIRTUAL_KEYBOARD,
                    0, android.view.KeyEvent.FLAG_SOFT_KEYBOARD));
        } else connection.commitText(text, 1, null);
        return true;
    }
}
