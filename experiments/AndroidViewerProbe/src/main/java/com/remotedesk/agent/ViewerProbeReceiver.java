package com.remotedesk.agent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.View;

public final class ViewerProbeReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context,Intent intent) {
        RemoteDeskViewerActivity viewer=ViewerProbeApplication.viewer;
        if(viewer==null)return;
        String action=intent.getStringExtra("action");
        ViewerProbeApplication.lastAction=action;
        try {
            AndroidViewerChrome chrome=(AndroidViewerChrome)ViewerProbeApplication.field(viewer,"chrome");
            View frame=(View)ViewerProbeApplication.field(viewer,"viewerFrame");
            if ("gesture_case".equals(action)) {
                AndroidViewerGestures gestures=(AndroidViewerGestures)ViewerProbeApplication.field(viewer,"gestures");
                gestures.cancel(); gestures.viewport.reset(); gestures.mode(intent.getBooleanExtra("trackpad",true)); gestures.centerCursor();
                gestureCase(frame, intent.getStringExtra("case"));
            }
            else if("landscape".equals(action)) viewer.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE);
            else if("portrait".equals(action)) viewer.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_PORTRAIT);
            else if("mode".equals(action)) chrome.mode.performClick();
            else if("keyboard".equals(action)) chrome.keyboard.performClick();
            else if("drag".equals(action)) chrome.drag.performClick();
            else if("original_size".equals(action) || "fit_size".equals(action)) {
                AndroidViewerViewport viewport=(AndroidViewerViewport)ViewerProbeApplication.field(viewer,"viewport");
                if("original_size".equals(action)) viewport.originalSize(); else viewport.reset();
                java.lang.reflect.Method refresh=viewer.getClass().getDeclaredMethod("refreshInteraction");
                refresh.setAccessible(true); refresh.invoke(viewer);
            }
            else if("send_text".equals(action)) {
                android.view.inputmethod.InputConnection connection=chrome.composer.onCreateInputConnection(new android.view.inputmethod.EditorInfo());
                connection.setComposingText("zhongwen",1);
                connection.commitText("中文测试😀"+intent.getStringExtra("text"),1);
                chrome.send.performClick();
            } else if("remote_tap".equals(action)) {
                AndroidViewerViewport viewport=(AndroidViewerViewport)ViewerProbeApplication.field(viewer,"viewport");
                AndroidViewerGestures gestures=(AndroidViewerGestures)ViewerProbeApplication.field(viewer,"gestures");
                if(gestures.trackpad) chrome.mode.performClick();
                float x=viewport.left()+intent.getFloatExtra("x",0)* (viewport.frameWidth-1)*viewport.scale();
                float y=viewport.top()+intent.getFloatExtra("y",0)* (viewport.frameHeight-1)*viewport.scale();
                long start=SystemClock.uptimeMillis();
                event(frame,start,0,MotionEvent.ACTION_DOWN,new float[]{x},new float[]{y});
                event(frame,start,80,MotionEvent.ACTION_UP,new float[]{x},new float[]{y});
            }
            else if("compose".equals(action)) {
                android.view.inputmethod.InputConnection connection=chrome.composer.onCreateInputConnection(new android.view.inputmethod.EditorInfo());
                connection.setComposingText("zhongwen",1);
                connection.setComposingText("zhongwenceshi",1);
                connection.commitText("中文测试😀",1);
                if(!"中文测试😀".contentEquals(chrome.composer.getText()))throw new IllegalStateException("IME composition duplicated");
            } else if("pinch".equals(action)||"scroll".equals(action)||"letterbox_pinch".equals(action)) {
                long start=SystemClock.uptimeMillis();
                float cx=frame.getWidth()/2f,cy=frame.getHeight()/2f;
                boolean pinch=!"scroll".equals(action);
                if("letterbox_pinch".equals(action)) cy=40;
                event(frame,start,0,MotionEvent.ACTION_DOWN,new float[]{cx-100},new float[]{cy});
                event(frame,start,10,MotionEvent.ACTION_POINTER_DOWN|(1<<8),new float[]{cx-100,cx+100},new float[]{cy,cy});
                for(int i=1;i<=12;i++) {
                    float span=pinch?100+i*15:100,dy=pinch?0:i*15;
                    event(frame,start,10+i*16,MotionEvent.ACTION_MOVE,new float[]{cx-span,cx+span},new float[]{cy+dy,cy+dy});
                }
                event(frame,start,220,MotionEvent.ACTION_POINTER_UP|(1<<8),new float[]{cx-100,cx+100},new float[]{cy,cy});
                event(frame,start,230,MotionEvent.ACTION_UP,new float[]{cx-100},new float[]{cy});
            } else if("cancel".equals(action)) {
                event(frame,SystemClock.uptimeMillis(),0,MotionEvent.ACTION_CANCEL,new float[]{10},new float[]{10});
            }
        } catch(Exception error) { ViewerProbeApplication.failure=error.toString(); }
    }
    private void gestureCase(View frame, String name) {
        float density=frame.getResources().getDisplayMetrics().density;
        float cx=frame.getWidth()/2f, cy=frame.getHeight()/2f, half=40*density;
        long start=SystemClock.uptimeMillis();
        event(frame,start,0,MotionEvent.ACTION_DOWN,new float[]{cx-half},new float[]{cy});
        event(frame,start,10,MotionEvent.ACTION_POINTER_DOWN|(1<<8),new float[]{cx-half,cx+half},new float[]{cy,cy});
        if ("jitter".equals(name)) {
            for (int i=1;i<=24;i++) {
                float wobble=(i%3)*4*density, dy=i*6*density;
                event(frame,start,10+i*16,MotionEvent.ACTION_MOVE,
                    new float[]{cx-half-wobble,cx+half+wobble},new float[]{cy+dy,cy+dy});
            }
        } else if ("replace".equals(name)) {
            event(frame,start,30,MotionEvent.ACTION_MOVE,new float[]{cx-half,cx+half},new float[]{cy+96*density,cy+96*density});
            event(frame,start,40,MotionEvent.ACTION_POINTER_UP|(1<<8),new float[]{cx-half,cx+half},new float[]{cy+96*density,cy+96*density});
            event(frame,start,50,MotionEvent.ACTION_MOVE,new float[]{cx-half},new float[]{cy-50*density});
            event(frame,start,60,MotionEvent.ACTION_POINTER_DOWN|(1<<8),new float[]{cx-half,cx+half*1.5f},new float[]{cy-50*density,cy-50*density});
            event(frame,start,70,MotionEvent.ACTION_MOVE,new float[]{cx-half,cx+half*1.5f},new float[]{cy+14*density,cy+14*density});
        } else if ("reverse_batch".equals(name)) {
            MotionEvent.PointerProperties[] props=new MotionEvent.PointerProperties[2];
            MotionEvent.PointerCoords[] coords=new MotionEvent.PointerCoords[2];
            for(int i=0;i<2;i++) {
                props[i]=new MotionEvent.PointerProperties(); props[i].id=i; props[i].toolType=MotionEvent.TOOL_TYPE_FINGER;
                coords[i]=new MotionEvent.PointerCoords(); coords[i].x=cx+(i==0?-half:half); coords[i].y=cy+100*density; coords[i].pressure=1;
            }
            MotionEvent batch=MotionEvent.obtain(start,start+30,MotionEvent.ACTION_MOVE,2,props,coords,0,0,1,1,0,0,InputDevice.SOURCE_TOUCHSCREEN,0);
            for(MotionEvent.PointerCoords p:coords) p.y=cy;
            batch.addBatch(start+60,coords,0);
            frame.dispatchTouchEvent(batch); batch.recycle();
        } else if ("horizontal".equals(name)) {
            event(frame,start,30,MotionEvent.ACTION_MOVE,new float[]{cx-half+80*density,cx+half+80*density},new float[]{cy+10*density,cy+10*density});
        } else if ("pinch_drift".equals(name)) {
            for(int i=1;i<=10;i++) {
                float spread=half*(1+i*.1f);
                event(frame,start,10+i*16,MotionEvent.ACTION_MOVE,new float[]{cx-spread,cx+spread},new float[]{cy+i*density,cy+i*density});
            }
        } else throw new IllegalArgumentException("Unknown bounded synthetic gesture");
        event(frame,start,430,MotionEvent.ACTION_POINTER_UP|(1<<8),new float[]{cx-half,cx+half},new float[]{cy,cy});
        event(frame,start,440,MotionEvent.ACTION_UP,new float[]{cx-half},new float[]{cy});
    }
    private void event(View target,long start,long elapsed,int action,float[] x,float[] y) {
        MotionEvent.PointerProperties[] properties=new MotionEvent.PointerProperties[x.length];
        MotionEvent.PointerCoords[] coordinates=new MotionEvent.PointerCoords[x.length];
        for(int i=0;i<x.length;i++) {
            properties[i]=new MotionEvent.PointerProperties();properties[i].id=i;properties[i].toolType=MotionEvent.TOOL_TYPE_FINGER;
            coordinates[i]=new MotionEvent.PointerCoords();coordinates[i].x=x[i];coordinates[i].y=y[i];coordinates[i].pressure=1;coordinates[i].size=1;
        }
        MotionEvent e=MotionEvent.obtain(start,start+elapsed,action,x.length,properties,coordinates,0,0,1,1,0,0,InputDevice.SOURCE_TOUCHSCREEN,0);
        target.dispatchTouchEvent(e);e.recycle();
    }
}
