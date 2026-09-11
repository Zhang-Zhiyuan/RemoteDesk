package com.remotedesk.agent;

/** Explicit synthetic-field regression. Never targets a different Activity. */
public final class TextInputProbeActivity extends android.app.Activity {
    private final android.os.Handler handler = new android.os.Handler(android.os.Looper.getMainLooper());
    private final org.json.JSONArray cases = new org.json.JSONArray();
    private android.widget.EditText editor;
    private android.widget.EditText previousEditor;
    private final AndroidInputInjector.GestureState gesture = new AndroidInputInjector.GestureState();
    private int index;
    private boolean stopped;

    @Override public void onCreate(android.os.Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        android.widget.LinearLayout root = new android.widget.LinearLayout(this);
        root.setOrientation(android.widget.LinearLayout.VERTICAL);
        root.setPadding(48, 180, 48, 80);
        android.widget.TextView label = new android.widget.TextView(this);
        label.setText("RemoteDesk synthetic text regression\nNo personal input fields");
        label.setTextSize(24);
        root.addView(label);
        editor = new android.widget.EditText(this);
        editor.setSingleLine(true);
        editor.setShowSoftInputOnFocus(false);
        root.addView(editor);
        previousEditor = new android.widget.EditText(this);
        previousEditor.setSingleLine(true);
        previousEditor.setShowSoftInputOnFocus(false);
        root.addView(previousEditor);
        setContentView(root);
        editor.requestFocus();
        handler.postDelayed(this::next, 800);
    }

    private boolean ownsInput() {
        return !stopped && hasWindowFocus() && !isFinishing();
    }

    private void next() {
        if (!ownsInput() || !RemoteDeskAccessibilityService.isEnabled()) {
            finishReport(false, "Owned focused test editor or accessibility unavailable");
            return;
        }
        if (index == 16) { finishReport(true, ""); return; }
        String before = "prefix-原有😀-suffix";
        String inserted = "LinuxToAndroid中文42😀-" + index;
        int cursor = index % 2 == 0 ? 7 : before.length();
        editor.setText(before);
        editor.requestFocus();
        editor.setSelection(cursor);
        previousEditor.setText("untouched");
        if (index >= 8) previousEditor.requestFocus();
        handler.postDelayed(() -> {
            int[] points = inserted.codePoints().toArray();
            if (index >= 8) {
                // A slow network can deliver click and typing in one burst.
                // Match the real host's worker thread, not the Android UI thread.
                int[] position = new int[2];
                editor.getLocationOnScreen(position);
                android.util.DisplayMetrics display = new android.util.DisplayMetrics();
                getWindowManager().getDefaultDisplay().getRealMetrics(display);
                int x = position[0] + editor.getWidth() / 2;
                int y = position[1] + editor.getHeight() / 2;
                new Thread(() -> {
                    for (int kind : new int[]{2, 3}) {
                        byte[] payload = java.nio.ByteBuffer.allocate(14).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                            .put((byte)kind).put((byte)1).putInt(x).putInt(y).putInt(0).array();
                        if (!ownsInput()) return;
                        AndroidInputInjector.apply(payload, display.widthPixels, display.heightPixels,
                            display.widthPixels, display.heightPixels, gesture);
                    }
                    for (int codePoint : points) {
                        byte[] payload = java.nio.ByteBuffer.allocate(14).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                            .put((byte)7).put((byte)0).putInt(0).putInt(0).putInt(codePoint).array();
                        if (!ownsInput()) return;
                        AndroidInputInjector.apply(payload, display.widthPixels, display.heightPixels,
                            display.widthPixels, display.heightPixels, gesture);
                    }
                }, "OwnedTextOrderProbe").start();
            } else {
            for (int i = 0; i < points.length; i++) {
                final String text = new String(Character.toChars(points[i]));
                handler.postDelayed(() -> {
                    if (!ownsInput()) { finishReport(false, "Foreground lost; no further input"); return; }
                    RemoteDeskAccessibilityService.inputTextFromAnyThread(text);
                }, (index % 4) * 4L * i);
            }
            }
            handler.postDelayed(() -> {
                if (!ownsInput()) { finishReport(false, "Foreground lost; no further input"); return; }
                String expected = before.substring(0, cursor) + inserted + before.substring(cursor);
                String actual = editor.getText().toString();
                boolean pass = index < 8 ? expected.equals(actual) : actual.contains(inserted) &&
                    actual.replace(inserted, "").equals(before) && "untouched".contentEquals(previousEditor.getText());
                try {
                    cases.put(new org.json.JSONObject().put("index", index).put("passed", pass)
                        .put("expected", expected).put("actual", actual).put("previousField", previousEditor.getText().toString()));
                } catch (org.json.JSONException error) { throw new IllegalStateException(error); }
                index++;
                next();
            }, 1000);
        }, 250);
    }

    private void finishReport(boolean completed, String failure) {
        stopped = true;
        handler.removeCallbacksAndMessages(null);
        try {
            boolean all = completed;
            for (int i = 0; i < cases.length(); i++) all &= cases.getJSONObject(i).getBoolean("passed");
            org.json.JSONObject report = new org.json.JSONObject().put("completed", completed)
                .put("passed", all).put("failure", failure).put("cases", cases)
                .put("scope", "Owned synthetic EditText; production accessibility text path; burst and paced Unicode");
            java.nio.file.Files.write(new java.io.File(getFilesDir(), "text-input-probe.json").toPath(),
                report.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } catch (Exception error) { throw new IllegalStateException(error); }
    }

    @Override protected void onDestroy() {
        stopped = true;
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
