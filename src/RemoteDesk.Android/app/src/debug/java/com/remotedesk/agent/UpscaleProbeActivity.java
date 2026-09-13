package com.remotedesk.agent;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.os.Bundle;
import android.view.Surface;
import android.widget.FrameLayout;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

/** Deterministic developer-only color/orientation/resize/recreate probe.
 * The CPU readback exists ONLY in this debug harness, never in the viewer. */
@android.annotation.SuppressLint("SetTextI18n") // Developer-only test result, not product UI.
public final class UpscaleProbeActivity extends Activity {
    private FrameLayout root;
    private AndroidExperimentalUpscaler renderer;
    private int cycle, checks, phase;
    private boolean done;
    private final int[] sizes = {64, 96, 128, 224};
    private final int[] colors = { Color.RED, Color.GREEN, Color.BLUE, Color.WHITE };

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        if (getIntent().getBooleanExtra("viewer", false) && getPackageName().endsWith(".upscaleprobe")) {
            int port = getIntent().getIntExtra("port", 0);
            if (port <= 0 || port > 65535) { finish(); return; }
            try {
                AndroidPasswordStore.saveViewer(this, "RemoteDesk-synthetic-ui-fixture");
                if (getIntent().getBooleanExtra("exercise", false)) {
                    getApplication().registerActivityLifecycleCallbacks(new android.app.Application.ActivityLifecycleCallbacks() {
                        public void onActivityResumed(Activity activity) {
                            if (activity instanceof RemoteDeskViewerActivity) {
                                getApplication().unregisterActivityLifecycleCallbacks(this);
                                UpscaleViewerProbe.start((RemoteDeskViewerActivity)activity);
                            }
                        }
                        public void onActivityCreated(Activity a, Bundle b) { }
                        public void onActivityStarted(Activity a) { }
                        public void onActivityPaused(Activity a) { }
                        public void onActivityStopped(Activity a) { }
                        public void onActivitySaveInstanceState(Activity a, Bundle b) { }
                        public void onActivityDestroyed(Activity a) { }
                    });
                }
                startActivity(new android.content.Intent(this, RemoteDeskViewerActivity.class)
                    .putExtra(RemoteDeskViewerActivity.EXTRA_HOST, "127.0.0.1")
                    .putExtra(RemoteDeskViewerActivity.EXTRA_PORT, port));
            } catch (Exception ex) { android.util.Log.e("RemoteDeskUpscaleProbe", "fixture start failed", ex); }
            finish(); return;
        }
        root = new FrameLayout(this); root.setBackgroundColor(Color.DKGRAY); setContentView(root);
        getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        writeResult("running", "");
        createRenderer();
    }

    private void createRenderer() {
        phase = 0;
        if (renderer != null) { renderer.close(); root.removeView(renderer); }
        renderer = new AndroidExperimentalUpscaler(this, 64, 64, new AndroidExperimentalUpscaler.Listener() {
            public void surfaceChanged(AndroidExperimentalUpscaler view, Surface surface) {
                if (view != renderer || surface == null || done) return;
                try {
                    Canvas canvas = surface.lockCanvas(null);
                    Paint paint = new Paint();
                    for (int i = 0; i < 4; i++) {
                        paint.setColor(colors[i]);
                        canvas.drawRect((i % 2) * 32, (i / 2) * 32, (i % 2 + 1) * 32, (i / 2 + 1) * 32, paint);
                    }
                    surface.unlockCanvasAndPost(canvas);
                } catch (Exception ex) { finishProbe(false, ex.toString()); }
            }
            public void frameDrawn(AndroidExperimentalUpscaler view) {
                runOnUiThread(() -> {
                    if (view != renderer || done) return;
                    view.setAlpha(1f);
                    root.postDelayed(() -> verify(view), 180);
                });
            }
            public void failed(AndroidExperimentalUpscaler view, String reason) { finishProbe(false, reason); }
        });
        renderer.geometry(16, 12, sizes[phase], sizes[phase]);
        root.addView(renderer, new FrameLayout.LayoutParams(256 + cycle, 251 + cycle));
    }

    private void verify(AndroidExperimentalUpscaler view) {
        if (done || view != renderer) return;
        Bitmap bitmap = view.getBitmap();
        if (bitmap == null) { finishProbe(false, "TextureView readback empty"); return; }
        try {
            int size = sizes[phase];
            for (int i=0; i<4; i++) {
                int x = 16 + size * (i%2 == 0 ? 1 : 3) / 4;
                int y = 12 + size * (i/2 == 0 ? 1 : 3) / 4;
                int observed = bitmap.getPixel(x, y), expected = colors[i];
                if (Math.abs(Color.red(observed)-Color.red(expected)) > 12 ||
                    Math.abs(Color.green(observed)-Color.green(expected)) > 12 ||
                    Math.abs(Color.blue(observed)-Color.blue(expected)) > 12)
                    throw new IllegalStateException("cycle="+cycle+" phase="+phase+" quadrant="+i+
                        " expected="+Integer.toHexString(expected)+" observed="+Integer.toHexString(observed));
                checks++;
            }
            int border = bitmap.getPixel(0, 0);
            if (Color.red(border)>10 || Color.green(border)>15 || Color.blue(border)>35)
                throw new IllegalStateException("letterbox was stretched");
            checks++;
        } catch (Exception ex) { finishProbe(false, ex.toString()); return; }
        finally { bitmap.recycle(); }
        phase++;
        if (phase < sizes.length) {
            renderer.geometry(16, 12, sizes[phase], sizes[phase]);
            root.postDelayed(() -> verify(view), 180);
        } else if (++cycle < 8) {
            createRenderer();
        } else finishProbe(true, "8 recreate cycles; native/1.5x/2x/3.5x; orientation, color, margins");
    }

    private void finishProbe(boolean success, String reason) {
        if (done) return;
        done = true;
        writeResult(success ? "passed" : "failed", reason);
        TextView status = new TextView(this);
        status.setText((success ? "PASS " : "FAIL ") + checks + " pixel checks\n" + reason);
        status.setTextColor(Color.WHITE);
        FrameLayout.LayoutParams layout = new FrameLayout.LayoutParams(-1, -2);
        layout.topMargin = 310; root.addView(status, layout);
        android.util.Log.i("RemoteDeskUpscaleProbe", (success ? "PASS " : "FAIL ") + reason);
    }

    private void writeResult(String status, String reason) {
        try (FileOutputStream stream = new FileOutputStream(new File(getFilesDir(), "upscale-probe.txt"))) {
            stream.write((status + "\nchecks=" + checks + "\n" + reason).getBytes(StandardCharsets.UTF_8));
        } catch (Exception ex) { android.util.Log.e("RemoteDeskUpscaleProbe", "result write failed", ex); }
    }

    @Override protected void onDestroy() {
        done = true;
        if (renderer != null) renderer.close();
        super.onDestroy();
    }
}
