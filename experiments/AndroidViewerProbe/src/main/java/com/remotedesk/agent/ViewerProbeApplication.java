package com.remotedesk.agent;
import android.app.Activity;
import android.app.Application;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Field;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

public final class ViewerProbeApplication extends Application implements Application.ActivityLifecycleCallbacks {
    static RemoteDeskViewerActivity viewer;
    static String lastAction = "launch", failure = "";
    static int surfaceChanges;
    private android.view.SurfaceView observedSurface;
    private final Handler handler = new Handler(Looper.getMainLooper());
    @Override public void onCreate() { super.onCreate(); registerActivityLifecycleCallbacks(this); }
    static Object field(Object target, String name) throws Exception {
        Field field=target.getClass().getDeclaredField(name); field.setAccessible(true); return field.get(target);
    }
    static JSONArray bounds(View view) {
        int[] p=new int[2]; view.getLocationOnScreen(p);
        return new JSONArray().put(p[0]).put(p[1]).put(p[0]+view.getWidth()).put(p[1]+view.getHeight());
    }
    private void snapshot() {
        if (viewer==null) return;
        try {
            AndroidViewerChrome chrome=(AndroidViewerChrome)field(viewer,"chrome");
            AndroidViewerViewport viewport=(AndroidViewerViewport)field(viewer,"viewport");
            AndroidViewerGestures gestures=(AndroidViewerGestures)field(viewer,"gestures");
            View frame=(View)field(viewer,"viewerFrame");
            android.view.SurfaceView surface=(android.view.SurfaceView)field(viewer,"surfaceView");
            if (surface != observedSurface) {
                observedSurface=surface;
                surface.getHolder().addCallback(new android.view.SurfaceHolder.Callback() {
                    public void surfaceCreated(android.view.SurfaceHolder holder) {}
                    public void surfaceDestroyed(android.view.SurfaceHolder holder) {}
                    public void surfaceChanged(android.view.SurfaceHolder holder, int format, int width, int height) { surfaceChanges++; }
                });
            }
            android.graphics.Rect buffer=surface.getHolder().getSurfaceFrame();
            JSONObject data=new JSONObject().put("action",lastAction).put("failure",failure)
                .put("status",chrome.status.getText()).put("health",chrome.health.getText())
                .put("frame",new JSONArray().put(viewport.frameWidth).put(viewport.frameHeight))
                .put("viewport",bounds(frame)).put("header",bounds(chrome.header)).put("dock",bounds(chrome.dock))
                .put("keyboard",chrome.keyboardOpen).put("keyboardPanel",bounds(chrome.keyboardPanel))
                .put("zoom",viewport.zoom).put("scale",viewport.scale()).put("panX",viewport.panX).put("panY",viewport.panY)
                .put("trackpad",gestures.trackpad).put("lockedDrag",gestures.lockedDrag)
                .put("cursor",new JSONArray().put(gestures.cursorX).put(gestures.cursorY))
                .put("h264",field(viewer,"h264SurfaceActive"))
                .put("surfaceFrame", new JSONArray().put(buffer.width()).put(buffer.height()))
                .put("surfaceView", new JSONArray().put(surface.getWidth()).put(surface.getHeight()))
                .put("surfaceChanges", surfaceChanges);
            data.put("configuration", viewer.getResources().getConfiguration().toString())
                .put("density",viewer.getResources().getDisplayMetrics().density)
                .put("healthVisible",chrome.health.getVisibility()).put("hintVisible",chrome.hint.getVisibility())
                .put("headerMeasured",chrome.header.getMeasuredHeight()).put("headerMinimum",chrome.header.getMinimumHeight())
                .put("headerPadding",new JSONArray().put(chrome.header.getPaddingTop()).put(chrome.header.getPaddingBottom()))
                .put("headerParams",chrome.header.getLayoutParams().height);
            Object owner=field(viewer,"connectionOwner");
            data.put("sampleUptimeMs", android.os.SystemClock.elapsedRealtime());
            if (owner != null) {
                data.put("fileBusy", ((java.util.concurrent.atomic.AtomicBoolean) field(owner, "fileBusy")).get());
                data.put("fileSenderActive", field(owner, "fileSender") != null);
                Object uploadLease = field(field(owner, "fileLiveness"), "active");
                // Explicit probe-only negative control. Production never receives
                // this intent or callback; restore its former 18-second policy.
                if (uploadLease != null && viewer.getIntent().getBooleanExtra("legacyFileWatchdogProbe", false))
                    ((AutoCloseable) uploadLease).close();
                data.put("fileWatchdogLease", field(field(owner, "fileLiveness"), "active") != null);
                data.put("fileLegacyDrain", field(field(owner, "fileLiveness"), "legacyDraining"));
                Object health = field(owner, "healthTracker");
                // Observe totals without snapshot(), which advances the real
                // UI's FPS sampling interval. Read consistently under its lock.
                synchronized (health) {
                    data.put("receivedFrames", field(health, "receivedFrameCount"))
                        .put("presentedFrames", field(health, "presentedFrameCount"))
                        .put("receivedEncodedBytes", field(health, "receivedEncodedBytes"));
                }
            }
            data.put("ownerGeneration",owner==null?0:field(owner,"generation"))
                .put("geometryReady",owner!=null && (boolean)field(owner,"displayGeometryReady"))
                .put("surfaceAlpha",((View)field(viewer,"surfaceView")).getAlpha())
                .put("keyboardEnabled",chrome.keyboard.isEnabled()).put("mouseEnabled",chrome.mouse.isEnabled())
                .put("draftLength",chrome.composer.length()).put("sendEnabled",chrome.send.isEnabled())
                .put("composerImeOptions",chrome.composer.getImeOptions());
            if (android.os.Build.VERSION.SDK_INT>=30 && frame.getRootWindowInsets()!=null) {
                data.put("imeInset",frame.getRootWindowInsets().getInsets(android.view.WindowInsets.Type.ime()).bottom)
                    .put("imeInsetSource","WindowInsets");
            } else {
                // API 26-29 has no IME inset type. Observe the actual visible
                // window instead; exclude normal navigation/status bars.
                android.graphics.Rect visible=new android.graphics.Rect();
                frame.getWindowVisibleDisplayFrame(visible);
                android.util.DisplayMetrics display=new android.util.DisplayMetrics();
                viewer.getWindowManager().getDefaultDisplay().getRealMetrics(display);
                int occluded=Math.max(0,display.heightPixels-visible.bottom);
                data.put("imeInset",occluded>display.heightPixels*.15f?occluded:0)
                    .put("imeInsetSource","legacy-visible-frame");
            }
            // The external reader uses adb/cat, not AtomicFile.openRead(). On
            // API 26 AtomicFile exposes a briefly empty base file during writes.
            // Publish only a completely written snapshot via same-directory rename.
            File temporary=new File(getFilesDir(),"viewer-state.json.tmp");
            try(FileOutputStream stream=new FileOutputStream(temporary)) {
                stream.write(data.toString(2).getBytes(StandardCharsets.UTF_8));
            }
            java.nio.file.Files.move(temporary.toPath(),new File(getFilesDir(),"viewer-state.json").toPath(),
                java.nio.file.StandardCopyOption.ATOMIC_MOVE,java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        } catch(Exception ex) { failure=ex.toString(); }
        handler.postDelayed(this::snapshot,300);
    }
    public void onActivityCreated(Activity activity,Bundle state) {
        if (activity instanceof MainActivity main && activity.getIntent().getBooleanExtra("receivedFilesProbe", false))
            handler.postDelayed(() -> ReceivedFilesUiProbe.start(main), 600);
        if(activity instanceof RemoteDeskViewerActivity){
            viewer=(RemoteDeskViewerActivity)activity;
            surfaceChanges=0;
            observedSurface=null;
            handler.postDelayed(this::snapshot,400);
        }
    }
    public void onActivityDestroyed(Activity activity){if(activity==viewer){viewer=null;handler.removeCallbacksAndMessages(null);}}
    public void onActivityStarted(Activity a){} public void onActivityResumed(Activity a){}
    public void onActivityPaused(Activity a){} public void onActivityStopped(Activity a){}
    public void onActivitySaveInstanceState(Activity a,Bundle b){}
}
