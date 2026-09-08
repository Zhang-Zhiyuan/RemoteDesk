package com.remotedesk.agent;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.text.method.TransformationMethod;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

final class AndroidUiTheme {
    static final int BACKGROUND = 0xFFF3F6FB;
    static final int SURFACE = 0xFFFFFFFF;
    static final int SURFACE_MUTED = 0xFFF8FAFC;
    static final int BORDER = 0xFFDBE4EF;
    static final int TEXT = 0xFF0F172A;
    static final int MUTED = 0xFF64748B;
    static final int PRIMARY = 0xFF2563EB;
    static final int PRIMARY_PRESSED = 0xFF1D4ED8;
    static final int PRIMARY_SOFT = 0xFFEFF6FF;
    static final int PRIMARY_SOFT_PRESSED = 0xFFDBEAFE;
    static final int SUCCESS = 0xFF15803D;
    static final int SUCCESS_SOFT = 0xFFF0FDF4;
    static final int DANGER = 0xFFB91C1C;
    static final int DANGER_SOFT = 0xFFFEF2F2;
    static final int HEADER = 0xFF0B1220;
    static final int HEADER_SURFACE = 0xFF172033;
    static final int HEADER_MUTED = 0xFF94A3B8;
    static final int VIEWER_BACKGROUND = 0xFF020617;

    enum ButtonRole {
        PRIMARY,
        SECONDARY,
        DANGER
    }

    enum StatusTone {
        INFO,
        SUCCESS,
        DANGER
    }

    private AndroidUiTheme() {
    }

    static LinearLayout createCard(Context context) {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(context, 18), dp(context, 16), dp(context, 18), dp(context, 16));
        card.setBackground(shape(context, SURFACE, 18, BORDER, 1));
        card.setElevation(dp(context, 1));
        return card;
    }

    static TextView createEyebrow(Context context, String text) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextColor(PRIMARY);
        view.setTextSize(11.0f);
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        view.setLetterSpacing(0.08f);
        return view;
    }

    static TextView createSectionTitle(Context context, String text) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextColor(TEXT);
        view.setTextSize(19.0f);
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return view;
    }

    static TextView createSectionSubtitle(Context context, String text) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextColor(MUTED);
        view.setTextSize(13.0f);
        view.setLineSpacing(0.0f, 1.12f);
        return view;
    }

    static TextView createFieldLabel(Context context, String text) {
        TextView view = new TextView(context);
        view.setText(text);
        view.setTextColor(MUTED);
        view.setTextSize(12.0f);
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return view;
    }

    static View createDivider(Context context) {
        View divider = new View(context);
        divider.setBackgroundColor(BORDER);
        return divider;
    }

    static void styleInput(Context context, EditText input) {
        input.setTextColor(TEXT);
        input.setHintTextColor(Color.rgb(148, 163, 184));
        input.setTextSize(15.0f);
        // TextView.setSingleLine replaces the transformation method, including
        // PasswordTransformationMethod. Styling must not reveal a password.
        TransformationMethod transformation = input.getTransformationMethod();
        input.setSingleLine(true);
        if (transformation != null) {
            input.setTransformationMethod(transformation);
        }
        input.setMinHeight(dp(context, 52));
        input.setPadding(dp(context, 14), 0, dp(context, 14), 0);
        input.setBackground(focusableFieldBackground(context));
    }

    static void styleButton(Context context, Button button, ButtonRole role) {
        int normal;
        int pressed;
        int disabled;
        int text;
        int disabledText;
        switch (role) {
            case PRIMARY:
                normal = PRIMARY;
                pressed = PRIMARY_PRESSED;
                disabled = Color.rgb(147, 197, 253);
                text = Color.WHITE;
                disabledText = Color.rgb(248, 250, 252);
                break;
            case DANGER:
                normal = DANGER_SOFT;
                pressed = Color.rgb(254, 226, 226);
                disabled = Color.rgb(248, 250, 252);
                text = DANGER;
                disabledText = Color.rgb(203, 213, 225);
                break;
            default:
                normal = Color.rgb(232, 238, 247);
                pressed = Color.rgb(219, 228, 239);
                disabled = Color.rgb(248, 250, 252);
                text = TEXT;
                disabledText = Color.rgb(148, 163, 184);
                break;
        }

        int[][] states = new int[][] {
            new int[] { -android.R.attr.state_enabled },
            new int[] { android.R.attr.state_pressed },
            new int[] {}
        };
        button.setAllCaps(false);
        button.setTextSize(14.0f);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setMinHeight(dp(context, 48));
        button.setMinimumHeight(dp(context, 48));
        button.setPadding(dp(context, 16), 0, dp(context, 16), 0);
        button.setBackgroundTintList(new ColorStateList(
            states,
            new int[] { disabled, pressed, normal }));
        button.setTextColor(new ColorStateList(
            states,
            new int[] { disabledText, text, text }));
        button.setElevation(0.0f);
    }

    static void styleViewerDisconnectButton(Context context, Button button) {
        int[][] states = new int[][] {
            new int[] { android.R.attr.state_pressed },
            new int[] {}
        };
        button.setAllCaps(false);
        button.setTextSize(13.0f);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setMinHeight(dp(context, 42));
        button.setMinimumHeight(dp(context, 42));
        button.setPadding(dp(context, 16), 0, dp(context, 16), 0);
        button.setBackgroundTintList(new ColorStateList(
            states,
            new int[] { 0xFFB91C1C, 0xFF7F1D1D }));
        button.setTextColor(Color.WHITE);
        button.setElevation(0.0f);
    }

    static void styleViewerToolbarButton(Context context, Button button) {
        int[][] states = new int[][] {
            new int[] { android.R.attr.state_pressed },
            new int[] {}
        };
        button.setAllCaps(false);
        button.setTextSize(13.0f);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setMinHeight(dp(context, 42));
        button.setMinimumHeight(dp(context, 42));
        button.setPadding(dp(context, 14), 0, dp(context, 14), 0);
        button.setBackgroundTintList(new ColorStateList(
            states,
            new int[] { 0xFF334155, HEADER_SURFACE }));
        button.setTextColor(Color.WHITE);
        button.setElevation(0.0f);
    }

    static void styleViewerStatusIndicator(Context context, View view, String status) {
        StatusTone tone = resolveStatusTone(status);
        int color = tone == StatusTone.SUCCESS
            ? 0xFF4ADE80
            : tone == StatusTone.DANGER ? 0xFFF87171 : 0xFF60A5FA;
        view.setBackground(shape(context, color, 99, color, 0));
    }

    static void styleReadiness(TextView view) {
        view.setTextColor(MUTED);
        view.setTextSize(13.0f);
        view.setLineSpacing(0.0f, 1.18f);
        view.setGravity(Gravity.START);
        view.setPadding(0, 0, 0, 0);
    }

    static void applyStatusBanner(Context context, TextView view, String text) {
        StatusTone tone = resolveStatusTone(text);
        int background;
        int foreground;
        int border;
        if (tone == StatusTone.SUCCESS) {
            background = SUCCESS_SOFT;
            foreground = SUCCESS;
            border = Color.rgb(187, 247, 208);
        } else if (tone == StatusTone.DANGER) {
            background = DANGER_SOFT;
            foreground = DANGER;
            border = Color.rgb(254, 202, 202);
        } else {
            background = PRIMARY_SOFT;
            foreground = Color.rgb(30, 64, 175);
            border = Color.rgb(191, 219, 254);
        }

        view.setText(text);
        view.setTextColor(foreground);
        view.setTextSize(14.0f);
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        view.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        view.setPadding(dp(context, 16), dp(context, 12), dp(context, 16), dp(context, 12));
        view.setBackground(shape(context, background, 14, border, 1));
    }

    static StatusTone resolveStatusTone(String text) {
        String normalized = text == null ? "" : text;
        if (normalized.contains("失败") ||
            normalized.contains("无效") ||
            normalized.contains("不能为空") ||
            normalized.contains("未完成") ||
            normalized.contains("没有可") ||
            normalized.contains("错误") ||
            normalized.contains("拒绝") ||
            normalized.contains("中断") ||
            normalized.contains("超时")) {
            return StatusTone.DANGER;
        }

        if (normalized.contains("正在运行") ||
            normalized.contains("已启动") ||
            normalized.contains("已连接") ||
            normalized.contains("正在查看") ||
            normalized.contains("已复制") ||
            normalized.contains("已生成") ||
            normalized.contains("已清空")) {
            return StatusTone.SUCCESS;
        }

        return StatusTone.INFO;
    }

    static GradientDrawable shape(
        Context context,
        int color,
        float radiusDp,
        int strokeColor,
        float strokeDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(context, radiusDp));
        if (strokeDp > 0.0f) {
            drawable.setStroke(Math.max(1, dp(context, strokeDp)), strokeColor);
        }
        return drawable;
    }

    private static StateListDrawable focusableFieldBackground(Context context) {
        StateListDrawable states = new StateListDrawable();
        states.addState(
            new int[] { android.R.attr.state_focused },
            shape(context, SURFACE, 12, PRIMARY, 2));
        states.addState(
            new int[] { -android.R.attr.state_enabled },
            shape(context, SURFACE_MUTED, 12, BORDER, 1));
        states.addState(
            new int[] {},
            shape(context, SURFACE, 12, BORDER, 1));
        return states;
    }

    private static int dp(Context context, float value) {
        return AndroidDisplay.dp(context, value);
    }
}
