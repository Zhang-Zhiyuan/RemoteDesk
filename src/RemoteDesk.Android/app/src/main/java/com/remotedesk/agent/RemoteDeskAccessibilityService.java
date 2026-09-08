package com.remotedesk.agent;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
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

    static boolean isEnabled() {
        return instance != null;
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
        RemoteDeskAccessibilityService service = instance;
        if (service == null) {
            return false;
        }

        return service.mainHandler.post(() -> service.performGlobalAction(action));
    }

    static boolean inputTextFromAnyThread(String text) {
        RemoteDeskAccessibilityService service = instance;
        if (service == null || text == null || text.isEmpty()) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance == service) service.replaceFocusedText(text, false);
        });
    }

    static boolean deleteTextBeforeCursorFromAnyThread() {
        RemoteDeskAccessibilityService service = instance;
        if (service == null) {
            return false;
        }

        return service.mainHandler.post(() -> {
            if (instance == service) service.replaceFocusedText("", true);
        });
    }

    @Override
    protected void onServiceConnected() {
        instance = null;
        AndroidInputInjector.onAccessibilityServiceUnavailable();
        instance = this;
    }

    @Override
    public void onDestroy() {
        if (instance == this) {
            instance = null;
        }
        AndroidInputInjector.onAccessibilityServiceUnavailable();

        super.onDestroy();
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
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
}
