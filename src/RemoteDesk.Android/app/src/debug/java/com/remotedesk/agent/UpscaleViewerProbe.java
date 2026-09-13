package com.remotedesk.agent;

import android.os.Handler;
import android.os.SystemClock;
import android.graphics.SurfaceTexture;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.io.File;
import java.io.FileOutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

/** Real viewer + synthetic peer test. Fault injection is debug-source-only. */
final class UpscaleViewerProbe implements Runnable {
    private final RemoteDeskViewerActivity viewer;
    private final Handler ui = new Handler(android.os.Looper.getMainLooper());
    private final long deadline = SystemClock.elapsedRealtime() + 25000;
    private int stage;
    private float zoom;
    private UpscaleViewerProbe(RemoteDeskViewerActivity viewer) { this.viewer = viewer; }
    static void start(RemoteDeskViewerActivity viewer) { new UpscaleViewerProbe(viewer).run(); }
    private static Object field(Object target, String name) throws Exception {
        Field field = target.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(target);
    }
    @Override public void run() {
        try {
            if (SystemClock.elapsedRealtime() >= deadline) throw new IllegalStateException("timeout at stage " + stage);
            Object owner = field(viewer, "connectionOwner");
            boolean presented = owner != null && ((AtomicBoolean)field(owner, "h264FirstFramePresented")).get();
            AndroidExperimentalUpscaler renderer = (AndroidExperimentalUpscaler)field(viewer, "experimentalUpscaler");
            boolean enabled = (boolean)field(viewer, "experimentalUpscaling");
            AndroidViewerViewport viewport = (AndroidViewerViewport)field(viewer, "viewport");
            if (stage == 0 && presented) {
                zoom = viewport.zoom;
                Method toggle = RemoteDeskViewerActivity.class.getDeclaredMethod("toggleExperimentalUpscaling");
                toggle.setAccessible(true); toggle.invoke(viewer); stage = 1;
            } else if (stage == 1 && presented && enabled && renderer != null && renderer.getAlpha() == 1f) {
                // Break ONLY our optional renderer's GL attachment. The product
                // must catch its next update failure and resume the old Surface.
                Handler gl = (Handler)field(renderer, "handler");
                gl.post(() -> {
                    try { ((SurfaceTexture)field(renderer, "decoderTexture")).detachFromGLContext(); }
                    catch (Exception error) { android.util.Log.e("RemoteDeskUpscaleProbe", "fault injection", error); }
                });
                stage = 2;
            } else if (stage == 2 && presented && !enabled && renderer == null) {
                if (viewport.zoom != zoom) throw new IllegalStateException("fallback changed zoom");
                finish("passed", "real viewer: enabled GPU output; injected GL failure; original Surface resumed without reconnect or geometry change");
                return;
            }
            ui.postDelayed(this, 150);
        } catch (Exception error) { finish("failed", error.toString()); }
    }
    private void finish(String status, String detail) {
        try (FileOutputStream out = new FileOutputStream(new File(viewer.getFilesDir(), "upscale-viewer-probe.txt"))) {
            out.write((status + "\n" + detail).getBytes(StandardCharsets.UTF_8));
        } catch (Exception ex) { android.util.Log.e("RemoteDeskUpscaleProbe", "write", ex); }
        android.util.Log.i("RemoteDeskUpscaleProbe", status + " " + detail);
        viewer.finish();
    }
}
