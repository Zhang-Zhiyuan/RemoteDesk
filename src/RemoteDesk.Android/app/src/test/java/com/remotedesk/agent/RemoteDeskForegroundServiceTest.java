package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.assertSame;
import static org.junit.Assert.assertThrows;

import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;

import org.junit.Test;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

public final class RemoteDeskForegroundServiceTest {
    @Test
    public void rejectedForegroundStartDoesNotCrashOrLoopRestart() {
        SecurityException rejected = new SecurityException("platform denied start");
        java.util.concurrent.atomic.AtomicReference<RuntimeException> failure = new java.util.concurrent.atomic.AtomicReference<>();
        assertEquals(android.app.Service.START_NOT_STICKY, RemoteDeskForegroundService.runStartSafely(
            () -> { throw rejected; }, failure::set));
        assertSame(rejected, failure.get());
    }

    @Test
    public void successfulStartPreservesItsRequestedRestartMode() {
        assertEquals(android.app.Service.START_STICKY, RemoteDeskForegroundService.runStartSafely(
            () -> android.app.Service.START_STICKY, ex -> { throw new AssertionError(ex); }));
    }

    @Test
    public void fatalVmErrorsAreNotDisguisedAsRecoverableStartFailures() {
        OutOfMemoryError fatal = new OutOfMemoryError("synthetic fatal condition");
        assertSame(fatal, assertThrows(OutOfMemoryError.class, () -> RemoteDeskForegroundService.runStartSafely(
            () -> { throw fatal; }, ex -> { throw new AssertionError(ex); })));
    }

    @Test
    public void delayedAutomaticResumeCannotUndoAnExplicitStop() {
        assertTrue(RemoteDeskForegroundService.shouldRejectAutomaticStart(true, false));
        assertFalse(RemoteDeskForegroundService.shouldRejectAutomaticStart(true, true));
    }

    @Test
    public void explicitStartRemainsAvailableWhenAutomaticResumeIsDisarmed() {
        assertFalse(RemoteDeskForegroundService.shouldRejectAutomaticStart(false, false));
        assertFalse(RemoteDeskForegroundService.shouldRejectAutomaticStart(false, true));
    }

    @Test
    public void idleListenerAndUnverifiedConnectionsDoNotKeepScreenOrCpuAwake() {
        assertFalse(RemoteDeskForegroundService.shouldHoldStreamingPower(false, false));
        assertFalse(RemoteDeskForegroundService.shouldHoldStreamingPower(true, false));
        assertFalse(RemoteDeskForegroundService.shouldHoldStreamingPower(false, true));
        assertTrue(RemoteDeskForegroundService.shouldHoldStreamingPower(true, true));
    }

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
