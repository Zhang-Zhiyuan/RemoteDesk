package com.remotedesk.agent;

import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.PowerManager;
import android.provider.Settings;

final class AndroidBatteryOptimization {
    private AndroidBatteryOptimization() {
    }

    static boolean isIgnoringBatteryOptimizations(Context context) {
        if (!supportsBatteryOptimizationBypass(Build.VERSION.SDK_INT)) {
            return true;
        }

        PowerManager powerManager = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
        return powerManager != null && powerManager.isIgnoringBatteryOptimizations(context.getPackageName());
    }

    static Intent createSettingsIntent(Context context) {
        // Open the system-managed list instead of requesting a direct exemption. This keeps
        // the choice explicit and avoids the restricted Play policy permission.
        return new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS);
    }

    static boolean supportsBatteryOptimizationBypass(int sdkInt) {
        return sdkInt >= 23;
    }

    static String formatStatus(boolean supported, boolean ignoringOptimizations) {
        if (!supported) {
            return "系统无需设置";
        }

        return ignoringOptimizations
            ? "已允许后台稳定运行"
            : "可能受省电影响，建议允许忽略电池优化";
    }
}
