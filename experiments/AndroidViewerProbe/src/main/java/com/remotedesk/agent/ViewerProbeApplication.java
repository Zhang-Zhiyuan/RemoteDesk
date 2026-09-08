package com.remotedesk.agent;
import android.app.Activity;
import android.app.Application;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.AtomicFile;
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
            JSONObject data=new JSONObject().put("action",lastAction).put("failure",failure)
                .put("status",chrome.status.getText()).put("health",chrome.health.getText())
                .put("frame",new JSONArray().put(viewport.frameWidth).put(viewport.frameHeight))
                .put("viewport",bounds(frame)).put("header",bounds(chrome.header)).put("dock",bounds(chrome.dock))
                .put("keyboard",chrome.keyboardOpen).put("keyboardPanel",bounds(chrome.keyboardPanel))
                .put("zoom",viewport.zoom).put("scale",viewport.scale()).put("panX",viewport.panX).put("panY",viewport.panY)
                .put("trackpad",gestures.trackpad).put("lockedDrag",gestures.lockedDrag)
                .put("cursor",new JSONArray().put(gestures.cursorX).put(gestures.cursorY))
                .put("h264",field(viewer,"h264SurfaceActive"));
            data.put("configuration", viewer.getResources().getConfiguration().toString())
                .put("density",viewer.getResources().getDisplayMetrics().density)
                .put("healthVisible",chrome.health.getVisibility()).put("hintVisible",chrome.hint.getVisibility())
                .put("headerMeasured",chrome.header.getMeasuredHeight()).put("headerMinimum",chrome.header.getMinimumHeight())
                .put("headerPadding",new JSONArray().put(chrome.header.getPaddingTop()).put(chrome.header.getPaddingBottom()))
                .put("headerParams",chrome.header.getLayoutParams().height);
            Object owner=field(viewer,"connectionOwner");
            data.put("ownerGeneration",owner==null?0:field(owner,"generation"))
                .put("geometryReady",owner!=null && (boolean)field(owner,"displayGeometryReady"))
                .put("surfaceAlpha",((View)field(viewer,"surfaceView")).getAlpha())
                .put("keyboardEnabled",chrome.keyboard.isEnabled()).put("mouseEnabled",chrome.mouse.isEnabled())
                .put("draftLength",chrome.composer.length()).put("sendEnabled",chrome.send.isEnabled());
            if (android.os.Build.VERSION.SDK_INT>=30 && frame.getRootWindowInsets()!=null)
                data.put("imeInset",frame.getRootWindowInsets().getInsets(android.view.WindowInsets.Type.ime()).bottom);
            AtomicFile file=new AtomicFile(new File(getFilesDir(),"viewer-state.json"));
            FileOutputStream stream=file.startWrite();
            try { stream.write(data.toString(2).getBytes(StandardCharsets.UTF_8)); file.finishWrite(stream); }
            catch(Exception ex){ file.failWrite(stream); throw ex; }
        } catch(Exception ex) { failure=ex.toString(); }
        handler.postDelayed(this::snapshot,300);
    }
    public void onActivityCreated(Activity activity,Bundle state) {
        if(activity instanceof RemoteDeskViewerActivity){ viewer=(RemoteDeskViewerActivity)activity; handler.postDelayed(this::snapshot,400); }
    }
    public void onActivityDestroyed(Activity activity){if(activity==viewer){viewer=null;handler.removeCallbacksAndMessages(null);}}
    public void onActivityStarted(Activity a){} public void onActivityResumed(Activity a){}
    public void onActivityPaused(Activity a){} public void onActivityStopped(Activity a){}
    public void onActivitySaveInstanceState(Activity a,Bundle b){}
}
