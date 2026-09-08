package com.remotedesk.agent;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Insets;
import android.os.Build;
import android.util.TypedValue;
import android.view.DisplayCutout;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;

import androidx.annotation.RequiresApi;

final class AndroidDisplay {
    private AndroidDisplay() {
    }

    static int dp(Context context, float value) {
        return Math.round(TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP,
            value,
            context.getResources().getDisplayMetrics()));
    }

    @SuppressWarnings("deprecation")
    static void configureEdgeToEdge(Activity activity, boolean useDarkSystemBarIcons) {
        Window window = activity.getWindow();
        window.setStatusBarColor(Color.TRANSPARENT);
        window.setNavigationBarColor(Color.TRANSPARENT);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false);
            // PhoneWindow.getInsetsController() dereferences its DecorView on
            // some Android 16 builds. onCreate can run before that DecorView
            // exists, so create it explicitly and use the View API. If the
            // view is not attached yet, View.post runs this once it is.
            View decorView = window.getDecorView();
            if (!tryApplySystemBarAppearance(decorView, useDarkSystemBarIcons)) {
                decorView.post(() ->
                    tryApplySystemBarAppearance(decorView, useDarkSystemBarIcons));
            }
            return;
        }

        int visibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION;
        if (useDarkSystemBarIcons) {
            visibility |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR |
                View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        }
        window.getDecorView().setSystemUiVisibility(visibility);
    }

    @SuppressWarnings("deprecation")
    static void setImmersiveMode(Activity activity, boolean enabled) {
        Window window = activity.getWindow();
        View decorView = window.getDecorView();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WindowInsetsController controller = decorView.getWindowInsetsController();
            if (controller == null) {
                decorView.post(() -> setImmersiveMode(activity, enabled));
                return;
            }

            int systemBars = WindowInsets.Type.statusBars() |
                WindowInsets.Type.navigationBars();
            if (enabled) {
                controller.setSystemBarsBehavior(
                    WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
                controller.hide(systemBars);
            } else {
                controller.show(systemBars);
            }
            return;
        }

        int visibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION;
        if (enabled) {
            visibility |= View.SYSTEM_UI_FLAG_FULLSCREEN |
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY;
        }
        decorView.setSystemUiVisibility(visibility);
    }

    @RequiresApi(Build.VERSION_CODES.R)
    private static boolean tryApplySystemBarAppearance(
        View decorView,
        boolean useDarkSystemBarIcons) {
        WindowInsetsController controller = decorView.getWindowInsetsController();
        if (controller == null) {
            return false;
        }

        int appearanceMask =
            WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS |
            WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
        controller.setSystemBarsAppearance(
            useDarkSystemBarIcons ? appearanceMask : 0,
            appearanceMask);
        return true;
    }

    @SuppressWarnings("deprecation")
    static SafeInsets safeInsets(WindowInsets windowInsets, boolean includeIme) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            int types = WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout();
            if (includeIme) {
                types |= WindowInsets.Type.ime();
            }
            Insets insets = windowInsets.getInsets(types);
            return new SafeInsets(insets.left, insets.top, insets.right, insets.bottom);
        }

        int left = windowInsets.getSystemWindowInsetLeft();
        int top = windowInsets.getSystemWindowInsetTop();
        int right = windowInsets.getSystemWindowInsetRight();
        int bottom = windowInsets.getSystemWindowInsetBottom();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            DisplayCutout cutout = windowInsets.getDisplayCutout();
            if (cutout != null) {
                left = Math.max(left, cutout.getSafeInsetLeft());
                top = Math.max(top, cutout.getSafeInsetTop());
                right = Math.max(right, cutout.getSafeInsetRight());
                bottom = Math.max(bottom, cutout.getSafeInsetBottom());
            }
        }
        return new SafeInsets(left, top, right, bottom);
    }

    static final class SafeInsets {
        final int left;
        final int top;
        final int right;
        final int bottom;

        SafeInsets(int left, int top, int right, int bottom) {
            this.left = left;
            this.top = top;
            this.right = right;
            this.bottom = bottom;
        }
    }
}
