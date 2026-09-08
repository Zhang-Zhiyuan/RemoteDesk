package com.remotedesk.agent;

final class AndroidTouchInputPolicy {
    static final long POINTER_MOVE_INTERVAL_MILLIS = 8L;

    private AndroidTouchInputPolicy() {
    }

    static boolean shouldSendMove(long lastMoveSentAtMillis, long eventTimeMillis) {
        return eventTimeMillis < lastMoveSentAtMillis ||
            eventTimeMillis - lastMoveSentAtMillis >= POINTER_MOVE_INTERVAL_MILLIS;
    }

    static int hiddenVideoSurfaceVisibility() {
        // SurfaceView's requestedVisible also requires View.VISIBLE on several
        // Android releases. Alpha hides it without destroying the Surface.
        return android.view.View.VISIBLE;
    }

    static float hiddenVideoSurfaceAlpha() {
        return 0.0f;
    }

    static int jpegVideoSurfaceVisibility() {
        // Some emulator/vendor compositors ignore SurfaceView alpha and leave
        // an opaque black hole above the JPEG ImageView. Once JPEG is actually
        // presenting, destroy that unused Surface instead of relying on alpha.
        return android.view.View.INVISIBLE;
    }
}
