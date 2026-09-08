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
            if("landscape".equals(action)) viewer.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE);
            else if("portrait".equals(action)) viewer.setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_PORTRAIT);
            else if("mode".equals(action)) chrome.mode.performClick();
            else if("keyboard".equals(action)) chrome.keyboard.performClick();
            else if("drag".equals(action)) chrome.drag.performClick();
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
