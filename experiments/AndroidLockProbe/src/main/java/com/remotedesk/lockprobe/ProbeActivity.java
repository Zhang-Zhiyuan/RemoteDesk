package com.remotedesk.lockprobe;

import android.app.Activity;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

public final class ProbeActivity extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(32, 80, 32, 32);
        TextView title = new TextView(this);
        title.setText("RemoteDesk owned screen / input test");
        title.setTextSize(22);
        layout.addView(title);
        EditText edit = new EditText(this);
        edit.setHint("Only enter synthetic test text here");
        edit.setSingleLine(false);
        edit.setMinLines(4);
        edit.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence text, int start, int count, int after) { }
            public void onTextChanged(CharSequence text, int start, int before, int count) { }
            public void afterTextChanged(Editable text) {
                try (FileOutputStream output = openFileOutput("input.txt", MODE_PRIVATE)) {
                    output.write(text.toString().getBytes(StandardCharsets.UTF_8));
                } catch (Exception ignored) { }
            }
        });
        layout.addView(edit);
        setContentView(layout);
    }
}
