package com.remotedesk.agent;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.SharedPreferences;
import android.text.InputType;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.util.Locale;

final class AndroidDirectSshDialog {
    static void show(Activity activity, String source) {
        SharedPreferences prefs = activity.getSharedPreferences("direct_ssh_endpoints", Context.MODE_PRIVATE);
        String device = source.isEmpty() ? prefs.getString("last", "") : source;
        String scope = device.toLowerCase(Locale.ROOT) + "/";
        LinearLayout form = new LinearLayout(activity);
        form.setOrientation(LinearLayout.VERTICAL);
        int pad = AndroidDisplay.dp(activity, 16);
        form.setPadding(pad, pad, pad, pad);
        CheckBox wsl = new CheckBox(activity); wsl.setText("连接 WSL 发行版"); form.addView(wsl);
        EditText host = field(activity, form, "SSH 主机 / IP", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText port = field(activity, form, "SSH 端口", InputType.TYPE_CLASS_NUMBER);
        TextView hint = new TextView(activity); form.addView(hint);
        Runnable fill = () -> {
            String key = scope + (wsl.isChecked() ? "wsl/" : "os/");
            host.setText(prefs.getString(key + "host", device));
            port.setText(prefs.getString(key + "port", wsl.isChecked() ? "" : "22"));
            hint.setText(wsl.isChecked() ? "填写发行版可达的 IP，或 Windows 地址和已有的 WSL 映射端口。" : "使用目标电脑的 SSH 服务，默认端口为 22。");
        };
        wsl.setChecked(prefs.getBoolean(scope + "wsl", false));
        fill.run(); wsl.setOnCheckedChangeListener((button, checked) -> fill.run());
        ScrollView scroll = new ScrollView(activity); scroll.addView(form);
        AlertDialog dialog = new AlertDialog.Builder(activity).setTitle("SSH 直连").setView(scroll)
            .setPositiveButton("打开终端", null).setNegativeButton("取消", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            try {
                String entry = port.getText().toString().trim();
                if (wsl.isChecked() && entry.isEmpty() && host.getText().toString().trim().equalsIgnoreCase(device))
                    throw new IllegalArgumentException("请填写 WSL 对外可达的 SSH 端口。");
                if (!entry.isEmpty() && !entry.matches("[0-9]{1,5}")) throw new IllegalArgumentException("SSH 端口必须在 1 到 65535 之间。");
                AndroidDirectSshTarget target = new AndroidDirectSshTarget(host.getText().toString(), entry.isEmpty() ? 22 : Integer.parseInt(entry));
                String origin = device.isEmpty() ? target.host : device;
                String prefix = origin.toLowerCase(Locale.ROOT) + "/";
                String key = prefix + (wsl.isChecked() ? "wsl/" : "os/");
                activity.startActivity(AndroidSshTerminalActivity.directIntent(activity, target));
                if (prefs.contains(key + "host") || prefs.getAll().size() < 250)
                    prefs.edit().putString("last", origin).putBoolean(prefix + "wsl", wsl.isChecked())
                        .putString(key + "host", target.host).putString(key + "port", String.valueOf(target.port)).apply();
                dialog.dismiss();
            } catch (IllegalArgumentException error) { hint.setText(error.getMessage()); }
        }));
        dialog.show();
    }

    private static EditText field(Activity activity, LinearLayout form, String label, int type) {
        TextView title = new TextView(activity); title.setText(label); form.addView(title);
        EditText field = new EditText(activity); field.setSingleLine(true); field.setInputType(type);
        field.setContentDescription(label); form.addView(field);
        return field;
    }
}
