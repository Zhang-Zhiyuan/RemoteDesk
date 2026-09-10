package com.remotedesk.agent;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerViewportTest {
    private AndroidViewerViewport viewport() { AndroidViewerViewport v = new AndroidViewerViewport(); v.geometry(1000, 1000, 1920, 1080); return v; }
    @Test public void fitNeverEnlargesSmallSource() { AndroidViewerViewport v = new AndroidViewerViewport(); v.geometry(2000, 1200, 800, 600); assertEquals(1, v.scale(), 0); }
    @Test public void letterboxDoesNotStartDirectInput() { assertNull(viewport().point(500, 100, true)); }
    @Test public void centerMapsToSourceCenter() { assertArrayEquals(new int[]{960,540}, viewport().point(500, 500, true)); }
    @Test public void zoomKeepsFocusPixelInPlace() { AndroidViewerViewport v = viewport(); int[] before = v.point(650,500,true); v.zoomAt(2,650,500); assertArrayEquals(before,v.point(650,500,true)); }
    @Test public void panCannotExposeExtraBlankSpace() { AndroidViewerViewport v=viewport(); v.zoomAt(4,500,500); v.pan(100000,-100000); assertTrue(v.left() <= 0); assertTrue(v.top()+v.frameHeight*v.scale() >= v.viewHeight); }
    @Test public void originalPixelsMeanScaleOneNotJustNoUpscaling() { AndroidViewerViewport v=viewport(); v.originalSize(); assertEquals(1, v.scale(), 0.0001); }
    @Test public void resetRestoresFitAndCenter() { AndroidViewerViewport v=viewport(); v.zoomAt(3,500,500); v.pan(60,90); v.reset(); assertEquals(1,v.zoom,0); assertEquals(0,v.panX,0); }
    @Test public void geometryChangeResetsOldScreenTransform() { AndroidViewerViewport v=viewport(); v.zoomAt(2,500,500); v.geometry(1000,1000,1080,1920); assertEquals(1,v.zoom,0); }
    @Test public void rotationPreservesMagnifiedPixelSizeButClampsPosition() { AndroidViewerViewport v=viewport(); v.zoomAt(2,500,500); float scale=v.scale(); v.geometry(1800,800,1920,1080); assertEquals(scale,v.scale(),.0001); assertNotNull(v.point(0,0,false)); }
    @Test public void keyboardResizeKeepsOriginalPixelsReadable() { AndroidViewerViewport v=viewport(); v.originalSize(); v.geometry(1000,180,1920,1080); assertEquals(1,v.scale(),.0001); v.geometry(1000,1000,1920,1080); assertEquals(1,v.scale(),.0001); }
    @Test public void fitModeStillRefitsAfterWindowResize() { AndroidViewerViewport v=viewport(); v.geometry(1000,300,1920,1080); assertEquals(1,v.zoom,0); assertEquals(300f/1080,v.scale(),.0001); }
    @Test public void extremeAndInvalidZoomIsBounded() { AndroidViewerViewport v=viewport(); v.zoomAt(Float.NaN,0,0); assertEquals(1,v.zoom,0); v.zoomAt(999,500,500); assertEquals(8,v.zoom,0); v.zoomAt(.001f,500,500); assertEquals(1,v.zoom,0); }
    @Test public void invalidPointsAreRejected() { AndroidViewerViewport v=viewport(); assertNull(v.point(Float.NaN, 0, false)); assertNull(new AndroidViewerViewport().point(0,0,false)); }
    @Test public void edgeClampsToLastPixel() { assertArrayEquals(new int[]{1919,1079}, viewport().point(10000,10000,false)); }

    @Test public void keyboardKeepsFittedPixelsAndClosingRestoresFit() {
        AndroidViewerViewport v = viewport(); float fit = v.scale();
        v.keyboard(true); v.geometry(1000,180,1920,1080);
        assertEquals(fit,v.scale(),.0001);
        assertTrue(v.zoom > 1);
        v.reveal(960,900);
        float x=v.left()+960*v.scale(), y=v.top()+900*v.scale();
        assertTrue(y >= 0 && y < v.viewHeight);
        assertArrayEquals(new int[]{960,900},v.point(x,y,true));
        v.keyboard(false); v.geometry(1000,1000,1920,1080);
        assertEquals(1,v.zoom,0); assertEquals(fit,v.scale(),.0001);
        assertEquals(0,v.panY,0);
    }

    @Test public void repeatedKeyboardRequestDoesNotForgetOriginalFitMode() {
        AndroidViewerViewport v=viewport(); v.keyboard(true);
        v.geometry(1000,180,1920,1080); v.keyboard(true);
        v.keyboard(false); v.geometry(1000,1000,1920,1080);
        assertEquals(1,v.zoom,0);
    }

    @Test public void transientEmptyAndClampedImeLayoutsDoNotLosePixelSize() {
        AndroidViewerViewport v=viewport(); v.originalSize(); v.keyboard(true);
        v.geometry(0,0,1920,1080); assertNull(v.point(0,0,false));
        v.geometry(1000,20,1920,1080); assertEquals(8,v.zoom,0);
        v.geometry(1000,180,1920,1080); assertEquals(1,v.scale(),.0001);
        v.geometry(1000,20,1920,1080); v.keyboard(false);
        v.geometry(1000,1000,1920,1080); assertEquals(1,v.scale(),.0001);
    }

    @Test public void explicitFitWhileKeyboardOpenOverridesTemporaryMagnification() {
        AndroidViewerViewport v=viewport(); v.originalSize(); v.keyboard(true);
        v.geometry(1000,180,1920,1080); v.reset();
        v.geometry(1000,180,1920,1080); assertEquals(1,v.zoom,0);
        v.keyboard(false); v.geometry(1000,1000,1920,1080); assertEquals(1,v.zoom,0);
    }

    @Test public void explicitOriginalPixelsWhileTypingSurviveKeyboardClose() {
        AndroidViewerViewport v=viewport(); v.keyboard(true);
        v.geometry(1000,180,1920,1080); v.originalSize();
        v.keyboard(false); v.geometry(1000,1000,1920,1080);
        assertEquals(1,v.scale(),.0001);
    }

    @Test public void screenSwitchWhileTypingDoesNotRestoreOldScreenZoom() {
        AndroidViewerViewport v=viewport(); v.originalSize(); v.keyboard(true);
        v.geometry(1000,180,1080,1920); assertEquals(1,v.zoom,0);
        v.keyboard(false); v.geometry(1000,1000,1080,1920); assertEquals(1,v.zoom,0);
    }

    @Test public void invalidPinchFocusCannotPoisonTheViewport() {
        AndroidViewerViewport v=viewport(); v.zoomAt(2,Float.NaN,100);
        v.zoomAt(2,100,Float.POSITIVE_INFINITY);
        assertEquals(1,v.zoom,0); assertEquals(0,v.panX,0); assertEquals(0,v.panY,0);
        assertNotNull(v.point(500,500,true));
    }
}
