package com.remotedesk.agent;

import org.junit.Test;
import static org.junit.Assert.*;

public final class AndroidViewerNavigationTest {
    @Test public void onlyAndroidAndExistingNavigationKeysAreAllowed() {
        for (int key : new int[] {0x1b, 0x24, 0x7b}) {
            assertTrue(AndroidViewerNavigation.allowed("Android", key));
            assertTrue(AndroidViewerNavigation.allowed("android", key));
            for (String platform : new String[] {null, "", "Windows", "Linux"})
                assertFalse(AndroidViewerNavigation.allowed(platform, key));
            assertEquals(2, AndroidViewerKeyboard.shortcut(1, 1, key).size());
        }
        for (int key : new int[] {8, 9, 0x5b, 0x7a, 0x2e})
            assertFalse(AndroidViewerNavigation.allowed("Android", key));
    }
}
