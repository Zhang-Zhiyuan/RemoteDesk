package com.remotedesk.agent;

import android.app.Activity;
import android.app.AlertDialog;
import java.util.ArrayList;
import java.util.function.Consumer;

final class DeveloperLogin {
    static final String CODEX = "codex login --device-auth";
    static final String CLAUDE = "claude auth login";

    static void choose(Activity owner, String target, String instruction, Consumer<String> useCommand, Runnable legacy) {
        ArrayList<String> labels = new ArrayList<>();
        if (useCommand != null) {
            labels.add("登录 Codex");
            labels.add("登录 Claude Code");
        }
        if (legacy != null) labels.add("发送已有的加密登录包…");
        AlertDialog.Builder menu = new AlertDialog.Builder(owner).setTitle(target).setNegativeButton("关闭", null);
        if (labels.isEmpty()) menu.setMessage(instruction);
        else menu.setItems(labels.toArray(new String[0]), (dialog, which) -> {
            if (useCommand == null || which == 2) { if (legacy != null) legacy.run(); return; }
            String command = which == 0 ? CODEX : CLAUDE;
            new AlertDialog.Builder(owner).setTitle(target).setMessage(command + "\n\n" + instruction)
                .setNegativeButton("取消", null).setPositiveButton("继续", (d, w) -> useCommand.accept(command)).show();
        });
        menu.show();
    }
}
