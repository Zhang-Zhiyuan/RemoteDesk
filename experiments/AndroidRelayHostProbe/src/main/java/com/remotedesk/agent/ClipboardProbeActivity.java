package com.remotedesk.agent;

/** Only the owned synthetic editor/clipboard. Never uses another app's fields. */
public final class ClipboardProbeActivity extends android.app.Activity {
    private static final String SAMPLE = "安卓中文😀\r\n第二行\t缩进\n";
    private final android.os.Handler handler = new android.os.Handler(android.os.Looper.getMainLooper());
    private final org.json.JSONArray checks = new org.json.JSONArray();
    private final AndroidInputInjector.GestureState gesture = new AndroidInputInjector.GestureState();
    private android.widget.EditText editor;
    private boolean failed;

    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        android.widget.LinearLayout root = new android.widget.LinearLayout(this);
        root.setOrientation(1); root.setPadding(24, 70, 24, 30);
        android.widget.TextView label = new android.widget.TextView(this);
        label.setText("RemoteDesk clipboard regression\nSynthetic editor only"); root.addView(label);
        editor = new android.widget.EditText(this);
        editor.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        editor.setShowSoftInputOnFocus(false); root.addView(editor);
        setContentView(root); editor.requestFocus();
        handler.postDelayed(this::paste, 1000);
    }

    private void check(String name, boolean pass) {
        try { checks.put(new org.json.JSONObject().put("name", name).put("passed", pass)); }
        catch (Exception error) { throw new IllegalStateException(error); }
        failed |= !pass;
        save(false);
    }

    private void paste() {
        try {
            check("owned focused editor and accessibility", hasWindowFocus() && RemoteDeskAccessibilityService.isEnabled());
            AndroidClipboardText.setText(this, SAMPLE);
            check("Android system clipboard Unicode/CRLF roundtrip", SAMPLE.equals(AndroidClipboardText.getText(this)));
            shortcut(0x56);
            handler.postDelayed(() -> {
                check("product Ctrl+V pastes full Unicode text into Android editor", SAMPLE.contentEquals(editor.getText()));
                copy();
            }, 650);
        } catch (Exception error) { fail(error); }
    }

    private void copy() {
        try {
            AndroidClipboardText.setText(this, "replace-me");
            shortcut(0x41); shortcut(0x43);
            handler.postDelayed(() -> {
                try {
                    check("product Ctrl+A/C copies selected Android text", SAMPLE.equals(AndroidClipboardText.getText(this)));
                    shortcut(0x58);
                    handler.postDelayed(() -> {
                        check("product Ctrl+X cuts selected Android text", editor.length() == 0);
                        timeoutWrite();
                    }, 650);
                } catch (Exception error) { fail(error); }
            }, 650);
        } catch (Exception error) { fail(error); }
    }

    private void shortcut(int key) {
        if (!hasWindowFocus()) throw new IllegalStateException("Test editor lost focus");
        for (int[] action : new int[][]{{5, 0x11}, {5, key}, {6, key}, {6, 0x11}}) {
            try {
                AndroidInputInjector.apply(RemoteDeskTransport.encodeInput(action[0], 0, 0, 0, action[1]),
                    1, 1, 1, 1, gesture, this::hasWindowFocus);
            } catch (Exception error) { throw new IllegalStateException(error); }
        }
    }

    private void timeoutWrite() {
        try {
            AndroidClipboardText.setText(this, "keep-after-timeout");
            java.util.concurrent.atomic.AtomicBoolean timedOut = new java.util.concurrent.atomic.AtomicBoolean();
            new Thread(() -> {
                try { AndroidClipboardText.setText(this, "late-write-must-not-run"); }
                catch (Exception expected) { timedOut.set(true); }
            }, "OwnedClipboardTimeout").start();
            // Intentional, bounded stall of this disposable test Activity only.
            Thread.sleep(1900);
            handler.postDelayed(() -> {
                try {
                    check("timed-out main-thread write cannot execute later", timedOut.get() &&
                        "keep-after-timeout".equals(AndroidClipboardText.getText(this)));
                    moveTaskToBack(true);
                    handler.postDelayed(() -> {
                        boolean blocked = false;
                        try { AndroidClipboardText.getText(this); }
                        catch (Exception expected) { blocked = true; }
                        check("background clipboard denial is reported, not returned as empty success", blocked);
                        save(true);
                    }, 900);
                } catch (Exception error) { fail(error); }
            }, 200);
        } catch (Exception error) { fail(error); }
    }

    private void fail(Exception error) { check(error.getClass().getSimpleName(), false); save(true); }
    private void save(boolean complete) {
        try {
            org.json.JSONObject result = new org.json.JSONObject().put("complete", complete).put("passed", !failed)
                .put("scope", "disposable Android emulator app; actual product clipboard/accessibility code and real synthetic EditText")
                .put("checks", checks);
            java.nio.file.Files.write(new java.io.File(getFilesDir(), "clipboard-probe.json").toPath(),
                result.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } catch (Exception error) { throw new IllegalStateException(error); }
    }
    @Override public void onDestroy() { handler.removeCallbacksAndMessages(null); gesture.forceReset(); super.onDestroy(); }
}
