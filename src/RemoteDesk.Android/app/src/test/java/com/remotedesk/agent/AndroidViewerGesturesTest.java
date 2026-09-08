package com.remotedesk.agent;
import java.util.ArrayList;
import java.util.List;
import org.junit.Test;
import static org.junit.Assert.*;

public class AndroidViewerGesturesTest {
    private final List<int[]> events = new ArrayList<>();
    private AndroidViewerGestures gestures() {
        AndroidViewerViewport v = new AndroidViewerViewport(); v.geometry(1000,1000,1920,1080);
        AndroidViewerGestures g = new AndroidViewerGestures(v,(k,b,x,y,d)->events.add(new int[]{k,b,x,y,d}),1);
        g.centerCursor(); return g;
    }
    private long count(int kind) { return events.stream().filter(e->e[0]==kind).count(); }
    @Test public void screenTransitionStillAllowsRelease() { assertTrue(AndroidViewerGestures.maySend(3,true,false)); }
    @Test public void screenTransitionBlocksNewInput() { for (int kind : new int[]{1,2,4,5,6,7}) assertFalse(AndroidViewerGestures.maySend(kind,true,false)); }
    @Test public void oldOrViewOnlyOwnerCannotSendEvenRelease() { assertFalse(AndroidViewerGestures.maySend(3,false,true)); assertFalse(AndroidViewerGestures.maySend(3,false,false)); }
    @Test public void touchingPadDoesNotPressMouse() { AndroidViewerGestures g=gestures(); g.down(400,500,0); assertTrue(events.isEmpty()); }
    @Test public void padMovesWithoutAccidentalDrag() { AndroidViewerGestures g=gestures(); g.down(100,100,0); g.move(200,120); g.up(200,120,100); assertEquals(0,count(2)); assertEquals(1,count(1)); assertTrue(g.cursorX>960); }
    @Test public void tapIsOneBalancedClick() { AndroidViewerGestures g=gestures(); g.down(100,100,0); g.up(100,100,100); assertEquals(1,count(2)); assertEquals(1,count(3)); }
    @Test public void longPressDragReleasesOnCancel() { AndroidViewerGestures g=gestures(); g.down(400,500,0); assertTrue(g.longPress()); g.move(450,520); g.cancel(); assertEquals(1,count(2)); assertEquals(1,count(3)); assertFalse(g.dragging); }
    @Test public void modeChangeReleasesLatchedDrag() { AndroidViewerGestures g=gestures(); g.toggleDrag(); g.mode(false); assertFalse(g.lockedDrag); assertEquals(1,count(3)); }
    @Test public void lockedDragSurvivesFingerLiftUntilExplicitRelease() { AndroidViewerGestures g=gestures(); g.toggleDrag(); g.down(200,200,0); g.move(250,250); g.up(250,250,100); assertEquals(0,count(3)); g.toggleDrag(); assertEquals(1,count(3)); }
    @Test public void directTapRejectsLetterbox() { AndroidViewerGestures g=gestures(); g.mode(false); g.down(10,10,0); g.up(10,10,100); assertTrue(events.isEmpty()); }
    @Test public void directDragWaitsForMovement() { AndroidViewerGestures g=gestures(); g.mode(false); g.down(500,500,0); assertEquals(0,count(2)); g.move(600,500); assertEquals(1,count(2)); g.up(620,500,100); assertEquals(1,count(3)); }
    @Test public void twoFingerTapIsRightClickWithoutLeftClick() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(400,500,200); g.pointerUp(); g.up(500,500,150); assertEquals(1,count(2)); assertEquals(2,events.get(0)[1]); }
    @Test public void pinchOnlyChangesLocalViewport() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,500,400); g.pointerUp(); g.up(500,500,500); assertEquals(2,g.viewport.zoom,0.01); assertTrue(events.isEmpty()); }
    @Test public void twoFingerScrollDoesNotZoomOrClick() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,548,200); g.pointerUp(); g.up(500,548,200); assertEquals(1,count(4)); assertEquals(0,count(2)); assertEquals(1,g.viewport.zoom,0); }
    @Test public void liftingOneFingerCannotResumeSingleFingerDrag() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200); g.multiMove(500,550,200); g.pointerUp(); g.move(800,600); g.up(800,600,300); assertEquals(0,count(2)); }
    @Test public void secondFingerReleasesExistingDrag() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.longPress(); g.secondDown(500,500,200); assertFalse(g.dragging); assertEquals(1,count(3)); }
    @Test public void pendingLongPressCannotOutliveCancellation() { AndroidViewerGestures g=gestures(); g.down(300,500,0); g.cancel(); assertFalse(g.longPress()); assertTrue(events.isEmpty()); }
    @Test public void directLetterboxAllowsLocalPinchWithoutRemoteClick() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0);
        g.secondDown(500,100,200); g.multiMove(500,100,400); g.pointerUp(); g.up(600,100,200);
        assertEquals(2,g.viewport.zoom,.001); assertTrue(events.isEmpty());
    }
    @Test public void directLetterboxNeverStartsDragOrScroll() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0);
        assertFalse(g.longPress()); g.move(400,500);
        g.secondDown(500,500,200); g.multiMove(500,600,200); g.pointerUp(); g.up(500,600,250);
        assertTrue(events.isEmpty());
    }
    @Test public void gradualPinchUsesOriginalSpanWhenRecognitionStarts() {
        AndroidViewerGestures g=gestures(); g.down(300,500,0); g.secondDown(500,500,200);
        for(int span=202;span<=400;span+=2) g.multiMove(500,500,span);
        assertEquals(2,g.viewport.zoom,.001); assertTrue(events.isEmpty());
    }
    @Test public void addingSecondFingerAfterDragDoesNotBecomeRightClick() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,500,0); g.move(350,500);
        g.secondDown(450,500,200); g.pointerUp(); g.up(450,500,200);
        assertEquals(1,count(2)); assertEquals(1,count(3));
    }
    @Test public void canceledLetterboxGestureCannotResumeAsPinch() {
        AndroidViewerGestures g=gestures(); g.mode(false); g.down(300,100,0); g.cancel();
        g.secondDown(500,100,200); g.multiMove(500,100,400);
        assertEquals(1,g.viewport.zoom,0); assertTrue(events.isEmpty());
    }
}
