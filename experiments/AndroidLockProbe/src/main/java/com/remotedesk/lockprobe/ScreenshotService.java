package com.remotedesk.lockprobe;

import android.accessibilityservice.AccessibilityService;
import android.app.KeyguardManager;
import android.graphics.Bitmap;
import android.hardware.HardwareBuffer;
import android.os.PowerManager;
import android.view.Display;
import android.view.accessibility.AccessibilityEvent;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import org.json.JSONObject;

public final class ScreenshotService extends AccessibilityService {
    private static volatile ScreenshotService instance;
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final AtomicBoolean busy = new AtomicBoolean();

    @Override protected void onServiceConnected() { instance = this; }
    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }
    @Override public void onInterrupt() { }
    @Override public void onDestroy() {
        if (instance == this) instance = null;
        executor.shutdown();
        super.onDestroy();
    }

    static void captureOnce() {
        ScreenshotService service = instance;
        if (service == null || !service.busy.compareAndSet(false, true)) return;
        try {
            service.takeScreenshot(Display.DEFAULT_DISPLAY, service.executor,
                new TakeScreenshotCallback() {
                    @Override public void onSuccess(ScreenshotResult result) {
                        Bitmap hardware = null;
                        Bitmap copy = null;
                        try (HardwareBuffer buffer = result.getHardwareBuffer()) {
                            hardware = Bitmap.wrapHardwareBuffer(buffer, result.getColorSpace());
                            if (hardware == null) throw new IllegalStateException("No bitmap");
                            copy = hardware.copy(Bitmap.Config.ARGB_8888, false);
                            try (FileOutputStream output = service.openFileOutput("frame.png", MODE_PRIVATE)) {
                                if (!copy.compress(Bitmap.CompressFormat.PNG, 100, output))
                                    throw new IllegalStateException("PNG encode failed");
                            }
                            service.record(true, 0, copy.getWidth(), copy.getHeight());
                        } catch (Exception error) {
                            service.record(false, -1, 0, 0);
                        } finally {
                            if (copy != null) copy.recycle();
                            if (hardware != null) hardware.recycle();
                            service.busy.set(false);
                        }
                    }
                    @Override public void onFailure(int error) {
                        service.record(false, error, 0, 0);
                        service.busy.set(false);
                    }
                });
        } catch (RuntimeException error) {
            service.record(false, -2, 0, 0);
            service.busy.set(false);
        }
    }

    private void record(boolean success, int error, int width, int height) {
        try {
            JSONObject state = new JSONObject();
            state.put("timeMillis", System.currentTimeMillis());
            state.put("success", success);
            state.put("error", error);
            state.put("width", width);
            state.put("height", height);
            state.put("locked", getSystemService(KeyguardManager.class).isKeyguardLocked());
            state.put("interactive", getSystemService(PowerManager.class).isInteractive());
            try (FileOutputStream output = openFileOutput("result.json", MODE_PRIVATE)) {
                output.write(state.toString().getBytes(StandardCharsets.UTF_8));
            }
        } catch (Exception ignored) { }
    }
}
