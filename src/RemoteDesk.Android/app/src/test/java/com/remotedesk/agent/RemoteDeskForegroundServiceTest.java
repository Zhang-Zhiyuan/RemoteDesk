package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;

import org.junit.Test;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

public final class RemoteDeskForegroundServiceTest {
    @Test
    @SuppressWarnings("deprecation")
    public void getStreamingWifiLockModeUsesPerformanceOrLowLatencyMode() {
        int mode = RemoteDeskForegroundService.getStreamingWifiLockMode();

        assertTrue(
            mode == WifiManager.WIFI_MODE_FULL_LOW_LATENCY ||
            mode == WifiManager.WIFI_MODE_FULL_HIGH_PERF);
    }

    @Test
    public void presenceUsesConnectedDeviceWithoutAndroid15DataSyncTimeout() {
        assertEquals(
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
            RemoteDeskForegroundService.foregroundServiceType(false));
        assertEquals(
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION,
            RemoteDeskForegroundService.foregroundServiceType(true));
    }

    @Test
    public void manifestDeclaresOnlyTheRequiredLongRunningServiceTypes() throws Exception {
        String manifest = new String(
            Files.readAllBytes(findManifest()),
            StandardCharsets.UTF_8);

        assertTrue(manifest.contains(
            "android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE"));
        assertTrue(manifest.contains(
            "android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION"));
        assertTrue(manifest.contains(
            "android:foregroundServiceType=\"connectedDevice|mediaProjection\""));
        assertFalse(manifest.contains("FOREGROUND_SERVICE_DATA_SYNC"));
        assertFalse(manifest.contains("dataSync"));
    }

    private static Path findManifest() {
        Path[] candidates = {
            Paths.get("src", "main", "AndroidManifest.xml"),
            Paths.get("app", "src", "main", "AndroidManifest.xml"),
            Paths.get(
                "src",
                "RemoteDesk.Android",
                "app",
                "src",
                "main",
                "AndroidManifest.xml")
        };
        for (Path candidate : candidates) {
            if (Files.isRegularFile(candidate)) {
                return candidate;
            }
        }

        throw new AssertionError("AndroidManifest.xml was not found from the test working directory.");
    }
}
