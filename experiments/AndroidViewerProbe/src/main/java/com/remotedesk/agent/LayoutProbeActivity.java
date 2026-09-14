package com.remotedesk.agent;

import android.app.Activity;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;

/** Local synthetic layout/input probe; no connection, capture or real text. */
public final class LayoutProbeActivity extends Activity {
    private AndroidViewerChrome chrome;
    private LinearLayout root;
    private TextView desktop;
    private int actions, hits;
    private boolean compact;
    private final JSONObject result = new JSONObject();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        compact = getIntent().getIntExtra("height", 640) < 420;
        chrome = new AndroidViewerChrome(this, new AndroidViewerChrome.Actions() {
            public void keyboard(boolean open) { actions++; }
            public void mode() { actions++; }
            public void mouse(int button) { actions++; }
            public void drag() { actions++; }
            public void more() { actions++; }
            public void screens() { actions++; }
            public void zoom() { actions++; }
            public void upscale() { actions++; }
            public void diagnostics() { actions++; }
            public void shortcut(int... keys) { actions++; }
            public boolean text(String text) { actions++; return true; }
        });
        root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
        root.addView(chrome.header);
        desktop = new TextView(this); desktop.setBackgroundColor(0xff426789);
        desktop.setText("Synthetic desktop / 本地测试画面");
        root.addView(desktop, new LinearLayout.LayoutParams(-1, 0, 1));
        root.addView(chrome.dock);
        addContentView(root, new ViewGroup.LayoutParams(
            dp(getIntent().getIntExtra("width", 320)), dp(getIntent().getIntExtra("height", 640))));
        chrome.composer.setShowSoftInputOnFocus(false);
        chrome.mode(false, false); chrome.adapt(compact);
        root.postDelayed(() -> runStage(0), 250);
    }

    private int dp(int value) { return AndroidDisplay.dp(this, value); }
    private void require(boolean valid, String message) {
        if (!valid) throw new AssertionError(message);
    }
    private void tap(View button) {
        if (button.getParent().getParent() instanceof HorizontalScrollView scroll)
            scroll.scrollTo(button.getLeft(), 0);
        Rect visible = new Rect();
        require(button.getGlobalVisibleRect(visible) && visible.width() >= dp(48) && visible.height() >= dp(48),
            "Clipped/small target: " + button.getContentDescription() + " " + visible);
        int[] origin = new int[2]; getWindow().getDecorView().getLocationOnScreen(origin);
        long at = SystemClock.uptimeMillis();
        for (int action : new int[] {MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP}) {
            MotionEvent event = MotionEvent.obtain(at, at + action, action,
                visible.centerX() - origin[0], visible.centerY() - origin[1], 0);
            getWindow().getDecorView().dispatchTouchEvent(event); event.recycle();
        }
        hits++;
    }
    private void runStage(int stage) {
        try {
            require(desktop.getHeight() > 0, "Toolbar covered desktop");
            require(chrome.dock.getBottom() <= root.getHeight(), "Dock outside window");
            if (stage == 0) {
                for (int i = 0; i < chrome.mainRow.getChildCount(); i++) tap(chrome.mainRow.getChildAt(i));
            } else if (stage == 1) {
                require(chrome.mainRow.getChildCount() == 7 && actions == 6,
                    "Coordinate taps missed callbacks (including zoom/upscale): " + actions);
                chrome.keyboard(true); chrome.adapt(compact);
            } else if (stage == 2) {
                Rect editor = new Rect();
                require(chrome.composer.getGlobalVisibleRect(editor) && editor.width() >= dp(64),
                    "Composer squeezed out: " + editor);
                chrome.composer.requestFocus(); chrome.composer.setText("布局输入 Layout 123");
                chrome.adapt(compact);
                require("布局输入 Layout 123".contentEquals(chrome.composer.getText()), "Resize lost text");
                tap(chrome.send);
            } else if (stage == 3) {
                require(chrome.composer.length() == 0 && actions == 7, "Send click failed");
                chrome.keyboard(false); chrome.setMouseOpen(true);
            } else if (stage == 4) {
                for (int i = 0; i < chrome.mousePanel.getChildCount(); i++) tap(chrome.mousePanel.getChildAt(i));
            } else {
                require(actions == 10, "Mouse action taps missed callbacks: " + actions);
                result.put("passed", true); result.put("coordinateTaps", hits);
                result.put("widthDp", getIntent().getIntExtra("width", 320));
                result.put("heightDp", getIntent().getIntExtra("height", 640));
                result.put("fontScale", getResources().getConfiguration().fontScale);
                save(); return;
            }
            root.postDelayed(() -> runStage(stage + 1), 150);
        } catch (Throwable error) {
            try { result.put("passed", false); result.put("error", error.toString()); save(); }
            catch (Exception nested) { throw new RuntimeException(nested); }
        }
    }
    private void save() throws Exception {
        try (FileOutputStream output = new FileOutputStream(new File(getFilesDir(), "layout-probe.json"))) {
            output.write(result.toString(2).getBytes(StandardCharsets.UTF_8));
        }
    }
}
