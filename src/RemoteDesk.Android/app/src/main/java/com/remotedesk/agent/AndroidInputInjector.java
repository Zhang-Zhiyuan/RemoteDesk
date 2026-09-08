package com.remotedesk.agent;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.graphics.Path;
import android.graphics.PointF;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Set;
import java.util.WeakHashMap;

final class AndroidInputInjector {
    private static final int INPUT_PAYLOAD_LENGTH = 14;
    private static final int INPUT_MOUSE_MOVE = 1;
    private static final int INPUT_MOUSE_DOWN = 2;
    private static final int INPUT_MOUSE_UP = 3;
    private static final int INPUT_MOUSE_WHEEL = 4;
    static final int INPUT_KEY_DOWN = 5;
    static final int INPUT_KEY_UP = 6;
    static final int INPUT_TEXT = 7;
    private static final int INPUT_PINCH_ZOOM = 8;
    private static final int MOUSE_BUTTON_LEFT = 1;
    private static final long DRAG_SEGMENT_DURATION_MILLIS = 40;
    private static final int VK_BACK = 8;
    private static final int VK_TAB = 9;
    private static final int VK_ESCAPE = 27;
    private static final int VK_HOME = 36;
    private static final int VK_F11 = 122;
    private static final int VK_F12 = 123;
    private static final Set<GestureState> gestureStates =
        Collections.newSetFromMap(new WeakHashMap<>());
    private static final Object gestureLifecycleLock = new Object();

    private AndroidInputInjector() {
    }

    static boolean isEnabled() {
        return RemoteDeskAccessibilityService.isEnabled();
    }

    static boolean apply(
        byte[] payload,
        int frameWidth,
        int frameHeight,
        int sourceWidth,
        int sourceHeight) {
        return apply(
            payload,
            frameWidth,
            frameHeight,
            sourceWidth,
            sourceHeight,
            LegacyGestureStateHolder.INSTANCE);
    }

    static boolean apply(
        byte[] payload,
        int frameWidth,
        int frameHeight,
        int sourceWidth,
        int sourceHeight,
        GestureState gestureState) {
        if (gestureState == null) {
            return false;
        }
        if (!isEnabled() ||
            payload.length != INPUT_PAYLOAD_LENGTH) {
            return false;
        }

        int kind = payload[0] & 0xFF;
        int button = payload[1] & 0xFF;
        int frameX = readInt32LittleEndian(payload, 2);
        int frameY = readInt32LittleEndian(payload, 6);
        int data = readInt32LittleEndian(payload, 10);

        if (kind == INPUT_TEXT) {
            return dispatchTextInput(data);
        }

        if (kind == INPUT_KEY_DOWN) {
            return dispatchKeyAction(data);
        }

        if (kind == INPUT_KEY_UP) {
            // Android global/focused-field actions are committed on key-down;
            // consuming key-up here keeps the reliable wire pair harmless.
            return true;
        }

        if (!requiresFrameGeometry(kind)) {
            return false;
        }

        if (frameWidth <= 0 ||
            frameHeight <= 0 ||
            sourceWidth <= 0 ||
            sourceHeight <= 0) {
            return false;
        }

        PointF point = mapPoint(frameX, frameY, frameWidth, frameHeight, sourceWidth, sourceHeight);

        switch (kind) {
            case INPUT_MOUSE_DOWN:
                if (button == MOUSE_BUTTON_LEFT) {
                    return beginTouch(gestureState, point);
                }
                return false;
            case INPUT_MOUSE_MOVE:
                // A move is accepted once it enters the host-side latest-only
                // continued-stroke pump. Accessibility completion is
                // asynchronous and is deliberately not misreported as an
                // applied acknowledgement by networking.
                return updateTouch(gestureState, point);
            case INPUT_MOUSE_UP:
                if (button == MOUSE_BUTTON_LEFT) {
                    return endTouch(gestureState, point);
                }
                return false;
            case INPUT_MOUSE_WHEEL:
                return dispatchScroll(point, data, sourceHeight);
            case INPUT_PINCH_ZOOM:
                return dispatchPinchZoom(point, data, sourceWidth, sourceHeight);
            default:
                return false;
        }
    }

    static void resetSessionState() {
        LegacyGestureStateHolder.INSTANCE.forceReset();
    }

    static void onAccessibilityServiceUnavailable() {
        synchronized (gestureLifecycleLock) {
            ArrayList<GestureState> snapshot;
            synchronized (gestureStates) {
                snapshot = new ArrayList<>(gestureStates);
            }
            for (GestureState state : snapshot) {
                state.forceReset();
            }
        }
    }

    static boolean requiresFrameGeometry(int kind) {
        return kind != INPUT_TEXT &&
            kind != INPUT_KEY_DOWN &&
            kind != INPUT_KEY_UP;
    }

    private static boolean beginTouch(GestureState state, PointF point) {
        synchronized (gestureLifecycleLock) {
            return isEnabled() && state.pump.beginSession(point.x, point.y);
        }
    }

    private static boolean updateTouch(GestureState state, PointF point) {
        return state.pump.move(point.x, point.y);
    }

    private static boolean endTouch(GestureState state, PointF point) {
        return state.pump.end(point.x, point.y);
    }

    private static boolean dispatchScroll(PointF point, int wheelDelta, int sourceHeight) {
        if (wheelDelta == 0) {
            return false;
        }

        float distance = Math.max(80, Math.min(360, sourceHeight * 0.18f));
        float direction = wheelDelta > 0 ? 1 : -1;
        float startY = clamp(point.y + direction * distance / 2, 1, sourceHeight - 2);
        float endY = clamp(point.y - direction * distance / 2, 1, sourceHeight - 2);

        Path path = new Path();
        path.moveTo(point.x, startY);
        path.lineTo(point.x, endY);
        return dispatch(path, 220);
    }

    private static boolean dispatch(Path path, long durationMillis) {
        GestureDescription.StrokeDescription stroke =
            new GestureDescription.StrokeDescription(path, 0, Math.max(40, durationMillis));
        GestureDescription gesture = new GestureDescription.Builder()
            .addStroke(stroke)
            .build();
        return RemoteDeskAccessibilityService.dispatchGestureFromAnyThread(gesture);
    }

    private static boolean dispatchPinchZoom(PointF center, int wheelDelta, int sourceWidth, int sourceHeight) {
        if (wheelDelta == 0 || sourceWidth <= 8 || sourceHeight <= 8) {
            return false;
        }

        float availableX = Math.max(12, Math.min(center.x - 2, sourceWidth - 3 - center.x));
        float maxRadius = Math.max(24, Math.min(Math.min(sourceWidth, sourceHeight) * 0.22f, availableX));
        float minRadius = Math.max(12, maxRadius * 0.42f);
        if (maxRadius <= minRadius + 4) {
            maxRadius = minRadius + 8;
        }

        boolean zoomIn = wheelDelta > 0;
        float startRadius = zoomIn ? minRadius : maxRadius;
        float endRadius = zoomIn ? maxRadius : minRadius;
        Path first = new Path();
        first.moveTo(clamp(center.x - startRadius, 1, sourceWidth - 2), center.y);
        first.lineTo(clamp(center.x - endRadius, 1, sourceWidth - 2), center.y);

        Path second = new Path();
        second.moveTo(clamp(center.x + startRadius, 1, sourceWidth - 2), center.y);
        second.lineTo(clamp(center.x + endRadius, 1, sourceWidth - 2), center.y);

        GestureDescription gesture = new GestureDescription.Builder()
            .addStroke(new GestureDescription.StrokeDescription(first, 0, 240))
            .addStroke(new GestureDescription.StrokeDescription(second, 0, 240))
            .build();
        return RemoteDeskAccessibilityService.dispatchGestureFromAnyThread(gesture);
    }

    private static boolean dispatchKeyAction(int virtualKey) {
        switch (virtualKey) {
            case VK_BACK:
                return RemoteDeskAccessibilityService.deleteTextBeforeCursorFromAnyThread();
            case VK_ESCAPE:
                return RemoteDeskAccessibilityService.performGlobalActionFromAnyThread(AccessibilityService.GLOBAL_ACTION_BACK);
            case VK_HOME:
                return RemoteDeskAccessibilityService.performGlobalActionFromAnyThread(AccessibilityService.GLOBAL_ACTION_HOME);
            case VK_F12:
                return RemoteDeskAccessibilityService.performGlobalActionFromAnyThread(AccessibilityService.GLOBAL_ACTION_RECENTS);
            case VK_F11:
                return RemoteDeskAccessibilityService.performGlobalActionFromAnyThread(AccessibilityService.GLOBAL_ACTION_NOTIFICATIONS);
            case VK_TAB:
                return RemoteDeskAccessibilityService.performGlobalActionFromAnyThread(AccessibilityService.GLOBAL_ACTION_QUICK_SETTINGS);
            default:
                return false;
        }
    }

    private static boolean dispatchTextInput(int codePoint) {
        if (!Character.isValidCodePoint(codePoint) || isUnsupportedTextControl(codePoint)) {
            return false;
        }

        return RemoteDeskAccessibilityService.inputTextFromAnyThread(
            new String(Character.toChars(codePoint)));
    }

    private static boolean isUnsupportedTextControl(int codePoint) {
        return Character.isISOControl(codePoint) &&
            codePoint != '\n' &&
            codePoint != '\t';
    }

    private static PointF mapPoint(
        int frameX,
        int frameY,
        int frameWidth,
        int frameHeight,
        int sourceWidth,
        int sourceHeight) {
        float xRatio = frameWidth <= 1 ? 0 : frameX / (float) (frameWidth - 1);
        float yRatio = frameHeight <= 1 ? 0 : frameY / (float) (frameHeight - 1);
        return new PointF(
            clamp(xRatio * (sourceWidth - 1), 0, sourceWidth - 1),
            clamp(yRatio * (sourceHeight - 1), 0, sourceHeight - 1));
    }

    private static float clamp(float value, float min, float max) {
        return Math.max(min, Math.min(max, value));
    }

    private static int readInt32LittleEndian(byte[] buffer, int offset) {
        return (buffer[offset] & 0xFF) |
            ((buffer[offset + 1] & 0xFF) << 8) |
            ((buffer[offset + 2] & 0xFF) << 16) |
            ((buffer[offset + 3] & 0xFF) << 24);
    }

    static final class GestureState {
        private final AndroidDragGestureDispatcher dispatcher =
            new AndroidDragGestureDispatcher();
        private final AndroidDragGesturePump pump =
            new AndroidDragGesturePump(dispatcher);

        GestureState() {
            synchronized (gestureStates) {
                gestureStates.add(this);
            }
        }

        void reset() {
            pump.cancel();
        }

        void forceReset() {
            pump.forceCancel();
        }

        boolean closeGracefully(long timeoutMillis) {
            return pump.cancelAndAwaitIdle(timeoutMillis);
        }
    }

    private static final class AndroidDragGestureDispatcher
        implements AndroidDragGesturePump.Dispatcher {
        private final Object lock = new Object();
        private GestureDescription.StrokeDescription activeStroke;
        private long activeGeneration;

        @Override
        public boolean dispatch(
            AndroidDragGesturePump.Segment segment,
            AndroidDragGesturePump.ResultCallback callback) {
            GestureDescription.StrokeDescription stroke;
            try {
                Path path = new Path();
                path.moveTo(segment.startX, segment.startY);
                // Keep the endpoint explicit for both moving and stationary
                // segments. A stationary first/final segment preserves click
                // semantics without manufacturing any extra displacement.
                path.lineTo(segment.endX, segment.endY);
                synchronized (lock) {
                    if (segment.first) {
                        activeGeneration = segment.generation;
                        stroke = new GestureDescription.StrokeDescription(
                            path,
                            0,
                            DRAG_SEGMENT_DURATION_MILLIS,
                            segment.willContinue);
                    } else {
                        if (activeStroke == null ||
                            activeGeneration != segment.generation) {
                            return false;
                        }
                        stroke = activeStroke.continueStroke(
                            path,
                            0,
                            DRAG_SEGMENT_DURATION_MILLIS,
                            segment.willContinue);
                    }
                    activeStroke = stroke;
                }
            } catch (RuntimeException ex) {
                clearIfCurrent(segment.generation);
                AndroidSessionLog.error("Could not build a continued drag stroke.", ex);
                return false;
            }

            GestureDescription gesture = new GestureDescription.Builder()
                .addStroke(stroke)
                .build();
            GestureDescription.StrokeDescription dispatchedStroke = stroke;
            boolean posted = RemoteDeskAccessibilityService.dispatchGestureFromAnyThread(
                gesture,
                new RemoteDeskAccessibilityService.GestureDispatchCallback() {
                    @Override
                    public boolean shouldDispatch() {
                        synchronized (lock) {
                            return activeGeneration == segment.generation &&
                                activeStroke == dispatchedStroke;
                        }
                    }

                    @Override
                    public void onCompleted() {
                        if (!segment.willContinue) {
                            clearIfCurrent(segment.generation);
                        }
                        callback.onCompleted();
                    }

                    @Override
                    public void onCancelled() {
                        clearIfCurrent(segment.generation);
                        callback.onCancelled();
                    }

                    @Override
                    public void onRejected() {
                        clearIfCurrent(segment.generation);
                        callback.onRejected();
                    }
                });
            if (!posted) {
                clearIfCurrent(segment.generation);
            }
            return posted;
        }

        @Override
        public void reset(long retiredGeneration) {
            clearIfCurrent(retiredGeneration);
        }

        private void clearIfCurrent(long expectedGeneration) {
            synchronized (lock) {
                if (activeGeneration != expectedGeneration) {
                    return;
                }
                activeStroke = null;
                activeGeneration = 0;
            }
        }
    }

    private static final class LegacyGestureStateHolder {
        private static final GestureState INSTANCE = new GestureState();
    }
}
