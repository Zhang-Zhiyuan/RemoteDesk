package com.remotedesk.agent;

import android.os.Build;

import java.util.Locale;

final class AndroidDeviceNames {
    private AndroidDeviceNames() {
    }

    static String displayName() {
        String manufacturer = clean(Build.MANUFACTURER);
        String model = clean(Build.MODEL);
        if (model.toLowerCase(Locale.ROOT).startsWith(
                manufacturer.toLowerCase(Locale.ROOT))) {
            return model;
        }

        return manufacturer + " " + model;
    }

    private static String clean(String value) {
        if (value == null || value.trim().isEmpty()) {
            return "Android";
        }

        return value.trim();
    }
}
