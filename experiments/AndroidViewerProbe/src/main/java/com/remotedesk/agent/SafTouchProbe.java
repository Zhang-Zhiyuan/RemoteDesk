package com.remotedesk.agent;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.MotionEvent;

/** Shell-only owned-emulator fixture: API 30's `input tap` uses TOOL_TYPE_UNKNOWN. */
public final class SafTouchProbe {
    private SafTouchProbe() { }

    public static void main(String[] args) throws Exception {
        if (args.length != 3) throw new IllegalArgumentException("x y holdMillis");
        float x = Float.parseFloat(args[0]);
        float y = Float.parseFloat(args[1]);
        long holdMillis = Long.parseLong(args[2]);
        if (x < 0 || y < 0 || holdMillis < 0 || holdMillis > 2000) {
            throw new IllegalArgumentException("Invalid test touch");
        }
        Class<?> managerClass = Class.forName("android.hardware.input.InputManager");
        Object manager = managerClass.getMethod("getInstance").invoke(null);
        java.lang.reflect.Method inject = managerClass.getMethod("injectInputEvent", InputEvent.class, int.class);
        MotionEvent.PointerProperties properties = new MotionEvent.PointerProperties();
        properties.id = 0;
        properties.toolType = MotionEvent.TOOL_TYPE_FINGER;
        MotionEvent.PointerCoords coordinates = new MotionEvent.PointerCoords();
        coordinates.x = x;
        coordinates.y = y;
        coordinates.pressure = 1;
        coordinates.size = 1;
        long downTime = SystemClock.uptimeMillis();
        for (int action : new int[] {MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP}) {
            if (action == MotionEvent.ACTION_UP) {
                SystemClock.sleep(Math.max(50, holdMillis));
                coordinates.pressure = 0;
            }
            MotionEvent event = MotionEvent.obtain(downTime, SystemClock.uptimeMillis(), action, 1,
                    new MotionEvent.PointerProperties[] {properties},
                    new MotionEvent.PointerCoords[] {coordinates}, 0, 0, 1, 1, 0, 0,
                    InputDevice.SOURCE_TOUCHSCREEN, 0);
            try {
                if (!Boolean.TRUE.equals(inject.invoke(manager, event, 2))) {
                    throw new IllegalStateException("Touch injection rejected");
                }
            } finally {
                event.recycle();
            }
        }
        System.out.println("Injected TOOL_TYPE_FINGER at " + x + "," + y);
    }
}
