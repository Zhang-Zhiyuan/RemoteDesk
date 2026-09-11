package com.remotedesk.agent;

/** Shared JPEG/Surface transform and inverse input mapping, in device pixels. */
final class AndroidViewerViewport {
    static final float MAX_ZOOM = 8f;
    int viewWidth, viewHeight, frameWidth, frameHeight;
    float zoom = 1f, panX, panY;
    private boolean keyboardOpen, restoreFitAfterKeyboard;
    private float retainedScale;

    void geometry(int vw, int vh, int fw, int fh) {
        boolean newSource = fw != frameWidth || fh != frameHeight;
        float previousScale = retainedScale > 0f ? retainedScale : scale();
        boolean keepPixelSize = !newSource && (zoom > 1f || keyboardOpen || retainedScale > 0f) && previousScale > 0f;
        viewWidth = Math.max(0, vw); viewHeight = Math.max(0, vh);
        frameWidth = Math.max(0, fw); frameHeight = Math.max(0, fh);
        // The IME temporarily crops/pans even a fitted desktop instead of
        // shrinking it to a thumbnail. Retain intent across zero-sized layout
        // passes and zoom clamping; no codec buffer/resolution change is needed.
        if (newSource) reset();
        else if (keepPixelSize) {
            retainedScale = previousScale;
            if (baseScale() > 0f) zoom = clamp(previousScale / baseScale(), 1f, maximumZoom());
        }
        constrain();
    }

    void keyboard(boolean open) {
        if (keyboardOpen == open) return;
        keyboardOpen = open;
        if (open) {
            restoreFitAfterKeyboard = zoom <= 1f && retainedScale <= 0f;
            if (retainedScale <= 0f) retainedScale = scale();
        } else if (restoreFitAfterKeyboard) reset();
    }

    float baseScale() {
        return AndroidViewerScalePolicy.fitScale(viewWidth, viewHeight, frameWidth, frameHeight, false);
    }

    float scale() { return baseScale() * zoom; }
    private float maximumZoom() {
        float base = baseScale();
        if (base <= 0f) return MAX_ZOOM;
        // Eight times the fitted thumbnail can still be smaller than 1:1 on
        // a high-resolution desktop or a tiny landscape IME viewport. Keep
        // native pixels and an already selected magnification reachable;
        // this changes only the local transform, never the source resolution.
        return Math.max(MAX_ZOOM, Math.max(1f, retainedScale) / base);
    }
    float left() { return (viewWidth - frameWidth * scale()) / 2f + panX; }
    float top() { return (viewHeight - frameHeight * scale()) / 2f + panY; }
    void reset() {
        zoom = 1f; panX = panY = 0f;
        retainedScale = keyboardOpen ? baseScale() : 0f;
        restoreFitAfterKeyboard = true;
    }

    void originalSize() {
        float base = baseScale();
        if (base <= 0f) return; // Keep the last valid intent during a layout gap.
        zoom = 1f / base;
        panX = panY = 0f;
        constrain();
        rememberUserScale();
    }

    void zoomAt(float factor, float x, float y) {
        if (!Float.isFinite(factor) || factor <= 0 || !Float.isFinite(x) || !Float.isFinite(y) || scale() <= 0) return;
        float previous = zoom;
        zoom = clamp(zoom * factor, 1f, maximumZoom());
        float ratio = zoom / previous;
        panX = x - viewWidth / 2f - (x - viewWidth / 2f - panX) * ratio;
        panY = y - viewHeight / 2f - (y - viewHeight / 2f - panY) * ratio;
        constrain();
        rememberUserScale();
    }

    private void rememberUserScale() {
        retainedScale = zoom > 1f || keyboardOpen ? scale() : 0f;
        restoreFitAfterKeyboard = zoom <= 1f;
    }

    void pan(float dx, float dy) {
        if (!Float.isFinite(dx) || !Float.isFinite(dy)) return;
        panX += dx; panY += dy; constrain();
    }

    void reveal(float x, float y) {
        float vx = left() + x * scale(), vy = top() + y * scale();
        float margin = Math.min(32f, Math.min(viewWidth, viewHeight) / 4f);
        pan(vx < margin ? margin - vx : vx > viewWidth - margin ? viewWidth - margin - vx : 0,
            vy < margin ? margin - vy : vy > viewHeight - margin ? viewHeight - margin - vy : 0);
    }

    int[] point(float x, float y, boolean insideOnly) {
        float scale = scale();
        if (scale <= 0 || !Float.isFinite(x) || !Float.isFinite(y)) return null;
        float sx = (x - left()) / scale, sy = (y - top()) / scale;
        if (insideOnly && (sx < 0 || sy < 0 || sx >= frameWidth || sy >= frameHeight)) return null;
        return new int[] { Math.round(clamp(sx, 0, frameWidth - 1)), Math.round(clamp(sy, 0, frameHeight - 1)) };
    }

    private void constrain() {
        float maxX = Math.max(0, (frameWidth * scale() - viewWidth) / 2f);
        float maxY = Math.max(0, (frameHeight * scale() - viewHeight) / 2f);
        panX = clamp(panX, -maxX, maxX); panY = clamp(panY, -maxY, maxY);
    }

    static float clamp(float n, float min, float max) { return Math.max(min, Math.min(max, n)); }
}
