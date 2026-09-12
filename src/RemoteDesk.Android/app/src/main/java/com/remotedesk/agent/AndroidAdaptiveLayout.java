package com.remotedesk.agent;

final class AndroidAdaptiveLayout {
    static final int MAIN_TWO_COLUMN_MIN_WIDTH_DP = 600;
    static final int MAIN_MAX_CONTENT_WIDTH_DP = 1120;
    private static final int MAIN_TWO_COLUMN_FONT_SCALE_ALLOWANCE_DP = 240;
    private static final int VIEWER_TOOLBAR_MIN_WIDTH_DP = 360;
    private static final int VIEWER_TOOLBAR_FONT_SCALE_ALLOWANCE_DP = 160;
    private static final int VIEWER_COMPACT_HEIGHT_MAX_DP = 480;
    private static final int VIEWER_COMPACT_MIN_WIDTH_DP = 240;
    private static final int VIEWER_COMPACT_FONT_SCALE_ALLOWANCE_DP = 40;

    enum ViewerToolbarMode {
        HORIZONTAL,
        STACKED,
        COMPACT_HEIGHT,
        COMPACT_HEIGHT_NARROW
    }

    private AndroidAdaptiveLayout() {
    }

    static boolean useMainTwoColumns(int availableWidthDp) {
        return useMainTwoColumns(availableWidthDp, 1.0f);
    }

    static boolean compactMainHeader(int availableWidthDp, float fontScale) {
        return availableWidthDp < viewerToolbarStackThresholdDp(fontScale);
    }

    static int mainTwoColumnThresholdDp(float fontScale) {
        float normalizedScale = normalizeFontScale(fontScale);
        return Math.round(
            MAIN_TWO_COLUMN_MIN_WIDTH_DP +
            (normalizedScale - 1.0f) * MAIN_TWO_COLUMN_FONT_SCALE_ALLOWANCE_DP);
    }

    static boolean useMainTwoColumns(int availableWidthDp, float fontScale) {
        return availableWidthDp >= mainTwoColumnThresholdDp(fontScale);
    }

    static int mainContentWidthDp(int availableWidthDp, int horizontalMarginDp) {
        int widthAfterMargins = Math.max(0, availableWidthDp - Math.max(0, horizontalMarginDp) * 2);
        return Math.min(widthAfterMargins, MAIN_MAX_CONTENT_WIDTH_DP);
    }

    static int viewerToolbarStackThresholdDp(float fontScale) {
        float normalizedScale = normalizeFontScale(fontScale);
        return Math.round(
            VIEWER_TOOLBAR_MIN_WIDTH_DP +
            (normalizedScale - 1.0f) * VIEWER_TOOLBAR_FONT_SCALE_ALLOWANCE_DP);
    }

    static boolean stackViewerToolbar(int availableWidthDp, float fontScale) {
        return availableWidthDp < viewerToolbarStackThresholdDp(fontScale);
    }

    static boolean compactViewerChrome(
        int measuredHeightPx, int verticalInsetsPx, float density, int fallbackHeightDp) {
        // Edge-to-edge windows keep their full configuration height while the
        // IME consumes root padding. Do not use the changing toolbar/dock
        // measurements here: doing so can make compact mode oscillate.
        float availableHeightDp = fallbackHeightDp;
        if (measuredHeightPx > 0 && Float.isFinite(density) && density > 0f) {
            availableHeightDp = Math.max(0, measuredHeightPx - Math.max(0, verticalInsetsPx)) / density;
        }
        return availableHeightDp >= 0 && availableHeightDp < 420;
    }

    static ViewerToolbarMode viewerToolbarMode(
        int availableWidthDp,
        int availableHeightDp,
        float fontScale) {
        if (availableHeightDp > 0 && availableHeightDp < VIEWER_COMPACT_HEIGHT_MAX_DP) {
            int compactMinimumWidth = Math.round(
                VIEWER_COMPACT_MIN_WIDTH_DP +
                (normalizeFontScale(fontScale) - 1.0f) * VIEWER_COMPACT_FONT_SCALE_ALLOWANCE_DP);
            return availableWidthDp >= compactMinimumWidth
                ? ViewerToolbarMode.COMPACT_HEIGHT
                : ViewerToolbarMode.COMPACT_HEIGHT_NARROW;
        }
        return stackViewerToolbar(availableWidthDp, fontScale)
            ? ViewerToolbarMode.STACKED
            : ViewerToolbarMode.HORIZONTAL;
    }

    private static float normalizeFontScale(float fontScale) {
        if (Float.isNaN(fontScale) || Float.isInfinite(fontScale)) {
            return 1.0f;
        }
        return Math.max(1.0f, Math.min(3.0f, fontScale));
    }
}
