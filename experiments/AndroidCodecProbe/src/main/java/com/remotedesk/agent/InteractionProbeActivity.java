package com.remotedesk.agent;

import android.app.Activity;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.text.method.PasswordTransformationMethod;
import android.util.AtomicFile;
import android.util.DisplayMetrics;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import org.json.JSONArray;
import org.json.JSONObject;

/** Synthetic remote-input target. No network, capture or accessibility permissions. */
public final class InteractionProbeActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private Button button;
    private EditText editor;
    private int clicks;
    private boolean masked;
    private boolean resumed;
    private boolean foregroundGuardArmed;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(0xfff3f6fb);
        root.setPadding(48, 180, 48, 80);
        TextView title = new TextView(this);
        title.setText("RemoteDesk physical input test\nOnly synthetic test data");
        title.setTextSize(24);
        title.setTextColor(0xff152030);
        root.addView(title);
        button = new Button(this);
        button.setText("Remote click target: 0");
        root.addView(button, new LinearLayout.LayoutParams(-1, 220));
        editor = new EditText(this);
        editor.setSingleLine(true);
        editor.setShowSoftInputOnFocus(false);
        editor.setHint("Remote text input target");
        root.addView(editor, new LinearLayout.LayoutParams(-1, 220));
        TextView animation = new TextView(this);
        animation.setTextColor(0xffffffff);
        animation.setTextSize(24);
        animation.setBackgroundColor(0xff2563eb);
        root.addView(animation, new LinearLayout.LayoutParams(-1, 360));
        masked = preservesMask(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD)
            && preservesMask(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD)
            && preservesMask(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_VARIATION_PASSWORD);
        button.setOnClickListener(view -> {
            clicks++;
            button.setText("Remote click target: " + clicks);
            snapshot();
        });
        editor.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) { }
            public void afterTextChanged(Editable s) { snapshot(); }
        });
        editor.setOnFocusChangeListener((view, focused) -> snapshot());
        setContentView(root);
        handler.postDelayed(this::snapshot, 300);
        handler.post(new Runnable() {
            int tick;
            public void run() {
                animation.setText("Synthetic motion frame " + (++tick));
                animation.setTranslationX((tick % 30) * 2);
                handler.postDelayed(this, 33);
            }
        });
    }

    private boolean preservesMask(int inputType) {
        EditText field = new EditText(this);
        field.setSingleLine(true);
        field.setInputType(inputType);
        field.setText("123456");
        AndroidUiTheme.styleInput(this, field);
        return field.getTransformationMethod() instanceof PasswordTransformationMethod
            && !"123456".contentEquals(field.getTransformationMethod().getTransformation(field.getText(), field));
    }

    private JSONArray bounds(View view) {
        int[] position = new int[2];
        view.getLocationOnScreen(position);
        return new JSONArray().put(position[0]).put(position[1])
            .put(position[0] + view.getWidth()).put(position[1] + view.getHeight());
    }

    private void snapshot() {
        if (button == null || editor == null) return;
        AtomicFile file = new AtomicFile(new File(getFilesDir(), "interaction-probe.json"));
        FileOutputStream stream = null;
        try {
            DisplayMetrics display = new DisplayMetrics();
            getWindowManager().getDefaultDisplay().getRealMetrics(display);
            JSONObject result = new JSONObject().put("clicks", clicks)
                .put("text", editor.getText().toString()).put("passwordStylesMasked", masked)
                .put("foregroundOwned", resumed && hasWindowFocus() && !isFinishing())
                .put("editorFocused", editor.hasFocus())
                .put("screenWidth", display.widthPixels).put("screenHeight", display.heightPixels)
                .put("buttonBounds", bounds(button)).put("editorBounds", bounds(editor));
            stream = file.startWrite();
            stream.write(result.toString(2).getBytes(StandardCharsets.UTF_8));
            file.finishWrite(stream);
        } catch (Exception error) {
            if (stream != null) file.failWrite(stream);
            android.util.Log.e("RemoteDeskInteractionProbe", "Unable to record synthetic result", error);
        }
    }

    @Override protected void onResume() {
        super.onResume();
        resumed = true;
        snapshot();
    }

    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused && resumed) foregroundGuardArmed = true;
        if (!focused) stopOwnedHostIfArmed();
        snapshot();
    }

    @Override protected void onPause() {
        resumed = false;
        stopOwnedHostIfArmed();
        snapshot();
        super.onPause();
    }

    private void stopOwnedHostIfArmed() {
        if (!foregroundGuardArmed || !getIntent().getBooleanExtra("guardHostedInput", false)) return;
        foregroundGuardArmed = false;
        // Only the optional test host in THIS APK's sandbox can be stopped.
        // Never stop or change the separate installed production host.
        if ("com.remotedesk.relayhostprobe".equals(getPackageName())) {
            stopService(new android.content.Intent(this, RemoteDeskForegroundService.class));
        }
    }

    @Override public void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
