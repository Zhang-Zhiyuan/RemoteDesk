package com.remotedesk.agent;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AndroidAdaptiveLayoutTest {
    @Test
    public void discoveryActionsStackBeforeTextAndHitTargetsAreCramped() {
        assertTrue(AndroidAdaptiveLayout.stackDiscoveryActions(252, 1f));
        assertFalse(AndroidAdaptiveLayout.stackDiscoveryActions(320, 1f));
        assertTrue(AndroidAdaptiveLayout.stackDiscoveryActions(320, 1.5f));
        assertFalse(AndroidAdaptiveLayout.stackDiscoveryActions(480, 2f));
        assertFalse(AndroidAdaptiveLayout.stackDiscoveryActions(320, Float.NaN));
        assertTrue(AndroidAdaptiveLayout.stackDiscoveryActions(200, 3f));
    }
    @Test
    public void portraitKeyboardUsesRemainingHeightInsteadOfFullConfigurationHeight() {
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(960, 0, 1.5f, 640));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(960, 614, 1.5f, 640));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(960, 425, 1.5f, 640));
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(960, 0, 1.5f, 640));
    }

    @Test
    public void viewerChromeHandlesResizeInsetsAndUnmeasuredLayouts() {
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(2340, 800, 2.75f, 850));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(1080, 550, 2.75f, 850));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(960, 960, 1.5f, 640));
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(0, 0, 1.5f, 640));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(0, 0, 1.5f, 320));
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(960, 614, Float.NaN, 640));
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(960, 614, 0f, 640));
        assertFalse(AndroidAdaptiveLayout.compactViewerChrome(630, 0, 1.5f, 640));
        assertTrue(AndroidAdaptiveLayout.compactViewerChrome(629, 0, 1.5f, 640));
    }

    @Test
    public void smallScreensAndLargeFontsUseCompactBrandingWithoutShrinkingText() {
        assertTrue(AndroidAdaptiveLayout.compactMainHeader(320, 1f));
        assertTrue(AndroidAdaptiveLayout.compactMainHeader(480, 2f));
        assertFalse(AndroidAdaptiveLayout.compactMainHeader(600, 2f));
        assertFalse(AndroidAdaptiveLayout.compactMainHeader(800, Float.NaN));
    }

    @Test
    public void mainColumnsSwitchAtStandardMediumWidthBreakpoint() {
        assertFalse(AndroidAdaptiveLayout.useMainTwoColumns(599));
        assertTrue(AndroidAdaptiveLayout.useMainTwoColumns(600));
    }

    @Test
    public void mainColumnsLeaveMoreRoomForLargeSystemFonts() {
        assertFalse(AndroidAdaptiveLayout.useMainTwoColumns(839, 2.0f));
        assertTrue(AndroidAdaptiveLayout.useMainTwoColumns(840, 2.0f));
        assertEquals(1080, AndroidAdaptiveLayout.mainTwoColumnThresholdDp(3.0f));
    }

    @Test
    public void mainContentWidthKeepsMarginsAndCapsExpandedWindows() {
        assertEquals(328, AndroidAdaptiveLayout.mainContentWidthDp(360, 16));
        assertEquals(1120, AndroidAdaptiveLayout.mainContentWidthDp(1400, 16));
    }

    @Test
    public void mainContentWidthHandlesTinyAndBoundaryWidths() {
        assertEquals(0, AndroidAdaptiveLayout.mainContentWidthDp(31, 16));
        assertEquals(0, AndroidAdaptiveLayout.mainContentWidthDp(32, 16));
        assertEquals(1, AndroidAdaptiveLayout.mainContentWidthDp(33, 16));
        assertEquals(1120, AndroidAdaptiveLayout.mainContentWidthDp(1152, 16));
        assertEquals(1120, AndroidAdaptiveLayout.mainContentWidthDp(1153, 16));
        assertEquals(320, AndroidAdaptiveLayout.mainContentWidthDp(320, -10));
    }

    @Test
    public void viewerToolbarAccountsForAvailableWidthAndFontScale() {
        assertTrue(AndroidAdaptiveLayout.stackViewerToolbar(359, 1.0f));
        assertFalse(AndroidAdaptiveLayout.stackViewerToolbar(360, 1.0f));
        assertTrue(AndroidAdaptiveLayout.stackViewerToolbar(480, 2.0f));
        assertFalse(AndroidAdaptiveLayout.stackViewerToolbar(520, 2.0f));
    }

    @Test
    public void viewerToolbarThresholdClampsExtremeFontScale() {
        assertEquals(360, AndroidAdaptiveLayout.viewerToolbarStackThresholdDp(0.8f));
        assertEquals(680, AndroidAdaptiveLayout.viewerToolbarStackThresholdDp(3.0f));
    }

    @Test
    public void viewerToolbarKeepsScreenVisibleInCompactHeight() {
        assertEquals(
            AndroidAdaptiveLayout.ViewerToolbarMode.COMPACT_HEIGHT,
            AndroidAdaptiveLayout.viewerToolbarMode(480, 479, 2.0f));
        assertEquals(
            AndroidAdaptiveLayout.ViewerToolbarMode.STACKED,
            AndroidAdaptiveLayout.viewerToolbarMode(480, 480, 2.0f));
        assertEquals(
            AndroidAdaptiveLayout.ViewerToolbarMode.HORIZONTAL,
            AndroidAdaptiveLayout.viewerToolbarMode(520, 480, 2.0f));
        assertEquals(
            AndroidAdaptiveLayout.ViewerToolbarMode.COMPACT_HEIGHT_NARROW,
            AndroidAdaptiveLayout.viewerToolbarMode(319, 320, 3.0f));
        assertEquals(
            AndroidAdaptiveLayout.ViewerToolbarMode.COMPACT_HEIGHT,
            AndroidAdaptiveLayout.viewerToolbarMode(320, 320, 3.0f));
    }

    @Test
    public void layoutThresholdsHandleInvalidFontScales() {
        assertEquals(600, AndroidAdaptiveLayout.mainTwoColumnThresholdDp(Float.NaN));
        assertEquals(360, AndroidAdaptiveLayout.viewerToolbarStackThresholdDp(Float.POSITIVE_INFINITY));
        assertEquals(720, AndroidAdaptiveLayout.mainTwoColumnThresholdDp(1.5f));
        assertEquals(440, AndroidAdaptiveLayout.viewerToolbarStackThresholdDp(1.5f));
    }
}
