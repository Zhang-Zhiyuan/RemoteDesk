package com.remotedesk.agent;

/** UI-thread gesture state, independently testable without Android MotionEvents. */
final class AndroidViewerGestures {
    interface Sink { void send(int kind, int button, int x, int y, int data); }
    static boolean maySend(int kind, boolean currentOwnerCanControl, boolean geometryReady) {
        // A screen transition blocks new clicks, but must not swallow the
        // release for a button held on the previous screen of this same owner.
        return currentOwnerCanControl && (geometryReady || kind == RemoteDeskProtocol.INPUT_MOUSE_UP);
    }
    final AndroidViewerViewport viewport;
    private final Sink sink;
    private final float slop, wheelStep;
    boolean trackpad = true, dragging, lockedDrag;
    float cursorX, cursorY;
    private float downX, downY, lastX, lastY, firstSpan, lastSpan, wheel;
    private long downTime;
    private boolean active, moved, multi, multiEnded, singleAllowed, multiTap;
    private int multiMode; // 0 undecided, 1 scrolling, 2 local pinch/pan

    AndroidViewerGestures(AndroidViewerViewport viewport, Sink sink, float density) {
        this.viewport = viewport; this.sink = sink;
        slop = Math.max(4, 8 * density); wheelStep = Math.max(12, 24 * density);
    }

    void centerCursor() { cursorX = viewport.frameWidth / 2f; cursorY = viewport.frameHeight / 2f; }
    void mode(boolean useTrackpad) { cancel(); trackpad = useTrackpad; }

    void down(float x, float y, long time) {
        cancelTouch();
        active = viewport.scale() > 0;
        downX = lastX = x; downY = lastY = y; downTime = time;
        moved = multi = multiEnded = false; multiMode = 0; wheel = 0;
        singleAllowed = true; multiTap = false;
        if (!trackpad && active) {
            int[] point = viewport.point(x, y, true);
            // Keep the touch sequence alive for a LOCAL pinch from a black
            // margin, without making that margin a remote click/scroll target.
            singleAllowed = point != null;
            if (singleAllowed) { cursorX = point[0]; cursorY = point[1]; movePointer(); }
        }
    }

    void move(float x, float y) {
        if (!active || multi || !singleAllowed) return;
        boolean crossed = Math.hypot(x - downX, y - downY) > slop;
        if (crossed && !moved && !trackpad && !lockedDrag) beginDrag();
        moved |= crossed;
        if (moved || dragging || lockedDrag) {
            if (trackpad) {
                cursorX += (x - lastX) / viewport.scale();
                cursorY += (y - lastY) / viewport.scale();
            } else {
                int[] point = viewport.point(x, y, false);
                if (point != null) { cursorX = point[0]; cursorY = point[1]; }
            }
            cursorX = AndroidViewerViewport.clamp(cursorX, 0, viewport.frameWidth - 1);
            cursorY = AndroidViewerViewport.clamp(cursorY, 0, viewport.frameHeight - 1);
            viewport.reveal(cursorX, cursorY);
            movePointer();
        }
        lastX = x; lastY = y;
    }

    boolean longPress() {
        if (!active || !singleAllowed || moved || multi || lockedDrag) return false;
        beginDrag(); return true;
    }

    void secondDown(float centerX, float centerY, float span) {
        if (!active) return;
        // Once a two-finger sequence has started, replacing a lifted finger is
        // not a fresh tap. Keep its scroll/pinch decision until all fingers lift.
        if (multi) { multiTap = false; return; }
        multiTap = singleAllowed && !moved && !dragging && !lockedDrag;
        releaseDrag();
        if (lockedDrag) toggleDrag();
        multi = true; multiMode = 0;
        downX = lastX = centerX; downY = lastY = centerY;
        firstSpan = lastSpan = Math.max(1, span);
    }

    void multiMove(float x, float y, float span) {
        if (!active || !multi || multiEnded) return;
        float distance = (float) Math.hypot(x - downX, y - downY);
        if (multiMode == 0) {
            if (Math.abs(span - firstSpan) > Math.max(slop * 1.5f, distance * 0.75f)) multiMode = 2;
            else if (distance > slop) multiMode = 1;
            // Until intent is known, retain the original span/centroid. Updating
            // lastSpan on every tiny movement loses the beginning of a pinch.
            if (multiMode == 0) return;
        }
        if (multiMode == 2) {
            viewport.zoomAt(span / Math.max(1, lastSpan), lastX, lastY);
            viewport.pan(x - lastX, y - lastY);
        } else if (multiMode == 1 && singleAllowed) {
            wheel += y - lastY;
            int steps = (int) (wheel / wheelStep);
            if (steps != 0) {
                emit(RemoteDeskProtocol.INPUT_MOUSE_WHEEL, RemoteDeskProtocol.MOUSE_NONE,
                     Math.max(-10, Math.min(10, steps)) * 120);
                wheel -= steps * wheelStep;
            }
        }
        lastX = x; lastY = y; lastSpan = Math.max(1, span);
    }

    void pointerUp() { if (multi) multiEnded = true; }

    void up(float x, float y, long time) {
        if (!active) return;
        if (multi) {
            if (multiTap && multiMode == 0 && time - downTime < 350) click(RemoteDeskProtocol.MOUSE_RIGHT);
        } else if (dragging) {
            move(x, y); releaseDrag();
        } else if (singleAllowed && !moved && !lockedDrag && time - downTime < 500) {
            click(RemoteDeskProtocol.MOUSE_LEFT);
        }
        active = false;
    }

    void click(int button) {
        if (lockedDrag) toggleDrag();
        emit(RemoteDeskProtocol.INPUT_MOUSE_DOWN, button, 0);
        emit(RemoteDeskProtocol.INPUT_MOUSE_UP, button, 0);
    }

    void toggleDrag() {
        releaseDrag(); lockedDrag = !lockedDrag;
        emit(lockedDrag ? RemoteDeskProtocol.INPUT_MOUSE_DOWN : RemoteDeskProtocol.INPUT_MOUSE_UP,
             RemoteDeskProtocol.MOUSE_LEFT, 0);
    }

    void cancel() { cancelTouch(); if (lockedDrag) toggleDrag(); }
    private void cancelTouch() { releaseDrag(); active = false; }
    private void beginDrag() { if (!dragging) { dragging = true; emit(RemoteDeskProtocol.INPUT_MOUSE_DOWN, RemoteDeskProtocol.MOUSE_LEFT, 0); } }
    private void releaseDrag() { if (dragging) { dragging = false; emit(RemoteDeskProtocol.INPUT_MOUSE_UP, RemoteDeskProtocol.MOUSE_LEFT, 0); } }
    private void movePointer() { emit(RemoteDeskProtocol.INPUT_MOUSE_MOVE, RemoteDeskProtocol.MOUSE_NONE, 0); }
    private void emit(int kind, int button, int data) { sink.send(kind, button, Math.round(cursorX), Math.round(cursorY), data); }
}
